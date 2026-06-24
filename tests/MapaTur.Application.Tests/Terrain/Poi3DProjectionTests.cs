using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Pois;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Poi3DProjectionTests
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

    private static MountainPoi BuildPoiInsideRaster(long id = 1L)
        => new(id, "Test Hut", new GeoPoint(49.5, 19.5), PoiKind.Hut);

    [Fact]
    public void Project_NullPois_Throws()
    {
        Action act = () => Poi3DProjection.Project(null!, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullRaster_Throws()
    {
        Action act = () => Poi3DProjection.Project(Array.Empty<MountainPoi>(), null!, BuildMesh(), LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullMesh_Throws()
    {
        Action act = () => Poi3DProjection.Project(Array.Empty<MountainPoi>(), BuildRaster(), null!, LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullCamera_Throws()
    {
        Action act = () => Poi3DProjection.Project(Array.Empty<MountainPoi>(), BuildRaster(), BuildMesh(), null!, 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_EmptyInput_ReturnsEmpty()
    {
        var result = Poi3DProjection.Project(Array.Empty<MountainPoi>(), BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Project_PoiInsideRaster_HasFiniteScreenPoint()
    {
        var poi = BuildPoiInsideRaster();

        var result = Poi3DProjection.Project(new[] { poi }, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Should().HaveCount(1);
        var screen = result.Single().ScreenPosition;
        screen.Should().NotBeNull();
        float.IsFinite(screen!.Value.X).Should().BeTrue();
        float.IsFinite(screen.Value.Y).Should().BeTrue();
    }

    [Fact]
    public void Project_PreservesSourcePoi()
    {
        var poi = BuildPoiInsideRaster();

        var result = Poi3DProjection.Project(new[] { poi }, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Single().Source.Should().BeSameAs(poi);
    }

    [Fact]
    public void Project_PoiOutsideRasterBounds_ExcludedFromResult()
    {
        var inside = BuildPoiInsideRaster();
        var farAway = new MountainPoi(2L, "Patagonia Refugio", new GeoPoint(-50.0, -73.0), PoiKind.WildernessHut);

        var result = Poi3DProjection.Project(
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
    public void Project_PoiBehindCamera_HasNullScreenPosition()
    {
        var poi = BuildPoiInsideRaster();
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1f,
            AzimuthRadians = 0f,
            PitchRadians = 0f,
            NearPlane = 1_000_000f,
            FarPlane = 2_000_000f,
        };

        var result = Poi3DProjection.Project(new[] { poi }, BuildRaster(), BuildMesh(), camera, 800f, 600f);

        result.Single().ScreenPosition.Should().BeNull();
    }

    [Fact]
    public void Project_MarkerLiftRaisesScreenPosition()
    {
        var poi = BuildPoiInsideRaster();
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var camera = LookDownCamera();

        var withSmallLift = Poi3DProjection.Project(new[] { poi }, raster, mesh, camera, 800f, 600f, markerLiftMeters: 0f);
        var withBigLift = Poi3DProjection.Project(new[] { poi }, raster, mesh, camera, 800f, 600f, markerLiftMeters: 500f);

        float lowY = withSmallLift.Single().ScreenPosition!.Value.Y;
        float highY = withBigLift.Single().ScreenPosition!.Value.Y;
        highY.Should().BeLessThan(lowY);
    }

    private static DetailElevationField BuildDetail(float elevation, MapBounds bounds)
    {
        var samples = new float[8 * 8];
        Array.Fill(samples, elevation);
        return new DetailElevationField(new DemRaster(8, 8, bounds, samples));
    }

    [Fact]
    public void ToWorld_SeatsOnDetailSurfaceWhenItCoversThePoi()
    {
        // The coarse base reads 1000 m here, but the rendered 1 m detail dips to 900 m (a saddle the base
        // smooths upward). The marker must seat on the detail surface, not float on the over-reporting base.
        var poi = BuildPoiInsideRaster();
        var mesh = BuildMesh();
        var detail = BuildDetail(900f, new MapBounds(new GeoPoint(49.4, 19.4), new GeoPoint(49.6, 19.6)));

        var seatedOnDetail = Poi3DProjection.ToWorld(new[] { poi }, BuildRaster(1000f), mesh, 0f, detail);
        var seatedOnBase900 = Poi3DProjection.ToWorld(new[] { poi }, BuildRaster(900f), mesh, 0f);
        var seatedOnBase1000 = Poi3DProjection.ToWorld(new[] { poi }, BuildRaster(1000f), mesh, 0f);

        seatedOnDetail.Single().World.Should().Be(seatedOnBase900.Single().World);
        seatedOnDetail.Single().World.Should().NotBe(seatedOnBase1000.Single().World);
    }

    [Fact]
    public void ToWorld_FallsBackToBaseOutsideDetailWindow()
    {
        // A POI outside the detail window has no rendered-surface sample → seat on the coarse base, unchanged.
        var poi = BuildPoiInsideRaster();
        var mesh = BuildMesh();
        var detailFarAway = BuildDetail(900f, new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(49.2, 19.2)));

        var withDetail = Poi3DProjection.ToWorld(new[] { poi }, BuildRaster(1000f), mesh, 0f, detailFarAway);
        var baseOnly = Poi3DProjection.ToWorld(new[] { poi }, BuildRaster(1000f), mesh, 0f);

        withDetail.Single().World.Should().Be(baseOnly.Single().World);
    }
}