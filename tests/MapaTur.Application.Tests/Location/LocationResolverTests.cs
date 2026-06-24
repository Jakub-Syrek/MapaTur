using FluentAssertions;

using MapaTur.Application.Location;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Location;

namespace MapaTur.Application.Tests.Location;

/// <summary>
/// TDD spec for the pure, offline-robust <see cref="LocationResolver"/> — the decision layer between the raw
/// GPS feed and the UI. In the mountains mobile internet drops but GPS keeps working; the resolver makes the
/// shown location trustworthy: it rejects outliers/teleports/coarse fixes, never regresses or blanks the marker,
/// and ages a fix from Live → Stale → Lost purely from injected time (no wall clock). Cases mirror the hardened
/// edge-case spec (EC-01 … EC-23).
/// </summary>
public sealed class LocationResolverTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 24, 8, 0, 0, TimeSpan.Zero);

    // A point in the Tatras (Kasprowy Wierch area) — well away from null-island.
    private static readonly GeoPoint Base = new(49.2316, 19.9817);

    private static GeoPoint NorthOf(GeoPoint p, double meters) => new(p.Latitude + (meters / 111_320.0), p.Longitude);

    private static UserLocation Fix(GeoPoint pos, double accuracy, double tSeconds)
        => new(pos, accuracy, T0.AddSeconds(tSeconds));

    private static UserLocation FixAt(double accuracy, double tSeconds) => Fix(Base, accuracy, tSeconds);

    // ── Bootstrap + empty state ───────────────────────────────────────────────────────────────────────

    [Fact] // EC-01
    public void Offer_FirstFix_AcceptsRegardlessOfAccuracy_AndResolvesLive()
    {
        var resolver = new LocationResolver();

        LocationOfferResult result = resolver.Offer(FixAt(accuracy: 500, tSeconds: 0)); // coarse cold-start fix

        result.Accepted.Should().BeTrue();
        result.Decision.Should().Be(LocationOfferDecision.Bootstrap);

        ResolvedLocation resolved = resolver.ResolveAt(T0.AddSeconds(1));
        resolved.Freshness.Should().Be(LocationFreshness.Live);
        resolved.Fix.Should().NotBeNull();
        resolved.IsFlaggedLastKnown.Should().BeFalse();
    }

    [Fact] // EC-02
    public void ResolveAt_WithNoAcceptedFix_ReturnsNoneWithNullFix()
    {
        var resolver = new LocationResolver();

        ResolvedLocation resolved = resolver.ResolveAt(T0);

        resolved.Freshness.Should().Be(LocationFreshness.None);
        resolved.Fix.Should().BeNull();
        resolved.Age.Should().BeNull();
        resolved.IsFlaggedLastKnown.Should().BeFalse();
    }

    // ── Ordering / duplicates ─────────────────────────────────────────────────────────────────────────

    [Fact] // EC-03
    public void Offer_TimestampOlderThanLastAccepted_RejectsAndKeepsNewerPosition()
    {
        var resolver = new LocationResolver();
        GeoPoint posA = NorthOf(Base, 0);
        GeoPoint posB = NorthOf(Base, 3);
        resolver.Offer(Fix(posA, 10, 100));

        LocationOfferResult late = resolver.Offer(Fix(posB, 10, 99)); // delivered late, older timestamp

        late.Accepted.Should().BeFalse();
        late.Decision.Should().Be(LocationOfferDecision.RejectedOutOfOrder);
        resolver.ResolveAt(T0.AddSeconds(101)).Fix!.Position.Should().Be(posA);
    }

    [Fact] // EC-04
    public void Offer_SameTimestampSamePositionBetterAccuracy_AcceptsAsUpgrade()
    {
        var resolver = new LocationResolver();
        resolver.Offer(Fix(Base, accuracy: 20, tSeconds: 0));

        LocationOfferResult upgrade = resolver.Offer(Fix(Base, accuracy: 5, tSeconds: 0)); // same instant, finer

        upgrade.Accepted.Should().BeTrue();
        resolver.ResolveAt(T0.AddSeconds(1)).Fix!.AccuracyMeters.Should().Be(5);
    }

    [Fact] // EC-05
    public void Offer_SameTimestampWorseAccuracy_Rejects()
    {
        var resolver = new LocationResolver();
        resolver.Offer(Fix(Base, accuracy: 5, tSeconds: 0));

        LocationOfferResult worse = resolver.Offer(Fix(Base, accuracy: 10, tSeconds: 0));

        worse.Accepted.Should().BeFalse();
        worse.Decision.Should().Be(LocationOfferDecision.RejectedDuplicate);
        resolver.ResolveAt(T0.AddSeconds(1)).Fix!.AccuracyMeters.Should().Be(5);
    }

    [Fact] // EC-05 (b) — same instant, different position would imply infinite speed
    public void Offer_SameTimestampDifferentPosition_Rejects()
    {
        var resolver = new LocationResolver();
        resolver.Offer(Fix(Base, accuracy: 5, tSeconds: 0));

        LocationOfferResult jumped = resolver.Offer(Fix(NorthOf(Base, 50), accuracy: 5, tSeconds: 0));

        jumped.Accepted.Should().BeFalse();
        resolver.ResolveAt(T0.AddSeconds(1)).Fix!.Position.Should().Be(Base);
    }

    [Fact] // EC-06
    public void Offer_ExactDuplicateFix_IsIdempotentNoOp()
    {
        var resolver = new LocationResolver();
        UserLocation f = FixAt(10, 0);
        resolver.Offer(f);

        LocationOfferResult dup = resolver.Offer(f); // the 2 s poller replaying LastKnown

        dup.Accepted.Should().BeFalse();
        dup.Decision.Should().Be(LocationOfferDecision.RejectedDuplicate);
    }

    // ── Accuracy gates (relative) + unknown accuracy ──────────────────────────────────────────────────

    [Fact] // EC-08 (a)
    public void Offer_CoarseFix_RejectedWhenRecentGoodFixHeld()
    {
        var resolver = new LocationResolver();
        resolver.Offer(Fix(Base, accuracy: 12, tSeconds: 0)); // good + recent

        LocationOfferResult coarse = resolver.Offer(Fix(NorthOf(Base, 2), accuracy: 200, tSeconds: 2));

        coarse.Accepted.Should().BeFalse();
        coarse.Decision.Should().Be(LocationOfferDecision.RejectedTooInaccurate);
    }

    [Fact] // EC-08 (b) — once the good fix is older than the good-fix window, a degraded fix beats Lost
    public void Offer_CoarseFix_AcceptedWhenHeldGoodFixIsOld()
    {
        var resolver = new LocationResolver();
        resolver.Offer(Fix(Base, accuracy: 12, tSeconds: 0));

        double afterWindow = LocationResolver.GoodFixWindow.TotalSeconds + 10;
        LocationOfferResult coarse = resolver.Offer(Fix(NorthOf(Base, 2), accuracy: 200, tSeconds: afterWindow));

        coarse.Accepted.Should().BeTrue();
    }

    [Fact] // EC-09 — ceiling is inclusive
    public void Offer_AccuracyExactlyAtGate_Accepts_JustAboveRejects()
    {
        double gate = LocationResolver.AccuracyGateMeters;

        var atGate = new LocationResolver();
        atGate.Offer(Fix(Base, accuracy: 12, tSeconds: 0));
        atGate.Offer(Fix(NorthOf(Base, 2), accuracy: gate, tSeconds: 2)).Accepted.Should().BeTrue();

        var aboveGate = new LocationResolver();
        aboveGate.Offer(Fix(Base, accuracy: 12, tSeconds: 0));
        aboveGate.Offer(Fix(NorthOf(Base, 2), accuracy: gate + 0.001, tSeconds: 2)).Accepted.Should().BeFalse();
    }

    [Fact] // EC-07 — accuracy 0 means UNKNOWN: accepted (not rejected by ceiling), but never "strictly better"
    public void Offer_AccuracyZero_AcceptedAsUnknown_ButNotAnUpgrade()
    {
        var accepted = new LocationResolver();
        accepted.Offer(Fix(Base, accuracy: 10, tSeconds: 0));
        accepted.Offer(Fix(NorthOf(Base, 2), accuracy: 0, tSeconds: 2)) // unknown, not coarse-rejected
            .Accepted.Should().BeTrue();

        var upgrade = new LocationResolver();
        upgrade.Offer(Fix(Base, accuracy: 10, tSeconds: 0));
        upgrade.Offer(Fix(Base, accuracy: 0, tSeconds: 0)) // same instant, unknown is not better than 10 m
            .Accepted.Should().BeFalse();
    }

    // ── Teleport vs real move (Δt-aware speed gate) ───────────────────────────────────────────────────

    [Fact] // EC-10
    public void Offer_LargeJumpSmallDeltaT_RejectsAsTeleport()
    {
        var resolver = new LocationResolver();
        resolver.Offer(Fix(Base, accuracy: 10, tSeconds: 0));

        LocationOfferResult teleport = resolver.Offer(Fix(NorthOf(Base, 500), accuracy: 10, tSeconds: 2)); // 250 m/s

        teleport.Accepted.Should().BeFalse();
        teleport.Decision.Should().Be(LocationOfferDecision.RejectedImplausibleSpeed);
    }

    [Fact] // EC-11
    public void Offer_LongMoveAfterGap_AcceptsViaDeltaTAwareGate()
    {
        var resolver = new LocationResolver();
        resolver.Offer(Fix(Base, accuracy: 10, tSeconds: 0));

        LocationOfferResult move = resolver.Offer(Fix(NorthOf(Base, 500), accuracy: 15, tSeconds: 300)); // ~1.7 m/s

        move.Accepted.Should().BeTrue();
    }

    [Fact] // EC-12 — movement within the combined accuracy halo is jitter, never a teleport
    public void Offer_MoveWithinCombinedAccuracyHalo_AcceptedNotTeleport()
    {
        var resolver = new LocationResolver();
        resolver.Offer(Fix(Base, accuracy: 600, tSeconds: 0));

        LocationOfferResult noisy = resolver.Offer(Fix(NorthOf(Base, 700), accuracy: 600, tSeconds: 2));

        noisy.Accepted.Should().BeTrue(); // 700 m < 600+600 halo
    }

    // ── Freshness banding ─────────────────────────────────────────────────────────────────────────────

    [Fact] // EC-14
    public void ResolveAt_AtBandBoundaries_LiveStaleLost_InclusiveLow()
    {
        var resolver = new LocationResolver();
        resolver.Offer(FixAt(10, 0));
        double live = LocationResolver.LiveMaxAge.TotalSeconds;   // 8
        double stale = LocationResolver.StaleMaxAge.TotalSeconds; // 90

        resolver.ResolveAt(T0.AddSeconds(live)).Freshness.Should().Be(LocationFreshness.Live);
        resolver.ResolveAt(T0.AddSeconds(live + 0.01)).Freshness.Should().Be(LocationFreshness.Stale);
        resolver.ResolveAt(T0.AddSeconds(stale)).Freshness.Should().Be(LocationFreshness.Stale);
        resolver.ResolveAt(T0.AddSeconds(stale + 0.01)).Freshness.Should().Be(LocationFreshness.Lost);
    }

    [Fact] // EC-15
    public void ResolveAt_LongAfterLastFix_StaysLostWithLastKnownFix()
    {
        var resolver = new LocationResolver();
        UserLocation f = FixAt(10, 0);
        resolver.Offer(f);

        ResolvedLocation resolved = resolver.ResolveAt(T0.AddHours(6));

        resolved.Freshness.Should().Be(LocationFreshness.Lost);
        resolved.Fix.Should().Be(f); // never blanked
        resolved.IsFlaggedLastKnown.Should().BeTrue();
        resolved.Age.Should().Be(TimeSpan.FromHours(6));
    }

    [Fact] // EC-16
    public void ResolveAt_RecoveryAfterLost_ReturnsToLiveOnFreshFix()
    {
        var resolver = new LocationResolver();
        resolver.Offer(FixAt(10, 0));
        resolver.ResolveAt(T0.AddSeconds(120)).Freshness.Should().Be(LocationFreshness.Lost);

        resolver.Offer(Fix(NorthOf(Base, 5), accuracy: 10, tSeconds: 121)); // GPS recovers, plausible move

        resolver.ResolveAt(T0.AddSeconds(122)).Freshness.Should().Be(LocationFreshness.Live);
    }

    [Fact] // EC-17 — clock skew: resolve time before the fix → clamp age to zero, stay Live, never negative
    public void ResolveAt_ResolveTimeBeforeFixTimestamp_ClampsAgeToZeroLive()
    {
        var resolver = new LocationResolver();
        resolver.Offer(FixAt(10, 0));

        ResolvedLocation resolved = resolver.ResolveAt(T0.AddSeconds(-1));

        resolved.Age.Should().Be(TimeSpan.Zero);
        resolved.Freshness.Should().Be(LocationFreshness.Live);
    }

    [Fact] // EC-18 — ResolveAt is pure: out-of-order queries are repeatable and don't corrupt state
    public void ResolveAt_OutOfOrderQueries_AreRepeatableAndPure()
    {
        var resolver = new LocationResolver();
        resolver.Offer(FixAt(10, 0));

        ResolvedLocation first = resolver.ResolveAt(T0.AddSeconds(50));
        resolver.ResolveAt(T0.AddSeconds(10));
        ResolvedLocation third = resolver.ResolveAt(T0.AddSeconds(50));

        third.Should().Be(first);
    }

    // ── Defensive validity ────────────────────────────────────────────────────────────────────────────

    [Theory] // EC-19
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-5.0)]
    public void Offer_NonFiniteOrNegativeAccuracy_RejectedWithoutThrow(double badAccuracy)
    {
        var resolver = new LocationResolver();
        resolver.Offer(FixAt(10, 0));

        LocationOfferResult result = resolver.Offer(new UserLocation(NorthOf(Base, 2), badAccuracy, T0.AddSeconds(2)));

        result.Accepted.Should().BeFalse();
        result.Decision.Should().Be(LocationOfferDecision.RejectedInvalid);
        resolver.ResolveAt(T0.AddSeconds(3)).Fix!.AccuracyMeters.Should().Be(10); // unchanged
    }

    [Fact] // EC-19 — null-island (0,0) is a classic garbage fix even though the coordinates are "valid"
    public void Offer_NullIsland_Rejected()
    {
        var resolver = new LocationResolver();

        LocationOfferResult result = resolver.Offer(new UserLocation(new GeoPoint(0, 0), 10, T0));

        result.Accepted.Should().BeFalse();
        result.Decision.Should().Be(LocationOfferDecision.RejectedInvalid);
        resolver.ResolveAt(T0).Freshness.Should().Be(LocationFreshness.None);
    }

    // ── Heading / follow-camera flag (movement, not an acceptance gate) ────────────────────────────────

    [Fact] // EC-20
    public void Offer_SubMinMove_AcceptedFreshButNotHeadingWorthy_LargerMoveIs()
    {
        var resolver = new LocationResolver();
        resolver.Offer(Fix(Base, accuracy: 5, tSeconds: 0));

        LocationOfferResult tiny = resolver.Offer(Fix(NorthOf(Base, 1), accuracy: 5, tSeconds: 2));
        tiny.Accepted.Should().BeTrue();
        tiny.MovedEnoughForHeading.Should().BeFalse();
        resolver.ResolveAt(T0.AddSeconds(3)).Freshness.Should().Be(LocationFreshness.Live); // still Live while standing

        LocationOfferResult walk = resolver.Offer(Fix(NorthOf(Base, 7), accuracy: 5, tSeconds: 4)); // ~6 m further
        walk.Accepted.Should().BeTrue();
        walk.MovedEnoughForHeading.Should().BeTrue();
    }

    // ── Never-blank invariant ─────────────────────────────────────────────────────────────────────────

    [Fact] // EC-23
    public void ResolveAt_AfterFirstAcceptance_NeverReturnsNullFix()
    {
        var resolver = new LocationResolver();
        resolver.Offer(FixAt(20, 0));

        // A messy stream: duplicate, teleport, out-of-order, coarse — all rejected; plus many resolve times.
        resolver.Offer(FixAt(20, 0));                              // duplicate
        resolver.Offer(Fix(NorthOf(Base, 5000), 10, 2));          // teleport
        resolver.Offer(Fix(Base, 10, -50));                       // out-of-order
        resolver.Offer(Fix(NorthOf(Base, 2), 400, 4));            // coarse

        foreach (double t in new[] { -10, 0, 1, 7.9, 8, 50, 90, 91, 3600, 86_400 })
        {
            resolver.ResolveAt(T0.AddSeconds(t)).Fix.Should().NotBeNull($"marker must never blank at t+{t}s");
        }
    }
}