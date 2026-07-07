using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="DragonFlight"/>: the pure arcade flight body for riding a dragon over the terrain.
/// Everything is real units — world XY are east/north metres, elevation is real metres — and the ground is an
/// injected sampler, so the flight unit-tests with no GL, DEM, or camera. Right-drag steers (yaw + pitch), W/S
/// throttle; the dragon always glides forward along its heading and never dips below a swoop clearance.
/// </summary>
public sealed class DragonFlightTests
{
    private static Func<Vector2, float?> Ground(Func<Vector2, float> f) => p => f(p);

    private static readonly DragonFlightParameters Params = new();

    private static DragonFlight NewDragon(Func<Vector2, float>? ground = null, float heading = 0f) =>
        new(Vector2.Zero, heading, Ground(ground ?? (_ => 0f)), Params);

    [Fact]
    public void LaunchesAboveTheGroundAtCruiseSpeed()
    {
        var dragon = new DragonFlight(Vector2.Zero, 0f, Ground(_ => 1000f), Params);

        dragon.ElevationMeters.Should().BeGreaterThan(1000f + Params.GroundClearanceMeters, "it launches airborne");
        dragon.SpeedMetersPerSecond.Should().Be(Params.CruiseSpeedMetersPerSecond);
    }

    [Fact]
    public void FliesForwardAlongItsHeading()
    {
        var dragon = NewDragon(heading: 0f); // facing +X (east)

        for (int i = 0; i < 20; i++)
        {
            dragon.Step(0.05f, yawInput: 0f, pitchInput: 0f, throttleInput: 0f);
        }

        dragon.PositionXY.X.Should().BeGreaterThan(10f, "cruising forward covers ground along +X");
        dragon.PositionXY.Y.Should().BeApproximately(0f, 0.5f, "no yaw ⇒ straight line");
    }

    [Fact]
    public void ThrottleUp_IncreasesSpeed_CappedAtMax()
    {
        var dragon = NewDragon();
        float start = dragon.SpeedMetersPerSecond;

        for (int i = 0; i < 200; i++)
        {
            dragon.Step(0.05f, 0f, 0f, throttleInput: 1f);
        }

        dragon.SpeedMetersPerSecond.Should().BeGreaterThan(start);
        dragon.SpeedMetersPerSecond.Should().BeLessThanOrEqualTo(Params.MaxSpeedMetersPerSecond);
    }

    [Fact]
    public void ThrottleDown_SlowsToTheMinimum_ButNeverStops()
    {
        var dragon = NewDragon();

        for (int i = 0; i < 300; i++)
        {
            dragon.Step(0.05f, 0f, 0f, throttleInput: -1f);
        }

        dragon.SpeedMetersPerSecond.Should().Be(Params.MinSpeedMetersPerSecond, "a dragon glides — it never fully stalls");
    }

    [Fact]
    public void YawSteering_TurnsTheHeading_AndBanksIntoTheTurn()
    {
        var dragon = NewDragon(heading: 0f);

        for (int i = 0; i < 10; i++)
        {
            dragon.Step(0.05f, yawInput: 1f, pitchInput: 0f, throttleInput: 0f);
        }

        dragon.HeadingRadians.Should().BeGreaterThan(0.2f, "sustained yaw turns the heading");
        dragon.RollRadians.Should().NotBe(0f, "the dragon banks (rolls) into the turn");
    }

    [Fact]
    public void PitchUp_ClimbsInAltitude()
    {
        var dragon = new DragonFlight(Vector2.Zero, 0f, Ground(_ => 0f), Params);
        float start = dragon.ElevationMeters;

        for (int i = 0; i < 20; i++)
        {
            dragon.Step(0.05f, 0f, pitchInput: 1f, throttleInput: 0f);
        }

        dragon.ElevationMeters.Should().BeGreaterThan(start + 20f, "nose up + forward speed gains height");
    }

    [Fact]
    public void NoPitchInput_EasesBackTowardLevel()
    {
        var dragon = NewDragon();
        for (int i = 0; i < 10; i++)
        {
            dragon.Step(0.05f, 0f, pitchInput: 1f, throttleInput: 0f); // pitch up
        }

        float climbing = dragon.PitchRadians;
        climbing.Should().BeGreaterThan(0.1f);

        for (int i = 0; i < 60; i++)
        {
            dragon.Step(0.05f, 0f, pitchInput: 0f, throttleInput: 0f); // release
        }

        dragon.PitchRadians.Should().BeApproximately(0f, 0.05f, "with no input the dragon levels out");
    }

    [Fact]
    public void DivingIntoTerrain_IsClampedToTheSwoopClearance()
    {
        // Flat ground at 500 m; dive hard for a long time — it must skim the clearance, never punch through.
        var dragon = new DragonFlight(Vector2.Zero, 0f, Ground(_ => 500f), Params);

        for (int i = 0; i < 400; i++)
        {
            dragon.Step(0.05f, 0f, pitchInput: -1f, throttleInput: 1f); // full dive + throttle
            dragon.ElevationMeters.Should().BeGreaterThanOrEqualTo(
                500f + Params.GroundClearanceMeters - 0.01f, "the dragon never dives below the swoop clearance");
        }
    }
}