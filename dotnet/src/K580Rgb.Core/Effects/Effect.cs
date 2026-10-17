// Software lighting effects rendered on the PC.
//
// Every effect maps (key, time) -> RGB for the 107 LEDs. The same frames are used for
// the live preview in the UI, for one-off per-key "snapshots" written to the stock
// Custom mode, and for streaming once the keyboard runs the direct-mode firmware.
using System.Globalization;
using System.Text.Json.Nodes;
using K580Rgb.Core.Device;

namespace K580Rgb.Core.Effects;

/// <summary>A UI parameter: '#rrggbb' colour, number, text, list of choices (first = default),
/// {"palette": [...]} (editable list of 2-8 colours) or "@media" (a file from the media library).</summary>
public sealed record ParamDef(string Name, JsonNode Default)
{
    public const string MediaMarker = "@media";
    public const string PaletteKey = "palette";

    public static ParamDef Color(string name, string hex) => new(name, JsonValue.Create(hex));
    public static ParamDef Number(string name, double v) => new(name, JsonValue.Create(v));
    public static ParamDef Text(string name, string v) => new(name, JsonValue.Create(v));
    public static ParamDef Choice(string name, params string[] options) => new(name, new JsonArray([.. options.Select(o => (JsonNode)o)]));
    public static ParamDef Palette(string name, params string[] colors) =>
        new(name, new JsonObject { [PaletteKey] = new JsonArray([.. colors.Select(c => (JsonNode)c)]) });
    public static ParamDef Media(string name) => new(name, JsonValue.Create(MediaMarker));
}

public sealed record EffectInfo(string Name, ParamDef[] Params, Func<EffectArgs, Effect> Factory,
                                bool Reactive = false, string? Source = null, string DefaultBlend = "Lighten",
                                bool Animated = true)
{
    public JsonObject ToJson()
    {
        var p = new JsonObject();
        foreach (var d in Params)
            p[d.Name] = d.Default.DeepClone();
        return new() { ["name"] = Name, ["params"] = p, ["animated"] = Animated, ["reactive"] = Reactive,
                       ["source"] = Source, ["blend"] = DefaultBlend };
    }
}

/// <summary>An effect's arguments from the UI, with the declared defaults filled in.</summary>
public sealed class EffectArgs
{
    readonly Dictionary<string, JsonNode?> _values = [];

    public EffectArgs(EffectInfo info, JsonObject? args, double speed)
    {
        Info = info;
        Speed = speed;
        foreach (var d in info.Params)
        {
            JsonNode? v = args != null && args.TryGetPropertyValue(d.Name, out var given) && given != null
                ? given : d.Default is JsonArray choices ? choices[0] : d.Default;
            _values[d.Name] = v;
        }
    }

    public EffectInfo Info { get; }
    public double Speed { get; }

    public JsonNode? Raw(string name) => _values[name];

    public string Str(string name) => _values[name] switch
    {
        JsonValue v when v.TryGetValue(out string? s) => s,
        null => "",
        var n => n.ToJsonString(),
    };

    public double Num(string name)
    {
        try
        {
            return JsonNum.Get(_values[name]);
        }
        catch (FormatException)
        {
            throw new FormatException($"'{name}' must be a number");
        }
    }

    public Rgb Color(string name) => ColorMath.HexToRgb(Str(name));

    /// <summary>A list of hex colours (from the UI) or the {"palette": [...]} default.</summary>
    public string[] Palette(string name)
    {
        var n = _values[name];
        if (n is JsonObject o)
            n = o[ParamDef.PaletteKey];
        if (n is JsonArray a && a.Count >= 2)
            return [.. a.Take(8).Select(c => c?.ToString() ?? "#000000")];
        var d = Info.Params.First(p => p.Name == name).Default[ParamDef.PaletteKey]!.AsArray();
        return [.. d.Select(c => c!.ToString())];
    }
}

/// <summary>Base class. Frames are 126 entries; null = transparent (layers below show through).</summary>
public abstract class Effect(EffectArgs a) : IDisposable
{
    protected readonly EffectArgs P = a;
    protected readonly double Speed = a.Speed;

    public EffectInfo Definition => P.Info;
    public string Name => P.Info.Name;
    /// <summary>Wants key presses.</summary>
    public virtual bool Reactive => P.Info.Reactive;

    protected virtual Rgb? Color(Key key, double t) => throw new NotImplementedException();

    public virtual void KeyPressed(int index, double t) { }

    /// <summary>Release capture sources etc. when the effect is replaced.</summary>
    public virtual void Dispose() { }

    public virtual Rgb?[] Frame(double t)
    {
        var output = Blank();
        foreach (var key in Layout.Lit)
            output[key.Index] = Color(key, t * Speed);
        return output;
    }

    protected static Rgb?[] Blank()
    {
        var output = new Rgb?[K580.NumLeds];
        Array.Fill(output, Rgb.Black);
        return output;
    }

    protected static Rgb?[] Transparent() => new Rgb?[K580.NumLeds];
}
