# Paczki danych offline (DEM 1 m + ortofoto)

Mechanizm dociągania danych mapowych do aplikacji **bez ręcznego side-loadu**: pre-zbudowane paczki
regionu leżą na serwerze (Railway), aplikacja pobiera je i rozpakowuje **dokładnie w te katalogi, z których
renderer i tak czyta** — więc pobranie niczego nie zmienia w rysowaniu, tylko napełnia dane. Im więcej
danych na telefonie, tym płynniej (zero streamingu, wszystko z dysku).

## Architektura

```
[bake na maszynie z danymi]                 [Railway]                 [telefon / desktop]
 dem-cache/gugik  --pack-dir-->  tatry-dem-1m-vN.zip   --HTTP Range-->  rozpak -> {AppData}/dem-cache/gugik
 dem-mobile/*.png --pack-dir-->  tatry-ortho-vN.zip    --HTTP Range-->  rozpak -> {AppData}/maps
                  \-->  manifest.json (id, wersja, rozmiar, sha256, url)
```

- **DEM** (`PackageLayer.Dem`, ZIP) → drzewo `{z}/{x}/{y}.tif` rozpakowane do `…/dem-cache/gugik`
  (skąd `GugikNmtDemTileSource`/`CompositeDemTileSource` czytają cache-first).
- **Ortofoto 3D** (`PackageLayer.Ortho`, ZIP) → kafle `tatry-ortho-r{R}-c{C}.png` do `…/maps`
  (skąd `FileSystemMapAutoLoader` montuje drape 3D — dopasowanie po nazwie pliku).
- **Mapa 2D** (`PackageFormat.MBTiles`, opcjonalnie) → `{id}.mbtiles` do `…/maps`.

Pobieranie jest **wznawialne** (HTTP Range → plik `.part`) i **weryfikowane** (SHA-256 z manifestu); zła suma
= partial odrzucony i błąd, więc retry pobiera od nowa, nigdy nie instaluje uszkodzonych danych.

### Kod
| Warstwa | Element |
|---|---|
| Application (czysta logika, testy) | `PackageModels`, `PackageManifestParser`, `PackageCatalog`, `PackageInstaller`, `OfflinePackageService` |
| Infrastructure (IO/HTTP) | `FileInstalledPackageStore`, `HttpPackageFileFetcher` (Range), `PackageContentExtractor`, `HttpPackageCatalogSource` |
| App | DI w `MauiProgram`, komenda `DownloadDataPackagesCommand` + przycisk „📦 Pobierz paczki danych" |
| Tools | `tools/PackageServer` (Railway), `tools/PackageBaker` (bake) |

## 1. Bake paczek (na maszynie, która ma dane)

```powershell
# DEM 1 m: spakuj cache GUGiK, który apka/desktop już napełniły
dotnet run --project tools/PackageBaker -c Release -- pack-dir `
  "$env:LOCALAPPDATA\...\dem-cache\gugik" `
  --id tatry-dem-1m --name "Tatry — detal 1 m" --layer Dem --version 1 `
  --out ./srv --base-url https://TWOJ-SERWIS.up.railway.app

# Ortofoto: spakuj kafle PNG z dem-mobile (tatry-ortho-r*-c*.png)
dotnet run --project tools/PackageBaker -c Release -- pack-dir `
  ./dem-mobile `
  --id tatry-ortho --name "Tatry — ortofoto" --layer Ortho --version 1 `
  --out ./srv --base-url https://TWOJ-SERWIS.up.railway.app
```

Wynik w `./srv/`: `manifest.json` + `packages/tatry-dem-1m-v1.zip` + `packages/tatry-ortho-v1.zip`.
Bump `--version`, gdy zmienisz dane — apka wykryje „aktualizacja dostępna" po wersji w markerze.

## 2. Deploy serwera (Railway)

1. New Project → Deploy from Repo → wskaż to repo.
2. Service **Root Directory** = `tools/PackageServer` (użyje tamtejszego `Dockerfile`).
3. Dodaj **Volume** zamontowany na `/data` (to jest `PACKAGES_DIR`).
4. Deploy. Railway wstrzykuje `PORT`; healthcheck: `/healthz`.
5. Wgraj zawartość `./srv` (z punktu 1) na volume `/data` (przez `railway run`/SFTP/init-job), tak by powstało:
   `/data/manifest.json` i `/data/packages/*`.
6. Skopiuj publiczny URL serwisu (np. `https://twoj-serwis.up.railway.app`).

> Ortofoto bywa duże (kilka GB) → egress Railway może zaboleć. Można zostawić `manifest.json` na Railway,
> a same bloby orto przerzucić na **Cloudflare R2 (zero egress)** — wystarczy, że `url` w manifeście pokaże R2.
> Apka idzie za tym, co manifest podaje; zero zmian w kodzie.

## 3. Wskaż URL aplikacji

W `src/MapaTur.App/MauiProgram.cs` jest `defaultPackagesBaseUrl` (placeholder). Ustaw go na URL z kroku 2
(albo nadpisz zmienną środowiskową `MAPATUR_PACKAGES_BASEURL` w czasie developmentu). Po zmianie zbuduj APK.

## 4. W aplikacji

Panel **Mapa** → **„📦 Pobierz paczki danych (serwer)"** (bramka WiFi). Pobiera tylko brakujące/nieaktualne
paczki, z paskiem `%` w pill statusu. Po pobraniu **uruchom ponownie**, by auto-load wczytał nowe dane
(DEM 1 m podchwytuje LOD na bieżąco; ortofoto/drape wchodzi przy starcie).