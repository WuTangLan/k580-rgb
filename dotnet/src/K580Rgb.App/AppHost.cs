// System-tray icon: open the window, live presets, built-in lighting, profiles, settings, quit.
//
// The icon can be hidden (Settings in the UI, or the menu); the app then keeps running in the
// background and starting it again opens the window.
using System.Text.Json.Nodes;
using K580Rgb.Core;
using K580Rgb.Core.Device;
using K580Rgb.Core.Web;

namespace K580Rgb.App;

sealed class AppHost : ApplicationContext, IAppHost
{
    static readonly string[] BrightnessLabels = ["Off", "12%", "25%", "50%", "75%", "100%"];   // firmware table 00 20 40 80 BE FF

    readonly Controller _c;
    readonly string _url;
    readonly NotifyIcon _tray;
    readonly Control _ui = new();               // marshals calls from server threads onto the UI thread
    MainWindow? _window;

    public static readonly Icon AppIcon = LoadIcon();

    public AppHost(Controller controller, string url)
    {
        (_c, _url) = (controller, url);
        _ui.CreateControl();
        _tray = new NotifyIcon
        {
            Icon = AppIcon, Text = "K580 RGB", ContextMenuStrip = new ContextMenuStrip(),
            Visible = (bool?)controller.Config["tray_icon"] ?? true,
        };
        _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu(_tray.ContextMenuStrip);
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        BuildMenu(_tray.ContextMenuStrip);
    }

    static Icon LoadIcon()
    {
        using var s = typeof(AppHost).Assembly.GetManifestResourceStream("K580Rgb.app.ico")!;
        return new Icon(s);
    }

    void OnUi(Action a)
    {
        if (_ui.InvokeRequired)
            _ui.BeginInvoke(a);
        else
            a();
    }

    // -- IAppHost -------------------------------------------------------------------

    public void ShowWindow() => OnUi(() =>
    {
        if (_window == null || _window.IsDisposed)
        {
            _window = new MainWindow(_url);
            _window.FormClosed += (_, _) => _window = null;   // closing frees the browser engine; the app keeps running
            _window.Show();
        }
        else
        {
            if (_window.WindowState == FormWindowState.Minimized)
                _window.WindowState = FormWindowState.Normal;
            _window.Activate();
        }
    });

    public void ShowTray(bool on) => OnUi(() => _tray.Visible = on);

    public void Quit() => OnUi(() =>
    {
        _window?.Close();
        _tray.Visible = false;
        ExitThread();
    });

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }

    // -- menu -----------------------------------------------------------------

    /// <summary>Runs a menu action off the UI thread (it may wait for the keyboard) and logs failures.</summary>
    static void Safe(string what, Action a) => Task.Run(() =>
    {
        try { a(); }
        catch (Exception e) { Log.Warn($"tray: could not {what}: {e.Message}"); }
    });

    void StartPreset(JsonNode layers) =>
        Safe("start preset", () => _c.SetLive(Presets.Normalize(layers.AsArray()), stream: true));

    void Hardware(Func<Params, Params> change) => Safe("change lighting", () =>
    {
        _c.SetLive(null);
        if (_c.Desired().Params is Params p)
            _c.Request(change(p), now: true);
    });

    void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        var cfg = _c.Config;
        JsonObject mine, profiles;
        bool automationOn;
        lock (_c.Sync)
        {
            mine = (JsonObject)(cfg["live_presets"]?.DeepClone() ?? new JsonObject());
            profiles = (JsonObject)(cfg["profiles"]?.DeepClone() ?? new JsonObject());
            automationOn = (bool?)cfg["automation"] ?? false;
        }
        var live = _c.Live;
        bool connected = _c.Status == "connected";
        var p = connected ? _c.Desired().Params : null;
        string status = !connected ? "Keyboard not connected"
            : live != null && _c.LiveStream ? $"Live: {live.Name}"
            : p != null ? $"Built-in: {(Modes.ByValue.TryGetValue(p.Mode, out var m) ? m.Name : "?")}" : "Built-in effect";

        var open = new ToolStripMenuItem("Open K580 RGB", null, (_, _) => ShowWindow()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripMenuItem(status) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        if (mine.Count > 0)
        {
            var my = new ToolStripMenuItem("My presets");
            foreach (var (name, layers) in mine.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                my.DropDownItems.Add(name, null, (_, _) => StartPreset(layers!));
            menu.Items.Add(my);
        }
        var builtin = new ToolStripMenuItem("Live presets");
        foreach (var (name, layers) in Presets.Builtin())
            builtin.DropDownItems.Add(name, null, (_, _) => StartPreset(layers!));
        menu.Items.Add(builtin);
        menu.Items.Add(new ToolStripMenuItem("Stop live effect", null, (_, _) => Safe("stop live effect", () => _c.SetLive(null)))
                       { Enabled = live != null });
        menu.Items.Add(new ToolStripSeparator());

        bool hwOn = p != null && !(live != null && _c.LiveStream);
        var effects = new ToolStripMenuItem("Built-in effects") { Enabled = p != null };
        foreach (var mode in Modes.All)
            effects.DropDownItems.Add(new ToolStripMenuItem(mode.Name, null, (_, _) => Hardware(x => x with { Mode = mode.Value }))
                                      { Checked = hwOn && p!.Mode == mode.Value });
        menu.Items.Add(effects);
        var brightness = new ToolStripMenuItem("Brightness") { Enabled = p != null };
        for (int b = Modes.BrightnessMax; b >= 0; b--)
        {
            int level = b;
            brightness.DropDownItems.Add(new ToolStripMenuItem(BrightnessLabels[b], null, (_, _) => Hardware(x => x with { Brightness = level }))
                                         { Checked = p != null && p.Brightness == b });
        }
        menu.Items.Add(brightness);
        if (profiles.Count > 0)
        {
            var prof = new ToolStripMenuItem("Profiles");
            foreach (var (name, _) in profiles)
                prof.DropDownItems.Add(name, null, (_, _) => Safe("apply profile", () => { _c.SetLive(null); _c.ApplyProfile(name); }));
            menu.Items.Add(prof);
            menu.Items.Add(new ToolStripMenuItem("Switch profiles by app", null, (_, _) => Safe("switch automation", () =>
            {
                lock (_c.Sync)
                {
                    _c.Config["automation"] = !((bool?)_c.Config["automation"] ?? false);
                    _c.SaveConfig();
                }
            })) { Checked = automationOn });
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => Safe("change autostart", () => Autostart.SetEnabled(!Autostart.Enabled)))
                       { Checked = Autostart.Enabled });
        menu.Items.Add(new ToolStripMenuItem("Hide this icon", null, (_, _) =>
        {
            lock (_c.Sync)
            {
                _c.Config["tray_icon"] = false;
                _c.SaveConfig();
            }
            _tray.Visible = false;
        }));
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));
    }
}
