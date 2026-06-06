using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Pins the "Wczytaj teren Tatry" preset to its quality/size contract. The user asked for BOTH the
/// best quality AND a large area; on Android those are traded against the mesh vertex cap (above it the
/// renderer decimates, throwing the quality away). The preset is the optimum: the planner must pick the
/// sharpest zoom (z16, near GUGiK's native 1 m), and the native-resolution mosaic must stay under the
/// Android cap so the renderer keeps every sample (step = 1) — maximum area at full detail.
/// </summary>
public sealed class TatraDemRegionTests
{
    // GugikNmtDemTileSource output grid per tile (see the DI registration in MauiProgram).
    private const int TileSize = 256;

    // MapPageViewModel.MaxMeshVerticesForPlatform on ANDROID — above this the raster is decimated.
    private const int AndroidVertexCap = 5_000_000;

    [Fact]
    public void ChooseZoom_PicksTheSharpestZoom_ForNearNativeDetail()
    {
        int zoom = DemTilePlanner.ChooseZoomForBudget(
            TatraDemRegion.Bounds, TatraDemRegion.MaxTiles, TatraDemRegion.MinZoom, TatraDemRegion.MaxZoom);

        zoom.Should().Be(TatraDemRegion.MaxZoom, "the budget is sized so the sharpest zoom still fits");
    }

    [Fact]
    public void TileCount_StaysWithinBudget_AtTheChosenZoom()
    {
        long tiles = DemTilePlanner.TileCount(TatraDemRegion.Bounds, TatraDemRegion.MaxZoom);

        tiles.Should().BeLessThanOrEqualTo(TatraDemRegion.MaxTiles);
    }

    [Fact]
    public void NativeResolutionMosaic_StaysUnderTheAndroidVertexCap_SoTheRendererNeverDecimates()
    {
        long worstCaseVertices = (long)TatraDemRegion.MaxTiles * TileSize * TileSize;

        worstCaseVertices.Should().BeLessThanOrEqualTo(
            AndroidVertexCap, "76 x 256^2 = 4.98 M keeps the subsample step at 1 (full detail)");
    }
}