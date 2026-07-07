# Handoff — 2026-07-07: RAM-cache, tryb chodzenia (ciupaga), lot smokiem (skinning glTF)

> Kontynuacja `docs/HANDOFF-2026-07-07.md`. Ten plik = stan wejściowy NOWEJ sesji dla trzech dużych rzeczy
> zrobionych tego dnia: RAM-cache kafli, tryb **chodzenia** z ciupagą, i tryb **lotu smokiem** (rigowany model glTF).
> Bramki przed pushem: `dotnet format MapaTur.slnx --verify-no-changes` + pełne `dotnet test MapaTur.slnx`.
> NIGDY Claude jako autor/co-author. Build desktop: `dotnet build src/MapaTur.App/MapaTur.App.csproj -f
> net10.0-windows10.0.19041.0`; ubić `MapaTur.App*` przed buildem; `Start-Process` exe; logi w `win-x64\logs`.

## Stan branchy / commitów
Wszystko zmergowane do **`main`** (2026-07-08): RAM-cache + chodzenie + pełny lot smokiem (silnik skinningu +
render 3D w GL + rig + sterowanie). `feat/walk-mode` = ostatni branch robczy (odbity od `8fd0261` RAM-cache).
Kluczowe commity: `8fd0261` RAM-cache, `5a7fb0d` walk, `d5815c8` DragonFlight+SkinnedModel(silnik), + commit
render 3D smoka (`DragonRig` + pass GL + sterowanie + strojenie). RAM-cache: [[lod-ram-tile-cache]].

## Sterowanie (final)
- **F8 chodzenie:** WASD/strzałki ruch, prawy-drag/QERF rozglądanie, Spacja skok (double jump), Shift bieg,
  **LPM** zamach/wbicie ciupagi (trzymany przy skale = self-arrest/zawiśnięcie).
- **F7 lot smokiem:** **strzałki ↑/↓ pitch** (↑ = **nurkowanie/dziób w dół**, ↓ = wznoszenie — flight-sim), **←/→
  yaw** (zgodnie), **prawy-drag** kręcenie (trzymany = holdPitch, „na zadanym pochyleniu"), **W/S** gaz, **A/D**
  przechył. Przechył idzie ZGODNIE ze skrętem (bank into turn). Kamera z **lagiem na pitchu** (smok pierwszy, kamera
  dogania). Dynamika: nurkując skrzydła **fold** + nie macha + przyspiesza (grawitacja na torze); wznosząc macha
  **szybciej** + zwalnia. Głowa: idle scan + w zakręt (roll) + za pitch.

## 1. Tryb chodzenia — F8 (na `feat/walk-mode`, pushnięte `5a7fb0d`)
Pierwsza osoba, ground-clamp. `WalkPhysics` (czysta, 15 testów): grawitacja/skok/slope-gate w REALNYCH metrach
(świat XY = metry, Z = ×Pion tylko przy stawianiu kamery). Reguły: pod górę stromiej niż limit = blok (trawers
przechodzi → „droga pod kątem"); zbyt stromo = ślizg; **double jump**; **self-arrest ciupagą** (trzymasz LPM przy
stromej skale w locie = zawiśnięcie). Sterowanie: **F8** toggle (debounce!), WASD/strzałki ruch, prawy-drag/QERF
rozglądanie, Spacja skok, Shift bieg, **LPM = zamach/wbicie ciupagi**. Walk owns kamerę → OnPaintSurface pomija
fly-floor i ustawia **near=0.3 m** (fly ≥5 m obcinał grunt pod nogami → „spód tekstury" przy skoku).
Ciupaga = proceduralny Skia viewmodel (brązowe drewno + **białe zakopiańskie ornamenty** rozeta/zygzak/leluja +
stalowy toporek + skórzana góralska rękawica), buja się, wbija przy zamachu/wiszeniu.

## 2. Lot smokiem — F7 (`d5815c8` + niezacommitowane strojenie)
**Fizyka** `DragonFlight` (czysta, 8 testów): smok szybuje do przodu wzdłuż heading; prawy-drag steruje yaw+pitch,
W/S gaz, A/D przechył, **strzałki**: ↑↓ pitch, ←→ yaw. Banking w zakrętach, swoop-clearance nad terenem.
**⚠️ PITCH JEST ODWRÓCONY w widoku** (`Terrain3DView` tick): ↑ = wznoszenie (dziób góra), ↓ = nurkowanie —
naiwny znak dawał odwrotnie (fizyka+kamera+model spójne, więc odwracamy INPUT, nie fizykę). Prawy-przycisk
trzymany = `holdPitch` (nie auto-poziomuje, „lecisz na zadanym pochyleniu").

**Silnik skinningu** `SkinnedModel` (SharpGLTF.Core/Runtime 1.0.6, MIT; **CPU skinning**; 5 testów na Khronos Fox):
ładuje glTF/GLB, `Pose(anim,t)` (baked) LUB proceduralnie `ResetPose()`+`RotateBone(nazwa, quat)`+`Skin()`.
**⚠️ 1.0.6:** `NodeInstance.LocalMatrix` (NIE `LocalTransform` — to master). **⚠️ Bounds/skala MUSZĄ być z
pozycji SKINNIĘTYCH, nie surowych POSITION** (kości sięgają ~13 j., surowe 0.22 → skala była 55× za duża) —
`FromModel` robi ResetPose+Skin i mierzy `PosedPositions`.

**Rig proceduralny** `DragonRig`: brak wgranej animacji lotu, więc machamy sami. Skrzydła `Shoulder/Arm/Forearm.L/R`
(prawe = NEGATED kąt, bo lustrzane ramki → biją RAZEM), tułów `Chest/Spine.005` follow, nogi **podwinięte do tyłu**
(`Thigh`/`Shin` tuck) + sway, ogon ripple, głowa: idle scan + **leans in turn (roll) + follows dive (pitch)**.
Osie/amplitudy strojone okiem — consts w `DragonRig`. Model: **forward=+Z, up=+Y, skrzydła=X** (z sondy kości).

**Render GL** — pass w `Terrain3DGlRenderer`: `SetDragon(model, world, normalRot, light, visible)` + `DrawDragon`
(nowy program GLES 300, dynamic VBO — skinnięte wierzchołki co klatkę, opaque, depth-test → teren zasłania).
Rysowany po lesie (`DrawDragon(gl, mvp)` ~linia 3654) w ABSOLUTNYM mvp. Kolor `0.34,0.09,0.09` (krwista czerwień),
brak tekstury (`tex=none` w tym modelu). Widok: `SkinnedModel.LoadGlb` z `dragon.glb` (MauiAsset), pozuje w
`OnDragonTick`, buduje macierz świata `center * scale * (remap RotX90 * bank * climb * yaw) * translate`, push do
renderera w `OnPaintSurface`. Skala docelowa 24 m, dystans kamery 20 m, `DragonYawOffset=+π/2`, `DragonDropMeters=1`,
`DragonFlapLiftMeters=1.6` (unosi na opadaniu skrzydeł). Proceduralny smok Skia = fallback dopóki model 3D nie doczyta.

**Asset** `src/MapaTur.App/Resources/Raw/dragon.glb` — rigowany low-poly (79 kości, 2482 tri), **CC-BY** (Sketchfab
„Dragon Rigged"). ⚠️ **ATRYBUCJA DO UZUPEŁNIENIA** przed dystrybucją — `THIRD-PARTY-ASSETS.md` (jak nie da się
potwierdzić źródła → podmienić na CC0 Quaternius). Fox.glb w `tests/.../TestData/` (tylko testy).

## Otwarte / następne
- **Commit smoka 3D** (niezacommitowane od `d5815c8`) — gdy user powie. Potem push `feat/walk-mode` + ew. PR.
- Chip UI „🚶 Chodzenie" / „🐉 Smok" + PL/EN (F8/F7 działają, ale bez discoverable UI) — odłożone.
- Strojenie smoka trwa iteracyjnie z okiem usera (skrzydła/nogi/głowa/rozmiar) — wszystkie pokrętła to `const` w
  `DragonRig` + `Terrain3DView` (Dragon* consts). Model bez tekstury → jednolity kolor; ew. inny model/tekstura.
- Niebo: łuk Drogi Mlecznej ([[night-sky-milkyway-goal]]) — dalej czeka.

## Twarde lekcje tej sesji (w pamięci)
- Jedna zmiana → build → **user patrzy** → werdykt → dalej (grafika/feel = oko usera; computer-use bywa zablokowany
  przez Brave/desktop-shell — wtedy zrzut od usera). Nie commituj bez „commit".
- Game Bar nagrywanie wyszarzone = usługa `BcastDVRUserService` w StopPending → taskkill svchosta + rejestr GameDVR;
  [[gamebar-recording-greyed-fix]] (NIE ubijać GameBarFTServer — [[never-kill-gamebar-crashes-app]]).
