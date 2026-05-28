using FluentAssertions;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Domain.Tests.Trails;

public sealed class TrailGeometrySimplifierTests
{
    private static GeoPoint Pt(double lat, double lon) => new(lat, lon);

    [Fact]
    public void Simplify_NullGeometry_Throws()
    {
        Action act = () => TrailGeometrySimplifier.Simplify(null!, 10.0);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Simplify_NegativeEpsilon_Throws()
    {
        Action act = () => TrailGeometrySimplifier.Simplify(Array.Empty<GeoPoint>(), -1.0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Simplify_EmptyInput_ReturnsEmpty()
    {
        var result = TrailGeometrySimplifier.Simplify(Array.Empty<GeoPoint>(), 10.0);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Simplify_SinglePoint_ReturnsThatPoint()
    {
        var input = new[] { Pt(49.0, 19.0) };

        var result = TrailGeometrySimplifier.Simplify(input, 10.0);

        result.Should().Equal(input);
    }

    [Fact]
    public void Simplify_TwoPoints_ReturnsBothUnchanged()
    {
        var input = new[] { Pt(49.0, 19.0), Pt(49.001, 19.001) };

        var result = TrailGeometrySimplifier.Simplify(input, 10.0);

        result.Should().Equal(input);
    }

    [Fact]
    public void Simplify_EpsilonZero_ReturnsAllPoints()
    {
        var input = new[]
        {
            Pt(49.0, 19.0),
            Pt(49.0005, 19.0005),
            Pt(49.001, 19.001),
            Pt(49.0015, 19.0015),
            Pt(49.002, 19.002),
        };

        var result = TrailGeometrySimplifier.Simplify(input, 0.0);

        result.Should().Equal(input);
    }

    [Fact]
    public void Simplify_AlmostStraightLine_DropsInteriorPoints()
    {
        // Five colinear points on a 200 m straight segment, epsilon 10 m → only endpoints remain.
        var input = new[]
        {
            Pt(49.0000, 19.0000),
            Pt(49.0001, 19.0000),
            Pt(49.0002, 19.0000),
            Pt(49.0003, 19.0000),
            Pt(49.0004, 19.0000),
        };

        var result = TrailGeometrySimplifier.Simplify(input, 10.0);

        result.Should().HaveCount(2);
        result[0].Should().Be(input[0]);
        result[^1].Should().Be(input[^1]);
    }

    [Fact]
    public void Simplify_SharpDetour_KeepsTheDetourVertex()
    {
        // Trail makes a ~1 km detour to the east in the middle — must survive epsilon = 50 m.
        var input = new[]
        {
            Pt(49.0000, 19.0000),
            Pt(49.0001, 19.0150), // ~1100 m east of the chord
            Pt(49.0002, 19.0000),
        };

        var result = TrailGeometrySimplifier.Simplify(input, 50.0);

        result.Should().HaveCount(3);
        result.Should().Equal(input);
    }

    [Fact]
    public void Simplify_PreservesFirstAndLastPoint()
    {
        var input = new[]
        {
            Pt(49.0, 19.0),
            Pt(49.0001, 19.0001),
            Pt(49.0002, 19.0002),
            Pt(49.0003, 19.0003),
        };

        var result = TrailGeometrySimplifier.Simplify(input, 100.0);

        result[0].Should().Be(input[0]);
        result[^1].Should().Be(input[^1]);
    }

    [Fact]
    public void Simplify_LargeEpsilon_CollapsesToEndpoints()
    {
        var input = new[]
        {
            Pt(49.0000, 19.0000),
            Pt(49.0010, 19.0010),
            Pt(49.0020, 19.0005),
            Pt(49.0030, 19.0015),
            Pt(49.0040, 19.0000),
        };

        var result = TrailGeometrySimplifier.Simplify(input, 100_000.0);

        result.Should().HaveCount(2);
        result[0].Should().Be(input[0]);
        result[1].Should().Be(input[^1]);
    }

    [Fact]
    public void Simplify_PttkLikeTrail_AchievesSignificantReduction()
    {
        // Synthesize a typical hiking trail: ~100 vertices along a wavy path
        // covering ~1 km. Real-world Douglas-Peucker at 10 m should keep
        // far fewer points (typically <30% of the original).
        var rng = new Random(42);
        var input = new List<GeoPoint>(capacity: 100);
        double lat = 49.0;
        double lon = 19.0;
        for (int i = 0; i < 100; i++)
        {
            input.Add(Pt(lat, lon));
            lat += 0.00009 + (rng.NextDouble() - 0.5) * 0.00001;
            lon += 0.00009 + (rng.NextDouble() - 0.5) * 0.00001;
        }

        var result = TrailGeometrySimplifier.Simplify(input, 10.0);

        result.Count.Should().BeLessThan(input.Count / 2);
        result[0].Should().Be(input[0]);
        result[^1].Should().Be(input[^1]);
    }
}
