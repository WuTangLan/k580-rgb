"""Keeps the keyboard, the on-disk config and the live effect engine in sync.

Stock firmware saves every lighting change to flash, so writes are debounced,
diffed against what the keyboard already holds and rate limited.
"""
import json
import logging
import os
import threading
import time

from . import effects
from .device import LED_INDICATOR_A, LED_INDICATOR_B, NUM_LEDS, K580, Params

log = logging.getLogger('k580rgb')
from .layout import BY_INDEX
from .modes import CUSTOM

WRITE_DEBOUNCE = 0.4      # s of quiet before a change is written
WRITE_MIN_INTERVAL = 1.0  # s between flash writes
LIVE_FPS = 30
RECONNECT_INTERVAL = 2.0

CONFIG_DIR = os.path.join(os.environ.get('APPDATA', os.path.expanduser('~')), 'k580rgb')
CONFIG_PATH = os.path.join(CONFIG_DIR, 'config.json')
LOG_PATH = os.path.join(CONFIG_DIR, 'k580rgb.log')

DEFAULT_CONFIG = {
    'profiles': {},
    'rules': [],
    'default_profile': None,
    'automation': False,
    'flash_writes': 0,
    'live_presets': {},          # name -> layer list (your own live-effect presets)
    'last_live': None,           # {'layers': [...]} running on the keyboard at last exit
}


def black():
    return [(0, 0, 0)] * NUM_LEDS


class Controller:
    def __init__(self):
        self.dev = K580()
        self.lock = threading.RLock()
        self.config = self._load()
        self.status = 'connecting'
        self.direct = False
        self.profile = 0                 # active hardware profile slot (0-2)
        self.params = None               # what the keyboard holds
        self.custom = None
        self.side = None                 # side strips: (mode, speed, color index)
        self._pending = None             # (params, custom) waiting to be written
        self._pending_at = 0.0
        self._last_write = 0.0
        self._last_reconnect = 0.0
        self.live = None                 # running effects.Scene
        self.live_specs = None
        self.live_started = 0.0
        self.live_stream = False         # stream to keyboard (direct firmware only)
        self.frame = black()
        self._stop = threading.Event()

    # -- config ------------------------------------------------------------------

    def _load(self):
        try:
            with open(CONFIG_PATH) as f:
                return {**DEFAULT_CONFIG, **json.load(f)}
        except (OSError, ValueError):
            return json.loads(json.dumps(DEFAULT_CONFIG))

    def save_config(self):
        os.makedirs(CONFIG_DIR, exist_ok=True)
        tmp = CONFIG_PATH + '.tmp'
        with open(tmp, 'w') as f:
            json.dump(self.config, f, indent=1)
        os.replace(tmp, CONFIG_PATH)

    # -- lifecycle -------------------------------------------------------------

    def start(self):
        threading.Thread(target=self._device_loop, daemon=True).start()
        threading.Thread(target=self._live_loop, daemon=True).start()

    def stop(self):
        self._stop.set()
        with self.lock:
            if self.live:
                self.live.close()
            if self.direct and self.dev.connected:
                try:
                    self.dev.direct_off()
                except Exception:
                    pass
            self.dev.close()

    def _sync(self):
        """(Re)connect and read the keyboard's current state."""
        if not self.dev.open():
            self.status = 'disconnected'
            return False
        self.profile = self.dev.active_profile()
        self.params = self.dev.read_params(self.profile)
        self.custom = self.dev.read_custom(self.profile)
        self.side = self.dev.read_side(self.profile)
        self.direct = self.dev.supports_direct()
        self.status = 'connected'
        return True

    def _lost(self, err):
        """Any I/O problem: drop the handle; _device_loop reconnects and streaming resumes."""
        with self.lock:
            if self.status == 'connected':
                log.warning('keyboard connection lost: %s', err)
                self._last_reconnect = 0.0    # first retry right away, then every RECONNECT_INTERVAL
            self.dev.close()
            self.status = 'disconnected'

    def _device_loop(self):
        while not self._stop.wait(0.05):
            try:
                with self.lock:
                    now = time.monotonic()
                    if self.status != 'connected':
                        if now - self._last_reconnect >= RECONNECT_INTERVAL:
                            self._last_reconnect = now
                            if self._sync():
                                log.info('keyboard connected (direct mode: %s)', self.direct)
                        continue
                    if (self._pending and now - self._pending_at >= WRITE_DEBOUNCE
                            and now - self._last_write >= WRITE_MIN_INTERVAL):
                        self._flush()
            except Exception as e:      # this thread must never die
                self._lost(e)

    def _flush(self):
        pending, self._pending = self._pending, None
        params, custom, side = pending.get('params'), pending.get('custom'), pending.get('side')
        before = self.dev.flash_writes
        if not self.streaming:
            self._release_direct()        # built-in changes must be visible
        try:
            # colours first so switching to Custom never flashes the old layout
            if custom is not None and custom != self.custom:
                self.dev.write_custom(self.profile, custom, old=self.custom)
                self.custom = custom
            if params is not None and params.to_bytes() != self.params.to_bytes():
                self.dev.write_params(self.profile, params, old=self.params)
                self.params = params
            if side is not None and tuple(side) != tuple(self.side):
                self.dev.write_side(self.profile, side, old=self.side)
                self.side = tuple(side)
        except Exception:
            if self._pending is None:     # retry after reconnect unless superseded
                self._pending = pending
            raise
        written = self.dev.flash_writes - before
        if written:
            self._last_write = time.monotonic()
            self.config['flash_writes'] += written
            self.save_config()

    # -- hardware lighting ---------------------------------------------------------

    def request(self, params=None, custom=None, side=None, now=False):
        """Queue a change; it is written once the user stops fiddling (now=True: skip that wait)."""
        with self.lock:
            pending = dict(self._pending or {})
            for k, v in (('params', params), ('custom', custom), ('side', side)):
                if v is not None:
                    pending[k] = tuple(v) if k == 'side' else v
            self._pending = pending
            self._pending_at = time.monotonic() - (WRITE_DEBOUNCE if now else 0)

    def desired(self):
        """(params, custom, side) the keyboard will show once pending writes are done."""
        p = self._pending or {}
        return p.get('params') or self.params, p.get('custom') or self.custom, p.get('side') or self.side

    def apply_profile(self, name):
        prof = self.config['profiles'].get(name)
        if not prof:
            raise KeyError(name)
        custom = [tuple(c) for c in prof['custom']] if prof.get('custom') else None
        self.request(Params.from_dict(prof['params']), custom, prof.get('side'))

    def bake(self, specs):
        """Write the current frame of a scene into the hardware Custom mode."""
        if self.live and self.live_specs == specs:
            frame = self.frame
        else:
            sc = effects.scene(specs)
            frame = sc.frame(0.0)
            sc.close()
        params = self.desired()[0]
        params = Params.from_dict({**params.to_dict(), 'mode': CUSTOM})
        self.request(params, leds_only(frame))

    # -- live software effects -------------------------------------------------------

    def set_live(self, specs, stream=False):
        """specs: layer list for effects.scene(); empty/None stops."""
        new = effects.scene(specs) if specs else None
        with self.lock:
            old, self.live, self.live_specs = self.live, new, specs or None
            if old:
                old.close()
            if not (new and old):
                self.live_started = time.monotonic()   # keep time running while tweaking
            self.live_stream = bool(stream and new)
            if not self.live_stream:
                self._release_direct()
            # remembered so the next start (e.g. at login) resumes what ran on the keyboard
            remember = {'layers': specs} if self.live_stream else None
            if self.config.get('last_live') != remember:
                self.config['last_live'] = remember
                self.save_config()

    def restore_last_live(self):
        """Resume the live effect that was running on the keyboard when the app last closed."""
        last = self.config.get('last_live')
        if not last:
            return False
        try:
            self.set_live(last['layers'], stream=True)
            log.info('resumed last live effect: %s', self.live.name)
            return True
        except Exception as e:             # e.g. a GIF it used was deleted
            log.warning('could not resume last live effect: %s', e)
            self.config['last_live'] = None
            return False

    def _release_direct(self):
        """Hand the LEDs back to the keyboard's own effects right away (no flash write).
        Sent whenever we're not streaming, even if we think we weren't: it's harmless
        and makes sure no stale direct-mode frame stays on the keyboard."""
        if self.direct and self.status == 'connected':
            try:
                self.dev.direct_off()
            except Exception as e:
                self._lost(e)

    @property
    def streaming(self):
        """Wanted and currently possible; resumes by itself after a reconnect."""
        return self.live_stream and self.live is not None and self.direct and self.status == 'connected'

    def key_pressed(self, index):
        eff = self.live
        if eff:
            eff.key_pressed(index, time.monotonic() - self.live_started)

    def _live_loop(self):
        period = 1.0 / LIVE_FPS
        nxt = time.monotonic()
        while not self._stop.is_set():
            nxt += period
            eff = self.live
            if eff:
                try:
                    frame = eff.frame(time.monotonic() - self.live_started)
                except Exception:               # a buggy effect must not kill the loop
                    log.exception('effect %s failed', eff.name)
                    frame = black()
                self.frame = frame
                if self.streaming:
                    try:
                        with self.lock:
                            if self.streaming and self.live is eff:   # re-check: may have stopped meanwhile
                                self.dev.direct_frame(with_indicators(frame))
                    except Exception as e:
                        self._lost(e)
            delay = nxt - time.monotonic()
            if delay > 0:
                time.sleep(delay)
            else:
                nxt = time.monotonic()

    # -- for the UI -----------------------------------------------------------------

    def snapshot(self):
        with self.lock:
            params, custom, side = self.desired()
            return {
                'status': self.status,
                'direct': self.direct,
                'profile_slot': self.profile,
                'params': params.to_dict() if params else None,
                'custom': [list(c) for c in custom] if custom else None,
                'side': list(side) if side else None,
                'pending': self._pending is not None,
                'flash_writes': self.config['flash_writes'],
                'profiles': self.config['profiles'],
                'rules': self.config['rules'],
                'default_profile': self.config['default_profile'],
                'automation': self.config['automation'],
                'live': self.live.name if self.live else None,
                'live_stream': self.live_stream,       # wanted
                'streaming': self.streaming,           # actually reaching the keyboard
            }


def leds_only(colors):
    """Zero entries without an LED (keeps flash data tidy)."""
    return [tuple(c) if i in BY_INDEX else (0, 0, 0) for i, c in enumerate(colors)]


def with_indicators(frame):
    """Draw the lock indicator LEDs the way stock firmware does (it can't while streaming)."""
    from .automation import lock_states
    num, caps, scroll = lock_states()
    out = list(frame)
    out[LED_INDICATOR_A] = (255 if scroll else 0, 255 if caps else 0, 255 if num else 0)
    out[LED_INDICATOR_B] = (255 if (num or caps or scroll) else 0, 0, 0)
    return out
