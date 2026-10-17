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

/// <summary>Rocket Mania Deluxe (PopCap): detects rocket launches and firework explosions by
/// recognising the game's own sound effects in the audio output. Not ported yet.</summary>
public sealed class RocketMania
{
    public string? Folder { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Recent detections: (time, "launch" | "explosion").</summary>
    public IReadOnlyList<(double T, string Kind)> Events => [];

    /// <summary>Feeds one audio block to the sound detector; returns the kinds detected.</summary>
    public IReadOnlyList<string> Feed(string folder, float[] block)
    {
        Error = "Rocket Mania sound detection is not ported to C# yet";
        return [];
    }
}

public static class Games
{
    public static readonly AssettoCorsa Assetto = new();
    public static readonly RocketMania RocketMania = new();
}
