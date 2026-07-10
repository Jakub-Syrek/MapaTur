namespace MapaTur.Application.Terrain;

/// <summary>
/// Tuning for <see cref="DragonFlight"/> — the arcade flight feel of riding a dragon over the terrain, in real
/// units (metres, seconds, radians). Defaults aim for a big, weighty Game-of-Thrones-style beast: fast cruise,
/// broad banking turns, generous climb/dive, and enough ground clearance that it swoops the ridges without
/// clipping into them. A record so a caller (or a test) can override one field.
/// </summary>
public sealed record DragonFlightParameters
{
    /// <summary>Slowest the dragon will fly, m/s (it never stalls to a hover — it's always gliding forward).</summary>
    public float MinSpeedMetersPerSecond { get; init; } = 18f;

    /// <summary>Top speed under full throttle, m/s.</summary>
    public float MaxSpeedMetersPerSecond { get; init; } = 150f;

    /// <summary>Speed the dragon launches at / settles toward, m/s.</summary>
    public float CruiseSpeedMetersPerSecond { get; init; } = 55f;

    /// <summary>Throttle acceleration, m/s² (W speeds up, S slows down).</summary>
    public float AccelMetersPerSecondSquared { get; init; } = 40f;

    /// <summary>Gravity's pull along the flight path, m/s²: diving picks up speed, climbing bleeds it — so a dive
    /// accelerates and a climb slows down without any throttle.</summary>
    public float PitchGravityMetersPerSecondSquared { get; init; } = 34f;

    /// <summary>Yaw (turn) rate at full steering input, rad/s.</summary>
    public float TurnRateRadiansPerSecond { get; init; } = 0.95f;

    /// <summary>Pitch (climb/dive) rate at full steering input, rad/s.</summary>
    public float PitchRateRadiansPerSecond { get; init; } = 1.5f;

    /// <summary>Climb/dive limit, radians (~72°).</summary>
    public float MaxPitchRadians { get; init; } = 1.25f;

    /// <summary>How fast pitch eases back to level when there's no pitch input, rad/s. Gentle, so a climb/dive you
    /// steered holds its altitude gain instead of snapping flat.</summary>
    public float PitchLevelRadiansPerSecond { get; init; } = 0.35f;

    /// <summary>Minimum altitude the dragon holds above the terrain below it, metres (a swoop clearance).</summary>
    public float GroundClearanceMeters { get; init; } = 30f;

    /// <summary>Bank (roll) limit, radians (~57 deg) - the turn is flown THROUGH this bank.</summary>
    public float MaxRollRadians { get; init; } = 1.0f;

    /// <summary>Seconds a held ±1 input takes to CHARGE the yaw command to full — a tap charges only a
    /// fraction, which is what makes small corrections precise (keys arrive as a hard 0/1).</summary>
    public float YawCommandAttackSeconds { get; init; } = 0.28f;

    /// <summary>Seconds a released yaw command takes to discharge back to zero.</summary>
    public float YawCommandReleaseSeconds { get; init; } = 0.18f;

    /// <summary>Expo exponent on the yaw command (cmd·|cmd|^(expo−1)): flat near centre for precision, full
    /// authority at the edge — the RC-transmitter classic.</summary>
    public float YawExpo { get; init; } = 1.3f;

    /// <summary>Natural frequency ωn (rad/s) of the CRITICALLY damped spring the roll uses to track its bank
    /// target — ζ=1 means no overshoot and no oscillation by construction; ~6 settles a full bank in ~0.8 s.</summary>
    public float RollSpringOmegaPerSecond { get; init; } = 6f;

    /// <summary>Coordinated-turn gain: heading rate = gain * tan(roll) / speed. At full bank and cruise this
    /// yields ~0.7 rad/s; slower flight carves tighter, faster sweeps wider.</summary>
    public float TurnFromBankGain { get; init; } = 26f;

    /// <summary>How quickly the visual roll chases its banked target (per second; higher = snappier).</summary>
    public float RollResponsePerSecond { get; init; } = 3.0f;

    // ── Landing cycle ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Autopilot approach speed toward the landing spot, m/s.</summary>
    public float ApproachSpeedMetersPerSecond { get; init; } = 25f;

    /// <summary>Distance from the target at which the approach hands over to the flare, metres.</summary>
    public float FlareStartDistanceMeters { get; init; } = 35f;

    /// <summary>Speed the flare bleeds down to before touching down, m/s.</summary>
    public float FlareSpeedMetersPerSecond { get; init; } = 4f;

    /// <summary>Nose-up attitude held through the flare (the wings braking), radians.</summary>
    public float FlareNoseUpRadians { get; init; } = 0.4f;

    /// <summary>Braking deceleration during flare/touchdown, m/s².</summary>
    public float LandingDecelMetersPerSecondSquared { get; init; } = 14f;

    /// <summary>Height held over the spot during the flare, metres.</summary>
    public float FlareHoverMeters { get; init; } = 1.5f;

    /// <summary>Vertical descent rate during the flare, m/s.</summary>
    public float FlareDescentMetersPerSecond { get; init; } = 7f;

    /// <summary>Distance from the target at which the flare settles into the touchdown, metres.</summary>
    public float TouchdownDistanceMeters { get; init; } = 4f;

    /// <summary>Duration of the final settling onto the spot, seconds.</summary>
    public float TouchdownSeconds { get; init; } = 0.7f;

    /// <summary>Vertical descent rate of the final settling, m/s.</summary>
    public float TouchdownDescentMetersPerSecond { get; init; } = 3.5f;

    /// <summary>Speed at which a takeoff hands back to free flight, m/s.</summary>
    public float TakeoffSpeedMetersPerSecond { get; init; } = 20f;

    /// <summary>Acceleration during takeoff, m/s² (harder than cruise throttle — powering off a summit).</summary>
    public float TakeoffAccelMetersPerSecondSquared { get; init; } = 14f;

    /// <summary>Climb attitude held during takeoff, radians.</summary>
    public float TakeoffPitchRadians { get; init; } = 0.3f;

    /// <summary>Peak upward rate of a FlapBoost wing-beat (Space in the air), m/s.</summary>
    public float FlapBoostClimbMetersPerSecond { get; init; } = 56f;

    /// <summary>How fast the FlapBoost lift decays, m/s2 (together with the peak rate this sets the beat's total hoist).</summary>
    public float FlapBoostDecayMetersPerSecondSquared { get; init; } = 40f;

    /// <summary>How fast the flight path chases the nose, per second (side-slip: lower = more drift through
    /// the turn; the nose leads, the body carves after it).</summary>
    public float VelocitySlipChasePerSecond { get; init; } = 2.2f;

    /// <summary>Induced drag of a held bank, m/s2 at full roll - a tight carve bleeds speed.</summary>
    public float TurnInducedDragMetersPerSecondSquared { get; init; } = 6f;

    /// <summary>How sharply a TurnImpulse jerk lands, per second (exponential ease-out: ~63% of the angle in
    /// 1/x s). Higher = snappier jump.</summary>
    public float TurnImpulseSharpnessPerSecond { get; init; } = 3f;

    /// <summary>Peak sideways speed of a turn stroke's shove, m/s (the visible jerk is this translation).</summary>
    public float TurnStrokeLateralPushMetersPerSecond { get; init; } = 16f;

    /// <summary>How fast the sideways shove decays, m/s2.</summary>
    public float TurnStrokeLateralDecayMetersPerSecondSquared { get; init; } = 14f;

    /// <summary>Distance over which the swoop clearance fades out on approach — the dragon must be allowed
    /// to reach ground level AT the spot while still being kept off the terrain far from it, metres.</summary>
    public float LandingClearanceFadeDistanceMeters { get; init; } = 150f;
}