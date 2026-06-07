using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Model 1: detail follows where the terrain is geometrically SHARP, not just near. A tile's roughness
/// (its <see cref="DemRasterRoughness.MaxDeviationFromBilinear"/>) becomes a factor that scales its
/// screen-space error: <c>tilePriority = screenSpaceError × roughnessFactor</c>. Ridges/walls/gullies
/// (high roughness) earn a finer zoom even from a distance; gentle valleys (low roughness) stay coarser.
/// </summary>
public sealed class ScreenSpaceLodRoughnessTests
{
    private const double HalfPi = Math.PI / 2.0;
    private static readonly int[] Candidates = { 16, 14, 11 };

    [Fact]
    public void RoughnessFactor_AtReferenceRoughness_IsOne()
    {
        ScreenSpaceLod.RoughnessFactor(roughnessMeters: 30.0, referenceRoughnessMeters: 30.0, maxFactor: 4.0)
            .Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void RoughnessFactor_RougherThanReference_BoostsProportionally()
    {
        ScreenSpaceLod.RoughnessFactor(90.0, 30.0, 4.0).Should().BeApproximately(3.0, 1e-9); // 3× as rough ⇒ 3×
    }

    [Fact]
    public void RoughnessFactor_VeryRough_ClampsToMaxFactor()
    {
        ScreenSpaceLod.RoughnessFactor(1000.0, 30.0, 4.0).Should().BeApproximately(4.0, 1e-9);
    }

    [Fact]
    public void RoughnessFactor_NearlyFlat_StaysAboveAFloorSoTilesArentDropped()
    {
        double factor = ScreenSpaceLod.RoughnessFactor(0.0, 30.0, 4.0);

        factor.Should().BeGreaterThan(0.0, "a flat valley is coarser, not invisible");
        factor.Should().BeLessThan(1.0, "but below the reference so it grades down");
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
}