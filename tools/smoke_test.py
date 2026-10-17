"""CI smoke test: every module imports, every effect and preset renders, macros compile.

No keyboard needed: audio and screen capture are stubbed (silence, black screen).
Usage: python tools/smoke_test.py
"""
import importlib
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from k580rgb import effects, keycodes, macros, presets, sources  # noqa: E402

sources.Source.acquire = lambda self: None
sources.Source.release = lambda self: None

MODULES = ['automation', 'autostart', 'controller', 'device', 'effects', 'games', 'keycodes', 'layout',
           'macros', 'media', 'modes', 'presets', 'server', 'sources', 'tray']


def check(scene_specs, label):
    sc = effects.scene(scene_specs)
    for t in (0.0, 0.5, 3.7):
        sc.key_pressed(44, t)
        frame = sc.frame(t)
        assert len(frame) == 126 and all(len(c) == 3 and all(0 <= v <= 255 for v in c) for c in frame), label
    sc.close()


def main():
    for m in MODULES:
        importlib.import_module('k580rgb.' + m)
    print('imports: %d modules' % len(MODULES))

    for name in effects.EFFECTS:
        check(presets.normalize([{'effect': name}]), name)
    print('effects: %d render' % len(effects.EFFECTS))

    for name, layers in presets.BUILTIN.items():
        check(presets.normalize(layers), name)
    print('presets: %d render' % len(presets.BUILTIN))

    ms = [macros.normalize_macro({'name': 'hello', 'events': keycodes.text_events('Hello!', 10)})]
    image, index = macros.compile_macros(ms)
    keymap = macros.build_keymap(bytes(macros.KEYMAP_SIZE), {'100': {'type': 'macro', 'macro': 'hello'}}, index)
    assert len(keymap) == macros.KEYMAP_SIZE and keymap[300:303] == bytes([5, 1, 0])
    print('macros: compiled %d bytes' % len(image))
    print('OK')


if __name__ == '__main__':
    main()
