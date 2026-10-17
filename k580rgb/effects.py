"""Software lighting effects rendered on the PC.

Every effect maps (key, time) -> RGB for the 107 LEDs. The same frames are used for
the live preview in the UI, for one-off per-key "snapshots" written to the stock
Custom mode, and for streaming once the keyboard runs the direct-mode firmware.
"""
import colorsys
import math
import random

from .layout import BY_INDEX, HEIGHT, SIDE_LEFT, SIDE_RIGHT, WIDTH
from .device import NUM_LEDS

BLACK = (0, 0, 0)


def hsv(h, s=1.0, v=1.0):
    r, g, b = colorsys.hsv_to_rgb(h % 1.0, s, v)
    return int(r * 255), int(g * 255), int(b * 255)


def lerp(c1, c2, t):
    t = min(max(t, 0.0), 1.0)
    return tuple(int(a + (b - a) * t) for a, b in zip(c1, c2))


def column(key, n):
    """Which of n equal columns across the keyboard a key falls in (side strips clamp to the edges)."""
    return min(max(int(key.cx / WIDTH * n), 0), n - 1)


def hex_to_rgb(s):
    s = s.lstrip('#')
    return tuple(int(s[i:i + 2], 16) for i in (0, 2, 4))


class Effect:
    """Base class. `params` come from the UI; `speed` is a multiplier around 1.0."""
    name = ''
    params = {}           # param name -> default: '#rrggbb' colour, float, or list of choices
    reactive = False      # wants key presses
    default_blend = 'Lighten'

    def __init__(self, **kw):
        self.p = {k: kw.get(k, v[0] if isinstance(v, list) else v) for k, v in self.params.items()}
        self.speed = float(kw.get('speed', 1.0))

    def color(self, key, t):
        raise NotImplementedError

    def key_pressed(self, index, t):
        pass

    def close(self):
        """Release capture sources etc. when the effect is replaced."""

    def frame(self, t):
        out = [BLACK] * NUM_LEDS
        for i, key in BY_INDEX.items():
            if not key.iso:
                out[i] = self.color(key, t * self.speed)
        return out


class Solid(Effect):
    name = 'Solid'
    params = {'color': '#00a0ff'}

    def color(self, key, t):
        return hex_to_rgb(self.p['color'])


class Gradient(Effect):
    name = 'Gradient'
    params = {'from': '#ff0080', 'to': '#00e0ff', 'angle': 0.0}

    def color(self, key, t):
        a = math.radians(float(self.p['angle']))
        x, y = key.cx / WIDTH - 0.5, key.cy / HEIGHT - 0.5
        return lerp(hex_to_rgb(self.p['from']), hex_to_rgb(self.p['to']),
                    0.5 + x * math.cos(a) + y * math.sin(a))


class RainbowWave(Effect):
    name = 'Rainbow Wave'
    params = {'width': 1.0, 'angle': 0.0}

    def color(self, key, t):
        a = math.radians(float(self.p['angle']))
        pos = (key.cx * math.cos(a) + key.cy * math.sin(a)) / WIDTH
        return hsv(pos / max(float(self.p['width']), 0.05) - t * 0.25)


class ColorShift(Effect):
    name = 'Color Shift'
    params = {}

    def color(self, key, t):
        return hsv(t * 0.1)


class Breathing(Effect):
    name = 'Breathing'
    params = {'color': '#ff3000'}

    def color(self, key, t):
        v = (math.sin(t * 2.0) + 1) / 2
        return lerp(BLACK, hex_to_rgb(self.p['color']), 0.08 + 0.92 * v * v)


class Spiral(Effect):
    name = 'Spiral'
    params = {}

    def color(self, key, t):
        ang = math.atan2(key.cy - HEIGHT / 2, (key.cx - WIDTH / 2) / 2)
        return hsv(ang / (2 * math.pi) + t * 0.2)


class Plasma(Effect):
    name = 'Plasma'
    params = {}

    def color(self, key, t):
        x, y = key.cx / 3, key.cy / 1.5
        v = math.sin(x + t) + math.sin(y + t * 0.7) + math.sin((x + y + t) * 0.6)
        v += math.sin(math.hypot(x - 3.5, y - 2) - t)
        return hsv(v / 8 + t * 0.03)


class Rain(Effect):
    name = 'Rain'
    params = {'color': '#40ff80'}

    def __init__(self, **kw):
        super().__init__(**kw)
        rnd = random.Random(7)
        self.cols = {round(k.cx * 2): (rnd.random() * 10, 0.5 + rnd.random()) for k in BY_INDEX.values()}

    def color(self, key, t):
        phase, rate = self.cols[round(key.cx * 2)]
        head = (t * rate * 3 + phase) % 9 - 1.5
        d = head - key.cy
        if 0 <= d < 3:
            return lerp(BLACK, hex_to_rgb(self.p['color']), 1 - d / 3)
        return BLACK


class Starfield(Effect):
    name = 'Starfield'
    params = {'color': '#ffffff', 'background': '#000010'}

    def __init__(self, **kw):
        super().__init__(**kw)
        rnd = random.Random(3)
        self.seed = {i: (rnd.random() * 6, 1.5 + rnd.random() * 3) for i in BY_INDEX}

    def color(self, key, t):
        phase, period = self.seed[key.index]
        v = max(0.0, math.sin((t + phase) * 2 * math.pi / period)) ** 6
        return lerp(hex_to_rgb(self.p['background']), hex_to_rgb(self.p['color']), v)


class Ripple(Effect):
    """Rings spreading from pressed keys (needs key events from the PC)."""
    name = 'Ripple'
    params = {'color': '#00ffd0', 'background': '#100020'}
    reactive = True

    def __init__(self, **kw):
        super().__init__(**kw)
        self.waves = []

    def key_pressed(self, index, t):
        k = BY_INDEX.get(index)
        if k:
            self.waves.append((k.cx, k.cy, t * self.speed))
            self.waves = self.waves[-12:]

    def color(self, key, t):
        c = hex_to_rgb(self.p['background'])
        for x, y, t0 in self.waves:
            r = (t - t0) * 8
            d = abs(math.hypot(key.cx - x, (key.cy - y) * 1.2) - r)
            if d < 1.2 and r < 25:
                c = lerp(c, hex_to_rgb(self.p['color']), (1 - d / 1.2) * (1 - r / 25))
        return c


class KeyFade(Effect):
    """Pressed keys light up and fade out."""
    name = 'Key Fade'
    params = {'style': ['Rainbow', 'Color'], 'color': '#ff2d55', 'background': '#000000', 'fade': 1.5}
    reactive = True

    def __init__(self, **kw):
        super().__init__(**kw)
        self.pressed = {}     # LED index -> (time, colour)

    def key_pressed(self, index, t):
        k = BY_INDEX.get(index)
        if not k:
            return
        t *= self.speed
        if self.p['style'] == 'Rainbow':
            c = hsv(k.cx / WIDTH * 0.6 + t * 0.15)   # varies across the board and over time
        else:
            c = hex_to_rgb(self.p['color'])
        self.pressed[index] = (t, c)

    def color(self, key, t):
        bg = hex_to_rgb(self.p['background'])
        hit = self.pressed.get(key.index)
        if not hit:
            return bg
        age = (t - hit[0]) / max(float(self.p['fade']), 0.1)
        if age >= 1:
            del self.pressed[key.index]
            return bg
        return lerp(bg, hit[1], (1 - age) ** 2)


def clamp(v, lo=0.0, hi=1.0):
    return lo if v < lo else hi if v > hi else v


def ramp(stops, v):
    """Piecewise-linear colour ramp; stops = [(pos, rgb), ...] sorted by pos."""
    v = clamp(v)
    for (p0, c0), (p1, c1) in zip(stops, stops[1:]):
        if v <= p1:
            return lerp(c0, c1, (v - p0) / (p1 - p0))
    return stops[-1][1]


FIRE = [(0, (0, 0, 0)), (0.35, (170, 0, 0)), (0.7, (255, 80, 0)), (1, (255, 210, 60))]
HEAT = [(0, (0, 0, 40)), (0.25, (0, 90, 255)), (0.5, (0, 220, 120)), (0.75, (255, 220, 0)), (1, (255, 30, 0))]


class Fire(Effect):
    name = 'Fire'
    params = {'height': 1.0}

    def color(self, key, t):
        x, up = key.cx, (HEIGHT - key.cy) / HEIGHT
        n = 0.45 + 0.25 * math.sin(x * 1.3 + t * 5.1) + 0.15 * math.sin(x * 2.7 - t * 7.3) \
            + 0.1 * math.sin(x * 0.6 + t * 2.9)
        heat = clamp(1 - up / max(n * float(self.p['height']) + 0.15, 0.05))
        return ramp(FIRE, heat * (0.85 + 0.15 * math.sin(t * 23 + x * 5)))


class Aurora(Effect):
    name = 'Aurora'
    params = {}

    def color(self, key, t):
        x, y = key.cx, key.cy
        v = (math.sin(x * 0.35 + t * 0.8 + math.sin(y * 0.9 + t * 0.5) * 1.5) + 1) / 2
        hue = 0.42 + 0.2 * math.sin(x * 0.15 - t * 0.2) + 0.08 * math.sin(y * 0.7 + t * 0.3)
        return hsv(hue, 0.9, 0.15 + 0.85 * v * v)


class Heatmap(Effect):
    """Keys glow hotter the more you use them; heat cools down over time."""
    name = 'Typing Heatmap'
    params = {'cooldown': 2.0}
    reactive = True

    def __init__(self, **kw):
        super().__init__(**kw)
        self.heat = {}        # index -> (heat, time)

    def _value(self, index, t):
        h, t0 = self.heat.get(index, (0.0, t))
        return h * 0.5 ** ((t - t0) / (float(self.p['cooldown']) * 10))   # half-life

    def key_pressed(self, index, t):
        t *= self.speed
        self.heat[index] = (self._value(index, t) + 1.0, t)

    def color(self, key, t):
        if key.index not in self.heat:
            return HEAT[0][1]
        return ramp(HEAT, self._value(key.index, t) / 8)


class AudioEffect(Effect):
    source = 'audio'

    def __init__(self, **kw):
        super().__init__(**kw)
        from .sources import AUDIO
        self.audio = AUDIO
        AUDIO.acquire()

    def close(self):
        self.audio.release()


class AudioSpectrum(AudioEffect):
    """Equaliser bars: one band per key column, rising from the bottom row."""
    name = 'Audio Spectrum'
    params = {'style': ['Rainbow', 'Gradient'], 'from': '#00ff60', 'to': '#ff0030'}

    def color(self, key, t):
        from .sources import AUDIO_BANDS
        level = self.audio.bands[column(key, AUDIO_BANDS)]
        up = (HEIGHT - key.cy) / HEIGHT
        lit = clamp((level - up) * 6 + 1)
        if not lit:
            return BLACK
        if self.p['style'] == 'Rainbow':
            c = hsv(key.cx / WIDTH * 0.8 + t * 0.02)
        else:
            c = lerp(hex_to_rgb(self.p['from']), hex_to_rgb(self.p['to']), up)
        return lerp(BLACK, c, lit)


class AudioPulse(AudioEffect):
    """Whole keyboard pulses with the bass; colour drifts slowly."""
    name = 'Audio Pulse'
    params = {'style': ['Rainbow', 'Color'], 'color': '#ff0050'}

    def color(self, key, t):
        c = hsv(t * 0.03 + key.cx / WIDTH * 0.15) if self.p['style'] == 'Rainbow' else hex_to_rgb(self.p['color'])
        return lerp(BLACK, c, 0.05 + 0.95 * self.audio.bass ** 1.5)


class ScreenAmbience(Effect):
    """Each key takes the colour of the matching area of your screen."""
    name = 'Screen Ambience'
    params = {'saturation': 1.5}
    source = 'screen'

    def __init__(self, **kw):
        super().__init__(**kw)
        from .sources import SCREEN
        self.screen = SCREEN
        SCREEN.acquire()

    def close(self):
        self.screen.release()

    def color(self, key, t):
        grid = self.screen.grid
        rows, cols = len(grid), len(grid[0])
        r, g, b = grid[min(int(key.cy / HEIGHT * rows), rows - 1)][column(key, cols)]
        h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        return hsv(h, clamp(s * float(self.p['saturation'])), v)


# -- more music modes ------------------------------------------------------------------

class AudioCenter(AudioEffect):
    """Spectrum mirrored from the middle: bass in the centre, treble at both edges."""
    name = 'Audio Center Bars'
    params = {'style': ['Rainbow', 'Gradient'], 'from': '#ff00a0', 'to': '#00e0ff'}

    def color(self, key, t):
        from .sources import AUDIO_BANDS
        dist = abs(key.cx - WIDTH / 2) / (WIDTH / 2)
        level = self.audio.bands[min(int(dist * AUDIO_BANDS), AUDIO_BANDS - 1)]
        up = (HEIGHT - key.cy) / HEIGHT
        lit = clamp((level - up) * 6 + 1)
        if not lit:
            return BLACK
        c = hsv(dist * 0.7 + t * 0.03) if self.p['style'] == 'Rainbow' else \
            lerp(hex_to_rgb(self.p['from']), hex_to_rgb(self.p['to']), dist)
        return lerp(BLACK, c, lit)


VU = [(0, (0, 255, 60)), (0.6, (255, 220, 0)), (1, (255, 0, 0))]


class AudioVU(AudioEffect):
    """Loudness meter filling the keyboard from the bottom, with a falling peak line."""
    name = 'Audio VU Meter'
    params = {}

    def __init__(self, **kw):
        super().__init__(**kw)
        self.peak, self.peak_t, self.level, self.last = 0.0, 0.0, 0.0, None

    def frame(self, t):
        if self.last != t:                 # update once per frame, not per key
            self.last = t
            self.level = clamp(self.audio.volume * 2.2)
            if self.level >= self.peak:
                self.peak, self.peak_t = self.level, t
            else:
                self.peak = max(self.level, self.peak - max(0.0, t - self.peak_t - 0.4) * 0.05)
        return super().frame(t)

    def color(self, key, t):
        up = (HEIGHT - key.cy) / HEIGHT
        if up <= self.level:
            return ramp(VU, up)
        if self.peak > 0.12 and abs(up - self.peak) < 0.09:     # no peak line in silence
            return (255, 255, 255)
        return BLACK


class AudioWaterfall(AudioEffect):
    """Spectrum history: newest on the bottom row, scrolling up."""
    name = 'Audio Waterfall'
    params = {}
    ROWS = 6

    def __init__(self, **kw):
        super().__init__(**kw)
        self.history = [[0.0] * 22 for _ in range(self.ROWS)]
        self.next_push = 0.0

    def frame(self, t):
        if t >= self.next_push or t < self.next_push - 1:
            self.next_push = t + 0.08
            self.history = self.history[1:] + [list(self.audio.bands)]
        return super().frame(t)

    def color(self, key, t):
        from .sources import AUDIO_BANDS
        age = min(int((HEIGHT - key.cy) / HEIGHT * self.ROWS), self.ROWS - 1)   # 0 = bottom = newest
        band = column(key, AUDIO_BANDS)
        return ramp(HEAT, self.history[-1 - age][band] * 1.1)


class AudioBeatRipple(AudioEffect):
    """A coloured ring bursts from the centre on every beat."""
    name = 'Audio Beat Ripple'
    params = {'background': '#05000a'}

    def __init__(self, **kw):
        super().__init__(**kw)
        self.rings = []          # (start time, hue)
        self.seen = self.audio.beats

    def frame(self, t):
        if self.audio.beats != self.seen:
            self.seen = self.audio.beats
            self.rings = (self.rings + [(t, (t * 0.07) % 1)])[-6:]
        return super().frame(t)

    def color(self, key, t):
        c = hex_to_rgb(self.p['background'])
        d = math.hypot(key.cx - WIDTH / 2, (key.cy - HEIGHT / 2) * 2)
        for t0, hue in self.rings:
            r = (t - t0) * 22
            w = abs(d - r)
            if w < 2.5 and r < 26:
                c = lerp(c, hsv(hue), (1 - w / 2.5) * (1 - r / 26))
        return c


class AudioColorFlow(AudioEffect):
    """Colour follows the pitch of the music (bassy = red, bright = blue), brightness the volume."""
    name = 'Audio Color Flow'
    params = {}

    def frame(self, t):
        bands = self.audio.bands
        total = sum(bands) or 1e-6
        self.centroid = sum(i * b for i, b in enumerate(bands)) / total / len(bands)
        return super().frame(t)

    def color(self, key, t):
        v = clamp(0.06 + self.audio.volume * 2.5)
        wave = 0.05 * math.sin(key.cx * 0.4 - t * 2)
        return hsv(self.centroid * 0.75 + wave, 1, v)


# -- black hole -------------------------------------------------------------------------

BH_THEMES = {
    'Interstellar': [(0, (255, 255, 230)), (0.3, (255, 190, 80)), (0.65, (230, 70, 0)), (1, (60, 0, 0))],
    'Purple': [(0, (255, 230, 255)), (0.3, (220, 90, 255)), (0.65, (110, 0, 200)), (1, (20, 0, 50))],
    'Ice': [(0, (240, 255, 255)), (0.3, (80, 220, 255)), (0.65, (0, 90, 220)), (1, (0, 10, 50))],
}


class BlackHole(Effect):
    """Event horizon with a swirling accretion disk; stars get pulled in."""
    name = 'Black Hole'
    params = {'theme': ['Interstellar', 'Purple', 'Ice', 'Rainbow'], 'size': 1.0, 'follow keys': ['Off', 'On']}

    def __init__(self, **kw):
        super().__init__(**kw)
        self.cx, self.cy = WIDTH / 2, HEIGHT / 2 + 0.3
        self.tx, self.ty = self.cx, self.cy
        self.reactive = self.p['follow keys'] == 'On'
        self.last = None
        rnd = random.Random(11)
        self.stars = {i: rnd.random() for i in BY_INDEX}

    def key_pressed(self, index, t):
        k = BY_INDEX.get(index)
        if k:
            self.tx, self.ty = k.cx, k.cy

    def frame(self, t):
        if self.last is not None:                     # glide towards the target key
            a = min(1.0, (t - self.last) * 2.5)
            self.cx += (self.tx - self.cx) * a
            self.cy += (self.ty - self.cy) * a
        self.last = t
        return super().frame(t)

    def color(self, key, t):
        s = float(self.p['size'])
        dx, dy = key.cx - self.cx, (key.cy - self.cy) * 2.2     # keys are wider than tall
        r = math.hypot(dx, dy) / s
        horizon = 1.6
        if r < horizon:
            return BLACK
        ang = math.atan2(dy, dx)
        swirl = (math.sin(ang * 2 - r * 1.3 + t * 3.0) + 1) / 2          # spiral arms
        ring = math.exp(-((r - horizon - 0.9) / 1.3) ** 2)                 # bright inner disk
        disk = math.exp(-((r - horizon) / 6.5) ** 2) * (0.35 + 0.65 * swirl)
        v = clamp(ring * 0.9 + disk)
        theme = self.p['theme']
        if theme == 'Rainbow':
            c = hsv(ang / (2 * math.pi) + r * 0.05 - t * 0.15, 1, v)
        else:
            c = lerp(BLACK, ramp(BH_THEMES[theme], 1 - ring * 0.8 - swirl * 0.2), v)
        # infalling stars: twinkles that drift inward
        ph = (self.stars[key.index] + t * 0.25 + 3 / max(r, 0.1)) % 1
        if ph < 0.05 and r > horizon + 2:
            c = lerp(c, (255, 255, 255), 1 - ph / 0.05)
        return c


# -- games --------------------------------------------------------------------------------

# 3x5 digits drawn on the numpad: rows Num / * | 7 8 9 | 4 5 6 | 1 2 3 | 0 0 .
NUMPAD = [[38, 39, 40], [59, 60, 61], [80, 81, 82], [101, 102, 103], [123, 123, 124]]
NUMPAD_BAR = [104, 62, 41]          # Enter, +, - : RPM bar bottom to top
F_ROW = list(range(1, 13))          # F1..F12 shift lights
GLYPHS = {
    'R': ['##.', '#.#', '##.', '#.#', '#.#'], 'N': ['##.', '#.#', '#.#', '#.#', '#.#'],
    1: ['.#.', '##.', '.#.', '.#.', '###'], 2: ['###', '..#', '###', '#..', '###'],
    3: ['###', '..#', '###', '..#', '###'], 4: ['#.#', '#.#', '###', '..#', '..#'],
    5: ['###', '#..', '###', '..#', '###'], 6: ['###', '#..', '###', '#.#', '###'],
    7: ['###', '..#', '..#', '..#', '..#'], 8: ['###', '#.#', '###', '#.#', '###'],
    9: ['###', '#.#', '###', '..#', '###'],
}
RPM_RAMP = [(0, (0, 255, 0)), (0.75, (0, 255, 0)), (0.87, (255, 200, 0)), (0.95, (255, 0, 0)), (1, (255, 0, 0))]


class AssettoCorsaGear(Effect):
    """Gear on the numpad, RPM bar and shift lights from Assetto Corsa / ACC telemetry.
    Transparent when you're not driving, so layers below show through."""
    name = 'Assetto Corsa: Gear & RPM'
    default_blend = 'Normal'
    params = {'shift lights': ['F-row + numpad', 'Numpad only'], 'side strips': ['RPM bar', 'Off'],
              'redline': 0.95, 'digit color': ['RPM', 'Color'], 'color': '#ffffff'}
    source = 'game'

    def __init__(self, **kw):
        super().__init__(**kw)
        from .games import ASSETTO
        self.game = ASSETTO
        self.state = None

    def frame(self, t):
        out = [None] * NUM_LEDS                       # transparent except what we own
        s = self.game.read()
        self.state = s
        if not s:
            return out
        owned = [i for row in NUMPAD for i in row] + NUMPAD_BAR
        if self.p['shift lights'] == 'F-row + numpad':
            owned += F_ROW
        if self.p['side strips'] == 'RPM bar':
            owned += SIDE_LEFT + SIDE_RIGHT
        for i in owned:
            out[i] = BLACK
        frac = clamp(s['rpm'] / s['max_rpm'])
        redline = clamp(float(self.p['redline']), 0.5, 1.0)
        shift = frac >= redline
        blink = shift and int(t * 10) % 2 == 0
        rpm_col = (0, 80, 255) if blink else ramp(RPM_RAMP, frac / max(redline, 0.01) * 0.95)
        digit_col = rpm_col if self.p['digit color'] == 'RPM' else hex_to_rgb(self.p['color'])
        glyph = GLYPHS.get(s['gear'] if s['gear'] in GLYPHS else min(int(s['gear']), 9), GLYPHS['N'])
        for r, row in enumerate(glyph):
            for c, ch in enumerate(row):
                if ch == '#':
                    out[NUMPAD[r][c]] = digit_col
        for i, led in enumerate(NUMPAD_BAR):          # 3-step vertical RPM bar
            if frac >= (i + 1) / 3.6:
                out[led] = rpm_col
        if self.p['side strips'] == 'RPM bar':          # both strips fill front to back
            n = int(clamp((frac - 0.3) / max(redline - 0.3, 0.01)) * 7 + 0.5)
            for strip in (SIDE_LEFT, SIDE_RIGHT):
                for i, led in enumerate(strip):
                    if blink:
                        out[led] = (0, 80, 255)
                    elif i < n:
                        out[led] = ramp(RPM_RAMP, 0.6 + (i + 1) / 7 * 0.35)
        if self.p['shift lights'] == 'F-row + numpad':
            lit = int(clamp((frac - 0.5) / max(redline - 0.5, 0.01)) * len(F_ROW) + 0.5)
            for i, led in enumerate(F_ROW):
                if blink:
                    out[led] = (0, 80, 255)
                elif i < lit:
                    out[led] = ramp(RPM_RAMP, 0.5 + (i + 1) / len(F_ROW) * 0.5 * redline / 0.95)
        return out


GAMING_KEYS = {
    'WASD': ['W', 'A', 'S', 'D'],
    'WASD + abilities': ['W', 'A', 'S', 'D', 'Q', 'E', 'R', 'F', '1', '2', '3', '4', '5', 'Shift', 'Ctrl', ''],
    'Arrows': ['↑', '←', '↓', '→'],
}


class GamingKeys(Effect):
    """Highlights the keys you play with; everything else is left to the layers below."""
    name = 'Gaming Keys'
    default_blend = 'Normal'
    params = {'keys': list(GAMING_KEYS), 'color': '#ff2030'}

    def __init__(self, **kw):
        super().__init__(**kw)
        labels = set(GAMING_KEYS[self.p['keys']])
        self.keys = {k.index for k in BY_INDEX.values()
                     if not k.iso and k.side is None and k.label in labels
                     and k.x < 18                                     # not the numpad
                     and (k.label not in ('Shift', 'Ctrl') or k.x < 1)}   # left Shift/Ctrl only

    def color(self, key, t):
        return hex_to_rgb(self.p['color']) if key.index in self.keys else None


class DamageFlash(Effect):
    """Flashes when your screen's edges turn red - how many games show you're taking damage."""
    name = 'Damage Flash (screen)'
    params = {'color': '#ff0000', 'sensitivity': 1.0}
    source = 'screen'

    def __init__(self, **kw):
        super().__init__(**kw)
        from .sources import SCREEN
        self.screen = SCREEN
        SCREEN.acquire()
        self.base, self.flash, self.last = None, 0.0, None

    def close(self):
        self.screen.release()

    def _redness(self):
        g = self.screen.grid
        edge = g[0] + g[-1] + [row[0] for row in g] + [row[-1] for row in g]
        return sum(max(0.0, r - (gg + b) / 2) for r, gg, b in edge) / (len(edge) * 255)

    def frame(self, t):
        red = self._redness()
        self.base = red if self.base is None else self.base * 0.97 + red * 0.03
        dt = 0 if self.last is None else t - self.last
        self.last = t
        if red - self.base > 0.12 / max(float(self.p['sensitivity']), 0.1):
            self.flash = 1.0
        else:
            self.flash = max(0.0, self.flash - dt * 1.8)
        if not self.flash:
            return [None] * NUM_LEDS
        c = lerp(BLACK, hex_to_rgb(self.p['color']), self.flash)
        return [c if i in BY_INDEX and not BY_INDEX[i].iso else None for i in range(NUM_LEDS)]


MEDIA = '@media'      # param marker: pick a file from the media library


class GifPlayer(Effect):
    """Plays a GIF (or shows an image) across the keys at its own frame timing."""
    name = 'GIF / Image'
    params = {'file': MEDIA, 'fit': ['Cover', 'Contain', 'Stretch'], 'brightness': 1.0}
    source = 'media'

    def __init__(self, **kw):
        super().__init__(**kw)
        from . import media
        name = self.p['file'] if self.p['file'] != MEDIA else (media.library() or [{'name': None}])[0]['name']
        self.clip = media.load(name, self.p['fit']) if name else None

    def frame(self, t):
        if not self.clip:
            return [BLACK] * NUM_LEDS
        f = self.clip.at(t * self.speed)
        b = float(self.p['brightness'])
        return f if b == 1.0 else [tuple(min(255, int(v * b)) for v in c) for c in f]


class ScrollingText(Effect):
    """Your text scrolling across the keyboard."""
    name = 'Scrolling Text'
    params = {'text': 'HELLO K580', 'size': ['Full height', 'Large', 'Medium'], 'style': ['Rainbow', 'Color'],
              'color': '#ffffff', 'background': '#000000'}
    source = 'media'
    SIZES = {'Full height': 0.97, 'Large': 0.75, 'Medium': 0.55}

    def __init__(self, **kw):
        super().__init__(**kw)
        from .media import TextStrip
        self.strip = TextStrip(str(self.p['text'])[:200], self.SIZES.get(self.p['size'], 0.97))

    def frame(self, t):
        t *= self.speed
        cover = self.strip.coverage(t * 40)                 # 40 px/s = 5 key widths per second
        bg = hex_to_rgb(self.p['background'])
        fixed = hex_to_rgb(self.p['color'])
        out = [BLACK] * NUM_LEDS
        for i, key in BY_INDEX.items():
            if key.iso:
                continue
            fg = hsv(key.cx / WIDTH * 0.5 + t * 0.1) if self.p['style'] == 'Rainbow' else fixed
            out[i] = lerp(bg, fg, min(1.0, cover[i] * 1.6))
        return out


EFFECTS = {cls.name: cls for cls in
           [RainbowWave, Aurora, Fire, BlackHole, Plasma, Spiral, ColorShift, Breathing, Rain, Starfield, Gradient,
            Solid, GifPlayer, ScrollingText,
            AudioSpectrum, AudioCenter, AudioVU, AudioWaterfall, AudioBeatRipple, AudioColorFlow, AudioPulse,
            ScreenAmbience, KeyFade, Ripple, Heatmap,
            AssettoCorsaGear, GamingKeys, DamageFlash]}

BLENDS = ['Lighten', 'Add', 'Normal']


ZONES = ['All', 'Keys only', 'Side strips only']
_ZONE_MASK = {
    'Keys only': {i for i, k in BY_INDEX.items() if k.side is None},
    'Side strips only': {i for i, k in BY_INDEX.items() if k.side},
}


class _Zoned:
    """Wraps an effect so it only paints its zone; elsewhere it is transparent."""

    def __init__(self, effect, zone):
        self.effect, self.mask = effect, _ZONE_MASK[zone]

    def __getattr__(self, name):
        return getattr(self.effect, name)

    def frame(self, t):
        return [c if i in self.mask else None for i, c in enumerate(self.effect.frame(t))]


class Scene:
    """A stack of effects. Each layer: (effect, blend, opacity 0..1)."""

    def __init__(self, layers):
        self.layers = layers
        self.name = ' + '.join(e.name for e, _, _ in layers)
        self.reactive = any(e.reactive for e, _, _ in layers)

    def key_pressed(self, index, t):
        for e, _, _ in self.layers:
            if e.reactive:
                e.key_pressed(index, t)

    def close(self):
        for e, _, _ in self.layers:
            e.close()

    def frame(self, t):
        """Effects may return None for a key = transparent (layers below show through)."""
        out = [BLACK] * NUM_LEDS
        for n, (e, blend, op) in enumerate(self.layers):
            f = e.frame(t)
            if n == 0 or blend == 'Normal':
                out = [o if c is None else lerp(o, c, op) for o, c in zip(out, f)]
            elif blend == 'Add':
                out = [o if c is None else tuple(min(255, a + int(b * op)) for a, b in zip(o, c))
                       for o, c in zip(out, f)]
            else:
                out = [o if c is None else tuple(max(a, int(b * op)) for a, b in zip(o, c))
                       for o, c in zip(out, f)]
        return out


def create(name, **kw):
    return EFFECTS[name](**kw)


def scene(specs):
    """specs: [{'effect', 'args', 'speed', 'blend', 'opacity'}, ...] bottom layer first."""
    layers = []
    try:
        for s in specs:
            eff = create(s['effect'], speed=float(s.get('speed', 1.0)), **s.get('args', {}))
            if s.get('zone', 'All') in _ZONE_MASK:
                eff = _Zoned(eff, s['zone'])
            layers.append((eff, s.get('blend', 'Lighten'), clamp(float(s.get('opacity', 1.0)))))
    except Exception:
        for e, _, _ in layers:
            e.close()
        raise
    return Scene(layers)


def as_list():
    return [{'name': n, 'params': cls.params, 'animated': cls not in (Solid, Gradient),
             'reactive': cls.reactive, 'source': getattr(cls, 'source', None), 'blend': cls.default_blend}
            for n, cls in EFFECTS.items()]
