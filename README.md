# K580 RGB

A custom lighting controller for the **Redragon K580 VATA** keyboard (USB `0C45:5004`,
Sonix SN32F248B / EVision firmware), with SignalRGB-style live effects on the keys and
both side strips. The stock effects and presets keep working.

- **Built-in effects:** all 19 hardware modes, per-key colors, side-strip modes, saved profiles,
  and automatic switching by the app in focus. These run with no software and are saved on the keyboard.
- **Live effects:** real-time layered animations: rainbow wave, aurora, fire, black hole, plasma,
  GIF playback, scrolling text, 7 music modes, screen ambience, key-press reactions,
  Battlefield combat effects (hits, firing, explosions), an Assetto Corsa gear/RPM display and generic game modes.
- **Macros and key assignments:** record or write macros (keys, combos, delays, repeat, text), and put them
  or media keys on G1–G5 or any key. They're stored on the keyboard (stock firmware), and the Fn shortcuts stay intact.
- **Tray app:** runs in the system tray (the icon can be hidden). Quick presets, built-in effects, brightness, profiles, start with Windows.
- **Direct mode:** a 196-byte addition to the stock firmware that lets the PC stream frames
  to the LEDs in RAM. Stock behaviour is unchanged, and the keyboard returns to its own effect
  about 2.5 s after the app stops. Patch v2 also removes the stock Fn + PrtSc instant factory reset
  and fixes the reset's profile 2/3 keymap bug.

**See [USAGE.md](USAGE.md)** for setup, the app, and how to install or remove direct mode.
**See [NOTES.md](NOTES.md)** for the reverse-engineering notes: protocol, memory map, patch design.

## Layout

| Path | What |
|---|---|
| `k580rgb/` | The app: HID protocol, effect engine, local web UI (`python -m k580rgb`) |
| `tools/` | Firmware extraction, disassembly, patch builder, emulator tests, flasher |
| `run.bat`, `k580rgb.pyw` | One-click start in the system tray (creates `.venv` on first run) |

## Not included

This repository contains **no Redragon firmware or software**. The firmware is extracted locally
from Redragon's own recovery tool, which you get from Redragon support (see USAGE.md).

## Disclaimer

Unofficial and not affiliated with Redragon. Flashing firmware is at your own risk.
The SN32F248B bootloader is in mask ROM, so the stock firmware can always be restored with
the steps in USAGE.md. The direct-mode patch has been tested only on the K580 VATA with
firmware build 2019/03/06 (checksum tag `753B`). `tools/patch.py` stops if the instructions it
hooks or the free area it uses don't match that build. `tools/flash.py` only checks an image's
basic structure (size, vector table, boot marker), so flash only images built from your own keyboard's firmware.
