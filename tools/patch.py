"""Build the K580 "direct mode" firmware patch (v2).

Adds vendor HID command 0x20 that streams R/G/B plane data straight into the
LED display buffers in RAM (no flash writes), and suppresses the stock effect
engine's present() while frames keep arriving. After TIMEOUT_TICKS without a
frame the stock effects resume on their own.

v2 also:
- counts its own SysTick ticks (the stock counter at 0x20000040 is reset every 16 ticks,
  so v1's fallback timer never expired and a crashed app left the LEDs frozen);
- removes Fn + Print Screen = instant factory reset (Fn + PrtSc now just sends Print Screen);
- fixes the factory reset writing the profile 2/3 keymaps to 0xA300/0xA500 instead of
  0xA27A/0xA3F4 (it scrambled 82 keys in those profiles).

Usage: python patch.py <stock.bin> <out_prefix> [device_flash_90D8.bin]
Writes <out_prefix>.bin and <out_prefix>.hex. With a device flash dump, the
settings area (0xA000-0xD3FF) is taken from the device.
"""
import os
import sys

from keystone import KS_ARCH_ARM, KS_MODE_THUMB, Ks

PATCH_BASE = 0xD400          # unused flash, outside flash_write's 0xA000-0xD3FF window
CMD_DIRECT = 0x20            # stock dispatcher only handles 0x00-0x11

# Stock firmware addresses (see NOTES.md)
SITE_DISPATCH = 0x1CF6       # bl __ARM_common_switch8 in the HID command dispatcher
SITE_PRESENT = 0x6A8A        # bl present() in the effects tick
SWITCH8 = 0x7BE2
PRESENT = 0x49A4
REPLY_TAIL = 0x1ED2          # dispatcher: send the 64-byte reply and return
REPORT_BUF = 0x200000D2      # copy of the received report
LED_PLANES = 0x200001D2      # R[126] G[126] B[126], read by the CT16B1 PWM ISR
PLANES_SIZE = 3 * 126
SYSTICK_VECTOR = 0x3C        # vector table entry 15
STOCK_SYSTICK = 0x795C       # increments 0x20000040 (which 0x280 resets every 16 ticks)
FN_TABLE = 0x7E98            # Fn-layer matrix positions (31), actions at 0x7EB7
FN_PRTSC_ENTRY = 13          # Fn + PrtSc (pos 84) -> 05 08 = factory reset
NO_KEY = 0xFE                # matrix positions are < 126, so this entry never matches
SITE_RESET_KEYMAP_P1 = 0x73EA   # factory reset: movs r0, #0xa3; lsls r0, r0, #8 (0xA300)
SITE_RESET_KEYMAP_P2 = 0x73F8   # factory reset: movs r0, #0xa5; lsls r0, r0, #8 (0xA500)
KEYMAP_P1, KEYMAP_P2 = 0xA27A, 0xA3F4

STATE = 0x20001F00           # free SRAM (stock uses < 0x20000AB0 of 8 KB): deadline, magic, tick counter
TICKS = STATE + 8            # u32 incremented every SysTick (0.3 ms) by our SysTick wrapper
MAGIC = 0xD1EC7C0D           # RAM isn't cleared at reset, so state is only valid with this
TIMEOUT_TICKS = 0x2000       # ~2.5 s

ASM = f"""
    b     hook_dispatch       @ PATCH_BASE + 0
    b     hook_present        @ PATCH_BASE + 2
    b     systick             @ PATCH_BASE + 4 (vector 15)
    b     keymap_p1           @ PATCH_BASE + 6
    b     keymap_p2           @ PATCH_BASE + 8

hook_dispatch:
    cmp   r3, #{CMD_DIRECT:#x}
    beq   direct_cmd
    ldr   r2, lit_switch8
    bx    r2
direct_cmd:
    ldr   r1, lit_state
    cmp   r6, #0
    beq   direct_off
    cmp   r6, #56
    bhi   bad
    adds  r0, r4, r6
    ldr   r2, lit_planes_size
    cmp   r0, r2
    bhi   bad
    ldr   r0, lit_planes
    adds  r0, r0, r4
    ldr   r2, lit_report_data
    movs  r3, #0
copy:
    ldrb  r5, [r2, r3]
    strb  r5, [r0, r3]
    adds  r3, r3, #1
    cmp   r3, r6
    blo   copy
    ldr   r0, lit_tick
    ldr   r0, [r0]
    ldr   r2, lit_timeout
    adds  r0, r0, r2
    str   r0, [r1]
    ldr   r0, lit_magic
    str   r0, [r1, #4]
    b     done
direct_off:
    movs  r0, #0
    str   r0, [r1, #4]
    b     done
bad:
    ldr   r0, lit_report
    movs  r2, #0xff
    strb  r2, [r0, #7]
done:
    ldr   r0, lit_reply_tail
    bx    r0

hook_present:
    ldr   r2, lit_state
    ldr   r3, [r2, #4]
    ldr   r1, lit_magic
    cmp   r3, r1
    bne   stock_present
    ldr   r1, lit_tick
    ldr   r1, [r1]
    ldr   r3, [r2]
    subs  r3, r3, r1          @ ticks left; also catches a stale deadline from before a reset
    ldr   r1, lit_timeout
    cmp   r3, r1
    bhi   expired
    bx    lr
expired:
    movs  r1, #0
    str   r1, [r2, #4]
stock_present:
    ldr   r2, lit_present
    bx    r2

systick:                      @ exception entry saved r0-r3; the stock handler returns
    ldr   r0, lit_tick
    ldr   r1, [r0]
    adds  r1, r1, #1
    str   r1, [r0]
    ldr   r0, lit_stock_systick
    bx    r0

keymap_p1:                    @ replaces movs/lsls in the factory reset (r1, r2 untouched)
    ldr   r0, lit_keymap_p1
    bx    lr
keymap_p2:
    ldr   r0, lit_keymap_p2
    bx    lr

.align 2
lit_switch8:     .word {SWITCH8 | 1:#x}
lit_present:     .word {PRESENT | 1:#x}
lit_reply_tail:  .word {REPLY_TAIL | 1:#x}
lit_state:       .word {STATE:#x}
lit_planes:      .word {LED_PLANES:#x}
lit_planes_size: .word {PLANES_SIZE:#x}
lit_report:      .word {REPORT_BUF:#x}
lit_report_data: .word {REPORT_BUF + 8:#x}
lit_tick:        .word {TICKS:#x}
lit_stock_systick: .word {STOCK_SYSTICK | 1:#x}
lit_keymap_p1:   .word {KEYMAP_P1:#x}
lit_keymap_p2:   .word {KEYMAP_P2:#x}
lit_timeout:     .word {TIMEOUT_TICKS:#x}
lit_magic:       .word {MAGIC:#x}
"""


def assemble(src, base):
    ks = Ks(KS_ARCH_ARM, KS_MODE_THUMB)
    enc, _ = ks.asm(src, base)
    return bytes(enc)


def bl(frm, to):
    off = (to - (frm + 4)) >> 1
    hi = 0xF000 | ((off >> 11) & 0x7FF)
    lo = 0xF800 | (off & 0x7FF)
    return hi.to_bytes(2, 'little') + lo.to_bytes(2, 'little')


def to_ihex(data):
    lines = []
    for addr in range(0, len(data), 16):
        chunk = data[addr:addr + 16]
        rec = bytes([len(chunk), addr >> 8, addr & 0xFF, 0]) + chunk
        lines.append(':' + rec.hex().upper() + '%02X' % (-sum(rec) & 0xFF))
    lines.append(':00000001FF')
    return '\n'.join(lines) + '\n'


def build(stock):
    fw = bytearray(stock)
    code = assemble(ASM, PATCH_BASE)
    hook_dispatch = PATCH_BASE
    hook_present = PATCH_BASE + 2
    systick = PATCH_BASE + 4
    keymap_p1, keymap_p2 = PATCH_BASE + 6, PATCH_BASE + 8
    movs_lsls = lambda hi: bytes([hi, 0x20, 0x00, 0x02])     # movs r0, #hi; lsls r0, r0, #8

    assert len(code) < 0x1000
    assert all(b in (0x00, 0xFF) for b in fw[PATCH_BASE:PATCH_BASE + len(code)])
    assert fw[SITE_DISPATCH:SITE_DISPATCH + 4] == bl(SITE_DISPATCH, SWITCH8)
    assert fw[SITE_PRESENT:SITE_PRESENT + 4] == bl(SITE_PRESENT, PRESENT)
    assert int.from_bytes(fw[SYSTICK_VECTOR:SYSTICK_VECTOR + 4], 'little') == STOCK_SYSTICK | 1
    assert fw[FN_TABLE + FN_PRTSC_ENTRY] == 84 and fw[0x7EB7 + FN_PRTSC_ENTRY * 3:][:3] == bytes([5, 8, 0])
    assert fw[SITE_RESET_KEYMAP_P1:SITE_RESET_KEYMAP_P1 + 4] == movs_lsls(0xA3)
    assert fw[SITE_RESET_KEYMAP_P2:SITE_RESET_KEYMAP_P2 + 4] == movs_lsls(0xA5)

    fw[PATCH_BASE:PATCH_BASE + len(code)] = code
    fw[SITE_DISPATCH:SITE_DISPATCH + 4] = bl(SITE_DISPATCH, hook_dispatch)
    fw[SITE_PRESENT:SITE_PRESENT + 4] = bl(SITE_PRESENT, hook_present)
    fw[SYSTICK_VECTOR:SYSTICK_VECTOR + 4] = (systick | 1).to_bytes(4, 'little')
    fw[FN_TABLE + FN_PRTSC_ENTRY] = NO_KEY
    fw[SITE_RESET_KEYMAP_P1:SITE_RESET_KEYMAP_P1 + 4] = bl(SITE_RESET_KEYMAP_P1, keymap_p1)
    fw[SITE_RESET_KEYMAP_P2:SITE_RESET_KEYMAP_P2 + 4] = bl(SITE_RESET_KEYMAP_P2, keymap_p2)
    return bytes(fw), hook_dispatch, hook_present, len(code)


if __name__ == '__main__':
    stock = open(sys.argv[1], 'rb').read()
    if len(sys.argv) > 3:
        # keep the user's saved settings: flash dump from 0x90D8 (k580.read_flash)
        dump = open(sys.argv[3], 'rb').read()
        base = 0x90D8
        assert dump[:0x9472 - base] == stock[base:0x9472], 'device runs different firmware'
        stock = stock[:0xA000] + dump[0xA000 - base:0xD400 - base] + stock[0xD400:]
    fw, hd, hp, n = build(stock)
    os.makedirs(os.path.dirname(os.path.abspath(sys.argv[2])), exist_ok=True)
    open(sys.argv[2] + '.bin', 'wb').write(fw)
    open(sys.argv[2] + '.hex', 'w').write(to_ihex(fw))
    print('patch %d bytes at %#x: hook_dispatch=%#x hook_present=%#x' % (n, PATCH_BASE, hd, hp))
    print('changed bytes: %d' % sum(a != b for a, b in zip(stock, fw)))
