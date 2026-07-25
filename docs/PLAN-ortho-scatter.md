# Gęsty scatter roślinności/skał sterowany kolorem orto — PLAN

> Badanie ultracode (8 agentów). Cel: teren w walk mode (F8) wygląda pusto na zbliżeniu — gęsto zaścielić
> drzewami + kamieniami rozmieszczonymi wg koloru orto (zieleń→las, szarość→skały), DUŻO obiektów.

## TL;DR
- **Asset:** KayKit „Forest" = **CC0 1.0** (ta sama rodzina co Adventurers/`hiker.glb`, którego już shippujemy),
  free tier 100+ modeli (drzewa, krzewy, skały, trawa), jeden wspólny atlas 1024². Werdykt: **bierzemy**. Jedyny
  drobny znak zapytania — strona itch wymienia `.GLTF`, nie potwierdza wprost `.GLB`; przy pobraniu repakujemy do
  `.glb` (zero zmian w loaderze).
- **Podejście:** kolor piksela orto decyduje o **klasie obiektu** (zielony→las, szary→skała), a slope/wysokość/
  treeline decydują **czy w ogóle coś rośnie i jak gęsto**. To AND, nie override. Cały instanced GL pipeline (near
  mesh + impostor atlas + LOD) **już istnieje, jest uśpiony** — reanimujemy go i dokładamy drugą ścieżkę na skały.
- **Gęstość:** rezygnujemy ze scatteru po siatce DEM na rzecz **stałej world-space jittered-grid per klasa**
  (deterministyczny hash, zero shimmeru), cache per chunk 32 m LRU (wzorzec `BakedDemTileCache`).
- **Największe ryzyko:** (1) „znów będzie brzydko" — stary las padł przez jednolitą skalę/gatunek/siatkę na złej
  geometrii; mamy konkretną listę anty-brzydota; (2) perf przy close-zoom (grass overdraw + znany alloc-storm
  gen2); (3) rozdzielczość/bias koloru orto (cień niebieski, jesienna trawa) przy klasyfikacji.
- **Effort (uczciwie):** MVP drzewa-na-zielonym ~2–3 dni; +skały ~1 dzień; retune LOD/impostory ~1–2 dni; sezony
  ~1 dzień. Razem ~1 tydzień do „ładnie w walk mode", plus tuning progów na urządzeniu.

---

## 1. Asset — KayKit „Forest"

**Licencja: CC0 1.0 Universal** (public domain, bez atrybucji), zweryfikowana bezpośrednio na
https://kaylousberg.itch.io/kaykit-forest z wysoką pewnością. Ta sama rodzina co Adventurers (Rogue_Hooded),
który już shippujemy — precedens prawny i techniczny domknięty. Redystrybucja w apce OK (scatterujemy modele w
renderze, nie sprzedajemy surowego packa).

**Zawartość (free tier, 100+ modeli):** Trees (kilka stylów/rozmiarów), Bushes (krzewy — podszycie +
kosodrzewina powyżej treeline), Rocks/boulders (głazy), Grass (dwa style, single-sided — wypełnienie ground pod
kamerą walk). ⚠️ Modular terrain pieces + 8 wariantów kolorystycznych = **tylko EXTRA tier ($9.99+)** —
**niepotrzebne**: klasyfikację robimy przez wybór MODELU + per-instance tint z piksela orto.

**Format / atlas / poly:** Ships jako `.FBX`, `.GLTF`, `.OBJ` + **jeden wspólny atlas gradientowy 1024²** dla
wszystkich meshy (→ jeden bind tekstury na cały las+skały, idealne pod instancing). ⚠️ Strona pisze `.GLTF`, nie
potwierdza `.GLB` — repack do `.glb` przy imporcie (jak `hiker.glb`). ⚠️ Poly (kandydackie 14–2450 tris, śr.
~395) niezweryfikowany na stronie — sprawdzić realny tris bazowego drzewa/skały po pobraniu.

**Import:** `.glb` do `Resources/Raw` + `TestData`, **NIE LFS**. Wpis do `THIRD-PARTY-ASSETS.md` (CC0, verified).

**Alternatywa jeśli zawiedzie** (poly za wysokie / brak `.glb` / atlas brzydki w naszym oświetleniu):
**Quaternius „Ultimate/Stylized Nature"** (CC0, glTF, jeszcze niżej-poly) — drop-in, trzymamy jako fallback.

---

## 2. Klasyfikacja orto

### Recipe world-XY → ortho-RGB (na CPU, bez GL readback)
Wszystkie struktury już żyją:
- `OrthoTextureCell` (`src/MapaTur.Application/Maps/OrthoTextureCell.cs:1-13`) = `record(Row, Col, Width,
  Height, byte[] Rgba)`, **top-row-first RGBA8**, piksel `(px,py)` na offsecie `((py*Width+px)*4)`.
- `TerrainMesh3D.WorldToGeo(Vector3)` (`TerrainMesh3D.cs:176-186`) — inwersja projekcji (XY→lat/lon).
- `OrthoCoverage.CellAt(geo)` + `LocalUv(geo,col,row)` (`OrthoCoverage.cs:39-56`) — `LocalUv` zwraca V
  north→south, zgodne z top-row-first `Rgba` (bez flipa), clampuje poza-coverage.

Per kandydat: `geo=WorldToGeo(pos)` → `(col,row,tileIdx)=CellAt(geo)` → cell z `Dictionary<int,OrthoTextureCell>`
(zbudowany raz per sesja z `Terrain3DView.OrthoTextureCells`) → `(u,v)=LocalUv` → `px=(int)(u*(W-1)),
py=(int)(v*(H-1))` → czytaj `Rgba[(py*W+px)*4 + {0,1,2}]`. Fallback bez komórki (`tileIdx<0`): `BiomeClassifier`.
**Uśrednij box 3×3 piksele** (kill JPEG-speckle). Rozdzielczość composite ~kilka–kilkanaście m/px — wystarcza do
zieleń/szarość.

### Klasyfikator (kanały/progi, 0..1)
Z `r,g,b`: `Y=0.299r+0.587g+0.114b` (luma), `Gx=g−max(r,b)` (greenness, ExG-podobny), `S=(max−min)/max` (sat),
`Bx=b−max(r,g)` (blueness).
```
Y > 0.80 && S < 0.12         → SNOW_COVER  (jasne odsycone → śnieg, brak propów)
Bx > 0.03 || lakeMask(here)  → WATER       (twardy exclude)
Gx > 0.06                    → VEGETATION  (zieleń → las/łąka)
S < 0.20 && 0.30 < Y < 0.78  → ROCK_SCREE  (szary/beż odsycony → kamienie)
else                         → BARE        (cień/ziemia — rzadkie, małe propy)
```
Progi tuningować **w renderze 3D, nie top-down** (memory `ortho-color-judge-in-app-not-topdown`). Trzymać w
niemutowalnym `record ScatterThresholds` obok `BiomeThresholds.Default` (A/B na urządzeniu).

**Płynne krawędzie:** nie hard-threshold na granicy las/piarg — `pForest = smoothstep(0.02, 0.14, Gx)` jako
**prawdopodobieństwo**, per-kandydat hash (`ForestPlacement.Hash :126`) decyduje drzewo/skała/nic. Krawędź czyta
się naturalnie przy 1 m, nie jako malowana linia.

**Bias koloru orto — pułapki:** niebieski cień jest FIZYCZNIE w GUGiK (nie kwestionować; test na `Gx` OK —
zacieniona trawa nadal ma dodatnie Gx); jesienna/wysokogórska trawa (brązowa) → mniej drzew >2200 m; kompresja →
box 3×3.

### Jak ominąć „looked bad" (kombinacja z geometrią)
Stary las padł, bo był **jednolitą zieloną mgłą o stałej skali/gatunku, wyrównaną do siatki, na złej geometrii.**
Reguły (deterministyczne, hash-of-cell, zero shimmeru):
1. **Nic wysokiego powyżej treeline** (~1550 m) — powyżej tylko krzewy (kosodrzewina) + trawa.
2. **Nic na `slope ≥ 50°`** (reuse `RockSlopeDegrees=50` z `BiomeClassifier` — ściana skalna prawie pusta).
3. **Zmienna skala per klasa** (głazy 2.2×, otoczaki 0.3×), nie jeden globalny zakres.
4. **Tint per-instance z lokalnego piksela orto** (~35% blend) — drzewa na złotym stoku ciepłe, w cieniu chłodne.
5. **Mix gatunków z drugiego kubełka hash** — sąsiednie drzewa różne (świerki + liściaste + krzewy + trawa).
6. **Polany:** drugi low-freq value-noise gate — pomiń całą 15–20 m plamę gdy `noise<0.3` (korona z dziurami).
7. **Yaw losowy** (już `:104`) + lekki lean ±5°, sadzenie po normalnej terenu do ~15°.

---

## 3. Rozmieszczenie (scatter)

### Rozszerzenie ForestPlacement (lub sibling `TerrainScatter`)
`ForestPlacement.Generate` (`ForestPlacement.cs:43-110`) gatuje dziś WYŁĄCZNIE na elewacji+slope (`:78,:83`) — to
źródło skargi usera. Plan:
- Nowa czysta `TerrainScatter.Generate(raster, frame, orthoCells, coverage, options)` zwraca **dwie listy**:
  `Trees` + `Rocks` (`record struct ScatterInstance(Vector3 Position, float Scale, float Yaw, byte MeshId,
  Vector3 Tint)`).
- Klasyfikator z §2 wstawiony w pętli **po slope-check, przed density-thinning**.
- Zachować deterministyczny hash (`Hash`/`Unit`, `:126-136`) keyed `(cellX, cellZ, layerId)` — każda warstwa
  niezależny, stabilny wzór.
- Wysokość: `raster.SampleBilinear × VerticalExaggeration` (ten sam bilinear co `TerrainMesh3D.cs:888`) — obiekty
  na renderowanej powierzchni.

### Gęstość per klasa — **stała jittered-grid, NIE siatka DEM**
Jedna próbka per komórka stałej world-grid, jitter z hasha (stratified/Poisson-look). Tabela docelowa:

| Warstwa | Klasa orto | Grid | Gęstość | Mesh |
|---|---|---|---|---|
| Korona drzew | VEGETATION, elev<treeline, slope<25° | 3.5 m | 0.082 /m² | świerk/drzewo A–C (hash) |
| Podszycie (krzew/sapling) | VEGETATION | 1.5 m | 0.44 /m² | krzew + małe drzewo |
| Trawa (near-only) | VEGETATION | 0.7 m | 2.0 /m² | grass card (collapse 40 m) |
| Kosodrzewina | VEGETATION, elev≥treeline OR 40–50° | — | krzewy+trawa, ZERO drzew | bush only |
| Głazy | ROCK_SCREE, slope<35° | 6 m | 0.028 /m² | duża skała A–B |
| Otoczaki/piarg | ROCK_SCREE, slope<50° | 1.8 m | 0.31 /m² | mały kamień/klaster |
| Ściana skalna | slope≥50° | — | ~0 (Density≤0.02) | — |

Sanity: korona 0.082/m² w ringu 150 m (≈70 700 m²) ≈ **5 800 drzew** — gęsty, chodliwy las. `ForestDensity`
slider (`Terrain3DView :651`) zostaje globalnym mnożnikiem.

### Cache — chunk 32 m LRU, POZA cyklem kafla DEM
**Nie** re-place'ować na referencji kafla LOD (dziś `EnsureForest :7364`) — kafle zmieniają step/extent → churn.
- Stała siatka chunków 32 m, cache LRU jak `BakedDemTileCache` (memory `lod-ram-tile-cache`). Klucz `(chunkX,chunkZ)`.
- Chunk wchodzi w `ScatterRadius≈600 m` od kamery → CPU-place raz → cache; eviction LRU poza promień (~1400 rezydentnych).
- Placement na **background task**; render-thread konsumuje gotowe listy. Determinizm (hash) → chunk wracający w
  promień = identyczne instancje → **zero shimmeru, zero per-frame rebuild**.

### Wykluczenia
- **Woda:** twardy exclude z `flatW>0` (JEDYNY dyskryminator wody, memory `water-regression-spiral` — nigdy nie
  usuwać) + snowline; nie klasyfikować wody z koloru (niebieski cień myli).
- **Szlaki:** odrzucić kandydatów w ~3 m od centerline (1 m szlak sub-cell, nierzetelny z orto) — zabija „drzewo
  na ścieżce".
- **Klify:** `slope≥50°` — materiał granitu (triplanar 55–75°, `uRockStrength`) obsługuje CIENIOWANIE ściany;
  kamienie-obiekty tylko na piargu 30–50°.

---

## 4. Render

**Cały instanced pipeline już istnieje i jest wired** (`Terrain3DGlRenderer.cs`): `EnsureForestProgram (:9356`,
3-tier conifer, 7 float bazowy + 5 float/instance divisor=1), `EnsureForestInstances (:10241)`, `DrawForest`
near (`:10270`, `DrawArraysInstanced`), `DrawForestImpostors` far (`:10204`, quad billboard), `BakeForestAtlas
(:9467`, 2048² 8×8 octahedral, 64 kierunki). **Nie potrzeba nowego GL na drzewa.**

**Zmiany:**
- **Statyczny instanced program dla mesh KayKit** (obok proceduralnego cross-card): realny VB (pos+normal+atlas-UV)
  + per-instance `(vec3 pos, float scale, float yaw, vec3 tint)`. **Jeden draw per mesh, wszystkie dzielą JEDEN
  atlas KayKit** → cały scatter ≈6–8 drawów + 1 bind tekstury.
- **Skały = druga ścieżka instanced**: `RockInstance` + `DrawRocks` klon `DrawForest` z rock-VAO, ten sam program
  atlasowy. Nie fałdować typu w jeden VBO (różne geometrie).
- **Retune LOD ringów pod walk** (dziś flyover 2500/5500/20000, `:9218-9225`, za grube dla 1 m):
  - Full mesh: **0–150 m**; Impostor (octa-atlas re-baked z KayKit): **150–600 m**, dithered crossfade 150–220 m;
    Collapse (`:9303`) drzewa/skały >800 m; **trawa >40 m**, podszycie-mesh >60 m.
- **Budżet + cull (CPU przed uploadem):** near-mesh cap 30 000, impostor 120 000, trawa ~20 000; overflow → drop
  najdalszych. **Frustum cull per chunk** (AABB `WorldMin/WorldMax`, `TerrainMesh3D.cs:74-77`), nie per instance.
- **Atlas:** feed `BakeForestAtlas` mesh KayKit zamiast proceduralnego świerka.

**Hooki:** `Terrain3DView.xaml.cs` — `EnsureForest (:7358`, dziś `Array.Empty :7371`) → realny call + `EnsureRocks`;
ortho binding `:618-627`. Renderer — pass `GpuPass.LakesForest (:3884-3895)`.

---

## 5. Fazowanie i ryzyka

### Fazy
1. **MVP — drzewa na zielonym (TDD).** `TerrainScatter.Generate` + klasyfikator jako **czyste klasy** — failing
   testy najpierw (jest `ForestPlacementTests.cs`): syntetyczny DEM + fake orto (ćwiartka zielona/szara/niebieska)
   → assert klasa→count, zero propów na wodzie, zero na ścianie 60°. Reanimacja `EnsureForest`, statyczny mesh
   KayKit, kilka tysięcy instanced drzew na zielonych pikselach. Werdykt wizualny F8.
2. **Skały na szarym** — `RockInstance` + `DrawRocks`, klasyfikator ROCK_SCREE, piarg 30–50°.
3. **LOD/impostory** — retune ringów pod walk, re-bake atlasu KayKit, budżety + chunk cull.
4. **Sezony** — powyżej snowline (`BiomeThresholds SnowElevationM=2200`, `AspectElevationShiftM=150`) **swap nie
   add**: świerk→ośnieżony, skała→snow-cap tint, trawa ukryta; sterowane tym samym snow-line co teren (`:7443`).

Za każdą warstwą: gate za toggle „Las" (opt-in), weryfikacja w apce w kilku spotach, **NIE ogłaszać sukcesu aż
user potwierdzi render**.

### Ryzyka (uczciwie)
- **„Znów brzydko"** — najgroźniejsze (raz już wyłączyliśmy las). Mitygacja = pełna lista anty-brzydota §2. To
  subiektywne — wymaga werdyktu usera, nie testu.
- **Perf close-zoom vs framerate** — grass overdraw (2/m² × alpha cards) realny hazard; twardy collapse 40 m +
  cap load-bearing. Plus znany **alloc-storm ~450 MB/s → gen2 gap** — placement musi być hash-only (zero
  RNG-state, zero per-frame alokacji). Profilować alokacje od startu.
- **Rozdzielczość/bias koloru orto** — composite ~kilka–kilkanaście m/px zaciera przejścia; smoothstep-
  probabilistyka zamiast hard-threshold; progi TYLKO w renderze 3D.
- **Ożywianie uśpionego GL** — impostor atlas/LOD strojone pod flyover; re-bake + retune to niepewny nakład.
  Trzymać za toggle.
- **`.gltf` vs `.glb` + realny poly** — potwierdzić po pobraniu.

### Kluczowe pliki
`ForestPlacement.cs:43-110` (+`Hash :126`), `BiomeClassifier.cs:124-131`, `Maps/OrthoTextureCell.cs:1-13`,
`OrthoCoverage.cs:39-56`, `MBTilesOrthoCompositor.cs:74-100`, `Terrain3DView.xaml.cs` (`EnsureForest :7358-7374`,
ortho `:618`, `ForestDensity :651`), `Terrain3DGlRenderer.cs` (`forest pass :3884-3895`, `EnsureForestProgram
:9356`, `EnsureForestInstances :10241`, `DrawForest :10270`, `DrawForestImpostors :10204`, `BakeForestAtlas
:9467`, LOD const `:9218-9225`).
