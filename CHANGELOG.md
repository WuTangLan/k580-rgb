# Changelog

## 0.4.0

### Added
- **Keys & macros tab:** macros stored on the keyboard (stock firmware's own macro player, no patch needed).
  Record them in the browser (with or without your timing), type text, add single presses/holds/releases,
  edit delays (~10 ms steps), reorder and repeat. Assign macros, media keys, other keys (incl. F13–F24) or
  "disabled" to G1–G5, the media keys or any key you click. Fn shortcuts (Fn + F1–F12 media etc.) always keep working;
  the Fn key can't be remapped. Saves are read back and verified.
- **Battlefield: Combat** effect and presets (BF1, also BF V / 2042 / 4 or any game): smoky ambience,
  muzzle flashes while firing, aim focus, reload bar, grenade blink, directional red hits (from the screen edge that
  turns red), low-health heartbeat and explosion shockwaves (game audio). Only reacts while the game is focused; demo mode.
- **Settings tab:** show/hide the tray icon, open the page on start, start with Windows, quit.
- **Tray:** built-in effects, brightness, profiles and automatic profile switching in the menu; *Hide this icon*.
- **Firmware patch v2** (`tools/patch.py`): its own SysTick timer, so the ~2.5 s fallback finally works;
  Fn + Print Screen no longer factory-resets the keyboard; the factory reset writes the profile 2/3 keymaps
  to the right address. New emulator tests for all three.

### Fixed
- Hardware profiles 2 and 3 had 82 scrambled keys after a keyboard factory reset (a stock-firmware bug). The Keys & macros
  tab detects it and saving repairs it.
- Replies to an earlier request could be taken as the answer to the current one; replies are now matched by command
  and offset, and a lost reply is retried up to 3 times.

## 0.3.0

### Added
- **System tray app:** runs windowless in the tray (`run.bat`, `k580rgb.pyw`). The menu has
  Open, your presets and the built-in presets, Stop live effect, Start with Windows, and Quit.
  `--console` keeps the terminal mode.
- **Resume:** the live effect running on the keyboard when the app closes is resumed at the next start
  (`--no-resume` to skip).
- **Single instance:** starting the app again opens the running copy instead of a second one.
- Built-in presets now live in `k580rgb/presets.py`, shared by the web UI and the tray menu.

### Fixed
- Two copies of the app could run at once on Windows (shared port via `SO_REUSEADDR`), with both driving
  the keyboard. The port is now bound exclusively.

## 0.2.1

### Added
- **Music effects:** every music layer has **React to** (All / Low (bass) / Mid / High (treble)) and
  **Sensitivity** (0.2–3×). Spectrum, Center Bars and Waterfall spread the chosen range across the keyboard;
  VU Meter, Pulse and Color Flow follow its loudness; Beat Ripple detects beats in that range
  (e.g. hi-hats with *High*).

### Fixed
- Side strips were mapped back to front, so they ran the opposite way to the keys
  (e.g. the VU meter filled them top to bottom). This affected every effect that moves along the strips.

## 0.2.0

### Added
- **Your own presets:** save the current live-effect layers under a name, then reload, overwrite or delete them.
  They're stored in `%APPDATA%\k580rgb\config.json`.
- **Multi-color gradients:** *Gradient* and *Vincent Sunset* take 2–8 colors, blended in OKLCH so in-between
  colors stay vivid (blue → yellow passes through teal instead of gray). Ready-made palettes, and
  **Suggest colors** from first → last in *Smooth*, *Vivid* and *Long way* versions. *Gradient* can scroll.
- **Vincent Sunset:** slowly drifting pink, cyan and purple synthwave gradients.
- **Rocket Mania Deluxe integration:** rocket launches send light streaks from the Caps rows to the middle,
  where they explode into fireworks (left strip ignites, right strip flashes). Launches are detected by
  recognizing the game's own sounds in the audio output; the game folder is found from the running game.

### Fixed
- Leaving a live effect froze the LEDs on the last frame, and built-in changes and **Apply** had no visible effect.
  The app now always hands the LEDs back to the keyboard, and no stray frame can follow a stop.

### Known issues
- The direct-mode firmware patch's own ~2.5 s fallback never fires: its timer counter is reset by the firmware
  every ~5 ms. The app releases the LEDs itself, but if it crashes mid-effect the keyboard keeps the last frame
  until it's replugged. A patch v2 with its own SysTick-based timer is planned.

## 0.1.0

First release: built-in effects, per-key colors, side strips, profiles and per-app switching; live layered
effects (ambient, GIF/image, scrolling text, music, screen, key reactions, Assetto Corsa, generic game mode);
direct-mode firmware patch with extraction, emulator tests and ISP flasher; reverse-engineering notes.
