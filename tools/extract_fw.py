"""Extract the stock K580 firmware from Redragon's recovery tool.

The recovery tool ("K580 Update Tool For Misusing Software.exe", from Redragon support)
embeds the full 64 KB image as RCDATA resource 4000, with its USB IDs and chip name
in resources 4008-4010. Nothing is executed; the .exe is only parsed.

Usage: python extract_fw.py <recovery.exe> <out.bin>
"""
import os
import struct
import sys

import pefile

RT_RCDATA = 10
EXPECTED = {4008: '0C45', 4009: '5004', 4010: 'SN32F24xB'}


def rcdata(pe):
    out = {}
    for typ in pe.DIRECTORY_ENTRY_RESOURCE.entries:
        if typ.id != RT_RCDATA:
            continue
        for res in typ.directory.entries:
            for lang in res.directory.entries:
                d = lang.data.struct
                out[res.id] = pe.get_data(d.OffsetToData, d.Size)
    return out


def main():
    pe = pefile.PE(sys.argv[1])
    res = rcdata(pe)
    for rid, want in EXPECTED.items():
        got = res.get(rid, b'').decode('utf-16-le', 'replace').rstrip('\x00')
        if got != want:
            sys.exit('resource %d is %r, expected %r: not the K580 recovery tool?' % (rid, got, want))
    fw = res[4000]
    sp, reset = struct.unpack_from('<II', fw)
    if len(fw) != 0x10000 or not 0x20000000 < sp <= 0x20002000 or not reset & 1:
        sys.exit('resource 4000 does not look like an SN32 firmware image')
    os.makedirs(os.path.dirname(os.path.abspath(sys.argv[2])), exist_ok=True)
    open(sys.argv[2], 'wb').write(fw)
    print('wrote %s (64 KB, SP=%#x, reset=%#x)' % (sys.argv[2], sp, reset))


if __name__ == '__main__':
    main()
