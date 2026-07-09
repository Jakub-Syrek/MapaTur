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
    public void RollInput_BanksTheDragon_AndTheBankTurnsTheHeading()
    {
        var dragon = NewDragon(heading: 0f);

        for (int i = 0; i < 30; i++)
        {
            dragon.Step(0.05f, yawInput: 1f, pitchInput: 0f, throttleInput: 0f); // 1.5 s held roll
        }

        dragon.RollRadians.Should().BeGreaterThan(0.9f, "held input banks toward the limit");
        dragon.HeadingRadians.Should().BeGreaterThan(0.3f, "the heading is flown THROUGH the bank");
    }

    [Fact]
    public void ReleasedRoll_SelfLevels_AndTheTurnStops()
    {
        var dragon = NewDragon(heading: 0f);
        for (int i = 0; i < 30; i++)
        {
            dragon.Step(0.05f, yawInput: 1f, pitchInput: 0f, throttleInput: 0f);
        }

        for (int i = 0; i < 40; i++)
        {
            dragon.Step(0.05f, yawInput: 0f, pitchInput: 0f, throttleInput: 0f); // release 2 s
        }

        dragon.RollRadians.Should().BeApproximately(0f, 0.05f, "no input levels the wings");
        float heading = dragon.HeadingRadians;
        dragon.Step(0.05f, 0f, 0f, 0f);
        dragon.HeadingRadians.Should().BeApproximately(heading, 1e-3f, "level wings = straight flight");
    }

    [Fact]
    public void InATurn_TheNoseLeads_AndTheFlightPathLagsBehind()
    {
        var dragon = NewDragon(heading: 0f);

        for (int i = 0; i < 20; i++)
        {
            dragon.Step(0.05f, yawInput: 1f, pitchInput: 0f, throttleInput: 0f); // 1 s of held bank
        }

        float slip = dragon.HeadingRadians - dragon.VelocityHeadingRadians;
        slip.Should().BeGreaterThan(0.03f, "the body side-slips: it moves along a direction BEHIND the nose");
    }

    [Fact]
    public void SlowerFlight_CarvesATighterTurn()
    {
        // Same held bank; the slow dragon must turn its heading further than the fast one.
        var slow = NewDragon(heading: 0f);
        var fast = NewDragon(heading: 0f);
        for (int i = 0; i < 100; i++)
        {
            slow.Step(0.05f, 1f, 0f, throttleInput: -1f);
            fast.Step(0.05f, 1f, 0f, throttleInput: 1f);
        }

        slow.HeadingRadians.Should().BeGreaterThan(fast.HeadingRadians, "turn rate scales with 1/speed");
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

    // ── Landing on a peak ────────────────────────────────────────────────────────────────────────────────────

    private static readonly Vector2 Peak = new(600f, 0f);
    private const float PeakElevation = 2000f;

    private static DragonFlight LandingDragon() =>
        new(Vector2.Zero, 0f, Ground(_ => PeakElevation), Params);

    /// <summary>Steps until the dragon reaches the given phase (or the step budget runs out).</summary>
    private static int StepUntil(DragonFlight dragon, DragonFlightPhase phase, int maxSteps = 6000)
    {
        int steps = 0;
        while (dragon.Phase != phase && steps < maxSteps)
        {
            dragon.Step(0.05f, 0f, 0f, 0f);
            steps++;
        }
        return steps;
    }

    [Fact]
    public void BeginLanding_FromFlying_EntersApproach()
    {
        var dragon = LandingDragon();

        dragon.BeginLanding(Peak, PeakElevation).Should().BeTrue();

        dragon.Phase.Should().Be(DragonFlightPhase.Approach);
    }

    [Fact]
    public void BeginLanding_IgnoredWhenAlreadyLanding()
    {
        var dragon = LandingDragon();
        dragon.BeginLanding(Peak, PeakElevation);

        dragon.BeginLanding(new Vector2(-500f, 0f), 1500f).Should().BeFalse("only one landing at a time");
    }

    [Fact]
    public void Approach_TurnsTowardTheTarget_AndClosesDistance()
    {
        // Target abeam to the left (north) — the autopilot must turn the heading toward it.
        var dragon = new DragonFlight(Vector2.Zero, 0f, Ground(_ => PeakElevation), Params);
        var target = new Vector2(0f, 800f);
        dragon.BeginLanding(target, PeakElevation);

        float startDistance = Vector2.Distance(dragon.PositionXY, target);
        for (int i = 0; i < 200; i++)
        {
            dragon.Step(0.05f, 0f, 0f, 0f);
        }

        Vector2.Distance(dragon.PositionXY, target).Should().BeLessThan(startDistance, "the autopilot flies at the target");
        dragon.HeadingRadians.Should().BeGreaterThan(0.5f, "heading turned toward the northerly target");
    }

    [Fact]
    public void FullLandingSequence_ReachesPerchedOnTheTarget_WithZeroSpeed()
    {
        var dragon = LandingDragon();
        dragon.BeginLanding(Peak, PeakElevation);

        StepUntil(dragon, DragonFlightPhase.Perched).Should().BeLessThan(6000, "the sequence must complete");

        dragon.Phase.Should().Be(DragonFlightPhase.Perched);
        dragon.PositionXY.X.Should().BeApproximately(Peak.X, 2f);
        dragon.PositionXY.Y.Should().BeApproximately(Peak.Y, 2f);
        dragon.ElevationMeters.Should().BeApproximately(PeakElevation, 1f, "perched ON the summit, not hovering the clearance above it");
        dragon.SpeedMetersPerSecond.Should().Be(0f);
    }

    [Fact]
    public void Approach_FullThrottle_AbortsBackToFlying()
    {
        var dragon = LandingDragon();
        dragon.BeginLanding(Peak, PeakElevation);
        dragon.Step(0.05f, 0f, 0f, 0f);

        dragon.Step(0.05f, 0f, 0f, throttleInput: 1f); // W = wave it off

        dragon.Phase.Should().Be(DragonFlightPhase.Flying);
    }

    [Fact]
    public void Perched_IgnoresSteering_AndStaysPut()
    {
        var dragon = LandingDragon();
        dragon.BeginLanding(Peak, PeakElevation);
        StepUntil(dragon, DragonFlightPhase.Perched);

        for (int i = 0; i < 50; i++)
        {
            dragon.Step(0.05f, yawInput: 1f, pitchInput: 1f, throttleInput: 1f);
        }

        dragon.Phase.Should().Be(DragonFlightPhase.Perched, "inputs don't move a perched dragon (takeoff is explicit)");
        dragon.PositionXY.X.Should().BeApproximately(Peak.X, 2f);
        dragon.SpeedMetersPerSecond.Should().Be(0f);
    }

    [Fact]
    public void BeginTakeoff_FromPerched_ClimbsBackToFlying()
    {
        var dragon = LandingDragon();
        dragon.BeginLanding(Peak, PeakElevation);
        StepUntil(dragon, DragonFlightPhase.Perched);

        dragon.BeginTakeoff().Should().BeTrue();
        dragon.Phase.Should().Be(DragonFlightPhase.Takeoff);

        StepUntil(dragon, DragonFlightPhase.Flying, maxSteps: 400).Should().BeLessThan(400, "takeoff completes");
        dragon.SpeedMetersPerSecond.Should().BeGreaterThanOrEqualTo(Params.TakeoffSpeedMetersPerSecond - 0.5f);
        dragon.ElevationMeters.Should().BeGreaterThan(PeakElevation, "airborne again above the summit");
    }

    [Fact]
    public void BeginTakeoff_IgnoredWhileFlying()
    {
        var dragon = LandingDragon();

        dragon.BeginTakeoff().Should().BeFalse();
        dragon.Phase.Should().Be(DragonFlightPhase.Flying);
    }

    [Fact]
    public void FlapBoost_InLevelFlight_GainsAltitude()
    {
        var dragon = NewDragon();
        for (int i = 0; i < 20; i++)
        {
            dragon.Step(0.05f, 0f, 0f, 0f); // settle level
        }

        float before = dragon.ElevationMeters;
        dragon.FlapBoost().Should().BeTrue();
        for (int i = 0; i < 40; i++)
        {
            dragon.Step(0.05f, 0f, 0f, 0f); // 2 s — the burst plays out
        }

        dragon.ElevationMeters.Should().BeGreaterThan(before + 5f, "one hard beat hoists the dragon upward");
    }

    [Fact]
    public void TurnImpulse_ShovesTheBodySideways_AndTurnsItGently()
    {
        var dragon = NewDragon(heading: 0f); // flying +X; a +impulse turns/pushes LEFT (+Y)

        dragon.TurnImpulse(0.5f).Should().BeTrue();
        for (int i = 0; i < 5; i++)
        {
            dragon.Step(0.05f, 0f, 0f, 0f); // 0.25 s
        }

        dragon.HeadingRadians.Should().BeLessThan(0.38f, "the torso comes around GENTLY — no heading teleport");
        dragon.PositionXY.Y.Should().BeGreaterThan(1.5f, "the beat shoves the body sideways before the nose follows");

        for (int i = 0; i < 40; i++)
        {
            dragon.Step(0.05f, 0f, 0f, 0f);
        }

        // Full impulse angle + a little extra from the bank-kick's own coordinated turn while it self-levels.
        dragon.HeadingRadians.Should().BeInRange(0.45f, 0.72f, "the impulse delivers its angle (plus the bank's tail) and stops");
    }

    [Fact]
    public void TurnImpulse_IgnoredWhilePerched()
    {
        var dragon = LandingDragon();
        dragon.BeginLanding(Peak, PeakElevation);
        StepUntil(dragon, DragonFlightPhase.Perched);
        float heading = dragon.HeadingRadians;

        dragon.TurnImpulse(0.5f).Should().BeFalse();
        dragon.Step(0.05f, 0f, 0f, 0f);

        dragon.HeadingRadians.Should().Be(heading);
    }

    [Fact]
    public void FlapBoost_IgnoredWhilePerched()
    {
        var dragon = LandingDragon();
        dragon.BeginLanding(Peak, PeakElevation);
        StepUntil(dragon, DragonFlightPhase.Perched);

        dragon.FlapBoost().Should().BeFalse();
        dragon.ElevationMeters.Should().BeApproximately(PeakElevation, 1f);
    }
}