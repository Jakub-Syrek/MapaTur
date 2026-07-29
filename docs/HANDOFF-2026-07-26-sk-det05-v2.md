# HANDOFF 2026-07-26 — SK det05 V2: rdzeń Tatr Wysokich (fetch W TOKU)

Pilot pasa przygranicznego ODEBRANY („jest ok, szew trochę razi ale z bliska") — pełny przebieg,
liczby i rollback: [`HANDOFF-2026-07-25-sk-det05-pilot.md`](HANDOFF-2026-07-25-sk-det05-pilot.md).
Recepta produkcyjna: [`TILE-PRODUCTION.md`](TILE-PRODUCTION.md) §11. Ten plik = rozszerzenie na rdzeń.

## ══ WSPÓLNY KANAŁ CLAUDE ↔ CODEX ══

**`C:\Repos\MAPATUR-AGENT-COMMS.md`** (poza repozytoriami, jak APP-LOCK). Dziennik append-only,
identyfikatory `C2C-RRRRMMDD-NNN`, potwierdzanie przez `ACK: <id>` — brak ACK NIE oznacza zgody.
**Czytaj przed każdym cyklem pracy, przed zmianą wspólnego interfejsu i przed zajęciem blokady.**
Zgłaszaj tam: gotowy merge, zmianę wspólnego formatu/interfejsu, planowane użycie `APP-LOCK.md`,
możliwe konflikty i operacje na wspólnym AppData. Sekretów i tokenów tam nie zapisujemy.

**Ustalony podział własności (C2C-20260729-001/002, przyjęty):**
- **Claude:** det05, `.opk`, coverage, streaming, helpery ortofoto, `Terrain3DView.xaml.cs` +
  `SetupDet05Streaming` + ortofotowe ścieżki shadera — do zakończenia mojego merge'u.
- **Codex:** format RMP3, baker geometrii, LOD, `SampleHybridSurface`.
- **Kolejność merge'u:** Claude kończy i merguje det05 → Codex rebase → dopiero integracja runtime RMP3.

## ══ STAŁA DECYZJA ARCHITEKTONICZNA: DWAJ AGENCI = BLOKADA APKI ══

**Ustanowione przez użytkownika 2026-07-27, obowiązuje KAŻDĄ kolejną sesję, w której nad MapaTur
pracuje więcej niż jeden agent.** Pełne brzmienie: `docs/ZASADY-MAPATUR.md` §20; wskaźnik w `AGENTS.md`
i `CLAUDE.md`; sam protokół i dziennik: **`C:\Repos\APP-LOCK.md`** (POZA repozytoriami — agenci
siedzą na różnych gałęziach i w różnych worktree'ach, więc plik w repo byłby dla nich niewidoczny).

Kontekst, który to wymusił: Codex pracuje równolegle w `C:\Repos\MapaTur-rock-material` na gałęzi
`codex/realistic-rock-material` (proceduralne skały), a Claude w `C:\Repos\MapaTur` na
`perf/pano-streaming`. Katalogi robocze są rozłączne, ale **cztery zasoby są wspólne**: proces apki,
katalog danych w AppData, RAM/VRAM i dysk.

Reguła: przed uruchomieniem apki lub bake'u czytasz STATUS → zajmujesz (kto/od/cel + wiersz
w dzienniku) → **po teście ZAMYKASZ apkę i ustawiasz WOLNE**. Nigdy nie zamykasz cudzej instancji
i nie uruchamiasz drugiej obok. Trzy zmierzone powody: bake potrzebuje ~8 GB RAM i zamkniętej apki
(2026-07-26 padł na `Unable to allocate pixels`); dwie instancje to 2×8 GB tablic det05 na karcie
16 GB i wygląda to identycznie jak „detal się nie odświeża"; podmiana kafli / `_coverage_p16.txt` /
`.opk` w trakcie cudzej sesji może sprawić, że drugi agent wczyta plik ucięty i **zniknie mu cała
warstwa 5 cm** — a będzie to diagnozował jako własny błąd.

**Aktualny stan blokady sprawdzaj WYŁĄCZNIE w `C:\Repos\APP-LOCK.md`** — ten handoff go NIE
przechowuje. (Zasada ogólna: statyczny dokument nie kopiuje stanu dynamicznego; kopia dezaktualizuje
się po minutach i wprowadza w błąd następną sesję.)

Kroki 1–6 poniżej (fetch, harmonizacja, merge straddlerów, alfa, integracja, coverage) **nie wymagają
apki** i można je prowadzić niezależnie od stanu blokady. Blokady wymaga dopiero **krok 8 (bake
+ `--verify-full`, ~40 min)** oraz krok 7 (sync do AppData — zapis do wspólnego katalogu danych).

## Decyzja zakresu — ZMIENIONA 2026-07-27 na PEŁNY ZAKRES (V1)

Użytkownik zwolnił ~470 GB („zwolniłem ci miejsce do ściągania, jedziesz z kitem") — wolne na C:
**162 → 632,9 GB**, więc jedyny powód odrzucenia V1 (dysk) zniknął. Fetch przełączony na pełny
footprint: `--bbox 19.80,49.10,20.30,49.21` (V2 zatrzymany po ~102 tys. kafli; fetcher jest
resumable, więc nic nie przepadło — pobrane kafle są pomijane).

Pozostało do pobrania ≈ **508 tys. kafli**. Workers zostaje 6: przy tej wartości pilot dał 0 błędów
na 52 tys. kafli, a to obca usługa publiczna.

**⚠ SPROSTOWANIE 2026-07-29 — nie ma żadnego throttlingu ZBGIS.** Wcześniej trzykrotnie zapisałem
tu i w raportach, że tempo „spada z 4,6 do 1,2–1,8 kafla/s przy długiej sesji, bo serwer przykręca".
**To było błędne.** Użytkownik usypia komputer na noc, więc proces po prostu stoi. Dowód — histogram
czasów zapisu kafli (próbka 80 kolumn): 22:00 → 920 kafli, 23:00 → 720, **00:00–08:59 → ZERO przez
10 godzin**, 09:00 → 560. Prawdziwe tempo jest STAŁE i wynosi **~4,4 kafla/s**, czyli tyle co na
pilocie (4,6). Wszystkie „spadki" to były średnie rozmyte o godziny snu.

**Lekcja metodyczna:** dziennik zdarzeń Windows **NIE pokazał tego snu** — `Kernel-Power` 42/107 dały
tylko 5-sekundowe mrugnięcie o 23:49, co utwierdziło mnie w błędnej tezie („maszyna nie spała").
Wiarygodnym detektorem przerwy w pracy jest **histogram `LastWriteTime` plików wyjściowych**, nie log
zasilania. Przy każdym kolejnym długim procesie liczyć ETA w GODZINACH CZUWANIA, nie w zegarowych.

### Poprzednia analiza (dla historii)

**Wybrane wtedy V2, nie V1 — bo V1 NIE MIEŚCIŁ SIĘ NA DYSKU.** Zmierzone dry-runem (metoda trafiła co do
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
2. **Harmonizacja — ⚠ ZMIERZONE 2026-07-29: TRZEBA PRZELICZYĆ CAŁOŚĆ, nie przyrostowo.**
   Przewidywana pułapka (resume-skip zostawiłby kafle pilota ze starymi parametrami) **potwierdziła
   się pomiarem**: drift wyniku transformacji na 60 losowych celach wspólnych = **mediana 2,4 lumy,
   p90 15,2, maks 38,5**; **27/60 cel powyżej 3 lumy, 12/60 powyżej 8**. Przyczyna: pas pilota miał
   tylko ~4 rzędy cel, więc większość pola parametrów w jego bounding-boksie była **interpolowana
   z sąsiadów**, a przy pełnym pokryciu jest zmierzona. Zostawienie kafli pilota dałoby patchwork
   tonalny dokładnie w pasie przygranicznym.
   ⇒ **Skasować `sk05-harm/` i puścić od zera:** `python testdata/maps/harmonize-sk05.py --workers 10`.
   662 tys. kafli przy 26 kafli/s ⇒ **~7 h czuwania**. Narzędzie pomiaru driftu: `scratchpad/harm_drift.py`
   (porównuje `_harm_params.npz` z parametrami policzonymi dla pełnego zbioru).
   **Reguła na przyszłość: po KAŻDYM poszerzeniu zakresu fetchu harmonizację liczyć od nowa** — pole
   parametrów zależy od tego, co leży na dysku, więc dokładanie kafli unieważnia poprzednie.
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
