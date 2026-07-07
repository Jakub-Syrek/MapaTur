"""§3.13 — Neutralise the blue-sky cast of the baked acquisition SHADOWS in the ortho (all 8 cells), while
PRESERVING deep colour and texture. This is the ROBUST, ship-now half of the shadow problem: the full
DEM-guided de-lighting (removing the shadow DARKNESS so the render does all lighting) is parked as a research
task (docs/DELIGHT-RESEARCH.md) — it failed to lock on this data (15 m DEM coarser than the cirque shadows,
ortho mis-orthorectified so the DEM shadow is misregistered — corr(luma,N·L) even went negative — and each
cirque is a patchwork of aerial acquisitions with DIFFERENT suns, so no single global sun fits).

What THIS does instead (colour-only, per-pixel, self-gating — no fitting, no failure modes, seam-safe because
the transform is identical everywhere):
  excess = max(0, B - max(R, G))     # how much blue EXCEEDS both other channels = the sky-lit shadow cast
  B' = B - KB*excess                 # remove the blue cast
  G' = G + KG*excess                 # tint the neutralised shadow toward LIGHT GREEN (the ZBGIS/Slovak look
                                     #   the user asked for), not brown
Where blue is NOT the dominant channel (green forest, grey rock, bright snow) excess≈0 → the pixel is
untouched. Luminance is ~preserved (colour is moved B->G, not lifted), so nothing washes out. Applies to PL
(GUGiK) and SK (ZBGIS) identically.

Reads the DEEP-colour base (*.pre-colorfix.bak) so it never stacks on earlier colour ops; restore = copy
*.pre-colorfix.bak -> PNG. Run: python testdata/maps/ortho-deblue-shadow.py
"""
import os
import numpy as np
from PIL import Image

Image.MAX_IMAGE_PIXELS = None
DIR = r"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem"
COLS, ROWS = 4, 2
KB = 0.85     # fraction of the blue-over-max(R,G) excess removed
KG = 0.35     # tint the de-blued shadow toward light green (ZBGIS/Slovak look)
STRIP = 1024


def deblue(I):
    R, G, B = I[..., 0], I[..., 1], I[..., 2]
    excess = np.clip(B - np.maximum(R, G), 0.0, None)
    out = I.copy()
    out[..., 1] = G + KG * excess
    out[..., 2] = B - KB * excess
    return np.clip(out, 0, 255)


def main():
    for r in range(ROWS):
        for c in range(COLS):
            p = rf"{DIR}\tatry-ortho-r{r}-c{c}.png"
            base = p + ".pre-colorfix.bak"
            src = base if os.path.exists(base) else p
            img = Image.open(src).convert("RGB")
            W, H = img.size
            arr = np.asarray(img)
            out = np.empty_like(arr)
            for y in range(0, H, STRIP):
                y1 = min(H, y + STRIP)
                out[y:y1] = deblue(arr[y:y1].astype(np.float32)).astype(np.uint8)
            Image.fromarray(out).save(p)
            print(f"  r{r}-c{c}: de-blued from {'pre-colorfix' if src == base else 'current'} -> {os.path.basename(p)}")


if __name__ == "__main__":
    main()
