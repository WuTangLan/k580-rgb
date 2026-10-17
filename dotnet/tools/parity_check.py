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


def main():
    bad = compare_static(json.loads(subprocess.check_output([PROBE, 'static'])))

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
