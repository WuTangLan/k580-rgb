# Changelog

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
