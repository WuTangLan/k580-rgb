"""USB HID key and media codes, and US-layout text -> key events."""

# (HID usage, name, browser KeyboardEvent.code)
KEYS = [
    *[(0x04 + i, c, 'Key' + c) for i, c in enumerate('ABCDEFGHIJKLMNOPQRSTUVWXYZ')],
    *[(0x1E + i, d, 'Digit' + d) for i, d in enumerate('1234567890')],
    (0x28, 'Enter', 'Enter'), (0x29, 'Esc', 'Escape'), (0x2A, 'Backspace', 'Backspace'),
    (0x2B, 'Tab', 'Tab'), (0x2C, 'Space', 'Space'), (0x2D, '-', 'Minus'), (0x2E, '=', 'Equal'),
    (0x2F, '[', 'BracketLeft'), (0x30, ']', 'BracketRight'), (0x31, '\\', 'Backslash'),
    (0x33, ';', 'Semicolon'), (0x34, "'", 'Quote'), (0x35, '`', 'Backquote'), (0x36, ',', 'Comma'),
    (0x37, '.', 'Period'), (0x38, '/', 'Slash'), (0x39, 'Caps Lock', 'CapsLock'),
    *[(0x3A + i, 'F%d' % (i + 1), 'F%d' % (i + 1)) for i in range(12)],
    (0x46, 'Print Screen', 'PrintScreen'), (0x47, 'Scroll Lock', 'ScrollLock'), (0x48, 'Pause', 'Pause'),
    (0x49, 'Insert', 'Insert'), (0x4A, 'Home', 'Home'), (0x4B, 'Page Up', 'PageUp'),
    (0x4C, 'Delete', 'Delete'), (0x4D, 'End', 'End'), (0x4E, 'Page Down', 'PageDown'),
    (0x4F, 'Right', 'ArrowRight'), (0x50, 'Left', 'ArrowLeft'), (0x51, 'Down', 'ArrowDown'),
    (0x52, 'Up', 'ArrowUp'), (0x53, 'Num Lock', 'NumLock'), (0x54, 'Num /', 'NumpadDivide'),
    (0x55, 'Num *', 'NumpadMultiply'), (0x56, 'Num -', 'NumpadSubtract'), (0x57, 'Num +', 'NumpadAdd'),
    (0x58, 'Num Enter', 'NumpadEnter'),
    *[(0x59 + i, 'Num %d' % (i + 1), 'Numpad%d' % (i + 1)) for i in range(9)],
    (0x62, 'Num 0', 'Numpad0'), (0x63, 'Num .', 'NumpadDecimal'), (0x64, 'ISO \\', 'IntlBackslash'),
    (0x65, 'Menu', 'ContextMenu'),
    *[(0x68 + i, 'F%d' % (i + 13), 'F%d' % (i + 13)) for i in range(12)],
    (0xE0, 'Left Ctrl', 'ControlLeft'), (0xE1, 'Left Shift', 'ShiftLeft'), (0xE2, 'Left Alt', 'AltLeft'),
    (0xE3, 'Left Win', 'MetaLeft'), (0xE4, 'Right Ctrl', 'ControlRight'), (0xE5, 'Right Shift', 'ShiftRight'),
    (0xE6, 'Right Alt', 'AltRight'), (0xE7, 'Right Win', 'MetaRight'),
]
NAMES = {u: n for u, n, _ in KEYS}
MOD_FIRST, MOD_LAST = 0xE0, 0xE7      # modifiers; the firmware sends them as bit 1 << (usage - 0xE0)

# Consumer-page (media) codes the keyboard can send from a remapped key
MEDIA = [
    (0xCD, 'Play / Pause'), (0xB5, 'Next track'), (0xB6, 'Previous track'), (0xB7, 'Stop'),
    (0xE2, 'Mute'), (0xE9, 'Volume up'), (0xEA, 'Volume down'),
    (0x183, 'Media player'), (0x18A, 'Mail'), (0x192, 'Calculator'), (0x194, 'File explorer'),
    (0x221, 'Search'), (0x223, 'Browser home'), (0x224, 'Browser back'), (0x225, 'Browser forward'),
    (0x227, 'Refresh'),
]
MEDIA_NAMES = dict(MEDIA)

SHIFT = 0xE1
_US = {' ': (0x2C, False), '\n': (0x28, False), '\t': (0x2B, False)}
for _u, _plain, _shifted in [(0x1E + i, d, s) for i, (d, s) in enumerate(zip('1234567890', '!@#$%^&*()'))] + [
        (0x2D, '-', '_'), (0x2E, '=', '+'), (0x2F, '[', '{'), (0x30, ']', '}'), (0x31, '\\', '|'),
        (0x33, ';', ':'), (0x34, "'", '"'), (0x35, '`', '~'), (0x36, ',', '<'), (0x37, '.', '>'), (0x38, '/', '?')]:
    _US[_plain], _US[_shifted] = (_u, False), (_u, True)
for _i, _c in enumerate('abcdefghijklmnopqrstuvwxyz'):
    _US[_c], _US[_c.upper()] = (0x04 + _i, False), (0x04 + _i, True)


def text_events(text, delay_ms=10):
    """Key events that type `text` with a US keyboard layout. Raises ValueError on other characters."""
    events = []
    for ch in text.replace('\r\n', '\n'):
        if ch not in _US:
            raise ValueError('can\'t type %r (only US-keyboard characters)' % ch)
        usage, shift = _US[ch]
        if shift:
            events.append(['down', SHIFT, 0])
        events += [['down', usage, delay_ms], ['up', usage, delay_ms]]
        if shift:
            events.append(['up', SHIFT, 0])
    return events
