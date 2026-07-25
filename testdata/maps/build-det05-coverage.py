"""Lista POKRYCIA cel det05 dla zadanego skoku kraty (pitch) — plik `_coverage_p{pitch}.txt`.

Po co: runtime bramkuje streaming 5 cm zbiorem KLUCZY cel (`ci*100000 + cj`) wczytywanym z `_coverage.txt`.
Klucze zależą od SKOKU kraty, więc zmiana `pitchTiles` unieważnia cały plik — każda cela wypada jako
„bez pokrycia", planer żąda zera cel i warstwa 5 cm ZNIKA (zdiagnozowane 2026-07-25, dwa nieudane
podejścia do cel rozłącznych).

Cela (ci,cj) obejmuje kafle [pitch*ci, pitch*ci + coverage) × [pitch*cj, pitch*cj + coverage).
Do listy trafiają cele, w których istnieje >= próg (domyślnie 95%) kafli źródłowych.

Użycie:
  python build-det05-coverage.py <katalog-det05> --pitch 16 [--coverage 16] [--min 0.95]
Wynik: <katalog-det05>/_coverage_p16.txt  (jeden klucz na linię, jak oryginalny `_coverage.txt`)
"""
import os
import re
import sys
from collections import defaultdict


def main(root, pitch, coverage, min_frac):
    tiles = set()
    for d in os.scandir(root):
        if d.is_dir() and re.fullmatch(r"\d+", d.name):
            i = int(d.name)
            for f in os.scandir(d.path):
                m = re.fullmatch(r"(\d+)\.webp", f.name)
                if m:
                    tiles.add((i, int(m.group(1))))
    print(f"kafli na dysku: {len(tiles)}")
    if not tiles:
        return

    imin = min(t[0] for t in tiles)
    imax = max(t[0] for t in tiles)
    jmin = min(t[1] for t in tiles)
    jmax = max(t[1] for t in tiles)
    print(f"zakres kafli: i {imin}..{imax}, j {jmin}..{jmax}")

    # zlicz kafle per cela (cela moze zawierac kafle spoza swojego "wlasnego" bloku, gdy pitch < coverage)
    counts = defaultdict(int)
    for (ti, tj) in tiles:
        ci_lo = max(0, (ti - coverage + pitch) // pitch)
        ci_hi = ti // pitch
        cj_lo = max(0, (tj - coverage + pitch) // pitch)
        cj_hi = tj // pitch
        for ci in range(ci_lo, ci_hi + 1):
            if not (pitch * ci <= ti < pitch * ci + coverage):
                continue
            for cj in range(cj_lo, cj_hi + 1):
                if pitch * cj <= tj < pitch * cj + coverage:
                    counts[(ci, cj)] += 1

    need = int(coverage * coverage * min_frac)
    keys = sorted(ci * 100000 + cj for (ci, cj), n in counts.items() if n >= need)
    out = os.path.join(root, f"_coverage_p{pitch}.txt")
    with open(out, "w", encoding="ascii") as fh:
        for k in keys:
            fh.write(f"{k}\n")
    print(f"cel z pokryciem >= {min_frac:.0%} ({need}/{coverage * coverage} kafli): {len(keys)} "
          f"(z {len(counts)} dotknietych)")
    print(f"zapisano: {out}")


if __name__ == "__main__":
    root = sys.argv[1]
    def arg(n, d, cast=int):
        return cast(sys.argv[sys.argv.index(n) + 1]) if n in sys.argv else d
    main(root, arg("--pitch", 16), arg("--coverage", 16), arg("--min", 0.95, float))
