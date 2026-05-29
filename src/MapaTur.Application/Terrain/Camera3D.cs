using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Orbit-style camera for 3D terrain rendering.
///
/// The camera is positioned at a given <see cref="Distance"/> from a
/// <see cref="Target"/> world point. Its position on the orbit sphere is
/// controlled by <see cref="AzimuthRadians"/> (rotation around the world Z
/// axis: 0 looks from +X toward the target, π/2 looks from +Y) and
/// <see cref="PitchRadians"/> (elevation above the horizon: 0 is horizontal,
/// π/2 is straight down from above).
///
/// World convention: X east, Y north, Z up. Right-handed.
/// </summary>
public sealed class Camera3D
{
    private const float MaxPitch = (MathF.PI / 2f) - 0.02f;  // ~89° — avoids singularity at zenith.

    /// <summary>World point the camera is aimed at.</summary>
    public Vector3 Target { get; set; } = Vector3.Zero;

    /// <summary>Distance from <see cref="Target"/> to the camera position.</summary>
    public float Distance { get; set; } = 1000f;

    /// <summary>Yaw angle around the world Z axis, in radians.</summary>
    public float AzimuthRadians { get; set; }

    /// <summary>Pitch above the horizon, in radians. Clamped to ±~89° internally.</summary>
    public float PitchRadians { get; set; } = MathF.PI / 4f;

    /// <summary>Vertical field of view in radians.</summary>
    public float FieldOfViewYRadians { get; set; } = MathF.PI / 4f;

    /// <summary>Near clipping plane distance.</summary>
    public float NearPlane { get; set; } = 10f;

    /// <summary>Far clipping plane distance.</summary>
    public float FarPlane { get; set; } = 1_000_000f;

    /// <summary>Up vector in world space (world Z up).</summary>
    public Vector3 Up => Vector3.UnitZ;

    /// <summary>Camera position computed from the orbit parameters.</summary>
    public Vector3 Position
    {
        get
        {
            float pitch = Math.Clamp(PitchRadians, -MaxPitch, MaxPitch);
            float cosP = MathF.Cos(pitch);
            float sinP = MathF.Sin(pitch);
            float cosA = MathF.Cos(AzimuthRadians);
            float sinA = MathF.Sin(AzimuthRadians);
            return new Vector3(
                Target.X + (Distance * cosP * cosA),
                Target.Y + (Distance * cosP * sinA),
                Target.Z + (Distance * sinP));
        }
    }

    /// <summary>Builds the view matrix from the current orbit parameters.</summary>
    public Matrix4x4 BuildViewMatrix() => Matrix4x4.CreateLookAt(Position, Target, Up);

    /// <summary>Builds the perspective projection matrix for the given aspect ratio.</summary>
    /// <param name="aspectRatio">Viewport width divided by height.</param>
    public Matrix4x4 BuildProjectionMatrix(float aspectRatio) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfViewYRadians, aspectRatio, NearPlane, FarPlane);

    /// <summary>Builds the combined view * projection matrix for the given aspect ratio.</summary>
    /// <remarks>
    /// Compute this once per frame and pass it to the matrix-accepting <see cref="ProjectToScreen(Vector3, Matrix4x4, float, float)"/>
    /// overload to avoid rebuilding view and projection per vertex on hot paths
    /// (trail/route overlays, mesh projection).
    /// </remarks>
    public Matrix4x4 BuildViewProjection(float aspectRatio) =>
        BuildViewMatrix() * BuildProjectionMatrix(aspectRatio);

    /// <summary>
    /// Projects a world-space point to screen pixel coordinates.
    /// Returns null if the point is behind the camera, beyond the far plane,
    /// or otherwise outside the homogeneous clip space.
    /// </summary>
    /// <param name="worldPoint">Point in world space.</param>
    /// <param name="screenWidth">Viewport width in pixels.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    /// <returns>(x, y, depthNdc) where x/y are pixels and depthNdc is in [0,1] for visible points.</returns>
    public Vector3? ProjectToScreen(Vector3 worldPoint, float screenWidth, float screenHeight)
    {
        if (screenHeight <= 0f || screenWidth <= 0f)
        {
            return null;
        }

        Matrix4x4 viewProjection = BuildViewProjection(screenWidth / screenHeight);
        return ProjectToScreen(worldPoint, viewProjection, screenWidth, screenHeight);
    }

    /// <summary>
    /// Projects a world-space point to screen pixel coordinates using a precomputed
    /// view-projection matrix. Use this in inner loops (trail/route/mesh vertices)
    /// to avoid the per-call cost of <see cref="BuildViewMatrix"/> and
    /// <see cref="BuildProjectionMatrix"/>.
    /// </summary>
    /// <param name="worldPoint">Point in world space.</param>
    /// <param name="viewProjection">Precomputed view * projection matrix (see <see cref="BuildViewProjection"/>).</param>
    /// <param name="screenWidth">Viewport width in pixels.</param>
    /// <param name="screenHeight">Viewport height in pixels.</param>
    /// <returns>(x, y, depthNdc) where x/y are pixels and depthNdc is in [0,1] for visible points.</returns>
    public Vector3? ProjectToScreen(Vector3 worldPoint, Matrix4x4 viewProjection, float screenWidth, float screenHeight)
    {
        if (screenHeight <= 0f || screenWidth <= 0f)
        {
            return null;
        }

        Vector4 clip = Vector4.Transform(new Vector4(worldPoint, 1f), viewProjection);

        if (clip.W <= 0f)
        {
            return null;
        }

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        float ndcZ = clip.Z / clip.W;

        if (ndcZ < 0f || ndcZ > 1f)
        {
            return null;
        }

        float screenX = (ndcX + 1f) * 0.5f * screenWidth;
        float screenY = (1f - ndcY) * 0.5f * screenHeight;
        return new Vector3(screenX, screenY, ndcZ);
    }
}