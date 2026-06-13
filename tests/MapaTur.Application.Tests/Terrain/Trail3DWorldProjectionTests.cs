using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Trail3DWorldProjectionTests
{
    private static DemRaster BuildRaster(float elevation = 1000f)
    {
        var samples = new float[16 * 16];
        Array.Fill(samples, elevation);
        var bounds = new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0));
        return new DemRaster(16, 16, bounds, samples);
    }

    private static TerrainMesh3D BuildMesh() => TerrainMesh3D.Build(BuildRaster());

    private static Camera3D LookDownCamera() => new()
    {
        Target = Vector3.Zero,
        Distance = 80_000f,
        AzimuthRadians = 0f,
        PitchRadians = MathF.PI / 3f,
        NearPlane = 10f,
        FarPlane = 200_000f,
    };

    private static Trail BuildTrailAtCenter()
    {
        var geometry = new List<GeoPoint>
        {
            new(49.45, 19.45),
            new(49.5, 19.5),
            new(49.55, 19.55),
        };
        var markings = new List<TrailMarking> { new(PttkColor.Red, "red:bar") };
        return new Trail(1L, "Test Trail", markings, geometry);
    }

    private static Trail BuildTrailFarOutside() => new(
        2L,
        "Far Away",
        new List<TrailMarking> { new(PttkColor.Blue, "blue:bar") },
        new List<GeoPoint> { new(60.0, 30.0), new(60.1, 30.1), new(60.2, 30.2) });

    [Fact]
    public void ToWorld_NullTrails_Throws()
    {
        Action act = () => Trail3DWorldProjection.ToWorld(null!, BuildRaster(), BuildMesh());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToWorld_NullRaster_Throws()
    {
        Action act = () => Trail3DWorldProjection.ToWorld(Array.Empty<Trail>(), null!, BuildMesh());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToWorld_NullMesh_Throws()
    {
        Action act = () => Trail3DWorldProjection.ToWorld(Array.Empty<Trail>(), BuildRaster(), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToWorld_EmptyTrails_ReturnsEmpty()
    {
        var result = Trail3DWorldProjection.ToWorld(Array.Empty<Trail>(), BuildRaster(), BuildMesh());

        result.Should().BeEmpty();
    }

    [Fact]
    public void ToWorld_TrailOutsideRaster_Excluded()
    {
        var inside = BuildTrailAtCenter();
        var outside = BuildTrailFarOutside();

        var result = Trail3DWorldProjection.ToWorld(new[] { inside, outside }, BuildRaster(), BuildMesh());

        result.Should().ContainSingle().Which.Source.Should().BeSameAs(inside);
    }

    [Fact]
    public void ToWorld_PreservesSourceTrail()
    {
        var trail = BuildTrailAtCenter();

        var result = Trail3DWorldProjection.ToWorld(new[] { trail }, BuildRaster(), BuildMesh());

        result.Single().Source.Should().BeSameAs(trail);
    }

    [Fact]
    public void ToWorld_DensifiesSparseGeometry_KeepingEndpoints()
    {
        // The trail's nodes are kilometres apart, so densification (12 m spacing) adds many vertices, while
        // the first and last original nodes are preserved exactly.
        var trail = BuildTrailAtCenter();

        var world = Trail3DWorldProjection.ToWorld(new[] { trail }, BuildRaster(), BuildMesh()).Single().World;

        world.Count.Should().BeGreaterThan(trail.Geometry.Count);
    }

    [Fact]
    public void ToWorld_FirstVertexMatchesGeoToWorldAtLiftedElevation()
    {
        var trail = BuildTrailAtCenter();
        var mesh = BuildMesh();
        const float lift = 5f;
        // Raster is a flat 1000 m plateau, so the sampled ground elevation is exactly 1000 m. The lift is
        // applied as TRUE metres (divided by the exaggeration). The first vertex is an original node, kept exactly.
        var expected = mesh.GeoToWorld(trail.Geometry[0], 1000f + (lift / mesh.VerticalExaggeration));

        var result = Trail3DWorldProjection.ToWorld(new[] { trail }, BuildRaster(), mesh, lift);

        result.Single().World[0].Should().Be(expected);
    }

    [Fact]
    public void ToScreen_NullWorldLines_Throws()
    {
        Action act = () => Trail3DWorldProjection.ToScreen(null!, LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToScreen_NullCamera_Throws()
    {
        Action act = () => Trail3DWorldProjection.ToScreen(Array.Empty<TrailWorldLine>(), null!, 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToScreen_PreservesSourceTrail()
    {
        var trail = BuildTrailAtCenter();
        var world = Trail3DWorldProjection.ToWorld(new[] { trail }, BuildRaster(), BuildMesh());

        var result = Trail3DWorldProjection.ToScreen(world, LookDownCamera(), 800f, 600f);

        result.Single().Source.Should().BeSameAs(trail);
    }

    [Fact]
    public void ToScreen_ProducesSameScreenPointsAsLegacyProject()
    {
        var trail = BuildTrailAtCenter();
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var camera = LookDownCamera();

        var legacy = Trail3DProjection.Project(new[] { trail }, raster, mesh, camera, 800f, 600f);
        var world = Trail3DWorldProjection.ToWorld(new[] { trail }, raster, mesh);
        var split = Trail3DWorldProjection.ToScreen(world, camera, 800f, 600f);

        split.Single().ScreenPoints.Should().Equal(legacy.Single().ScreenPoints);
    }

    private static DemRaster BuildDetailRaster(float elevation) =>
        new(8, 8, new MapBounds(new GeoPoint(49.4, 19.4), new GeoPoint(49.6, 19.6)),
            CreateFilled(8 * 8, elevation));

    private static float[] CreateFilled(int count, float value)
    {
        var samples = new float[count];
        Array.Fill(samples, value);
        return samples;
    }

    [Fact]
    public void ToWorld_VertexInsideDetailWindow_SeatsOnDetailElevationNotBase()
    {
        // Base plateau = 1000 m; the 1 m detail carves the same area to 900 m. A trail vertex inside the
        // detail window must seat on the detail (900 m) so it doesn't float over the deeper-carved surface;
        // a vertex outside the window keeps the base (1000 m).
        var mesh = BuildMesh(); // 1000 m base
        var detail = new DetailElevationField(BuildDetailRaster(900f));
        var inside = new GeoPoint(49.5, 19.5);   // within the detail window
        var outside = new GeoPoint(49.2, 19.2);  // inside base, outside detail
        var trail = new Trail(
            7L, "Detail probe",
            new List<TrailMarking> { new(PttkColor.Red, "red:bar") },
            new List<GeoPoint> { inside, outside });

        var result = Trail3DWorldProjection.ToWorld(new[] { trail }, BuildRaster(), mesh, trailLiftMeters: 0f, detail: detail);

        // Endpoints are preserved by densification: first node inside the detail window (900 m), last node
        // outside it on the base (1000 m).
        result.Single().World[0].Should().Be(mesh.GeoToWorld(inside, 900f));
        result.Single().World[^1].Should().Be(mesh.GeoToWorld(outside, 1000f));
    }

    [Fact]
    public void ToWorld_NullDetail_FallsBackToBaseEverywhere()
    {
        var mesh = BuildMesh();
        var trail = BuildTrailAtCenter();

        var withNull = Trail3DWorldProjection.ToWorld(new[] { trail }, BuildRaster(), mesh, trailLiftMeters: 0f, detail: null);
        var legacy = Trail3DWorldProjection.ToWorld(new[] { trail }, BuildRaster(), mesh, trailLiftMeters: 0f);

        withNull.Single().World.Should().Equal(legacy.Single().World);
    }
}