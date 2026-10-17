"""Live game telemetry.

Assetto Corsa / Assetto Corsa Competizione / AC Rally publish telemetry in named
shared memory (acpmf_physics, acpmf_graphics, acpmf_static). We only open existing
mappings read-only, so nothing is created while the game isn't running.
"""
import ctypes
import os
import struct
import threading
import time
from ctypes import wintypes

kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
kernel32.OpenFileMappingW.restype = wintypes.HANDLE
kernel32.OpenFileMappingW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
kernel32.MapViewOfFile.restype = ctypes.c_void_p
kernel32.MapViewOfFile.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_size_t]
kernel32.UnmapViewOfFile.argtypes = [ctypes.c_void_p]
kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.OpenProcess.restype = wintypes.HANDLE
kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
FILE_MAP_READ = 0x0004


class SharedMemory:
    """Read-only view of an existing named mapping (None if the game isn't running)."""

    def __init__(self, name, size):
        self.name, self.size = name, size
        self.handle = self.view = None

    def open(self):
        h = kernel32.OpenFileMappingW(FILE_MAP_READ, False, self.name)
        if not h:
            return False
        v = kernel32.MapViewOfFile(h, FILE_MAP_READ, 0, 0, self.size)
        if not v:
            kernel32.CloseHandle(h)
            return False
        self.handle, self.view = h, v
        return True

    def read(self, offset, fmt):
        return struct.unpack_from(fmt, ctypes.string_at(self.view + offset, struct.calcsize(fmt)))

    def close(self):
        if self.view:
            kernel32.UnmapViewOfFile(self.view)
            kernel32.CloseHandle(self.handle)
        self.handle = self.view = None


class AssettoCorsa:
    """Gear / RPM from AC, ACC or AC Rally. Offsets from the AC shared-memory SDK:
    physics: gear @16 (0=R, 1=N, 2=1st), rpms @20, speedKmh @28
    graphics: status @4 (0 off, 1 replay, 2 live, 3 pause)
    static: maxRpm @412
    """
    RETRY = 2.0

    def __init__(self):
        self.physics = SharedMemory('Local\\acpmf_physics', 32)
        self.graphics = SharedMemory('Local\\acpmf_graphics', 8)
        self.static = SharedMemory('Local\\acpmf_static', 416)
        self._next_try = 0.0
        self._seen_max = 0
        self._lock = threading.Lock()

    def _attach(self):
        if self.physics.view:
            return True
        now = time.monotonic()
        if now < self._next_try:
            return False
        self._next_try = now + self.RETRY
        if self.physics.open() and self.graphics.open() and self.static.open():
            self._seen_max = 0
            return True
        for m in (self.physics, self.graphics, self.static):
            m.close()
        return False

    def read(self):
        """{'gear': 'R'|'N'|1.., 'rpm', 'max_rpm', 'speed'} while driving, else None."""
        with self._lock:
            if not self._attach():
                return None
            try:
                (status,) = self.graphics.read(4, '<i')
                gear, rpm = self.physics.read(16, '<ii')
                (speed,) = self.physics.read(28, '<f')
                (max_rpm,) = self.static.read(412, '<i')
            except OSError:
                for m in (self.physics, self.graphics, self.static):
                    m.close()
                return None
            if status not in (2, 3):          # menus / replays: stay quiet
                return None
            self._seen_max = max(self._seen_max, rpm)
            if max_rpm < 1000:                # some mod cars leave maxRpm empty
                max_rpm = max(self._seen_max, 1)
            return {'gear': 'R' if gear == 0 else 'N' if gear == 1 else gear - 1,
                    'rpm': rpm, 'max_rpm': max_rpm, 'speed': speed}


ASSETTO = AssettoCorsa()


# -- sound-event games ------------------------------------------------------------------

def running_exe_path(exe_name):
    """Full path of a running process by executable name (case-insensitive), else None."""
    TH32CS_SNAPPROCESS = 0x2

    class PROCESSENTRY32W(ctypes.Structure):
        _fields_ = [('dwSize', wintypes.DWORD), ('cntUsage', wintypes.DWORD), ('th32ProcessID', wintypes.DWORD),
                    ('th32DefaultHeapID', ctypes.c_void_p), ('th32ModuleID', wintypes.DWORD),
                    ('cntThreads', wintypes.DWORD), ('th32ParentProcessID', wintypes.DWORD),
                    ('pcPriClassBase', ctypes.c_long), ('dwFlags', wintypes.DWORD),
                    ('szExeFile', ctypes.c_wchar * 260)]

    kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if not snap or snap == wintypes.HANDLE(-1).value:
        return None
    try:
        entry = PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(entry)
        ok = kernel32.Process32FirstW(snap, ctypes.byref(entry))
        while ok:
            if entry.szExeFile.lower() == exe_name.lower():
                h = kernel32.OpenProcess(0x1000, False, entry.th32ProcessID)   # QUERY_LIMITED_INFORMATION
                if h:
                    try:
                        buf = ctypes.create_unicode_buffer(1024)
                        size = wintypes.DWORD(len(buf))
                        if kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size)):
                            return buf.value
                    finally:
                        kernel32.CloseHandle(h)
            ok = kernel32.Process32NextW(snap, ctypes.byref(entry))
    finally:
        kernel32.CloseHandle(snap)
    return None


class SoundSpotter:
    """Recognises a game's own sound effects in the PC's audio output.

    Each reference sound and the live stream are turned into the same features: per
    ~21 ms block, log energy in 24 bands (80 Hz - 12 kHz). The latest N blocks are
    compared with each reference (z-normalised correlation, so music playing on top
    and volume don't matter much); a hit needs a high score and real loudness.
    """
    RATE, BLOCK, BANDS = 48000, 1024, 24

    def __init__(self, sounds, cooldown=0.35):
        """sounds: kind -> (reference sample arrays, sample rate, seconds to match, threshold)"""
        import numpy as np
        self.np = np
        freqs = np.fft.rfftfreq(self.BLOCK, 1 / self.RATE)
        edges = np.geomspace(80, 12000, self.BANDS + 1)
        self.bins = [np.where((freqs >= lo) & (freqs < hi))[0] for lo, hi in zip(edges[:-1], edges[1:])]
        self.window = np.hanning(self.BLOCK)
        self.templates = {}           # kind -> [features z-normalised]
        self.thresholds = {}
        for kind, (samples, rate, seconds, threshold) in sounds.items():
            self.thresholds[kind] = threshold
            for s in samples:
                f = self._features(self._resample(s, rate)[:int(seconds * self.RATE)])
                self.templates.setdefault(kind, []).append(self._znorm(f))
        self.frames = max(t.shape[0] for ts in self.templates.values() for t in ts)
        self.history = np.zeros((self.frames, self.BANDS), np.float32)
        self.loud = np.zeros(self.frames, np.float32)
        self.cooldown = cooldown
        self.last_hit = {k: 0.0 for k in self.templates}
        self.events = []              # (time.monotonic(), kind)
        self.scores = {k: 0.0 for k in self.templates}

    def _resample(self, x, rate):
        np = self.np
        if rate == self.RATE:
            return x
        n = int(len(x) * self.RATE / rate)
        return np.interp(np.linspace(0, len(x) - 1, n), np.arange(len(x)), x).astype(np.float32)

    def _block_features(self, block):
        np = self.np
        spec = np.abs(np.fft.rfft(block * self.window)) ** 2
        return np.log10(np.array([spec[b].sum() for b in self.bins]) + 1e-9), float(np.sqrt((block ** 2).mean()))

    def _features(self, x):
        np = self.np
        n = len(x) // self.BLOCK
        return np.array([self._block_features(x[i * self.BLOCK:(i + 1) * self.BLOCK])[0] for i in range(n)], np.float32)

    def _znorm(self, f):
        # remove each band's average over the window: drops the spectral tilt every sound
        # shares and steady background music, keeping how the sound evolves
        f = f - f.mean(axis=0, keepdims=True)
        return f / (self.np.linalg.norm(f) + 1e-9)

    def feed(self, block, now=None):
        """One BLOCK of mono float audio at RATE. Returns the kinds detected in this block."""
        np = self.np
        feat, rms = self._block_features(block)
        self.history = np.roll(self.history, -1, axis=0)
        self.history[-1] = feat
        self.loud = np.roll(self.loud, -1)
        self.loud[-1] = rms
        now = time.monotonic() if now is None else now
        hits = []
        for kind, temps in self.templates.items():
            best = 0.0
            for t in temps:
                n = t.shape[0]
                if self.loud[-n:].max() < 0.004:          # silence: nothing to match
                    continue
                best = max(best, float((self._znorm(self.history[-n:]) * t).sum()))
            self.scores[kind] = best
            if best >= self.thresholds[kind] and now - self.last_hit[kind] >= self.cooldown:
                self.last_hit[kind] = now
                hits.append(kind)
                self.events = (self.events + [(now, kind)])[-32:]
        return hits


def load_wav_mono(path):
    import wave
    import numpy as np
    try:
        with wave.open(path) as w:
            n, rate, ch, width = w.getnframes(), w.getframerate(), w.getnchannels(), w.getsampwidth()
            raw = w.readframes(n)
    except (RuntimeError, EOFError, wave.Error) as e:      # some game WAVs have broken chunk sizes
        raise ValueError('unreadable WAV %s: %s' % (os.path.basename(path), e or type(e).__name__))
    raw = raw[:len(raw) // (width * ch) * width * ch]
    if width == 2:
        x = np.frombuffer(raw, np.int16).astype(np.float32) / 32768
    elif width == 1:
        x = (np.frombuffer(raw, np.uint8).astype(np.float32) - 128) / 128
    else:
        raise ValueError('unsupported WAV sample width %d' % width)
    return x.reshape(-1, ch).mean(1), rate


class RocketMania:
    """Rocket Mania Deluxe (PopCap): detects rocket launches and firework explosions by
    recognising the game's own sound effects (sounds/cached_*.wav) in the audio output.
    The game folder is found from the running RocketMania.exe, or given explicitly."""
    EXE = 'RocketMania.exe'
    LAUNCH = ['cached_Rocket Launch Woosh 03b.wav']
    EXPLOSIONS = ['cached_Firework Explosion 0%d.wav' % i for i in (1, 3, 4, 5, 6, 7)]

    def __init__(self):
        self.spotter = None
        self.folder = None
        self.error = None
        self._next_look = 0.0
        self._lock = threading.Lock()

    def ensure(self, folder=''):
        """Load the game's sounds once (from `folder` or the running game). Returns spotter or None."""
        with self._lock:
            if self.spotter and (not folder or folder == self.folder):
                return self.spotter
            now = time.monotonic()
            if now < self._next_look and not folder:
                return None
            self._next_look = now + 3.0
            if not folder:
                exe = running_exe_path(self.EXE)
                if not exe:
                    self.error = 'Rocket Mania is not running (or set its folder)'
                    return None
                folder = os.path.dirname(exe)
            sounds = os.path.join(folder, 'sounds')
            try:
                launch = [load_wav_mono(os.path.join(sounds, f)) for f in self.LAUNCH]
                boom = []
                for f in self.EXPLOSIONS:
                    try:
                        boom.append(load_wav_mono(os.path.join(sounds, f)))
                    except (OSError, ValueError):
                        pass              # skip a missing/damaged variant, keep the rest
                if not boom:
                    raise FileNotFoundError('no firework sounds in %s' % sounds)
                rate = launch[0][1]
                # windows/thresholds tuned on simulated gameplay audio (music + other game sounds):
                # launch 100% hit, 0 false; explosion ~60% hit, ~1 false per 5 min
                self.spotter = SoundSpotter({'launch': ([s for s, _ in launch], rate, 0.45, 0.55),
                                             'explosion': ([s for s, _ in boom], rate, 0.12, 0.70)})
                self.folder, self.error = folder, None
            except (OSError, ValueError) as e:
                self.error = 'cannot load game sounds: %s' % e
                self.spotter = None
            return self.spotter


ROCKET_MANIA = RocketMania()
