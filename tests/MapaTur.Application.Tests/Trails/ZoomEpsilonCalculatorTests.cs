using FluentAssertions;
using MapaTur.Application.Trails;

namespace MapaTur.Application.Tests.Trails;

public sealed class ZoomEpsilonCalculatorTests
{
    [Fact]
    public void EpsilonMetersForResolution_ScalesWithResolution()
    {
        // The simplification tolerance must shrink as the user zooms in (lower
        // resolution = denser pixel grid = each metre is visible).
        double zoomedOut = ZoomEpsilonCalculator.EpsilonMetersForResolution(150.0); // ~zoom 10
        double zoomedIn = ZoomEpsilonCalculator.EpsilonMetersForResolution(2.4);   // ~zoom 16

        zoomedOut.Should().BeGreaterThan(zoomedIn);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void EpsilonMetersForResolution_InvalidInput_ReturnsZero(double bad)
    {
        ZoomEpsilonCalculator.EpsilonMetersForResolution(bad).Should().Be(0.0);
    }

    [Fact]
    public void EpsilonMetersForResolution_NeverExceedsMaximum()
    {
        // A very low zoom (high resolution) must not produce an epsilon so large
        // that valid hairpins get smoothed out of existence.
        double epsilon = ZoomEpsilonCalculator.EpsilonMetersForResolution(10_000.0);

        epsilon.Should().BeLessThanOrEqualTo(ZoomEpsilonCalculator.MaxEpsilonMeters);
    }

    [Fact]
    public void EpsilonMetersForResolution_NeverBelowFloor()
    {
        // At extreme zoom-in there's no point applying microscopic simplification —
        // ensure the floor still kills sub-vertex jitter without zeroing out.
        double epsilon = ZoomEpsilonCalculator.EpsilonMetersForResolution(0.01);

        epsilon.Should().BeGreaterThanOrEqualTo(ZoomEpsilonCalculator.MinEpsilonMeters);
    }

    [Fact]
    public void EpsilonMetersForResolution_AtTypicalZoom13_RoughlyMatchesTenMeters()
    {
        // Zoom 13 in Web Mercator ≈ 19 m/pixel. 2 px tolerance ≈ 38 m, but
        // we want a tighter trail-realistic value around 10–20 m. Sanity
        // check the calculator stays in the practical band.
        double epsilon = ZoomEpsilonCalculator.EpsilonMetersForResolution(19.0);

        epsilon.Should().BeInRange(5.0, 50.0);
    }
}