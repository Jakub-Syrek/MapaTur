using MapaTur.Domain.Geography;

namespace MapaTur.Application.Maps;

/// <summary>
/// Lightweight, pre-load description of a basemap archive used to decide load order.
/// </summary>
/// <param name="Path">Absolute path to the MBTiles archive.</param>
/// <param name="MaxZoomLevel">Highest zoom level the archive contains (detail proxy).</param>
/// <param name="Bounds">Geographic coverage, if the archive declares it.</param>
public sealed record BasemapDescriptor(string Path, int MaxZoomLevel, MapBounds? Bounds);

/// <summary>
/// The order in which to load a set of basemaps plus which one the camera should
/// frame on launch.
/// </summary>
/// <param name="LoadOrder">
/// Archive paths ordered coarse → detailed. Because the renderer stacks basemaps in
/// load order (the last loaded paints on top where coverage overlaps), the most
/// detailed archive is last so it wins over coarser archives beneath it.
/// </param>
/// <param name="PrimaryPath">
/// The most detailed / most local archive — the sensible default zoom target — or
/// <see langword="null"/> when there are no basemaps.
/// </param>
public sealed record BasemapLoadPlan(IReadOnlyList<string> LoadOrder, string? PrimaryPath);

/// <summary>
/// Decides a deterministic basemap load order so a high-detail local archive (e.g. a
/// purchased Tatra raster) is drawn on top of, and framed in preference to, a coarse
/// broad-area archive (e.g. a generated Carpathian render) whose bounds happen to
/// contain it.
/// </summary>
/// <remarks>
/// Without this, basemaps loaded in <see cref="System.IO.Directory.EnumerateFiles(string)"/>
/// order let a coarse archive that merely sorts first hijack the launch viewport and/or
/// paint over a finer archive — the symptom being "the detailed map never shows".
/// </remarks>
public static class BasemapLoadPlanner
{
    /// <summary>
    /// Ranks the supplied basemaps by detail and returns the load order plus primary.
    /// </summary>
    /// <param name="basemaps">Pre-load descriptors for the discovered basemap archives.</param>
    /// <returns>A plan listing the load order (coarse → detailed) and the primary archive.</returns>
    public static BasemapLoadPlan Plan(IReadOnlyList<BasemapDescriptor> basemaps)
    {
        ArgumentNullException.ThrowIfNull(basemaps);

        // Ascending detail: least-detailed first, most-detailed last.
        //  1. Lower max zoom is coarser, so it loads (and draws) first.
        //  2. At equal zoom, a bounds-less archive can't be framed and is treated as the
        //     coarsest, sinking below any bounded peer.
        //  3. At equal zoom, a larger footprint is less local, so it loads first.
        //  4. Path breaks remaining ties for a stable, input-order-independent result.
        var ordered = basemaps
            .OrderBy(b => b.MaxZoomLevel)
            .ThenBy(b => b.Bounds.HasValue ? 1 : 0)
            .ThenByDescending(b => AreaSquareDegrees(b.Bounds))
            .ThenBy(b => b.Path, StringComparer.Ordinal)
            .ToList();

        var loadOrder = ordered.Select(b => b.Path).ToList();
        string? primaryPath = ordered.Count > 0 ? ordered[^1].Path : null;
        return new BasemapLoadPlan(loadOrder, primaryPath);
    }

    private static double AreaSquareDegrees(MapBounds? bounds)
    {
        if (bounds is not { } b)
        {
            return 0.0;
        }

        double width = b.NorthEast.Longitude - b.SouthWest.Longitude;
        double height = b.NorthEast.Latitude - b.SouthWest.Latitude;
        return width * height;
    }
}