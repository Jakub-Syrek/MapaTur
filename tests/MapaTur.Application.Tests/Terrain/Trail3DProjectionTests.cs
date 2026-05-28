using System.Numerics;
using FluentAssertions;
using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Trail3DProjectionTests
{
    private static DemRaster BuildRaster(float elevation = 1000f)
    {
        var samples = new float[16 * 16];
        Array.Fill(samples, elevation);
        var bounds = new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0));
        return new DemRaster(16, 16, bounds, samples);
    }

    private static TerrainMesh3D BuildMesh() => TerrainMesh3D.Build(BuildRaster());

    private static Camera3D LookDownCamera()
    {
        return new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 80_000f,
            AzimuthRadians = 0f,
            PitchRadians = MathF.PI / 3f,
            NearPlane = 10f,
            FarPlane = 200_000f,
        };
    }

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

    [Fact]
    public void Project_NullTrails_Throws()
    {
        Action act = () => Trail3DProjection.Project(null!, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullRaster_Throws()
    {
        Action act = () => Trail3DProjection.Project(Array.Empty<Trail>(), null!, BuildMesh(), LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullMesh_Throws()
    {
        Action act = () => Trail3DProjection.Project(Array.Empty<Trail>(), BuildRaster(), null!, LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullCamera_Throws()
    {
        Action act = () => Trail3DProjection.Project(Array.Empty<Trail>(), BuildRaster(), BuildMesh(), null!, 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_EmptyTrails_ReturnsEmpty()
    {
        var result = Trail3DProjection.Project(Array.Empty<Trail>(), BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Project_ScreenPointsCountMatchesGeometryCount()
    {
        var trail = BuildTrailAtCenter();
        var result = Trail3DProjection.Project(new[] { trail }, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Single().ScreenPoints.Should().HaveCount(trail.Geometry.Count);
    }

    [Fact]
    public void Project_PreservesSourceTrail()
    {
        var trail = BuildTrailAtCenter();
        var result = Trail3DProjection.Project(new[] { trail }, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Single().Source.Should().BeSameAs(trail);
    }

    [Fact]
    public void Project_VerticesInsideFrustum_HaveScreenPointWithFiniteValues()
    {
        var trail = BuildTrailAtCenter();
        var result = Trail3DProjection.Project(new[] { trail }, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Single().ScreenPoints.Should().AllSatisfy(p =>
        {
            p.Should().NotBeNull();
            float.IsFinite(p!.Value.X).Should().BeTrue();
            float.IsFinite(p.Value.Y).Should().BeTrue();
        });
    }

    [Fact]
    public void Project_VertexBehindCamera_IsNull()
    {
        var trail = BuildTrailAtCenter();
        // Camera too close + huge near plane to make every vertex fall outside the view frustum.
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1f,
            AzimuthRadians = 0f,
            PitchRadians = 0f,
            NearPlane = 1_000_000f,
            FarPlane = 2_000_000f,
        };

        var result = Trail3DProjection.Project(new[] { trail }, BuildRaster(), BuildMesh(), camera, 800f, 600f);

        result.Single().ScreenPoints.Should().AllSatisfy(p => p.Should().BeNull());
    }

    [Fact]
    public void Project_TrailEntirelyOutsideRasterBounds_ExcludedFromResult()
    {
        var inBoundsTrail = BuildTrailAtCenter();
        var outOfBoundsTrail = new Trail(
            2L,
            "Far Away",
            new List<TrailMarking> { new(PttkColor.Blue, "blue:bar") },
            new List<GeoPoint>
            {
                new(60.0, 30.0),
                new(60.1, 30.1),
                new(60.2, 30.2),
            });

        var result = Trail3DProjection.Project(
            new[] { inBoundsTrail, outOfBoundsTrail },
            BuildRaster(),
            BuildMesh(),
            LookDownCamera(),
            800f,
            600f);

        result.Should().HaveCount(1);
        result.Single().Source.Should().BeSameAs(inBoundsTrail);
    }

    [Fact]
    public void Project_TrailPartiallyInsideRaster_IsProjected()
    {
        // One vertex inside the raster, two outside → bbox intersects, so trail must be projected.
        var trail = new Trail(
            3L,
            "Crossing",
            new List<TrailMarking> { new(PttkColor.Green, "green:bar") },
            new List<GeoPoint>
            {
                new(49.5, 19.5),    // inside (raster is 49..50, 19..20)
                new(48.5, 18.5),    // outside (SW)
                new(48.0, 18.0),    // outside (SW)
            });

        var result = Trail3DProjection.Project(
            new[] { trail },
            BuildRaster(),
            BuildMesh(),
            LookDownCamera(),
            800f,
            600f);

        result.Should().HaveCount(1);
        result.Single().ScreenPoints.Should().HaveCount(3);
    }

    [Fact]
    public void Project_TrailLiftRaisesScreenPosition()
    {
        var trail = BuildTrailAtCenter();
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var camera = LookDownCamera();

        var withSmallLift = Trail3DProjection.Project(new[] { trail }, raster, mesh, camera, 800f, 600f, trailLiftMeters: 0f);
        var withBigLift = Trail3DProjection.Project(new[] { trail }, raster, mesh, camera, 800f, 600f, trailLiftMeters: 500f);

        // Higher world Z → smaller screen Y (screen Y grows downward, world Z up tilts toward top).
        var lowY = withSmallLift.Single().ScreenPoints[1]!.Value.Y;
        var highY = withBigLift.Single().ScreenPoints[1]!.Value.Y;
        highY.Should().BeLessThan(lowY);
    }
}