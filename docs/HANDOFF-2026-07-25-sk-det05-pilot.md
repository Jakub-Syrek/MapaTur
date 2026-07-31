# HANDOFF 2026-07-25 (wieczór) — SK det05: pilot V3 URUCHOMIONY + kolejność do końca

**Decyzja usera (2026-07-25): startujemy pilotem V3** (pas przygraniczny ~1,5 km, ~52,4k kafli).
Zakres zgody = TYLKO pilot. V2 (rdzeń, 285k kafli / +37 GB opk) i V1 (całość, 612k / +80 GB) wymagają
OSOBNEJ zgody po werdykcie z pilota. Rozpoznanie z liczbami: [`PLAN-sk-det05-zbgis.md`](PLAN-sk-det05-zbgis.md);
recepta produkcyjna: [`TILE-PRODUCTION.md`](TILE-PRODUCTION.md) §11.

## Co się dzieje TERAZ

- **Fetch pilota leci w tle** (start ~22:00 lokalnie, ETA ~8-10 h — rano powinien być gotowy):
  `python testdata/maps/fetch-ortho-detail.py --bbox 19.80,49.15,20.10,49.26 --level sk05 --strip-km 1.5 --workers 6`
  Wyjście: `dem/ortho-detail/tatry/sk05/` (repo; OSOBNY katalog od det05 — integracja dopiero po kolorze).
  Dry-run zweryfikowany: would=52 395 / sk_side=196 678 / far=160 367 z 409 440 pozycji kraty.
- Rocznik sprawdzony REST-em 2026-07-25: **większość pasa (w tym 20.03E) = 2024-07-31, 15 cm (stred,
  3. cykl); rejon Rysów = 2022-08-26, 20 cm (východ, 2. cykl)**. Granica kampanii między 20.03 a 20.088E —
  pilot celowo obejmuje oba roczniki i ich wewnętrzny szew. Sonda webowa myliła się co do przebiegu
  granicy stref (nie jest to prosty południk ~20.0E) — ufać REST-owi, nie mapkom poglądowym.

## ══ KOLEJNOŚĆ PO FETCHU — RÓB PO KOLEI, NIE POMIJAJ BRAMEK ══

### 1. Walidacja fetchu — ✅ WYKONANA 2026-07-25/26 (wszystkie bramki zielone)
- **Komplet: ok=52 395 = DOKŁADNIE liczba z dry-runu; err=0; nodata=0; 3,17 h; ~2,5 GB (~48 KB/kafel).**
- **Blue-cast: 0,72/255 mean, p95 3,37 (próbka 200)** — surowy ZBGIS czyta się jak „DISK-CORRECTED";
  brak niebieskiego zafarbu cieni znanego z GUGiK. Do A/B po bake'u zostaje tylko: czy shaderowy
  de-blue czegoś nie PRZEciąga.
- **Fill/nodata: NIE ISTNIEJE w pasie.** Scan 400 losowych = 0; audyt wszystkich 360 kafli z
  `_partial.txt`: dokładne (255,255,255) max 1,5% powierzchni (17/360 kafli, rdzenie prześwietleń),
  losowe 300 spoza listy = 0. **`_partial.txt` w sk05 = ŚNIEG i prześwietlona skała (fałszywe pozytywy
  heurystyki min>244), NIE braki danych** — zweryfikowane wizualnie (1543/934 = płat śniegu). Nakładka
  ZBGIS za granicę pokryła 100% pasa realnymi danymi. ⇒ ŻADNEJ reguły wygaszania bieli dla sk05 —
  wygaszenie zabiłoby śnieg (patrz saga detektora śniegu w epice deshadow!).
- spot-check obu roczników (2024 zach., 2022 Rysy): dane zdrowe; 2022 miększy/ciemniejszy.
- **⚠ ZNAK WODNY WPISANY W RASTER (odkryty na mozaikach, NIE w pojedynczych kaflach):** wielkie
  (~50 m) półprzezroczyste obrysowe litery + napisy „2022" / „© GKÚ, NLC", rozsiane rzadko (grid?).
  Zweryfikowane 2 rozmiarami requestu na tym samym bbox: pozycja GEO-stała, tekstura prześwituje ⇒
  wypalone w serwowanym rastrze, nie per-request. Przykład: litery „M A A" na piargu nad Chatą pod
  Rysami (`dev/sk05-preview/wm-test-*.png`, kafel ~i=1652,j=978). DO ZROBIENIA: (a) policzyć gęstość
  w pasie (skan przy harmonizacji), (b) sprawdzić na 1 próbce, czy opendata/MAPKA są czyste — jeśli
  tak, to argument za przejściem na paczki przy V2; (c) pilot: zostawić, user oceni widoczność w apce
  (na piargu wyglądają jak jasne ścieżki). Inpaint = ostateczność, osobna decyzja.

### 2. Harmonizacja koloru DATA-SIDE (pixel święty: na KOPII, surowe sk05 zostają)
- Wzorzec istnieje: `overlay-zbgis-ortho.py:150-162` `color_match_zbgis` — Reinhard per-kanał
  (mean/std) względem GUGiK. Dla detalu: **per-cela det05 (409,6 m), referencja = det25** (pokrywa cały
  masyw, radiometrycznie zgodna w miejscach oświetlonych — pomiar 07-25: mediana 83 vs 83).
- Wyjście do `dem/ortho-detail/tatry/sk05-harm/`. KAŻDY parametr/komendę wpisać do TILE-PRODUCTION §11.
- Zmierzone na rozpoznaniu (patrz PLAN §4): skalar NIE zszyje fenologii trawy (rotacja odcienia,
  satGap −42) ani szwu cień/słońce — na pilocie oceniamy, czy Reinhard wystarcza na piętrze
  skalno-piarzystym (większość pasa). Trawa/kosodrzewina = wiedzieć, że będzie gorzej; deshadow i
  korekta per-klasa terenu to OSOBNA epika (nie whack-a-mole w pilocie!).
- Bramka liczbowa: powtórzyć pomiar par granicznych (`probe-zbgis-overlap-color.py` + pary z
  `border-pairs`) na kaflach PO harmonizacji — |dLuma| na oświetlonej skale ma spaść z +21…+29 do <8.

### ⚠ ZNALEZISKO 07-26: nodata GUGiK **HighResolution jest BIAŁE**, a apka gasi tylko CZARNE

Zgłoszenie usera („na zszyciach białe prostokąty" na mozaice podglądu) = **realny błąd apki, zastany,
niezależny od pilota.** Cała maszyneria z 07-24/25 (`OrthoNodata.ZeroAlphaOnBlack` + `ZeroAlphaOnNodataRim`)
obsługuje **wyłącznie** dokładną czerń i rąbek near-black — bo det25 pochodzi ze **StandardResolution**,
które wypełnia nodata czernią. **det05 pochodzi z HighResolution, które wypełnia BIELĄ** — i tego nie gasi
NIC (grep po `244`/white w `src/` = zero trafień w kodzie). Biały piksel przechodzi każdą bramkę `dcs.a`
i maluje **kryjący biały teren**.

Zmierzone (2026-07-26, na pełnym `det05/_partial.txt` = 3381 kafli):
- **1050 kafli ma >2% białego fillu**, mediana **48%** powierzchni kafla, max 98%, łącznie **~33 ha**;
- próbka 600: biały fill 172, czarny 378, oba 22 ⇒ **oba typy współistnieją w det05**;
- **dziś streamuje tylko 67 z nich (19 cel)** — reszta leży w celach, które nie przechodzą bramki pokrycia,
  i dlatego artefakt jest w apce prawie niewidoczny;
- **PILOT BY TO WZMOCNIŁ:** kafle SK dopychają cele graniczne ponad próg pokrycia ⇒ uśpione białe kafle
  zaczęłyby streamować. Dlatego to musi być załatwione RAZEM z pilotem, nie po nim.

**Naprawione merge'em (krok 3): 772 z 1050.** Zostaje **278 kafli (~6,4 ha, mediana 20% kafla)** na
krawędziach kampanii HighRes **w głębi PL**, gdzie ZBGIS nie sięga. Opcje (DO DECYZJI USERA):
1. **data-side alfa (rekomendacja):** zapisać te 278 jako WebP **RGBA z alfa=0 na fillu**. Ścieżki dekodu
   to przepuszczą — zweryfikowane w kodzie: runtime `DecodeOrtho` używa `SKAlphaType.Unpremul`
   (`Terrain3DView.xaml.cs:9186-9190`), bake `Copy(SKColorType.Rgba8888)` (`OrthoBake/Program.cs:128-132`).
   Zero zmian w runtime, zgodne z KONTRAKT-ORTO („korekcje TYLKO data-side"), odwracalne backupem.
2. **reguła białego w `OrthoNodata`** (symetria do czerni: ziarno = dokładne 255,255,255, zalew near-white,
   tylko komponenty dotykające krawędzi kafla). RYZYKO: **śnieg** — płat śniegu przecina krawędź kafla
   25,6 m nagminnie; to jest dokładnie pułapka, na której poległy dwa detektory śniegu w epice deshadow.
3. skasować najgorsze kafle → shader spada na det25/bazę (zaprojektowany fallback), ale traci się
   80% dobrego 5 cm w kaflach, gdzie biel to tylko 20%.

**WYBRANA ŚCIEŻKA 1 (user, 07-26) — WYKONANA:** `zero-alpha-white-nodata-det05.py --write`, **273 kafle**
(mediana 21% kafla, 6,4 ha), WebP RGBA lossless+exact, backup w `det05-premerge/`, lista `_white-alpha.txt`.
Weryfikacja: tryb RGBA 60/60, kolor poza maską **bit-w-bit 80/80**.
**Kryjąca biel: 1050 → 37 kafli, a te 37 to PRAWDZIWY TEREN**, nie nodata (średnio 4% kafla; kafel
1050/535 = biały dach hali; 12 z 37 nie ma nawet ziarna dokładnego 255). Maska ich nie ruszyła —
dowód, że kryterium „ziarno 255 + spójność + dotyk krawędzi" NIE zjada jasnego terenu ani śniegu.

### 3. Merge straddlerów granicznych — ✅ WYKONANY 2026-07-26
- Problem: det05 PL ma 3381 kafli częściowych (`det05/_partial.txt`) z wygaszoną (nodata) połową SK;
  resume-skip fetchera NIGDY ich nie uzupełni. ZBGIS ma dane także w głąb PL (nakładka ≥1,2 km przy
  20.03E), więc da się je wypełnić w całości.
- Spec narzędzia `merge-zbgis-into-partial-det05.py` (analog merge-sk-into-partial-tiles.py, ale orto):
  wejście = `_partial.txt` + kafle det05; źródło pikseli SK: **NAJPIERW lokalny `sk05/<i>/<j>.webp`**
  (fetch pilota z maską "sk" pobiera też straddlery — te same pozycje kraty!), fallback fetch ZBGIS
  512px tylko dla braków. Harmonizacja TĄ SAMĄ transformacją co §2 (spójność przez szew!), wpisanie
  WYŁĄCZNIE w piksele nodata/near-black GUGiK (kryterium jak `OrthoNodata`: dokładna czerń + zalew
  rąbka lumą ≤16), backup oryginałów do `det05-premerge/`, zapis WebP q90. Piksele GUGiK bit-w-bit
  nietknięte. **Po walidacji §1: po stronie ZBGIS brak filla — pikseli sk05 NIE filtrować po bieli
  (śnieg!); jedyne wygaszanie dotyczy strony GUGiK (czerń), reszta idzie z sk05 w całości.**
- Straddlery z sk05 przy integracji §4 NIE są kopiowane (kolizja z det05 = idą przez merge, nie kopię).
- **WYNIK (`merge-zbgis-into-partial-det05.py --write`):** zmergowane **867** z 3381 (2511 bez kafla
  sk05-harm — to krawędzie kampanii HighRes w głębi PL, nie granica państwa; 3 z maską <0,5%).
  Udział wypełnienia w zmergowanych: mediana **56%** kafla, p90 95%, max 100%. Backup bit-w-bit →
  `det05-premerge/`, lista → `det05/_sk-merged.txt`. Biel w kadrze dowodowym szwu: **1,78% → 0,95%**.
  Maska fillu obsługuje BIAŁY (HighRes) i CZARNY (Standard), bierze tylko komponenty **dotykające
  krawędzi kafla** — śnieg jako blob wewnętrzny zostaje nietknięty.

### ⚠ LEKCJA 07-26: kafle pochodne zapisywać BEZSTRATNIE

Pierwsze podejście (merge + alfa) zapisywało WebP `quality=90` — czyli **drugą generację kompresji
stratnej** na pikselach GUGiK POZA maską. Zmierzone: kolor zmieniony na **60/60** sprawdzonych kafli.
Naprawa: przywrócenie 1137 kafli z `det05-premerge/` i ponowny zapis `lossless=True, exact=True`
(konwencja z bake'u deshadow). Po poprawce: kolor poza maską **bit-w-bit na 80/80** w obu narzędziach.
Koszt: 1137 kafli = 252 MB (vs ~55 MB stratnie). **Każde nowe narzędzie piszące kafle pochodne MUSI
używać lossless** — inaczej cicho degraduje 5 cm przy każdym przebiegu.

### ⚠ LEKCJA 07-26: bake OOM-uje przy uruchomionej apce
`--parallel 14` × grupa det05 (8192² RGBA + 256 kafli + mipy ≈ 600 MB) ≈ 8 GB. Przy działającej apce
(5,8 GB) i 13,4 GB wolnego → `Unable to allocate pixels for the bitmap` (Program.cs:128).
**Przed bakiem ubić MapaTur.App i dać `--parallel 6`** (po rundzie apkę postawić z powrotem).

### 4. Integracja: sk05-harm → det05 — ✅ WYKONANA 2026-07-26
- **51 140 nowych kafli** skopiowanych; **1255 kolizji → GUGiK 5 cm ZOSTAJE** (finest-wins, zero regresji
  polskiej strony). Lista rollbackowa: `det05/_sk-pilot-added.txt`.
- det05 na dysku: **394 217 kafli** (było ~343 tys.), zakres i 851..1703, j 434..1030.
- Coverage p16 przeliczone: **1222 → 1416 cel** (+194 nowych cel 5 cm).
- Sync do AppData (robocopy): 52 282 pliki / 2,37 GB, 0 błędów. Drzewa repo i AppData były identyczne
  co do rozmiaru+mtime, więc srcHash bake'u pozostaje ważny dla niezmienionych kafli.

### 4b. Integracja (opis pierwotny)
- Kopiowanie `sk05-harm/<i>/<j>.webp` → `dem/ortho-detail/tatry/det05/<i>/<j>.webp`.
- **ZAPISAĆ listę dodanych (i,j) do `det05/_sk-pilot-added.txt`** — to jest rollback (usuń z listy +
  krok 5-6 od nowa = stan sprzed pilota; plus przywrócenie `det05-premerge` dla straddlerów).
- Kolizje NIE istnieją z definicji maski (sk_side/pl), ale asercja w skrypcie kopiującym: jeśli plik
  istnieje → STOP i zbadać (to byłby straddler, który powinien iść przez §3, nie przez kopię).

### 5. Coverage (inwariant z 07-25: zły plik = warstwa 5 cm ZNIKA cała)
- `python testdata/maps/build-det05-coverage.py <katalog det05> --pitch 16` → `_coverage_p16.txt`.
- Sanity: liczba cel PO regeneracji > liczba PRZED (stara lista ~zapisana w logu regen; różnica ≈
  liczba nowych cel pasa ~ (52395+3381)/256 ≈ 210-260 cel).

### 5-7. Coverage + bake + sync — ✅ WYKONANE 2026-07-26
```
dotnet run --project src/MapaTur.OrthoBake -c Release -- --layer det05 \
  --src "<AppData>/dem/ortho-detail/tatry/det05" --out "<AppData>/dem/ortho-detail/tatry/opk/det05" --parallel 6
```
**302 pakiety wypieczone + 1317 pominiętych (przyrostowo), 7,1 min**, stron 395 836, kafli źle 0;
`[verify] strony(TOC)−taile = 394 217 = kafle źródłowe → ZGODNE`, próbka CRC 128/128 OK, wyjście **52,17 GB**
(z 45,0 GB). Apka po restarcie: `[Det05] .opk page streaming ON (… 1619 grup)`, tablice 192 cele /8,0 GB,
`glGetError clean`, cele czytają się z opk (np. `(104,61) opk-read 1410ms | layer 0 (A) resident`).
DROBIAZG: log bake'u pisze „det25 GOTOWE" niezależnie od `--layer` (kosmetyczny błąd etykiety w Program.cs).

**`--verify-full` ZIELONE (6,0 min):** `pakiety=1619 strony: OK=395836 BAD=0 | layoutBad=0 dupPageId=0 |
pliki-poza-indeksem=0`. Czyli komplet: żadna strona nie ma złego CRC, layout i bijekcja kluczy czyste,
w katalogu nie leży ani jeden osierocony pakiet.

## ══ PILOT ODEBRANY — WERDYKT USERA 2026-07-26: „jest ok" ══

**To jest STAN ZAAKCEPTOWANY PRZEZ USERA — zasada 19: NIE WOLNO go cofać ani „ulepszać" bez jego zgody.**
Obowiązuje na: harmonizację do tonu bazy (`sk05-harm`), 51 140 kafli SK w det05, rozstrzygnięcie 1255
kolizji na korzyść GUGiK, alfę na białym nodata (273 kafle), merge 867 straddlerów, coverage 1416 cel,
bake 52,17 GB. Każda przyszła zmiana w tym obszarze = DODAWANIE, nigdy cofanie (por.
`never-regress-working-showcase`).

Pełne brzmienie werdyktu: **„jest ok, szew trochę razi ale z bliska; do tematu wrócimy, ale na teraz
jest lepiej niż było"**. Czyli: stan PRZYJĘTY jako poprawa netto, z JEDNYM otwartym zastrzeżeniem.

Rozstrzygnięte tym werdyktem: **Reinhard per-cela do tonu bazy WYSTARCZA** na piętrze skalno-piarzystym —
nie trzeba deshadow ani korekty per-klasa terenu PRZED rozszerzeniem zakresu.

### ⚠ OTWARTE: szew widoczny Z BLISKA (do wrócenia, NIE do rozgrzebywania od zera)

Objaw potwierdzony przez usera: szew PL|SK razi **tylko z bliskiej odległości**. To zgadza się z pomiarem
i mówi, gdzie NIE szukać:
- z daleka detal ustępuje bazie/det25, a te są tonalnie ciągłe (harm siedzi na bazie w ±1–5 lumy) → brak skoku;
- z bliska po OBU stronach stoi det05 5 cm i widać pełną różnicę: **+25 lumy na Rysach**, z czego
  ~90–100% to **wypalony cień polskiego nalotu 2021** (dowód: baza−GUGiK ≈ harm−GUGiK).

**⇒ Naprawa idzie przez ROZJAŚNIENIE STRONY POLSKIEJ (deshadow R4), NIE przez ruszanie SK.**
Dociąganie SK do GUGiK odrzucone pomiarem 07-26 (patrz „co daje opcja 2"): 69% cel pasa nie ma żadnej
referencji GUGiK, a skok przeniósłby się na południową krawędź pasa (~24 lumy, prosta linia w poprzek
stoku = gorzej widoczna niż szew po grani). **Nie wracać do tego pomysłu.**
Wątek łączy się z epiką deshadow: **ZBGIS 2024 ma światło tam, gdzie 2021 jest wypalony** (za Mnichem
42 vs 19, pod Rysami 32 vs 13) — czyli materiał na referencję luminancji dla R4 leży już na dysku.

Znaki wodne GKÚ nie zostały zgłoszone jako problem — ale to NIE jest ich formalna akceptacja dla V2
(przy 285 tys. kafli będzie ich ~5× więcej).

**Otwarte decyzje (każda wymaga OSOBNEJ zgody):** V2 rdzeń Tatr Wysokich (285 tys. kafli / +37 GB) teraz
vs czekanie na východ-2025 (15 cm, publikacja „leto 2026" — sprawdzać krokiem 0 z TILE-PRODUCTION §11);
sprzątanie `opk/*-prerim` (8,6 GB) i `gpu-cache` (6,8 GB); ZBGIS 2024 jako referencja światła w epice
deshadow (pomiar i zasięg — §9 wyżej).

### 6b. Bake — opis pierwotny
- `dotnet run --project src/MapaTur.OrthoBake -c Release -- --layer det05 --src <det05> --out <opk/det05>`
  — srcHash przebake'uje TYLKO nowe/zmienione cele (pełny det05 = 60 min; pilot ≈ kilka-kilkanaście min).
- `--verify-full` po bake'u (CRC wszystkich stron). Dysk: +~7 GB opk; PRZEDTEM sprzątnąć
  `opk/det25-prerim`+`det1m-prerim` (8,6 GB, po werdykcie usera za rąbek) i `gpu-cache` (6,8 GB, martwe).

### 7. Sync do AppData (runtime NIE czyta repo!)
- Cel: `C:/Users/jaqbs/AppData/Local/User Name/com.companyname.mapatur.app/Data/dem/ortho-detail/tatry/`
  — skopiować: nowe kafle det05 (webp), `_coverage_p16.txt`, zmienione pakiety `opk/det05/*.opk` + `index.bin`.
- Potwierdzić datami plików (lekcja: stary exe/stare dane = fałszywy werdykt).

### 8. Werdykt (podwójne kryterium: wygląd ORAZ płynność, skompilowany exe, dane z AppData, DELL P2722H)
- Kadry: (a) Rysy/grań od strony SK, (b) przelot F7/F9 przez granicę (czy pas SK się doostrza),
  (c) szew wewnętrzny 2024|2022 między 20.03 a 20.088E, (d) **MO/schronisko — showcase NIE WOLNO
  zregresować** (finest-wins: PL det05 zostaje, SK tylko DODAJE).
- Bramki liczbowe: `measure-coverage-edge-lines.py` (jasne i `--dark`) na kadrach szwu granicy —
  porównać z baseline SPRZED integracji (zrobić zrzuty baseline PRZED krokiem 7!). Perf: terrain ms
  i gapy jak w handoffie 07-25 (cele w VRAM stałe 192 — wzrost tylko liczby KANDYDATÓW).
- **Werdykt wizualny należy do USERA** (zasada 3 + 19). Nic nie jest „done" po zielonych bramkach.

### 9. Po pilocie — decyzje (każda = pytanie do usera, nie samowolka)
- Czy Reinhard wystarczył na skale? Jeśli nie → epika harmonizacji per-klasa/deshadow PRZED V2.
- **V2 rdzeń: czekać na východ-2025 (15 cm, publikacja „leto 2026") czy brać 2022 (20 cm) teraz?**
  Przed startem V2 zawsze krok 0 z TILE-PRODUCTION §11 (REST DATUM) — jak wyjdzie 2025, rdzeń od razu lepszy.
- Test prawdy rozdzielczości (opcjonalny): 1 arkusz 20 cm przez MAPKA vs WMS 4,9 cm — czy WMS serwuje
  coś ponad nominal (rozstrzyga, ile realnie niesie nasz sk05).
- Deshadow „odcinka Mnicha" danymi SK — ZMIERZONE 07-25 (pytanie usera): ZBGIS pokrywa WIĘKSZOŚĆ kotła
  za Mnichem nakładką z nalotu **2024-07-31 (15 cm)** — granica danych per lon: 20.035→49.198,
  20.045→49.196, 20.050→49.193, 20.055→49.191, 20.060→49.190, 20.065→49.187, 20.070→49.185,
  20.075→49.182. Na płytach za Mnichem ZBGIS ma światło, gdzie 2021 ma wypalony cień (med. 42 vs 19;
  pod Rysami 32 vs 13; korejestracja ~0,25 m). POZA nakładką: baszta Mnicha (20.065 sięga tylko 49.187)
  i ściana Kazalnicy (werdykt „nienaprawialna" bez zmian). Wpięcie = decyzja usera w epice deshadow
  (V2 ZAMROŻONE): ZBGIS jako DODATKOWY rocznik referencyjny w R4, TYLKO luminancja po lokalnej
  normalizacji (jak 2019), chroma nadal wyłącznie z 2021. Wymaga małego fetchu strefy nakładki
  (~90 kafli det25-grid lub ~2,3k det05-grid — pole i tak jest low-pass 25-40 m, 25 cm wystarczy).
  Zapis w pamięci epiki: `ortho-deshadow-luminance-field`.

## Ryzyka / pułapki (szczegóły w PLAN §7)

- de-blue shadera strojony na GUGiK może przeciągać ZBGIS → A/B klawiszem 9 na pilocie.
- Placek na tafli MO (ZASTANY, handoff coverage-edge §2) — nie mylić z regresją pilota.
- Fetch może paść w nocy (timeout/serwer) — jest resumable: ta sama komenda dociąga resztę.
- Jedna instancja apki przy werdykcie (2×8 GB VRAM = fałszywe „nie odświeża się").

## Stan plików (wszystko NIEZACOMMITOWANE, gałąź perf/pano-streaming)

- nowe: `docs/PLAN-sk-det05-zbgis.md`, ten handoff, `testdata/maps/probe-zbgis-native-res.py`,
  `testdata/maps/probe-zbgis-overlap-color.py`
- zmienione: `testdata/maps/fetch-ortho-detail.py` (poziom sk05, `--strip-km`, `--dry-run`,
  atrybucja per-poziom), `docs/TILE-PRODUCTION.md` (§11)
- dane: `dem/ortho-detail/tatry/sk05/` rośnie w tle (~2,7-4,8 GB docelowo)
