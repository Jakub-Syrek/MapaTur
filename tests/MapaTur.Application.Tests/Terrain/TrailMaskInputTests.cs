using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Tests.Terrain;

public sealed class TrailMaskInputTests
{
    private static TrailWorldLine TrailLine(PttkColor color, params Vector3[] world) =>
        new(
            new Trail(1L, "t", new List<TrailMarking> { new(color) }, new List<GeoPoint> { new(49.0, 19.0), new(49.1, 19.1) }),
            world);

    private static readonly Vector3[] SampleWorld = { new(0, 0, 0), new(10, 10, 0) };

    [Fact]
    public void TrailColor_Red_MatchesPttkPalette()
    {
        TrailMaskInput.TrailColor(PttkColor.Red).Should().Be(((byte)0xDC, (byte)0x26, (byte)0x26));
    }

    [Fact]
    public void TrailColor_None_FallsBackToSlate()
    {
        TrailMaskInput.TrailColor(PttkColor.None).Should().Be(((byte)0x94, (byte)0xA3, (byte)0xB8));
    }

    [Fact]
    public void Build_AllInputsNull_ReturnsEmpty()
    {
        TrailMaskInput.Build().Should().BeEmpty();
    }

    [Fact]
    public void Build_IncludesEveryDecalLayer()
    {
        // The route is NOT a decal layer (it's the translucent dashed overlay), so only roads/trails/exposed go in.
        var lines = TrailMaskInput.Build(
            trails: new[] { TrailLine(PttkColor.Red, SampleWorld) },
            roads: new[] { TrailLine(PttkColor.None, SampleWorld) },
            exposed: new[] { TrailLine(PttkColor.None, SampleWorld) });

        lines.Should().HaveCount(3);
    }

    [Fact]
    public void Build_PriorityOrdering_ExposedOverTrailOverRoad()
    {
        // Decal draw order is roads → trails → exposed (the route is drawn separately, on top, as an overlay).
        TrailMaskInput.ExposedPriority.Should().BeGreaterThan(TrailMaskInput.TrailPriority);
        TrailMaskInput.TrailPriority.Should().BeGreaterThan(TrailMaskInput.RoadPriority);
    }

    [Fact]
    public void Build_AssignsLayerPriorityToEachLine()
    {
        var lines = TrailMaskInput.Build(
            trails: new[] { TrailLine(PttkColor.Red, SampleWorld) },
            roads: new[] { TrailLine(PttkColor.None, SampleWorld) },
            exposed: new[] { TrailLine(PttkColor.None, SampleWorld) });

        lines.Select(l => l.Priority).Should().Contain(new[]
        {
            TrailMaskInput.RoadPriority,
            TrailMaskInput.TrailPriority,
            TrailMaskInput.ExposedPriority,
        });
    }

    [Fact]
    public void Build_TrailUsesPrimaryColour()
    {
        var lines = TrailMaskInput.Build(trails: new[] { TrailLine(PttkColor.Blue, SampleWorld) });

        var trail = lines.Single();
        (trail.R, trail.G, trail.B).Should().Be(TrailMaskInput.TrailColor(PttkColor.Blue));
    }

    [Fact]
    public void RouteColor_IsViolet()
    {
        // The route is drawn as the translucent dashed overlay (not a decal layer); this constant is its tint.
        TrailMaskInput.RouteColor.Should().Be(((byte)0x7C, (byte)0x3A, (byte)0xED));
    }

    [Fact]
    public void Build_TrailsOnly_YieldsOnlyTrailPriority()
    {
        var lines = TrailMaskInput.Build(trails: new[] { TrailLine(PttkColor.Red, SampleWorld) });

        lines.Should().ContainSingle().Which.Priority.Should().Be(TrailMaskInput.TrailPriority);
    }

    [Fact]
    public void Build_PassesWorldVerticesThrough()
    {
        var lines = TrailMaskInput.Build(trails: new[] { TrailLine(PttkColor.Red, SampleWorld) });

        lines.Single().Points.Should().BeSameAs(SampleWorld);
    }
}