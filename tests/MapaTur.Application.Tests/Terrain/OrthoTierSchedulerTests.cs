using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="OrthoTierScheduler"/>: the per-frame decision of how an ortho cell reaches the
/// resolution tier its camera distance demands. The contract that kills the first-swap hitch: a demotion to
/// the far tier NEVER computes the box-average on the calling (GL) thread — it either swaps a buffer that
/// already exists (master / cached far) or asks for an off-thread compute and waits. The far buffer is
/// computed at most once per cell: a later re-demotion must reuse the cache, not recompute.
/// </summary>
public sealed class OrthoTierSchedulerTests
{
    private const int Near = OrthoDistanceTier.NearCapPx; // 8192
    private const int Far = OrthoDistanceTier.FarCapPx;   // 2048

    [Fact]
    public void should_do_nothing_when_desired_tier_matches_current()
    {
        OrthoTierScheduler.Decide(
            desiredCapPx: Far, currentCapPx: Far, masterLongestPx: Near,
            hasCachedFar: false, farComputePending: false)
            .Should().Be(OrthoTierAction.None);
    }

    [Fact]
    public void should_swap_to_master_when_promoting_to_near()
    {
        OrthoTierScheduler.Decide(
            desiredCapPx: Near, currentCapPx: Far, masterLongestPx: Near,
            hasCachedFar: true, farComputePending: false)
            .Should().Be(OrthoTierAction.SwapToMaster);
    }

    [Fact]
    public void should_swap_to_master_when_master_already_fits_the_far_cap()
    {
        // A cell whose retained master is small (≤ far cap) never needs a downsample at all.
        OrthoTierScheduler.Decide(
            desiredCapPx: Far, currentCapPx: 0, masterLongestPx: Far,
            hasCachedFar: false, farComputePending: false)
            .Should().Be(OrthoTierAction.SwapToMaster);
    }

    [Fact]
    public void should_start_far_compute_on_first_demand_without_cache()
    {
        OrthoTierScheduler.Decide(
            desiredCapPx: Far, currentCapPx: 0, masterLongestPx: Near,
            hasCachedFar: false, farComputePending: false)
            .Should().Be(OrthoTierAction.StartFarCompute);
    }

    [Fact]
    public void should_wait_while_far_compute_is_pending()
    {
        OrthoTierScheduler.Decide(
            desiredCapPx: Far, currentCapPx: 0, masterLongestPx: Near,
            hasCachedFar: false, farComputePending: true)
            .Should().Be(OrthoTierAction.None);
    }

    [Fact]
    public void should_swap_to_cached_far_once_compute_completed()
    {
        OrthoTierScheduler.Decide(
            desiredCapPx: Far, currentCapPx: 0, masterLongestPx: Near,
            hasCachedFar: true, farComputePending: false)
            .Should().Be(OrthoTierAction.SwapToCachedFar);
    }

    [Fact]
    public void should_swap_to_cached_far_on_re_demotion_without_recompute()
    {
        // Promoted to near earlier, camera flew away again: the far buffer from the first demotion must be
        // reused — this is the "downsample stall on a distant flight" fix (no second compute, ever).
        OrthoTierScheduler.Decide(
            desiredCapPx: Far, currentCapPx: Near, masterLongestPx: Near,
            hasCachedFar: true, farComputePending: false)
            .Should().Be(OrthoTierAction.SwapToCachedFar);
    }

    [Fact]
    public void should_swap_to_master_even_while_far_compute_pending()
    {
        // Camera came back before the far compute finished: promotion must not wait on the stale compute.
        OrthoTierScheduler.Decide(
            desiredCapPx: Near, currentCapPx: 0, masterLongestPx: Near,
            hasCachedFar: false, farComputePending: true)
            .Should().Be(OrthoTierAction.SwapToMaster);
    }
}