using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace CodexApiBridge;

public sealed class BridgeServer : IAsyncDisposable
{
    private readonly BridgeState _state;
    private readonly SettingsStore _settingsStore;
    private readonly ResponseChatMapper _mapper;
    private WebApplication? _app;

    public BridgeServer(BridgeState state, SettingsStore settingsStore)
    {
        _state = state;
        _settingsStore = settingsStore;
        _mapper = new ResponseChatMapper(state);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }

        var settings = _settingsStore.Current;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(settings.ListenUrl);
        builder.Services.AddRouting();

        _app = builder.Build();
        MapRoutes(_app);

        await _app.StartAsync(cancellationToken);
        _state.MarkStarted();
        _state.Info($"代理已启动：{settings.ListenUrl}");
        _state.Info($"管理页面：{settings.ListenUrl.TrimEnd('/')}/admin");
        _state.Info($"上游接口：{settings.UpstreamBaseUrl}/v1/chat/completions");
    }

    public async Task StopAsync()
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync();
        await _app.DisposeAsync();
        _app = null;
        _state.Info("代理已停止。");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private void MapRoutes(IEndpointRouteBuilder app)
    {
        app.MapMethods("/{**path}", ["OPTIONS"], async context =>
        {
            AddCors(context);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            await Task.CompletedTask;
        });

        app.MapGet("/", context =>
        {
            AddCors(context);
            context.Response.Redirect("/admin");
            return Task.CompletedTask;
        });

        app.MapGet("/admin", WriteAdminPageAsync);
        app.MapGet("/api/settings", WriteSettingsAsync);
        app.MapPost("/api/settings", SaveSettingsAsync);
        app.MapGet("/api/stats", WriteStatsAsync);
        app.MapGet("/api/logs", WriteLogsAsync);

        app.MapPost("/v1/responses", HandleResponsesAsync);
        app.MapPost("/responses", HandleResponsesAsync);
        app.MapPost("/v1/chat/completions", context => ProxyRawAsync(context, "/v1/chat/completions", "chat"));
        app.MapGet("/v1/models", context => ProxyRawAsync(context, "/v1/models", "models"));
    }

    private async Task HandleResponsesAsync(HttpContext context)
    {
        AddCors(context);
        var sw = Stopwatch.StartNew();
        var settings = _settingsStore.Current;
        var body = await ReadBodyAsync(context);
        JsonObject? responseRequest;

        try
        {
            responseRequest = JsonNode.Parse(body) as JsonObject;
            if (responseRequest is null)
            {
                throw new InvalidOperationException("请求体必须是 JSON object。");
            }
        }
        catch (Exception ex)
        {
            _state.MarkFailure();
            await WriteJsonAsync(context, StatusCodes.Status400BadRequest, _mapper.ErrorToResponse(ex.Message, 400));
            return;
        }

        var stream = Json.Bool(responseRequest, "stream");
        _state.MarkRequest("responses", stream);
        if (settings.EnableVerboseBodyLogging)
        {
            _state.Info($"Responses 请求：{body}");
        }

        try
        {
            var chatRequest = _mapper.ToChatCompletion(responseRequest, settings);
            if (stream)
            {
                await StreamChatCompletionWithToolsAsync(context, settings, chatRequest, responseRequest, sw, context.RequestAborted);
                return;
            }

            var executionResult = await ExecuteChatCompletionWithToolsAsync(context, settings, chatRequest, context.RequestAborted);
            if (!executionResult.Success)
            {
                _state.MarkFailure();
                _state.Error($"上游返回错误 {executionResult.StatusCode}: {Trim(executionResult.ErrorBody, 500)}");
                context.Response.StatusCode = executionResult.StatusCode;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync(executionResult.ErrorBody, context.RequestAborted);
                return;
            }

            var chatResponse = executionResult.Response ?? new JsonObject();
            RecordUsage(chatResponse["usage"], sw.Elapsed.TotalMilliseconds);
            var response = _mapper.ToResponse(chatResponse, responseRequest);
            if (stream)
            {
                await WriteResponseAsSseAsync(context, response, sw);
                return;
            }

            await WriteJsonAsync(context, StatusCodes.Status200OK, response);
            _state.RecordCompletedRequest("responses", false, true, 200, sw.Elapsed.TotalMilliseconds);
            _state.Info($"Responses 请求完成：{sw.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException)
        {
            _state.Warn("客户端取消了请求。");
        }
        catch (Exception ex)
        {
            _state.MarkFailure();
            _state.RecordCompletedRequest("responses", stream, false, 502, sw.Elapsed.TotalMilliseconds);
            _state.Error(ex.Message);
            await WriteJsonAsync(context, StatusCodes.Status502BadGateway, _mapper.ErrorToResponse(ex.Message, 502));
        }
    }

    private async Task ProxyRawAsync(HttpContext context, string upstreamPath, string kind)
    {
        AddCors(context);
        var sw = Stopwatch.StartNew();
        var settings = _settingsStore.Current;
        var body = context.Request.Method == HttpMethods.Get ? "" : await ReadBodyAsync(context);
        var stream = body.Contains("\"stream\":true", StringComparison.OrdinalIgnoreCase)
            || body.Contains("\"stream\": true", StringComparison.OrdinalIgnoreCase);
        _state.MarkRequest(kind, stream);

        try
        {
            using var client = CreateClient(settings);
            using var request = BuildUpstreamRequest(context, settings, new HttpMethod(context.Request.Method), upstreamPath);
            if (context.Request.Method != HttpMethods.Get)
            {
                request.Content = new StringContent(body, Encoding.UTF8, context.Request.ContentType ?? "application/json");
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            _state.RecordCompletedRequest(kind, stream, response.IsSuccessStatusCode, (int)response.StatusCode, sw.Elapsed.TotalMilliseconds);
            _state.Info($"{kind} 透传完成：{sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            _state.MarkFailure();
            _state.RecordCompletedRequest(kind, stream, false, 502, sw.Elapsed.TotalMilliseconds);
            _state.Error(ex.Message);
            await WriteJsonAsync(context, StatusCodes.Status502BadGateway, _mapper.ErrorToResponse(ex.Message, 502));
        }
    }

    private async Task<ChatExecutionResult> ExecuteChatCompletionWithToolsAsync(
        HttpContext context,
        BridgeSettings settings,
        JsonObject chatRequest,
        CancellationToken cancellationToken)
    {
        var workingRequest = chatRequest.DeepClone().AsObject();
        workingRequest["stream"] = false;

        for (var iteration = 0; iteration < settings.MaxToolForwardingIterations; iteration++)
        {
            using var client = CreateClient(settings);
            using var upstreamRequest = BuildUpstreamRequest(context, settings, HttpMethod.Post, "/v1/chat/completions");
            upstreamRequest.Content = new StringContent(workingRequest.ToJsonString(Json.Options), Encoding.UTF8, "application/json");
            using var upstreamResponse = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);
            var body = await upstreamResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!upstreamResponse.IsSuccessStatusCode)
            {
                return new ChatExecutionResult(false, (int)upstreamResponse.StatusCode, body, null);
            }

            var chatResponse = JsonNode.Parse(body) as JsonObject ?? new JsonObject();
            if (!settings.EnableToolForwardingExecution)
            {
                return new ChatExecutionResult(true, 200, "", chatResponse);
            }

            var message = chatResponse["choices"]?[0]?["message"] as JsonObject;
            var toolCalls = message?["tool_calls"] as JsonArray;
            if (message is null || toolCalls is null || toolCalls.Count == 0)
            {
                return new ChatExecutionResult(true, 200, "", chatResponse);
            }

            var executableCalls = toolCalls
                .OfType<JsonObject>()
                .Select(call => new { Call = call, Rule = FindExecutableRule(settings, call) })
                .Where(item => item.Rule is not null)
                .ToArray();

            if (executableCalls.Length == 0)
            {
                return new ChatExecutionResult(true, 200, "", chatResponse);
            }

            if (executableCalls.Length != toolCalls.Count)
            {
                _state.Warn("上游返回了未配置自动执行网关的工具调用，已停止内部执行并把工具调用返回给 Codex。");
                return new ChatExecutionResult(true, 200, "", chatResponse);
            }

            var messages = workingRequest["messages"] as JsonArray;
            if (messages is null)
            {
                return new ChatExecutionResult(true, 200, "", chatResponse);
            }

            messages.Add(CloneAssistantToolCallMessage(message));
            foreach (var item in executableCalls)
            {
                var output = await ExecuteForwardedToolAsync(item.Rule!, item.Call, cancellationToken);
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = Json.String(item.Call, "id") ?? "",
                    ["content"] = output
                });
            }

            _state.Info($"已执行 {executableCalls.Length} 个外部工具调用，继续请求上游模型。");
        }

        return new ChatExecutionResult(false, 508, "{\"error\":{\"message\":\"工具执行轮数超过上限。\"}}", null);
    }

    private static ToolForwardingRule? FindExecutableRule(BridgeSettings settings, JsonObject toolCall)
    {
        var name = Json.String(toolCall["function"], "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return settings.ToolForwardingRules.FirstOrDefault(rule =>
            rule.Enabled
            && !string.IsNullOrWhiteSpace(rule.ForwardEndpoint)
            && !string.Equals(rule.ForwardMode, "function", StringComparison.OrdinalIgnoreCase)
            && string.Equals(rule.FunctionName, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> ExecuteForwardedToolAsync(ToolForwardingRule rule, JsonObject toolCall, CancellationToken cancellationToken)
    {
        if (string.Equals(rule.ForwardMode, "mcp_sse", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteMcpSseToolAsync(rule, toolCall, cancellationToken);
        }

        if (string.Equals(rule.ForwardMode, "mcp_stdio", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteMcpStdioToolAsync(rule, toolCall, cancellationToken);
        }

        var function = toolCall["function"] as JsonObject;
        var rawArguments = Json.String(function, "arguments") ?? "{}";
        JsonNode? arguments;
        try
        {
            arguments = JsonNode.Parse(rawArguments);
        }
        catch
        {
            arguments = new JsonObject { ["input"] = rawArguments };
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var payload = new JsonObject
        {
            ["call_id"] = Json.String(toolCall, "id") ?? "",
            ["name"] = Json.String(function, "name") ?? rule.FunctionName,
            ["responses_tool_type"] = rule.ResponsesToolType,
            ["arguments"] = arguments?.DeepClone(),
            ["raw_arguments"] = rawArguments,
            ["forward_mode"] = rule.ForwardMode
        };

        _state.Info($"调用外部工具网关：{rule.FunctionName} -> {rule.ForwardEndpoint}");
        using var response = await client.PostAsync(
            rule.ForwardEndpoint,
            new StringContent(payload.ToJsonString(Json.Options), Encoding.UTF8, "application/json"),
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _state.Warn($"外部工具网关返回错误 {(int)response.StatusCode}: {Trim(body, 300)}");
            return JsonSerializer.Serialize(new { error = body, status = (int)response.StatusCode }, Json.Options);
        }

        return ExtractToolOutput(body);
    }

    private async Task<string> ExecuteMcpSseToolAsync(ToolForwardingRule rule, JsonObject toolCall, CancellationToken cancellationToken)
    {
        var function = toolCall["function"] as JsonObject;
        var rawArguments = Json.String(function, "arguments") ?? "{}";
        JsonNode? arguments;
        try
        {
            arguments = JsonNode.Parse(rawArguments);
        }
        catch
        {
            arguments = new JsonObject { ["input"] = rawArguments };
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var sseRequest = new HttpRequestMessage(HttpMethod.Get, rule.ForwardEndpoint);
        sseRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        _state.Info($"连接 MCP SSE 工具：{rule.FunctionName} -> {rule.ForwardEndpoint}");
        using var sseResponse = await client.SendAsync(sseRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!sseResponse.IsSuccessStatusCode)
        {
            var error = await sseResponse.Content.ReadAsStringAsync(cancellationToken);
            _state.Warn($"MCP SSE 连接失败 {(int)sseResponse.StatusCode}: {Trim(error, 300)}");
            return JsonSerializer.Serialize(new { error, status = (int)sseResponse.StatusCode }, Json.Options);
        }

        await using var stream = await sseResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var messageEndpoint = await ReadMcpSseEndpointAsync(reader, rule.ForwardEndpoint, cancellationToken);
        if (messageEndpoint is null)
        {
            return "{\"error\":\"MCP SSE endpoint event was not received.\"}";
        }

        var initializeId = 1;
        await PostMcpSseJsonRpcAsync(client, messageEndpoint, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = initializeId,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "CodexApiBridge", ["version"] = "1.0" }
            }
        }, cancellationToken);

        var initializeResponse = await ReadMcpSseJsonRpcResponseAsync(reader, initializeId, cancellationToken);
        if (initializeResponse["error"] is not null)
        {
            return ExtractMcpOutput(initializeResponse);
        }

        await PostMcpSseJsonRpcAsync(client, messageEndpoint, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
            ["params"] = new JsonObject()
        }, cancellationToken);

        var toolName = Json.String(function, "name") ?? rule.FunctionName;
        var toolCallId = 2;
        await PostMcpSseJsonRpcAsync(client, messageEndpoint, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = toolCallId,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments?.DeepClone() ?? new JsonObject()
            }
        }, cancellationToken);

        var result = await ReadMcpSseJsonRpcResponseAsync(reader, toolCallId, cancellationToken);
        return ExtractMcpOutput(result);
    }

    private async Task<string> ExecuteMcpStdioToolAsync(ToolForwardingRule rule, JsonObject toolCall, CancellationToken cancellationToken)
    {
        var command = SplitCommandLine(rule.ForwardEndpoint);
        if (command.Count == 0)
        {
            return "MCP stdio command is empty.";
        }

        var function = toolCall["function"] as JsonObject;
        var rawArguments = Json.String(function, "arguments") ?? "{}";
        JsonNode? arguments;
        try
        {
            arguments = JsonNode.Parse(rawArguments);
        }
        catch
        {
            arguments = new JsonObject { ["input"] = rawArguments };
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = command[0],
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in command.Skip(1))
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        _state.Info($"启动 MCP stdio 工具：{rule.ForwardEndpoint}");
        process.Start();

        var id = 1;
        await SendJsonRpcAsync(process, id++, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "CodexApiBridge", ["version"] = "1.0" }
        }, cancellationToken);
        await ReadJsonRpcResponseAsync(process, 1, cancellationToken);
        await SendJsonRpcNotificationAsync(process, "notifications/initialized", new JsonObject(), cancellationToken);

        var toolName = Json.String(function, "name") ?? rule.FunctionName;
        await SendJsonRpcAsync(process, id, "tools/call", new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = arguments?.DeepClone() ?? new JsonObject()
        }, cancellationToken);
        var result = await ReadJsonRpcResponseAsync(process, id, cancellationToken);

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        return ExtractMcpOutput(result);
    }

    private static async Task SendJsonRpcAsync(Process process, int id, string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        };
        await process.StandardInput.WriteLineAsync(payload.ToJsonString().AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task SendJsonRpcNotificationAsync(Process process, string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters
        };
        await process.StandardInput.WriteLineAsync(payload.ToJsonString().AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task<JsonObject> ReadJsonRpcResponseAsync(Process process, int id, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var node = JsonNode.Parse(line) as JsonObject;
            if (node?["id"]?.GetValue<int>() == id)
            {
                return node;
            }
        }

        return new JsonObject { ["error"] = new JsonObject { ["message"] = "MCP server closed stdout before response." } };
    }

    private static async Task PostMcpSseJsonRpcAsync(HttpClient client, Uri endpoint, JsonObject payload, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync(
            endpoint,
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<Uri?> ReadMcpSseEndpointAsync(StreamReader reader, string sseEndpoint, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var item = await ReadNextSseEventAsync(reader, cancellationToken);
            if (item is null)
            {
                return null;
            }

            var (eventName, data) = item.Value;
            if (!string.Equals(eventName, "endpoint", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Uri.TryCreate(data.Trim(), UriKind.Absolute, out var absolute))
            {
                return absolute;
            }

            return new Uri(new Uri(sseEndpoint), data.Trim());
        }

        return null;
    }

    private static async Task<JsonObject> ReadMcpSseJsonRpcResponseAsync(StreamReader reader, int id, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var item = await ReadNextSseEventAsync(reader, cancellationToken);
            if (item is null)
            {
                break;
            }

            var (eventName, data) = item.Value;
            if (!string.IsNullOrWhiteSpace(eventName) && !string.Equals(eventName, "message", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            JsonObject? node;
            try
            {
                node = JsonNode.Parse(data) as JsonObject;
            }
            catch
            {
                continue;
            }

            if (node?["id"]?.GetValue<int>() == id)
            {
                return node;
            }
        }

        return new JsonObject { ["error"] = new JsonObject { ["message"] = "MCP SSE stream closed before response." } };
    }

    private static async Task<(string EventName, string Data)?> ReadNextSseEventAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        string? eventName = null;
        var data = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                if (eventName is not null || data.Length > 0)
                {
                    return (eventName ?? "", data.ToString());
                }

                return null;
            }

            if (line.Length == 0)
            {
                if (eventName is not null || data.Length > 0)
                {
                    return (eventName ?? "", data.ToString());
                }

                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            var separator = line.IndexOf(':');
            var field = separator >= 0 ? line[..separator] : line;
            var value = separator >= 0 ? line[(separator + 1)..] : "";
            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            if (string.Equals(field, "event", StringComparison.OrdinalIgnoreCase))
            {
                eventName = value;
            }
            else if (string.Equals(field, "data", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }

                data.Append(value);
            }
        }

        return null;
    }

    private static string ExtractMcpOutput(JsonObject response)
    {
        if (response["error"] is JsonObject error)
        {
            return error.ToJsonString();
        }

        var result = response["result"];
        if (result?["content"] is JsonArray content)
        {
            var parts = content
                .OfType<JsonObject>()
                .Select(item => Json.String(item, "text") ?? item.ToJsonString())
                .Where(text => !string.IsNullOrWhiteSpace(text));
            return string.Join("\n", parts);
        }

        return result?.ToJsonString() ?? response.ToJsonString();
    }

    private static List<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < commandLine.Length; index++)
        {
            var ch = commandLine[index];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static JsonObject CloneAssistantToolCallMessage(JsonObject message)
    {
        var clone = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = Json.Clone(message["content"]),
            ["tool_calls"] = Json.Clone(message["tool_calls"])
        };

        if (message.TryGetPropertyValue("reasoning_content", out var reasoningContent))
        {
            clone["reasoning_content"] = Json.Clone(reasoningContent);
        }

        return clone;
    }

    private static string ExtractToolOutput(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonObject obj)
            {
                return Json.String(obj, "output")
                    ?? Json.String(obj, "result")
                    ?? Json.String(obj, "content")
                    ?? Json.String(obj, "text")
                    ?? obj.ToJsonString();
            }

            return node?.ToJsonString() ?? body;
        }
        catch
        {
            return body;
        }
    }

    private async Task StreamResponsesAsync(HttpContext context, HttpResponseMessage upstreamResponse, JsonObject originalRequest, Stopwatch sw)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";

        var responseId = $"resp_{Guid.NewGuid():N}";
        var outputItemId = $"msg_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var model = Json.String(originalRequest, "model") ?? "unknown";
        var text = new StringBuilder();
        var reasoningText = new StringBuilder();
        var toolCalls = new Dictionary<int, JsonObject>();

        await WriteSseAsync(context, "response.created", new JsonObject
        {
            ["type"] = "response.created",
            ["response"] = CreateStreamingResponse(responseId, createdAt, model, "in_progress")
        });

        await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);
        using var reader = new StreamReader(stream);
        while (!context.RequestAborted.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(context.RequestAborted);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data == "[DONE]")
            {
                break;
            }

            var chunk = JsonNode.Parse(data) as JsonObject;
            var delta = chunk?["choices"]?[0]?["delta"] as JsonObject;
            if (delta is null)
            {
                continue;
            }

            var deltaText = Json.String(delta, "content");
            if (!string.IsNullOrEmpty(deltaText))
            {
                text.Append(deltaText);
                await WriteSseAsync(context, "response.output_text.delta", new JsonObject
                {
                    ["type"] = "response.output_text.delta",
                    ["item_id"] = outputItemId,
                    ["output_index"] = 0,
                    ["content_index"] = 0,
                    ["delta"] = deltaText
                });
            }

            var reasoningDelta = Json.String(delta, "reasoning_content") ?? Json.String(delta, "reasoning");
            if (!string.IsNullOrEmpty(reasoningDelta))
            {
                reasoningText.Append(reasoningDelta);
            }

            if (delta["tool_calls"] is JsonArray calls)
            {
                await HandleToolCallDeltasAsync(context, calls, toolCalls);
            }

            if (chunk?["usage"] is not null)
            {
                RecordUsage(chunk["usage"], sw.Elapsed.TotalMilliseconds);
            }
        }

        await WriteSseAsync(context, "response.output_item.done", new JsonObject
        {
            ["type"] = "response.output_item.done",
            ["output_index"] = 0,
            ["item"] = ResponseChatMapper.CreateMessageItem(text.ToString())
        });

        if (reasoningText.Length > 0)
        {
            await WriteSseAsync(context, "response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["output_index"] = 1,
                ["item"] = ResponseChatMapper.CreateReasoningItem(reasoningText.ToString())
            });
        }

        foreach (var toolCall in toolCalls.Values)
        {
            await WriteSseAsync(context, "response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["output_index"] = 2,
                ["item"] = ResponseChatMapper.CreateFunctionCallItem(toolCall)
            });
        }

        await WriteSseAsync(context, "response.completed", new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = CreateStreamingResponse(responseId, createdAt, model, "completed", text.ToString(), toolCalls.Values, reasoningText.ToString())
        });
        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
        _state.RecordCompletedRequest("responses", true, true, 200, sw.Elapsed.TotalMilliseconds);
        _state.Info($"Responses 流式请求完成：{sw.ElapsedMilliseconds} ms");
    }

    private async Task StreamChatCompletionWithToolsAsync(
        HttpContext context,
        BridgeSettings settings,
        JsonObject chatRequest,
        JsonObject originalRequest,
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";

        var responseId = $"resp_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var model = Json.String(originalRequest, "model") ?? "unknown";
        var outputText = new StringBuilder();
        var reasoningText = new StringBuilder();
        var workingRequest = chatRequest.DeepClone().AsObject();
        workingRequest["stream"] = true;

        await WriteSseAsync(context, "response.created", new JsonObject
        {
            ["type"] = "response.created",
            ["response"] = CreateStreamingResponse(responseId, createdAt, model, "in_progress")
        });

        for (var iteration = 0; iteration < settings.MaxToolForwardingIterations; iteration++)
        {
            using var client = CreateClient(settings);
            using var upstreamRequest = BuildUpstreamRequest(context, settings, HttpMethod.Post, "/v1/chat/completions");
            upstreamRequest.Content = new StringContent(workingRequest.ToJsonString(Json.Options), Encoding.UTF8, "application/json");
            using var upstreamResponse = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!upstreamResponse.IsSuccessStatusCode)
            {
                var error = await upstreamResponse.Content.ReadAsStringAsync(cancellationToken);
                _state.MarkFailure();
                _state.RecordCompletedRequest("responses", true, false, (int)upstreamResponse.StatusCode, sw.Elapsed.TotalMilliseconds);
                _state.Error($"上游返回错误 {(int)upstreamResponse.StatusCode}: {Trim(error, 500)}");
                await WriteSseAsync(context, "response.failed", new JsonObject
                {
                    ["type"] = "response.failed",
                    ["error"] = JsonNode.Parse(error) ?? new JsonObject { ["message"] = error }
                });
                await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
                return;
            }

            var toolCalls = new Dictionary<int, JsonObject>();
            await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    break;
                }

                var chunk = JsonNode.Parse(data) as JsonObject;
                var delta = chunk?["choices"]?[0]?["delta"] as JsonObject;
                if (delta is null)
                {
                    continue;
                }

                var deltaText = Json.String(delta, "content");
                if (!string.IsNullOrEmpty(deltaText))
                {
                    outputText.Append(deltaText);
                    await WriteSseAsync(context, "response.output_text.delta", new JsonObject
                    {
                        ["type"] = "response.output_text.delta",
                        ["output_index"] = 0,
                        ["content_index"] = 0,
                        ["delta"] = deltaText
                    });
                }

                var reasoningDelta = Json.String(delta, "reasoning_content") ?? Json.String(delta, "reasoning");
                if (!string.IsNullOrEmpty(reasoningDelta))
                {
                    reasoningText.Append(reasoningDelta);
                }

                if (delta["tool_calls"] is JsonArray calls)
                {
                    await HandleToolCallDeltasAsync(context, calls, toolCalls);
                }

                if (chunk?["usage"] is not null)
                {
                    RecordUsage(chunk["usage"], sw.Elapsed.TotalMilliseconds);
                }
            }

            if (toolCalls.Count == 0 || !settings.EnableToolForwardingExecution)
            {
                break;
            }

            var executableCalls = toolCalls.Values
                .Select(call => new { Call = call, Rule = FindExecutableRule(settings, call) })
                .Where(item => item.Rule is not null)
                .ToArray();
            if (executableCalls.Length == 0 || executableCalls.Length != toolCalls.Count)
            {
                _state.Warn("流式响应包含未配置网关的工具调用，已停止内部工具执行。");
                break;
            }

            var messages = workingRequest["messages"] as JsonArray;
            if (messages is null)
            {
                break;
            }

            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = outputText.Length == 0 ? null : outputText.ToString(),
                ["tool_calls"] = new JsonArray(toolCalls.Values.Select(call => call.DeepClone()).ToArray()),
                ["reasoning_content"] = reasoningText.Length == 0 ? settings.MissingReasoningContentFallback : reasoningText.ToString()
            });

            foreach (var item in executableCalls)
            {
                var output = await ExecuteForwardedToolAsync(item.Rule!, item.Call, cancellationToken);
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = Json.String(item.Call, "id") ?? "",
                    ["content"] = output
                });
            }

            _state.Info($"流式模式已执行 {executableCalls.Length} 个外部工具调用，继续请求上游模型。");
        }

        var response = CreateStreamingResponse(responseId, createdAt, model, "completed", outputText.ToString(), null, reasoningText.ToString());
        await WriteSseAsync(context, "response.output_text.done", new JsonObject
        {
            ["type"] = "response.output_text.done",
            ["text"] = outputText.ToString(),
            ["output_index"] = 0,
            ["content_index"] = 0
        });
        await WriteSseAsync(context, "response.completed", new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = response
        });
        await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
        _state.RecordCompletedRequest("responses", true, true, 200, sw.Elapsed.TotalMilliseconds);
        _state.Info($"Responses 流式工具循环完成：{sw.ElapsedMilliseconds} ms");
    }

    private async Task WriteResponseAsSseAsync(HttpContext context, JsonObject response, Stopwatch sw)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";

        await WriteSseAsync(context, "response.created", new JsonObject
        {
            ["type"] = "response.created",
            ["response"] = Json.Clone(response)
        });

        var outputText = Json.String(response, "output_text") ?? "";
        if (!string.IsNullOrEmpty(outputText))
        {
            await WriteSseAsync(context, "response.output_text.delta", new JsonObject
            {
                ["type"] = "response.output_text.delta",
                ["delta"] = outputText,
                ["output_index"] = 0,
                ["content_index"] = 0
            });
            await WriteSseAsync(context, "response.output_text.done", new JsonObject
            {
                ["type"] = "response.output_text.done",
                ["text"] = outputText,
                ["output_index"] = 0,
                ["content_index"] = 0
            });
        }

        await WriteSseAsync(context, "response.completed", new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = Json.Clone(response)
        });
        await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
        _state.RecordCompletedRequest("responses", true, true, 200, sw.Elapsed.TotalMilliseconds);
        _state.Info($"Responses 工具执行流式响应完成：{sw.ElapsedMilliseconds} ms");
    }

    private async Task HandleToolCallDeltasAsync(HttpContext context, JsonArray calls, Dictionary<int, JsonObject> toolCalls)
    {
        foreach (var call in calls.OfType<JsonObject>())
        {
            var index = call["index"]?.GetValue<int>() ?? 0;
            if (!toolCalls.TryGetValue(index, out var stored))
            {
                stored = new JsonObject
                {
                    ["id"] = Json.String(call, "id") ?? $"call_{Guid.NewGuid():N}",
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "", ["arguments"] = "" }
                };
                toolCalls[index] = stored;
            }

            if (call["function"] is not JsonObject function)
            {
                continue;
            }

            var storedFunction = stored["function"]!.AsObject();
            var name = Json.String(function, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                storedFunction["name"] = name;
            }

            var argumentsDelta = Json.String(function, "arguments");
            if (!string.IsNullOrEmpty(argumentsDelta))
            {
                storedFunction["arguments"] = (Json.String(storedFunction, "arguments") ?? "") + argumentsDelta;
            }
        }
    }

    private async Task WriteAdminPageAsync(HttpContext context)
    {
        AddCors(context);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(AdminPage.Html);
    }

    private async Task WriteSettingsAsync(HttpContext context)
    {
        AddCors(context);
        await context.Response.WriteAsJsonAsync(_settingsStore.Snapshot(), Json.Options);
    }

    private async Task SaveSettingsAsync(HttpContext context)
    {
        AddCors(context);
        var settings = await JsonSerializer.DeserializeAsync<BridgeSettings>(context.Request.Body, Json.Options, context.RequestAborted);
        if (settings is null)
        {
            await WriteJsonAsync(context, StatusCodes.Status400BadRequest, _mapper.ErrorToResponse("配置不能为空。", 400));
            return;
        }

        _settingsStore.Save(settings);
        _state.Info("配置已从 Web 管理页面保存。监听地址变更需要重启进程后生效。");
        await WriteSettingsAsync(context);
    }

    private async Task WriteStatsAsync(HttpContext context)
    {
        AddCors(context);
        await context.Response.WriteAsJsonAsync(_state.Snapshot(), Json.Options);
    }

    private async Task WriteLogsAsync(HttpContext context)
    {
        AddCors(context);
        await context.Response.WriteAsJsonAsync(_state.GetRecentLogs(), Json.Options);
    }

    private HttpClient CreateClient(BridgeSettings settings)
    {
        return new HttpClient
        {
            BaseAddress = new Uri(settings.UpstreamBaseUrl),
            Timeout = TimeSpan.FromSeconds(Math.Max(30, settings.RequestTimeoutSeconds))
        };
    }

    private HttpRequestMessage BuildUpstreamRequest(HttpContext context, BridgeSettings settings, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        var authorization = settings.ForwardIncomingAuthorization ? context.Request.Headers.Authorization.ToString() : "";
        if (!string.IsNullOrWhiteSpace(settings.UpstreamApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.UpstreamApiKey);
        }
        else if (!string.IsNullOrWhiteSpace(authorization) && AuthenticationHeaderValue.TryParse(authorization, out var parsed))
        {
            request.Headers.Authorization = parsed;
        }

        foreach (var header in context.Request.Headers)
        {
            if (header.Key.StartsWith("OpenAI-", StringComparison.OrdinalIgnoreCase)
                || header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        return request;
    }

    private void RecordUsage(JsonNode? usage, double latencyMs)
    {
        if (usage is not JsonObject obj)
        {
            return;
        }

        long Read(string name) => obj.TryGetPropertyValue(name, out var value) && value is not null && value.GetValueKind() == JsonValueKind.Number
            ? value.GetValue<long>()
            : 0;

        _state.AddUsage(Read("prompt_tokens"), Read("completion_tokens"), Read("total_tokens"), latencyMs);
    }

    private static JsonObject CreateStreamingResponse(
        string id,
        long createdAt,
        string model,
        string status,
        string? text = null,
        IEnumerable<JsonObject>? toolCalls = null,
        string? reasoningContent = null)
    {
        var output = new JsonArray();
        if (!string.IsNullOrWhiteSpace(reasoningContent))
        {
            output.Add(ResponseChatMapper.CreateReasoningItem(reasoningContent));
        }

        if (text is not null)
        {
            output.Add(ResponseChatMapper.CreateMessageItem(text));
        }

        if (toolCalls is not null)
        {
            foreach (var toolCall in toolCalls)
            {
                output.Add(ResponseChatMapper.CreateFunctionCallItem(toolCall));
            }
        }

        return new JsonObject
        {
            ["id"] = id,
            ["object"] = "response",
            ["created_at"] = createdAt,
            ["status"] = status,
            ["model"] = model,
            ["output"] = output,
            ["output_text"] = text ?? ""
        };
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(context.RequestAborted);
    }

    private static async Task WriteJsonAsync(HttpContext context, int statusCode, JsonObject payload)
    {
        AddCors(context);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(payload.ToJsonString(Json.Options), context.RequestAborted);
    }

    private static async Task WriteSseAsync(HttpContext context, string eventName, JsonObject payload)
    {
        payload["type"] ??= eventName;
        await context.Response.WriteAsync($"event: {eventName}\n", context.RequestAborted);
        await context.Response.WriteAsync($"data: {payload.ToJsonString()}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static void AddCors(HttpContext context)
    {
        context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.AccessControlAllowHeaders = "*";
        context.Response.Headers.AccessControlAllowMethods = "GET,POST,OPTIONS";
    }

    private static string Trim(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "...";
}

public sealed record ChatExecutionResult(bool Success, int StatusCode, string ErrorBody, JsonObject? Response);
