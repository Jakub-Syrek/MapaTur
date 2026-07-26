# HANDOFF 2026-07-26 — SK det05 V2: rdzeń Tatr Wysokich (fetch W TOKU)

Pilot pasa przygranicznego ODEBRANY („jest ok, szew trochę razi ale z bliska") — pełny przebieg,
liczby i rollback: [`HANDOFF-2026-07-25-sk-det05-pilot.md`](HANDOFF-2026-07-25-sk-det05-pilot.md).
Recepta produkcyjna: [`TILE-PRODUCTION.md`](TILE-PRODUCTION.md) §11. Ten plik = rozszerzenie na rdzeń.

## Decyzja zakresu (user: „jedziemy dalej z resztą Tatr", 2026-07-26)

**Wybrane V2, nie V1 — bo V1 NIE MIEŚCI SIĘ NA DYSKU.** Zmierzone dry-runem (metoda trafiła co do
sztuki na pilocie: przewidziane 52 395 = pobrane 52 395):

| wariant | nowe kafle | źródła ×2 kopie | przyrost opk | dysk razem | werdykt |
|---|---|---|---|---|---|
| V2 rdzeń 19.95-20.25 × 49.12-49.20 | **270 806** | ~26 GB | ~+37 GB | **~63 GB** | ✅ zostaje ~84 GB |
| V1 całość 19.80-20.30 × 49.10-49.21 | 610 048 | ~58 GB | ~+84 GB | ~142 GB | ❌ przy 147 GB wolnego |

Kafle żyją w DWÓCH kopiach (repo `dem/...` + AppData, z której czyta runtime) — dlatego źródła liczą
się podwójnie. Rozszerzenie o Bielskie/Zachodnie później jest PRZYROSTOWE (krata globalna ⇒ większy
bbox to ścisły nadzbiór, nigdy re-fetch).

## Rocznik — sprawdzony PRZED startem (krok 0, obowiązkowy)

```
zbgis.skgeodesy.sk/zbgis/rest/services/Ortofoto/MapServer/0/query?geometry=LON,LAT&geometryType=esriGeometryPoint&inSR=4326&outFields=*&f=json
```
Stan 2026-07-26: **východ-2025 (15 cm) NIE opublikowany.** Rysy / Gierlach / Łomnica / Zelené pleso /
Bielskie = **2022-08-26 (20 cm)**; Krywań / Koprowy / Zachodnie = **2024-07-31 (15 cm)**. Granica stref
między 20.020 a 20.088°E.

**Świadome ryzyko:** jeśli východ-2025 wyjdzie po tym fetchu, rdzeń warto będzie pobrać ponownie
(~16 h + harmonizacja 3 h + przyrostowy bake). To czas, nie ryzyko — pipeline jest sprawdzony,
fetcher resumable, a stan zaakceptowany przez usera zostaje do czasu podmiany.

## Komenda (leci od 2026-07-26 ~01:0x, w tle)

```
python testdata/maps/fetch-ortho-detail.py --bbox 19.95,49.12,20.25,49.20 --level sk05 --workers 6
```
BEZ `--strip-km` (chcemy pełną głębokość, nie pas). `workers 6` jak na pilocie — dało 0 błędów na
52 395 kafli; nie podnosić, to obca usługa publiczna. ETA: pilot dał 4,6 kafla/s licząc z klasyfikacją
(w fazie aktywnej ~11/s) ⇒ **~7–16 h**. Resumable: ta sama komenda dociąga resztę.

## ══ PO FETCHU — DOKŁADNIE TA SAMA SEKWENCJA CO NA PILOCIE ══

Bramki i pułapki są opisane w handoffie pilota; tu tylko różnice skali.

1. **Walidacja:** `ok` ma się zgodzić z 270 806 (± nodata); `err=0`. `audit-ortho-blue-cast.py`.
   `_partial.txt` w sk05 to ŚNIEG/prześwietlenia, NIE braki — **nie wygaszać po bieli**.
2. **Harmonizacja:** `python testdata/maps/harmonize-sk05.py --workers 8`. Skrypt przelicza pole
   parametrów dla WSZYSTKICH cel na dysku i pomija już istniejące pliki w `sk05-harm/`
   (`if os.path.exists(dst): return "skip"`), więc przetworzy tylko nowe. Tempo pilota: 26 kafli/s
   ⇒ **~3 h** dla 271 tys. UWAGA: cele pilota dostaną PONOWNIE policzone parametry (więcej kafli
   w celi = inne statystyki) — to jest OK i pożądane, ale kafle pilota w `sk05-harm` NIE zostaną
   przeliczone (skip). Jeśli miałaby wyjść niespójność tonu na styku pilot|V2, skasować
   `sk05-harm/` w całości i puścić od nowa (~3,5 h) — **sprawdzić to pomiarem, nie zakładać**.
3. **Merge straddlerów:** `merge-zbgis-into-partial-det05.py --write` (nowe kafle SK odblokują
   kolejne kafle częściowe det05).
4. **Alfa na białym nodata:** `zero-alpha-white-nodata-det05.py --write`.
5. **Integracja:** kopia `sk05-harm` → `det05` z pominięciem kolizji (GUGiK wygrywa), dopisać do
   `_sk-pilot-added.txt`.
6. **Coverage:** `build-det05-coverage.py <det05> --pitch 16` (pilot: 1222 → 1416 cel).
7. **Sync do AppData** (robocopy) — runtime czyta AppData, nie repo.
8. **Bake:** `--layer det05 --parallel 6`, **apka UBITA** (OOM przy działającej). Pilot: 302 pakiety
   / 7,1 min; V2 to ~1000+ pakietów ⇒ **~25–40 min**. Potem `--verify-full`.
9. **Werdykt usera** na DELL P2722H: Rysy od SK, Gierlach, Łomnica, przelot F7/F9, **MO bez regresji**.

## Otwarte (bez zmian)

- **Szew z bliska** — naprawa przez deshadow strony POLSKIEJ (R4), NIE przez ruszanie SK.
  Dociąganie SK do GUGiK to ślepa uliczka odrzucona pomiarem (69% cel bez referencji GUGiK).
- **Znaki wodne GKÚ** wypalone w rastrze ZBGIS — przy V2 będzie ich ~5× więcej niż na pilocie.
  Jeśli zaczną razić: sprawdzić, czy paczki opendata/MAPKA są czyste (to byłby argument za GeoTIFF).
- **Sprzątanie 15,4 GB:** `opk/det25-prerim` + `det1m-prerim` (8,6 GB), `gpu-cache` (6,8 GB) —
  czeka na zgodę usera. Przy V2 zapas dyskowy jest wystarczający i bez tego.
- Kosmetyka: log bake'u pisze „det25 GOTOWE" niezależnie od `--layer`.
