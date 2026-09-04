# HANDOFF 2026-08-26 — det25 PL zderywowany z det05, ODEBRANY przez usera

## Werdykt

User (2026-08-26, po obejrzeniu w apce, PID 25268): **„jest ok, szew przy Zakopanem nie
przeszkadza"** — stan ODEBRANY, nie cofać (zasada 19). Wcześniejszy werdykt zakresu: „A pełny".

## Co zostało zrobione (pełna recepta + liczby: TILE-PRODUCTION §14)

- Zmierzony 08-25 (próbka alpejska, PLAN-ALPY §10) mleczno-niebieski nalot WMS StandardResolution
  w det25 (MO lum ~57–75 / B−R +19…+34; też Chochołowska ~30 km², Białka). Refetch martwy —
  WMS dziś serwuje ten sam rocznik (11/12 kafli identycznych).
- **13 656 kafli det25 PL** (komplet 25/25 dzieci det05, spoza listy SK) zderywowanych box 5×5
  z **zaakceptowanego det05 (AppData)** — `testdata/maps/derive-det25-from-det05.py`; 0 błędów;
  rejestracja lit-delta med −0,06 lumy. Backup `det25-prewms/` + lista `det25/_wms-derived.txt`.
- Okno APP-LOCK 16:02–16:20 (C2C-055/056): robocopy 13 657 plików/1,02 GB; bake przyrostowy
  det25 269+836 pominiętych / 66 796 stron / 0 złych / 3,1 min; det1m 87 pak.; **verify-full
  det25 BAD=0, det1m BAD=0**. Format `.opk`, indeksy, kod — bez zmian.
- Commity na `claude/quirky-morse-145976`: `beafdf0` (narzędzie + §14) i ten handoff z werdyktem.

## Świadomie zaakceptowany koszt

Szew na granicy pokrycia det05 (|dLum| med 14 / p90 50): prosta linia przez rejon Zakopanego
(lat ~49.30, lon 19.80–20.10) i zachód Chochołowskiej (lon ~19.80) — na południe czysty rocznik
2021 z det05, na północ pozostały welon WMS. User: nie przeszkadza. Derived det25 niesie też
wypalone cienie 2021 (spójne z bliskim pierścieniem 5 cm).

## Następne kroki (kolejność bez zmian, nic pilnego)

1. **Fetch det05 regionu C** (§0-A: wybór rocznika!) → ponowny `derive-det25-from-det05.py --write`
   (idempotentny: przeliczy tylko nowe komplety 25/25) → szew przesuwa się/znika + naloty WMS
   poza dzisiejszym pokryciem też się naprawią.
2. Deshadow R4 (kiedy wróci) → re-derywacja det25 jedną komendą naprawi cienie 2021 na OBU poziomach.
3. Merge gałęzi do main = razem z resztą commitów worktree (taski #8/#9 mają własne otwarte
   werdykty — nie mergować wybiórczo bez decyzji usera).

## Gdzie szukać

- Recepta + liczby: `docs/TILE-PRODUCTION.md` §14 · narzędzie: `testdata/maps/derive-det25-from-det05.py`
- Rollback: pliki z `_wms-derived.txt` ← `det25-prewms/` (repo) → robocopy → bake przyrostowy
- Memory: `det25-wms-dark-nalot-mo` · C2C: wpisy 055/056 (+ werdykt 057)
