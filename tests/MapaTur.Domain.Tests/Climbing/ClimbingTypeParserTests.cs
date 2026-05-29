using FluentAssertions;

using MapaTur.Domain.Climbing;

namespace MapaTur.Domain.Tests.Climbing;

public sealed class ClimbingTypeParserTests
{
    [Fact]
    public void Parse_PrefersExplicitMultiPitch()
    {
        var type = ClimbingTypeParser.Parse(
            climbingTag: "route",
            climbingSportTag: "yes",
            climbingTradTag: null,
            climbingMultiPitchTag: "yes",
            climbingBoulderTag: null,
            naturalTag: null);

        type.Should().Be(ClimbingType.MultiPitch);
    }

    [Fact]
    public void Parse_RecognisesBouldering()
    {
        var type = ClimbingTypeParser.Parse(
            climbingTag: "boulder",
            climbingSportTag: null,
            climbingTradTag: null,
            climbingMultiPitchTag: null,
            climbingBoulderTag: null,
            naturalTag: null);

        type.Should().Be(ClimbingType.Boulder);
    }

    [Fact]
    public void Parse_FallsBackToSportRouteForRouteTag()
    {
        var type = ClimbingTypeParser.Parse(
            climbingTag: "route",
            climbingSportTag: null,
            climbingTradTag: null,
            climbingMultiPitchTag: null,
            climbingBoulderTag: null,
            naturalTag: null);

        type.Should().Be(ClimbingType.SportRoute);
    }

    [Fact]
    public void Parse_RecognisesCragWhenTagIsArea()
    {
        var type = ClimbingTypeParser.Parse(
            climbingTag: "area",
            climbingSportTag: null,
            climbingTradTag: null,
            climbingMultiPitchTag: null,
            climbingBoulderTag: null,
            naturalTag: null);

        type.Should().Be(ClimbingType.Crag);
    }

    [Fact]
    public void Parse_DetectsCliffViaNaturalTag()
    {
        var type = ClimbingTypeParser.Parse(
            climbingTag: null,
            climbingSportTag: null,
            climbingTradTag: null,
            climbingMultiPitchTag: null,
            climbingBoulderTag: null,
            naturalTag: "cliff");

        type.Should().Be(ClimbingType.Cliff);
    }

    [Fact]
    public void Parse_ReturnsUnspecifiedWhenNothingMatches()
    {
        var type = ClimbingTypeParser.Parse(
            climbingTag: null,
            climbingSportTag: null,
            climbingTradTag: null,
            climbingMultiPitchTag: null,
            climbingBoulderTag: null,
            naturalTag: null);

        type.Should().Be(ClimbingType.Unspecified);
    }
}