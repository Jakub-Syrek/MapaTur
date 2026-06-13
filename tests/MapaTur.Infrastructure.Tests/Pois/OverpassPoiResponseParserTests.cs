using System.Text;

using FluentAssertions;

using MapaTur.Domain.Pois;
using MapaTur.Infrastructure.Pois;

namespace MapaTur.Infrastructure.Tests.Pois;

public sealed class OverpassPoiResponseParserTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void Parse_NodeHut_BuildsPoiWithKindNameAndElevation()
    {
        string json = """
        { "elements": [
            { "type": "node", "id": 1, "lat": 49.23, "lon": 19.98,
              "tags": { "tourism": "alpine_hut", "name": "Murowaniec", "ele": "1500 m" } }
        ] }
        """;

        var pois = OverpassPoiResponseParser.Parse(Utf8(json));

        pois.Should().HaveCount(1);
        pois[0].Kind.Should().Be(PoiKind.Hut);
        pois[0].Name.Should().Be("Murowaniec");
        pois[0].Position.Latitude.Should().BeApproximately(49.23, 1e-9);
        pois[0].ElevationMeters.Should().BeApproximately(1500.0, 1e-6);
    }

    [Fact]
    public void Parse_WayViewpoint_UsesCenterPoint()
    {
        string json = """
        { "elements": [
            { "type": "way", "id": 7, "center": { "lat": 49.25, "lon": 19.93 },
              "tags": { "tourism": "viewpoint" } }
        ] }
        """;

        var pois = OverpassPoiResponseParser.Parse(Utf8(json));

        pois.Should().HaveCount(1);
        pois[0].Kind.Should().Be(PoiKind.Viewpoint);
        pois[0].Position.Longitude.Should().BeApproximately(19.93, 1e-9);
    }

    [Fact]
    public void Parse_SkipsElementsWithoutAPoiTag()
    {
        string json = """
        { "elements": [
            { "type": "node", "id": 2, "lat": 49.2, "lon": 19.9, "tags": { "amenity": "restaurant" } },
            { "type": "node", "id": 3, "lat": 49.2, "lon": 19.9, "tags": { "amenity": "shelter" } }
        ] }
        """;

        var pois = OverpassPoiResponseParser.Parse(Utf8(json));

        pois.Should().ContainSingle(p => p.Kind == PoiKind.Shelter);
    }

    [Fact]
    public void Parse_DeduplicatesById()
    {
        string json = """
        { "elements": [
            { "type": "node", "id": 5, "lat": 49.2, "lon": 19.9, "tags": { "tourism": "chalet" } },
            { "type": "node", "id": 5, "lat": 49.2, "lon": 19.9, "tags": { "tourism": "chalet" } }
        ] }
        """;

        OverpassPoiResponseParser.Parse(Utf8(json)).Should().HaveCount(1);
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        Action act = () => OverpassPoiResponseParser.Parse(Utf8("not json"));

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_NamelessParkingNearAPlace_IsNamedAfterTheNearestPlace()
    {
        // Brzeziny: OSM has a place=neighbourhood node, but the parking nearby has NO name tag.
        // The parser should borrow the nearest place's name → "Parking Brzeziny".
        string json = """
        { "elements": [
            { "type": "node", "id": 100, "lat": 49.2843, "lon": 20.0050, "tags": { "place": "neighbourhood", "name": "Brzeziny" } },
            { "type": "node", "id": 101, "lat": 49.2840, "lon": 20.0055, "tags": { "amenity": "parking", "fee": "yes" } }
        ] }
        """;

        var pois = OverpassPoiResponseParser.Parse(Utf8(json));

        var parking = pois.Single(p => p.Kind == PoiKind.Parking);
        parking.Name.Should().Be("Parking Brzeziny");
    }

    [Fact]
    public void Parse_NamedParking_KeepsItsOwnName()
    {
        string json = """
        { "elements": [
            { "type": "node", "id": 200, "lat": 49.28, "lon": 20.00, "tags": { "place": "village", "name": "Zakopane" } },
            { "type": "node", "id": 201, "lat": 49.281, "lon": 20.001, "tags": { "amenity": "parking", "name": "Parking pod Reglami" } }
        ] }
        """;

        var pois = OverpassPoiResponseParser.Parse(Utf8(json));

        pois.Single(p => p.Kind == PoiKind.Parking).Name.Should().Be("Parking pod Reglami");
    }

    [Fact]
    public void Parse_PlaceNode_IsNotItselfAPoi()
    {
        string json = """
        { "elements": [
            { "type": "node", "id": 300, "lat": 49.28, "lon": 20.00, "tags": { "place": "neighbourhood", "name": "Brzeziny" } }
        ] }
        """;

        OverpassPoiResponseParser.Parse(Utf8(json)).Should().BeEmpty("a place node is naming context, not a POI");
    }

    [Fact]
    public void Parse_NamelessParkingFarFromAnyPlace_StaysGenericParking()
    {
        string json = """
        { "elements": [
            { "type": "node", "id": 400, "lat": 49.10, "lon": 20.50, "tags": { "place": "village", "name": "Faraway" } },
            { "type": "node", "id": 401, "lat": 49.28, "lon": 20.00, "tags": { "amenity": "parking" } }
        ] }
        """;

        var pois = OverpassPoiResponseParser.Parse(Utf8(json));

        pois.Single(p => p.Kind == PoiKind.Parking).Name.Should().Be("Parking");
    }

}