# Usage

## Run

Requirements: Windows 10/11 (the WebView2 runtime is included in Windows 11).

```bat
K580Rgb.exe                  :: tray app; opens its window
K580Rgb.exe --background     :: tray only, no window (used by "Start with Windows")
```

Options: `--port N`, `--no-resume`.

- Click the tray icon or choose **Open K580 RGB** to show the window. Closing it leaves the app running in the tray.
- Starting the exe again opens the running copy's window.
- Only one program can control the keyboard at a time. Quit the Python app and other RGB software first.
- Settings, uploaded media and the log are in `%APPDATA%\k580rgb\` (shared with the Python app).

## The app

| Tab | What it does |
|---|---|
| Lighting | Built-in effect, brightness, speed, color, side strips. Saved to the keyboard. |
| Per-key | Paint per-key colors (stored in the built-in Custom effect). |
| Live effects | Stack effect layers and stream them to the keyboard (needs direct mode). Nothing is written to flash. |
| Keys & macros | Record macros and assign them, media keys or other keys to G1–G5 or any key. Stored on the keyboard. |
| Profiles & automation | Save profiles and switch them by the app in focus. |
| Settings | Tray icon, open on start, start with Windows, quit. |

Every built-in change is written to the keyboard's flash. The app writes only real changes, at most once per second.

## Direct mode (firmware patch)

The patch tools are Python scripts in `tools/` (Python 3.10+). They add 196 bytes to the stock
firmware so the PC can stream LED frames into RAM. Version 2 also removes the instant Fn + PrtSc
factory reset and fixes the reset's profile 2/3 keymap bug.

1. Get the K580 recovery tool from Redragon support and extract the firmware:

   ```bat
   python -m venv .venv
   .venv\Scripts\python -m pip install -r tools\requirements-dev.txt
   cd tools
   ..\.venv\Scripts\python extract_fw.py "..\recovery\K580 Update Tool For Misusing Software.exe" ..\extracted\k580_stock_fw.bin
   ```

2. Build, test and flash. Quit the app first; the keyboard stops typing for about 20 s.

   ```bat
   ..\.venv\Scripts\python patch.py ..\extracted\k580_stock_fw.bin ..\build\k580_direct
   ..\.venv\Scripts\python emu_test.py ..\extracted\k580_stock_fw.bin ..\build\k580_direct.bin   :: must say ALL PASSED
   ..\.venv\Scripts\python flash.py ..\build\k580_direct.bin --check                            :: validates only
   ..\.venv\Scripts\python flash.py ..\build\k580_direct.bin
   ```

`patch.py` takes an optional third argument, a dump of the keyboard's settings area, to keep your saved settings in the image.

**Back to stock:** `flash.py ..\extracted\k580_stock_fw.bin`. If a flash is interrupted, replug and run it again.

## Troubleshooting

| Problem | Fix |
|---|---|
| "K580 not found" | Replug; quit the Python app, the Redragon software and other RGB apps. |
| "The Python version of K580 RGB is running" | Quit it from its tray icon, then start the exe again. |
| Built-in changes don't show | A live effect is running; press **Stop**. |
| "Run on keyboard" greyed out | Direct mode isn't installed. |
| Music effects don't move | They follow the default Windows audio output. |
