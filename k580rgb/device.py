"""HID protocol for the Redragon K580 (Sonix SN32F248B, EVision firmware, 0C45:5004).

All packets are 64 bytes on the vendor collection (usage page 0xFF1C, usage 0x92):
  [0]=0x04 report id  [1..2]=checksum of bytes 3..63  [3]=command  [4]=length
  [5..6]=offset (LE)  [7]=status (0xFF = rejected)    [8..63]=data (max 56 bytes)

Stock commands write straight to flash (see NOTES.md), so callers should only send
real changes. Command 0x20 exists only on the direct-mode patched firmware.
"""
import threading
import time

import hid

VID, PID = 0x0C45, 0x5004
USAGE_PAGE, USAGE = 0xFF1C, 0x92

CMD_READ_HEADER = 0x03    # flash 0xA000 + offset
CMD_READ_PARAMS = 0x05    # flash 0xA080 + offset
CMD_WRITE_PARAMS = 0x06
CMD_READ_CUSTOM = 0x10    # flash 0xA600 + offset
CMD_WRITE_CUSTOM = 0x11
CMD_DIRECT = 0x20         # patched firmware: RAM-only LED planes

MAX_DATA = 56
PROFILES = 3
PROFILE_PARAMS_SIZE = 0x2A
PROFILE_CUSTOM_SIZE = 0x200
HEADER_PROFILE = 0x0A     # active profile index in the header
NUM_LEDS = 126            # 6 rows x 21 columns, index = row * 21 + col
CUSTOM_SIZE = 3 * NUM_LEDS
STATUS_ERROR = 0xFF
DIRECT_KEEPALIVE = 1.0    # s; patched firmware falls back to stock effects after ~2.5 s
REPLY_TIMEOUT = 0.5       # s per attempt

# Side light strips: 14 LEDs in two groups of 7 (firmware table 0x8335). Their green and
# blue are wired swapped relative to the keys (firmware writes G into the B plane for them).
SIDE_GROUP_A = [13, 112, 114, 115, 116, 117, 118]     # left strip, front to back
SIDE_GROUP_B = [17, 18, 19, 20, 79, 100, 98]          # right strip, front to back
SIDE_LEDS = SIDE_GROUP_A + SIDE_GROUP_B
SIDE_OFFSET = 0x14                 # profile bytes: mode, speed, color index
SIDE_MODES = ['Off', 'Rainbow flow', 'Color cycle', 'Breathing', 'Static', 'Follow keys']
SIDE_COLORS = ['Red', 'Orange', 'Yellow', 'Green', 'Cyan', 'Blue', 'Purple', 'White']

# Indicator LEDs (not keys). Stock present() lights them from the host's LED report:
# 77: R=Scroll, G=Caps, B=Num   78: R=any lock on, G=Kana, B=Compose
LED_INDICATOR_A, LED_INDICATOR_B = 77, 78


class DeviceError(IOError):
    pass


class Params:
    """The 8 effect parameters at the start of each profile."""
    __slots__ = ('mode', 'brightness', 'speed', 'direction', 'random', 'color')

    def __init__(self, mode=6, brightness=5, speed=3, direction=0, random=False, color=(255, 255, 255)):
        self.mode, self.brightness, self.speed = mode, brightness, speed
        self.direction, self.random, self.color = direction, random, tuple(color)

    @classmethod
    def from_bytes(cls, b):
        return cls(b[0], b[1], b[2], 1 if b[3] else 0, bool(b[4]), (b[5], b[6], b[7]))

    def to_bytes(self):
        r, g, b = self.color
        return bytes([self.mode, self.brightness, self.speed, self.direction,
                      1 if self.random else 0, r, g, b])

    def to_dict(self):
        return {'mode': self.mode, 'brightness': self.brightness, 'speed': self.speed,
                'direction': self.direction, 'random': self.random, 'color': list(self.color)}

    @classmethod
    def from_dict(cls, d):
        return cls(int(d['mode']), int(d['brightness']), int(d['speed']), int(d['direction']),
                   bool(d['random']), tuple(int(c) for c in d['color']))


class K580:
    def __init__(self):
        self._dev = None
        self._lock = threading.RLock()
        self.flash_writes = 0     # packets that caused a flash write this session
        self._direct_last = None  # last streamed planes
        self._direct_sent_at = 0.0

    # -- connection ----------------------------------------------------------

    @staticmethod
    def find_path():
        for d in hid.enumerate(VID, PID):
            if d['usage_page'] == USAGE_PAGE and d['usage'] == USAGE:
                return d['path']
        return None

    @property
    def connected(self):
        return self._dev is not None

    def open(self):
        with self._lock:
            if self._dev:
                return True
            path = self.find_path()
            if not path:
                return False
            dev = hid.device()
            try:
                dev.open_path(path)
            except OSError:           # still re-enumerating, or held exclusively for a moment
                return False
            self._dev = dev
            self._direct_last = None
            return True

    def close(self):
        with self._lock:
            if self._dev:
                try:
                    self._dev.close()
                finally:
                    self._dev = None

    # -- transport -----------------------------------------------------------

    def command(self, cmd, length=0, offset=0, data=b''):
        pkt = bytearray(64)
        pkt[0], pkt[3], pkt[4] = 0x04, cmd, length
        pkt[5], pkt[6] = offset & 0xFF, offset >> 8
        pkt[8:8 + len(data)] = data
        cs = sum(pkt[3:]) & 0xFFFF
        pkt[1], pkt[2] = cs & 0xFF, cs >> 8
        with self._lock:
            if not self._dev and not self.open():
                raise DeviceError('keyboard not connected')
            try:
                for _ in range(2):            # every command is idempotent: resend once if the reply got lost
                    self._dev.write(bytes(pkt))
                    deadline = time.monotonic() + REPLY_TIMEOUT
                    while time.monotonic() < deadline:
                        r = bytes(self._dev.read(64, 100))
                        if len(r) >= 8 and r[0] == 0x04 and r[3] == cmd:
                            return r
            except (OSError, ValueError) as e:
                self.close()
                raise DeviceError(str(e)) from e
        raise DeviceError('no reply to command %#04x' % cmd)

    def _read(self, cmd, offset, size):
        out = bytearray()
        while len(out) < size:
            n = min(MAX_DATA, size - len(out))
            out += self.command(cmd, n, offset + len(out))[8:8 + n]
        return bytes(out)

    def _write(self, cmd, offset, data, old=None):
        """Write in 56-byte packets, skipping packets identical to `old`."""
        for pos in range(0, len(data), MAX_DATA):
            chunk = data[pos:pos + MAX_DATA]
            if old is not None and old[pos:pos + len(chunk)] == chunk:
                continue
            r = self.command(cmd, len(chunk), offset + pos, chunk)
            self.flash_writes += 1
            if r[7] == STATUS_ERROR:
                raise DeviceError('keyboard rejected write %#04x @%#x' % (cmd, offset + pos))

    # -- stock firmware --------------------------------------------------------

    def active_profile(self):
        p = self.command(CMD_READ_HEADER, 1, HEADER_PROFILE)[8]
        return p if p < PROFILES else 0

    def read_params(self, profile):
        return Params.from_bytes(self._read(CMD_READ_PARAMS, profile * PROFILE_PARAMS_SIZE, 8))

    def write_params(self, profile, params, old=None):
        self._write(CMD_WRITE_PARAMS, profile * PROFILE_PARAMS_SIZE, params.to_bytes(),
                    old.to_bytes() if old else None)

    def read_side(self, profile):
        """Side strips: (mode 0-5, speed, color index 0-7) at profile bytes 0x14-0x16."""
        return tuple(self._read(CMD_READ_PARAMS, profile * PROFILE_PARAMS_SIZE + SIDE_OFFSET, 3))

    def write_side(self, profile, side, old=None):
        self._write(CMD_WRITE_PARAMS, profile * PROFILE_PARAMS_SIZE + SIDE_OFFSET, bytes(side),
                    bytes(old) if old else None)

    def read_custom(self, profile):
        """Per-key colours of the Custom effect: 126 x (R, G, B)."""
        raw = self._read(CMD_READ_CUSTOM, profile * PROFILE_CUSTOM_SIZE, CUSTOM_SIZE)
        return [tuple(raw[i:i + 3]) for i in range(0, CUSTOM_SIZE, 3)]

    def write_custom(self, profile, colors, old=None):
        flat = lambda cs: bytes(v for c in cs for v in c)
        self._write(CMD_WRITE_CUSTOM, profile * PROFILE_CUSTOM_SIZE, flat(colors),
                    flat(old) if old else None)

    # -- patched firmware ------------------------------------------------------

    def supports_direct(self):
        # stock firmware answers unknown commands with status 0xFF; length 0 = "direct off"
        return self.command(CMD_DIRECT, 0, 0)[7] != STATUS_ERROR

    def direct_frame(self, colors):
        """Stream one frame (126 x RGB) to RAM. Plane order in RAM is R, B, G.

        Only changed packets are sent, except that every packet goes out at least
        once per DIRECT_KEEPALIVE so the firmware's ~2.5 s fallback timer never fires.
        """
        colors = list(colors)
        for i in SIDE_LEDS:                      # side strips are wired R, B, G
            r_, g_, b_ = colors[i]
            colors[i] = (r_, b_, g_)
        r = bytes(c[0] for c in colors)
        g = bytes(c[1] for c in colors)
        b = bytes(c[2] for c in colors)
        planes = r + b + g
        now = time.monotonic()
        full = self._direct_last is None or now - self._direct_sent_at >= DIRECT_KEEPALIVE
        for pos in range(0, len(planes), MAX_DATA):
            chunk = planes[pos:pos + MAX_DATA]
            if full or self._direct_last[pos:pos + len(chunk)] != chunk:
                self.command(CMD_DIRECT, len(chunk), pos, chunk)
        if full:
            self._direct_sent_at = now
        self._direct_last = planes

    def direct_off(self):
        self._direct_last = None
        self._direct_sent_at = 0.0
        self.command(CMD_DIRECT, 0, 0)
