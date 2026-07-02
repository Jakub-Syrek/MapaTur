"""One-off: force-bake z16 tile (36419,22455) from the LOT26 DMR5 sheet and replace the stale VOID
gugik-cache tile. This is the single border tile bake-sk-dmr5-tiles.py skipped (poland_fraction>0.005:
the WMS ortho mask claims PL coverage because the tile straddles the border ridge), while the on-disk
gugik tile (2026-06-06) is 100% void -> no .bdt baked -> AllChildrenBaked=false -> the whole z15 quad
renders coarse right on the Mieguszowieckie peaks ("rock cut in half" seam).

Reuses the EXACT sampling/transform from bake-sk-dmr5-tiles.py (same SheetIndex, same EPSG:3857->8353,
same pixel-centre grid), just without the PL-mask/coverage gates. Partial coverage is fine - the PL
border tiles in the cache are themselves ~61% void and bake correctly.
"""
import importlib.util
import os
import shutil
import sys

import numpy as np
import tifffile

spec = importlib.util.spec_from_file_location(
    "skbake", r"C:\Repos\MapaTur\testdata\maps\bake-sk-dmr5-tiles.py")
skbake = importlib.util.module_from_spec(spec)
spec.loader.exec_module = spec.loader.exec_module  # no-op, clarity
# Importing runs only defs (main() is __main__-gated).
spec.loader.exec_module(skbake)

import pyproj  # noqa: E402  (after module load, same env)

# Usage: python sk-force-bake-tile.py [tileX tileY]   (default = the Mieguszowieckie border tile)
TX, TY = (int(sys.argv[1]), int(sys.argv[2])) if len(sys.argv) >= 3 else (36419, 22455)
GUGIK = r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem-cache\gugik\16"

idx = skbake.SheetIndex(skbake.SRC_DIR)
tr = pyproj.Transformer.from_crs("EPSG:3857", "EPSG:8353", always_xy=True)

minx, miny, maxx, maxy = skbake.tile_3857_bounds(TX, TY, skbake.ZOOM)
px = (np.arange(skbake.TILE_PX, dtype=np.float64) + 0.5) / skbake.TILE_PX
mx = minx + px * (maxx - minx)
my = maxy - px * (maxy - miny)
MX, MY = np.meshgrid(mx, my)
sx, sy = tr.transform(MX, MY)
val = idx.sample(np.asarray(sx), np.asarray(sy))

cov = float(np.isfinite(val).mean())
real = val[np.isfinite(val)]
print(f"tile {TX}/{TY}: coverage={cov*100:.1f}%  "
      f"min={real.min():.1f}m max={real.max():.1f}m mean={real.mean():.1f}m" if real.size else
      f"tile {TX}/{TY}: coverage=0% - LOT26 sheet does NOT cover it, aborting")
if real.size == 0 or cov < 0.30:
    print("ABORT: not enough real data to be an improvement")
    sys.exit(1)

# Sanity: Mieguszowieckie ridge -> heights must be high-alpine (1500-2500 m), not garbage.
if not (1200.0 < float(real.min()) and float(real.max()) < 2700.0):
    print("ABORT: height range implausible for this ridge")
    sys.exit(1)

dst = os.path.join(GUGIK, str(TX), f"{TY}.tif")
bak = dst + ".void.bak"
if os.path.exists(dst) and not os.path.exists(bak):
    shutil.move(dst, bak)
    print(f"backed up stale void tile -> {bak}")

tifffile.imwrite(dst, val.astype(np.float32), compression=None, photometric="minisblack")
print(f"WROTE {dst}")

# Verify what we wrote decodes to the same stats.
chk = tifffile.imread(dst)
chk_real = chk[np.isfinite(chk)]
print(f"verify: shape={chk.shape} coverage={float(np.isfinite(chk).mean())*100:.1f}% "
      f"min={chk_real.min():.1f} max={chk_real.max():.1f}")
