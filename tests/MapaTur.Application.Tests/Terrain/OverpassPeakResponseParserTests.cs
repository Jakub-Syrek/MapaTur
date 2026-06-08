using System.Text;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class OverpassPeakResponseParserTests
{
    private static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void Parse_NodePeak_BuildsOsmPeakWithNameElevationAndPosition()
    {
        string json = """
        { "elements": [
            { "type": "node", "id": 42, "lat": 49.1795, "lon": 20.0882,
              "tags": { "natural": "peak", "name": "Rysy", "ele": "2501" } }
        ] }
        """;

        var peaks = OverpassPeakResponseParser.Parse(Utf8(json));

        peaks.Should().HaveCount(1);
        peaks[0].Id.Should().Be(42);
        peaks[0].Name.Should().Be("Rysy");
        peaks[0].Position.Latitude.Should().BeApproximately(49.1795, 1e-9);
        peaks[0].Position.Longitude.Should().BeApproximately(20.0882, 1e-9);
        peaks[0].ElevationMeters.Should().BeApproximately(2501.0, 1e-6);
    }

    [Fact]
    public void Parse_PrefersPolishNameOverDefaultName()
    {
        string json = """
        { "elements": [
            { "type": "node", "id": 1, "lat": 49.164, "lon": 20.134,
              "tags": { "natural": "peak", "name": "Gerlachovský štít", "name:pl": "Gerlach", "ele": "2655" } }
        ] }
        """;

        var peaks = OverpassPeakResponseParser.Parse(Utf8(json));

        peaks[0].Name.Should().Be("Gerlach");
    }

    [Fact]
    public void Parse_MissingElevation_YieldsNull()
    {
        string json = """
        { "elements": [
            { "type": "node", "id": 2, "lat": 49.2, "lon": 20.0,
              "tags": { "natural": "peak", "name": "Bezimienna" } }
        ] }
        """;

        var peaks = OverpassPeakResponseParser.Parse(Utf8(json));

        peaks[0].ElevationMeters.Should().BeNull();
    }

    [Fact]
    public void Parse_MissingName_YieldsEmptyName()
    {
        string json = """
        { "elements": [
            { "type": "node", "id": 3, "lat": 49.2, "lon": 20.0,
              "tags": { "natural": "peak", "ele": "1500" } }
        ] }
        """;

        var peaks = OverpassPeakResponseParser.Parse(Utf8(json));

        peaks.Should().HaveCount(1);
        peaks[0].Name.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ElevationWithUnitSuffix_ParsesLeadingNumber()
    {
        string json = """
        { "elements": [
            { "type": "node", "id": 4, "lat": 49.25, "lon": 19.93,
              "tags": { "natural": "peak", "name": "Giewont", "ele": "1894 m" } }
        ] }
        """;

        var peaks = OverpassPeakResponseParser.Parse(Utf8(json));

        peaks[0].ElevationMeters.Should().BeApproximately(1894.0, 1e-6);
    }

    [Fact]
    public void Parse_WayWithCenter_UsesCenterPoint()
    {
        string json = """
        { "elements": [
            { "type": "way", "id": 5, "center": { "lat": 49.18, "lon": 20.06 },
              "tags": { "natural": "peak", "name": "Szeroka", "ele": "2210" } }
        ] }
        """;

        var peaks = OverpassPeakResponseParser.Parse(Utf8(json));

        peaks.Should().HaveCount(1);
        peaks[0].Position.Latitude.Should().BeApproximately(49.18, 1e-9);
        peaks[0].Position.Longitude.Should().BeApproximately(20.06, 1e-9);
    }

    [Fact]
    public void Parse_DeduplicatesById()
    {
        string json = """
        { "elements": [
            { "type": "node", "id": 7, "lat": 49.2, "lon": 20.0, "tags": { "natural": "peak", "name": "A", "ele": "1600" } },
            { "type": "node", "id": 7, "lat": 49.2, "lon": 20.0, "tags": { "natural": "peak", "name": "A", "ele": "1600" } }
        ] }
        """;

        var peaks = OverpassPeakResponseParser.Parse(Utf8(json));

        peaks.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_InvalidJson_Throws()
    {
        Action act = () => OverpassPeakResponseParser.Parse(Utf8("{ not json"));

        act.Should().Throw<InvalidDataException>();
    }
}