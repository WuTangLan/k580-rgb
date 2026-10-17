"""Physical layout of the K580's 107 LEDs.

LED index = row * 21 + col of the key matrix (firmware keymap at 0xA100, LED list at
0x8232). Positions are in key units for a full-size ANSI board. The three ISO/JIS
positions have LEDs but no key on ANSI boards, so they are marked iso=True.
"""

COLS = 21

# (label, row, col, x, y, w, h)
_KEYS = [
    ('Esc', 0, 0, 0, 0, 1, 1),
    *[('F%d' % (i + 1), 0, i + 1, x, 0, 1, 1)
      for i, x in enumerate([2, 3, 4, 5, 6.5, 7.5, 8.5, 9.5, 11, 12, 13, 14])],
    ('PrtSc', 0, 14, 15.25, 0, 1, 1), ('ScrLk', 0, 15, 16.25, 0, 1, 1), ('Pause', 0, 16, 17.25, 0, 1, 1),

    ('`', 1, 0, 0, 1.5, 1, 1),
    *[(k, 1, i + 1, i + 1, 1.5, 1, 1) for i, k in enumerate('1234567890')],
    ('-', 1, 11, 11, 1.5, 1, 1), ('=', 1, 12, 12, 1.5, 1, 1), ('Bksp', 1, 13, 13, 1.5, 2, 1),
    ('Ins', 1, 14, 15.25, 1.5, 1, 1), ('Home', 1, 15, 16.25, 1.5, 1, 1), ('PgUp', 1, 16, 17.25, 1.5, 1, 1),
    ('Num', 1, 17, 18.5, 1.5, 1, 1), ('/', 1, 18, 19.5, 1.5, 1, 1), ('*', 1, 19, 20.5, 1.5, 1, 1),
    ('-', 1, 20, 21.5, 1.5, 1, 1),

    ('Tab', 2, 0, 0, 2.5, 1.5, 1),
    *[(k, 2, i + 1, 1.5 + i, 2.5, 1, 1) for i, k in enumerate('QWERTYUIOP')],
    ('[', 2, 11, 11.5, 2.5, 1, 1), (']', 2, 12, 12.5, 2.5, 1, 1), ('\\', 2, 13, 13.5, 2.5, 1.5, 1),
    ('Del', 2, 14, 15.25, 2.5, 1, 1), ('End', 2, 15, 16.25, 2.5, 1, 1), ('PgDn', 2, 16, 17.25, 2.5, 1, 1),
    ('7', 2, 17, 18.5, 2.5, 1, 1), ('8', 2, 18, 19.5, 2.5, 1, 1), ('9', 2, 19, 20.5, 2.5, 1, 1),
    ('+', 2, 20, 21.5, 2.5, 1, 2),

    ('Caps', 3, 0, 0, 3.5, 1.75, 1),
    *[(k, 3, i + 1, 1.75 + i, 3.5, 1, 1) for i, k in enumerate('ASDFGHJKL')],
    (';', 3, 10, 10.75, 3.5, 1, 1), ("'", 3, 11, 11.75, 3.5, 1, 1), ('Enter', 3, 13, 12.75, 3.5, 2.25, 1),
    ('4', 3, 17, 18.5, 3.5, 1, 1), ('5', 3, 18, 19.5, 3.5, 1, 1), ('6', 3, 19, 20.5, 3.5, 1, 1),

    ('Shift', 4, 0, 0, 4.5, 2.25, 1),
    *[(k, 4, i + 2, 2.25 + i, 4.5, 1, 1) for i, k in enumerate('ZXCVBNM')],
    (',', 4, 9, 9.25, 4.5, 1, 1), ('.', 4, 10, 10.25, 4.5, 1, 1), ('/', 4, 11, 11.25, 4.5, 1, 1),
    ('Shift', 4, 13, 12.25, 4.5, 2.75, 1), ('↑', 4, 15, 16.25, 4.5, 1, 1),
    ('1', 4, 17, 18.5, 4.5, 1, 1), ('2', 4, 18, 19.5, 4.5, 1, 1), ('3', 4, 19, 20.5, 4.5, 1, 1),
    ('Enter', 4, 20, 21.5, 4.5, 1, 2),

    ('Ctrl', 5, 0, 0, 5.5, 1.25, 1), ('Win', 5, 1, 1.25, 5.5, 1.25, 1), ('Alt', 5, 2, 2.5, 5.5, 1.25, 1),
    ('', 5, 3, 3.75, 5.5, 6.25, 1), ('Alt', 5, 4, 10, 5.5, 1.25, 1), ('Fn', 5, 5, 11.25, 5.5, 1.25, 1),
    ('Menu', 5, 6, 12.5, 5.5, 1.25, 1), ('Ctrl', 5, 8, 13.75, 5.5, 1.25, 1),
    ('←', 5, 14, 15.25, 5.5, 1, 1), ('↓', 5, 15, 16.25, 5.5, 1, 1), ('→', 5, 16, 17.25, 5.5, 1, 1),
    ('0', 5, 18, 18.5, 5.5, 2, 1), ('.', 5, 19, 20.5, 5.5, 1, 1),
]

# LEDs present only for ISO/JIS key positions
_ISO = [('#', 3, 12), ('\\', 4, 1), ('Ro', 4, 12)]

WIDTH, HEIGHT = 22.5, 6.5


class Key:
    __slots__ = ('index', 'label', 'x', 'y', 'w', 'h', 'iso', 'side')

    def __init__(self, index, label, x, y, w, h, iso=False, side=None):
        self.index, self.label, self.x, self.y, self.w, self.h, self.iso = index, label, x, y, w, h, iso
        self.side = side          # None for keys, 'left' / 'right' for side-strip LEDs

    @property
    def cx(self):
        return self.x + self.w / 2

    @property
    def cy(self):
        return self.y + self.h / 2

    def to_dict(self):
        return {'index': self.index, 'label': self.label, 'x': self.x, 'y': self.y,
                'w': self.w, 'h': self.h, 'iso': self.iso, 'side': self.side}


# Side light strips: 7 LEDs per side, listed front (spacebar end) to back. The firmware's
# table (0x8335) runs back to front; confirmed on the keyboard with the VU meter, where the
# strips must rise together with the key rows. They sit just outside the key area so
# spatial effects flow onto them.
SIDE_LEFT = [118, 117, 116, 115, 114, 112, 13]
SIDE_RIGHT = [98, 100, 79, 20, 19, 18, 17]
SIDE_GAP, SIDE_W = 0.35, 0.4
_seg = HEIGHT / 7


def _strip(indices, x, side):
    return [Key(i, '', x, HEIGHT - (n + 1) * _seg, SIDE_W, _seg, side=side) for n, i in enumerate(indices)]


KEYS = [Key(r * COLS + c, label, x, y, w, h) for label, r, c, x, y, w, h in _KEYS]
ISO_KEYS = [Key(r * COLS + c, label, 0, 0, 0, 0, iso=True) for label, r, c in _ISO]
SIDE_KEYS = _strip(SIDE_LEFT, -SIDE_GAP - SIDE_W, 'left') + _strip(SIDE_RIGHT, WIDTH + SIDE_GAP, 'right')
BY_INDEX = {k.index: k for k in KEYS + ISO_KEYS + SIDE_KEYS}
BOUNDS = (-SIDE_GAP - SIDE_W, 0, WIDTH + SIDE_GAP + SIDE_W, HEIGHT)   # x0, y0, x1, y1 incl. strips

assert len({k.index for k in KEYS + ISO_KEYS}) == 107 and len(BY_INDEX) == 121, len(BY_INDEX)
