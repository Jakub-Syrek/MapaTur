# HANDOFF 2026-07-25 (popołudnie) — zasięg detalu, kolor, demo F9

Pierwsza połowa dnia (szwy na granicy pokrycia, rąbek nodata) jest w
[`HANDOFF-2026-07-25-coverage-edge.md`](HANDOFF-2026-07-25-coverage-edge.md). Ten plik opisuje resztę dnia
i **to, co trzeba zrobić dalej**. Wszystko poniżej jest zmierzone; gdzie czegoś nie wiem, jest to napisane.

---

## ══ START — ZRÓB TO W TEJ KOLEJNOŚCI ══

### 1. Odbierz werdykty użytkownika (nic nie jest odebrane)

Trzy rzeczy czekają na jego oczy, bo są czysto wizualne albo dotyczą odczucia płynności:

| co | jak sprawdzić | co było mierzone |
|---|---|---|
| odświeżanie detalu po ruchu kamery | przejechać kamerą, patrzeć czy teren przed nosem się doostrza | `resident 182 / desired 140 / queue 0` → po fixie eksmisja rusza także przy głodzeniu |
| F9: panel postępu + auto-nagrywanie | nacisnąć F9 | bramka 6 s → tyknięcie 250 ms; gotowość = komplet cel, nie „cisza" |
| tonemapa | `MAPATUR_TONEMAP=0` vs brak zmiennej | patrz §3 poniżej — decyzja NALEŻY DO USERA |

### 2. Tonemapa — stress test PRZED decyzją

Zmierzone na pozie Szpiglasowego, render kontra **baza z dysku (jej własne źródło)**:

| | źródło | ACES 1.0 (dziś) | ACES 0.5 | liniowo 0.0 |
|---|---|---|---|---|
| mediana luma | 92,7 | 102,5 | 88,5 | 76,0 |
| p90 | 156,6 | 138,0 | 132,4 | **157,6** |
| p99 | 210,4 | 162,1 | 161,3 | **183,3** |
| kontrast (std) | 37,2 | 28,3 | 29,2 | **41,8** |
| nasycenie | 0,168 | 0,132 | 0,137 | **0,150** |
| piksele ≥250 | 0,00% | 0,00% | 0,00% | 0,00% |

Wniosek: **nic nie jest przepalone** — to podpis krzywej ACES (podniesione półtony, ścięte światła,
wyprany kolor), nie ekspozycji (ta jest neutralna 1.0 od 07-07). Wariant liniowy trafia w źródło niemal
dokładnie. **Połowa mocy nic nie daje** (+0,005 nasycenia) — ta krzywa się nie skaluje.

**CZEGO TEN POMIAR NIE ROZSTRZYGA:** kadr nie zawiera śniegu ani tafli w pełnym słońcu, a ACES istnieje
właśnie po to, żeby te dwie rzeczy nie zbielały. **Przed jakąkolwiek zmianą domyślnej wartości** zrób ten
sam pomiar na kadrze ze śniegiem (suwak śniegu / zima) i porównaj kolumnę „piksele ≥250". Dopiero z tymi
dwoma kompletami liczb user może wybrać. Domyślna wartość dziś **NIE ZMIENIONA** (ACES 1.0).

Narzędzia: `MAPATUR_TONEMAP=0..1` (bez rebuildu), `testdata/maps/audit-render-exposure.py`.

### 3. Detal 5 cm dla SŁOWACJI — jest dostępny, nie jest pobrany

Zmierzone dzisiaj (sonda WMS na Dolinie Temnosmreczyńskiej, `scratchpad/zbgis_native.py`):

| żądana gęstość | HF | przyrost |
|---|---|---|
| 39 cm/px | 0,390 | — |
| 19,5 cm/px | 0,881 | ×2,26 |
| 9,8 cm/px | 1,628 | ×1,85 |
| **4,9 cm/px** | **2,249** | ×1,38 |
| 2,4 cm/px | 1,353 | ×0,60 (spadek = sufit) |

⇒ **natywna rozdzielczość ZBGIS to ~5–10 cm**, porównywalnie z polskim nalotem. Zrzut potwierdza wizualnie
(pojedyncze głazy, ziarno piargu). Granica polskiego det05 wije się po **granicy państwa**
(lat 49,1859…49,2333 zależnie od długości) — GUGiK nie publikuje niczego poza PL, i to jest jedyny powód,
dla którego SK nie ma detalu.

**Co trzeba zrobić:** fetch + bake analogiczny do polskiego pipeline'u. `testdata/maps/overlay-zbgis-ortho.py`
już rozmawia z tą usługą (`https://zbgisws.skgeodesy.sk/zbgis_ortofoto_wms/service.svc/get`, LAYERS=1,
CRS:84, MaxWidth/Height 4096), ale woła ją w rozdzielczości BAZY. Kroki:
1. pobranie kafli SK w 5 cm do osobnego drzewa (wolumen rzędu polskiego det05 — dziś 343 077 kafli / 17 GB
   źródeł, 45 GB `.opk`; SK to podobny rząd — **policzyć bbox × rozdzielczość PRZED startem**),
2. bake do `.opk` tym samym CLI (`--layer det05`),
3. **NAJPIERW zmierzyć zgodność kolorystyczną z GUGiK** na pasie przygranicznym — inaczej dostaniemy szew
   PL/SK w 5 cm zamiast dzisiejszego w bazie. Narzędzie jest: `audit-ortho-layer-style-gap.py`.

Uwaga: nie znam rocznika nalotu ZBGIS ani jego zgodności z 2021 (PL). To trzeba sprawdzić, zanim się to
zszyje — patrz §„wyrównywanie stylów" niżej.

### 4. Detal w locie F9 — zrobić PREFETCH WZDŁUŻ TRASY

Podczas lotu (zmierzona prędkość **~230 m/s**) polityka rezydencji zwraca **pusty** zbiór żądanych cel
powyżej progu `fastMotionSpeedMps`, więc demo leci nad bazą. Próba zdjęcia progu (25 → 300 m/s)
**COFNIĘTA POMIAREM tego samego dnia**: 128 cel × 11 MB dociągało się przy każdym ruchu kamery i — w parze
z ograniczoną retencją puli — dało burzę alokacji: sterta 3,1 → 8-10 GB, gapy 8 → 98, user: „rwie okrutnie".

**Właściwa droga:** F9 zna waypointy z góry, więc cele należy dociągać **z wyprzedzeniem wzdłuż trasy**,
a nie gonić kamerę. Wejście: `BuildRouteFilmTimeline` / lista waypointów; polityka dostaje zbiór cel
pokrywających trasę na najbliższe N sekund zamiast pierścienia wokół bieżącej pozycji. To osobna robota,
NIE jedna stała.

### 5. Zacięcia od uploadu (osobne od GC — nie mylić)

Po ograniczeniu retencji puli gapy spadły **240 → 8**, a sterta **11,2 → 3,1 GB**. Zostały zacięcia
z `pendingUploads` rzędu setek zaraz po starcie i po dużym skoku kamery — to fizyczne wgranie ~9,4 GB do
tekstur. Lewar: **budżet uploadu na klatkę** (wolniejsze napełnianie, brak skoków). To wymiana
„szybciej ↔ płynniej", więc **zasada 19: pytać usera**, nie decydować.

---

## Stan na koniec dnia (zmierzony, na `main`)

```
det05:  192 sloty (3 tablice × 64 warstwy = 8,0 GB BC1), CELE ROZŁĄCZNE (pitch = coverage = 16),
        ring 3200 m → ~152 cele rezydentne / promień ~2,8 km
det25:  128 cel (1,4 GB), ring 5000 m → ~4,9 km
det1m:  54 pakiety rezydentne (576 MB)
budżet orto: 78% VRAM, sufit 14 GiB       pula buforów: retencja ≤512 MB
terrain 1,01 ms | sumGpu ~5 ms | gapy: mediana 212 ms, p90 313 (po fixie retencji: n=8 w 9 min)
```

Ostrość na presecie MO, rano kontra teraz (energia HF w pasach kadru):
**+78% / +106% / +47% / +59% / +53%** (od najdalszego do najbliższego pasa).

## Co zostało naprawione dziś po południu (i dlaczego to działało źle)

1. **Pozorny cap.** `Uniform4(det05ArrAabbLoc, 48, p)` — licznik uploadu uniformów **wpisany na sztywno**.
   Każde podniesienie capa powyżej 48 było fikcją: cele lądowały w VRAM i były NIEWIDOCZNE. To samo w det25
   (32). Teraz licznik wynika z rozmiaru bufora.
2. **Cele nakładkowe 2,7×.** Cela pokrywa 409,6 m, a leżała na kracie o skoku 153,6 m — wnosiła 1/7,1 swojej
   powierzchni. Nakładka była wymogiem wyboru celi PER DRAW (`CellContains`); wybór jest PER FRAGMENT od
   07-20, więc wymóg był martwy od pięciu dni.
3. **Klucze pokrycia zależne od kraty.** `_coverage.txt` trzyma klucze `ci*100000+cj` policzone dla skoku 6.
   Zmiana skoku unieważnia KAŻDY klucz → bramka odrzuca wszystkie cele → `desired 0` → warstwa 5 cm znika.
   To był powód **dwóch nieudanych podejść** do cel rozłącznych tego dnia (szukałem w shaderze i asemblerze).
   Generator: `testdata/maps/build-det05-coverage.py --pitch N`.
4. **Głodzenie żądanych cel.** Eksmisja ruszała wyłącznie przy przekroczeniu limitu (`over > 0`), więc gdy
   pula zapełniła się DOKŁADNIE do capa, nic nigdy nie było zwalniane — teren przed kamerą przestawał się
   odświeżać. Błąd spał przy capie 48 (nigdy nie wysycanym); ujawnił się przy 192.
5. **Retencja puli buforów.** `MeshBufferPool` miał limit LICZBY buforów, ale nie ich rozmiaru; łańcuch BC1
   celi to ~43 MB, więc przy 192 celach ~8,2 GB kopii CPU siedziało w puli na stałe.
6. **Trzecia tablica GPU.** 192 slotów nie mieści się w dwóch teksturach: jedna nie może przekroczyć
   **4 GiB** (32-bitowe pole rozmiaru — sufit „białych dziur" z 07-20), a 96 warstw to dokładnie 4 GiB.
   3 × 64 = 2,73 GiB na tablicę. Trzecia siedzi na **unicie 7** — jedynym wolnym; **wszystkie 16 jednostek
   fragmentu są teraz zajęte**, kolejna tekstura wymaga zwolnienia którejś (kandydat: unit 9, legacy mozaika
   det25, do kasacji w kroku 8).

## Otwarte wątki (poza listą START)

- **Wyrównywanie stylów orto** — zdiagnozowane, NIE zrobione. To **nie jest** różnica stylów: wewnątrz det25
  nie ma patchworku (skorowidz: 2024 = 34 168 kafli, 2025 = 5 671; granice kampanii to 0,2% krawędzi, a skok
  na nich nieodróżnialny od kontroli). Różnica det05↔det25 to **wypalony cień nalotu 2021**: tam gdzie obie
  warstwy są oświetlone, mają identyczną medianę (83 vs 83); gdzie różnica >15, det05 ma 52 przy det25 = 86,
  a korelacja |dL| z jasnością ciemniejszej warstwy = −0,568. Cel: dokończyć deshadow det05, **używając
  det25 jako referencji oświetlenia** (pokrywa cały masyw, radiometrycznie zgodna w miejscach oświetlonych).
  Szczegóły i skrypty: commit `5afa120`, `docs/HANDOFF-2026-07-21-ortho-deshadow-rysy.md`.
- **Blady trójkątny placek na tafli MO** — ZASTANY, nie z dzisiejszych zmian (dwa testy 1:1: znika przy killu
  warstw detalu, jest identyczny na wczorajszym shaderze). To orto malowane na lustrze wody. Kierunek:
  bramkować detal maską wody (`flatW × darkW` — NIGDY nie usuwać, §C.5 checklisty).
- **`[Mem] det25 resident 0` to MYLĄCY licznik** — dotyczy starej ścieżki compose; tablica działa (bisekcja:
  det25 zmienia 34% pikseli kadru). Poprawić log, bo dwa razy dziś wysłał mnie w złą stronę.
- **Do skasowania:** `gpu-cache` (6,8 GB przestarzałych mtgc v3), `opk/det25-prerim` + `opk/det1m-prerim`
  (8,5 GB, rollback rebake'u rąbka — po werdykcie usera).
- **Bramka `dotnet format`** jest zielona (naprawiona dziś, commit `537cbac`) — utrzymać.

## Narzędzia dodane dziś

| narzędzie | do czego |
|---|---|
| `testdata/maps/measure-coverage-edge-lines.py` | bramka liczbowa szwów (jasnych i `--dark`) na zrzucie |
| `testdata/maps/audit-ortho-nodata-rim.py` | rąbek nodata: profil, audyt bezpieczeństwa reguły, skan warstwy |
| `testdata/maps/audit-ortho-acquisition-seams.py` | skok statystyk na krawędziach kafli **z kontrolą parowaną** |
| `testdata/maps/audit-ortho-campaign-steps.py` | skok dokładnie na granicach kampanii (skorowidz GUGiK) |
| `testdata/maps/audit-ortho-layer-style-gap.py` | różnica warstw na tym samym terenie + test hipotezy cienia |
| `testdata/maps/audit-render-exposure.py` | render kontra dane źródłowe: luma, p99, kontrast, nasycenie, clip |
| `testdata/maps/build-det05-coverage.py` | lista pokrycia cel dla dowolnego skoku kraty |
| `testdata/maps/gugik-ortho-campaigns.json` | skorowidz 2312 arkuszy (rok, godło, px, obrys) |

Zmienne diagnostyczne: `MAPATUR_TONEMAP=0..1`, `MAPATUR_ORTHO_TONE=0`, `MAPATUR_ORTHO_TONE_DEBUG=1`,
`MAPATUR_F9_RECORD=0`, plus dotychczasowe (`MAPATUR_START_POSE`, `MAPATUR_KILL`, `MAPATUR_SHOT_DIR`…).

## Reguły procesu — WYNIESIONE Z BŁĘDÓW DZISIEJSZEGO DNIA

1. **Zasada 19** (`docs/ZASADY-MAPATUR.md`, ustanowiona przez usera dziś): konflikt wytycznych rozstrzyga
   WYŁĄCZNIE user. Nie wolno też cofać stanu, który user już widział i zaakceptował. Powód: wczorajszy agent
   sam cofnął 96 cel na podstawie pomiaru `terrain 18,7 ms`, który po przywróceniu **nie odtworzył się**
   (0,55 ms), a user stracił na tym dzień.
2. **Log nie jest dowodem.** Dwa razy dziś wysłał mnie w złą stronę (`resident 96` przy niewidocznej
   warstwie; `det25 resident 0` przy działającej tablicy). Werdykt = **zrzut z apki w znanym kadrze,
   porównany przed/po**.
3. **Metryka bez kontroli parowanej kłamie.** „9,8% granic kafli to szwy akwizycji" upadło, gdy ta sama
   metryka na SZTUCZNEJ krawędzi wewnątrz kafla dała 8,1% wobec 8,0%. Każdy nowy detektor artefaktu
   kalibrować na znanym A/B, ZANIM się na nim oprze wniosek.
4. **Zmiany mierzyć RAZEM, nie osobno.** Ograniczenie retencji puli i podniesienie progu prędkości były
   poprawne z osobna, a razem dały burzę alokacji.
5. **Jedna instancja apki.** Dwie naraz (2 × 8 GB VRAM na karcie 16 GB) wyglądają jak „nie odświeża się
   detal". Przed każdym runem ubić poprzednią.

## Rollbacki

| co | jak cofnąć |
|---|---|
| cały dzień | `git revert` merge'y na main albo `git checkout 5e37b20` (stan 07-24) |
| stan zaakceptowany przez usera (96 cel nakładkowych) | commit `04799f1` |
| rebake rąbka nodata | zamiana nazw `opk/det25-prerim` ↔ `opk/det25` (i det1m) |
| cele rozłączne | `Det05PitchTiles` 16 → 6 (lista `_coverage.txt` dla skoku 6 leży nietknięta) |
| tonemapa | domyślna NIE zmieniona; `MAPATUR_TONEMAP` tylko diagnostyka |

Gałąź: `perf/pano-streaming` = `main` (zmergowane i wypchnięte, 0 commitów różnicy).
Testy: **2180 zielonych** (Application 1766, Climbing 87, Domain 144, Infrastructure 154, Routing 29);
`MapaTur.Integration.Tests` jest PUSTY — zero testów, nie mylić z „przeszedł".
