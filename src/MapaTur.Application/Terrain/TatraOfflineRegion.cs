using MapaTur.Domain.Geography;

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
    /// <summary>Whole Polish Tatras (~30 × 14 km), south edge at the border.</summary>
    public static MapBounds Bounds { get; } =
        new(new GeoPoint(49.17, 19.73), new GeoPoint(49.30, 20.15));

    /// <summary>Zoom for the offline pull; z16 ≈ 1.5 m/px at 49° N, the tier closest to GUGiK's native 1 m.</summary>
    public const int DownloadZoom = 16;

    /// <summary>Rough on-disk size of one cached tile (256×256 float32 GeoTIFF) for a pre-download estimate.</summary>
    public const long ApproxBytesPerTile = 256L * 256 * 4;

    /// <summary>Rough total download size in bytes for <paramref name="tileCount"/> tiles (UI warning).</summary>
    public static long EstimatedBytes(int tileCount) => tileCount * ApproxBytesPerTile;
}