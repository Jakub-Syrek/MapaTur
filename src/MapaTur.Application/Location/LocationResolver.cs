using MapaTur.Domain.Geography;
using MapaTur.Domain.Location;

namespace MapaTur.Application.Location;

/// <summary>
/// Pure, offline-robust decision layer between the raw GPS feed and the UI. In the mountains mobile internet
/// drops but GPS keeps working — what fails is fix QUALITY (slow cold start with no A-GPS, multipath in gorges,
/// dropped signal, coarse cell-assisted fixes, the odd teleport outlier). This resolver makes the shown location
/// trustworthy and stable:
/// <list type="bullet">
/// <item><see cref="Offer"/> decides whether a new fix replaces the displayed one — rejecting out-of-order,
/// duplicate, garbage, much-coarser and physically-impossible (teleport) fixes — relative to the last accepted
/// fix only (no wall clock).</item>
/// <item><see cref="ResolveAt"/> is a pure read: it bands the last accepted fix into
/// <see cref="LocationFreshness"/> by age and NEVER blanks the marker once a fix exists (Stale/Lost keep showing
/// the last-known position so "centre on me" and follow-camera always have a usable point).</item>
/// </list>
/// All time is injected (the <c>now</c> parameter) so every boundary is a deterministic unit test — there is no
/// <see cref="DateTimeOffset.UtcNow"/> anywhere. Not thread-safe; call from one thread (the location/UI thread).
/// </summary>
public sealed class LocationResolver
{
    /// <summary>A fix this age or younger is <see cref="LocationFreshness.Live"/>.</summary>
    public static readonly TimeSpan LiveMaxAge = TimeSpan.FromSeconds(8);

    /// <summary>Older than <see cref="LiveMaxAge"/> up to this is <see cref="LocationFreshness.Stale"/>; beyond is Lost.</summary>
    public static readonly TimeSpan StaleMaxAge = TimeSpan.FromSeconds(90);

    /// <summary>A held good fix younger than this still guards against being degraded by a coarse fix.</summary>
    public static readonly TimeSpan GoodFixWindow = TimeSpan.FromSeconds(30);

    /// <summary>Accuracy radius (m) at/under which a fix counts as "good" for the relative coarse-fix gate.</summary>
    public const double AccuracyGateMeters = 50.0;

    /// <summary>Speed (m/s, ~43 km/h) above which a move is treated as a teleport outlier (with escape hatches).</summary>
    public const double MaxPlausibleSpeedMps = 12.0;

    /// <summary>Minimum accepted travel (m) before a move recomputes the heading — kills follow-camera jitter spin.</summary>
    public const double MinMoveForHeadingMeters = 4.0;

    /// <summary>Halo radius (m) to assume when a fix reports accuracy 0 ("unknown", not "perfect").</summary>
    public const double UnknownAccuracyMeters = 35.0;

    private UserLocation? lastAccepted;

    /// <summary>The fix currently being displayed, or null before any fix is accepted.</summary>
    public UserLocation? LastAccepted => lastAccepted;

    /// <summary>
    /// Offers a fix from the GPS feed. Returns whether it became the new displayed position and why. On rejection
    /// the resolver state is untouched, so the previous fix simply keeps ageing (the marker never jumps to a bad
    /// reading, never blanks).
    /// </summary>
    public LocationOfferResult Offer(UserLocation candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // R1 — defensive validity. UserLocation's public record ctor bypasses UserLocation.Create, so a NaN /
        // negative accuracy or a null-island (0,0) fix can reach us; reject without throwing.
        if (!IsValid(candidate))
        {
            return Reject(LocationOfferDecision.RejectedInvalid);
        }

        // R2 — bootstrap: the very first fix is the baseline. The quality gates compare against a previous fix,
        // so they have no anchor yet — accept regardless of accuracy (a 500 m fix beats a blank cold-start map).
        if (lastAccepted is not { } prev)
        {
            lastAccepted = candidate;
            return new LocationOfferResult(true, LocationOfferDecision.Bootstrap, MovedEnoughForHeading: false);
        }

        // R3 — monotonic time: a late-delivered older fix must never regress the displayed position.
        if (candidate.Timestamp < prev.Timestamp)
        {
            return Reject(LocationOfferDecision.RejectedOutOfOrder);
        }

        double dtSeconds = (candidate.Timestamp - prev.Timestamp).TotalSeconds;
        double distance = prev.Position.HaversineDistanceMetersTo(candidate.Position);

        // R4 — same instant as the last fix: no Δt, so never compute speed (no divide-by-zero). The only valid
        // same-instant change is an in-place accuracy UPGRADE at the identical position; anything else is a dupe.
        if (dtSeconds <= 0.0)
        {
            bool sameSpot = SamePosition(prev.Position, candidate.Position);
            bool strictlyFiner = candidate.AccuracyMeters > 0.0
                && (prev.AccuracyMeters <= 0.0 || candidate.AccuracyMeters < prev.AccuracyMeters);
            if (sameSpot && strictlyFiner)
            {
                lastAccepted = candidate;
                return new LocationOfferResult(true, LocationOfferDecision.Accepted, MovedEnoughForHeading: false);
            }

            return Reject(LocationOfferDecision.RejectedDuplicate);
        }

        // R6 — relative coarse-fix gate: reject a much-less-accurate fix ONLY while we still hold a good, recent
        // fix (don't smear a trustworthy marker with a cell-assisted reading). With nothing good and recent, a
        // degraded fix is better than going Lost, so it passes. AccuracyMeters == 0 is "unknown", never coarse.
        bool heldGoodAndRecent = prev.AccuracyMeters > 0.0
            && prev.AccuracyMeters < AccuracyGateMeters
            && dtSeconds < GoodFixWindow.TotalSeconds;
        if (candidate.AccuracyMeters > AccuracyGateMeters && heldGoodAndRecent)
        {
            return Reject(LocationOfferDecision.RejectedTooInaccurate);
        }

        // R7 — Δt-aware teleport gate. A move within the combined accuracy halos is just jitter; after a signal
        // gap a long move is real (bound by max speed over the whole gap); otherwise reject impossible speeds.
        double combinedHalo = SafeAccuracy(prev.AccuracyMeters) + SafeAccuracy(candidate.AccuracyMeters);
        bool plausible;
        if (distance <= combinedHalo)
        {
            plausible = true;
        }
        else if (dtSeconds >= LiveMaxAge.TotalSeconds)
        {
            plausible = distance <= (MaxPlausibleSpeedMps * dtSeconds) + combinedHalo;
        }
        else
        {
            plausible = (distance / dtSeconds) <= MaxPlausibleSpeedMps;
        }

        if (!plausible)
        {
            return Reject(LocationOfferDecision.RejectedImplausibleSpeed);
        }

        // R8/R9 — accept atomically; flag whether the move is far enough to recompute a travel bearing.
        lastAccepted = candidate;
        bool movedEnough = distance >= MinMoveForHeadingMeters;
        return new LocationOfferResult(true, LocationOfferDecision.Accepted, movedEnough);
    }

    /// <summary>
    /// The location to display at <paramref name="now"/>. Pure and referentially transparent — it mutates nothing,
    /// so out-of-order or repeated queries are safe. Returns <see cref="ResolvedLocation.None"/> only before any
    /// fix is accepted; afterwards the fix is always present, banded Live/Stale/Lost by age.
    /// </summary>
    public ResolvedLocation ResolveAt(DateTimeOffset now)
    {
        // R10 — the only legitimate blank: nothing accepted yet (UI shows an "acquiring GPS" state).
        if (lastAccepted is not { } fix)
        {
            return ResolvedLocation.None;
        }

        // R13 — never a negative age (a future-stamped fix / resolve-clock skew clamps to zero, stays Live).
        TimeSpan age = now - fix.Timestamp;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        // R11/R12 — freshness is a pure function of age, inclusive at the low boundary; no stored state machine,
        // so None→Live→Stale→Lost and recovery Lost→Live all just fall out of (timestamp, now).
        LocationFreshness freshness = age <= LiveMaxAge
            ? LocationFreshness.Live
            : age <= StaleMaxAge
                ? LocationFreshness.Stale
                : LocationFreshness.Lost;

        // R15 — never-blank: Stale/Lost still return the real last-known fix; the UI fades it via the flag.
        return new ResolvedLocation(fix, freshness, age, IsFlaggedLastKnown: freshness != LocationFreshness.Live);
    }

    private static LocationOfferResult Reject(LocationOfferDecision decision)
        => new(false, decision, MovedEnoughForHeading: false); // R9 — mutate nothing on reject

    private static bool IsValid(UserLocation fix)
    {
        if (!double.IsFinite(fix.AccuracyMeters) || fix.AccuracyMeters < 0.0)
        {
            return false;
        }

        GeoPoint p = fix.Position;
        if (!double.IsFinite(p.Latitude) || !double.IsFinite(p.Longitude))
        {
            return false;
        }

        // Null-island (0,0) is the canonical "no real fix" sentinel — reject it even though it parses as valid.
        return p.Latitude != 0.0 || p.Longitude != 0.0;
    }

    // Accuracy 0 means UNKNOWN, not perfect: use the unknown-accuracy radius wherever a fix's halo feeds a gate.
    private static double SafeAccuracy(double accuracy) => accuracy > 0.0 ? accuracy : UnknownAccuracyMeters;

    private static bool SamePosition(GeoPoint a, GeoPoint b)
        => a.Latitude == b.Latitude && a.Longitude == b.Longitude;
}