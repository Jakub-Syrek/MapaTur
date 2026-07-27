using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockWallCoveragePlannerTests
{
    private static readonly RockWallCoverageOptions Options = new(
        Center: new Vector3(10f, 20f, 30f),
        OutwardNormal: Vector3.UnitY,
        WidthMeters: 48f,
        HeightMeters: 42f,
        NominalPatchHeightMeters: 14f,
        DepthMeters: 2.8f,
        OverlapFraction: 0.28f,
        Seed: 271828);

    [Fact]
    public void should_generate_identical_coverage_for_the_same_seed()
    {
        // Arrange
        float[] aspectRatios = [0.8f, 1.05f, 1.25f];

        // Act
        IReadOnlyList<RockWallCoveragePatch> first = RockWallCoveragePlanner.Plan(Options, aspectRatios);
        IReadOnlyList<RockWallCoveragePatch> second = RockWallCoveragePlanner.Plan(Options, aspectRatios);

        // Assert
        second.Should().Equal(first);
    }

    [Fact]
    public void should_change_layout_when_seed_changes()
    {
        // Arrange
        float[] aspectRatios = [0.8f, 1.05f, 1.25f];

        // Act
        IReadOnlyList<RockWallCoveragePatch> first = RockWallCoveragePlanner.Plan(Options, aspectRatios);
        IReadOnlyList<RockWallCoveragePatch> second = RockWallCoveragePlanner.Plan(
            Options with { Seed = Options.Seed + 1 },
            aspectRatios);

        // Assert
        second.Should().NotEqual(first);
    }

    [Fact]
    public void should_not_repeat_the_same_scan_variant_across_direct_grid_neighbours()
    {
        // Arrange
        float[] aspectRatios = [0.8f, 1.05f, 1.25f];

        // Act
        IReadOnlyList<RockWallCoveragePatch> patches = RockWallCoveragePlanner.Plan(Options, aspectRatios);

        // Assert
        patches.Should().OnlyContain(patch => !patches.Any(other =>
            other.VariantIndex == patch.VariantIndex
            && ((other.Column == patch.Column - 1 && other.Row == patch.Row)
                || (other.Column == patch.Column && other.Row == patch.Row - 1))));
    }

    [Fact]
    public void should_extend_patch_footprints_past_all_requested_coverage_edges()
    {
        // Arrange
        float[] aspectRatios = [0.8f, 1.05f, 1.25f];
        Vector3 outward = Vector3.Normalize(Options.OutwardNormal);
        Vector3 up = Vector3.Normalize(Vector3.UnitZ - (outward * Vector3.Dot(Vector3.UnitZ, outward)));
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, outward));

        // Act
        IReadOnlyList<RockWallCoveragePatch> patches = RockWallCoveragePlanner.Plan(Options, aspectRatios);

        // Assert
        patches.Min(patch =>
                Vector3.Dot(patch.Placement.Center - Options.Center, tangent) - (patch.WidthMeters * 0.5f))
            .Should().BeLessThanOrEqualTo(-Options.WidthMeters * 0.5f);
        patches.Max(patch =>
                Vector3.Dot(patch.Placement.Center - Options.Center, tangent) + (patch.WidthMeters * 0.5f))
            .Should().BeGreaterThanOrEqualTo(Options.WidthMeters * 0.5f);
        patches.Min(patch =>
                Vector3.Dot(patch.Placement.Center - Options.Center, up) - (patch.Placement.HeightMeters * 0.5f))
            .Should().BeLessThanOrEqualTo(-Options.HeightMeters * 0.5f);
        patches.Max(patch =>
                Vector3.Dot(patch.Placement.Center - Options.Center, up) + (patch.Placement.HeightMeters * 0.5f))
            .Should().BeGreaterThanOrEqualTo(Options.HeightMeters * 0.5f);
    }

    [Fact]
    public void should_keep_variation_bounded_and_patch_centres_on_the_original_wall_plane()
    {
        // Arrange
        float[] aspectRatios = [0.8f, 1.05f, 1.25f];
        Vector3 outward = Vector3.Normalize(Options.OutwardNormal);
        float wallPlane = Vector3.Dot(Options.Center, outward);

        // Act
        IReadOnlyList<RockWallCoveragePatch> patches = RockWallCoveragePlanner.Plan(Options, aspectRatios);

        // Assert
        patches.Should().OnlyContain(patch =>
            patch.Placement.HeightMeters >= Options.NominalPatchHeightMeters * 0.88f
            && patch.Placement.HeightMeters <= Options.NominalPatchHeightMeters * 1.12f
            && MathF.Abs(patch.Placement.RollRadians) <= RockWallCoveragePlanner.MaximumRollRadians
            && MathF.Abs(Vector3.Dot(patch.Placement.Center, outward) - wallPlane) < 0.001f);
    }
}
