# Using K580 RGB

## 1. Requirements

- Windows 10/11 and a Redragon K580 VATA (shows up as USB `0C45:5004`)
- Python 3.10+ from [python.org](https://www.python.org/downloads/) ("Add to PATH" ticked)

## 2. Start the app

Double-click **`run.bat`**. The first run creates `.venv` and installs the dependencies
(`hidapi`, `numpy`, `soundcard`, `mss`, `pillow`, `pystray`). The app then runs **in the system tray**
(no window) and opens **http://127.0.0.1:5800/** in your browser.

**Tray icon** (keyboard with a rainbow glow; it may sit under the ^ arrow next to the clock):

- **Left-click / Open K580 RGB:** opens the app in your browser.
- **My presets / Live presets:** start a live effect on the keyboard with one click.
- **Stop live effect:** gives the keyboard back its built-in effect.
- **Built-in effects / Brightness:** switch the keyboard's own effect or brightness (stops a live effect).
- **Profiles / Switch profiles by app:** apply a saved profile, turn automatic switching on or off.
- **Start with Windows:** starts the app quietly at login (a per-user Run entry; no admin rights needed).
- **Hide this icon:** the app keeps running without an icon (turn it back on under **Settings**).
- **Quit:** stops the app and hands the LEDs back to the keyboard.

The live effect running on the keyboard when you quit is resumed the next time the app starts.
Starting the app again while it runs just opens the running one, which is also how you reach it with the icon hidden.

From a terminal instead:

```bat
python -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
.venv\Scripts\python -m k580rgb --console   :: --port 5800  --no-browser  --no-resume
```

`run.bat --console` does the same. In console mode, close the app with **Ctrl+C**.
Settings live in `%APPDATA%\k580rgb\`: `config.json` holds profiles, presets and rules, `media\` your
uploaded GIFs, and `k580rgb.log` errors.

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
| Games | Battlefield: Combat, Assetto Corsa: Gear & RPM, Rocket Mania |

Reactive effects only check *which keys are down* while they run; nothing you type is recorded.
Screen effects average the screen into a few colors and never store images.

**Assetto Corsa / ACC / AC Rally:** add the *Assetto Corsa: gear & RPM* preset. While you drive,
the numpad shows your gear (R, N, 1–9), colored by RPM, and the − / + / Enter keys form an RPM bar.
F1–F12 and both side strips act as shift lights and flash blue at the redline (adjustable).
In menus the layer is see-through. Telemetry is read from the game's official shared memory.

### Keys & macros tab (stock firmware, no patch needed)

Macros and key assignments are stored **on the keyboard** and work without the app, on any PC.

- **Key assignments:** G1–G5, the three media keys, and any other key you click on the keyboard picture.
  Each can play a macro, send a media key (play/pause, volume, calculator, …) or another key (F13–F24 too,
  handy as hotkeys for other software), or be disabled. *Default* restores the factory function. For G1–G5
  that's the REC recording made on the keyboard (REC, then a G-key, then type, then REC again).
- **Fn shortcuts always keep working.** Fn + F1–F12 media keys, Fn + arrows and the other Fn combinations are fixed
  in the firmware and take priority while Fn is held. The Fn key itself can't be remapped.
- **Macros:** **Record** captures what you type in the browser, with your timing or a fixed 30 ms per step.
  **Add text** types a sentence (US keyboard layout). **Add** inserts a single press, hold or release
  (for keys the browser can't record: Windows key, Print Screen). Every step can be edited, moved or removed,
  and **Play … time(s)** repeats the whole macro. Delays come in steps of about 10 ms (max 39 s per step),
  and keys still held at the end are released automatically.
- **Save to keyboard** writes the macros and the keymap of all three hardware profiles, then reads them back to check them.
  Up to 48 macros and about 1,700 steps fit in the keyboard.

If the tab shows *"The keymap of hardware profile 2 and 3 is damaged"*: that's a stock-firmware bug
(see section 4). Saving rewrites them.

**Battlefield 1 (also V, 2042, 4, or any game):** add a *Battlefield 1: combat* preset. While the game window
is focused:

- The keys glow with drifting smoke, and WASD, the weapon slots 1–7, R and G are highlighted.
- **Firing** (left mouse held) sends muzzle flashes through the middle of the keyboard and the side strips.
- **Aiming** (right mouse) darkens the outer keys.
- **R** runs a reload bar across the number row, and **G** makes the G key blink like a primed grenade.
- **Getting shot** flashes red from the side the hit came from, judged by which screen edge turns red.
- **Low health** (screen edges stay bloody) adds a heartbeat pulse.
- **Explosions** (loud booms in the game audio) send an orange shockwave from the bottom of the keyboard.

Battlefield has no lighting API, and its anti-cheat rules out reading game memory, so this only uses your screen,
audio and your own inputs. Set the game to **Borderless** (or windowed) in its video settings, because exclusive
fullscreen can capture as black. *Damage sensitivity* tunes hit detection, and the *demo* preset shows everything
without the game.

### Profiles & automation tab

Save the current built-in lighting as a named profile, then add rules like
`valorant.exe → Gaming`. With automation on, the profile switches when that app gets focus.
Automatic switches are at least 5 s apart, to protect the flash.

### Settings tab

Show or hide the tray icon, open the page when the app starts, start with Windows, and quit the app.

## 4. Direct mode (firmware patch)

Live effects need a small addition to the stock firmware: one new USB command that writes
LED colors to RAM. It is 196 bytes in unused flash. Built-in effects, the Redragon software,
macros and the other Fn shortcuts work as before.

Version 2 of the patch also fixes three stock-firmware problems:

- **The LEDs no longer stay frozen** if the app crashes during a live effect. The keyboard takes back its own
  effect after about 2.5 s; v1's timer never expired.
- **Fn + Print Screen no longer factory-resets the keyboard.** In the stock firmware, Fn + PrtSc
  instantly erases all settings, macros and per-key colors, with no confirmation. With v2 it just sends Print Screen.
- **A factory reset no longer scrambles profiles 2 and 3.** The stock reset writes their keymaps to the wrong
  address, which mixed up 82 keys there.

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
