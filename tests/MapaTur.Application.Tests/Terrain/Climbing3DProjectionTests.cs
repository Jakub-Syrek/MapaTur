using System.Numerics;
using FluentAssertions;
using MapaTur.Application.Terrain;
using MapaTur.Domain.Climbing;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Climbing3DProjectionTests
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

    private static ClimbingArea BuildAreaInsideRaster(long id = 1L)
        => new(id, "Test Crag", new GeoPoint(49.5, 19.5), ClimbingType.SportRoute);

    [Fact]
    public void Project_NullAreas_Throws()
    {
        Action act = () => Climbing3DProjection.Project(null!, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullRaster_Throws()
    {
        Action act = () => Climbing3DProjection.Project(Array.Empty<ClimbingArea>(), null!, BuildMesh(), LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullMesh_Throws()
    {
        Action act = () => Climbing3DProjection.Project(Array.Empty<ClimbingArea>(), BuildRaster(), null!, LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullCamera_Throws()
    {
        Action act = () => Climbing3DProjection.Project(Array.Empty<ClimbingArea>(), BuildRaster(), BuildMesh(), null!, 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_EmptyInput_ReturnsEmpty()
    {
        var result = Climbing3DProjection.Project(Array.Empty<ClimbingArea>(), BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Project_AreaInsideRaster_HasFiniteScreenPoint()
    {
        var area = BuildAreaInsideRaster();

        var result = Climbing3DProjection.Project(new[] { area }, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Should().HaveCount(1);
        var screen = result.Single().ScreenPosition;
        screen.Should().NotBeNull();
        float.IsFinite(screen!.Value.X).Should().BeTrue();
        float.IsFinite(screen.Value.Y).Should().BeTrue();
    }

    [Fact]
    public void Project_PreservesSourceArea()
    {
        var area = BuildAreaInsideRaster();

        var result = Climbing3DProjection.Project(new[] { area }, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Single().Source.Should().BeSameAs(area);
    }

    [Fact]
    public void Project_AreaOutsideRasterBounds_ExcludedFromResult()
    {
        var inside = BuildAreaInsideRaster();
        var farAway = new ClimbingArea(2L, "Patagonia", new GeoPoint(-50.0, -73.0), ClimbingType.TradRoute);

        var result = Climbing3DProjection.Project(
            new[] { inside, farAway },
            BuildRaster(),
            BuildMesh(),
            LookDownCamera(),
            800f,
            600f);

        result.Should().HaveCount(1);
        result.Single().Source.Should().BeSameAs(inside);
    }

    [Fact]
    public void Project_AreaBehindCamera_HasNullScreenPosition()
    {
        var area = BuildAreaInsideRaster();
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1f,
            AzimuthRadians = 0f,
            PitchRadians = 0f,
            NearPlane = 1_000_000f,
            FarPlane = 2_000_000f,
        };

        var result = Climbing3DProjection.Project(new[] { area }, BuildRaster(), BuildMesh(), camera, 800f, 600f);

        result.Single().ScreenPosition.Should().BeNull();
    }

    [Fact]
    public void Project_MarkerLiftRaisesScreenPosition()
    {
        var area = BuildAreaInsideRaster();
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var camera = LookDownCamera();

        var withSmallLift = Climbing3DProjection.Project(new[] { area }, raster, mesh, camera, 800f, 600f, markerLiftMeters: 0f);
        var withBigLift = Climbing3DProjection.Project(new[] { area }, raster, mesh, camera, 800f, 600f, markerLiftMeters: 500f);

        float lowY = withSmallLift.Single().ScreenPosition!.Value.Y;
        float highY = withBigLift.Single().ScreenPosition!.Value.Y;
        highY.Should().BeLessThan(lowY);
    }
}