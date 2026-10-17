# K580 RGB for Windows

Custom lighting controller for the **Redragon K580 VATA** keyboard (USB `0C45:5004`), as a single
Windows exe. It's a C# port of the Python app on the [`main`](../../tree/main) branch, with the same
features, UI and settings.

- **Built-in effects:** the 19 hardware modes, per-key colors, side strips and profiles, saved on the keyboard.
- **Live effects:** layered real-time animations, music and screen reactive modes, GIFs, scrolling text and game effects.
- **Macros and key remapping:** stored on the keyboard, with no firmware patch needed.
- **Runs in the tray:** the UI opens in its own window, not a browser.

Live effects need **direct mode**, a small patch to the stock firmware (see [USAGE.md](USAGE.md)).

## Usage

Requirements: Windows 10/11 (the WebView2 runtime is included in Windows 11).

1. Download `K580Rgb.exe` from [Releases](../../releases). No install or .NET runtime is needed.
2. Run it. The app starts in the system tray and opens its window.
3. Close the window to keep it running in the tray; click the tray icon to open it again.

```bat
K580Rgb.exe                  :: tray app with its window
K580Rgb.exe --background     :: tray only (used by "Start with Windows")
```

Only one program can control the keyboard: quit the Python app and other RGB software first.
Settings, uploaded media and the log are in `%APPDATA%\k580rgb\` (shared with the Python app).
See [USAGE.md](USAGE.md) for the app's tabs, the firmware patch and troubleshooting.

## Building

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bat
cd dotnet
dotnet build K580Rgb.slnx           :: development build
publish.bat                         :: dist\K580Rgb.exe: one self-contained file
```

Parity check against the Python app (renders the same effects, macros, GIFs and text in both),
needs Python with `requirements.txt` installed:

```bat
python dotnet\tools\parity_check.py
```

The Python app runs from source as on `main` (`run.bat`), and the firmware patch tools need
`tools\requirements-dev.txt`; see [USAGE.md](USAGE.md#direct-mode-firmware-patch).

**CI:** every push runs `.github/workflows/dotnet.yml` on Windows (build, parity check, publish;
`K580Rgb.exe` as a build artifact) and `python.yml` (Python smoke test).

## Layout

| Path | What |
|---|---|
| `dotnet/` | The C# app (see [dotnet/README.md](dotnet/README.md)) |
| `k580rgb/` | The Python app; its UI page and sample GIFs are built into the exe |
| `tools/` | Firmware extraction, patch builder, emulator tests, flasher, smoke test (Python) |
| `NOTES.md` | Reverse-engineering notes: protocol, memory map, patch design |

## Notes

- No Redragon firmware or software is included. The firmware is extracted locally from Redragon's recovery tool.
- Unofficial, not affiliated with Redragon. Flashing firmware is at your own risk; the bootloader is
  in mask ROM, so stock firmware can always be restored.
