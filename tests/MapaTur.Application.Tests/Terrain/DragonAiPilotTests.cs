using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="DragonAiPilot"/>: the autonomous "policy" that steers an AI dragon by producing the
/// same yaw/pitch/throttle inputs a rider would feed <see cref="DragonFlight.Step"/>. It flies a lazy banked
/// circle around a centre, converging onto a target radius and holding a target altitude. Tested as a CLOSED
/// LOOP against the real flight body over flat injected ground — no GL, DEM, or camera.
/// </summary>
public sealed class DragonAiPilotTests
{
    private const float GroundElevation = 1000f;
    private static readonly DragonFlightParameters Params = new();

    private static Func<Vector2, float?> FlatGround => _ => GroundElevation;

    private static DragonAiPilot NewPilot(Vector2 center, float radius = 400f, float altitude = 1600f, int dir = 1) => new()
    {
        CircleCenter = center,
        CircleRadiusMeters = radius,
        TargetAltitudeMeters = altitude,
        Direction = dir,
    };

    // Runs the closed loop and returns per-step stats gathered AFTER a warm-up (so the convergence transient
    // doesn't pollute the steady-state band assertions).
    private static (float MinDist, float MaxDist, float HeadingSweep, float FinalDist, float MinAlt, float MaxAlt) Run(
        DragonFlight dragon, DragonAiPilot pilot, int steps, float dt, float warmupSeconds)
    {
        float minDist = float.PositiveInfinity, maxDist = 0f, sweep = 0f, minAlt = float.PositiveInfinity, maxAlt = 0f;
        float prevHeading = dragon.HeadingRadians;
        float t = 0f;
        for (int i = 0; i < steps; i++)
        {
            (float yaw, float pitch, float throttle) = pilot.Compute(
                dragon.PositionXY, dragon.HeadingRadians, dragon.ElevationMeters, dragon.SpeedMetersPerSecond);
            dragon.Step(dt, yaw, pitch, throttle);
            t += dt;

            float dHeading = WrapAngle(dragon.HeadingRadians - prevHeading);
            prevHeading = dragon.HeadingRadians;

            if (t < warmupSeconds)
            {
                continue;
            }

            sweep += MathF.Abs(dHeading);
            float dist = (dragon.PositionXY - pilot.CircleCenter).Length();
            minDist = MathF.Min(minDist, dist);
            maxDist = MathF.Max(maxDist, dist);
            minAlt = MathF.Min(minAlt, dragon.ElevationMeters);
            maxAlt = MathF.Max(maxAlt, dragon.ElevationMeters);
        }

        return (minDist, maxDist, sweep, (dragon.PositionXY - pilot.CircleCenter).Length(), minAlt, maxAlt);
    }

    private static float WrapAngle(float a)
    {
        while (a <= -MathF.PI)
        {
            a += 2f * MathF.PI;
        }

        while (a > MathF.PI)
        {
            a -= 2f * MathF.PI;
        }

        return a;
    }

    [Fact]
    public void OrbitsWithinABandAroundTheCentre()
    {
        var center = new Vector2(0f, 0f);
        var dragon = new DragonFlight(new Vector2(400f, 0f), MathF.PI / 2f, FlatGround, Params);
        var pilot = NewPilot(center, radius: 400f);

        (float minDist, float maxDist, _, _, _, _) = Run(dragon, pilot, steps: 4000, dt: 0.05f, warmupSeconds: 40f);

        minDist.Should().BeGreaterThan(200f, "the orbit never collapses onto the centre");
        maxDist.Should().BeLessThan(700f, "the orbit never spirals out past ~1.7×R");
    }

    [Fact]
    public void CompletesFullLoops_HeadingSweepsBeyondOneCircle()
    {
        var center = new Vector2(0f, 0f);
        var dragon = new DragonFlight(new Vector2(400f, 0f), MathF.PI / 2f, FlatGround, Params);
        var pilot = NewPilot(center, radius: 400f);

        (_, _, float sweep, _, _, _) = Run(dragon, pilot, steps: 4000, dt: 0.05f, warmupSeconds: 40f);

        sweep.Should().BeGreaterThan(2f * MathF.PI, "over ~180 s it circles at least once — it is actually orbiting");
    }

    [Fact]
    public void ConvergesInwardWhenStartedFarOutside()
    {
        var center = new Vector2(0f, 0f);
        var dragon = new DragonFlight(new Vector2(1200f, 0f), MathF.PI / 2f, FlatGround, Params); // 3×R out
        var pilot = NewPilot(center, radius: 400f);

        (_, _, _, float finalDist, _, _) = Run(dragon, pilot, steps: 4000, dt: 0.05f, warmupSeconds: 40f);

        finalDist.Should().BeLessThan(700f, "started at 1200 m it is pulled back toward the 400 m orbit radius");
    }

    [Fact]
    public void ClimbsToAndHoldsTheTargetAltitude()
    {
        var center = new Vector2(0f, 0f);
        var dragon = new DragonFlight(new Vector2(400f, 0f), MathF.PI / 2f, FlatGround, Params);
        var pilot = NewPilot(center, radius: 400f, altitude: 1600f);

        (_, _, _, _, float minAlt, float maxAlt) = Run(dragon, pilot, steps: 4000, dt: 0.05f, warmupSeconds: 40f);

        minAlt.Should().BeGreaterThan(1600f - 150f, "it holds near the target altitude, not sagging to the ground");
        maxAlt.Should().BeLessThan(1600f + 150f, "it holds near the target altitude, not ballooning up");
    }

    [Fact]
    public void FollowsAMovingCentre_TheOrbitTracksThePlayer()
    {
        // The "react to the player" mode is the same orbit with a MOVING centre: as the centre drifts, the
        // dragon's orbit drifts with it, so it stays near the centre rather than being left behind.
        var dragon = new DragonFlight(new Vector2(400f, 0f), MathF.PI / 2f, FlatGround, Params);
        var pilot = NewPilot(new Vector2(0f, 0f), radius: 300f);

        float dt = 0.05f;
        var center = new Vector2(0f, 0f);
        float maxDistAfterWarmup = 0f;
        for (int i = 0; i < 4000; i++)
        {
            center += new Vector2(8f, 0f) * dt; // centre glides east at 8 m/s (a moving "player")
            pilot.CircleCenter = center;
            (float yaw, float pitch, float throttle) = pilot.Compute(
                dragon.PositionXY, dragon.HeadingRadians, dragon.ElevationMeters, dragon.SpeedMetersPerSecond);
            dragon.Step(dt, yaw, pitch, throttle);
            if (i > 800)
            {
                maxDistAfterWarmup = MathF.Max(maxDistAfterWarmup, (dragon.PositionXY - center).Length());
            }
        }

        maxDistAfterWarmup.Should().BeLessThan(900f, "the orbit tracks the moving centre instead of being left behind");
    }
}