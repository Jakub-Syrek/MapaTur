using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class CameraClipPlanesTests
{
    [Fact]
    public void Fit_CameraOutsideScene_WrapsNearAndFarTightlyAroundIt()
    {
        var (near, far) = CameraClipPlanes.Fit(distance: 80_000f, sceneRadius: 33_000f);

        // Near just in front of the closest scene point, far just past the farthest.
        near.Should().BeGreaterThan(1f);
        near.Should().BeLessThan(80_000f - 33_000f + 1f);
        far.Should().BeGreaterThan(80_000f + 33_000f);
        near.Should().BeLessThan(far);
    }

    [Fact]
    public void Fit_CameraInsideScene_ClampsNearToPositiveMinimum()
    {
        var (near, far) = CameraClipPlanes.Fit(distance: 100f, sceneRadius: 33_000f);

        near.Should().BeGreaterThanOrEqualTo(1f);
        far.Should().BeGreaterThan(100f);
        near.Should().BeLessThan(far);
    }

    [Fact]
    public void Fit_FarFurtherThanNear_ForAnyPositiveInputs()
    {
        foreach (var (d, r) in new[] { (100f, 1f), (100f, 100_000f), (500_000f, 33_000f), (1_000f, 0f) })
        {
            var (near, far) = CameraClipPlanes.Fit(d, r);
            near.Should().BeGreaterThanOrEqualTo(1f, $"near must stay positive for d={d}, r={r}");
            far.Should().BeGreaterThan(near, $"far must exceed near for d={d}, r={r}");
        }
    }

    [Fact]
    public void Fit_TighterFarThanTheOldFixedMillionMetrePlane()
    {
        // The whole point: replace the fixed 1_000_000 m far plane with one scaled to the
        // scene, so NDC depth precision (and thus the painter's sort) is far better.
        var (_, far) = CameraClipPlanes.Fit(distance: 80_000f, sceneRadius: 33_000f);

        far.Should().BeLessThan(1_000_000f);
    }
}