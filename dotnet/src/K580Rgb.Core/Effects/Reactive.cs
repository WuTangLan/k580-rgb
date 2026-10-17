using K580Rgb.Core.Device;
using K580Rgb.Core.Sources;
using static K580Rgb.Core.Effects.ColorMath;
using static K580Rgb.Core.Layout;

namespace K580Rgb.Core.Effects;

/// <summary>Base for music effects. Each frame, Fb holds the chosen frequency range's band
/// levels (0..1, sensitivity applied) and FLevel their average loudness.
/// Subclasses put per-frame logic in Update(t).</summary>
abstract class AudioEffect : Effect
{
    // Which of the 22 analysis bands (40 Hz - 16 kHz, log spaced) each "react to" choice uses
    static readonly Dictionary<string, (int Lo, int Hi)> FocusBands = new()
    {
        ["All"] = (0, 22), ["Low (bass)"] = (0, 7), ["Mid"] = (7, 17), ["High (treble)"] = (17, 22),
    };
    static readonly Dictionary<string, double> FocusBoost = new()   // treble is quieter
    {
        ["All"] = 1.0, ["Low (bass)"] = 1.0, ["Mid"] = 1.2, ["High (treble)"] = 1.6,
    };

    /// <summary>The "react to" + "sensitivity" controls every music effect has.</summary>
    protected static ParamDef[] AudioControls(string focus = "All") =>
    [
        ParamDef.Choice("react to", [focus, .. FocusBands.Keys.Where(f => f != focus)]),
        ParamDef.Number("sensitivity", 1.0),
    ];

    protected readonly AudioSource Audio = Capture.Audio;
    protected readonly (int Lo, int Hi) Focus;
    readonly double _gain;
    protected double[] Fb;
    protected double FLevel;

    protected AudioEffect(EffectArgs a) : base(a)
    {
        bool hasFocus = a.Info.Params.Any(p => p.Name == "react to");
        string focus = hasFocus ? a.Str("react to") : "All";
        Focus = FocusBands.GetValueOrDefault(focus, FocusBands["All"]);
        _gain = (hasFocus ? a.Num("sensitivity") : 1.0) * FocusBoost.GetValueOrDefault(focus, 1.0);
        Fb = new double[Focus.Hi - Focus.Lo];
        Audio.Acquire();
    }

    public override void Dispose() => Audio.Release();

    public override Rgb?[] Frame(double t)
    {
        var levels = Audio.Levels;
        var fb = new double[Focus.Hi - Focus.Lo];
        for (int i = 0; i < fb.Length; i++)
            fb[i] = Math.Min(1.0, levels[Focus.Lo + i] * _gain);
        Fb = fb;
        FLevel = fb.Average();
        Update(t);
        return base.Frame(t);
    }

    protected virtual void Update(double t) { }

    /// <summary>Level of the chosen range at position frac (0 = lowest, 1 = highest frequency).</summary>
    protected double BandAt(double frac) => Fb[Math.Clamp((int)(frac * Fb.Length), 0, Fb.Length - 1)];
}

/// <summary>Equaliser bars: the chosen frequency range spread across the key columns.</summary>
sealed class AudioSpectrum(EffectArgs a) : AudioEffect(a)
{
    public static readonly EffectInfo Info = new("Audio Spectrum",
        [.. AudioControls(), ParamDef.Choice("style", "Rainbow", "Gradient"), ParamDef.Color("from", "#00ff60"),
         ParamDef.Color("to", "#ff0030")],
        a => new AudioSpectrum(a), Source: "audio");
    readonly bool _rainbow = a.Str("style") == "Rainbow";
    readonly Rgb _from = a.Color("from"), _to = a.Color("to");

    protected override Rgb? Color(Key key, double t)
    {
        double level = BandAt(Column(key, 1000) / 1000.0);
        double up = (Height - key.Cy) / Height;
        double lit = Clamp((level - up) * 6 + 1);
        if (lit == 0)
            return Rgb.Black;
        var c = _rainbow ? Hsv(key.Cx / Width * 0.8 + t * 0.02) : Lerp(_from, _to, up);
        return Lerp(Rgb.Black, c, lit);
    }
}

/// <summary>Whole keyboard pulses with the music (bass by default); colour drifts slowly.</summary>
sealed class AudioPulse(EffectArgs a) : AudioEffect(a)
{
    public static readonly EffectInfo Info = new("Audio Pulse",
        [.. AudioControls("Low (bass)"), ParamDef.Choice("style", "Rainbow", "Color"), ParamDef.Color("color", "#ff0050")],
        a => new AudioPulse(a), Source: "audio");
    readonly bool _rainbow = a.Str("style") == "Rainbow";
    readonly Rgb _color = a.Color("color");

    protected override Rgb? Color(Key key, double t)
    {
        var c = _rainbow ? Hsv(t * 0.03 + key.Cx / Width * 0.15) : _color;
        return Lerp(Rgb.Black, c, 0.05 + 0.95 * Math.Pow(FLevel, 1.5));
    }
}

/// <summary>Spectrum mirrored from the middle: low notes in the centre, high ones at both edges.</summary>
sealed class AudioCenter(EffectArgs a) : AudioEffect(a)
{
    public static readonly EffectInfo Info = new("Audio Center Bars",
        [.. AudioControls(), ParamDef.Choice("style", "Rainbow", "Gradient"), ParamDef.Color("from", "#ff00a0"),
         ParamDef.Color("to", "#00e0ff")],
        a => new AudioCenter(a), Source: "audio");
    readonly bool _rainbow = a.Str("style") == "Rainbow";
    readonly Rgb _from = a.Color("from"), _to = a.Color("to");

    protected override Rgb? Color(Key key, double t)
    {
        double dist = Math.Abs(key.Cx - Width / 2) / (Width / 2);
        double level = BandAt(dist);
        double up = (Height - key.Cy) / Height;
        double lit = Clamp((level - up) * 6 + 1);
        if (lit == 0)
            return Rgb.Black;
        var c = _rainbow ? Hsv(dist * 0.7 + t * 0.03) : Lerp(_from, _to, dist);
        return Lerp(Rgb.Black, c, lit);
    }
}

/// <summary>Loudness meter filling the keyboard from the bottom, with a falling peak line.</summary>
sealed class AudioVU(EffectArgs a) : AudioEffect(a)
{
    public static readonly EffectInfo Info = new("Audio VU Meter", AudioControls(), a => new AudioVU(a), Source: "audio");
    double _peak, _peakT, _level;

    protected override void Update(double t)
    {
        _level = Clamp(FLevel * 2.2);
        if (_level >= _peak)
            (_peak, _peakT) = (_level, t);
        else
            _peak = Math.Max(_level, _peak - Math.Max(0.0, t - _peakT - 0.4) * 0.05);
    }

    protected override Rgb? Color(Key key, double t)
    {
        double up = (Height - key.Cy) / Height;
        if (up <= _level)
            return Ramp(Ramps.Vu, up);
        if (_peak > 0.12 && Math.Abs(up - _peak) < 0.09)     // no peak line in silence
            return new Rgb(255, 255, 255);
        return Rgb.Black;
    }
}

/// <summary>Spectrum history: newest on the bottom row, scrolling up.</summary>
sealed class AudioWaterfall : AudioEffect
{
    public static readonly EffectInfo Info = new("Audio Waterfall", AudioControls(), a => new AudioWaterfall(a), Source: "audio");
    const int Rows = 6;
    readonly Queue<double[]> _history = new();
    double _nextPush;

    AudioWaterfall(EffectArgs a) : base(a)
    {
        for (int i = 0; i < Rows; i++)
            _history.Enqueue(Fb);
    }

    double[][] _rows = [];

    protected override void Update(double t)
    {
        if (t >= _nextPush || t < _nextPush - 1)
        {
            _nextPush = t + 0.08;
            _history.Dequeue();
            _history.Enqueue(Fb);
        }
        _rows = [.. _history];
    }

    protected override Rgb? Color(Key key, double t)
    {
        int age = Math.Min((int)((Height - key.Cy) / Height * Rows), Rows - 1);   // 0 = bottom = newest
        var row = _rows[^(1 + age)];
        return Ramp(Ramps.Heat, row[Column(key, row.Length)] * 1.1);
    }
}

/// <summary>A coloured ring bursts from the centre on every beat of the chosen range
/// (kicks with Low, snares/vocals with Mid, hi-hats with High).</summary>
sealed class AudioBeatRipple(EffectArgs a) : AudioEffect(a)
{
    public static readonly EffectInfo Info = new("Audio Beat Ripple",
        [.. AudioControls("Low (bass)"), ParamDef.Color("background", "#05000a")],
        a => new AudioBeatRipple(a), Source: "audio");
    readonly List<(double T0, double Hue)> _rings = [];
    readonly Rgb _background = a.Color("background");
    double _avg, _lastBeat = -1.0;

    protected override void Update(double t)
    {
        double e = FLevel;           // onset: loudness jumps well above its running average
        if (e > 0.3 && e > _avg * 1.35 && t - _lastBeat > 0.18)
        {
            _lastBeat = t;
            _rings.Add((t, Mod(t * 0.07)));
            if (_rings.Count > 6)
                _rings.RemoveAt(0);
        }
        _avg = _avg * 0.93 + e * 0.07;
    }

    protected override Rgb? Color(Key key, double t)
    {
        var c = _background;
        double d = Ripple.Hypot(key.Cx - Width / 2, (key.Cy - Height / 2) * 2);
        foreach (var (t0, hue) in _rings)
        {
            double r = (t - t0) * 22;
            double w = Math.Abs(d - r);
            if (w < 2.5 && r < 26)
                c = Lerp(c, Hsv(hue), (1 - w / 2.5) * (1 - r / 26));
        }
        return c;
    }
}

/// <summary>Colour follows the pitch of the music (bassy = red, bright = blue), brightness the volume.</summary>
sealed class AudioColorFlow(EffectArgs a) : AudioEffect(a)
{
    public static readonly EffectInfo Info = new("Audio Color Flow", AudioControls(), a => new AudioColorFlow(a), Source: "audio");
    double _centroid;

    protected override void Update(double t)
    {
        // spectral centroid of the chosen range, placed on the full 22-band scale so bass
        // stays red and treble blue whichever range you pick
        double total = Fb.Sum();
        if (total == 0) total = 1e-6;
        double local = Fb.Select((b, i) => i * b).Sum() / total;
        _centroid = (Focus.Lo + local) / 22;
    }

    protected override Rgb? Color(Key key, double t)
    {
        double v = Clamp(0.06 + FLevel * 2.5);
        double wave = 0.05 * Math.Sin(key.Cx * 0.4 - t * 2);
        return Hsv(_centroid * 0.75 + wave, 1, v);
    }
}

/// <summary>Each key takes the colour of the matching area of your screen.</summary>
sealed class ScreenAmbience : Effect
{
    public static readonly EffectInfo Info = new("Screen Ambience", [ParamDef.Number("saturation", 1.5)],
        a => new ScreenAmbience(a), Source: "screen");
    readonly ScreenSource _screen = Capture.Screen;
    readonly double _saturation;
    Rgb[][] _grid = [];

    ScreenAmbience(EffectArgs a) : base(a)
    {
        _saturation = a.Num("saturation");
        _screen.Acquire();
    }

    public override void Dispose() => _screen.Release();

    public override Rgb?[] Frame(double t)
    {
        _grid = _screen.Grid;
        return base.Frame(t);
    }

    protected override Rgb? Color(Key key, double t)
    {
        int rows = _grid.Length, cols = _grid[0].Length;
        var c = _grid[Math.Min((int)(key.Cy / Height * rows), rows - 1)][Column(key, cols)];
        var (h, s, v) = RgbToHsv(c.R / 255.0, c.G / 255.0, c.B / 255.0);
        return Hsv(h, Clamp(s * _saturation), v);
    }
}
