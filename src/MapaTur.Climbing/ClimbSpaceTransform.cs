using System.Numerics;

namespace MapaTur.Climbing;

/// <summary>World constants for the MapaTur climb simulation space.</summary>
public static class ClimbWorld
{
    /// <summary>Gravity in real-world metres, X-east / Y-north / Z-up.</summary>
    public static Vector3 Gravity => new(0f, 0f, -9.81f);
}

/// <summary>
/// Converts between ClimbSpace (real-world metres, the only space climb solvers may see)
/// and RenderSpace (Z multiplied by the terrain's vertical exaggeration). Normals do not
/// scale like points: they transform with the inverse-transpose of the point scale.
/// </summary>
public readonly struct ClimbSpaceTransform
{
    public ClimbSpaceTransform(float verticalExaggeration)
    {
        if (!(verticalExaggeration > 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(verticalExaggeration), verticalExaggeration, "Vertical exaggeration must be positive.");
        }

        VerticalExaggeration = verticalExaggeration;
    }

    public static ClimbSpaceTransform Identity { get; } = new(1f);

    public float VerticalExaggeration { get; }

    public Vector3 ToClimbPoint(Vector3 renderPoint)
        => new(renderPoint.X, renderPoint.Y, renderPoint.Z / VerticalExaggeration);

    public Vector3 ToRenderPoint(Vector3 climbPoint)
        => new(climbPoint.X, climbPoint.Y, climbPoint.Z * VerticalExaggeration);

    public Vector3 ToClimbNormal(Vector3 renderNormal)
        => Vector3.Normalize(new Vector3(renderNormal.X, renderNormal.Y, renderNormal.Z * VerticalExaggeration));

    public Vector3 ToRenderNormal(Vector3 climbNormal)
        => Vector3.Normalize(new Vector3(climbNormal.X, climbNormal.Y, climbNormal.Z / VerticalExaggeration));
}