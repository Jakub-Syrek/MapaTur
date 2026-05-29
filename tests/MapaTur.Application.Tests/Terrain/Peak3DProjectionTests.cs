using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class Peak3DProjectionTests
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

    private static TerrainPeak PeakInside() => new(new GeoPoint(49.5, 19.5), 2000.0);

    [Fact]
    public void Project_NullPeaks_Throws()
    {
        Action act = () => Peak3DProjection.Project(null!, BuildMesh(), LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullMesh_Throws()
    {
        Action act = () => Peak3DProjection.Project(Array.Empty<TerrainPeak>(), null!, LookDownCamera(), 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_NullCamera_Throws()
    {
        Action act = () => Peak3DProjection.Project(Array.Empty<TerrainPeak>(), BuildMesh(), null!, 800f, 600f);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Project_EmptyInput_ReturnsEmpty()
    {
        var result = Peak3DProjection.Project(Array.Empty<TerrainPeak>(), BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Project_PeakInside_HasFiniteScreenPoint()
    {
        var peak = PeakInside();

        var result = Peak3DProjection.Project(new[] { peak }, BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Should().HaveCount(1);
        var screen = result.Single().ScreenPosition;
        screen.Should().NotBeNull();
        float.IsFinite(screen!.Value.X).Should().BeTrue();
        float.IsFinite(screen.Value.Y).Should().BeTrue();
    }

    [Fact]
    public void Project_PreservesSourcePeak()
    {
        var peak = PeakInside();

        var result = Peak3DProjection.Project(new[] { peak }, BuildMesh(), LookDownCamera(), 800f, 600f);

        result.Single().Source.Should().Be(peak);
    }

    [Fact]
    public void Project_PeakBehindCamera_HasNullScreenPosition()
    {
        var peak = PeakInside();
        var camera = new Camera3D
        {
            Target = Vector3.Zero,
            Distance = 1f,
            AzimuthRadians = 0f,
            PitchRadians = 0f,
            NearPlane = 1_000_000f,
            FarPlane = 2_000_000f,
        };

        var result = Peak3DProjection.Project(new[] { peak }, BuildMesh(), camera, 800f, 600f);

        result.Single().ScreenPosition.Should().BeNull();
    }

    [Fact]
    public void Project_MarkerLiftRaisesScreenPosition()
    {
        var peak = PeakInside();
        var mesh = BuildMesh();
        var camera = LookDownCamera();

        var low = Peak3DProjection.Project(new[] { peak }, mesh, camera, 800f, 600f, markerLiftMeters: 0f);
        var high = Peak3DProjection.Project(new[] { peak }, mesh, camera, 800f, 600f, markerLiftMeters: 500f);

        float lowY = low.Single().ScreenPosition!.Value.Y;
        float highY = high.Single().ScreenPosition!.Value.Y;
        highY.Should().BeLessThan(lowY, "a higher marker projects nearer the top of the screen");
    }
}