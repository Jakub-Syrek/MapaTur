using System.Numerics;

namespace MapaTur.Climbing;

/// <summary>
/// Gravity-relative joint measurements shared by the demo rig guardrails and future MapaTur pose adapters.
/// </summary>
public static class ClimbJointAnatomy
{
    public static float HeightAboveHip(Vector3 hip, Vector3 knee, Vector3 gravity)
    {
        float gravityLengthSquared = gravity.LengthSquared();
        if (gravityLengthSquared < 1e-8f)
        {
            return 0f;
        }

        Vector3 gravityUp = -gravity / MathF.Sqrt(gravityLengthSquared);
        return Vector3.Dot(knee - hip, gravityUp);
    }
}