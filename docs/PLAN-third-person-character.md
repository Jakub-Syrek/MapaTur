# Plan: 3rd-person humanoid (Janosik walk mode) — model, asset, integracja, kamera, animacje

> Badanie ultracode (27 agentów, 15 kandydatów zweryfikowanych adwersaryjnie, 8 rekomendowanych).
> Deliverable badawczo-planistyczny — NIE kod. Fizyka `WalkPhysics` + loader `SkinnedModel` już istnieją;
> trzecia osoba to głównie kamera podążająca + drugi model + spięcie stanów fizyki z klipami.

## TL;DR (executive summary)
- **Model: KayKit „Character Pack: Adventurers"** (Kay Lousberg), postać `Rogue_Hooded.glb` — **CC0**, jeden
  natywny GLB z **76 wypełnionymi klipami**, ~3.66 MB, 41 kości. Zero konwersji, ładuje się przez istniejący
  `SkinnedModel.LoadGlb` dokładnie jak `dragon-animated.glb`. Zarazem najczystsza licencyjnie „wrzutka" i
  najbogatszy pojedynczy plik.
- **Nakład: ~3–5 dni roboczych** na MVP+wspinaczkę: MVP (model w 3. osobie idący za fizyką) ~1 dzień,
  follow-kamera ~1 dzień, FSM animacji ~1 dzień, procedural climb overlay + polish ~1–2 dni.
- **Największe ryzyko:** kolizja kamera↔teren na graniach (Orla Perć) — długi boom „mieszka w skale";
  rozwiązane asymetrycznym spring-armem (snap-in / ease-out) na `SampleWalkGround`. Drugorzędne: brak klipu
  `climb` (pokrywamy proceduralnie, jak wing-beat smoka) i koszt CPU-skinningu drugiego modelu na telefonie.
- **Pułapka do ominięcia:** Mixamo/ActorCore — licencja zabrania **redystrybucji surowego pliku**, a MapaTur
  commituje modele do repo (Git LFS, commit `cfdfe8f`). To dyskwalifikuje Mixamo mimo „darmowości". Bierzemy
  wyłącznie **CC0/CC-BY** (można bundlować i shipować).

---

## 1. Rekomendacja modelu

Ranking wg: licencja (bundle+ship) × gotowość klipów × koszt konwersji × dopasowanie do górskiego klimatu.

### Pick #1 — KayKit „Adventurers" / `Rogue_Hooded.glb` ⭐ (najczystsza licencja + drop-in)
- **Licencja:** **CC0 1.0** (itch.io + GitHub `LICENSE.txt`). Bundle + ship + modyfikacja + commit do repo —
  wszystko dozwolone, atrybucja niewymagana. Prośba autora „nie odsprzedawaj pojedynczo" to grzeczność, nie
  ograniczenie prawne — nas nie dotyczy.
- **Klipy (76, wbudowane w GLB):** `Idle`, `Walking_A/B/C`, `Walking_Backwards`, `Running_A/B`,
  `Running_Strafe_Left/Right`, `Jump_Start/Idle/Land`, `Jump_Full_Long/Short`, `Dodge_Forward/Backward`,
  `Interact`, `PickUp`, `Sit_Floor_Down`, `Death_A`… Pełne locomotion pod nasz FSM.
- **Format/konwersja:** natywny **GLB (glTF 2.0)**, `POSITION/NORMAL/TEXCOORD_0/JOINTS_0/WEIGHTS_0`, klipy po
  nazwie/indeksie. **Koszt konwersji = zero.** Wrzucamy do `Resources/Raw/`, ładujemy jak smoka.
- **Rozmiar/mobile:** ~3.66 MB, 41 kości (vs 219 smoka) — lekki, mobile-friendly, tani w CPU-skinningu.
- **Dlaczego:** jedyny kandydat, który jest **jednocześnie** CC0, pojedynczym plikiem z wypełnionymi klipami i
  lekki. `Rogue_Hooded` (kaptur, sylwetka wędrowca) to najlepszy stand-in górala z tej paczki.
- **Luka:** brak klipu `climb`. **Nie blokuje** — wspinaczkę pokrywamy proceduralnie
  (`SkinnedModel.RotateBone/RotateBoneOverlay`), tą samą metodą co wing-beat smoka bez baked clip.
- URL: https://kaylousberg.itch.io/kaykit-adventurers

### Alternatywa #2 — Quaternius: Universal Base Characters + Universal Animation Library 2 (najbogatsza animacja)
- **Licencja:** **CC0** (obie paczki). Bundle+ship OK.
- **Klipy:** 130+ (42 free) — walk/run/idle/jump, **parkour**, wersje root-motion i **root-motion-disabled**
  (bierzemy disabled, bo pozycję pcha `WalkPhysics`).
- **Konwersja:** **niezerowa** — mesh (Base Characters) i animacje (Animation Library) to **osobne downloady**;
  trzeba w Blenderze/gltf-transform przenieść akcje na wspólny szkielet i wyeksportować **jeden** GLB z baked
  clipami. Szkielety pasują (retarget = kopiuj akcje), ~2–4 h + weryfikacja `JOINTS_0/WEIGHTS_0`.
- **Kiedy brać:** jeśli chcemy bogatszy zestaw ruchów (parkour do wspinaczki) i akceptujemy jednorazowy merge
  w Blenderze. To jest ścieżka „richest-animation".
- URL: https://quaternius.itch.io/universal-base-characters

### Alternatywa #3 — Kenney „Animated Characters 3" (bezpieczny fallback)
- **CC0**, GLB bezpośrednio, `Idle/Running/Jump`. Styl mocno „klockowy" (Minifig) — słabszy klimatycznie, ale
  zero ryzyka licencyjnego i zero konwersji. Rezerwa, jeśli KayKit z jakiegoś powodu odpadnie.

### Tylko jako asset testowy (NIE bohater)
- **Fox** (CC0 mesh + CC-BY rig): **czworonóg**, do walidacji pipeline'u; **brak NORMAL** → oświetlenie płaskie.
- **CesiumMan** (CC-BY + znak towarowy Cesium wtopiony w teksturę): tylko 1 klip `walk`, logo trzeba
  przemalować. Oba do smoke-testów loadera, nie do shipu.

### Odrzucone — pułapka redystrybucji
- **Mixamo XBot/YBot/auto-rig:** „royalty-free do osadzenia", **ale FAQ wprost zabrania „free distribution of
  character or animation raw files".** MapaTur trzyma modele w repo (Git LFS) → commit surowego GLB =
  naruszenie. Dodatkowo klipy nie są dostarczane baked (osobne FBX per-clip → merge/retarget ~6–8 h) i niosą
  root-motion. **Odrzucone.**

**Werdykt:** **KayKit `Rogue_Hooded` jako model docelowy.** Zero konwersji, CC0, lekki. Jeśli później zabraknie
ruchów — dołożyć klipy z Quaternius UAL2 (CC0) przez jednorazowy merge.

---

## 2. Ścieżka assetu

### Wariant A — KayKit (rekomendowany, CC0-direct, zero konwersji)
1. Pobierz paczkę z itch.io → `Characters/gltf/Rogue_Hooded.glb`.
2. Skopiuj do `src/MapaTur.App/Resources/Raw/hiker.glb` (wildcard `MauiAsset` w `MapaTur.App.csproj:112` ogarnia
   bundling — **bez edycji .csproj**).
3. Commit jako **zwykły blob** (Resources/Raw NIE jest Git LFS — LFS jest scoped do `data/**`; tak samo
   `dragon-animated.glb` leży jako normalny blob). ~3.6 MB, akceptowalne.
4. Smoke-test loadera (przed integracją wizualną): `SkinnedModel.LoadGlb(bytes)` → sprawdź `model.Animations`
   (loguj nazwy/indeksy), `model.Primitives.Count > 0`, po `Pose(0,0f)` `PosedPositions` niepuste,
   `BoundsMin/Max` nie NaN, `BaseColorImageBytes` dekoduje się w SkiaSharp.
5. Zmapuj indeksy klipów po nazwie (wzorzec z `Terrain3DView.xaml.cs:2468–2483`): `Idle`, `Walking_A`,
   `Running_A`, `Jump_Start/Idle/Land`. Brakujący `climb`/`hang`/`slide` → obsługa proceduralna (§5, §6).

### Wariant B — Quaternius (jeśli chcemy climb/parkour z klipów)
1. Pobierz **Universal Base Characters** (mesh) + **Universal Animation Library 2** (klipy), oba CC0.
2. Blender lub `gltf-transform`: zaimportuj mesh, nałóż **root-motion-DISABLED** akcje na wspólny szkielet,
   przytnij do potrzebnych klipów (idle/walk/run/jump/parkour), **wyeksportuj jeden GLB** z baked animation
   channels.
3. Nazwij klip główny lokomocji spójnie; zweryfikuj po eksporcie `JOINTS_0/WEIGHTS_0` + indeksy klipów.
4. **Mobile:** nie pakuj 130 klipów — utnij do ~10 (idle/walk/run/jump/parkour/slide). Docelowy GLB trzymaj
   < ~5 MB. `dragon-animated.glb` (35 MB) jest desktop-only; humanoid ma być lean na telefonie.

### Wpis atrybucji — `THIRD-PARTY-ASSETS.md`
KayKit jest CC0 (atrybucja niewymagana), ale dokumentujemy pochodzenie:

```markdown
## 3D Models

### Hiker character (walk-mode 3rd-person avatar)
- File: src/MapaTur.App/Resources/Raw/hiker.glb
- Source: KayKit "Character Pack: Adventurers" (Rogue_Hooded), Kay Lousberg
- URL: https://kaylousberg.itch.io/kaykit-adventurers
- License: CC0 1.0 Universal (public domain) — no attribution required; listed as courtesy.
```

Gdyby wybrano **Fox/CesiumMan** (CC-BY) — atrybucja **obowiązkowa** na ekranie „O aplikacji": PixelMannen,
tomkranis, AsoboStudio/scurest (Fox) lub Cesium (CesiumMan, + przemalowana tekstura bez logo).

---

## 3. Integracja w kodzie

Cała ścieżka renderu smoka jest reużywalna dla humanoida — to CPU-skinned glTF przez ten sam program GL.

### 3.1 Ładowanie modelu
- Nowa metoda `LoadHumanoidModelAsync()` w `Terrain3DView.xaml.cs`, wzór z `LoadDragonModelAsync`
  (`:2444–2503`): `FileSystem.OpenAppPackageFileAsync("hiker.glb")` → `SkinnedModel.LoadGlb(bytes)` → zapis do
  pola `humanoidModel3D`, mapowanie indeksów klipów (`:2468–2483`).
- Wywołaj przy wejściu w walk mode (`EnterWalkMode`, `:2227–2276`).

### 3.2 Rysowanie / skala / orientacja / seating
- W `Terrain3DGlRenderer.cs` dodaj `SetHumanoid(model, world, normalRot, light, visible)` — **analog
  `SetDragon`** (wołanego z `:6958`). Reużywa `dragonProgram`, `UploadAndDrawDragonPrimitives()`
  (`:7618–7666`), depth `Lequal`.
- **Skala:** `scale = HumanoidHeightMeters / model.LocalExtent`, gdzie `HumanoidHeightMeters ≈ 1.8` (vs
  `DragonModelSizeMeters=24`). Reużyj kompozycji macierzy z `:3293–3349`:
  - `remap = CreateRotationX(π/2)` (Y-up glTF → Z-up świata) — bez zmian.
  - `yawRot = CreateRotationZ(walkHeadingRadians + HumanoidYawOffset)` — `HumanoidYawOffset` dobierz tak, by
    „przód" modelu = kierunek marszu (do potwierdzenia wizualnie, jak `DragonYawOffset=π/2`).
  - **Bez pitch/roll lotu** (`DragonPitchSign/RollSign` → 0 w trybie chodu).
- **Seating na ziemi:** zamiast wysokości lotu ustaw `worldZ = w.FeetElevation * exaggeration`, pivot =
  **stopy** (foot-anchor), nie bounds-center. Logika osadzania stóp już istnieje dla przysiadu smoka
  (`:3335–3345`) — reużyj.

### 3.3 Podmiana pierwszej osoby na follow-kamerę
- Dziś walk mode jest **first-person**: `OnWalkTick` (`:2334–2339`) buduje `eye` na `w.EyeElevation*exaggeration`
  i woła `ApplyFreeCamera(eye, eye+look*250)`. **`ApplyFreeCamera` (`:1861`) zostaje jedynym punktem wejścia** —
  follow-kamera liczy pozycję kamery + look-at i karmi tę samą metodę. Renderer/`Camera3D` bez zmian.
- Zastąp blok `:2334–2339` algorytmem follow-kamery (§4).
- **Rysuj ciało:** usuń/wyłącz `DrawWalkViewmodel` (`:3578–3639`, dwie ciupagi Skia) w 3. osobie — zamiast
  overlay Skia renderuj model 3D przez `SetHumanoid`. (Ciupagi zostają jako overlay tylko jeśli utrzymamy
  opcjonalny tryb 1. osoby.)

### 3.4 Move wish → camera-relative
- Dziś `wish` liczony z `walkHeadingRadians` (`:2317–2332`), a heading == yaw kamery (jedna zmienna).
  **Rozdziel:**
  - `camYaw` — orbita kamery, sterowana myszą/klawiszami look (mouse-look już pisze deltę: `:5665`,
    `WalkMouseLookRadiansPerPixel=0.005`).
  - `walkHeadingRadians` — kierunek **ciała**, podąża za `wish`, nie za myszą.
- Schemat (BOTW/Death Stranding, terrain-friendly):
  ```
  fwd=(cos camYaw,sin camYaw); right=(sin camYaw,-cos camYaw)
  wish = W*fwd - S*fwd + D*right - A*right           // względem KAMERY
  if wish≠0: walkHeadingRadians = SmoothDampAngle(walkHeadingRadians, atan2(wish.y,wish.x), 0.10, dt)
  w.Step(dt, normalize(wish), speed, jump, hangHeld: walkLmbDown)   // seam bez zmian, :2332
  ```
- **`WalkPhysics.Step` i cała lokomocja pozostają nietknięte** — pętla fizyki jest już właścicielem
  ruchu/kolizji/gaitu.

---

## 4. Kamera 3. osoby (spec)

Zdekuplowany **orbit boom** z krytycznie tłumionym wygładzaniem na 3 kanałach (anchor, kąty, długość boomu).
Kluczowa zasada terenu: **snap-in natychmiast, ease-out z opóźnieniem** (nigdy nie interpoluj *w* zbocze).
Wszystko w metrach realnych; exaggeration na Z tylko przy finalnym umieszczeniu.

```
// Anchor (pivot boomu)
CamAnchorHeightMeters   = 1.5     // wysokość barku nad stopami
CamAnchorForwardMeters  = 0.0     // 0.3–0.5 dla over-the-shoulder
CamAnchorLagTau         = 0.12 s

// Boom
CamDistanceMeters       = 4.5     // teren stromy: 3.5–5, NIE 8+
CamDistanceMinMeters    = 1.2     // poniżej → fallback do 1. osoby
CamBoomOutTau           = 0.25 s  // wysuwanie po minięciu ściany
CamBoomInSnap           = true    // wciąganie na kolizji = natychmiast
CamCollisionRadiusMeters= 0.35
CamCollisionMarginMeters= 0.30

// Kąty
CamPitchMinRadians      = -0.20   // ~ -11° (patrzy lekko w górę na wspinacza)
CamPitchMaxRadians      =  1.20   // ~ +69° (czytanie trasy z góry; < singularność 89°)
CamPitchDefaultRadians  =  0.30
CamYawAutoAlignTau      = 0.8 s   // dryf yaw za heading gdy idzie i mysz bezczynna
CamYawAutoAlignMoveGate = 0.5 m/s
CamMouseIdleSeconds     = 1.2 s

// Optyka
CamFovYRadians          = 1.05    // ~60° (szerzej niż obecne 45°)
CamNearPlaneMeters      = 0.05
```

**Wygładzanie:** critically-damped `SmoothDamp(current,target,ref vel,tau,dt)` (tau→0 = snap). Użyj dla anchor
i boom-out; boom-in to przypisanie.

**Kolizja z terenem (`RaycastBoom`)** — bez raycastu mesha, marsz po height-fieldzie na istniejącym
`SampleWalkGround(xy)->float?`:
```
step=0.5m; for t in step..desiredLen:
  p = anchor + boomDir*t
  if (p.z - (SampleWalkGround(p.xy) ?? -inf)) < margin+radius: return max(t-step, MinDist)
return desiredLen
```
~9 próbek/tick — pomijalne przy streamingu 1 m.

**Tryb wspinaczki (`IsClimbing`):** zamroź auto-align, przechyl kamerę lekko **w górę** (`pitch → CamPitchMin`),
by widzieć chwyty nad postacią (wzorzec Jusant/BOTW).

**Init w `EnterWalkMode`:** `camYaw = walkHeadingRadians`, `boomLen = CamDistanceMeters`, `smoothAnchor =`
startowy anchor (unik szarpnięcia na wejściu). Na wejściu ustaw `Camera.FieldOfViewYRadians` i `NearPlane`,
przywróć na wyjściu.

---

## 5. Maszyna stanów animacji (WalkPhysics → klipy)

### Sygnały wyprowadzane per-tick (w kontrolerze, NIE w fizyce)
| Sygnał | Wyliczenie | Po co |
|---|---|---|
| `groundSpeed` | `‖PositionXY_now − PositionXY_prev‖ / dt` | **Prawda o ruchu** (walk-gate blokuje kroki → wish≠actual). Anty-foot-skate. |
| `moveYaw` | `atan2(ΔN,ΔE)` gdy `groundSpeed>ε`, inaczej hold | Yaw ciała (pozycja z fizyki → klipy in-place + yaw). |
| `vSpeed` | `VerticalVelocity` | Podział airborne: wznoszenie vs opadanie. |
| `climbUpFraction` | **wymaga dodania w fizyce — §6** | Blend climb-up ↔ climb-traverse. |
| edge-triggery | porównanie `IsGrounded/IsClimbing/IsHanging/IsSliding` z poprzednim tickiem | Jump-launch / Land / Mantle. |

### Wymagany zestaw klipów (in-place, root w origin)
KayKit pokrywa: `Idle`, `Walking_A`(walk), `Running_A`(run), `Jump_Start`(launch), `Jump_Idle`(airborne),
`Jump_Land`(land). **Brakujące** `slide`/`climb_up`/`climb_traverse`/`hang`/`mantle` → **proceduralnie** (§6,
jak wing-beat smoka), do czasu ewentualnego dołożenia klipów Quaternius.

### Tablica stanów (priorytet góra→dół)
`GS = groundSpeed`. Crossfade 0.1–0.2 s (lokomocja), krótszy dla reakcji.

| Stan | Warunek wejścia (priorytet) | Klip | Crossfade IN | Uwagi |
|---|---|---|---|---|
| **Climb** | `IsClimbing` | proceduralny reach L/R blend up↔traverse wg `climbUpFraction` | 0.15 s | rate = `GS/climbRefMps`; bez exit-time |
| **Hang** | `IsHanging` | proceduralny near-static + micro-sway ciupagi | 0.12 s | |
| **Slide** | `IsSliding` | proceduralny brace / lean w fall-line | 0.10 s | |
| **Jump-launch** | rising `IsGrounded→false` ∧ `vSpeed>0` | `Jump_Start` | 0.05 s | non-loop → Airborne po końcu / `vSpeed≤0` |
| **Airborne** | `!IsGrounded` (i nie Climb/Hang) | `Jump_Idle` | 0.15 s | loop w opadaniu |
| **Land** | rising `IsGrounded→true` z Airborne | `Jump_Land` | 0.08 s | skaluj intensywność `|vSpeed|`; → Locomotion |
| **Locomotion** | `IsGrounded ∧ GS>walkStart(~0.3)` | blend `Walking_A`↔`Running_A` wg GS | 0.20 s | rate-sync; bez exit-time |
| **Idle** | `IsGrounded ∧ GS≤idleStop(~0.15)` | `Idle` | 0.25 s | hub/fallback |

**Krytyczne:** **Climb/Hang testuj PRZED Airborne** — w `WalkPhysics` wspinaczka/zwis też ustawia
`IsGrounded=false` (linie 148/143). Histereza `walkStart>idleStop` przeciw migotaniu. Każdy non-loop
(launch/land/mantle) ma jawną drogę powrotu (unik animation-lock).

### Dopasowanie tempa (anti-foot-skate)
Dla Locomotion steruj i wagą blendu, i tempem klipu z realnego `GS`:
```
t = clamp01((GS - walkRefMps)/(runRefMps - walkRefMps))       // 1D speed blend
refMps = lerp(walkRefMps, runRefMps, t)
phase += (GS/refMps) * (dt/clipLenAt(t))                       // wspólna faza, wrap [0,1)
```
Clamp mnożnika tempa ~[0.6,1.6]; poza tym blenduj ku drugiemu klipowi zamiast rozciągać. Zanotuj
`walkRefMps/runRefMps` (autorskie tempo klipów KayKit) przy load.

### Warstwy proceduralne (na bazowym pozie, po `Pose(...)`)
`RotateBone/RotateBoneOverlay/BlendBoneTowardBind`:
- **Naprzemienny axe-plant (Climb):** faza ze zintegrowanego `GS`, overlay bark+łokieć L/R w antyfazie —
  synchronizuj z kadencją ciupagi (spójne z first-person `DrawOneCiupaga`).
- **Look-at (stany naziemne):** additive yaw/pitch szyi+głowy ku look kamery, clamp ~±60°/±35°.
- **Slide brace / land absorb:** lean kręgosłupa ∝ `|VerticalVelocity|`.
- Wyjście ze stanu: `BlendBoneTowardBind` (płynny zanik overlay, nie snap).

---

## 6. Ulepszenia wspinaczki (value/effort)

Wszystko w `WalkPhysics.Step()` (gałąź `axeOnRock`, linie 114–152) + nowe pola w `WalkParameters.cs`. Rdzeń
jest już unit-testowalny (wstrzykiwany `sampleGround`, bez GL) → każdy punkt = cykl red-green.

### Tier 1 — wysoka wartość, niski nakład
1. **Ledge detection + mantle (top-out)** — największy brakujący beat. Próbkuj `sampleGround` kawałek
   do-przodu-i-w-górę; gdy lokalny grade < `MaxStandSlopeGrade`, odpal mantle (interpolacja ~0.4 s, potem
   `IsGrounded=true`). Pola: `MantleReachMeters(~1.0)`, `MantleProbeAheadMeters(~1.5)`, stan `IsMantling`+timer.
2. **Grip/stamina** — czyni wspinaczkę decyzją (BOTW/Jusant). Pola: `MaxGripStaminaSeconds(~8)`,
   `GripDrainPerSecond(1.0)`, `GripJumpCost(~1.5)`, `GripRegenPerSecond`; stan `GripStamina` (eksponowany na HUD
   jak `VerticalVelocity`). Przy `GripStamina<=0` → `IsHanging` → puść w spadek. Additive, ~40 linii.
3. **Zmienna prędkość wg grade** — dziś 45° i pion wspina się identycznie 1.4 m/s. Skaluj
   `ClimbSpeedMetersPerSecond` wg `slope`. Pola: `SteepClimbFraction(~0.55)`, `SteepClimbGrade`. Łączy się ze
   staminą (stromiej=wolniej=więcej drenu/metr).
4. **Kadencja hold-to-climb (pulsy na axe-plant)** — ruch w pulsach zsynchronizowanych z uderzeniami ciupagi
   (surge na plancie, coast między). Stan `climbPhase`, pola `ClimbCadenceHz(~1.2)`, `ClimbSurgeSharpness`.
   Eksponuj `ClimbPhase`, by renderer synchronizował swing **do fizyki** i by audio/haptyka (8) miały beat.

### Tier 2 — wysoka wartość, średni nakład
5. **Fall-and-catch (re-grab window)** — po staminie (2) potrzebny verb odzysku (coyote-time). Pola:
   `CatchReachMeters(~1.5)`, `CatchWindowSeconds(~0.35)`. Krótki poślizg przed self-arrest.
6. **Foothold snapping (anti-jitter)** — filtruj punkt zaczepu (`climbSurfaceElevation`) zamiast snapować surowe
   `sampleGround` (chatter na 1 m). Pole `ClimbAttachTau`. Prerekwizyt czystego mantle (1) i kadencji (4).
7. **Kadrowanie kamery wspinaczki** — bias ku wall-normal + look-up (już w §4). Prezentacja w
   `Terrain3DView.xaml.cs`, `WalkPhysics` bez zmian.

### Tier 3 — polish
8. **Audio/haptyka na axe-plant** (po 4) — reuse istniejącego audio (bed smoka/Pixabay).
9. **Wet/exposure slip** (jeśli jest stan pogody) — podnieś `HangMinSlopeGrade`, szybszy dren.
10. **Pitony/lina (Jusant)** — genre-shifting, wysoki nakład, defer.

### Dodatki do fizyki (małe, non-breaking) — potrzebne kontrolerowi (§5)
- **`public float ClimbBlend { get; private set; }`** = `alongUp/(|alongUp|+|alongSide|)` (wartości już
  liczone, linie 128–129, dziś odrzucane). Karmi `climbUpFraction`.
- **`ImpactSpeed`** (lub złap `VerticalVelocity` na rising-edge `IsGrounded` zanim wyzerowane, linia 224) —
  skalowanie `land`. Opcjonalne.

**Kolejność (TDD):** 2 → 3 → (6+1) → (4+8) → 5 → 7 → 9/10.

---

## 7. Fazowanie i ryzyka

### Faza 0 — Smoke-test loadera (0.5 dnia)
Unit test: `SkinnedModel.LoadGlb("hiker.glb")` ładuje się bez wyjątku, `Animations.Count>0` + log nazw/indeksów,
po `Pose(0,0)` pozy niepuste, tekstura dekoduje, bounds sensowne. **Gate przed integracją wizualną.**

### Faza 1 — MVP: model w 3. osobie idący za fizyką (~1 dzień)
`SetHumanoid` w rendererze, seating na `FeetElevation`, yaw=`walkHeadingRadians`, statyczny `Idle`/`Walking_A`.
Follow-kamera (§4) zastępuje eye 1. osoby. Move wish camera-relative (§3.4). **Werdykt usera: postać stoi/idzie
na ziemi, kamera za nią, nie wchodzi w skałę.**

### Faza 2 — FSM lokomocji (~1 dzień)
Tablica stanów §5 dla naziemnych (Idle/Locomotion/Jump/Airborne/Land) + rate-sync. Klipy KayKit. TDD: kontroler
jako czysta klasa (sygnały in → wybór stanu out).

### Faza 3 — Wspinaczka wizualna (~1 dzień)
Procedural Climb/Hang/Slide overlay (RotateBone, wzór wing-beat). Wymaga `ClimbBlend` w fizyce (§6-dodatki,
red-green test). Kamera climb-mode bias.

### Faza 4 — Feel wspinaczki (~1–2 dni, iteracyjnie)
Tier 1 climb upgrades (stamina → speed → mantle+snapping → kadencja+audio). Każdy = test-first w `WalkPhysics`
z fake `sampleGround`, bez build/deploy loop.

**Nakład łącznie: ~3–5 dni** (MVP+FSM+climb ~3, feel-upgrades +1–2).

### TDD-friendliness
`WalkPhysics` już unit-testowane (wstrzykiwany sampler, bez GL/kamery). Kontroler animacji i `FollowCameraRig`
wyekstrahuj jako czyste klasy (metry in/out) → asercje bez GL. Hooki testowe kamery:
`should_shorten_boom_when_ground_between`, `should_snap_in_instantly_but_ease_out`,
`should_never_go_below_min_distance`, `should_map_WASD_to_camera_relative_wish`,
`should_clamp_pitch_within_singularity`.

### Główne ryzyka
1. **Pułapka licencyjna (Mixamo/CC-BY-NC/ND):** unikamy — bierzemy CC0 (KayKit/Quaternius/Kenney). CC-BY
   (Fox/CesiumMan) wymaga atrybucji + repaint logo → tylko test-assety. **Nie commitować surowego Mixamo do
   repo.**
2. **Ból retargetu FBX→glTF:** dotyczy tylko wariantu B (Quaternius merge) — root-motion drift, indeksy klipów.
   **Mityg: KayKit = zero konwersji** (główna ścieżka).
3. **Rozmiar mobile:** KayKit ~3.66 MB OK. Quaternius-merge trzymać <5 MB, przyciąć klipy. `dragon-animated.glb`
   35 MB pokazuje, że tego NIE robimy dla humanoida.
4. **Koszt CPU-skinningu 2. modelu:** 41 kości KayKit vs 219 smoka — tani, ale per-frame dynamic VBO upload
   (`UploadAndDrawDragonPrimitives`). Na telefonie profiluj; humanoid rzadko dzieli klatkę ze smokiem (różne
   tryby F7/F8).
5. **Kolizja kamera↔teren:** największe ryzyko UX na graniach. Mityg: asymetryczny spring-arm (snap-in/ease-out)
   + thick sweep (`CamCollisionRadiusMeters`) na `SampleWalkGround`; boom tight (4.5 m, min 1.2 → fallback 1.
   osoba).
6. **`HumanoidYawOffset` / orientacja:** dobór osi „przodu" KayKit wymaga weryfikacji wizualnej (jak
   `DragonYawOffset=π/2`). Ustalić w Fazie 1 na żywym renderze, nie zgadywać.

### Kluczowe pliki
- `src/MapaTur.App/Views/Terrain3DView.xaml.cs` (walk tick `:2305–2364`, eye block `:2334–2339`, viewmodel
  `:3578–3639`, GL push `:6958`, mouse-look `:5665`, `EnterWalkMode :2227`)
- `src/MapaTur.App/Services/Terrain3DGlRenderer.cs` (`SetDragon`/`UploadAndDrawDragonPrimitives :7618–7666`,
  program `:6619`)
- `src/MapaTur.Application/Terrain/SkinnedModel.cs` (loader)
- `src/MapaTur.Application/Terrain/WalkPhysics.cs` / `WalkParameters.cs` (fizyka + wspinaczka)
- `THIRD-PARTY-ASSETS.md` (atrybucja)
