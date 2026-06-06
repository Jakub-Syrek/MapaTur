using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of the avalanche-style slope-steepness shading: terrain slope angle is classified into the
/// eight legend bands (0-20, 20-30 … 80-90°) and each band maps to a fixed colour on the green→red→violet
/// ramp. Both are the single source of truth the terrain shader mirrors.
/// </summary>
public sealed class SlopeShadingTests
{
    [Fact]
    public void BandCount_IsEight()
    {
        SlopeClassification.BandCount.Should().Be(8);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(10.0, 0)]
    [InlineData(19.999, 0)]   // first band is the wide 0-20° step
    [InlineData(20.0, 1)]     // boundary belongs to the upper band
    [InlineData(29.999, 1)]
    [InlineData(30.0, 2)]
    [InlineData(45.0, 3)]     // 40-50°
    [InlineData(55.0, 4)]
    [InlineData(65.0, 5)]
    [InlineData(75.0, 6)]
    [InlineData(80.0, 7)]     // 80-90°
    [InlineData(90.0, 7)]
    public void Classify_MapsAngleToExpectedBand(double slopeDegrees, int expectedBand)
    {
        SlopeClassification.Classify(slopeDegrees).Should().Be(expectedBand);
    }

    [Theory]
    [InlineData(-5.0, 0)]     // below range clamps to the gentlest band
    [InlineData(120.0, 7)]    // above range clamps to the steepest band
    public void Classify_ClampsOutOfRangeAngles(double slopeDegrees, int expectedBand)
    {
        SlopeClassification.Classify(slopeDegrees).Should().Be(expectedBand);
    }

    [Fact]
    public void Palette_HasOneColourPerBand()
    {
        for (int band = 0; band < SlopeClassification.BandCount; band++)
        {
            Vector3 c = SlopePalette.ColorFor(band);
            c.X.Should().BeInRange(0f, 1f);
            c.Y.Should().BeInRange(0f, 1f);
            c.Z.Should().BeInRange(0f, 1f);
        }
    }

    [Fact]
    public void Palette_GentlestBand_IsGreenDominant()
    {
        // 0-20° = safe/gentle = green (g greater than r and b).
        Vector3 c = SlopePalette.ColorFor(0);

        c.Y.Should().BeGreaterThan(c.X);
        c.Y.Should().BeGreaterThan(c.Z);
    }

    [Fact]
    public void Palette_SteepestBand_IsNotGreenDominant()
    {
        // 80-90° = extreme = the hot/violet end, never green.
        Vector3 c = SlopePalette.ColorFor(7);

        (c.Y > c.X && c.Y > c.Z).Should().BeFalse();
    }

    [Fact]
    public void Palette_ClampsBandIndexIntoRange()
    {
        SlopePalette.ColorFor(-3).Should().Be(SlopePalette.ColorFor(0));
        SlopePalette.ColorFor(99).Should().Be(SlopePalette.ColorFor(SlopeClassification.BandCount - 1));
    }
}
