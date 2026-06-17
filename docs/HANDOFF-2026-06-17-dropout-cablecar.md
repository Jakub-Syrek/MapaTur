# Handoff — 2026-06-17 (dropout-trench fix · cable car · golden-hour slide)

## ⏱ SESSION-END STATE — START HERE

| Thing | State |
|---|---|
| **§F.2 "fault trench"** (Jaworzynka/Goryczkowa) | ✅ **RESOLVED, user-confirmed, pushed to main, on phone.** It was a corrupt ROW/COLUMN dropout strip in `tatry.dem`, fixed by runtime `FillDropoutStrips`. |
| **Cable-car fix** (floating masts + no moving cabins) | ✅ **deployed to desktop + phone (APK v101) AND committed** (`ada5a34`). |
| **PL/SK z16 seam clip** (411 misaligned SK tiles) | done on the **DESKTOP cache only**; backup in `C:\Repos\MapaTur\.sk-clip-backup`. **Phone cache NOT clipped** (would still show PL↔SK steps near Morskie Oko if visited). |
| **Golden-hour physics slide** | delivered in-chat (a `show_widget` SVG over a live screenshot) — not a repo file. |
| main vs origin | `478423c` and earlier are on `origin/main`; **`ada5a34` (cable-car fix) + `1e76b76` (this handoff) are committed locally, NOT yet pushed** (awaiting OK). |
| tests / format | 1151/1151 green, `dotnet format --verify-no-changes` clean (Application/Tests/App) at the last commit. The uncommitted cable-car fix adds 2 passing tests; re-run before committing. |

**Top to-do: push** `ada5a34` (cable-car fix) + `1e76b76` (this handoff) to `origin/main` — committed locally, awaiting OK.

---

## What shipped to `origin/main` this session

- `478423c` **fix(terrain): repair base row/column dropout strips (`FillDropoutStrips`)** — the §F.2 fix (below).
- `c26b35e` **feat(3d): Kasprowy cable-car overlay** — the *original* MVP (sagging cable + masts). The masts-on-terrain + moving-gondola fix that followed is the UNCOMMITTED work above.
- `9f17ce7` **fix(terrain): cut LOD adaptive tiles at ortho cell boundaries** (`CutsWithCellBoundaries`) — prior-session "strata stripes" fix, finally committed.
- Plus the 7 earlier unpushed commits (snow model, lidar despike bake, FillNarrowZeroStrips guard, summit-label radius, etc.) were pushed.

---

## §F.2 dropout-trench — root cause + fix (the big one)

**Error class:** a corrupt single ROW or COLUMN *strip* in `tatry.dem` (the 15 m base) — a run of cells sitting hundreds of m below BOTH perpendicular neighbours (a lidar scanline / LiDAR↔Copernicus mosaic seam that dropped a strip). Renders as a dead-straight narrow trench **on the base**.

- Confirmed offenders: `row 488` (lat 49.333, raw ≈268 m), `row 1253` (lat 49.229 by Jaworowa Kopa, raw ≈500 m) + shorter segment cuts elsewhere.
- **Why `FillPits` left it:** a strip cell's two along-strip neighbours are *also* dropped → the 4-neighbour median only halves it and converges to within its ~20 m threshold → a **~20–30 m residual trench** survives. So the base *looks* "smooth" unless you scan for it specifically.
- **Fix:** `DemRasterRepair.FillDropoutStrips(>50 m, run ≥3)` — finds a RUN of ≥3 consecutive cells each >50 m below the line on both perpendicular sides, fills the whole run with the mean of the two bracketing lines (one pass, no residual). Scans rows then columns; snapshot-bracketed. Wired into the base chain **before `FillPits`** at the two base-reading paths (auto-load + ring base in `MapPageViewModel`). Runs on LOAD — **no re-bake**.
- Residual after fix: deep strips (>40 m) fully gone; only shallow ≤34 m residues remain (mostly real micro-terrain) — left alone on purpose (lowering the threshold risks flattening real V-valleys).
- Full write-up + diagnosis recipe: **`docs/TERRAIN-GRAPHICS-CHECKLIST.md` §A.2b** (and §F.2 marked ✅ RESOLVED).

**Diagnostic methods that cracked it (the rest was ~30 builds of wrong guesses — don't repeat):**
1. **Cyan detail-tint in hypso** (`ShowDiagnosticDetailTint=true`, skirt=0): the trench sat OUTSIDE the cyan detail window → it's the BASE, not a detail-window/skirt/weld seam. This single observation killed the "window edge" hypothesis (and the workflow's verdict).
2. **Reproduce the base mesh offline** (RingBasePlanner → BuildAdaptiveTiles) and scan triangles, instead of trusting "the DEM is smooth".
3. **Gradient-SPIKE detector** (one row/col boundary far steeper than its neighbours), NOT a groove detector (deeper-than-both passes monotonic slopes and smooth valleys too). Then dump a cross-section: flat above + flat below + one sharp low line = a strip dropout.

**Ruled out (do not re-chase):** z16 detail trench (full 7749-tile sweep = 0), detail-window edge / morph / weld (base geometry mesh-scan = 0 walls >40 m; welds never move vertices so they can't make a tall wall), ortho-cell forced cut (removed, trench stayed), ring-LOD step T-junction, skirt (=0, trench stayed), base DEM despike-rebake (wrong layer).

---

## Cable car (Kasprowy) — ⚠️ UNCOMMITTED, deployed

User report: "słupy wiszą w powietrzu i wagoniki nie jeżdżą". Both fixed in `Terrain3DGlRenderer.DrawCableCar` + `CableCarGeometry`:

- **Floating masts → seat on terrain.** Mast base used the hand-authored `station.ElevationMeters` (×exaggeration) → off the ground by `(hardcoded − terrain)·exaggeration`. New `SeatGroundElevation(station, raster, detail)` samples the actual rendered surface (1 m `DetailElevationField` in-window, else base `Raster.SampleBilinear`, else the hardcoded value). `DrawCableCar` now takes `raster` + `detail` (passed at the call site, like `DrawTrailLines`); cache invalidates on `lastCableDetail` too.
- **No cabins → animated gondolas.** New pure `CableCarGeometry.CabinParameter(seconds, speed, index, count)` = triangle ping-pong t∈[0,1], cabins phase-offset a half-cycle so a pair runs one-up/one-down. Each frame (NOT cached — uses `atmosphereClock.Elapsed.TotalSeconds`) a yellow gondola (hanger + body bar) is drawn at `PointOnSpan(t)` below the cable.
- **Tunable constants** (in the renderer): `CabinsPerSpan=2`, `CabinSpeed=0.045` (one-way trips/s), `CabinHangM=12`, `CabinBodyM=7`, `CabinHalfWidthPx=3.0`, `CabinR/G/B` (yellow). 2 new tests in `CableCarGeometryTests`.
- Desktop-confirmed by user ("ok"); on phone APK v101.

---

## Device deploy recipe (learned the hard way this session)

Android Release APK, install-over without wiping data:
```
dotnet build src/MapaTur.App/MapaTur.App.csproj -f net10.0-android -c Release `
  -p:EmbedAssembliesIntoApk=true -p:ApplicationVersion=<N> `
  -p:AndroidSigningKeyStore="$HOME\mapatur.keystore" -p:AndroidSigningKeyAlias=mapatur `
  -p:AndroidSigningStorePass=Zarathustra_781 -p:AndroidSigningKeyPass=Zarathustra_781
```
- **adb** is at `C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe` (NOT on PATH, NOT in `%LOCALAPPDATA%`). Device = `RFCY1198TTX`. Installed versionCode is now **101** — next build needs `ApplicationVersion ≥ 102` or install fails `INSTALL_FAILED_VERSION_DOWNGRADE` (csproj default is 1).
- **Signing:** the device app is signed with `C:\Users\jaqbs\mapatur.keystore` (alias `mapatur`, pass `Zarathustra_781`). Build WITHOUT it → auto-key → `INSTALL_FAILED_UPDATE_INCOMPATIBLE`. Always pass the keystore.
- Bumping `-p:ApplicationVersion` alone is cached — delete `bin/Release/net10.0-android/*.apk` (or clean `obj/Release/net10.0-android`) to force a re-package with the new versionCode/signature.
- Install + verify: `adb install -r <-Signed.apk>` → `adb shell am force-stop` → `monkey -p … LAUNCHER 1` → confirm `adb shell pidof` (fresh pid) + `dumpsys package … | versionCode/lastUpdateTime`. **Never** `adb uninstall` (wipes the on-device DEM/ortho → 2D).
- Desktop run: `dotnet build … -f net10.0-windows10.0.19041.0 -c Debug -p:WindowsPackageType=None`, run `bin/Debug/.../win-x64/MapaTur.App.exe`. Kill the running instance first (it locks the exe).

---

## Open / next

1. **Push** `ada5a34` (cable-car) + `1e76b76` (handoff) — committed locally, not yet on origin.
2. **Phone PL/SK clip** — if the user reports PL↔SK steps on the phone (Morskie Oko / east), replicate the desktop clip on the phone's `dem-cache/gugik/16` (remove SK tiles that spilled into PL coverage). The SK DMR5 bake (`bake-sk-dmr5-tiles.py`) also still has the failed clip — see `[[sk-dmr5-detail-epic]]` memory; a proper re-bake needs the 3.2 GB LOT26 source which is no longer on disk.
3. **Cable-car polish** if asked: cabin size/speed/colour/count are the constants above; gondolas point straight down (no along-cable tilt) and there's one cable per span (no separate up/down haul ropes).
4. **`DemRasterRepair.RepairForMesh(...)` consolidation** still open (checklist §F.1) — one entry point so repair coverage can't drift across the 4 paths.

## Pointers
- `docs/TERRAIN-GRAPHICS-CHECKLIST.md` — single source of truth for terrain graphics (read before any bake / pipeline change; CLAUDE.md mandates it).
- Memory: `[[base-dropout-strips-resolved]]`, `[[sk-dmr5-detail-epic]]`, `[[device-data-restore]]`, `[[confirm-build-deploy]]`.
