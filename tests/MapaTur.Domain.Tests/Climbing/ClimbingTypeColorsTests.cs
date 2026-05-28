using FluentAssertions;

using MapaTur.Domain.Climbing;

namespace MapaTur.Domain.Tests.Climbing;

public sealed class ClimbingTypeColorsTests
{
    [Theory]
    [InlineData(ClimbingType.SportRoute, "E11D48")]
    [InlineData(ClimbingType.TradRoute, "2563EB")]
    [InlineData(ClimbingType.MultiPitch, "9333EA")]
    [InlineData(ClimbingType.Boulder, "F97316")]
    [InlineData(ClimbingType.Crag, "475569")]
    [InlineData(ClimbingType.Cliff, "6B7280")]
    public void ToHex_KnownType_ReturnsExpectedHex(ClimbingType type, string expected)
    {
        ClimbingTypeColors.ToHex(type).Should().Be(expected);
    }

    [Fact]
    public void ToHex_Unspecified_FallsBackToSportRed()
    {
        ClimbingTypeColors.ToHex(ClimbingType.Unspecified).Should().Be("E11D48");
    }

    [Fact]
    public void ToHex_UnknownEnumValue_FallsBackToSportRed()
    {
        ClimbingTypeColors.ToHex((ClimbingType)999).Should().Be("E11D48");
    }
}