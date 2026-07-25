# HANDOFF 2026-07-25 — granica pokrycia detalu: dwa artefakty, obie przyczyny zmierzone i naprawione

## ══ NAJWAŻNIEJSZE: czeka WERDYKT WIZUALNY USERA (DELL P2722H, skompilowany exe) ══

Zmiany są w kodzie i w danych, przetestowane liczbowo i wzrokowo przeze mnie — ale **nic nie jest
odebrane, dopóki nie obejrzy tego user** (zasada 3). Poza do odtworzenia kadru dowodowego 1:1:

```
MAPATUR_START_POSE="5674.639;-6026.549;1944.8878;1449.4592;4.0875397;0.39500022"
```

## Co było zgłoszone i co się okazało

**Zgłoszenie usera (07-24 wieczór):** „łączenia są masakryczne gdzieniegdzie" — jasna PIŁOKSZTAŁTNA linia
wzdłuż zbocza (grań Szpiglasowy Wierch / Dolina Temnosmreczyńska).

**Plan z handoffu 07-24 (feather alfy w bake, formaty v5, rebake ~74 min) OKAZAŁ SIĘ NIEPOTRZEBNY.**
Pomiar wskazał inną przyczynę. Kolejność diagnozy (każdy krok obalał hipotezę):

1. Odtworzenie kadru harnessem → linia widoczna, metryka `LINE_PX=1097`, 7 komponentów wzdłuż granicy.
2. Pomiar koloru linii: RGB 165,167,162 przy terenie 95,99,94 i niebie 158,162,169 ⇒ „to prześwit nieba
   przez szczeliny geometrii". **BŁĄD METODY** — detektor brał najjaśniejszy piksel z każdej kolumny, więc
   na skalnej teksturze zawsze coś znajdował (fałszywy pozytyw).
3. `MAPATUR_KILL=det1m,det25arr,det05arr,mosaic` → **linii NIE MA** ⇒ to nie geometria, tylko warstwy
   detalu. Metryka poprawiona: liczy tylko struktury CIENKIE i DŁUGIE (≥120 px, wypełnienie bbox < 0,30).
4. Bisekcja per warstwa: kill det05arr → piła znika (i odsłania CIEMNĄ nitkę); kill det25arr/det1m → piła
   zostaje ⇒ maluje ją **det05Array**, a ciemna nitka siedzi w warstwach pod spodem.

## Artefakt 1 — JASNA PIŁA: prawo tonu czytało CZERŃ z głębokiego mipa (commit `fa57679`)

Texel przezroczysty DXT1a dekoduje się jako **RGBA(0,0,0,0)** (`Bc1Encoder`: indeks 3 = przezroczysta
CZERŃ), a mipy alfa-ważone piszą **RGB=0** przy pustej czwórce (`BuildMipChain`/`Half`: `if (sumA == 0)`).
Każde filtrowanie przy krawędzi pokrycia rozcieńczało więc kolor czernią ∝(1−α). Skutki dwa, przyczyna
jedna: (a) ciemna nitka w wyświetlanym samplu; (b) ten sam sampel podany prawu tonu dawał `delta < 0`,
a `dc − delta·mism` **PODBIJA** jasność ⇒ jasne, kwantowane do kafli pasmo. Wzmocnienie z 07-24: `toneLod +3`
sięgnął 8× głębiej w mipy, a próg `mism` 0,16/0,35 czyni korektę binarną — stąd ostra, jasna krawędź.

**Fix jest dokładny, nie heurystyczny:** `rgb_f = Σ wᵢcᵢ` (przezroczyste wnoszą 0), `α_f = Σ_pokryte wᵢ`,
więc `rgb_f / α_f` **jest** średnią po pokrytych texelach — `unpremulPunch()`. Wewnątrz pokrycia (α=1) to
identyczność, więc zatwierdzony wygląd nie może zadryfować. Dodatkowo: `mix(base, rgb/α, α) = rgb + (1−α)·base`,
czyli poprawny **over-composite** — stary kod mnożył premultiplied kolor przez α po raz drugi.

Wpięte naraz w `applyOrthoDet1m`, `applyOrthoDet25Arr`, `applyOrthoDet05Array`, `applyOrthoDetail`
+ oba helpery bikubiczne (zwracają `vec4`; baza orto jest kryjąca i bierze `.rgb`). Referencja tonu:
`vec4` + un-premultiply + bramka pokrycia footprintu `smoothstep(0.02, 0.12, toneA)`.

**ZMIERZONE** (identyczne warunki: ten sam exe-harness, poza, okno 1424×713, klatka nr 8):
`LINE_PX 1097 → 120`, komponenty liniowe na granicy **7 → 0** (pozostałe 120 px to stała cecha terenu,
obecna także w baseline).

## Artefakt 2 — CZARNA KROPKOWANA LINIA: rąbek near-black wokół nodata (commit `241a8e3`)

§9.1 (07-24) gasił alfę tylko na **dokładnym** (0,0,0). Stratny WebP zostawia wokół kryjącej czerni GUGiK
pierścień pikseli near-black — dokładnym zerem NIE są, więc wchodziły do BC1 jako kryjący, czarny „teren".
Profil poprzeczny na pozie usera: luma **120 → 8-9 na JEDNYM wierszu**.

Zmierzone w źródłowych kaflach det25 (10 kafli granicznych): mediana lumy **1.0 / 1.3 / 8.0** w odległości
1/2/3 px od czerni (udział <16: 95,3% / 76,4% / 51,1%), od 4 px już teren (90.8), od 7 px czysto.
Kontrola — teren daleko od nodata (n=982 659 px): mediana **97.7**, udział <16 = **0,0%**.

**Fix:** `OrthoNodata.ZeroAlphaOnNodataRim` — zalew 8-spójny **OD dokładnej czerni** przez lumę ≤16.
Kryterium to SPÓJNOŚĆ, nie próg jasności: głęboki cień w środku zdjęcia nie dotyka nodata i zostaje.
Audyt bezpieczeństwa (600 kafli): **no-op na 96,5%** zbioru (579 kafli bez ani jednego czarnego piksela),
rąbek dogaszony średnio +1,19 pkt proc. pokrycia (max 4,70%), luma rąbka max 16.0.

**Rebake WYŁĄCZNIE det25+det1m, 3,1 min** — det05 nie zawiera nodata (audyt 400 z 343 077 kafli = 0 trafień),
a det1m powstaje z tego samego źródła co det25. **BEZ bumpu wersji formatu** (bump unieważniłby 45 GB det05,
które nie mają czego naprawiać). `--verify-full`: det25 **40 535/40 535** stron CRC OK, det1m **2 790/2 790** OK.
Stare pakiety leżą jako `opk/det25-prerim` i `opk/det1m-prerim` — **rollback = zamiana nazw katalogów**.

**ZMIERZONE po podmianie** (ta sama poza i okno): metryka linii CIEMNYCH **124 px → 0**, komponenty **1 → 0**.

## Stan bramek

- testy: **1766/1766** zielone w `MapaTur.Application.Tests` (w tym 7 nowych `OrthoNodataRimTests`
  i 7 `TerrainShaderPunchThroughTests`)
- shader linkuje się czysto (`glGetError clean`), ścieżka `.opk` żywa (`opk-read`, **zero `compose`**)
- `dotnet format --verify-no-changes` na `MapaTur.Application` **czerwony ZASTANE** — te same błędy
  ENDOFLINE w plikach, których nie dotykałem (`OrthoPagePack`, `Bc1Encoder`, `GpuCellCache`,
  `OrthoPackIndex`, `OrthoPageWindowAssembler`, `RealisticClimberRig`), CRLF już w HEAD. **Nie ruszałem** —
  naprawa dotknęłaby 7 obcych plików i zaśmieciła diff. Do decyzji: osobny commit „normalizacja końców linii".

## ══ START 2026-07-26 ══

1. **WERDYKT USERA** na pozie z nagłówka: czy „łączenia" zniknęły; czy ton/ostrość bliskiego planu bez
   regresji; czy MO (showcase 5 cm) niezmienione.
2. **BLADY TRÓJKĄTNY PLACEK na tafli MO** (i podobny na stoku pod Rysami) — ZASTANY, NIE z dzisiejszych
   zmian. Dwa testy 1:1 na tej samej pozie `8836.09;-5599.396;1394.5;900;4.0875397;0.39500022`:
   - `MAPATUR_KILL=det1m,det25arr,det05arr,mosaic` ⇒ **placka NIE MA** (tafla jednolita) ⇒ maluje go
     warstwa detalu, nie geometria ani woda;
   - build ze **wczorajszym shaderem** (`git checkout 5e37b20 -- Terrain3DGlRenderer.cs`) ⇒ **placek JEST**,
     identyczny kształt i pozycja ⇒ un-premultiply go nie stworzył ani nie uwidocznił.
   Zostaje do wyjaśnienia: to ortofoto detalu malowane NA lustrze wody (jasna, mglista tafla ze zdjęcia
   lotniczego kontra ciemna woda z renderera), a granica placka to krawędź celi/pokrycia w perspektywie.
   Kierunek: bramkować detal maską wody (`flatW × darkW` — NIGDY nie usuwać, §C.5) albo tłumić detal
   na taflach jezior. **Uwaga metodyczna:** exe po tym A/B trzeba PRZEBUDOWAĆ — inaczej na dysku zostaje
   build ze starym shaderem (zrobione: DLL 08:32:45, 11 wystąpień `unpremulPunch` w źródle).
3. Pojedyncze ciemne piksele zostają miejscami na granicy (np. kolumna x=565 w kadrze dowodowym: luma 3).
   Metryka nie widzi ich jako linii (0 komponentów), ale jeśli user je zauważy — sprawdzić, czy to kafle,
   w których nodata NIE jest dokładnym zerem (brak ziarna do zalewu) albo realny cień.

## Kolejka (bez zmian priorytetów z 07-24)

1. O(1) wybór celi det05/det25 (krata→slot jak det1m `sliceIdx`) — odblokuje 96+ cel (pomiar: pętla slotów
   18,7 ms przy 96/64, cofnięte do 48/32).
2. Bench F9 cold+warm na bramce „scena dobudowana" — dzisiejsze zmiany shadera NIE są zmierzone F9
   (koszt: jedno dzielenie na sampel; pakiety det25 SCHUDŁY 7,86 → 5,22 GB dzięki zstd).
3. Spike relokacji: load `tatry.dem` 13 s, budowa meshy 16 s BEZ LOGÓW (dodać telemetrię), dekod bazy PNG
   ~10 s, hitch tile-swap 701 ms (lines=383 ms).
4. Krok 7 (tail-first/burst), krok 8 (kasacja compose/mtgc/`OrthoDetailAssembler`; przy okazji zniknie
   czwarta ścieżka dekodu, którą dziś domknąłem tylko dla spójności inwariantu).
5. Drobne: magenta w `MAPATUR_DET1M_DEBUG` na prawym brzegu; audyt samplerów pozostałych programów
   (linie/billboardy/ghost/particles); `gpu-cache` 6,8 GB przestarzałych mtgc v3 do skasowania;
   `opk/*-prerim` (8,5 GB) do skasowania po werdykcie.

## Narzędzia dodane dziś (odtwarzalność)

- `testdata/maps/audit-ortho-nodata-rim.py {width|safety|scan} <katalog-warstwy>` — profil rąbka, audyt
  bezpieczeństwa reguły, sprawdzenie czy warstwa w ogóle ma nodata (decyduje o zakresie rebake'u).
- `testdata/maps/measure-coverage-edge-lines.py <plik|katalog> [--dark]` — **bramka liczbowa** szwów
  na granicy pokrycia (jasnych i ciemnych). Bez niej werdykt „przed/po" nie jest odtwarzalny.
- `MAPATUR_ORTHO_TONE=0` — wyłącza SAMĄ harmonizację tonu (de-blue zostaje);
  `MAPATUR_ORTHO_TONE_DEBUG=1` — mapa korekty tonu (czerwony = rozjaśnienie, niebieski = przyciemnienie).

## Lekcje metodyczne (do przestrzegania, nie do powtarzania)

- **Detektor artefaktu trzeba skalibrować na znanym A/B** (kadr z artefaktem vs kadr bez), zanim się na
  nim oprze wniosek. Pierwsza metryka „najjaśniejszy piksel w kolumnie" dała fałszywy pozytyw i wysłała
  mnie w hipotezę szczelin geometrii — kosztowało jeden zbędny workflow rekonesansu.
- **Plan z handoffu jest hipotezą, nie wyrokiem.** Feather w bake + rebake 74 min był gotowym planem;
  pomiar pokazał, że problem jest w shaderze, a rebake potrzebny na coś zupełnie innego (i 24× tańszy).
- Inwariant zamiast pamięci: §C.11 checklisty + test na WSZYSTKICH 4 ścieżkach (historyczny tryb awarii
  to cichy replace, który wszedł na jedną ścieżkę).
