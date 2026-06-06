using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Krok 2 (screen-space-error LOD): the metric that turns a tile's geometric error into how many
/// screen pixels that error spans, and the threshold-based LOD choice built on it. This is what
/// lets a distant-but-large massif earn high detail while a close-but-tiny patch does not — detail
/// follows what occupies the screen, not raw distance.
/// </summary>
public sealed class ScreenSpaceErrorTests
{
    private const double HalfPi = Math.PI / 2.0;       // 90° vertical FOV ⇒ tan(45°) = 1, clean arithmetic.
    private const double ThirdPi = Math.PI / 3.0;      // 60° vertical FOV ⇒ narrower than 90°.

    [Fact]
    public void InPixels_KnownGeometry_MatchesClosedForm()
    {
        // error·viewportH / (2·distance·tan(fov/2)) = 10·1000 / (2·1000·1) = 5 px.
        double pixels = ScreenSpaceError.InPixels(
            geometricErrorMeters: 10.0, distanceMeters: 1000.0, fieldOfViewYRadians: HalfPi, viewportHeightPixels: 1000.0);

        pixels.Should().BeApproximately(5.0, 1e-9);
    }

    [Fact]
    public void InPixels_NarrowerFieldOfView_ProjectsMorePixels()
    {
        double wide = ScreenSpaceError.InPixels(10.0, 1000.0, HalfPi, 1000.0);
        double narrow = ScreenSpaceError.InPixels(10.0, 1000.0, ThirdPi, 1000.0);

        narrow.Should().BeGreaterThan(wide); // a narrower FOV magnifies the scene, so the same error spans more pixels.
    }

    [Fact]
    public void InPixels_DecreasesWithDistance()
    {
        double near = ScreenSpaceError.InPixels(10.0, 500.0, HalfPi, 1000.0);
        double mid = ScreenSpaceError.InPixels(10.0, 1000.0, HalfPi, 1000.0);
        double far = ScreenSpaceError.InPixels(10.0, 4000.0, HalfPi, 1000.0);

        near.Should().BeGreaterThan(mid);
        mid.Should().BeGreaterThan(far);
    }

    [Fact]
    public void InPixels_IncreasesWithGeometricError()
    {
        double small = ScreenSpaceError.InPixels(1.0, 1000.0, HalfPi, 1000.0);
        double large = ScreenSpaceError.InPixels(30.0, 1000.0, HalfPi, 1000.0);

        large.Should().BeGreaterThan(small);
    }

    [Fact]
    public void InPixels_IncreasesWithViewportHeight()
    {
        double low = ScreenSpaceError.InPixels(10.0, 1000.0, HalfPi, 720.0);
        double high = ScreenSpaceError.InPixels(10.0, 1000.0, HalfPi, 1440.0);

        high.Should().BeGreaterThan(low);
    }

    [Fact]
    public void InPixels_NonPositiveDistance_IsInfinite()
    {
        ScreenSpaceError.InPixels(10.0, 0.0, HalfPi, 1000.0).Should().Be(double.PositiveInfinity);
        ScreenSpaceError.InPixels(10.0, -5.0, HalfPi, 1000.0).Should().Be(double.PositiveInfinity);
    }

    // Four LOD levels finest→coarsest. With fov 90° and viewportH 1000, InPixels(g,d) = g·500/d.
    private static readonly double[] Levels = { 1.0, 5.0, 10.0, 30.0 };

    [Fact]
    public void SelectLod_VeryClose_PicksFinestLevel()
    {
        // d = 100 ⇒ even the finest (1 m) projects to 5 px (> 2), so the finest LOD is forced.
        int lod = ScreenSpaceError.SelectLod(Levels, distanceMeters: 100.0, fieldOfViewYRadians: HalfPi, viewportHeightPixels: 1000.0, maxErrorPixels: 2.0);

        lod.Should().Be(0);
    }

    [Fact]
    public void SelectLod_VeryFar_PicksCoarsestLevel()
    {
        // d = 20000 ⇒ even the coarsest (30 m) projects to 0.75 px (≤ 2), so the cheapest LOD is enough.
        int lod = ScreenSpaceError.SelectLod(Levels, distanceMeters: 20000.0, fieldOfViewYRadians: HalfPi, viewportHeightPixels: 1000.0, maxErrorPixels: 2.0);

        lod.Should().Be(3);
    }

    [Fact]
    public void SelectLod_IntermediateDistance_PicksCoarsestLevelWithinBudget()
    {
        // d = 5000, threshold 2 px: errors project to 0.1 / 0.5 / 1.0 / 3.0 px. Coarsest within budget is level 2.
        int lod = ScreenSpaceError.SelectLod(Levels, distanceMeters: 5000.0, fieldOfViewYRadians: HalfPi, viewportHeightPixels: 1000.0, maxErrorPixels: 2.0);

        lod.Should().Be(2);
    }

    [Fact]
    public void SelectLod_IsMonotonicNonDecreasingWithDistance()
    {
        double[] distances = { 100, 500, 1000, 3000, 5000, 10000, 50000 };

        int previous = -1;
        foreach (double d in distances)
        {
            int lod = ScreenSpaceError.SelectLod(Levels, d, HalfPi, 1000.0, maxErrorPixels: 2.0);
            lod.Should().BeGreaterThanOrEqualTo(previous); // farther never demands a finer LOD than nearer.
            previous = lod;
        }
    }

    [Fact]
    public void SelectLod_EmptyLevels_Throws()
    {
        Action act = () => ScreenSpaceError.SelectLod(Array.Empty<double>(), 1000.0, HalfPi, 1000.0, 2.0);

        act.Should().Throw<ArgumentException>();
    }
}