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

    /// <summary>Half-width (metres) of the central-difference used to estimate the ground gradient/slope. Wider
    /// = smoother (ignores sub-metre bumps); ~2 m matches the mesh's normal-smoothing scale.</summary>
    public float SlopeProbeMeters { get; init; } = 2.0f;

    /// <summary>Upward launch speed of a jump, derived from <see cref="JumpHeightMeters"/> and gravity
    /// (v = √(2·g·h)). A jump is ballistic — it ignores the walk-slope gate — so a well-aimed jump clears a step
    /// too steep to walk onto.</summary>
    public float JumpSpeedMetersPerSecond =>
        MathF.Sqrt(2f * GravityMetersPerSecondSquared * MathF.Max(0f, JumpHeightMeters));
}