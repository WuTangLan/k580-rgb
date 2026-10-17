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


# -- palettes (perceptual blending in OKLab) ------------------------------------------------

def _to_linear(c):
    c /= 255
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def _to_srgb(c):
    c = min(max(c, 0.0), 1.0)
    return round(255 * (12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055))


def rgb_to_oklab(rgb):
    r, g, b = (_to_linear(v) for v in rgb)
    l = (0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b) ** (1 / 3)
    m = (0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b) ** (1 / 3)
    s = (0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b) ** (1 / 3)
    return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s)


def _oklab_to_linear(L, a, b):
    l = (L + 0.3963377774 * a + 0.2158037573 * b) ** 3
    m = (L - 0.1055613458 * a - 0.0638541728 * b) ** 3
    s = (L - 0.0894841775 * a - 1.2914855480 * b) ** 3
    return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s)


def oklab_to_rgb(lab):
    """OKLab -> sRGB. Out-of-range colours lose chroma (keeping hue and lightness)
    instead of being clipped per channel, which would shift the hue."""
    L, a, b = lab
    for _ in range(30):
        lin = _oklab_to_linear(L, a, b)
        if all(-0.0005 <= v <= 1.0005 for v in lin):
            break
        a, b = a * 0.92, b * 0.92
    else:
        lin = _oklab_to_linear(L, 0.0, 0.0)
    return tuple(_to_srgb(v) for v in lin)


PALETTE = 'palette'   # param marker: {'palette': [...]} = editable list of 2-8 colours


def oklch_mix(c1, c2, f):
    """Blend two RGB colours in OKLCH: lightness and chroma linearly, hue the short way
    round. Unlike RGB or OKLab, opposite hues don't wash out to grey halfway."""
    (L1, a1, b1), (L2, a2, b2) = rgb_to_oklab(c1), rgb_to_oklab(c2)
    C1, C2 = math.hypot(a1, b1), math.hypot(a2, b2)
    h1, h2 = math.atan2(b1, a1), math.atan2(b2, a2)
    if C1 < 0.02:                     # grey/black/white has no hue: borrow the other one's
        h1 = h2
    if C2 < 0.02:
        h2 = h1
    dh = (h2 - h1 + math.pi) % (2 * math.pi) - math.pi
    L, C, h = L1 + (L2 - L1) * f, C1 + (C2 - C1) * f, h1 + dh * f
    return oklab_to_rgb((L, C * math.cos(h), C * math.sin(h)))


class Palette:
    """Colour stops blended perceptually (OKLCH), precomputed into a 256-entry lookup."""
    SIZE = 256

    def __init__(self, hex_colors, cyclic=False):
        stops = [hex_to_rgb(c) for c in hex_colors[:8]] or [(0, 0, 0)]
        if cyclic:
            stops = stops + stops[:1]               # wrap back to the first colour
        n = len(stops) - 1
        self.lut = []
        for i in range(self.SIZE):
            pos = i / (self.SIZE - 1) * n if n else 0
            j = min(int(pos), n - 1) if n else 0
            self.lut.append(oklch_mix(stops[j], stops[min(j + 1, n)], pos - j))

    def at(self, v):
        return self.lut[int(min(max(v, 0.0), 1.0) * (self.SIZE - 1))]


def palette_param(value, default):
    """Accept a list of hex colours (from the UI) or the {'palette': [...]} default."""
    if isinstance(value, dict):
        value = value.get('palette')
    if isinstance(value, (list, tuple)) and len(value) >= 2:
        return [str(c) for c in value[:8]]
    return default['palette']


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
    """Any number of colours across the keyboard; optionally scrolling as a seamless loop."""
    name = 'Gradient'
    params = {'colors': {PALETTE: ['#ff0080', '#7b2cff', '#00e0ff']}, 'angle': 0.0,
              'motion': ['Static', 'Scroll']}

    def __init__(self, **kw):
        super().__init__(**kw)
        self.scroll = self.p['motion'] == 'Scroll'
        self.pal = Palette(palette_param(self.p['colors'], self.params['colors']), cyclic=self.scroll)

    def color(self, key, t):
        a = math.radians(float(self.p['angle']))
        x, y = key.cx / WIDTH - 0.5, key.cy / HEIGHT - 0.5
        v = 0.5 + x * math.cos(a) + y * math.sin(a)
        if self.scroll:
            return self.pal.at((v * 0.8 - t * 0.08) % 1.0)
        return self.pal.at(v)


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


class VincentSunset(Effect):
    """Retro-futuristic synthwave: pink, cyan and purple gradients drifting slowly across the keys."""
    name = 'Vincent Sunset'
    params = {'colors': {PALETTE: ['#7b2cff', '#ff2e88', '#14e1ff']}}

    def __init__(self, **kw):
        super().__init__(**kw)
        colors = palette_param(self.p['colors'], self.params['colors'])
        self.pal = Palette(colors, cyclic=True)              # loops seamlessly
        self.horizon = hex_to_rgb(colors[1 if len(colors) > 1 else 0])

    def color(self, key, t):
        x, y = key.cx / WIDTH, key.cy / HEIGHT
        tau = 2 * math.pi
        # two slow drifting waves, one bending the other, plus a lower "horizon" bias
        v = 0.5 * math.sin(tau * (x * 0.7 - t * 0.035) + 1.4 * math.sin(tau * (y * 0.5 + t * 0.02)))
        v += 0.25 * math.sin(tau * (x * 0.25 + y * 0.6 + t * 0.025))
        v = (v * 0.5 + 0.5 + y * 0.25 + t * 0.01) % 1.0
        c = self.pal.at(v)
        glow = 0.78 + 0.22 * math.sin(tau * (x * 0.4 + t * 0.05))             # gentle breathing
        warm = lerp(c, self.horizon, clamp((y - 0.7) * 1.2) * 0.35)            # warmer bottom rows
        return tuple(int(ch * glow) for ch in warm)


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


# Which of the 22 analysis bands (40 Hz - 16 kHz, log spaced) each "react to" choice uses
FOCUS_BANDS = {'All': (0, 22), 'Low (bass)': (0, 7), 'Mid': (7, 17), 'High (treble)': (17, 22)}
FOCUS_BOOST = {'All': 1.0, 'Low (bass)': 1.0, 'Mid': 1.2, 'High (treble)': 1.6}   # treble is quieter


def audio_controls(focus='All'):
    """The 'react to' + 'sensitivity' controls every music effect has."""
    return {'react to': [focus] + [f for f in FOCUS_BANDS if f != focus], 'sensitivity': 1.0}


class AudioEffect(Effect):
    """Base for music effects. Each frame, `self.fb` holds the chosen frequency range's
    band levels (0..1, sensitivity applied) and `self.flevel` their average loudness.
    Subclasses put per-frame logic in update(t)."""
    source = 'audio'

    def __init__(self, **kw):
        super().__init__(**kw)
        from .sources import AUDIO
        self.audio = AUDIO
        AUDIO.acquire()
        focus = self.p.get('react to', 'All')
        self.focus = FOCUS_BANDS.get(focus, FOCUS_BANDS['All'])
        self.gain = float(self.p.get('sensitivity', 1.0)) * FOCUS_BOOST.get(focus, 1.0)
        self.fb, self.flevel = [0.0] * (self.focus[1] - self.focus[0]), 0.0

    def close(self):
        self.audio.release()

    def frame(self, t):
        lo, hi = self.focus
        self.fb = [min(1.0, b * self.gain) for b in self.audio.bands[lo:hi]]
        self.flevel = sum(self.fb) / len(self.fb)
        self.update(t)
        return super().frame(t)

    def update(self, t):
        pass

    def band_at(self, frac):
        """Level of the chosen range at position frac (0 = lowest, 1 = highest frequency)."""
        return self.fb[min(max(int(frac * len(self.fb)), 0), len(self.fb) - 1)]


class AudioSpectrum(AudioEffect):
    """Equaliser bars: the chosen frequency range spread across the key columns."""
    name = 'Audio Spectrum'
    params = {**audio_controls(), 'style': ['Rainbow', 'Gradient'], 'from': '#00ff60', 'to': '#ff0030'}

    def color(self, key, t):
        level = self.band_at(column(key, 1000) / 1000)
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
    """Whole keyboard pulses with the music (bass by default); colour drifts slowly."""
    name = 'Audio Pulse'
    params = {**audio_controls('Low (bass)'), 'style': ['Rainbow', 'Color'], 'color': '#ff0050'}

    def color(self, key, t):
        c = hsv(t * 0.03 + key.cx / WIDTH * 0.15) if self.p['style'] == 'Rainbow' else hex_to_rgb(self.p['color'])
        return lerp(BLACK, c, 0.05 + 0.95 * self.flevel ** 1.5)


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
    """Spectrum mirrored from the middle: low notes in the centre, high ones at both edges."""
    name = 'Audio Center Bars'
    params = {**audio_controls(), 'style': ['Rainbow', 'Gradient'], 'from': '#ff00a0', 'to': '#00e0ff'}

    def color(self, key, t):
        dist = abs(key.cx - WIDTH / 2) / (WIDTH / 2)
        level = self.band_at(dist)
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
    params = audio_controls()

    def __init__(self, **kw):
        super().__init__(**kw)
        self.peak, self.peak_t, self.level = 0.0, 0.0, 0.0

    def update(self, t):
        self.level = clamp(self.flevel * 2.2)
        if self.level >= self.peak:
            self.peak, self.peak_t = self.level, t
        else:
            self.peak = max(self.level, self.peak - max(0.0, t - self.peak_t - 0.4) * 0.05)

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
    params = audio_controls()
    ROWS = 6

    def __init__(self, **kw):
        super().__init__(**kw)
        self.history = [list(self.fb) for _ in range(self.ROWS)]
        self.next_push = 0.0

    def update(self, t):
        if t >= self.next_push or t < self.next_push - 1:
            self.next_push = t + 0.08
            self.history = self.history[1:] + [list(self.fb)]

    def color(self, key, t):
        age = min(int((HEIGHT - key.cy) / HEIGHT * self.ROWS), self.ROWS - 1)   # 0 = bottom = newest
        row = self.history[-1 - age]
        return ramp(HEAT, row[column(key, len(row))] * 1.1)


class AudioBeatRipple(AudioEffect):
    """A coloured ring bursts from the centre on every beat of the chosen range
    (kicks with Low, snares/vocals with Mid, hi-hats with High)."""
    name = 'Audio Beat Ripple'
    params = {**audio_controls('Low (bass)'), 'background': '#05000a'}

    def __init__(self, **kw):
        super().__init__(**kw)
        self.rings = []          # (start time, hue)
        self.avg, self.last_beat = 0.0, -1.0

    def update(self, t):
        e = self.flevel          # onset: loudness jumps well above its running average
        if e > 0.3 and e > self.avg * 1.35 and t - self.last_beat > 0.18:
            self.last_beat = t
            self.rings = (self.rings + [(t, (t * 0.07) % 1)])[-6:]
        self.avg = self.avg * 0.93 + e * 0.07

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
    params = audio_controls()

    def update(self, t):
        # spectral centroid of the chosen range, placed on the full 22-band scale so bass
        # stays red and treble blue whichever range you pick
        total = sum(self.fb) or 1e-6
        local = sum(i * b for i, b in enumerate(self.fb)) / total
        self.centroid = (self.focus[0] + local) / 22

    def color(self, key, t):
        v = clamp(0.06 + self.flevel * 2.5)
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


class RocketManiaEffect(AudioEffect):
    """Rocket Mania Deluxe: when rockets launch in the game, light streaks shoot from the
    Caps rows to the middle of the keyboard and explode into fireworks."""
    name = 'Rocket Mania: Launch & Fireworks'
    params = {'rockets': ['Rainbow', 'Color'], 'color': '#ffb000', 'game folder': '',
              'demo': ['Off', 'Every 3 s']}
    source = 'game'
    ROWS = [3.0, 4.0, 5.0]            # Tab, Caps, Shift row centres
    MID_X = WIDTH * 0.5
    TRAVEL = 0.45                     # s from the left edge to the middle
    BURST = 1.3                       # s a burst lasts

    def __init__(self, **kw):
        super().__init__(**kw)
        from .games import ROCKET_MANIA
        self.game = ROCKET_MANIA
        self.folder = str(self.p['game folder']).strip().strip('"')
        self.pending = []             # events from the audio thread
        self.rockets = []             # (start t, row y, hue)
        self.bursts = []              # (start t, x, y, hue)
        self.launch_t = -9.0
        self.next_demo = 0.0
        self.rnd = random.Random()
        self.audio.listeners.append(self._on_audio)

    def close(self):
        if self._on_audio in self.audio.listeners:
            self.audio.listeners.remove(self._on_audio)
        super().close()

    def _on_audio(self, block):
        spotter = self.game.ensure(self.folder)
        if spotter:
            self.pending.extend(spotter.feed(block))

    def _hue(self):
        return self.rnd.random() if self.p['rockets'] == 'Rainbow' else None

    def _launch(self, t):
        self.launch_t = t
        for n in range(self.rnd.choice((1, 2, 2, 3))):
            row = self.ROWS[(n + self.rnd.randrange(3)) % 3]
            self.rockets.append((t + n * 0.09, row, self._hue()))

    def _color(self, hue, v=1.0):
        c = hex_to_rgb(self.p['color']) if hue is None else hsv(hue)
        return tuple(int(ch * v) for ch in c)

    def frame(self, t):
        while self.pending:                                   # events from the game's sounds
            kind = self.pending.pop(0)
            if kind == 'launch':
                self._launch(t)
            else:
                self.bursts.append((t, self.rnd.uniform(7, 20), self.rnd.uniform(0.3, 2.8), self._hue()))
        if self.p['demo'] == 'Every 3 s' and t >= self.next_demo:
            self.next_demo = t + 3.0
            self._launch(t)
        for r in list(self.rockets):                          # arrived -> explode at the middle
            if t - r[0] >= self.TRAVEL:
                self.rockets.remove(r)
                self.bursts.append((t, self.MID_X + self.rnd.uniform(-1.5, 1.5), r[1] - 1.0, r[2]))
        self.bursts = [b for b in self.bursts if t - b[0] < self.BURST][-8:]
        return super().frame(t)

    def color(self, key, t):
        out = None
        def add(c):
            nonlocal out
            out = c if out is None else tuple(max(a, b) for a, b in zip(out, c))
        if key.side == 'left':                                # launch pad ignites bottom to top
            age = t - self.launch_t
            if 0 <= age < 0.9 and (HEIGHT - key.cy) / HEIGHT <= min(1.0, age / 0.3):
                fade = 1 - max(0.0, age - 0.3) / 0.6
                add(tuple(int(c * fade) for c in lerp((255, 255, 255), (255, 120, 0), age / 0.9)))
        if key.side == 'right' and self.bursts:              # strip flashes with each burst
            tb, _, _, hue = self.bursts[-1]
            v = max(0.0, 1 - (t - tb) / 0.7)
            if v:
                add(self._color(hue, v))
        for t0, row, hue in self.rockets:                     # streaks: bright head, coloured tail
            age = t - t0
            if age < 0 or key.side or abs(key.cy - row) > 0.5:
                continue
            p = 1 - (1 - age / self.TRAVEL) ** 2              # ease-out
            head = p * self.MID_X
            d = head - key.cx
            if -0.6 < d < 4.5:
                v = 1.0 if d < 0.6 else max(0.0, 1 - (d - 0.6) / 3.9)
                add(lerp(self._color(hue, v), (255, 255, 255), 0.7 if d < 0.6 else 0.0))
        for t0, bx, by, hue in self.bursts:                   # firework: flash, ring, sparkles
            age = t - t0
            dist = math.hypot(key.cx - bx, (key.cy - by) * 1.6)
            fade = (1 - age / self.BURST) ** 1.5
            if age < 0.12 and dist < 2.0:
                add((255, 255, 255))
            ring = abs(dist - age * 9)
            if ring < 1.3 and age * 9 < 9:
                add(self._color(hue, (1 - ring / 1.3) * fade))
            if dist < age * 9 and (hash((key.index, int(t0 * 100), int(t * 12))) % 9) == 0:
                add(lerp(self._color(hue, fade), (255, 255, 255), 0.5))
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


BATTLEFIELD_GAMES = {
    'Battlefield 1': {'bf1.exe'},
    'Battlefield V': {'bfv.exe'},
    'Battlefield 2042': {'bf2042.exe'},
    'Battlefield 4': {'bf4.exe', 'bf4_x86.exe'},
    'Any game (always on)': None,
}
COMBAT_THEMES = {   # smoke base, highlight (WASD etc.), muzzle flash
    'Battlefield 1 (amber)': ((46, 20, 4), (255, 140, 30), (255, 200, 90)),
    'Military (olive)': ((14, 24, 6), (150, 200, 60), (255, 220, 120)),
    'Night ops (blue)': ((2, 10, 30), (40, 140, 255), (200, 230, 255)),
}
EXPLOSION = [(0, (255, 255, 200)), (0.25, (255, 170, 30)), (0.6, (230, 50, 0)), (1, (60, 0, 0))]
VK_LBUTTON, VK_RBUTTON, VK_R, VK_G, VK_SHIFT = 0x01, 0x02, 0x52, 0x47, 0x10
COMBAT_KEYS = {'move': [44, 64, 65, 66], 'slots': list(range(22, 29)), 'reload': 46, 'grenade': 68,
               'number row': list(range(21, 35))}


class BattlefieldCombat(Effect):
    """Battlefield-style combat lighting: smoky ambience, muzzle flashes while you fire, a reload bar,
    red hits from the side you were shot from, a heartbeat at low health and shockwaves for explosions.

    Battlefield has no lighting API (and its anti-cheat rules out reading game memory), so this reacts to
    what you see and hear: red damage at the screen edges, loud low booms in the game audio, and your own
    mouse buttons and keys while the game window is focused."""
    name = 'Battlefield: Combat'
    params = {'game': list(BATTLEFIELD_GAMES), 'theme': list(COMBAT_THEMES), 'damage sensitivity': 1.0,
              'shots': ['Muzzle flash', 'Off'], 'explosions': ['On', 'Off'], 'demo': ['Off', 'On']}
    source = 'screen'
    FOCUS_POLL = 0.5

    def __init__(self, **kw):
        super().__init__(**kw)
        from .sources import AUDIO, SCREEN
        self.screen, self.audio = SCREEN, AUDIO
        SCREEN.acquire()
        if self.p['explosions'] == 'On':
            AUDIO.acquire()
        self.exes = BATTLEFIELD_GAMES.get(self.p['game'], {'bf1.exe'})
        self.smoke_c, self.hi_c, self.flash_c = COMBAT_THEMES.get(self.p['theme'], next(iter(COMBAT_THEMES.values())))
        self.sens = max(float(self.p['damage sensitivity']), 0.1)
        self.demo = self.p['demo'] == 'On'
        self.rnd = random.Random()
        self.focused, self._focus_at = self.exes is None, -9.0
        self.last = None
        self.side_base = [None] * 4           # running redness per screen edge: left, right, top, bottom
        self.slow_base = None
        self.low_since = None
        self.hits = []                        # (t, side 0-3, strength)
        self.booms = []                       # (t, strength)
        self.bass_avg = 0.0
        self.last_boom = -9.0
        self.firing_since = None
        self.reload_t = self.grenade_t = -9.0
        self.keys_down = set()
        self.aiming = False
        self.next_demo = 0.0

    def close(self):
        self.screen.release()
        if self.p['explosions'] == 'On':
            self.audio.release()

    # -- inputs ----------------------------------------------------------------------------

    def _in_game(self, t):
        if self.exes is None:
            return True
        if t - self._focus_at > self.FOCUS_POLL or t < self._focus_at:
            from .automation import foreground_process
            self._focus_at = t
            self.focused = foreground_process() in self.exes
        return self.focused

    def _poll_input(self, t):
        from .automation import user32
        down = {vk for vk in (VK_LBUTTON, VK_RBUTTON, VK_R, VK_G) if user32.GetAsyncKeyState(vk) & 0x8000}
        pressed = down - self.keys_down
        self.keys_down = down
        if VK_LBUTTON in down:
            self.firing_since = self.firing_since if self.firing_since is not None else t
        else:
            self.firing_since = None
        self.aiming = VK_RBUTTON in down
        if VK_R in pressed:
            self.reload_t = t
        if VK_G in pressed:
            self.grenade_t = t

    def _screen(self, t, dt):
        g = self.screen.grid
        edges = [[row[0] for row in g], [row[-1] for row in g], g[0], g[-1]]
        red = [sum(max(0.0, r - (gg + b) / 2) for r, gg, b in e) / (len(e) * 255) for e in edges]
        total = sum(red) / 4
        thresh = 0.10 / self.sens
        for side in range(4):
            base = self.side_base[side]
            self.side_base[side] = red[side] if base is None else base * 0.95 + red[side] * 0.05
            if base is not None and red[side] - base > thresh and not any(
                    h[1] == side and t - h[0] < 0.25 for h in self.hits):
                self.hits.append((t, side, min(1.0, (red[side] - base) / thresh / 2)))
        # low health: the edges stay red/bloody for a while, far above the long-run level
        self.slow_base = total if self.slow_base is None else self.slow_base * 0.998 + total * 0.002
        if total - self.slow_base > 0.06 / self.sens:
            self.low_since = self.low_since if self.low_since is not None else t
        else:
            self.low_since = None

    def _audio(self, t):
        instant = sum(self.audio.bands[:4]) / 4
        if instant > 0.55 and instant > self.bass_avg * 1.7 and t - self.last_boom > 0.35:
            self.last_boom = t
            self.booms.append((t, min(1.0, instant)))
        self.bass_avg = self.bass_avg * 0.95 + instant * 0.05

    def _demo(self, t):
        if t < self.next_demo:
            return
        what = self.rnd.choice(['burst', 'burst', 'hit', 'boom', 'reload'])
        if what == 'burst':
            self.firing_since = t
            self.next_demo = t + 0.8
            return
        self.firing_since = None
        if what == 'hit':
            self.hits.append((t, self.rnd.randrange(4), 1.0))
        elif what == 'boom':
            self.booms.append((t, 1.0))
        else:
            self.reload_t = t
        self.next_demo = t + self.rnd.uniform(0.8, 1.6)

    # -- drawing -----------------------------------------------------------------------------

    def frame(self, t):
        dt = 0.0 if self.last is None else max(0.0, t - self.last)
        self.last = t
        in_game = self.demo or self._in_game(t)
        if self.demo:
            if self.firing_since is not None and t - self.firing_since > 0.7:
                self.firing_since = None
            self._demo(t)
        elif in_game:
            self._poll_input(t)
            self._screen(t, dt)
            if self.p['explosions'] == 'On':
                self._audio(t)
        else:
            self.firing_since, self.aiming = None, False
        self.hits = [h for h in self.hits if t - h[0] < 0.9]
        self.booms = [b for b in self.booms if t - b[0] < 1.6]

        # muzzle flash: ~11 shots/s while the trigger is held, each a short decaying flash
        flash = 0.0
        if self.firing_since is not None and self.p['shots'] == 'Muzzle flash':
            phase = ((t - self.firing_since) * 11) % 1.0
            flash = (1 - phase) ** 2 * (0.75 + 0.25 * self.rnd.random())
        heart = 0.0
        if self.low_since is not None and t - self.low_since > 1.0:
            beat = (t * 1.2) % 1.0                         # lub-dub at 72 bpm
            heart = max(math.exp(-((beat - 0.05) / 0.05) ** 2), 0.7 * math.exp(-((beat - 0.25) / 0.05) ** 2))
        reload = (t - self.reload_t) / 1.6                  # reload progress 0..1 across the number row
        grenade = t - self.grenade_t

        out = [BLACK] * NUM_LEDS
        for i, key in BY_INDEX.items():
            if key.iso:
                continue
            x, y = key.cx / WIDTH, key.cy / HEIGHT
            # drifting battlefield smoke
            n = (math.sin(x * 7 + t * 0.6) + math.sin(y * 5 - t * 0.45 + x * 3) + math.sin((x + y) * 4 + t * 0.3)) / 3
            c = lerp(BLACK, self.smoke_c, 0.55 + 0.45 * n)
            if self.aiming:                                 # aiming down sights: focus on the middle
                c = lerp(c, BLACK, clamp(abs(x - 0.45) * 1.6))
            if i in COMBAT_KEYS['move'] or i in COMBAT_KEYS['slots']:
                c = lerp(c, self.hi_c, 0.55 if i in COMBAT_KEYS['move'] else 0.25)
            if i == COMBAT_KEYS['reload'] or i == COMBAT_KEYS['grenade']:
                c = lerp(c, self.hi_c, 0.35)
            if 0 <= reload < 1 and i in COMBAT_KEYS['number row']:
                pos = (key.cx - 0.5) / 14.5
                c = lerp(c, self.hi_c, 1.0 if pos <= reload else 0.15)
            if 0 <= grenade < 2.0 and i == COMBAT_KEYS['grenade']:
                c = lerp(c, (255, 40, 0), 0.5 + 0.5 * math.sin(grenade * 18))
            if flash:
                near = 1.0 if key.side else clamp(1.2 - abs(x - 0.5) * 1.4)
                c = lerp(c, self.flash_c, flash * near)
            for t0, strength in self.booms:                # shockwave from the bottom middle
                age = t - t0
                d = math.hypot(key.cx - WIDTH / 2, (key.cy - HEIGHT) * 1.3)
                ring = clamp(1 - abs(d - age * 22) / 3.5) * clamp(1 - age / 1.6) * strength
                glow = clamp(1 - age / 0.35) * strength * 0.8
                k = max(ring, glow)
                if k > 0:
                    c = lerp(c, ramp(EXPLOSION, age / 1.6 + (1 - ring) * 0.2), k)
            for t0, side, strength in self.hits:           # red hit from the side you were shot from
                age = t - t0
                edge = (x, 1 - x, y, 1 - y)[side]         # side strips sit just outside 0..1
                k = clamp(1 - edge * 2.2) * clamp(1 - age / 0.9) ** 1.5 * strength
                if k > 0:
                    c = lerp(c, (255, 0, 0), k)
            if heart:
                c = lerp(c, (200, 0, 0), heart * (0.9 if key.side else 0.6))
            out[i] = c
        return out


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
           [RainbowWave, VincentSunset, Aurora, Fire, BlackHole, Plasma, Spiral, ColorShift, Breathing, Rain, Starfield, Gradient,
            Solid, GifPlayer, ScrollingText,
            AudioSpectrum, AudioCenter, AudioVU, AudioWaterfall, AudioBeatRipple, AudioColorFlow, AudioPulse,
            ScreenAmbience, KeyFade, Ripple, Heatmap,
            AssettoCorsaGear, RocketManiaEffect, BattlefieldCombat, GamingKeys, DamageFlash]}

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
