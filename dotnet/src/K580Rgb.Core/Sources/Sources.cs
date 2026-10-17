// Background capture threads for audio- and screen-reactive effects.
// A source runs only while at least one effect holds it (Acquire/Release).
using System.Runtime.InteropServices;
using K580Rgb.Core.Device;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace K580Rgb.Core.Sources;

public abstract class Source
{
    readonly Lock _lock = new();
    int _users;
    CancellationTokenSource? _stop;

    /// <summary>Why capture stopped (missing device, driver hiccup), for the UI.</summary>
    public string? Error { get; protected set; }

    /// <summary>Tests: sources never start, so effects see silence and a black screen.</summary>
    public static bool CaptureDisabled { get; set; }

    public void Acquire()
    {
        lock (_lock)
        {
            if (++_users != 1 || CaptureDisabled)
                return;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            new Thread(() => RunSafe(token)) { IsBackground = true, Name = GetType().Name }.Start();
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            _users = Math.Max(0, _users - 1);
            if (_users == 0)
                _stop?.Cancel();
        }
    }

    void RunSafe(CancellationToken stop)
    {
        try
        {
            Error = null;
            Run(stop);
        }
        catch (Exception e)           // missing device, driver hiccup: report and stop
        {
            Error = $"{e.GetType().Name}: {e.Message}";
        }
    }

    protected abstract void Run(CancellationToken stop);
}

/// <summary>System audio (what you hear) -> log-spaced band levels 0..1 with auto gain.</summary>
public sealed class AudioSource : Source
{
    public const int Bands = 22;               // one per key column
    public const int Rate = 48000, Block = 1024;
    static readonly TimeSpan SilenceAfter = TimeSpan.FromMilliseconds(60);   // ~3 blocks' worth of time without audio

    volatile double[] _levels = new double[Bands];
    volatile Action<float[]>[] _listeners = [];
    readonly Lock _listenerLock = new();

    /// <summary>Latest band levels; replaced (never modified) every block, so readers get a consistent copy.</summary>
    public double[] Levels => _levels;
    public double Bass { get; private set; }
    public double Volume { get; private set; }

    /// <summary>Callbacks get every mono float block at <see cref="Rate"/>, e.g. game sound detectors.</summary>
    public void AddListener(Action<float[]> l) { lock (_listenerLock) _listeners = [.. _listeners, l]; }
    public void RemoveListener(Action<float[]> l) { lock (_listenerLock) _listeners = [.. _listeners.Where(x => x != l)]; }

    /// <summary>Sample rate of the blocks listeners get (the playback device's mix rate).</summary>
    public int SampleRate { get; private set; } = Rate;

    protected override void Run(CancellationToken stop)
    {
        using var capture = new WasapiLoopbackCapture();     // default playback device: what you hear
        var fmt = capture.WaveFormat;
        SampleRate = fmt.SampleRate;
        var pending = new Queue<float>();
        var signal = new AutoResetEvent(false);
        Exception? failed = null;
        capture.DataAvailable += (_, e) =>
        {
            var mono = ToMono(e.Buffer, e.BytesRecorded, fmt);
            lock (pending)
                foreach (var v in mono)
                    pending.Enqueue(v);
            signal.Set();
        };
        capture.RecordingStopped += (_, e) => { failed = e.Exception; signal.Set(); };
        capture.StartRecording();
        try
        {
            var fft = new Fft(Block);
            var window = Fft.Hanning(Block);
            var mag = new double[Block / 2 + 1];
            var edges = Fft.GeomSpace(40, 16000, Bands + 1);
            var idx = new int[Bands][];
            for (int b = 0; b < Bands; b++)
            {
                var bins = Enumerable.Range(0, Block / 2 + 1)
                    .Where(k => k * (double)fmt.SampleRate / Block >= edges[b] && k * (double)fmt.SampleRate / Block < edges[b + 1]).ToArray();
                // bands narrower than one FFT bin use the bin nearest their lower edge
                idx[b] = bins.Length > 0 ? bins : [(int)Math.Round(edges[b] * Block / fmt.SampleRate)];
            }
            double peak = 1e-3;
            var levels = new double[Bands];
            var block = new float[Block];
            long lastBlock = System.Diagnostics.Stopwatch.GetTimestamp();
            while (!stop.IsCancellationRequested)
            {
                if (failed != null)
                    throw failed;
                if (!NextBlock(pending, block, System.Diagnostics.Stopwatch.GetElapsedTime(lastBlock) >= SilenceAfter))
                {
                    signal.WaitOne(20);
                    continue;
                }
                lastBlock = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (var listener in _listeners)
                    listener((float[])block.Clone());
                fft.Magnitudes(block, window, mag);
                var raw = new double[Bands];
                for (int b = 0; b < Bands; b++)
                    raw[b] = idx[b].Average(k => mag[k]) * (1 + 2.0 * b / (Bands - 1));
                peak = Math.Max(Math.Max(peak * 0.995, raw.Max()), 1e-3);         // slow auto gain
                var next = new double[Bands];
                for (int b = 0; b < Bands; b++)
                    next[b] = Math.Max(Math.Pow(Math.Clamp(raw[b] / peak, 0, 1), 0.7), levels[b] * 0.82);   // fast attack, smooth fall
                levels = next;
                _levels = next;
                Bass = next.Take(4).Average();
                Volume = next.Average();
            }
        }
        finally
        {
            capture.StopRecording();
        }
    }

    /// <summary>Fill <paramref name="block"/> from the captured samples. WASAPI loopback delivers no samples
    /// while nothing plays (it still raises empty DataAvailable events), so once <paramref name="silent"/>
    /// the block is padded with silence and the levels fall. False: wait for more audio.</summary>
    static bool NextBlock(Queue<float> pending, float[] block, bool silent)
    {
        lock (pending)
        {
            if (pending.Count < block.Length && !silent)
                return false;
            int n = Math.Min(block.Length, pending.Count);
            for (int i = 0; i < n; i++)
                block[i] = pending.Dequeue();
            Array.Clear(block, n, block.Length - n);
            while (pending.Count > block.Length * 8)    // fell behind: drop old audio, keep latency low
                pending.Dequeue();
            return true;
        }
    }

    static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");   // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    static float[] ToMono(byte[] buf, int bytes, WaveFormat fmt)
    {
        int ch = fmt.Channels, size = fmt.BitsPerSample / 8, frames = bytes / (size * ch);
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat
                       || (fmt is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < ch; c++)
            {
                int o = (f * ch + c) * size;
                sum += isFloat ? BitConverter.ToSingle(buf, o)
                     : size == 2 ? BitConverter.ToInt16(buf, o) / 32768.0
                     : size == 3 ? ((buf[o] << 8 | buf[o + 1] << 16 | buf[o + 2] << 24) >> 8) / 8388608.0
                     : BitConverter.ToInt32(buf, o) / 2147483648.0;
            }
            mono[f] = (float)(sum / ch);
        }
        return mono;
    }
}

/// <summary>Primary monitor averaged down to a 22 x 7 grid of colours, ~15 fps.</summary>
public sealed class ScreenSource : Source
{
    public const int Cols = 22, Rows = 7;

    volatile Rgb[][] _grid = [.. Enumerable.Range(0, Rows).Select(_ => new Rgb[Cols])];

    /// <summary>Rows of colours, top first; replaced as a whole on every capture.</summary>
    public Rgb[][] Grid => _grid;

    const int Px = 8;                          // screen samples per grid cell, each way

    /// <summary>GDI shrinks the primary monitor in one StretchBlt (HALFTONE averages the pixels),
    /// then each grid cell is the mean of its Px x Px samples.</summary>
    protected override void Run(CancellationToken stop)
    {
        int w = Cols * Px, h = Rows * Px;
        var screen = GetDC(IntPtr.Zero);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        SetStretchBltMode(mem, 4 /* HALFTONE */);
        SetBrushOrgEx(mem, 0, 0, IntPtr.Zero);
        var info = new BitmapInfoHeader { Size = 40, Width = w, Height = -h, Planes = 1, BitCount = 32 };   // top-down BGRA
        var pixels = new byte[w * h * 4];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                long t = System.Diagnostics.Stopwatch.GetTimestamp();
                int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
                if (!StretchBlt(mem, 0, 0, w, h, screen, 0, 0, sw, sh, 0x00CC0020 /* SRCCOPY */)
                    || GetDIBits(mem, bmp, 0, (uint)h, pixels, ref info, 0) == 0)
                    throw new IOException("screen capture failed");
                var grid = new Rgb[Rows][];
                for (int r = 0; r < Rows; r++)
                {
                    grid[r] = new Rgb[Cols];
                    for (int c = 0; c < Cols; c++)
                    {
                        int sr = 0, sg = 0, sb = 0;
                        for (int y = r * Px; y < (r + 1) * Px; y++)
                            for (int x = c * Px; x < (c + 1) * Px; x++)
                            {
                                int o = (y * w + x) * 4;
                                sb += pixels[o]; sg += pixels[o + 1]; sr += pixels[o + 2];
                            }
                        const int n = Px * Px;
                        grid[r][c] = new Rgb((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));
                    }
                }
                _grid = grid;
                var left = TimeSpan.FromSeconds(1.0 / 15) - System.Diagnostics.Stopwatch.GetElapsedTime(t);
                if (left > TimeSpan.Zero)
                    stop.WaitHandle.WaitOne(left);
            }
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
        public uint Colors;                    // room for one RGBQUAD
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr prev);
    [DllImport("gdi32.dll")]
    static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")]
    static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
}

public static class Capture
{
    public static readonly AudioSource Audio = new();
    public static readonly ScreenSource Screen = new();

    /// <summary>Which effect sources work in this build (the UI disables the others).</summary>
    public static Dictionary<string, bool> Available() =>
        new() { ["audio"] = true, ["screen"] = true, ["media"] = true, ["game"] = true };
}
