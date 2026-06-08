using System.Globalization;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

public sealed class OverpassPeakQueryBuilderTests
{
    private static readonly MapBounds TatraBounds = new(
        new GeoPoint(49.10, 19.70),
        new GeoPoint(49.32, 20.40));

    [Fact]
    public void BuildPeakQuery_RequestsNaturalPeakWithinBbox()
    {
        string query = OverpassPeakQueryBuilder.BuildPeakQuery(TatraBounds);

        query.Should().Contain("[out:json]");
        query.Should().Contain("nwr[\"natural\"=\"peak\"](49.100000,19.700000,49.320000,20.400000);");
        query.Should().Contain("out tags center;");
    }

    [Fact]
    public void BuildPeakQuery_EmitsBboxAsSouthWestNorthEast()
    {
        string query = OverpassPeakQueryBuilder.BuildPeakQuery(TatraBounds);

        // Overpass bbox order is south,west,north,east.
        query.Should().Contain("(49.100000,19.700000,49.320000,20.400000)");
    }

    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    public void BuildPeakQuery_HonoursTimeout(int timeoutSeconds)
    {
        string query = OverpassPeakQueryBuilder.BuildPeakQuery(TatraBounds, timeoutSeconds);

        query.Should().Contain($"[timeout:{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}]");
    }
}