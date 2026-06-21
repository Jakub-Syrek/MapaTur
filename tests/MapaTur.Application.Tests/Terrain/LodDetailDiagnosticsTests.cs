using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="LodDetailDiagnostics.Format"/>, the one-line on-screen readout that lets
/// the live 1 m-detail decision be read straight off the phone (logcat/Serilog has been coming back empty on
/// device). The format is culture-invariant so it renders identically regardless of the device locale, and it
/// must make the "detail skipped because the chosen zoom fell at/below the base floor" case unmistakable —
/// that is the dominant cause of the mobile "plasticine" terrain.
/// </summary>
public sealed class LodDetailDiagnosticsTests
{
    [Fact]
    public void Format_FlagsDetailOff_WhenZoomAtOrBelowFloor()
    {
        string text = LodDetailDiagnostics.Format(
            source: "look-at", distanceMeters: 58_000, viewportHeight: 870,
            detailZoom: 12, baseDetailZoomFloor: 12, requestedTiles: null, cachedTiles: null);

        text.Should().Contain("z12").And.Contain("OFF").And.Contain("58.0 km").And.Contain("vh870");
    }

    [Fact]
    public void Format_OmitsCacheCounts_WhenDetailSkipped()
    {
        string text = LodDetailDiagnostics.Format(
            "look-at", 58_000, 870, 12, 12, requestedTiles: null, cachedTiles: null);

        text.Should().NotContain("/");
    }

    [Fact]
    public void Format_ShowsDetailOnWithCacheCounts_WhenZoomAboveFloor()
    {
        string text = LodDetailDiagnostics.Format(
            "look-at", 1_200, 870, detailZoom: 16, baseDetailZoomFloor: 12,
            requestedTiles: 64, cachedTiles: 40);

        text.Should().Contain("z16").And.Contain("40/64").And.Contain("1.2 km");
    }

    [Fact]
    public void Format_ShowsMeshCoarseness_WhenStepProvided()
    {
        // avgStep≈1 = true 1 m; ≥2 = budget-demoted/coarse. Culture-invariant decimal (s2.5, not s2,5).
        string text = LodDetailDiagnostics.Format(
            "look-at", 1_200, 870, detailZoom: 16, baseDetailZoomFloor: 12,
            requestedTiles: 64, cachedTiles: 40, avgStep: 2.5, finestStep: 1);

        text.Should().Contain("s2.5/1");
    }

    [Fact]
    public void Format_FlagsBaseWithReason_WhenDetailMeshDidNotRender()
    {
        // z16 selected & cached, but the per-tile mesh came back null (no step) → the bare base is on screen.
        string text = LodDetailDiagnostics.Format(
            "look-at", 1_300, 2109, detailZoom: 16, baseDetailZoomFloor: 12,
            requestedTiles: 144, cachedTiles: 143, avgStep: null, finestStep: null, note: "no-terrain");

        text.Should().Contain("BASE(no-terrain)").And.NotContain("s1");
    }

    [Fact]
    public void Format_ShowsMetres_WhenDistanceBelowOneKilometre()
    {
        string text = LodDetailDiagnostics.Format(
            "look-at", distanceMeters: 300, viewportHeight: 870,
            detailZoom: 16, baseDetailZoomFloor: 12, requestedTiles: 10, cachedTiles: 10);

        text.Should().Contain("300 m");
    }

    [Fact]
    public void Format_IsCultureInvariant_ForTheDecimalKilometre()
    {
        // Polish locale renders 1,2 — the readout must stay 1.2 so it is unambiguous on a Polish phone.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("pl-PL");

            string text = LodDetailDiagnostics.Format(
                "look-at", 1_200, 870, 16, 12, 64, 40);

            text.Should().Contain("1.2 km").And.NotContain("1,2");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}