using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Where the dragon is in its flight/landing cycle. <see cref="DragonFlight.BeginLanding"/> starts the
/// Approach → Flare → Touchdown → Perched sequence; <see cref="DragonFlight.BeginTakeoff"/> leaves the perch.
/// </summary>
public enum DragonFlightPhase
{
    /// <summary>Free flight under the rider's control.</summary>
    Flying,

    /// <summary>Autopilot glide toward the landing target (throttle up to abort).</summary>
    Approach,

    /// <summary>Final braking: nose up, wings wide, bleeding speed just above the target.</summary>
    Flare,

    /// <summary>The last metres of settling onto the spot.</summary>
    Touchdown,

    /// <summary>Standing on the landing spot (a summit) until <see cref="DragonFlight.BeginTakeoff"/>.</summary>
    Perched,

    /// <summary>Powering back up to flying speed after leaving the perch.</summary>
    Takeoff,
}

/// <summary>
/// Pure arcade flight body for riding a dragon over the terrain (F7 mode). Real units throughout — world XY are
/// east/north metres, <see cref="ElevationMeters"/> is real elevation — so the caller multiplies elevation by the
/// vertical exaggeration only when placing the camera. The dragon always glides FORWARD along its heading; the
/// rider steers the heading + pitch (right-drag) and throttles the speed (W/S). It banks (rolls) into turns for
/// looks and never dips below a swoop clearance above the ground below it. A landing cycle
/// (<see cref="BeginLanding"/>) flies an autopilot approach onto a target point — a mountain summit — flares,
/// touches down and perches there until <see cref="BeginTakeoff"/>.
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
        VelocityHeadingRadians = startHeadingRadians;
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

    /// <summary>
    /// Direction the body actually MOVES along, radians. In a turn it lags <see cref="HeadingRadians"/> — the
    /// nose rolls into the turn first and the flight path carves after it (side-slip/momentum) — which is what
    /// keeps a banked turn from looking like it runs on rails.
    /// </summary>
    public float VelocityHeadingRadians { get; private set; }

    /// <summary>Forward speed in m/s.</summary>
    public float SpeedMetersPerSecond { get; private set; }

    /// <summary>Current phase of the flight/landing cycle.</summary>
    public DragonFlightPhase Phase { get; private set; } = DragonFlightPhase.Flying;

    /// <summary>Landing spot (world XY, metres) while a landing cycle is active.</summary>
    public Vector2 LandingTargetXY { get; private set; }

    /// <summary>Landing spot elevation (real metres) while a landing cycle is active.</summary>
    public float LandingTargetElevation { get; private set; }

    private float touchdownTimer;

    // Ramps 0 → 1 over a few seconds after a takeoff hands back to free flight, so the swoop clearance
    // doesn't SNAP the freshly-airborne dragon 30 m up off the summit in a single frame.
    private float clearanceRamp = 1f;

    // Decaying upward rate of the last FlapBoost (one hard wing-beat lifting the dragon).
    private float flapBoostClimbRate;

    // Remaining heading of the last TurnImpulse (a single-stroke turn jerk), delivered exponentially.
    private float turnImpulseRemaining;

    // Decaying sideways speed of the last TurnImpulse: the wing-beat SHOVES the body toward the turn side
    // (the visible "jerk" is translation, not a heading snap — the torso comes around gently after it).
    private float turnLateralRate;

    /// <summary>
    /// Starts the landing sequence onto the given spot (typically a summit). Only accepted in free flight —
    /// returns false when a landing is already in progress or the dragon is perched/taking off.
    /// </summary>
    /// <param name="targetXY">Landing point, world XY metres.</param>
    /// <param name="targetElevationMeters">Landing point elevation, real metres.</param>
    public bool BeginLanding(Vector2 targetXY, float targetElevationMeters)
    {
        if (Phase != DragonFlightPhase.Flying)
        {
            return false;
        }

        LandingTargetXY = targetXY;
        LandingTargetElevation = targetElevationMeters;
        Phase = DragonFlightPhase.Approach;
        return true;
    }

    /// <summary>Leaves the perch: a jump plus hard wing-beats back to flying speed. Only valid while perched.</summary>
    public bool BeginTakeoff()
    {
        if (Phase != DragonFlightPhase.Perched)
        {
            return false;
        }

        Phase = DragonFlightPhase.Takeoff;
        return true;
    }

    /// <summary>
    /// One hard wing-beat in free flight: a decaying upward burst that hoists the dragon several metres
    /// (Space in the air). Re-triggering resets the burst to full. Only valid while <see cref="DragonFlightPhase.Flying"/>.
    /// </summary>
    public bool FlapBoost()
    {
        if (Phase != DragonFlightPhase.Flying)
        {
            return false;
        }

        this.flapBoostClimbRate = this.p.FlapBoostClimbMetersPerSecond;
        return true;
    }

    /// <summary>
    /// A single-stroke turn: the wing-beat SHOVES the body sideways toward the turn (a decaying lateral push —
    /// the visible jerk is translation, no heading teleport) while the torso rotates by
    /// <paramref name="signedRadians"/> gently behind it, banked into the move. Only valid while flying.
    /// </summary>
    public bool TurnImpulse(float signedRadians)
    {
        if (Phase != DragonFlightPhase.Flying)
        {
            return false;
        }

        this.turnImpulseRemaining += signedRadians;
        this.turnLateralRate = MathF.Sign(signedRadians) * this.p.TurnStrokeLateralPushMetersPerSecond;
        RollRadians = Math.Clamp(
            RollRadians + (MathF.Sign(signedRadians) * this.p.MaxRollRadians * 0.5f),
            -this.p.MaxRollRadians,
            this.p.MaxRollRadians);
        return true;
    }

    /// <summary>
    /// Advances the flight by <paramref name="dt"/> seconds. <paramref name="yawInput"/> is the ROLL command in
    /// [-1,1] (+ banks left): the dragon turns THROUGH its bank — heading follows tan(roll)/speed like a real
    /// coordinated turn — and self-levels when the input releases (unless <paramref name="holdPitch"/> holds the
    /// attitude). <paramref name="pitchInput"/> noses up/down; <paramref name="throttleInput"/> in [-1,1] speeds
    /// up (W) / slows down (S). The dragon glides forward along its heading and is held above the terrain by the
    /// swoop clearance.
    /// </summary>
    public void Step(float dt, float yawInput, float pitchInput, float throttleInput, bool holdPitch = false)
    {
        if (dt <= 0f)
        {
            return;
        }

        switch (Phase)
        {
            case DragonFlightPhase.Approach:
                StepApproach(dt, throttleInput);
                return;
            case DragonFlightPhase.Flare:
                StepFlare(dt, throttleInput);
                return;
            case DragonFlightPhase.Touchdown:
                StepTouchdown(dt);
                return;
            case DragonFlightPhase.Perched:
                StepPerched(dt);
                return;
            case DragonFlightPhase.Takeoff:
                StepTakeoff(dt);
                return;
        }

        // BANKED TURN: the input ROLLS the dragon (released, it self-levels — unless the rider holds the
        // attitude); the HEADING follows the bank like a real coordinated turn — tan(bank) over speed, so a
        // slow dragon carves tight and a fast one sweeps wide. No direct heading steering in free flight.
        if (MathF.Abs(yawInput) > 1e-3f)
        {
            RollRadians = Math.Clamp(
                RollRadians + (yawInput * this.p.RollRatePerSecond * dt),
                -this.p.MaxRollRadians,
                this.p.MaxRollRadians);
        }
        else if (!holdPitch)
        {
            RollRadians = MoveToward(RollRadians, 0f, this.p.RollLevelRatePerSecond * dt);
        }

        HeadingRadians += this.p.TurnFromBankGain * MathF.Tan(RollRadians)
            / MathF.Max(20f, SpeedMetersPerSecond) * dt;

        // Turn-impulse jerk: deliver the remaining snap exponentially — sharp first frames, smooth landing.
        if (this.turnImpulseRemaining != 0f)
        {
            float delta = this.turnImpulseRemaining * Math.Clamp(this.p.TurnImpulseSharpnessPerSecond * dt, 0f, 1f);
            HeadingRadians += delta;
            this.turnImpulseRemaining -= delta;
            if (MathF.Abs(this.turnImpulseRemaining) < 0.002f)
            {
                HeadingRadians += this.turnImpulseRemaining;
                this.turnImpulseRemaining = 0f;
            }
        }

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
        // A held bank additionally bleeds speed (induced drag) — a tight carve costs energy, adding weight.
        float speedDelta =
            ((throttleInput * this.p.AccelMetersPerSecondSquared)
            - (MathF.Sin(PitchRadians) * this.p.PitchGravityMetersPerSecondSquared)
            - (MathF.Abs(MathF.Sin(RollRadians)) * this.p.TurnInducedDragMetersPerSecondSquared)) * dt;
        SpeedMetersPerSecond = Math.Clamp(
            SpeedMetersPerSecond + speedDelta,
            this.p.MinSpeedMetersPerSecond,
            this.p.MaxSpeedMetersPerSecond);

        // SIDE-SLIP: the flight path lags the nose — the body carves after where the head already points —
        // so a banked turn drifts through the arc instead of running on rails.
        VelocityHeadingRadians += WrapAngle(HeadingRadians - VelocityHeadingRadians)
            * Math.Clamp(this.p.VelocitySlipChasePerSecond * dt, 0f, 1f);

        // Glide forward along the VELOCITY heading + pitch, plus the decaying lift of a FlapBoost wing-beat.
        float cp = MathF.Cos(PitchRadians), sp = MathF.Sin(PitchRadians);
        float ch = MathF.Cos(VelocityHeadingRadians), sh = MathF.Sin(VelocityHeadingRadians);
        float step = SpeedMetersPerSecond * dt;
        PositionXY += new Vector2(cp * ch, cp * sh) * step;
        ElevationMeters += (sp * step) + (this.flapBoostClimbRate * dt);
        this.flapBoostClimbRate = MoveToward(this.flapBoostClimbRate, 0f, this.p.FlapBoostDecayMetersPerSecondSquared * dt);

        // Sideways shove of a turn stroke (decaying): + = toward the port (left) side of the motion.
        if (this.turnLateralRate != 0f)
        {
            float side = VelocityHeadingRadians + (MathF.PI / 2f);
            PositionXY += new Vector2(MathF.Cos(side), MathF.Sin(side)) * (this.turnLateralRate * dt);
            this.turnLateralRate = MoveToward(this.turnLateralRate, 0f, this.p.TurnStrokeLateralDecayMetersPerSecondSquared * dt);
        }

        // Swoop clearance: never dip below the ground beneath us; a dive that hits the floor levels off.
        // The clearance ramps back in after a takeoff (it starts at the summit's ground level).
        this.clearanceRamp = MoveToward(this.clearanceRamp, 1f, dt / 3f);
        if (this.sampleGround(PositionXY) is float ground)
        {
            float minElevation = ground + (this.p.GroundClearanceMeters * this.clearanceRamp);
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

    // ── Landing cycle phases ─────────────────────────────────────────────────────────────────────────────────

    // Autopilot glide at the target: heading turns onto the bearing, speed settles to the approach speed, pitch
    // follows the glide slope down to the spot. Throttling UP waves the landing off (back to free flight). The
    // swoop clearance FADES near the target — it must reach ground level there, not hover 30 m over it.
    private void StepApproach(float dt, float throttleInput)
    {
        if (throttleInput > 0.5f)
        {
            Phase = DragonFlightPhase.Flying;
            return;
        }

        Vector2 toTarget = LandingTargetXY - PositionXY;
        float distXY = toTarget.Length();

        float bearing = MathF.Atan2(toTarget.Y, toTarget.X);
        HeadingRadians = TurnToward(HeadingRadians, bearing, this.p.TurnRateRadiansPerSecond * dt);
        SpeedMetersPerSecond = MoveToward(
            SpeedMetersPerSecond, this.p.ApproachSpeedMetersPerSecond, this.p.AccelMetersPerSecondSquared * dt);

        float glideSlope = Math.Clamp(
            MathF.Atan2(LandingTargetElevation - ElevationMeters, MathF.Max(1f, distXY)),
            -this.p.MaxPitchRadians,
            this.p.MaxPitchRadians);
        PitchRadians = MoveToward(PitchRadians, glideSlope, this.p.PitchRateRadiansPerSecond * dt);

        // Bank into the autopilot's own turning, so the approach still LOOKS flown, not railed.
        float turning = Math.Clamp(WrapAngle(bearing - HeadingRadians) / 0.5f, -1f, 1f);
        RollRadians += ((turning * this.p.MaxRollRadians) - RollRadians) * Math.Clamp(this.p.RollResponsePerSecond * dt, 0f, 1f);

        Advance(dt, clearanceScale: Math.Min(1f, distXY / this.p.LandingClearanceFadeDistanceMeters));

        if (Vector2.Distance(PositionXY, LandingTargetXY) < this.p.FlareStartDistanceMeters)
        {
            Phase = DragonFlightPhase.Flare;
        }
    }

    // Final braking just above the spot: nose up, speed bleeding hard, altitude easing down to a couple of
    // metres over the target. Motion heads STRAIGHT at the target (autopilot owns it fully now).
    private void StepFlare(float dt, float throttleInput)
    {
        if (throttleInput > 0.5f)
        {
            Phase = DragonFlightPhase.Flying;
            return;
        }

        Vector2 toTarget = LandingTargetXY - PositionXY;
        float distXY = toTarget.Length();

        HeadingRadians = TurnToward(HeadingRadians, MathF.Atan2(toTarget.Y, toTarget.X), this.p.TurnRateRadiansPerSecond * dt);
        SpeedMetersPerSecond = MoveToward(
            SpeedMetersPerSecond, this.p.FlareSpeedMetersPerSecond, this.p.LandingDecelMetersPerSecondSquared * dt);
        PitchRadians = MoveToward(PitchRadians, this.p.FlareNoseUpRadians, this.p.PitchRateRadiansPerSecond * dt);
        RollRadians = MoveToward(RollRadians, 0f, this.p.RollResponsePerSecond * 0.5f * dt);

        float step = MathF.Min(SpeedMetersPerSecond * dt, distXY);
        if (distXY > 1e-3f)
        {
            PositionXY += toTarget / distXY * step;
        }

        ElevationMeters = MoveToward(
            ElevationMeters, LandingTargetElevation + this.p.FlareHoverMeters, this.p.FlareDescentMetersPerSecond * dt);

        if (Vector2.Distance(PositionXY, LandingTargetXY) < this.p.TouchdownDistanceMeters
            && SpeedMetersPerSecond <= this.p.FlareSpeedMetersPerSecond + 1f)
        {
            this.touchdownTimer = 0f;
            Phase = DragonFlightPhase.Touchdown;
        }
    }

    // The last metres: settle onto the exact spot, kill the speed, level the body.
    private void StepTouchdown(float dt)
    {
        this.touchdownTimer += dt;

        Vector2 toTarget = LandingTargetXY - PositionXY;
        float distXY = toTarget.Length();
        float step = MathF.Min(MathF.Max(SpeedMetersPerSecond, 2f) * dt, distXY);
        if (distXY > 1e-3f)
        {
            PositionXY += toTarget / distXY * step;
        }

        SpeedMetersPerSecond = MoveToward(SpeedMetersPerSecond, 0f, this.p.LandingDecelMetersPerSecondSquared * dt);
        ElevationMeters = MoveToward(ElevationMeters, LandingTargetElevation, this.p.TouchdownDescentMetersPerSecond * dt);
        PitchRadians = MoveToward(PitchRadians, 0f, this.p.PitchRateRadiansPerSecond * dt);
        RollRadians = MoveToward(RollRadians, 0f, this.p.RollResponsePerSecond * dt);

        if (this.touchdownTimer >= this.p.TouchdownSeconds)
        {
            PositionXY = LandingTargetXY;
            ElevationMeters = LandingTargetElevation;
            SpeedMetersPerSecond = 0f;
            PitchRadians = 0f;
            RollRadians = 0f;
            Phase = DragonFlightPhase.Perched;
        }
    }

    // Standing on the summit. Steering does nothing (takeoff is explicit) — just keep the body settled.
    private void StepPerched(float dt)
    {
        PitchRadians = MoveToward(PitchRadians, 0f, this.p.PitchRateRadiansPerSecond * dt);
        RollRadians = MoveToward(RollRadians, 0f, this.p.RollResponsePerSecond * dt);
        SpeedMetersPerSecond = 0f;
    }

    // Powering back up: nose up, hard acceleration; free flight resumes at takeoff speed. The swoop clearance
    // stays off until then (it would otherwise teleport the dragon 30 m up off the summit on the first frame).
    private void StepTakeoff(float dt)
    {
        SpeedMetersPerSecond = MoveToward(
            SpeedMetersPerSecond, this.p.MaxSpeedMetersPerSecond, this.p.TakeoffAccelMetersPerSecondSquared * dt);
        PitchRadians = MoveToward(PitchRadians, this.p.TakeoffPitchRadians, this.p.PitchRateRadiansPerSecond * dt);

        Advance(dt, clearanceScale: 0f);

        if (SpeedMetersPerSecond >= this.p.TakeoffSpeedMetersPerSecond)
        {
            this.clearanceRamp = 0f; // fade the swoop clearance back in — no 30 m snap off the summit
            VelocityHeadingRadians = HeadingRadians; // autopilot flew the nose line — start the slip in sync
            Phase = DragonFlightPhase.Flying;
        }
    }

    // Shared forward integrator + (scaled) swoop-clearance clamp, used by the autopilot phases.
    private void Advance(float dt, float clearanceScale)
    {
        float cp = MathF.Cos(PitchRadians), sp = MathF.Sin(PitchRadians);
        float ch = MathF.Cos(HeadingRadians), sh = MathF.Sin(HeadingRadians);
        float step = SpeedMetersPerSecond * dt;
        PositionXY += new Vector2(cp * ch, cp * sh) * step;
        ElevationMeters += sp * step;

        if (clearanceScale > 0f && this.sampleGround(PositionXY) is float ground)
        {
            float minElevation = ground + (this.p.GroundClearanceMeters * clearanceScale);
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

    private static float TurnToward(float currentRadians, float targetRadians, float maxDelta)
    {
        float delta = WrapAngle(targetRadians - currentRadians);
        return MathF.Abs(delta) <= maxDelta ? targetRadians : currentRadians + (MathF.Sign(delta) * maxDelta);
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

    private static float MoveToward(float current, float target, float maxDelta)
    {
        float delta = target - current;
        return MathF.Abs(delta) <= maxDelta ? target : current + (MathF.Sign(delta) * maxDelta);
    }
}