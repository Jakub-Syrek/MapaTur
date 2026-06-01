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
    /// Lower bound on <see cref="Camera3D.PitchRadians"/> reachable through input (~20°). Keeps the
    /// camera looking down at the terrain: at a horizon-grazing or below-ground angle the
    /// painter's-algorithm sort and back-face cull both break down and the surface tears, and
    /// looking from underneath the mesh is never a useful map view anyway.
    /// </summary>
    public float MinPitchRadians { get; set; } = MathF.PI / 9f;

    /// <summary>
    /// Lower pitch bound for <see cref="ApplyLookAround"/> only (~-75°). Look-around rotates the
    /// view in place WITHOUT moving the camera position, so it carries none of the surface-tunnelling
    /// risk that forces <see cref="MinPitchRadians"/> to stay positive for orbit. The wider range lets
    /// the user tilt the gaze well above the horizon to take in the sky/clouds, or steeply down at
    /// their feet, without the camera flying through space.
    /// </summary>
    public float LookAroundMinPitchRadians { get; set; } = -1.3f;

    /// <summary>Radians of orbit per input-pixel.</summary>
    public float OrbitSensitivity { get; set; } = 0.005f;

    /// <summary>World-metres per input-pixel, per unit camera distance.</summary>
    public float PanSensitivity { get; set; } = 0.001f;

    /// <summary>Lower bound on <see cref="Camera3D.Distance"/>. Raised to 800 m so a pinch-in
    /// can't drive the camera through the surface of a typical mountain mesh (Tatra peaks reach
    /// ~5 km world-Z after vertical exaggeration; with the 20° pitch minimum, an 800 m distance
    /// keeps the camera safely outside the surface envelope at typical Target elevations).</summary>
    public float MinDistance { get; set; } = 800f;

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
        // Deliberately no EnforceCameraFloor here: an orbit is a rotation around a FIXED target,
        // so the camera's height tracks pitch — adding a floor lift made small pitch changes
        // produce visible distance jumps as the lift kicked in/out, which felt like the camera
        // was juddering. The MinDistance + clamped Target.Z together keep the camera safely above
        // ground for sane mesh + user input combinations.
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
        Camera.PitchRadians = Math.Clamp(Camera.PitchRadians + (dyPixels * OrbitSensitivity), LookAroundMinPitchRadians, MaxPitch);
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

    /// <summary>
    /// Minimum world-Z the camera position is allowed to reach. Default <see cref="float.NaN"/>
    /// disables the floor (no clamp). Set this to <c>mesh.MaxElevationZ + margin</c> so the camera
    /// can never zoom into / fly underneath the surface — without it, a pinch-in or a low-pitch
    /// orbit can plunge the camera through the highest peak and out the bottom of the world.
    /// </summary>
    public float CameraFloorZ { get; set; } = float.NaN;

    /// <summary>
    /// Allowed horizontal extent of <see cref="Camera3D.Target"/>. Defaults are wide-open
    /// (±infinity); set them to the mesh footprint so panning can't drag the focal point off the
    /// terrain and into empty space. <see cref="ApplyPan"/> clamps after every move.
    /// </summary>
    public float MinTargetX { get; set; } = float.NegativeInfinity;

    /// <inheritdoc cref="MinTargetX" />
    public float MaxTargetX { get; set; } = float.PositiveInfinity;

    /// <inheritdoc cref="MinTargetX" />
    public float MinTargetY { get; set; } = float.NegativeInfinity;

    /// <inheritdoc cref="MinTargetX" />
    public float MaxTargetY { get; set; } = float.PositiveInfinity;

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
    /// Result is clamped into the [Min..MaxTargetX × Min..MaxTargetY] footprint so the camera
    /// target can never wander off the loaded mesh.
    /// </summary>
    public void ApplyPan(float dxPixels, float dyPixels)
    {
        float scale = PanSensitivity * Camera.Distance;
        float cosA = MathF.Cos(Camera.AzimuthRadians);
        float sinA = MathF.Sin(Camera.AzimuthRadians);
        Vector3 right = new(-sinA, cosA, 0f);
        Vector3 forward = new(-cosA, -sinA, 0f);
        Vector3 newTarget = Camera.Target + (((right * dxPixels) + (forward * dyPixels)) * scale);
        newTarget = new Vector3(
            Math.Clamp(newTarget.X, MinTargetX, MaxTargetX),
            Math.Clamp(newTarget.Y, MinTargetY, MaxTargetY),
            newTarget.Z);
        Camera.Target = newTarget;
    }

    /// <summary>
    /// Minimum and maximum world-Z the target is allowed to reach via <see cref="ApplyVertical"/>.
    /// The lower bound is intentionally very negative because Target.Z and Camera.Position.Z are
    /// linked through the orbit offset (Pos.Z = Target.Z + Distance × sin(pitch)). With pitch=45°
    /// and Distance=30 km, Target.Z = −2 km still leaves the camera 19 km up — Wys. ▼ "stopped a
    /// kilometre above the ground". −50 km gives plenty of headroom for the camera to actually
    /// descend to ground level on any practical view. MaxTargetElevation 8 km keeps Wys. ▲ from
    /// chasing the target into the stratosphere.
    /// </summary>
    public float MinTargetElevation { get; set; } = -50_000f;

    /// <inheritdoc cref="MinTargetElevation" />
    public float MaxTargetElevation { get; set; } = 8_000f;

    /// <summary>
    /// Raises (positive) or lowers (negative) the camera target along world-Z,
    /// keeping the orbit otherwise intact. Same per-distance scaling as
    /// <see cref="ApplyPan"/> so the step covers a sensible vertical distance
    /// whether the camera is at 1 km or 100 km out. Clamped to
    /// [<see cref="MinTargetElevation"/>, <see cref="MaxTargetElevation"/>] so a runaway click
    /// can't push the target off the mesh.
    /// </summary>
    public void ApplyVertical(float dPixels)
    {
        float scale = PanSensitivity * Camera.Distance;
        float newZ = Camera.Target.Z + (dPixels * scale);
        newZ = Math.Clamp(newZ, MinTargetElevation, MaxTargetElevation);
        Camera.Target = new Vector3(Camera.Target.X, Camera.Target.Y, newZ);
    }
}