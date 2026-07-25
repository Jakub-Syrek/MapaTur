# HANDOFF 2026-07-17 — przejęcie Climber3d: wspinaczka chwyt-po-chwycie ŻYWA w apce

Sesja 07-16 wieczór → 07-17 południe. Czytaj RAZEM z: `C:\Repos\Climber3d\docs\MAPATUR_INTEGRATION_HANDOFF.md`
(kanon metodyki — etapy, kontrakty, twarde vs miękkie bramki), pamięć `climber3d-takeover-epic` (pełna
chronologia decyzji), `docs/HANDOFF-2026-07-16-seams-ortho-memory.md` (poprzednik — teren/orto).

---

## 0. TL;DR

- **Cały stack wspinaczkowy Climber3d jest przeniesiony i ŻYWY w apce**: fizyka (Core 1:1, corpus 28/28 w Z-up
  na pionie i przewieszeniu 24°), powierzchnia patchowa (BVH, stabilne HoldId), **VERBATIM rig z PoC** na
  licencjonowanym modelu, sesja z autoasekuracją, sterowanie dwuklikiem z obwódkami i statystyką ryzyka.
- **Realistyczny wspinacz = DOMYŚLNA postać wszędzie** (wytyczna usera): chód na klipach Mixamo
  (auto-rig + retarget pipeline odtwarzalny), wspinaczka na oryginalnym rigu z IK; hiker tylko fallback.
- **Metodyka usera utrwalona:** anatomii NIE wolno łamać nigdy — ręczny klik omija tylko miękkie bramki
  (stabilność/risk), twarde (capacity/zasięg/kolizja/SMPL-X) zawsze odrzucają z revertem i logiem.
- **Stary tryb ciupagi WYŁĄCZONY w apce** (mieszał się z sesją; kod+testy zostają uśpione). LMB uwolniony.
- **20 commitów na feat/walk-mode** (`5a75466`…`8b6e1b3`), NIE pushnięte. Testy: 1644 App + 82 Climbing green.
- **Fetch det05 DOKOŃCZONY** (07-16 21:24, ~345k kafli, manifest) — kolejka coverage/audyt/sync CZEKA NA ZGODĘ.

---

## 1. Stan repo / assetów

**Commity sesji (feat/walk-mode, autor Jakub Syrek, zero AI):** `5a75466` port Core → `aef1099` transform+corpus
→ `575409e` kwarantanna modeli → `2d679be` patch surface → `872d37d` adapter hikera → `f5f3b3f` ClimbSession
→ `feb302d` wstawka C → `32bb7d8` VERBATIM rig PoC → `8d7bf53` fix ścieżki Data\Data → `3d984ba` awatar chodu
Mixamo → `de5f976` debounce C+logi → `e417341` koniec ciupagi → `cf89f6e` LMB wolny → `1846c56` klik v1
→ `75915aa` permissive grab → `681e11b` dwuklik+obwódki+statystyki → `b6dec6d` gęstość chwytów → `e8c05c5`
energia OFF (flaga) → `9a5b4d1` match 2 rąk/2 stóp → `8b6e1b3` weryfikacja twardych bramek.

**Tag w Climber3d:** `mapatur-handoff-baseline` = `7335a954` (46/46, 28/28) — hash w README MapaTur.Climbing.

**Assety (LOKALNE, gitignored `data/climber/` — NIE do redystrybucji, zakaz commit/Resources/Raw/instalator/paczki):**
`RockClimber_Realistic.glb` (162 MB, rig wspinaczkowy), `RockClimber_Walk.glb` (151 MB, Mixamo-rig + klipy
Idle/Walking_A/Running_A/Jump_Idle/Jump_Land/Jump_Start + oryginalne tekstury), `merge-climber-walk.py` +
README z CAŁYM pipeline. Kanoniczna lokalizacja ładowania: `%LOCALAPPDATA%\User Name\com.companyname.mapatur.app\
Data\models\` (⚠️ `FileSystem.AppDataDirectory` już KOŃCZY się na `\Data` — nie dokładać drugiego!).
Env-override: `MAPATUR_CLIMBER_MODEL`. Bez modelu: chód=hiker, wspinaczka NIEDOSTĘPNA (log).

## 2. Architektura wspinaczki (co gdzie)

| Warstwa | Typ | Rola |
|---|---|---|
| `MapaTur.Climbing` | Core 1:1 + `ClimbSpaceTransform`/`ClimbWorld` | fizyka: chwyty/zajętość (match 2 rąk LUB 2 stóp ≥0.30/0.32 m), quasi-statyka, whole-body solver, anatomia SMPL-X; REALNE metry Z-up, VE nigdy nie wchodzi |
| `ClimbSurfacePatch`+`TrianglePatchClimbSurface` | Climbing | trójkątowy patch (prawdziwe przewieszenia), BVH closest-point/raycast, grid chwytów, HoldId stabilne przy retesselacji |
| `ClimberSkinnedModel` | Application | **VERBATIM z PoC** (NIE "ulepszać"!): IK z pole vectors, palce-effectory, CCD dolnej kości, kostka, skinning równoległy (+normalne) |
| `RealisticClimberRig` | Application | IClimbWholeBodyKinematics: mapa osi z MIERZONEGO forward (uniwersalna (s·x,−s·z,y)), landmarki, COM, kapsuły clearance |
| `ClimberRigKinematics` | Application | adapter hikera (testy/referencja; apka go nie używa do wspinaczki) |
| `ClimbSession` | Application | stan sesji: start (strony w układzie POSTACI!), planner WASD (strict), `TryGrabHold` (permissive: capacity+zasięg), `RevertLastGrab`, `AssessMove` (what-if), pitony/lina, `DrainGripStamina=false` |
| `GripClimbController` | App/Services | patch z terenu + proceduralne chwyty (hash, ~2.5/m², FootEdge szer. 0.36), dwuklik+kandydaci+weryfikacja twardych bramek, pozowanie (solve→Skin→kopiowanie buforów do render-twina), mirror do WalkPhysics |
| `Terrain3DView` | App | klawisz C, ray-pick z kamery, `DrawClimbSelectionOverlay` (Skia: obwódki+%), przełączanie modelu w SetHumanoid |

**Własność ciała:** sesja aktywna = `WalkPhysics.Step` NIE woła się; walker to read-only mirror
(`SyncFromClimb` — jedyny legalny zapis pozycji poza Teleport). Wyjście: RopeCatch/Fall/Released → handback.
Rope-arrest działa też W LOCIE (`StepAirborne` — załatana luka F4).

## 3. Sterowanie (stan wdrożony)

F8 → chód realistycznym wspinaczem (klipy Mixamo, crossfade Idle↔Walk↔Run). Przy stromym terenie (≥~37°,
span pionu chwytów startowych ≥0.4 m — inaczej odmowa z powodem w logu): **C** = sesja. **Klik 1** na TRZYMANY
chwyt = wybór kończyny → kandydaci w zasięgu dostają obwódkę (zielona=strict solver OK, pomarańczowa=tylko
permissive) + mały % ryzyka; **klik 2** = ruch (permissive, ale po ruchu weryfikacja TWARDYCH bramek → revert
przy naruszeniu, log `climb.move_rejected_hard` z cechami). Klik w chwyt zajęty przez inną kończynę przy
aktywnym wyborze = GEST MATCH (2 ręce/2 stopy). WASD = tryb auto (pełna fizyka, anty-ping-pong). C/Spacja =
puszczenie. Energia nie zużywa się (flaga). Logi strukturalne: session_started/move_applied/move_blocked(powód)/
whole_body_solved(ms)/hand_match_gesture/move_rejected_hard/session_finished.

## 4. Lekcje twarde (NIE diagnozować od zera)

1. **SharpGLTF `NodeInstance.ModelMatrix` = leniwy cache czuły na KOLEJNOŚĆ odczytów po zapisie LocalMatrix**
   → potomkowie STALE („przedramię rozciągnięte 12%", potem „kulka" w skinningu). W pętlach IK TYLKO własne FK:
   `SkinnedModel.GetBonePosedPositionStrict`/`GetBoneModelMatrix` (parentByNode z glTF). Strażnicy:
   `SkinnedModelClimbIkTests` + anty-kulka w `RealisticClimberRigTests` (gated na realny GLB, CI pomija).
2. **Jest sprawdzony kod → portuj VERBATIM, nie re-derywuj** (moja reimplementacja riga = kulka; port 1:1 = działa).
   Weryfikuj SKÓROWANE WIERZCHOŁKI/piksele, nie tylko kości.
3. **Mixamo pipeline**: FBX ze szkieletem ODRZUCA — upload OBJ; klipy With Skin+30fps, Walk/Run z **In Place**;
   Mixamo ZLEWA materiały → rozcięcie po centroidach trójkątów (pozycje bit-identyczne z OBJ, 99.9% match)
   + tekstury z oryginału per materiał (WSPÓLNA tablica bajtów per materiał → jeden GL-texture; humanoid
   rysuje tekstury PER-PRYMITYW). Całość w `data/climber/merge-climber-walk.py` + README.
4. **`SideAlongSurface` ma LOSOWY znak względem postaci** → strony kończyn liczyć w układzie POSTACI
   (lewa = Z × kierunek-w-ścianę); inaczej start ze skrzyżowanymi rękami.
5. **Fizyka = ściany, nie stoki**: na ~40° „wyżej" jest daleko w poziomie → „no viable vertical band";
   bramka startu + czytelna odmowa zamiast martwej sesji.
6. Key auto-repeat C flapował sesję (8 startów/2 s) → toggle tylko na pierwsze wciśnięcie (`WasKeyDown`).
7. Dwa systemy wspinaczki naraz = walka o ciało → stary ciągły climb wyłączony (hangHeld:false), LMB uwolniony
   od `StartCiupagaSwing` (zjadał wszystkie kliki: Handled+capture).

## 5. OTWARTE — kolejka wspinaczki

1. **Werdykt usera z dwukliku po `8b6e1b3`** (match, statystyki, twarde bramki w akcji).
2. **Fazy ruchu Preload→Release→Reach→Latch→Settle + interpolacja** (poza zmienia się skokowo) — wzorzec
   w PoC ClimberWindow; potem solver na workera (solve 17–100 ms na UI thread = mikro-hitch).
3. **Przeskok modeli chód↔wspinaczka** (dwa szkielety: Mixamo vs Root_M) — wygładzić.
4. **Prawdziwy generator chwytów** (nachylenie/krzywizna/klasyfikacja orto — synergia z `docs/PLAN-ortho-scatter.md`);
   obecny = deterministyczny hash-placeholder. To CEL USERA po dopieszczeniu sterowania.
5. Mantle/topout; UX po RopeCatch; powrót energii (flaga `DrainGripStamina`); statystyki kandydatów
   po twardych bramkach (teraz tylko risk ze strict-solvera); haircard/lashbrow bez base-color (płaskie włosy).
6. Etap 6 (pełny planner ze scoringiem techniki) i Etap 7 (perf/mobile — bez modelu wspinaczka wyłączona).

## 6. OTWARTE — teren/orto (bez zmian od 07-16, patrz poprzedni handoff)

**det05: fetch DOKOŃCZONY 07-16 21:24** (~345k kafli, manifest, skiplist 703, `_partial.txt` 3381 zapisane).
Kolejka ZA ZGODĄ usera: regen `_coverage.txt` (przepis HANDOFF-07-15 §9.3) → **OBOWIĄZKOWY
`audit-ortho-blue-cast.py`** → sync repo→AppData (tam stare 75k). Reszta: werdykt pamięci lotu, A/B det05,
derywacja z13-15, re-sync telefonu, LruCache — patrz `HANDOFF-2026-07-16-seams-ortho-memory.md` §5.

## 7. Komendy

```powershell
# build+run (stale-exe trap!)
Get-Process MapaTur.App -EA SilentlyContinue | Stop-Process -Force; Start-Sleep 1
dotnet build src/MapaTur.App/MapaTur.App.csproj -c Debug -f net10.0-windows10.0.19041.0 -p:WindowsAppSDKSelfContained=false
Start-Process -NoNewWindow dotnet -ArgumentList 'run','--project','src/MapaTur.App','-f','net10.0-windows10.0.19041.0','-p:WindowsAppSDKSelfContained=false','--no-build'

# testy wspinaczki
dotnet test tests/MapaTur.Climbing.Tests --nologo                                        # 82: Core+patch+corpus
dotnet test tests/MapaTur.Application.Tests --filter "FullyQualifiedName~ClimbSession"   # sesja+autoasekuracja
dotnet test tests/MapaTur.Application.Tests --filter "FullyQualifiedName~RealisticClimberRig"  # gated: realny GLB, anty-kulka
dotnet test tests/MapaTur.Application.Tests --filter "FullyQualifiedName~SkinnedModelClimbIk"  # strażnicy FK/stale-cache

# logi wspinaczki (żywe)
Get-Content src/MapaTur.App/bin/Debug/net10.0-windows10.0.19041.0/win-x64/logs/mapatur-*.log -Tail 50 | Select-String "\[Climb\]"
```

## 8. Kotwice w kodzie (symbole)

| Co | Gdzie |
|---|---|
| Kontrakt whole-body | `MapaTur.Climbing.IClimbWholeBodyKinematics`, `SequentialWholeBodyClimbSolver`, `SmplxPosePriorProfile` |
| Patch/BVH | `ClimbSurfacePatch`, `TrianglePatchClimbSurface.{ClosestPoint,Raycast,FindHolds}` |
| Rig PoC (verbatim) | `ClimberSkinnedModel.{PoseContacts,SolveTwoBoneLimb,ApplyModelSpaceRotation,Skin}` |
| Wrapper Z-up | `RealisticClimberRig.{Evaluate,BuildWorldMatrix,MapDirection}` (forwardSign!) |
| Sesja | `ClimbSession.{TryStart(out reason),TryMoveToward,TryGrabHold,RevertLastGrab,AssessMove,HandBackTo}` |
| Kontroler | `GripClimbController.{TryEnter,Tick,HandleClick,SelectLimb,VerifyHardConstraintsAfterGrab,BuildTerrainPatch}` |
| Widok | `Terrain3DView.{TryClimbHoldClick,DrawClimbSelectionOverlay}`, blok climb w `OnWalkTick`, `LoadHumanoidModelAsync` (wybór awatara) |
| SkinnedModel | `{GetBonePosedPositionStrict,GetBoneModelMatrix,RotateBoneModelSpace,Primitive.BaseColorImageBytes}` |
| WalkPhysics | `{SyncFromClimb,StepAirborne(rope-arrest)}`; ciupaga uśpiona (hangHeld=false w widoku) |

## 9. Twarde zasady tej domeny (user powiedział wprost)

- **Anatomia NIENARUSZALNA przy każdym ruchu** — twarde bramki (capacity/zasięg/kolizja/SMPL-X) zawsze;
  miękkie (stabilność/risk) wolno omijać w trybie ręcznym. Metodyka = handoff Climber3d.
- **Modele wspinacza NIE do redystrybucji** — lokalne `data/climber/` + AppData; zakaz commit/pakowania.
- Realistyczny wspinacz DEFAULTEM wszędzie; hiker fallbackiem.
- Klik = chwyt (realizm jako informacja, nie blokada); wszystkie odmowy z powodem w logu.
- Przejęcia robić z GOTOWEGO kodu (verbatim), nie re-derywować; weryfikować piksele/wierzchołki.
