namespace MapaTur.Application.Terrain;

/// <summary>How an ortho cell should reach the resolution tier its camera distance demands this frame.</summary>
public enum OrthoTierAction
{
    /// <summary>Nothing to do: already at the desired tier, or the off-thread compute for it is still running.</summary>
    None,

    /// <summary>Swap to the retained master buffer (promotion to near, or a master that already fits the cap).</summary>
    SwapToMaster,

    /// <summary>Swap to the cell's cached far-tier buffer — no compute needed.</summary>
    SwapToCachedFar,

    /// <summary>Kick the far-tier box-average OFF the calling thread; keep rendering the current texture meanwhile.</summary>
    StartFarCompute,
}

/// <summary>
/// Per-frame tier decision for an ortho cell. The synchronous version of this decision box-averaged a
/// ~180 MB master on the GL thread the moment a cell's tier changed — ~1 s of the measured first-swap
/// hitch (4 far cells at scene start) and the occasional stall on a distant flight. The contract here:
/// a demotion NEVER computes on the calling thread — it swaps an existing buffer (master or cached far)
/// or requests an off-thread compute and waits, and the far buffer is computed at most once per cell
/// (every later demotion is a pointer swap).
/// </summary>
public static class OrthoTierScheduler
{
    /// <summary>
    /// Decides the action for one cell. <paramref name="desiredCapPx"/> is the tier cap the camera distance
    /// asks for (<see cref="OrthoDistanceTier.DesiredCapPx"/>), <paramref name="currentCapPx"/> the cap the
    /// cell's buffer currently reflects (0 = never assigned), <paramref name="masterLongestPx"/> the longest
    /// side of the retained master, <paramref name="hasCachedFar"/> whether the far-tier buffer already
    /// exists, and <paramref name="farComputePending"/> whether its off-thread compute is in flight.
    /// </summary>
    public static OrthoTierAction Decide(
        int desiredCapPx, int currentCapPx, int masterLongestPx, bool hasCachedFar, bool farComputePending)
    {
        if (desiredCapPx == currentCapPx)
        {
            return OrthoTierAction.None;
        }

        if (desiredCapPx >= masterLongestPx)
        {
            return OrthoTierAction.SwapToMaster;
        }

        if (hasCachedFar)
        {
            return OrthoTierAction.SwapToCachedFar;
        }

        return farComputePending ? OrthoTierAction.None : OrthoTierAction.StartFarCompute;
    }
}