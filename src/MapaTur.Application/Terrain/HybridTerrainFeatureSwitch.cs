namespace MapaTur.Application.Terrain;

public enum HybridTerrainFeatureTransition
{
    None,
    Activate,
    Deactivate,
}

/// <summary>
/// Pure policy for the RMP3 product switch. An unavailable or disabled layer is never allowed to retain an
/// active streaming/GPU lifetime; the renderer can therefore use
/// <see cref="HybridTerrainFeatureTransition.Deactivate"/> as a hard release.
/// </summary>
public static class HybridTerrainFeatureSwitch
{
    public static bool ShouldBeActive(bool requestedEnabled, bool hasConfiguration) =>
        requestedEnabled && hasConfiguration;

    public static HybridTerrainFeatureTransition Resolve(
        bool wasActive,
        bool requestedEnabled,
        bool hasConfiguration)
    {
        bool shouldBeActive = ShouldBeActive(requestedEnabled, hasConfiguration);
        if (wasActive == shouldBeActive)
        {
            return HybridTerrainFeatureTransition.None;
        }

        return shouldBeActive
            ? HybridTerrainFeatureTransition.Activate
            : HybridTerrainFeatureTransition.Deactivate;
    }
}
