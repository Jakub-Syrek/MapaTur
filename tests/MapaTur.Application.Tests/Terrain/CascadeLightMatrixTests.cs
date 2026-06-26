using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="CascadeLightMatrix"/>: the orthographic light view-projection that a
/// Cascaded-Shadow-Map cascade renders the depth pass through. It must look along the sun direction and
/// bound the cascade's frustum slice tightly — every slice corner inside the light's NDC box, and the box
/// no larger than it needs to be (so shadow-map texels aren't wasted).
/// </summary>
public sealed class CascadeLightMatrixTests
{
    private static Camera3D KnownCamera() => new()
    {
        Target = Vector3.Zero,
        Distance = 1000f,
        AzimuthRadians = 0.7f,
        PitchRadians = 0.5f,
        FieldOfViewYRadians = MathF.PI / 4f,
    };

    private static readonly Vector3 Sun = Vector3.Normalize(new Vector3(0.3f, 0.3f, 1f));

    private static Vector2 ClipXY(Matrix4x4 m, Vector3 p)
    {
        Vector4 c = Vector4.Transform(new Vector4(p, 1f), m);
        return new Vector2(c.X / c.W, c.Y / c.W);
    }

    [Fact]
    public void Build_BoundsEverySliceCornerWithinLightNdc()
    {
        Matrix4x4 lightVp = CascadeLightMatrix.Build(KnownCamera(), aspectRatio: 1.6f, sliceNear: 10f, sliceFar: 200f, Sun);
        Vector3[] corners = FrustumSliceCorners.Compute(KnownCamera(), 1.6f, 10f, 200f);

        foreach (Vector3 corner in corners)
        {
            Vector2 ndc = ClipXY(lightVp, corner);
            ndc.X.Should().BeInRange(-1.001f, 1.001f);
            ndc.Y.Should().BeInRange(-1.001f, 1.001f);
        }
    }

    [Fact]
    public void Build_FitsTheSliceTightly()
    {
        // The orthographic box should hug the slice: the extreme corners reach the NDC edges (±1), so no
        // shadow-map resolution is wasted on empty margin.
        Matrix4x4 lightVp = CascadeLightMatrix.Build(KnownCamera(), 1.6f, 10f, 200f, Sun);
        Vector3[] corners = FrustumSliceCorners.Compute(KnownCamera(), 1.6f, 10f, 200f);

        float maxAbsX = corners.Max(c => MathF.Abs(ClipXY(lightVp, c).X));
        float maxAbsY = corners.Max(c => MathF.Abs(ClipXY(lightVp, c).Y));

        maxAbsX.Should().BeApproximately(1f, 0.02f);
        maxAbsY.Should().BeApproximately(1f, 0.02f);
    }

    [Fact]
    public void Build_IsFinite()
    {
        Matrix4x4 m = CascadeLightMatrix.Build(KnownCamera(), 1.6f, 10f, 200f, Sun);

        foreach (float v in new[] { m.M11, m.M22, m.M33, m.M44, m.M41, m.M42, m.M43 })
        {
            float.IsFinite(v).Should().BeTrue();
        }
    }

    [Fact]
    public void Build_HandlesNearVerticalSun()
    {
        // Sun almost straight overhead: the up-vector fallback must keep the look-at non-degenerate.
        var overhead = Vector3.Normalize(new Vector3(0.01f, 0.01f, 1f));

        Matrix4x4 m = CascadeLightMatrix.Build(KnownCamera(), 1.6f, 10f, 200f, overhead);
        Vector3[] corners = FrustumSliceCorners.Compute(KnownCamera(), 1.6f, 10f, 200f);

        foreach (Vector3 corner in corners)
        {
            ClipXY(m, corner).X.Should().BeInRange(-1.01f, 1.01f);
        }
    }
}