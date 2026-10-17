"""Built-in live-effect presets (layer stacks, bottom layer first).

Shared by the web UI (served via /api/static) and the tray menu.
"""
from . import effects

WHITE_FADE = {'effect': 'Key Fade', 'args': {'style': 'Color', 'color': '#ffffff'}}

BUILTIN = {
    'Vincent Sunset': [{'effect': 'Vincent Sunset'}],
    'Vincent Sunset + white key fade': [{'effect': 'Vincent Sunset'}, WHITE_FADE],
    'Aurora + rainbow key fade': [{'effect': 'Aurora'}, {'effect': 'Key Fade', 'args': {'style': 'Rainbow'}}],
    'Fire + ripples': [{'effect': 'Fire'},
                       {'effect': 'Ripple', 'args': {'background': '#000000', 'color': '#ffe080'}, 'blend': 'Add'}],
    'Music: spectrum over plasma': [{'effect': 'Plasma', 'opacity': 0.2}, {'effect': 'Audio Spectrum'}],
    'Music: bass pulse': [{'effect': 'Audio Pulse'}],
    'Screen ambience': [{'effect': 'Screen Ambience'}],
    'Rainbow wave + white keys': [{'effect': 'Rainbow Wave', 'opacity': 0.6}, WHITE_FADE],
    'Typing heatmap': [{'effect': 'Typing Heatmap'}],
    'GIF: Pac-Man chase': [{'effect': 'GIF / Image', 'args': {'file': 'Pac-Man chase.gif'}}],
    'GIF: fireworks + key fade': [{'effect': 'GIF / Image', 'args': {'file': 'Fireworks.gif'}}, WHITE_FADE],
    'Scrolling text over aurora': [{'effect': 'Aurora', 'opacity': 0.35},
                                   {'effect': 'Scrolling Text', 'args': {'style': 'Color', 'color': '#ffffff'}}],
    'Black hole (follows your keys)': [{'effect': 'Black Hole', 'args': {'follow keys': 'On'}}],
    'Black hole: purple': [{'effect': 'Black Hole', 'args': {'theme': 'Purple'}}],
    'Music: center bars': [{'effect': 'Audio Center Bars'}],
    'Music: VU meter': [{'effect': 'Audio VU Meter'}],
    'Music: waterfall': [{'effect': 'Audio Waterfall'}],
    'Music: beat ripples over aurora': [{'effect': 'Aurora', 'opacity': 0.3},
                                        {'effect': 'Audio Beat Ripple', 'args': {'background': '#000000'}, 'blend': 'Add'}],
    'Music: color flow + key fade': [{'effect': 'Audio Color Flow'}, WHITE_FADE],
    'Rocket Mania: launch & fireworks': [{'effect': 'Rocket Mania: Launch & Fireworks'}],
    'Rocket Mania over Vincent Sunset': [{'effect': 'Vincent Sunset', 'opacity': 0.35},
                                         {'effect': 'Rocket Mania: Launch & Fireworks'}],
    'Battlefield 1: combat': [{'effect': 'Battlefield: Combat'}],
    'Battlefield 1: combat (demo, no game needed)': [{'effect': 'Battlefield: Combat', 'args': {'demo': 'On'}}],
    'Battlefield 1: combat, night ops': [{'effect': 'Battlefield: Combat', 'args': {'theme': 'Night ops (blue)'}}],
    'Assetto Corsa: gear & RPM': [{'effect': 'Aurora', 'opacity': 0.35}, {'effect': 'Assetto Corsa: Gear & RPM'}],
    'Rainbow keys + music on strips': [{'effect': 'Rainbow Wave', 'zone': 'Keys only'},
                                       {'effect': 'Audio Pulse', 'zone': 'Side strips only', 'blend': 'Normal'}],
    'Black hole + pulsing strips': [{'effect': 'Black Hole', 'zone': 'Keys only'},
                                    {'effect': 'Breathing', 'args': {'color': '#ff6a00'},
                                     'zone': 'Side strips only', 'blend': 'Normal'}],
    'Generic game: WASD + damage flash': [{'effect': 'Rainbow Wave', 'opacity': 0.25},
                                          {'effect': 'Gaming Keys', 'args': {'keys': 'WASD + abilities'}},
                                          {'effect': 'Damage Flash (screen)'}, WHITE_FADE],
    'Starfield + rain': [{'effect': 'Starfield'}, {'effect': 'Rain', 'blend': 'Add', 'opacity': 0.7}],
}


def normalize(layers):
    """Fill in each layer's defaults the same way the web UI does (incl. the effect's
    preferred blend mode), dropping layers whose effect doesn't exist."""
    out = []
    for layer in layers:
        cls = effects.EFFECTS.get(layer.get('effect'))
        if not cls:
            continue
        out.append({'effect': layer['effect'], 'args': dict(layer.get('args', {})),
                    'speed': layer.get('speed', 1.0), 'blend': layer.get('blend') or cls.default_blend,
                    'opacity': layer.get('opacity', 1.0), 'zone': layer.get('zone', 'All')})
    return out
