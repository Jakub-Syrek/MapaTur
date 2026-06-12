namespace MapaTur.Application.Terrain;

/// <summary>
/// State machine for the "2D map" view: when the camera climbs past the altitude ceiling the 3D view
/// morphs into a top-down hypsometric map (the view drives pitch to nadir and the renderer fades the
/// orthophoto out by <see cref="Blend"/>), the user repositions fast, and descending restores the exact
/// pitch/azimuth they had on entry — at the new location. Hysteresis between the enter and exit
/// altitudes stops the mode from flapping at the boundary. Pure math: the view feeds the eye altitude
/// (REAL metres, i.e. world-Z ÷ exaggeration) + frame delta, and consumes <see cref="Blend"/> +
/// the saved view.
/// </summary>
public sealed class TopDownMapMode
{
    /// <summary>Climbing past this eye altitude (m a.s.l.) enters map mode.</summary>
    public double EnterAltitudeMeters { get; set; } = 7_200.0;

    /// <summary>Descending below this eye altitude (m a.s.l.) leaves map mode. Keep it well under
    /// <see cref="EnterAltitudeMeters"/> — the gap is the anti-flap hysteresis band.</summary>
    public double ExitAltitudeMeters { get; set; } = 6_500.0;

    /// <summary>Seconds for the full 3D ↔ map morph (pitch swing + ortho fade).</summary>
    public double TransitionSeconds { get; set; } = 0.6;

    /// <summary>True while the camera is in map mode (above the ceiling, view pinned to nadir).</summary>
    public bool IsActive { get; private set; }

    /// <summary>0 = full 3D view, 1 = full top-down map; ramps over <see cref="TransitionSeconds"/>.</summary>
    public float Blend { get; private set; }

    /// <summary>Pitch the user had at the moment map mode engaged — restored on descent.</summary>
    public float SavedPitchRadians { get; private set; }

    /// <summary>Azimuth the user had at the moment map mode engaged — restored on descent.</summary>
    public float SavedAzimuthRadians { get; private set; }

    /// <summary>
    /// Advances the state machine by one frame.
    /// </summary>
    /// <param name="eyeAltitudeMeters">Camera EYE altitude in REAL metres a.s.l. (world-Z ÷ exaggeration).</param>
    /// <param name="dtSeconds">Frame delta, in seconds.</param>
    /// <param name="currentPitchRadians">Current camera pitch — captured as the saved view on entry.</param>
    /// <param name="currentAzimuthRadians">Current camera azimuth — captured as the saved view on entry.</param>
    public void Update(double eyeAltitudeMeters, double dtSeconds, float currentPitchRadians, float currentAzimuthRadians)
    {
        if (!IsActive && eyeAltitudeMeters >= EnterAltitudeMeters)
        {
            // Capture the view ONCE per entry, before the mode itself starts steering the pitch.
            IsActive = true;
            SavedPitchRadians = currentPitchRadians;
            SavedAzimuthRadians = currentAzimuthRadians;
        }
        else if (IsActive && eyeAltitudeMeters <= ExitAltitudeMeters)
        {
            IsActive = false;
        }

        float target = IsActive ? 1f : 0f;
        float step = TransitionSeconds > 0.0 ? (float)(dtSeconds / TransitionSeconds) : 1f;
        Blend = Blend < target
            ? MathF.Min(Blend + step, target)
            : MathF.Max(Blend - step, target);
    }
}