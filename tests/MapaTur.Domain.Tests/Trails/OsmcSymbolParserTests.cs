using FluentAssertions;

using MapaTur.Domain.Trails;

namespace MapaTur.Domain.Tests.Trails;

public sealed class OsmcSymbolParserTests
{
    [Theory]
    [InlineData("red:white:red_stripe", PttkColor.Red)]
    [InlineData("blue:white:blue_stripe", PttkColor.Blue)]
    [InlineData("green:white:green_stripe", PttkColor.Green)]
    [InlineData("yellow:white:yellow_stripe", PttkColor.Yellow)]
    [InlineData("black:white:black_stripe", PttkColor.Black)]
    public void Parse_RecognisesPttkColors(string osmc, PttkColor expected)
    {
        var marking = OsmcSymbolParser.Parse(osmc);

        marking.Color.Should().Be(expected);
        marking.OsmcRaw.Should().Be(osmc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("orange:white:dot")]
    [InlineData(":missing:colour")]
    public void Parse_ReturnsNoneForUnknownOrEmpty(string? input)
    {
        var marking = OsmcSymbolParser.Parse(input);

        marking.Color.Should().Be(PttkColor.None);
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        OsmcSymbolParser.Parse("RED:white").Color.Should().Be(PttkColor.Red);
        OsmcSymbolParser.Parse(" Red : white ").Color.Should().Be(PttkColor.Red);
    }

    [Fact]
    public void ToHex_ReturnsExpectedHexForEachColor()
    {
        OsmcSymbolParser.ToHex(PttkColor.Red).Should().Be("#DC2626");
        OsmcSymbolParser.ToHex(PttkColor.Blue).Should().Be("#2563EB");
        OsmcSymbolParser.ToHex(PttkColor.None).Should().Be("#94A3B8");
    }
}