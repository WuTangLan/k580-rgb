using K580Rgb.Core.Device;
using static K580Rgb.Core.Effects.ColorMath;
using static K580Rgb.Core.Layout;

namespace K580Rgb.Core.Effects;

sealed class Solid(EffectArgs a) : Effect(a)
{
    public static readonly EffectInfo Info = new("Solid", [ParamDef.Color("color", "#00a0ff")], a => new Solid(a), Animated: false);
    readonly Rgb _color = a.Color("color");

    protected override Rgb? Color(Key key, double t) => _color;
}

/// <summary>Any number of colours across the keyboard; optionally scrolling as a seamless loop.</summary>
sealed class Gradient : Effect
{
    public static readonly EffectInfo Info = new("Gradient",
        [ParamDef.Palette("colors", "#ff0080", "#7b2cff", "#00e0ff"), ParamDef.Number("angle", 0.0),
         ParamDef.Choice("motion", "Static", "Scroll")],
        a => new Gradient(a), Animated: false);

    readonly bool _scroll;
    readonly Palette _pal;
    readonly double _angle;

    Gradient(EffectArgs a) : base(a)
    {
        _scroll = a.Str("motion") == "Scroll";
        _pal = new Palette(a.Palette("colors"), cyclic: _scroll);
        _angle = a.Num("angle") * Math.PI / 180;
    }

    protected override Rgb? Color(Key key, double t)
    {
        double x = key.Cx / Width - 0.5, y = key.Cy / Height - 0.5;
        double v = 0.5 + x * Math.Cos(_angle) + y * Math.Sin(_angle);
        return _scroll ? _pal.At(Mod(v * 0.8 - t * 0.08)) : _pal.At(v);
    }
}

sealed class RainbowWave(EffectArgs a) : Effect(a)
{
    public static readonly EffectInfo Info = new("Rainbow Wave",
        [ParamDef.Number("width", 1.0), ParamDef.Number("angle", 0.0)], a => new RainbowWave(a));
    readonly double _angle = a.Num("angle") * Math.PI / 180, _width = Math.Max(a.Num("width"), 0.05);

    protected override Rgb? Color(Key key, double t)
    {
        double pos = (key.Cx * Math.Cos(_angle) + key.Cy * Math.Sin(_angle)) / Width;
        return Hsv(pos / _width - t * 0.25);
    }
}

sealed class ColorShift(EffectArgs a) : Effect(a)
{
    public static readonly EffectInfo Info = new("Color Shift", [], a => new ColorShift(a));

    protected override Rgb? Color(Key key, double t) => Hsv(t * 0.1);
}

sealed class Breathing(EffectArgs a) : Effect(a)
{
    public static readonly EffectInfo Info = new("Breathing", [ParamDef.Color("color", "#ff3000")], a => new Breathing(a));
    readonly Rgb _color = a.Color("color");

    protected override Rgb? Color(Key key, double t)
    {
        double v = (Math.Sin(t * 2.0) + 1) / 2;
        return Lerp(Rgb.Black, _color, 0.08 + 0.92 * v * v);
    }
}

sealed class Spiral(EffectArgs a) : Effect(a)
{
    public static readonly EffectInfo Info = new("Spiral", [], a => new Spiral(a));

    protected override Rgb? Color(Key key, double t)
    {
        double ang = Math.Atan2(key.Cy - Height / 2, (key.Cx - Width / 2) / 2);
        return Hsv(ang / (2 * Math.PI) + t * 0.2);
    }
}

sealed class Plasma(EffectArgs a) : Effect(a)
{
    public static readonly EffectInfo Info = new("Plasma", [], a => new Plasma(a));

    protected override Rgb? Color(Key key, double t)
    {
        double x = key.Cx / 3, y = key.Cy / 1.5;
        double v = Math.Sin(x + t) + Math.Sin(y + t * 0.7) + Math.Sin((x + y + t) * 0.6);
        v += Math.Sin(Math.Sqrt((x - 3.5) * (x - 3.5) + (y - 2) * (y - 2)) - t);
        return Hsv(v / 8 + t * 0.03);
    }
}

sealed class Rain : Effect
{
    public static readonly EffectInfo Info = new("Rain", [ParamDef.Color("color", "#40ff80")], a => new Rain(a));
    readonly Dictionary<int, (double Phase, double Rate)> _cols = [];
    readonly Rgb _color;

    Rain(EffectArgs a) : base(a)
    {
        _color = a.Color("color");
        var rnd = new Random(7);
        foreach (var k in ByIndex.Values)
            _cols[(int)Math.Round(k.Cx * 2)] = (rnd.NextDouble() * 10, 0.5 + rnd.NextDouble());
    }

    protected override Rgb? Color(Key key, double t)
    {
        var (phase, rate) = _cols[(int)Math.Round(key.Cx * 2)];
        double head = Mod(t * rate * 3 + phase, 9) - 1.5;
        double d = head - key.Cy;
        return d >= 0 && d < 3 ? Lerp(Rgb.Black, _color, 1 - d / 3) : Rgb.Black;
    }
}

sealed class Starfield : Effect
{
    public static readonly EffectInfo Info = new("Starfield",
        [ParamDef.Color("color", "#ffffff"), ParamDef.Color("background", "#000010")], a => new Starfield(a));
    readonly Dictionary<int, (double Phase, double Period)> _seed = [];
    readonly Rgb _color, _background;

    Starfield(EffectArgs a) : base(a)
    {
        (_color, _background) = (a.Color("color"), a.Color("background"));
        var rnd = new Random(3);
        foreach (int i in ByIndex.Keys)
            _seed[i] = (rnd.NextDouble() * 6, 1.5 + rnd.NextDouble() * 3);
    }

    protected override Rgb? Color(Key key, double t)
    {
        var (phase, period) = _seed[key.Index];
        double v = Math.Pow(Math.Max(0.0, Math.Sin((t + phase) * 2 * Math.PI / period)), 6);
        return Lerp(_background, _color, v);
    }
}

/// <summary>Rings spreading from pressed keys (needs key events from the PC).</summary>
sealed class Ripple(EffectArgs a) : Effect(a)
{
    public static readonly EffectInfo Info = new("Ripple",
        [ParamDef.Color("color", "#00ffd0"), ParamDef.Color("background", "#100020")], a => new Ripple(a), Reactive: true);
    readonly List<(double X, double Y, double T0)> _waves = [];
    readonly Rgb _color = a.Color("color"), _background = a.Color("background");

    public override void KeyPressed(int index, double t)
    {
        if (!ByIndex.TryGetValue(index, out var k))
            return;
        _waves.Add((k.Cx, k.Cy, t * Speed));
        if (_waves.Count > 12)
            _waves.RemoveAt(0);
    }

    protected override Rgb? Color(Key key, double t)
    {
        var c = _background;
        foreach (var (x, y, t0) in _waves)
        {
            double r = (t - t0) * 8;
            double d = Math.Abs(Hypot(key.Cx - x, (key.Cy - y) * 1.2) - r);
            if (d < 1.2 && r < 25)
                c = Lerp(c, _color, (1 - d / 1.2) * (1 - r / 25));
        }
        return c;
    }

    internal static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);
}

/// <summary>Pressed keys light up and fade out.</summary>
sealed class KeyFade(EffectArgs a) : Effect(a)
{
    public static readonly EffectInfo Info = new("Key Fade",
        [ParamDef.Choice("style", "Rainbow", "Color"), ParamDef.Color("color", "#ff2d55"),
         ParamDef.Color("background", "#000000"), ParamDef.Number("fade", 1.5)],
        a => new KeyFade(a), Reactive: true);
    readonly Dictionary<int, (double T, Rgb Color)> _pressed = [];   // LED index -> (time, colour)
    readonly bool _rainbow = a.Str("style") == "Rainbow";
    readonly Rgb _color = a.Color("color"), _background = a.Color("background");
    readonly double _fade = Math.Max(a.Num("fade"), 0.1);

    public override void KeyPressed(int index, double t)
    {
        if (!ByIndex.TryGetValue(index, out var k))
            return;
        t *= Speed;
        _pressed[index] = (t, _rainbow ? Hsv(k.Cx / Width * 0.6 + t * 0.15) : _color);   // varies across the board and over time
    }

    protected override Rgb? Color(Key key, double t)
    {
        if (!_pressed.TryGetValue(key.Index, out var hit))
            return _background;
        double age = (t - hit.T) / _fade;
        if (age >= 1)
        {
            _pressed.Remove(key.Index);
            return _background;
        }
        return Lerp(_background, hit.Color, (1 - age) * (1 - age));
    }
}

static class Ramps
{
    public static readonly (double, Rgb)[] Fire =
        [(0, new(0, 0, 0)), (0.35, new(170, 0, 0)), (0.7, new(255, 80, 0)), (1, new(255, 210, 60))];
    public static readonly (double, Rgb)[] Heat =
        [(0, new(0, 0, 40)), (0.25, new(0, 90, 255)), (0.5, new(0, 220, 120)), (0.75, new(255, 220, 0)), (1, new(255, 30, 0))];
    public static readonly (double, Rgb)[] Vu = [(0, new(0, 255, 60)), (0.6, new(255, 220, 0)), (1, new(255, 0, 0))];
}

sealed class Fire(EffectArgs a) : Effect(a)
{
    public static readonly EffectInfo Info = new("Fire", [ParamDef.Number("height", 1.0)], a => new Fire(a));
    readonly double _height = a.Num("height");

    protected override Rgb? Color(Key key, double t)
    {
        double x = key.Cx, up = (Height - key.Cy) / Height;
        double n = 0.45 + 0.25 * Math.Sin(x * 1.3 + t * 5.1) + 0.15 * Math.Sin(x * 2.7 - t * 7.3)
                   + 0.1 * Math.Sin(x * 0.6 + t * 2.9);
        double heat = Clamp(1 - up / Math.Max(n * _height + 0.15, 0.05));
        return Ramp(Ramps.Fire, heat * (0.85 + 0.15 * Math.Sin(t * 23 + x * 5)));
    }
}

sealed class Aurora(EffectArgs a) : Effect(a)
{
    public static readonly EffectInfo Info = new("Aurora", [], a => new Aurora(a));

    protected override Rgb? Color(Key key, double t)
    {
        double x = key.Cx, y = key.Cy;
        double v = (Math.Sin(x * 0.35 + t * 0.8 + Math.Sin(y * 0.9 + t * 0.5) * 1.5) + 1) / 2;
        double hue = 0.42 + 0.2 * Math.Sin(x * 0.15 - t * 0.2) + 0.08 * Math.Sin(y * 0.7 + t * 0.3);
        return Hsv(hue, 0.9, 0.15 + 0.85 * v * v);
    }
}

/// <summary>Retro-futuristic synthwave: pink, cyan and purple gradients drifting slowly across the keys.</summary>
sealed class VincentSunset : Effect
{
    public static readonly EffectInfo Info = new("Vincent Sunset",
        [ParamDef.Palette("colors", "#7b2cff", "#ff2e88", "#14e1ff")], a => new VincentSunset(a));
    readonly Palette _pal;
    readonly Rgb _horizon;

    VincentSunset(EffectArgs a) : base(a)
    {
        var colors = a.Palette("colors");
        _pal = new Palette(colors, cyclic: true);              // loops seamlessly
        _horizon = HexToRgb(colors[colors.Length > 1 ? 1 : 0]);
    }

    protected override Rgb? Color(Key key, double t)
    {
        double x = key.Cx / Width, y = key.Cy / Height;
        const double tau = 2 * Math.PI;
        // two slow drifting waves, one bending the other, plus a lower "horizon" bias
        double v = 0.5 * Math.Sin(tau * (x * 0.7 - t * 0.035) + 1.4 * Math.Sin(tau * (y * 0.5 + t * 0.02)));
        v += 0.25 * Math.Sin(tau * (x * 0.25 + y * 0.6 + t * 0.025));
        v = Mod(v * 0.5 + 0.5 + y * 0.25 + t * 0.01);
        var c = _pal.At(v);
        double glow = 0.78 + 0.22 * Math.Sin(tau * (x * 0.4 + t * 0.05));             // gentle breathing
        var warm = Lerp(c, _horizon, Clamp((y - 0.7) * 1.2) * 0.35);                  // warmer bottom rows
        return Scale(warm, glow);
    }
}

/// <summary>Keys glow hotter the more you use them; heat cools down over time.</summary>
sealed class Heatmap(EffectArgs a) : Effect(a)
{
    public static readonly EffectInfo Info = new("Typing Heatmap", [ParamDef.Number("cooldown", 2.0)], a => new Heatmap(a), Reactive: true);
    readonly Dictionary<int, (double Heat, double T)> _heat = [];
    readonly double _cooldown = a.Num("cooldown");

    double Value(int index, double t)
    {
        var (h, t0) = _heat.TryGetValue(index, out var e) ? e : (0.0, t);
        return h * Math.Pow(0.5, (t - t0) / (_cooldown * 10));   // half-life
    }

    public override void KeyPressed(int index, double t)
    {
        t *= Speed;
        _heat[index] = (Value(index, t) + 1.0, t);
    }

    protected override Rgb? Color(Key key, double t) =>
        _heat.ContainsKey(key.Index) ? Ramp(Ramps.Heat, Value(key.Index, t) / 8) : Ramps.Heat[0].Item2;
}

/// <summary>Event horizon with a swirling accretion disk; stars get pulled in.</summary>
sealed class BlackHole : Effect
{
    public static readonly EffectInfo Info = new("Black Hole",
        [ParamDef.Choice("theme", "Interstellar", "Purple", "Ice", "Rainbow"), ParamDef.Number("size", 1.0),
         ParamDef.Choice("follow keys", "Off", "On")],
        a => new BlackHole(a));

    static readonly Dictionary<string, (double, Rgb)[]> Themes = new()
    {
        ["Interstellar"] = [(0, new(255, 255, 230)), (0.3, new(255, 190, 80)), (0.65, new(230, 70, 0)), (1, new(60, 0, 0))],
        ["Purple"] = [(0, new(255, 230, 255)), (0.3, new(220, 90, 255)), (0.65, new(110, 0, 200)), (1, new(20, 0, 50))],
        ["Ice"] = [(0, new(240, 255, 255)), (0.3, new(80, 220, 255)), (0.65, new(0, 90, 220)), (1, new(0, 10, 50))],
    };

    double _cx = Width / 2, _cy = Height / 2 + 0.3, _tx, _ty;
    double? _last;
    readonly bool _follow;
    readonly double _size;
    readonly string _theme;
    readonly Dictionary<int, double> _stars = [];

    BlackHole(EffectArgs a) : base(a)
    {
        (_tx, _ty) = (_cx, _cy);
        _follow = a.Str("follow keys") == "On";
        _size = a.Num("size");
        _theme = a.Str("theme");
        var rnd = new Random(11);
        foreach (int i in ByIndex.Keys)
            _stars[i] = rnd.NextDouble();
    }

    public override bool Reactive => _follow;

    public override void KeyPressed(int index, double t)
    {
        if (ByIndex.TryGetValue(index, out var k))
            (_tx, _ty) = (k.Cx, k.Cy);
    }

    public override Rgb?[] Frame(double t)
    {
        if (_last is double last)                     // glide towards the target key
        {
            double a = Math.Min(1.0, (t - last) * 2.5);
            _cx += (_tx - _cx) * a;
            _cy += (_ty - _cy) * a;
        }
        _last = t;
        return base.Frame(t);
    }

    protected override Rgb? Color(Key key, double t)
    {
        double dx = key.Cx - _cx, dy = (key.Cy - _cy) * 2.2;     // keys are wider than tall
        double r = Ripple.Hypot(dx, dy) / _size;
        const double horizon = 1.6;
        if (r < horizon)
            return Rgb.Black;
        double ang = Math.Atan2(dy, dx);
        double swirl = (Math.Sin(ang * 2 - r * 1.3 + t * 3.0) + 1) / 2;          // spiral arms
        double ring = Math.Exp(-Math.Pow((r - horizon - 0.9) / 1.3, 2));         // bright inner disk
        double disk = Math.Exp(-Math.Pow((r - horizon) / 6.5, 2)) * (0.35 + 0.65 * swirl);
        double v = Clamp(ring * 0.9 + disk);
        Rgb c = _theme == "Rainbow" || !Themes.TryGetValue(_theme, out var stops)
            ? Hsv(ang / (2 * Math.PI) + r * 0.05 - t * 0.15, 1, v)
            : Lerp(Rgb.Black, Ramp(stops, 1 - ring * 0.8 - swirl * 0.2), v);
        // infalling stars: twinkles that drift inward
        double ph = Mod(_stars[key.Index] + t * 0.25 + 3 / Math.Max(r, 0.1));
        if (ph < 0.05 && r > horizon + 2)
            c = Lerp(c, new Rgb(255, 255, 255), 1 - ph / 0.05);
        return c;
    }
}
