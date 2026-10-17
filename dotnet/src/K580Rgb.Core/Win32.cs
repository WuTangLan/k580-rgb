// Windows helpers: foreground app, key and lock states.
using System.Runtime.InteropServices;
using System.Text;

namespace K580Rgb.Core;

public static class Win32
{
    const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref uint size);

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>(num, caps, scroll) lock state from the OS.</summary>
    public static (bool Num, bool Caps, bool Scroll) LockStates() =>
        ((GetKeyState(0x90) & 1) != 0, (GetKeyState(0x14) & 1) != 0, (GetKeyState(0x91) & 1) != 0);

    /// <summary>Full path of a process's executable, or null when it can't be queried.</summary>
    public static string? ProcessImage(uint pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            var buf = new StringBuilder(1024);
            uint size = (uint)buf.Capacity;
            return QueryFullProcessImageNameW(h, 0, buf, ref size) ? buf.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>Executable name of the focused window, e.g. "chrome.exe" (lower case).</summary>
    public static string? ForegroundProcess()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        var path = ProcessImage(pid);
        return path == null ? null : Path.GetFileName(path).ToLowerInvariant();
    }
}
