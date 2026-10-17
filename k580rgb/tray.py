"""System-tray icon: open the UI, start/stop live presets, start with Windows, quit."""
import logging
import os
import sys
import threading
import webbrowser
import winreg

import pystray
from PIL import Image, ImageDraw

from . import __version__, presets

log = logging.getLogger('k580rgb')

RUN_KEY = r'Software\Microsoft\Windows\CurrentVersion\Run'
RUN_NAME = 'K580 RGB'
LAUNCHER = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'k580rgb.pyw')


def icon_image(size=64):
    """A small keyboard with a rainbow glow, drawn at runtime (no image files)."""
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = size / 64
    for i, c in enumerate([(255, 40, 80), (255, 160, 0), (40, 220, 120), (0, 170, 255), (150, 60, 255)]):
        d.rectangle((8 * s + i * 9.6 * s, 14 * s, 8 * s + (i + 1) * 9.6 * s, 50 * s), fill=c + (255,))
    d.rounded_rectangle((6 * s, 16 * s, 58 * s, 48 * s), radius=6 * s, fill=(20, 22, 30, 235))
    for row, y in enumerate((21, 29, 37)):
        for col in range(7 - (row == 2)):
            x = 10 * s + col * 6.6 * s + (row == 2) * 3.3 * s
            d.rounded_rectangle((x, y * s, x + 5 * s, (y + 5.5) * s), radius=1.2 * s, fill=(235, 240, 255, 255))
    d.rounded_rectangle((20 * s, 44 * s, 44 * s, 46.5 * s), radius=1 * s, fill=(235, 240, 255, 255))
    return img


# -- start with Windows ---------------------------------------------------------------

def _pythonw():
    exe = sys.executable
    w = os.path.join(os.path.dirname(exe), 'pythonw.exe')
    return w if os.path.isfile(w) else exe


def autostart_command():
    return '"%s" "%s" --no-browser' % (_pythonw(), LAUNCHER)


def autostart_enabled():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as k:
            return bool(winreg.QueryValueEx(k, RUN_NAME)[0])
    except OSError:
        return False


def set_autostart(on):
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, RUN_KEY, 0, winreg.KEY_SET_VALUE) as k:
        if on:
            winreg.SetValueEx(k, RUN_NAME, 0, winreg.REG_SZ, autostart_command())
        else:
            try:
                winreg.DeleteValue(k, RUN_NAME)
            except FileNotFoundError:
                pass


# -- tray -----------------------------------------------------------------------------

class Tray:
    def __init__(self, controller, url):
        self.c, self.url = controller, url
        self._stopped = threading.Event()
        self.icon = pystray.Icon('k580rgb', icon_image(), 'K580 RGB', menu=pystray.Menu(self._items))

    def _start(self, layers):
        def run(icon, item):
            try:
                self.c.set_live(presets.normalize(layers), stream=True)
            except Exception as e:
                log.warning('tray: could not start preset: %s', e)
        return run

    def _items(self):
        mine = self.c.config.get('live_presets', {})
        live = self.c.live
        status = 'Live: %s' % live.name if live and self.c.live_stream else 'Built-in effect on the keyboard'
        if self.c.status != 'connected':
            status = 'Keyboard not connected'
        yield pystray.MenuItem('Open K580 RGB', lambda icon, item: webbrowser.open(self.url), default=True)
        yield pystray.MenuItem(status, None, enabled=False)
        yield pystray.Menu.SEPARATOR
        if mine:
            yield pystray.MenuItem('My presets', pystray.Menu(
                *[pystray.MenuItem(n, self._start(l)) for n, l in sorted(mine.items(), key=lambda kv: kv[0].lower())]))
        yield pystray.MenuItem('Built-in presets', pystray.Menu(
            *[pystray.MenuItem(n, self._start(l)) for n, l in presets.BUILTIN.items()]))
        yield pystray.MenuItem('Stop live effect', lambda icon, item: self.c.set_live(None),
                               enabled=bool(live))
        yield pystray.Menu.SEPARATOR
        yield pystray.MenuItem('Start with Windows', self._toggle_autostart, checked=lambda item: autostart_enabled())
        yield pystray.MenuItem('Quit', self._quit)

    def _toggle_autostart(self, icon, item):
        try:
            set_autostart(not autostart_enabled())
        except OSError as e:
            log.warning('tray: could not change autostart: %s', e)

    def _quit(self, icon, item):
        self._stopped.set()
        icon.visible = False
        icon.stop()                  # makes run() return; the caller then shuts down

    def run(self):
        """Blocks (main thread) until Quit."""
        log.info('tray icon running (K580 RGB %s)', __version__)
        self.icon.run(setup=self._setup)

    def _setup(self, icon):
        icon.visible = True

        def refresh():               # keep presets and status in the menu current
            while not self._stopped.wait(3):
                try:
                    icon.update_menu()
                except Exception:
                    return
        threading.Thread(target=refresh, daemon=True).start()
