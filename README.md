# K580 RGB

Custom lighting controller for the **Redragon K580 VATA** keyboard (USB `0C45:5004`).

- **Built-in effects:** the 19 hardware modes, per-key colors, side strips and profiles, saved on the keyboard.
- **Live effects:** layered real-time animations, music and screen reactive modes, GIFs, scrolling text and game effects.
- **Macros and key remapping:** stored on the keyboard, with no firmware patch needed.
- **Tray app:** quick presets, profiles, switching by app, start with Windows.

Live effects need **direct mode**, a small patch to the stock firmware (see [USAGE.md](USAGE.md)).
A single-exe C# version is on the [`csharp-port`](../../tree/csharp-port) branch.

## Usage

Requirements: Windows 10/11 and Python 3.10+ (with "Add to PATH").

1. Download `K580-RGB-python-*.zip` from [Releases](../../releases) (or clone the repo) and unzip it.
2. Double-click **`run.bat`**. The first run creates `.venv` and installs the dependencies.
3. The app runs in the system tray and opens http://127.0.0.1:5800/ in your browser.

```bat
run.bat                 :: tray app
run.bat --console       :: in the terminal instead (Ctrl+C quits)
run.bat --no-browser    :: don't open the page on start
```

Settings, uploaded media and the log are in `%APPDATA%\k580rgb\`. See [USAGE.md](USAGE.md) for the
app's tabs, the firmware patch and troubleshooting.

## Building

Nothing to compile; set up an environment and run from source:

```bat
python -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
.venv\Scripts\python -m k580rgb --console
.venv\Scripts\python tools\smoke_test.py      :: imports, renders every effect, compiles macros
```

Release zip (what CI uploads):

```bat
git archive --format=zip --prefix=K580-RGB/ -o K580-RGB-python.zip HEAD k580rgb k580rgb.pyw run.bat requirements.txt README.md USAGE.md
```

The firmware patch tools need `tools\requirements-dev.txt`; see [USAGE.md](USAGE.md#direct-mode-firmware-patch).

**CI:** every push runs `.github/workflows/python.yml` on Windows (smoke test, then the zip as a build artifact).

## Layout

| Path | What |
|---|---|
| `k580rgb/` | The app: HID protocol, effects, local web UI |
| `tools/` | Firmware extraction, patch builder, emulator tests, flasher, smoke test |
| `NOTES.md` | Reverse-engineering notes: protocol, memory map, patch design |

## Notes

- No Redragon firmware or software is included. The firmware is extracted locally from Redragon's recovery tool.
- Unofficial, not affiliated with Redragon. Flashing firmware is at your own risk; the bootloader is
  in mask ROM, so stock firmware can always be restored.
