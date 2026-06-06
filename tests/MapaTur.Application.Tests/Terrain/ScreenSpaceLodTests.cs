using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Krok 3 (screen-space-error LOD, Model 2): assign a detail zoom per tile around the look-at point
/// using the screen-space-error metric instead of fixed distance bands. The detail ring is centred on
/// the look-at (Krok 1), and each cell's zoom falls off with its screen-space error — so the highest
/// detail lands where the camera is aimed and on what occupies the screen, not nearest the camera.
/// </summary>
public sealed class ScreenSpaceLodTests
{
    private const double HalfPi = Math.PI / 2.0; // 90° vertical FOV ⇒ projection factor = viewportH / 2.
    private static readonly int[] Candidates = { 16, 14, 11 }; // finest → coarsest (TerrainLodBands zooms).

    [Fact]
    public void MetersPerPixel_AtZoomZeroEquator_IsWebMercatorGroundResolution()
    {
        double mpp = ScreenSpaceLod.MetersPerPixel(zoom: 0, latitudeDegrees: 0.0);

        mpp.Should().BeApproximately(156543.03392, 1e-2);
    }

    [Fact]
    public void MetersPerPixel_HalvesPerZoomLevel()
    {
        double z10 = ScreenSpaceLod.MetersPerPixel(10, 49.0);
        double z11 = ScreenSpaceLod.MetersPerPixel(11, 49.0);

        z11.Should().BeApproximately(z10 / 2.0, 1e-9);
    }

    [Fact]
    public void MetersPerPixel_ShrinksWithLatitude()
    {
        double equator = ScreenSpaceLod.MetersPerPixel(14, 0.0);
        double tatry = ScreenSpaceLod.MetersPerPixel(14, 49.0);

        tatry.Should().BeLessThan(equator); // cos(lat) factor: a pixel covers less ground away from the equator.
    }

    [Fact]
    public void ZoomForCameraDistance_VeryClose_PicksFinestZoom()
    {
        // d = 100 m: even zoom 16 (~1.57 m/px) projects to ~7.8 px (> 2), so the finest zoom is forced.
        int zoom = ScreenSpaceLod.ZoomForCameraDistance(Candidates, cameraDistanceMeters: 100.0, latitudeDegrees: 49.0, fovY: HalfPi, viewportHeight: 1000.0, maxErrorPixels: 2.0);

        zoom.Should().Be(16);
    }

    [Fact]
    public void ZoomForCameraDistance_VeryFar_PicksCoarsestZoom()
    {
        // d = 50 km: even zoom 11 (~50 m/px) projects to ~0.5 px (≤ 2), so the cheapest zoom suffices.
        int zoom = ScreenSpaceLod.ZoomForCameraDistance(Candidates, cameraDistanceMeters: 50000.0, latitudeDegrees: 49.0, fovY: HalfPi, viewportHeight: 1000.0, maxErrorPixels: 2.0);

        zoom.Should().Be(11);
    }

    [Fact]
    public void ZoomForCameraDistance_MidRange_PicksMidZoom()
    {
        // d = 2000 m: zoom 16 → 0.39 px, zoom 14 → 1.57 px (both ≤ 2), zoom 11 → 12.5 px (> 2) ⇒ zoom 14.
        int zoom = ScreenSpaceLod.ZoomForCameraDistance(Candidates, cameraDistanceMeters: 2000.0, latitudeDegrees: 49.0, fovY: HalfPi, viewportHeight: 1000.0, maxErrorPixels: 2.0);

        zoom.Should().Be(14);
    }

    [Fact]
    public void ZoomForCameraDistance_IsMonotonicNonIncreasingWithDistance()
    {
        double[] distances = { 100, 500, 2000, 5000, 20000, 80000 };

        int previous = int.MaxValue;
        foreach (double d in distances)
        {
            int zoom = ScreenSpaceLod.ZoomForCameraDistance(Candidates, d, 49.0, HalfPi, 1000.0, 2.0);
            zoom.Should().BeLessThanOrEqualTo(previous); // farther never demands a finer (higher) zoom.
            previous = zoom;
        }
    }

    [Fact]
    public void ZoomForCameraDistance_EmptyCandidates_Throws()
    {
        Action act = () => ScreenSpaceLod.ZoomForCameraDistance(Array.Empty<int>(), 1000.0, 49.0, HalfPi, 1000.0, 2.0);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AssignAroundLookAt_ReturnsOneAssignmentPerRingCell()
    {
        var lookAt = new GeoPoint(49.18, 20.08);
        var cameraPosition = new Vector3(0f, 0f, 1300f); // overhead, 300 m above the 1000 m look-at terrain.

        var plan = ScreenSpaceLod.AssignAroundLookAt(
            lookAt, lookAtElevationMeters: 1000f, cameraPosition, projectionAnchor: lookAt, verticalExaggeration: 1f,
            zoomCandidatesFinestFirst: Candidates, gridZoom: 14, radiusTiles: 2,
            fovY: HalfPi, viewportHeight: 1000.0, maxErrorPixels: 2.0);

        plan.Should().HaveCount(25); // 5×5 ring.
        plan.Select(t => t.Cell).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void AssignAroundLookAt_FinestAtCentre_CoarserTowardEdges()
    {
        var lookAt = new GeoPoint(49.18, 20.08);
        // Camera overhead and low so the look-at cell is much closer than the ring corners.
        var cameraPosition = new Vector3(0f, 0f, 1300f);

        var plan = ScreenSpaceLod.AssignAroundLookAt(
            lookAt, lookAtElevationMeters: 1000f, cameraPosition, projectionAnchor: lookAt, verticalExaggeration: 1f,
            zoomCandidatesFinestFirst: Candidates, gridZoom: 14, radiusTiles: 2,
            fovY: HalfPi, viewportHeight: 1000.0, maxErrorPixels: 2.0);

        var (cx, cy) = SlippyTileMath.LonLatToTile(lookAt.Longitude, lookAt.Latitude, 14);
        int centreZoom = plan.Single(t => t.Cell == new DemTileKey(14, cx, cy)).Zoom;

        int maxZoom = plan.Max(t => t.Zoom);
        int minZoom = plan.Min(t => t.Zoom);

        centreZoom.Should().Be(maxZoom);   // the look-at cell gets the finest detail...
        minZoom.Should().BeLessThan(maxZoom); // ...and the ring coarsens toward its edges (concentric falloff).
    }
}