using System.Text.Json.Nodes;

namespace Songify.Core.Infrastructure;

/// <summary>Lenient readers for third-party JSON: a missing, null or wrongly typed value is null.</summary>
public static class JsonValues
{
    /// <summary>A non-blank string value, else null.</summary>
    public static string? Text(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    public static double? Number(JsonNode? node, string key) =>
        node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
