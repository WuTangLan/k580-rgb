using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace K580Rgb.Core;

/// <summary>Numbers from JSON nodes, whatever created them. A JsonValue built in code keeps its CLR
/// type (int, byte, double...) and refuses to convert to another one, unlike a parsed value.</summary>
public static class JsonNum
{
    public static double Get(JsonNode? n) => n switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.Number =>
            double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.GetValueKind() == JsonValueKind.True ? 1 : 0,
        _ => throw new FormatException($"expected a number, got {n?.ToJsonString() ?? "nothing"}"),
    };

    public static double Get(JsonNode? n, double fallback) => n is null ? fallback : Get(n);

    public static int Int(JsonNode? n) => (int)Get(n);

    public static int Int(JsonNode? n, int fallback) => n is null ? fallback : Int(n);
}
