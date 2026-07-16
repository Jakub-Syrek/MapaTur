# HANDOFF 2026-07-15 — Streaming orto (25 cm żywy, 5 cm za flagą) + ROOT CAUSE siatki terenu

Sesja ~40 tur. Ten dokument = pełny, dokładny stan. Czytaj RAZEM z:
`docs/HANDOFF-2026-07-14-ortho-hires.md` (poprzednik), `docs/PLAN-ortho-massif-streaming.md`,
`docs/TERRAIN-GRAPHICS-CHECKLIST.md`, pamięć: `terrain-tile-grid-normal-seam`, `ortho-5cm-hardening-order`,
`never-regress-working-showcase`.

---

## 0. TL;DR — gdzie jesteśmy

- **Streaming 25 cm (det25) ŻYJE i jest przyjęty przez usera** („jest ok" ×2 — wygląd i perf). Per-draw bind, unit 10.
- **Streaming 5 cm (det05) DZIAŁA za flagą** `MAPATUR_DET05_STREAM=1` (unit 11, coverage-gated, two-level budget).
  Zweryfikowany logiem (komórki komponują się i rezydują). **BRAK werdyktu A/B usera.**
- **⭐ NAJWAŻNIEJSZE ZNALEZISKO: siatka kafli w geometrii terenu = SZEW NORMALNYCH baked-kafli.**
  Root cause potwierdzony w kodzie, **fix zaprojektowany, NIE zaimplementowany** (§3). To NIE jest orto.
- **Nic nie zacommitowane.** Branch `feat/walk-mode`, HEAD `4e6e73b`. Testy **1578 green**.
- ⚠️ **W trakcie sesji spowodowałem crash-regres** (heap 24 GB) — naprawiony (§5.3), ale to lekcja (§7).

---

## 1. Stan drzewa roboczego (NIC nie zacommitowane)

**Zmodyfikowane (`git diff --stat`):**
```
docs/TERRAIN-GRAPHICS-CHECKLIST.md               17 +-     (z poprzedniej sesji)
docs/TILE-PRODUCTION.md                         112 ++      (z poprzedniej sesji)
src/MapaTur.App/Services/Terrain3DGlRenderer.cs 1068 ++     ← główna robota
src/MapaTur.App/Views/Terrain3DView.xaml.cs      279 ++     ← wiring
```

**Nowe pliki (untracked):**
```
src/MapaTur.Application/Terrain/OrthoTileDecodeCache.cs
src/MapaTur.Application/Terrain/OrthoDetailCellComposer.cs
src/MapaTur.Application/Terrain/TwoLevelDetailResidencyPolicy.cs
tests/MapaTur.Application.Tests/Terrain/OrthoTileDecodeCacheTests.cs          (8 testów)
tests/MapaTur.Application.Tests/Terrain/OrthoDetailCellComposerTests.cs       (5)
tests/MapaTur.Application.Tests/Terrain/TwoLevelDetailResidencyPolicyTests.cs (11)
```
**Testy: 1578 green** (`dotnet test tests/MapaTur.Application.Tests`). Przed sesją 1554.

⚠️ **Przed pushem:** `dotnet format --verify-no-changes` + pełny gate. **Autor commitu = tylko user, ZERO atrybucji AI.**

---

## 2. Co zbudowane (z liczbami z pomiaru)

### 2.1 Warstwa CPU (Application, czysta, TDD)
- **`OrthoTileDecodeCache`** — RAM-LRU zdekodowanych kafli 512² RGBA, keyed `(i,j)`, budżet bajtowy, thread-safe,
  `Lazy` per-key (dedup równoległych żądań), **null NIE cache'owany** (kafel dociągnięty później nie jest
  zapamiętany jako dziura). Diagnostyka: `Hits/Misses/ResidentBytes/DecodeMillis`. Kalka `BakedDemTileCache`.
- **`OrthoDetailCellComposer : IOrthoDetailComposer`** — grid + `OrthoDetailAssembler` + wstrzyknięty tileProvider
  + opcjonalny `baseFill`. **Zwraca `null` gdy ŻADEN kafel komórki nie istnieje** (manager pomija → baza widoczna,
  zero uploadu pustej tekstury 4096²). Komórka częściowa → bufor z dziurami **alpha=0**.
- **`TwoLevelDetailResidencyPolicy`** (+ `DetailLevelSpec`, `TwoLevelDesired`) — koordynuje det05+det25 przeciw
  JEDNEJ księdze VRAM, finest-wins, **coverage-gated fine**, **rezerwa coarse-backing** (patrz §2.3).

### 2.2 Renderer (Terrain3DGlRenderer.cs)
- **det25 streaming, unit 10, per-draw bind:** każdy kafel terenu wybiera najbliższą rezydentną komórkę
  (`CellForPoint` na środku kafla) → bind + `uDet25MinXY/MaxXY` per draw. **Model wątkowania = kopia sprawdzonego
  base-ortho** (`Task.Run` compose off-thread → harvest non-blocking `IsCompleted` → strip-upload `TexSubImage2D`
  z budżetem → `GenerateMipmap` + promote → evict `DeleteTexture`).
- **det05 streaming, unit 11, za flagą** — równoległa ścieżka (metody `StreamDet05`/`DrainDet05Uploads`/
  `BindDet05ForTile`/`DisposeDet05Cell`), sterowana `TwoLevelDetailResidencyPolicy`.
- **Shader (zmiany additive, §C checklisty zachowane):**
  - `applyOrthoDetail(..., float rangeFade)` — nowy parametr; **`w *= rangeFade * dcs.a`**.
  - **alpha-honoring** (`dcs.a`) — dziury (alpha 0) → baza/det25 per-piksel zamiast czerni. Kafle kompletne
    (alpha 255) bez zmian → **zero regresji**.
  - **distance-fade** (`uDet25EyeXY/FadeInner/FadeOuter`, 0.62→1.05× promienia ringu) — miękki front ringu
    zamiast twardego popu (plan §R0.7).
- **Diagnostyka:** `[Mem] det25 N cells ~MB (resident/staging/composing/empty) | desired | queue | inflight | eye lat,lon cell (ci,cj)`,
  `[Mem] det25 tilecache N tiles ~MB | hit/miss (%) | decode avg ms/tile`, `[Mem] det05 ...`,
  `[Det25] cell (ci,cj) compose Xms | upload Yms | mipmap Zms | Npx`.
- **Debug:** `MAPATUR_DET25_FOCUS=lat,lon` — wymusza focus ringu (pomiar nad pokryciem niezależnie od kamery).

### 2.3 Stałe (po hartowaniu — NIE podnosić bez pomiaru!)
```
Det25HardCapCells          = 8      // ≈683 MB; 14 ZABIŁO appkę (VRAM terenu + heap) — patrz §5.3
Det25MaxConcurrentComposes = 2      // 4→2: tempo alokacji buforów 89 MB
Det25RingRadiusMeters      = 1500
Det25UploadBudgetMsPerFrame= 4.0    // ponad 6 ms base-ortho
det25 tile cache           = 384 MB // zmierzone: peak 553 MB @14-way; hit ~42-59%
Det05CoverageTiles         = 16     // 8192² komórka (409.6 m @0.05) — margines 128 m, seam-safe dla z17
Det05HardCapCells          = 3      // ≈341 MB/komórkę!
Det05RingRadiusMeters      = 350
Det05CoarseBackingCells    = 4      // det25 zarezerwowane pod ring det05 (no-hole)
det05 tile cache           = 512 MB
```

---

## 3. ⭐ ROOT CAUSE: siatka kafli w GEOMETRII terenu (NIE zaimplementowany fix)

**Objaw:** widoczna siatka granic kafli w geometrii, **widać z WYŁĄCZONYM orto** (user potwierdził zrzutem),
na gładkich stokach, bliski zoom. User: „wcześniej się doczytywało i znikało, **teraz trwa**".

**ROOT CAUSE (potwierdzony w kodzie):**
1. Baked kafel meszowany jako **OSOBNY, samodzielny raster** — `BakedTileMeshBuilder.cs:69` → `AsRaster(tile)`.
   **Bez danych sąsiada.**
2. Normalne (central-difference) **klampują na własnej krawędzi kafla** — `TerrainMesh3D.cs:911-917`:
   ```csharp
   int cW = Math.Max(c - normalRadius, 0);        // klamp = krawędź KAFLA (nie mapy!)
   int cE = Math.Min(c + normalRadius, cols - 1);
   ```
   → na styku normalna **jednostronna** ≠ normalna sąsiada, **mimo że wysokości są bit-identyczne**.
3. Shader bierze **`vNormal` wprost** do Lamberta (`shN = normalize(vNormal)`; `lambert = dot(shN, uLightDir)`)
   → nieciągłość normalnej = **jasna/ciemna linia dokładnie na granicy kafla**.
4. Na **gładkich stokach** nic tego nie maskuje → widoczna siatka.

⚠️ **Komentarz w `TerrainMesh3D.cs:903` KŁAMIE dla baked:** twierdzi *„sampling the full raster so tile-edge
normals use real neighbour cells (continuous shading across seams)"* — **prawda dla live-LOD** (jeden duży raster),
**FAŁSZ dla baked** (kafel = cały „raster").

**Czym to NIE jest (sprawdzone — NIE diagnozuj od zera):**
- ❌ **NIE orto/detal** — widać z wyłączoną nakładką.
- ❌ **NIE brak refinementu ani cap** — badge `res 637/637` **BEZ `(cap)`**; 1047/2400 kafli, 3.3/10.24 GB
  → budżety nieprzycięte, **mesh w PEŁNI załadowany** ⇒ trwały szew, nie artefakt ładowania (stąd „trwa").
  (Badge = `ResidentCount/Desired`, `MapPageViewModel.cs:3940-3942`.)
- ❌ **NIE regresja tej sesji** — git: **cały mesh/LOD nietknięty** (working tree = wyłącznie orto).
  Prapierwotne; **mocno uwidocznione przez sub-1m z18/z19** (12-07 `2a2a22a`) = przy bliskim zoomie
  **dużo więcej kafli = dużo więcej szwów**.

**FIX (zaprojektowany, do zrobienia) — `NormalSmoothingRadius = 1` (`TerrainMeshOptions.cs:47`) ⇒ apron 1 komórki wystarczy:**
1. `TerrainMeshOptions`: nowe **`NormalApronCells`** (default **0** ⇒ zero zmian dla istniejących ścieżek).
2. `TerrainMesh3D.Build`: przy `apron>0` emituj wierzchołki **tylko dla wnętrza** `[apron .. cols-1-apron]`;
   **pętla normalnych bez zmian** (próbkuje pełny raster z halo → na krawędzi wnętrza ma PRAWDZIWYCH sąsiadów;
   klamp działa dopiero na krawędzi halo, która nie jest emitowana).
3. `BakedTileMeshBuilder`: zbuduj raster `(cols+2)×(rows+2)` z **8 sąsiadów** (provider `Func<DemTileKey,BakedDemTile?>`
   — cache/store kafli je ma); brak sąsiada (skraj piramidy) → **replikacja krawędzi = dzisiejsze zachowanie**.
⚠️ **Dotyka RDZENIA przyjętej geometrii** (`sub-1m-geometry-epic`: „geometria wygląda dobrze") → **TDD +
weryfikacja wizualna**, świeża sesja. Zadanie #11.

---

## 4. Pokrycie danych — TWARDA rzeczywistość (mierzone, nie deklarowane)

**Artefakt-mapa:** https://claude.ai/code/artifact/8fc7e549-eabb-4dac-b6ef-80405085d711

| warstwa | kafli (repo, 07-15) | zasięg realny | uwaga |
|---|---|---|---|
| **det25** (25 cm) | **39 851** | **991 kompletnych komórek**, ci 18–77 / cj 0–31 ≈ **lon 19.69–20.33, lat 49.18–49.40** (~657 km²) | polski rdzeń; **SK i skraje NIE pokryte** |
| **det05** (5 cm) | **130 397** ⬆ (start sesji: 60 170) | przy 60k: pas **lon 19.80–20.10, lat 49.28–49.30** (21.8×2.3 km, 78% gęstości), **0 komórek 256/256**, 411 ≥95%, ~6.6 km² gęstego — **POZA Morskim Okiem** | **fetch ŻYWY, urósł 2×** → zasięg dziś ZNACZNIE większy, **mapa nieaktualna** |
| **sk20** (SK 20 cm) | 38 254 | — | nietknięte w tej sesji |

⚠️ **KRYTYCZNE dla następnej sesji:** det05 urósł 60k→130k **w trakcie sesji**. Wszystkie liczby det05 powyżej
(pas, 403/411 komórek) są **STALE**. **Trzeba przeliczyć pokrycie i zregenerować `_coverage.txt`** (§9.3).

**5 cm nad Morskim Okiem** pochodzi z **osobnej pre-bake'owanej mozaiki** (`dem/ortho-detail/morskie-oko/det05_mosaic.png`),
**nie z piramidy det05**. Showcase = ta mozaika (unit 11, statyczna, flag OFF).

---

## 5. Perf — zmierzone, zahartowane, i JEDEN crash-regres

### 5.1 Pomiary det25 (realne, z logu)
| metryka | wartość |
|---|---|
| compose/komórkę | **~800 ms** (przy 14-way było ~1200 ms — kontencja) |
| decode realny WebP | **8.9 ms/kafel** (×64 = ~570 ms) — **0.4 ms** to koszt nietrafionego pliku (null), NIE dekod |
| upload (strip) | 5–20 ms, cięty budżetem 4 ms/klatkę |
| mipmap | ≤1 ms (był 1× spike 23 ms przy 14-way) |
| cache hit-rate | 42–59% (sąsiednie komórki dzielą kafle krawędziowe) |

### 5.2 Zahartowane (przed→po, przyjęte przez usera „jest ok")
- concurrency 14→**4**→**2**; cap 14→**8**; cache 1 GB→**384 MB**; statyczny 5 cm **w tej samej księdze** budżetu;
  distance-fade na froncie ringu; alpha-honoring.

### 5.3 ⚠️ CRASH-REGRES (moja wina) — przyczyna i fix
**Objaw:** heap rósł do **14 → 24 GB**, frame-gapy (gen2), **app crash**; dodatkowo VRAM det25 dławił teren.
**Przyczyny (dwie, złożone):**
1. **Przeciek `composeInFlight`**: `DisposeDet25Cell` NIE dekrementował licznika przy ewikcji **komponującej się**
   komórki (usunięta z dict → harvest jej nie widzi → licznik przecieka do limitu → **kicki stają** → det25 utykał
   na **4/14 rezydentnych** → bliskie pole spadało do bazy 1 m = „rozmyte 25 cm"). Ukryte nad MO (kamera stała).
2. Po naprawie (1) det25 zaczął się wypełniać → **więcej compose'ów + osierocone Taski** (ewikcja komponującej
   komórki zostawiała działający Task trzymający bufor 89 MB) → **tempo alokacji > Server GC** → balon heap → crash.

**FIX (zaimplementowany, zweryfikowany):**
- **`EvictDet25ToBudget`/det05: NIGDY nie ewiktuj komórki z `Compose != null`** (`|| c.Compose is not null → continue`).
  To jednocześnie **zamyka przeciek** (harvest zawsze ją zobaczy → zdekrementuje) **i kończy osierocone Taski**.
- `DisposeDet25Cell`/`DisposeDet05Cell`: dekrement + `Compose=null` jako **safety net**.
- cap 14→8, concurrency 4→2.

**Zweryfikowane po fixie:** heap **stabilny ~3.7 GB** w spoczynku (spada → GC zbiera), ws 7.2 GB (było 10–11),
det25 **8/8 rezydentnych**, `inflight 0`. **Ale przy locie heap dochodził do 8 GB / ws 15.5 GB** — patrz §8.

---

## 6. det05 streaming (za flagą) — stan

**Włączenie:** `$env:MAPATUR_DET05_STREAM='1'` przed uruchomieniem. **Domyślnie OFF** ⇒ ścieżka przyjęta
(det25 + statyczna mozaika 5 cm MO) **bajt w bajt nietknięta**.
- Flag ON: `SetupDet05Streaming` (View) buduje grid `(0.05, cov16, pitch6)` + cache 512 MB + composer +
  **predykat pokrycia z `_coverage.txt`**; `renderer.SetOrthoDetail05Streaming(...)` buduje `TwoLevelDetailResidencyPolicy`.
  **Statyczna mozaika MO NIE jest ładowana** (streamowany det05 przejmuje unit 11) ⇒ **flaga = przełącznik A/B**.
- **Zweryfikowane logiem:** `det05 streaming ON — ring 350m, cap 3 cells, cellPx 8192 (two-level, coverage-gated)`,
  `wired ... (403 covered cells)`, `[Det05] cell (235,76) compose 11570ms | 8192px resident` ×3, **zero błędów GL**.
- ⚠️ **compose ~11.5 s/komórkę** (256 kafli → bufor 8192²). Poprawne, ale wolne — **wymaga osobnego hartowania**.
- ❌ **BRAK werdyktu A/B usera** (user zapiwotował na siatkę terenu).

---

## 7. Lekcje z tej sesji (twarde)

1. **Ewikcja obiektu z działającym w tle Taskiem = przeciek licznika + osierocony bufor.** Zawsze: albo nie
   ewiktuj w trakcie, albo anuluj + rozlicz. To kosztowało crash 24 GB.
2. **`0.4 ms/kafel` to był koszt NIEISTNIEJĄCEGO pliku, nie dekodu.** Mierząc perf sprawdź, czy mierzysz
   ścieżkę happy-path (realny dekod = 8.9 ms).
3. **Test może kodować buggy zachowanie.** Mój test „fine prioritised" asertował, że fine trzyma komórki przy
   ściśnięciu — to była **zachłanność**, która głodziła fallback. Adwersaryjny przegląd to złapał.
4. **`dotnet run ... | Select-Object -First N` UBIJA apkę**, gdy pipe się zamknie. Używaj `Start-Process -NoNewWindow`.
5. **Pułapka stale-exe jest realna:** działająca instancja → build nie podmieni exe → `dotnet run` odpala STARY
   binarny (log pokazywał `resident 14` mimo `cap 8`). **Zawsze: kill → build → sprawdź datę DLL → run.**
6. **User ma rację, gdy mówi „to nie jest to, co obiecujesz".** Sprzedawałem „5 cm", gdy realnie był skrawek,
   a user patrzył na 25 cm/bazę. **Mierz zasięg, zanim nazwiesz rezultat.**
7. **Nie każdy objaw graficzny to Twoja warstwa.** Siatka wyglądała na artefakt orto — była szwem normalnych
   w geometrii. **Test usera `0` (wyłącz nakładkę) rozstrzygnął w 2 sekundy.**

---

## 8. OTWARTE — priorytety dla następnej sesji

1. **⭐ Apron normalnych (zadanie #11, §3)** — to jest to, co user realnie widzi. Fix zaprojektowany, wąski
   (apron 1 komórki). Dotyka rdzenia geometrii → TDD + weryfikacja wizualna.
2. **Regen pokrycia det05** (§4, §9.3) — fetch urósł 60k→130k, `_coverage.txt` (403) jest stale.
   Przeliczyć + zsynchronizować do AppData + odświeżyć mapę-artefakt.
3. **Pamięć przy locie** — po fixie heap w spoczynku 3.7 GB, **ale przy locie 8 GB / ws 15.5 GB**. Dominuje
   **teren (1047 kafli / 3.3 GB geometrii) + baza orto (2.5 GB)** — moje orto to ~1–2 GB na wierzchu.
   Kandydaci: **pooling buforów compose** (recykling 89/268 MB zamiast alokacji per compose), ewikcja tekstur
   det25 gdy nakładka OFF. Znany prapierwotny wątek: „alokacje 450 MB/s → gen2 gap" (`sub-1m-geometry-epic`).
4. **Werdykt A/B det05** (§6) + hartowanie compose 11.5 s (mniejsze komórki? większa równoległość?).
5. **Stoki: materiał skalny** — top-down orto **rozciąga się na pionie niezależnie od rozdzielczości**
   (nawet 5 cm). Google wygrywa na ścianach, bo ma **fotogrametrię** (naloty ukośne). Nasza droga to
   **proceduralny granit** (`rock-material-on-steep-slopes`, toggle „Skały") — wygląda na słaby/wyłączony.
6. **det25 pominięty w odbiciu wody** — świadomie: odbicie renderuje JEDNĄ płaszczyznę (MO), gdzie det05 5 cm
   i tak wygrywa w obu passach; det25 tam redundantny + zaburzony falą. `useDet25=0` w odbiciu to legalny stan
   (shader early-outuje). Gdyby kiedyś trzeba: jedna linia (`BindDet25ForTile` w pętli odbicia).

---

## 9. Uruchomienie / weryfikacja / dane

### 9.1 Build + run (⚠️ kolejność!)
```powershell
Get-Process MapaTur.App -EA SilentlyContinue | Stop-Process -Force   # NAJPIERW kill (stale-exe!)
Start-Sleep 1
dotnet build src/MapaTur.App/MapaTur.App.csproj -c Debug -f net10.0-windows10.0.19041.0 -p:WindowsAppSDKSelfContained=false
# sprawdź datę: src/MapaTur.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/MapaTur.App.dll
$env:MAPATUR_DET05_STREAM='1'                                        # opcjonalnie: streamowane 5 cm
Start-Process -NoNewWindow dotnet -ArgumentList 'run','--project','src/MapaTur.App','-f','net10.0-windows10.0.19041.0','-p:WindowsAppSDKSelfContained=false'
```
**NIE** używaj `| Select-Object -First N` — ubija apkę (lekcja §7.4).
Log: `src/MapaTur.App/bin/.../win-x64/logs/mapatur-YYYYMMDD.log`.

### 9.2 Klawisze / flagi
- **`0`** overlay orto detalu on/off (**test diagnostyczny: czy artefakt jest w orto czy w geometrii!**),
  **`9`** kolor raw↔de-blue, **`8`** obrys granic komórek.
- `MAPATUR_DET05_STREAM=1` — streamowane 5 cm (zamiast statycznej mozaiki MO).
- `MAPATUR_DET25_FOCUS=lat,lon` — wymuś focus ringu (pomiar nad pokryciem; np. `49.29,20.00` = pas det05,
  `49.20,20.07` = MO). Zeruje velocity.
- `MAPATUR_ORTHO_SLICE=1` — stara statyczna 2-komórkowa ścieżka debug.

### 9.3 Dane (⚠️ apka czyta z AppData, NIE z repo!)
Baza: `C:\Users\<user>\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem\`
```powershell
# sync kafli repo → AppData (robocopy exit 1 = SUKCES, skopiowane)
robocopy "C:\Repos\MapaTur\dem\ortho-detail\tatry\det25" "<APPDATA>\Data\dem\ortho-detail\tatry\det25" /E /MT:16 /NFL /NDL /NJH /NP
robocopy "C:\Repos\MapaTur\dem\ortho-detail\tatry\det05" "<APPDATA>\Data\dem\ortho-detail\tatry\det05" /E /MT:16 /NFL /NDL /NJH /NP
```
**Regen `_coverage.txt`** (predykat pokrycia det05; komórki ≥95% z 256 kafli, klucz = `ci*100000+cj`):
skan w `<APPDATA>\...\det05`, geometria `COV=16, PIT=6`, próg `c>=243`. (Skrypt inline użyty w sesji —
przepisać do `testdata/maps/` przy okazji, zgodnie z regułą „każdy proces na kaflach → TILE-PRODUCTION".)

### 9.4 Fetch det05 (ŻYWY, wznawialny)
```
python testdata/maps/fetch-ortho-detail.py --bbox 19.80,49.17,20.10,49.30 --level det05 --area tatry --workers 6
```
Idempotentny. Stan 07-15: **130 397 kafli** (2 procesy python żyły). Monitor: `find dem/ortho-detail/tatry/det05 -name '*.webp' | wc -l`.

---

## 10. Kluczowe kotwice w kodzie (żeby nie szukać)

| Co | Plik:linia |
|---|---|
| Szew normalnych (ROOT CAUSE) | `TerrainMesh3D.cs:903-932` (komentarz 903 kłamie dla baked), `:911-917` klamp |
| Baked kafel → osobny raster | `BakedTileMeshBuilder.cs:69` (`AsRaster`), `:113-114` |
| `NormalSmoothingRadius = 1` | `TerrainMeshOptions.cs:47` |
| Badge `res N/M` | `MapPageViewModel.cs:3940-3942` (`ResidentCount/Desired`, `(cap)` = przycięty) |
| Budżety terenu | `MapPageViewModel.cs:4239-4274` (2400 kafli / 10.24 GB / 24 loads) |
| Skirty (SĄ, 6 m @z16 ×2/poziom) | `TerrainMesh3D.cs:1024-1071`, `MapPageViewModel.cs:3796-3803` |
| det25 streaming | `Terrain3DGlRenderer.cs` → `StreamOrthoDetail`, `EvictDet25ToBudget`, `DrainDet25Uploads`, `BindDet25ForTile` |
| det05 streaming | tamże → `SetOrthoDetail05Streaming`, `StreamDet05`, `DrainDet05Uploads`, `BindDet05ForTile` |
| Shader detalu | `Terrain3DGlRenderer.cs` GLSL → `applyOrthoDetail` (+`rangeFade`, `dcs.a`), call-sites |
| Wiring View | `Terrain3DView.xaml.cs` → `TryLoadOrthoDetailPoc`, `SetupOrthoDetailStreaming`, `SetupDet05Streaming` |

---

## 11. Twarde zasady (złamanie = utrata zaufania)

- ⚠️⚠️ **NIGDY nie regresuj 5 cm nad Morskim Okiem** (miejsce referencyjne). Domyślny stan apki ≥ dotychczas.
- **Mierz, zanim nazwiesz.** Nie mów „5 cm", gdy user patrzy na 25 cm/bazę (lekcja §7.6).
- **Test `0` przed diagnozą grafiki** — rozstrzyga orto vs geometria w 2 sekundy.
- Przed GL czytaj `docs/TERRAIN-GRAPHICS-CHECKLIST.md`; stosuj na WSZYSTKICH ścieżkach (odbicie + teren).
- Commity: autor **tylko user (Jakub Syrek)**, **ZERO atrybucji AI**. `dotnet format --verify` + testy green przed push.
- Nie commituj integracji 5 cm przed testem w otwartej apce (warunek usera).
