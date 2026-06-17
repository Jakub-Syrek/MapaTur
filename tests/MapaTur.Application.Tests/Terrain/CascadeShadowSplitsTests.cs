using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="CascadeShadowSplits"/>: the practical split scheme that divides the
/// camera frustum's [near, far] depth range into cascades for Cascaded Shadow Maps. Each cascade covers
/// [previous far, its far]; near cascades are kept tight (high texel density close up) and far ones grow,
/// blending a logarithmic and a uniform split by <c>lambda</c>. Pure math, unit-tested so the GL pass just
/// consumes the distances.
/// </summary>
public sealed class CascadeShadowSplitsTests
{
    [Fact]
    public void FarDistances_ReturnsOnePerCascade_LastEqualsFar()
    {
        IReadOnlyList<float> splits = CascadeShadowSplits.FarDistances(near: 1f, far: 100f, cascadeCount: 3, lambda: 0.5f);

        splits.Should().HaveCount(3);
        splits[^1].Should().BeApproximately(100f, 0.001f, "the last cascade must reach the far plane");
    }

    [Fact]
    public void FarDistances_AreStrictlyIncreasing()
    {
        IReadOnlyList<float> splits = CascadeShadowSplits.FarDistances(1f, 100f, 4, 0.6f);

        for (int i = 1; i < splits.Count; i++)
        {
            splits[i].Should().BeGreaterThan(splits[i - 1]);
        }
    }

    [Fact]
    public void FarDistances_StayWithinNearAndFar()
    {
        IReadOnlyList<float> splits = CascadeShadowSplits.FarDistances(2f, 500f, 3, 0.75f);

        splits[0].Should().BeGreaterThan(2f);
        foreach (float s in splits)
        {
            s.Should().BeLessThanOrEqualTo(500f);
        }
    }

    [Fact]
    public void FarDistances_LambdaZero_IsUniformSplit()
    {
        // Pure uniform: split i (1-based) sits at near + (far-near) * i/N.
        IReadOnlyList<float> splits = CascadeShadowSplits.FarDistances(near: 10f, far: 110f, cascadeCount: 2, lambda: 0f);

        splits[0].Should().BeApproximately(60f, 0.01f);
        splits[1].Should().BeApproximately(110f, 0.01f);
    }

    [Fact]
    public void FarDistances_LambdaOne_IsLogarithmicSplit()
    {
        // Pure logarithmic: split i sits at near * (far/near)^(i/N). For near=1, far=100, N=2 → {10, 100}.
        IReadOnlyList<float> splits = CascadeShadowSplits.FarDistances(near: 1f, far: 100f, cascadeCount: 2, lambda: 1f);

        splits[0].Should().BeApproximately(10f, 0.01f);
        splits[1].Should().BeApproximately(100f, 0.01f);
    }

    [Theory]
    [InlineData(0f, 100f, 3, 0.5f)]   // near must be > 0 (logarithmic term divides by near)
    [InlineData(-1f, 100f, 3, 0.5f)]
    [InlineData(10f, 10f, 3, 0.5f)]   // far must exceed near
    [InlineData(10f, 5f, 3, 0.5f)]
    [InlineData(1f, 100f, 0, 0.5f)]   // at least one cascade
    [InlineData(1f, 100f, 3, -0.1f)]  // lambda in [0,1]
    [InlineData(1f, 100f, 3, 1.1f)]
    public void FarDistances_RejectsInvalidArguments(float near, float far, int count, float lambda)
    {
        var act = () => CascadeShadowSplits.FarDistances(near, far, count, lambda);

        act.Should().Throw<ArgumentException>();
    }
}
