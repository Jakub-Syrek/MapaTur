# Architektura: dostarczanie paczek danych (serwer → telefon)

Opis techniczny mechanizmu pobierania paczek danych (ortofoto, DEM) z serwera do aplikacji.
Diagram: [`diagrams/package-deploy-model.svg`](diagrams/package-deploy-model.svg). Runbook (jak postawić /
zasilić): [`PACKAGES.md`](PACKAGES.md).

## Cel

Dostarczyć ciężkie dane mapowe **bez ręcznego side-loadu**, tak by rozpakowana zawartość trafiała
**dokładnie w katalogi, z których renderer już czyta cache-first** — pobranie niczego nie zmienia w
rysowaniu, tylko napełnia dane. Im więcej paczek na telefonie, tym płynniej (zero streamingu w terenie).

```
[KOMPUTER · bake]            [RAILWAY · serwer]              [TELEFON · apka]
 dane na dysku                PackageServer                   1 Katalog (manifest → stan)
   │ PackageBaker             static · Range · HEAD           2 Pobieranie (Range, .part, 64 KB)
   ▼ zip + SHA-256 + manifest Volume /data                    3 Weryfikacja SHA-256
 artefakty  ──PUT (token)──►  PUT /admin/upload  ──HTTP──►    4 Rozpak → maps/ / dem-cache
                              mapatur-production…app           5 Teren 3D offline (cache-first)
```

## Etap 1 — Bake (komputer, jednorazowo)

`PackageBaker` (`tools/PackageBaker`, poza solucją/CI) zamienia dane z dysku w wersjonowaną paczkę:

- `pack-dir <katalog> --id … --layer Dem|Ortho --version N --out <srv> --base-url <url>`.
- **Zipuje** drzewo (`CompressionLevel.Optimal`, bez katalogu bazowego), liczy **SHA-256** całego zipa i rozmiar.
- **Upsertuje `manifest.json`** tym samym `PackageManifestParser`, którego używa aplikacja (zgodność formatu) —
  podmienia wpis o tym samym `id`, sortuje po `id`.

Wyjście: `srv/packages/<id>-vN.zip` + `srv/manifest.json`.

## Etap 2 — Serwer (Railway)

`PackageServer` — ASP.NET minimal API (.NET 10), kontener (`sdk:10.0` → `aspnet:10.0`), Root Directory
`tools/PackageServer`, słucha na `$PORT` (8080):

- **`PACKAGES_DIR=/data`** podpięty pod **Railway Volume** (trwałość między deployami).
- **Static file middleware** serwuje `/manifest.json` + `/packages/*` z **HTTP Range + HEAD**
  (`Accept-Ranges: bytes`), `Cache-Control: immutable` (paczki wersjonowane w nazwie), MIME `.zip/.mbtiles/.tif`.
- `GET /healthz` → `ok`.
- **`PUT /admin/upload/{*relPath}`** — kanał zasilania volume'u (Railway nie ma uploadu w UI): nagłówek
  `X-Upload-Token` == env `UPLOAD_TOKEN` (bez tokena = 404), anti-traversal (zapis tylko w `/data`),
  strumieniowy zapis body, zdjęty limit body Kestrela.

**`url` każdej paczki w manifeście jest absolutny** — bloby można przenieść na CDN (np. Cloudflare R2,
zero egress) zostawiając manifest na Railway; aplikacja idzie za tym, co manifest podaje, **bez zmiany kodu**.

## Etap 3 — Telefon (aplikacja, offline-first)

DI w `MauiProgram`: `HttpPackageCatalogSource` (→ `{base}/manifest.json`), `FileInstalledPackageStore`
(`{AppData}/packages/installed`), `PackageContentExtractor` (DEM→`dem-cache/gugik`, orto→`maps/`),
`PackageInstaller` (work-dir `{AppData}/packages/work`), spięte w `OfflinePackageService`.

1. **Katalog.** `GetCatalogAsync()` → manifest + `PackageCatalog.Merge(manifest, store.List())` →
   `PackageStatus` ze stanem **NotInstalled / Installed / UpdateAvailable** (po `version`).
2. **Pobieranie (`PackageInstaller.InstallAsync`).** HEAD → `Content-Length` = `total`; plik `{id}.part`,
   `have` = jego rozmiar (**wznawianie**); `have>total` → odrzuć. Jeśli `have<total`:
   `Range: bytes={have}-` → `206` → strumień **chunkami 64 KB**, dopisywany do `.part`, raport
   `PackageDownloadProgress(received, total)`.
3. **Weryfikacja.** SHA-256 z `.part` == `sha256` z manifestu; niezgodność → `.part` skasowany +
   `InvalidDataException` (retry pobiera czysto, nigdy nie instaluje uszkodzonych danych).
4. **Rozpak (`PackageContentExtractor`).** Orto-zip → **`maps/`** (kafle `tatry-ortho-r{R}-c{C}.png`,
   znajdowane regexem `FileSystemMapAutoLoader`); DEM-zip → `dem-cache/gugik`; mbtiles → `maps/{id}.mbtiles`.
5. **Marker + render.** `InstalledPackage(id, version, sha256)` jako JSON; renderer czyta cache-first →
   teren/ortofoto 3D z dysku.

UI: przycisk **„📦 Pobierz paczki danych"** → `DownloadDataPackagesCommand` (tylko paczki ≠ Installed,
procent w pill); bramka WiFi w code-behind.

## Chunking / wznawialność

Plik = ciąg bajtowych zakresów. Pobrana część siedzi w `.part`; **zerwane łącze → wznowienie od ostatniego
bajtu (`Range: bytes=N-`), nie od zera.** Sekwencja: `Range: bytes=N-` → `206 Partial Content` → chunki 64 KB
→ komplet → `SHA-256 ✓` → rozpak.

## Format manifestu (kontrakt bake ↔ apka)

```json
{ "packages": [ {
  "id": "tatry-ortho", "name": "Tatry ortofoto (mobile)",
  "layer": "Ortho", "format": "ZipTileCache",
  "version": 1, "sizeBytes": 540806038,
  "sha256": "da79e736…",
  "url": "https://mapatur-production.up.railway.app/packages/tatry-ortho-v1.zip"
} ] }
```

Enumy jako stringi (`layer`: Dem|Ortho, `format`: ZipTileCache|MBTiles), `version` napędza aktualizacje,
`sha256` — integralność, `url` — absolutny (swap na CDN bez zmiany kodu).

## Backend vs devops

- **Backend / systemy:** `PackageServer`, protokół transferu (Range + wznawianie + SHA-256), manifest jako
  kontrakt API, parser/katalog/installer.
- **DevOps:** Dockerfile, Railway, Volume, env/sekrety, domena, CI auto-deploy, hosting/egress (Railway↔R2).
- **Mobile/client:** DI w MAUI, komenda + przycisk, wpięcie w ścieżki cache renderera.

Papierek lakmusowy granicy: przeniesienie orto na R2 = **zero zmian w backendzie** (bo `url` absolutny),
zmiana **wyłącznie devops**.