"""Disassemble a VA range of a 32-bit PE: x86dis.py <exe> <start_va> <end_va>

Annotates immediates that point at ASCII/UTF-16 strings.
"""
import re
import sys

import pefile
from capstone import CS_ARCH_X86, CS_MODE_32, Cs

pe = pefile.PE(sys.argv[1])
img = pe.get_memory_mapped_image()
base = pe.OPTIONAL_HEADER.ImageBase
start, end = int(sys.argv[2], 16), int(sys.argv[3], 16)
imports = {imp.address: '%s!%s' % (e.dll.decode(), (imp.name or b'?').decode())
           for e in pe.DIRECTORY_ENTRY_IMPORT for imp in e.imports}


def string_at(va):
    off = va - base
    if not 0 <= off < len(img):
        return None
    m = re.match(rb'[\x20-\x7e]{4,}', img[off:off + 80])
    if m:
        return repr(m.group().decode())
    m = re.match(rb'(?:[\x20-\x7e]\x00){4,}', img[off:off + 160])
    if m:
        return 'L' + repr(m.group().decode('utf-16-le'))
    return None


md = Cs(CS_ARCH_X86, CS_MODE_32)
for ins in md.disasm(img[start - base:end - base], start):
    note = ''
    for v in re.findall(r'0x[0-9a-f]+', ins.op_str):
        v = int(v, 16)
        if v in imports:
            note = imports[v]
        elif string_at(v):
            note = string_at(v)
    print('%08x  %-7s %-40s %s' % (ins.address, ins.mnemonic, ins.op_str, ('; ' + note) if note else ''))
