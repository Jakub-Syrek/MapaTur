# HANDOFF — LOD demo: bugi ROZWIĄZANE + rozszerzenie na całe Tatry W TOKU (2026-06-11, noc)

> **START TUTAJ.** Trzy poranne bugi (paski, czarne dziury, pęknięcia kafli) są **ROZWIĄZANE i potwierdzone
> przez usera** — commity na gałęzi `fix/lod-rings-black`. Trwa rozszerzenie demo na CAŁE Tatry przez
> **lokalny `tatry.dem` jako bazę LOD** (niezacommitowane, w drzewie). Na telefonie jest build 21:17:36;
> nowszy build (fix zamrożenia wejścia) **NIE wszedł** (USB padło w trakcie instalacji — sprawdź
> `lastUpdateTime` zanim cokolwiek ocenisz!). User zgłasza: **„wróciły dziury, w innych miejscach"** —
> NOWY, niezdiagnozowany problem na buildzie z lokalną bazą.

---

## 1. STAN REPO / URZĄDZENIA

- Gałąź: **`fix/lod-rings-black`** (NIE pushnięta; `main` = `fdfed0b` nietknięty):
  - `385e9e5` — paski (supersampler OFF) + czarne (ambient floor) — **user-confirmed**
  - `09c8a89` — bezszwowy per-kafel detal 1 m (`BuildAdaptiveTiles`) — **user-confirmed** („ok")
- **Niezacommitowane** (`MapPageViewModel.cs`, +22/−8): baza LOD z lokalnego `tatry.dem` + prep bazy na
  wątku tła (fix zamrożenia wejścia). Szczegóły w sekcji 3.
- **Telefon: build z 21:17:36, pid 8279** = lokalna baza, ale prep JESZCZE na wątku UI (wolne wejście
  do demo ~20-40 s). Build z fixem wątku (task `bufwz6fa6`) NIE zainstalował się — USB drop; output pusty.
  **Pierwsza czynność następnej sesji: przebudować + wgrać + POTWIERDZIĆ (lastUpdateTime + pid).**
- Deploy: `dotnet build C:\Repos\MapaTur\src\MapaTur.App\MapaTur.App.csproj -c Debug -f net10.0-android
  -t:Install -p:EmbedAssembliesIntoApk=true -p:AdbTarget="-s RFCY1198TTX"` → POTEM
  `adb shell dumpsys package com.companyname.mapatur.app | sls lastUpdateTime` + `pidof`. ZAWSZE pisz
  userowi „wgrane i potwierdzone (godzina + pid)".

## 2. ROZWIĄZANE DZIŚ (nie ruszać, działa — user potwierdził „nie widzę nic z bugów")

1. **„Paski"/pierścienie/obwódki (siatka na bazie)** = **mora supersamplera GUGiK**.
   `GugikNmtDemTileSource.MaxSupersampleFactor = 1` (OFF). NIE konstrukcja kafli (uniform step ==
   adaptive — dowiedzione), NIE ortho, NIE mesh (maxTileSide 240 vs 120 identyczne), NIE decymacja.
   Plan B w kodzie: `DemTileSupersampler.LowPassDownsample` (Gauss, cache-safe) gdyby wrócił washboard.
   ⚠️ **PUŁAPKA:** zmiana factora = zmiana nazwy plików cache → wymusza pełny re-fetch bazy; przerwany
   re-fetch = ZNIKAJĄCE KAFLE (dzisiejsza wpadka). Nie ruszać factora bez migracji cache.
2. **„Czarne dziury"** = za niski ambient na stromiznach od-słonecznych (dowód: unlit render = 0 czarnych
   px; clear = NIEBO). Fix: `Terrain3DGlRenderer` shader: `lightSum = max(lightSum, uSkyAmbient * 0.45);`.
3. **Pęknięcia/kropki na łączeniach kafli detalu** = stary path `crop+Subsample(step)` (niezależne wycinki
   → krawędzie różnych kroków w różnych miejscach świata). Fix: `TerrainMesh3D.BuildAdaptiveTiles` —
   każdy kafel z PEŁNEGO rastra na absolutnej siatce, zgrzew krawędzi do grubszego sąsiada
   (`WeldEdgeVertex`), pod-podział step-aligned pod limit 16-bit. Planner niesie kroki sąsiadów
   (`PerTileLodDecision.EdgeStep*`). **755 testów Application zielonych** (3 nowe dowodzą bezszwowości).

## 3. W TOKU: demo = CAŁE Tatry (decyzja usera: „demo ma być wersją docelową")

**Kierunek (uzgodniony):** baza = **lokalny `tatry.dem`** (całe Tatry, ~30 m, 38 MB, już na urządzeniu w
`/storage/emulated/0/Android/data/com.companyname.mapatur.app/files/dem/tatry.dem`) — **bez streamingu
bazy**, offline, bez ryzyka brakujących kafli, bez supersamplera. Detal 1 m dalej selektywnie przy
look-at (setki GB na całość — nie da się bundlować). To jest docelowa architektura (trwała baza + 1 m).

**Co już w drzewie (niezacommitowane):**
- `BuildLodDemoAsync`: baza z `autoLoader.Discover().DemPath` → `DemRasterReader.Read` (fallback: stary
  online z13, 6 km — `LodBaseHalfWidthMeters` wrócił do 6000 jako fallback-only).
- Prep bazy (Subsample→HoleBelow→FillInteriorKeepEdgeGaps ~9,5 M komórek) przeniesiony do `Task.Run`
  (wejście do demo zamrażało wątek UI — symptom „nie wchodzi demo"). **NIEZWERYFIKOWANE na urządzeniu**
  (build nie wszedł — patrz wyżej).

**Otwarte pytanie do usera (zadane, bez odpowiedzi):** start kamery w demo — środek DEM (pogórze) czy
wysoki rdzeń Tatr (efektowniej, jak dawniej)?

## 4. NOWY OTWARTY BUG: „wróciły dziury, w innych miejscach"

Zgłoszone na buildzie 21:17:36 (lokalna baza). Zrzuty: `.tmp-screen2\holes-back.png` (+ crops
`hb-top/mid/bot.png`). Widok = całe Tatry z ortho; w dolnych rogach czarne kliny, w środkowych
partiach ciemne płaty.

**Hipotezy (NIEZWERYFIKOWANE — nie ufać, testować):**
- (a) **kliny krawędzi skończonego terenu** w NOWYCH miejscach — lokalny DEM ma krawędzie gdzie indziej
  niż stare 12-km okno; ten sam grazing-edge artefakt co rano, tylko przeniesiony. (Najbardziej promujące:
  „w innych miejscach" pasuje 1:1.)
- (b) cień głęboki vs ambient floor na grubszej siatce bazy (cała baza ~9,5 M → subsample do 2 M budżetu
  → większy cell pitch ~60-90 m → inne normalne).
- (c) interakcja detal↔nowa baza (edge-match/backfill kontra nowy `TerrainRaster`).
- (d) NoData wewnątrz `tatry.dem` (HoleBelow(100 m) nie powinno nic wyciąć w Tatrach, ale sprawdzić).

**Przepis na rozstrzygnięcie (sprawdzone dziś metody):**
1. test MAGENTA (forsuj `fragColor` w shaderze terenu) → magenta = geometria (cień), nie-magenta = brak
   geometrii (dziura/krawędź);
2. test UNLIT (`fragColor = vec4(vColor.rgb,1.0)`) → znika = oświetlenie, zostaje = kolor/geometria;
3. detektor: near-black (`max(RGB)<25`) Z jasnym terenem w promieniu 12 px (odsiewa skałę);
4. ZAWSZE najpierw potwierdź który build jest na telefonie.

## 5. KOLEJNOŚĆ NA NASTĘPNĄ SESJĘ

1. Rebuild + install + **potwierdź** (lastUpdateTime + pid) — fix wątku UI wejdzie dopiero teraz.
2. Wejdź w demo, zmierz czas wejścia (ma być płynne) i zrób zrzuty dziur na ZNANYM buildzie.
3. Diagnoza dziur wg przepisu z sekcji 4 (magenta/unlit/detektor) — dopiero potem fix.
4. Po czystym stanie: commit kroku „lokalna baza całych Tatr" na `fix/lod-rings-black`.
5. Start kamery wg decyzji usera (sekcja 3).
6. Przed ewentualnym pushem: `dotnet format --verify-no-changes` (editorconfig zabrania final newline!)
   + pełne testy; push TYLKO za wyraźną zgodą usera.

## 6. ZASADY (dzisiejsze lekcje — user je wymusił, łamanie = utrata zaufania)

- **Pytaj przed każdą większą/destrukcyjną zmianą** (rekey cache, wyłączenie feature'a, reset, push).
- **Zawsze potwierdzaj wgranie builda** (lastUpdateTime + pid) i pisz to userowi wprost.
- **Nie zwalaj na sieć/LTE** — dzisiejsze „znikające kafle" to był rekey cache, nie sieć.
- **Nie ogłaszaj sukcesu** dopóki user nie potwierdzi na ekranie.
- Realizm to główny cel aplikacji — artefakty renderu to bugi krytyczne, nie kosmetyka.
- Narzędzia: `frame.ps1` (deterministyczny zrzut), crops przez PIL, logi appki NIE idą do logcat.
