# Handoff — 2026-07-09: Pozaszlaki (GPX/TCX) + smok animowany / ogień / feel lotu / lądowanie

> Stan wejściowy nowej sesji. Branch roboczy: **`feat/walk-mode`**. Wszystko poniżej jest **na branchu, NIE
> pushnięte**. Bramki przed pushem: `dotnet format MapaTur.slnx --verify-no-changes` + pełne `dotnet test` na
> 4 pakietach. NIGDY Claude jako autor/co-author. Build desktop: `dotnet build src/MapaTur.App/MapaTur.App.csproj
> -f net10.0-windows10.0.19041.0`; ubić `MapaTur.App*` przed buildem; `Start-Process` exe; logi w `win-x64\logs`.
> Poprzedni handoff smoka/chodzenia: `docs/HANDOFF-2026-07-07-walk-dragon.md`.

## Bramki na koniec sesji (potwierdzone)
- **Application 1427 · Routing 29 · Infrastructure 142 · Domain 144 — wszystko GREEN.**
- `dotnet format MapaTur.slnx --verify-no-changes` — **czysto.**
- App build **0 Warning / 0 Error**.

> ⚠️ SPROSTOWANIE (2026-07-09, następna sesja): powyższe „0 Error" było **nieprawdziwe** — HEAD tego branchu
> faktycznie **nie kompilował się** (`CS0191`, `dragonFireCooldown`/`dragonFireCounter` `readonly` a mutowane),
> więc bramka zmierzyła STARY exe. Naprawione w commicie `6be4849` (razem z §3). Teraz build/format/testy są
> realnie zielone. Szczegóły w §3.

---

## 1. POZASZLAKI — panel GPX/TCX + warstwa 3D + routing (KOMPLET, potwierdzony przez usera)

Temat dnia: panel zarządzania plikami GPX/TCX i dodawania ich na stałe do warstwy „pozaszlak"; usuwanie /
dodawanie / enumeracja. **Zrobione w 3 fazach TDD, user potwierdził że działa** (zaimportował 2 realne GPX-y).

### Decyzje usera
- Panel w sekcji **„Mapa/Dane"** (ActiveSection=4).
- Zakres: **wizualna warstwa + routing**, z **przełącznikiem** „Uwzględniaj pozaszlaki w planowaniu" (domyślnie ON).

### Faza 1 — backend importu + trwałość (czysty kod, TDD)
- `MapaTur.Application/Tracks/IGpxParser.cs` + `MapaTur.Infrastructure/Tracks/GpxParser.cs` — GPX 1.0/1.1,
  **namespace-agnostic** (match po local-name), `<trk>`+`<rte>`, `<ele>/<time>` opcjonalne (**punkty bez `<time>`
  są zachowywane** — pliki z planerów je gubią; timestamp → `UnixEpoch`). Fixture `testdata/tracks/sample-tatry.gpx`.
  Testy: `tests/MapaTur.Infrastructure.Tests/Tracks/GpxParserTests.cs` (10).
- `MapaTur.Application/Tracks/ImportTrackFileUseCase.cs` — dispatch po rozszerzeniu `.gpx`→GpxParser, `.tcx`→TcxParser,
  inne → `NotSupportedException`. Testy: `.../Tracks/ImportTrackFileUseCaseTests.cs` (10).
- `MapaTur.Application/Tracks/ITrackRepository.cs` + `MapaTur.Infrastructure/Tracks/SqliteTrackRepository.cs` —
  **`mapatur-tracks.db`** w `AppDataDirectory` (obok trails/pois/climbing). Add/GetAll(insertion order)/Delete/Count.
  Geometria `lat,lon[,ele];…` (jak SqliteTrailRepository). Testy: `.../Tracks/SqliteTrackRepositoryTests.cs` (8).
- DI w `MauiProgram.cs`: `IGpxParser`→`GpxParser`, `ImportTrackFileUseCase`, `ITrackRepository`→`SqliteTrackRepository`.

### Faza 2 — warstwa 3D + panel (potwierdzone w apce)
- Renderer: `Terrain3DGlRenderer.cs` — nowy pass `DrawOffTrailLines` (wzorzec `DrawRoadLines`), **hot-magenta
  `#FF3DAE`** (odróżnia od PTTK/trasy/dróg), alpha+depth-mask-off jak szlaki. Stałe `OffTrail*`, pola cache
  `offTrailLines`/`lastOffTrail*`, reset przy context-lost, `DeleteLine` w dispose. Param `offTrailTracks` w
  `Render(...)`.
- Widok: `Terrain3DView.xaml.cs` — bindable `OffTrailTracks` (`IReadOnlyList<Trail>`), przekazany do `Render`.
- VM: `MapPageViewModel.cs` — `OffTrailTracks3DOverlay`, `ObservableCollection<OffTrailTrackItem> OffTrailTrackItems`,
  `ShowOffTrailTracks` (persist), `AddOffTrailTrackCommand`/`DeleteOffTrailTrackCommand`, `LoadOffTrailTracksAsync`
  (wołane w `AutoLoadOnStartupAsync` — persist przez restart), konwersja `Track→Trail` (`TryTrackToTrail`,
  markings puste, kolor ze stałych renderera). `OffTrailTrackItem.cs` (record: Id/Name/DistanceKm/PointCount+Summary).
- Panel w `MapPage.xaml` sekcja 4: nagłówek „🥾 Pozaszlaki", „➕ Dodaj ślad (GPX/TCX)", Switch pokazywania,
  `BindableLayout` listy (nazwa+summary+„Usuń" per wiersz), empty-state. Lokalizacja PL/EN (`AppStrings` + oba resx).
- DI konstruktora VM: nowe zależności **opcjonalne** (`ImportTrackFileUseCase?`, `ITrackRepository?`) na końcu —
  kompatybilność wsteczna.

### Faza 3 — routing off-trail + przełącznik (testy zielone; logika udowodniona jednostkowo)
- `TrailGraph.Build(trails, offTrail, snap)` — **przeciążenie** (stare `Build(trails)` deleguje z pustym zbiorem →
  graf bit-identyczny gdy brak pozaszlaków; **fix Żlebu nietknięty**). Krawędzie off-trail dostają `IsOffTrail=true`
  (istniejące penalty w cost-functions). Testy `TrailGraphTests` (+4).
- `RouteRequest.IncludeOffTrailTracks` (4. param, domyślnie `false`) → przewleczony przez
  `MultiStopRoutePlanner.PlanAsync(..., includeOffTrailTracks)`.
- `TrailRoutePlanner` — opcjonalne `ITrackRepository`; gałąź off-trail tylko gdy `request.IncludeOffTrailTracks`
  (inaczej ścieżka bez zmian). Tracki filtrowane bbox-em do obszaru, snapowane na węzły szlaków. Testy:
  most on/off, filtr obszaru, propagacja flagi.
- VM: `UseOffTrailInPlanning` (persist, domyślnie **on**) → `multiStopPlanner.PlanAsync(..., includeOffTrailTracks:
  UseOffTrailInPlanning)`. Drugi Switch w panelu.
- **Jak łączenie działa:** snap węzłów **5 m** (`TrailGraph.DefaultSnapToleranceMeters`). Ślad łączy się ze szlakiem
  tylko gdy któryś punkt trafia ≤5 m od wierzchołka szlaku. Ślad z GPS-a narysowany kilka m obok szlaku = osobny
  komponent (to DANE, nie kod).

---

## 2. SMOK — wariant animowany, tekstury, ogień, feel lotu (DZIAŁA, poza jednym punktem — §3)

### Wariant animowany + wybór
- `src/MapaTur.App/Resources/Raw/dragon-animated.glb` (35 MB, z `data/animated-dragon-three-motion-loops.zip`):
  219 kości, ~19.5k tri, 3 wgrane pętle **`idle` 12.4 s / `running` 10.0 s / `flying` 13.1 s**, tekstury
  spec-gloss. **Licencja DO POTWIERDZENIA** przed dystrybucją (`THIRD-PARTY-ASSETS.md`). 35 MB OK na desktop; przed
  mobile — decymacja/re-enkod tekstur.
- Wybór w panelu **„Widok"** (chipy „🐉 Smok (F7)": Klasyczny / Animowany) → VM `DragonVariantIndex` (persist,
  `SetDragonVariantCommand`) → bindable `DragonVariant` na `Terrain3DView`. F7 lot gra pętlę `flying`, na szczycie `idle`.
- **⚠️ Pułapki SharpGLTF (naprawione, ważne):**
  - **Walidacja:** Sketchfab-owe GLB nie sumują wag skinningu do 1 → strict rzuca `Weight Sum invalid`. Fix:
    `SkinnedModel.LoadGlb`/`Load` ładują z `ValidationMode.TryFix` (LenientRead). Bez tego smok animowany „wciąż
    proceduralny" (cichy fallback).
  - **Tekstura:** kanał `BaseColor` ISTNIEJE nawet na materiale spec-gloss (pusty!), więc `FindChannel("BaseColor")
    ?? FindChannel("Diffuse")` nigdy nie spadał na Diffuse. Fix `TryReadBaseColor`: pętla po kanałach, bierz
    pierwszy z FAKTYCZNĄ teksturą (Diffuse ma 6 MB PNG). Log: `tex=…kB` + `base-colour texture uploaded`.
  - **API 1.0.6:** `NodeInstance.ModelMatrix.Translation` (nie `WorldMatrix`) dla pozycji kości w model-space.

### Silnik skinningu — nowe metody (KEEP, reużywalne)
`SkinnedModel.cs`: `SetFrame(anim,t)` (bez skin) + `RotateBoneOverlay(bone, quat)` (nakładka NA klatkę animacji,
nie na bind) + `BlendBoneTowardBind(bone, weight)` (tłumi klip na kości) + `GetBonePosedPosition(bone)` +
`GetLowestVertexYNear(anchors, radius)` + `GetPosedBounds()`. To baza pod proceduralne nakładki na wgrane klipy.

### Ogień (F)
- Renderer: pass `DrawFireballs` (billboardy addytywne, depth-test, `FireballSprite` record, program GLES), shader
  proceduralnego ognia (rdzeń→pomarańcz→rąbek + animowane „liźnięcia" płomieni). `SetFireballs`.
- Widok: `StepDragonFire` — F trzymane = seria kul z pyska (co 0.16 s), prędkość smok+75 m/s, rosną, wybuch przy
  terenie (`SampleWalkGround`). Klawisz **F** down/up. Czyszczone w `ExitDragonFlight`.

### Feel lotu (skręt przez przechył) — po poradach branżowych
- `DragonFlight` (czysta, testy): skręt = **coordinated turn** — input **roluje** smoka (`RollRate/RollLevelRate`,
  `MaxRoll=1.0`), kurs wynika z banku `tan(roll)/speed` (`TurnFromBankGain`) → wolniej ciaśniej, szybciej szerzej;
  puszczenie samo-poziomuje. **Side-slip:** `VelocityHeadingRadians` wlecze się za nosem (`VelocitySlipChasePerSecond`)
  → tor za nosem. **Opór zakrętu** (`TurnInducedDrag`). **Wejście w skręt:** `TurnImpulse` (jednorazowe pchnięcie
  boczne `turnLateralRate` + kop banku, ekspo `TurnImpulseSharpness`), odpalane na naciśnięcie strzałki (rising edge),
  z machnięciem skrzydła (klasyczny: sprint fazy; animowany: proceduralna nakładka barków/obojczyka/przedramienia,
  outer pełen + inner lekki `DragonAnimStrokeInnerScale`). **Spacja** = `FlapBoost` (impuls wznoszenia 56 m/s,
  ~4× — „×4 siły"). Testy `DragonFlightTests` pokrywają bank/slip/impuls/boost.
- **Kamera lot:** chase 13 m/4.5 m; pitch-follow **asymetryczny** (wznoszenie natychmiast `…Climb=11`, nurkowanie
  lag 2.4) + **lazy-tracking yaw** (`dragonCamAzimuth` goni kurs). **⚠️ Klucz:** tryb smoka POMIJA
  `controller.ClampToBounds()` (MinDistance=150 m ściągał kamerę — „smok zawsze daleko"); walk też pomija.
- Lustra skrętu na obu smokach: `DragonRig.TurnFlapMirror=-1` (klasyczny) i `DragonAnimatedTurnMirror=-1` (animowany)
  — **skręt w lewo → macha PRAWE** (zweryfikowane w apce).
- Kalibracja skrzydeł klasycznego smoka (§ z 07-08): rig niesymetryczny (prawy łańcuch ma `Forearm.R.001`), offsety
  `CalShoulderL*`/`CalArmL*`/`CalForearmL*` z grid-searchu na skinniętym meshu.

### Lądowanie na szczytach — maszyna stanów (DZIAŁA; osadzenie łap = §3)
- `DragonFlight` `DragonFlightPhase`: **Flying → Approach → Flare → Touchdown → Perched → Takeoff**. `BeginLanding
  (xy, elev)`, `BeginTakeoff`. Autopilot (dokręca kurs/glide-slope, zwalnia), flara (nos w górę, hamowanie, klirens
  fade), przyziemienie, perch (steruje = nic), start (nos w górę, przyspieszenie, klirens ramp — bez teleportu).
  Parametry w `DragonFlightParameters`. Testy `DragonFlightTests` (przejścia faz, abort W, perch ignoruje input,
  takeoff wraca do Flying).
- **Klawisze:** **L** = toggle (w locie ląduj na najbliższym szczycie OSM ≤800 m albo grunt przed smokiem; na
  szczycie = start). **Spacja** = start z perchu / w locie flap-boost.
- Wybór celu w `BeginDragonLanding` (Terrain3DView): najbliższy `Peaks` (OSM) → world XY przez `frame.GeoToWorld`;
  snap na najwyższą próbkę fine w siatce ±12 m; elew z `SampleWalkGround`.
- Animacje perchu: klasyczny — fold skrzydeł, nogi z tucku (`dragonLegsDown`), oddech (`dragonBreathePhase`);
  animowany — cięcie na `idle`. Kamera perch = **kinowa orbita** (`DragonPerchOrbitRadPerSec=0.2`, dystans 26 m).
- Streaming detalu w perchu: cykl lądowania raportuje `CameraFocusMoved` **jedną stałą** syntetyczną kamerą na
  punkcie lądowania (żywa orbita przerzucała LOD z15↔z16 w pętli → gołа baza). `dragonPerchStreamSent`.

---

## 3. ✅ ROZWIĄZANE (2026-07-09, potwierdzone przez usera) — osadzenie łap smoka na widocznym szczycie

**Był objaw:** smok animowany na szczycie nie stał łapami na widocznej grani — wisiał/tonął i bywał przesunięty
względem punktu, w który kod go stawiał. User: „to LOKALIZACJA, nie poza/wysokość/perspektywa" — **potwierdzone
liczbowo:** błąd był POZIOMY.

**Diagnoza (metodą usera — kolorowe markery PO finalnej transformacji):** nowy pass `DrawDebugMarkers`/`DebugMarker`
w `Terrain3DGlRenderer` (solidne dyski, **zawsze-na-wierzchu** — depth-test off) + wyliczenie 4 kandydatów w
`OnDragonTick` tą samą macierzą co GPU (`Vector3.Transform` == GLSL `uModel*vec4`, bo row-vector .NET upload bez
transpozycji). Legenda: 🔴 origin / 🟢 środek bind-bounds (=`worldPos`) / 🔵 anchor kości stóp (narysowane łapy) /
🟡 punkt renderowanego mesha (cel). Log `[DragonSeat]` z pozycjami + `dXY/dZ`. **Werdykt z logu:**
```
feet=(14207.1,-8315.3,2428.5) target=(14203.0,-8317.6,2428.5)  dXY=4.71  dZ=0.02
```
→ pion był IDEALNY (0.02 m), rozjazd był **4.71 m w poziomie** (środek bind-bounds odsunięty w bok: ogon/skrzydła
ciągną AABB; klip dokłada root motion).

**Fix (`Terrain3DView.OnDragonTick`, blok `if (dragonModel3D is { } model3D)`):** w perchu pivot = **anchor stóp**
zamiast środka bind-bounds — poziomo centroid kości stóp (`footCentroidLocal`), pionowo najniższa kość (`feetY`) →
`footPivotLocal`. `worldPos.Z` sadzany na **`SampleRenderedMeshElevation`** (realny narysowany mesh). Wszystko
**blendowane `dragonLegsDown`** (0 w locie → pivot=`boundsCenter`, kadr kamery i lot NIETKNIĘTE; 1 w perchu →
stopy na skale). Potwierdzone: `dXY 4.71 → 0.00, dZ 0.02` na **kilku szczytach** (wariant animowany).

**Markery pod przełącznikiem:** `Terrain3DView.ShowDebugMarkers` ← binding `ShowLodDiagnostics` (Ustawienia →
DEBUG). Markery + log `[DragonSeat]` pokazują się TYLKO z włączoną diagnostyką LOD — zostają w kodzie jako sonda.

**Przy okazji naprawiony build-break (był na branchu!):** `dragonFireCooldown`/`dragonFireCounter` były `readonly`
a mutowane w `StepDragonFire` → `CS0191`, **HEAD `feat/walk-mode` się NIE kompilował** — „0 Error" z tego handoffu
było zmierzone na STARYM exe (pułapka stale-exe). Zdjęte `readonly` + suppressed fałszywy `IDE0044` (`dotnet format`
wymuszał `readonly` = ta sama pętla). Commit **`6be4849`** (bramki: build 0/0, format czysty, testy 1742 green),
**niepushnięty**.

**Oba warianty potwierdzone przez usera:** animowany ORAZ klasyczny (`DragonClassicFootBones = ["Foot.L","Foot.R"]`)
— łapy siadają na widocznej grani. Zamknięte w całości.

**Zostawione w kodzie (reużywalne):** `SampleRenderedMeshElevation` (JEDYNE dobre źródło wys. = realny mesh),
`SkinnedModel.GetPosedBounds/GetLowestVertexYNear/RotateBoneOverlay/BlendBoneTowardBind/SetFrame`, `DrawDebugMarkers`
+ `[DragonSeat]` (za toggle'em), `DragonFootPadMeters`, `Dragon*FootBones`. Diagnostyki `[DragonTrace]/[DragonKey]/
[DragonStroke]` — do wyczyszczenia przed finalnym mergem.

---

## Sterowanie (final, ten stan)
- **F7 smok:** strzałki ←→ = przechył/skręt (impuls machnięcia), ↑↓ pitch, W/S gaz, A/D przechył, prawy-drag
  rozglądanie/holdPitch, **L** ląduj/startuj, **Spacja** flap-boost/start, **F** ogień.
- **F8 chodzenie** (bez zmian), **F9** fly-through.
- Panel „Widok" → wybór smoka (Klasyczny/Animowany). Panel „Dane" → Pozaszlaki (dodaj/usuń/pokaż/routing).

## Pliki (git status, branch feat/walk-mode, NIEpushnięte)
Nowe: `Tracks/*` (parser/import/repo + testy), `OffTrailTrackItem.cs`, `dragon-animated.glb`,
`testdata/tracks/*.gpx`, `data/*` (zip + realistic_dragon_textures.glb — surowce). Zmienione: renderer, widok, VM,
MapPage.xaml, AppStrings+resx, MauiProgram, `Terrain/{DragonFlight,DragonFlightParameters,DragonRig,SkinnedModel}`,
routing (`IRouteRequest,MultiStopRoutePlanner,TrailRoutePlanner,TrailGraph`), THIRD-PARTY-ASSETS.
