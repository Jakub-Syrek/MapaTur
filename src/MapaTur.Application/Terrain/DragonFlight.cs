using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Pure arcade flight body for riding a dragon over the terrain (F7 mode). Real units throughout — world XY are
/// east/north metres, <see cref="ElevationMeters"/> is real elevation — so the caller multiplies elevation by the
/// vertical exaggeration only when placing the camera. The dragon always glides FORWARD along its heading; the
/// rider steers the heading + pitch (right-drag) and throttles the speed (W/S). It banks (rolls) into turns for
/// looks and never dips below a swoop clearance above the ground below it.
///
/// The ground is an injected sampler (world XY → real elevation metres, or null off-coverage), so the whole class
/// is deterministic and unit-tests with no DEM, GL, or camera. Not thread-safe: step it from one place (the flight
/// tick), one update at a time.
/// </summary>
public sealed class DragonFlight
{
    private readonly Func<Vector2, float?> sampleGround;
    private readonly DragonFlightParameters p;

    /// <summary>Creates a dragon launched airborne above <paramref name="startXY"/>, facing
    /// <paramref name="startHeadingRadians"/>, at cruise speed.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="sampleGround"/> is null.</exception>
    public DragonFlight(
        Vector2 startXY, float startHeadingRadians, Func<Vector2, float?> sampleGround, DragonFlightParameters? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(sampleGround);
        this.sampleGround = sampleGround;
        this.p = parameters ?? new DragonFlightParameters();

        PositionXY = startXY;
        HeadingRadians = startHeadingRadians;
        SpeedMetersPerSecond = this.p.CruiseSpeedMetersPerSecond;
        float ground = sampleGround(startXY) ?? 0f;
        // Launch comfortably airborne — well above the swoop clearance so the first frames aren't spent clamping.
        ElevationMeters = ground + MathF.Max(this.p.GroundClearanceMeters + 90f, 120f);
    }

    /// <summary>World horizontal position (east, north) in metres.</summary>
    public Vector2 PositionXY { get; private set; }

    /// <summary>Real elevation of the dragon in metres.</summary>
    public float ElevationMeters { get; private set; }

    /// <summary>Facing/flight direction (yaw) in radians.</summary>
    public float HeadingRadians { get; private set; }

    /// <summary>Climb/dive angle in radians (+ up).</summary>
    public float PitchRadians { get; private set; }

    /// <summary>Visual bank (roll) into turns, radians — for the view to tilt the dragon; does not affect the path.</summary>
    public float RollRadians { get; private set; }

    /// <summary>Forward speed in m/s.</summary>
    public float SpeedMetersPerSecond { get; private set; }

    /// <summary>
    /// Advances the flight by <paramref name="dt"/> seconds. <paramref name="yawInput"/> and
    /// <paramref name="pitchInput"/> are steering in [-1,1] (right-drag: +yaw turns right-hand, +pitch noses up);
    /// <paramref name="throttleInput"/> in [-1,1] speeds up (W) / slows down (S). The dragon glides forward along
    /// its heading and is held above the terrain by the swoop clearance.
    /// </summary>
    public void Step(float dt, float yawInput, float pitchInput, float throttleInput, bool holdPitch = false)
    {
        if (dt <= 0f)
        {
            return;
        }

        HeadingRadians += yawInput * this.p.TurnRateRadiansPerSecond * dt;

        // Pitch follows the input; when the stick is centred it eases back to level UNLESS holdPitch is set (the
        // rider is holding an attitude, e.g. right-mouse steering), in which case the current pitch is kept so
        // you can hold a climb/dive with the mouse instead of it snapping flat the moment you stop moving.
        if (MathF.Abs(pitchInput) > 1e-3f)
        {
            PitchRadians = Math.Clamp(
                PitchRadians + (pitchInput * this.p.PitchRateRadiansPerSecond * dt),
                -this.p.MaxPitchRadians,
                this.p.MaxPitchRadians);
        }
        else if (!holdPitch)
        {
            PitchRadians = MoveToward(PitchRadians, 0f, this.p.PitchLevelRadiansPerSecond * dt);
        }

        // Throttle plus gravity along the flight path: diving (pitch < 0) speeds up, climbing bleeds speed.
        float speedDelta =
            ((throttleInput * this.p.AccelMetersPerSecondSquared)
            - (MathF.Sin(PitchRadians) * this.p.PitchGravityMetersPerSecondSquared)) * dt;
        SpeedMetersPerSecond = Math.Clamp(
            SpeedMetersPerSecond + speedDelta,
            this.p.MinSpeedMetersPerSecond,
            this.p.MaxSpeedMetersPerSecond);

        // Bank into the turn (visual only): roll chases a target proportional to the yaw input, SAME sign as the
        // turn so the dragon leans into the direction it's actually turning.
        float rollTarget = Math.Clamp(yawInput, -1f, 1f) * this.p.MaxRollRadians;
        RollRadians += (rollTarget - RollRadians) * Math.Clamp(this.p.RollResponsePerSecond * dt, 0f, 1f);

        // Glide forward along heading + pitch.
        float cp = MathF.Cos(PitchRadians), sp = MathF.Sin(PitchRadians);
        float ch = MathF.Cos(HeadingRadians), sh = MathF.Sin(HeadingRadians);
        float step = SpeedMetersPerSecond * dt;
        PositionXY += new Vector2(cp * ch, cp * sh) * step;
        ElevationMeters += sp * step;

        // Swoop clearance: never dip below the ground beneath us; a dive that hits the floor levels off.
        if (this.sampleGround(PositionXY) is float ground)
        {
            float minElevation = ground + this.p.GroundClearanceMeters;
            if (ElevationMeters < minElevation)
            {
                ElevationMeters = minElevation;
                if (PitchRadians < 0f)
                {
                    PitchRadians = 0f;
                }
            }
        }
    }

    private static float MoveToward(float current, float target, float maxDelta)
    {
        float delta = target - current;
        return MathF.Abs(delta) <= maxDelta ? target : current + (MathF.Sign(delta) * maxDelta);
    }
}