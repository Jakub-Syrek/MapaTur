# HANDOFF 2026-08-26 — task #8 ROZSTRZYGNIĘTY: pełzanie commit GPU = pady XAML nad SwapChainPanelem

**START następnej sesji: memory `app-unusable-route-planning-p0` + ten plik.** Pełny protokół rundy
(surowe liczby, narzędzia, dyscyplina falsyfikacji): [`dev/t8-vidmm/NOTATKI-0826.md`](../dev/t8-vidmm/NOTATKI-0826.md).

## Werdykt w jednym akapicie

Pełzanie `gpuDed` (~+400 MB/min na U, nośnik = czas przemiatania — zagadka z 08-08/08-14) to w ~90%
**powierzchnie kompozycji WinUI/XAML tworzone przez `AltitudePad` + `PanTiltPad`** — dwa pady dotykowe
nakładane na SwapChainPanel widoku GL. Dowód dwutorowy: (1) ślad ETW VidMm (event 33/34/39/371,
stackwalki) — ekranowe alokacje A8R8O8B8 ze stosem `microsoft.ui.xaml+d3d11+nvwgf2umx` **bez
libGLESv2**, żywe (Terminate w locie = 0, więc NIE deferred-destroy sterownika), w rytmie ticków
kompozycji 60 Hz; (2) behawioralnie — `MAPATUR_CHROME=0` (ukrywa dokładnie te dwa pady,
`Terrain3DView.xaml.cs:1943`) zbija nachylenie **+212 → +21,7 MB/min** i poziom **11,7→13,5 GB
na 3,9→4,4 GB** (konfund ETW wykluczony: chrome+ETW 212 / chrome bez ETW 397–424 / bez chrome 21,7).
Wyjaśnia to WSZYSTKIE regularności z poprzednich rund: nośnik czasowy ✓, głuchota na DXGI Trim ✓,
„2× upload" = korelacja pozorna ✓, łamanie mnożnika w F9 ✓. **Renderer GL jest czysty**: resztka
+21,7 MB/min = treść DXT1 far-ringu (~17,6) + własne pule (`glPoolMB` +63/min, plateau na capach).

## Pomiary (okno werdyktu uptime 300..510)

| bieg | chrome | ETW | gpuDed MB/min | poziom @90s→@504s | fps |
|---|---|---|---|---|---|
| U 08-14 (1922/1947) | on | — | +397/+424 | ~12,9→15,4 GB | ~300 |
| U 0826-1112 | on | ✓ | **+212,4** | 11,7→13,5 GB | 60 |
| U 0826-1134 | **off** | — | **+21,7** | 3,9→4,4 GB | 60 |

Rampa: ~7,3 GB różnicy poziomu materializuje się w ~30 s od startu renderowania sceny (t=30→62 s).
⚠ fps 60 w OBU dzisiejszych biegach (08-14 było ~300) — wygląda na vsync z innego powodu niż ETW;
nachylenie jest na to niewrażliwe (zmierzone 08-14), ale odnotować przy porównaniach.

## Narzędzia z tej rundy (zostają na stałe)

- `dev/t8-vidmm/vidmm.wprp` — profil WPR DxgKrnl (maska 0xC5 + stackwalki). ⚠ 8 min = ETL 8,7 GB
  (firehose MakeResident/Evict); przy powtórce bez stacków starczy węższa maska.
- `scripts/bench-t8-vidmm.ps1` — kanoniczny bieg T8U + ETW (elevated helper wpr przez flagi plikowe,
  1×UAC; apka zostaje nie-elevated). tracerpt ODPADA na dużych ETL (XML 10×, godziny).
- `dev/t8-vidmm/EtlDump/` — TraceEvent: filtr 30,4 mln→1,86 mln eventów w 12 s (tryb 1) + atrybucja
  stackami przez TraceLog (tryb `stacks`). `analyze-vidmm.py` — bilans żywych/deferred + okno werdyktu.
- Dowody: `dev/t8-vidmm/*.jsonl`, `dev/t8-draws/bench-T8U-0826-*.csv` — NIE kasować. ETL 8,7 GB
  można skasować po ew. weryfikacji (JSONL wystarcza do odtworzenia analizy).

## OTWARTE po tej rundzie

1. **Naprawa padów = DECYZJA USERA** (pady to widoczny UX). Kandydaci: (a) pady rysowane w Skii/GL
   wewnątrz powierzchni SKGLView (zero XAML nad swapchainem); (b) reuse powierzchni/BitmapCache;
   (c) auto-chowanie padów podczas lotu/orbity (plaster); (d) zgłoszenie upstream MAUI/WinUI.
   Przed wyborem: **minirepro poza MapaTur** (goły SwapChainPanel + 2 XAML-owe pady + inwalidacja
   co klatkę) rozstrzygnie, czy to ogólny wzorzec WinUI, czy własność naszych padów (przezroczystość,
   cień, binding co klatkę?).
2. Pkt 2 kolejki 08-14 (zagadka draws/klatkę 60 vs 267 U/C) — bez zmian, osobna sprawa.
3. Pkt 3 (realna szkodliwość) — po naprawie padów powtórzyć test 30–60 min: czy resztkowe
   +21,7 MB/min (treść+pule) plateau'uje. Kandydat (a) cap puli 4→8 GB — prawdopodobnie MOOT.
4. Werdykt taska #9 (zaćmienie) NADAL OTWARTY po stronie usera; push main (5+ commitów lokalnych +
   ew. commit tej rundy) po werdyktach, bramki: build 0/0, testy, format verify.

## Nie ruszać / konteksty

- Biegi porównawcze zawsze parami w identycznych warunkach (lekcja: dziś fps 60 vs 300 z 08-14).
- APP-LOCK: oba biegi za blokadą, zwolnione, zero instancji na koniec.
- sk25/det05 ODEBRANE, RMP3 ZAMROŻONE, plan Alp (`docs/PLAN-ALPY.md`) czeka ZA domknięciem #8.
