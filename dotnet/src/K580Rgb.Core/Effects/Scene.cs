using System.Text.Json.Nodes;
using K580Rgb.Core.Device;

namespace K580Rgb.Core.Effects;

public static class EffectRegistry
{
    public static readonly IReadOnlyList<EffectInfo> All =
    [
        RainbowWave.Info, VincentSunset.Info, Aurora.Info, Fire.Info, BlackHole.Info, Plasma.Info, Spiral.Info,
        ColorShift.Info, Breathing.Info, Rain.Info, Starfield.Info, Gradient.Info, Solid.Info,
        GifPlayer.Info, ScrollingText.Info,
        AudioSpectrum.Info, AudioCenter.Info, AudioVU.Info, AudioWaterfall.Info, AudioBeatRipple.Info,
        AudioColorFlow.Info, AudioPulse.Info,
        ScreenAmbience.Info, KeyFade.Info, Ripple.Info, Heatmap.Info,
        AssettoCorsaGear.Info, RocketManiaEffect.Info, BattlefieldCombat.Info, GamingKeys.Info, DamageFlash.Info,
    ];

    public static readonly IReadOnlyDictionary<string, EffectInfo> ByName = All.ToDictionary(e => e.Name);

    public static readonly string[] Blends = ["Lighten", "Add", "Normal"];
    public static readonly string[] Zones = ["All", "Keys only", "Side strips only"];
    static readonly Dictionary<string, HashSet<int>> ZoneMask = new()
    {
        ["Keys only"] = [.. Layout.ByIndex.Values.Where(k => k.Side == null).Select(k => k.Index)],
        ["Side strips only"] = [.. Layout.ByIndex.Values.Where(k => k.Side != null).Select(k => k.Index)],
    };

    public static Effect Create(string name, JsonObject? args = null, double speed = 1.0)
    {
        if (!ByName.TryGetValue(name, out var info))
            throw new KeyNotFoundException($"unknown effect '{name}'");
        return info.Factory(new EffectArgs(info, args, speed));
    }

    /// <summary>specs: [{effect, args, speed, blend, opacity, zone}, ...] bottom layer first.</summary>
    public static Scene Scene(JsonArray specs)
    {
        var layers = new List<Scene.Layer>();
        try
        {
            foreach (var node in specs)
            {
                var s = node!.AsObject();
                var eff = Create((string)s["effect"]!, s["args"] as JsonObject, (double?)s["speed"] ?? 1.0);
                ZoneMask.TryGetValue((string?)s["zone"] ?? "All", out var mask);
                layers.Add(new(eff, (string?)s["blend"] ?? "Lighten", ColorMath.Clamp((double?)s["opacity"] ?? 1.0), mask));
            }
        }
        catch
        {
            foreach (var l in layers)
                l.Effect.Dispose();
            throw;
        }
        return new Scene(layers);
    }

    public static JsonArray ToJson() => [.. All.Select(e => (JsonNode)e.ToJson())];
}

/// <summary>A stack of effects, bottom layer first. Frames and key presses come from different
/// threads, so both go through one lock.</summary>
public sealed class Scene : IDisposable
{
    public sealed record Layer(Effect Effect, string Blend, double Opacity, HashSet<int>? Mask);

    readonly IReadOnlyList<Layer> _layers;
    readonly Lock _lock = new();

    public Scene(IReadOnlyList<Layer> layers)
    {
        _layers = layers;
        Name = string.Join(" + ", layers.Select(l => l.Effect.Name));
    }

    public string Name { get; }
    public bool Reactive => _layers.Any(l => l.Effect.Reactive);

    public void KeyPressed(int index, double t)
    {
        lock (_lock)
            foreach (var l in _layers)
                if (l.Effect.Reactive)
                    l.Effect.KeyPressed(index, t);
    }

    public void Dispose()
    {
        lock (_lock)
            foreach (var l in _layers)
                l.Effect.Dispose();
    }

    public Rgb[] Frame(double t)
    {
        var output = new Rgb[K580.NumLeds];
        lock (_lock)
        {
            for (int n = 0; n < _layers.Count; n++)
            {
                var (eff, blend, op, mask) = _layers[n];
                var f = eff.Frame(t);
                for (int i = 0; i < output.Length; i++)
                {
                    if (f[i] is not Rgb c || (mask != null && !mask.Contains(i)))
                        continue;            // transparent, or outside the layer's zone
                    var o = output[i];
                    output[i] = n == 0 || blend == "Normal" ? ColorMath.Lerp(o, c, op)
                        : blend == "Add" ? new Rgb((byte)Math.Min(255, o.R + (int)(c.R * op)),
                                                   (byte)Math.Min(255, o.G + (int)(c.G * op)),
                                                   (byte)Math.Min(255, o.B + (int)(c.B * op)))
                        : new Rgb((byte)Math.Max(o.R, (int)(c.R * op)), (byte)Math.Max(o.G, (int)(c.G * op)),
                                  (byte)Math.Max(o.B, (int)(c.B * op)));
                }
            }
        }
        return output;
    }
}
