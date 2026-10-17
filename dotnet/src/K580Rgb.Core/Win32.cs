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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [ThreadStatic] static IntPtr _timer;

    /// <summary>Sleep with ~1 ms precision (Thread.Sleep follows the 15.6 ms system tick).</summary>
    public static void PreciseSleep(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
            return;
        if (_timer == IntPtr.Zero)
            _timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2 /* HIGH_RESOLUTION */, 0x1F0003 /* TIMER_ALL_ACCESS */);
        long due = -delay.Ticks;                       // relative, in 100 ns units
        if (_timer == IntPtr.Zero || !SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            Thread.Sleep(delay);
        else
            WaitForSingleObject(_timer, 0xFFFFFFFF);
    }

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
