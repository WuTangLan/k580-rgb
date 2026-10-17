"""Flash a full 64 KB image to the K580 through the Sonix SN32F248B ROM bootloader.

Protocol mirrors SonixQMK/sonix-flasher (cmd_reboot_evision + cmd_flash at offset 0).
The bootloader lives in mask ROM (firmware jumps to 0x1FFF0301), so a failed or bad
flash can always be redone, e.g. with extracted/k580_stock_fw.bin.

Usage:
  python flash.py <image.bin> --check     validate image and show devices, no writes
  python flash.py <image.bin> --probe     enter bootloader, handshake, return; no writes
  python flash.py <image.bin>             reboot to bootloader, flash, verify
"""
import struct
import sys
import time

import hid

from k580 import FW_READ_BASE, K580, PID as APP_PID, VID

BOOT_PID = 0x7040                      # SN32F248B ROM bootloader
OTHER_BOOT_PIDS = (0x7010, 0x7900)     # other Sonix chips: refuse
EVISION_REBOOT = struct.pack('<II', 0x5AA555AA, 0xCC3300FF)

CMD_BASE = 0x55AA00
CMD_INIT = CMD_BASE + 1
CMD_PREPARE = CMD_BASE + 5
CMD_REBOOT = CMD_BASE + 7
EXPECTED_STATUS = 0xFAFAFAFA


def validate(img):
    sp, reset = struct.unpack_from('<II', img)
    problems = []
    if len(img) != 0x10000:
        problems.append('size is %d, expected 65536' % len(img))
    if not 0x20000000 < sp <= 0x20002000:
        problems.append('initial SP %#x not in SRAM' % sp)
    if not (reset & 1 and reset < 0xA000):
        problems.append('reset vector %#x invalid' % reset)
    if img[0xFFFC:] != b'\x55\x55\xAA\xAA':
        problems.append('boot marker at 0xFFFC missing')
    return problems


def find(pid, interface=0):
    for d in hid.enumerate(VID, pid):
        if d['interface_number'] == interface:
            return d['path']
    return None


def wait_for(pid, seconds):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        path = find(pid)
        if path:
            return path
        time.sleep(0.25)
    return None


def set_feature(dev, report):
    dev.send_feature_report(b'\x00' + report + b'\x00' * (64 - len(report)))


def get_feature(dev):
    return bytes(dev.get_feature_report(0, 65)[1:])


def isp_init(dev, seconds=5):
    """ROM bootloader handshake; returns the raw reply. Retries while the ROM starts up."""
    end = time.monotonic() + seconds
    last = None
    while time.monotonic() < end:
        try:
            set_feature(dev, struct.pack('<I', CMD_INIT))
            resp = get_feature(dev)
            if struct.unpack_from('<I', resp)[0] == CMD_INIT:
                return resp
            last = resp.hex()
        except (OSError, ValueError) as e:
            last = str(e)
        time.sleep(0.25)
    raise IOError('no ISP handshake (last reply: %s)' % last)


def flash(dev, img):
    set_feature(dev, struct.pack('<III', CMD_PREPARE, 0, len(img) // 64))
    cmd, status = struct.unpack_from('<II', get_feature(dev))
    if cmd != CMD_PREPARE or status != EXPECTED_STATUS:
        raise IOError('prepare failed: cmd %#x status %#x' % (cmd, status))
    for addr in range(0, len(img), 64):
        set_feature(dev, img[addr:addr + 64])
        if addr % 0x1000 == 0:
            print('  %3d%%' % (addr * 100 // len(img)), flush=True)
    print('  100%')


def enter_isp():
    """Return an open handle to the ROM bootloader.

    On this keyboard the ROM keeps the existing USB connection, so it still shows
    up as 0C45:5004 (Windows sees no re-enumeration). Sonix chips that do re-enumerate
    appear as 0C45:7040, so that is accepted too.
    """
    boot = find(BOOT_PID)
    if not boot:
        app = find(APP_PID)
        if not app:
            sys.exit('keyboard not found')
        print('switching keyboard to ROM bootloader (it stops typing until done)...')
        dev = hid.device()
        dev.open_path(app)
        set_feature(dev, EVISION_REBOOT)       # sent exactly once
        dev.close()
        time.sleep(0.5)
        boot = find(BOOT_PID) or find(APP_PID)
    dev = hid.device()
    dev.open_path(boot)
    return dev


def return_to_user_mode(dev):
    try:
        set_feature(dev, struct.pack('<I', CMD_REBOOT))
    except (OSError, ValueError):
        pass
    dev.close()


def main():
    img = open(sys.argv[1], 'rb').read()
    problems = validate(img)
    if problems:
        sys.exit('image rejected: ' + '; '.join(problems))
    print('image ok: %s' % sys.argv[1])

    app, boot = find(APP_PID), find(BOOT_PID)
    others = [p for p in OTHER_BOOT_PIDS if hid.enumerate(VID, p)]
    print('keyboard (5004): %s   bootloader (7040): %s' % (bool(app), bool(boot)))
    if others:
        sys.exit('unexpected Sonix bootloader %s present, refusing' % ', '.join('%04x' % p for p in others))
    if '--check' in sys.argv:
        return

    dev = enter_isp()
    try:
        resp = isp_init(dev)
        print('ISP handshake ok: %s' % resp[:16].hex(' '))
        if '--probe' in sys.argv:
            print('probe only: returning to user mode, nothing written')
        else:
            print('flashing...')
            flash(dev, img)
    except IOError as e:
        print('ISP failed: %s -> returning to user mode' % e)
        return_to_user_mode(dev)
        sys.exit(1)
    return_to_user_mode(dev)
    if '--probe' in sys.argv:
        time.sleep(2)
        try:
            k = K580()
            print('keyboard firmware answering again:', k.command(0x05, 1, 0)[3] == 0x05)
            k.close()
        except (IOError, OSError) as e:
            print('keyboard not answering yet (%s); replug if it stays frozen' % e)
        return

    print('waiting for keyboard to restart...')
    k = None
    end = time.monotonic() + 15
    while k is None and time.monotonic() < end:
        time.sleep(1)
        try:
            k = K580()
            k.command(0x05, 1, 0)
        except (IOError, OSError):
            k = None
    if k is None:
        sys.exit('keyboard did not come back; replug it, then rerun this script')
    try:
        back = k.read_flash(FW_READ_BASE, 0x10000 - FW_READ_BASE)
        same = back == img[FW_READ_BASE:]
        print('readback 0x90D8-0xFFFF: %s' % ('MATCH' if same else 'MISMATCH'))
        status = k.command(0x20, 0, 0)[7]
        print('direct mode command: %s' % ('present' if status == 0 else 'absent (stock firmware)'))
    finally:
        k.close()


if __name__ == '__main__':
    main()
