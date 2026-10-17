// GIF / image playback and text rendering mapped onto the keys. Decoding isn't ported yet.
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

/// <summary>White-on-black text as a scrolling strip: coverage 0..1 per key.</summary>
public sealed class TextStrip
{
    public TextStrip(string text, double height) =>
        throw new NotSupportedException("scrolling text is not ported to C# yet");

    public double[] Coverage(double dx) => new double[K580.NumLeds];
}

public static class Media
{
    public const int MaxUpload = 25 * 1024 * 1024;
    public static readonly string[] Extensions = [".gif", ".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    public static readonly string[] Fits = ["Cover", "Contain", "Stretch"];
    public static string UserDir => Path.Combine(AppPaths.ConfigDir, "media");
    public static string SampleDir => Path.Combine(AppContext.BaseDirectory, "media_samples");

    public sealed record Item(string Name, bool Sample);

    /// <summary>User files first, then the bundled samples.</summary>
    public static List<Item> Library()
    {
        var items = new List<Item>();
        foreach (var (folder, sample) in new[] { (UserDir, false), (SampleDir, true) })
            if (Directory.Exists(folder))
                items.AddRange(Directory.EnumerateFiles(folder)
                    .Select(Path.GetFileName).OfType<string>()
                    .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Select(f => new Item(f, sample)));
        return items;
    }

    public static string PathOf(string name)
    {
        foreach (var folder in new[] { UserDir, SampleDir })
        {
            var p = Path.Combine(folder, Path.GetFileName(name));
            if (File.Exists(p))
                return p;
        }
        throw new KeyNotFoundException(name);
    }

    public static Clip Load(string name, string fit)
    {
        PathOf(name);
        throw new NotSupportedException("GIF / image playback is not ported to C# yet");
    }
}
