# K580 RGB for Windows

Custom lighting controller for the **Redragon K580 VATA** keyboard (USB `0C45:5004`), as a single
Windows exe. It's a C# port of the Python app on the `main` branch, with the same features, UI and settings.

- **Built-in effects:** the 19 hardware modes, per-key colors, side strips and profiles, saved on the keyboard.
- **Live effects:** layered real-time animations, music and screen reactive modes, GIFs, scrolling text and game effects.
- **Macros and key remapping:** stored on the keyboard, with no firmware patch needed.
- **Runs in the tray:** the UI opens in its own window, not a browser.

Live effects need **direct mode**, a small patch to the stock firmware (see [USAGE.md](USAGE.md)).

## Quick start

Download `K580Rgb.exe` from the latest release and run it. No install or .NET runtime is needed.

## Build

Needs the .NET 10 SDK. Run `dotnet\publish.bat`; it writes `dotnet\dist\K580Rgb.exe`.

## Layout

| Path | What |
|---|---|
| `dotnet/` | The C# app (see `dotnet/README.md`) |
| `k580rgb/` | The Python app; its UI page and sample GIFs are built into the exe |
| `tools/` | Firmware extraction, patch builder, emulator tests, flasher (Python) |
| `NOTES.md` | Reverse-engineering notes: protocol, memory map, patch design |

## Notes

- No Redragon firmware or software is included. The firmware is extracted locally from Redragon's recovery tool.
- Unofficial, not affiliated with Redragon. Flashing firmware is at your own risk; the bootloader is
  in mask ROM, so stock firmware can always be restored.
