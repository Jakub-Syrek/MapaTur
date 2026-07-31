using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class HybridTerrainFeatureSwitchTests
{
    [Theory]
    [InlineData(false, false, false, HybridTerrainFeatureTransition.None)]
    [InlineData(false, true, false, HybridTerrainFeatureTransition.None)]
    [InlineData(false, true, true, HybridTerrainFeatureTransition.Activate)]
    [InlineData(true, true, true, HybridTerrainFeatureTransition.None)]
    [InlineData(true, false, true, HybridTerrainFeatureTransition.Deactivate)]
    [InlineData(true, true, false, HybridTerrainFeatureTransition.Deactivate)]
    public void should_resolve_a_hard_runtime_transition(
        bool wasActive,
        bool requestedEnabled,
        bool hasConfiguration,
        HybridTerrainFeatureTransition expected)
    {
        HybridTerrainFeatureSwitch.Resolve(wasActive, requestedEnabled, hasConfiguration)
            .Should()
            .Be(expected);
    }

    [Fact]
    public void should_leave_no_streaming_or_drawing_active_when_the_switch_is_off()
    {
        HybridTerrainFeatureSwitch.ShouldBeActive(
                requestedEnabled: false,
                hasConfiguration: true)
            .Should()
            .BeFalse();
    }
}
