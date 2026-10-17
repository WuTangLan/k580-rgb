// Hardware effects built into the stock firmware (mode byte of a profile).
//
// Capabilities follow OpenRGB's EVision driver; 0x0F and 0x11 are not covered there,
// so they only expose what the firmware visibly reads. Speed 0 is fastest, 5 slowest.
using System.Text.Json.Nodes;

namespace K580Rgb.Core;

public sealed record Mode(int Value, string Name, string FwName, bool Speed, bool Color, bool Direction);

public static class Modes
{
    public const int Custom = 0x14;
    public const int BrightnessMax = 5;
    public const int SpeedMax = 5;

    public static readonly IReadOnlyList<Mode> All =
    [
        new(0x06, "Static", "Normally on", false, true, false),
        new(0x05, "Breathing", "Breathing", true, true, false),
        new(0x04, "Spectrum Cycle", "The trial of light", true, false, false),
        new(0x02, "Color Wave", "Clouds fly", true, true, true),
        new(0x01, "Color Wave (Short)", "Go with the stream", true, true, true),
        new(0x03, "Color Wheel", "Winding paths", true, true, true),
        new(0x0C, "Vertical Rainbow", "Snow winter jasmine", true, true, true),
        new(0x12, "Rainbow Circle", "Fast and the Furious", false, false, false),
        new(0x0D, "Hurricane", "Hurricane", true, true, false),
        new(0x0E, "Accumulate", "Accumulate", true, true, false),
        new(0x0A, "Starlight", "Swift action", true, true, false),
        new(0x0F, "Starlight (Slow)", "Digital times", true, true, false),
        new(0x0B, "Blooming", "Flowers blooming", true, true, false),
        new(0x10, "Visor", "Both ways", false, true, false),
        new(0x11, "Surmount", "Surmount", true, false, false),
        new(0x07, "Reactive", "Pass without trace", true, true, false),
        new(0x08, "Reactive Ripple", "Ripple graff", true, true, false),
        new(0x09, "Reactive Line", "Fast run without trace", true, true, false),
        new(Custom, "Custom (per-key)", "Coastal", false, false, false),
    ];

    public static readonly IReadOnlyDictionary<int, Mode> ByValue = All.ToDictionary(m => m.Value);

    public static JsonArray ToJson() =>
        [.. All.Select(m => (JsonNode)new JsonObject
        {
            ["value"] = m.Value, ["name"] = m.Name, ["fw_name"] = m.FwName,
            ["speed"] = m.Speed, ["color"] = m.Color, ["direction"] = m.Direction,
        })];
}
