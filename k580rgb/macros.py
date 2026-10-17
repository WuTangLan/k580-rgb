"""Macros and key assignments stored on the keyboard (stock firmware, no patch needed).

Keymap (cmd 0x07/0x08, flash 0xA100 + profile * 0x17A): 126 entries of 3 bytes, one per
matrix position pos = col * 6 + row:
  02 02 kk   HID key            02 01 mm   modifier bit        02 00 00   nothing
  03 lo hi   media (consumer)   05 01 nn   play macro nn       05 1A nn   REC slot nn (G1-G5)
The Fn layer (Fn + F1..F12 media, Fn + arrows, ...) is a fixed table in the firmware and
always wins while Fn is held, so remapping never touches it. The Fn key itself is never remapped.

Macros (cmd 0x09/0x0A, flash 0xAC00): u16 offsets at 0xAC10 + n * 2 point (relative to 0xAC00)
at a 4-byte header [event count lo, hi, repeat, 0] followed by 4-byte events:
  [delay lo, 0x20 | 0x80 if release | delay hi nibble, 1 = modifier / 2 = key, code or modifier bit]
The delay runs after the event, in steps of DELAY_UNIT_MS (firmware tick: 32 SysTicks).
"""
from . import keycodes, layout

KEYMAP_SIZE = 126 * 3
MACRO_BASE = 0xAC00
TABLE_OFFSET = 0x10            # index table at 0xAC10
MAX_MACROS = 48                # table stays inside the 0xAC00-0xAC7F block the factory reset clears
DATA_OFFSET = 0x80             # macro data from 0xAC80
AREA_END = 0xC800              # REC-recorded G-key macros start here
DELAY_UNIT_MS = 9.6
MAX_DELAY = 0xFFF              # 12 bits per event (~39 s)
MAX_REPEAT = 255

FN_POS = 5 * 6 + 5             # Fn key (col 5, row 5): never remapped
G_KEYS = [('G1', 100), ('G2', 88), ('G3', 99), ('G4', 93), ('G5', 87)]   # firmware table 0x810E
MEDIA_KEYS = [('Previous', 114), ('Play/Pause', 125), ('Next', 123)]
EXTRA_KEYS = G_KEYS + MEDIA_KEYS


def pos_of_led(index):
    row, col = divmod(index, 21)
    return col * 6 + row


def assignable():
    """[{pos, label, group}] for every key that can be remapped."""
    out = [{'pos': p, 'label': n, 'group': 'macro'} for n, p in G_KEYS]
    out += [{'pos': p, 'label': n, 'group': 'media'} for n, p in MEDIA_KEYS]
    for k in layout.KEYS:
        pos = pos_of_led(k.index)
        if pos != FN_POS and not k.iso:
            out.append({'pos': pos, 'label': k.label or 'Space', 'group': 'key', 'led': k.index})
    return out


VALID_POS = {a['pos'] for a in assignable()}


# -- macros -------------------------------------------------------------------------------

def _delay_units(ms):
    return max(0, min(MAX_DELAY, round(float(ms) / DELAY_UNIT_MS)))


def normalize_macro(m):
    """Validate one macro {name, events: [[down|up, usage, delay_ms]...], repeat} from the UI."""
    name = str(m.get('name', '')).strip()[:40]
    if not name:
        raise ValueError('a macro needs a name')
    events = []
    for ev in m.get('events') or []:
        action, usage, delay = ev[0], int(ev[1]), float(ev[2])
        if action not in ('down', 'up') or usage not in keycodes.NAMES:
            raise ValueError('bad event in macro "%s"' % name)
        events.append([action, usage, max(0, min(MAX_DELAY * DELAY_UNIT_MS, round(delay)))])
    if not events:
        raise ValueError('macro "%s" is empty' % name)
    return {'name': name, 'events': events, 'repeat': max(1, min(MAX_REPEAT, int(m.get('repeat', 1))))}


def balanced(events):
    """Release anything still held at the end, so a macro can never leave a key stuck."""
    held = []
    for action, usage, _ in events:
        if action == 'down' and usage not in held:
            held.append(usage)
        elif action == 'up' and usage in held:
            held.remove(usage)
    return events + [['up', u, 0] for u in reversed(held)]


def encode_event(action, usage, delay_ms):
    d = _delay_units(delay_ms)
    if keycodes.MOD_FIRST <= usage <= keycodes.MOD_LAST:
        kind, code = 1, 1 << (usage - keycodes.MOD_FIRST)
    else:
        kind, code = 2, usage
    return bytes([d & 0xFF, 0x20 | (0x80 if action == 'up' else 0) | (d >> 8), kind, code])


def compile_macros(macros):
    """Macro list -> (bytes for flash 0xAC00.., index of each macro by name)."""
    if len(macros) > MAX_MACROS:
        raise ValueError('at most %d macros' % MAX_MACROS)
    table = bytearray(DATA_OFFSET - TABLE_OFFSET)
    data = bytearray()
    index = {}
    for n, m in enumerate(macros):
        events = balanced(m['events'])
        off = DATA_OFFSET + len(data)
        table[n * 2:n * 2 + 2] = off.to_bytes(2, 'little')
        data += bytes([len(events) & 0xFF, len(events) >> 8, m.get('repeat', 1), 0])
        for ev in events:
            data += encode_event(*ev)
        index[m['name']] = n
    image = bytes(TABLE_OFFSET) + bytes(table) + bytes(data)
    if MACRO_BASE + len(image) > AREA_END:
        raise ValueError('macros too long for the keyboard (%d of %d bytes)'
                         % (len(image) - DATA_OFFSET, AREA_END - MACRO_BASE - DATA_OFFSET))
    return image, index


def macro_bytes_used(macros):
    return sum(4 + 4 * len(balanced(m['events'])) for m in macros)


# -- keymap ---------------------------------------------------------------------------------

def entry(assign, macro_index):
    """Assignment dict from the UI -> 3 keymap bytes, or None for the factory default."""
    kind = assign.get('type')
    if kind in (None, 'default'):
        return None
    if kind == 'macro':
        n = macro_index.get(assign.get('macro'))
        return None if n is None else bytes([0x05, 0x01, n])
    if kind == 'key':
        u = int(assign['code'])
        if keycodes.MOD_FIRST <= u <= keycodes.MOD_LAST:
            return bytes([0x02, 0x01, 1 << (u - keycodes.MOD_FIRST)])
        if u not in keycodes.NAMES:
            raise ValueError('unknown key code %r' % u)
        return bytes([0x02, 0x02, u])
    if kind == 'media':
        c = int(assign['code'])
        if c not in keycodes.MEDIA_NAMES:
            raise ValueError('unknown media code %r' % c)
        return bytes([0x03, c & 0xFF, c >> 8])
    if kind == 'disabled':
        return bytes([0x02, 0x00, 0x00])
    raise ValueError('unknown assignment %r' % kind)


def build_keymap(default, assignments, macro_index):
    """Factory keymap with the assignments applied (assignments: {pos: assign})."""
    km = bytearray(default)
    for pos, assign in assignments.items():
        pos = int(pos)
        if pos not in VALID_POS:
            raise ValueError('key %d can\'t be remapped' % pos)
        e = entry(assign, macro_index)
        if e is not None:
            km[pos * 3:pos * 3 + 3] = e
    return bytes(km)


def normalize_assignments(assignments, macro_names):
    out = {}
    for pos, a in (assignments or {}).items():
        pos = int(pos)
        if pos not in VALID_POS or not isinstance(a, dict) or a.get('type') in (None, 'default'):
            continue
        if a['type'] == 'macro' and a.get('macro') not in macro_names:
            continue                      # its macro was deleted
        a = {k: a[k] for k in ('type', 'macro', 'code') if k in a}
        entry(a, {n: 0 for n in macro_names})        # validates
        out[str(pos)] = a
    return out
