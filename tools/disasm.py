"""Linear Thumb disassembly of the K580 firmware with literal-pool annotation.

Usage: python disasm.py <fw.bin> <out.lst> [code_end_hex]
"""
import struct
import sys

from capstone import CS_ARCH_ARM, CS_MODE_THUMB, CS_MODE_MCLASS, Cs

SWITCH8 = 0x7BE2  # __ARM_common_switch8: an inline byte table follows the bl

fw = open(sys.argv[1], 'rb').read()
code_end = int(sys.argv[3], 16) if len(sys.argv) > 3 else 0x9472

md = Cs(CS_ARCH_ARM, CS_MODE_THUMB | CS_MODE_MCLASS)
md.skipdata = True

out = []


def emit(ins):
    note = ''
    # ldr rX, [pc, #imm] -> show literal value
    if ins.mnemonic == 'ldr' and '[pc' in ins.op_str:
        imm = int(ins.op_str.split('#')[-1].rstrip(']'), 0) if '#' in ins.op_str else 0
        addr = ((ins.address + 4) & ~3) + imm
        if addr + 4 <= len(fw):
            note = '  ; =0x%08X' % struct.unpack_from('<I', fw, addr)[0]
    out.append('%05X: %-10s %-8s %s%s' % (ins.address, ins.bytes.hex(), ins.mnemonic, ins.op_str, note))


def is_switch(ins):
    return ins.mnemonic == 'bl' and ins.op_str == '#0x%x' % SWITCH8


pc = 0
while pc < code_end:
    last = None
    for ins in md.disasm(fw[pc:code_end], pc):
        last = ins
        emit(ins)
        if is_switch(ins):
            break
    if last is None:
        break
    pc = last.address + last.size
    if is_switch(last):
        # helper: r3 = index; table byte[0] = n, byte[1+i] = halfword offset from table+1
        tbl = pc
        n = fw[tbl]
        targets = [tbl + 1 + 2 * fw[tbl + 1 + i] for i in range(n + 1)]
        out.append('%05X: switch8 n=%d  ' % (tbl, n) +
                   ' '.join('%d:%05X' % (i, t) for i, t in enumerate(targets[:n])) +
                   '  default:%05X' % targets[n])
        pc = (tbl + n + 2 + 1) & ~1

open(sys.argv[2], 'w').write('\n'.join(out) + '\n')
print('%d lines' % len(out))
