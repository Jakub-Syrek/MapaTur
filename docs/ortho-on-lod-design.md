# Design Doc — Ortofoto na streaming LOD (Faza 0)

**Status:** ZAAKCEPTOWANY (2026-06-07). Faza 1 w toku.
**Decyzje (sek. 9):** #2 MVP **bez cięcia** (kafel = komórka ortho jego środka; drobny błąd UV na styku → Faza 2).
#3 oświetlenie ortho **to samo** co hipsometria (ambient 0.5, słońce 16; osobny balans tylko jeśli za ciemne).
#6 **baza też ortho** (drape na bazę z12 i detal — bez szwu hipso/zdjęcie).
**Cel:** drapować istniejące ortofoto na kafle 1 m streaming-LOD (dziś hipsometria), żeby bliski teren
wyglądał jak realne zdjęcie ("Google Earth dla Tatr"), zachowując stabilny silnik LOD/oświetlenie.

---

## 1. Stan obecny (co JUŻ jest)

- **Ortofoto w normalnym 3D — działa.** Bundlowane `tatry-ortho-*` (auto-load: siatka **4×2**, ~8192×5462
  px/komórkę, ~1 m), composited PL (GUGiK) + SK (ÚGKK). `discovery.OrthoTilePaths/OrthoGridCols/Rows`.
- **Model komórki:** `OrthoTextureCell(Row, Col, Width, Height, byte[] Rgba)` (Application/Maps).
- **Mesh:** `TerrainMesh3D.BuildTiles(raster, …, orthoGridCols, orthoGridRows)` dzieli raster na siatkę
  `OrthoCell(ColStart,ColEnd,RowStart,RowEnd,TileIndex,Spans)`; każdy kafel ma `OrthoTileIndex` + **texCoords
  LOKALNE w komórce** (`(c−cell.ColStart)/uDenom`). Kafel NIGDY nie przekracza granicy komórki (pętla per cell).
- **Render:** `Terrain3DGlRenderer.SetOrthoTextures(list)` → tablica tekstur; shader binduje teksturę po
  `OrthoTileIndex` kafla. Mipmaps + anizotropia. `OrthoEnabled` flaga.
- **Georeferencja — NIEJAWNA:** siatka ortho mapuje **1:1 na siatkę rastra DEM**; pokrycie ortho == bounds
  załadowanego DEM (`tatry.dem`). Normalny 3D buduje CAŁY `tatry.dem` z siatką ortho → exteny pasują 1:1.
- **LOD demo CELOWO wyłącza ortho:** `OrthoTexturePath/Paths/Cells = null`, `orthoGridCols/Rows = 1`
  (hipsometria, żeby pokazać geometrię).

## 2. Luka (czemu nie działa wprost na LOD)

LOD ładuje **pod-regiony** innego extentu niż ortho:
- baza LOD = `Around(center, 3000)` (~6 km), detal = `Around(focus, 2000)` (~4 km),
- ortho pokrywa **cały** `tatry.dem` (~dziesiątki km).

Skoro texCoords są lokalne-w-komórce przy założeniu raster-extent == ortho-extent, dla pod-regionu wychodzą
**błędne UV**. Potrzebne:
1. **Geo-referenced UV** — `UV = (vertexGeo − orthoSW)/(orthoNE − orthoSW)`, niezależne od extentu rastra LOD.
2. **Resolve komórki ortho z geo** — `OrthoTileIndex` z tego, w której komórce 4×2 leży kafel.
3. **Cięcie kafla po granicy komórki ortho** — okno 4 km może przeciąć granicę komórki (~7-8 km), a kafel nie
   może próbkować dwóch tekstur (jak w base path, ale w geo).

Reużywalne bez zmian: upload/bind tekstur per `OrthoTileIndex`, sam shader, mip/aniso, rozdzielczość (~1 m
ortho ≈ 1 m detal). Sedno nowego = geo-UV + geo-resolve + geo-cięcie.

## 3. Źródła ortho

- **Teraz:** bundlowane komórki (`tatry-ortho`, PL+SK composited, ~1 m) — pokrycie = `tatry.dem`. Rezydentne
  w pamięci (RGBA8, ~1,9 GB po mip na flagowcu — patrz M11 ROADMAP). **MVP używa tego, zero nowych źródeł.**
- **Później (poza tym doc):** strumieniowe ortho GUGiK z16 (jak DEM streaming) dla pokrycia całego
  województwa — osobny milestone, NIE w zakresie ortho-na-LOD MVP.

## 4. Przepływ docelowy

```
LookAt (raycast środek/fallback)
  ↓
Detail window z16  (BuildPerTileDetailAsync)
  ↓
PerTileDetailPlanner → decyzje (crop, subsampleStep)
  ↓
per decyzja: Crop + Subsample
  ↓
BuildTiles(..., orthoCoverage: orthoBounds, orthoGridCols/Rows)   ← NOWE: geo-UV + geo-resolve + geo-cięcie
  ↓
combine z bazą (też ortho)
  ↓
Render: bind ortho tekstura per OrthoTileIndex  (istnieje)
```

## 5. Zmiana w `BuildTiles` (rdzeń, czysty, testowalny)

Nowy opcjonalny parametr (lub `TerrainMeshOptions` pole):
```
OrthoCoverage? orthoCoverage = null   // record: MapBounds Bounds; int GridCols; int GridRows
```
- `null` (default) → zachowanie obecne (UV lokalne, raster-extent == ortho-extent). **Zero regresji.**
- ustawione → dla każdego vertexa:
  - `u = (vertexLon − Bounds.West)/(Bounds.East − Bounds.West)` (clamp 0..1), analogicznie `v` po szerokości
    (uwaga na oś: row 0 = N, ortho v rośnie w dół),
  - **globalny** UV `[0,1]` w pokryciu; komórka = `floor(u*GridCols), floor(v*GridRows)`;
  - `OrthoTileIndex = row*GridCols + col`; texCoord lokalny-w-komórce = `(u*GridCols − col, v*GridRows − row)`.
- **Cięcie:** przed budową kafla policz, w której komórce ortho leży crop; jeśli crop przecina granicę
  komórki → podziel crop na sub-prostokąty po granicach komórek (geo), każdy z własnym `OrthoTileIndex`.
  (Analogiczne do obecnej pętli per `OrthoCell`, ale granice liczone z geo pokrycia, nie z indeksów rastra.)

**Why czyste/testowalne:** wejście = raster bounds + orthoCoverage; wyjście = texCoords + OrthoTileIndex.
Bez GL, bez I/O.

## 6. Wiring (VM `BuildPerTileDetailAsync`)

- Udostępnić **pokrycie ortho** (bounds `tatry.dem`) + siatkę (4×2) + komórki rezydentne w rendererze.
- Po `Crop`/`Subsample` wołać `BuildTiles(..., orthoCoverage: ortho)`.
- Baza LOD analogicznie (żeby baza też miała ortho, brak mismatchu hipsometria/zdjęcie).
- Upewnić się, że renderer ma załadowane komórki ortho w trybie LOD (dziś LOD je zeruje) — podać te same
  bundlowane komórki, których używa normalny 3D.

## 7. Pamięć

- MVP: te same rezydentne komórki co normalny 3D (bez dodatkowego kosztu — już w VRAM gdy ortho on).
- LOD nie mnoży tekstur (detal reużywa regionalnych komórek przez `OrthoTileIndex`).
- Budżet/streaming/eviction ortho = Faza 2 (i pokrywa się z M11 #39/#43 w ROADMAP).

## 8. Fazy (wg propozycji usera)

- **Faza 0 — ten doc.** Akceptacja założeń (źródła, format, przepływ, UV, cięcie, pamięć, ryzyka).
- **Faza 1 — MVP (udowodnić, że działa):** geo-UV w `BuildTiles` (TDD) + detal LOD próbkuje **jedną** komórkę
  ortho (najpierw kadr mieszczący się w jednej komórce — odłóż cięcie), render. Bez blendingu/cache/atlasów.
- **Faza 2 — robustność:** geo-cięcie po granicach komórek (kafel na styku), (opcj.) async upload, budżet
  pamięci. Tu wchodzi M11 #39/#43.
- **Faza 3 — jakość:** blending PL/SK, szwy, przejścia, ew. korekcja kolorystyczna/ośw. ortho.

## 9. Ryzyka / decyzje do podjęcia

1. **Oś V / kierunek wierszy** — ortho i raster: row 0 = N; przy geo-UV pilnować, by v nie był odbity
   (klasyczna pomyłka → ortho do góry nogami). Test jednostkowy na to.
2. **Cięcie kafli (Faza 2)** — okno 4 km vs komórka ~7-8 km: większość kadrów zmieści się w 1-2 komórkach.
   MVP (Faza 1) może wybrać tylko komórkę środka kafla (drobny błąd UV na brzegu) — zaakceptować jako MVP?
3. **Ortho a oświetlenie** — dziś ortho-drape jest mnożone przez Lambert/atmosferę. Po obniżeniu ambientu
   (0.5) i niskim słońcu ortho może być za ciemne/za kontrastowe. Możliwe, że ortho potrzebuje innego
   balansu ośw. niż hipsometria (decyzja: osobny `orthoAmbient`? czy to samo?).
4. **Rozdzielczość vs zoom** — ~1 m ortho pasuje do detalu 1 m; przy step 4/8 (dalsze kafle) ortho i tak
   wygląda OK (mip). Brak nowego problemu.
5. **Pokrycie poza `tatry.dem`** — detal w rejonie bez ortho (poza bundlowanym pokryciem) → fallback do
   hipsometrii (jak dziś `OrthoEnabled`/brak komórki). Trzeba zdefiniować zachowanie na brzegu pokrycia.
6. **Spójność baza↔detal** — jeśli baza zostaje hipsometryczna a detal dostaje ortho → szew wizualny.
   Decyzja: w MVP drapować ortho też na bazę LOD, czy zostawić bazę hipso (i zaakceptować różnicę pod detalem)?

## 10. Plan testów (Faza 1, TDD przed kodem)

- `BuildTiles_GeoReferencedOrtho_MapsVertexToGlobalUv` — vertex w SW pokrycia → UV(0, v_dolne); środek → 0.5.
- `…_PicksCorrectOrthoCellByGeo` — kafel w komórce (1,2) → `OrthoTileIndex == 1*GridCols + 2`.
- `…_VAxisNotFlipped` — N krawędź → właściwy wiersz (nie odbity).
- `…_NullCoverage_KeepsLegacyLocalUv` — bez `orthoCoverage` identycznie jak dziś (regression guard).
- (Faza 2) `…_CropStraddlingCellBoundary_SplitsPerCell`.

## 11. Definicja ukończenia (MVP/Faza 1)

Na telefonie, w LOD demo, bliska grań pokryta realnym ortofoto (zamiast hipsometrii), zgrane geometrycznie z
terenem, FPS bez regresu, brak odwróconego/przesuniętego zdjęcia. Reszta (cięcie, blending, cache) — kolejne fazy.

---

**Do akceptacji:** decyzje #2 (MVP bez cięcia?), #3 (balans ośw. ortho), #6 (baza ortho czy hipso) z sekcji 9.
Po akceptacji → Faza 1: testy geo-UV (TDD) → implementacja → wiring → device-validate.
