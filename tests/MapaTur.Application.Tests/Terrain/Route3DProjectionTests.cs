using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Route3DProjectionTests
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

    private static Route BuildRouteAtCenter()
    {
        var segments = new List<RouteSegment>
        {
            new(new GeoPoint(49.45, 19.45), new GeoPoint(49.5, 19.5), 7000.0, 50.0, 0.0, 5400.0),
            new(new GeoPoint(49.5, 19.5), new GeoPoint(49.55, 19.55), 7000.0, 50.0, 0.0, 5400.0),
        };
        return new Route(segments);
    }

    [Fact]
    public void Project_NullRoute_Throws()
    {
        Action act = () => Route3DProjection.Project(null!, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullRaster_Throws()
    {
        Action act = () => Route3DProjection.Project(BuildRouteAtCenter(), null!, BuildMesh(), LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullMesh_Throws()
    {
        Action act = () => Route3DProjection.Project(BuildRouteAtCenter(), BuildRaster(), null!, LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullCamera_Throws()
    {
        Action act = () => Route3DProjection.Project(BuildRouteAtCenter(), BuildRaster(), BuildMesh(), null!, 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_ScreenPointsCountMatchesPolylineLength()
    {
        var route = BuildRouteAtCenter();
        int expectedPoints = route.ToPolyline().Count;

        var result = Route3DProjection.Project(route, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.ScreenPoints.Should().HaveCount(expectedPoints);
    }

    [Fact]
    public void Project_PreservesSourceRoute()
    {
        var route = BuildRouteAtCenter();

        var result = Route3DProjection.Project(route, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Source.Should().BeSameAs(route);
    }

    [Fact]
    public void Project_VerticesInsideFrustum_HaveFiniteScreenPoints()
    {
        var route = BuildRouteAtCenter();

        var result = Route3DProjection.Project(route, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.ScreenPoints.Should().AllSatisfy(p =>
        {
            p.Should().NotBeNull();
            float.IsFinite(p!.Value.X).Should().BeTrue();
            float.IsFinite(p.Value.Y).Should().BeTrue();
        });
    }

    [Fact]
    public void Project_VertexBehindCamera_IsNull()
    {
        var route = BuildRouteAtCenter();
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1f,
            AzimuthRadians = 0f,
            PitchRadians = 0f,
            NearPlane = 1_000_000f,
            FarPlane = 2_000_000f,
        };

        var result = Route3DProjection.Project(route, BuildRaster(), BuildMesh(), camera, 800f, 600f);

        result.ScreenPoints.Should().AllSatisfy(p => p.Should().BeNull());
    }

    [Fact]
    public void Project_RouteLiftRaisesScreenPosition()
    {
        var route = BuildRouteAtCenter();
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var camera = LookDownCamera();

        var withSmallLift = Route3DProjection.Project(route, raster, mesh, camera, 800f, 600f, routeLiftMeters: 0f);
        var withBigLift = Route3DProjection.Project(route, raster, mesh, camera, 800f, 600f, routeLiftMeters: 500f);

        float lowY = withSmallLift.ScreenPoints[1]!.Value.Y;
        float highY = withBigLift.ScreenPoints[1]!.Value.Y;
        highY.Should().BeLessThan(lowY);
    }
}