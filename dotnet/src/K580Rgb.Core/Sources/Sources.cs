// Background capture threads for audio- and screen-reactive effects.
// A source runs only while at least one effect holds it (Acquire/Release).
using K580Rgb.Core.Device;

namespace K580Rgb.Core.Sources;

public abstract class Source
{
    readonly Lock _lock = new();
    int _users;
    CancellationTokenSource? _stop;

    /// <summary>Why capture stopped (missing device, driver hiccup), for the UI.</summary>
    public string? Error { get; protected set; }

    public void Acquire()
    {
        lock (_lock)
        {
            if (++_users != 1)
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

    protected override void Run(CancellationToken stop) =>
        throw new PlatformNotSupportedException("audio capture is not ported to C# yet");
}

/// <summary>Primary monitor averaged down to a 22 x 7 grid of colours, ~15 fps.</summary>
public sealed class ScreenSource : Source
{
    public const int Cols = 22, Rows = 7;

    volatile Rgb[][] _grid = [.. Enumerable.Range(0, Rows).Select(_ => new Rgb[Cols])];

    /// <summary>Rows of colours, top first; replaced as a whole on every capture.</summary>
    public Rgb[][] Grid => _grid;

    protected override void Run(CancellationToken stop) =>
        throw new PlatformNotSupportedException("screen capture is not ported to C# yet");
}

public static class Capture
{
    public static readonly AudioSource Audio = new();
    public static readonly ScreenSource Screen = new();

    /// <summary>Which effect sources work in this build (the UI disables the others).</summary>
    public static Dictionary<string, bool> Available() =>
        new() { ["audio"] = false, ["screen"] = false, ["media"] = false, ["game"] = true };
}
