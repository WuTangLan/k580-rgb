// Per-app profile switching and key presses for reactive effects.
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace K580Rgb.Core;

public sealed class Automation(Controller c)
{
    static readonly TimeSpan PollForeground = TimeSpan.FromSeconds(1.0);
    static readonly TimeSpan MinSwitchInterval = TimeSpan.FromSeconds(5.0);   // between automatic profile changes (flash wear)
    static readonly TimeSpan PollKeys = TimeSpan.FromMilliseconds(20);

    // LED index (row * 21 + col) -> Windows virtual-key code
    static readonly int?[][] VkRows =
    [
        [0x1B, .. Enumerable.Range(0x70, 12).Select(v => (int?)v), null, 0x2C, 0x91, 0x13],
        [0xC0, .. Enumerable.Range(0x31, 9).Select(v => (int?)v), 0x30, 0xBD, 0xBB, 0x08, 0x2D, 0x24, 0x21, 0x90, 0x6F, 0x6A, 0x6D],
        [0x09, .. "QWERTYUIOP".Select(ch => (int?)ch), 0xDB, 0xDD, 0xDC, 0x2E, 0x23, 0x22, 0x67, 0x68, 0x69, 0x6B],
        [0x14, .. "ASDFGHJKL".Select(ch => (int?)ch), 0xBA, 0xDE, null, 0x0D, null, null, null, 0x64, 0x65, 0x66],
        [0xA0, null, .. "ZXCVBNM".Select(ch => (int?)ch), 0xBC, 0xBE, 0xBF, null, 0xA1, null, 0x26, null, 0x61, 0x62, 0x63],
        [0xA2, 0x5B, 0xA4, 0x20, 0xA5, null, 0x5D, null, 0xA3, null, null, null, null, null, 0x25, 0x28, 0x27, null, 0x60, 0x6E],
    ];
    static readonly Dictionary<int, int> VkToLed = VkRows
        .SelectMany((vks, r) => vks.Select((vk, col) => (vk, led: r * 21 + col)))
        .Where(x => x.vk != null).ToDictionary(x => x.vk!.Value, x => x.led);

    static readonly string Self = Path.GetFileName(Environment.ProcessPath ?? "k580rgb.exe").ToLowerInvariant();

    volatile string[] _recent = [];
    long _lastSwitch;

    public string? CurrentApp { get; private set; }
    /// <summary>Most recent first, for building rules in the UI.</summary>
    public IReadOnlyList<string> RecentApps => _recent;
    /// <summary>Profile last applied automatically; reset when the rules change.</summary>
    public string? Applied { get; set; }

    public void Start()
    {
        new Thread(AppLoop) { IsBackground = true, Name = "automation" }.Start();
        new Thread(KeyLoop) { IsBackground = true, Name = "keys" }.Start();
    }

    string? TargetProfile(string app)
    {
        var cfg = c.Config;
        foreach (var rule in cfg["rules"]!.AsArray())
            if (string.Equals((string?)rule!["process"], app, StringComparison.OrdinalIgnoreCase))
                return (string?)rule["profile"];
        return (string?)cfg["default_profile"];
    }

    void AppLoop()
    {
        while (true)
        {
            Thread.Sleep(PollForeground);
            try
            {
                var app = Win32.ForegroundProcess();
                if (app != null)
                {
                    CurrentApp = app;
                    if (_recent.FirstOrDefault() != app)
                        _recent = [app, .. _recent.Where(a => a != app).Take(14)];
                }
                if (app == null || app == Self)
                    continue;
                lock (c.Sync)
                {
                    var cfg = c.Config;
                    if (!(bool)cfg["automation"]!)
                        continue;
                    var target = TargetProfile(app);
                    if (target != null && target != Applied && cfg["profiles"]![target] is JsonObject
                        && (_lastSwitch == 0 || Stopwatch.GetElapsedTime(_lastSwitch) >= MinSwitchInterval))
                    {
                        _lastSwitch = Stopwatch.GetTimestamp();
                        Applied = target;
                        c.ApplyProfile(target);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warn($"automation: {e.Message}");
            }
        }
    }

    void KeyLoop()
    {
        var down = new HashSet<int>();
        while (true)
        {
            Win32.PreciseSleep(PollKeys);
            var eff = c.Live;
            if (eff == null || !eff.Reactive)
            {
                down.Clear();
                continue;
            }
            foreach (var (vk, led) in VkToLed)
            {
                if (Win32.IsDown(vk))
                {
                    if (down.Add(vk))
                        c.KeyPressed(led);
                }
                else
                {
                    down.Remove(vk);
                }
            }
        }
    }
}
