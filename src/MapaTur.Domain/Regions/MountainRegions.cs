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
        DemCacheSubdir: "gugik");

    /// <summary>The region the app boots into until region switching exists.</summary>
    public static MountainRegion Default => Tatry;

    /// <summary>All registered regions, default first.</summary>
    public static IReadOnlyList<MountainRegion> All { get; } = [Tatry];

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