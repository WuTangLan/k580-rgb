"""Host-side access to the K580 vendor HID interface (EVision protocol).

Packets are 64 bytes: [0]=0x04 report id, [1..2]=checksum (sum of bytes 3..63),
[3]=command, [4]=length, [5..6]=offset (LE), [7]=status (0xFF = error), [8..63]=data.
"""
import time

import hid

VID, PID = 0x0C45, 0x5004
USAGE_PAGE, USAGE = 0xFF1C, 0x92

CMD_READ_FW = 0x0F           # read flash at 0x90D8 + offset (stock)
CMD_DIRECT = 0x20            # patched firmware: write LED planes in RAM
FW_READ_BASE = 0x90D8
MAX_DATA = 56

PLANE = 126                  # 6 rows x 21 columns per colour channel
ROWS, COLS = 6, 21


class K580:
    def __init__(self):
        path = next((d['path'] for d in hid.enumerate(VID, PID)
                     if d['usage_page'] == USAGE_PAGE and d['usage'] == USAGE), None)
        if path is None:
            raise IOError('K580 vendor interface (0C45:5004, page FF1C) not found')
        self.dev = hid.device()
        self.dev.open_path(path)

    def close(self):
        self.dev.close()

    def command(self, cmd, length=0, offset=0, data=b''):
        pkt = bytearray(64)
        pkt[0], pkt[3], pkt[4] = 0x04, cmd, length
        pkt[5], pkt[6] = offset & 0xFF, offset >> 8
        pkt[8:8 + len(data)] = data
        cs = sum(pkt[3:]) & 0xFFFF
        pkt[1], pkt[2] = cs & 0xFF, cs >> 8
        self.dev.write(bytes(pkt))
        deadline = time.monotonic() + 1.0
        while time.monotonic() < deadline:
            r = bytes(self.dev.read(64, 200))
            if r and r[0] == 0x04 and r[3] == cmd:
                return r
        raise TimeoutError('no reply to command %#04x' % cmd)

    def read_flash(self, addr, size):
        """Read flash via stock command 0x0F (only addr >= 0x90D8)."""
        out = bytearray()
        while len(out) < size:
            n = min(MAX_DATA, size - len(out))
            r = self.command(CMD_READ_FW, n, addr + len(out) - FW_READ_BASE)
            out += r[8:8 + n]
        return bytes(out)

    def direct_frame(self, planes):
        """Send a full 378-byte R/G/B plane frame (patched firmware only)."""
        assert len(planes) == 3 * PLANE
        for off in range(0, len(planes), MAX_DATA):
            chunk = planes[off:off + MAX_DATA]
            r = self.command(CMD_DIRECT, len(chunk), off, chunk)
            if r[7] == 0xFF:
                raise IOError('direct frame rejected (stock firmware?)')

    def direct_off(self):
        self.command(CMD_DIRECT, 0, 0)
