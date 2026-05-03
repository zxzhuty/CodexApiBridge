using System.IO;
using System.Text.Json;

namespace CodexApiBridge;

public sealed class BridgeSettings
{
    public string ListenUrl { get; set; } = "http://127.0.0.1:11434";
    public string UpstreamBaseUrl { get; set; } = "https://api.openai.com";
    public string UpstreamApiKey { get; set; } = "";
    public string ModelOverride { get; set; } = "";
    public bool ForwardIncomingAuthorization { get; set; } = true;
    public bool EnableCustomToolCompatibility { get; set; } = true;
    public bool EnableImageGenerationToolCompatibility { get; set; } = true;
    public bool EnableNamespaceToolCompatibility { get; set; } = true;
    public bool EnableReasoningContentCompatibility { get; set; } = true;
    public bool EnableMissingReasoningContentFallback { get; set; } = true;
    public string MissingReasoningContentFallback { get; set; } = "Reasoning content was not captured by the bridge in a previous turn.";
    public bool EnableVerboseBodyLogging { get; set; }
    public bool EnableToolForwardingExecution { get; set; } = true;
    public int MaxToolForwardingIterations { get; set; } = 4;
    public int RequestTimeoutSeconds { get; set; } = 600;
    public string LogDirectory { get; set; } = "logs";
    public List<ToolForwardingRule> ToolForwardingRules { get; set; } =
    [
        new ToolForwardingRule
        {
            Enabled = true,
            ResponsesToolType = "web_search",
            FunctionName = "web_search",
            Description = "Search the web using the configured external search/MCP gateway.",
            ParametersJson = """
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "Search query." }
              },
              "required": ["query"],
              "additionalProperties": true
            }
            """,
            ForwardMode = "function"
        }
    ];

    public static BridgeSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                var settings = new BridgeSettings();
                settings.Save(path);
                return settings;
            }

            return JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(path), Json.Options) ?? new BridgeSettings();
        }
        catch
        {
            return new BridgeSettings();
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, Json.Options));
    }

    public BridgeSettings Clone() => new()
    {
        ListenUrl = ListenUrl.Trim().TrimEnd('/'),
        UpstreamBaseUrl = UpstreamBaseUrl.Trim().TrimEnd('/'),
        UpstreamApiKey = UpstreamApiKey.Trim(),
        ModelOverride = ModelOverride.Trim(),
        ForwardIncomingAuthorization = ForwardIncomingAuthorization,
        EnableCustomToolCompatibility = EnableCustomToolCompatibility,
        EnableImageGenerationToolCompatibility = EnableImageGenerationToolCompatibility,
        EnableNamespaceToolCompatibility = EnableNamespaceToolCompatibility,
        EnableReasoningContentCompatibility = EnableReasoningContentCompatibility,
        EnableMissingReasoningContentFallback = EnableMissingReasoningContentFallback,
        MissingReasoningContentFallback = string.IsNullOrWhiteSpace(MissingReasoningContentFallback)
            ? "Reasoning content was not captured by the bridge in a previous turn."
            : MissingReasoningContentFallback.Trim(),
        EnableVerboseBodyLogging = EnableVerboseBodyLogging,
        EnableToolForwardingExecution = EnableToolForwardingExecution,
        MaxToolForwardingIterations = Math.Clamp(MaxToolForwardingIterations, 1, 12),
        RequestTimeoutSeconds = RequestTimeoutSeconds,
        LogDirectory = string.IsNullOrWhiteSpace(LogDirectory) ? "logs" : LogDirectory.Trim(),
        ToolForwardingRules = ToolForwardingRules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.ResponsesToolType))
            .Select(rule => rule.Clone())
            .ToList()
    };
}

public sealed class ToolForwardingRule
{
    public bool Enabled { get; set; } = true;
    public string ResponsesToolType { get; set; } = "";
    public string FunctionName { get; set; } = "";
    public string Description { get; set; } = "";
    public string ParametersJson { get; set; } = """
    {
      "type": "object",
      "properties": {
        "input": { "type": "string" }
      },
      "additionalProperties": true
    }
    """;
    public string ForwardMode { get; set; } = "function";
    public string ForwardEndpoint { get; set; } = "";

    public ToolForwardingRule Clone() => new()
    {
        Enabled = Enabled,
        ResponsesToolType = ResponsesToolType.Trim(),
        FunctionName = FunctionName.Trim(),
        Description = Description.Trim(),
        ParametersJson = ParametersJson.Trim(),
        ForwardMode = string.IsNullOrWhiteSpace(ForwardMode) ? "function" : ForwardMode.Trim(),
        ForwardEndpoint = ForwardEndpoint.Trim()
    };
}
