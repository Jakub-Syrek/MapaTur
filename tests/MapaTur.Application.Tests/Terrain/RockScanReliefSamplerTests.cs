using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockScanReliefSamplerTests
{
    private static readonly RockHeightMap[] Scans =
    [
        new RockHeightMap(4, 4,
        [
            0.10f, 0.25f, 0.80f, 0.45f,
            0.70f, 0.35f, 0.15f, 0.90f,
            0.20f, 0.95f, 0.55f, 0.30f,
            0.85f, 0.40f, 0.05f, 0.65f,
        ]),
        new RockHeightMap(4, 4,
        [
            0.90f, 0.30f, 0.60f, 0.15f,
            0.20f, 0.75f, 0.40f, 0.95f,
            0.55f, 0.10f, 0.85f, 0.35f,
            0.45f, 0.65f, 0.25f, 0.80f,
        ]),
    ];

    [Fact]
    public void should_return_the_same_scan_sample_for_the_same_world_position()
    {
        // Arrange
        var sampler = new RockScanReliefSampler(Scans, featureSizeMeters: 4f, amplitudeMeters: 0.55f);
        var position = new Vector3(81.37f, -22.91f, 15.42f);
        var normal = Vector3.Normalize(new Vector3(0.8f, 0.3f, 0.2f));

        // Act
        RockSurfaceSample first = sampler.Sample(position, normal);
        RockSurfaceSample second = sampler.Sample(position, normal);

        // Assert
        first.Should().Be(second);
    }

    [Fact]
    public void should_break_the_single_texture_period()
    {
        // Arrange
        var sampler = new RockScanReliefSampler(Scans, featureSizeMeters: 4f, amplitudeMeters: 0.55f);
        var normal = Vector3.UnitX;

        // Act
        float first = sampler.Sample(new Vector3(0f, 1.13f, 2.27f), normal).DisplacementMeters;
        float onePeriodAway = sampler.Sample(new Vector3(0f, 5.13f, 2.27f), normal).DisplacementMeters;

        // Assert
        onePeriodAway.Should().NotBeApproximately(first, 0.0001f);
    }

    [Fact]
    public void should_keep_geometric_relief_inside_the_configured_amplitude()
    {
        // Arrange
        const float amplitude = 0.42f;
        var sampler = new RockScanReliefSampler(Scans, featureSizeMeters: 3.7f, amplitude);

        // Act
        float maximum = Enumerable.Range(0, 500)
            .Select(i =>
            {
                var position = new Vector3(i * 0.17f, i * -0.31f, i * 0.23f);
                var normal = Vector3.Normalize(new Vector3(0.7f, 0.2f + (i % 3), 0.4f));
                return MathF.Abs(sampler.Sample(position, normal).DisplacementMeters);
            })
            .Max();

        // Assert
        maximum.Should().BeLessThanOrEqualTo(amplitude);
    }

    [Fact]
    public void should_change_continuously_across_a_stochastic_cell_boundary()
    {
        // Arrange
        var sampler = new RockScanReliefSampler(Scans, featureSizeMeters: 4f, amplitudeMeters: 0.55f);
        var normal = Vector3.UnitX;

        // Act
        float before = sampler.Sample(new Vector3(0f, 3.9999f, 1.73f), normal).DisplacementMeters;
        float after = sampler.Sample(new Vector3(0f, 4.0001f, 1.73f), normal).DisplacementMeters;

        // Assert
        MathF.Abs(after - before).Should().BeLessThan(0.01f);
    }

    [Fact]
    public void should_put_most_geometric_energy_into_broad_rock_forms()
    {
        // Arrange
        var sampler = new RockScanReliefSampler(Scans, featureSizeMeters: 4f, amplitudeMeters: 1f);
        var normal = Vector3.UnitX;
        Vector3[] positions = Enumerable.Range(0, 120)
            .Select(index => new Vector3(0f, index * 0.71f, index * -0.37f))
            .ToArray();

        // Act
        float closeDifference = positions
            .Average(position => MathF.Abs(
                sampler.Sample(position + new Vector3(0f, 0.25f, 0.1f), normal).DisplacementMeters
                - sampler.Sample(position, normal).DisplacementMeters));
        float broadDifference = positions
            .Average(position => MathF.Abs(
                sampler.Sample(position + new Vector3(0f, 8f, 3f), normal).DisplacementMeters
                - sampler.Sample(position, normal).DisplacementMeters));

        // Assert
        closeDifference.Should().BeLessThan(broadDifference * 0.08f);
    }

}
