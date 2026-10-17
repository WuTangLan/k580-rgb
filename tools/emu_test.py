"""Emulate the stock and patched firmware's HID dispatcher / present() with unicorn.

Usage: python emu_test.py <stock.bin> <patched.bin>
"""
import struct
import sys

from unicorn import UC_ARCH_ARM, UC_HOOK_CODE, UC_MODE_MCLASS, UC_MODE_THUMB, Uc
from unicorn.arm_const import UC_ARM_REG_LR, UC_ARM_REG_PC, UC_ARM_REG_R0, UC_ARM_REG_SP

DISPATCH = 0x1C98
PRESENT = 0x49A4
HOOK_PRESENT = 0xD402
SEND_REPLY = 0x1BD4
USB_CTX = 0x200007B4          # +0x54 flags (bit19 = report received), +0x58 report
REPORT = 0x200000D2
PLANES = 0x200001D2
WORK = {0x2000034C: 0, 0x20000448: 126, 0x200003CA: 252}  # working buf -> plane offset (R, G, B)
TICK = 0x20000040
STATE = 0x20001F00
MAGIC = 0xD1EC7C0D
STOP = 0x0000FFF0

failures = 0


def check(name, cond):
    global failures
    print(('  ok   ' if cond else '  FAIL ') + name)
    failures += not cond


class Emu:
    def __init__(self, fw):
        self.uc = uc = Uc(UC_ARCH_ARM, UC_MODE_THUMB | UC_MODE_MCLASS)
        uc.mem_map(0, 0x10000)
        uc.mem_write(0, fw)
        uc.mem_map(0x20000000, 0x2000)
        uc.mem_map(0x40000000, 0x80000)
        uc.mem_map(0xE000E000, 0x1000)
        uc.hook_add(UC_HOOK_CODE, self._stub, begin=SEND_REPLY, end=SEND_REPLY)

    def _stub(self, uc, addr, size, _):
        # send_reply(ep, buf, len) -> 0 (success)
        uc.reg_write(UC_ARM_REG_R0, 0)
        uc.reg_write(UC_ARM_REG_PC, uc.reg_read(UC_ARM_REG_LR))

    def call(self, addr):
        self.uc.reg_write(UC_ARM_REG_SP, 0x20000AB0)
        self.uc.reg_write(UC_ARM_REG_LR, STOP | 1)
        self.uc.emu_start(addr | 1, STOP, count=200000)

    def rd(self, addr, n):
        return bytes(self.uc.mem_read(addr, n))

    def wr(self, addr, data):
        self.uc.mem_write(addr, bytes(data))

    def u32(self, addr):
        return struct.unpack('<I', self.rd(addr, 4))[0]

    def set_u32(self, addr, v):
        self.wr(addr, struct.pack('<I', v))

    def command(self, cmd, length=0, offset=0, data=b''):
        pkt = bytearray(64)
        pkt[0], pkt[3], pkt[4] = 0x04, cmd, length
        pkt[5], pkt[6] = offset & 0xFF, offset >> 8
        pkt[8:8 + len(data)] = data
        self.set_u32(USB_CTX + 0x54, self.u32(USB_CTX + 0x54) | (1 << 19))
        self.wr(USB_CTX + 0x58, pkt)
        self.call(DISPATCH)
        return self.rd(REPORT, 64)


def fresh(fw, tick=1000):
    e = Emu(fw)
    e.set_u32(TICK, tick)
    for buf, off in WORK.items():
        e.wr(buf, bytes((off + i) & 0xFF for i in range(126)))
    return e


def main():
    stock = open(sys.argv[1], 'rb').read()
    patched = open(sys.argv[2], 'rb').read()

    print('direct frame (cmd 0x20)')
    e = fresh(patched)
    data = bytes(range(0x80, 0x80 + 56))
    reply = e.command(0x20, 56, 0x10, data)
    planes = e.rd(PLANES, 378)
    check('data lands in planes at offset', planes[0x10:0x10 + 56] == data)
    check('rest of planes untouched', planes[:0x10] == bytes(0x10) and planes[0x48:] == bytes(378 - 0x48))
    check('reply status ok', reply[7] == 0)
    check('deadline armed', e.u32(STATE) == 1000 + 0x2000)
    check('magic set', e.u32(STATE + 4) == MAGIC)

    e = fresh(patched)
    e.command(0x20, 56, 378 - 56, data)
    check('last chunk accepted', e.rd(PLANES + 378 - 56, 56) == data)

    print('rejects bad packets')
    for name, length, off in [('len 57', 57, 0), ('past end', 56, 378 - 55), ('huge offset', 1, 0xFFFF)]:
        e = fresh(patched)
        reply = e.command(0x20, length, off, data)
        check(name + ': error status', reply[7] == 0xFF)
        check(name + ': no RAM write', e.rd(PLANES, 378) == bytes(378) and e.u32(STATE + 4) != MAGIC)

    print('direct off (len 0)')
    e = fresh(patched)
    e.command(0x20, 8, 0, data)
    e.command(0x20, 0, 0)
    check('magic cleared', e.u32(STATE + 4) == 0)

    print('stock commands unchanged')
    for cmd, length, off in [(0x01, 0, 0), (0x02, 0, 0), (0x03, 56, 0), (0x05, 56, 0), (0x07, 56, 0),
                             (0x09, 56, 0), (0x0F, 56, 0), (0x10, 56, 0), (0x10, 56, 0x150), (0x13, 0, 0),
                             (0x06, 8, 0), (0x11, 56, 0)]:
        a, b = fresh(stock), fresh(patched)
        ra = a.command(cmd, length, off, data)
        rb = b.command(cmd, length, off, data)
        same = ra == rb and a.rd(0x20000000, 0x1F00) == b.rd(0x20000000, 0x1F00)
        same = same and a.rd(0x40062000, 0x20) == b.rd(0x40062000, 0x20)
        check('cmd %#04x off %#x: identical reply/RAM/flash regs' % (cmd, off), same)

    print('present() hook')
    a, b = fresh(stock), fresh(patched)
    a.call(PRESENT)
    b.call(HOOK_PRESENT)
    check('inactive: identical to stock present', a.rd(0x20000000, 0x1F00) == b.rd(0x20000000, 0x1F00))
    check('inactive: planes filled from working buffers', b.rd(PLANES, 4) == bytes([0, 1, 2, 3]))

    e = fresh(patched)
    e.command(0x20, 56, 0, data)
    e.call(HOOK_PRESENT)
    check('active: planes keep direct frame', e.rd(PLANES, 56) == data)

    e.set_u32(TICK, 1000 + 0x2000)
    e.call(HOOK_PRESENT)
    check('at deadline: still direct', e.rd(PLANES, 56) == data)

    e.set_u32(TICK, 1000 + 0x2001)
    e.call(HOOK_PRESENT)
    check('expired: stock frame restored', e.rd(PLANES, 4) == bytes([0, 1, 2, 3]))
    check('expired: magic cleared', e.u32(STATE + 4) == 0)

    e = fresh(patched, tick=0xFFFFF000)
    e.command(0x20, 56, 0, data)
    e.set_u32(TICK, 0x00000100)
    e.call(HOOK_PRESENT)
    check('tick wraparound: still direct', e.rd(PLANES, 56) == data)

    e = fresh(patched)
    e.set_u32(STATE, 0x12345678)
    e.set_u32(STATE + 4, 0xA5A5A5A5)
    e.call(HOOK_PRESENT)
    check('garbage RAM at boot: stock behaviour', e.rd(PLANES, 4) == bytes([0, 1, 2, 3]))

    print('\n%s' % ('ALL PASSED' if not failures else '%d FAILED' % failures))
    sys.exit(1 if failures else 0)


if __name__ == '__main__':
    main()
