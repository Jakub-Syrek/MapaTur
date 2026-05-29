using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Route3DOverlayProjectorTests
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

    private static Route BuildRoute()
    {
        var a = new GeoPoint(49.45, 19.45);
        var b = new GeoPoint(49.55, 19.55);
        var segment = new RouteSegment(a, b, 1500.0, 50.0, 0.0, 1200.0);
        return new Route(new[] { segment });
    }

    [Fact]
    public void Project_NullRoute_Throws()
    {
        var projector = new Route3DOverlayProjector();

        Action act = () => projector.Project(null!, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullRaster_Throws()
    {
        var projector = new Route3DOverlayProjector();

        Action act = () => projector.Project(BuildRoute(), null!, BuildMesh(), LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullMesh_Throws()
    {
        var projector = new Route3DOverlayProjector();

        Action act = () => projector.Project(BuildRoute(), BuildRaster(), null!, LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullCamera_Throws()
    {
        var projector = new Route3DOverlayProjector();

        Action act = () => projector.Project(BuildRoute(), BuildRaster(), BuildMesh(), null!, 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_PreservesSourceRoute()
    {
        var route = BuildRoute();
        var projector = new Route3DOverlayProjector();

        var result = projector.Project(route, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Source.Should().BeSameAs(route);
    }

    [Fact]
    public void Project_ProducesSameScreenPointsAsLegacyProject()
    {
        var route = BuildRoute();
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var camera = LookDownCamera();

        var legacy = Route3DProjection.Project(route, raster, mesh, camera, 800f, 600f);
        var projected = new Route3DOverlayProjector().Project(route, raster, mesh, camera, 800f, 600f);

        projected.ScreenPoints.Should().Equal(legacy.ScreenPoints);
    }

    [Fact]
    public void Project_ReusesScreenBufferWhenInputsUnchanged()
    {
        var route = BuildRoute();
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var projector = new Route3DOverlayProjector();

        var first = projector.Project(route, raster, mesh, LookDownCamera(), 800f, 600f);
        var firstBuffer = first.ScreenPoints;
        var second = projector.Project(route, raster, mesh, OrbitedCamera(), 800f, 600f);

        second.ScreenPoints.Should().BeSameAs(firstBuffer);
    }

    [Fact]
    public void Project_RebuildsBufferOnRouteReferenceChange()
    {
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var camera = LookDownCamera();
        var projector = new Route3DOverlayProjector();

        var first = projector.Project(BuildRoute(), raster, mesh, camera, 800f, 600f);
        var firstBuffer = first.ScreenPoints;
        var second = projector.Project(BuildRoute(), raster, mesh, camera, 800f, 600f);

        second.ScreenPoints.Should().NotBeSameAs(firstBuffer);
    }

    [Fact]
    public void Project_ReprojectsWhenCameraChanges()
    {
        var route = BuildRoute();
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var projector = new Route3DOverlayProjector();

        var first = projector.Project(route, raster, mesh, LookDownCamera(), 800f, 600f);
        var snapshot = first.ScreenPoints.ToList();
        projector.Project(route, raster, mesh, OrbitedCamera(), 800f, 600f);

        first.ScreenPoints.Should().NotEqual(snapshot);
    }
}
