# Handoff — offline data packages (DONE+deployed) + ortho "strata stripes" fix (impl, pending visual confirm)

Sesja 2026-06-15. Dwa duże wątki. Czytaj też pamięć: `[[offline-package-download]]`, `[[ortho-strata-stripes]]`,
`[[no-premature-success-claims]]`, `[[no-big-decisions-without-consent]]`, `[[water-regression-spiral]]`.

---

## STAN NA KONIEC SESJI

- **Wątek 1 (paczki danych z sieci): ZROBIONY + WDROŻONY + device-confirmed.** Wszystko na `main` (tip `7fee7af`).
- **Wątek 2 (paski/trench w ortofoto 3D): FIX NAPISANY + UNIT-TESTED (859 Application green), ale NIEzacommitowany
  i NIEpotwierdzony wzrokowo.** Czeka na: rebuild desktopu → user patrzy na normalny render → paski znikły? → commit+push.

**Working tree (uncommitted):**
- `src/MapaTur.Application/Terrain/TerrainMesh3D.cs` — fix pasków (cell-cut w `BuildAdaptiveTiles`). ZOSTAW.
- `tests/MapaTur.Application.Tests/Terrain/TerrainMesh3DAdaptiveTests.cs` — nowy test. ZOSTAW.
- `testdata/maps/generate-tatry-dem-lidar.py` — ślepy zaułek (re-bake DEM anti-moiré). **Do rozważenia revert** (patrz niżej).
- `dem/tatry.dem.pre-rebake` — backup (gitignored); `dem/tatry.dem` jest ORYGINALNY (re-bake cofnięty).

---

## WĄTEK 1 — Paczki danych z sieci (serwer → telefon)

Apka nie miała pobierania danych (side-load adb/instalator). User: „serwer Railway, 1 m, im więcej danych tym płynniej".
Zbudowany + WDROŻONY pełny mechanizm pre-baked paczek po HTTP, rozpakowywanych w katalogi z których renderer czyta.
Pełny opis: `docs/PACKAGES.md` (runbook) + `docs/PACKAGES-architecture.md` (techniczny) + `docs/diagrams/package-deploy-model.svg`.

**Kod (na main):** namespace **`Packaging`** (NIE `Packages` — `.gitignore **/[Pp]ackages/*` zjadał folder!).
- Application: `PackageModels` (RegionPackage/PackageManifest/PackageLayer{Dem,Ortho,BaseDem}/PackageFormat + porty),
  `PackageManifestParser` (**forward-compatible**: nieznana warstwa/format → pomiń paczkę, nie wywalaj), `PackageCatalog.Merge`,
  `PackageInstaller` (HTTP Range → `.part` → resume → SHA-256 → extract → marker), `OfflinePackageService`.
- Infrastructure: `FileInstalledPackageStore`, `HttpPackageFileFetcher` (Range, owning-stream dla CA2000),
  `PackageContentExtractor` (DEM-zip→dem-cache/gugik; orto-PNG-zip→maps; **BaseDem-zip→dem/**; mbtiles→maps), `HttpPackageCatalogSource`.
- App: DI w `MauiProgram` (`defaultPackagesBaseUrl` = **https://mapatur-production.up.railway.app**, env override `MAPATUR_PACKAGES_BASEURL`),
  `DownloadDataPackagesCommand` + przycisk „📦 Pobierz paczki danych" + `OnDownloadDataPackagesTapped` (bramka WiFi).
- Tools (poza slnx/CI): `tools/PackageServer` (Railway), `tools/PackageBaker` (`pack-dir`/`pack-file`).

**SERWER — ŻYJE (Railway):** projekt `graceful-quietude`/serwis `MapaTur`, EU West, Root Directory `tools/PackageServer`,
Volume `mapatur-volume`→`/data`, domena **https://mapatur-production.up.railway.app** (`/healthz`=ok).
- Wgrane paczki: `tatry-ortho` v1 (516 MB) + `tatry-dem-base` v1 (30 MB) + `manifest.json`. Prod-zweryfikowane (GET/HEAD/Range/SHA).
- **Upload na volume:** endpoint `PUT /admin/upload/{*relPath}` chroniony nagłówkiem `X-Upload-Token` == env `UPLOAD_TOKEN`.
  Token tej sesji: `32eaab475e5f4c3df6a2805ebdda12a7c01a9bbb` (też w `%TEMP%\mapatur-upload-token.txt`). **Do rotacji/usunięcia** gdy niepotrzebny (usuń `UPLOAD_TOKEN` → endpoint 404).
- Pułapki Railway: (1) Dockerfile `VOLUME` ODRZUCONY („use Railway Volumes") → usunąć linię; (2) „Generate Domain" wymaga
  DZIAŁAJĄCEGO serwisu + pyta o target port → **8080** (z `EXPOSE 8080`); (3) volume zasilasz endpointem, nie UI.

**DEVICE-CONFIRMED (Galaxy S25 Ultra na kablu, adb):** reinstal wymazał dane → DEM przywrócony kablem (`adb push dem/tatry.dem
→ /sdcard/Android/data/com.companyname.mapatur.app/files/dem/`) → 3D+strzałki wróciły → „📦" ściągnęło 516 MB orto z Railway →
restart → ortofoto na terenie. **Deploy-z-sieci potwierdzony na żywym telefonie.**

**Jak dosłać kolejną paczkę (np. 1 m DEM):** `docs/PACKAGES.md` — `PackageBaker pack-dir <cache> --layer Dem … --base-url <railway>`
→ `curl -T … -H "X-Upload-Token: …" {base}/admin/upload/packages/…`. Egress orto duży → opcja R2 (sam `url` w manifeście).

---

## WĄTEK 2 — Ortho „strata stripes" + czarny trench (FIX NAPISANY, niepotwierdzony)

User pokazał na desktopie 3D: równoległe paski na stoku („z punktów", **niezależne od kąta**) + pionowy czarny trench.

### ❌ ŚLEPE ZAUŁKI (NIE POWTARZAĆ — spalony czas)
1. **Re-bake `tatry.dem` (anti-moiré low-pass + despike)** — dane były CZYSTE (grazing hillshade to pokazał), paski NIE są w danych.
   Re-bake **cofnięty** (`dem/tatry.dem` = oryginał, backup `dem/tatry.dem.pre-rebake`). Skrypt `generate-tatry-dem-lidar.py`
   ma jeszcze te zmiany (masked Gauss low-pass + despike, env `DEM_OUTPUT`/`DEM_LOWPASS_SIGMA`) — **zweryfikowane jako
   surface-preserving** (szczyty 0, jeziora <1.7 m) ale NIEzwiązane z bugiem. **Decyzja next session: revert skryptu albo zachować
   jako latentne ulepszenie bake'u** (bilinear-downsample faktycznie aliasuje, ale to nie była przyczyna pasków).
2. **`uDebugUv=1` (UV-as-colour gradient)** — BEZUŻYTECZNE: płaski kolor bez cieniowania, a paski są zjawiskiem oświetleniowym/teksturowym.
   Notatka `[[ortho-strata-stripes]]` wprost: „use the clamp viz (`uDebugUv=2`), not UV viz". (Również: grazing/normalne = WRONG, „~30 builds wasted".)

### ✅ PRAWDZIWA PRZYCZYNA (znaleziona w kodzie)
`TerrainMesh3D.BuildAdaptiveTiles` dzieliła kafle przez `StepAlignedCuts` — **tylko siatka kroku, NIE granice komórek ortofoto**.
Blok straddl­ujący granicę komórki → `BuildBlock` wybiera komórkę po środku (`CellAt(centre)`) → dalsze wierzchołki klampują UV
(`LocalUv` poza [0,1] → GL_CLAMP_TO_EDGE) → rząd brzegowych texeli rozciągnięty = **paski niezależne od reliefu**.
Bliźniacza `BuildTiles` dostała cięcie na komórkach w `197cde6` (`BuildTileCuts`), ale **baza LOD desktopu jedzie
`BuildAdaptiveTiles`** (`MapPageViewModel.cs:2409`) — fix był w JEDNEJ ścieżce, a desktop używa DRUGIEJ.

### ✅ FIX (napisany, 859 Application tests green)
W `BuildAdaptiveTiles`: liczę granice komórek z `orthoCoverage` (jak `BuildTiles`) i zamieniłem `StepAlignedCuts` →
`CutsWithCellBoundaries` (cięcie na komórkach + równe części ≤seg). Bloki nie straddl­ują → brak clampu → paski znikają.
Equal-part cuts likwidują też 1-2-kolumnowe slivery na krawędzi komórki → **prawdopodobnie kasuje też pionowy trench**.
Crack-free/welding nietknięte (test `BuildAdaptiveTiles_AdjacentTiles_ShareBoundaryVerticesExactly` green). Nowy test:
`BuildAdaptiveTiles_SpanningTwoOrthoCells_CutsAtTheCellBoundary`.

### ➡️ NEXT (dokończyć wątek 2)
1. Rebuild desktopu (`run.ps1`; ubij blokującą instancję twardo: `Stop-Process -Force` ×2, bo `taskkill` daje Access denied).
2. **User patrzy na NORMALNY render** (debug off) na tych stokach: paski znikły? trench znikł?
3. Jeśli TAK → commit (`fix(terrain): cut adaptive LOD tiles at ortho cell boundaries — kills strata stripes on the desktop base`)
   + push (za „tak"). Zdecyduj o `generate-tatry-dem-lidar.py` (revert lub osobny commit jako quality-improvement).
4. Jeśli NIE → paski to nie ta ścieżka; wróć do `uDebugUv=2` clamp viz (GREEN=V pinned = smoking gun) — ale to wymaga
   widoku z cieniowaniem? Nie — clamp viz jest osobnym renderem; jeśli mimo fixu green band jest, znaczy inny straddle/coverage.

---

## LEKCJE TEJ SESJI (kosztowne)
- **Czytaj pamięć ZANIM zdiagnozujesz** — `[[ortho-strata-stripes]]` miała gotową diagnozę (cell-clamp, NOT grazing, użyj clamp viz).
  Powtórzyłem oba spalone wcześniej błędy (grazing + zła viz). User wkurzony, słusznie.
- **Moje czytanie zrzutów jest zawodne na drobnym detalu** — nie udawaj „widzę paski"; daj userowi patrzeć, ja czytam kod.
- **Nie testuj zjawiska oświetleniowego płaskim kolorem bez cieniowania.**
- **Diagnoza z danych ≠ z renderu:** grazing hillshade danych = czysty ⇒ artefakt jest w rendererze (tiling/UV), nie w `tatry.dem`.
- `run.ps1`: stara instancja blokuje exe (MSB3027); `taskkill /F` daje Access denied → `Get-Process | Stop-Process -Force` (×2).
