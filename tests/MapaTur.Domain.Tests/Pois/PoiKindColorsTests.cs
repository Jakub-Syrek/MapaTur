using FluentAssertions;

using MapaTur.Domain.Pois;

namespace MapaTur.Domain.Tests.Pois;

public sealed class PoiKindColorsTests
{
    [Theory]
    [InlineData(PoiKind.Hut, "DC2626")]
    [InlineData(PoiKind.WildernessHut, "EA580C")]
    [InlineData(PoiKind.Chalet, "B45309")]
    [InlineData(PoiKind.Shelter, "0D9488")]
    [InlineData(PoiKind.Viewpoint, "2563EB")]
    [InlineData(PoiKind.Parking, "475569")]
    [InlineData(PoiKind.Pass, "CA8A04")]
    public void ToHex_KnownKind_ReturnsExpectedHex(PoiKind kind, string expected)
    {
        PoiKindColors.ToHex(kind).Should().Be(expected);
    }

    [Fact]
    public void ToHex_UnknownEnumValue_FallsBackToHutRed()
    {
        PoiKindColors.ToHex((PoiKind)999).Should().Be("DC2626");
    }
}