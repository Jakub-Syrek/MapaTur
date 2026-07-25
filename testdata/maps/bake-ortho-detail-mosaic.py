"""Bake the fetched detail tiles into a single per-level mosaic PNG + AABB json, for the render PoC.

For the additive-overlay PoC we bind ONE resident texture per level (not a tile streamer): simplest,
lowest-risk. Each mosaic is capped at 8192 px (safe GL size, power-of-two, VRAM-friendly):
  det25 : 16x512=8192 over 2048 m  -> ~0.25 m/px
  det05 : 20x512=10240 over 512 m  -> resized to 8192 -> ~0.0625 m/px (~5 cm)
Output: dem/ortho-detail/<area>/<level>_mosaic.png  and  mosaics.json (lon/lat AABB per level).
"""
import json, os
from PIL import Image
Image.MAX_IMAGE_PIXELS = None

REPO = "C:/Repos/MapaTur"
AREA = "morskie-oko"
DET = f"{REPO}/dem/ortho-detail/{AREA}"
TILE = 512
CAP = 8192

def bake(level):
    m = json.load(open(f"{DET}/{level}/manifest.json", encoding="utf-8"))
    n = m["n_tiles_side"]
    full = n * TILE
    canvas = Image.new("RGB", (full, full))
    miss = 0
    for j in range(n):
        for i in range(n):
            p = f"{DET}/{level}/{i}/{j}.webp"
            if os.path.exists(p):
                canvas.paste(Image.open(p), (i * TILE, j * TILE))
            else:
                miss += 1
    if full > CAP:
        canvas = canvas.resize((CAP, CAP), Image.Resampling.LANCZOS)
    out = f"{DET}/{level}_mosaic.png"
    canvas.save(out)
    aabb = {
        "level": level, "west": m["west"], "east": m["east"],
        "north": m["north"], "south": m["south"],
        "px": canvas.size[0], "res_m": (m["east"] - m["west"]) * 72765 / canvas.size[0],
        "file": f"{level}_mosaic.png",
    }
    print(f"[{level}] {full}->{canvas.size[0]}px, missing={miss}, ~{aabb['res_m']*100:.1f} cm/px, "
          f"AABB lon[{m['west']:.5f},{m['east']:.5f}] lat[{m['south']:.5f},{m['north']:.5f}] -> {out}")
    return aabb

if __name__ == "__main__":
    mosaics = {a["level"]: a for a in (bake("det25"), bake("det05"))}
    with open(f"{DET}/mosaics.json", "w", encoding="utf-8") as fh:
        json.dump({"area": AREA, "crs": "EPSG:4326-platecarree", "levels": mosaics}, fh, indent=2)
    print("wrote", f"{DET}/mosaics.json")
