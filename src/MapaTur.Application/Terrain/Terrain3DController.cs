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

    /// <summary>
    /// Lower bound on <see cref="Camera3D.PitchRadians"/> reachable through input (~10°). Keeps the
    /// camera looking down at the terrain: at a horizon-grazing or below-ground angle the
    /// painter's-algorithm sort and back-face cull both break down and the surface tears.
    /// </summary>
    public float MinPitchRadians { get; set; } = MathF.PI / 18f;

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
        Camera.PitchRadians = Math.Clamp(newPitch, MinPitchRadians, MaxPitch);
    }

    /// <summary>
    /// Look-around in place: rotates the view direction (azimuth/pitch) while keeping the camera
    /// <see cref="Camera3D.Position"/> fixed — "I stand still and turn my head", as opposed to
    /// <see cref="ApplyOrbit"/> which circles the camera around the target. Implemented by swinging the
    /// target so the recomputed orbit position lands back on the original spot.
    /// </summary>
    public void ApplyLookAround(float dxPixels, float dyPixels)
    {
        Vector3 position = Camera.Position;
        // Looking around turns the head: dragging right should swing the view right, the opposite azimuth
        // sign to ApplyOrbit (which circles the camera the other way). dy keeps its sign — dragging up looks up.
        Camera.AzimuthRadians -= dxPixels * OrbitSensitivity;
        Camera.PitchRadians = Math.Clamp(Camera.PitchRadians + (dyPixels * OrbitSensitivity), MinPitchRadians, MaxPitch);
        Camera.Target = position - OrbitOffset();
    }

    // Offset from target to camera position for the current orbit angles (mirrors Camera3D.Position).
    private Vector3 OrbitOffset()
    {
        float cosP = MathF.Cos(Camera.PitchRadians);
        float sinP = MathF.Sin(Camera.PitchRadians);
        float cosA = MathF.Cos(Camera.AzimuthRadians);
        float sinA = MathF.Sin(Camera.AzimuthRadians);
        return new Vector3(
            Camera.Distance * cosP * cosA,
            Camera.Distance * cosP * sinA,
            Camera.Distance * sinP);
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

    /// <summary>
    /// Raises (positive) or lowers (negative) the camera target along world-Z,
    /// keeping the orbit otherwise intact. Same per-distance scaling as
    /// <see cref="ApplyPan"/> so the step covers a sensible vertical distance
    /// whether the camera is at 1 km or 100 km out.
    /// </summary>
    public void ApplyVertical(float dPixels)
    {
        float scale = PanSensitivity * Camera.Distance;
        Camera.Target = new Vector3(Camera.Target.X, Camera.Target.Y, Camera.Target.Z + (dPixels * scale));
    }
}