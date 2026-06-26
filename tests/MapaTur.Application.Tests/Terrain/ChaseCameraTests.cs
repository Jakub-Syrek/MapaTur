using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ChaseCameraTests
{
    // World frame is X=east, Y=north; the camera eye offset direction is (cos A, sin A). A follow/chase
    // camera must sit BEHIND a subject travelling along the compass bearing, so the eye is opposite the
    // travel direction: bearing N (0°) → eye to the south (0,-1), bearing E (90°) → eye to the west (-1,0).
    [Theory]
    [InlineData(0.0, 0.0, -1.0)]   // travelling north → eye south
    [InlineData(90.0, -1.0, 0.0)]  // travelling east  → eye west
    [InlineData(180.0, 0.0, 1.0)]  // travelling south → eye north
    [InlineData(270.0, 1.0, 0.0)]  // travelling west  → eye east
    public void AzimuthForBearing_PlacesEyeBehindTravelDirection(double bearing, double expCosA, double expSinA)
    {
        float a = ChaseCamera.AzimuthRadiansForBearingDegrees(bearing);

        MathF.Cos(a).Should().BeApproximately((float)expCosA, 1e-4f);
        MathF.Sin(a).Should().BeApproximately((float)expSinA, 1e-4f);
    }

    [Fact]
    public void AzimuthForBearing_IsPeriodic()
    {
        ChaseCamera.AzimuthRadiansForBearingDegrees(45.0)
            .Should().BeApproximately(ChaseCamera.AzimuthRadiansForBearingDegrees(405.0), 1e-5f);
    }
}