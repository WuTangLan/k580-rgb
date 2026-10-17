"""GIF / image playback and text rendering mapped onto the keys.

Images are fitted onto a canvas with the keyboard's proportions (PX px per key unit);
each key then takes the average colour of exactly the area it covers (summed-area
table, so one lookup per key). Decoded clips are cached per file.
"""
import os
import re
import threading

import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageSequence

from .controller import CONFIG_DIR
from .device import NUM_LEDS
from .layout import BY_INDEX, HEIGHT, WIDTH

PX = 8                                    # canvas pixels per key unit
CANVAS = (round(WIDTH * PX), round(HEIGHT * PX))
MAX_FRAMES = 600
MAX_UPLOAD = 25 * 1024 * 1024
MIN_FRAME_MS = 20
GAP_PX = 3 * PX                           # blank space between repeats of scrolling text
USER_DIR = os.path.join(CONFIG_DIR, 'media')
SAMPLE_DIR = os.path.join(os.path.dirname(__file__), 'media_samples')
EXTENSIONS = ('.gif', '.png', '.jpg', '.jpeg', '.webp', '.bmp')
FITS = ['Cover', 'Contain', 'Stretch']

Image.MAX_IMAGE_PIXELS = 40_000_000       # refuse decompression bombs

_KEYS = [k for k in BY_INDEX.values() if not k.iso]


def _rect(k):
    """Canvas rectangle (x0, y0, x1, y1) a LED averages over. Side-strip LEDs sit outside
    the canvas, so they read a thin band along the matching edge instead."""
    y0 = int(k.y * PX) + 1
    y1 = max(int((k.y + k.h) * PX) - 1, y0 + 1)
    if k.side == 'left':
        return 0, y0, PX // 2, y1
    if k.side == 'right':
        return CANVAS[0] - PX // 2, y0, CANVAS[0], y1
    x0 = int(k.x * PX) + 1
    return x0, y0, max(int((k.x + k.w) * PX) - 1, x0 + 1), y1


_RECTS = [(k.index, *_rect(k)) for k in _KEYS]


def sample_keys(arr, dx=0):
    """arr: float32 (h, w, 3) canvas image (w may be wider for scrolling).
    Returns 126 RGB tuples; keys are read at horizontal offset dx."""
    sat = np.zeros((arr.shape[0] + 1, arr.shape[1] + 1, 3), np.float64)
    sat[1:, 1:] = arr.cumsum(0).cumsum(1)
    return _sample_sat(sat, dx)


def _sample_sat(sat, dx=0):
    out = [(0, 0, 0)] * NUM_LEDS
    for i, x0, y0, x1, y1 in _RECTS:
        x0, x1 = x0 + dx, x1 + dx
        s = sat[y1, x1] - sat[y0, x1] - sat[y1, x0] + sat[y0, x0]
        n = (x1 - x0) * (y1 - y0)
        out[i] = tuple(int(v / n) for v in s)
    return out


def fit(img, mode):
    """Fit a PIL image onto the keyboard canvas (RGB, black background)."""
    img = img.convert('RGBA')
    cw, ch = CANVAS
    if mode == 'Stretch':
        img = img.resize(CANVAS, Image.LANCZOS)
    else:
        scale = (max if mode == 'Cover' else min)(cw / img.width, ch / img.height)
        img = img.resize((max(1, round(img.width * scale)), max(1, round(img.height * scale))), Image.LANCZOS)
        if mode == 'Cover':                    # centre crop to the canvas
            left, top = (img.width - cw) // 2, (img.height - ch) // 2
            img = img.crop((left, top, left + cw, top + ch))
    canvas = Image.new('RGBA', CANVAS, (0, 0, 0, 255))
    canvas.alpha_composite(img, ((cw - img.width) // 2, (ch - img.height) // 2))
    return canvas.convert('RGB')


# -- library -------------------------------------------------------------------------

def _safe_name(name):
    base, ext = os.path.splitext(os.path.basename(name))
    base = re.sub(r'[^A-Za-z0-9 _.-]', '', base).strip(' .')[:60] or 'image'
    return base + ext.lower()


def library():
    """[{'name', 'sample'}] user files first."""
    items = []
    for folder, sample in ((USER_DIR, False), (SAMPLE_DIR, True)):
        if os.path.isdir(folder):
            for f in sorted(os.listdir(folder), key=str.lower):
                if f.lower().endswith(EXTENSIONS):
                    items.append({'name': f, 'sample': sample})
    return items


def path_of(name):
    for folder in (USER_DIR, SAMPLE_DIR):
        p = os.path.join(folder, os.path.basename(name))
        if os.path.isfile(p):
            return p
    raise KeyError(name)


def save_upload(filename, data):
    if len(data) > MAX_UPLOAD:
        raise ValueError('file larger than 25 MB')
    name = _safe_name(filename)
    if not name.lower().endswith(EXTENSIONS):
        raise ValueError('unsupported file type (use GIF, PNG, JPG, WEBP or BMP)')
    os.makedirs(USER_DIR, exist_ok=True)
    tmp = os.path.join(USER_DIR, '.upload.tmp')
    with open(tmp, 'wb') as f:
        f.write(data)
    try:
        with Image.open(tmp) as im:
            im.verify()                       # reject anything that isn't a real image
    except Exception as e:
        os.remove(tmp)
        raise ValueError('not a valid image: %s' % e)
    root, ext = os.path.splitext(name)
    n = 2
    while os.path.exists(os.path.join(USER_DIR, name)):
        name = '%s (%d)%s' % (root, n, ext)
        n += 1
    os.replace(tmp, os.path.join(USER_DIR, name))
    _cache.clear()
    return name


def delete(name):
    p = os.path.join(USER_DIR, os.path.basename(name))
    if os.path.isfile(p):
        os.remove(p)
    _cache.clear()


# -- clips ---------------------------------------------------------------------------

class Clip:
    """Decoded animation: per-frame key colours and cumulative end times (s)."""

    def __init__(self, frames, durations):
        self.frames = frames
        self.ends = list(np.cumsum(durations))
        self.total = self.ends[-1]

    def at(self, t):
        t %= self.total
        lo, hi = 0, len(self.ends) - 1
        while lo < hi:                         # first frame ending after t
            mid = (lo + hi) // 2
            if self.ends[mid] > t:
                hi = mid
            else:
                lo = mid + 1
        return self.frames[lo]


_cache = {}
_cache_lock = threading.Lock()


def load(name, fit_mode):
    p = path_of(name)
    key = (p, os.path.getmtime(p), fit_mode)
    with _cache_lock:
        if key in _cache:
            return _cache[key]
    frames, durations = [], []
    with Image.open(p) as im:
        for i, fr in enumerate(ImageSequence.Iterator(im)):
            if i >= MAX_FRAMES:
                break
            arr = np.asarray(fit(fr, fit_mode), np.float32)
            frames.append(sample_keys(arr))
            durations.append(max(fr.info.get('duration', 100) or 100, MIN_FRAME_MS) / 1000)
    clip = Clip(frames, durations)
    with _cache_lock:
        if len(_cache) > 16:
            _cache.clear()
        _cache[key] = clip
    return clip


# -- text ----------------------------------------------------------------------------

def _font(size):
    for f in ('arialbd.ttf', 'segoeuib.ttf', 'arial.ttf', 'DejaVuSans-Bold.ttf'):
        try:
            return ImageFont.truetype(f, size)
        except OSError:
            continue
    return ImageFont.load_default()


class TextStrip:
    """White-on-black text as a scrolling strip; `mask` holds coverage 0..1 per key."""

    def __init__(self, text, height=0.95):
        """height: fraction of the keyboard's height the text's ink should fill."""
        cw, ch = CANVAS
        text = text or ' '
        _, t0, _, b0 = _font(100).getbbox(text)           # size the font by measured ink height
        font = _font(max(8, int(100 * ch * height / max(b0 - t0, 1))))
        left, top, right, bottom = font.getbbox(text)
        w = (right - left) + GAP_PX                        # one period: text + gap before it repeats
        strip = Image.new('L', (w + cw, ch), 0)            # + one canvas so the window never wraps
        draw = ImageDraw.Draw(strip)
        y = (ch - (bottom - top)) / 2 - top
        # copies exactly one period apart from x=0: strip[x] == strip[x + w], so the
        # window strip[dx:dx+cw] loops seamlessly when dx wraps from w back to 0
        for k in range((w + cw) // w + 1):
            draw.text((k * w - left, y), text, font=font, fill=255)
        arr = np.asarray(strip, np.float32)[..., None] / 255
        self.period = w
        self.sat = np.zeros((arr.shape[0] + 1, arr.shape[1] + 1, 1), np.float64)
        self.sat[1:, 1:] = arr.cumsum(0).cumsum(1)

    def coverage(self, dx):
        """Per LED index: fraction of the key covered by text at scroll offset dx px."""
        return [c[0] for c in _sample_sat_float(self.sat, int(dx) % self.period)]


def _sample_sat_float(sat, dx):
    out = [(0.0,)] * NUM_LEDS
    for i, x0, y0, x1, y1 in _RECTS:
        x0, x1 = x0 + dx, x1 + dx
        s = sat[y1, x1] - sat[y0, x1] - sat[y1, x0] + sat[y0, x0]
        out[i] = (float(s[0]) / ((x1 - x0) * (y1 - y0)),)
    return out
