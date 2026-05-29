using FluentAssertions;

using MapaTur.Application.Roads;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Roads;

public sealed class OverpassRoadQueryBuilderTests
{
    private static MapBounds Bounds() => new(new GeoPoint(49.10, 19.50), new GeoPoint(49.40, 20.40));

    [Fact]
    public void BuildRoadsQuery_SelectsHighwayWaysWithGeometry()
    {
        string query = OverpassRoadQueryBuilder.BuildRoadsQuery(Bounds());

        query.Should().Contain("[out:json]");
        query.Should().Contain("way[\"highway\"~");
        query.Should().Contain("out geom;");
    }

    [Fact]
    public void BuildRoadsQuery_EmbedsBoundingBoxInSwNeOrder()
    {
        string query = OverpassRoadQueryBuilder.BuildRoadsQuery(Bounds());

        query.Should().Contain("49.100000,19.500000,49.400000,20.400000");
    }

    [Fact]
    public void BuildRoadsQuery_ExcludesFootAndPathClasses()
    {
        string query = OverpassRoadQueryBuilder.BuildRoadsQuery(Bounds());

        query.Should().Contain("motorway");
        query.Should().NotContain("footway");
        query.Should().NotContain("path");
    }

    [Fact]
    public void BuildRoadsQuery_NonPositiveTimeout_Throws()
    {
        Action act = () => OverpassRoadQueryBuilder.BuildRoadsQuery(Bounds(), timeoutSeconds: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
