# HANDOFF — 2026-06-21 (wieczór) — „plastelina" na mobile ROZWIĄZANA, push na main, PL/EN otwarte

> Living doc. Najważniejsze na górze. Głęboki kontekst przyczyny → memory `lod-mobile-plasticine-detail-nulled`.

---

## ⭐ START HERE — stan na koniec sesji

- **`main` jest ZIELONE i WYPCHNIĘTE.** `origin/main = 0e93dd1`. Oba workflowy CI = **SUCCESS** (CI: build+test 4 liby + format + MAUI Windows Release; „Latest APK": build Androida). 15 commitów: backlog poprzedniej sesji (7) + cały epos plasteliny + diagnostyka + README (3 obrazki) + UI.
- **Working tree CZYSTY** — wszystko scommitowane. `signing.local.props` jest **gitignored** (`*.local.props`) i trzyma hasło keystore lokalnie; CI/inni go nie mają (csproj importuje warunkowo).
- **Telefon** `RFCY1198TTX`: zainstalowany **vc141** (release-signed). Ma realne kafle z16 (z paczki `tatry-dem-1m`) → **teren OSTRY**. Desktop też budowany+odpalany w tej sesji (Windows TFM).
- **JEDYNE NAPRAWDĘ OTWARTE: lokalizacja PL/EN.** Ledwie zaczęta — tylko rozpoznanie architektury, **ZERO kodu**. Plan + co znalazłem niżej (§ PL/EN — NASTĘPNY KROK).
- Miękkie: brak formalnego werdyktu wizualnego z desktopu na świeżej robocie UI (menu 6-tab / lift szlaków / A-D strafe / drag-reorder / locate-bar). User aktywnie ich używał (dokładał na nich kolejne featury), więc de facto działają — ale jak coś poprawić, to tam.

---

## 🏔️ Z CZYM WALCZYLIŚMY — „plastelina" na telefonie (GŁÓWNA BITWA, ROZWIĄZANA device-confirmed)

**Objaw:** na telefonie Tatry wyglądały jak wygładzona plastelina — ściany→kopuły, granie→obłe wały, kotły znikały, jezioro=płytkie wgłębienie — mimo badge'a „LOD 1 m". Desktop był OSTRY. Trwało to dniami.

**Fałszywe tropy (NIE goń ich znów — spalone w tej sesji):**
- ❌ baza uśredniana (low-pass) — OBALONE: baza to nearest-neighbour **stride-decimation**, zero uśredniania.
- ❌ sprzężenie baza↔detal (detal clamp/morph do bazy) — OBALONE: detal czyta wysokość absolutnie, `edgeHeightSource=null` na aktywnej ścieżce.
- ❌ precyzja dekodu / inny DEM na mobile — OBALONE: Float32 oba, ten sam `tatry.dem`.
- ❌ `BaseDetailZoomFloor` null-out / budżet wierzchołków 3M / okno 2200 m / promienie bazy — to były **objawy/wzmacniacze, nie przyczyna**. Budżet 6M i vh-fix NIE pomogły, bo detal w ogóle nie powstawał z sensownych danych.

**PRAWDZIWA PRZYCZYNA (device-confirmed, `run-as` + PIL):**
> **Cały cache z16 1 m na telefonie to KAFLE WYPEŁNIONE ZERAMI** — poprawne GeoTIFF-y 256×256 float32 (~262 KB), ale treść = same 0.0. Desktop ma realne (np. ten sam kafel `16/36359/22423.tif`: telefon 262751 B extrema (0,0); desktop 262416 B **913–1006 m**). Z 7338 kafli z16 desktopu **4265 ma realne dane, 3073 to legalne zera** (brak pokrycia GUGiK — strona SK/zachód). Skutek w kodzie: `BuildPerTileDetailAsync` → `DemRasterCoverage.HasTerrain(holed, 100)` zwraca false (max=0) → `return null` → render BAZY → płasko.

**Skąd zera:** „Pobierz Tatry offline" → `OfflineRegionDownloader` → `GugikNmtDemTileSource` zapisywał odpowiedź GUGiK WCS **verbatim, bez walidacji treści**. GUGiK 2026-06-15 (flaky dzień / przeciążenie / brzeg) zwrócił walidne-strukturalnie, ale zerowe kafle. Zero przechodziło: HTTP200 → dekoduje → `SanitizeNoData` (próg −10000, więc 0.0 zostaje jako „0 m") → liczone jako pobrane → `IsCached`=`File.Exists` ⇒ **trwała trucizna, nigdy nie przefetchowana**. Detektor `HasTerrain` istniał, ale tylko przy renderze, nie przy cache'owaniu.

**FIX (3 warstwy, wszystkie device-confirmed):**
1. **Natychmiastowy:** ręczna kopia realnych kafli desktop→telefon (`run-as cp`). Teren od razu ostry.
2. **Durable GUARD (na main, commit `d2dd30a`):** `GugikNmtDemTileSource.GetTileAsync` waliduje teren **PRZED** zapisem cache (`!DemRasterCoverage.HasTerrain(raster, EmptyTileFloorMeters=1.0)` → `return null`, nic nie zapisuje). All-zero z GUGiK się nie cache'uje → przejściowe zero **samo się leczy** przy kolejnym fetchu. TDD-test: all-zero TIFF → null + brak pliku. ⚠️ Próg NISKI (1 m), nie 100 m — bo źródło serwuje też niżej; i NIE robi blind-audytu istniejących zer (te 3073 legalne by się re-fetchowały w kółko = churn).
3. **Dystrybucja — paczka (§ niżej).**

**LEKCJA:** gdy „plastelina" mimo z16 ON → **sprawdź realną TREŚĆ kafli na urządzeniu** (`run-as`), zanim zaczniesz dłubać w geometrii/mesh/budżecie. To była czysta DANA, nie pipeline.

Pełen kontekst: memory `lod-mobile-plasticine-detail-nulled`.

---

## 📦 Paczka offline `tatry-dem-1m` (DZIAŁA, live na Railway, device-confirmed)

- Zbudowana `tools/PackageBaker`: `dotnet run --project tools/PackageBaker -c Release -- pack-dir <stage z folderem `16`> --id tatry-dem-1m --name "Tatry - detal 1 m" --layer Dem --version 1 --out <out> --base-url https://mapatur-production.up.railway.app`.
  - Staging realnych kafli: `robocopy "<desktop dem-cache/gugik>\16" "<stage>\16" *.tif /S /MAX:262750` (wyklucza 262751 B zera). 4265 kafli.
  - Format paczki Dem = ZIP wpisów **`z/x/y.tif` w korzeniu** → `PackageContentExtractor` (Layer.Dem) rozpakowuje do `AppDataDirectory/dem-cache/gugik/{z}/{x}/{y}.tif`.
- **Wynik: `tatry-dem-1m-v1.zip` = 636 MB, sha256 `cff07272a9df708db3e589718193743fc055ecbed1b78604d9143c7dbb993e60`.** Manifest scalony do 3 paczek (zachowane `tatry-ortho` + `tatry-dem-base`).
- Wgrane na Railway: `PUT /admin/upload/...` z `X-Upload-Token` (= env `UPLOAD_TOKEN` na Railway Variables; **token NIE w repo/handoffie** — user go podał w sesji: `32eaab…`, oznaczony do-rotacji). Zip 200, manifest 200, HEAD 200 + Accept-Ranges.
- **Device-test (rygorystyczny) PRZESZEDŁ:** `run-as mv 16→16_bak` (wymaz) → plastelina (`badge z16 ON 0/144 →BASE(no-raster)`) → w apce „Pobierz paczki danych (serwer)" pobrano TYLKO `tatry-dem-1m` → restart → **ostro** (`badge z16 ON 144/144 s3.5/1`). `16_bak` usunięty.
- Runbook serwera/paczek: `docs/PACKAGES.md`. Re-bake = bump `--version`.

---

## 🛠️ Narzędzia diagnostyczne, które ZADZIAŁAŁY (zapamiętaj)

- **`run-as DZIAŁA na Debug-buildzie** (`debuggable=true`)** — handoff poprzedniej sesji mylił się, że puste (to był release-config). `adb exec-out run-as PKG cat files/dem-cache/gugik/16/X/Y.tif > t.tif` (binarnie OK) → PIL `Image.open().getextrema()`. Tak złapaliśmy zera.
- **Kopia plików NA telefon przez run-as:** `MSYS_NO_PATHCONV=1` (Git Bash mangluje ścieżki Androida `/data/...`→`C:/...`!); `adb push <plik> /storage/emulated/0/Android/data/PKG/files/_stage` → `adb shell run-as PKG cp <ext> files/dem-cache/gugik/16/X/Y.tif` (ścieżka dst RELATYWNA, cwd=home; redirect `>` blokuje SELinux, `cp` z external-app-dir działa, `/data/local/tmp` NIE — shell-owned). `sh -c` ma cwd=`/` nie home → używaj ścieżek bezwzględnych ALBO bezpośredniego `run-as PKG <cmd>`.
- **On-screen badge `LodDetailDiagnostics`** (na main, `0d460ae`) — bo logcat/Serilog z telefonu pusty. Pokazuje `z.. ON/OFF/→BASE(powód raw/holed) · cache x/y · s avg/finest · dist · vh · src`. To on zdiagnozował null/no-terrain/raw0. Plik: `src/MapaTur.Application/Terrain/LodDetailDiagnostics.cs` (czyste, culture-invariant, 7 testów).
- **vh-fix (mobile-only):** `Terrain3DView.SurfacePixelHeight` (realny backbuffer) wpięty do `OnDetailFocusAsync` — bo 2D Mapsui viewport nie jest layoutowany w 3D → `TryGetMapFocus` zwracał false → vh=1000. Desktop nietknięty.
- **Deploy:** lokalny build podpisuje się sam (props z keystore). `adb install -r` po release-signie zachowuje dane (memory `deploy-sign-release-keystore`). versionCode > obecnego na telefonie (był 141; bumpuj). adb: `C:\Program Files (x86)\Android\android-sdk\platform-tools\adb.exe`.

---

## 🎨 Robota UI (na main, desktop-built; mobile dostanie po buildzie)

- **Menu przebudowane na 6 tabów** (`0e93dd1`): 🧭 Trasa (tylko planowanie) · 🎚️ Tryby (wszystkie warstwy/przełączniki/POI) · 🎥 Widok · 🗺️ Mapa (dane: regiony/wczytaj 1m/offline/paczki) · 🌦️ Pogoda · ⚙️ Ustawienia. `ActiveSection` 1..6. Code-behind `AnimateActiveSection` + VM `OnActiveSectionChanged value==6`.
- **Pasek find-me/teleport** — osobny, ZAWSZE dostępny (niezależny od planowania trasy + od ActiveSection), **zwijalny** (nagłówek „🔍 Lokalizacja i szukanie" ▾/▸ → `ToggleLocateBarCommand` / `IsLocateBarExpanded`). Zawiera 🎯 Na mnie, przełącznik śledzenia, teleport (`PlaceQuery`→`PlaceResults`→„🎯 Lecę" `TeleportToPlaceCommand`).
- **Trasa — drag-reorder przystanków:** `CollectionView CanReorderItems="True"` + `ReorderCompleted="OnRouteStopsReorderCompleted"` → `viewModel.ReplanAfterReorderAsync()`. Ładniejsze kafle (uchwyt ⠿ + nazwa + wysokość + chip „✕ Usuń"). ⚠️ MAUI `CanReorderItems` na desktopie bywa kapryśny — jak nie chwyta, dodać strzałki ↑/↓ na kaflu (pewny reorder).
- **A/D = strafe (desktop):** `Terrain3DView.OnPlatformKeyDown` — A/D `ApplyPan` (było `ApplyOrbit`); W/S przód/tył, mysz obraca. Windows-only handler. Usunięto osierocony `KeyOrbitPixelStep`.
- **Lift overlayów 6→10 m** (`bc9d1bb`): szlaki/trasa/drogi były gdzieniegdzie przykrywane przez detal 1 m. Lift to offset world-space → grań PRZED linią nadal zasłania (lift≪wysokość grani) → fix bez powrotu „szlaki przez skały" (bias `-=0.04` nietknięty). **WSPÓLNE dla obu platform** (user świadomie chciał oba, testuje na desktopie).
- **README** — 3 obrazki showcase w `docs/screenshots/` (desktop Morskie Oko + golden-hour Czarny Szczyt + realna fotka Tatr), commity `561a605`/`6f73517`. ⚠️ `screens/` jest gitignored — obrazki do README kopiuj do `docs/screenshots/`.

---

## 🌐 PL/EN — OTWARTE, NASTĘPNY KROK (tu przerwaliśmy)

User chce wsparcia polski/angielski (panel ustawień/środowisko). **Zakres NIE wybrany** — wcześniej dawałem opcje: (a) menu+ustawienia teraz, (b) tylko przełącznik teraz, (c) pełna apka. User powiedział tylko „pl en". **ZAPYTAJ o zakres** zanim ruszysz dużą migrację.

**Architektura, którą zdążyłem rozpoznać:**
- Lokalizacja JEST: `src/MapaTur.App/Resources/Localization/AppResources.resx` (**domyślny = ANGIELSKI**, 76 wpisów) + `AppResources.pl.resx` (polski). Klasa `src/MapaTur.App/Localization/AppStrings.cs` = `ResourceManager` wybierający satelitę wg `CultureInfo.CurrentUICulture`. Wzór `[Get(nameof(X))]`.
- **Kultura ustawiana NA SZTYWNO na polski** w `src/MapaTur.App/App.xaml.cs:17-18` (`DefaultThreadCurrentUICulture = polish; CurrentThread.CurrentUICulture = polish`). To trzeba zmienić na czytane z ustawień.
- **PROBLEM GŁÓWNY:** większość widocznego UI (chipy menu „Mapa"/„Pogoda", „Na mnie", warstwy, etykiety) jest **HARDCODE'owana po polsku** w `MapPage.xaml`, NIE przez `AppStrings`. Tylko parę miejsc używa `{x:Static loc:AppStrings.XXX}` (Title, accessibility, Download*-przyciski, vertical-exaggeration hint) + parę statusów w VM przez `string.Format(CurrentUICulture, AppStrings...)`. Więc **sam przełącznik kultury NIC nie zmieni w menu**, póki stringi nie trafią do resx (EN+PL).
- ⚠️ `{x:Static loc:AppStrings.X}` jest rozwiązywane **compile-time** — nie zaktualizuje się przy zmianie kultury w runtime. Dla LIVE-przełącznika trzeba markup-extension/`LocalizationResourceManager` (re-resolve + PropertyChanged) ALBO **restart apki** po zmianie języka (prościej). Settings-store do zapisu preferencji języka: **NIE znalazłem dokładnie** (grep trafił `PackageManifestParser` — szukaj `ISettingsStore`/`Preferences`/`SqliteSettingsStore` w `src/MapaTur.Infrastructure` lub jak zapisywane są inne ustawienia w VM, np. `settingsStore.RouteStopsJson`).

**Proponowany pragmatyczny plan (gdy user wybierze (a)):**
1. Settings: dodaj `Language` (pl/en) do settings-store; w `App.xaml.cs` czytaj kulturę z ustawień (fallback pl).
2. Ustawienia panel: przełącznik PL/EN → zapis + **restart apki** (najprościej; live-update to dużo więcej).
3. Migracja widocznego chrome: przenieś hardcode'owane stringi menu/nagłówki/przyciski do `AppResources.resx` (EN) + `AppResources.pl.resx` (PL), podmień na `{x:Static loc:AppStrings.X}`. Zacznij od 6 chipów + nagłówków sekcji + paska find-me/teleport.
4. Reszta (długie komunikaty, inne ekrany) przyrostowo.

⚠️ CI **format-checkuje TYLKO** Domain/Application/Infrastructure/Routing (NIE `MapaTur.App`) — pliki App nie muszą przechodzić `dotnet format` (mają LF, gitattributes `*.cs eol=lf`; lokalny `dotnet format` na Windows krzyczy CRLF = fałszywy alarm, ignoruj). CI buduje też App: build-and-test (4 liby+5 testów) + maui-build (App Windows Release publish) + osobny „Latest APK" (Android). Przed pushem na main: lokalny CI-equivalent = format 4 libów + `dotnet test` 5 projektów (Release) + `dotnet publish App -f net10.0-windows10.0.19041.0 -c Release -p:UseAppHost=false -p:WindowsPackageType=None`.

---

## Git / deploy / stan

- `main` == `0e93dd1` == feature-branch `fix/mobile-1m-zero-tiles` (ten sam commit; branch można usunąć). Wszystko na `origin/main`, CI green.
- ⚠️ **Push na main wymaga WPROST zgody usera** (CLAUDE.md + harness blokuje „go"/„a" jako za miękkie; trzeba „tak, push na origin/main" albo PR). Reguła: nigdy Claude jako autor/co-author (twardy zakaz — w tej sesji zweryfikowane: 15 commitów sam Jakub).
- Telefon: vc141, dane zachowane. Następny build mobilny: bump versionCode > 141, podpis auto z `signing.local.props`, `adb install -r`, force-stop+launch+`pidof`, „wgrane i potwierdzone".
- Desktop: `dotnet build src/MapaTur.App/MapaTur.App.csproj -f net10.0-windows10.0.19041.0 -c Debug` → exe w `…\net10.0-windows10.0.19041.0\win-x64\MapaTur.App.exe` (ubij `MapaTur.App.exe` przed buildem — lock).
