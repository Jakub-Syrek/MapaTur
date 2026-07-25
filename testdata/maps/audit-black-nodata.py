# Audyt nodata w kaflach det25 WebP: ile pikseli ma dokladnie RGB=(0,0,0) i jaka ma alfe.
# Uzycie: python audit-black-nodata.py <dir> <i0> <i1> <j0> <j1>
import sys
import os
from PIL import Image

d, i0, i1, j0, j1 = sys.argv[1], int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5])
rows = []
for i in range(i0, i1 + 1):
    for j in range(j0, j1 + 1):
        p = os.path.join(d, str(i), f"{j}.webp")
        if not os.path.exists(p):
            continue
        im = Image.open(p)
        has_alpha = im.mode in ("RGBA", "LA")
        im = im.convert("RGBA")
        px = im.tobytes()
        n = len(px) // 4
        black_opaque = 0
        black_transp = 0
        for k in range(0, len(px), 4):
            if px[k] == 0 and px[k + 1] == 0 and px[k + 2] == 0:
                if px[k + 3] == 0:
                    black_transp += 1
                else:
                    black_opaque += 1
        if black_opaque or black_transp:
            rows.append((i, j, im.mode, has_alpha, black_opaque, black_transp, n))

rows.sort(key=lambda r: -r[4])
print(f"kafli z czarnymi pikselami: {len(rows)}")
for i, j, mode, ha, bo, bt, n in rows[:25]:
    print(f"  {i}/{j}.webp srcmode_alpha={ha} black_opaque={bo} ({100.0*bo/n:.1f}%) black_transparent={bt}")
