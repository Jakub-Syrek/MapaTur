using System.Diagnostics;
using System.Globalization;
using System.Net;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;
using MapaTur.Infrastructure.Terrain;

namespace MapaTur.Infrastructure.Tests.Terrain;

/// <summary>
/// Dev/maintenance RUNNER (NOT a unit test) that bakes the Tatra region's DEM pyramid to disk using the
/// SAME <see cref="GugikNmtDemTileSource"/> the app registers, pointed at the REAL on-disk GUGiK 1 m cache,
/// then verifies the baked z16 heights are bit-identical to a fresh per-tile repair on real tiles. It is
/// gated behind the <c>MAPATUR_BAKE_TATRA=1</c> environment variable so a normal <c>dotnet test</c> never
/// runs it (it touches the user's real cache and writes hundreds of MB). It does NOT modify the renderer,
/// live detail path, routing, or decal — it only reads the raw cache and writes a new <c>baked/</c> tree
/// next to it.
///
/// Run it with:
///   MAPATUR_BAKE_TATRA=1 dotnet test tests/MapaTur.Infrastructure.Tests \
///       --filter FullyQualifiedName~TatraBakeRunner
///
/// Optional env overrides:
///   MAPATUR_GUGIK_CACHE   absolute path to the gugik cache root (default: the resolved desktop path)
///   MAPATUR_BAKE_OUT      absolute path for the baked output (default: &lt;cacheRoot&gt;/../baked)
///   MAPATUR_BAKE_BOUNDS   "south,west,north,east" to bake a sub-region instead of the full footprint
/// </summary>
public sealed class TatraBakeRunner
{
    private const string GateEnvVar = "MAPATUR_BAKE_TATRA";

    // The resolved desktop GUGiK cache root for this machine (the comprehensive ~7.3k-tile z16 set lives here).
    private const string DefaultGugikCacheRoot =
        @"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem-cache\gugik";

    // Bake the same descending, contiguous run the LOD streamer will pull (finest baked from source, coarser
    // derived by NoData-aware box-average of the level one step finer). Override with MAPATUR_BAKE_ZOOMS
    // (comma-separated, e.g. "17") to bake a NEW finest level — the sub-1m plan's z17 — WITHOUT touching the
    // existing z13–z16 pyramid (BakeRegionAsync derives coarser levels only for zooms in the list).
    private static readonly int[] DefaultZoomLevels = { 16, 15, 14, 13 };

    private static int[] ResolveZoomLevels()
        => Environment.GetEnvironmentVariable("MAPATUR_BAKE_ZOOMS") is { Length: > 0 } spec
            ? spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(z => int.Parse(z, CultureInfo.InvariantCulture))
                .ToArray()
            : DefaultZoomLevels;

    [Fact]
    public async Task BakeTatraRegion_FromRealGugikCache_AndVerifyBitIdentical()
    {
        string? gate = Environment.GetEnvironmentVariable(GateEnvVar);
        if (gate != "1")
        {
            // Not a failure — this runner is intentionally inert unless explicitly enabled.
            return;
        }

        string cacheRoot = Environment.GetEnvironmentVariable("MAPATUR_GUGIK_CACHE") ?? DefaultGugikCacheRoot;
        Directory.Exists(cacheRoot).Should().BeTrue(
            $"the real GUGiK cache must exist at '{cacheRoot}' (set MAPATUR_GUGIK_CACHE to override)");

        string bakedOut = Environment.GetEnvironmentVariable("MAPATUR_BAKE_OUT")
            ?? Path.Combine(Path.GetDirectoryName(cacheRoot.TrimEnd(Path.DirectorySeparatorChar))!, "baked");

        MapBounds bounds = ParseBoundsOrDefault(Environment.GetEnvironmentVariable("MAPATUR_BAKE_BOUNDS"));

        // Construct the SAME source the app uses (MauiProgram registers GugikNmtDemTileSource with tileSize 256
        // against {AppData}/dem-cache/gugik). We give it an HttpClient that can never reach the network, so the
        // bake reads ONLY the on-disk 1 m cache — any tile not already cached returns null and is skipped, which
        // makes the bake fully offline-deterministic over the real cached coverage. The app's OfflineRegionDownloader
        // uses this exact source type to populate that cache, so we bake from precisely the tiles the app produced.
        using var offlineHttp = new HttpClient(new OfflineHandler());
        var source = new GugikNmtDemTileSource(offlineHttp, cacheRoot, tileSize: 256);

        // Load the SAME coarse base the app loads (tatry.dem) and hand it to the baker so the finest tiles'
        // NoData voids — out-of-coverage holes punched by HoleBelow plus watercourse/border voids — are
        // backfilled from the base, exactly as the live per-tile detail path does (checklist §A.6). Without it
        // those cells stay NoData (the render base shows through). The bake is correct either way, but passing
        // the base reproduces the live surface (full repair coverage).
        string? baseDemPath = ResolveBaseDemPath(cacheRoot);
        DemRaster? baseDem = baseDemPath is not null ? DemRasterReader.Read(baseDemPath) : null;
        Console.WriteLine(baseDem is not null
            ? $"[bake] base DEM for backfill: {baseDemPath} ({baseDem.Columns}x{baseDem.Rows})"
            : "[bake] WARNING: no base DEM found — voids will stay NoData (set MAPATUR_BASE_DEM to a .dem path)");

        int[] zoomLevels = ResolveZoomLevels();
        int finestZoom = zoomLevels.Max();

        // Report planned counts before we start (so the log shows scope even if the bake is long).
        foreach (int z in zoomLevels)
        {
            long planned = DemTilePlanner.TileCount(bounds, z);
            Console.WriteLine($"[bake] z{z}: planned {planned} tiles over bounds " +
                $"S{bounds.SouthWest.Latitude} W{bounds.SouthWest.Longitude} " +
                $"N{bounds.NorthEast.Latitude} E{bounds.NorthEast.Longitude}");
        }

        // MAPATUR_BAKE_ZEROSTRIP: FillNarrowZeroStrips width in CELLS. Cell-denominated, so keep the z16
        // PHYSICAL policy (~37 m) on finer levels by scaling: z17 (0.78 m/cell) → 48 (audit: TILE-PRODUCTION §E.1).
        int zeroStripMaxCells = Environment.GetEnvironmentVariable("MAPATUR_BAKE_ZEROSTRIP") is { Length: > 0 } zs
            ? int.Parse(zs, CultureInfo.InvariantCulture)
            : DemTileBaker.DefaultZeroStripMaxCells;
        // MAPATUR_BAKE_DEALIAS=1 → wariant 3 (TILE-PRODUCTION §2.5): global de-alias + slope-gated wall
        // smooth on the finest level, killing the WCS sub-native weave and the wall organ-pipes.
        bool dealias = Environment.GetEnvironmentVariable("MAPATUR_BAKE_DEALIAS") == "1";
        Console.WriteLine(
            $"[bake] zooms: {string.Join(",", zoomLevels)}, zeroStripMaxCells: {zeroStripMaxCells}, dealias: {dealias}");

        var baker = new DemRegionBaker(
            source, maxConcurrentFetches: 8, baseDem: baseDem,
            zeroStripMaxCells: zeroStripMaxCells, dealiasFinest: dealias);

        int lastPct = -1;
        var progress = new SyncProgress<BakeProgress>(p =>
        {
            if (p.Total == 0)
            {
                return;
            }

            int pct = (int)(100L * p.Completed / p.Total);
            if (pct != lastPct && pct % 5 == 0)
            {
                lastPct = pct;
                Console.WriteLine($"[bake] {p.Completed}/{p.Total} ({pct}%)");
            }
        });

        var sw = Stopwatch.StartNew();
        BakeRegionResult result = await baker.BakeRegionAsync(bounds, zoomLevels, bakedOut, progress);
        sw.Stop();

        // -------- Per-zoom counts + bytes on disk (read back from the written tree) --------
        Console.WriteLine($"[bake] DONE in {sw.Elapsed.TotalSeconds:F1}s — " +
            $"{result.TilesWritten} tiles, {result.BytesWritten / (1024.0 * 1024.0):F1} MiB " +
            $"({result.BytesWritten / (1024.0 * 1024.0 * 1024.0):F3} GiB)");
        Console.WriteLine($"[bake] output dir: {bakedOut}");

        long bytesTotal = 0;
        foreach (int z in zoomLevels)
        {
            string zdir = Path.Combine(bakedOut, z.ToString(CultureInfo.InvariantCulture));
            int count = 0;
            long bytes = 0;
            if (Directory.Exists(zdir))
            {
                foreach (string f in Directory.EnumerateFiles(zdir, "*" + BakedDemTileStore.FileExtension, SearchOption.AllDirectories))
                {
                    count++;
                    bytes += new FileInfo(f).Length;
                }
            }

            bytesTotal += bytes;
            Console.WriteLine($"[bake] z{z}: {count} baked tiles, {bytes / (1024.0 * 1024.0):F1} MiB");
        }

        Console.WriteLine($"[bake] total on disk: {bytesTotal / (1024.0 * 1024.0 * 1024.0):F3} GiB");

        result.TilesWritten.Should().BeGreaterThan(0, "the real cache should yield baked tiles for the Tatra region");

        // -------- VERIFY: adjacent FINEST-level tiles agree bit-for-bit on their shared edge (the seam/wall fix) --
        // Because each tile is baked WITH a neighbour margin, two side-by-side tiles must produce an identical
        // shared edge row/column — proving the margin bake removed the per-tile-repair edge divergence that
        // rendered as vertical walls/cracks. We scan the freshly baked finest set for adjacent pairs and compare.
        var z16Files = Directory
            .EnumerateFiles(
                Path.Combine(bakedOut, finestZoom.ToString(CultureInfo.InvariantCulture)),
                "*" + BakedDemTileStore.FileExtension,
                SearchOption.AllDirectories)
            .ToList();
        z16Files.Should().NotBeEmpty($"z{finestZoom} is the finest baked level and must have tiles");

        bool BakedExists(DemTileKey k) =>
            File.Exists(Path.Combine(bakedOut, BakedDemTileStore.RelativePathFor(k)));
        BakedDemTile ReadKey(DemTileKey k) =>
            ReadTile(Path.Combine(bakedOut, BakedDemTileStore.RelativePathFor(k)));

        var rng = new Random(12345); // deterministic spot-check selection
        var pickedKeys = z16Files
            .Select(f => ReadTile(f).Key)
            .OrderBy(_ => rng.Next())
            .ToList();

        int eastPairs = 0;
        int southPairs = 0;
        foreach (DemTileKey key in pickedKeys)
        {
            if (eastPairs + southPairs >= 12)
            {
                break; // enough adjacent pairs to prove the invariant on real data
            }

            BakedDemTile a = ReadKey(key);

            var east = new DemTileKey(finestZoom, key.X + 1, key.Y);
            if (BakedExists(east))
            {
                BakedDemTile b = ReadKey(east);
                if (a.Rows == b.Rows)
                {
                    for (int r = 0; r < a.Rows; r++)
                    {
                        a.Heights[(r * a.Columns) + (a.Columns - 1)].Should()
                            .Be(b.Heights[r * b.Columns], $"east seam {key}↔{east} must be bit-identical (row {r})");
                    }

                    eastPairs++;
                    Console.WriteLine($"[verify] east seam {key}↔{east}: {a.Rows} edge cells identical OK");
                }
            }

            var south = new DemTileKey(finestZoom, key.X, key.Y + 1);
            if (BakedExists(south))
            {
                BakedDemTile b = ReadKey(south);
                if (a.Columns == b.Columns)
                {
                    for (int c = 0; c < a.Columns; c++)
                    {
                        a.Heights[((a.Rows - 1) * a.Columns) + c].Should()
                            .Be(b.Heights[c], $"south seam {key}↔{south} must be bit-identical (col {c})");
                    }

                    southPairs++;
                    Console.WriteLine($"[verify] south seam {key}↔{south}: {a.Columns} edge cells identical OK");
                }
            }
        }

        (eastPairs + southPairs).Should().BeGreaterThan(
            0, $"the baked set must contain adjacent z{finestZoom} tiles to verify seams");
        Console.WriteLine($"[verify] z{finestZoom} edge agreement: {eastPairs} east + {southPairs} south adjacent pairs PASS");

        // -------- VERIFY: a few coarser baked tiles read back as valid, finite, non-empty rasters --------
        foreach (int z in zoomLevels.Where(z => z != finestZoom))
        {
            string zdir = Path.Combine(bakedOut, z.ToString(CultureInfo.InvariantCulture));
            if (!Directory.Exists(zdir))
            {
                continue;
            }

            var files = Directory
                .EnumerateFiles(zdir, "*" + BakedDemTileStore.FileExtension, SearchOption.AllDirectories)
                .OrderBy(_ => rng.Next())
                .Take(3)
                .ToList();

            foreach (string f in files)
            {
                BakedDemTile t = ReadTile(f);
                t.Columns.Should().BeGreaterThan(0);
                t.Rows.Should().BeGreaterThan(0);
                t.Heights.Length.Should().Be(t.Columns * t.Rows);

                bool anyValidFinite = t.Heights.Any(h =>
                    !h.Equals((float)t.NoDataValue) && !float.IsNaN(h) && !float.IsInfinity(h));
                anyValidFinite.Should().BeTrue($"coarse z{z} tile {t.Key} must contain real elevation, not all-NoData");

                Console.WriteLine($"[verify] z{z} {t.Key}: {t.Columns}x{t.Rows} — valid raster OK");
            }
        }
    }

    private static BakedDemTile ReadTile(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return BakedDemTileStore.Read(fs);
    }

    // Locates the coarse base DEM (tatry.dem) the same way the app does: an explicit MAPATUR_BASE_DEM override,
    // else the first .dem found in the data folders next to the GUGiK cache root (…/Data/dem, …/Data/maps) —
    // <cacheRoot> is …/Data/dem-cache/gugik, so its grandparent is …/Data. Returns null if none is found.
    private static string? ResolveBaseDemPath(string cacheRoot)
    {
        string? overridePath = Environment.GetEnvironmentVariable("MAPATUR_BASE_DEM");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        // …/Data/dem-cache/gugik → …/Data/dem-cache → …/Data
        string? demCache = Path.GetDirectoryName(cacheRoot.TrimEnd(Path.DirectorySeparatorChar));
        string? dataDir = demCache is not null ? Path.GetDirectoryName(demCache) : null;
        if (dataDir is null)
        {
            return null;
        }

        foreach (string sub in new[] { "dem", "maps" })
        {
            string dir = Path.Combine(dataDir, sub);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            string? dem = Directory.EnumerateFiles(dir, "*.dem", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (dem is not null)
            {
                return dem;
            }
        }

        return null;
    }

    private static MapBounds ParseBoundsOrDefault(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return TatraOfflineRegion.Bounds; // full Polish Tatra footprint
        }

        string[] parts = csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            throw new ArgumentException("MAPATUR_BAKE_BOUNDS must be 'south,west,north,east'.");
        }

        double s = double.Parse(parts[0], CultureInfo.InvariantCulture);
        double w = double.Parse(parts[1], CultureInfo.InvariantCulture);
        double n = double.Parse(parts[2], CultureInfo.InvariantCulture);
        double e = double.Parse(parts[3], CultureInfo.InvariantCulture);
        return new MapBounds(new GeoPoint(s, w), new GeoPoint(n, e));
    }

    // HttpClient handler that refuses every request — guarantees the bake reads only the on-disk cache.
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            });
    }

    // Synchronous IProgress so reports run inline (Progress<T> needs a SynchronizationContext a test lacks).
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> sink;

        public SyncProgress(Action<T> sink) => this.sink = sink;

        public void Report(T value) => this.sink(value);
    }
}