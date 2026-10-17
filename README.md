# K580 RGB

Custom lighting controller for the **Redragon K580 VATA** keyboard (USB `0C45:5004`).

- **Built-in effects:** the 19 hardware modes, per-key colors, side strips and profiles, saved on the keyboard.
- **Live effects:** layered real-time animations, music and screen reactive modes, GIFs, scrolling text and game effects.
- **Macros and key remapping:** stored on the keyboard, with no firmware patch needed.
- **Tray app:** quick presets, profiles, switching by app, start with Windows.

Live effects need **direct mode**, a small patch to the stock firmware (see [USAGE.md](USAGE.md)).

## Quick start

Install Python 3.10+, then double-click `run.bat`. The app starts in the system tray and opens
http://127.0.0.1:5800/.

## Layout

| Path | What |
|---|---|
| `k580rgb/` | The app: HID protocol, effects, local web UI |
| `tools/` | Firmware extraction, patch builder, emulator tests, flasher |
| `NOTES.md` | Reverse-engineering notes: protocol, memory map, patch design |

## Notes

- No Redragon firmware or software is included. The firmware is extracted locally from Redragon's recovery tool.
- Unofficial, not affiliated with Redragon. Flashing firmware is at your own risk; the bootloader is
  in mask ROM, so stock firmware can always be restored.
