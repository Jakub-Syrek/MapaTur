using System.Numerics;

namespace MapaTur.Climbing;

/// <summary>
/// Anatomical direction constraint for the shoe/forefoot after positional leg IK. It prevents an end-effector-only
/// solve from folding the visible foot back along the shin or rotating it arbitrarily with the lower leg.
/// </summary>
public static class ClimbAnkleOrientation
{
    public static Vector3 ConstrainToeDirection(
        Vector3 knee,
        Vector3 ankle,
        Vector3 neutralToe,
        Vector3 desiredToe,
        float minimumShinToeAngleDegrees = 70f,
        float maximumShinToeAngleDegrees = 135f,
        float maximumRotationFromNeutralDegrees = 45f)
    {
        Vector3 shinDirection = NormalizeOr(knee - ankle, Vector3.UnitY);
        Vector3 neutralDirection = NormalizeOr(neutralToe - ankle, PerpendicularTo(shinDirection));
        Vector3 desiredDirection = NormalizeOr(desiredToe - ankle, neutralDirection);
        float minimumAngle = Math.Clamp(minimumShinToeAngleDegrees, 1f, 179f) * (MathF.PI / 180f);
        float maximumAngle = Math.Clamp(maximumShinToeAngleDegrees, minimumShinToeAngleDegrees, 179f)
            * (MathF.PI / 180f);
        float desiredAngle = MathF.Acos(Math.Clamp(Vector3.Dot(shinDirection, desiredDirection), -1f, 1f));
        float constrainedAngle = Math.Clamp(desiredAngle, minimumAngle, maximumAngle);
        Vector3 desiredPerpendicular = desiredDirection
            - (shinDirection * Vector3.Dot(desiredDirection, shinDirection));
        if (desiredPerpendicular.LengthSquared() < 1e-8f)
        {
            desiredPerpendicular = neutralDirection
                - (shinDirection * Vector3.Dot(neutralDirection, shinDirection));
        }

        desiredPerpendicular = NormalizeOr(desiredPerpendicular, PerpendicularTo(shinDirection));
        Vector3 anatomicalDirection = (shinDirection * MathF.Cos(constrainedAngle))
            + (desiredPerpendicular * MathF.Sin(constrainedAngle));
        float maximumNeutralRotation = Math.Clamp(maximumRotationFromNeutralDegrees, 0f, 179f)
            * (MathF.PI / 180f);
        Vector3 neutralLimited = RotateToward(neutralDirection, anatomicalDirection, maximumNeutralRotation);
        // The rig's authored bind pose is not guaranteed to be an anatomical neutral (the purchased asset has one
        // foot already strongly plantarflexed). The shin-toe cone is therefore the hard safety constraint and wins
        // over the softer rotation-from-bind preference.
        return ClampToShinCone(
            neutralLimited,
            shinDirection,
            desiredPerpendicular,
            minimumAngle,
            maximumAngle);
    }

    private static Vector3 ClampToShinCone(
        Vector3 direction,
        Vector3 shinDirection,
        Vector3 fallbackPerpendicular,
        float minimumAngle,
        float maximumAngle)
    {
        float angle = MathF.Acos(Math.Clamp(Vector3.Dot(shinDirection, direction), -1f, 1f));
        float clampedAngle = Math.Clamp(angle, minimumAngle, maximumAngle);
        if (MathF.Abs(clampedAngle - angle) < 1e-5f)
        {
            return direction;
        }

        Vector3 perpendicular = direction - (shinDirection * Vector3.Dot(direction, shinDirection));
        perpendicular = NormalizeOr(perpendicular, fallbackPerpendicular);
        return Vector3.Normalize(
            (shinDirection * MathF.Cos(clampedAngle))
            + (perpendicular * MathF.Sin(clampedAngle)));
    }

    private static Vector3 RotateToward(Vector3 from, Vector3 to, float maximumAngle)
    {
        float dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        float angle = MathF.Acos(dot);
        if (angle < 1e-5f || angle <= maximumAngle)
        {
            return NormalizeOr(to, from);
        }

        float amount = maximumAngle / angle;
        float sine = MathF.Sin(angle);
        if (MathF.Abs(sine) < 1e-5f)
        {
            Vector3 axis = PerpendicularTo(from);
            return Vector3.Normalize(
                (from * MathF.Cos(maximumAngle))
                + (axis * MathF.Sin(maximumAngle)));
        }

        float fromWeight = MathF.Sin((1f - amount) * angle) / sine;
        float toWeight = MathF.Sin(amount * angle) / sine;
        return Vector3.Normalize((from * fromWeight) + (to * toWeight));
    }

    private static Vector3 PerpendicularTo(Vector3 direction)
    {
        Vector3 reference = MathF.Abs(direction.X) < 0.80f ? Vector3.UnitX : Vector3.UnitZ;
        return Vector3.Normalize(Vector3.Cross(direction, reference));
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
    {
        float lengthSquared = value.LengthSquared();
        return lengthSquared > 1e-8f ? value / MathF.Sqrt(lengthSquared) : fallback;
    }
}