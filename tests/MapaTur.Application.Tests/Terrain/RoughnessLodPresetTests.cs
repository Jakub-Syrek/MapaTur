using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>The three on-device tuning presets and how they cap the roughness factor.</summary>
public sealed class RoughnessLodPresetTests
{
    [Fact]
    public void Balanced_IsTheDefaultStartValues()
    {
        RoughnessLodPreset.Balanced.ReferenceRoughnessMeters.Should().Be(30.0);
        RoughnessLodPreset.Balanced.MaxBoost.Should().Be(3.0);
        RoughnessLodPreset.Balanced.MaxFactor.Should().Be(4.0);
    }

    [Theory]
    [InlineData(3.0)] // Safe → 3×
    [InlineData(4.0)] // Balanced → 4×
    [InlineData(5.0)] // Aggressive → 5×
    public void EveryPreset_CapsAVeryRoughTileAtItsMaxFactor(double expectedMaxFactor)
    {
        RoughnessLodPreset preset = expectedMaxFactor switch
        {
            3.0 => RoughnessLodPreset.Safe,
            4.0 => RoughnessLodPreset.Balanced,
            _ => RoughnessLodPreset.Aggressive,
        };

        double factor = ScreenSpaceLod.RoughnessFactor(
            roughnessMeters: 1000.0, preset.ReferenceRoughnessMeters, preset.MaxBoost);

        factor.Should().Be(preset.MaxFactor).And.Be(expectedMaxFactor);
    }
}