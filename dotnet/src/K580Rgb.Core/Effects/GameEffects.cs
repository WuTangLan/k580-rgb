using System.Collections.Concurrent;
using K580Rgb.Core.Device;
using K580Rgb.Core.Sources;
using static K580Rgb.Core.Effects.ColorMath;
using static K580Rgb.Core.Layout;

namespace K580Rgb.Core.Effects;

/// <summary>Gear on the numpad, RPM bar and shift lights from Assetto Corsa / ACC telemetry.
/// Transparent when you're not driving, so layers below show through.</summary>
sealed class AssettoCorsaGear(EffectArgs a) : Effect(a)
{
    public static readonly EffectInfo Info = new("Assetto Corsa: Gear & RPM",
        [ParamDef.Choice("shift lights", "F-row + numpad", "Numpad only"), ParamDef.Choice("side strips", "RPM bar", "Off"),
         ParamDef.Number("redline", 0.95), ParamDef.Choice("digit color", "RPM", "Color"), ParamDef.Color("color", "#ffffff")],
        a => new AssettoCorsaGear(a), Source: "game", DefaultBlend: "Normal");

    // 3x5 digits drawn on the numpad: rows Num / * | 7 8 9 | 4 5 6 | 1 2 3 | 0 0 .
    static readonly int[][] Numpad = [[38, 39, 40], [59, 60, 61], [80, 81, 82], [101, 102, 103], [123, 123, 124]];
    static readonly int[] NumpadBar = [104, 62, 41];          // Enter, +, - : RPM bar bottom to top
    static readonly int[] FRow = [.. Enumerable.Range(1, 12)];  // F1..F12 shift lights
    static readonly Dictionary<string, string[]> Glyphs = new()
    {
        ["R"] = ["##.", "#.#", "##.", "#.#", "#.#"], ["N"] = ["##.", "#.#", "#.#", "#.#", "#.#"],
        ["1"] = [".#.", "##.", ".#.", ".#.", "###"], ["2"] = ["###", "..#", "###", "#..", "###"],
        ["3"] = ["###", "..#", "###", "..#", "###"], ["4"] = ["#.#", "#.#", "###", "..#", "..#"],
        ["5"] = ["###", "#..", "###", "..#", "###"], ["6"] = ["###", "#..", "###", "#.#", "###"],
        ["7"] = ["###", "..#", "..#", "..#", "..#"], ["8"] = ["###", "#.#", "###", "#.#", "###"],
        ["9"] = ["###", "#.#", "###", "..#", "###"],
    };
    static readonly (double, Rgb)[] RpmRamp =
        [(0, new(0, 255, 0)), (0.75, new(0, 255, 0)), (0.87, new(255, 200, 0)), (0.95, new(255, 0, 0)), (1, new(255, 0, 0))];
    static readonly Rgb ShiftBlue = new(0, 80, 255);

    readonly bool _fRow = a.Str("shift lights") == "F-row + numpad";
    readonly bool _strips = a.Str("side strips") == "RPM bar";
    readonly bool _rpmDigits = a.Str("digit color") == "RPM";
    readonly double _redline = Clamp(a.Num("redline"), 0.5, 1.0);
    readonly Rgb _color = a.Color("color");

    public override Rgb?[] Frame(double t)
    {
        var output = Transparent();                   // transparent except what we own
        var s = Games.Assetto.Read();
        if (s == null)
            return output;
        IEnumerable<int> owned = Numpad.SelectMany(r => r).Concat(NumpadBar);
        if (_fRow) owned = owned.Concat(FRow);
        if (_strips) owned = owned.Concat(SideLeft).Concat(SideRight);
        foreach (int i in owned)
            output[i] = Rgb.Black;
        double frac = Clamp((double)s.Rpm / s.MaxRpm);
        bool shift = frac >= _redline;
        bool blink = shift && (int)(t * 10) % 2 == 0;
        var rpmCol = blink ? ShiftBlue : Ramp(RpmRamp, frac / Math.Max(_redline, 0.01) * 0.95);
        var digitCol = _rpmDigits ? rpmCol : _color;
        string gear = Glyphs.ContainsKey(s.Gear) ? s.Gear : int.TryParse(s.Gear, out int g) ? Math.Min(g, 9).ToString() : "N";
        var glyph = Glyphs.GetValueOrDefault(gear, Glyphs["N"]);
        for (int r = 0; r < glyph.Length; r++)
            for (int c = 0; c < glyph[r].Length; c++)
                if (glyph[r][c] == '#')
                    output[Numpad[r][c]] = digitCol;
        for (int i = 0; i < NumpadBar.Length; i++)    // 3-step vertical RPM bar
            if (frac >= (i + 1) / 3.6)
                output[NumpadBar[i]] = rpmCol;
        if (_strips)                                  // both strips fill front to back
        {
            int n = (int)(Clamp((frac - 0.3) / Math.Max(_redline - 0.3, 0.01)) * 7 + 0.5);
            foreach (var strip in new[] { SideLeft, SideRight })
                for (int i = 0; i < strip.Length; i++)
                    if (blink) output[strip[i]] = ShiftBlue;
                    else if (i < n) output[strip[i]] = Ramp(RpmRamp, 0.6 + (i + 1) / 7.0 * 0.35);
        }
        if (_fRow)
        {
            int lit = (int)(Clamp((frac - 0.5) / Math.Max(_redline - 0.5, 0.01)) * FRow.Length + 0.5);
            for (int i = 0; i < FRow.Length; i++)
                if (blink) output[FRow[i]] = ShiftBlue;
                else if (i < lit) output[FRow[i]] = Ramp(RpmRamp, 0.5 + (i + 1.0) / FRow.Length * 0.5 * _redline / 0.95);
        }
        return output;
    }
}

/// <summary>Rocket Mania Deluxe: when rockets launch in the game, light streaks shoot from the
/// Caps rows to the middle of the keyboard and explode into fireworks.</summary>
sealed class RocketManiaEffect : AudioEffect
{
    public static readonly EffectInfo Info = new("Rocket Mania: Launch & Fireworks",
        [ParamDef.Choice("rockets", "Rainbow", "Color"), ParamDef.Color("color", "#ffb000"), ParamDef.Text("game folder", ""),
         ParamDef.Choice("demo", "Off", "Every 3 s")],
        a => new RocketManiaEffect(a), Source: "game");
    static readonly double[] Rows = [3.0, 4.0, 5.0];   // Tab, Caps, Shift row centres
    const double MidX = Width * 0.5;
    const double Travel = 0.45;                         // s from the left edge to the middle
    const double Burst = 1.3;                           // s a burst lasts

    readonly string _folder;
    readonly bool _rainbow, _demo;
    readonly Rgb _color;
    readonly ConcurrentQueue<string> _pending = new();  // events from the audio thread
    readonly List<(double T0, double Row, double? Hue)> _rockets = [];
    List<(double T0, double X, double Y, double? Hue)> _bursts = [];
    double _launchT = -9.0, _nextDemo;
    readonly Random _rnd = new();

    RocketManiaEffect(EffectArgs a) : base(a)
    {
        _folder = a.Str("game folder").Trim().Trim('"');
        _rainbow = a.Str("rockets") == "Rainbow";
        _demo = a.Str("demo") == "Every 3 s";
        _color = a.Color("color");
        Audio.AddListener(OnAudio);
    }

    public override void Dispose()
    {
        Audio.RemoveListener(OnAudio);
        base.Dispose();
    }

    void OnAudio(float[] block)
    {
        foreach (var kind in Games.RocketMania.Feed(_folder, block))
            _pending.Enqueue(kind);
    }

    double? Hue() => _rainbow ? _rnd.NextDouble() : null;

    void Launch(double t)
    {
        _launchT = t;
        int count = new[] { 1, 2, 2, 3 }[_rnd.Next(4)];
        for (int n = 0; n < count; n++)
            _rockets.Add((t + n * 0.09, Rows[(n + _rnd.Next(3)) % 3], Hue()));
    }

    Rgb Col(double? hue, double v = 1.0) => Scale(hue is double h ? Hsv(h) : _color, v);

    double Uniform(double lo, double hi) => lo + _rnd.NextDouble() * (hi - lo);

    public override Rgb?[] Frame(double t)
    {
        while (_pending.TryDequeue(out var kind))             // events from the game's sounds
        {
            if (kind == "launch")
                Launch(t);
            else
                _bursts.Add((t, Uniform(7, 20), Uniform(0.3, 2.8), Hue()));
        }
        if (_demo && t >= _nextDemo)
        {
            _nextDemo = t + 3.0;
            Launch(t);
        }
        foreach (var r in _rockets.ToList())                  // arrived -> explode at the middle
        {
            if (t - r.T0 < Travel)
                continue;
            _rockets.Remove(r);
            _bursts.Add((t, MidX + Uniform(-1.5, 1.5), r.Row - 1.0, r.Hue));
        }
        _bursts = [.. _bursts.Where(b => t - b.T0 < Burst).TakeLast(8)];
        return base.Frame(t);
    }

    protected override Rgb? Color(Key key, double t)
    {
        Rgb? output = null;
        void Add(Rgb c) => output = output is Rgb o ? Max(o, c) : c;

        if (key.Side == "left")                               // launch pad ignites bottom to top
        {
            double age = t - _launchT;
            if (age >= 0 && age < 0.9 && (Height - key.Cy) / Height <= Math.Min(1.0, age / 0.3))
            {
                double fade = 1 - Math.Max(0.0, age - 0.3) / 0.6;
                Add(Scale(Lerp(new Rgb(255, 255, 255), new Rgb(255, 120, 0), age / 0.9), fade));
            }
        }
        if (key.Side == "right" && _bursts.Count > 0)         // strip flashes with each burst
        {
            var (tb, _, _, hue) = _bursts[^1];
            double v = Math.Max(0.0, 1 - (t - tb) / 0.7);
            if (v > 0)
                Add(Col(hue, v));
        }
        foreach (var (t0, row, hue) in _rockets)              // streaks: bright head, coloured tail
        {
            double age = t - t0;
            if (age < 0 || key.Side != null || Math.Abs(key.Cy - row) > 0.5)
                continue;
            double p = 1 - Math.Pow(1 - age / Travel, 2);     // ease-out
            double d = p * MidX - key.Cx;
            if (d > -0.6 && d < 4.5)
            {
                double v = d < 0.6 ? 1.0 : Math.Max(0.0, 1 - (d - 0.6) / 3.9);
                Add(Lerp(Col(hue, v), new Rgb(255, 255, 255), d < 0.6 ? 0.7 : 0.0));
            }
        }
        foreach (var (t0, bx, by, hue) in _bursts)            // firework: flash, ring, sparkles
        {
            double age = t - t0;
            double dist = Ripple.Hypot(key.Cx - bx, (key.Cy - by) * 1.6);
            double fade = Math.Pow(1 - age / Burst, 1.5);
            if (age < 0.12 && dist < 2.0)
                Add(new Rgb(255, 255, 255));
            double ring = Math.Abs(dist - age * 9);
            if (ring < 1.3 && age * 9 < 9)
                Add(Col(hue, (1 - ring / 1.3) * fade));
            if (dist < age * 9 && Sparkle(key.Index, (int)(t0 * 100), (int)(t * 12)) % 9 == 0)
                Add(Lerp(Col(hue, fade), new Rgb(255, 255, 255), 0.5));
        }
        return output;
    }

    /// <summary>Deterministic per (key, burst, time step) so sparkles hold for a moment.</summary>
    static uint Sparkle(int a, int b, int c)
    {
        uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u ^ (uint)c * 0xC2B2AE3Du;
        h ^= h >> 15;
        h *= 0x2C1B3C6Du;
        return h ^ (h >> 12);
    }
}

/// <summary>Highlights the keys you play with; everything else is left to the layers below.</summary>
sealed class GamingKeys : Effect
{
    static readonly Dictionary<string, string[]> Sets = new()
    {
        ["WASD"] = ["W", "A", "S", "D"],
        ["WASD + abilities"] = ["W", "A", "S", "D", "Q", "E", "R", "F", "1", "2", "3", "4", "5", "Shift", "Ctrl", ""],
        ["Arrows"] = ["↑", "←", "↓", "→"],
    };
    public static readonly EffectInfo Info = new("Gaming Keys",
        [ParamDef.Choice("keys", [.. Sets.Keys]), ParamDef.Color("color", "#ff2030")],
        a => new GamingKeys(a), DefaultBlend: "Normal");

    readonly HashSet<int> _keys;
    readonly Rgb _color;

    GamingKeys(EffectArgs a) : base(a)
    {
        var labels = Sets.GetValueOrDefault(a.Str("keys"), Sets["WASD"]).ToHashSet();
        _keys = [.. ByIndex.Values.Where(k => !k.Iso && k.Side == null && labels.Contains(k.Label)
                                             && k.X < 18                                      // not the numpad
                                             && (k.Label is not ("Shift" or "Ctrl") || k.X < 1))   // left Shift/Ctrl only
                                  .Select(k => k.Index)];
        _color = a.Color("color");
    }

    protected override Rgb? Color(Key key, double t) => _keys.Contains(key.Index) ? _color : null;
}

static class ScreenEdges
{
    /// <summary>How much redder than green/blue a set of screen cells is, 0..1.</summary>
    public static double Redness(IEnumerable<Rgb> cells)
    {
        double sum = 0;
        int n = 0;
        foreach (var c in cells)
        {
            sum += Math.Max(0.0, c.R - (c.G + c.B) / 2.0);
            n++;
        }
        return n == 0 ? 0 : sum / (n * 255);
    }
}

/// <summary>Flashes when your screen's edges turn red - how many games show you're taking damage.</summary>
sealed class DamageFlash : Effect
{
    public static readonly EffectInfo Info = new("Damage Flash (screen)",
        [ParamDef.Color("color", "#ff0000"), ParamDef.Number("sensitivity", 1.0)],
        a => new DamageFlash(a), Source: "screen");
    readonly ScreenSource _screen = Capture.Screen;
    readonly Rgb _color;
    readonly double _sensitivity;
    double? _base, _last;
    double _flash;

    DamageFlash(EffectArgs a) : base(a)
    {
        _color = a.Color("color");
        _sensitivity = a.Num("sensitivity");
        _screen.Acquire();
    }

    public override void Dispose() => _screen.Release();

    public override Rgb?[] Frame(double t)
    {
        var g = _screen.Grid;
        double red = ScreenEdges.Redness(g[0].Concat(g[^1]).Concat(g.Select(r => r[0])).Concat(g.Select(r => r[^1])));
        _base = _base is double b ? b * 0.97 + red * 0.03 : red;
        double dt = _last is double last ? t - last : 0;
        _last = t;
        _flash = red - _base > 0.12 / Math.Max(_sensitivity, 0.1) ? 1.0 : Math.Max(0.0, _flash - dt * 1.8);
        var output = Transparent();
        if (_flash == 0)
            return output;
        var c = Lerp(Rgb.Black, _color, _flash);
        foreach (var k in Lit)
            output[k.Index] = c;
        return output;
    }
}

/// <summary>Plays a GIF (or shows an image) across the keys at its own frame timing.</summary>
sealed class GifPlayer : Effect
{
    public static readonly EffectInfo Info = new("GIF / Image",
        [ParamDef.Media("file"), ParamDef.Choice("fit", "Cover", "Contain", "Stretch"), ParamDef.Number("brightness", 1.0)],
        a => new GifPlayer(a), Source: "media");
    readonly Clip? _clip;
    readonly double _brightness;

    GifPlayer(EffectArgs a) : base(a)
    {
        string? name = a.Str("file");
        if (name == ParamDef.MediaMarker)
            name = Media.Library().FirstOrDefault()?.Name;
        _clip = name != null ? Media.Load(name, a.Str("fit")) : null;
        _brightness = a.Num("brightness");
    }

    public override Rgb?[] Frame(double t)
    {
        var output = Blank();
        if (_clip == null)
            return output;
        var f = _clip.At(t * Speed);
        for (int i = 0; i < f.Length; i++)
            output[i] = _brightness == 1.0 ? f[i]
                : new Rgb((byte)Math.Min(255, (int)(f[i].R * _brightness)), (byte)Math.Min(255, (int)(f[i].G * _brightness)),
                          (byte)Math.Min(255, (int)(f[i].B * _brightness)));
        return output;
    }
}

/// <summary>Your text scrolling across the keyboard.</summary>
sealed class ScrollingText : Effect
{
    public static readonly EffectInfo Info = new("Scrolling Text",
        [ParamDef.Text("text", "HELLO K580"), ParamDef.Choice("size", "Full height", "Large", "Medium"),
         ParamDef.Choice("style", "Rainbow", "Color"), ParamDef.Color("color", "#ffffff"), ParamDef.Color("background", "#000000")],
        a => new ScrollingText(a), Source: "media");
    static readonly Dictionary<string, double> Sizes = new() { ["Full height"] = 0.97, ["Large"] = 0.75, ["Medium"] = 0.55 };

    readonly TextStrip _strip;
    readonly bool _rainbow;
    readonly Rgb _color, _background;

    ScrollingText(EffectArgs a) : base(a)
    {
        var text = a.Str("text");
        _strip = new TextStrip(text[..Math.Min(200, text.Length)], Sizes.GetValueOrDefault(a.Str("size"), 0.97));
        _rainbow = a.Str("style") == "Rainbow";
        (_color, _background) = (a.Color("color"), a.Color("background"));
    }

    public override Rgb?[] Frame(double t)
    {
        t *= Speed;
        var cover = _strip.Coverage(t * 40);                  // 40 px/s = 5 key widths per second
        var output = Blank();
        foreach (var key in Lit)
        {
            var fg = _rainbow ? Hsv(key.Cx / Width * 0.5 + t * 0.1) : _color;
            output[key.Index] = Lerp(_background, fg, Math.Min(1.0, cover[key.Index] * 1.6));
        }
        return output;
    }
}
