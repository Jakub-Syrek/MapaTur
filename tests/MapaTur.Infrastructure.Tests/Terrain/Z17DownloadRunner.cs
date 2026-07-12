using System.Diagnostics;
using System.Globalization;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Terrain;
using MapaTur.Infrastructure.Terrain;

namespace MapaTur.Infrastructure.Tests.Terrain;

/// <summary>
/// Dev/maintenance RUNNER (NOT a unit test) — Faza A of <c>docs/PLAN-sub-1m-geometry.md</c>: downloads the
/// z17 (≈0.78 m/px) GUGiK WCS tiles for the WHOLE current z16 footprint (user decision 2026-07-10: „całe
/// Tatry, jak obecna mapa") into <c>dem-cache/gugik/17</c>. For every cached z16 tile it fetches the four
/// z17 children through the app's own <see cref="GugikNmtDemTileSource"/> — so caching, NoData sanitising
/// and the all-zero guard behave exactly like the app — and is naturally RESUMABLE: already-cached children
/// are skipped, so re-running after an abort or transient failures only fetches what is missing.
///
/// Children that come back null (out of PL coverage — the SK/border strip — or a transient WCS failure) are
/// listed in <c>z17-download-missing.txt</c>: the border part is the work list for the DMR5 z17 merge
/// (TILE-PRODUCTION §1.2/§2.3 at zoom 17), transients disappear on a re-run.
///
/// Gated behind <c>MAPATUR_DOWNLOAD_Z17=1</c> (network + hours). Run:
///   MAPATUR_DOWNLOAD_Z17=1 dotnet test tests/MapaTur.Infrastructure.Tests --filter FullyQualifiedName~Z17DownloadRunner
/// Optional env: MAPATUR_GUGIK_CACHE (cache root). Progress: <c>z17-download-progress.txt</c> next to the
/// cache (overwritten every batch), final summary in <c>z17-download-report.txt</c>.
/// </summary>
public sealed class Z17DownloadRunner
{
    private const string GateEnvVar = "MAPATUR_DOWNLOAD_Z17";

    private const string DefaultGugikCacheRoot =
        @"C:\Users\jaqbs\AppData\Local\User Name\com.companyname.mapatur.app\Data\dem-cache\gugik";

    // Concurrent WCS requests — polite to GUGiK, same order as the baker's fetch concurrency.
    private const int MaxConcurrentFetches = 6;

    [Fact]
    public async Task DownloadZ17_ForTheWholeZ16Footprint()
    {
        if (Environment.GetEnvironmentVariable(GateEnvVar) != "1")
        {
            return; // intentionally inert unless explicitly enabled
        }

        string cacheRoot = Environment.GetEnvironmentVariable("MAPATUR_GUGIK_CACHE") ?? DefaultGugikCacheRoot;
        string z16Dir = Path.Combine(cacheRoot, "16");
        Directory.Exists(z16Dir).Should().BeTrue(
            $"the z16 cache must exist at '{z16Dir}' (set MAPATUR_GUGIK_CACHE to override)");

        string statusDir = Path.GetDirectoryName(cacheRoot.TrimEnd(Path.DirectorySeparatorChar))!;
        string progressPath = Path.Combine(statusDir, "z17-download-progress.txt");
        string missingPath = Path.Combine(statusDir, "z17-download-missing.txt");
        string reportPath = Path.Combine(statusDir, "z17-download-report.txt");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var source = new GugikNmtDemTileSource(http, cacheRoot, tileSize: 256);

        // Every cached z16 tile (16/{x}/{y}.tif) contributes its four z17 children.
        var children = new List<DemTileKey>();
        foreach (string xDir in Directory.EnumerateDirectories(z16Dir))
        {
            if (!int.TryParse(Path.GetFileName(xDir), NumberStyles.None, CultureInfo.InvariantCulture, out int x))
            {
                continue;
            }

            foreach (string tif in Directory.EnumerateFiles(xDir, "*.tif"))
            {
                string stem = Path.GetFileNameWithoutExtension(tif);
                int underscore = stem.IndexOf('_');
                if (underscore >= 0)
                {
                    stem = stem[..underscore]; // legacy supersampled name {y}_{px}.tif
                }

                if (!int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out int y))
                {
                    continue;
                }

                for (int dy = 0; dy < 2; dy++)
                {
                    for (int dx = 0; dx < 2; dx++)
                    {
                        children.Add(new DemTileKey(17, (2 * x) + dx, (2 * y) + dy));
                    }
                }
            }
        }

        var clock = Stopwatch.StartNew();
        int done = 0, fetched = 0, cached = 0;
        var missing = new List<DemTileKey>();
        var missingLock = new object();
        var inv = CultureInfo.InvariantCulture;

        async Task WriteProgressAsync()
        {
            double rate = done / Math.Max(1.0, clock.Elapsed.TotalSeconds);
            double etaMin = rate > 0 ? (children.Count - done) / rate / 60.0 : double.NaN;
            string line = string.Create(
                inv,
                $"{DateTime.Now:HH:mm:ss} z17 download: {done}/{children.Count} (fetched={fetched}, cached={cached}, missing={missing.Count}) rate={rate:F1}/s ETA~{etaMin:F0} min");
            Console.WriteLine(line);
            try
            {
                await File.WriteAllTextAsync(progressPath, line);
            }
            catch (IOException)
            {
                // Progress file busy (user peeking) — the next batch rewrites it.
            }
        }

        await Parallel.ForEachAsync(
            children,
            new ParallelOptions { MaxDegreeOfParallelism = MaxConcurrentFetches },
            async (key, ct) =>
            {
                bool wasCached = source.IsCached(key);
                DemRaster? tile = await source.GetTileAsync(key, ct);
                if (tile is null)
                {
                    lock (missingLock)
                    {
                        missing.Add(key);
                    }
                }
                else if (wasCached)
                {
                    Interlocked.Increment(ref cached);
                }
                else
                {
                    Interlocked.Increment(ref fetched);
                }

                int n = Interlocked.Increment(ref done);
                if (n % 1000 == 0)
                {
                    await WriteProgressAsync();
                }
            });

        await WriteProgressAsync();
        missing.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        await File.WriteAllLinesAsync(
            missingPath, missing.Select(k => string.Create(inv, $"{k.Zoom}/{k.X}/{k.Y}")));

        string summary = string.Create(
            inv,
            $"z17 download finished {DateTime.Now:yyyy-MM-dd HH:mm} in {clock.Elapsed.TotalMinutes:F0} min: " +
            $"total={children.Count}, fetched={fetched}, already-cached={cached}, missing={missing.Count} " +
            $"(missing list: {missingPath}; border/SK strip needs the DMR5 z17 merge — TILE-PRODUCTION §1.2/§2.3, " +
            $"transients vanish on a re-run).");
        Console.WriteLine(summary);
        await File.WriteAllTextAsync(reportPath, summary);

        children.Should().NotBeEmpty("the z16 cache should yield z17 children to download");
    }
}