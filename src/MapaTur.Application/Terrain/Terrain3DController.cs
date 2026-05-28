using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Translates pointer/gesture input deltas into <see cref="Camera3D"/> mutations.
/// Pure math, no UI dependencies — safe to unit-test.
/// </summary>
public sealed class Terrain3DController
{
    private const float MaxPitch = (MathF.PI / 2f) - 0.02f;

    /// <summary>Camera the controller mutates in place.</summary>
    public Camera3D Camera { get; }

    /// <summary>Radians of orbit per input-pixel.</summary>
    public float OrbitSensitivity { get; set; } = 0.005f;

    /// <summary>World-metres per input-pixel, per unit camera distance.</summary>
    public float PanSensitivity { get; set; } = 0.001f;

    /// <summary>Lower bound on <see cref="Camera3D.Distance"/>.</summary>
    public float MinDistance { get; set; } = 100f;

    /// <summary>Upper bound on <see cref="Camera3D.Distance"/>.</summary>
    public float MaxDistance { get; set; } = 500_000f;

    public Terrain3DController(Camera3D camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Camera = camera;
    }

    /// <summary>Drag-orbit: <paramref name="dxPixels"/> rotates azimuth, <paramref name="dyPixels"/> tilts pitch (clamped).</summary>
    public void ApplyOrbit(float dxPixels, float dyPixels)
    {
        Camera.AzimuthRadians += dxPixels * OrbitSensitivity;
        float newPitch = Camera.PitchRadians + (dyPixels * OrbitSensitivity);
        Camera.PitchRadians = Math.Clamp(newPitch, -MaxPitch, MaxPitch);
    }

    /// <summary>Pinch-zoom: <paramref name="scale"/> &gt; 1 brings the camera closer (divides distance).</summary>
    public void ApplyZoom(float scale)
    {
        if (scale <= 0f)
        {
            return;
        }

        Camera.Distance = Math.Clamp(Camera.Distance / scale, MinDistance, MaxDistance);
    }

    /// <summary>
    /// Two-finger pan: translates <see cref="Camera3D.Target"/> in the ground plane,
    /// with magnitude proportional to current distance so far-zoom pans cover more ground.
    /// </summary>
    public void ApplyPan(float dxPixels, float dyPixels)
    {
        float scale = PanSensitivity * Camera.Distance;
        float cosA = MathF.Cos(Camera.AzimuthRadians);
        float sinA = MathF.Sin(Camera.AzimuthRadians);
        Vector3 right = new(-sinA, cosA, 0f);
        Vector3 forward = new(-cosA, -sinA, 0f);
        Camera.Target += ((right * dxPixels) + (forward * dyPixels)) * scale;
    }
}