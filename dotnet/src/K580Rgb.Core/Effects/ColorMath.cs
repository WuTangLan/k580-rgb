using K580Rgb.Core.Device;

namespace K580Rgb.Core.Effects;

/// <summary>Colour helpers shared by the effects. Same rounding as the Python version (truncate).</summary>
public static class ColorMath
{
    public static double Clamp(double v, double lo = 0.0, double hi = 1.0) => v < lo ? lo : v > hi ? hi : v;

    /// <summary>Python-style modulo: the result has the sign of <paramref name="m"/>.</summary>
    public static double Mod(double a, double m = 1.0) => a - m * Math.Floor(a / m);

    public static Rgb Hsv(double h, double s = 1.0, double v = 1.0)
    {
        var (r, g, b) = HsvToRgb(Mod(h), s, v);
        return new Rgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    // colorsys.hsv_to_rgb
    static (double, double, double) HsvToRgb(double h, double s, double v)
    {
        if (s == 0.0)
            return (v, v, v);
        int i = (int)(h * 6.0);
        double f = h * 6.0 - i, p = v * (1.0 - s), q = v * (1.0 - s * f), t = v * (1.0 - s * (1.0 - f));
        return (i % 6) switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t),
            3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
        };
    }

    // colorsys.rgb_to_hsv, inputs 0..1
    public static (double H, double S, double V) RgbToHsv(double r, double g, double b)
    {
        double maxc = Math.Max(r, Math.Max(g, b)), minc = Math.Min(r, Math.Min(g, b));
        double rangec = maxc - minc;
        if (minc == maxc)
            return (0.0, 0.0, maxc);
        double s = rangec / maxc;
        double rc = (maxc - r) / rangec, gc = (maxc - g) / rangec, bc = (maxc - b) / rangec;
        double h = r == maxc ? bc - gc : g == maxc ? 2.0 + rc - bc : 4.0 + gc - rc;
        return (Mod(h / 6.0), s, maxc);
    }

    public static Rgb Lerp(Rgb c1, Rgb c2, double t)
    {
        t = Clamp(t);
        return new Rgb((byte)(c1.R + (c2.R - c1.R) * t), (byte)(c1.G + (c2.G - c1.G) * t),
                       (byte)(c1.B + (c2.B - c1.B) * t));
    }

    public static Rgb Scale(Rgb c, double v) => new((byte)(c.R * v), (byte)(c.G * v), (byte)(c.B * v));

    public static Rgb Max(Rgb a, Rgb b) => new(Math.Max(a.R, b.R), Math.Max(a.G, b.G), Math.Max(a.B, b.B));

    public static Rgb HexToRgb(string s)
    {
        s = s.TrimStart('#');
        if (s.Length < 6)
            throw new FormatException($"invalid colour '{s}'");
        return new Rgb(Convert.ToByte(s[..2], 16), Convert.ToByte(s[2..4], 16), Convert.ToByte(s[4..6], 16));
    }

    /// <summary>Which of n equal columns across the keyboard a key falls in (side strips clamp to the edges).</summary>
    public static int Column(Key key, int n) => Math.Clamp((int)(key.Cx / Layout.Width * n), 0, n - 1);

    /// <summary>Piecewise-linear colour ramp; stops sorted by position.</summary>
    public static Rgb Ramp((double Pos, Rgb Color)[] stops, double v)
    {
        v = Clamp(v);
        for (int i = 0; i + 1 < stops.Length; i++)
        {
            var (p0, c0) = stops[i];
            var (p1, c1) = stops[i + 1];
            if (v <= p1)
                return Lerp(c0, c1, (v - p0) / (p1 - p0));
        }
        return stops[^1].Color;
    }

    // -- palettes (perceptual blending in OKLab) ------------------------------------------------

    static double ToLinear(double c)
    {
        c /= 255;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    static byte ToSrgb(double c)
    {
        c = Clamp(c);
        return (byte)Math.Round(255 * (c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055));
    }

    public static (double L, double A, double B) RgbToOklab(Rgb rgb)
    {
        double r = ToLinear(rgb.R), g = ToLinear(rgb.G), b = ToLinear(rgb.B);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    static (double, double, double) OklabToLinear(double L, double a, double b)
    {
        double l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
        double m = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
        double s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);
        return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    /// <summary>OKLab -> sRGB. Out-of-range colours lose chroma (keeping hue and lightness)
    /// instead of being clipped per channel, which would shift the hue.</summary>
    public static Rgb OklabToRgb(double L, double a, double b)
    {
        static bool InGamut(double v) => v >= -0.0005 && v <= 1.0005;
        var lin = OklabToLinear(L, a, b);
        int n = 0;
        for (; n < 30; n++)
        {
            lin = OklabToLinear(L, a, b);
            if (InGamut(lin.Item1) && InGamut(lin.Item2) && InGamut(lin.Item3))
                break;
            a *= 0.92;
            b *= 0.92;
        }
        if (n == 30)
            lin = OklabToLinear(L, 0.0, 0.0);
        return new Rgb(ToSrgb(lin.Item1), ToSrgb(lin.Item2), ToSrgb(lin.Item3));
    }

    /// <summary>Blend two RGB colours in OKLCH: lightness and chroma linearly, hue the short way
    /// round. Unlike RGB or OKLab, opposite hues don't wash out to grey halfway.</summary>
    public static Rgb OklchMix(Rgb c1, Rgb c2, double f)
    {
        var (L1, a1, b1) = RgbToOklab(c1);
        var (L2, a2, b2) = RgbToOklab(c2);
        double C1 = Math.Sqrt(a1 * a1 + b1 * b1), C2 = Math.Sqrt(a2 * a2 + b2 * b2);
        double h1 = Math.Atan2(b1, a1), h2 = Math.Atan2(b2, a2);
        if (C1 < 0.02)                    // grey/black/white has no hue: borrow the other one's
            h1 = h2;
        if (C2 < 0.02)
            h2 = h1;
        double dh = Mod(h2 - h1 + Math.PI, 2 * Math.PI) - Math.PI;
        double L = L1 + (L2 - L1) * f, C = C1 + (C2 - C1) * f, h = h1 + dh * f;
        return OklabToRgb(L, C * Math.Cos(h), C * Math.Sin(h));
    }
}

/// <summary>Colour stops blended perceptually (OKLCH), precomputed into a 256-entry lookup.</summary>
public sealed class Palette
{
    const int Size = 256;
    readonly Rgb[] _lut = new Rgb[Size];

    public Palette(IReadOnlyList<string> hexColors, bool cyclic = false)
    {
        var stops = hexColors.Take(8).Select(ColorMath.HexToRgb).ToList();
        if (stops.Count == 0)
            stops.Add(Rgb.Black);
        if (cyclic)
            stops.Add(stops[0]);                    // wrap back to the first colour
        int n = stops.Count - 1;
        for (int i = 0; i < Size; i++)
        {
            double pos = n > 0 ? (double)i / (Size - 1) * n : 0;
            int j = n > 0 ? Math.Min((int)pos, n - 1) : 0;
            _lut[i] = ColorMath.OklchMix(stops[j], stops[Math.Min(j + 1, n)], pos - j);
        }
    }

    public Rgb At(double v) => _lut[(int)(ColorMath.Clamp(v) * (Size - 1))];
}
