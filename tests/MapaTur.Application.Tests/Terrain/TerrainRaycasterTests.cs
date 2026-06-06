using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Krok 1 (screen-space-error LOD): the pure ray/terrain intersection at the heart of
/// the look-at point. A ray is cast in the metric world frame (X east, Y north, Z up)
/// and intersected against a DEM heightfield. This is what lets the LOD centre on what
/// the camera is actually looking at — possibly kilometres away — instead of the camera.
/// </summary>
public sealed class TerrainRaycasterTests
{
    // Anchor at the SW corner keeps world (0,0) on the DEM's SW corner so test geometry
    // reads in clean metres east/north of the origin.
    private static readonly GeoPoint CornerAnchor = new(49.0, 20.0);

    private static DemRaster Flat(double heightMeters, GeoPoint sw, GeoPoint ne, int cols = 11, int rows = 11)
    {
        var samples = new float[cols * rows];
        Array.Fill(samples, (float)heightMeters);
        return new DemRaster(cols, rows, new MapBounds(sw, ne), samples);
    }

    /// <summary>A plane whose height rises linearly toward the east: height = slope × metresEastOfAnchor.</summary>
    private static DemRaster TiltedEast(double slope, GeoPoint sw, GeoPoint ne, GeoPoint anchor, int cols = 21, int rows = 21)
    {
        double metersPerLon = LocalTangentProjection.MetersPerLatDegree * Math.Cos(anchor.Latitude * Math.PI / 180.0);
        var samples = new float[cols * rows];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                double lon = sw.Longitude + ((ne.Longitude - sw.Longitude) * c / (cols - 1));
                double eastMeters = (lon - anchor.Longitude) * metersPerLon;
                samples[(r * cols) + c] = (float)(slope * eastMeters);
            }
        }

        return new DemRaster(cols, rows, new MapBounds(sw, ne), samples);
    }

    [Fact]
    public void Intersect_RayStraightDown_ReturnsPointUnderCamera()
    {
        // Flat terrain at 100 m around the world origin; anchor centred so (0,0) is interior.
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01));
        var ray = new Ray(new Vector3(0f, 0f, 1000f), new Vector3(0f, 0f, -1f));

        Vector3? hit = TerrainRaycaster.Intersect(ray, dem, anchor, verticalExaggeration: 1f, maxDistanceMeters: 2000f, stepMeters: 5f);

        hit.Should().NotBeNull();
        hit!.Value.X.Should().BeApproximately(0f, 0.5f);
        hit.Value.Y.Should().BeApproximately(0f, 0.5f);
        hit.Value.Z.Should().BeApproximately(100f, 0.5f);
    }

    [Fact]
    public void Intersect_RayAngledAtDistantSlope_ReturnsPointOnThatSlope()
    {
        // Plane z = 0.2·x (east). Ray from (0,0,400) heading east and down: z = 400 − 0.3·x.
        // Crossing: 400 − 0.3x = 0.2x ⇒ x = 800, z = 160. The hit must be far away, not under the camera.
        DemRaster dem = TiltedEast(0.2, new GeoPoint(49.0, 20.0), new GeoPoint(49.02, 20.02), CornerAnchor);
        var ray = new Ray(new Vector3(0f, 0f, 400f), Vector3.Normalize(new Vector3(1f, 0f, -0.3f)));

        Vector3? hit = TerrainRaycaster.Intersect(ray, dem, CornerAnchor, verticalExaggeration: 1f, maxDistanceMeters: 3000f, stepMeters: 5f);

        hit.Should().NotBeNull();
        hit!.Value.X.Should().BeApproximately(800f, 2f);
        hit.Value.Y.Should().BeApproximately(0f, 0.5f);
        hit.Value.Z.Should().BeApproximately(160f, 2f);
        // The hit lies on the plane z = 0.2·x — proving we found the distant slope, not a point below the camera.
        hit.Value.Z.Should().BeApproximately(0.2f * hit.Value.X, 1f);
    }

    [Fact]
    public void Intersect_RayPointingUp_ReturnsNull()
    {
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01));
        var ray = new Ray(new Vector3(0f, 0f, 1000f), new Vector3(0f, 0f, 1f));

        Vector3? hit = TerrainRaycaster.Intersect(ray, dem, anchor, verticalExaggeration: 1f, maxDistanceMeters: 2000f, stepMeters: 5f);

        hit.Should().BeNull();
    }

    [Fact]
    public void Intersect_RayNeverEntersRasterBounds_ReturnsNull()
    {
        // DEM lives far to the north; the straight-down ray at the anchor never samples real terrain.
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(50.0, 20.0), new GeoPoint(50.02, 20.02));
        var ray = new Ray(new Vector3(0f, 0f, 1000f), new Vector3(0f, 0f, -1f));

        Vector3? hit = TerrainRaycaster.Intersect(ray, dem, anchor, verticalExaggeration: 1f, maxDistanceMeters: 2000f, stepMeters: 5f);

        hit.Should().BeNull();
    }

    [Fact]
    public void Intersect_AllNoData_ReturnsNull()
    {
        var anchor = new GeoPoint(49.0, 20.0);
        var samples = new float[11 * 11];
        Array.Fill(samples, -9999.0f);
        var dem = new DemRaster(11, 11, new MapBounds(new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01)), samples);
        var ray = new Ray(new Vector3(0f, 0f, 1000f), new Vector3(0f, 0f, -1f));

        Vector3? hit = TerrainRaycaster.Intersect(ray, dem, anchor, verticalExaggeration: 1f, maxDistanceMeters: 2000f, stepMeters: 5f);

        hit.Should().BeNull();
    }

    [Fact]
    public void Intersect_VerticalExaggerationScalesHitElevation()
    {
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01));
        var ray = new Ray(new Vector3(0f, 0f, 1000f), new Vector3(0f, 0f, -1f));

        Vector3? hit = TerrainRaycaster.Intersect(ray, dem, anchor, verticalExaggeration: 2f, maxDistanceMeters: 2000f, stepMeters: 5f);

        hit.Should().NotBeNull();
        hit!.Value.Z.Should().BeApproximately(200f, 0.5f);
    }

    [Fact]
    public void Intersect_CameraBelowSurface_ReturnsNull()
    {
        // Degenerate: the ray origin is already under the terrain. Looking down finds no forward crossing.
        var anchor = new GeoPoint(49.0, 20.0);
        DemRaster dem = Flat(100.0, new GeoPoint(48.99, 19.99), new GeoPoint(49.01, 20.01));
        var ray = new Ray(new Vector3(0f, 0f, 50f), new Vector3(0f, 0f, -1f));

        Vector3? hit = TerrainRaycaster.Intersect(ray, dem, anchor, verticalExaggeration: 1f, maxDistanceMeters: 2000f, stepMeters: 5f);

        hit.Should().BeNull();
    }
}