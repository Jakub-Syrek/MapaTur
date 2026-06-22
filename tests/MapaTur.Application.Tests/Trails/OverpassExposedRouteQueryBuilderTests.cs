using FluentAssertions;

using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Trails;

public sealed class OverpassExposedRouteQueryBuilderTests
{
    private static MapBounds Bounds() => new(new GeoPoint(49.1, 19.8), new GeoPoint(49.3, 20.2));

    [Fact]
    public void Query_SelectsExposedWayTags()
    {
        string q = OverpassExposedRouteQueryBuilder.BuildExposedRoutesQuery(Bounds());

        q.Should().Contain("sac_scale");          // demanding / alpine hiking
        q.Should().Contain("via_ferrata");        // highway=via_ferrata + via_ferrata_scale
        q.Should().Contain("trail_visibility");   // unmarked "perci" / guide paths
        q.Should().Contain("informal");           // informal=yes social/guide paths
        q.Should().Contain("out geom");           // inline way geometry
        q.Should().Contain("way[");               // standalone ways, not relations
    }

    [Fact]
    public void Query_EmbedsBoundingBox()
    {
        string q = OverpassExposedRouteQueryBuilder.BuildExposedRoutesQuery(Bounds());

        q.Should().Contain("49.100000");
        q.Should().Contain("19.800000");
        q.Should().Contain("49.300000");
        q.Should().Contain("20.200000");
    }

    [Fact]
    public void Query_RejectsNonPositiveTimeout()
    {
        Action act = () => OverpassExposedRouteQueryBuilder.BuildExposedRoutesQuery(Bounds(), 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
