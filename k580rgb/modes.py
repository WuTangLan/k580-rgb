"""Hardware effects built into the stock firmware (mode byte of a profile).

Capabilities follow OpenRGB's EVision driver; 0x0F and 0x11 are not covered there,
so they only expose what the firmware visibly reads. Speed 0 is fastest, 5 slowest.
"""

CUSTOM = 0x14

# value: (name, firmware name, speed, color, direction)
MODES = {
    0x06: ('Static', 'Normally on', False, True, False),
    0x05: ('Breathing', 'Breathing', True, True, False),
    0x04: ('Spectrum Cycle', 'The trial of light', True, False, False),
    0x02: ('Color Wave', 'Clouds fly', True, True, True),
    0x01: ('Color Wave (Short)', 'Go with the stream', True, True, True),
    0x03: ('Color Wheel', 'Winding paths', True, True, True),
    0x0C: ('Vertical Rainbow', 'Snow winter jasmine', True, True, True),
    0x12: ('Rainbow Circle', 'Fast and the Furious', False, False, False),
    0x0D: ('Hurricane', 'Hurricane', True, True, False),
    0x0E: ('Accumulate', 'Accumulate', True, True, False),
    0x0A: ('Starlight', 'Swift action', True, True, False),
    0x0F: ('Starlight (Slow)', 'Digital times', True, True, False),
    0x0B: ('Blooming', 'Flowers blooming', True, True, False),
    0x10: ('Visor', 'Both ways', False, True, False),
    0x11: ('Surmount', 'Surmount', True, False, False),
    0x07: ('Reactive', 'Pass without trace', True, True, False),
    0x08: ('Reactive Ripple', 'Ripple graff', True, True, False),
    0x09: ('Reactive Line', 'Fast run without trace', True, True, False),
    CUSTOM: ('Custom (per-key)', 'Coastal', False, False, False),
}

BRIGHTNESS_MAX = 5
SPEED_MAX = 5


def as_list():
    return [{'value': v, 'name': n, 'fw_name': fn, 'speed': s, 'color': c, 'direction': d}
            for v, (n, fn, s, c, d) in MODES.items()]
