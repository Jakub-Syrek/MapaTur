using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Terrain3DProjectionTests
{
    private static DemRaster BuildRaster(int cols, int rows, float elevation)
    {
        var samples = new float[cols * rows];
        Array.Fill(samples, elevation);
        var bounds = new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0));
        return new DemRaster(cols, rows, bounds, samples);
    }

    private static Camera3D LookDownCamera(float distance = 50_000f)
    {
        return new Camera3D
        {
            Target = Vector3.Zero,
            Distance = distance,
            AzimuthRadians = 0f,
            PitchRadians = MathF.PI / 3f, // 60° above horizon
            NearPlane = 10f,
            FarPlane = 200_000f,
        };
    }

    [Fact]
    public void Project_ReturnsScreenVertexPerInputVertex()
    {
        var mesh = TerrainMesh3D.Build(BuildRaster(3, 3, 1000f));
        var camera = LookDownCamera();

        ProjectedTerrainFrame frame = Terrain3DProjection.Project(mesh, camera, 800f, 600f);

        frame.ScreenVertices.Length.Should().Be(mesh.Vertices.Length);
    }

    [Fact]
    public void Project_VisibleIndicesAreMultipleOfThree()
    {
        var mesh = TerrainMesh3D.Build(BuildRaster(4, 4, 1000f));
        var camera = LookDownCamera();

        ProjectedTerrainFrame frame = Terrain3DProjection.Project(mesh, camera, 800f, 600f);

        (frame.VisibleIndices.Length % 3).Should().Be(0);
    }

    [Fact]
    public void Project_CameraAboveTerrain_ReturnsNonEmptyVisibleIndices()
    {
        var mesh = TerrainMesh3D.Build(BuildRaster(4, 4, 1000f));
        var camera = LookDownCamera();

        ProjectedTerrainFrame frame = Terrain3DProjection.Project(mesh, camera, 800f, 600f);

        // (4-1) × (4-1) × 2 = 18 triangles, all front-facing from above.
        frame.VisibleIndices.Length.Should().Be(18 * 3);
    }

    [Fact]
    public void Project_VisibleIndicesAreSortedBackToFront()
    {
        var mesh = TerrainMesh3D.Build(BuildRaster(8, 8, 1000f));
        var camera = LookDownCamera();

        ProjectedTerrainFrame frame = Terrain3DProjection.Project(mesh, camera, 800f, 600f);

        // Walking the triangle list, centroid depth must monotonically decrease
        // (back-to-front = larger depth NDC first).
        float previousDepth = float.PositiveInfinity;
        for (int t = 0; t < frame.VisibleIndices.Length; t += 3)
        {
            float z0 = frame.ScreenVertices[frame.VisibleIndices[t]].Z;
            float z1 = frame.ScreenVertices[frame.VisibleIndices[t + 1]].Z;
            float z2 = frame.ScreenVertices[frame.VisibleIndices[t + 2]].Z;
            float centroid = (z0 + z1 + z2) / 3f;
            centroid.Should().BeLessThanOrEqualTo(previousDepth + 1e-3f,
                $"triangle {t / 3} centroid depth {centroid} must not exceed previous {previousDepth}");
            previousDepth = centroid;
        }
    }

    [Fact]
    public void Project_SortsBackToFront_EvenWhenDepthRangeIsCompressedByFarPlane()
    {
        // Production camera defaults: near 10, far 1_000_000. With such an extreme near/far
        // ratio the whole visible mesh lands in a tiny NDC-z window. Quantising that window
        // over [0,1] wastes ~90% of the depth buckets, so many triangles collapse into one
        // bucket and emit in index order rather than depth order — which tears on rotate.
        // Viewed from the -X side (azimuth = π), per-row index order runs near→far, i.e. the
        // exact OPPOSITE of the back-to-front order the painter's algorithm needs, so a
        // collapsed bucket sort produces visible mis-ordering. Quantising over the ACTUAL
        // depth range keeps the order correct regardless of the near/far ratio.
        var mesh = TerrainMesh3D.Build(BuildRaster(12, 12, 1000f));
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 60_000f,
            AzimuthRadians = MathF.PI, // view from the opposite side: index order != depth order
            PitchRadians = MathF.PI / 3f,
            NearPlane = 10f,
            FarPlane = 1_000_000f,
        };

        ProjectedTerrainFrame frame = Terrain3DProjection.Project(mesh, camera, 800f, 600f);

        float previousDepth = float.PositiveInfinity;
        for (int t = 0; t < frame.VisibleIndices.Length; t += 3)
        {
            float z0 = frame.ScreenVertices[frame.VisibleIndices[t]].Z;
            float z1 = frame.ScreenVertices[frame.VisibleIndices[t + 1]].Z;
            float z2 = frame.ScreenVertices[frame.VisibleIndices[t + 2]].Z;
            float centroid = (z0 + z1 + z2) / 3f;
            centroid.Should().BeLessThanOrEqualTo(previousDepth + 1e-6f,
                $"triangle {t / 3} centroid depth {centroid} must not exceed previous {previousDepth} (back-to-front)");
            previousDepth = centroid;
        }
    }

    [Fact]
    public void Project_BackFacingTrianglesAreCulled()
    {
        var mesh = TerrainMesh3D.Build(BuildRaster(2, 2, 1000f));
        // Camera *below* terrain looking up: all front-facing triangles (normals +Z) point away from camera, so they cull.
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 50_000f,
            AzimuthRadians = 0f,
            PitchRadians = -MathF.PI / 3f,  // looking up from below
            NearPlane = 10f,
            FarPlane = 200_000f,
        };

        ProjectedTerrainFrame frame = Terrain3DProjection.Project(mesh, camera, 800f, 600f);

        frame.VisibleIndices.Length.Should().Be(0, "flat terrain viewed from below culls all front-facing triangles");
    }

    [Fact]
    public void Project_TrianglesWithVerticesBehindCameraAreCulled()
    {
        var mesh = TerrainMesh3D.Build(BuildRaster(2, 2, 1000f));
        // Camera *inside* the mesh, with near plane > horizontal extent: clips everything.
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1f,
            AzimuthRadians = 0f,
            PitchRadians = 0f,
            NearPlane = 1_000_000f,
            FarPlane = 2_000_000f,
        };

        ProjectedTerrainFrame frame = Terrain3DProjection.Project(mesh, camera, 800f, 600f);

        frame.VisibleIndices.Length.Should().Be(0);
    }
}