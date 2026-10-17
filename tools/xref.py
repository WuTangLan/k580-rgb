"""Function/call-graph helpers over fw.lst.

  xref.py <fw.lst> lit <hexvalue>     functions that load a literal
  xref.py <fw.lst> callers <hexaddr>  functions that bl to addr
  xref.py <fw.lst> func <hexaddr>     print the function containing addr
"""
import re
import sys

lines = open(sys.argv[1]).read().splitlines()
rows = []
for l in lines:
    a = int(l[:5], 16)
    rows.append((a, l))

starts = sorted({a for a, l in rows if re.search(r'\bpush\s+\{.*lr', l)} |
                {int(m, 16) for _, l in rows for m in re.findall(r'\bbl\s+#0x([0-9a-f]+)', l)})


def func_of(a):
    s = 0
    for x in starts:
        if x <= a:
            s = x
        else:
            break
    return s


cmd, arg = sys.argv[2], sys.argv[3]
if cmd == 'lit':
    hits = sorted({func_of(a) for a, l in rows if ('=0x%08X' % int(arg, 16)) in l})
    print(' '.join('%05X' % h for h in hits))
elif cmd == 'callers':
    t = int(arg, 16)
    hits = sorted({func_of(a) for a, l in rows if re.search(r'\bbl\s+#0x%x\b' % t, l)})
    print(' '.join('%05X' % h for h in hits))
elif cmd == 'func':
    s = func_of(int(arg, 16))
    nxt = next((x for x in starts if x > s), 1 << 20)
    for a, l in rows:
        if s <= a < nxt:
            print(l)
