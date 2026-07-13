namespace MapaTur.Application.Terrain;

/// <summary>
/// Tuning for <see cref="WalkPhysics"/> — the first-person walker's feel, all in REAL-world units (metres,
/// seconds), independent of the terrain's vertical exaggeration. Defaults model an average hiker: eye ~1.7 m,
/// a ~1.2 m hop, and slope limits that make a marked path (cut into a slope at a gentle grade, or switchbacking)
/// walkable while a bare steep face is not — the "wysokie stoki niedostępne jeżeli nie ma drogi pod kątem" rule.
/// A record so a caller (or a test) can override one field with <c>with</c>/an object initialiser.
/// </summary>
public sealed record WalkParameters
{
    /// <summary>Camera eye height above the walker's feet, in metres.</summary>
    public float EyeHeightMeters { get; init; } = 1.7f;

    /// <summary>Downward gravitational acceleration, m/s².</summary>
    public float GravityMetersPerSecondSquared { get; init; } = 9.81f;

    /// <summary>Apex height of a standing jump, in metres — sets the jump impulse via <see cref="JumpSpeedMetersPerSecond"/>.</summary>
    public float JumpHeightMeters { get; init; } = 1.2f;

    /// <summary>Steepest ground you can WALK UP, as a grade (rise/run) measured ALONG the move direction.
    /// tan(35°) ≈ 0.70. A step whose along-move uphill grade exceeds this is blocked — walk it off at an
    /// angle (a switchback) or jump it.</summary>
    public float MaxWalkSlopeGrade { get; init; } = 0.70f;

    /// <summary>Steepest ground you can STAND on, as the fall-line grade (steepest local gradient). tan(45°) = 1.0.
    /// On ground steeper than this the walker cannot hold and slides downhill (gravity on slopes).</summary>
    public float MaxStandSlopeGrade { get; init; } = 1.00f;

    /// <summary>Downhill speed while sliding on too-steep ground, m/s.</summary>
    public float SlideSpeedMetersPerSecond { get; init; } = 4.0f;

    /// <summary>Jumps available before touching ground/rock again. 1 = single, 2 = double jump.</summary>
    public int MaxJumps { get; init; } = 2;

    /// <summary>How close (metres) the feet must be to the surface below for the ciupaga to bite when self-arresting.</summary>
    public float HangReachMeters { get; init; } = 2.0f;

    /// <summary>Minimum fall-line grade for a ciupaga self-arrest to catch — you can only plant the axe into a
    /// steep face (a wall), not float on the flat. tan(~22°) ≈ 0.4.</summary>
    public float HangMinSlopeGrade { get; init; } = 0.4f;

    /// <summary>Climb speed with the two ciupagas planted, m/s — slower than a walk: you haul yourself up the
    /// face one axe at a time. Holding the axe on steep rock (≥ <see cref="HangMinSlopeGrade"/>) and pushing a
    /// move direction ascends/traverses the wall at this rate, OVERRIDING the walk-slope gate (you climb even a
    /// vertical face). No input → the axes just hang (self-arrest).</summary>
    public float ClimbSpeedMetersPerSecond { get; init; } = 1.4f;

    /// <summary>Sideways (contour) climb speed as a fraction of the up/down fall-line rate — traversing a face
    /// is slower than going straight up, the Isonzo Ascent feel ("moving sideways is slower"). 0.5 = half.</summary>
    public float ClimbTraverseFraction { get; init; } = 0.5f;

    /// <summary>On a face this steep (fall-line grade) or steeper, climbing slows to <see cref="SteepClimbFraction"/>
    /// of the flat-wall rate — hauling up a near-vertical wall is harder than a leaning one. tan(~63°) = 2.0.</summary>
    public float SteepClimbGrade { get; init; } = 2.0f;

    /// <summary>Climb-rate multiplier at/above <see cref="SteepClimbGrade"/> (1 = no slowdown; the climb never
    /// stalls, so this stays above 0).</summary>
    public float SteepClimbFraction { get; init; } = 0.55f;

    /// <summary>Auto-belay: a piton is planted every this many metres of climbing (the first on the initial grab).</summary>
    public float PitonSpacingMeters { get; init; } = 10f;

    /// <summary>Most pitons kept on the wall at once — planting a further one pulls the oldest (rolling protection).</summary>
    public int MaxPitons { get; init; } = 3;

    /// <summary>How far below the highest piton the rope lets you drop before it goes taut and holds you (a fall).</summary>
    public float RopeLengthMeters { get; init; } = 6f;

    /// <summary>Full grip stamina, in seconds of continuous climbing before you're spent and peel off.</summary>
    public float MaxGripStaminaSeconds { get; init; } = 24f;

    /// <summary>Grip drained per second while on the wall (climbing or hanging on the axes).</summary>
    public float GripDrainPerSecond { get; init; } = 1f;

    /// <summary>Grip regained per second while resting on safe ground or dangling on the rope.</summary>
    public float GripRegenPerSecond { get; init; } = 4f;

    /// <summary>Grip you must have to ENGAGE the wall (once on, you hold until it hits zero — hysteresis).</summary>
    public float GripEngageThresholdSeconds { get; init; } = 3f;

    /// <summary>How far ahead-and-up (along the fall line) the mantle probe looks for a walkable ledge to top out onto.</summary>
    public float MantleProbeAheadMeters { get; init; } = 1.5f;

    /// <summary>Most a mantle ledge may rise above the feet and still be pulled onto.</summary>
    public float MantleReachMeters { get; init; } = 1.6f;

    /// <summary>The probe ledge must be at least this walkable (fall-line grade below it) to mantle onto. tan(~35°).</summary>
    public float MantleMaxSlopeGrade { get; init; } = 0.7f;

    /// <summary>Half-width (metres) of the central-difference used to estimate the ground gradient/slope. Wider
    /// = smoother (ignores sub-metre bumps); ~2 m matches the mesh's normal-smoothing scale.</summary>
    public float SlopeProbeMeters { get; init; } = 2.0f;

    /// <summary>Upward launch speed of a jump, derived from <see cref="JumpHeightMeters"/> and gravity
    /// (v = √(2·g·h)). A jump is ballistic — it ignores the walk-slope gate — so a well-aimed jump clears a step
    /// too steep to walk onto.</summary>
    public float JumpSpeedMetersPerSecond =>
        MathF.Sqrt(2f * GravityMetersPerSecondSquared * MathF.Max(0f, JumpHeightMeters));
}