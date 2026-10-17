// Local web UI + JSON API (127.0.0.1 only).
//
// POSTs must carry the header X-K580: 1. Browsers can't send custom headers
// cross-origin without a CORS preflight (which we never approve), so other websites
// can't drive the keyboard or wear its flash.
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using K580Rgb.Core.Device;
using K580Rgb.Core.Effects;
using K580Rgb.Core.Sources;

namespace K580Rgb.Core.Web;

/// <summary>What the UI can do to the app itself.</summary>
public interface IAppHost
{
    void Quit();
    void ShowTray(bool on);
    void ShowWindow();
}

public sealed class Api
{
    const int PreviewFps = 20;

    readonly Controller _c;
    readonly Automation _automation;
    readonly IAppHost? _app;
    readonly HashSet<string> _allowedHosts;
    readonly byte[] _static;
    readonly Dictionary<string, Func<JsonObject, JsonObject?>> _posts;

    public static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    public Api(Controller controller, Automation automation, int port, IAppHost? app)
    {
        (_c, _automation, _app) = (controller, automation, app);
        _allowedHosts = [$"127.0.0.1:{port}", $"localhost:{port}"];
        _static = Encoding.UTF8.GetBytes(StaticInfo().ToJsonString());
        _posts = new()
        {
            ["/api/media/delete"] = b => { Media.Delete((string)b["name"]!); return null; },
            ["/api/apply"] = Apply,
            ["/api/profiles/save"] = SaveProfile,
            ["/api/profiles/delete"] = DeleteProfile,
            ["/api/presets/save"] = SavePreset,
            ["/api/presets/delete"] = b => { LivePresets().Remove((string)b["name"]!); _c.SaveConfig(); return null; },
            ["/api/profiles/apply"] = b => { _c.ApplyProfile((string)b["name"]!); return null; },
            ["/api/automation"] = SetAutomation,
            ["/api/live"] = b => { _c.SetLive(b["layers"] as JsonArray, (bool?)b["stream"] ?? false); return null; },
            ["/api/bake"] = b => { _c.Bake(b["layers"]!.AsArray()); return null; },
            ["/api/keys/save"] = b => { _c.SaveKeys(b["macros"]!.AsArray(), b["assignments"] as JsonObject); return null; },
            ["/api/macros/text"] = TextMacro,
            ["/api/settings"] = Settings,
            ["/api/quit"] = Quit,
            ["/api/show"] = b => { _app?.ShowWindow(); return null; },
        };
    }

    static JsonObject StaticInfo() => new()
    {
        ["layout"] = new JsonArray([.. Layout.Keys.Concat(Layout.SideKeys).Select(k => (JsonNode)k.ToJson())]),
        ["iso_keys"] = new JsonArray([.. Layout.IsoKeys.Select(k => (JsonNode)k.ToJson())]),
        ["size"] = new JsonArray(Layout.Width, Layout.Height),
        ["modes"] = Modes.ToJson(),
        ["custom_mode"] = Modes.Custom,
        ["brightness_max"] = Modes.BrightnessMax,
        ["speed_max"] = Modes.SpeedMax,
        ["effects"] = EffectRegistry.ToJson(),
        ["zones"] = new JsonArray([.. EffectRegistry.Zones.Select(z => (JsonNode)z)]),
        ["side_modes"] = new JsonArray([.. K580.SideModes.Select(z => (JsonNode)z)]),
        ["side_colors"] = new JsonArray([.. K580.SideColors.Select(z => (JsonNode)z)]),
        ["bounds"] = new JsonArray([.. Layout.Bounds.Select(b => (JsonNode)b)]),
        ["blends"] = new JsonArray([.. EffectRegistry.Blends.Select(z => (JsonNode)z)]),
        ["presets"] = Presets.Builtin(),
        ["version"] = Version,
        ["sources"] = new JsonObject(Capture.Available().Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
        ["keycodes"] = Keycodes.KeysJson(),
        ["media_codes"] = Keycodes.MediaJson(),
        ["assignable"] = Macros.Assignable(),
        ["macro_limits"] = new JsonObject
        {
            ["macros"] = Macros.MaxMacros, ["delay_unit_ms"] = Macros.DelayUnitMs,
            ["max_delay_ms"] = (int)(Macros.MaxDelay * Macros.DelayUnitMs),
            ["bytes"] = Macros.AreaEnd - Macros.MacroBase - Macros.DataOffset,
        },
    };

    // -- POST handlers ---------------------------------------------------------------------

    JsonObject? Apply(JsonObject body)
    {
        var p = body["params"] is JsonObject pj ? Params.FromJson(pj) : null;
        var custom = Controller.CustomFromJson(body["custom"]);
        var side = Controller.SideFromJson(body["side"]);
        if (side != null && !(side.Length == 3 && side[0] < K580.SideModes.Length && side[1] <= 5 && side[2] < K580.SideColors.Length))
            throw new ArgumentException("side settings out of range");
        if (custom != null && custom.Length != K580.NumLeds)
            throw new ArgumentException("custom colours need 126 entries");
        _c.Request(p, custom, side, now: (bool?)body["now"] ?? false);
        return null;
    }

    JsonObject? SaveProfile(JsonObject body)
    {
        var name = ((string?)body["name"] ?? "").Trim();
        if (name.Length == 0)
            throw new ArgumentException("empty name");
        var (p, custom, side) = _c.Desired();
        if (p == null)
            throw new IOException("keyboard not connected");
        _c.Config["profiles"]![name] = new JsonObject
        {
            ["params"] = p.ToJson(),
            ["custom"] = p.Mode == Modes.Custom && custom != null
                ? new JsonArray([.. custom.Select(x => (JsonNode)new JsonArray(x.R, x.G, x.B))]) : null,
            ["side"] = side != null ? new JsonArray([.. side.Select(v => (JsonNode)(int)v)]) : null,
        };
        _c.SaveConfig();
        return null;
    }

    JsonObject LivePresets()
    {
        if (_c.Config["live_presets"] is not JsonObject presets)
            _c.Config["live_presets"] = presets = [];
        return presets;
    }

    JsonObject? SavePreset(JsonObject body)
    {
        var name = (body["name"]?.ToString() ?? "").Trim();
        name = name[..Math.Min(40, name.Length)];
        if (name.Length == 0)
            throw new ArgumentException("empty name");
        if (body["layers"] is not JsonArray { Count: >= 1 and <= 8 } layers)
            throw new ArgumentException("a preset needs 1-8 layers");
        foreach (var layer in layers)
            if (layer is not JsonObject l || (string?)l["effect"] is not string e || !EffectRegistry.ByName.ContainsKey(e))
                throw new ArgumentException("unknown effect in preset");
        var presets = LivePresets();
        if (!presets.ContainsKey(name) && presets.Count >= 100)
            throw new ArgumentException("too many presets (100 max)");
        presets[name] = layers.DeepClone();
        _c.SaveConfig();
        return null;
    }

    JsonObject? DeleteProfile(JsonObject body)
    {
        var name = (string)body["name"]!;
        var cfg = _c.Config;
        cfg["profiles"]!.AsObject().Remove(name);
        cfg["rules"] = new JsonArray([.. cfg["rules"]!.AsArray().Where(r => (string?)r!["profile"] != name).Select(r => r!.DeepClone())]);
        if ((string?)cfg["default_profile"] == name)
            cfg["default_profile"] = null;
        _c.SaveConfig();
        return null;
    }

    JsonObject? SetAutomation(JsonObject body)
    {
        var cfg = _c.Config;
        cfg["rules"] = new JsonArray([.. body["rules"]!.AsArray()
            .Where(r => ((string?)r!["process"] ?? "").Trim().Length > 0)
            .Select(r => (JsonNode)new JsonObject
            {
                ["process"] = ((string)r!["process"]!).Trim().ToLowerInvariant(), ["profile"] = r["profile"]?.DeepClone(),
            })]);
        cfg["default_profile"] = (string?)body["default_profile"] is { Length: > 0 } d ? d : null;
        cfg["automation"] = (bool?)body["automation"] ?? false;
        _automation.Applied = null;
        _c.SaveConfig();
        return null;
    }

    static JsonObject? TextMacro(JsonObject body)
    {
        var text = body["text"]?.ToString() ?? "";
        double delay = JsonNum.Get(body["delay"], 10);
        return new JsonObject { ["events"] = Keycodes.TextEvents(text[..Math.Min(2000, text.Length)], delay) };
    }

    JsonObject? Settings(JsonObject body)
    {
        var cfg = _c.Config;
        foreach (var k in new[] { "tray_icon", "open_browser" })
            if (body.ContainsKey(k))
                cfg[k] = (bool?)body[k] ?? false;
        if (body.ContainsKey("tray_icon"))
            _app?.ShowTray((bool)cfg["tray_icon"]!);
        if (body.ContainsKey("autostart"))
            Autostart.SetEnabled((bool?)body["autostart"] ?? false);
        _c.SaveConfig();
        return null;
    }

    JsonObject? Quit(JsonObject body)
    {
        if (_app != null)     // like Quit in the tray: the LEDs are released on exit, the live effect resumes next start
            _ = Task.Delay(300).ContinueWith(_ => _app.Quit());      // reply first
        return null;
    }

    // -- GET ------------------------------------------------------------------------------------

    JsonObject State()
    {
        lock (_c.Sync)
        {
            var s = _c.Snapshot();
            var cfg = _c.Config;
            s["recent_apps"] = new JsonArray([.. _automation.RecentApps.Select(a => (JsonNode)a)]);
            s["media"] = new JsonArray([.. Media.Library().Select(m => (JsonNode)new JsonObject { ["name"] = m.Name, ["sample"] = m.Sample })]);
            s["live_presets"] = cfg["live_presets"]?.DeepClone() ?? new JsonObject();
            s["rocket_mania"] = RocketManiaStatus();
            s["macros"] = cfg["macros"]?.DeepClone();
            s["key_assignments"] = cfg["key_assignments"]?.DeepClone();
            s["settings"] = new JsonObject
            {
                ["tray_icon"] = (bool?)cfg["tray_icon"] ?? true,
                ["open_browser"] = (bool?)cfg["open_browser"] ?? true,
                ["autostart"] = Autostart.Enabled,
                ["can_quit"] = _app != null,
            };
            s["source_errors"] = new JsonObject { ["audio"] = Capture.Audio.Error, ["screen"] = Capture.Screen.Error };
            return s;
        }
    }

    static JsonObject RocketManiaStatus()
    {
        var rm = Games.RocketMania;
        var last = new JsonObject();
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        foreach (var (t, kind) in rm.Events)
            last[kind] = Math.Round(now - t, 1);
        return new JsonObject { ["folder"] = rm.Folder, ["error"] = rm.Error, ["last"] = last };
    }

    // -- dispatch ---------------------------------------------------------------------------------

    static byte[] Json(JsonNode n) => Encoding.UTF8.GetBytes(n.ToJsonString());

    static JsonObject Error(string msg) => new() { ["error"] = msg };

    public async Task Handle(HttpContext ctx)
    {
        if (ctx.Header("Host") is not string host || !_allowedHosts.Contains(host))
        {
            await ctx.SendAsync(403, Json(Error("forbidden")));
            return;
        }
        var path = ctx.Path;
        if (ctx.Method == "GET")
        {
            if (path is "/" or "/index.html")
                await ctx.SendAsync(200, UiPage(), "text/html; charset=utf-8");
            else if (path == "/api/static")
                await ctx.SendAsync(200, _static);
            else if (path == "/api/state")
                await ctx.SendAsync(200, Json(await Task.Run(State)));
            else if (path == "/api/frames")
                await StreamFrames(ctx);
            else
                await ctx.SendAsync(404, Json(Error("not found")));
            return;
        }
        if (ctx.Method != "POST")
        {
            await ctx.SendAsync(404, Json(Error("not found")));
            return;
        }
        if (ctx.Header("X-K580") != "1")
        {
            await ctx.SendAsync(403, Json(Error("missing X-K580 header")));
            return;
        }
        if (path == "/api/media/upload")
        {
            await Upload(ctx);
            return;
        }
        if (!_posts.TryGetValue(path, out var handler))
        {
            await ctx.SendAsync(404, Json(Error("not found")));
            return;
        }
        JsonObject? result;
        try
        {
            var body = ctx.Body.Length > 0 ? JsonNode.Parse(ctx.Body) as JsonObject ?? [] : [];
            result = await Task.Run(() =>
            {
                lock (_c.Sync)            // config and device state are shared with the background loops
                    return handler(body);
            });
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or FormatException or JsonException
                                      or InvalidOperationException or NullReferenceException or IOException
                                      or UnauthorizedAccessException or NotSupportedException or InvalidCastException)
        {
            await ctx.SendAsync(400, Json(Error($"{e.GetType().Name}: {e.Message}")));
            return;
        }
        var ok = new JsonObject { ["ok"] = true };
        foreach (var (k, v) in result ?? [])
            ok[k] = v?.DeepClone();
        await ctx.SendAsync(200, Json(ok));
    }

    async Task Upload(HttpContext ctx)
    {
        try
        {
            var filename = Uri.UnescapeDataString(ctx.Header("X-Filename") ?? "image.gif");
            var name = await Task.Run(() => Media.SaveUpload(filename, ctx.Body));
            await ctx.SendAsync(200, Json(new JsonObject { ["ok"] = true, ["name"] = name }));
        }
        catch (Exception e) when (e is ArgumentException or IOException)
        {
            await ctx.SendAsync(400, Json(Error(e.Message)));
        }
    }

    async Task StreamFrames(HttpContext ctx)
    {
        var stream = await ctx.BeginEventStreamAsync();
        var period = TimeSpan.FromSeconds(1.0 / PreviewFps);
        var hex = new StringBuilder(K580.NumLeds * 6 + 8);
        while (true)
        {
            if (_c.Live != null)
            {
                hex.Clear().Append("data: ");
                foreach (var c in _c.Frame)
                    hex.Append(c.R.ToString("x2")).Append(c.G.ToString("x2")).Append(c.B.ToString("x2"));
                hex.Append("\n\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(hex.ToString()));
                await stream.FlushAsync();
            }
            await Task.Delay(period);
        }
    }

    static byte[]? _page;

    /// <summary>The UI page, embedded in the assembly (the same file the Python app serves).</summary>
    static byte[] UiPage()
    {
        if (_page != null)
            return _page;
        using var s = typeof(Api).Assembly.GetManifestResourceStream("K580Rgb.ui.index.html")
                      ?? throw new FileNotFoundException("embedded UI page missing");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return _page = ms.ToArray();
    }
}
