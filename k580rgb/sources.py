"""Background capture threads for audio- and screen-reactive effects.

Both are optional: they need numpy + soundcard (audio) or numpy + mss (screen).
A source runs only while at least one effect holds it (acquire/release).
"""
import threading
import time
import warnings

try:
    import numpy as np
except ImportError:
    np = None

AUDIO_BANDS = 22           # one per key column
SCREEN_GRID = (22, 7)      # columns x rows sampled from the screen


class Source:
    def __init__(self):
        self._users = 0
        self._lock = threading.Lock()
        self._stop = None
        self.error = None

    def acquire(self):
        with self._lock:
            self._users += 1
            if self._users == 1:
                self._stop = threading.Event()
                threading.Thread(target=self._run, args=(self._stop,), daemon=True).start()

    def release(self):
        with self._lock:
            self._users = max(0, self._users - 1)
            if self._users == 0 and self._stop:
                self._stop.set()

    def _run(self, stop):
        try:
            self.run(stop)
        except Exception as e:  # missing device, driver hiccup: report and stop
            self.error = '%s: %s' % (type(e).__name__, e)


class AudioSource(Source):
    """System audio (what you hear) -> log-spaced band levels 0..1 with auto gain."""

    RATE, BLOCK = 48000, 1024

    def __init__(self):
        super().__init__()
        self.bands = [0.0] * AUDIO_BANDS
        self.bass = 0.0
        self.volume = 0.0
        self.beats = 0            # increments on every detected beat
        self.beat_time = 0.0      # time.monotonic() of the last beat

    def run(self, stop):
        import soundcard as sc
        if np is None:
            raise ImportError('numpy')
        speaker = sc.default_speaker()
        mic = sc.get_microphone(id=str(speaker.name), include_loopback=True)
        freqs = np.fft.rfftfreq(self.BLOCK, 1 / self.RATE)
        edges = np.geomspace(40, 16000, AUDIO_BANDS + 1)
        idx = [np.where((freqs >= lo) & (freqs < hi))[0] for lo, hi in zip(edges[:-1], edges[1:])]
        idx = [i if len(i) else np.array([np.argmin(abs(freqs - lo))]) for i, lo in zip(idx, edges)]
        window = np.hanning(self.BLOCK)
        peak = 1e-3
        levels = np.zeros(AUDIO_BANDS)
        bass_avg = 0.0
        with warnings.catch_warnings():
            warnings.simplefilter('ignore')   # soundcard warns on discontinuities
            with mic.recorder(samplerate=self.RATE, channels=2, blocksize=self.BLOCK) as rec:
                while not stop.is_set():
                    data = rec.record(numframes=self.BLOCK).mean(axis=1)
                    spec = np.abs(np.fft.rfft(data * window))
                    raw = np.array([spec[i].mean() for i in idx]) * np.linspace(1, 3, AUDIO_BANDS)
                    peak = max(peak * 0.995, raw.max(), 1e-3)         # slow auto gain
                    new = np.clip(raw / peak, 0, 1) ** 0.7
                    levels = np.maximum(new, levels * 0.82)           # fast attack, smooth fall
                    self.bands = levels.tolist()
                    self.bass = float(levels[:4].mean())
                    self.volume = float(levels.mean())
                    # beat: bass jumps well above its running average
                    instant = float(new[:4].mean())
                    now = time.monotonic()
                    if instant > 0.3 and instant > bass_avg * 1.35 and now - self.beat_time > 0.18:
                        self.beats += 1
                        self.beat_time = now
                    bass_avg = bass_avg * 0.93 + instant * 0.07


class ScreenSource(Source):
    """Primary monitor averaged down to SCREEN_GRID colours, ~15 fps."""

    def __init__(self):
        super().__init__()
        cols, rows = SCREEN_GRID
        self.grid = [[(0, 0, 0)] * cols for _ in range(rows)]

    def run(self, stop):
        import mss
        if np is None:
            raise ImportError('numpy')
        cols, rows = SCREEN_GRID
        with getattr(mss, 'MSS', mss.mss)() as grabber:
            mon = grabber.monitors[1]
            while not stop.is_set():
                t = time.monotonic()
                img = np.frombuffer(grabber.grab(mon).bgra, np.uint8).reshape(mon['height'], mon['width'], 4)
                small = img[::8, ::8, :3].astype(np.float32)     # subsample, then cell means
                h, w = small.shape[:2]
                ys = np.linspace(0, h, rows + 1).astype(int)
                xs = np.linspace(0, w, cols + 1).astype(int)
                self.grid = [[tuple(int(v) for v in small[ys[r]:ys[r + 1], xs[c]:xs[c + 1]].mean(axis=(0, 1))[::-1])
                              for c in range(cols)] for r in range(rows)]
                stop.wait(max(0.0, 1 / 15 - (time.monotonic() - t)))


AUDIO = AudioSource()
SCREEN = ScreenSource()


def available():
    def has(mod):
        try:
            __import__(mod)
            return True
        except ImportError:
            return False
    return {'audio': np is not None and has('soundcard'), 'screen': np is not None and has('mss'),
            'media': np is not None and has('PIL'), 'game': True}
