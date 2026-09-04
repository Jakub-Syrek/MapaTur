using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Regions;

/// <summary>
/// Extent + tile budget for a region's "load terrain" pass (was <c>TatraDemRegion</c>): the bounds the
/// user-facing load covers and the zoom/budget envelope the planner picks the sharpest fitting zoom from.
/// </summary>
/// <param name="Bounds">Area of the on-demand DEM load.</param>
/// <param name="MaxTiles">Tile budget; sized so tiles×256² stays under the 5 M Android vertex cap.</param>
/// <param name="MinZoom">Lowest fallback zoom if even it overflows the budget.</param>
/// <param name="MaxZoom">Highest zoom the planner may pick (tier closest to the source's native GSD).</param>
public sealed record RegionDemLoad(MapBounds Bounds, int MaxTiles, int MinZoom, int MaxZoom);

/// <summary>
/// Extent + zoom for the region's "download the whole range offline" pass (was <c>TatraOfflineRegion</c>).
/// </summary>
/// <param name="Bounds">Whole-range download area.</param>
/// <param name="DownloadZoom">Zoom of the offline pull (near the DEM source's native resolution).</param>
/// <param name="ApproxBytesPerTile">Rough on-disk size of one cached tile for the pre-download estimate.</param>
public sealed record RegionOfflineDownload(MapBounds Bounds, int DownloadZoom, long ApproxBytesPerTile)
{
    /// <summary>Rough total download size in bytes for <paramref name="tileCount"/> tiles (UI warning).</summary>
    public long EstimatedBytes(int tileCount) => tileCount * ApproxBytesPerTile;
}

/// <summary>
/// Anchor of the plate-carrée lattice of the hi-res ortho DETAIL pyramid plus the region's path segment
/// under <c>dem/ortho-detail/</c>. The anchor MUST match the region's fetcher
/// (<c>testdata/maps/fetch-ortho-detail.py</c> for Tatry) — cells must align 1:1 with tiles on disk.
/// </summary>
/// <param name="Lon0">Lattice NW anchor longitude (west edge of the largest planned window).</param>
/// <param name="Lat0">Lattice NW anchor latitude (north edge).</param>
/// <param name="RefLat">Reference latitude fixing the longitude pitch (tiles square in ground metres).</param>
/// <param name="PathSegment">Directory name of the region's detail pyramid (e.g. <c>tatry</c>).</param>
public sealed record RegionDetailLattice(double Lon0, double Lat0, double RefLat, string PathSegment);

/// <summary>Default 2D map viewport the app opens on for this region.</summary>
/// <param name="Latitude">Viewport centre latitude (WGS84).</param>
/// <param name="Longitude">Viewport centre longitude (WGS84).</param>
/// <param name="Resolution">Mapsui resolution (metres per pixel in Spherical Mercator).</param>
public sealed record RegionMapStart(double Latitude, double Longitude, double Resolution);

/// <summary>
/// One mountain region as DATA (PLAN-ALPY §3): everything the engine previously hard-coded about the
/// Tatras, gathered into a single record so a future region (Zermatt, Mont Blanc…) is a new entry, not a
/// code fork. Deliberately carries SEPARATE bounds per purpose — the pre-registry code had five different
/// "Tatra bboxes" (DEM load, offline pull, trail filter, trail auto-sync, POI core) with different extents
/// ON PURPOSE, and merging them would change behaviour.
/// </summary>
/// <param name="Id">Stable identifier; also the legacy-alias key (paths/package ids keep their old names).</param>
/// <param name="DemLoad">"Load terrain" extent + budget.</param>
/// <param name="Offline">"Download whole range offline" extent + zoom.</param>
/// <param name="TrailFilterBounds">Trail-visibility toggle bbox (was <c>KarpatRegions.Tatry</c>).</param>
/// <param name="TrailSyncBounds">Trail auto-download bbox (was <c>TrailAutoSyncPolicy.TatraBounds</c>, "Region C").</param>
/// <param name="PoiCoreBounds">POI/roads fetch bbox for the 3D view (was <c>MapPageViewModel.TatraCoreRegion</c>).</param>
/// <param name="MapStart">Default 2D viewport.</param>
/// <param name="DetailLattice">Hi-res ortho detail lattice anchor + on-disk path segment.</param>
/// <param name="DemCacheSubdir">Subdirectory of <c>dem-cache/</c> holding the region's raw DEM source tiles
/// (legacy alias: <c>gugik</c> for Tatry — never migrate user data on disk).</param>
/// <param name="DisplayName">Human name shown by the desktop region chooser (P-A3); data, not a UI map.</param>
public sealed record MountainRegion(
    string Id,
    RegionDemLoad DemLoad,
    RegionOfflineDownload Offline,
    MapBounds TrailFilterBounds,
    MapBounds TrailSyncBounds,
    MapBounds PoiCoreBounds,
    RegionMapStart MapStart,
    RegionDetailLattice DetailLattice,
    string DemCacheSubdir,
    string DisplayName = "")
{
    /// <summary>Registry id of the pre-registry region whose user state lives under the UNSCOPED keys.</summary>
    private const string PreRegistryId = "tatry";

    /// <summary>
    /// Preferences key for per-region user state (saved camera, route stops). Tatry — entry #1, the
    /// region the app shipped with before the registry — keeps the bare <paramref name="baseKey"/> so
    /// existing user data is read bit-for-bit with zero migration; every other region gets
    /// <c>baseKey.Id</c>, so switching regions (run-tatry.cmd / run-zermatt.cmd) never overwrites the
    /// other region's last position or planned route.
    /// </summary>
    public string PreferenceKey(string baseKey) =>
        Id == PreRegistryId ? baseKey : $"{baseKey}.{Id}";
}