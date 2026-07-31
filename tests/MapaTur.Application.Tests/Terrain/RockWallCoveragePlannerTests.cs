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
    public void should_not_repeat_the_same_scan_variant_at_the_nearest_spatial_neighbour()
    {
        // Arrange
        float[] aspectRatios = [0.8f, 1.05f, 1.25f];

        // Act
        IReadOnlyList<RockWallCoveragePatch> patches = RockWallCoveragePlanner.Plan(Options, aspectRatios);

        // Assert
        patches.Should().OnlyContain(patch =>
            patches
                .Where(other => other != patch)
                .MinBy(other => Vector3.DistanceSquared(
                    patch.Placement.Center,
                    other.Placement.Center))
                .VariantIndex != patch.VariantIndex);
    }

    [Fact]
    public void should_not_form_a_directional_row_and_column_lattice()
    {
        // Arrange
        float[] aspectRatios = [0.8f, 1.05f, 1.25f];
        Vector3 outward = Vector3.Normalize(Options.OutwardNormal);
        Vector3 up = Vector3.Normalize(Vector3.UnitZ - (outward * Vector3.Dot(Vector3.UnitZ, outward)));
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, outward));

        // Act
        IReadOnlyList<RockWallCoveragePatch> patches = RockWallCoveragePlanner.Plan(Options, aspectRatios);
        int directionBuckets = patches
            .Select(patch =>
            {
                RockWallCoveragePatch nearest = patches
                    .Where(other => other != patch)
                    .MinBy(other => Vector3.DistanceSquared(
                        patch.Placement.Center,
                        other.Placement.Center));
                Vector3 delta = nearest.Placement.Center - patch.Placement.Center;
                float angle = MathF.Atan2(Vector3.Dot(delta, up), Vector3.Dot(delta, tangent));
                angle = (angle + MathF.PI) % MathF.PI;
                return (int)MathF.Floor(angle / (MathF.PI / 12f));
            })
            .Distinct()
            .Count();

        // Assert
        directionBuckets.Should().BeGreaterThanOrEqualTo(8);
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
    public void should_cover_the_sampled_wall_without_holes_between_irregular_patches()
    {
        // Arrange
        float[] aspectRatios = [0.8f, 1.05f, 1.25f];
        Vector3 outward = Vector3.Normalize(Options.OutwardNormal);
        Vector3 up = Vector3.Normalize(Vector3.UnitZ - (outward * Vector3.Dot(Vector3.UnitZ, outward)));
        Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, outward));

        // Act
        IReadOnlyList<RockWallCoveragePatch> patches = RockWallCoveragePlanner.Plan(Options, aspectRatios);
        var uncovered = new List<Vector2>();
        for (float v = -Options.HeightMeters * 0.5f; v <= Options.HeightMeters * 0.5f; v += 1f)
        {
            for (float u = -Options.WidthMeters * 0.5f; u <= Options.WidthMeters * 0.5f; u += 1f)
            {
                bool covered = patches.Any(patch =>
                {
                    Vector3 offset = patch.Placement.Center - Options.Center;
                    float centerU = Vector3.Dot(offset, tangent);
                    float centerV = Vector3.Dot(offset, up);
                    float cosine = MathF.Cos(patch.Placement.RollRadians);
                    float sine = MathF.Sin(patch.Placement.RollRadians);
                    float deltaU = u - centerU;
                    float deltaV = v - centerV;
                    float localU = (deltaU * cosine) + (deltaV * sine);
                    float localV = (-deltaU * sine) + (deltaV * cosine);
                    return MathF.Abs(localU) <= patch.WidthMeters * 0.5f
                        && MathF.Abs(localV) <= patch.Placement.HeightMeters * 0.5f;
                });
                if (!covered)
                {
                    uncovered.Add(new Vector2(u, v));
                }
            }
        }

        // Assert
        uncovered.Should().BeEmpty();
    }

    [Fact]
    public void should_mix_every_available_scan_pattern_in_the_wall()
    {
        // Arrange
        float[] aspectRatios = [0.8f, 1.05f, 1.25f];

        // Act
        IReadOnlyList<RockWallCoveragePatch> patches = RockWallCoveragePlanner.Plan(Options, aspectRatios);

        // Assert
        patches.Select(patch => patch.VariantIndex).Distinct().Should().HaveCount(aspectRatios.Length);
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
