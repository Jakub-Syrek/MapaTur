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

    /// <summary>Lower bound on <see cref="Camera3D.Distance"/>. Low (150 m) so the camera can descend close
    /// to the ground; tunnelling under the surface is prevented by the per-frame LOCAL floor
    /// (<see cref="CameraFloorZ"/>) instead of a blanket large min-distance, which used to strand the camera
    /// ~500 m up.</summary>
    public float MinDistance { get; set; } = 150f;

    /// <summary>Upper bound on <see cref="Camera3D.Distance"/>.</summary>
    public float MaxDistance { get; set; } = 500_000f;

    /// <summary>
    /// Floor on the camera distance used to scale the <see cref="ApplyVertical"/> step. The vertical
    /// step is normally proportional to <see cref="Camera3D.Distance"/> (far view → coarse, close view →
    /// fine), but a pinch-zoomed-in camera has a tiny Distance, which shrank the step to almost nothing —
    /// the raise/lower arrows "stopped working" once you zoomed in. Clamping the distance factor to at
    /// least this keeps a usable vertical step at any zoom while still scaling up for far-out views.
    /// </summary>
    public float VerticalStepMinDistance { get; set; } = 4_000f;

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
        ClampCameraOverMap();
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
        ClampCameraOverMap();
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
    /// Maximum world-Z the camera EYE (<see cref="Camera3D.Position"/>) is allowed to reach — a hard
    /// altitude ceiling. Default <see cref="float.NaN"/> disables it. Set to <c>ceilingAltitudeMeters ×
    /// exaggeration</c> so "raise" / zoom-out can't fly the camera off above the scene; the rig is lowered
    /// (Target.Z) until the eye sits back at the ceiling.
    /// </summary>
    public float CameraCeilingZ { get; set; } = float.NaN;

    /// <summary>
    /// World-space footprint the camera EYE (<see cref="Camera3D.Position"/>) is kept inside — set to the
    /// mesh's actual rectangle so the camera can never fly off the terrain into empty space. Defaults are
    /// wide-open (±infinity = no clamp). Every gesture re-applies <see cref="ClampCameraOverMap"/>.
    /// NOTE: this bounds the EYE, not the look-target. Clamping the target instead leaves the eye free to
    /// swing a full orbit-radius past the edge; clamping BOTH makes the far side unreachable.
    /// </summary>
    public float MinTargetX { get; set; } = float.NegativeInfinity;

    /// <inheritdoc cref="MinTargetX" />
    public float MaxTargetX { get; set; } = float.PositiveInfinity;

    /// <inheritdoc cref="MinTargetX" />
    public float MinTargetY { get; set; } = float.NegativeInfinity;

    /// <inheritdoc cref="MinTargetX" />
    public float MaxTargetY { get; set; } = float.PositiveInfinity;

    /// <summary>
    /// Keeps the camera EYE over the map footprint by translating the whole rig (eye + target together)
    /// back inside when an orbit / zoom / pan / tilt would carry it off the terrain. Translating the target
    /// (Position = Target + orbit offset) preserves the look direction, distance and pitch, so the boundary
    /// reads as a soft wall rather than a snap. No-op while the footprint is unbounded.
    /// </summary>
    private void ClampCameraOverMap()
    {
        if (float.IsInfinity(MinTargetX) || float.IsInfinity(MaxTargetX)
            || float.IsInfinity(MinTargetY) || float.IsInfinity(MaxTargetY))
        {
            return;
        }

        Vector3 pos = Camera.Position;
        float dx = Math.Clamp(pos.X, MinTargetX, MaxTargetX) - pos.X;
        float dy = Math.Clamp(pos.Y, MinTargetY, MaxTargetY) - pos.Y;
        if (dx != 0f || dy != 0f)
        {
            Camera.Target = new Vector3(Camera.Target.X + dx, Camera.Target.Y + dy, Camera.Target.Z);
        }
    }

    /// <summary>
    /// Stops the camera EYE from dropping below <see cref="CameraFloorZ"/> (set above the highest terrain),
    /// i.e. from going UNDER the map. Lifts the look-point (Target.Z) just enough that Position.Z = floor.
    /// Applied on the altitude controls (zoom, vertical), not orbit — a per-pitch lift there would judder
    /// the distance. No-op while the floor is unset (NaN).
    /// </summary>
    private void ClampCameraFloor()
    {
        if (float.IsNaN(CameraFloorZ))
        {
            return;
        }

        float posZ = Camera.Position.Z;
        if (posZ >= CameraFloorZ)
        {
            return;
        }

        Camera.Target = new Vector3(Camera.Target.X, Camera.Target.Y, Camera.Target.Z + (CameraFloorZ - posZ));
    }

    /// <summary>
    /// Stops the camera EYE from rising above <see cref="CameraCeilingZ"/> — a hard altitude ceiling so
    /// "raise" / zoom-out can't fly the camera off above the scene. Lowers the look-point (Target.Z) just
    /// enough that Position.Z = ceiling. No-op while the ceiling is unset (NaN).
    /// </summary>
    private void ClampCameraCeiling()
    {
        if (float.IsNaN(CameraCeilingZ))
        {
            return;
        }

        float posZ = Camera.Position.Z;
        if (posZ <= CameraCeilingZ)
        {
            return;
        }

        Camera.Target = new Vector3(Camera.Target.X, Camera.Target.Y, Camera.Target.Z - (posZ - CameraCeilingZ));
    }

    /// <summary>
    /// Re-applies the distance, floor, ceiling and over-map bounds to the CURRENT camera. Call after framing
    /// a mesh or restoring a saved camera so a stale state can't start the view off / under / above the map.
    /// </summary>
    public void ClampToBounds()
    {
        Camera.Distance = Math.Clamp(Camera.Distance, MinDistance, MaxDistance);
        ClampCameraFloor();
        ClampCameraCeiling();
        ClampCameraOverMap();
    }

    /// <summary>Pinch-zoom: <paramref name="scale"/> &gt; 1 brings the camera closer (divides distance).</summary>
    public void ApplyZoom(float scale)
    {
        if (scale <= 0f)
        {
            return;
        }

        Camera.Distance = Math.Clamp(Camera.Distance / scale, MinDistance, MaxDistance);
        ClampCameraFloor();
        ClampCameraOverMap();
    }

    /// <summary>
    /// Two-finger pan: translates <see cref="Camera3D.Target"/> in the ground plane, with magnitude
    /// proportional to current distance so far-zoom pans cover more ground. The EYE is then clamped into
    /// the footprint (<see cref="ClampCameraOverMap"/>); the target itself is left unclamped so a low-pitch
    /// / far-zoom view can still reach the far side of the map.
    /// </summary>
    public void ApplyPan(float dxPixels, float dyPixels)
    {
        float scale = PanSensitivity * Camera.Distance;
        float cosA = MathF.Cos(Camera.AzimuthRadians);
        float sinA = MathF.Sin(Camera.AzimuthRadians);
        Vector3 right = new(-sinA, cosA, 0f);
        Vector3 forward = new(-cosA, -sinA, 0f);
        Camera.Target += ((right * dxPixels) + (forward * dyPixels)) * scale;
        ClampCameraOverMap();
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
        // Scale the step by distance, but never below VerticalStepMinDistance, so the arrows keep moving
        // the camera a usable amount even when pinch-zoomed right in (where Camera.Distance is tiny).
        float scale = PanSensitivity * MathF.Max(Camera.Distance, VerticalStepMinDistance);
        float newZ = Camera.Target.Z + (dPixels * scale);
        newZ = Math.Clamp(newZ, MinTargetElevation, MaxTargetElevation);
        Camera.Target = new Vector3(Camera.Target.X, Camera.Target.Y, newZ);
        ClampCameraFloor();
        ClampCameraOverMap();
    }
}