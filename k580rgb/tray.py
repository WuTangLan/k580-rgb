"""System-tray icon: open the UI, live presets, built-in lighting, profiles, settings, quit.

The icon can be hidden (Settings in the UI, or the menu); the app then keeps running in the
background and starting it again opens the UI.
"""
import logging
import threading
import webbrowser

import pystray
from PIL import Image, ImageDraw

from . import __version__, autostart, modes, presets
from .device import Params

log = logging.getLogger('k580rgb')
BRIGHTNESS_LABELS = ['Off', '12%', '25%', '50%', '75%', '100%']    # firmware table 00 20 40 80 BE FF


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


class Tray:
    def __init__(self, controller, url, on_quit):
        self.c, self.url, self.on_quit = controller, url, on_quit
        self._stopped = threading.Event()
        self.icon = pystray.Icon('k580rgb', icon_image(), 'K580 RGB', menu=pystray.Menu(self._items))

    # -- actions --------------------------------------------------------------

    def _safe(self, fn, what):
        def run(icon, item):
            try:
                fn()
            except Exception as e:
                log.warning('tray: could not %s: %s', what, e)
        return run

    def _start(self, layers):
        return self._safe(lambda: self.c.set_live(presets.normalize(layers), stream=True), 'start preset')

    def _hardware(self, **change):
        """Switch to a built-in effect / brightness (stops a live effect so it shows)."""
        def apply():
            self.c.set_live(None)
            params = self.c.desired()[0]
            if params:
                self.c.request(Params.from_dict({**params.to_dict(), **change}), now=True)
        return self._safe(apply, 'change lighting')

    def _profile(self, name):
        def apply():
            self.c.set_live(None)
            self.c.apply_profile(name)
        return self._safe(apply, 'apply profile')

    def _toggle_automation(self, icon, item):
        self.c.config['automation'] = not self.c.config['automation']
        self.c.save_config()

    def _toggle_autostart(self, icon, item):
        try:
            autostart.set_enabled(not autostart.enabled())
        except OSError as e:
            log.warning('tray: could not change autostart: %s', e)

    def _hide(self, icon, item):
        self.c.config['tray_icon'] = False
        self.c.save_config()
        self.set_visible(False)

    def _quit(self, icon, item):
        self.on_quit()

    # -- menu -----------------------------------------------------------------

    def _items(self):
        c = self.c
        mine = c.config.get('live_presets', {})
        live = c.live
        params = c.desired()[0] if c.status == 'connected' else None
        if c.status != 'connected':
            status = 'Keyboard not connected'
        elif live and c.live_stream:
            status = 'Live: %s' % live.name
        else:
            status = 'Built-in: %s' % modes.MODES.get(params.mode, ('?',))[0] if params else 'Built-in effect'
        yield pystray.MenuItem('Open K580 RGB', lambda icon, item: webbrowser.open(self.url), default=True)
        yield pystray.MenuItem(status, None, enabled=False)
        yield pystray.Menu.SEPARATOR
        if mine:
            yield pystray.MenuItem('My presets', pystray.Menu(
                *[pystray.MenuItem(n, self._start(l)) for n, l in sorted(mine.items(), key=lambda kv: kv[0].lower())]))
        yield pystray.MenuItem('Live presets', pystray.Menu(
            *[pystray.MenuItem(n, self._start(l)) for n, l in presets.BUILTIN.items()]))
        yield pystray.MenuItem('Stop live effect', lambda icon, item: c.set_live(None), enabled=bool(live))
        yield pystray.Menu.SEPARATOR
        hw_on = params is not None and not (live and c.live_stream)
        yield pystray.MenuItem('Built-in effects', pystray.Menu(
            *[pystray.MenuItem(name, self._hardware(mode=value), radio=True,
                               checked=lambda item, v=value: hw_on and c.desired()[0].mode == v)
              for value, (name, *_rest) in modes.MODES.items()]), enabled=params is not None)
        yield pystray.MenuItem('Brightness', pystray.Menu(
            *[pystray.MenuItem(BRIGHTNESS_LABELS[b], self._hardware(brightness=b), radio=True,
                               checked=lambda item, b=b: c.desired()[0] is not None and c.desired()[0].brightness == b)
              for b in range(modes.BRIGHTNESS_MAX, -1, -1)]), enabled=params is not None)
        profiles = c.config.get('profiles', {})
        if profiles:
            yield pystray.MenuItem('Profiles', pystray.Menu(
                *[pystray.MenuItem(n, self._profile(n)) for n in profiles]))
            yield pystray.MenuItem('Switch profiles by app', self._toggle_automation,
                                   checked=lambda item: c.config['automation'])
        yield pystray.Menu.SEPARATOR
        yield pystray.MenuItem('Start with Windows', self._toggle_autostart, checked=lambda item: autostart.enabled())
        yield pystray.MenuItem('Hide this icon', self._hide)
        yield pystray.MenuItem('Quit', self._quit)

    # -- lifecycle ------------------------------------------------------------

    def set_visible(self, on):
        try:
            self.icon.visible = bool(on)
        except Exception as e:
            log.warning('tray: could not change icon visibility: %s', e)

    def stop(self):
        self._stopped.set()
        try:
            self.icon.visible = False
        except Exception:
            pass
        self.icon.stop()             # makes run() return

    def run(self):
        """Blocks (main thread) until stop()."""
        log.info('tray running (K580 RGB %s, icon %s)', __version__,
                 'shown' if self.c.config.get('tray_icon', True) else 'hidden')
        self.icon.run(setup=self._setup)

    def _setup(self, icon):
        icon.visible = bool(self.c.config.get('tray_icon', True))

        def refresh():               # keep presets and status in the menu current
            while not self._stopped.wait(3):
                try:
                    if icon.visible:
                        icon.update_menu()
                except Exception:
                    return
        threading.Thread(target=refresh, daemon=True).start()
