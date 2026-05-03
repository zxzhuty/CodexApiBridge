using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexApiBridge;

public sealed class ResponseChatMapper
{
    private readonly BridgeState _state;

    public ResponseChatMapper(BridgeState state)
    {
        _state = state;
    }

    public JsonObject ToChatCompletion(JsonObject responseRequest, BridgeSettings settings)
    {
        var chat = new JsonObject();
        Copy(responseRequest, chat, "model");
        if (!string.IsNullOrWhiteSpace(settings.ModelOverride))
        {
            chat["model"] = settings.ModelOverride;
        }

        foreach (var property in new[]
                 {
                     "temperature", "top_p", "stream", "stop", "presence_penalty", "frequency_penalty",
                     "seed", "user", "metadata", "parallel_tool_calls"
                 })
        {
            Copy(responseRequest, chat, property);
        }

        if (responseRequest.TryGetPropertyValue("max_output_tokens", out var maxOutputTokens))
        {
            chat["max_tokens"] = Json.Clone(maxOutputTokens);
        }

        var messages = BuildMessages(responseRequest["input"], settings);
        if (responseRequest.TryGetPropertyValue("instructions", out var instructions) && instructions is not null)
        {
            messages.Insert(0, new JsonObject
            {
                ["role"] = "system",
                ["content"] = instructions.GetValue<string>()
            });
        }

        EnsureAssistantReasoningContent(messages, settings);
        chat["messages"] = messages;

        if (responseRequest.TryGetPropertyValue("tools", out var tools) && tools is JsonArray toolsArray)
        {
            var chatTools = new JsonArray();
            foreach (var tool in toolsArray)
            {
                var mapped = MapTool(tool, settings);
                if (mapped is not null)
                {
                    chatTools.Add(mapped);
                }
            }

            if (chatTools.Count > 0)
            {
                chat["tools"] = chatTools;
                MapToolChoice(responseRequest, chat, settings);
            }
        }

        MapResponseFormat(responseRequest, chat);
        return chat;
    }

    public JsonObject ToResponse(JsonObject chatResponse, JsonObject originalRequest)
    {
        var output = new JsonArray();
        var outputText = "";

        if (chatResponse["choices"] is JsonArray choices && choices.Count > 0)
        {
            var message = choices[0]?["message"] as JsonObject;
            if (message is not null)
            {
                var reasoningContent = Json.String(message, "reasoning_content") ?? Json.String(message, "reasoning");
                if (!string.IsNullOrWhiteSpace(reasoningContent))
                {
                    output.Add(CreateReasoningItem(reasoningContent));
                }

                outputText = ExtractContentText(message["content"]);
                if (!string.IsNullOrEmpty(outputText))
                {
                    output.Add(CreateMessageItem(outputText));
                }

                if (message["tool_calls"] is JsonArray toolCalls)
                {
                    foreach (var toolCall in toolCalls.OfType<JsonObject>())
                    {
                        output.Add(CreateFunctionCallItem(toolCall));
                    }
                }
            }
        }

        var response = new JsonObject
        {
            ["id"] = $"resp_{Guid.NewGuid():N}",
            ["object"] = "response",
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["status"] = "completed",
            ["model"] = Json.String(chatResponse, "model") ?? Json.String(originalRequest, "model") ?? "unknown",
            ["output"] = output,
            ["output_text"] = outputText
        };

        if (chatResponse.TryGetPropertyValue("usage", out var usage))
        {
            response["usage"] = MapUsage(usage);
        }

        return response;
    }

    public JsonObject ErrorToResponse(string message, int statusCode)
    {
        return new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = message,
                ["type"] = "bridge_error",
                ["code"] = statusCode
            }
        };
    }

    public JsonObject MapUsage(JsonNode? chatUsage)
    {
        var input = ReadLong(chatUsage, "prompt_tokens");
        var output = ReadLong(chatUsage, "completion_tokens");
        var total = ReadLong(chatUsage, "total_tokens");
        return new JsonObject
        {
            ["input_tokens"] = input,
            ["output_tokens"] = output,
            ["total_tokens"] = total,
            ["prompt_tokens"] = input,
            ["completion_tokens"] = output
        };
    }

    public static JsonObject CreateMessageItem(string text)
    {
        return new JsonObject
        {
            ["id"] = $"msg_{Guid.NewGuid():N}",
            ["type"] = "message",
            ["status"] = "completed",
            ["role"] = "assistant",
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "output_text",
                    ["text"] = text,
                    ["annotations"] = new JsonArray()
                }
            }
        };
    }

    public static JsonObject CreateFunctionCallItem(JsonObject toolCall)
    {
        var function = toolCall["function"] as JsonObject;
        var id = Json.String(toolCall, "id") ?? $"call_{Guid.NewGuid():N}";
        return new JsonObject
        {
            ["id"] = id,
            ["type"] = "function_call",
            ["status"] = "completed",
            ["call_id"] = id,
            ["name"] = Json.String(function, "name") ?? "",
            ["arguments"] = Json.String(function, "arguments") ?? "{}"
        };
    }

    public static JsonObject CreateReasoningItem(string reasoningContent)
    {
        return new JsonObject
        {
            ["id"] = $"rs_{Guid.NewGuid():N}",
            ["type"] = "reasoning",
            ["summary"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "summary_text",
                    ["text"] = reasoningContent
                }
            }
        };
    }

    private JsonArray BuildMessages(JsonNode? input, BridgeSettings settings)
    {
        var messages = new JsonArray();
        var precedingToolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var pendingReasoningContent = "";
        if (input is null)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "" });
            return messages;
        }

        if (input is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = value.GetValue<string>() });
            return messages;
        }

        if (input is not JsonArray inputArray)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = input.ToJsonString() });
            return messages;
        }

        foreach (var item in inputArray)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var type = Json.String(obj, "type");
            if (type is "function_call" or "custom_tool_call" or "custom_call")
            {
                var toolCallMessage = CreateAssistantToolCallMessage(obj);
                if (!string.IsNullOrWhiteSpace(pendingReasoningContent))
                {
                    toolCallMessage["reasoning_content"] = pendingReasoningContent;
                    pendingReasoningContent = "";
                }
                else if (settings.EnableReasoningContentCompatibility
                         && settings.EnableMissingReasoningContentFallback)
                {
                    toolCallMessage["reasoning_content"] = settings.MissingReasoningContentFallback;
                }

                var toolCallId = Json.String(toolCallMessage["tool_calls"]?[0], "id");
                if (!string.IsNullOrWhiteSpace(toolCallId))
                {
                    precedingToolCallIds.Add(toolCallId);
                }

                messages.Add(toolCallMessage);
                continue;
            }

            if (type is "function_call_output" or "custom_tool_call_output" or "custom_call_output")
            {
                var toolCallId = Json.String(obj, "call_id") ?? Json.String(obj, "id") ?? "";
                if (!precedingToolCallIds.Contains(toolCallId))
                {
                    _state.Warn($"Responses 工具结果缺少前置 tool_call（{toolCallId}），已降级为 user 消息。");
                    messages.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = $"Tool output for {toolCallId}: {ExtractContentText(obj["output"])}"
                    });
                    continue;
                }

                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = toolCallId,
                    ["content"] = ExtractContentText(obj["output"])
                });
                continue;
            }

            if (type == "message" || obj.ContainsKey("role"))
            {
                var role = Json.String(obj, "role") ?? "user";
                var message = new JsonObject
                {
                    ["role"] = role == "developer" ? "system" : role,
                    ["content"] = MapContent(obj["content"])
                };

                if (role == "assistant" && !string.IsNullOrWhiteSpace(pendingReasoningContent))
                {
                    message["reasoning_content"] = pendingReasoningContent;
                    pendingReasoningContent = "";
                }
                else if (role == "assistant"
                         && settings.EnableReasoningContentCompatibility
                         && settings.EnableMissingReasoningContentFallback)
                {
                    message["reasoning_content"] = settings.MissingReasoningContentFallback;
                }

                messages.Add(message);
                continue;
            }

            if (type == "reasoning")
            {
                if (settings.EnableReasoningContentCompatibility)
                {
                    var reasoningContent = ExtractReasoningContent(obj);
                    if (!string.IsNullOrWhiteSpace(reasoningContent))
                    {
                        pendingReasoningContent = string.IsNullOrWhiteSpace(pendingReasoningContent)
                            ? reasoningContent
                            : pendingReasoningContent + "\n" + reasoningContent;
                        continue;
                    }
                }

                _state.Warn("Responses reasoning item 无法提取 reasoning_content，已跳过。");
                continue;
            }

            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = ExtractContentText(item)
            });
        }

        if (messages.Count == 0)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = "" });
        }

        if (!string.IsNullOrWhiteSpace(pendingReasoningContent))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["reasoning_content"] = pendingReasoningContent
            });
        }

        return messages;
    }

    private static string ExtractReasoningContent(JsonObject reasoning)
    {
        var direct = Json.String(reasoning, "content")
            ?? Json.String(reasoning, "text")
            ?? Json.String(reasoning, "reasoning_content");
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        if (reasoning["summary"] is JsonArray summaryArray)
        {
            var parts = summaryArray.Select(ExtractContentText).Where(part => !string.IsNullOrWhiteSpace(part));
            var joined = string.Join("\n", parts);
            if (!string.IsNullOrWhiteSpace(joined))
            {
                return joined;
            }
        }

        if (reasoning["content"] is JsonArray contentArray)
        {
            var parts = contentArray.Select(ExtractContentText).Where(part => !string.IsNullOrWhiteSpace(part));
            var joined = string.Join("\n", parts);
            if (!string.IsNullOrWhiteSpace(joined))
            {
                return joined;
            }
        }

        return "";
    }

    private JsonObject CreateAssistantToolCallMessage(JsonObject item)
    {
        var type = Json.String(item, "type") ?? "function_call";
        var callId = Json.String(item, "call_id") ?? Json.String(item, "id") ?? $"call_{Guid.NewGuid():N}";
        var name = SanitizeFunctionName(Json.String(item, "name") ?? (type == "function_call" ? "tool" : type));
        var arguments = Json.String(item, "arguments");

        if (string.IsNullOrWhiteSpace(arguments))
        {
            var input = Json.String(item, "input");
            arguments = input is null
                ? "{}"
                : new JsonObject { ["input"] = input }.ToJsonString();
        }

        return new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = null,
            ["tool_calls"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = callId,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = name,
                        ["arguments"] = arguments
                    }
                }
            }
        };
    }

    private static void EnsureAssistantReasoningContent(JsonArray messages, BridgeSettings settings)
    {
        if (!settings.EnableReasoningContentCompatibility || !settings.EnableMissingReasoningContentFallback)
        {
            return;
        }

        foreach (var messageNode in messages)
        {
            if (messageNode is not JsonObject message)
            {
                continue;
            }

            if (Json.String(message, "role") != "assistant")
            {
                continue;
            }

            var reasoningContent = Json.String(message, "reasoning_content");
            if (string.IsNullOrWhiteSpace(reasoningContent))
            {
                message["reasoning_content"] = settings.MissingReasoningContentFallback;
            }
        }
    }

    private JsonNode MapContent(JsonNode? content)
    {
        if (content is null)
        {
            return "";
        }

        if (content is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            return value.GetValue<string>();
        }

        if (content is not JsonArray contentArray)
        {
            return ExtractContentText(content);
        }

        var parts = new JsonArray();
        foreach (var part in contentArray)
        {
            if (part is not JsonObject obj)
            {
                continue;
            }

            var type = Json.String(obj, "type");
            if (type is "input_text" or "output_text" or "text")
            {
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = Json.String(obj, "text") ?? "" });
            }
            else if (type == "input_image")
            {
                var imageUrl = Json.String(obj, "image_url");
                if (!string.IsNullOrWhiteSpace(imageUrl))
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject
                        {
                            ["url"] = imageUrl,
                            ["detail"] = Json.String(obj, "detail") ?? "auto"
                        }
                    });
                }
            }
            else if (type == "input_file")
            {
                _state.Warn("上游 Chat Completions 通常不支持 Responses input_file，已转为文本占位。");
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = "[input_file omitted by bridge]" });
            }
        }

        return parts.Count == 1 && parts[0]?["type"]?.GetValue<string>() == "text"
            ? parts[0]!["text"]!.GetValue<string>()
            : parts;
    }

    private JsonObject? MapTool(JsonNode? tool, BridgeSettings settings)
    {
        if (tool is not JsonObject obj)
        {
            return null;
        }

        var type = Json.String(obj, "type");
        if (type == "function")
        {
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = SanitizeFunctionName(Json.String(obj, "name") ?? "tool"),
                    ["description"] = Json.String(obj, "description") ?? "",
                    ["parameters"] = Json.Clone(obj["parameters"] ?? obj["input_schema"] ?? new JsonObject())
                }
            };
        }

        if (type == "custom" && settings.EnableCustomToolCompatibility)
        {
            var name = SanitizeFunctionName(Json.String(obj, "name") ?? "custom_tool");
            _state.Warn($"Responses custom 工具已降级为 Chat Completions function：{name}");
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = BuildCustomToolDescription(obj),
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["input"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Raw free-form input for the original Responses custom tool."
                            }
                        },
                        ["required"] = new JsonArray("input"),
                        ["additionalProperties"] = true
                    }
                }
            };
        }

        if (type == "image_generation" && settings.EnableImageGenerationToolCompatibility)
        {
            _state.Warn("Responses image_generation 工具已降级为 Chat Completions function：image_generation。是否真正生成图片取决于上游模型/服务。");
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = "image_generation",
                    ["description"] = "Compatibility wrapper for the original Responses image_generation tool. Use it when the user asks to generate or edit images.",
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "Image generation prompt." },
                            ["size"] = new JsonObject { ["type"] = "string", ["description"] = "Requested image size, if any." },
                            ["quality"] = new JsonObject { ["type"] = "string", ["description"] = "Requested image quality, if any." },
                            ["background"] = new JsonObject { ["type"] = "string", ["description"] = "Requested background mode, if any." },
                            ["output_format"] = new JsonObject { ["type"] = "string", ["description"] = "Requested output format, if any." }
                        },
                        ["required"] = new JsonArray("prompt"),
                        ["additionalProperties"] = true
                    }
                }
            };
        }

        if (type == "namespace" && settings.EnableNamespaceToolCompatibility)
        {
            var name = BuildNamespaceFunctionName(obj);
            _state.Warn($"Responses namespace 工具已降级为 Chat Completions function：{name}");
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = BuildNamespaceToolDescription(obj),
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["name"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Tool or operation name inside the original Responses namespace."
                            },
                            ["arguments"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["description"] = "Arguments for the original namespace operation.",
                                ["additionalProperties"] = true
                            },
                            ["input"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "Raw input when the namespace operation is free-form."
                            }
                        },
                        ["additionalProperties"] = true
                    }
                }
            };
        }

        var forwardingRule = settings.ToolForwardingRules.FirstOrDefault(rule =>
            rule.Enabled && string.Equals(rule.ResponsesToolType, type, StringComparison.OrdinalIgnoreCase));
        if (forwardingRule is not null)
        {
            var functionName = SanitizeFunctionName(string.IsNullOrWhiteSpace(forwardingRule.FunctionName)
                ? forwardingRule.ResponsesToolType
                : forwardingRule.FunctionName);
            _state.Warn($"Responses 工具类型 {type} 已按配置转发为 Chat Completions function：{functionName}");
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = functionName,
                    ["description"] = string.IsNullOrWhiteSpace(forwardingRule.Description)
                        ? $"Forwarded compatibility wrapper for Responses tool type '{type}'."
                        : forwardingRule.Description,
                    ["parameters"] = ParseParametersSchema(forwardingRule.ParametersJson)
                }
            };
        }

        _state.Warn($"上游 Chat Completions 不支持 Responses 工具类型 {type}，已跳过。");
        return null;
    }

    private static void MapToolChoice(JsonObject responseRequest, JsonObject chat, BridgeSettings settings)
    {
        if (!responseRequest.TryGetPropertyValue("tool_choice", out var toolChoice) || toolChoice is null)
        {
            return;
        }

        if (toolChoice is JsonValue)
        {
            chat["tool_choice"] = Json.Clone(toolChoice);
            return;
        }

        if (toolChoice is not JsonObject obj)
        {
            return;
        }

        var type = Json.String(obj, "type");
        if (type == "function")
        {
            chat["tool_choice"] = Json.Clone(toolChoice);
            return;
        }

        if (type is "custom" or "image_generation" or "namespace")
        {
            var name = type == "image_generation"
                ? "image_generation"
                : type == "namespace"
                    ? BuildNamespaceFunctionName(obj)
                    : SanitizeFunctionName(Json.String(obj, "name") ?? "custom_tool");
            chat["tool_choice"] = new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name }
            };
            return;
        }

        var forwardingRule = settings.ToolForwardingRules.FirstOrDefault(rule =>
            rule.Enabled && string.Equals(rule.ResponsesToolType, type, StringComparison.OrdinalIgnoreCase));
        if (forwardingRule is not null)
        {
            chat["tool_choice"] = new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = SanitizeFunctionName(string.IsNullOrWhiteSpace(forwardingRule.FunctionName)
                        ? forwardingRule.ResponsesToolType
                        : forwardingRule.FunctionName)
                }
            };
        }
    }

    private static void MapResponseFormat(JsonObject responseRequest, JsonObject chat)
    {
        if (responseRequest["text"] is not JsonObject text || text["format"] is not JsonObject format)
        {
            return;
        }

        var type = Json.String(format, "type");
        if (type == "json_object")
        {
            chat["response_format"] = new JsonObject { ["type"] = "json_object" };
        }
        else if (type == "json_schema")
        {
            chat["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = Json.Clone(format)
            };
        }
    }

    private static string BuildCustomToolDescription(JsonObject tool)
    {
        var description = Json.String(tool, "description");
        var format = tool["format"]?.ToJsonString();
        if (string.IsNullOrWhiteSpace(format))
        {
            return string.IsNullOrWhiteSpace(description)
                ? "Compatibility wrapper for a Responses custom tool. Pass the raw custom-tool input as the input string."
                : description;
        }

        return string.IsNullOrWhiteSpace(description)
            ? $"Compatibility wrapper for a Responses custom tool. Original format: {format}"
            : $"{description}\n\nOriginal custom tool format: {format}";
    }

    private static string BuildNamespaceFunctionName(JsonObject tool)
    {
        var rawName = Json.String(tool, "name")
            ?? Json.String(tool, "namespace")
            ?? Json.String(tool, "namespace_name")
            ?? "namespace_tool";
        return SanitizeFunctionName($"namespace_{rawName}");
    }

    private static string BuildNamespaceToolDescription(JsonObject tool)
    {
        var description = Json.String(tool, "description");
        var namespaceName = Json.String(tool, "namespace") ?? Json.String(tool, "namespace_name") ?? Json.String(tool, "name");
        var prefix = string.IsNullOrWhiteSpace(namespaceName)
            ? "Compatibility wrapper for a Responses namespace tool."
            : $"Compatibility wrapper for the original Responses namespace tool '{namespaceName}'.";

        if (tool["tools"] is JsonArray tools && tools.Count > 0)
        {
            prefix += $" Original namespace tool declarations: {tools.ToJsonString()}";
        }

        return string.IsNullOrWhiteSpace(description) ? prefix : $"{description}\n\n{prefix}";
    }

    private static JsonNode ParseParametersSchema(string schemaJson)
    {
        if (!string.IsNullOrWhiteSpace(schemaJson))
        {
            try
            {
                return JsonNode.Parse(schemaJson) ?? DefaultParametersSchema();
            }
            catch
            {
                return DefaultParametersSchema();
            }
        }

        return DefaultParametersSchema();
    }

    private static JsonObject DefaultParametersSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["input"] = new JsonObject { ["type"] = "string" }
            },
            ["additionalProperties"] = true
        };
    }

    private static string SanitizeFunctionName(string name)
    {
        var sanitized = Regex.Replace(name.Trim(), @"[^a-zA-Z0-9_-]", "_");
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "tool";
        }

        if (!char.IsLetter(sanitized[0]) && sanitized[0] != '_')
        {
            sanitized = "_" + sanitized;
        }

        return sanitized.Length <= 64 ? sanitized : sanitized[..64];
    }

    private static void Copy(JsonObject source, JsonObject target, string property)
    {
        if (source.TryGetPropertyValue(property, out var value))
        {
            target[property] = Json.Clone(value);
        }
    }

    private static string ExtractContentText(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        if (node is JsonValue value)
        {
            return value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();
        }

        if (node is JsonArray array)
        {
            return string.Join("", array.Select(ExtractContentText));
        }

        if (node is JsonObject obj)
        {
            return Json.String(obj, "text")
                ?? Json.String(obj, "output")
                ?? Json.String(obj, "content")
                ?? obj.ToJsonString();
        }

        return node.ToJsonString();
    }

    private static long ReadLong(JsonNode? node, string property)
    {
        if (node is not JsonObject obj || !obj.TryGetPropertyValue(property, out var value) || value is null)
        {
            return 0;
        }

        return value.GetValueKind() == JsonValueKind.Number ? value.GetValue<long>() : 0;
    }
}
