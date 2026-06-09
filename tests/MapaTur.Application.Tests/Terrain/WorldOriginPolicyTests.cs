using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of the world-origin re-anchor policy — the anti-jitter heart of wider 1 m coverage. A streamed
/// scene must keep its float world-origin CLOSE to the camera (a far origin loses float precision → jitter, the
/// bug that killed the moving-window approach). The policy decides when the origin has drifted too far and must
/// re-anchor to the camera, and returns the world-space shift to apply to already-built geometry so it stays put
/// in the new frame (instead of rebuilding every tile).
/// </summary>
public sealed class WorldOriginPolicyTests
{
    // Morskie Oko area; world distances use the engine's local-tangent metric (LocalTangentProjection).
    private static readonly GeoPoint Origin = new(49.20, 20.07);

    // ~5 km due north of Origin (5000 / 111320 deg of latitude).
    private static readonly GeoPoint FiveKmNorth = new(49.20 + (5000.0 / 111_320.0), 20.07);

    [Fact]
    public void Evaluate_CameraWithinThreshold_DoesNotReanchor()
    {
        WorldOriginDecision d = WorldOriginPolicy.Evaluate(Origin, FiveKmNorth, reanchorThresholdMeters: 6000.0);

        d.ShouldReanchor.Should().BeFalse();
        d.Origin.Should().Be(Origin);
        d.ExistingShift.Should().Be(Vector3.Zero);
    }

    [Fact]
    public void Evaluate_CameraBeyondThreshold_ReanchorsToCamera()
    {
        WorldOriginDecision d = WorldOriginPolicy.Evaluate(Origin, FiveKmNorth, reanchorThresholdMeters: 3000.0);

        d.ShouldReanchor.Should().BeTrue();
        d.Origin.Should().Be(FiveKmNorth);
    }

    [Fact]
    public void Evaluate_CameraExactlyAtThreshold_DoesNotReanchor()
    {
        // Distance is ~5000 m; threshold equal to it must NOT trip (boundary is inclusive = stay).
        double distance = LocalTangentProjection.GeoToWorld(FiveKmNorth, 0f, Origin, 1f).Length();

        WorldOriginDecision d = WorldOriginPolicy.Evaluate(Origin, FiveKmNorth, reanchorThresholdMeters: distance);

        d.ShouldReanchor.Should().BeFalse();
    }

    [Fact]
    public void Reanchor_ExistingShift_KeepsFixedGeometryInPlace()
    {
        // A fixed geographic point built about the OLD origin, plus the shift, must land where it would have been
        // built about the NEW origin (within a couple of metres — the local-tangent cos-lat approximation).
        var fixedPoint = new GeoPoint(49.21, 20.08);
        WorldOriginDecision d = WorldOriginPolicy.Evaluate(Origin, FiveKmNorth, reanchorThresholdMeters: 3000.0);

        Vector3 builtAboutOld = LocalTangentProjection.GeoToWorld(fixedPoint, 0f, Origin, 1f);
        Vector3 builtAboutNew = LocalTangentProjection.GeoToWorld(fixedPoint, 0f, d.Origin, 1f);

        Vector3 shifted = builtAboutOld + d.ExistingShift;

        shifted.X.Should().BeApproximately(builtAboutNew.X, 2.0f);
        shifted.Y.Should().BeApproximately(builtAboutNew.Y, 2.0f);
    }

    [Fact]
    public void Evaluate_NoReanchor_ShiftIsZero()
    {
        WorldOriginDecision d = WorldOriginPolicy.Evaluate(Origin, Origin, reanchorThresholdMeters: 1000.0);

        d.ShouldReanchor.Should().BeFalse();
        d.ExistingShift.Should().Be(Vector3.Zero);
    }
}