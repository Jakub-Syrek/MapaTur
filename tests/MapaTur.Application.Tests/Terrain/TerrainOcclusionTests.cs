using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Marker occlusion: a peak / POI label must hide when a ridge stands between the camera and the
/// summit it names (the label would otherwise "punch through" the terrain). The test casts the
/// camera→marker ray at the DEM: a hit that lands well short of the marker means a ridge blocks it.
/// </summary>
public sealed class TerrainOcclusionTests
{
    private static readonly GeoPoint Anchor = new(49.0, 20.0);

    // A DEM with a tall wall down the middle column: low (200 m) everywhere except column 5 which is a
    // 3000 m ridge. East half and west half are separated by that wall.
    private static DemRaster RidgeWall()
    {
        const int n = 11;
        var s = new float[n * n];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                s[(r * n) + c] = c == 5 ? 3000f : 200f;
            }
        }

        return new DemRaster(n, n, new MapBounds(new GeoPoint(48.95, 19.95), new GeoPoint(49.05, 20.05)), s);
    }

    private static DemRaster Flat() => new(
        11, 11, new MapBounds(new GeoPoint(48.95, 19.95), new GeoPoint(49.05, 20.05)), CreateFlat(200f));

    private static float[] CreateFlat(float h)
    {
        var s = new float[121];
        Array.Fill(s, h);
        return s;
    }

    [Fact]
    public void IsVisible_ClearLineOfSightOverFlatGround_True()
    {
        DemRaster dem = Flat();
        // Camera high to the west, marker a low point to the east — open sky between them.
        Vector3 camera = new(-5000f, 0f, 4000f);
        Vector3 marker = new(3000f, 0f, 210f);

        TerrainOcclusion.IsVisible(camera, marker, dem, Anchor, verticalExaggeration: 1f).Should().BeTrue();
    }

    [Fact]
    public void IsVisible_MarkerBehindATallRidge_False()
    {
        DemRaster dem = RidgeWall();
        // Camera low in the WEST half, marker low in the EAST half — the 3000 m central wall blocks the view.
        Vector3 camera = new(-4000f, 0f, 250f);
        Vector3 marker = new(4000f, 0f, 260f);

        TerrainOcclusion.IsVisible(camera, marker, dem, Anchor, verticalExaggeration: 1f).Should().BeFalse();
    }

    [Fact]
    public void IsVisible_MarkerOnTheSummitTheRayHits_True()
    {
        DemRaster dem = RidgeWall();
        // Camera looks straight at the top of the central ridge — the ray hits AT the marker, not before it.
        var frame = default(GeoPoint); // unused; keep anchor
        _ = frame;
        Vector3 marker = new(0f, 0f, 3000f); // the ridge crest (column 5 ≈ centre)
        Vector3 camera = new(-4000f, 0f, 3200f);

        TerrainOcclusion.IsVisible(camera, marker, dem, Anchor, verticalExaggeration: 1f).Should().BeTrue();
    }

    [Fact]
    public void IsVisible_DegenerateCameraAtMarker_True()
    {
        DemRaster dem = Flat();
        Vector3 p = new(100f, 100f, 300f);

        TerrainOcclusion.IsVisible(p, p, dem, Anchor, verticalExaggeration: 1f).Should().BeTrue();
    }
}