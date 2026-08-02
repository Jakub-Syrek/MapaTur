namespace MapaTur.Application.Terrain;

/// <summary>
/// Per-cascade shadow-map refresh policy (2026-08-02, zawieszanie menu przy planowaniu trasy).
/// <para>
/// Measured in the user's own 29-minute session: the shadow pass was 89–191 ms of a 103–200 ms GPU
/// frame (87–95 %), the frame took ~100 ms wall-clock and the UI thread — which owns rendering —
/// stalled 546 times, up to 1078 ms. Every cascade re-rendered whenever the camera drifted 0.5 m,
/// even the 15 km cascade whose 2048 px map is <b>7.3 m per texel</b>: work whose result cannot
/// differ by a single texel.
/// </para>
/// <para>
/// The threshold therefore scales with the cascade's own texel footprint: a cascade re-renders only
/// once the camera has drifted a few of ITS texels. Near shadows still follow the camera tightly;
/// the distant cascade — the one that draws nearly every tile — refreshes rarely. No change to
/// shadow-map size, cascade count, range or scene content: the same picture, computed when it can
/// actually change.
/// </para>
/// </summary>
public static class CascadeRefreshPolicy
{
    // A cascade's ortho box spans roughly 2 × sliceFar across its map, so one texel ≈ 2·far/size metres.
    // Three texels of drift is the point where a re-render can move a shadow edge by a visible pixel.
    private const double TexelsOfDriftTolerated = 3.0;

    // Same sun cone the renderer already used (~0.11°): the simulated sun moves 0.25°/min.
    private const float SunCosThreshold = 0.999998f;

    /// <summary>Camera drift (metres) a cascade tolerates before its map must be re-rendered.</summary>
    public static double MoveThresholdMeters(float sliceFar, int shadowMapSize)
    {
        if (shadowMapSize <= 0)
        {
            return 0.0;
        }

        double texelMeters = 2.0 * Math.Max(1f, sliceFar) / shadowMapSize;
        return texelMeters * TexelsOfDriftTolerated;
    }

    /// <summary>
    /// Whether this cascade must re-render this frame.
    /// </summary>
    /// <param name="movedMeters">Camera movement since this cascade was last rendered.</param>
    /// <param name="sliceFar">The cascade's far distance (metres).</param>
    /// <param name="shadowMapSize">Shadow map resolution (px).</param>
    /// <param name="sunDot">Dot product of the current and last-rendered sun direction.</param>
    /// <param name="sceneChanged">Tile set changed — freshly streamed terrain must cast shadows now.</param>
    /// <param name="everRendered">False until the cascade has a valid map.</param>
    /// <param name="cascadeIndex">0 = nearest. Drives how long a cascade may defer a scene-change refresh.</param>
    /// <param name="msSinceLastRender">Milliseconds since this cascade last rendered.</param>
    public static bool ShouldRefresh(
        float movedMeters,
        float sliceFar,
        int shadowMapSize,
        float sunDot,
        bool sceneChanged,
        bool everRendered,
        int cascadeIndex,
        long msSinceLastRender)
    {
        if (!everRendered || sunDot <= SunCosThreshold)
        {
            return true;
        }

        // Streaming flips the tile set almost every frame while the camera travels, so treating "scene
        // changed" as an unconditional refresh re-rendered all three cascades every frame — measured at
        // 3.00 cascades/frame and up to 200 ms of shadow during long-distance jumps, which is exactly when
        // the user reports the menu freezing. Near shadows still land immediately; the coarser cascades
        // (which draw nearly every tile) coalesce a burst of streaming into one refresh.
        if (sceneChanged && msSinceLastRender >= SceneChangeMinIntervalMs(cascadeIndex))
        {
            return true;
        }

        return movedMeters >= MoveThresholdMeters(sliceFar, shadowMapSize);
    }

    /// <summary>How long a cascade may coalesce streaming-driven refreshes (ms). 0 = refresh at once.</summary>
    public static int SceneChangeMinIntervalMs(int cascadeIndex) => cascadeIndex switch
    {
        <= 0 => 0,
        1 => 150,
        _ => 400,
    };
}