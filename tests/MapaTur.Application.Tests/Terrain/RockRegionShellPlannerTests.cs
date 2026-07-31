using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockRegionShellPlannerTests
{
    private static readonly SteepRockRegion[] Regions =
    [
        new(new Vector3(-30f, 0f, 20f), Vector3.UnitY, 24f, 30f, 80),
        new(new Vector3(0f, 0f, 22f), Vector3.UnitY, 32f, 22f, 95),
        new(new Vector3(28f, 0f, 18f), Vector3.UnitY, 20f, 34f, 72),
        new(new Vector3(4f, 0f, 56f), Vector3.UnitY, 28f, 24f, 66),
    ];

    [Fact]
    public void should_plan_exactly_one_original_scan_shell_per_steep_region()
    {
        // Arrange
        float[] aspectRatios = [0.8f, 1.1f, 1.6f, 2.2f];

        // Act
        IReadOnlyList<RockWallCoveragePatch> patches = RockRegionShellPlanner.Plan(
            Regions,
            aspectRatios,
            maximumDepthMeters: 3f,
            seed: 271828);

        // Assert
        patches.Should().HaveSameCount(Regions);
    }

    [Fact]
    public void should_cover_every_region_after_its_random_roll()
    {
        // Arrange
        float[] aspectRatios = [0.8f, 1.1f, 1.6f, 2.2f];

        // Act
        IReadOnlyList<RockWallCoveragePatch> patches = RockRegionShellPlanner.Plan(
            Regions,
            aspectRatios,
            maximumDepthMeters: 3f,
            seed: 271828);

        // Assert
        patches.Select((patch, index) => CoversRegion(patch, Regions[index])).Should().OnlyContain(value => value);
    }

    [Fact]
    public void should_not_repeat_a_scan_at_the_nearest_region()
    {
        // Arrange
        float[] aspectRatios = [0.8f, 1.1f, 1.6f, 2.2f];

        // Act
        IReadOnlyList<RockWallCoveragePatch> patches = RockRegionShellPlanner.Plan(
            Regions,
            aspectRatios,
            maximumDepthMeters: 3f,
            seed: 271828);

        // Assert
        patches.Should().OnlyContain(patch =>
            patches
                .Where(other => other != patch)
                .MinBy(other => Vector3.DistanceSquared(
                    patch.Placement.Center,
                    other.Placement.Center))
                .VariantIndex != patch.VariantIndex);
    }

    private static bool CoversRegion(RockWallCoveragePatch patch, SteepRockRegion region)
    {
        float cosine = MathF.Cos(patch.Placement.RollRadians);
        float sine = MathF.Sin(patch.Placement.RollRadians);
        foreach (float u in new[] { -region.WidthMeters * 0.5f, region.WidthMeters * 0.5f })
        {
            foreach (float v in new[] { -region.HeightMeters * 0.5f, region.HeightMeters * 0.5f })
            {
                float localU = (u * cosine) + (v * sine);
                float localV = (-u * sine) + (v * cosine);
                if (MathF.Abs(localU) > patch.WidthMeters * 0.5f
                    || MathF.Abs(localV) > patch.Placement.HeightMeters * 0.5f)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
