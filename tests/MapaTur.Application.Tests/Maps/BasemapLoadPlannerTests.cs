using FluentAssertions;

using MapaTur.Application.Maps;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Maps;

public sealed class BasemapLoadPlannerTests
{
    // Real-world shapes: the purchased Polish Tatra raster is small + high detail
    // (z15), the generated Slovak Carpathian render is broad + coarse (z13) and its
    // bounds fully contain the Tatra bbox.
    private static readonly BasemapDescriptor TatryDetailed = new(
        "Tatry_Polskie_2026.mbtiles",
        MaxZoomLevel: 15,
        Bounds: new MapBounds(new GeoPoint(49.174287, 19.753218), new GeoPoint(49.357477, 20.155346)));

    private static readonly BasemapDescriptor SlovakBroad = new(
        "carpathian-sk.mbtiles",
        MaxZoomLevel: 13,
        Bounds: new MapBounds(new GeoPoint(48.0, 18.5), new GeoPoint(50.2, 23.5)));

    [Fact]
    public void Plan_EmptyInput_ReturnsEmptyOrder()
    {
        var plan = BasemapLoadPlanner.Plan(Array.Empty<BasemapDescriptor>());

        plan.LoadOrder.Should().BeEmpty();
    }

    [Fact]
    public void Plan_EmptyInput_ReturnsNullPrimary()
    {
        var plan = BasemapLoadPlanner.Plan(Array.Empty<BasemapDescriptor>());

        plan.PrimaryPath.Should().BeNull();
    }

    [Fact]
    public void Plan_SingleBasemap_IsPrimary()
    {
        var plan = BasemapLoadPlanner.Plan(new[] { SlovakBroad });

        plan.PrimaryPath.Should().Be(SlovakBroad.Path);
    }

    [Fact]
    public void Plan_HigherMaxZoom_IsPrimary()
    {
        // Discovery enumerates coarse-first (alphabetical "carpathian" < "Tatry").
        var plan = BasemapLoadPlanner.Plan(new[] { SlovakBroad, TatryDetailed });

        plan.PrimaryPath.Should().Be(TatryDetailed.Path);
    }

    [Fact]
    public void Plan_MostDetailedBasemapLoadsLast_SoItDrawsOnTop()
    {
        // Loader stacks in load order: last loaded paints on top where they overlap.
        // The detailed Polish map must therefore be the final entry.
        var plan = BasemapLoadPlanner.Plan(new[] { SlovakBroad, TatryDetailed });

        plan.LoadOrder.Should().Equal(SlovakBroad.Path, TatryDetailed.Path);
    }

    [Fact]
    public void Plan_EqualMaxZoom_SmallerAreaIsPrimary()
    {
        var bigSameZoom = new BasemapDescriptor(
            "big.mbtiles", MaxZoomLevel: 14,
            Bounds: new MapBounds(new GeoPoint(48.0, 18.0), new GeoPoint(51.0, 24.0)));
        var smallSameZoom = new BasemapDescriptor(
            "small.mbtiles", MaxZoomLevel: 14,
            Bounds: new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(49.5, 20.0)));

        var plan = BasemapLoadPlanner.Plan(new[] { bigSameZoom, smallSameZoom });

        plan.PrimaryPath.Should().Be(smallSameZoom.Path);
    }

    [Fact]
    public void Plan_NullBoundsBasemap_RanksBelowBoundedPeerOfSameZoom()
    {
        // A bounds-less archive can't be a sensible zoom target; it should sink to the
        // bottom of the stack and never win primary against a bounded peer.
        var unbounded = new BasemapDescriptor("unbounded.mbtiles", MaxZoomLevel: 13, Bounds: null);

        var plan = BasemapLoadPlanner.Plan(new[] { unbounded, SlovakBroad });

        plan.PrimaryPath.Should().Be(SlovakBroad.Path);
    }

    [Fact]
    public void Plan_IsDeterministic_ForEqualRankByPath()
    {
        var a = new BasemapDescriptor("a.mbtiles", MaxZoomLevel: 12,
            Bounds: new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0)));
        var b = new BasemapDescriptor("b.mbtiles", MaxZoomLevel: 12,
            Bounds: new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0)));

        var plan1 = BasemapLoadPlanner.Plan(new[] { a, b });
        var plan2 = BasemapLoadPlanner.Plan(new[] { b, a });

        plan1.LoadOrder.Should().Equal(plan2.LoadOrder);
    }
}
