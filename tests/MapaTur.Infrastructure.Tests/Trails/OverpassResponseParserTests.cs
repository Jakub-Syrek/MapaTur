using FluentAssertions;
using MapaTur.Domain.Trails;
using MapaTur.Infrastructure.Trails.Overpass;

namespace MapaTur.Infrastructure.Tests.Trails;

public sealed class OverpassResponseParserTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "testdata", "trails", "overpass-tatry-sample.json");

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

        // Way 200001 contributes 3 points, way 200002 contributes 2, total 5.
        czerwone.Geometry.Should().HaveCount(5);
        czerwone.Name.Should().Be("Czerwone Wierchy via Kasprowy");
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
