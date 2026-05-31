using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Location;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class UserLocation3DProjectionTests
{
    private static readonly MapBounds TatraBounds = new(
        new GeoPoint(49.10, 19.50),
        new GeoPoint(49.40, 20.40));

    private static readonly DateTimeOffset T = new(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);

    // 64×32 flat raster at 1500 m elevation — enough for the projector to do its lat/lon bounds check
    // and DEM lookup without dragging in a real Tatry .dem fixture.
    private static DemRaster FlatRaster(float elevation = 1500f)
    {
        const int cols = 64;
        const int rows = 32;
        var samples = new float[cols * rows];
        Array.Fill(samples, elevation);
        return new DemRaster(cols, rows, TatraBounds, samples);
    }

    private static TerrainMesh3D BuildMesh() => TerrainMesh3D.Build(FlatRaster());

    [Fact]
    public void ToWorld_EmptyList_ReturnsEmpty()
    {
        IReadOnlyList<MarkerWorldPoint<UserLocation>> world = UserLocation3DProjection.ToWorld(
            Array.Empty<UserLocation>(), FlatRaster(), BuildMesh());

        world.Should().BeEmpty();
    }

    [Fact]
    public void ToWorld_FixWithGnssAltitude_PrefersAltitudeOverDemLookup()
    {
        // Raster sits at 1500 m; the fix reports a wildly different altitude (3000 m) so we can prove
        // the projection trusted the GNSS reading instead of the DEM. World Z is multiplied by the mesh's
        // VerticalExaggeration (default 2×) when GeoToWorld runs — assert against that scaled value.
        const float lift = 20f;
        var positionWithAlt = new GeoPoint(49.25, 19.95, 3000.0);
        var fix = UserLocation.Create(positionWithAlt, accuracyMeters: 4.0, timestamp: T);
        TerrainMesh3D mesh = BuildMesh();

        IReadOnlyList<MarkerWorldPoint<UserLocation>> world = UserLocation3DProjection.ToWorld(
            new[] { fix }, FlatRaster(), mesh, markerLiftMeters: lift);

        world.Should().ContainSingle();
        world[0].World.Z.Should().BeApproximately((3000f + lift) * mesh.VerticalExaggeration, precision: 0.5f);
    }

    [Fact]
    public void ToWorld_FixWithoutAltitude_SamplesDemElevation()
    {
        const float demElevation = 1500f;
        const float lift = 20f;
        var positionNoAlt = new GeoPoint(49.25, 19.95);
        var fix = UserLocation.Create(positionNoAlt, accuracyMeters: 8.0, timestamp: T);
        TerrainMesh3D mesh = BuildMesh();

        IReadOnlyList<MarkerWorldPoint<UserLocation>> world = UserLocation3DProjection.ToWorld(
            new[] { fix }, FlatRaster(demElevation), mesh, markerLiftMeters: lift);

        world.Should().ContainSingle();
        world[0].World.Z.Should().BeApproximately((demElevation + lift) * mesh.VerticalExaggeration, precision: 0.5f);
    }

    [Fact]
    public void ToWorld_FixOutsideDemAndWithoutAltitude_IsDropped()
    {
        var positionOutside = new GeoPoint(50.50, 22.00); // way north-east of the Tatra bbox
        var fix = UserLocation.Create(positionOutside, accuracyMeters: 8.0, timestamp: T);

        IReadOnlyList<MarkerWorldPoint<UserLocation>> world = UserLocation3DProjection.ToWorld(
            new[] { fix }, FlatRaster(), BuildMesh());

        world.Should().BeEmpty();
    }

    [Fact]
    public void ToWorld_FixOutsideDemButWithAltitude_IsKept()
    {
        // Out-of-DEM fixes are normally dropped, but when GNSS gives us altitude the marker has a real
        // position to render at — the projection should NOT silently drop it.
        var positionOutsideWithAlt = new GeoPoint(50.50, 22.00, 250.0);
        var fix = UserLocation.Create(positionOutsideWithAlt, accuracyMeters: 8.0, timestamp: T);

        IReadOnlyList<MarkerWorldPoint<UserLocation>> world = UserLocation3DProjection.ToWorld(
            new[] { fix }, FlatRaster(), BuildMesh());

        world.Should().ContainSingle();
    }

    [Fact]
    public void ToWorld_NoRasterAndNoAltitude_DropsFix()
    {
        var positionNoAlt = new GeoPoint(49.25, 19.95);
        var fix = UserLocation.Create(positionNoAlt, accuracyMeters: 8.0, timestamp: T);

        IReadOnlyList<MarkerWorldPoint<UserLocation>> world = UserLocation3DProjection.ToWorld(
            new[] { fix }, raster: null, BuildMesh());

        world.Should().BeEmpty();
    }
}