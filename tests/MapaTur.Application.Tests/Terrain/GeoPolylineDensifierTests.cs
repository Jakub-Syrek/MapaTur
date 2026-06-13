using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

public sealed class GeoPolylineDensifierTests
{
    [Fact]
    public void Densify_LongSegment_InsertsPointsNoFartherApartThanSpacing_AndKeepsEndpoints()
    {
        // ~111 m north–south segment, 30 m spacing → 4 sub-segments.
        var a = new GeoPoint(49.0, 19.0);
        var b = new GeoPoint(49.001, 19.0);

        IReadOnlyList<GeoPoint> result = GeoPolylineDensifier.Densify(new[] { a, b }, maxSpacingMeters: 30.0);

        result[0].Should().Be(a);
        result[^1].Should().Be(b);
        result.Count.Should().BeGreaterThan(2);
        for (int i = 0; i < result.Count - 1; i++)
        {
            result[i].HaversineDistanceMetersTo(result[i + 1]).Should().BeLessThanOrEqualTo(30.5);
        }
    }

    [Fact]
    public void Densify_ShortSegment_LeavesItUnchanged()
    {
        // ~11 m segment, 30 m spacing → no intermediate points.
        var a = new GeoPoint(49.0, 19.0);
        var b = new GeoPoint(49.0001, 19.0);

        IReadOnlyList<GeoPoint> result = GeoPolylineDensifier.Densify(new[] { a, b }, maxSpacingMeters: 30.0);

        result.Should().Equal(a, b);
    }

    [Fact]
    public void Densify_PreservesOriginalVertices()
    {
        // Three real nodes; each must still appear in order (densify only ADDS between them).
        var a = new GeoPoint(49.0, 19.0);
        var b = new GeoPoint(49.001, 19.0);
        var c = new GeoPoint(49.001, 19.001);

        IReadOnlyList<GeoPoint> result = GeoPolylineDensifier.Densify(new[] { a, b, c }, maxSpacingMeters: 30.0);

        result[0].Should().Be(a);
        result[^1].Should().Be(c);
        result.Should().Contain(b);
    }

    [Fact]
    public void Densify_SinglePointOrEmpty_ReturnedAsIs()
    {
        var single = new[] { new GeoPoint(49.0, 19.0) };

        GeoPolylineDensifier.Densify(single, 30.0).Should().Equal(single);
        GeoPolylineDensifier.Densify(Array.Empty<GeoPoint>(), 30.0).Should().BeEmpty();
    }
}