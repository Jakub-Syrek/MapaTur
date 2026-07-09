using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Autonomous "pilot" for an AI dragon: given the dragon's current flight state and an orbit target
/// (<see cref="CircleCenter"/> + <see cref="CircleRadiusMeters"/> + <see cref="TargetAltitudeMeters"/>), it
/// returns the yaw/pitch/throttle a rider would feed <see cref="DragonFlight.Step"/>. The dragon flies a lazy
/// banked circle around the centre — converging onto the target radius and holding the target altitude — so the
/// SAME policy drives both the ambient "circle a peak" behaviour (fixed centre) and the "react to the player"
/// behaviour (centre moved onto the player each tick). Pure and deterministic; no GL, DEM, or camera.
/// </summary>
public sealed class DragonAiPilot
{
    /// <summary>World-XY centre the dragon orbits. Move it each tick to make the orbit follow a target.</summary>
    public Vector2 CircleCenter { get; set; }

    /// <summary>Orbit radius in metres.</summary>
    public float CircleRadiusMeters { get; set; } = 400f;

    /// <summary>Absolute cruise altitude to hold, in real metres.</summary>
    public float TargetAltitudeMeters { get; set; } = 1600f;

    /// <summary>Orbit sense: +1 = counter-clockwise (heading increases), -1 = clockwise.</summary>
    public int Direction { get; set; } = 1;

    /// <summary>Heading-error → roll command gain (how hard it banks to chase the desired heading).</summary>
    public float SteerGain { get; set; } = 1.6f;

    /// <summary>How strongly the desired heading is biased back toward the target radius (0 = pure tangent).</summary>
    public float RadialCorrectionStrength { get; set; } = 1.1f;

    /// <summary>Altitude error of this many metres saturates the pitch command. LARGE so altitude is corrected
    /// GENTLY — an aggressive pitch makes the dragon porpoise (climb→overshoot→dive), and the dives spin the speed
    /// up until it's too fast to hold the orbit and drifts away.</summary>
    public float PitchAltitudeScaleMeters { get; set; } = 140f;

    /// <summary>Cap on the pitch command so the AI noses up/down gently, never a hard dive/zoom.</summary>
    public float MaxPitchCommand { get; set; } = 0.35f;

    /// <summary>Cruise speed the pilot holds with the throttle (m/s). Keeping the speed down is what lets a slow,
    /// steady dragon carve the orbit instead of building energy on dives and flying wide.</summary>
    public float TargetSpeedMetersPerSecond { get; set; } = 42f;

    /// <summary>Speed error of this many m/s saturates the throttle command.</summary>
    public float SpeedThrottleScale { get; set; } = 12f;

    /// <summary>
    /// Computes the flight inputs for this tick from the dragon's current state. Feed the result straight into
    /// <see cref="DragonFlight.Step"/>.
    /// </summary>
    /// <param name="positionXY">Dragon world position (east, north) in metres.</param>
    /// <param name="headingRadians">Dragon facing/heading in radians.</param>
    /// <param name="elevationMeters">Dragon elevation in real metres.</param>
    /// <param name="speedMetersPerSecond">Dragon forward speed in m/s (for the throttle hold).</param>
    /// <returns>Yaw (roll command, [-1,1]), pitch ([-1,1], + climbs), throttle ([-1,1]).</returns>
    public (float Yaw, float Pitch, float Throttle) Compute(
        Vector2 positionXY, float headingRadians, float elevationMeters, float speedMetersPerSecond)
    {
        // Radial (centre → dragon) and the tangent that carries the orbit.
        Vector2 radial = positionXY - CircleCenter;
        float dist = radial.Length();
        Vector2 radialUnit = dist > 1e-3f
            ? radial / dist
            : new Vector2(MathF.Cos(headingRadians), MathF.Sin(headingRadians));
        Vector2 tangentUnit = Direction >= 0
            ? new Vector2(-radialUnit.Y, radialUnit.X)   // CCW
            : new Vector2(radialUnit.Y, -radialUnit.X);  // CW

        // Bias the desired heading back toward the target radius: too far out → steer inward (−radial), too far
        // in → steer outward (+radial). Blended with the tangent so it spirals onto the ring, then orbits it.
        float radialError = Math.Clamp((dist - CircleRadiusMeters) / MathF.Max(1f, CircleRadiusMeters), -1f, 1f);
        Vector2 desiredDir = tangentUnit - (radialUnit * (radialError * RadialCorrectionStrength));
        if (desiredDir.LengthSquared() < 1e-6f)
        {
            desiredDir = tangentUnit;
        }

        float desiredHeading = MathF.Atan2(desiredDir.Y, desiredDir.X);
        float headingError = WrapAngle(desiredHeading - headingRadians);
        // +heading error means we need to turn to increase heading; +yaw banks that way (heading follows the bank).
        float yaw = Math.Clamp(SteerGain * headingError, -1f, 1f);

        // Hold altitude: below the target → climb (+pitch), above → descend. Gentle (see PitchAltitudeScaleMeters).
        float altitudeError = TargetAltitudeMeters - elevationMeters;
        float pitch = Math.Clamp(altitudeError / MathF.Max(1f, PitchAltitudeScaleMeters), -MaxPitchCommand, MaxPitchCommand);

        // Hold cruise speed with the throttle: too fast → ease off (throttle down), too slow → power up. This
        // caps the energy a dive would otherwise dump into speed, keeping the turn radius inside the orbit.
        float speedError = TargetSpeedMetersPerSecond - speedMetersPerSecond;
        float throttle = Math.Clamp(speedError / MathF.Max(1f, SpeedThrottleScale), -1f, 1f);

        return (yaw, pitch, throttle);
    }

    /// <summary>Wraps an angle to (−π, π].</summary>
    private static float WrapAngle(float radians)
    {
        while (radians <= -MathF.PI)
        {
            radians += 2f * MathF.PI;
        }

        while (radians > MathF.PI)
        {
            radians -= 2f * MathF.PI;
        }

        return radians;
    }
}