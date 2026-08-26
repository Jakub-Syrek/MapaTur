using MapaTur.Domain.Geography;
using MapaTur.Domain.Regions;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Extent + zoom for the "download the whole Tatras offline" pass — the field needs no signal once the
/// tiles are on disk, so the user pulls the lot over WiFi first. Covers the Polish Tatra range (Western +
/// High Tatras, Tatra National Park), south edge at the Slovak border ridge (tiles past it have no GUGiK
/// coverage and just come back as skips). Downloaded at <see cref="DownloadZoom"/> (≈1.5 m/px, near native
/// 1 m) so the cached coverage is full-detail; an LOD layer later picks a render-sized window from it.
/// </summary>
public static class TatraOfflineRegion
{
    // P-A (rejestr regionów): fasada nad wpisem "tatry" — wartości pinuje MountainRegionsTests.
    /// <summary>Whole Polish Tatras (~30 × 14 km), south edge at the border.</summary>
    public static MapBounds Bounds => MountainRegions.Tatry.Offline.Bounds;

    /// <summary>Zoom for the offline pull; z16 ≈ 1.5 m/px at 49° N, the tier closest to GUGiK's native 1 m.</summary>
    public static int DownloadZoom => MountainRegions.Tatry.Offline.DownloadZoom;

    /// <summary>Rough on-disk size of one cached tile (256×256 float32 GeoTIFF) for a pre-download estimate.</summary>
    public static long ApproxBytesPerTile => MountainRegions.Tatry.Offline.ApproxBytesPerTile;

    /// <summary>Rough total download size in bytes for <paramref name="tileCount"/> tiles (UI warning).</summary>
    public static long EstimatedBytes(int tileCount) => MountainRegions.Tatry.Offline.EstimatedBytes(tileCount);
}