using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Computes the 8 world-space corners of a camera frustum slice between two depths. Cascaded Shadow Maps
/// fit each cascade's orthographic light box to one such slice, so this is the geometric input to
/// <c>CascadeLightMatrix</c>. The camera basis is rebuilt from its orbit parameters (same look-at the
/// renderer uses): forward = Target − Position, right = forward × up, up = right × forward.
/// </summary>
public static class FrustumSliceCorners
{
    /// <summary>
    /// The 8 corners, near plane first then far plane, each as (bottom-left, bottom-right, top-right,
    /// top-left) relative to the camera's right/up axes. World convention X east, Y north, Z up.
    /// </summary>
    public static Vector3[] Compute(Camera3D camera, float aspectRatio, float sliceNear, float sliceFar)
    {
        ArgumentNullException.ThrowIfNull(camera);

        Vector3 pos = camera.Position;
        Vector3 forward = Vector3.Normalize(camera.Target - pos);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, camera.Up));
        Vector3 up = Vector3.Cross(right, forward);

        float tanHalfV = MathF.Tan(camera.FieldOfViewYRadians * 0.5f);
        float halfHNear = sliceNear * tanHalfV;
        float halfWNear = halfHNear * aspectRatio;
        float halfHFar = sliceFar * tanHalfV;
        float halfWFar = halfHFar * aspectRatio;

        Vector3 centreNear = pos + (forward * sliceNear);
        Vector3 centreFar = pos + (forward * sliceFar);

        return new[]
        {
            centreNear - (right * halfWNear) - (up * halfHNear),
            centreNear + (right * halfWNear) - (up * halfHNear),
            centreNear + (right * halfWNear) + (up * halfHNear),
            centreNear - (right * halfWNear) + (up * halfHNear),
            centreFar - (right * halfWFar) - (up * halfHFar),
            centreFar + (right * halfWFar) - (up * halfHFar),
            centreFar + (right * halfWFar) + (up * halfHFar),
            centreFar - (right * halfWFar) + (up * halfHFar),
        };
    }
}