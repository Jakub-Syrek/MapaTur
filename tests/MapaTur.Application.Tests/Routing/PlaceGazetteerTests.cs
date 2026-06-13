using FluentAssertions;

using MapaTur.Application.Routing;
using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Pois;
using MapaTur.Domain.Routing;

namespace MapaTur.Application.Tests.Routing;

/// <summary>
/// The searchable place picker: peaks, lakes and POIs are unioned into one ranked list of route
/// waypoints, searchable by name regardless of case OR Polish/Slovak diacritics ("rysy" finds "Rysy",
/// "lomnica" finds "Łomnica", "strbske" finds "Štrbské pleso"). Prefix matches outrank mid-word ones,
/// then height breaks ties so the prominent places float up.
/// </summary>
public sealed class PlaceGazetteerTests
{
    private static readonly TerrainPeak[] Peaks =
    {
        new(new GeoPoint(49.179, 20.088), 2501, "Rysy", 2503),
        new(new GeoPoint(49.164, 20.134), 2655, "Gerlach", 2655),
        new(new GeoPoint(49.195, 20.211), 2634, "Łomnica", 2634),
    };

    private static readonly MountainLake[] Lakes =
    {
        new("Morskie Oko", 1395, new[] { new GeoPoint(49.20, 20.07), new GeoPoint(49.20, 20.072), new GeoPoint(49.198, 20.071) }),
        new("Štrbské pleso", 1346, new[] { new GeoPoint(49.12, 20.06), new GeoPoint(49.12, 20.062), new GeoPoint(49.118, 20.061) }),
    };

    private static readonly MountainPoi[] Pois =
    {
        new(1, "Schronisko nad Morskim Okiem", new GeoPoint(49.201, 20.073), PoiKind.Hut, 1410),
        new(2, "", new GeoPoint(49.30, 20.00), PoiKind.Shelter), // unnamed — must be dropped
    };

    private static PlaceGazetteer Gazetteer() => new(Peaks, Lakes, Pois);

    [Fact]
    public void All_UnionsNamedPeaksLakesAndPois_DropsUnnamed()
    {
        IReadOnlyList<RouteWaypoint> all = Gazetteer().All;

        all.Should().HaveCount(6, "3 peaks + 2 lakes + 1 named POI; the unnamed shelter is dropped");
        all.Select(w => w.Kind).Should().Contain(new[] { WaypointKind.Peak, WaypointKind.Lake, WaypointKind.Hut });
    }

    [Fact]
    public void Search_IsCaseInsensitive()
    {
        Gazetteer().Search("rysy", 10).Should().ContainSingle(w => w.Name == "Rysy");
    }

    [Fact]
    public void Search_IgnoresPolishDiacritics()
    {
        Gazetteer().Search("lomnica", 10).Should().ContainSingle(w => w.Name == "Łomnica");
    }

    [Fact]
    public void Search_IgnoresSlovakDiacritics()
    {
        Gazetteer().Search("strbske", 10).Should().ContainSingle(w => w.Name == "Štrbské pleso");
    }

    [Fact]
    public void Search_MatchesMidWord()
    {
        // "morski" prefixes "Morskie Oko" and appears mid-name in "...nad Morskim Okiem".
        var names = Gazetteer().Search("morski", 10).Select(w => w.Name).ToList();
        names.Should().Contain("Morskie Oko");
        names.Should().Contain("Schronisko nad Morskim Okiem");
    }

    [Fact]
    public void Search_PrefixMatch_OutranksMidWordMatch()
    {
        // "mor" prefixes "Morskie Oko" but only appears mid-name in the hut ("...Morskim..." after a space).
        var results = Gazetteer().Search("mor", 10);
        results[0].Name.Should().Be("Morskie Oko", "a name starting with the query ranks first");
    }

    [Fact]
    public void Search_EmptyQuery_ReturnsMostProminentFirst()
    {
        var results = Gazetteer().Search("  ", 3);

        results.Should().HaveCount(3);
        results[0].Name.Should().Be("Gerlach", "the highest place leads an empty query");
        results.Select(w => w.ElevationMeters).Should().BeInDescendingOrder();
    }

    [Fact]
    public void Search_RespectsTheMaxCount()
    {
        Gazetteer().Search("", 2).Should().HaveCount(2);
    }

    [Fact]
    public void Search_NoMatch_ReturnsEmpty()
    {
        Gazetteer().Search("xyzzy", 10).Should().BeEmpty();
    }

    [Fact]
    public void Waypoint_CarriesLocationAndElevation()
    {
        RouteWaypoint rysy = Gazetteer().Search("rysy", 1).Single();

        rysy.Location.Latitude.Should().BeApproximately(49.179, 1e-3);
        rysy.ElevationMeters.Should().Be(2503, "the peak's published label elevation is preferred");
    }
}