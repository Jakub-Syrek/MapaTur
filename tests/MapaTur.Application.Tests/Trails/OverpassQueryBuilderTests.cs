using FluentAssertions;
using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Trails;

public sealed class OverpassQueryBuilderTests
{
    [Fact]
    public void BuildHikingTrailsQuery_EmbedsBoundsInInvariantCulture()
    {
        var bounds = new MapBounds(
            new GeoPoint(49.0, 19.5),
            new GeoPoint(49.5, 20.5));

        string query = OverpassQueryBuilder.BuildHikingTrailsQuery(bounds);

        query.Should().Contain("relation[\"route\"=\"hiking\"](49.000000,19.500000,49.500000,20.500000)");
        query.Should().Contain("[out:json][timeout:60]");
    }

    [Fact]
    public void BuildHikingTrailsQuery_HonoursCustomTimeout()
    {
        var bounds = new MapBounds(
            new GeoPoint(49.0, 19.5),
            new GeoPoint(49.5, 20.5));

        string query = OverpassQueryBuilder.BuildHikingTrailsQuery(bounds, timeoutSeconds: 15);

        query.Should().Contain("[timeout:15]");
    }

    [Fact]
    public void BuildHikingTrailsQuery_RejectsNonPositiveTimeout()
    {
        var bounds = new MapBounds(
            new GeoPoint(49.0, 19.5),
            new GeoPoint(49.5, 20.5));

        var act = () => OverpassQueryBuilder.BuildHikingTrailsQuery(bounds, timeoutSeconds: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}