// Keeps the keyboard, the on-disk config and the live effect engine in sync.
//
// Stock firmware saves every lighting change to flash, so writes are debounced,
// diffed against what the keyboard already holds and rate limited.
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using K580Rgb.Core.Device;
using K580Rgb.Core.Effects;

namespace K580Rgb.Core;

public sealed class Controller
{
    static readonly TimeSpan WriteDebounce = TimeSpan.FromSeconds(0.4);       // quiet before a change is written
    static readonly TimeSpan WriteMinInterval = TimeSpan.FromSeconds(1.0);    // between flash writes
    const int LiveFps = 30;
    static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(2.0);

    const string DefaultConfig = """
    {
      "profiles": {}, "rules": [], "default_profile": null, "automation": false, "flash_writes": 0,
      "live_presets": {}, "last_live": null, "macros": [], "key_assignments": {},
      "tray_icon": true, "open_browser": true
    }
    """;
    // live_presets: name -> layer list (your own live-effect presets)
    // last_live: {"layers": [...]} running on the keyboard at last exit
    // macros: [{name, events: [[down|up, usage, delay_ms]...], repeat}]
    // key_assignments: matrix pos -> {type: macro|key|media|disabled, macro|code}
    // tray_icon: show the tray icon (off: runs hidden; start the app again to open it)
    // open_browser: open the window when the app starts

    sealed class Pending
    {
        public Params? Params;
        public Rgb[]? Custom;
        public byte[]? Side;
    }

    public readonly K580 Dev = new();
    /// <summary>Guards the device and the state below; reentrant like Python's RLock.</summary>
    public readonly Lock Sync = new();
    public JsonObject Config { get; }
    public string Status { get; private set; } = "connecting";
    public bool Direct { get; private set; }
    public int Profile { get; private set; }            // active hardware profile slot (0-2)
    Params? _params;                                    // what the keyboard holds
    Rgb[]? _custom;
    byte[]? _side;                                      // side strips: (mode, speed, color index)
    byte[][]? _keymaps;                                 // the keyboard's keymap per hardware profile
    byte[]? _defaultKeymap;                             // factory keymap (firmware table)
    Pending? _pending;                                  // waiting to be written
    long _pendingAt, _lastWrite, _lastReconnect;
    volatile Scene? _live;
    JsonArray? _liveSpecs;
    long _liveStarted;
    bool _liveStream;                                   // stream to keyboard (direct firmware only)
    volatile Rgb[] _frame = new Rgb[K580.NumLeds];
    readonly CancellationTokenSource _stop = new();

    public Controller()
    {
        Config = Load();
    }

    public Scene? Live => _live;
    public bool LiveStream => _liveStream;
    /// <summary>Latest rendered frame, for the UI preview.</summary>
    public Rgb[] Frame => _frame;

    static long Now => Stopwatch.GetTimestamp();
    static bool Elapsed(long since, TimeSpan span) => Stopwatch.GetElapsedTime(since) >= span;

    // -- config ------------------------------------------------------------------

    static JsonObject Load()
    {
        var cfg = JsonNode.Parse(DefaultConfig)!.AsObject();
        try
        {
            if (JsonNode.Parse(File.ReadAllText(AppPaths.ConfigPath)) is JsonObject saved)
                foreach (var (k, v) in saved.ToList())
                    cfg[k] = v?.DeepClone();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return cfg;
    }

    static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented = true, IndentSize = 1, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void SaveConfig()
    {
        lock (Sync)
        {
            Directory.CreateDirectory(AppPaths.ConfigDir);
            var tmp = AppPaths.ConfigPath + ".tmp";
            File.WriteAllText(tmp, Config.ToJsonString(SaveOptions));
            File.Move(tmp, AppPaths.ConfigPath, overwrite: true);
        }
    }

    // -- lifecycle -------------------------------------------------------------

    public void Start()
    {
        new Thread(DeviceLoop) { IsBackground = true, Name = "device" }.Start();
        new Thread(LiveLoop) { IsBackground = true, Name = "live", Priority = ThreadPriority.AboveNormal }.Start();
    }

    public void Stop()
    {
        _stop.Cancel();
        lock (Sync)
        {
            _live?.Dispose();
            if (Direct && Dev.Connected)
            {
                try { Dev.DirectOff(); }
                catch (Exception) { }
            }
            Dev.Close();
        }
    }

    /// <summary>(Re)connect and read the keyboard's current state.</summary>
    bool Sync_()
    {
        if (!Dev.Open())
        {
            Status = "disconnected";
            return false;
        }
        Profile = Dev.ActiveProfile();
        _params = Dev.ReadParams(Profile);
        _custom = Dev.ReadCustom(Profile);
        _side = Dev.ReadSide(Profile);
        Direct = Dev.SupportsDirect();
        _defaultKeymap = Dev.ReadDefaultKeymap();
        _keymaps = [.. Enumerable.Range(0, K580.Profiles).Select(Dev.ReadKeymap)];
        Status = "connected";
        return true;
    }

    /// <summary>Any I/O problem: drop the handle; DeviceLoop reconnects and streaming resumes.</summary>
    void Lost(Exception err)
    {
        lock (Sync)
        {
            if (Status == "connected")
            {
                Log.Warn($"keyboard connection lost: {err.Message}");
                _lastReconnect = 0;           // first retry right away, then every ReconnectInterval
            }
            Dev.Close();
            Status = "disconnected";
        }
    }

    void DeviceLoop()
    {
        while (!_stop.Token.WaitHandle.WaitOne(50))
        {
            try
            {
                lock (Sync)
                {
                    if (Status != "connected")
                    {
                        if (_lastReconnect == 0 || Elapsed(_lastReconnect, ReconnectInterval))
                        {
                            _lastReconnect = Now;
                            if (Sync_())
                                Log.Info($"keyboard connected (direct mode: {Direct})");
                        }
                        continue;
                    }
                    if (_pending != null && Elapsed(_pendingAt, WriteDebounce)
                        && (_lastWrite == 0 || Elapsed(_lastWrite, WriteMinInterval)))
                        Flush();
                }
            }
            catch (Exception e)          // this thread must never die
            {
                Lost(e);
            }
        }
    }

    void Flush()
    {
        var pending = _pending!;
        _pending = null;
        int before = Dev.FlashWrites;
        if (!Streaming)
            ReleaseDirect();             // built-in changes must be visible
        try
        {
            // colours first so switching to Custom never flashes the old layout
            if (pending.Custom != null && !pending.Custom.SequenceEqual(_custom ?? []))
            {
                Dev.WriteCustom(Profile, pending.Custom, _custom);
                _custom = pending.Custom;
            }
            if (pending.Params != null && !pending.Params.ToBytes().SequenceEqual(_params!.ToBytes()))
            {
                Dev.WriteParams(Profile, pending.Params, _params);
                _params = pending.Params;
            }
            if (pending.Side != null && !pending.Side.SequenceEqual(_side ?? []))
            {
                Dev.WriteSide(Profile, pending.Side, _side);
                _side = pending.Side;
            }
        }
        catch
        {
            _pending ??= pending;        // retry after reconnect unless superseded
            throw;
        }
        int written = Dev.FlashWrites - before;
        if (written > 0)
        {
            _lastWrite = Now;
            Config["flash_writes"] = JsonNum.Int(Config["flash_writes"], 0) + written;
            SaveConfig();
        }
    }

    // -- hardware lighting ---------------------------------------------------------

    /// <summary>Queue a change; it is written once the user stops fiddling (now: skip that wait).</summary>
    public void Request(Params? p = null, Rgb[]? custom = null, byte[]? side = null, bool now = false)
    {
        lock (Sync)
        {
            var pending = _pending ?? new Pending();
            _pending = new Pending
            {
                Params = p ?? pending.Params, Custom = custom ?? pending.Custom, Side = side ?? pending.Side,
            };
            _pendingAt = now ? Now - (long)(WriteDebounce.TotalSeconds * Stopwatch.Frequency) : Now;
        }
    }

    /// <summary>(params, custom, side) the keyboard will show once pending writes are done.</summary>
    public (Params? Params, Rgb[]? Custom, byte[]? Side) Desired()
    {
        lock (Sync)
            return (_pending?.Params ?? _params, _pending?.Custom ?? _custom, _pending?.Side ?? _side);
    }

    public void ApplyProfile(string name)
    {
        if (Config["profiles"]![name] is not JsonObject prof)
            throw new KeyNotFoundException(name);
        Request(Params.FromJson(prof["params"]!), CustomFromJson(prof["custom"]), SideFromJson(prof["side"]));
    }

    /// <summary>Write the current frame of a scene into the hardware Custom mode.</summary>
    public void Bake(JsonArray specs)
    {
        Rgb[] frame;
        if (_live != null && _liveSpecs != null && JsonNode.DeepEquals(_liveSpecs, specs))
            frame = _frame;
        else
            using (var sc = EffectRegistry.Scene(specs))
                frame = sc.Frame(0.0);
        var p = Desired().Params ?? throw new IOException("keyboard not connected");
        Request(p with { Mode = Modes.Custom }, LedsOnly(frame));
    }

    public static Rgb[]? CustomFromJson(JsonNode? n) =>
        n is JsonArray a ? [.. a.Select(c => new Rgb((byte)JsonNum.Int(c![0]), (byte)JsonNum.Int(c[1]), (byte)JsonNum.Int(c[2])))] : null;

    public static byte[]? SideFromJson(JsonNode? n) => n is JsonArray a ? [.. a.Select(v => (byte)JsonNum.Int(v))] : null;

    static JsonArray CustomToJson(Rgb[] c) => [.. c.Select(x => (JsonNode)new JsonArray(x.R, x.G, x.B))];

    // -- keys and macros ---------------------------------------------------------------

    List<JsonObject> MacroList() => [.. Config["macros"]!.AsArray().Select(m => m!.AsObject())];

    byte[] WantedKeymap()
    {
        var index = MacroList().Select((m, n) => ((string)m["name"]!, n)).ToDictionary(x => x.Item1, x => x.n);
        return Macros.BuildKeymap(_defaultKeymap!, Config["key_assignments"]!.AsObject(), index);
    }

    /// <summary>Per hardware profile: does the keyboard hold the keymap the app expects?</summary>
    JsonNode? KeysInSync()
    {
        if (_keymaps == null || _defaultKeymap == null)
            return null;
        var want = WantedKeymap();
        return new JsonArray([.. _keymaps.Select(km => (JsonNode)km.SequenceEqual(want))]);
    }

    /// <summary>Validate, store in the config and write macros + keymaps (all 3 profiles) to the keyboard.</summary>
    public void SaveKeys(JsonArray macroList, JsonObject? assignments)
    {
        var ms = macroList.Select(Macros.NormalizeMacro).ToList();
        var names = ms.Select(m => (string)m["name"]!).ToList();
        if (names.Distinct().Count() != names.Count)
            throw new ArgumentException("two macros have the same name");
        var assigned = Macros.NormalizeAssignments(assignments, names);
        var (image, index) = Macros.Compile(ms);
        lock (Sync)
        {
            if (Status != "connected")
                throw new IOException("keyboard not connected");
            var keymap = Macros.BuildKeymap(_defaultKeymap!, assigned, index);
            int before = Dev.FlashWrites;
            try
            {
                var old = Dev.ReadMacros(image.Length);
                Dev.WriteMacros(image, old);
                for (int p = 0; p < K580.Profiles; p++)
                    Dev.WriteKeymap(p, keymap, _keymaps![p]);
                // read everything back: a flash write that didn't land must not look saved
                _keymaps = [.. Enumerable.Range(0, K580.Profiles).Select(Dev.ReadKeymap)];
                if (!Dev.ReadMacros(image.Length).SequenceEqual(image) || _keymaps.Any(km => !km.SequenceEqual(keymap)))
                    throw new IOException("the keyboard did not store everything; please save again");
            }
            finally
            {
                Config["flash_writes"] = JsonNum.Int(Config["flash_writes"], 0) + Dev.FlashWrites - before;
                Config["macros"] = new JsonArray([.. ms]);
                Config["key_assignments"] = assigned;
                SaveConfig();
            }
            Log.Info($"keys saved: {ms.Count} macros, {assigned.Count} remapped keys, {Dev.FlashWrites - before} packets written");
        }
    }

    // -- live software effects -------------------------------------------------------

    /// <summary>specs: layer list for EffectRegistry.Scene(); empty/null stops.</summary>
    public void SetLive(JsonArray? specs, bool stream = false)
    {
        var fresh = specs is { Count: > 0 } ? EffectRegistry.Scene(specs) : null;
        lock (Sync)
        {
            var old = _live;
            _live = fresh;
            _liveSpecs = fresh != null ? (JsonArray)specs!.DeepClone() : null;
            old?.Dispose();
            if (fresh == null || old == null)
                _liveStarted = Now;      // keep time running while tweaking
            _liveStream = stream && fresh != null;
            if (!_liveStream)
                ReleaseDirect();
            // remembered so the next start (e.g. at login) resumes what ran on the keyboard
            JsonNode? remember = _liveStream ? new JsonObject { ["layers"] = specs!.DeepClone() } : null;
            if (!JsonNode.DeepEquals(Config["last_live"], remember))
            {
                Config["last_live"] = remember;
                SaveConfig();
            }
        }
    }

    /// <summary>Resume the live effect that was running on the keyboard when the app last closed.</summary>
    public bool RestoreLastLive()
    {
        if (Config["last_live"] is not JsonObject last)
            return false;
        try
        {
            SetLive((JsonArray)last["layers"]!.DeepClone(), stream: true);
            Log.Info($"resumed last live effect: {_live?.Name}");
            return true;
        }
        catch (Exception e)              // e.g. a GIF it used was deleted
        {
            Log.Warn($"could not resume last live effect: {e.Message}");
            Config["last_live"] = null;
            return false;
        }
    }

    /// <summary>Hand the LEDs back to the keyboard's own effects right away (no flash write).
    /// Sent whenever we're not streaming, even if we think we weren't: it's harmless
    /// and makes sure no stale direct-mode frame stays on the keyboard.</summary>
    void ReleaseDirect()
    {
        if (Direct && Status == "connected")
        {
            try { Dev.DirectOff(); }
            catch (Exception e) { Lost(e); }
        }
    }

    /// <summary>Wanted and currently possible; resumes by itself after a reconnect.</summary>
    public bool Streaming => _liveStream && _live != null && Direct && Status == "connected";

    double LiveTime => Stopwatch.GetElapsedTime(_liveStarted).TotalSeconds;

    public void KeyPressed(int index) => _live?.KeyPressed(index, LiveTime);

    void LiveLoop()
    {
        var period = TimeSpan.FromSeconds(1.0 / LiveFps);
        long next = Now;
        while (!_stop.IsCancellationRequested)
        {
            next += (long)(period.TotalSeconds * Stopwatch.Frequency);
            var eff = _live;
            if (eff != null)
            {
                Rgb[] frame;
                try
                {
                    frame = eff.Frame(LiveTime);
                }
                catch (Exception e)       // a buggy effect must not kill the loop
                {
                    Log.Error($"effect {eff.Name} failed", e);
                    frame = new Rgb[K580.NumLeds];
                }
                _frame = frame;
                if (Streaming)
                {
                    try
                    {
                        lock (Sync)
                            if (Streaming && _live == eff)   // re-check: may have stopped meanwhile
                                Dev.DirectFrame(WithIndicators(frame));
                    }
                    catch (Exception e)
                    {
                        Lost(e);
                    }
                }
            }
            long delay = next - Now;
            if (delay > 0)
                Win32.PreciseSleep(TimeSpan.FromSeconds((double)delay / Stopwatch.Frequency));
            else
                next = Now;
        }
    }

    // -- for the UI -----------------------------------------------------------------

    public JsonObject Snapshot()
    {
        lock (Sync)
        {
            var (p, custom, side) = Desired();
            return new JsonObject
            {
                ["status"] = Status,
                ["direct"] = Direct,
                ["profile_slot"] = Profile,
                ["params"] = p?.ToJson(),
                ["custom"] = custom != null ? CustomToJson(custom) : null,
                ["side"] = side != null ? new JsonArray([.. side.Select(b => (JsonNode)(int)b)]) : null,
                ["pending"] = _pending != null,
                ["flash_writes"] = Config["flash_writes"]?.DeepClone(),
                ["profiles"] = Config["profiles"]?.DeepClone(),
                ["rules"] = Config["rules"]?.DeepClone(),
                ["default_profile"] = Config["default_profile"]?.DeepClone(),
                ["automation"] = Config["automation"]?.DeepClone(),
                ["keys_in_sync"] = KeysInSync(),
                ["live"] = _live?.Name,
                ["live_stream"] = _liveStream,       // wanted
                ["streaming"] = Streaming,           // actually reaching the keyboard
            };
        }
    }

    /// <summary>Zero entries without an LED (keeps flash data tidy).</summary>
    static Rgb[] LedsOnly(Rgb[] colors) =>
        [.. colors.Select((c, i) => Layout.ByIndex.ContainsKey(i) ? c : Rgb.Black)];

    /// <summary>Draw the lock indicator LEDs the way stock firmware does (it can't while streaming).</summary>
    static Rgb[] WithIndicators(Rgb[] frame)
    {
        var (num, caps, scroll) = Win32.LockStates();
        var output = (Rgb[])frame.Clone();
        output[K580.LedIndicatorA] = new Rgb((byte)(scroll ? 255 : 0), (byte)(caps ? 255 : 0), (byte)(num ? 255 : 0));
        output[K580.LedIndicatorB] = new Rgb((byte)(num || caps || scroll ? 255 : 0), 0, 0);
        return output;
    }
}
