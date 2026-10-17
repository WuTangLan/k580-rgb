"""Windows helpers: per-app profile switching and key presses for reactive effects."""
import ctypes
import os
import threading
import time
from ctypes import wintypes

user32 = ctypes.WinDLL('user32', use_last_error=True)
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
user32.GetAsyncKeyState.restype = ctypes.c_short
kernel32.OpenProcess.restype = wintypes.HANDLE
user32.GetForegroundWindow.restype = wintypes.HWND

PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
POLL_FOREGROUND = 1.0
MIN_SWITCH_INTERVAL = 5.0     # s between automatic profile changes (flash wear)
POLL_KEYS = 0.02

# LED index (row * 21 + col) -> Windows virtual-key code
_VK_ROWS = {
    0: [0x1B, *range(0x70, 0x7C), None, 0x2C, 0x91, 0x13],
    1: [0xC0, *range(0x31, 0x3A), 0x30, 0xBD, 0xBB, 0x08, 0x2D, 0x24, 0x21, 0x90, 0x6F, 0x6A, 0x6D],
    2: [0x09, *map(ord, 'QWERTYUIOP'), 0xDB, 0xDD, 0xDC, 0x2E, 0x23, 0x22, 0x67, 0x68, 0x69, 0x6B],
    3: [0x14, *map(ord, 'ASDFGHJKL'), 0xBA, 0xDE, None, 0x0D, None, None, None, 0x64, 0x65, 0x66],
    4: [0xA0, None, *map(ord, 'ZXCVBNM'), 0xBC, 0xBE, 0xBF, None, 0xA1, None, 0x26, None, 0x61, 0x62, 0x63],
    5: [0xA2, 0x5B, 0xA4, 0x20, 0xA5, None, 0x5D, None, 0xA3, None, None, None, None, None,
        0x25, 0x28, 0x27, None, 0x60, 0x6E],
}
VK_TO_LED = {vk: r * 21 + c for r, vks in _VK_ROWS.items() for c, vk in enumerate(vks) if vk}


def lock_states():
    """(num, caps, scroll) lock state from the OS."""
    return tuple(bool(user32.GetKeyState(vk) & 1) for vk in (0x90, 0x14, 0x91))


def foreground_process():
    """Executable name of the focused window, e.g. 'chrome.exe' (lower case)."""
    hwnd = user32.GetForegroundWindow()
    if not hwnd:
        return None
    pid = wintypes.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid.value)
    if not h:
        return None
    try:
        buf = ctypes.create_unicode_buffer(1024)
        size = wintypes.DWORD(len(buf))
        if not kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
            return None
        return os.path.basename(buf.value).lower()
    finally:
        kernel32.CloseHandle(h)


class Automation:
    def __init__(self, controller):
        self.c = controller
        self.current_app = None
        self.recent_apps = []         # most recent first, for building rules in the UI
        self.applied = None
        self._last_switch = 0.0

    def start(self):
        threading.Thread(target=self._app_loop, daemon=True).start()
        threading.Thread(target=self._key_loop, daemon=True).start()

    def target_profile(self, app):
        cfg = self.c.config
        for rule in cfg['rules']:
            if rule.get('process', '').lower() == app:
                return rule.get('profile')
        return cfg['default_profile']

    def _app_loop(self):
        while True:
            time.sleep(POLL_FOREGROUND)
            try:
                app = foreground_process()
            except OSError:
                continue
            if app:
                self.current_app = app
                if app not in self.recent_apps[:1]:
                    self.recent_apps = ([app] + [a for a in self.recent_apps if a != app])[:15]
            cfg = self.c.config
            if not (cfg['automation'] and app) or app in ('python.exe', 'pythonw.exe'):
                continue
            target = self.target_profile(app)
            if (target and target != self.applied and target in cfg['profiles']
                    and time.monotonic() - self._last_switch >= MIN_SWITCH_INTERVAL):
                self._last_switch = time.monotonic()
                self.applied = target
                self.c.apply_profile(target)

    def _key_loop(self):
        down = set()
        while True:
            time.sleep(POLL_KEYS)
            eff = self.c.live
            if not eff or not eff.reactive:
                down.clear()
                continue
            for vk, led in VK_TO_LED.items():
                pressed = user32.GetAsyncKeyState(vk) & 0x8000
                if pressed and vk not in down:
                    down.add(vk)
                    self.c.key_pressed(led)
                elif not pressed:
                    down.discard(vk)
