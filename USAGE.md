# Using K580 RGB

## 1. Requirements

- Windows 10/11 and a Redragon K580 VATA (shows up as USB `0C45:5004`)
- Python 3.10+ from [python.org](https://www.python.org/downloads/) ("Add to PATH" ticked)

## 2. Start the app

Double-click **`run.bat`**. The first run creates `.venv` and installs the dependencies
(`hidapi`, `numpy`, `soundcard`, `mss`, `pillow`). Then the app opens at **http://127.0.0.1:5800/**.

From a terminal instead:

```bat
python -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
.venv\Scripts\python -m k580rgb            :: --port 5800  --no-browser
```

Close the app with **Ctrl+C** in its window. Settings live in `%APPDATA%\k580rgb\`:
`config.json` holds profiles and rules, `media\` your uploaded GIFs, and `k580rgb.log` errors.

The UI only listens on 127.0.0.1 and refuses requests from other websites.

## 3. The app

### Lighting tab: built-in effects (no app needed afterwards)

- Pick one of the 19 hardware effects and set brightness, speed, direction and color.
- **Side strips:** mode (Off, Rainbow flow, Color cycle, Breathing, Static, Follow keys),
  speed and color. FN + PAUSE on the keyboard cycles the same modes.
- Changes are saved on the keyboard automatically, about 0.4 s after you stop adjusting.
  **Apply to keyboard** sends them right away.
- If a live effect is running it hides the built-in ones, so changing anything here stops it.
  A banner tells you when that's the case.

Every built-in change is written to the keyboard's flash memory. The app only sends what changed
and writes at most once a second. The counter in the top right shows the lifetime number of writes.

### Per-key tab

Paint keys with the brush (right-click picks a color), then **Apply to keyboard**. This switches
the keyboard to its built-in *Custom* effect, so the layout stays on the keyboard without the app.
The side strips aren't part of the Custom effect; set them in the Lighting tab.

### Live effects tab (needs direct mode, see section 4)

- **Run on keyboard** streams to the keyboard at up to ~36 fps; **Preview only** animates just
  the on-screen keyboard. Nothing is written to flash.
- Build a scene from **layers**: each has an effect, speed, opacity, blend mode
  (Lighten / Add / Normal) and **Applies to** (All, Keys only, Side strips only).
- **Load a preset** for ready-made scenes. **Save as preset…** stores your current layers (effects, colors,
  settings, blend, opacity, zones) under a name. They're listed under *My presets*, are kept in
  `%APPDATA%\k580rgb\config.json`, and can be overwritten or deleted.
- **Save frame to keyboard** stores the current frame as a per-key layout (Custom effect).
- If the keyboard is unplugged or the PC sleeps, streaming pauses ("Reconnecting…")
  and resumes on its own.

| Group | Effects |
|---|---|
| Ambient | Rainbow Wave, Aurora, Fire, Black Hole, Plasma, Spiral, Color Shift, Breathing, Rain, Starfield, Gradient, Solid |
| Media | **GIF / Image**: upload GIF/PNG/JPG/WEBP/BMP (≤ 25 MB); wide, simple, high-contrast images look best. **Scrolling Text** |
| Music | Spectrum, Center Bars, VU Meter, Waterfall, Beat Ripple, Color Flow, Pulse (react to your default audio output) |
| Screen | Screen Ambience, Damage Flash (flashes when the screen edges turn red) |
| Keys | Key Fade, Ripple, Typing Heatmap, Gaming Keys (WASD / abilities / arrows) |
| Games | Assetto Corsa: Gear & RPM |

Reactive effects only check *which keys are down* while they run; nothing you type is recorded.
Screen effects average the screen into a few colors and never store images.

**Assetto Corsa / ACC / AC Rally:** add the *Assetto Corsa: gear & RPM* preset. While you drive,
the numpad shows your gear (R, N, 1–9), colored by RPM, and the − / + / Enter keys form an RPM bar.
F1–F12 and both side strips act as shift lights and flash blue at the redline (adjustable).
In menus the layer is see-through. Telemetry is read from the game's official shared memory.

### Profiles & automation tab

Save the current built-in lighting as a named profile, then add rules like
`valorant.exe → Gaming`. With automation on, the profile switches when that app gets focus.
Automatic switches are at least 5 s apart, to protect the flash.

## 4. Direct mode (firmware patch)

Live effects need a small addition to the stock firmware: one new USB command that writes
LED colors to RAM. It is 156 bytes in unused flash and changes nothing else.
Built-in effects, the Redragon software and FN shortcuts work as before.

### Get the firmware

Ask Redragon support for the K580 recovery tool (*"K580 Update Tool For Misusing Software.exe"*).
It contains the full stock firmware. Then:

```bat
.venv\Scripts\python -m pip install -r tools\requirements-dev.txt
cd tools
..\.venv\Scripts\python extract_fw.py "..\recovery\K580 Update Tool For Misusing Software.exe" ..\extracted\k580_stock_fw.bin
```

### Build, test, flash

```bat
cd tools
:: optional: keep your keyboard's saved settings (reads them over USB, read-only)
..\.venv\Scripts\python -c "from k580 import K580, FW_READ_BASE; k=K580(); open('../extracted/settings.bin','wb').write(k.read_flash(FW_READ_BASE, 0x10000-FW_READ_BASE))"

..\.venv\Scripts\python patch.py ..\extracted\k580_stock_fw.bin ..\build\k580_direct ..\extracted\settings.bin
..\.venv\Scripts\python emu_test.py ..\extracted\k580_stock_fw.bin ..\build\k580_direct.bin    :: must say ALL PASSED

..\.venv\Scripts\python flash.py ..\build\k580_direct.bin --check    :: validates, writes nothing
..\.venv\Scripts\python flash.py ..\build\k580_direct.bin --probe    :: bootloader handshake, writes nothing
..\.venv\Scripts\python flash.py ..\build\k580_direct.bin            :: flash + read back + verify
```

Close the app first. While flashing, the keyboard stops typing for about 20 s.
`flash.py` sends the reboot-to-bootloader command **once**. The bootloader keeps the same USB
connection (still `0C45:5004`), so don't wait for a new device to appear.

### Go back to stock

```bat
cd tools
..\.venv\Scripts\python flash.py ..\extracted\k580_stock_fw.bin
```

This works whether or not direct mode is installed, and resets the keyboard's saved settings to
factory defaults. The bootloader is in mask ROM, so flashing can't erase it. If a flash is
interrupted, replug the keyboard and run the same command again. `flash.py` also looks for the
bootloader under its own USB ID (`0C45:7040`) in case the keyboard comes up that way.

## 5. Troubleshooting

| Problem | Fix |
|---|---|
| "K580 not found" | Replug; close the Redragon software and other RGB apps (OpenRGB, SignalRGB). |
| Built-in effect changes don't show | A live effect is running; press **Stop** (Lighting tab banner). |
| Live effect stops | It resumes on its own after a reconnect. If not, check `%APPDATA%\k580rgb\k580rgb.log`. |
| "Run on keyboard" greyed out | Direct mode isn't installed (top bar: *Direct mode: not installed*). |
| Music effects don't move | They follow the **default** Windows output device; play audio there. |
| Keyboard frozen after a flash attempt | Replug it; the stock or patched firmware boots again. |
