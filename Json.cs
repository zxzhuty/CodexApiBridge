using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexApiBridge;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static JsonNode? Clone(JsonNode? node) => node?.DeepClone();

    public static string? String(JsonNode? node, string property)
        => node is JsonObject obj && obj.TryGetPropertyValue(property, out var value) ? value?.GetValue<string>() : null;

    public static bool Bool(JsonNode? node, string property, bool fallback = false)
        => node is JsonObject obj && obj.TryGetPropertyValue(property, out var value) && value is not null
            ? value.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback
            }
            : fallback;
}
