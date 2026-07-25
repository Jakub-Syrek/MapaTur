using System.Collections.ObjectModel;
using System.Numerics;

namespace MapaTur.Climbing;

/// <summary>
/// Runtime settings for the renderer-independent contact mechanics. Gravity is expressed in world space, so the
/// same solver can run in the Y-up demo and in MapaTur's X-east/Y-north/Z-up world.
/// </summary>
public sealed record ClimbMechanicsConfiguration
{
    public Vector3 Gravity { get; init; } = new(0f, -9.81f, 0f);

    public float BodyMassKilograms { get; init; } = 75f;

    public float CharacteristicLengthMeters { get; init; } = 1.72f;

    public int SolverIterations { get; init; } = 1_400;

    public float ForceRegularization { get; init; } = 0.0025f;

    public float ForceResidualTolerance { get; init; } = 0.09f;

    public float MomentResidualTolerance { get; init; } = 0.12f;

    /// <summary>Maximum deviation of a pulling hand force from the shoulder-to-hand axis.</summary>
    public float HandTensionConeDegrees { get; init; } = 32f;
}

/// <summary>A gravity-relative tangent frame sampled from a climbable surface.</summary>
public readonly record struct ClimbSurfaceFrame(
    Vector3 Position,
    Vector3 Normal,
    Vector3 UpAlongSurface,
    Vector3 SideAlongSurface)
{
    public static ClimbSurfaceFrame Create(Vector3 position, Vector3 normal, Vector3 gravity)
    {
        Vector3 safeNormal = NormalizeOr(normal, Vector3.UnitZ);
        Vector3 gravityUp = -NormalizeOr(gravity, -Vector3.UnitY);
        Vector3 upAlongSurface = gravityUp - (safeNormal * Vector3.Dot(gravityUp, safeNormal));

        if (upAlongSurface.LengthSquared() < 1e-8f)
        {
            Vector3 fallback = MathF.Abs(Vector3.Dot(safeNormal, Vector3.UnitX)) < 0.9f
                ? Vector3.UnitX
                : Vector3.UnitY;
            upAlongSurface = fallback - (safeNormal * Vector3.Dot(fallback, safeNormal));
        }

        upAlongSurface = Vector3.Normalize(upAlongSurface);
        Vector3 sideAlongSurface = NormalizeOr(Vector3.Cross(upAlongSurface, safeNormal), Vector3.UnitX);
        return new ClimbSurfaceFrame(position, safeNormal, upAlongSurface, sideAlongSurface);
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : Vector3.Normalize(fallback);
}

public readonly record struct ClimberBodyMassState(
    Vector3 CenterOfMass,
    float MassKilograms,
    float CharacteristicLengthMeters);

/// <summary>
/// A point contact supplied by a route/terrain adapter. Position and normal must be in the same world coordinates as
/// the body centre of mass. A normal points out of the rock and toward the climber.
/// </summary>
public readonly record struct ClimbMechanicsContact
{
    public ClimbMechanicsContact(
        ClimbLimb limb,
        Vector3 position,
        Vector3 normal,
        float quality,
        ClimbHoldType holdType,
        Vector3? proximalPosition = null)
    {
        Limb = limb;
        Position = position;
        Normal = normal.LengthSquared() > 1e-8f ? Vector3.Normalize(normal) : Vector3.UnitZ;
        Quality = Math.Clamp(quality, 0.05f, 1f);
        HoldType = holdType;
        ProximalPosition = proximalPosition;
    }

    public ClimbLimb Limb { get; }

    public Vector3 Position { get; }

    public Vector3 Normal { get; }

    public float Quality { get; }

    public ClimbHoldType HoldType { get; }

    /// <summary>
    /// Shoulder for a hand or hip/ankle-chain origin for another articulated contact. When absent, the body centre
    /// of mass is used as a conservative force-axis approximation.
    /// </summary>
    public Vector3? ProximalPosition { get; }

    public static ClimbMechanicsContact From(LimbContact contact, Vector3? contactPosition = null) => new(
        contact.Limb,
        contactPosition ?? contact.Hold.ContactPoint,
        contact.Hold.Normal,
        contact.Hold.Quality * (1f - (contact.Fatigue * 0.55f)),
        contact.Hold.Type);
}

public sealed record ClimbContactForce(
    Vector3 ForceNewtons,
    float LoadFraction,
    float Utilization,
    float NormalForceNewtons,
    float TangentialForceNewtons);

public sealed record ClimbRootPoseRecommendation(
    Vector3 GravityUp,
    Vector3 BodyUp,
    Vector3 FacingDirection,
    float SurfaceAlignment,
    float SurfacePitchRadians,
    float OutwardSagMeters,
    float GravityDropMeters,
    float BarnDoorRadians);

public sealed record ClimbEquilibriumResult(
    bool IsFeasible,
    float ForceResidualFraction,
    float MomentResidualFraction,
    float GravityNormalFraction,
    float GravityTangentialFraction,
    float HandLoadFraction,
    float FootLoadFraction,
    IReadOnlyDictionary<ClimbLimb, ClimbContactForce> ContactForces,
    ClimbSurfaceFrame SurfaceFrame,
    ClimbRootPoseRecommendation RootPose)
{
    public float TotalHandForceNewtons => ContactForces
        .Where(pair => pair.Key.IsHand())
        .Sum(pair => pair.Value.ForceNewtons.Length());

    public float TotalFootForceNewtons => ContactForces
        .Where(pair => pair.Key.IsFoot())
        .Sum(pair => pair.Value.ForceNewtons.Length());
}

/// <summary>
/// Solves quasi-static contact equilibrium. It balances gravity in 3D and the moments about the centre of mass,
/// while projecting ordinary foot contacts onto a unilateral friction cone. Hands may pull on positive holds;
/// non-hooking feet may push on rock but can never pull the climber into it.
/// </summary>
public sealed class ClimbMechanicsSolver
{
    private readonly ClimbMechanicsConfiguration configuration;

    public ClimbMechanicsSolver(ClimbMechanicsConfiguration? configuration = null)
    {
        this.configuration = configuration ?? new ClimbMechanicsConfiguration();
        if (this.configuration.Gravity.LengthSquared() < 1e-8f)
        {
            throw new ArgumentException("Gravity must be a non-zero vector.", nameof(configuration));
        }
    }

    public Vector3 Gravity => configuration.Gravity;

    public ClimbEquilibriumResult Solve(
        ClimberBodyMassState body,
        IEnumerable<ClimbMechanicsContact> contacts)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        ClimbMechanicsContact[] contactArray = contacts.ToArray();
        if (contactArray.Length == 0)
        {
            throw new ArgumentException("At least one contact is required.", nameof(contacts));
        }

        float mass = body.MassKilograms > 0f ? body.MassKilograms : configuration.BodyMassKilograms;
        float length = body.CharacteristicLengthMeters > 0f
            ? body.CharacteristicLengthMeters
            : configuration.CharacteristicLengthMeters;
        float gravityMagnitude = configuration.Gravity.Length();
        float bodyWeight = mass * gravityMagnitude;
        Vector3 gravityDirection = configuration.Gravity / gravityMagnitude;
        Vector3 requiredForce = -gravityDirection;

        Vector3 averagePosition = Average(contactArray.Select(contact => contact.Position));
        Vector3 averageNormal = NormalizeOr(Average(contactArray.Select(contact => contact.Normal)), contactArray[0].Normal);
        ClimbSurfaceFrame surfaceFrame = ClimbSurfaceFrame.Create(averagePosition, averageNormal, configuration.Gravity);

        Vector3[] normalizedForces = new Vector3[contactArray.Length];
        Vector3[] normalizedArms = new Vector3[contactArray.Length];
        Vector3[] forceAxes = new Vector3[contactArray.Length];
        float[] capacities = new float[contactArray.Length];
        float[] effortWeights = new float[contactArray.Length];

        float maximumArmSquared = 0f;
        float handConeSlope = MathF.Tan(
            Math.Clamp(configuration.HandTensionConeDegrees, 5f, 75f) * (MathF.PI / 180f));
        for (int index = 0; index < contactArray.Length; index++)
        {
            ClimbMechanicsContact contact = contactArray[index];
            normalizedArms[index] = (contact.Position - body.CenterOfMass) / MathF.Max(0.2f, length);
            Vector3 proximal = contact.ProximalPosition ?? body.CenterOfMass;
            forceAxes[index] = NormalizeOr(contact.Position - proximal, requiredForce);
            maximumArmSquared = MathF.Max(maximumArmSquared, normalizedArms[index].LengthSquared());
            capacities[index] = CapacityInBodyWeights(contact);
            effortWeights[index] = contact.Limb.IsFoot() ? 0.38f : 1f;

            // Seed both halves of the contact system. A zero seed gives a poor numerical start at the apex of a
            // friction cone, especially when gravity is tangent to a vertical wall.
            Vector3 seed = requiredForce / contactArray.Length;
            if (contact.Limb.IsFoot())
            {
                seed += contact.Normal * 0.16f;
            }
            else
            {
                seed -= contact.Normal * 0.16f;
            }

            normalizedForces[index] = ProjectContactForce(
                seed,
                contact,
                forceAxes[index],
                capacities[index],
                handConeSlope);
        }

        float step = 0.82f / MathF.Max(
            1f,
            contactArray.Length * (1f + maximumArmSquared) + configuration.ForceRegularization);

        for (int iteration = 0; iteration < configuration.SolverIterations; iteration++)
        {
            ComputeResiduals(normalizedForces, normalizedArms, requiredForce, out Vector3 forceResidual, out Vector3 momentResidual);

            for (int index = 0; index < contactArray.Length; index++)
            {
                Vector3 gradient = forceResidual
                    + Vector3.Cross(momentResidual, normalizedArms[index])
                    + (configuration.ForceRegularization * effortWeights[index] * normalizedForces[index]);
                Vector3 candidate = normalizedForces[index] - (step * gradient);
                normalizedForces[index] = ProjectContactForce(
                    candidate,
                    contactArray[index],
                    forceAxes[index],
                    capacities[index],
                    handConeSlope);
            }
        }

        ComputeResiduals(normalizedForces, normalizedArms, requiredForce, out Vector3 finalForceResidual, out Vector3 finalMomentResidual);
        float forceResidualFraction = finalForceResidual.Length();
        float momentResidualFraction = finalMomentResidual.Length();

        float totalMagnitude = normalizedForces.Sum(force => force.Length());
        float handMagnitude = 0f;
        float footMagnitude = 0f;
        Dictionary<ClimbLimb, ClimbContactForce> contactForces = new();
        for (int index = 0; index < contactArray.Length; index++)
        {
            ClimbMechanicsContact contact = contactArray[index];
            Vector3 forceNewtons = normalizedForces[index] * bodyWeight;
            float forceMagnitude = forceNewtons.Length();
            float normalForce = Vector3.Dot(forceNewtons, contact.Normal);
            float tangentForce = (forceNewtons - (contact.Normal * normalForce)).Length();
            float loadFraction = totalMagnitude > 1e-6f ? normalizedForces[index].Length() / totalMagnitude : 0f;
            float utilization = capacities[index] > 1e-6f ? normalizedForces[index].Length() / capacities[index] : 1f;
            contactForces[contact.Limb] = new ClimbContactForce(
                forceNewtons,
                loadFraction,
                Math.Clamp(utilization, 0f, 2f),
                normalForce,
                tangentForce);

            if (contact.Limb.IsHand())
            {
                handMagnitude += normalizedForces[index].Length();
            }
            else
            {
                footMagnitude += normalizedForces[index].Length();
            }
        }

        float gravityNormalFraction = Math.Clamp(Vector3.Dot(gravityDirection, surfaceFrame.Normal), -1f, 1f);
        float gravityTangentialFraction = MathF.Sqrt(MathF.Max(0f, 1f - (gravityNormalFraction * gravityNormalFraction)));
        float combinedMagnitude = handMagnitude + footMagnitude;
        float handLoadFraction = combinedMagnitude > 1e-6f ? handMagnitude / combinedMagnitude : 0f;
        float footLoadFraction = combinedMagnitude > 1e-6f ? footMagnitude / combinedMagnitude : 0f;
        ClimbRootPoseRecommendation rootPose = RecommendRootPose(
            body,
            surfaceFrame,
            gravityNormalFraction,
            handLoadFraction,
            footLoadFraction,
            contactArray,
            finalMomentResidual);

        bool isFeasible = forceResidualFraction <= configuration.ForceResidualTolerance
            && momentResidualFraction <= configuration.MomentResidualTolerance;
        return new ClimbEquilibriumResult(
            isFeasible,
            forceResidualFraction,
            momentResidualFraction,
            gravityNormalFraction,
            gravityTangentialFraction,
            handLoadFraction,
            footLoadFraction,
            new ReadOnlyDictionary<ClimbLimb, ClimbContactForce>(contactForces),
            surfaceFrame,
            rootPose);
    }

    private ClimbRootPoseRecommendation RecommendRootPose(
        ClimberBodyMassState body,
        ClimbSurfaceFrame surface,
        float gravityNormalFraction,
        float handLoadFraction,
        float footLoadFraction,
        IReadOnlyList<ClimbMechanicsContact> contacts,
        Vector3 momentResidual)
    {
        Vector3 gravityUp = -Vector3.Normalize(configuration.Gravity);
        float surfacePitch = MathF.Atan2(
            Vector3.Dot(Vector3.Cross(gravityUp, surface.UpAlongSurface), surface.SideAlongSurface),
            Math.Clamp(Vector3.Dot(gravityUp, surface.UpAlongSurface), -1f, 1f));

        float outwardGravity = MathF.Max(0f, gravityNormalFraction);
        float surfaceAlignment = Math.Clamp(
            0.14f + (0.30f * footLoadFraction) - (0.16f * outwardGravity),
            0.08f,
            0.48f);
        Vector3 bodyUp = NormalizeOr(
            Vector3.Lerp(gravityUp, surface.UpAlongSurface, surfaceAlignment),
            gravityUp);
        Vector3 facing = -surface.Normal;
        facing -= bodyUp * Vector3.Dot(facing, bodyUp);
        facing = NormalizeOr(facing, -surface.Normal);

        float characteristicLength = body.CharacteristicLengthMeters > 0f
            ? body.CharacteristicLengthMeters
            : configuration.CharacteristicLengthMeters;
        float outwardSag = outwardGravity
            * characteristicLength
            * (0.10f + (0.22f * handLoadFraction));
        float averageHandHeight = contacts
            .Where(contact => contact.Limb.IsHand())
            .Select(contact => Vector3.Dot(contact.Position, gravityUp))
            .DefaultIfEmpty(Vector3.Dot(body.CenterOfMass, gravityUp))
            .Average();
        float handHeightAboveMass = averageHandHeight - Vector3.Dot(body.CenterOfMass, gravityUp);
        float desiredHangHeight = characteristicLength * 0.68f;
        float overhangActivation = SmoothStep(Math.Clamp(outwardGravity / 0.28f, 0f, 1f));
        float gravityDrop = Math.Clamp(
            MathF.Max(0f, desiredHangHeight - handHeightAboveMass) * overhangActivation,
            0f,
            characteristicLength * 0.34f);
        float barnDoor = Math.Clamp(
            Vector3.Dot(momentResidual, surface.Normal) * 0.35f,
            -0.24f,
            0.24f);

        return new ClimbRootPoseRecommendation(
            gravityUp,
            bodyUp,
            facing,
            surfaceAlignment,
            surfacePitch * surfaceAlignment,
            outwardSag,
            gravityDrop,
            barnDoor);
    }

    private static void ComputeResiduals(
        IReadOnlyList<Vector3> forces,
        IReadOnlyList<Vector3> arms,
        Vector3 requiredForce,
        out Vector3 forceResidual,
        out Vector3 momentResidual)
    {
        Vector3 force = Vector3.Zero;
        Vector3 moment = Vector3.Zero;
        for (int index = 0; index < forces.Count; index++)
        {
            force += forces[index];
            moment += Vector3.Cross(arms[index], forces[index]);
        }

        forceResidual = force - requiredForce;
        momentResidual = moment;
    }

    private static Vector3 ProjectContactForce(
        Vector3 force,
        ClimbMechanicsContact contact,
        Vector3 forceAxis,
        float capacity,
        float handConeSlope)
    {
        Vector3 projected;
        if (contact.Limb.IsHand())
        {
            // A hand on a positive hold is a pulling contact. The wall force on the climber must follow the
            // shoulder-to-hand tension line within a bounded cone; a sideways arm cannot invent vertical lift.
            projected = ProjectOntoUnilateralCone(force, forceAxis, handConeSlope);
        }
        else
        {
            float friction = FootFriction(contact.HoldType) * (0.55f + (0.45f * contact.Quality));
            projected = ProjectOntoUnilateralCone(force, contact.Normal, friction);
        }

        float magnitude = projected.Length();
        return magnitude > capacity ? projected * (capacity / magnitude) : projected;
    }

    private static Vector3 ProjectOntoUnilateralCone(Vector3 force, Vector3 axis, float coneSlope)
    {
        float axial = Vector3.Dot(force, axis);
        Vector3 transverse = force - (axis * axial);
        float transverseMagnitude = transverse.Length();
        if (axial >= 0f && transverseMagnitude <= coneSlope * axial)
        {
            return force;
        }

        // Closest point on |t| = slope*a, a >= 0, in Euclidean force coordinates.
        float coneAxial = (axial + (coneSlope * transverseMagnitude)) / (1f + (coneSlope * coneSlope));
        if (coneAxial <= 0f || transverseMagnitude < 1e-8f)
        {
            return coneAxial > 0f ? axis * coneAxial : Vector3.Zero;
        }

        return (axis * coneAxial)
            + ((transverse / transverseMagnitude) * (coneSlope * coneAxial));
    }

    private static float CapacityInBodyWeights(ClimbMechanicsContact contact)
    {
        float shape = contact.HoldType switch
        {
            ClimbHoldType.Crimp => 0.82f,
            ClimbHoldType.Sloper => 0.66f,
            ClimbHoldType.Pinch => 0.86f,
            ClimbHoldType.Pocket => 0.72f,
            ClimbHoldType.FootEdge => contact.Limb.IsFoot() ? 1.05f : 0.58f,
            _ => 1f
        };
        float baseCapacity = contact.Limb.IsHand() ? 1.15f : 1.45f;
        return baseCapacity * shape * (0.35f + (0.65f * contact.Quality));
    }

    private static float FootFriction(ClimbHoldType holdType) => holdType switch
    {
        ClimbHoldType.FootEdge => 1.35f,
        ClimbHoldType.Crimp => 1.15f,
        ClimbHoldType.Sloper => 0.72f,
        ClimbHoldType.Pinch => 0.92f,
        ClimbHoldType.Pocket => 0.82f,
        _ => 1.05f
    };

    private static Vector3 Average(IEnumerable<Vector3> values)
    {
        Vector3 sum = Vector3.Zero;
        int count = 0;
        foreach (Vector3 value in values)
        {
            sum += value;
            count++;
        }

        return count > 0 ? sum / count : Vector3.Zero;
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback) =>
        value.LengthSquared() > 1e-8f ? Vector3.Normalize(value) : Vector3.Normalize(fallback);

    private static float SmoothStep(float value) => value * value * (3f - (2f * value));
}