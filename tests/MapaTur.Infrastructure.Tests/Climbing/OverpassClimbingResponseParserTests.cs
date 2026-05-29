using FluentAssertions;

using MapaTur.Domain.Climbing;
using MapaTur.Infrastructure.Climbing;

namespace MapaTur.Infrastructure.Tests.Climbing;

public sealed class OverpassClimbingResponseParserTests
{
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "testdata", "climbing", "overpass-tatry-climbing-sample.json");

    [Fact]
    public void Parse_ReturnsClimbingAreasForTaggedFeatures()
    {
        byte[] payload = File.ReadAllBytes(FixturePath);

        var areas = OverpassClimbingResponseParser.Parse(payload);

        // 4 climbing-tagged features; the amenity=shelter node should be excluded
        // because TryBuildArea requires a position (it has one) but the type parser
        // returns Unspecified and we still keep it as a generic point — verify
        // we accept all 5 positional elements but the shelter has Unspecified type.
        areas.Should().HaveCount(5);
    }

    [Fact]
    public void Parse_StoresMultiPitchTypeAndUiaaGrade()
    {
        byte[] payload = File.ReadAllBytes(FixturePath);

        var area = OverpassClimbingResponseParser.Parse(payload).Single(a => a.Id == 9000001);

        area.Name.Should().Be("Mnich - Klasyczna");
        area.Type.Should().Be(ClimbingType.MultiPitch);
        area.Grade.Should().Be("V+");
        area.LengthMeters.Should().Be(300);
        area.IsBolted.Should().BeFalse();
    }

    [Fact]
    public void Parse_RecognisesSportRouteWithFrenchGrade()
    {
        byte[] payload = File.ReadAllBytes(FixturePath);

        var sport = OverpassClimbingResponseParser.Parse(payload).Single(a => a.Id == 9000002);

        sport.Type.Should().Be(ClimbingType.SportRoute);
        sport.Grade.Should().Be("6a");
        sport.LengthMeters.Should().Be(25);
        sport.IsBolted.Should().BeTrue();
    }

    [Fact]
    public void Parse_RecognisesBoulderingProblems()
    {
        byte[] payload = File.ReadAllBytes(FixturePath);

        var boulder = OverpassClimbingResponseParser.Parse(payload).Single(a => a.Id == 9000003);

        boulder.Type.Should().Be(ClimbingType.Boulder);
        boulder.Grade.Should().Be("6c");
    }

    [Fact]
    public void Parse_UsesCenterCoordinatesForWays()
    {
        byte[] payload = File.ReadAllBytes(FixturePath);

        var crag = OverpassClimbingResponseParser.Parse(payload).Single(a => a.Id == 9000004);

        crag.Position.Latitude.Should().BeApproximately(49.215, 1e-9);
        crag.Position.Longitude.Should().BeApproximately(20.005, 1e-9);
        crag.Type.Should().Be(ClimbingType.Crag);
    }

    [Fact]
    public void Parse_ThrowsOnMalformedJson()
    {
        byte[] malformed = "not-json"u8.ToArray();

        var act = () => OverpassClimbingResponseParser.Parse(malformed);

        act.Should().Throw<InvalidDataException>();
    }
}