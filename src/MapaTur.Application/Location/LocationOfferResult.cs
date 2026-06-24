namespace MapaTur.Application.Location;

/// <summary>Why the resolver accepted or rejected an offered GPS fix (for diagnostics + tests).</summary>
public enum LocationOfferDecision
{
    /// <summary>First fix ever — accepted unconditionally as the baseline (quality gates have no anchor yet).</summary>
    Bootstrap,

    /// <summary>Accepted: a plausible newer fix (a move, or a same-instant accuracy upgrade).</summary>
    Accepted,

    /// <summary>Rejected: non-finite/negative accuracy, or a garbage position (null-island).</summary>
    RejectedInvalid,

    /// <summary>Rejected: timestamp older than the last accepted fix (late delivery) — position must not regress.</summary>
    RejectedOutOfOrder,

    /// <summary>Rejected: same instant as the last fix with no accuracy improvement, or an exact duplicate.</summary>
    RejectedDuplicate,

    /// <summary>Rejected: much coarser than a recent good fix — don't degrade a trustworthy marker.</summary>
    RejectedTooInaccurate,

    /// <summary>Rejected: the implied speed is impossible (a teleport outlier).</summary>
    RejectedImplausibleSpeed,
}

/// <summary>
/// Outcome of offering one fix to the <see cref="LocationResolver"/>.
/// </summary>
/// <param name="Accepted">Whether the fix became the new displayed position.</param>
/// <param name="Decision">The precise accept/reject reason.</param>
/// <param name="MovedEnoughForHeading">
/// True when an accepted fix moved far enough from the previous one to recompute a travel bearing — drives
/// follow-camera chase/heading. False for the bootstrap fix, same-instant upgrades, and sub-threshold jitter
/// (so the camera doesn't spin while the hiker stands still). Always false on rejection.
/// </param>
public sealed record LocationOfferResult(bool Accepted, LocationOfferDecision Decision, bool MovedEnoughForHeading);