using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Builds the orthographic light view-projection for one Cascaded-Shadow-Map cascade: a directional-light
/// camera that looks along the sun direction and whose box hugs the cascade's frustum slice. The depth pass
/// renders the terrain through this matrix into the cascade's shadow map, and the terrain shader transforms
/// world positions by it to look the depth up. Fitting the box tightly to the slice keeps shadow-map texel
/// density high where the cascade actually covers.
/// </summary>
public static class CascadeLightMatrix
{
    /// <summary>
    /// Light view-projection for the cascade covering camera depths [<paramref name="sliceNear"/>,
    /// <paramref name="sliceFar"/>]. <paramref name="sunDirection"/> points from the surface toward the sun.
    /// <paramref name="depthPadding"/> pulls the near plane back toward the light so occluders above the
    /// slice still cast into it.
    /// </summary>
    public static Matrix4x4 Build(
        Camera3D camera, float aspectRatio, float sliceNear, float sliceFar, Vector3 sunDirection, float depthPadding = 0f)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Vector3[] corners = FrustumSliceCorners.Compute(camera, aspectRatio, sliceNear, sliceFar);

        Vector3 centre = Vector3.Zero;
        foreach (Vector3 c in corners)
        {
            centre += c;
        }
        centre /= corners.Length;

        float radius = 0f;
        foreach (Vector3 c in corners)
        {
            radius = MathF.Max(radius, Vector3.Distance(centre, c));
        }

        // Light looks along −sunDirection (sun shines from the sky down onto the scene). Eye sits above the
        // slice centre toward the sun; up falls back to world +Y when the sun is near-vertical (else the
        // look-at degenerates against world-Z up).
        Vector3 lightDir = Vector3.Normalize(sunDirection);
        Vector3 up = MathF.Abs(lightDir.Z) > 0.99f ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 eye = centre + (lightDir * radius);
        Matrix4x4 lightView = Matrix4x4.CreateLookAt(eye, centre, up);

        // Axis-aligned bounds of the slice in light view space (camera looks down −Z, so corners have z < 0).
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (Vector3 c in corners)
        {
            Vector3 lc = Vector3.Transform(c, lightView);
            min = Vector3.Min(min, lc);
            max = Vector3.Max(max, lc);
        }

        // View-space −Z is depth in front of the light; convert the z bounds to positive near/far distances.
        float nearDist = MathF.Max(0.01f, -max.Z - depthPadding);
        float farDist = -min.Z;
        Matrix4x4 ortho = Matrix4x4.CreateOrthographicOffCenter(min.X, max.X, min.Y, max.Y, nearDist, farDist);

        return lightView * ortho;
    }
}