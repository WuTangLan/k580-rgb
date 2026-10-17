# K580 RGB: C# source

| Project | What |
|---|---|
| `src/K580Rgb.Core` | Device protocol, effects, controller, macros, capture, local HTTP API |
| `src/K580Rgb.App` | Tray icon, WebView2 window, `K580Rgb.exe` |
| `tools/K580Rgb.Probe` | `K580Rgb.Probe read`: read-only device check; JSON dumps for the parity check |
| `tools/parity_check.py` | Compares effects, macros, GIFs and text with the Python app |

```bat
dotnet build K580Rgb.slnx                                :: development build
publish.bat                                              :: dist\K580Rgb.exe (self-contained, single file)
..\.venv\Scripts\python tools\parity_check.py            :: after changing effects (needs the Python app's .venv)
```
