using FluentAssertions;
using MapaTur.Domain.Climbing;
using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Tests.Climbing;

public sealed class ClimbingAreaTests
{
    [Fact]
    public void Constructor_AcceptsMinimalArguments()
    {
        var area = new ClimbingArea(
            id: 100,
            name: "Mnich",
            position: new GeoPoint(49.196, 20.075),
            type: ClimbingType.MultiPitch);

        area.Id.Should().Be(100);
        area.Name.Should().Be("Mnich");
        area.Grade.Should().BeNull();
        area.LengthMeters.Should().BeNull();
        area.IsBolted.Should().BeNull();
    }

    [Fact]
    public void Constructor_RejectsNegativeLength()
    {
        var act = () => new ClimbingArea(
            id: 1,
            name: "x",
            position: new GeoPoint(49.0, 19.0),
            type: ClimbingType.SportRoute,
            lengthMeters: -1);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("lengthMeters");
    }

    [Fact]
    public void Constructor_RetainsAllOptionalMetadata()
    {
        var area = new ClimbingArea(
            id: 42,
            name: "Sample",
            position: new GeoPoint(49.2, 20.0),
            type: ClimbingType.SportRoute,
            grade: "6a",
            lengthMeters: 25,
            isBolted: true);

        area.Grade.Should().Be("6a");
        area.LengthMeters.Should().Be(25);
        area.IsBolted.Should().BeTrue();
    }
}