// K580Rgb.Probe [read | static | frames <specs.json>]
//
// No argument: list the keyboard's HID collections (touches nothing).
// read: open the vendor collection and run read-only commands, like the app does on connect.
//       Quit the app first: two processes on the device mix up each other's replies.
// static, frames: JSON for tools/parity_check.py, which compares the port with the Python app.
using System.Text.Json.Nodes;
using HidSharp;
using K580Rgb.Core;
using K580Rgb.Core.Device;
using K580Rgb.Core.Effects;

if (args is ["static"])
{
    Console.WriteLine(new JsonObject
    {
        ["layout"] = new JsonArray([.. Layout.Keys.Concat(Layout.SideKeys).Select(k => (JsonNode)k.ToJson())]),
        ["iso_keys"] = new JsonArray([.. Layout.IsoKeys.Select(k => (JsonNode)k.ToJson())]),
        ["bounds"] = new JsonArray([.. Layout.Bounds.Select(b => (JsonNode)b)]),
        ["modes"] = Modes.ToJson(),
        ["effects"] = EffectRegistry.ToJson(),
        ["presets"] = Presets.Builtin(),
        ["normalized"] = new JsonObject(Presets.Builtin().Select(kv =>
            KeyValuePair.Create(kv.Key, (JsonNode?)Presets.Normalize(kv.Value!.AsArray())))),
        ["keycodes"] = Keycodes.KeysJson(),
        ["media_codes"] = Keycodes.MediaJson(),
        ["text_events"] = Keycodes.TextEvents("Hi! a_b\n", 15),
    }.ToJsonString());
    return 0;
}
if (args is ["macros", var macroPath])
{
    // {macros, assignments, default_keymap (hex)} -> {normalized, image (hex), index, keymap (hex)}
    var spec = JsonNode.Parse(File.ReadAllText(macroPath))!;
    var ms = spec["macros"]!.AsArray().Select(Macros.NormalizeMacro).ToList();
    var assigned = Macros.NormalizeAssignments(spec["assignments"]!.AsObject(), [.. ms.Select(m => (string)m["name"]!)]);
    var (image, index) = Macros.Compile(ms);
    var keymap = Macros.BuildKeymap(Convert.FromHexString((string)spec["default_keymap"]!), assigned, index);
    Console.WriteLine(new JsonObject
    {
        ["normalized"] = new JsonArray([.. ms]), ["assignments"] = assigned,
        ["image"] = Convert.ToHexString(image).ToLowerInvariant(), ["keymap"] = Convert.ToHexString(keymap).ToLowerInvariant(),
        ["index"] = new JsonObject(index.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value))),
    }.ToJsonString());
    return 0;
}
if (args is ["media", var mediaName, var fit, var times])
{
    var clip = K580Rgb.Core.Sources.Media.Load(mediaName, fit);
    Console.WriteLine(new JsonArray([.. times.Split(',').Select(t => (JsonNode)new JsonArray([..
        clip.At(double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).Select(c => (JsonNode)new JsonArray(c.R, c.G, c.B))]))]).ToJsonString());
    return 0;
}
if (args is ["text", var text, var height, var offsets])
{
    var strip = new K580Rgb.Core.Sources.TextStrip(text, double.Parse(height, System.Globalization.CultureInfo.InvariantCulture));
    Console.Error.WriteLine($"period {strip.Period}");
    Console.WriteLine(new JsonArray([.. offsets.Split(',').Select(o => (JsonNode)new JsonArray([..
        strip.Coverage(double.Parse(o, System.Globalization.CultureInfo.InvariantCulture)).Select(v => (JsonNode)Math.Round(v, 4))]))]).ToJsonString());
    return 0;
}
if (args is ["audio"])
{
    // capture what you hear for 3 s: block count and band levels (touches no keyboard)
    var audio = K580Rgb.Core.Sources.Capture.Audio;
    int blocks = 0;
    audio.AddListener(_ => Interlocked.Increment(ref blocks));
    audio.Acquire();
    for (int i = 0; i < 6; i++)
    {
        Thread.Sleep(500);
        Console.WriteLine($"{(i + 1) * 0.5:F1}s blocks {blocks} rate {audio.SampleRate} error {audio.Error ?? "-"} " +
                          $"levels {string.Join(" ", audio.Levels.Select(l => l.ToString("F2")))}");
    }
    audio.Release();
    return 0;
}
if (args is ["frames", var specPath])
{
    // [{name, layers, presses: [[index, t]...], times: [...]}] -> {name: [[r,g,b] x 126 per time]}
    var result = new JsonObject();
    foreach (var spec in JsonNode.Parse(File.ReadAllText(specPath))!.AsArray())
    {
        var frames = new JsonArray();
        using (var scene = EffectRegistry.Scene(spec!["layers"]!.AsArray()))
        {
            var presses = spec["presses"]?.AsArray() ?? [];
            int next = 0;
            foreach (var tn in spec["times"]!.AsArray())
            {
                double t = (double)tn!;
                for (; next < presses.Count && (double)presses[next]![1]! <= t; next++)
                    scene.KeyPressed((int)presses[next]![0]!, (double)presses[next]![1]!);
                frames.Add(new JsonArray([.. scene.Frame(t).Select(c => (JsonNode)new JsonArray(c.R, c.G, c.B))]));
            }
        }
        result[(string)spec["name"]!] = frames;
    }
    Console.WriteLine(result.ToJsonString());
    return 0;
}

foreach (var d in DeviceList.Local.GetHidDevices(K580.Vid, K580.Pid))
{
    string usages;
    try
    {
        usages = string.Join(", ", d.GetReportDescriptor().DeviceItems
            .SelectMany(i => i.Usages.GetAllValues()).Select(u => $"{u >> 16:X4}:{u & 0xFFFF:X2}"));
    }
    catch (Exception e) { usages = "? (" + e.GetType().Name + ")"; }
    Console.WriteLine($"{usages,-24} in {d.GetMaxInputReportLength(),2} out {d.GetMaxOutputReportLength(),2}  {d.DevicePath}");
}
var found = K580.Find();
Console.WriteLine(found == null ? "vendor collection: not found" : "vendor collection: " + found.DevicePath);

if (args is ["read"] && found != null)
{
    using var dev = new K580();
    if (!dev.Open())
    {
        Console.WriteLine("could not open the vendor collection");
        return 1;
    }
    int profile = dev.ActiveProfile();
    Console.WriteLine($"active profile: {profile}");
    Console.WriteLine($"params:         {dev.ReadParams(profile)}");
    Console.WriteLine($"side:           {string.Join(", ", dev.ReadSide(profile))}");
    var custom = dev.ReadCustom(profile);
    Console.WriteLine($"custom:         {custom.Count(c => c != Rgb.Black)} of {custom.Length} LEDs lit, first {custom[0]}");
    Console.WriteLine($"factory keymap: {Convert.ToHexString(dev.ReadDefaultKeymap()[..12])}...");
    Console.WriteLine($"direct mode:    {dev.SupportsDirect()}");
    Console.WriteLine($"flash writes:   {dev.FlashWrites}");
}
return 0;
