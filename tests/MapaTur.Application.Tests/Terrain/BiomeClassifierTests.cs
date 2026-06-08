using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of the elevation-zone biome classifier: terrain is sorted into the alpine zonation bands
/// (meadow/hala → scree/piargi → bare rock → snow → ice) from elevation, slope angle and aspect
/// (northness). It extends the steep-rock mask and is the single source of truth the terrain shader
/// mirrors, paired with a fixed <see cref="BiomePalette"/>.
/// </summary>
public sealed class BiomeClassifierTests
{
    // --- Slope dominates: steep faces are bare rock at any elevation (continues the rock mask) ---

    [Theory]
    [InlineData(1500.0)]   // low steep face
    [InlineData(2300.0)]   // high steep face — steepness overrides the snow zone
    public void Classify_SteepFace_IsRock_RegardlessOfElevation(double elevationM)
    {
        BiomeClassifier.Classify(elevationM, slopeDegrees: 70.0, northness: 0.0)
            .Should().Be(Biome.Rock);
    }

    [Fact]
    public void Classify_RockSlopeBoundary_BelongsToRock()
    {
        BiomeThresholds t = BiomeThresholds.Default;

        BiomeClassifier.Classify(1500.0, t.RockSlopeDegrees, northness: 0.0)
            .Should().Be(Biome.Rock);
    }

    // --- Below the rock slope, low ground splits on slope: gentle = meadow, medium-steep = scree ---

    [Fact]
    public void Classify_LowGentle_IsMeadow()
    {
        BiomeClassifier.Classify(1500.0, slopeDegrees: 8.0, northness: 0.0)
            .Should().Be(Biome.Meadow);
    }

    [Fact]
    public void Classify_LowMediumSteep_IsScree()
    {
        BiomeClassifier.Classify(1500.0, slopeDegrees: 35.0, northness: 0.0)
            .Should().Be(Biome.Scree);
    }

    [Fact]
    public void Classify_ScreeSlopeBoundary_BelongsToScree()
    {
        BiomeThresholds t = BiomeThresholds.Default;

        BiomeClassifier.Classify(1500.0, t.ScreeSlopeDegrees, northness: 0.0)
            .Should().Be(Biome.Scree);
    }

    // --- Elevation zonation on gentle ground: meadow → scree → snow → ice as you climb ---

    [Fact]
    public void Classify_MidElevationGentle_IsScree()
    {
        // Above the meadow ceiling but below the snowline, even on gentle ground, is talus/scree.
        BiomeClassifier.Classify(1950.0, slopeDegrees: 8.0, northness: 0.0)
            .Should().Be(Biome.Scree);
    }

    [Fact]
    public void Classify_HighGentle_IsSnow()
    {
        BiomeClassifier.Classify(2250.0, slopeDegrees: 8.0, northness: 0.0)
            .Should().Be(Biome.Snow);
    }

    [Fact]
    public void Classify_VeryHighGentle_IsIce()
    {
        BiomeClassifier.Classify(2450.0, slopeDegrees: 5.0, northness: 0.0)
            .Should().Be(Biome.Ice);
    }

    [Fact]
    public void Classify_MeadowCeilingBoundary_BelongsToScree()
    {
        BiomeThresholds t = BiomeThresholds.Default;

        BiomeClassifier.Classify(t.MeadowMaxElevationM, slopeDegrees: 8.0, northness: 0.0)
            .Should().Be(Biome.Scree);
    }

    [Fact]
    public void Classify_SnowlineBoundary_BelongsToSnow()
    {
        BiomeThresholds t = BiomeThresholds.Default;

        BiomeClassifier.Classify(t.SnowElevationM, slopeDegrees: 8.0, northness: 0.0)
            .Should().Be(Biome.Snow);
    }

    [Fact]
    public void Classify_IcelineBoundary_BelongsToIce()
    {
        BiomeThresholds t = BiomeThresholds.Default;

        BiomeClassifier.Classify(t.IceElevationM, slopeDegrees: 5.0, northness: 0.0)
            .Should().Be(Biome.Ice);
    }

    // --- Aspect: north-facing (cold) lowers the effective snowline; south-facing raises it ---

    [Fact]
    public void Classify_SameElevation_NorthFaceColderThanSouthFace()
    {
        // Just below the flat snowline: a north face (northness=1) tips into snow, a south face (-1) stays bare.
        const double elev = 2100.0;

        BiomeClassifier.Classify(elev, slopeDegrees: 8.0, northness: 1.0)
            .Should().Be(Biome.Snow);

        BiomeClassifier.Classify(elev, slopeDegrees: 8.0, northness: -1.0)
            .Should().NotBe(Biome.Snow);
    }

    // --- Monotonicity: on gentle ground the zone never steps back down as elevation rises ---

    [Fact]
    public void Classify_GentleGround_ZoneIsMonotonicInElevation()
    {
        Biome prev = Biome.Meadow;

        for (double elev = 1400.0; elev <= 2600.0; elev += 25.0)
        {
            Biome b = BiomeClassifier.Classify(elev, slopeDegrees: 6.0, northness: 0.0);
            ZoneRank(b).Should().BeGreaterThanOrEqualTo(ZoneRank(prev),
                $"zone must not descend as elevation rises (at {elev} m got {b})");
            prev = b;
        }
    }

    private static int ZoneRank(Biome b) => b switch
    {
        Biome.Meadow => 0,
        Biome.Scree => 1,
        Biome.Snow => 2,
        Biome.Ice => 3,
        Biome.Rock => 1,   // rock only appears on steep ground (excluded from this gentle-ground walk)
        _ => 0,
    };

    // --- Palette: one in-range colour per biome, clamped, with sensible hues ---

    [Fact]
    public void Palette_HasOneInRangeColourPerBiome()
    {
        foreach (Biome b in Enum.GetValues<Biome>())
        {
            Vector3 c = BiomePalette.ColorFor(b);
            c.X.Should().BeInRange(0f, 1f);
            c.Y.Should().BeInRange(0f, 1f);
            c.Z.Should().BeInRange(0f, 1f);
        }
    }

    [Fact]
    public void Palette_Meadow_IsGreenDominant()
    {
        Vector3 c = BiomePalette.ColorFor(Biome.Meadow);

        c.Y.Should().BeGreaterThan(c.X);
        c.Y.Should().BeGreaterThan(c.Z);
    }

    [Fact]
    public void Palette_Snow_IsBright()
    {
        Vector3 c = BiomePalette.ColorFor(Biome.Snow);

        c.X.Should().BeGreaterThan(0.85f);
        c.Y.Should().BeGreaterThan(0.85f);
        c.Z.Should().BeGreaterThan(0.85f);
    }

    [Fact]
    public void Palette_All_IsInBiomeOrder()
    {
        IReadOnlyList<Vector3> all = BiomePalette.All;

        all.Should().HaveCount(Enum.GetValues<Biome>().Length);
        all[(int)Biome.Meadow].Should().Be(BiomePalette.ColorFor(Biome.Meadow));
        all[(int)Biome.Ice].Should().Be(BiomePalette.ColorFor(Biome.Ice));
    }
}
