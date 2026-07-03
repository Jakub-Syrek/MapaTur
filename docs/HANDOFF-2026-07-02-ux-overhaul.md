# Handoff — 2026-07-02: przegląd UX (ładowanie / pasek statusu / menu / Pion / paczki) — W TOKU

> Pisany RÓWNOLEGLE z pracą (na życzenie usera), żeby przejście do nowej sesji było bezbolesne.
> Aktualizuj sekcję „STAN" po każdym domkniętym kroku. Czytaj razem z: `docs/TILE-PRODUCTION.md`
> (pipeline danych kaflowych — OBOWIĄZKOWO dokumentować tam każdy proces graficzny),
> `docs/SMOOTH-SURFACE-BUG.md` (saga detalu terenu), pamięć `trails-decal-not-depth-overlay` (finalna
> architektura szlaków).

## Kontekst repo
- Branch: `feat/atmosphere-effects-toggle`, wypchnięte do origin. Ostatnie commity:
  - `7f44dc7` — silnik baked-tile (detail layer BDT2, merge PL/SK DEM, osadzanie overlay'ów na baked z16).
  - `0dd5674` — ortho: wybór najostrzejszego kompletu, bikubik, pipeline szwów nalotów + `docs/TILE-PRODUCTION.md`.
  - `36b3ab4` — szlaki: casing w decalu + X-ray ghost-pass (żleb Kulczyńskiego czytelny; user potwierdził).
- Bramki przed KAŻDYM pushem: `dotnet format MapaTur.slnx --verify-no-changes` + pełne `dotnet test MapaTur.slnx`
  (2026-07-02: 1580 testów zielonych). NIGDY Claude jako autor/co-author.
- Build desktop: `dotnet build src/MapaTur.App/MapaTur.App.csproj -f net10.0-windows10.0.19041.0` (BEZ
  `-p:Platform=x64` — pułapka stary-exe), start `bin\Debug\...\win-x64\MapaTur.App.exe`, pełny load ~100 s,
  logi `win-x64\logs\mapatur-*.log`. Przed buildem ubić proces (lock DLL-i).

## ZLECENIE BIEŻĄCE (user, 2026-07-02 po południu) — 5 punktów
1. **Profesjonalne animacje ładowania** — „teraz jest bieda", ogarnąć SYSTEMOWO (jeden spójny mechanizm,
   nie łatki per miejsce).
2. **Ikonki aktywności na pasku** — gdy coś się wczytuje (DEM/orto/szlaki/kafle/paczki/trasa), na górnym
   pasku mają być widoczne małe wskaźniki „co się dzieje".
3. **Przegrupowanie menu** — obecnie „grubo pomieszane": suwaki vs checkboxy niespójnie, duplikaty
   kontrolek, kategorie „z dupy". Zaprojektować nową, sensowną strukturę (propozycja WYMAGA akceptacji
   usera przed przebudową — decyzja produktowa).
4. **BUG: „Pion" w Widoku nie działa** — suwak przewyższenia nie robi nic. Hipoteza robocza (do
   potwierdzenia przez recon): suwak odpala legacy `StartMeshRebuild` (ścieżkę, którą baked streaming
   omija), a `BakedTileStreamingManager` dostaje `VerticalExaggeration` RAZ przy `SetUpBakedStreaming`
   (`MapPageViewModel.cs` ~3221-3241) i nigdy potem — rezydentne kafle nie są przebudowywane.
5. **Sprzątanie „paczek"** — „nie wszystkie paczki są potrzebne na tym etapie". Dwuznaczne: paczki
   offline w UI (Railway) i/lub NuGety. Recon inwentaryzuje OBA; decyzja z userem.

## STAN (aktualizować!)
- [x] Rekonesans wystrzelony: 4 równoległe agenty (menu-mapa, loading-inwentarz, Pion-trace,
      paczki-inwentarz). Wyniki wkleić/streścić tu po powrocie.
- [x] Fix Pion ZAIMPLEMENTOWANY (build 0/0, 1291 testów green; czeka na werdykt usera): przy
      `IsLodStreaming` suwak idzie przez `StartLodExaggerationRebuild` — wydzielone `BuildLodBaseTiles(...)`
      (ta sama budowa ring/uniform bazy co scena, nowe pole `lodRingBase`), po przebudowie
      `SetUpBakedStreaming(nowa)` (świeży manager → rezydenci odbudują się z nową skalą) + natychmiastowa
      publikacja bazy + `KickBakedStream()` (nowe pola `lastBakedStreamCamera/W/H` zapamiętywane na wejściu
      `StreamBakedDetailAsync`; kick zeruje debounce). Koalescencja przez istniejący `meshRebuildCoalescer`
      z replayem trailing na TĘ SAMĄ ścieżkę.
- [x] WODA v1 ZAIMPLEMENTOWANA (wtrącone zlecenie „ponaprawiaj strumyki, wodospady... i błyszczenie wody";
      build 0/0, testy 1297+124 green, shader skompilował się bez błędów; CZEKA NA WERDYKT USERA):
      * Dane: `MapaTur.Application/Waterways/` (query builder Overpass `waterway~^(river|stream)$` + nody
        `waterway=waterfall`, `IWaterwayOverpassClient`, rekordy `Waterfall`/`WaterwayFetchResult`) +
        `MapaTur.Infrastructure/Waterways/` (HTTP client + parser, lustro wzorca dróg). DI w `MauiProgram`
        (timeout 90 s). Testy: 3 query-builder + 4 parser + 3 water-field (TrailMask) — green.
      * VM: `Waterways3DOverlay`/`Waterfalls3DOverlay`, toggle `ShowWaterways` (Tryby: „Strumienie
        i wodospady"), komenda `DownloadWaterwaysForViewportAsync` → przycisk „Pobierz cieki (widok)"
        w panelu MAPA. Stringi resx PL/EN (StatusWaterwaysLoadedFormat itd.).
      * Render: ścieki wmalowane w decal RGBA (WaterColor 0x4F9ED9, priorytet -1 POD szlakami; wodospady
        = krzyżyki piany FoamColor prio 3) + RÓWNOLEGŁE pole odległości wody R8 na `TextureUnit.Texture6`
        (`uWaterMask`/`uWaterStrength`; UnpackAlignment 1→4 przy uploadzie!). Shader: mokry tint
        `vec3(0.16,0.34,0.46)` + glint Blinna pow 96 na shN — TYLKO tam gdzie pole wody, szlak krzyżujący
        strumień NIE błyszczy (dlatego osobne pole R8, nie heurystyka koloru). Zewnętrzna bramka decalu
        rozdzielona: `(uTrailStrength>0.001 || uWaterStrength>0.001)`.
      * Jeziora: glint pow 160→200 ×0.40→0.85 + nowy szeroki sheen pow 24 ×0.22 (blok debugPolyVao).
      * Cache-key `EnsureTrailMask` rozszerzony o Waterways/Waterfalls; `TrailMask.Water` (byte[]?) +
        `PaintWaterSegment` (max-combine) w `TrailMaskBuilder`; `TrailMaskInput.Build(...)` przyjmuje
        `waterways` (appendowane PIERWSZE = najniższy priorytet).
      * **PIVOT (wieczór 2026-07-02): runtime decal wody ODRZUCONY przez usera** (slideshow — rebuild SDF
        4096² na wątku GL przy churnie klucza; wygląd „niebieskie szlaki, proste równoległe odcinki").
        Woda idzie DO ORTHO: `testdata/maps/bake-waterways-into-ortho.py` + proces §3.9 TILE-PRODUCTION.md
        (próbki → wybór usera [wariant B teal] → JEDNA komórka → werdykt → rollout). WERDYKT POZYTYWNY
        („poblask jezior zajebisty, strumyczki też") → za zgodą usera rollout na pozostałe 7 komórek
        (w toku wieczorem 2026-07-02; r1-c2 NIE re-bake'ować — patrz ⚠️ w §3.9: double-paint!).
        `ShowWaterways` default OFF (decal zostaje do A/B). PRZY OKAZJI naprawione DWA realne bugi:
        (1) okno maski clampowane do `tiles[0]` umierało przy streamingu (`windowOk=false` → szlakowy
        decal też znikał w locie) — fix: okno kamery ±4 km bez clampu + diagnostyka `[TrailMask]
        rebuilt/skip`; (2) budowa maski (projekcja+SDF, ~3 s przy 560 liniach!) szła SYNCHRONICZNIE na
        wątku GL — po fixie okna każdy skok okna = 3-sek. stall („straszliwie zarywa") → PRZENIESIONA
        NA TŁO (`trailMaskBuildTask`/`BuildTrailMaskCpu`/`UploadTrailMask`: single-flight snapshot,
        stara tekstura żywa podczas budowy, GL płaci tylko 2×TexImage2D; scratch-buffery bezpieczne przez
        sekwencjonowanie land-before-kick). User potwierdził płynność. Context-loss reset + Dispose
        uzupełnione o waterMaskTex. ZNANE do sprzątnięcia: churn klucza maski (identyczne rebuildy — refy
        list republikowane przy streamingu; teraz tanie bo w tle).
- [ ] **NASTĘPNE PO WODZIE (user przypomniał 2026-07-02 wieczorem: „wciąż włączam apkę i nie wiem, co ona
      robi")**: system ładowania — serwis stanów aktywności (VM) + overlay startowego ładowania sceny
      z realnym postępem (DEM → baked → orto → szlaki; `isInitialLoading`/`loadProgress` JUŻ SĄ w VM,
      tylko niezbindowane!) + tacka ikon aktywności na górnym pasku dla każdego pobierania.
- [x] **FREEZE przy ładowaniu — ROZWIĄZANE w 2 krokach (2026-07-03 wieczór)**: (1) budżet uploadów VBO
      czasowy ~6 ms/klatkę + drain od NAJBLIŻSZYCH (3a32bf0; stary brał z ogona = najdalsze najpierw);
      (2) **upload ORTO pocięty na paski** — tekstura alokowana pusta, wypełniana TexSubImage2D ~24 MB
      z budżetem 6 ms/klatkę, mipmapy+promocja po ostatnim pasku (częściowa tekstura nigdy nie samplowana);
      staging sprzątany na tier-change/ewikcji/context-loss/dispose. POMIAR przed/po (ten sam start sceny):
      7 gapów ~42 s (w tym 12.1 s i 14.2 s) → **2 gapy: 2.1 s + 0.3 s**. Overlay startowy działa
      (0.15 DEM → 0.3 baza → 0.7 detal → 1.0 + etap słowem), pigułka „Doczytywanie terenu… N/M" przy fillu.
      ZOSTAJE (drobiazgi): (a) pojedynczy ~2.1 s hitch pierwszego swapu sceny (nie-orto; do profilowania),
      (b) CPU-downsample przy zmianie tieru odległości orto wciąż na wątku GL (rzadkie — daleki przelot),
      (c) histereza młócki loaded=8/evicted=8 przy klamrze; (d) FPS(draw-call) — PassTimes 23 ms @ 796 kafli.
- [ ] **NOWE (user 2026-07-02 ~21:30, screenshot + fotka referencyjna): OŚWIETLENIE — „jak jest słońce,
      powinno być DUŻO jaśniej" + „przepatrz cienie ponownie, żeby były zgodne ze słońcem”.** Scena w apce
      (Kościelec/Świnica) jest mroczna-szarozielona nawet w dzień; referencja (fot. Nienartowicz, Hala
      Gąsienicowa w słońcu): jasna, świetlista, żywa zieleń, wyraźne kierunkowe światło. Do przejrzenia:
      (a) ekspozycja przy słońcu — intensywność słońca / floor ambientu `lightSum` / `uCloudDark`
      (czy zachmurzenie nie dusi sceny nawet przy niskim suwaku?) / brak tone-mapu;
      (b) zgodność cieni ze słońcem — CSM vs `uLightDir` ORAZ **ZNANY LATENTNY BUG**: cloud-shadow liczy
      się w układzie render-frame zamiast absolutnym (ta sama klasa co bug śniegu, fix wzorcowy =
      `vStableWorldPos`; pamięć `snow-angle-camera-relative-frame` — cloud-shadow świadomie NIE ruszony).
      Kolejność wg usera: „potem” — po systemie ładowania/anty-freeze.
      czeka na screenshot usera (geometria czy tekstura?). Podejrzany #1: łatka §3.6 (z16 overwrite
      ciemnych nalotów, ~1.6× miększa niż z17) mogła objąć pas nad Orlą Percią (r1-c2), SK nietknięte →
      utrata mikrokontrastu skał. Weryfikacja: sonda ostrości (wariancja Laplace'a) PNG vs
      `.pre-z16patch.bak` w bbox Orlej; jeśli potwierdzone — opcje: wyłączyć łatkę z obszarów
      wysokogórskich (maska wysokości/nachylenia) albo ortho opcja 2 (pełny re-bake z z16, wtedy
      jednolita miękkość). UWAGA: PNG ma już wypieczoną WODĘ — restore z `.pre-z16patch.bak` cofnąłby
      wodę i gainy; do prób używać kopii roboczych, nie produkcji.
- [ ] **Szwy LOD przy grazing light („obcięcie nożem", Buczynowe 2026-07-02 ~22:30)**: przy `res 256/256
      (cap)` gruby kafel z13/14 sąsiaduje z z16 w kadrze; box-averaged makro-normalna grubego celuje w cień
      przy nisko wiszącym słońcu → prosty świetlny nóż na granicy kafla. MITYGACJA wdrożona: budżet
      desktop 256→448 kafli / 1280→2048 MB (platform-split `DeviceInfo`, telefon bez zmian). FIX DOCELOWY:
      (a) selektor quadtree powinien ważyć błąd NORMALNYCH × kierunek światła (grazing → wymuś finer LOD),
      (b) ew. blend detail-normali w pas graniczny grubego kafla. Powiązane: anty-freeze uploadów (większy
      budżet = dłuższa fala wypełnienia).
- [ ] Sprzątanie paczek (po decyzji usera którego znaczenia dotyczy / obu).
- [x] WODA DOMKNIĘTA I WYPCHNIĘTA (91cfe97, 2026-07-02 ~21:15): 8/8 komórek ortho z ciekami wariant B
      + maska jezior (user: „poblask jezior zajebisty, strumyczki też"; jeziora czyste po masce);
      async trail-mask (płynność potwierdzona), fix okna decali, rock 55→75° („jest ok, pushuj").
      Bramki przeszły: format (po auto-fixie FINALNEWLINE w 6 nowych plikach) + 1590 testów green.
- [x] DRUGI PUSH DNIA (d5944be, ~23:03): szlaki 0.8 m połówki + casing tylko pod ciemnymi + klamra aa
      + mipmapy maski (grube wstęgi na dystansie FIXED); granit płytowy v4 (rotacja regionalna, płyty
      4.5–9 m, rzadkie płytkie szwy, fasety) — user iterował 3×: „zbyt równoległe"→kratka→v4 (werdykt
      v4 NIEDOMKNIĘTY — sprawdzić rano); budżet rezydencji desktop 448/2 GB (platform-split) — MITYGACJA
      szwu LOD, przy widoku przez kotlinę selektor DALEJ clamped ⇒ fix docelowy w kolejce.
- [ ] Bramki + commit + push (osobno per domknięty etap, nie jeden wór).
- [x] RANO 2026-07-03: werdykty zebrane — szlaki OK; granit v4 ODRZUCONY („flizy") → iteracje v5 (Voronoi
      bryły) → v6 (miks płyt+brył, rotacja regionalna) → v7 (zagnieżdżone Voronoi: płaty 26 m z bleachem
      + bloki 5 m wzdłuż linii spadku). **ROOT CAUSE pasów z v4–v7: zmienny w przestrzeni rozmiar komórki
      + `floor(coord/size)` na absolutnych współrzędnych = indeksy komórek suną wzdłuż poziomic szumu →
      regularne faliste pasy (na ścianach poziome, bo Z dominuje). Fix = STAŁE rozmiary kraty; różnorodność
      brył daje jitter Voronoi (~2:1), pofalowanie granic daje warp ADDYTYWNY (translacja nie skaluje).**
      Werdykt usera na v7+fix: „jest niezle" — przyjęte na teraz.
- [ ] **ODLEGŁA PRZYSZŁOŚĆ (user 2026-07-03: „kiedyś do tego wrócimy jak będzie więcej czasu")**: dalszy
      polish granitu v7 — dobór parametrów pod referencje (komin Orlej Perci + ściana Granatów): proporcje
      płatów/bloków, kontrast tonów/bleach, gęstość i głębokość szczelin, wydłużenie bloków; ew. porosty
      (żółto-zielone plamy) i lepszy kolor bazowy `rockCol`. NIE wracać do zmiennych rozmiarów komórek
      (artefakt pasów — patrz wyżej).
- [ ] **ODLEGŁA PRZYSZŁOŚĆ 2 (user 2026-07-03 wieczór, porównanie z fotą komina Mniszka: „na razie jest ok,
      mamy większe bugi, wrócimy do z17/18 kiedyś")** — sufit wierności blisko ściany; składniki i opcje wg
      dźwigni/koszt: (a) **AO z krzywizny DEM** (ciemne żleby/szczeliny — największa głębia najtaniej, bez
      nowych danych); (b) **proceduralny mikrorelief/detail-normal na z16** (struktury <1.5 m — de facto
      dokończenie granitu v7 jako normal); (c) **bake z17/z18 do bliskich ujęć** (prawdziwe sub-metrowe
      krawędzie; 4–16× danych, nowy poziom pipeline w TILE-PRODUCTION — duża decyzja). Kontekst: baked z16
      = siatka ~1.5 m (kafel 256²/~400 m), więc żyletki/kolumnowe spękania z foty NIE istnieją w geometrii.
- [x] SAGA „DWA SZLAKI" (2026-07-03, ~6 iteracji): przerywany bliźniak przy każdej linii = DWA źródła:
      (a) DECAL szlaków — samo-okluzja pasa na szorstkim reliefie pod skosem (dowód: bisekcja magenta + A/B
      z decalGate na CPU, własne zrzuty przez computer-use) → **decal WYŁĄCZONY** (`TrailDecalStrength=0`;
      maska żyje — niesie wodę); (b) X-ray ghost rysował szlaki ZA CAŁYM masywem → usunięty i przywrócony
      na życzenie usera jako v2: **0.65× szerokości + bramka GRUBOŚCI SKAŁY** (depth-blit sceny →
      `EnsureGhostDepthTarget`, duch tylko przy zagłębieniu <25–60 m; bez depth-tekstury = brak ducha).
      Werdykt usera: „problem szlaków chyba ok". Przy okazji: Honoratka — duplikat „Zmarzła Przełączka
      Wyżnia" tłumiony też z POBRANYCH POI (`TatraPasses.SuppressedOsmNames` + filtr w `EffectivePois`
      + log diagnostyczny; kuratorowany wpis -175 usunięty; testy 15/15).
      LEKCJA PROCESU: grafika = diagnozuj WŁASNYMI zrzutami (computer-use) + deterministycznymi
      przełącznikami CPU, nie iteracjami fade'ów w shaderze na werdyktach z drugiej ręki.
- [x] **„LOTNISKO" obok ostrej grani ROZWIĄZANE (2026-07-03, user: „grań wróciła”) — TRZY przyczyny,
      wszystkie zmierzone przed fixem:**
      (1) selektor kotwiczył pierścienie detalu POD KAMERĄ — patrząc na grań przez dolinę, cel był poza
      drobnym ringiem → **kotwica = grunt pod `camera.Target`** (3222c2e; orbit = identyczna selekcja;
      ta sama lekcja co okno maski szlaków);
      (2) stream ładował TYLKO przy ruchu kamery — stojąc w miejscu fill zamarzał w połowie (log: utknął
      296/448) → **self-kick co 150 ms aż resident==desired**, stop przy braku postępu (6f73300);
      (3) box-averaged BAZA leży 0.5–4 m PONAD powierzchnią z16 na WYPUKŁYCH stokach (zmierzone offline:
      kopuła nad Roztoką z13−z16=+0.5..+3.8 m) → zawsze-rysowana baza depth-testem ZAKOPYWAŁA gotowe meshe
      z16 („obły pagórek” = wypukłość) → **culling bazy ON, okludery WYŁĄCZNIE bezdziurowe z16**
      (regresja szwów spod grubych kafli nie wraca, bo grube nie cullują) (6f73300).
      METODA: pomiary offline przez PowerShell+reflection na DLL-ach apki (BakedTileAvailabilityIndex.Scan,
      SampleBilinear, lapRMS, selekcja QuadtreeTileSelector z ręczną kamerą) — NIE zgadywanie z obrazka.
      DOMKNIĘCIE (2026-07-03 wieczór, po feedbacku „po co ci 2k testów jak każda zmiana robi rozpierdol"):
      lekcje przekute w TESTY SYSTEMOWE zamiast notatek — `Invariant_StillCamera_ConvergesToFullDesiredSet`,
      `Invariant_FocusJump_EvictsEveryStaleResident` (był CZERWONY: rezydenci spoza desired siedzieli pod
      capem NA ZAWSZE — teza usera „eviction zjebany" potwierdzona; fix = stale-eviction z 3-updatową łaską
      + `StalePending` napędza pętlę aż poczekalnia pusta), `LookingAround_KeepsUnderfootDetail` (bańka oka
      0.4× wokół kamery — metryka dwuogniskowa min(d_target, d_eye/0.4)), `LookAtFocus...`. Zakopywanie
      z16 pod skorupą bazy rozwiązane WŁAŚCICIELEM POWIERZCHNI: `BaseCoverageMaskBuilder` (unia pełnych
      rezydentnych z16, erozja 1 texel = konserwatywnie; 4 testy) → R8 na unit 8 → shader DISCARDUJE
      piksele BAZY (per-mesh `IsBaseSkin`) w masce. Per-kafel culling bazy wyłączony jako nadzedowany
      (culled 0–1/340 w praktyce). Weryfikacja własna: Koszysta/Kopka (wypukłe) z fakturą, res śledzi
      desired (368/367). Potem: system ładowania + anty-freeze.
- [ ] **SZEW LOD rozszerzony (23:15, screenshot Czarny Mniszek cam 0.2 km): ODSŁONIĘTY SKIRT = „prostokątna
      płaska ściana" w kadrze** („błąd geometrii, tak nie ma w realu — chuj z tym, czym jest pokryta").
      Skirt (BakedTileSkirtDepthMeters 6–96 m) widoczny jako wielka pionowa płaszczyzna z rozsmarowanym
      pionowo orto (faliste „słoje" = wtórne). Do fixu razem ze szwem: (a) PRIORYTET near-field w planie
      residency/ładowania — najbliższe kafle wypełniać PRZED szerokością, nigdy nie odsłaniać skirtu przy
      kamerze; (b) skirt renderować dyskretnie (ciemny/bez rozciągniętego orto); (c) pytanie do usera
      OTWARTE: ściana znika po dociągnięciu streamu (transient) czy trwała (clamp)? — ustala wagę (a) vs (b).
      Poboczne z tej samej sceny: sharpen orto powinien GASNĄĆ przy magnifikacji (texele>piksel → ringing
      „słojów" też na legalnej geometrii; fade smoothstep(0.4,1.0, texels-per-pixel) przy :400).
- [ ] **AKTYWNE WYŁADOWYWANIE dalekiego detalu (user 23:25: „miały się wyładowywać detale z odległych
      szczytów, a wciąż widzę rzeczkę 10 km z detalem")**: po podbiciu budżetu do 448 eviction (farthest-
      first POD PRESJĄ budżetu) przestał działać w praktyce (`evicted=0`) — daleki z16 zostaje rezydentny.
      Fix w tym samym pakiecie co selektor: (a) desire z DYSTANSOWYM sufitem zoomu (szczyt 10 km od
      kamery NIGDY nie chce z16 — SSE per odległość), (b) aktywna eviction rezydentów, których desired
      już nie zawiera (nie czekać na presję budżetu), (c) to też walka o FPS — 669-758 kafli @ 13 FPS to
      draw-calle (znany temat FPS(draw-call)). UWAGA odróżniać: wstążki wody w ORTO (bake, tekstura) są
      widoczne z każdej odległości i to jest OK — „wyładowanie" dotyczy geometrii z16.

## Wyniki rekonesansu (wklejać streszczenia z file:line)
- Menu-mapa: GOTOWE. Panele w `MapPage.xaml`: top bar :88-181 (chipy → `SelectSectionCommand` 1-6) +
  stały pasek lokalizacji :186-249. TRASA :262-368; TRYBY :371-558 (worek ~26 kontrolek: Switches
  Ortho/Skały/Biomy/Nachylenie/Szlaki/Drogi/Eksponowane/Sauron/Orły/Atmosfera + chipy kolorów szlaków +
  chipy WARSTWY + slider zasięgu nazw + 7 chipów POI); WIDOK :600-616 (TYLKO 3: **suwak „Pion"
  :605-610 → `VerticalExaggeration`, TwoWay, 1-5**, reset kamery, fly-through); MAPA :619-704 (regiony,
  teren 1m, **„📦 Pobierz paczki danych (serwer)" :675-681 → `OnDownloadDataPackagesTapped`**, pobierz-dla-
  widoku, ZDUBLOWANY blok TRASA); POGODA :561-597 (5 sliderów Czas/Chmury/Wiatr/Śnieg/Burza);
  USTAWIENIA :707-799 (język, jakość segmented, cache, DEBUG 3 switche).
  **Duplikaty:** (1) `ShowTrails` 2× W TYM SAMYM panelu Tryby — Switch :405 + chip :476; (2)
  `ClearRouteCommand` Trasa :360 + Mapa :697; (3) `ExportRouteCommand` Trasa :362 + Mapa :699; (4)
  wyszukiwarka `PlaceQuery`/`PlaceResults` pasek :227/:230 + Trasa :280/:284.
  **Złe kategorie:** „Efekty atmosfery" (perf/bateria) w Trybach zamiast przy JAKOŚCI w Ustawieniach;
  „Niebo nocne" w Trybach a czas doby w Pogodzie; warstwy raz Switch raz chip (niespójnie); Widok 3
  kontrolki vs Tryby ~26; pobieranie danych rozbite Mapa vs czyszczenie cache w Ustawieniach.
- Loading-inwentarz: GOTOWE. Jedyny wskaźnik = 16×16 `ActivityIndicator` w dolnej pigułce statusu
  (`MapPage.xaml:806-815`, `IsStatusPillVisible`/`StatusMessage`, linger 5 s, VM :141-201). **Startup/scena:
  NIC** — `isInitialLoading` (VM :216) i `loadProgress` (VM :220, ustawiane :3010/:3076/:3192) NIGDY nie
  zbindowane w XAML (overlay z doc-commentów nie istnieje); Terrain3DView maluje tylko błękit
  (`Terrain3DView.xaml.cs:1834`). Wszystkie długie aktywności dzielą jeden globalny `IsBusy` (VM :132);
  transfery mają % w tekście pigułki (paczki :2949-2952, teren 1m :2797-2800, offline :2875-2879).
  **Bez ŻADNEJ flagi**: `lodDetailLoading` (private, VM :3537), BakedTileStreamingManager (żadnych
  eventów/statusu), autoload startowy, dekod/upload ortho. Niewykorzystane: styl `ProgressBar`
  (`Styles.xaml:212`, zero użyć). Top bar = `Border` GlassBar :88 + `Grid` 6 kolumn :89 — tacka ikon
  wchodzi jako dodatkowa kolumna/sąsiad; bar chowany przez `ChromeVisible` (VM :235). Animacje: tylko
  mikrointerakcja otwarcia panelu (`MapPage.xaml.cs:257-280`, FadeTo/TranslateTo CubicOut); brak Lottie.
- Pion-trace: GOTOWE, hipoteza POTWIERDZONA. Slider `MapPage.xaml:607-608` → `VerticalExaggeration`
  (TwoWay, 1-5) → `OnVerticalExaggerationChanged` (VM :915-932) → legacy `StartMeshRebuild` (:934-968,
  `BuildTiles` całego rastra, grid 1×1, bez baked detalu) → następny publish streamu
  (`StreamBakedDetailAsync` :3326-3340) NADPISUJE `TerrainTiles` STARYMI `lodBaseTiles` + rezydentami
  zbudowanymi z exaggeration złapanym RAZ w `SetUpBakedStreaming` (:3180→:3225-3228; manager trzyma je
  na zawsze, `BakedTileStreamingManager.cs:51,138`; Z wpieczone w wierzchołki `TerrainMesh3D.cs:867`).
  Renderer nie ma uniformu exaggeration (czyta tylko do warstwic/biomów/odbicia). **Fix-point:**
  `OnVerticalExaggerationChanged` przy `bakedStreamActive` → przebudować `lodBaseTiles`
  (`BuildAdaptiveTiles` z nowymi opcjami) + ponowne `SetUpBakedStreaming(nowa)` (drop rezydentów →
  odbudowa z nową skalą) + wymusić republish; z debounce (przeciąganie suwaka = seria zmian).
- Paczki-inwentarz: GOTOWE. (A) UI: jeden przycisk „📦 Pobierz paczki danych" (Mapa, `MapPage.xaml:675-681`
  → `DownloadDataPackagesAsync` VM :2920-2970) — **bez pickera**, ciągnie WSZYSTKO z manifestu serwera
  (lista NIE jest w apce; `PackageCatalog.Merge`); serwer = dev Railway hardcode (`MauiProgram.cs:277`,
  env `MAPATUR_PACKAGES_BASEURL` :279). Nakładające się pobierania w tym samym panelu: „Wczytaj teren 1m",
  „Pobierz offline (Tatry)" (osobny mechanizm!), 4×pobierz-dla-widoku. Chipy regionów
  Beskidy/Pieniny/Bieszczady = TYLKO filtry szlaków, zero danych za nimi (placeholdery na tym etapie).
  (B) NuGet: martwe wpisy w `Directory.Packages.props`: `Microsoft.Extensions.Hosting` :28 (zero użyć),
  `coverlet.msbuild` :53 (żaden csproj); transitive-piny OK. Ciężki kandydat dyskusyjny: `Mapsui.*`
  (83 użycia, ale wyłącznie deprecated 2D) — NIE ruszać bez decyzji usera.

## Otwarte sprawy spoza tego zlecenia (nie zgubić)
- **RAM desktopu ~16-17 GB**: duplikacja zdekodowanego ortho w cache widoku (`Terrain3DView.cachedOrthoDecoded`
  trzyma pełne 16384-owe dekody ~5.7 GB obok kopii renderera). Fix: cache widoku w rozdzielczości master
  (po `OrthoCellDownsampler`) albo w ogóle bez cache (renderer trzyma własne CPU-kopie). ~−5 GB.
- **Mobilny zestaw ortho** (`Data\maps`, 8192×4096) nie przeszedł korekcji kolorów 3.3-3.6 z
  `TILE-PRODUCTION.md` — przegenerować z poprawionych masterów (`generate-tatry-ortho-mobile.py`).
- **282 kafle z16 NoData** na skrajnym zachodzie (róg arkusza LOT26) — potrzebny sąsiedni LOT SK.
- Weryfikacja telefonu (GLES/Adreno) dla całej dzisiejszej pracy shaderowej (bikubik, casing, ghost-pass,
  hashT wrap) — desktop ANGLE nie łapie problemów Adreno (pamięć `golden-hour-effects-epic`).

## Zasady procesu (twarde, z pamięci projektu)
- Jedna zmiana → build → apka OTWARTA → werdykt usera → dalej. Nie ogłaszać sukcesów z build/logów.
- Duże/destrukcyjne ruchy (re-bake całości, push, zmiany wizualne całej mapy) — propozycja + koszt,
  czekać na „tak". Odpowiadać po polsku; kod/commity po angielsku.
- Overlay'e/szlaki: NIE ruszać bias 0.09, NIE podnosić liftów, NIE wyłączać depth-testu solid-pasa —
  pełna architektura w pamięci `trails-decal-not-depth-overlay`.
