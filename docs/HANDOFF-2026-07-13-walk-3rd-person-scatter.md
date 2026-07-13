# Handoff — 2026-07-13: walk mode 3. osoby (komplet) + wspinaczka + auto-belay + plan scatteru orto

> Branch: **`feat/walk-mode`** (NIE pushnięty). Wszystko z tej sesji **ZACOMMITOWANE** (14 commitów, autor Jakub,
> **zero AI-atrybucji**), **1516 testów green**, build Windows 0 błędów. Bramka przed pushem: `dotnet format
> MapaTur.slnx --verify-no-changes` (⚠️ pre-existing whitespace w shaderze lasu `Terrain3DGlRenderer.cs` może
> blokować gate — zdjąć przed pushem) + pełne `dotnet test`. **NIGDY Claude jako autor/co-author.** Desktop-only.

---

## 0. TL;DR sesji
Pełny **widok 3. osoby w walk mode (F8)** z ludzikiem KayKit + rozbudowana **wspinaczka** (auto-belay: haki/lina,
stamina, mantle, swobodna kamera), **ciągłość przełączeń F7↔F8**, **fix audio smoka**, oraz **zbadany + rozpoczęty
epik gęstego scatteru lasu/kamieni sterowanego kolorem orto** (rdzeń TDD gotowy, render niezrobiony).

Dwa plany-dokumenty: `docs/PLAN-third-person-character.md` (zrealizowany) i `docs/PLAN-ortho-scatter.md` (do zrobienia).

---

## 1. Widok 3. osoby (Fazy 0–2) — ZROBIONE, przyjęte wizualnie
- **Model = KayKit „Adventurers" / `Rogue_Hooded.glb`** (CC0 1.0, 76 klipów baked) → `Resources/Raw/hiker.glb` +
  `TestData/hiker.glb`. **NIE LFS** (Resources/Raw = zwykłe bloby; LFS scoped do `data/**`). ⚠️ **Mixamo ODRZUCONE**
  (zakaz redystrybucji surowego pliku vs repo). Smoke-test `HumanoidModelTests.cs`.
- **Reużywa pipeline smoka**: renderer `SetHumanoid`/`DrawHumanoid` (ten sam `dragonProgram`/VAO/upload, uFireCount=0,
  tekstura przez `EnsureAiDragonTexture`); view `LoadHumanoidModelAsync`, `PoseAndSeatHumanoid` (skala 1.8 m, remap
  Y-up→Z-up, seating na najniższym posed-vertex).
- **Follow-kamera** zamiast oka 1. osoby (boom za+nad postacią, swobodny pitch spojrzenia, ground-clamp), **zoom
  rolką** (`walkCamBack` 1.6–12 m), sterowanie: mysz obraca postać, WASD heading-relative.
- **Strzał z kuszy (F)** + **lecąca strzała** (`arrow.glb` — repack KayKit `arrow.gltf`; pocisk ballistyczny, orient
  wg prędkości, shaft=lokalne +Y).
- **Faza 2 animacje**: `SkinnedModel.PoseBlend(animA,tA,animB,tB,weight)` (crossfade per-kość, TRS-slerp) + czysta
  `HumanoidAnimator` (sygnały→crossfade dwóch klipów; Idle↔Walk↔Run po prędkości z histerezą + speed-match,
  Jump_Idle/Land, shoot). TDD.
- ⚠️ Tuned wizualnie (nie zgadywać od zera): `HumanoidYawOffset=π/2`, arrow shaft +Y, climb arm axis `Vector3.UnitX`.

## 2. Wspinaczka (Faza 4) — ZROBIONE, przyjęte
- **Feel** (`WalkPhysics`, TDD): zmienna prędkość wg stromości (`SteepClimbGrade`/`SteepClimbFraction`), `ClimbBlend`.
- **Auto-belay** (spec usera): haki co **10 m** climb (3D), rolling **max 3** (`Pitons` lista, 4. usuwa najstarszy),
  lina, **fall-arrest** — odpadnięcie zatrzymuje zjazd o `RopeLengthMeters`=6 pod najwyższym hakiem (`IsRoped`, na
  powierzchni ściany — heightfield nie uniesie zawisu w powietrzu). Pitony czyszczone na bezpiecznym gruncie/Teleport.
- **Stamina** (`GripStamina`, TDD): **24 s** wspinaczki, drain 1/s, regen 4/s na gruncie/linie, histereza (`gripEngaged`,
  próg 3 s → trzymasz do 0 → odpadasz→lina→odpoczynek→znów). **HUD** = pasek Skia `DrawClimbStaminaHud` (green→amber→red).
- **Mantle** (top-out): sonda w górę-przód trafia chodliwą półkę → wychodzisz i stajesz.
- **Wizual wspinaczki**: proceduralne ręce (`PoseClimb`/`ApplyArmReach`, `RotateBoneOverlay` na upperarm/lowerarm — jak
  wing-beat smoka). Haki+lina rysowane markerami (`BuildPitonRopeMarkers` → `SetDebugMarkers`; pomarańczowe kule=haki,
  kropki tan=lina). ⏳ **ładniejszy model szpili + ciągła lina 3D = OTWARTE (Tier-1 cz.2)**.
- **Swobodna kamera przy wspinaczce**: LPM(climb)+PPM(drag) = orbita kamery bez zmiany headingu. ⚠️ Walk-mouse czytany
  z **żywych flag przycisków** w `OnPlatformPointerMoved` (drugi przycisk myszy NIE odpala niezawodnie `PointerPressed`
  na Windows) — nie polegać na `mouseDragButton` dla walk.

## 3. Ciągłość F7↔F8 + audio — ZROBIONE
- **Przełączenie F7↔F8** przenosi XY+heading z DRUGIEGO trybu (przechwycone PRZED zamknięciem: `dragon.PositionXY` /
  `walker.PositionXY`), nie z kamery. Wejścia z orbity bez zmian. Diag-log `[Walk] enter from=...`.
- **Fix audio smoka**: `SetFlightBed(0,0,0)` tylko fade'uje 15%/wywołanie → pętle grały po wyjściu. Dodane
  `DragonAudioService.Silence()` — twardy stop wszystkich pętli + one-shotów, wołane z `ExitDragonFlight`.

## 4. Scatter lasu/kamieni z orto — ZBADANE + RDZEŃ (render OTWARTY)
Cel: teren pusty na zbliżeniu (walk) → gęsto drzewa+kamienie wg **koloru orto**. **Pełny plan: `docs/PLAN-ortho-scatter.md`.**
- **Asset = KayKit „Forest" (CC0 1.0, zweryfikowane)** — 100+ modeli (drzewa/krzewy/skały/trawa) + jeden atlas 1024².
  ⏳ **do pobrania**, repack do `.glb`, Resources/Raw+TestData NIE LFS. Fallback: Quaternius Nature (CC0).
- **Instanced GL las JUŻ ISTNIEJE, uśpiony** (`Terrain3DGlRenderer`: `EnsureForestProgram :9356`, `DrawForest :10270`,
  `DrawForestImpostors :10204`, `BakeForestAtlas :9467`, LOD const `:9218-9225`) — **reanimować, NIE pisać od zera.**
- **ZROBIONE (TDD, 12 testów, zacommitowane):**
  - `OrthoScatterClassifier.cs` — kolor orto RGB → klasa: `Gx=g−max(r,b)>0.06`→Vegetation, `S<0.20 && 0.30<Y<0.78`→
    RockScree, `Bx>0.03`→Water, jasny+odsycony→Snow. Progi w `ScatterThresholds` (⚠️ tuning TYLKO w renderze 3D).
  - `TerrainScatter.cs` — zieleń→drzewa (below treeline, off steep), szarość→kamienie (off cliffs), gęstość jako
    pokrętło, per-instance tint z orto, deterministyczny hash. Wstrzykiwany `Func<Vector2,Vector3?> sampleOrthoRgb`
    → czysty, testowalny. Zwraca `(Trees, Rocks)` listy `ScatterInstance(Position, Scale, Yaw, MeshId, Tint)`.
- **OTWARTE (następny krok — tu mierzymy FPS):**
  1. **Sampler orto** w view (impure): world XY → `WorldToGeo` → `OrthoCoverage.CellAt/LocalUv` → `OrthoTextureCell.Rgba`
     (top-row RGBA8, box 3×3), fallback `BiomeClassifier`. Zbudować `Dictionary<int,OrthoTextureCell>` raz z
     `Terrain3DView.OrthoTextureCells`.
  2. **Wpięcie w renderer**: `EnsureForest` (`Terrain3DView :7358`, dziś `Array.Empty :7371`) → realny call
     `TerrainScatter.Generate`; reanimować `DrawForest` (najpierw proceduralny świerk = szybki test gęstości+FPS,
     potem swap mesh KayKit); dodać `EnsureRocks`/`DrawRocks` (klon).
  3. **Perf**: pokrętło gęstości (już `ForestDensity :651`) + cap instancji + frustum cull per chunk; **cache chunk
     32 m LRU** poza cyklem kafla DEM (wzorzec `BakedDemTileCache`); background place; hash-only (zero per-frame
     alokacji — inaczej gen2 alloc-storm). Retune LOD ringów pod walk (dziś 2500/5500/20000 = flyover). Mierzyć FPS z
     logu `[GL3D] PassTimes`.
  4. **Anti-„brzydko"** (bo raz już wyłączyliśmy las): nic >treeline, nic slope≥50°, zmienna skala per klasa, mix
     gatunków (2. kubełek hash), polany (low-freq noise gate), tint z orto, exclude woda `flatW>0`
     ([[water-regression-spiral]])/szlaki(~3 m)/klify. Wszystko za toggle „Las".
  5. Potem: skały na szarym → LOD/impostory (re-bake atlasu KayKit) → sezony (swap nad snowline).

---

## 5. Commity tej sesji (`feat/walk-mode`, od najstarszego)
`59d6bd6` chore(assets) hiker+arrow · `6404044` test humanoid loader · `76dfedc` feat 3rd-person avatar+kamera+zoom+
kusza+strzała · `fa5d41b` docs plan 3-osoby · `3f55de3` feat PoseBlend · `4e42508` feat HumanoidAnimator · `7529525`
feat drive avatar FSM · `fcca4fc` feat climbing feel+auto-belay · `c93bbf8` fix dragon audio Silence · `df52cfd`
feat climb anim+piton markers+F7/F8 continuity · `ad3a27a` feat grip stamina+mantle · `00ca587` feat stamina HUD+
free-look · `dbea4c0` feat ortho scatter classifier+TerrainScatter · `303c48e` docs plan ortho-scatter.

## 6. OPEN ITEMS (priorytet malejąco)
1. **Scatter render (Faza 1 dokończenie)** — sampler orto + wpięcie `TerrainScatter` do reanimowanego `DrawForest` +
   pokrętło gęstości + cap/cull → **pomiar FPS** przy gęstym lesie (główna troska usera). `docs/PLAN-ortho-scatter.md`.
2. **Pobrać KayKit Forest** (CC0) → repack `.glb` → Resources/Raw; potwierdzić realny poly + `.glb`.
3. **Tier-1 cz.2 wspinaczki (odłożone):** kadencja audio wbijania haka + ładniejszy model szpili + ciągła lina 3D.
4. **Push `feat/walk-mode`** — dopiero po `dotnet format --verify` (zdjąć pre-existing whitespace shadera lasu) +
   testy green. Bez AI-autora. Za zgodą usera.
5. Odłożone: skały-scatter, LOD/impostory retune, sezony (Fazy 2–4 planu scatteru).

## 7. Kluczowe pliki
- 3. osoba/wspinaczka: `src/MapaTur.App/Views/Terrain3DView.xaml.cs` (walk tick, follow-cam, PoseAndSeatHumanoid,
  PoseClimb, BuildPitonRopeMarkers, DrawClimbStaminaHud, input handlers, EnterWalk/DragonFlight),
  `src/MapaTur.App/Services/Terrain3DGlRenderer.cs` (SetHumanoid/DrawHumanoid, SetArrows/DrawArrows, forest pass),
  `src/MapaTur.Application/Terrain/` (WalkPhysics.cs, WalkParameters.cs, SkinnedModel.cs, HumanoidAnimator.cs).
- Scatter: `src/MapaTur.Application/Terrain/OrthoScatterClassifier.cs`, `TerrainScatter.cs`,
  `src/MapaTur.Application/Maps/OrthoTextureCell.cs`, `OrthoCoverage.cs`, `Terrain/ForestPlacement.cs`,
  `BiomeClassifier.cs`.
- Audio: `src/MapaTur.App/Services/DragonAudioService.cs` + `Platforms/Windows/DragonAudioService.windows.cs`.
- Plany: `docs/PLAN-third-person-character.md`, `docs/PLAN-ortho-scatter.md`.
