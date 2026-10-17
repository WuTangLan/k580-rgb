// Start with Windows: a per-user Run entry (no admin rights needed). Same entry name as
// the Python app, so turning it on here replaces the Python launcher.
using Microsoft.Win32;

namespace K580Rgb.Core;

public static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "K580 RGB";

    static string Command => $"\"{Environment.ProcessPath}\" --background";

    /// <summary>On only when the entry starts this exe (an entry for the Python app counts as off).</summary>
    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string v && Environment.ProcessPath is string exe
                   && v.Contains(exe, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
            k.SetValue(RunName, Command);
        else if (Enabled)
            k.DeleteValue(RunName, throwOnMissingValue: false);
    }
}
