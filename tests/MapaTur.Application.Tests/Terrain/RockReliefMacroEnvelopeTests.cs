using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockReliefMacroEnvelopeTests
{
    [Fact]
    public void should_vary_relief_strength_over_broad_world_regions()
    {
        // Arrange
        Vector3[] positions = Enumerable.Range(0, 12)
            .Select(index => new Vector3(index * 71f, index * -43f, index * 29f))
            .ToArray();

        // Act
        float[] strengths = positions
            .Select(position => RockReliefMacroEnvelope.GetStrength(position, 80f, seed: 20260761))
            .ToArray();

        // Assert
        strengths.Select(value => MathF.Round(value, 2)).Distinct().Should().HaveCountGreaterThan(4);
    }

    [Fact]
    public void should_keep_macro_strength_bounded_without_removing_all_relief()
    {
        // Arrange
        Vector3[] positions = Enumerable.Range(0, 500)
            .Select(index => new Vector3(index * 13.7f, index * -7.9f, index * 4.1f))
            .ToArray();

        // Act
        float[] strengths = positions
            .Select(position => RockReliefMacroEnvelope.GetStrength(position, 80f, seed: 20260761))
            .ToArray();

        // Assert
        strengths.Should().OnlyContain(value => value >= 0.45f && value <= 1f);
    }

    [Fact]
    public void should_change_smoothly_across_neighbouring_vertices()
    {
        // Arrange
        var position = new Vector3(7766.8f, -6925f, 2326f);

        // Act
        float first = RockReliefMacroEnvelope.GetStrength(position, 80f, seed: 20260761);
        float neighbour = RockReliefMacroEnvelope.GetStrength(
            position + new Vector3(0.01f, -0.01f, 0.01f),
            80f,
            seed: 20260761);

        // Assert
        MathF.Abs(neighbour - first).Should().BeLessThan(0.001f);
    }
}
