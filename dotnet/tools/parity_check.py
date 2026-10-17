"""Compare the C# port with the Python app: static UI data and rendered effect frames.

Run from the repo root with the app's Python (needs the k580rgb package's dependencies),
after building the solution:
    .venv\\Scripts\\python dotnet\\tools\\parity_check.py
Audio and screen capture are stubbed on both sides (silence, black screen), so frames are
deterministic. Effects seeded with Python's random module can't match pixel for pixel.
"""
import json
import os
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, ROOT)

from k580rgb import effects, keycodes, layout, modes, presets, sources  # noqa: E402

sources.Source.acquire = lambda self: None      # no capture threads: silence and a black screen
sources.Source.release = lambda self: None

PROBE = os.path.join(ROOT, 'dotnet', 'tools', 'K580Rgb.Probe', 'bin', 'Debug', 'net10.0-windows', 'K580Rgb.Probe.exe')
TIMES = [0.0, 0.05, 0.5, 1.3, 3.7, 12.34, 61.0]
PRESSES = [[44, 0.0], [65, 0.4], [87, 1.0], [44, 1.2], [3, 3.5], [110, 12.0]]
RANDOM_SEEDED = {'Rain', 'Starfield', 'Black Hole'}   # per-key phases come from random.Random(seed)
NOT_PORTED = {'GIF / Image', 'Scrolling Text', 'Rocket Mania: Launch & Fireworks'}


def specs():
    out = [{'name': name, 'layers': presets.normalize([{'effect': name}]), 'times': TIMES, 'presses': PRESSES}
           for name in effects.EFFECTS if name not in NOT_PORTED]
    out.append({'name': 'Gradient scroll', 'times': TIMES, 'layers': presets.normalize(
        [{'effect': 'Gradient', 'args': {'motion': 'Scroll', 'angle': 30,
                                         'colors': ['#ff0000', '#00ff00', '#0000ff', '#ffffff']}}])})
    out.append({'name': 'Black Hole rainbow follow', 'times': TIMES, 'presses': PRESSES, 'layers': presets.normalize(
        [{'effect': 'Black Hole', 'args': {'theme': 'Rainbow', 'follow keys': 'On', 'size': 1.4}}])})
    out.append({'name': 'Battlefield demo off, theme olive', 'times': TIMES, 'layers': presets.normalize(
        [{'effect': 'Battlefield: Combat', 'args': {'theme': 'Military (olive)', 'game': 'Battlefield 4'}}])})
    for name, layers in presets.BUILTIN.items():
        if not any(l['effect'] in NOT_PORTED for l in layers) and 'demo' not in name:
            out.append({'name': 'preset: ' + name, 'layers': presets.normalize(layers), 'times': TIMES, 'presses': PRESSES})
    return out


def python_frames(spec):
    sc = effects.scene(spec['layers'])
    frames, presses = [], list(spec.get('presses', []))
    for t in spec['times']:
        while presses and presses[0][1] <= t:
            i, tp = presses.pop(0)
            sc.key_pressed(i, tp)
        frames.append([list(c) for c in sc.frame(t)])
    sc.close()
    return frames


def compare_static(cs):
    py = json.loads(json.dumps({
        'layout': [k.to_dict() for k in layout.KEYS + layout.SIDE_KEYS],
        'iso_keys': [k.to_dict() for k in layout.ISO_KEYS],
        'bounds': list(layout.BOUNDS),
        'modes': modes.as_list(),
        'effects': effects.as_list(),
        'presets': presets.BUILTIN,
        'normalized': {n: presets.normalize(l) for n, l in presets.BUILTIN.items()},
        'keycodes': [[u, n, c] for u, n, c in keycodes.KEYS],
        'media_codes': [list(m) for m in keycodes.MEDIA],
        'text_events': keycodes.text_events('Hi! a_b\n', 15),
    }))
    bad = 0
    for k in py:
        same = py[k] == cs[k]
        bad += not same
        print('%-4s static %s' % ('ok' if same else 'FAIL', k))
        if not same and isinstance(py[k], list):
            for i, (a, b) in enumerate(zip(py[k], cs[k])):
                if a != b:
                    print('       first difference at [%d]:\n       py %s\n       cs %s' % (i, a, b))
                    break
            if len(py[k]) != len(cs[k]):
                print('       length py %d, cs %d' % (len(py[k]), len(cs[k])))
    return bad


def compare_macros():
    """Macro and keymap bytes go to flash: they must match exactly."""
    from k580rgb import macros
    default = bytes((i * 7 + 3) & 0xFF for i in range(macros.KEYMAP_SIZE))      # stand-in factory keymap
    ms = [{'name': ' Copy all ', 'repeat': 1, 'events': [['down', 0xE0, 10], ['down', 0x04, 20], ['up', 0x04, 0], ['up', 0xE0, 30]]},
          {'name': 'held', 'repeat': 3, 'events': [['down', 0xE1, 0], ['down', 0x0B, 5000], ['up', 0x0B, 41000]]},
          {'name': 'typed', 'events': keycodes.text_events('Hello, World!', 12)}]
    assign = {'100': {'type': 'macro', 'macro': 'held'}, '6': {'type': 'key', 'code': 0x29},
              '12': {'type': 'key', 'code': 0xE6}, '125': {'type': 'media', 'code': 0xCD}, '40': {'type': 'disabled'},
              '88': {'type': 'macro', 'macro': 'deleted one'}, '35': {'type': 'key', 'code': 4}}
    norm = [macros.normalize_macro(m) for m in ms]
    names = [m['name'] for m in norm]
    assigned = macros.normalize_assignments(assign, names)
    image, index = macros.compile_macros(norm)
    py = json.loads(json.dumps({'normalized': norm, 'assignments': assigned, 'image': image.hex(),
                                'keymap': macros.build_keymap(default, assigned, index).hex(), 'index': index}))
    with tempfile.NamedTemporaryFile('w', suffix='.json', delete=False) as f:
        json.dump({'macros': ms, 'assignments': assign, 'default_keymap': default.hex()}, f)
    try:
        cs = json.loads(subprocess.check_output([PROBE, 'macros', f.name]))
    finally:
        os.remove(f.name)
    bad = 0
    for k in py:
        same = py[k] == cs[k]
        bad += not same
        print('%-4s macros %s' % ('ok' if same else 'FAIL', k))
        if not same:
            print('       py %s\n       cs %s' % (json.dumps(py[k])[:300], json.dumps(cs[k])[:300]))
    return bad


def compare_media():
    """GDI+ and Pillow resample differently, so GIF frames only need to be close; same for text glyphs."""
    from k580rgb import media
    bad = 0
    times = [0.0, 0.37, 1.1, 2.9]
    for name in ('Pac-Man chase.gif', 'Fireworks.gif', 'Bouncing balls.gif'):
        for fit in ('Cover', 'Contain', 'Stretch'):
            clip = media.load(name, fit)
            cs = json.loads(subprocess.check_output([PROBE, 'media', name, fit, ','.join(map(str, times))]))
            diffs = [abs(a - b) for t, fc in zip(times, cs) for pa, ca in zip(clip.at(t), fc) for a, b in zip(pa, ca)]
            mean = sum(diffs) / len(diffs)
            ok = mean < 6
            bad += not ok
            print('%-4s media %-22s %-8s mean channel diff %5.2f, max %3d' % ('ok' if ok else 'FAIL', name, fit, mean, max(diffs)))
    for text, size in (('HELLO K580', 0.97), ('Typing is fun', 0.55)):
        strip = media.TextStrip(text, size)
        offsets = [0, 13, 57, 140]
        cs = json.loads(subprocess.check_output([PROBE, 'text', text, str(size), ','.join(map(str, offsets))]))
        diffs = [abs(a - b) for o, fc in zip(offsets, cs) for a, b in zip(strip.coverage(o), fc)]
        mean = sum(diffs) / len(diffs)
        ok = mean < 0.1           # FreeType hints glyph advances, GDI+ does not: periods differ by 1-3 px
        bad += not ok
        print('%-4s text  %-31s mean coverage diff %.3f, max %.2f' % ('ok' if ok else 'FAIL', repr(text), mean, max(diffs)))
    return bad


def main():
    bad = compare_static(json.loads(subprocess.check_output([PROBE, 'static'])))
    bad += compare_macros()
    bad += compare_media()

    sp = specs()
    with tempfile.NamedTemporaryFile('w', suffix='.json', delete=False) as f:
        json.dump(sp, f)
    try:
        cs = json.loads(subprocess.check_output([PROBE, 'frames', f.name]))
    finally:
        os.remove(f.name)
    for spec in sp:
        py = python_frames(spec)
        worst = diffs = 0
        for fp, fc in zip(py, cs[spec['name']]):
            for a, b in zip(fp, fc):
                d = max(abs(x - y) for x, y in zip(a, b))
                worst = max(worst, d)
                diffs += d > 1
        seeded = any(l['effect'] in RANDOM_SEEDED for l in spec['layers'])
        ok = worst <= 1 or seeded
        bad += not ok
        print('%-4s %-52s max diff %3d, LEDs off by >1: %4d%s' % (
            'ok' if ok else 'FAIL', spec['name'], worst, diffs,
            '  (random seeds differ by design)' if seeded and worst > 1 else ''))
    print('\n' + ('all match' if not bad else '%d mismatches' % bad))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
