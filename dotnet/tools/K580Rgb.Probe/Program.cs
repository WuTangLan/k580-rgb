// K580Rgb.Probe [read]
//
// No argument: list the keyboard's HID collections (touches nothing).
// read: open the vendor collection and run read-only commands, like the app does on connect.
// Quit the app first: two processes on the device mix up each other's replies.
using HidSharp;
using K580Rgb.Core.Device;

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
