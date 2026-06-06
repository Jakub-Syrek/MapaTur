using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Pins the shared-origin option on <see cref="TerrainMesh3D.BuildTiles"/> — the foundation for roam-the-
/// Tatras LOD. By default a mesh anchors its world frame on its OWN bounds centre, so reloading the detail
/// window onto a new patch would shift the origin and make the camera jump. Passing a fixed projection
/// anchor makes independently-built windows share one world frame: a given geographic point lands at the
/// same world position in every window, so the camera stays put as tiles stream in. The default path
/// (no anchor) must stay byte-for-byte unchanged.
/// </summary>
public sealed class TerrainMesh3DSharedOriginTests
{
    private static DemRaster Flat(double west, double south, double east, double north)
    {
        var bounds = new MapBounds(new GeoPoint(south, west), new GeoPoint(north, east));
        return new DemRaster(2, 2, bounds, new float[4]);
    }

    [Fact]
    public void BuildTiles_WithoutAnchor_KeepsTheMeshCentredOnItsOwnBounds()
    {
        var raster = Flat(20.00, 49.00, 20.10, 49.10);

        TerrainMesh3D mesh = TerrainMesh3D.BuildTiles(raster)[0];

        // The bounds centre projects to ~origin — unchanged legacy behaviour.
        mesh.GeoToWorld(new GeoPoint(49.05, 20.05), 0f).Length()
            .Should().BeLessThan(1f, "no anchor → world frame centred on the mesh's own bounds");
    }

    [Fact]
    public void BuildTiles_WithSharedAnchor_PlacesACommonPointAtTheSameWorldInDifferentWindows()
    {
        var anchor = new GeoPoint(49.20, 20.07);
        var shared = new GeoPoint(49.18, 20.05);
        var windowA = Flat(20.00, 49.15, 20.10, 49.25);
        var windowB = Flat(20.02, 49.10, 20.12, 49.20); // different bounds, both contain `shared`

        TerrainMesh3D meshA = TerrainMesh3D.BuildTiles(windowA, projectionAnchor: anchor)[0];
        TerrainMesh3D meshB = TerrainMesh3D.BuildTiles(windowB, projectionAnchor: anchor)[0];

        Vector3 a = meshA.GeoToWorld(shared, 0f);
        Vector3 b = meshB.GeoToWorld(shared, 0f);
        (a - b).Length().Should().BeLessThan(0.01f, "a shared anchor gives both windows one world frame");
    }

    [Fact]
    public void BuildTiles_WithSharedAnchor_OffsetsVerticesToMatchGeoToWorld()
    {
        var anchor = new GeoPoint(49.20, 20.07);
        var raster = Flat(20.00, 49.15, 20.10, 49.25);

        TerrainMesh3D mesh = TerrainMesh3D.BuildTiles(raster, projectionAnchor: anchor)[0];

        // vertices[0] is the NW corner (row 0 = north edge, col 0 = west edge) at elevation 0.
        Vector3 nwVertex = mesh.Vertices[0];
        Vector3 nwGeo = mesh.GeoToWorld(new GeoPoint(49.25, 20.00), 0f);
        (new Vector3(nwVertex.X, nwVertex.Y, 0f) - nwGeo).Length()
            .Should().BeLessThan(0.5f, "vertices are positioned in the same anchored frame GeoToWorld uses");
    }
}