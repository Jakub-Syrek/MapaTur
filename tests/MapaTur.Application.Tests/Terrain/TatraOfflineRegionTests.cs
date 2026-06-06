using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Pins the "download whole Tatras offline" preset to a sane envelope: it must cover the Polish Tatras
/// (inside GUGiK's coverage), pull at the sharpest near-native zoom, and amount to a one-time WiFi
/// download that is substantial (the whole range) but not absurd (a few hundred MB, not tens of GB).
/// </summary>
public sealed class TatraOfflineRegionTests
{
    [Fact]
    public void DownloadZoom_IsTheNearNativeTier()
    {
        TatraOfflineRegion.DownloadZoom.Should().Be(16);
    }

    [Fact]
    public void Bounds_LieInsidePoland_SoGugikCoversThem()
    {
        // GUGiK gate: lon 14.0–24.2°E, lat 48.9–55.0°N.
        TatraOfflineRegion.Bounds.SouthWest.Longitude.Should().BeInRange(14.0, 24.2);
        TatraOfflineRegion.Bounds.NorthEast.Longitude.Should().BeInRange(14.0, 24.2);
        TatraOfflineRegion.Bounds.SouthWest.Latitude.Should().BeInRange(48.9, 55.0);
        TatraOfflineRegion.Bounds.NorthEast.Latitude.Should().BeInRange(48.9, 55.0);
    }

    [Fact]
    public void TileCount_CoversTheWholeRange_WithoutBeingAbsurd()
    {
        long tiles = DemTilePlanner.TileCount(TatraOfflineRegion.Bounds, TatraOfflineRegion.DownloadZoom);

        tiles.Should().BeGreaterThan(1000, "it is the whole range, not a single peak");
        tiles.Should().BeLessThan(5000, "a one-time WiFi pull, not tens of GB");
    }

    [Fact]
    public void EstimatedBytes_ForTheRange_IsAFewHundredMegabytes()
    {
        long tiles = DemTilePlanner.TileCount(TatraOfflineRegion.Bounds, TatraOfflineRegion.DownloadZoom);

        long bytes = TatraOfflineRegion.EstimatedBytes((int)tiles);

        bytes.Should().BeGreaterThan(150L * 1024 * 1024, "covering the whole range is not tiny");
        bytes.Should().BeLessThan(2L * 1024 * 1024 * 1024, "still a sane one-time WiFi download");
    }
}