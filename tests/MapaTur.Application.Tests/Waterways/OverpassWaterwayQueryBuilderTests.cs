using FluentAssertions;

using MapaTur.Application.Waterways;
using MapaTur.Domain.Geography;

using Xunit;

namespace MapaTur.Application.Tests.Waterways;

public sealed class OverpassWaterwayQueryBuilderTests
{
    private static readonly MapBounds Tatry = new(new GeoPoint(49.10, 19.50), new GeoPoint(49.40, 20.40));

    [Fact]
    public void Query_SelectsStreamAndRiverWays_AndWaterfallNodes()
    {
        string q = OverpassWaterwayQueryBuilder.BuildWaterwaysQuery(Tatry);

        q.Should().Contain("way[\"waterway\"~\"^(river|stream)$\"]");
        q.Should().Contain("node[\"waterway\"=\"waterfall\"]");
        q.Should().Contain("out geom;");
    }

    [Fact]
    public void Query_FormatsBboxInvariantly()
    {
        string q = OverpassWaterwayQueryBuilder.BuildWaterwaysQuery(Tatry);

        q.Should().Contain("(49.100000,19.500000,49.400000,20.400000)");
    }

    [Fact]
    public void Query_RejectsNonPositiveTimeout()
    {
        FluentActions.Invoking(() => OverpassWaterwayQueryBuilder.BuildWaterwaysQuery(Tatry, timeoutSeconds: 0))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}