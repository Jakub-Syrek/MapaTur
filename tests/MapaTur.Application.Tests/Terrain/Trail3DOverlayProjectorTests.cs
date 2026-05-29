using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Trail3DOverlayProjectorTests
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

    private static Camera3D OrbitedCamera() => new()
    {
        Target = Vector3.Zero,
        Distance = 80_000f,
        AzimuthRadians = MathF.PI / 2f,
        PitchRadians = MathF.PI / 3f,
        NearPlane = 10f,
        FarPlane = 200_000f,
    };

    private static Trail BuildTrailAtCenter() => new(
        1L,
        "Test Trail",
        new List<TrailMarking> { new(PttkColor.Red, "red:bar") },
        new List<GeoPoint> { new(49.45, 19.45), new(49.5, 19.5), new(49.55, 19.55) });

    [Fact]
    public void Project_NullTrails_Throws()
    {
        var projector = new Trail3DOverlayProjector();

        Action act = () => projector.Project(null!, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullRaster_Throws()
    {
        var projector = new Trail3DOverlayProjector();

        Action act = () => projector.Project(Array.Empty<Trail>(), null!, BuildMesh(), LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullMesh_Throws()
    {
        var projector = new Trail3DOverlayProjector();

        Action act = () => projector.Project(Array.Empty<Trail>(), BuildRaster(), null!, LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullCamera_Throws()
    {
        var projector = new Trail3DOverlayProjector();

        Action act = () => projector.Project(Array.Empty<Trail>(), BuildRaster(), BuildMesh(), null!, 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_PreservesSourceTrail()
    {
        var trail = BuildTrailAtCenter();
        var projector = new Trail3DOverlayProjector();

        var result = projector.Project(new[] { trail }, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Single().Source.Should().BeSameAs(trail);
    }

    [Fact]
    public void Project_ProducesSameScreenPointsAsLegacyProject()
    {
        var trails = new[] { BuildTrailAtCenter() };
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var camera = LookDownCamera();

        var legacy = Trail3DProjection.Project(trails, raster, mesh, camera, 800f, 600f);
        var projected = new Trail3DOverlayProjector().Project(trails, raster, mesh, camera, 800f, 600f);

        projected.Single().ScreenPoints.Should().Equal(legacy.Single().ScreenPoints);
    }

    [Fact]
    public void Project_ReusesScreenBuffersWhenInputsUnchanged()
    {
        var trails = new[] { BuildTrailAtCenter() };
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var projector = new Trail3DOverlayProjector();

        var first = projector.Project(trails, raster, mesh, LookDownCamera(), 800f, 600f);
        var firstBuffer = first.Single().ScreenPoints;
        var second = projector.Project(trails, raster, mesh, OrbitedCamera(), 800f, 600f);

        second.Single().ScreenPoints.Should().BeSameAs(firstBuffer);
    }

    [Fact]
    public void Project_ReusesResultListWhenInputsUnchanged()
    {
        var trails = new[] { BuildTrailAtCenter() };
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var projector = new Trail3DOverlayProjector();

        var first = projector.Project(trails, raster, mesh, LookDownCamera(), 800f, 600f);
        var second = projector.Project(trails, raster, mesh, OrbitedCamera(), 800f, 600f);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void Project_RebuildsOnTrailsReferenceChange()
    {
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var camera = LookDownCamera();
        var projector = new Trail3DOverlayProjector();
        var trailsA = new[] { BuildTrailAtCenter() };
        var trailsB = new[] { BuildTrailAtCenter() };

        var first = projector.Project(trailsA, raster, mesh, camera, 800f, 600f);
        var second = projector.Project(trailsB, raster, mesh, camera, 800f, 600f);

        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void Project_ReprojectsWhenCameraChanges()
    {
        var trails = new[] { BuildTrailAtCenter() };
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var projector = new Trail3DOverlayProjector();

        var first = projector.Project(trails, raster, mesh, LookDownCamera(), 800f, 600f);
        var snapshot = first.Single().ScreenPoints.ToList();
        projector.Project(trails, raster, mesh, OrbitedCamera(), 800f, 600f);

        first.Single().ScreenPoints.Should().NotEqual(snapshot);
    }
}
