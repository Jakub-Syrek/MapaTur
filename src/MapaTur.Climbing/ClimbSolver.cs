using System.Collections.ObjectModel;
using System.Numerics;

namespace MapaTur.Climbing;

public sealed record ClimbConfiguration(
    float HandReach = 1.85f,
    float FootReach = 2.1f,
    float ReachTolerance = 0.08f,
    float MinimumStabilityMargin = -0.04f,
    float FallRiskThreshold = 0.96f);

public sealed record StabilityAssessment(
    bool IsStable,
    float Margin,
    float Risk,
    IReadOnlyDictionary<ClimbLimb, float> Loads)
{
    public ClimbEquilibriumResult? Equilibrium { get; init; }
}

public sealed record ClimbMoveResult(
    bool Succeeded,
    ClimbState State,
    StabilityAssessment Assessment,
    string? FailureReason = null);

public sealed class ClimbSolver
{
    private readonly ClimbConfiguration configuration;
    private readonly ClimbMechanicsSolver mechanics;

    public ClimbSolver(
        ClimbConfiguration? configuration = null,
        ClimbMechanicsConfiguration? mechanicsConfiguration = null)
    {
        this.configuration = configuration ?? new ClimbConfiguration();
        mechanics = new ClimbMechanicsSolver(mechanicsConfiguration);
    }

    public StabilityAssessment Assess(ClimbState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        LimbContact[] contacts = state.Contacts.Values.ToArray();
        if (contacts.Length < 2)
        {
            return new StabilityAssessment(false, -1f, 1f, new ReadOnlyDictionary<ClimbLimb, float>(new Dictionary<ClimbLimb, float>()));
        }

        Vector3 gravityUp = -Vector3.Normalize(mechanics.Gravity);
        Dictionary<ClimbLimb, Vector3> layoutPoints = contacts.ToDictionary(
            contact => contact.Limb,
            contact => GetLayoutContactPoint(contacts, contact.Limb));
        Dictionary<ClimbLimb, Vector3> mechanicsContactPoints = contacts.ToDictionary(
            contact => contact.Limb,
            contact => state.GetMechanicsContactPoint(contact.Limb, mechanics.Gravity));
        Vector3 surfaceSide = ClimbSurfaceFrame.Create(
            layoutPoints[contacts[0].Limb],
            contacts[0].Hold.Normal,
            mechanics.Gravity).SideAlongSurface;
        Vector2 centroid = new(
            contacts.Average(contact => Vector3.Dot(layoutPoints[contact.Limb], surfaceSide)),
            contacts.Average(contact => Vector3.Dot(layoutPoints[contact.Limb], gravityUp)));
        Vector2 centerOfMass = new(
            Vector3.Dot(state.GetCenterOfMass(mechanics.Gravity), surfaceSide),
            Vector3.Dot(state.GetCenterOfMass(mechanics.Gravity), gravityUp));

        float spread = contacts.Max(contact => Vector2.Distance(
            new Vector2(
                Vector3.Dot(layoutPoints[contact.Limb], surfaceSide),
                Vector3.Dot(layoutPoints[contact.Limb], gravityUp)),
            centroid));
        float offset = Vector2.Distance(centerOfMass, centroid);
        float margin = MathF.Max(0.16f, spread * 0.9f) - offset;

        Dictionary<ClimbLimb, float> loadFactors = contacts.ToDictionary(
            contact => contact.Limb,
            contact => contact.Hold.Quality
                * contact.Hold.LoadMultiplier(contact.Limb)
                * (contact.Limb.IsFoot() ? 1.2f : 1f)
                * (1f - (contact.Fatigue * 0.65f)));
        float totalFactor = loadFactors.Values.Sum();
        Dictionary<ClimbLimb, float> loads = loadFactors.ToDictionary(pair => pair.Key, pair => pair.Value / totalFactor);

        float fatigueRisk = contacts.Max(contact => contact.Fatigue);
        float balanceRisk = 1f - Math.Clamp((margin + 0.18f) / MathF.Max(0.18f, spread), 0f, 1f);
        ClimbMechanicsContact[] mechanicsContacts = contacts
            .Select(contact => ClimbMechanicsContact.From(contact, mechanicsContactPoints[contact.Limb]))
            .ToArray();
        Vector3 rawCenterOfMass = state.GetCenterOfMass(mechanics.Gravity);
        ClimbEquilibriumResult initialEquilibrium = mechanics.Solve(
            new ClimberBodyMassState(rawCenterOfMass, 0f, 0f),
            mechanicsContacts);
        Vector3 hangingCenterOfMass = rawCenterOfMass
            + (initialEquilibrium.SurfaceFrame.Normal * initialEquilibrium.RootPose.OutwardSagMeters)
            - (initialEquilibrium.RootPose.GravityUp * initialEquilibrium.RootPose.GravityDropMeters);
        ClimbEquilibriumResult equilibrium = mechanics.Solve(
            new ClimberBodyMassState(hangingCenterOfMass, 0f, 0f),
            mechanicsContacts) with
        {
            // The second pass evaluates the recommended hanging position; retain the first pass's absolute root
            // displacement because that is what a renderer/character controller must actually apply.
            RootPose = initialEquilibrium.RootPose
        };
        float mechanicsRisk = Math.Clamp(
            (equilibrium.ForceResidualFraction / 0.24f * 0.62f)
            + (equilibrium.MomentResidualFraction / 0.35f * 0.38f),
            0f,
            1f);
        float risk = Math.Clamp(
            (balanceRisk * 0.45f) + (fatigueRisk * 0.20f) + (mechanicsRisk * 0.35f),
            0f,
            1f);

        return new StabilityAssessment(
            margin >= configuration.MinimumStabilityMargin && equilibrium.ForceResidualFraction <= 0.26f,
            margin,
            risk,
            new ReadOnlyDictionary<ClimbLimb, float>(loads))
        {
            Equilibrium = equilibrium
        };
    }

    public ClimbMoveResult TryMove(ClimbState state, ClimbLimb limb, ClimbHold target)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(target);

        if (state.HasFallen)
        {
            return Failure(state, "The climber has fallen; reset before moving again.");
        }

        if (!target.SupportsLimb(limb))
        {
            return Failure(state, $"{target.Id} is a {target.Type} and cannot be used by {limb} ({target.RuleDescription}).");
        }

        ClimbLimb[] targetOccupants = state.Contacts.Values
            .Where(contact => contact.Limb != limb && contact.Hold.Id == target.Id)
            .Select(contact => contact.Limb)
            .ToArray();
        if (!target.CanAcceptContact(limb, targetOccupants))
        {
            return Failure(
                state,
                $"{target.Id} has no free contact slot for {limb} " +
                $"(hand capacity {target.HandContactCapacity}; occupied by {string.Join(", ", targetOccupants)})."
            );
        }

        Dictionary<ClimbLimb, LimbContact> contacts = state.Contacts.ToDictionary(pair => pair.Key, pair => pair.Value);
        LimbContact currentContact = contacts[limb];
        contacts[limb] = new LimbContact(limb, target, currentContact.Fatigue);

        if (!TryRepositionPelvis(state.Pelvis, contacts.Values, out Vector3 nextPelvis))
        {
            return Failure(state, "The contact arrangement leaves no viable vertical band for the pelvis.");
        }

        // A real climbing move transfers the hips over the three planted contacts before the reaching limb is
        // fully extended. Plan that transfer first; checking only the old pelvis made sparse but valid sequences
        // look impossible and left the scripted route retrying one hold forever.
        float maximumReach = limb.IsHand() ? configuration.HandReach : configuration.FootReach;
        float reachLimit = (maximumReach * target.ReachMultiplier(limb)) + configuration.ReachTolerance;
        Vector3 targetContactPoint = GetLayoutContactPoint(contacts.Values, limb);
        float reach = Vector3.Distance(nextPelvis, targetContactPoint);
        for (int iteration = 0; iteration < 20 && reach > reachLimit; iteration++)
        {
            if (!TryRepositionPelvis(nextPelvis, contacts.Values, out Vector3 transferredPelvis))
            {
                break;
            }

            nextPelvis = transferredPelvis;
            reach = Vector3.Distance(nextPelvis, targetContactPoint);
        }

        if (reach > reachLimit)
        {
            return Failure(state, $"{limb} cannot reach {target.Id} after hip transfer ({reach:F2} m / {reachLimit:F2} m including hold tolerance).");
        }

        ClimbState tentative = state.With(nextPelvis, contacts);
        StabilityAssessment assessment = Assess(tentative);
        float movementEffort =
            (0.035f + (Vector3.Distance(currentContact.Hold.Position, target.Position) / maximumReach * 0.12f))
            * target.FatigueMultiplier(limb);

        foreach (ClimbLimb contactLimb in contacts.Keys.ToArray())
        {
            LimbContact contact = contacts[contactLimb];
            float load = assessment.Loads[contactLimb];
            float fatigueDelta = contactLimb == limb
                ? movementEffort + (load * (1f - (target.Quality * target.LoadMultiplier(limb))) * 0.08f)
                : load * 0.012f;
            contacts[contactLimb] = contact with { Fatigue = Math.Clamp(contact.Fatigue + fatigueDelta, 0f, 1f) };
        }

        tentative = state.With(nextPelvis, contacts);
        assessment = Assess(tentative);
        bool hasFallen = !assessment.IsStable && assessment.Risk >= configuration.FallRiskThreshold;
        ClimbState resultState = state.With(nextPelvis, contacts, hasFallen);

        return hasFallen
            ? new ClimbMoveResult(false, resultState, assessment, "Balance was lost and the climber fell.")
            : new ClimbMoveResult(true, resultState, assessment);
    }

    public float GetMaximumReach(ClimbLimb limb) => limb.IsHand() ? configuration.HandReach : configuration.FootReach;

    private ClimbMoveResult Failure(ClimbState state, string reason) => new(false, state, Assess(state), reason);

    private bool TryRepositionPelvis(Vector3 currentPelvis, IEnumerable<LimbContact> contacts, out Vector3 pelvis)
    {
        LimbContact[] contactArray = contacts.ToArray();
        Dictionary<ClimbLimb, Vector3> contactPoints = contactArray.ToDictionary(
            contact => contact.Limb,
            contact => GetLayoutContactPoint(contactArray, contact.Limb));
        Vector3 average = new(
            contactPoints.Values.Average(position => position.X),
            contactPoints.Values.Average(position => position.Y),
            contactPoints.Values.Average(position => position.Z));
        Vector3 gravityUp = -Vector3.Normalize(mechanics.Gravity);
        Vector3 averageNormal = Vector3.Normalize(contactArray.Aggregate(
            Vector3.Zero,
            (sum, contact) => sum + contact.Hold.Normal));
        float footHeight = contactArray
            .Where(contact => contact.Limb.IsFoot())
            .Average(contact => Vector3.Dot(contactPoints[contact.Limb], gravityUp));
        float handHeight = contactArray
            .Where(contact => contact.Limb.IsHand())
            .Average(contact => Vector3.Dot(contactPoints[contact.Limb], gravityUp));
        float lowerPelvisLimit = footHeight + 0.45f;
        float upperPelvisLimit = handHeight - 0.45f;
        if (lowerPelvisLimit > upperPelvisLimit)
        {
            pelvis = currentPelvis;
            return false;
        }

        float desiredHeight = Math.Clamp(Vector3.Dot(average, gravityUp), lowerPelvisLimit, upperPelvisLimit);
        Vector3 outwardOffset = averageNormal * 0.58f;
        Vector3 target = average
            + outwardOffset
            + (gravityUp * (desiredHeight - Vector3.Dot(average + outwardOffset, gravityUp)));

        pelvis = Vector3.Lerp(currentPelvis, target, 0.38f);
        return true;
    }

    private Vector3 GetLayoutContactPoint(IEnumerable<LimbContact> contacts, ClimbLimb limb)
    {
        LimbContact[] contactArray = contacts as LimbContact[] ?? contacts.ToArray();
        LimbContact contact = contactArray.Single(candidate => candidate.Limb == limb);
        ClimbLimb[] occupants = contactArray
            .Where(candidate => candidate.Hold.Id == contact.Hold.Id)
            .Select(candidate => candidate.Limb)
            .ToArray();
        // Route reach and pelvis transfer are defined against the feature centre. The physical contact offset is
        // reserved for force/IK layers; feeding it back into planning changed every existing single-contact pose.
        return contact.Hold.Position
            + contact.Hold.SharedContactOffset(limb, occupants, mechanics.Gravity);
    }
}