using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class TemporalPassCadenceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    public void ShouldRefresh_WhenThrottleIsDisabled_AlwaysRefreshes(long frame)
    {
        TemporalPassCadence.ShouldRefresh(frame, throttled: false, hasReusableResult: true)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ShouldRefresh_WhenNoReusableResult_AlwaysRefreshes(long frame)
    {
        TemporalPassCadence.ShouldRefresh(frame, throttled: true, hasReusableResult: false)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void ShouldRefresh_WithReusableResult_RefreshesEverySecondFrame(long frame, bool expected)
    {
        TemporalPassCadence.ShouldRefresh(frame, throttled: true, hasReusableResult: true)
            .Should().Be(expected);
    }
}