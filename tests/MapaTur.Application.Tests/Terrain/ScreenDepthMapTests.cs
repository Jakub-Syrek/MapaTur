using System.Numerics;
using FluentAssertions;
using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScreenDepthMapTests
{
    [Fact]
    public void Constructor_NonPositiveResolution_Throws()
    {
        Action act = () => _ = new ScreenDepthMap(0, 4);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Reset_MakesAllBinsTreatedAsInfinitelyFar()
    {
        var map = new ScreenDepthMap(4, 4);
        map.Configure(100f, 100f);
        map.Reset();

        // Querying any pixel before anyone writes returns "everything is in front of you".
        map.IsBehind(50f, 50f, 0.5f).Should().BeFalse();
    }

    [Fact]
    public void Write_ThenSample_ReturnsMinDepthInBin()
    {
        var map = new ScreenDepthMap(4, 4);
        map.Configure(100f, 100f);
        map.Reset();

        // Both pixels fall into the same bin (25x25 px tiles). Min Z = 0.3.
        map.Write(50f, 50f, 0.7f);
        map.Write(60f, 55f, 0.3f);

        map.IsBehind(55f, 55f, 0.5f).Should().BeTrue();  // 0.5 > 0.3 + eps → occluded
        map.IsBehind(55f, 55f, 0.2f).Should().BeFalse(); // 0.2 < 0.3 → in front
    }

    [Fact]
    public void IsBehind_OutsideScreen_ReturnsFalse()
    {
        var map = new ScreenDepthMap(4, 4);
        map.Configure(100f, 100f);
        map.Reset();
        map.Write(50f, 50f, 0.5f);

        map.IsBehind(-5f, 50f, 0.9f).Should().BeFalse();
        map.IsBehind(50f, 200f, 0.9f).Should().BeFalse();
    }

    [Fact]
    public void IsBehind_NoMeshWritten_ReturnsFalse()
    {
        var map = new ScreenDepthMap(4, 4);
        map.Configure(100f, 100f);
        map.Reset();

        map.IsBehind(50f, 50f, 0.9f).Should().BeFalse();
    }

    [Fact]
    public void Write_NaNCoordinate_IsIgnored()
    {
        var map = new ScreenDepthMap(4, 4);
        map.Configure(100f, 100f);
        map.Reset();
        map.Write(float.NaN, float.NaN, 0.1f);

        map.IsBehind(50f, 50f, 0.9f).Should().BeFalse();
    }

    [Fact]
    public void IsBehind_NullPoint_ReturnsFalse()
    {
        var map = new ScreenDepthMap(4, 4);
        map.Configure(100f, 100f);
        map.Reset();
        map.Write(50f, 50f, 0.1f);

        // Off-frustum trail points are represented as null in our pipelines and
        // must not be flagged occluded — caller already won't render them.
        map.IsBehind((Vector3?)null, 0.05f).Should().BeFalse();
    }

    [Fact]
    public void IsBehind_EpsilonAllowsTrailsAtMeshDepth()
    {
        var map = new ScreenDepthMap(4, 4);
        map.Configure(100f, 100f);
        map.Reset();
        map.Write(50f, 50f, 0.50f);

        // Trail lifted ~5 m above ground projects to ~0.499 vs mesh 0.50 — well
        // within the default tolerance so the trail still draws.
        map.IsBehind(50f, 50f, 0.501f, epsilon: 0.005f).Should().BeFalse();
        // But a trail clearly behind the mountain (0.6) is occluded.
        map.IsBehind(50f, 50f, 0.6f, epsilon: 0.005f).Should().BeTrue();
    }
}