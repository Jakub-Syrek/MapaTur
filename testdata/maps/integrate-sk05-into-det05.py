"""Wpina zharmonizowane kafle SK (`sk-harm`) do warstwy detalu GUGiK — jedna krata, jedna warstwa runtime.

Poziomy (--level):  sk05: sk05-harm -> det05   |   sk25: sk25-harm -> det25

Dlaczego to osobne narzędzie, a nie kopiowanie ręką: niesie inwariant, którego złamanie jest ciche
i kosztowne — **przy kolizji WYGRYWA GUGiK**. Kafel z polskiego nalotu jest ostrzejszy od ZBGIS
(zmierzone dla 5 cm: HF ZBGIS ≈ 44-55% GUGiK na tej samej fakturze skalnej), więc nadpisanie go
słowackim byłoby regresją showcase'u — a zasada „nowa warstwa tylko DODAJE, nigdy nie cofa"
jest twarda (`never-regress-working-showcase`). Kolizje występują na kaflach granicznych, które
oba fetche pobrały (maska `pl` bierze >=2% PL, maska `sk` bierze <=98% PL).

Rollback: lista dopisanych kluczy trafia do `<det>/_sk-pilot-added.txt` (append, bez duplikatów) —
usunięcie tych plików przywraca stan sprzed integracji. Kafle w `sk-harm/` zostają nietknięte,
więc integrację można powtórzyć.

UWAGA na kolejność: uruchamiać PO `merge-zbgis-into-partial-det05.py` (straddlery mają swoją ścieżkę
— wchodzą przez merge pikseli w istniejący kafel, nie przez kopię) i PO harmonizacji.
Po integracji sk05 OBOWIĄZKOWO `build-det05-coverage.py <det05> --pitch 16` (klucze pokrycia zależą
od zawartości katalogu). det25 odpowiednika NIE MA — pokrycie det25/det1m bierze się z `index.bin`
pakietów `.opk`, więc następnym krokiem jest bake (OrthoBake --layer det25).

Run:
  python testdata/maps/integrate-sk05-into-det05.py --level sk25 --dry-run
  python testdata/maps/integrate-sk05-into-det05.py --level sk25 --write
"""
from __future__ import annotations

import argparse
import os
import shutil

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, "..", ".."))
TATRY = os.path.join(REPO_ROOT, "dem", "ortho-detail", "tatry")
LEVELS = {
    "sk05": {"src": "sk05-harm", "dst": "det05"},
    "sk25": {"src": "sk25-harm", "dst": "det25"},
}
SRC = DST = ADDED = None


def main() -> None:
    global SRC, DST, ADDED
    ap = argparse.ArgumentParser()
    ap.add_argument("--level", default="sk05", choices=list(LEVELS))
    ap.add_argument("--write", action="store_true")
    ap.add_argument("--dry-run", action="store_true")
    a = ap.parse_args()
    if not (a.write or a.dry_run):
        print(__doc__)
        return
    lv = LEVELS[a.level]
    SRC = os.path.join(TATRY, lv["src"])
    DST = os.path.join(TATRY, lv["dst"])
    ADDED = os.path.join(DST, "_sk-pilot-added.txt")
    print(f"poziom {a.level}: {SRC} -> {DST}")

    prev = set()
    if os.path.exists(ADDED):
        prev = set(l.strip() for l in open(ADDED, encoding="utf-8") if l.strip())
        print(f"juz odnotowanych wczesniej: {len(prev)}")

    copied = collide = already = 0
    added: list[str] = []
    for c in sorted(os.listdir(SRC)):
        if not c.isdigit():
            continue
        s_dir = os.path.join(SRC, c)
        d_dir = os.path.join(DST, c)
        for f in os.listdir(s_dir):
            if not f.endswith(".webp"):
                continue
            key = f"{c}/{f[:-5]}"
            d = os.path.join(d_dir, f)
            if os.path.exists(d):
                # kafel juz jest: albo GUGiK (kolizja -> GUGiK wygrywa), albo nasz z poprzedniego biegu
                if key in prev:
                    already += 1
                else:
                    collide += 1
                continue
            if a.write:
                os.makedirs(d_dir, exist_ok=True)
                shutil.copy2(os.path.join(s_dir, f), d)
            copied += 1
            added.append(key)

    print(f"{'SKOPIOWANE' if a.write else 'DO SKOPIOWANIA'}: {copied}")
    print(f"kolizje (det05/GUGiK zostaje): {collide}")
    print(f"juz wpiete w poprzednim biegu: {already}")
    if a.write and added:
        with open(ADDED, "a", encoding="utf-8") as fh:
            fh.write("\n".join(added) + "\n")
        print(f"lista rollbackowa -> {ADDED} (+{len(added)}, razem {len(prev) + len(added)})")
    if a.write:
        print("\nNASTEPNY KROK (OBOWIAZKOWY):")
        if a.level == "sk05":
            print(f"  python testdata/maps/build-det05-coverage.py {os.path.relpath(DST, REPO_ROOT)} --pitch 16")
        else:
            print("  sync do AppData + OrthoBake --layer det25 --det1m-out ... (pokrycie = index.bin z bake'u)")


if __name__ == "__main__":
    main()
