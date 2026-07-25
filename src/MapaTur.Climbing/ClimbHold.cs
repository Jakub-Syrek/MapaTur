using System.Numerics;

namespace MapaTur.Climbing;

public enum ClimbHoldType
{
    Jug,
    Crimp,
    Sloper,
    Pinch,
    Pocket,
    FootEdge
}

/// <summary>A grippable point attached to a climbing surface. Quality represents friction and usable shape.</summary>
public sealed record ClimbHold
{
    public ClimbHold(
        string id,
        Vector3 position,
        Vector3 normal,
        float quality,
        ClimbHoldType type = ClimbHoldType.Jug,
        float? usableWidthMeters = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        Id = id;
        Position = position;
        Normal = normal.LengthSquared() < float.Epsilon ? Vector3.UnitZ : Vector3.Normalize(normal);
        Quality = Math.Clamp(quality, 0.05f, 1f);
        Type = type;
        UsableWidthMeters = Math.Clamp(usableWidthMeters ?? DefaultUsableWidthMeters(type), 0.02f, 1.5f);
    }

    public string Id { get; }

    public Vector3 Position { get; }

    public Vector3 Normal { get; }

    public float Quality { get; }

    public ClimbHoldType Type { get; }

    /// <summary>
    /// Tangential width that can actually carry contacts. This belongs to the physical hold, not to a route side or
    /// a preferred limb. A MapaTur adapter should derive it from the local feature/mesh footprint.
    /// </summary>
    public float UsableWidthMeters { get; }

    /// <summary>Number of hands that can share the hold in a deliberate match.</summary>
    public int HandContactCapacity => Type switch
    {
        ClimbHoldType.Pocket or ClimbHoldType.Pinch or ClimbHoldType.FootEdge => 1,
        _ => UsableWidthMeters >= 0.32f ? 2 : 1
    };

    /// <summary>Number of feet that can share the hold (both feet on one wide ledge/edge).</summary>
    public int FootContactCapacity => Type switch
    {
        ClimbHoldType.Pocket or ClimbHoldType.Pinch => 1,
        _ => UsableWidthMeters >= 0.30f ? 2 : 1
    };

    /// <summary>World-space distance from the wall surface to the palm/toe contact point.</summary>
    public float ContactOffsetMeters => Type switch
    {
        ClimbHoldType.Crimp => 0.14f,
        ClimbHoldType.Sloper => 0.18f,
        ClimbHoldType.Pinch => 0.21f,
        ClimbHoldType.Pocket => 0.18f,
        ClimbHoldType.FootEdge => 0.04f,
        _ => 0.21f
    };

    public Vector3 ContactPoint => Position + (Normal * ContactOffsetMeters);

    /// <summary>
    /// Keep the hand contact just outside the hold and wall. The target is consumed by the imported hand/finger
    /// end-effector; it is a surface clearance, not a scale or deformation of the hand.
    /// </summary>
    public float HandContactClearanceMeters => 0.05f;

    /// <summary>
    /// Returns the limb-specific point used by the visual IK layer. The imported hand/finger end-effector target is
    /// slightly outside the palm contact point. An ankle/foot bone is above the sole, and an overhang
    /// also moves the wall along Y, so a foot target needs both a vertical lift and a normal clearance; targeting
    /// only the hold centre made the rendered shoe sink behind the step even though the ankle technically reached it.
    /// </summary>
    public Vector3 ContactPointFor(ClimbLimb limb) => ContactPointFor(limb, new Vector3(0f, -9.81f, 0f));

    public Vector3 ContactPointFor(ClimbLimb limb, Vector3 gravity)
    {
        if (limb.IsHand())
        {
            return ContactPoint + (Normal * HandContactClearanceMeters);
        }

        ClimbSurfaceFrame frame = ClimbSurfaceFrame.Create(Position, Normal, gravity);
        return ContactPoint + (Normal * FootWallClearanceMeters) + (frame.UpAlongSurface * FootContactLiftMeters);
    }

    // A micro edge is visually shallow, but the target drives the imported ankle bone rather than the shoe sole.
    // Keep the ankle at the same total normal distance that it had before the edge mesh was made realistic:
    // 0.04 m contact offset + 0.18 m ankle clearance = 0.22 m.
    public float FootWallClearanceMeters => Type switch
    {
        ClimbHoldType.FootEdge => 0.18f,
        _ => 0.10f
    };

    public float FootContactLiftMeters => Type switch
    {
        ClimbHoldType.FootEdge => 0.18f,
        _ => 0.15f
    };

    // A small edge may be poor for a hand, but it is still physically touchable and can be crimped. Hold type
    // changes capacity/fatigue rather than making the object disappear from hand selection.
    public bool SupportsLimb(ClimbLimb limb) => true;

    /// <summary>
    /// Checks physical occupancy only. Hold ids and left/right route labels have no semantic effect. A hold may
    /// accept either side, while a sufficiently wide hand feature can accept both hands at once.
    /// </summary>
    public bool CanAcceptContact(ClimbLimb incoming, IEnumerable<ClimbLimb> currentOccupants)
    {
        ClimbLimb[] others = currentOccupants
            .Where(limb => limb != incoming)
            .Distinct()
            .ToArray();
        if (others.Length == 0)
        {
            return true;
        }

        // Same-kind sharing only: two hands may match a wide hand feature, two feet may share a wide
        // ledge. A hand and a foot never silently overlap one physical slot.
        if (incoming.IsHand())
        {
            return others.All(limb => limb.IsHand()) && others.Length + 1 <= HandContactCapacity;
        }

        return others.All(limb => limb.IsFoot()) && others.Length + 1 <= FootContactCapacity;
    }

    /// <summary>
    /// Separates two matched palms across the usable tangent width instead of driving both wrists into one point.
    /// </summary>
    public Vector3 SharedContactOffset(
        ClimbLimb limb,
        IEnumerable<ClimbLimb> occupants,
        Vector3 gravity)
    {
        ClimbLimb[] sameKind = occupants
            .Where(candidate => candidate.IsHand() == limb.IsHand())
            .Distinct()
            .ToArray();
        int capacity = limb.IsHand() ? HandContactCapacity : FootContactCapacity;
        if (sameKind.Length < 2 || capacity < 2)
        {
            return Vector3.Zero;
        }

        ClimbSurfaceFrame frame = ClimbSurfaceFrame.Create(Position, Normal, gravity);
        float slotOffset = Math.Clamp(UsableWidthMeters * 0.20f, 0.055f, 0.09f);
        return frame.SideAlongSurface * (limb.IsLeft() ? -slotOffset : slotOffset);
    }

    public float ReachMultiplier(ClimbLimb limb) => Type switch
    {
        ClimbHoldType.Crimp => limb.IsHand() ? 0.97f : 0.92f,
        ClimbHoldType.Sloper => limb.IsHand() ? 0.92f : 0.96f,
        ClimbHoldType.Pinch => limb.IsHand() ? 0.95f : 0.90f,
        ClimbHoldType.Pocket => limb.IsHand() ? 0.90f : 0.88f,
        ClimbHoldType.FootEdge => limb.IsFoot() ? 0.96f : 0.94f,
        _ => 1f
    };

    public float LoadMultiplier(ClimbLimb limb) => Type switch
    {
        ClimbHoldType.Crimp => limb.IsHand() ? 0.82f : 0.90f,
        ClimbHoldType.Sloper => limb.IsHand() ? 0.66f : 0.88f,
        ClimbHoldType.Pinch => limb.IsHand() ? 0.86f : 0.82f,
        ClimbHoldType.Pocket => limb.IsHand() ? 0.72f : 0.78f,
        ClimbHoldType.FootEdge => limb.IsFoot() ? 1.05f : 0.58f,
        _ => 1f
    };

    public float FatigueMultiplier(ClimbLimb limb) => Type switch
    {
        ClimbHoldType.Crimp => limb.IsHand() ? 1.22f : 1.08f,
        ClimbHoldType.Sloper => limb.IsHand() ? 1.30f : 1.10f,
        ClimbHoldType.Pinch => limb.IsHand() ? 1.18f : 1.12f,
        ClimbHoldType.Pocket => limb.IsHand() ? 1.34f : 1.16f,
        ClimbHoldType.FootEdge => limb.IsFoot() ? 0.92f : 1.45f,
        _ => 1f
    };

    public string RuleDescription => Type switch
    {
        ClimbHoldType.Crimp => "krawądka: obciążaj opuszkami, krótszy zasięg i większe zmęczenie",
        ClimbHoldType.Sloper => "oblaczek: wymaga docisku i ma mniejszą nośność dłoni",
        ClimbHoldType.Pinch => "ścisk: wymaga przeciwstawnego docisku kciuka",
        ClimbHoldType.Pocket => "dziurka: ograniczona nośność i wysoki koszt palców",
        ClimbHoldType.FootEdge => "mała krawądka: dobra dla stopy; dłoń może użyć jej jako wymagającego micro-crimpa",
        _ => "jug: pełny chwyt zamknięty"
    };

    private static float DefaultUsableWidthMeters(ClimbHoldType type) => type switch
    {
        ClimbHoldType.Jug => 0.46f,
        ClimbHoldType.Crimp => 0.54f,
        ClimbHoldType.Sloper => 0.50f,
        ClimbHoldType.Pinch => 0.28f,
        ClimbHoldType.Pocket => 0.24f,
        ClimbHoldType.FootEdge => 0.14f,
        _ => 0.30f
    };
}