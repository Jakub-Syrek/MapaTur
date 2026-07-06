namespace MapaTur.Application.Terrain;

/// <summary>
/// Weight of the PERENNIAL firn patches ("lodowczyki" — glacierets) — the correction that above ~2000 m
/// the Tatra snow presence stops being a function of altitude alone. The real patches (Mięguszowiecki
/// Kocioł, Bandzioch, pod Rysami) survive the summer on LOCAL mass balance: wind and avalanches collect
/// snow from a whole catchment into a small cirque (accumulation ≫ direct snowfall), the N-facing walls
/// shade it (minimal insolation), and 334 kJ/kg of latent heat buffers the melt — so the deciding
/// variables are ASPECT (N vs S), TERRAIN SHAPE (cirque vs ridge) and deposition susceptibility, not an
/// elevation threshold. Sheltering is a WEIGHTED SUM of northness and concavity (not a product): the
/// Bandzioch floor is near-flat (northness ≈ 0) yet fully enclosed — deep concavity compensates aspect.
/// Concavity doubles as the wind/avalanche-deposition proxy (cirques are the deposition zones).
/// Pure and unit-tested; the terrain fragment shader mirrors the same formula from these constants.
/// </summary>
public static class PerennialFirn
{
    /// <summary>Real altitude (m a.s.l.) around which perennial patches become possible on OPEN ground.</summary>
    public const float LineMeters = 2_000f;

    /// <summary>How far (m) full concavity pulls the effective line DOWN: avalanche/wind deposition keeps
    /// couloir tongues and runout fans alive well below the open-ground line — the photo-verified "jęzory"
    /// run all the way to the Czarny Staw shore (~1583 m), so a channel line of 2000−550 = 1450 m lets the
    /// fan splay right down to the water. Open slopes still need the full altitude.</summary>
    public const float RunoutDropMeters = 550f;

    /// <summary>Softness band of the altitude gate (m) — half below, half above the effective line.</summary>
    public const float BandMeters = 300f;

    /// <summary>How strongly a southern (insolated) exposure cancels sheltering — a sunlit spot has no
    /// chance of a glacieret no matter how high or concave ("w miejscu nasłonecznionym nie ma szans").</summary>
    public const float SouthPenalty = 0.85f;

    /// <summary>
    /// Firn presence in [0,1]. V2 (photo-matched at Czarny Staw pod Rysami): CONCAVITY IS PRIMARY — real
    /// patches are bright tongues pressed into couloirs and enclosed floors, while open N slopes stay bare;
    /// pure northness alone tops out BELOW the patch threshold (v1 painted whole north faces with a milky
    /// glaze). The final smoothstep sharpens the result into discrete patches instead of a translucent film.
    /// </summary>
    /// <param name="elevationMeters">Real elevation (m a.s.l., BEFORE vertical exaggeration).</param>
    /// <param name="northness">max(0, normal.y): 1 = squarely north-facing, 0 = flat or non-north.</param>
    /// <param name="southness">max(0, -normal.y): 1 = squarely south-facing.</param>
    /// <param name="concavity">0 = open/convex ground, 1 = deeply enclosed channel/floor (1−AO normalised).</param>
    /// <param name="slopeCos">cos(slope) = normal.z: steep headwalls shed the pack mechanically.</param>
    /// <param name="channel">Proximity to a MAPPED watercourse (0..1): the real tongues lie exactly along
    /// the meltwater streams they feed ("w żlebach gdzie płyną cieki jest ten śnieg"), so a mapped stream
    /// counts as a full deposition channel.</param>
    public static float Weight(
        float elevationMeters, float northness, float southness, float concavity, float slopeCos,
        float channel = 0f)
    {
        // CHANNEL-DOMINANT (v6, photo-ground-truthed at Czarny Staw pod Rysami): the real patches are
        // NARROW tongues in the couloir SLOTS the meltwater streams run down — NOT the broad avalanche
        // apron on the cirque floor, which the vertex-scale AO also reads as "concave" but which melts out
        // in the summer sun. So the mapped stream proximity is the PRIMARY driver; broad AO concavity is a
        // weak secondary that only counts when it also faces north. This is what stops the firn from
        // glazing the whole bowl white at snow-slider 0.
        channel = Math.Clamp(channel, 0f, 1f);
        float broadConcave = SmoothStep(0.20f, 0.55f, concavity); // deep enclosed nooks only
        float depo = Math.Max(channel, broadConcave);

        // Deposition channels hold snow LOWER: the effective line sinks with the deposition potential.
        float effLine = LineMeters - (RunoutDropMeters * depo);
        float altGate = SmoothStep(effLine - (BandMeters * 0.5f), effLine + (BandMeters * 0.5f), elevationMeters);
        if (altGate <= 0f)
        {
            return 0f;
        }

        // Shelter: the stream channel earns firn outright; a deep north-facing nook off-channel earns a
        // little; bare northness / open slopes / south faces earn ~nothing (no more whole-bowl glaze).
        float shelter = Math.Clamp(
            channel + (broadConcave * northness * 0.5f) + (0.10f * northness) - (SouthPenalty * southness),
            0f, 1f);

        // Mechanical hold — relaxed IN CHANNELS: avalanche-packed couloir firn is anchored to the bed and
        // clings far steeper (to ~70°) than the loose seasonal pack on open slopes (SnowModel angles).
        float cosBare = MathF.Cos(SnowModel.SnowBaresAboveDegrees * (MathF.PI / 180f)) - (0.25f * depo);
        float cosFull = MathF.Cos(SnowModel.SnowHoldsBelowDegrees * (MathF.PI / 180f)) - (0.20f * depo);
        float hold = SmoothStep(cosBare, cosFull, slopeCos);

        // Sharpen into discrete tongues, with a HIGH threshold so only strong (channel) signals paint.
        return SmoothStep(0.45f, 0.72f, altGate * shelter) * hold;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - (2f * t));
    }
}