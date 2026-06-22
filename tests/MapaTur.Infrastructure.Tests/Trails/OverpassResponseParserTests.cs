using FluentAssertions;

using MapaTur.Domain.Trails;
using MapaTur.Infrastructure.Trails.Overpass;

namespace MapaTur.Infrastructure.Tests.Trails;

public sealed class OverpassResponseParserTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "testdata", "trails", "overpass-tatry-sample.json");

    [Fact]
    public void ParseWays_EmitsOneTrailPerWayWithGeometry()
    {
        const string json = """
        {"elements":[
          {"type":"way","id":501,"tags":{"sac_scale":"demanding_mountain_hiking","name":"Orla Perć"},
           "geometry":[{"lat":49.22,"lon":20.01},{"lat":49.221,"lon":20.012},{"lat":49.222,"lon":20.014}]},
          {"type":"way","id":502,"tags":{"highway":"via_ferrata"},
           "geometry":[{"lat":49.20,"lon":20.05}]},
          {"type":"way","id":503,"tags":{"via_ferrata_scale":"3"},
           "geometry":[{"lat":49.18,"lon":20.06},{"lat":49.181,"lon":20.061}]}
        ]}
        """;

        var routes = OverpassResponseParser.ParseWays(System.Text.Encoding.UTF8.GetBytes(json));

        // Way 502 has a single point (< 2) and is dropped; 501 and 503 remain.
        routes.Should().HaveCount(2);
        routes.Should().Contain(t => t.Id == 501 && t.Name == "Orla Perć" && t.Geometry.Count == 3);
        routes.Should().Contain(t => t.Id == 503 && t.Name == "Via ferrata"); // named from the via_ferrata_scale tag
    }

    [Fact]
    public void Parse_ReturnsTrailsForRelationsWithGeometry()
    {
        byte[] payload = File.ReadAllBytes(FixturePath);

        var trails = OverpassResponseParser.Parse(payload);

        // Two relations have ways with geometry; the empty relation should be filtered out.
        trails.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_StitchesMemberWaysIntoSingleGeometry()
    {
        byte[] payload = File.ReadAllBytes(FixturePath);

        var trails = OverpassResponseParser.Parse(payload);
        var czerwone = trails.Single(trail => trail.Id == 100001);

        // Way 200001 contributes 3 points; way 200002's first point is the same
        // node as 200001's last (a shared junction in OSM) so the stitcher drops
        // the duplicate and adds 1 fresh point. 3 + 1 = 4.
        czerwone.Geometry.Should().HaveCount(4);
        czerwone.Name.Should().Be("Czerwone Wierchy via Kasprowy");
    }

    [Fact]
    public void Parse_DisconnectedWays_SplitsIntoSeparateTrails()
    {
        const string payload = """
        {
          "elements": [
            {
              "type": "relation",
              "id": 900,
              "members": [
                { "type": "way", "ref": 1 },
                { "type": "way", "ref": 2 }
              ],
              "tags": { "route": "hiking", "name": "Disconnected", "osmc:symbol": "red:white:red_stripe" }
            },
            { "type": "way", "id": 1, "geometry": [
              { "lat": 49.10, "lon": 19.10 },
              { "lat": 49.11, "lon": 19.11 }
            ]},
            { "type": "way", "id": 2, "geometry": [
              { "lat": 49.50, "lon": 20.50 },
              { "lat": 49.51, "lon": 20.51 }
            ]}
          ]
        }
        """;

        var trails = OverpassResponseParser.Parse(System.Text.Encoding.UTF8.GetBytes(payload));

        // Both ways share the relation's name + colour but live in separate
        // continuous polylines — no straight "jumper" line between them.
        trails.Should().HaveCount(2);
        trails.Should().AllSatisfy(t => t.Name.Should().Be("Disconnected"));
        trails.Select(t => t.Geometry.Count).Should().AllBeEquivalentTo(2);
    }

    [Fact]
    public void Parse_WayInReverseOrder_StillStitchesAsOneSegment()
    {
        // Relation member ways come out of OSM with no guaranteed orientation;
        // the parser must accept a way that runs end-first if its tail matches
        // the current segment's tail.
        const string payload = """
        {
          "elements": [
            {
              "type": "relation",
              "id": 800,
              "members": [
                { "type": "way", "ref": 1 },
                { "type": "way", "ref": 2 }
              ],
              "tags": { "route": "hiking", "name": "Reverse-friendly" }
            },
            { "type": "way", "id": 1, "geometry": [
              { "lat": 49.10, "lon": 19.10 },
              { "lat": 49.11, "lon": 19.11 }
            ]},
            { "type": "way", "id": 2, "geometry": [
              { "lat": 49.13, "lon": 19.13 },
              { "lat": 49.12, "lon": 19.12 },
              { "lat": 49.11, "lon": 19.11 }
            ]}
          ]
        }
        """;

        var trails = OverpassResponseParser.Parse(System.Text.Encoding.UTF8.GetBytes(payload));

        trails.Should().HaveCount(1);
        trails[0].Geometry.Should().HaveCount(4);
    }

    [Fact]
    public void Parse_RecognisesPttkColorsFromTags()
    {
        byte[] payload = File.ReadAllBytes(FixturePath);

        var trails = OverpassResponseParser.Parse(payload);

        trails.Single(trail => trail.Id == 100001).PrimaryColor.Should().Be(PttkColor.Red);
        trails.Single(trail => trail.Id == 100002).PrimaryColor.Should().Be(PttkColor.Blue);
    }

    [Fact]
    public void Parse_ThrowsOnMalformedJson()
    {
        byte[] malformed = "not-json"u8.ToArray();

        var act = () => OverpassResponseParser.Parse(malformed);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_ThrowsWhenElementsArrayMissing()
    {
        byte[] empty = "{\"version\":0.6}"u8.ToArray();

        var act = () => OverpassResponseParser.Parse(empty);

        act.Should().Throw<InvalidDataException>();
    }
}