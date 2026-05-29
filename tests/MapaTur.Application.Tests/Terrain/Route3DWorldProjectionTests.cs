using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Route3DWorldProjectionTests
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

    private static Route BuildRoute()
    {
        var a = new GeoPoint(49.45, 19.45);
        var b = new GeoPoint(49.55, 19.55);
        var segment = new RouteSegment(a, b, 1500.0, 50.0, 0.0, 1200.0);
        return new Route(new[] { segment });
    }

    [Fact]
    public void ToWorld_NullRoute_Throws()
    {
        Action act = () => Route3DWorldProjection.ToWorld(null!, BuildRaster(), BuildMesh());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToWorld_NullRaster_Throws()
    {
        Action act = () => Route3DWorldProjection.ToWorld(BuildRoute(), null!, BuildMesh());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToWorld_NullMesh_Throws()
    {
        Action act = () => Route3DWorldProjection.ToWorld(BuildRoute(), BuildRaster(), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToWorld_PreservesSourceRoute()
    {
        var route = BuildRoute();

        var result = Route3DWorldProjection.ToWorld(route, BuildRaster(), BuildMesh());

        result.Source.Should().BeSameAs(route);
    }

    [Fact]
    public void ToWorld_WorldCountMatchesPolyline()
    {
        var route = BuildRoute();

        var result = Route3DWorldProjection.ToWorld(route, BuildRaster(), BuildMesh());

        result.World.Should().HaveCount(route.ToPolyline().Count);
    }

    [Fact]
    public void ToWorld_WorldPointMatchesGeoToWorldAtLiftedElevation()
    {
        var route = BuildRoute();
        var mesh = BuildMesh();
        const float lift = 8f;
        var polyline = route.ToPolyline();
        // Flat 1000 m plateau, so sampled ground elevation is exactly 1000 m.
        var expected = mesh.GeoToWorld(polyline[0], 1000f + lift);

        var result = Route3DWorldProjection.ToWorld(route, BuildRaster(), mesh, lift);

        result.World[0].Should().Be(expected);
    }

    [Fact]
    public void ToScreen_NullCamera_Throws()
    {
        var world = Route3DWorldProjection.ToWorld(BuildRoute(), BuildRaster(), BuildMesh());

        Action act = () => Route3DWorldProjection.ToScreen(world, null!, 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToScreen_PreservesSourceRoute()
    {
        var route = BuildRoute();
        var world = Route3DWorldProjection.ToWorld(route, BuildRaster(), BuildMesh());

        var result = Route3DWorldProjection.ToScreen(world, LookDownCamera(), 800f, 600f);

        result.Source.Should().BeSameAs(route);
    }

    [Fact]
    public void ToScreen_ProducesSameScreenPointsAsLegacyProject()
    {
        var route = BuildRoute();
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var camera = LookDownCamera();

        var legacy = Route3DProjection.Project(route, raster, mesh, camera, 800f, 600f);
        var world = Route3DWorldProjection.ToWorld(route, raster, mesh);
        var split = Route3DWorldProjection.ToScreen(world, camera, 800f, 600f);

        split.ScreenPoints.Should().Equal(legacy.ScreenPoints);
    }
}
