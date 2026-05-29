using System.Text;

using FluentAssertions;

using MapaTur.Infrastructure.Roads;

namespace MapaTur.Infrastructure.Tests.Roads;

public sealed class OverpassRoadResponseParserTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void Parse_HighwayWay_ProducesOneRoadWithGeometryAndName()
    {
        string json = """
        {"elements":[
          {"type":"way","id":42,"tags":{"highway":"track","name":"Droga pod Reglami"},
           "geometry":[{"lat":49.27,"lon":19.95},{"lat":49.28,"lon":19.96},{"lat":49.29,"lon":19.97}]}
        ]}
        """;

        var roads = OverpassRoadResponseParser.Parse(Utf8(json));

        roads.Should().HaveCount(1);
        roads[0].Id.Should().Be(42);
        roads[0].Name.Should().Be("Droga pod Reglami");
        roads[0].Geometry.Should().HaveCount(3);
        roads[0].Markings.Should().BeEmpty();
    }

    [Fact]
    public void Parse_WayWithFewerThanTwoPoints_IsSkipped()
    {
        string json = """
        {"elements":[
          {"type":"way","id":1,"geometry":[{"lat":49.27,"lon":19.95}]}
        ]}
        """;

        OverpassRoadResponseParser.Parse(Utf8(json)).Should().BeEmpty();
    }

    [Fact]
    public void Parse_IgnoresNonWayElements()
    {
        string json = """
        {"elements":[
          {"type":"node","id":7,"lat":49.27,"lon":19.95},
          {"type":"way","id":2,"geometry":[{"lat":49.1,"lon":19.5},{"lat":49.2,"lon":19.6}]}
        ]}
        """;

        var roads = OverpassRoadResponseParser.Parse(Utf8(json));

        roads.Should().HaveCount(1);
        roads[0].Id.Should().Be(2);
    }

    [Fact]
    public void Parse_MissingElementsArray_Throws()
    {
        Action act = () => OverpassRoadResponseParser.Parse(Utf8("{\"x\":1}"));

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        Action act = () => OverpassRoadResponseParser.Parse(Utf8("not json"));

        act.Should().Throw<InvalidDataException>();
    }
}
