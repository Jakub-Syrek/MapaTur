using FluentAssertions;

using MapaTur.Application.Pois;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Pois;

public sealed class OverpassPoiQueryBuilderTests
{
    private static MapBounds Bounds() => new(new GeoPoint(49.1, 19.5), new GeoPoint(49.4, 20.4));

    [Fact]
    public void BuildPoiQuery_IncludesEveryPoiSelector()
    {
        string query = OverpassPoiQueryBuilder.BuildPoiQuery(Bounds());

        query.Should().Contain("\"tourism\"=\"alpine_hut\"");
        query.Should().Contain("\"tourism\"=\"wilderness_hut\"");
        query.Should().Contain("\"tourism\"=\"chalet\"");
        query.Should().Contain("\"tourism\"=\"viewpoint\"");
        query.Should().Contain("\"amenity\"=\"shelter\"");
    }

    [Fact]
    public void BuildPoiQuery_EmitsJsonWithTagsAndCenter()
    {
        string query = OverpassPoiQueryBuilder.BuildPoiQuery(Bounds());

        query.Should().Contain("[out:json]");
        query.Should().Contain("out tags center;");
    }

    [Fact]
    public void BuildPoiQuery_EmbedsTheBoundingBox()
    {
        string query = OverpassPoiQueryBuilder.BuildPoiQuery(Bounds());

        query.Should().Contain("49.100000,19.500000,49.400000,20.400000");
    }

    [Fact]
    public void BuildPoiQuery_RejectsNonPositiveTimeout()
    {
        Action act = () => OverpassPoiQueryBuilder.BuildPoiQuery(Bounds(), 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}