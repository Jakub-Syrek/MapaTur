using System.Text;

using FluentAssertions;

using MapaTur.Infrastructure.Waterways;

using Xunit;

namespace MapaTur.Infrastructure.Tests.Waterways;

public sealed class OverpassWaterwayResponseParserTests
{
    [Fact]
    public void Parse_SplitsWaysIntoStreams_AndNodesIntoWaterfalls()
    {
        const string json = """
            {
              "elements": [
                {
                  "type": "way", "id": 11,
                  "tags": { "waterway": "stream", "name": "Roztoka" },
                  "geometry": [
                    { "lat": 49.20, "lon": 20.08 },
                    { "lat": 49.21, "lon": 20.09 }
                  ]
                },
                {
                  "type": "node", "id": 22, "lat": 49.2145, "lon": 20.0328,
                  "tags": { "waterway": "waterfall", "name": "Siklawa" }
                }
              ]
            }
            """;

        var result = OverpassWaterwayResponseParser.Parse(Encoding.UTF8.GetBytes(json));

        result.Streams.Should().ContainSingle();
        result.Streams[0].Id.Should().Be(11);
        result.Streams[0].Name.Should().Be("Roztoka");
        result.Streams[0].Geometry.Should().HaveCount(2);
        result.Waterfalls.Should().ContainSingle();
        result.Waterfalls[0].Name.Should().Be("Siklawa");
        result.Waterfalls[0].Position.Latitude.Should().BeApproximately(49.2145, 1e-9);
    }

    [Fact]
    public void Parse_SkipsWaysWithFewerThanTwoPoints()
    {
        const string json = """
            { "elements": [ { "type": "way", "id": 1, "geometry": [ { "lat": 49.2, "lon": 20.0 } ] } ] }
            """;

        var result = OverpassWaterwayResponseParser.Parse(Encoding.UTF8.GetBytes(json));

        result.Streams.Should().BeEmpty();
        result.Waterfalls.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        FluentActions.Invoking(() => OverpassWaterwayResponseParser.Parse("not json"u8))
            .Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Parse_MissingElements_Throws()
    {
        FluentActions.Invoking(() => OverpassWaterwayResponseParser.Parse("{}"u8))
            .Should().Throw<InvalidDataException>();
    }
}