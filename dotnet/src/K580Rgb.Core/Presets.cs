// Built-in live-effect presets (layer stacks, bottom layer first).
// Shared by the web UI (served via /api/static) and the tray menu.
using System.Text.Json.Nodes;
using K580Rgb.Core.Effects;

namespace K580Rgb.Core;

public static class Presets
{
    // JSON keeps these identical to the Python table; parsed fresh on each use so callers own the nodes
    const string BuiltinJson = """
    {
      "Vincent Sunset": [{"effect": "Vincent Sunset"}],
      "Vincent Sunset + white key fade": [{"effect": "Vincent Sunset"}, {"effect": "Key Fade", "args": {"style": "Color", "color": "#ffffff"}}],
      "Aurora + rainbow key fade": [{"effect": "Aurora"}, {"effect": "Key Fade", "args": {"style": "Rainbow"}}],
      "Fire + ripples": [{"effect": "Fire"}, {"effect": "Ripple", "args": {"background": "#000000", "color": "#ffe080"}, "blend": "Add"}],
      "Music: spectrum over plasma": [{"effect": "Plasma", "opacity": 0.2}, {"effect": "Audio Spectrum"}],
      "Music: bass pulse": [{"effect": "Audio Pulse"}],
      "Screen ambience": [{"effect": "Screen Ambience"}],
      "Rainbow wave + white keys": [{"effect": "Rainbow Wave", "opacity": 0.6}, {"effect": "Key Fade", "args": {"style": "Color", "color": "#ffffff"}}],
      "Typing heatmap": [{"effect": "Typing Heatmap"}],
      "GIF: Pac-Man chase": [{"effect": "GIF / Image", "args": {"file": "Pac-Man chase.gif"}}],
      "GIF: fireworks + key fade": [{"effect": "GIF / Image", "args": {"file": "Fireworks.gif"}}, {"effect": "Key Fade", "args": {"style": "Color", "color": "#ffffff"}}],
      "Scrolling text over aurora": [{"effect": "Aurora", "opacity": 0.35}, {"effect": "Scrolling Text", "args": {"style": "Color", "color": "#ffffff"}}],
      "Black hole (follows your keys)": [{"effect": "Black Hole", "args": {"follow keys": "On"}}],
      "Black hole: purple": [{"effect": "Black Hole", "args": {"theme": "Purple"}}],
      "Music: center bars": [{"effect": "Audio Center Bars"}],
      "Music: VU meter": [{"effect": "Audio VU Meter"}],
      "Music: waterfall": [{"effect": "Audio Waterfall"}],
      "Music: beat ripples over aurora": [{"effect": "Aurora", "opacity": 0.3}, {"effect": "Audio Beat Ripple", "args": {"background": "#000000"}, "blend": "Add"}],
      "Music: color flow + key fade": [{"effect": "Audio Color Flow"}, {"effect": "Key Fade", "args": {"style": "Color", "color": "#ffffff"}}],
      "Rocket Mania: launch & fireworks": [{"effect": "Rocket Mania: Launch & Fireworks"}],
      "Rocket Mania over Vincent Sunset": [{"effect": "Vincent Sunset", "opacity": 0.35}, {"effect": "Rocket Mania: Launch & Fireworks"}],
      "Battlefield 1: combat": [{"effect": "Battlefield: Combat"}],
      "Battlefield 1: combat (demo, no game needed)": [{"effect": "Battlefield: Combat", "args": {"demo": "On"}}],
      "Battlefield 1: combat, night ops": [{"effect": "Battlefield: Combat", "args": {"theme": "Night ops (blue)"}}],
      "Assetto Corsa: gear & RPM": [{"effect": "Aurora", "opacity": 0.35}, {"effect": "Assetto Corsa: Gear & RPM"}],
      "Rainbow keys + music on strips": [{"effect": "Rainbow Wave", "zone": "Keys only"}, {"effect": "Audio Pulse", "zone": "Side strips only", "blend": "Normal"}],
      "Black hole + pulsing strips": [{"effect": "Black Hole", "zone": "Keys only"}, {"effect": "Breathing", "args": {"color": "#ff6a00"}, "zone": "Side strips only", "blend": "Normal"}],
      "Generic game: WASD + damage flash": [{"effect": "Rainbow Wave", "opacity": 0.25}, {"effect": "Gaming Keys", "args": {"keys": "WASD + abilities"}}, {"effect": "Damage Flash (screen)"}, {"effect": "Key Fade", "args": {"style": "Color", "color": "#ffffff"}}],
      "Starfield + rain": [{"effect": "Starfield"}, {"effect": "Rain", "blend": "Add", "opacity": 0.7}]
    }
    """;

    public static JsonObject Builtin() => JsonNode.Parse(BuiltinJson)!.AsObject();

    /// <summary>Fill in each layer's defaults the same way the web UI does (incl. the effect's
    /// preferred blend mode), dropping layers whose effect doesn't exist.</summary>
    public static JsonArray Normalize(JsonArray layers)
    {
        var output = new JsonArray();
        foreach (var node in layers)
        {
            if (node is not JsonObject layer || (string?)layer["effect"] is not string name
                || !EffectRegistry.ByName.TryGetValue(name, out var info))
                continue;
            output.Add(new JsonObject
            {
                ["effect"] = name,
                ["args"] = layer["args"]?.DeepClone() ?? new JsonObject(),
                ["speed"] = layer["speed"]?.DeepClone() ?? 1.0,
                ["blend"] = (string?)layer["blend"] is { Length: > 0 } b ? b : info.DefaultBlend,
                ["opacity"] = layer["opacity"]?.DeepClone() ?? 1.0,
                ["zone"] = layer["zone"]?.DeepClone() ?? "All",
            });
        }
        return output;
    }
}
