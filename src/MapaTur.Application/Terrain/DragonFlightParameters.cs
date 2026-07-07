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

    /// <summary>Yaw (turn) rate at full steering input, rad/s.</summary>
    public float TurnRateRadiansPerSecond { get; init; } = 0.95f;

    /// <summary>Pitch (climb/dive) rate at full steering input, rad/s.</summary>
    public float PitchRateRadiansPerSecond { get; init; } = 0.85f;

    /// <summary>Climb/dive limit, radians (~63°).</summary>
    public float MaxPitchRadians { get; init; } = 1.1f;

    /// <summary>How fast pitch eases back to level when there's no pitch input, rad/s.</summary>
    public float PitchLevelRadiansPerSecond { get; init; } = 0.9f;

    /// <summary>Minimum altitude the dragon holds above the terrain below it, metres (a swoop clearance).</summary>
    public float GroundClearanceMeters { get; init; } = 30f;

    /// <summary>Peak visual bank (roll) into a full-rate turn, radians.</summary>
    public float MaxRollRadians { get; init; } = 0.7f;

    /// <summary>How quickly the visual roll chases its banked target (per second; higher = snappier).</summary>
    public float RollResponsePerSecond { get; init; } = 3.0f;
}