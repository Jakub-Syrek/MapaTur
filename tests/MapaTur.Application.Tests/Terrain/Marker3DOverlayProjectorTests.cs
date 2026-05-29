using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Climbing;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Marker3DOverlayProjectorTests
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

    private static ClimbingArea AreaInside(long id = 1L)
        => new(id, "Test Crag", new GeoPoint(49.5, 19.5), ClimbingType.SportRoute);

    // A climbing-area instantiation of the generic projector, mirroring how the view wires it.
    private static Marker3DOverlayProjector<ClimbingArea, ProjectedClimbingArea> ClimbingProjector()
        => new(
            (areas, raster, mesh, lift) => Climbing3DProjection.ToWorld(areas, raster!, mesh, lift),
            (source, screen) => new ProjectedClimbingArea(source, screen));

    [Fact]
    public void Ctor_NullWorldBuilder_Throws()
    {
        Action act = () => _ = new Marker3DOverlayProjector<ClimbingArea, ProjectedClimbingArea>(
            null!, (s, screen) => new ProjectedClimbingArea(s, screen));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullResultFactory_Throws()
    {
        Action act = () => _ = new Marker3DOverlayProjector<ClimbingArea, ProjectedClimbingArea>(
            (areas, raster, mesh, lift) => Climbing3DProjection.ToWorld(areas, raster!, mesh, lift), null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullItems_Throws()
    {
        Action act = () => ClimbingProjector().Project(null!, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f, 30f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullMesh_Throws()
    {
        Action act = () => ClimbingProjector().Project(new[] { AreaInside() }, BuildRaster(), null!, LookDownCamera(), 800f, 600f, 30f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullCamera_Throws()
    {
        Action act = () => ClimbingProjector().Project(new[] { AreaInside() }, BuildRaster(), BuildMesh(), null!, 800f, 600f, 30f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_ProducesSameScreenPositionsAsEagerProjection()
    {
        var areas = new[] { AreaInside() };
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var camera = LookDownCamera();

        var eager = Climbing3DProjection.Project(areas, raster, mesh, camera, 800f, 600f, markerLiftMeters: 30f);
        var projected = ClimbingProjector().Project(areas, raster, mesh, camera, 800f, 600f, 30f);

        projected.Should().HaveCount(eager.Count);
        projected.Single().Source.Should().BeSameAs(eager.Single().Source);
        projected.Single().ScreenPosition.Should().Be(eager.Single().ScreenPosition);
    }

    [Fact]
    public void Project_DropsMarkersOutsideDemBbox()
    {
        var inside = AreaInside();
        var farAway = new ClimbingArea(2L, "Patagonia", new GeoPoint(-50.0, -73.0), ClimbingType.TradRoute);

        var projected = ClimbingProjector().Project(
            new[] { inside, farAway }, BuildRaster(), BuildMesh(), LookDownCamera(), 800f, 600f, 30f);

        projected.Should().HaveCount(1);
        projected.Single().Source.Should().BeSameAs(inside);
    }

    [Fact]
    public void Project_ReusesResultBufferWhenInputsUnchanged()
    {
        var areas = new[] { AreaInside() };
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var projector = ClimbingProjector();

        var first = projector.Project(areas, raster, mesh, LookDownCamera(), 800f, 600f, 30f);
        var second = projector.Project(areas, raster, mesh, OrbitedCamera(), 800f, 600f, 30f);

        second.Should().BeSameAs(first);
    }

    [Fact]
    public void Project_RebuildsOnItemsReferenceChange()
    {
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var camera = LookDownCamera();
        var projector = ClimbingProjector();

        var first = projector.Project(new[] { AreaInside() }, raster, mesh, camera, 800f, 600f, 30f);
        var second = projector.Project(new[] { AreaInside() }, raster, mesh, camera, 800f, 600f, 30f);

        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void Project_RebuildsOnMeshReferenceChange()
    {
        var areas = new[] { AreaInside() };
        var raster = BuildRaster();
        var camera = LookDownCamera();
        var projector = ClimbingProjector();

        var first = projector.Project(areas, raster, BuildMesh(), camera, 800f, 600f, 30f);
        var second = projector.Project(areas, raster, BuildMesh(), camera, 800f, 600f, 30f);

        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void Project_ReprojectsWhenCameraChanges()
    {
        var areas = new[] { AreaInside() };
        var raster = BuildRaster();
        var mesh = BuildMesh();
        var projector = ClimbingProjector();

        // Pan the camera target off the marker (which sits at the mesh centre) — a pure azimuth
        // orbit would leave a centred point projecting to the same pixel, so move the target instead.
        var pannedCamera = new Camera3D
        {
            Target = new Vector3(20_000f, 15_000f, 0f),
            Distance = 80_000f,
            AzimuthRadians = 0f,
            PitchRadians = MathF.PI / 3f,
            NearPlane = 10f,
            FarPlane = 200_000f,
        };

        var first = projector.Project(areas, raster, mesh, LookDownCamera(), 800f, 600f, 30f);
        Vector3? before = first.Single().ScreenPosition;
        projector.Project(areas, raster, mesh, pannedCamera, 800f, 600f, 30f);

        // The result buffer is reused in place, so `first` now reflects the panned camera.
        first.Single().ScreenPosition.Should().NotBe(before);
    }

    [Fact]
    public void PeakInstantiation_ProducesSameScreenPositionsAsEagerProjection()
    {
        var peaks = new[] { new TerrainPeak(new GeoPoint(49.5, 19.5), 2100.0, "Rysy") };
        var mesh = BuildMesh();
        var camera = LookDownCamera();

        var peakProjector = new Marker3DOverlayProjector<TerrainPeak, ProjectedPeak>(
            (items, _, m, lift) => Peak3DProjection.ToWorld(items, m, lift),
            (source, screen) => new ProjectedPeak(source, screen));

        var eager = Peak3DProjection.Project(peaks, mesh, camera, 800f, 600f, markerLiftMeters: 40f);
        var projected = peakProjector.Project(peaks, null, mesh, camera, 800f, 600f, 40f);

        projected.Should().HaveCount(eager.Count);
        projected.Single().ScreenPosition.Should().Be(eager.Single().ScreenPosition);
    }
}