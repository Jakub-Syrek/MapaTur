using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="FrustumSliceCorners"/>: the 8 world-space corners of a camera frustum
/// slice between two depths, used to fit each Cascaded-Shadow-Map cascade's orthographic light box. The
/// orbit camera at azimuth 0, pitch 0 sits at (Distance,0,0) looking toward −X with world-Z up, giving
/// hand-checkable corner coordinates.
/// </summary>
public sealed class FrustumSliceCornersTests
{
    private const float Tol = 0.01f;

    private static Camera3D KnownCamera() => new()
    {
        Target = Vector3.Zero,
        Distance = 1000f,
        AzimuthRadians = 0f,
        PitchRadians = 0f,
        FieldOfViewYRadians = MathF.PI / 4f, // tan(22.5°) = 0.41421
    };

    [Fact]
    public void Compute_ReturnsEightCorners()
    {
        Vector3[] corners = FrustumSliceCorners.Compute(KnownCamera(), aspectRatio: 1f, sliceNear: 10f, sliceFar: 100f);

        corners.Should().HaveCount(8);
    }

    [Fact]
    public void Compute_NearCornersSitOnTheNearSlicePlane()
    {
        // Camera at X=1000 looking toward −X: the near slice plane (depth 10) is at X = 990.
        Vector3[] c = FrustumSliceCorners.Compute(KnownCamera(), 1f, 10f, 100f);

        for (int i = 0; i < 4; i++)
        {
            c[i].X.Should().BeApproximately(990f, Tol);
        }
    }

    [Fact]
    public void Compute_FarCornersSitOnTheFarSlicePlane()
    {
        Vector3[] c = FrustumSliceCorners.Compute(KnownCamera(), 1f, 10f, 100f);

        for (int i = 4; i < 8; i++)
        {
            c[i].X.Should().BeApproximately(900f, Tol);
        }
    }

    [Fact]
    public void Compute_CornerHalfExtentsScaleWithDepthAndFov()
    {
        // Half-height = depth * tan(fovY/2); half-width = half-height * aspect. At aspect 1 the slice is
        // square, so |Y| = |Z| = depth * 0.41421 at each plane.
        Vector3[] c = FrustumSliceCorners.Compute(KnownCamera(), 1f, 10f, 100f);

        float nearExtent = 10f * 0.41421f;  // ≈ 4.142
        float farExtent = 100f * 0.41421f;  // ≈ 41.421

        // Near corners span ±nearExtent in Y and Z.
        c.Take(4).Max(p => p.Y).Should().BeApproximately(nearExtent, Tol);
        c.Take(4).Min(p => p.Y).Should().BeApproximately(-nearExtent, Tol);
        c.Take(4).Max(p => p.Z).Should().BeApproximately(nearExtent, Tol);

        // Far corners span ±farExtent.
        c.Skip(4).Max(p => p.Y).Should().BeApproximately(farExtent, Tol);
        c.Skip(4).Min(p => p.Z).Should().BeApproximately(-farExtent, Tol);
    }

    [Fact]
    public void Compute_WiderAspect_StretchesHorizontalExtentOnly()
    {
        // Aspect 2 doubles the horizontal (right-axis = world +Y here) extent; vertical (Z) is unchanged.
        Vector3[] c = FrustumSliceCorners.Compute(KnownCamera(), aspectRatio: 2f, sliceNear: 10f, sliceFar: 100f);

        float farH = 100f * 0.41421f;
        c.Skip(4).Max(p => p.Y).Should().BeApproximately(farH * 2f, 0.05f);
        c.Skip(4).Max(p => p.Z).Should().BeApproximately(farH, 0.05f);
    }
}