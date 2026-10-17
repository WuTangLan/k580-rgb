using K580Rgb.Core.Device;
using K580Rgb.Core.Sources;
using static K580Rgb.Core.Effects.ColorMath;
using static K580Rgb.Core.Layout;

namespace K580Rgb.Core.Effects;

/// <summary>Battlefield-style combat lighting: smoky ambience, muzzle flashes while you fire, a reload bar,
/// red hits from the side you were shot from, a heartbeat at low health and shockwaves for explosions.
///
/// Battlefield has no lighting API (and its anti-cheat rules out reading game memory), so this reacts to
/// what you see and hear: red damage at the screen edges, loud low booms in the game audio, and your own
/// mouse buttons and keys while the game window is focused.</summary>
sealed class BattlefieldCombat : Effect
{
    static readonly Dictionary<string, string[]?> GamesByName = new()
    {
        ["Battlefield 1"] = ["bf1.exe"],
        ["Battlefield V"] = ["bfv.exe"],
        ["Battlefield 2042"] = ["bf2042.exe"],
        ["Battlefield 4"] = ["bf4.exe", "bf4_x86.exe"],
        ["Any game (always on)"] = null,
    };
    static readonly Dictionary<string, (Rgb Smoke, Rgb Highlight, Rgb Flash)> Themes = new()
    {   // smoke base, highlight (WASD etc.), muzzle flash
        ["Battlefield 1 (amber)"] = (new(46, 20, 4), new(255, 140, 30), new(255, 200, 90)),
        ["Military (olive)"] = (new(14, 24, 6), new(150, 200, 60), new(255, 220, 120)),
        ["Night ops (blue)"] = (new(2, 10, 30), new(40, 140, 255), new(200, 230, 255)),
    };
    static readonly (double, Rgb)[] Explosion =
        [(0, new(255, 255, 200)), (0.25, new(255, 170, 30)), (0.6, new(230, 50, 0)), (1, new(60, 0, 0))];
    const int VkLButton = 0x01, VkRButton = 0x02, VkR = 0x52, VkG = 0x47;
    static readonly HashSet<int> Move = [44, 64, 65, 66], Slots = [.. Enumerable.Range(22, 7)],
                                 NumberRow = [.. Enumerable.Range(21, 14)];
    const int Reload = 46, Grenade = 68;
    const double FocusPoll = 0.5;

    public static readonly EffectInfo Info = new("Battlefield: Combat",
        [ParamDef.Choice("game", [.. GamesByName.Keys]), ParamDef.Choice("theme", [.. Themes.Keys]),
         ParamDef.Number("damage sensitivity", 1.0), ParamDef.Choice("shots", "Muzzle flash", "Off"),
         ParamDef.Choice("explosions", "On", "Off"), ParamDef.Choice("demo", "Off", "On")],
        a => new BattlefieldCombat(a), Source: "screen");

    readonly ScreenSource _screen = Capture.Screen;
    readonly AudioSource _audio = Capture.Audio;
    readonly string[]? _exes;
    readonly Rgb _smoke, _hi, _flashC;
    readonly double _sens;
    readonly bool _demo, _explosions, _shots;
    readonly Random _rnd = new();
    bool _focused;
    double _focusAt = -9.0;
    double? _last, _slowBase, _lowSince, _firingSince;
    readonly double?[] _sideBase = new double?[4];       // running redness per screen edge: left, right, top, bottom
    List<(double T, int Side, double Strength)> _hits = [];
    List<(double T, double Strength)> _booms = [];
    double _bassAvg, _lastBoom = -9.0, _reloadT = -9.0, _grenadeT = -9.0, _nextDemo;
    HashSet<int> _keysDown = [];
    bool _aiming;

    BattlefieldCombat(EffectArgs a) : base(a)
    {
        _exes = GamesByName.TryGetValue(a.Str("game"), out var exes) ? exes : ["bf1.exe"];
        (_smoke, _hi, _flashC) = Themes.GetValueOrDefault(a.Str("theme"), Themes.Values.First());
        _sens = Math.Max(a.Num("damage sensitivity"), 0.1);
        _demo = a.Str("demo") == "On";
        _explosions = a.Str("explosions") == "On";
        _shots = a.Str("shots") == "Muzzle flash";
        _focused = _exes == null;
        _screen.Acquire();
        if (_explosions)
            _audio.Acquire();
    }

    public override void Dispose()
    {
        _screen.Release();
        if (_explosions)
            _audio.Release();
    }

    // -- inputs ----------------------------------------------------------------------------

    bool InGame(double t)
    {
        if (_exes == null)
            return true;
        if (t - _focusAt > FocusPoll || t < _focusAt)
        {
            _focusAt = t;
            _focused = Win32.ForegroundProcess() is string fg && _exes.Contains(fg);
        }
        return _focused;
    }

    void PollInput(double t)
    {
        var down = new[] { VkLButton, VkRButton, VkR, VkG }.Where(Win32.IsDown).ToHashSet();
        var pressed = down.Except(_keysDown).ToHashSet();
        _keysDown = down;
        _firingSince = down.Contains(VkLButton) ? _firingSince ?? t : null;
        _aiming = down.Contains(VkRButton);
        if (pressed.Contains(VkR)) _reloadT = t;
        if (pressed.Contains(VkG)) _grenadeT = t;
    }

    void Screen(double t)
    {
        var g = _screen.Grid;
        IEnumerable<Rgb>[] edges = [g.Select(r => r[0]), g.Select(r => r[^1]), g[0], g[^1]];
        var red = edges.Select(ScreenEdges.Redness).ToArray();
        double total = red.Sum() / 4;
        double thresh = 0.10 / _sens;
        for (int side = 0; side < 4; side++)
        {
            var b = _sideBase[side];
            _sideBase[side] = b is double bb ? bb * 0.95 + red[side] * 0.05 : red[side];
            if (b is double prev && red[side] - prev > thresh && !_hits.Any(h => h.Side == side && t - h.T < 0.25))
                _hits.Add((t, side, Math.Min(1.0, (red[side] - prev) / thresh / 2)));
        }
        // low health: the edges stay red/bloody for a while, far above the long-run level
        _slowBase = _slowBase is double s ? s * 0.998 + total * 0.002 : total;
        _lowSince = total - _slowBase > 0.06 / _sens ? _lowSince ?? t : null;
    }

    void Audio(double t)
    {
        double instant = _audio.Levels.Take(4).Average();
        if (instant > 0.55 && instant > _bassAvg * 1.7 && t - _lastBoom > 0.35)
        {
            _lastBoom = t;
            _booms.Add((t, Math.Min(1.0, instant)));
        }
        _bassAvg = _bassAvg * 0.95 + instant * 0.05;
    }

    void Demo(double t)
    {
        if (t < _nextDemo)
            return;
        string what = new[] { "burst", "burst", "hit", "boom", "reload" }[_rnd.Next(5)];
        if (what == "burst")
        {
            _firingSince = t;
            _nextDemo = t + 0.8;
            return;
        }
        _firingSince = null;
        if (what == "hit") _hits.Add((t, _rnd.Next(4), 1.0));
        else if (what == "boom") _booms.Add((t, 1.0));
        else _reloadT = t;
        _nextDemo = t + 0.8 + _rnd.NextDouble() * 0.8;
    }

    // -- drawing -----------------------------------------------------------------------------

    public override Rgb?[] Frame(double t)
    {
        double dt = _last is double last ? Math.Max(0.0, t - last) : 0.0;
        _last = t;
        bool inGame = _demo || InGame(t);
        if (_demo)
        {
            if (_firingSince is double fs && t - fs > 0.7)
                _firingSince = null;
            Demo(t);
        }
        else if (inGame)
        {
            PollInput(t);
            Screen(t);
            if (_explosions)
                Audio(t);
        }
        else
        {
            (_firingSince, _aiming) = (null, false);
        }
        _hits = [.. _hits.Where(h => t - h.T < 0.9)];
        _booms = [.. _booms.Where(b => t - b.T < 1.6)];

        // muzzle flash: ~11 shots/s while the trigger is held, each a short decaying flash
        double flash = 0.0;
        if (_firingSince is double since && _shots)
        {
            double phase = Mod((t - since) * 11);
            flash = (1 - phase) * (1 - phase) * (0.75 + 0.25 * _rnd.NextDouble());
        }
        double heart = 0.0;
        if (_lowSince is double low && t - low > 1.0)
        {
            double beat = Mod(t * 1.2);                       // lub-dub at 72 bpm
            heart = Math.Max(Math.Exp(-Math.Pow((beat - 0.05) / 0.05, 2)), 0.7 * Math.Exp(-Math.Pow((beat - 0.25) / 0.05, 2)));
        }
        double reload = (t - _reloadT) / 1.6;                  // reload progress 0..1 across the number row
        double grenade = t - _grenadeT;

        var output = Blank();
        foreach (var key in Lit)
        {
            int i = key.Index;
            double x = key.Cx / Width, y = key.Cy / Height;
            // drifting battlefield smoke
            double n = (Math.Sin(x * 7 + t * 0.6) + Math.Sin(y * 5 - t * 0.45 + x * 3) + Math.Sin((x + y) * 4 + t * 0.3)) / 3;
            var c = Lerp(Rgb.Black, _smoke, 0.55 + 0.45 * n);
            if (_aiming)                                       // aiming down sights: focus on the middle
                c = Lerp(c, Rgb.Black, Clamp(Math.Abs(x - 0.45) * 1.6));
            if (Move.Contains(i) || Slots.Contains(i))
                c = Lerp(c, _hi, Move.Contains(i) ? 0.55 : 0.25);
            if (i is Reload or Grenade)
                c = Lerp(c, _hi, 0.35);
            if (reload >= 0 && reload < 1 && NumberRow.Contains(i))
                c = Lerp(c, _hi, (key.Cx - 0.5) / 14.5 <= reload ? 1.0 : 0.15);
            if (grenade >= 0 && grenade < 2.0 && i == Grenade)
                c = Lerp(c, new Rgb(255, 40, 0), 0.5 + 0.5 * Math.Sin(grenade * 18));
            if (flash > 0)
            {
                double near = key.Side != null ? 1.0 : Clamp(1.2 - Math.Abs(x - 0.5) * 1.4);
                c = Lerp(c, _flashC, flash * near);
            }
            foreach (var (t0, strength) in _booms)             // shockwave from the bottom middle
            {
                double age = t - t0;
                double d = Ripple.Hypot(key.Cx - Width / 2, (key.Cy - Height) * 1.3);
                double ring = Clamp(1 - Math.Abs(d - age * 22) / 3.5) * Clamp(1 - age / 1.6) * strength;
                double glow = Clamp(1 - age / 0.35) * strength * 0.8;
                double k = Math.Max(ring, glow);
                if (k > 0)
                    c = Lerp(c, Ramp(Explosion, age / 1.6 + (1 - ring) * 0.2), k);
            }
            foreach (var (t0, side, strength) in _hits)        // red hit from the side you were shot from
            {
                double age = t - t0;
                double edge = side switch { 0 => x, 1 => 1 - x, 2 => y, _ => 1 - y };   // side strips sit just outside 0..1
                double k = Clamp(1 - edge * 2.2) * Math.Pow(Clamp(1 - age / 0.9), 1.5) * strength;
                if (k > 0)
                    c = Lerp(c, new Rgb(255, 0, 0), k);
            }
            if (heart > 0)
                c = Lerp(c, new Rgb(200, 0, 0), heart * (key.Side != null ? 0.9 : 0.6));
            output[i] = c;
        }
        return output;
    }
}
