using FluentAssertions;

using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Tests.Geography;

public sealed class MapBoundsTests
{
    private static MapBounds Box(double southLat, double westLon, double northLat, double eastLon)
        => new(new GeoPoint(southLat, westLon), new GeoPoint(northLat, eastLon));

    [Fact]
    public void Intersect_DisjointBounds_ReturnsNull()
    {
        var a = Box(40, 10, 50, 20);
        var b = Box(60, 30, 70, 40);

        a.Intersect(b).Should().BeNull();
    }

    [Fact]
    public void Intersect_IdenticalBounds_ReturnsSameBounds()
    {
        var a = Box(40, 10, 50, 20);

        a.Intersect(a).Should().Be(a);
    }

    [Fact]
    public void Intersect_OneInsideAnother_ReturnsInner()
    {
        var outer = Box(40, 10, 60, 30);
        var inner = Box(45, 15, 55, 25);

        outer.Intersect(inner).Should().Be(inner);
    }

    [Fact]
    public void Intersect_PartialOverlap_ReturnsOverlapRect()
    {
        var a = Box(40, 10, 50, 20);
        var b = Box(45, 15, 55, 25);

        var result = a.Intersect(b);

        result.Should().NotBeNull();
        result!.Value.SouthWest.Should().Be(new GeoPoint(45, 15));
        result.Value.NorthEast.Should().Be(new GeoPoint(50, 20));
    }

    [Fact]
    public void Intersect_TouchingEdge_ReturnsNull()
    {
        var a = Box(40, 10, 50, 20);
        var b = Box(50, 10, 60, 20);

        a.Intersect(b).Should().BeNull();
    }

    [Fact]
    public void Intersect_IsCommutative()
    {
        var a = Box(40, 10, 50, 20);
        var b = Box(45, 15, 55, 25);

        a.Intersect(b).Should().Be(b.Intersect(a));
    }

    [Fact]
    public void Union_DisjointBounds_ReturnsBoxCoveringBoth()
    {
        var a = Box(40, 10, 45, 15);
        var b = Box(50, 20, 55, 25);

        var result = a.Union(b);

        result.SouthWest.Should().Be(new GeoPoint(40, 10));
        result.NorthEast.Should().Be(new GeoPoint(55, 25));
    }

    [Fact]
    public void Union_IdenticalBounds_ReturnsSameBounds()
    {
        var a = Box(40, 10, 50, 20);

        a.Union(a).Should().Be(a);
    }

    [Fact]
    public void Union_OneInsideAnother_ReturnsOuter()
    {
        var outer = Box(40, 10, 60, 30);
        var inner = Box(45, 15, 55, 25);

        outer.Union(inner).Should().Be(outer);
    }

    [Fact]
    public void Union_IsCommutative()
    {
        var a = Box(40, 10, 50, 20);
        var b = Box(45, 15, 55, 25);

        a.Union(b).Should().Be(b.Union(a));
    }
}