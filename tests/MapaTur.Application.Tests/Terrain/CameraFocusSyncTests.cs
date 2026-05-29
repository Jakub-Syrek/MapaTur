using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class CameraFocusSyncTests
{
    // fovY chosen so tan(fovY/2) = 0.5 → exact, easy-to-reason arithmetic.
    private static readonly double HalfAngleFov = 2.0 * Math.Atan(0.5);

    [Fact]
    public void DistanceToResolution_AtEquator_MatchesFrustumGeometry()
    {
        // groundSpan = 2 * distance * tan(fovY/2) = 2 * 1000 * 0.5 = 1000 m across 1000 px → 1 m/px.
        // At the equator cos(lat) = 1, so mercator resolution == ground m/px.
        double resolution = CameraFocusSync.DistanceToResolution(
            distance: 1000.0, fovYRadians: HalfAngleFov, viewportHeightPixels: 1000.0, latitudeDegrees: 0.0);

        resolution.Should().BeApproximately(1.0, 1e-6);
    }

    [Fact]
    public void DistanceToResolution_FartherCamera_GivesLargerResolution()
    {
        double near = CameraFocusSync.DistanceToResolution(1000.0, HalfAngleFov, 1000.0, 49.0);
        double far = CameraFocusSync.DistanceToResolution(5000.0, HalfAngleFov, 1000.0, 49.0);

        far.Should().BeGreaterThan(near);
    }

    [Fact]
    public void DistanceToResolution_HigherLatitude_GivesLargerResolution()
    {
        // Mercator stretches toward the poles, so covering the same ground span needs
        // more mercator metres per pixel at higher latitude.
        double atEquator = CameraFocusSync.DistanceToResolution(1000.0, HalfAngleFov, 1000.0, 0.0);
        double atTatry = CameraFocusSync.DistanceToResolution(1000.0, HalfAngleFov, 1000.0, 49.0);

        atTatry.Should().BeGreaterThan(atEquator);
    }

    [Fact]
    public void DistanceResolutionRoundTrip_RecoversDistance()
    {
        double resolution = CameraFocusSync.DistanceToResolution(2500.0, HalfAngleFov, 900.0, 49.2);

        double distance = CameraFocusSync.ResolutionToDistance(resolution, HalfAngleFov, 900.0, 49.2);

        distance.Should().BeApproximately(2500.0, 1e-3);
    }

    [Fact]
    public void ResolutionDistanceRoundTrip_RecoversResolution()
    {
        double distance = CameraFocusSync.ResolutionToDistance(152.0, HalfAngleFov, 720.0, 49.2);

        double resolution = CameraFocusSync.DistanceToResolution(distance, HalfAngleFov, 720.0, 49.2);

        resolution.Should().BeApproximately(152.0, 1e-3);
    }

    [Fact]
    public void DistanceToResolution_NonPositiveViewportHeight_Throws()
    {
        Action act = () => CameraFocusSync.DistanceToResolution(1000.0, HalfAngleFov, 0.0, 49.0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ResolutionToDistance_NonPositiveViewportHeight_Throws()
    {
        Action act = () => CameraFocusSync.ResolutionToDistance(150.0, HalfAngleFov, -10.0, 49.0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}