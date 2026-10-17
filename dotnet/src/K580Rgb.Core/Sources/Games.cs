// Live game telemetry.
//
// Assetto Corsa / Assetto Corsa Competizione / AC Rally publish telemetry in named
// shared memory (acpmf_physics, acpmf_graphics, acpmf_static). We only open existing
// mappings read-only, so nothing is created while the game isn't running.
using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace K580Rgb.Core.Sources;

/// <summary>Gear is "R", "N" or "1".., while driving.</summary>
public sealed record CarState(string Gear, int Rpm, int MaxRpm, float Speed);

/// <summary>Gear / RPM from AC, ACC or AC Rally. Offsets from the AC shared-memory SDK:
/// physics: gear @16 (0=R, 1=N, 2=1st), rpms @20, speedKmh @28;
/// graphics: status @4 (0 off, 1 replay, 2 live, 3 pause); static: maxRpm @412.</summary>
public sealed class AssettoCorsa
{
    static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);

    readonly Lock _lock = new();
    MemoryMappedViewAccessor? _physics, _graphics, _static;
    readonly List<IDisposable> _open = [];
    long _nextTry;
    int _seenMax;

    MemoryMappedViewAccessor OpenView(string name, int size)
    {
        var mm = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        _open.Add(mm);
        var view = mm.CreateViewAccessor(0, size, MemoryMappedFileAccess.Read);
        _open.Add(view);
        return view;
    }

    void Detach()
    {
        foreach (var d in _open)
            d.Dispose();
        _open.Clear();
        _physics = _graphics = _static = null;
    }

    bool Attach()
    {
        if (_physics != null)
            return true;
        long now = Stopwatch.GetTimestamp();
        if (now < _nextTry)
            return false;
        _nextTry = now + (long)(Retry.TotalSeconds * Stopwatch.Frequency);
        try
        {
            _physics = OpenView(@"Local\acpmf_physics", 32);
            _graphics = OpenView(@"Local\acpmf_graphics", 8);
            _static = OpenView(@"Local\acpmf_static", 416);
            _seenMax = 0;
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            Detach();
            return false;
        }
    }

    public CarState? Read()
    {
        lock (_lock)
        {
            if (!Attach())
                return null;
            int status, gear, rpm, maxRpm;
            float speed;
            try
            {
                status = _graphics!.ReadInt32(4);
                gear = _physics!.ReadInt32(16);
                rpm = _physics.ReadInt32(20);
                speed = _physics.ReadSingle(28);
                maxRpm = _static!.ReadInt32(412);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Detach();
                return null;
            }
            if (status is not (2 or 3))      // menus / replays: stay quiet
                return null;
            _seenMax = Math.Max(_seenMax, rpm);
            if (maxRpm < 1000)                // some mod cars leave maxRpm empty
                maxRpm = Math.Max(_seenMax, 1);
            return new CarState(gear == 0 ? "R" : gear == 1 ? "N" : (gear - 1).ToString(), rpm, maxRpm, speed);
        }
    }
}

// -- sound-event games ------------------------------------------------------------------

/// <summary>Recognises a game's own sound effects in the PC's audio output.
///
/// Each reference sound and the live stream are turned into the same features: per
/// 1024-sample block, log energy in 24 bands (80 Hz - 12 kHz). The latest N blocks are
/// compared with each reference (z-normalised correlation, so music playing on top
/// and volume don't matter much); a hit needs a high score and real loudness.</summary>
public sealed class SoundSpotter
{
    const int Block = 1024, BandCount = 24;
    readonly int _rate;
    readonly int[][] _bins;
    readonly double[] _window = Fft.Hanning(Block);
    readonly Fft _fft = new(Block);
    readonly double[] _mag = new double[Block / 2 + 1];
    readonly Dictionary<string, List<double[,]>> _templates = [];   // kind -> features z-normalised
    readonly Dictionary<string, double> _thresholds = [];
    readonly int _frames;
    readonly double[,] _history;
    readonly double[] _loud;
    readonly double _cooldown;
    readonly Dictionary<string, double> _lastHit = [];
    readonly List<(double T, string Kind)> _events = [];

    /// <param name="sounds">kind -> (reference samples, their sample rate, seconds to match, threshold)</param>
    /// <param name="rate">Sample rate of the live audio blocks.</param>
    public SoundSpotter(Dictionary<string, (List<float[]> Samples, int Rate, double Seconds, double Threshold)> sounds,
                        int rate, double cooldown = 0.35)
    {
        _rate = rate;
        var edges = Fft.GeomSpace(80, 12000, BandCount + 1);
        _bins = [.. Enumerable.Range(0, BandCount).Select(b => Enumerable.Range(0, Block / 2 + 1)
            .Where(k => k * (double)rate / Block >= edges[b] && k * (double)rate / Block < edges[b + 1]).ToArray())];
        foreach (var (kind, (samples, srate, seconds, threshold)) in sounds)
        {
            _thresholds[kind] = threshold;
            _templates[kind] = [];
            foreach (var s in samples)
            {
                var x = Resample(s, srate);
                _templates[kind].Add(ZNorm(Features(x.AsSpan(0, Math.Min(x.Length, (int)(seconds * rate))))));
            }
            _lastHit[kind] = double.NegativeInfinity;
        }
        _frames = _templates.Values.SelectMany(t => t).Max(t => t.GetLength(0));
        _history = new double[_frames, BandCount];
        _loud = new double[_frames];
        _cooldown = cooldown;
    }

    public IReadOnlyList<(double T, string Kind)> Events { get { lock (_events) return [.. _events]; } }

    float[] Resample(float[] x, int rate)
    {
        if (rate == _rate)
            return x;
        int n = (int)((long)x.Length * _rate / rate);
        var output = new float[n];
        for (int i = 0; i < n; i++)
        {
            double pos = n > 1 ? (double)i * (x.Length - 1) / (n - 1) : 0;
            int j = (int)pos;
            double f = pos - j;
            output[i] = (float)(j + 1 < x.Length ? x[j] * (1 - f) + x[j + 1] * f : x[j]);
        }
        return output;
    }

    (double[] Feat, double Rms) BlockFeatures(ReadOnlySpan<float> block)
    {
        _fft.Magnitudes(block, _window, _mag);
        var feat = new double[BandCount];
        for (int b = 0; b < BandCount; b++)
        {
            double e = 0;
            foreach (int k in _bins[b])
                e += _mag[k] * _mag[k];
            feat[b] = Math.Log10(e + 1e-9);
        }
        double sq = 0;
        foreach (var v in block)
            sq += v * v;
        return (feat, Math.Sqrt(sq / block.Length));
    }

    double[,] Features(ReadOnlySpan<float> x)
    {
        int n = x.Length / Block;
        var f = new double[n, BandCount];
        for (int i = 0; i < n; i++)
        {
            var (feat, _) = BlockFeatures(x.Slice(i * Block, Block));
            for (int b = 0; b < BandCount; b++)
                f[i, b] = feat[b];
        }
        return f;
    }

    /// <summary>Remove each band's average over the window: drops the spectral tilt every sound
    /// shares and steady background music, keeping how the sound evolves.</summary>
    static double[,] ZNorm(double[,] f)
    {
        int n = f.GetLength(0), bands = f.GetLength(1);
        var output = new double[n, bands];
        double norm = 0;
        for (int b = 0; b < bands; b++)
        {
            double mean = 0;
            for (int i = 0; i < n; i++) mean += f[i, b];
            mean /= Math.Max(n, 1);
            for (int i = 0; i < n; i++)
            {
                output[i, b] = f[i, b] - mean;
                norm += output[i, b] * output[i, b];
            }
        }
        norm = Math.Sqrt(norm) + 1e-9;
        for (int i = 0; i < n; i++)
            for (int b = 0; b < bands; b++)
                output[i, b] /= norm;
        return output;
    }

    /// <summary>One block of mono float audio at the stream rate. Returns the kinds detected in this block.</summary>
    public List<string> Feed(float[] block)
    {
        var (feat, rms) = BlockFeatures(block.AsSpan(0, Block));
        for (int i = 0; i + 1 < _frames; i++)
        {
            for (int b = 0; b < BandCount; b++)
                _history[i, b] = _history[i + 1, b];
            _loud[i] = _loud[i + 1];
        }
        for (int b = 0; b < BandCount; b++)
            _history[_frames - 1, b] = feat[b];
        _loud[_frames - 1] = rms;
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var hits = new List<string>();
        foreach (var (kind, temps) in _templates)
        {
            double best = 0.0;
            foreach (var t in temps)
            {
                int n = t.GetLength(0);
                if (_loud.AsSpan(_frames - n).ToArray().Max() < 0.004)       // silence: nothing to match
                    continue;
                var recent = new double[n, BandCount];
                for (int i = 0; i < n; i++)
                    for (int b = 0; b < BandCount; b++)
                        recent[i, b] = _history[_frames - n + i, b];
                var z = ZNorm(recent);
                double score = 0;
                for (int i = 0; i < n; i++)
                    for (int b = 0; b < BandCount; b++)
                        score += z[i, b] * t[i, b];
                best = Math.Max(best, score);
            }
            if (best >= _thresholds[kind] && now - _lastHit[kind] >= _cooldown)
            {
                _lastHit[kind] = now;
                hits.Add(kind);
                lock (_events)
                {
                    _events.Add((now, kind));
                    if (_events.Count > 32)
                        _events.RemoveAt(0);
                }
            }
        }
        return hits;
    }

    /// <summary>Mono samples and sample rate of a PCM WAV (8 or 16 bit). Tolerates broken chunk sizes.</summary>
    public static (float[] Samples, int Rate) LoadWavMono(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 12 || BitConverter.ToUInt32(b, 0) != 0x46464952 || BitConverter.ToUInt32(b, 8) != 0x45564157)   // RIFF / WAVE
            throw new InvalidDataException($"unreadable WAV {Path.GetFileName(path)}: not a RIFF/WAVE file");
        int ch = 0, rate = 0, width = 0, pos = 12;
        while (pos + 8 <= b.Length)
        {
            uint id = BitConverter.ToUInt32(b, pos);
            int size = (int)Math.Min(BitConverter.ToUInt32(b, pos + 4), (uint)(b.Length - pos - 8));
            if (id == 0x20746D66)                        // "fmt "
            {
                ch = BitConverter.ToUInt16(b, pos + 10);
                rate = BitConverter.ToInt32(b, pos + 12);
                width = BitConverter.ToUInt16(b, pos + 22) / 8;
            }
            else if (id == 0x61746164 && ch > 0)         // "data"
            {
                if (width is not (1 or 2))
                    throw new InvalidDataException($"unsupported WAV sample width {width}");
                int frames = size / (width * ch);
                var x = new float[frames];
                for (int f = 0; f < frames; f++)
                {
                    double sum = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        int o = pos + 8 + (f * ch + c) * width;
                        sum += width == 2 ? BitConverter.ToInt16(b, o) / 32768.0 : (b[o] - 128) / 128.0;
                    }
                    x[f] = (float)(sum / ch);
                }
                return (x, rate);
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException($"unreadable WAV {Path.GetFileName(path)}: no audio data");
    }
}

/// <summary>Rocket Mania Deluxe (PopCap): detects rocket launches and firework explosions by
/// recognising the game's own sound effects (sounds/cached_*.wav) in the audio output.
/// The game folder is found from the running RocketMania.exe, or given explicitly.</summary>
public sealed class RocketMania
{
    const string Exe = "RocketMania";
    static readonly string[] Launch = ["cached_Rocket Launch Woosh 03b.wav"];
    static readonly string[] Explosions = [.. new[] { 1, 3, 4, 5, 6, 7 }.Select(i => $"cached_Firework Explosion 0{i}.wav")];

    readonly Lock _lock = new();
    SoundSpotter? _spotter;
    long _nextLook;

    public string? Folder { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Recent detections: (time, "launch" | "explosion").</summary>
    public IReadOnlyList<(double T, string Kind)> Events => _spotter?.Events ?? [];

    /// <summary>Load the game's sounds once (from <paramref name="folder"/> or the running game).</summary>
    SoundSpotter? Ensure(string folder, int rate)
    {
        lock (_lock)
        {
            if (_spotter != null && (folder.Length == 0 || folder == Folder))
                return _spotter;
            if (Stopwatch.GetTimestamp() < _nextLook && folder.Length == 0)
                return null;
            _nextLook = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
            if (folder.Length == 0)
            {
                var exe = Process.GetProcessesByName(Exe).Select(p => Win32.ProcessImage((uint)p.Id)).FirstOrDefault(p => p != null);
                if (exe == null)
                {
                    Error = "Rocket Mania is not running (or set its folder)";
                    return null;
                }
                folder = Path.GetDirectoryName(exe)!;
            }
            var sounds = Path.Combine(folder, "sounds");
            try
            {
                var launch = Launch.Select(f => SoundSpotter.LoadWavMono(Path.Combine(sounds, f))).ToList();
                var boom = new List<(float[] Samples, int Rate)>();
                foreach (var f in Explosions)
                {
                    try { boom.Add(SoundSpotter.LoadWavMono(Path.Combine(sounds, f))); }
                    catch (IOException) { }      // skip a missing/damaged variant, keep the rest
                }
                if (boom.Count == 0)
                    throw new FileNotFoundException($"no firework sounds in {sounds}");
                int srate = launch[0].Rate;
                // windows/thresholds tuned on simulated gameplay audio (music + other game sounds):
                // launch 100% hit, 0 false; explosion ~60% hit, ~1 false per 5 min
                _spotter = new SoundSpotter(new()
                {
                    ["launch"] = ([.. launch.Select(s => s.Samples)], srate, 0.45, 0.55),
                    ["explosion"] = ([.. boom.Select(s => s.Samples)], srate, 0.12, 0.70),
                }, rate);
                (Folder, Error) = (folder, null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Error = $"cannot load game sounds: {e.Message}";
                _spotter = null;
            }
            return _spotter;
        }
    }

    /// <summary>Feeds one audio block to the sound detector; returns the kinds detected.</summary>
    public IReadOnlyList<string> Feed(string folder, float[] block) =>
        Ensure(folder, Capture.Audio.SampleRate)?.Feed(block) ?? [];
}

public static class Games
{
    public static readonly AssettoCorsa Assetto = new();
    public static readonly RocketMania RocketMania = new();
}
