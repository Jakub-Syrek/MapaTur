using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

public sealed class OrthoDetailCoveragePlannerTests
{
    private static readonly int[] EsriZoomCandidates = { 18, 17, 16, 15, 14, 13 };
    private static readonly GeoPoint Focus = new(49.18, 20.06);

    // The single-texture drape this feature replaces is ~16 m/px.
    private const double BaselineMetersPerPixel = 16.0;

    private static MapBounds Window(double radiusMeters) => LodTerrainWindow.Around(Focus, radiusMeters);

    private static OrthoDetailOptions Options(long maxResidentBytes = 512L * 1024 * 1024, int maxCellPixels = 2048) =>
        new(EsriZoomCandidates, MaxErrorPixels: 1.5, MaxCellPixels: maxCellPixels, MaxResidentBytes: maxResidentBytes);

    [Fact]
    public void Plan_CloseCamera_PicksFineZoomBeatingTheBaselineResolution()
    {
        var plan = OrthoDetailCoveragePlanner.Plan(
            Window(800), cameraToLookAtMeters: 150, fovY: Math.PI / 4, viewportHeight: 1080, Options());

        plan.Zoom.Should().BeGreaterThanOrEqualTo(16);
        ScreenSpaceLod.MetersPerPixel(plan.Zoom, Focus.Latitude).Should().BeLessThan(BaselineMetersPerPixel);
    }

    [Fact]
    public void Plan_FarCamera_PicksCoarserZoomThanCloseCamera()
    {
        var near = OrthoDetailCoveragePlanner.Plan(Window(800), 150, Math.PI / 4, 1080, Options());
        var far = OrthoDetailCoveragePlanner.Plan(Window(800), 6000, Math.PI / 4, 1080, Options());

        far.Zoom.Should().BeLessThan(near.Zoom);
    }

    [Fact]
    public void Plan_GridCoversTheWholeWindow()
    {
        MapBounds window = Window(800);
        var plan = OrthoDetailCoveragePlanner.Plan(window, 150, Math.PI / 4, 1080, Options());

        double mpp = ScreenSpaceLod.MetersPerPixel(plan.Zoom, Focus.Latitude);
        double widthMeters = (window.NorthEast.Longitude - window.SouthWest.Longitude)
            * 111_320.0 * Math.Cos(Focus.Latitude * Math.PI / 180.0);
        double heightMeters = (window.NorthEast.Latitude - window.SouthWest.Latitude) * 111_320.0;

        ((double)plan.GridCols * plan.CellPixels * mpp).Should().BeGreaterThanOrEqualTo(widthMeters);
        ((double)plan.GridRows * plan.CellPixels * mpp).Should().BeGreaterThanOrEqualTo(heightMeters);
    }

    [Fact]
    public void Plan_StaysWithinVramBudget()
    {
        var options = Options();
        var plan = OrthoDetailCoveragePlanner.Plan(Window(800), 150, Math.PI / 4, 1080, options);

        plan.ResidentBytes.Should().BeLessThanOrEqualTo(options.MaxResidentBytes);
    }

    [Fact]
    public void Plan_TightBudget_DropsZoomToFit()
    {
        var generous = OrthoDetailCoveragePlanner.Plan(Window(800), 150, Math.PI / 4, 1080, Options(512L * 1024 * 1024));
        // A tiny budget can't afford the fine zoom's grid → it must drop to a coarser zoom.
        var tight = OrthoDetailCoveragePlanner.Plan(Window(800), 150, Math.PI / 4, 1080, Options(8L * 1024 * 1024));

        tight.ResidentBytes.Should().BeLessThanOrEqualTo(8L * 1024 * 1024);
        tight.Zoom.Should().BeLessThan(generous.Zoom);
    }

    [Fact]
    public void Plan_NeverExceedsTheFinestCandidateZoom()
    {
        var plan = OrthoDetailCoveragePlanner.Plan(Window(400), 5, Math.PI / 4, 1080, Options());

        plan.Zoom.Should().BeLessThanOrEqualTo(EsriZoomCandidates[0]);
    }
}