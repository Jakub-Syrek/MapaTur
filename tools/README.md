# tools/

Stand-alone developer/operator utilities that ship **alongside** the app but are **not part of any
shipped binary**. They are deliberately kept out of the solution's app/test build so the MAUI app never
takes a dependency on them. Each is a self-contained .NET project you run by hand.

> Data-generation scripts (DEM / orthophoto / hillshade / trails / lake bakes) live separately under
> [`../testdata/maps/`](../testdata/maps/) — those produce the `.dem` / ortho fixtures. The tools here are
> about **distributing** that baked data to devices.

| Tool | What it is | When you reach for it |
|---|---|---|
| [`PackageServer/`](PackageServer/) | ASP.NET minimal API that serves the offline data-package catalogue (`manifest.json`) + the package blobs over HTTP. | Hosting the region packages the app downloads (deployed on Railway). |
| [`PackageBaker/`](PackageBaker/) | CLI that zips on-disk baked data (DEM tile cache, ortho `.mbtiles`) into versioned packages and writes the `manifest.json`. | Producing a new package to upload to the server. |

Together they are the **offline-data delivery pipeline**: the app's in-app downloader pulls a manifest from
`PackageServer`, downloads the package blobs (HTTP Range → resume → SHA-256 → extract into the renderer's
data dirs). The matching in-app code is the `MapaTur.Application.Packaging` namespace + its Infrastructure
adapters. Full design + runbook: [`../docs/PACKAGES.md`](../docs/PACKAGES.md) and
[`../docs/PACKAGES-architecture.md`](../docs/PACKAGES-architecture.md).

---

## PackageBaker

Bakes a directory or a single file into a versioned `*.zip` (or passes an `.mbtiles` through) and emits a
`manifest.json` that advertises each package's download URL, layer, format, byte size and SHA-256.

```bash
# DEM 1 m: zip the app's GUGiK tile cache into a package
dotnet run --project tools/PackageBaker -- pack-dir "<dem-cache>/gugik" \
  --id tatry-dem-1m --name "Tatry — detal 1 m" --layer Dem --version 1 \
  --out ./srv --base-url https://mapatur-production.up.railway.app

# Ortho: package a prebuilt .mbtiles
dotnet run --project tools/PackageBaker -- pack-file ./tatry-ortho.mbtiles \
  --id tatry-ortho --name "Tatry — ortofoto" --layer Ortho --version 1 \
  --out ./srv --base-url https://mapatur-production.up.railway.app
```

The `--out` directory ends up as `{out}/manifest.json` + `{out}/packages/<files>` — that layout maps 1:1
onto the server's `PACKAGES_DIR`. Run with no args for the full usage text.

## PackageServer

A static-file server (HTTP `Range` + `HEAD` come for free, which is exactly what the resumable in-app
downloader needs) plus a small token-gated upload endpoint.

| Route | Purpose |
|---|---|
| `GET /manifest.json` | The package catalogue the app reads. |
| `GET /packages/<file>` | The package blobs (Range-enabled, cached `immutable`). |
| `GET /healthz` | Liveness probe. |
| `PUT /admin/upload/{*relPath}` | Push a baked package onto the volume. **Disabled unless `UPLOAD_TOKEN` is set**; requires header `X-Upload-Token: <token>`; path-traversal-confined to the data dir. |

| Env var | Default | Meaning |
|---|---|---|
| `PORT` | `8080` | Listen port (injected by Railway/most PaaS). |
| `PACKAGES_DIR` | `<contentRoot>/data` | Where manifest + `packages/` live — point at a persistent volume. |
| `UPLOAD_TOKEN` | *(unset → upload 404s)* | Shared secret for `PUT /admin/upload`. Rotate/remove when not actively uploading. |

Run locally:

```bash
dotnet run --project tools/PackageServer   # serves ./data on http://localhost:8080
```

**Deployed on Railway** (the app's default `manifest` URL is
`https://mapatur-production.up.railway.app`): set the service **Root Directory** to `tools/PackageServer`,
attach a **Volume** mounted at `/data`, and deploy — the `Dockerfile` builds it. Gotchas baked into the
Dockerfile comments: Railway **rejects a Docker `VOLUME` instruction** (use a Railway Volume instead), and
"Generate Domain" needs a running service and asks for the target port → **8080**.
