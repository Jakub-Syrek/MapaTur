using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Model 1: detail follows where the terrain is geometrically SHARP, not just near. A tile's roughness
/// (its <see cref="DemRasterRoughness.Roughness"/>) becomes a factor that scales its
/// screen-space error: <c>tilePriority = screenSpaceError × roughnessFactor</c>. Ridges/walls/gullies
/// (high roughness) earn a finer zoom even from a distance; gentle valleys (low roughness) stay coarser.
/// </summary>
public sealed class ScreenSpaceLodRoughnessTests
{
    private const double HalfPi = Math.PI / 2.0;
    private static readonly int[] Candidates = { 16, 14, 11 };

    [Fact]
    public void RoughnessFactor_FlatTile_IsOne_NoPenalty()
    {
        // SSE stays the decision base: a flat valley gets no boost and no penalty.
        ScreenSpaceLod.RoughnessFactor(roughnessMeters: 0.0, referenceRoughnessMeters: 30.0, maxBoost: 4.0)
            .Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void RoughnessFactor_AtReferenceRoughness_AddsAFullBoost()
    {
        ScreenSpaceLod.RoughnessFactor(30.0, 30.0, 4.0).Should().BeApproximately(2.0, 1e-9); // 1 + 1
    }

    [Fact]
    public void RoughnessFactor_RougherThanReference_BoostsProportionally()
    {
        ScreenSpaceLod.RoughnessFactor(90.0, 30.0, 4.0).Should().BeApproximately(4.0, 1e-9); // 1 + clamp(3,0,4)
    }

    [Fact]
    public void RoughnessFactor_VeryRough_ClampsTheBoost()
    {
        ScreenSpaceLod.RoughnessFactor(1000.0, 30.0, 3.0).Should().BeApproximately(4.0, 1e-9); // 1 + min(33,3)
    }

    [Fact]
    public void ZoomForCameraDistance_RoughTile_EarnsFinerZoomThanASmoothOneAtTheSameDistance()
    {
        // d = 2000 m: a smooth tile (factor 1) resolves to z14; a rough tile (factor 4) is pushed to z16.
        int smooth = ScreenSpaceLod.ZoomForCameraDistance(Candidates, 2000.0, 49.0, HalfPi, 1000.0, 2.0, roughnessFactor: 1.0);
        int rough = ScreenSpaceLod.ZoomForCameraDistance(Candidates, 2000.0, 49.0, HalfPi, 1000.0, 2.0, roughnessFactor: 4.0);

        smooth.Should().Be(14);
        rough.Should().Be(16, "high roughness amplifies the screen-space error → finer zoom from the same distance");
    }

    [Fact]
    public void ZoomForCameraDistance_DefaultRoughnessFactor_MatchesUnweighted()
    {
        int weighted = ScreenSpaceLod.ZoomForCameraDistance(Candidates, 5000.0, 49.0, HalfPi, 1000.0, 2.0);
        int explicitOne = ScreenSpaceLod.ZoomForCameraDistance(Candidates, 5000.0, 49.0, HalfPi, 1000.0, 2.0, roughnessFactor: 1.0);

        weighted.Should().Be(explicitOne);
    }

    [Fact]
    public void AssignAroundLookAt_PerTileRoughness_BoostsRoughCellsNeverCoarsens()
    {
        var lookAt = new GeoPoint(49.18, 20.08);
        var camera = new Vector3(0f, 0f, 1300f);

        IReadOnlyList<LookAtLodTile> baseline = ScreenSpaceLod.AssignAroundLookAt(
            lookAt, 1000f, camera, lookAt, 1f, Candidates, gridZoom: 14, radiusTiles: 1,
            fovY: HalfPi, viewportHeight: 1000.0, maxErrorPixels: 2.0);

        // Pretend every cell is very rough: each cell's screen-space error is boosted ⇒ finer (or equal) zoom.
        IReadOnlyList<LookAtLodTile> boosted = ScreenSpaceLod.AssignAroundLookAt(
            lookAt, 1000f, camera, lookAt, 1f, Candidates, gridZoom: 14, radiusTiles: 1,
            fovY: HalfPi, viewportHeight: 1000.0, maxErrorPixels: 2.0,
            cellRoughnessMeters: _ => 200.0, referenceRoughnessMeters: 30.0, maxRoughnessBoost: 3.0);

        var baselineByCell = baseline.ToDictionary(t => t.Cell, t => t.Zoom);
        foreach (LookAtLodTile tile in boosted)
        {
            tile.Zoom.Should().BeGreaterThanOrEqualTo(baselineByCell[tile.Cell], "roughness only boosts, never coarsens");
        }

        boosted.Sum(t => t.Zoom).Should().BeGreaterThan(baseline.Sum(t => t.Zoom), "rough cells move to a finer zoom");
    }
}