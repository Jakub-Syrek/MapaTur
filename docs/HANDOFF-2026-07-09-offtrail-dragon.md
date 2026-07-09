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

## 3. ⚠️ OTWARTY PUNKT — osadzenie łap smoka na widocznym szczycie (NIE ROZWIĄZANE)

**Objaw (potwierdzony przez usera, ~35 iteracji):** smok animowany na szczycie **nie stoi łapami na widocznej
grani** — raz wisi kilka m nad skałą, raz jest w teksturze, i bywa **przesunięty względem punktu, w który kod
go stawia**. User: „to LOKALIZACJA, nie poza/wysokość/perspektywa".

**Stan kodu TERAZ:** osadzenie **cofnięte do prostej działającej wersji** — `Terrain3DView.OnDragonTick` blok
`if (dragonModel3D is { } model3D)`: centr na **bind** `boundsCenter`, `worldPos = (PositionXY, (elev+seat)*exagg)`,
seat = flightSeat↔perchSeat(feetY z najniższej kości stopy) blendowany `dragonLegsDown`. **Smok w locie i przy L
jest widoczny i w dobrym kadrze.** Łapy mogą wisieć ~3 m na ostrych szczytach — ZAAKCEPTOWANE tymczasowo.

**Co ustalono (twarde fakty, nie teoria — NIE zaczynać od zera):**
1. **`exagg=1.00`** na testowanych szczytach SK — przewyższenia NIE ma. Wszystkie moje teorie o „seat×exagg" były
   nietrafione (choć fix jest poprawny na wypadek Pion>1).
2. **Kości stóp = realne pazury** (sonda offline: `l_ball/r_ball/l_toeA/r_toeA` w pozie idle mają Y≈0.004, a
   `posedMin.Y=-0.004` — czyli są przy samym dole). Nie trzeba szukać innych kości.
3. **Debug-marker (kolumna fireballi w world-XY/Z smoka) UDOWODNIŁ rozjazd:** rysowany model NIE pokrywa się z
   punktem, w który kod go stawia. To transform/lokalizacja.
4. **`GetPosedBounds()` (środek POZY, nie bind) mocno zmniejszył rozjazd** — klip `idle`/`flying` **przesuwa siatkę
   (root motion)**, więc centrowanie na bind-środku odsuwa model. ALE zastosowane w LOCIE psuje kadr kamery
   (kamera celuje w worldPos=bind-środek) → smok poza kadrem. Musi być **tylko w perchu**.
5. **Foot-pivot** (centr poziomo na centroidzie stóp zamiast środka boxa — długi ogon/skrzydła ciągną AABB-środek
   w bok) zmniejszył dalej, ale **nadal została resztka** i też przesuwał smoka względem kamery (musi być perch-only).
6. **`SampleRenderedMeshElevation(x,y)`** (próbkuje realny trójkąt narysowanego mesha — JEDYNE poprawne źródło
   wysokości, `DetailElevation`/fine/base wszystkie się rozjeżdżały) + korekcja Z stóp działała w Z, ale problem
   jest też w XY.

**Recepta na następną sesję (droga usera, słuszna):** narysować **kolorowe** markery PO finalnej transformacji na:
(a) origin modelu, (b) `boundsCenter`, (c) kości stóp, (d) docelowy punkt mesha — zobaczyć **która kropka jest przy
widocznych pazurach**. Potem przesunąć CAŁY model tak, żeby **foot-anchor (nie pivot)** wylądował na tym punkcie —
XY **i** Z — **tylko w perchu**. Do tego: dodać kolor do `FireballSprite` (vertex-attribute tint) albo osobny
mini-pass punktów.

**Co ZOSTAWIONE w kodzie do reużycia (NIE KASOWAĆ — user wyraźnie prosił):**
- `Terrain3DView.SampleRenderedMeshElevation(worldX, worldY)` — sampler realnego mesha (bbox-reject + trójkąt +
  bary-interp, world Z ÷ exagg). To jest poprawne źródło „gdzie jest narysowana skała".
- `SkinnedModel.GetPosedBounds()` / `GetLowestVertexYNear()` / `RotateBoneOverlay()` / `BlendBoneTowardBind()` /
  `SetFrame()` — introspekcja/nakładki pozy.
- Pola `dragonSeatLogAccum`, `dragonPerchGroundElev` (pragma-suppressed CS0169/CS0414/IDE0044 — komentarz „KEEP").
- `DragonFootPadMeters`, `DragonAnimatedFootBones`/`DragonClassicFootBones`.
- Technika **markera** (emit `FireballSprite` w punkcie świata = wizualizacja gdzie kod myśli że coś jest).
- Diagnostyki logu: `[DragonSeat]`, `[DragonTrace]`, `[DragonKey]`, `[DragonStroke]` (do wyczyszczenia przed
  finalnym mergem, ale przydatne w następnej sesji).

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
