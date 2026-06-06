using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="LodTerrainWindow"/>, the brain of roam-the-whole-Tatras LOD: a render
/// mesh can only hold ~3 km of 1 m terrain (the vertex cap), so as the camera flies we keep a window of
/// that size centred on what it looks at and reload — from the local cache, so it is instant — only once
/// the focus has drifted far enough toward the window edge that a reload is due. <see cref="LodTerrainWindow.Around"/>
/// gives the bounds to load; <see cref="LodTerrainWindow.ShouldReload"/> debounces so a tiny camera nudge
/// does not thrash a rebuild.
/// </summary>
public sealed class LodTerrainWindowTests
{
    // Morskie Oko-ish focus.
    private static readonly GeoPoint Focus = new(49.20, 20.07);

    [Fact]
    public void Around_CentresTheBoundsOnTheFocus()
    {
        MapBounds window = LodTerrainWindow.Around(Focus, halfWidthMeters: 1500);

        double centerLat = (window.SouthWest.Latitude + window.NorthEast.Latitude) / 2.0;
        double centerLon = (window.SouthWest.Longitude + window.NorthEast.Longitude) / 2.0;
        centerLat.Should().BeApproximately(Focus.Latitude, 1e-6);
        centerLon.Should().BeApproximately(Focus.Longitude, 1e-6);
    }

    [Fact]
    public void Around_SpansRoughlyTwiceTheHalfWidth()
    {
        const double halfWidthMeters = 1500;

        MapBounds window = LodTerrainWindow.Around(Focus, halfWidthMeters);

        double latSpanMeters = (window.NorthEast.Latitude - window.SouthWest.Latitude) * 111_320.0;
        latSpanMeters.Should().BeApproximately(2 * halfWidthMeters, 5.0);
    }

    [Fact]
    public void ShouldReload_IsFalse_WhenTheFocusIsStillNearTheLoadedCentre()
    {
        // ~110 m north of the loaded centre — well inside a 1000 m threshold.
        var focus = new GeoPoint(Focus.Latitude + 0.001, Focus.Longitude);

        LodTerrainWindow.ShouldReload(Focus, focus, thresholdMeters: 1000).Should().BeFalse();
    }

    [Fact]
    public void ShouldReload_IsTrue_WhenTheFocusHasDriftedBeyondTheThreshold()
    {
        // ~1.6 km north of the loaded centre — past a 1000 m threshold, so a reload is due.
        var focus = new GeoPoint(Focus.Latitude + 0.0145, Focus.Longitude);

        LodTerrainWindow.ShouldReload(Focus, focus, thresholdMeters: 1000).Should().BeTrue();
    }
}