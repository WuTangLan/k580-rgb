// GIF / image playback and text rendering mapped onto the keys.
//
// Images are fitted onto a canvas with the keyboard's proportions (Px px per key unit);
// each key then takes the average colour of exactly the area it covers (summed-area
// table, so one lookup per key). Decoded clips are cached per file.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using K580Rgb.Core.Device;

namespace K580Rgb.Core.Sources;

/// <summary>Decoded animation: per-frame key colours and cumulative end times (s).</summary>
public sealed class Clip
{
    readonly Rgb[][] _frames;
    readonly double[] _ends;

    public Clip(Rgb[][] frames, double[] durations)
    {
        _frames = frames;
        _ends = new double[durations.Length];
        double sum = 0;
        for (int i = 0; i < durations.Length; i++)
            _ends[i] = sum += durations[i];
    }

    public Rgb[] At(double t)
    {
        t = Effects.ColorMath.Mod(t, _ends[^1]);
        int lo = 0, hi = _ends.Length - 1;
        while (lo < hi)                        // first frame ending after t
        {
            int mid = (lo + hi) / 2;
            if (_ends[mid] > t) hi = mid;
            else lo = mid + 1;
        }
        return _frames[lo];
    }
}

/// <summary>Summed-area table of a canvas-sized (or wider) image: average over any rectangle in O(1).</summary>
sealed class Sat
{
    readonly double[,,] _s;          // [y, x, channel], one larger than the image each way
    public int Width { get; }

    public Sat(Bitmap bmp, int channels)
    {
        Width = bmp.Width;
        int w = bmp.Width, h = bmp.Height;
        _s = new double[h + 1, w + 1, channels];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < channels; c++)
                    {
                        double v = row[x * 4 + 2 - c];            // BGRA -> R, G, B
                        _s[y + 1, x + 1, c] = v + _s[y, x + 1, c] + _s[y + 1, x, c] - _s[y, x, c];
                    }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    public double Mean(int x0, int y0, int x1, int y1, int c) =>
        (_s[y1, x1, c] - _s[y0, x1, c] - _s[y1, x0, c] + _s[y0, x0, c]) / ((x1 - x0) * (y1 - y0));
}

/// <summary>White-on-black text as a scrolling strip: coverage 0..1 per key.</summary>
public sealed class TextStrip
{
    const int GapPx = 3 * Media.Px;            // blank space between repeats of the text
    readonly Sat _sat;
    readonly int _period;

    /// <param name="height">Fraction of the keyboard's height the text's ink should fill.</param>
    public int Period => _period;

    public TextStrip(string text, double height)
    {
        int cw = Media.CanvasW, ch = Media.CanvasH;
        if (text.Length == 0)
            text = " ";
        using var family = Media.Font();
        var probe = Ink(text, family, 100);             // size the font by measured ink height
        float size = Math.Max(8, (int)(100 * ch * height / Math.Max(probe.Height, 1)));
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, size, PointF.Empty, StringFormat.GenericTypographic);
        var ink = path.GetBounds();
        float top = ink.Height > 0 ? ink.Top : 0, bottom = ink.Height > 0 ? ink.Bottom : size;
        // horizontally the text runs from the pen origin to the end of the last advance (like
        // Pillow's getbbox), so the gap between repeats matches the Python app
        using var font = new Font(family, size, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
        float advance;
        using (var measure = new Bitmap(1, 1))
        using (var mg = Graphics.FromImage(measure))
            advance = mg.MeasureString(text, font, PointF.Empty, fmt).Width;
        int w = (int)Math.Round(Math.Max(advance, ink.Right)) + GapPx;   // one period: text + gap before it repeats
        using var strip = new Bitmap(w + cw, ch, PixelFormat.Format32bppArgb);   // + one canvas so the window never wraps
        using (var g = Graphics.FromImage(strip))
        {
            g.Clear(Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float y = (ch - (bottom - top)) / 2 - top;
            // copies exactly one period apart from x=0: strip[x] == strip[x + w], so the
            // window strip[dx:dx+cw] loops seamlessly when dx wraps from w back to 0
            for (int k = 0; k < (w + cw) / w + 1; k++)
            {
                using var copy = (GraphicsPath)path.Clone();
                using var m = new Matrix();
                m.Translate(k * w, y);
                copy.Transform(m);
                g.FillPath(Brushes.White, copy);
            }
        }
        _period = w;
        _sat = new Sat(strip, 1);
    }

    static RectangleF Ink(string text, FontFamily family, float size)
    {
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, size, PointF.Empty, StringFormat.GenericTypographic);
        return path.GetBounds();
    }

    /// <summary>Per LED index: fraction of the key covered by text at scroll offset dx px.</summary>
    public double[] Coverage(double dx)
    {
        int off = (int)Effects.ColorMath.Mod(Math.Floor(dx), _period);
        var output = new double[K580.NumLeds];
        foreach (var (i, x0, y0, x1, y1) in Media.Rects)
            output[i] = _sat.Mean(x0 + off, y0, x1 + off, y1, 0) / 255;
        return output;
    }
}

public static class Media
{
    public const int Px = 8;                               // canvas pixels per key unit
    public static readonly int CanvasW = (int)Math.Round(Layout.Width * Px), CanvasH = (int)Math.Round(Layout.Height * Px);
    const int MaxFrames = 600;
    public const int MaxUpload = 25 * 1024 * 1024;
    const int MinFrameMs = 20;
    public static readonly string[] Extensions = [".gif", ".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    public static string UserDir => Path.Combine(AppPaths.ConfigDir, "media");
    const string SamplePrefix = "K580Rgb.media_samples.";

    /// <summary>Canvas rectangle (x0, y0, x1, y1) a LED averages over. Side-strip LEDs sit outside
    /// the canvas, so they read a thin band along the matching edge instead.</summary>
    internal static readonly (int Index, int X0, int Y0, int X1, int Y1)[] Rects =
        [.. Layout.Lit.Select(k =>
        {
            int y0 = (int)(k.Y * Px) + 1;
            int y1 = Math.Max((int)((k.Y + k.H) * Px) - 1, y0 + 1);
            if (k.Side == "left")
                return (k.Index, 0, y0, Px / 2, y1);
            if (k.Side == "right")
                return (k.Index, CanvasW - Px / 2, y0, CanvasW, y1);
            int x0 = (int)(k.X * Px) + 1;
            return (k.Index, x0, y0, Math.Max((int)((k.X + k.W) * Px) - 1, x0 + 1), y1);
        })];

    internal static FontFamily Font()
    {
        foreach (var name in new[] { "Arial", "Segoe UI" })
        {
            try { return new FontFamily(name); }
            catch (ArgumentException) { }
        }
        return new FontFamily(System.Drawing.Text.GenericFontFamilies.SansSerif);
    }

    static Rgb[] SampleKeys(Bitmap canvas)
    {
        var sat = new Sat(canvas, 3);
        var output = new Rgb[K580.NumLeds];
        foreach (var (i, x0, y0, x1, y1) in Rects)
            output[i] = new Rgb((byte)sat.Mean(x0, y0, x1, y1, 0), (byte)sat.Mean(x0, y0, x1, y1, 1),
                                (byte)sat.Mean(x0, y0, x1, y1, 2));
        return output;
    }

    /// <summary>Fit an image onto the keyboard canvas (black background).</summary>
    static Bitmap Fit(Image img, string mode)
    {
        int cw = CanvasW, ch = CanvasH;
        var canvas = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.Black);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        Rectangle dest;
        if (mode == "Stretch")
        {
            dest = new Rectangle(0, 0, cw, ch);
        }
        else
        {
            double scale = mode == "Cover" ? Math.Max((double)cw / img.Width, (double)ch / img.Height)
                                           : Math.Min((double)cw / img.Width, (double)ch / img.Height);
            int nw = Math.Max(1, (int)Math.Round(img.Width * scale)), nh = Math.Max(1, (int)Math.Round(img.Height * scale));
            dest = mode == "Cover" ? new Rectangle(-((nw - cw) / 2), -((nh - ch) / 2), nw, nh)   // centre crop
                                   : new Rectangle((cw - nw) / 2, (ch - nh) / 2, nw, nh);
        }
        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY);       // no dark fringe from sampling outside the image
        g.DrawImage(img, dest, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, attrs);
        return canvas;
    }

    // -- library -------------------------------------------------------------------------

    static string SafeName(string name)
    {
        var file = Path.GetFileName(name);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        var stem = Regex.Replace(Path.GetFileNameWithoutExtension(file), "[^A-Za-z0-9 _.-]", "").Trim(' ', '.');
        stem = stem[..Math.Min(60, stem.Length)];
        return (stem.Length > 0 ? stem : "image") + ext;
    }

    static IEnumerable<string> SampleNames() =>
        typeof(Media).Assembly.GetManifestResourceNames().Where(n => n.StartsWith(SamplePrefix)).Select(n => n[SamplePrefix.Length..]);

    public sealed record Item(string Name, bool Sample);

    /// <summary>User files first, then the bundled samples.</summary>
    public static List<Item> Library()
    {
        var items = new List<Item>();
        if (Directory.Exists(UserDir))
            items.AddRange(Directory.EnumerateFiles(UserDir).Select(Path.GetFileName).OfType<string>()
                .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .Order(StringComparer.OrdinalIgnoreCase).Select(f => new Item(f, false)));
        items.AddRange(SampleNames().Order(StringComparer.OrdinalIgnoreCase).Select(f => new Item(f, true)));
        return items;
    }

    /// <summary>The file's bytes and a cache key that changes when the file does.</summary>
    static (byte[] Data, string Key) Read(string name)
    {
        name = Path.GetFileName(name);
        var p = Path.Combine(UserDir, name);
        if (File.Exists(p))
            return (File.ReadAllBytes(p), $"{p}|{File.GetLastWriteTimeUtc(p).Ticks}");
        using var s = typeof(Media).Assembly.GetManifestResourceStream(SamplePrefix + name)
                      ?? throw new KeyNotFoundException(name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return (ms.ToArray(), "sample|" + name);
    }

    public static string SaveUpload(string filename, byte[] data)
    {
        if (data.Length > MaxUpload)
            throw new ArgumentException("file larger than 25 MB");
        var name = SafeName(filename);
        if (!Extensions.Contains(Path.GetExtension(name)))
            throw new ArgumentException("unsupported file type (use GIF, PNG, JPG, WEBP or BMP)");
        try
        {
            using var img = Image.FromStream(new MemoryStream(data), useEmbeddedColorManagement: false, validateImageData: true);
        }
        catch (Exception e) when (e is ArgumentException or ExternalException or OutOfMemoryException)
        {
            throw new ArgumentException("not a valid image (or a format Windows can't decode): " + e.Message);
        }
        Directory.CreateDirectory(UserDir);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int n = 2; File.Exists(Path.Combine(UserDir, name)); n++)
            name = $"{stem} ({n}){ext}";
        var tmp = Path.Combine(UserDir, ".upload.tmp");
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, Path.Combine(UserDir, name));
        lock (Cache)
            Cache.Clear();
        return name;
    }

    public static void Delete(string name)
    {
        var p = Path.Combine(UserDir, Path.GetFileName(name));
        if (File.Exists(p))
            File.Delete(p);
        lock (Cache)
            Cache.Clear();
    }

    // -- clips ---------------------------------------------------------------------------

    static readonly Dictionary<string, Clip> Cache = [];

    public static Clip Load(string name, string fit)
    {
        var (data, key) = Read(name);
        key += "|" + fit;
        lock (Cache)
            if (Cache.TryGetValue(key, out var hit))
                return hit;
        var frames = new List<Rgb[]>();
        var durations = new List<double>();
        try
        {
            using var img = Image.FromStream(new MemoryStream(data));
            bool animated = img.FrameDimensionsList.Contains(FrameDimension.Time.Guid);
            int count = animated ? img.GetFrameCount(FrameDimension.Time) : 1;
            int[] delays = [];
            if (animated && img.PropertyIdList.Contains(0x5100))          // PropertyTagFrameDelay, 1/100 s
            {
                var raw = img.GetPropertyItem(0x5100)!.Value!;
                delays = [.. Enumerable.Range(0, raw.Length / 4).Select(i => BitConverter.ToInt32(raw, i * 4) * 10)];
            }
            for (int i = 0; i < Math.Min(count, MaxFrames); i++)
            {
                if (animated)
                    img.SelectActiveFrame(FrameDimension.Time, i);
                using var canvas = Fit(img, fit);
                frames.Add(SampleKeys(canvas));
                int ms = i < delays.Length && delays[i] > 0 ? delays[i] : 100;
                durations.Add(Math.Max(ms, MinFrameMs) / 1000.0);
            }
        }
        catch (Exception e) when (e is ArgumentException or ExternalException or OutOfMemoryException)
        {
            throw new ArgumentException($"can't decode {name}: {e.Message}");
        }
        var clip = new Clip([.. frames], [.. durations]);
        lock (Cache)
        {
            if (Cache.Count > 16)
                Cache.Clear();
            Cache[key] = clip;
        }
        return clip;
    }
}
