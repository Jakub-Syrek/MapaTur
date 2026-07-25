using System.Numerics;

namespace MapaTur.Climbing;

/// <summary>
/// A reach envelope for a planted limb. <see cref="Proximal"/> is the shoulder or hip, while
/// <see cref="Contact"/> remains fixed on the climbing surface.
/// </summary>
public sealed record ClimbReachConstraint(
    ClimbLimb Limb,
    Vector3 Proximal,
    Vector3 Contact,
    float MaximumReachMeters);

public sealed record ClimbHangingRootResult(
    float AdditionalGravityDropMeters,
    float TargetArmExtensionRatio,
    float MaximumPermittedDropMeters);

/// <summary>
/// Places the body below its planted hands instead of using elbow flexion to absorb an arbitrarily high root pose.
/// The calculation is gravity-relative and renderer-independent, so it works in both the viewer's Y-up coordinates
/// and MapaTur's Z-up world.
/// </summary>
public static class ClimbHangingRootSolver
{
    public static ClimbHangingRootResult RecommendAdditionalDrop(
        IEnumerable<ClimbReachConstraint> constraints,
        Vector3 gravity,
        float targetArmExtensionRatio = 0.985f,
        float maximumExtensionRatio = 0.995f,
        float maximumAdditionalDropMeters = 0.55f)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        ClimbReachConstraint[] planted = constraints
            .Where(constraint => constraint.MaximumReachMeters > 0.0001f)
            .ToArray();
        Vector3 gravityUp = NormalizeOrZero(-gravity);
        float targetRatio = Math.Clamp(targetArmExtensionRatio, 0.70f, 0.995f);
        float maximumRatio = Math.Clamp(maximumExtensionRatio, targetRatio, 0.999f);
        float dropLimit = Math.Max(0f, maximumAdditionalDropMeters);
        if (planted.Length == 0 || gravityUp == Vector3.Zero || dropLimit <= 0f)
        {
            return new ClimbHangingRootResult(0f, targetRatio, 0f);
        }

        float desiredDrop = planted
            .Where(constraint => constraint.Limb.IsHand())
            .Select(constraint => RequiredDropForTarget(constraint, gravityUp, targetRatio))
            .DefaultIfEmpty(0f)
            .Max();
        float maximumPermittedDrop = planted
            .Select(constraint => MaximumDropInsideReach(constraint, gravityUp, maximumRatio))
            .DefaultIfEmpty(dropLimit)
            .Min();
        maximumPermittedDrop = Math.Clamp(maximumPermittedDrop, 0f, dropLimit);
        float additionalDrop = Math.Clamp(desiredDrop, 0f, maximumPermittedDrop);

        return new ClimbHangingRootResult(additionalDrop, targetRatio, maximumPermittedDrop);
    }

    private static float RequiredDropForTarget(
        ClimbReachConstraint constraint,
        Vector3 gravityUp,
        float targetRatio)
    {
        Vector3 proximalToContact = constraint.Proximal - constraint.Contact;
        float currentDistance = proximalToContact.Length();
        float targetDistance = constraint.MaximumReachMeters * targetRatio;
        if (currentDistance >= targetDistance)
        {
            return 0f;
        }

        float alongGravityUp = Vector3.Dot(proximalToContact, gravityUp);
        // A passive hang assumes the hand is above the shoulder. If it is below the shoulder, translating the whole
        // body downward would initially shorten the arm and then pass beneath the hold, which is not this pose mode.
        if (alongGravityUp >= 0f)
        {
            return 0f;
        }

        float perpendicularSquared = MathF.Max(
            0f,
            proximalToContact.LengthSquared() - (alongGravityUp * alongGravityUp));
        float targetSquared = targetDistance * targetDistance;
        if (perpendicularSquared >= targetSquared)
        {
            return 0f;
        }

        return MathF.Max(0f, alongGravityUp + MathF.Sqrt(targetSquared - perpendicularSquared));
    }

    private static float MaximumDropInsideReach(
        ClimbReachConstraint constraint,
        Vector3 gravityUp,
        float maximumRatio)
    {
        Vector3 proximalToContact = constraint.Proximal - constraint.Contact;
        float alongGravityUp = Vector3.Dot(proximalToContact, gravityUp);
        float perpendicularSquared = MathF.Max(
            0f,
            proximalToContact.LengthSquared() - (alongGravityUp * alongGravityUp));
        float maximumDistance = constraint.MaximumReachMeters * maximumRatio;
        float maximumSquared = maximumDistance * maximumDistance;
        if (perpendicularSquared >= maximumSquared)
        {
            return 0f;
        }

        return MathF.Max(0f, alongGravityUp + MathF.Sqrt(maximumSquared - perpendicularSquared));
    }

    private static Vector3 NormalizeOrZero(Vector3 value)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-8f ? value / MathF.Sqrt(lengthSquared) : Vector3.Zero;
    }
}