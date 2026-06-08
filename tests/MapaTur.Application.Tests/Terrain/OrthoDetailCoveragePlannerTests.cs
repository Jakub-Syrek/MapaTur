using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

public sealed class OrthoDetailCoveragePlannerTests
{
    private static readonly int[] EsriZoomCandidates = { 18, 17, 16, 15, 14, 13 };
    private static readonly GeoPoint Focus = new(49.18, 20.06);

    private const double Radius = 800.0;
    private const double Fov = Math.PI / 4;
    private const double ViewportHeight = 1080;

    // The single-texture drape this feature replaces is ~16 m/px.
    private const double BaselineMetersPerPixel = 16.0;

    private static OrthoDetailOptions Options(
        long maxResidentBytes = 512L * 1024 * 1024,
        int maxCellPixels = 2048,
        double baseMetersPerPixel = BaselineMetersPerPixel) =>
        new(EsriZoomCandidates, MaxErrorPixels: 1.5, MaxCellPixels: maxCellPixels,
            MaxResidentBytes: maxResidentBytes, BaseMetersPerPixel: baseMetersPerPixel);

    private static OrthoDetailCoverage Plan(double cameraToLookAt, OrthoDetailOptions? options = null) =>
        OrthoDetailCoveragePlanner.Plan(Focus, Radius, cameraToLookAt, Fov, ViewportHeight, options ?? Options());

    [Fact]
    public void Plan_CloseCamera_StreamsFineZoomBeatingTheBaseline()
    {
        var plan = Plan(cameraToLookAt: 150);

        plan.Decision.Should().Be(OrthoDetailDecision.Stream);
        plan.Zoom.Should().BeGreaterThanOrEqualTo(16);
        plan.MetersPerPixel.Should().BeLessThan(BaselineMetersPerPixel);
    }

    [Fact]
    public void Plan_FarCamera_PicksCoarserZoomThanCloseCamera()
    {
        Plan(6000).Zoom.Should().BeLessThan(Plan(150).Zoom);
    }

    [Fact]
    public void Plan_OutputsNearFieldWindowCentredOnFocus()
    {
        var plan = Plan(150);

        plan.Window.Center.Latitude.Should().BeApproximately(Focus.Latitude, 1e-4);
        plan.Window.Center.Longitude.Should().BeApproximately(Focus.Longitude, 1e-4);
    }

    [Fact]
    public void Plan_GridCoversTheWholeWindow()
    {
        var plan = Plan(150);

        double widthMeters = (plan.Window.NorthEast.Longitude - plan.Window.SouthWest.Longitude)
            * 111_320.0 * Math.Cos(Focus.Latitude * Math.PI / 180.0);
        double heightMeters = (plan.Window.NorthEast.Latitude - plan.Window.SouthWest.Latitude) * 111_320.0;

        ((double)plan.GridCols * plan.CellPixels * plan.MetersPerPixel).Should().BeGreaterThanOrEqualTo(widthMeters);
        ((double)plan.GridRows * plan.CellPixels * plan.MetersPerPixel).Should().BeGreaterThanOrEqualTo(heightMeters);
    }

    [Fact]
    public void Plan_ReportsMetersPerPixelForTheChosenZoom()
    {
        var plan = Plan(150);

        plan.MetersPerPixel.Should().BeApproximately(
            ScreenSpaceLod.MetersPerPixel(plan.Zoom, Focus.Latitude), 1e-9);
    }

    [Fact]
    public void Plan_EstimatedVramWithinBudget()
    {
        var options = Options();
        var plan = Plan(150, options);

        plan.EstimatedVramBytes.Should().BeLessThanOrEqualTo(options.MaxResidentBytes);
    }

    [Fact]
    public void Plan_TightBudget_DropsZoomToFit()
    {
        var generous = Plan(150, Options(512L * 1024 * 1024));
        var tight = Plan(150, Options(8L * 1024 * 1024));

        tight.EstimatedVramBytes.Should().BeLessThanOrEqualTo(8L * 1024 * 1024);
        tight.Zoom.Should().BeLessThan(generous.Zoom);
    }

    [Fact]
    public void Plan_NeverExceedsTheFinestCandidateZoom()
    {
        Plan(5).Zoom.Should().BeLessThanOrEqualTo(EsriZoomCandidates[0]);
    }

    [Fact]
    public void Plan_DecidesSkipNoGain_WhenChosenZoomIsNoFinerThanBase()
    {
        // Pretend the base drape is already razor-sharp (0.1 m/px); no ESRI zoom can beat it.
        var plan = Plan(150, Options(baseMetersPerPixel: 0.1));

        plan.Decision.Should().Be(OrthoDetailDecision.SkipNoGain);
    }
}