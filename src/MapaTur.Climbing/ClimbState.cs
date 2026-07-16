using System.Collections.ObjectModel;
using System.Numerics;

namespace MapaTur.Climbing;

public sealed record LimbContact(ClimbLimb Limb, ClimbHold Hold, float Fatigue)
{
    public float Fatigue { get; init; } = Math.Clamp(Fatigue, 0f, 1f);
}

public sealed record ClimbState
{
    private ClimbState(Vector3 pelvis, IReadOnlyDictionary<ClimbLimb, LimbContact> contacts, bool hasFallen)
    {
        Pelvis = pelvis;
        Contacts = contacts;
        HasFallen = hasFallen;
    }

    public Vector3 Pelvis { get; }

    public IReadOnlyDictionary<ClimbLimb, LimbContact> Contacts { get; }

    public bool HasFallen { get; }

    public Vector3 CenterOfMass => Pelvis + new Vector3(0f, 0.18f, 0f);

    public Vector3 GetCenterOfMass(Vector3 gravity)
    {
        Vector3 gravityUp = gravity.LengthSquared() > 1e-8f ? -Vector3.Normalize(gravity) : Vector3.UnitY;
        return Pelvis + (gravityUp * 0.18f);
    }

    /// <summary>Returns the limb-specific IK target, including a separate palm slot for a two-hand match.</summary>
    public Vector3 GetContactTarget(ClimbLimb limb, Vector3 gravity)
    {
        LimbContact contact = Contacts[limb];
        ClimbLimb[] occupants = Contacts.Values
            .Where(candidate => candidate.Hold.Id == contact.Hold.Id)
            .Select(candidate => candidate.Limb)
            .ToArray();
        return contact.Hold.ContactPointFor(limb, gravity)
            + contact.Hold.SharedContactOffset(limb, occupants, gravity);
    }

    /// <summary>Returns the physical force application point for the contact layout.</summary>
    public Vector3 GetMechanicsContactPoint(ClimbLimb limb, Vector3 gravity)
    {
        LimbContact contact = Contacts[limb];
        ClimbLimb[] occupants = Contacts.Values
            .Where(candidate => candidate.Hold.Id == contact.Hold.Id)
            .Select(candidate => candidate.Limb)
            .ToArray();
        return contact.Hold.ContactPoint
            + contact.Hold.SharedContactOffset(limb, occupants, gravity);
    }

    public static ClimbState Create(Vector3 pelvis, IEnumerable<LimbContact> contacts)
    {
        Dictionary<ClimbLimb, LimbContact> copied = contacts.ToDictionary(contact => contact.Limb);

        if (copied.Count != Enum.GetValues<ClimbLimb>().Length)
        {
            throw new ArgumentException("A climb state must contain one contact for each hand and foot.", nameof(contacts));
        }

        return new ClimbState(pelvis, new ReadOnlyDictionary<ClimbLimb, LimbContact>(copied), false);
    }

    internal ClimbState With(Vector3 pelvis, IDictionary<ClimbLimb, LimbContact> contacts, bool hasFallen = false) =>
        new(pelvis, new ReadOnlyDictionary<ClimbLimb, LimbContact>(new Dictionary<ClimbLimb, LimbContact>(contacts)), hasFallen);
}