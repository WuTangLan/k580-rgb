"""Start with Windows: a per-user Run entry (no admin rights needed)."""
import os
import sys
import winreg

RUN_KEY = r'Software\Microsoft\Windows\CurrentVersion\Run'
RUN_NAME = 'K580 RGB'
LAUNCHER = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'k580rgb.pyw')


def _pythonw():
    exe = sys.executable
    w = os.path.join(os.path.dirname(exe), 'pythonw.exe')
    return w if os.path.isfile(w) else exe


def command():
    return '"%s" "%s" --no-browser' % (_pythonw(), LAUNCHER)


def enabled():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as k:
            return bool(winreg.QueryValueEx(k, RUN_NAME)[0])
    except OSError:
        return False


def set_enabled(on):
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as k:
        if on:
            winreg.SetValueEx(k, RUN_NAME, 0, winreg.REG_SZ, command())
        else:
            try:
                winreg.DeleteValue(k, RUN_NAME)
            except FileNotFoundError:
                pass
