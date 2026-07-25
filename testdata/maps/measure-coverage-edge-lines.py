"""Bramka liczbowa dla SZWÓW na granicy pokrycia detalu — mierzy CIENKIE DŁUGIE linie na zrzucie z apki.

Po co: artefakty 1-px na granicy pokrycia (jasna „piła" z prawa tonu, czarna kropkowana linia z rąbka
nodata) są niewidoczne na podglądach downscalowanych i łatwo je uznać za naprawione „na oko". Ta metryka
daje liczbę przed/po w IDENTYCZNYCH warunkach (ta sama poza, ten sam rozmiar okna, ta sama klatka).

Metoda: ridge = luma − mediana(luma) w pionowym oknie 15 px (white top-hat wzdłuż Y; --dark odwraca znak
dla linii CIEMNYCH). Maska = ridge > próg ORAZ niska saturacja (odsiewa kolorowe szlaki). Z komponentów
spójnych liczone są TYLKO struktury cienkie i długie (≥120 px, wypełnienie bbox < 0,30) — jasny piarg
i etykiety szczytów są zwarte i wypadają.

Kalibracja (2026-07-25, poza Szpiglasowego, okno 1424×713, ostatnia klatka):
  baseline (jasna piła obecna)        LINE_PX = 1097, 7 komponentów wzdłuż granicy
  po un-premultiply punch-through     LINE_PX =  120, 0 komponentów na granicy (próg szumu)
  kill wszystkich warstw detalu       0 komponentów w tym rejonie (kontrola: to nie geometria)

Użycie:
  python measure-coverage-edge-lines.py <plik.png|katalog> [--th 25] [--dark] [--json]
Katalog ⇒ brana jest ostatnia klatka (scena dobudowana).

Zrzuty robi harness apki: MAPATUR_SHOT_DIR + MAPATUR_AUTOSHOT_SEC (patrz HANDOFF, sekcja HARNESS).
Wymaga: numpy, pillow, scipy.
"""
import glob
import json
import os
import sys

import numpy as np
from PIL import Image
from scipy import ndimage

TH = 25.0        # próg nadwyżki nad lokalną medianą
WIN = 15         # pionowe okno mediany [px]
MIN_PX = 120     # krótsze struktury to szum tekstury
MAX_FILL = 0.30  # wypełnienie bbox — powyżej to plama, nie linia
MAX_SAT = 0.30   # kolorowe (szlaki, trasy) nie są szwem


def metric(path, th=TH, dark=False):
    im = np.asarray(Image.open(path).convert("RGB")).astype(np.float32)
    lum = im[..., 0] * 0.299 + im[..., 1] * 0.587 + im[..., 2] * 0.114
    med = ndimage.median_filter(lum, size=(WIN, 1), mode="nearest")
    ridge = (med - lum) if dark else (lum - med)

    mx, mn = im.max(axis=2), im.min(axis=2)
    sat = (mx - mn) / np.maximum(mx, 1.0)
    mask = (ridge > th) & (sat < (0.45 if dark else MAX_SAT))

    lab, n = ndimage.label(mask, structure=np.ones((3, 3)))
    line_px, comps = 0, []
    for i, sl in enumerate(ndimage.find_objects(lab) if n else [], start=1):
        sub = lab[sl] == i
        npx = int(sub.sum())
        if npx < MIN_PX:
            continue
        bh, bw = sub.shape
        fill = npx / float(bh * bw)
        if fill > MAX_FILL:
            continue
        line_px += npx
        comps.append({
            "px": npx, "w": bw, "h": bh, "fill": round(fill, 3),
            "x": int(sl[1].start), "y": int(sl[0].start),
            "mean_excess": round(float(ridge[sl][sub].mean()), 1),
            "rgb": [int(v) for v in im[sl][sub].mean(axis=0)],
        })

    comps.sort(key=lambda c: -c["px"])
    return {"file": os.path.basename(path), "line_px": line_px, "n_comp": len(comps), "top": comps[:8]}


if __name__ == "__main__":
    target = sys.argv[1]
    th = float(sys.argv[sys.argv.index("--th") + 1]) if "--th" in sys.argv else TH
    dark = "--dark" in sys.argv
    files = sorted(glob.glob(os.path.join(target, "*.png")))[-1:] if os.path.isdir(target) else [target]
    out = [metric(f, th, dark) for f in files]
    if "--json" in sys.argv:
        print(json.dumps(out, indent=1))
    else:
        for r in out:
            print(f"== {r['file']}: LINE_PX={r['line_px']}  komponentów={r['n_comp']}"
                  f"{'  (CIEMNE)' if dark else ''}")
            for c in r["top"]:
                print(f"   px={c['px']:5d} bbox={c['w']:4d}x{c['h']:3d} fill={c['fill']:.2f} "
                      f"@({c['x']},{c['y']}) nadwyżka={c['mean_excess']:5.1f} rgb={c['rgb']}")
