# Redragon K580 Vata — reverse-engineering notes

## Hardware / IDs
- Running firmware: USB `0C45:5004`, MCU Sonix SN32F24xB (EVision VS11K09A), 64 KB flash.
- Bootloader (ISP): `0C45:7687`. Recovery tool ISP check cmd `AA55A55AFF0033CC`.
- Vendor control channel: interface 1, usage page `0xFF1C`, usage `0x92`, 64-byte in/out reports (report ID 0x04).
- OpenRGB supports this device as "EVision Keyboard 0C45:5004" (EVision V1 protocol), with no Direct mode.

## Firmware image
- `extracted/k580_stock_fw.bin`: 64 KB, from the recovery tool's RCDATA resource 4000 (build 2019/03/06, checksum tag 753B).
- `extracted/SN32F240B.hex`: same code. It is identical to the .bin up to 0xCCA7, then has 0xFF where the .bin has 0x00.
- Vectors: SP=0x20000AB0, Reset=0x7995.
- Layout: code 0x0000–0x9471, zero pad 0x9472–0x9FFF, settings 0xA000–0xD3FF (only range `flash_write` accepts), 0xD400–0xFFFB unused, 0xFFFC = `55 55 AA AA` marker.

## Key routines
- `0x7158` `flash_write(addr, len, src)`: refuses addr outside 0xA000–0xD3FF.
- `0x7BE2` `__ARM_common_switch8` (inline byte table follows the `bl`).
- `0x1C98` vendor HID command dispatcher. Copies the 64-byte report to `0x200000D2`, switches on byte[3]; len=byte[4], offset=byte[5..6].

| cmd | op | flash base |
|-----|----|-----------|
| 0x01/0x02 | begin / end session (flag bit 8 @0x20000004) | — |
| 0x03/0x04 | read / write | 0xA000 (header: VID/PID, mode list) |
| 0x05/0x06 | read / write | 0xA080 (mode parameters) |
| 0x07/0x08 | read / write | 0xA100 |
| 0x09/0x0A | read / write | 0xAC00 |
| 0x0F | read firmware table | 0x90D8 |
| 0x10/0x11 | read / write custom per-key colors | 0xA600 |

**Every write command (0x04/06/08/0A/11) calls `flash_write` synchronously inside the handler.**
Streaming per-key frames through 0x11 therefore programs flash on every packet, so it can't be used for live animation.

## Settings layout (stock protocol)
- Header 0xA000: `55 AA ..`, 0xA004 VID/PID, 0xA00A = active hardware profile (0-2), 0xA010 = Fn mode-cycle order.
- Profile p params at 0xA080 + p*0x2A (cmd 0x05/0x06 offset = p*0x2A + field):
  `[0]` mode, `[1]` brightness 0-5 (table 0x8565: 00 20 40 80 BE FF), `[2]` speed 0-5 (0 fastest),
  `[3]` direction (0 / non-zero), `[4]` random colors flag, `[5..7]` R, G, B.
- Custom (mode 0x14) colors for profile p: 0xA600 + p*0x200 (cmd 0x10/0x11), 3 bytes R,G,B per LED index.
- Keymap per profile p at 0xA100 + p*0x17A (cmd 0x07/0x08 offset p*0x17A): 126 x 3-byte entries, one per
  matrix position pos = col*6 + row. The key handler (0x3CE0) reads it straight from flash, so writes apply at once.
  Types: `02 02 kk` HID key, `02 01 mm` modifier bit, `02 00 00` nothing, `03 lo hi` consumer (media),
  `05 xx yy` firmware function (0x32E4): `05 01 nn` play software macro nn, `05 1A 0n` REC slot of G1-G5,
  `05 19 00` REC, `05 02 02` Fn, `05 18 18` (pos 120, toggles settings byte 0x12).
  The factory keymap is the read-only table at 0x90D8 (cmd 0x0F offset 0).
- Extra keys without LEDs: G1 pos 100, G2 88, G3 99, G4 93, G5 87 (table 0x810E), REC 78,
  media keys Previous 114, Next 123, Play/Pause 125.
- **Fn layer is fixed in firmware**: 31 matrix positions at 0x7E98, actions (3 bytes, same format) at 0x7EB7.
  While Fn is held a match wins over the keymap, so remapping keys never affects it. Fn+F1..F12 = consumer
  0x183, vol-, vol+, mute, stop, prev, play, next, 0x18A mail, 0x223 home, 0x192 calculator, 0x221 search;
  Fn+Win `05 04` (Win lock), **Fn+PrtSc `05 08` = immediate factory reset** (sets flag bit 27 of 0x20000000,
  the main loop then calls 0x7382), Fn+arrows / `-` / `=` brightness and speed, Fn+Ins..PgDn `05 11..16`,
  Fn+Pause `05 1D` side strips, Fn+Space `05 17`, Fn+` `05 0F`, Fn+REC `05 1B`, Fn+ScrLk `05 1E`.
- **Factory reset bug (0x7382)**: it rewrites the header, params, custom colors and macros, and copies the factory keymap to
  0xA100, **0xA300 and 0xA500**, but profiles 2/3 are read from 0xA27A/0xA3F4. After a reset (Fn+PrtSc)
  82 keys of profiles 2 and 3 are scrambled. Seen on this keyboard; the app's Keys & macros save rewrites them.

## Macros
- **Software macros** (cmd 0x09/0x0A, flash 0xAC00, played by 0x419C): u16 offset at 0xAC10 + n*2 (relative to 0xAC00)
  -> header `[count lo, count hi, repeat, 0]` + 4-byte events
  `[delay lo, 0x20 | 0x80 if release | delay bits 8-11, 1 = modifier bit / 2 = key, code]`.
  Triggered by keymap entry `05 01 nn` on key press. One event per 16-SysTick tick (4.8 ms); the delay (after the
  event) counts down every second tick, so 1 unit = 9.6 ms (max 0xFFF = 39 s). Repeat byte > 1 replays.
  The factory reset clears 0xAC00-0xAC7F, so the app keeps at most 48 macros there and puts data from 0xAC80.
- **REC macros** (on the keyboard): REC, then a G-key records into 0xC800 + slot*64 (key codes only, FE/FF
  modifier markers, no timing); a `05 1A 0n` entry plays slot n. The REC capture matches the matrix position
  (table 0x810E), not the keymap.
- LEDs: 107 (list at 0x8232), index = row*21 + col. No LEDs on G1-G5, media keys, or matrix slot (5,7).
- Live writes via 0x06/0x11 take effect immediately; no begin/end commands are needed (same as OpenRGB).

## Side light strips (confirmed on the keyboard, 2026-10-17)
- 14 LEDs on matrix slots from table 0x8335: left `13,112,114,115,116,117,118`, right `17,18,19,20,79,100,98`
  (the table lists each strip back to front; layout.py uses front to back). Their G and B are swapped vs keys: the firmware writes G into the B plane.
- Profile bytes: `0x14` mode (0 off, 1 per-LED color-wheel flow, 2 whole-strip color cycle, 3 breathing that steps
  color each breath, 4 static, 5 copies the current key-effect color), `0x15` speed, `0x16` color index into 0x8BFD
  (red, orange, yellow, green, cyan, blue, purple, white). The side engine is the second switch in the effects tick (0x6A5E);
  `0x4A72` paints all 14. FN+PAUSE (handler ~0x39AA) cycles modes 1..5 in RAM, and mode 4 steps through the colors.
- Direct mode drives them like keys (plane order + G/B swap handled in `k580rgb/device.py`).

## LED pipeline
- CT16B1 IRQ (`0x763C`) multiplexes 18 phases (6 rows x R/G/B). Each phase loads 21 PWM match registers from a display plane.
- Display planes, contiguous 378 bytes: R `0x200001D2`, B `0x20000250`, G `0x200002CE` (6x21 each; index = row*21 + col).
  Color order comes from the custom-mode code (0x4D56): byte0 to plane 0x1D2, byte1 to 0x2CE, byte2 to 0x250.
- Effects tick `0x6992` (main loop) runs the mode effect into working buffers, then calls present `0x49A4`.
  Present copies working R `0x2000034C`, B `0x200003CA`, G `0x20000448` to the planes and overlays the lock indicators.
- SysTick (`0x795C`) increments a u32 at `0x20000040` every 0.3 ms (reload = 3*clk/10000).
- SRAM is 8 KB; the stock firmware uses < `0x20000AB0` (stack top).

## Bootloader / flashing
- `0x1954`: a feature report of `AA55A55A FF0033CC` (LE `0x5AA555AA, 0xCC3300FF`) jumps to mask ROM `0x1FFF0301`. The ROM bootloader can't be erased.
- ROM bootloader `0C45:7040` (SN32F248B), protocol as in SonixQMK/sonix-flasher.
- **The ROM keeps the USB connection:** after the reboot magic the ROM bootloader takes over the *same* enumerated
  device, still 0C45:5004 to Windows (there is no re-enumeration to 7040). Speak the ISP protocol (feature reports on
  interface 0) to that same device. ISP cmds (`0x55AA00 | n`): 01 init/get version, 04 enable erase, 05 prepare/program,
  06 checksum, 07 return to user mode. A probe = magic, init, 07; nothing is written and the keyboard comes back on its own.
- 2026-10-17: flashed `build/k580_direct.bin` with `tools/flash.py`. Readback matched and direct mode answers.
  Streaming tops out at about 36 fps (7 packets per frame). To revert: `tools/flash.py build/k580_stock_with_my_settings.bin`.
- Read-only check on 2026-10-17: device flash 0x90D8–0xFFFF matches `k580_stock_fw.bin`. Only the settings bytes 0xA080–0xA088 differ (current mode).

## Direct-mode patch (`tools/patch.py`)
- Cmd `0x20`: len (<= 56) bytes at plane offset (0..377) go into the display planes. len 0 = direct off. Errors set byte[7]=0xFF (stock firmware also answers 0xFF, which is how to detect it).
- Each frame arms a deadline of +0x2000 ticks (~2.5 s). While it hasn't passed, present() is skipped; after that the stock effects resume.
- State at `0x20001F00` {deadline, magic `0xD1EC7C0D`, v2: tick counter}. Hooks: `bl` at `0x1CF6` -> `0xD400`, `bl` at `0x6A8A` -> `0xD402`.
- v1 (flashed 2026-10-17, 156 bytes) used the stock tick 0x20000040 for the deadline, but 0x280 resets that counter every
  16 ticks, so the fallback never fired.
- v2 (196 bytes): vector 15 (SysTick) -> wrapper at 0xD404 that counts its own ticks at 0x20001F08 and then runs the stock
  handler; a deadline more than 0x2000 ticks ahead counts as expired (stale state after a warm reset).
  Also: Fn table entry 13 (PrtSc) gets position 0xFE so Fn+PrtSc no longer factory-resets, and the factory reset's
  keymap writes at 0x73EA/0x73F8 call stubs that return 0xA27A/0xA3F4. `emu_test.py` covers all of it.
- 2026-10-17: flashed v2 (built with the device's settings), readback matched, direct mode answers. v1 kept as `build/k580_direct_v1.bin`.
- `tools/emu_test.py` emulates stock vs patched firmware (unicorn): all stock commands give identical RAM, replies and flash registers.

## Files
- `build/k580_direct.bin`: patched image with the device's settings.
- `build/k580_stock_with_my_settings.bin`: stock image with the device's settings (revert).
- `extracted/k580_stock_fw.bin`: factory stock image.
- `tools/flash.py <img> [--check]`: reboot to ROM bootloader, flash, read back, verify.
