using System.Numerics;

namespace MapaTur.Climbing;

/// <summary>
/// Boundary owned by a terrain renderer. MapaTur will later implement this from a local terrain mesh and its
/// generated micro-holds; the climbing rules never need to know about DEM tiles, OpenGL, or a UI framework.
/// </summary>
public interface IClimbSurface
{
    Vector3 ProjectToSurface(Vector3 worldPosition);

    /// <summary>
    /// Samples the local tangent frame. A MapaTur adapter should use the hit triangle's interpolated position and
    /// normal here; no terrain mesh or renderer type leaks into the climbing module.
    /// </summary>
    ClimbSurfaceFrame SampleSurface(Vector3 worldPosition, Vector3 gravity);

    IReadOnlyList<ClimbHold> FindHolds(Vector3 worldPosition, float radius);
}

public sealed class InMemoryClimbSurface : IClimbSurface
{
    private readonly ClimbHold[] holds;
    private readonly Vector3 normal;

    public InMemoryClimbSurface(IEnumerable<ClimbHold> holds, Vector3? normal = null)
    {
        this.holds = holds.ToArray();
        Vector3 candidate = normal ?? Vector3.UnitZ;
        this.normal = candidate.LengthSquared() > 1e-8f ? Vector3.Normalize(candidate) : Vector3.UnitZ;
    }

    public Vector3 ProjectToSurface(Vector3 worldPosition) =>
        worldPosition - (normal * Vector3.Dot(worldPosition, normal));

    public ClimbSurfaceFrame SampleSurface(Vector3 worldPosition, Vector3 gravity) =>
        ClimbSurfaceFrame.Create(ProjectToSurface(worldPosition), normal, gravity);

    public IReadOnlyList<ClimbHold> FindHolds(Vector3 worldPosition, float radius) =>
        holds.Where(hold => Vector3.Distance(hold.Position, worldPosition) <= radius).ToArray();
}