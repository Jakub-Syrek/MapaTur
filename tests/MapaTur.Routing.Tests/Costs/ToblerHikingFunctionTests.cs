using FluentAssertions;

using MapaTur.Routing.Costs;

namespace MapaTur.Routing.Tests.Costs;

public sealed class ToblerHikingFunctionTests
{
    [Fact]
    public void SpeedMetersPerSecond_PeaksNearSlopeMinus0_05()
    {
        // Tobler's curve peaks at slope = -0.05 with v = 6 km/h = 1.6667 m/s.
        double peakSpeed = ToblerHikingFunction.SpeedMetersPerSecond(-0.05);

        peakSpeed.Should().BeApproximately(6000.0 / 3600.0, 1e-6);
    }

    [Fact]
    public void SpeedMetersPerSecond_DecreasesAwayFromPeak()
    {
        double peak = ToblerHikingFunction.SpeedMetersPerSecond(-0.05);
        double uphill = ToblerHikingFunction.SpeedMetersPerSecond(0.3);
        double steepDownhill = ToblerHikingFunction.SpeedMetersPerSecond(-0.5);

        uphill.Should().BeLessThan(peak);
        steepDownhill.Should().BeLessThan(peak);
    }

    [Fact]
    public void TravelTimeSeconds_ZeroDistanceReturnsZero()
    {
        ToblerHikingFunction.TravelTimeSeconds(0.0, 0.0, 0.0).Should().Be(0.0);
    }

    [Fact]
    public void TravelTimeSeconds_FlatTerrainMatchesDistanceOverSpeed()
    {
        double seconds = ToblerHikingFunction.TravelTimeSeconds(distanceMeters: 1000.0, ascentMeters: 0.0, descentMeters: 0.0);
        double expectedSpeed = ToblerHikingFunction.SpeedMetersPerSecond(0.0);

        seconds.Should().BeApproximately(1000.0 / expectedSpeed, 1e-3);
    }

    [Fact]
    public void TravelTimeSeconds_UphillTakesLongerThanFlat()
    {
        double flat = ToblerHikingFunction.TravelTimeSeconds(distanceMeters: 1000.0, ascentMeters: 0.0, descentMeters: 0.0);
        double uphill = ToblerHikingFunction.TravelTimeSeconds(distanceMeters: 1000.0, ascentMeters: 200.0, descentMeters: 0.0);

        uphill.Should().BeGreaterThan(flat);
    }
}