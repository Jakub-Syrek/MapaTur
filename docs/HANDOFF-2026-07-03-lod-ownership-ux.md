# Handoff — 2026-07-03: własność powierzchni, inwarianty systemowe, doświadczenie startu

> Kontynuacja `docs/HANDOFF-2026-07-02-ux-overhaul.md` (tam pełna historia dnia poprzedniego i szczegóły
> sag; ten plik to stan wejściowy NOWEJ sesji). Czytaj razem z `docs/TERRAIN-GRAPHICS-CHECKLIST.md`
> (obowiązkowa przed dotykaniem terenu) i `docs/TILE-PRODUCTION.md` (obowiązkowa przed dotykaniem danych).

## Kontekst repo
- Branch: `feat/atmosphere-effects-toggle`, wypchnięty do origin (ostatni push: `ddb96da` + testy/handoff).
- Bramki przed KAŻDYM pushem: `dotnet format MapaTur.slnx --verify-no-changes` + pełne
  `dotnet test MapaTur.slnx` (2026-07-03 wieczór: 1614 testów green). NIGDY Claude jako autor/co-author.
- Build desktop: `dotnet build src/MapaTur.App/MapaTur.App.csproj -f net10.0-windows10.0.19041.0`
  (absolutne ścieżki — cwd shelli potrafi się rozjechać), start exe z `bin\Debug\...\win-x64\`,
  pełny load ~100 s, logi `win-x64\logs\mapatur-*.log`. Przed buildem ubić proces. `Start-Process`
  przywraca okno na SWÓJ monitor — NIE używać `open_application` (spawnuje drugą instancję, kradnie
  monitor; pamięć `no-window-stealing-computer-use`).

## Najważniejsza zmiana filozofii (feedback usera, przeczytaj zanim cokolwiek naprawisz)
User: „po co ci 2k testów jak każda twoja zmiana robi rozpierdol w innych rzeczach" oraz „zamiast pisać
testy robisz wpisy i notatki". **Lekcje przekuwa się w TESTY SYSTEMOWE w repo, nie w notatki.** Regresje
działy się na SZWACH między przetestowanymi klockami — teraz szwy mają testy-inwarianty
(`BakedTileStreamingManagerTests` sekcja SYSTEM INVARIANTS, `QuadtreeTileSelectorTests`,
`BaseCoverageMaskBuilderTests`, `PoiMergerTests`, `PeakNamerTests.RefineOnRaster*`). Nową regresję tej
klasy NAJPIERW przypnij czerwonym testem, potem naprawiaj. Diagnozy graficzne: własne zrzuty
(computer-use, pasywnie) + deterministyczne przełączniki CPU + pomiary offline przez PowerShell+reflection
na DLL-ach apki (Add-Type bin\*.dll → BakedTileAvailabilityIndex.Scan / SampleBilinear / lapRMS /
QuadtreeTileSelector.Select z ręczną kamerą) — NIE iteracje na oko przez screenshoty usera (saga
„dwa szlaki" = 6 nietrafionych prób, zanim przyszedł pomiar).

## Co jest ZROBIONE i ZWERYFIKOWANE (2026-07-03)
1. **Architektura LOD — cztery defekty, wszystkie przypięte testami:**
   - selektor: pierścienie kotwiczone metryką dwuogniskową `min(d_target, d_eye/0.4)` — detal tam gdzie
     patrzysz + bańka pod stopami; orbita w obrębie fine-ringu = identyczna selekcja;
   - stream sam dokręca fill do `resident==desired` przy nieruchomej kamerze (self-kick 150 ms);
   - stale-eviction: rezydent poza desired po 3 updatach łaski wylatuje; `StalePending` napędza pętlę;
   - **własność powierzchni**: `BaseCoverageMaskBuilder` (unia pełnych rezydentnych z16, erozja 1 texel,
     R8 na unit 8) → shader DISCARDUJE piksele bazy (per-mesh `IsBaseSkin`) — box-averaged baza leży
     0.5–4 m NAD z16 na wypukłościach i zakopywała detal („lotnisko"); cienie (CSM) mają TĘ SAMĄ maskę.
2. **Szlaki**: decal WYŁĄCZONY (`TrailDecalStrength=0` — samo-okluzja pasa = fantomowe kreski; maska żyje,
   niesie wodę); wstążka = jedyna reprezentacja; duch x-ray 0.65× szerokości z bramką GRUBOŚCI SKAŁY
   (depth-blit sceny → duch tylko przy zagłębieniu 25–60 m; bez depth-tekstury ducha nie ma).
3. **POI/etykiety**: `PoiMerger` (dedup nazwa + bliskość 80 m w obrębie typu; 124 duplikaty zwinięte);
   szczyty doprecyzowane na z16 z kotwicą w koordynacie OSM (igły: Mnich/Zadni Mnich; gęste grupy:
   Mięgusze) + bezpiecznik na złe koordy kuratorowane (>150 m poniżej publikowanej wysokości → zostaje
   zgrubny snap).
4. **Światło/materiał**: day-gain słońca (+50% w południe, złota godzina nietknięta), ambient hemisferyczny
   + hemisferyczna podłoga anty-czerń (bryły granitu czytelne w cieniu), granit chłodny szary
   `(0.44,0.46,0.49)` z kontrastem albedo ~×0.45 („jak przy pełnym śniegu" — struktura ze światła facetów,
   nie z malowanych linii). Granit v7 = zagnieżdżone Voronoi o STAŁYCH rozmiarach kraty (pamięć
   `granite-voronoi-stripe-artifact`: zmienny rozmiar w floor() = pasy!).
5. **Doświadczenie startu**: overlay (etapy 0.15 DEM → 0.3 baza → 0.7 detal → 1.0; własny tekst
   `InitialLoadStage`, zero call-to-action; gaszony w finally auto-loadu — nigdy nie więzi usera) →
   **paski orto** (TexSubImage2D ~24 MB @ 6 ms/klatkę, mipy po ostatnim pasku; POMIAR: ~42 s zamrożeń →
   2.4 s) → pigułka „Doczytywanie terenu… N/M" → budżet czasowy VBO (drain od najbliższych). Komunikaty
   odżargonizowane (PL/EN). Przycisk 📷 = tryb screenshot (chowa całe UI, zostaje tylko on).

## KOLEJKA na nową sesję (priorytety wg dokuczliwości)
1. **Hitch ~2.1 s pierwszego swapu sceny** (jedyny pozostały gap startu; nie-orto — profilować: SyncTiles
   wanted-set? pierwsze uploady VBO? OnTilesChanged?).
2. **Histereza młócki przy klamrze**: desired==cap==448, clamped=true ⇒ loaded=8/evicted=8 co ~1 s bez
   końca (churn krawędzi ringu). Rozważyć też strojenie promieni, żeby ideal ~≤ cap.
3. **FPS/draw-calle**: PassTimes sumGpu ~23 ms @ ~800 meshów (shadow 11.7!). Znany kierunek: batching/
   instancing/mniej passów; skirt „płaska ściana" przy okazji (handoff 2026-07-02).
4. **Downsample tieru orto poza wątek GL** (rzadki stall przy dalekim przelocie).
5. **Menu przegrupowanie** (rekonesans w handoffie 2026-07-02 — duplikaty kontrolek, złe kategorie;
   WYMAGA akceptacji projektu przez usera) + **tacka ikon aktywności** na górnym pasku.
6. Weryfikacja telefonu (GLES/Adreno) dla CAŁEJ pracy shaderowej z 2-3.07: bramka głębi ducha
   (depth-blit MSAA!), maska własności (discard w passie cieni), hemisferyczny ambient, day-gain,
   paski orto. Desktop ANGLE nie łapie problemów Adreno.
7. „Kiedyś" (user odłożył): z17/z18 do bliskich ujęć, AO z krzywizny DEM, mikrorelief proceduralny na z16,
   polish granitu v7.

## Twarde zasady procesu (kosztowały nas dziś nerwy — nie łamać)
- Jedna zmiana → build → apka OTWARTA (zostaje otwarta!) → werdykt usera → dalej. Push TYLKO po „pushuj".
- Computer-use: pasywne zrzuty + switch_display; kill+Start-Process OK (okno wraca na swój monitor);
  ZERO open_application/AppActivate/przenoszenia okien; user pracuje na tej maszynie.
- Komunikaty UI: żadnych instrukcji, których user nie może wykonać w danym stanie (lekcja overlay
  „wczytaj MBTiles" za blokującym spinnerem); żadnego żargonu (LOD/z16/Etap N) w tekstach dla usera.
- Overlay'e/szlaki: bias 0.09 NIE ruszać, liftów NIE podnosić, depth-testu solid-pasa NIE wyłączać;
  decal szlaków NIE przywracać bez fixu samo-okluzji (pamięć `trails-decal-not-depth-overlay`).
- Po polsku do usera; kod/commity po angielsku.
