"""Live game telemetry.

Assetto Corsa / Assetto Corsa Competizione / AC Rally publish telemetry in named
shared memory (acpmf_physics, acpmf_graphics, acpmf_static). We only open existing
mappings read-only, so nothing is created while the game isn't running.
"""
import ctypes
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
