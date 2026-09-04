using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Regions;

/// <summary>
/// The built-in region registry (P-A, PLAN-ALPY §3). Entry #1 is Tatry, carrying BIT-FOR-BIT the values
/// the app hard-coded before the registry existed — pinned by <c>MountainRegionsTests</c>; the old static
/// classes are facades over this entry, so nothing on a user's disk or screen changes. Future regions
/// (Zermatt…) become new entries here (later: loaded from data files), never code forks.
/// </summary>
public static class MountainRegions
{
    /// <summary>Polish Tatras — the original region and the registry's zero-regression reference.</summary>
    public static MountainRegion Tatry { get; } = new(
        Id: "tatry",
        DemLoad: new RegionDemLoad(
            new MapBounds(new GeoPoint(49.183, 20.050), new GeoPoint(49.207, 20.093)),
            MaxTiles: 76,
            MinZoom: 11,
            MaxZoom: 16),
        Offline: new RegionOfflineDownload(
            new MapBounds(new GeoPoint(49.17, 19.73), new GeoPoint(49.30, 20.15)),
            DownloadZoom: 16,
            ApproxBytesPerTile: 256L * 256 * 4),
        TrailFilterBounds: new MapBounds(new GeoPoint(49.05, 19.55), new GeoPoint(49.40, 20.30)),
        TrailSyncBounds: new MapBounds(new GeoPoint(49.10, 19.50), new GeoPoint(49.40, 20.40)),
        PoiCoreBounds: new MapBounds(new GeoPoint(49.08, 19.78), new GeoPoint(49.32, 20.35)),
        MapStart: new RegionMapStart(Latitude: 49.2326, Longitude: 19.9819, Resolution: 152.0),
        DetailLattice: new RegionDetailLattice(Lon0: 19.50, Lat0: 49.40, RefLat: 49.25, PathSegment: "tatry"),
        DemCacheSubdir: "gugik",
        DisplayName: "Tatry");

    /// <summary>
    /// Zermatt/Matterhorn pilot (P-B, TILE-PRODUCTION-ALPY §A0): the window 7.58–7.88 E × 45.92–46.08 N
    /// (~413 km²) fetched 2026-08-27 from swisstopo (DEM 0.5 m all-2024, ortho all-2023, vertical datum
    /// measured orthometric — §A2). All five purpose-bboxes start IDENTICAL to the window (the Tatra
    /// entry's five differing boxes grew historically; a new region starts from one). Data namespaces
    /// are its own: <c>dem-cache/swisstopo</c>, detail pyramid segment <c>zermatt</c>, lattice anchored
    /// at the window's NW corner with RefLat at its mid-latitude — never the Tatra lattice.
    /// </summary>
    public static MountainRegion Zermatt { get; } = CreateZermatt();

    /// <summary>The region the app boots into. Until product region switching lands (P-A3), the harness
    /// env <c>MAPATUR_REGION</c> selects a non-default region for pilot runs; unknown/absent = Tatry.</summary>
    public static MountainRegion Default { get; } =
        ResolveDefault(Environment.GetEnvironmentVariable("MAPATUR_REGION")) ?? Tatry;

    /// <summary>All registered regions, default-boot region first.</summary>
    public static IReadOnlyList<MountainRegion> All { get; } = [Tatry, Zermatt];

    /// <summary>Resolves the boot-region override value (case-insensitive id); null when unknown/absent.</summary>
    public static MountainRegion? ResolveDefault(string? regionId)
    {
        if (string.IsNullOrWhiteSpace(regionId))
        {
            return null;
        }

        string id = regionId.Trim().ToLowerInvariant();
        return id == Tatry.Id ? Tatry : id == Zermatt.Id ? Zermatt : null;
    }

    private static MountainRegion CreateZermatt()
    {
        var window = new MapBounds(new GeoPoint(45.92, 7.58), new GeoPoint(46.08, 7.88));
        return new MountainRegion(
            Id: "zermatt",
            DemLoad: new RegionDemLoad(window, MaxTiles: 76, MinZoom: 11, MaxZoom: 16),
            Offline: new RegionOfflineDownload(window, DownloadZoom: 16, ApproxBytesPerTile: 256L * 256 * 4),
            TrailFilterBounds: window,
            TrailSyncBounds: window,
            PoiCoreBounds: window,
            MapStart: new RegionMapStart(Latitude: 46.0207, Longitude: 7.7491, Resolution: 152.0),
            DetailLattice: new RegionDetailLattice(Lon0: 7.58, Lat0: 46.08, RefLat: 46.0, PathSegment: "zermatt"),
            DemCacheSubdir: "swisstopo",
            DisplayName: "Alpy — Zermatt / Matterhorn");
    }

    /// <summary>The region with the given <paramref name="id"/>, or null when unknown.</summary>
    public static MountainRegion? ById(string id)
    {
        foreach (MountainRegion region in All)
        {
            if (region.Id == id)
            {
                return region;
            }
        }

        return null;
    }
}