# K580 RGB for Windows (C# / .NET 10)

A port of the Python app in `k580rgb/` to a single Windows exe. Same features, same UI page, same
config (`%APPDATA%\k580rgb\config.json`). The first run keeps a copy of the Python app's config
as `config.python-backup.json`.

It runs headless in the system tray and opens its UI in its own window (WebView2), never in a
browser. Closing the window frees the browser engine; the app keeps running in the tray.

## Build

```
publish.bat          -> dist\K580Rgb.exe  (one self-contained file, no .NET install needed)
```

Needs the .NET 10 SDK. For development: `dotnet build K580Rgb.slnx`.

## Run

```
K580Rgb.exe [--background] [--port N] [--no-resume]
```

- `--background`: don't open the window on start ("Start with Windows" uses it).
- Starting it again while it runs opens the running copy's window.
- Only one program may talk to the keyboard: quit the Python app first (the exe tells you if it is running).
- "Start with Windows" in Settings or the tray menu replaces the Python app's autostart entry with this exe.

## Layout

| Project | What |
|---|---|
| `src/K580Rgb.Core` | Device protocol (HidSharp), effects engine, controller, macros, automation, audio/screen capture, GIF/text, local HTTP API. The UI page and sample GIFs are embedded from `k580rgb/`. |
| `src/K580Rgb.App` | Tray icon, WebView2 window, single-instance handling (`K580Rgb.exe`). |
| `tools/K580Rgb.Probe` | Hardware probe (`read`: read-only commands) and JSON dumps for the parity check. |
| `tools/parity_check.py` | Renders the same scenes, macros, GIFs and text with both apps and compares them. |

Run the parity check after changing effects (needs the Python app's environment):

```
.venv\Scripts\python dotnet\tools\parity_check.py
```

Deterministic effects match pixel for pixel; Rain, Starfield and Black Hole use per-key random
phases that differ by design. Scrolling text differs slightly (GDI+ vs FreeType glyph spacing).
