using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Przegląd adwersarialny batchingu (2026-09-05, 7 potwierdzonych wniosków): grupa była przebudowywana W TEJ SAMEJ
/// klatce po każdej zmianie składu (podczas dociągania 2 stron/klatkę grupa 16 stron scalała się do 16 razy = LOH
/// + GC gen2 na wątku renderu), budżet liczył grupy zamiast stron, a kolejność była słownikowa (niewidoczne grupy
/// pierścienia prefetch konkurowały z widocznymi). Kontrakt: przebudowa dopiero po ustaniu zmian (minAgeFrames),
/// budżet w stronach, priorytet = widoczne, potem najstarsze zmiany.
/// </summary>
public sealed class ScannedRockPageBatchTrackerDebounceTests
{
    private static ScannedRockPageStub Stub(int x, int y, byte lod = 0, int ortho = 6) =>
        new(new ScannedRockPageKey(x, y, lod), ortho, new Vector3(x * 55f, y * 55f, 1000f), new Vector3((x * 55f) + 55f, (y * 55f) + 55f, 1100f));

    private static readonly ScannedRockGroupKey G0 = new(6, 0, 0);
    private static readonly ScannedRockGroupKey G1 = new(6, 1, 0);
    private static readonly ScannedRockGroupKey G2 = new(6, 2, 0);

    [Fact]
    public void dirty_group_is_not_offered_until_its_membership_settled_for_min_age_frames()
    {
        var tracker = new ScannedRockPageBatchTracker(groupCells: 4);
        tracker.Update([Stub(0, 0)], frame: 100);

        tracker.TakeDirty(maxPages: 16, minAgeFrames: 10, frame: 105).Should().BeEmpty();
        tracker.TakeDirty(maxPages: 16, minAgeFrames: 10, frame: 110).Should().Equal(G0);
    }

    [Fact]
    public void every_membership_change_restarts_the_settle_clock()
    {
        var tracker = new ScannedRockPageBatchTracker(groupCells: 4);
        tracker.Update([Stub(0, 0)], frame: 100);
        tracker.Update([Stub(0, 0), Stub(1, 0)], frame: 108); // dociągnięta druga strona

        tracker.TakeDirty(16, 10, frame: 112).Should().BeEmpty();
        tracker.TakeDirty(16, 10, frame: 118).Should().Equal(G0);
    }

    [Fact]
    public void budget_is_counted_in_member_pages_but_always_admits_at_least_one_group()
    {
        var tracker = new ScannedRockPageBatchTracker(groupCells: 4);
        List<ScannedRockPageStub> stubs = [];
        for (int i = 0; i < 12; i++)
        {
            stubs.Add(Stub(i, 0)); // grupy: G0 = x 0..3 (4 strony), G1 = x 4..7 (4), G2 = x 8..11 (4)
        }

        tracker.Update(stubs, frame: 0);

        tracker.TakeDirty(maxPages: 8, minAgeFrames: 0, frame: 0).Should().HaveCount(2);
        tracker.TakeDirty(maxPages: 1, minAgeFrames: 0, frame: 0).Should().HaveCount(1); // grupa 4 stron > budżet 1, ale zawsze ≥ 1
    }

    [Fact]
    public void visible_groups_are_offered_before_invisible_ones_then_oldest_change_first()
    {
        var tracker = new ScannedRockPageBatchTracker(groupCells: 4);
        tracker.Update([Stub(0, 0)], frame: 0);                              // G0 zmieniona w 0
        tracker.Update([Stub(0, 0), Stub(4, 0)], frame: 5);                  // G1 zmieniona w 5
        tracker.Update([Stub(0, 0), Stub(4, 0), Stub(8, 0)], frame: 9);      // G2 zmieniona w 9

        IReadOnlyList<ScannedRockGroupKey> order = tracker.TakeDirty(
            maxPages: 100, minAgeFrames: 0, frame: 20, isVisible: g => g == G2 || g == G1);

        order.Should().Equal(G1, G2, G0); // widoczne (G1 starsza od G2), potem niewidoczna G0
    }

    [Fact]
    public void old_signature_without_debounce_still_returns_all_dirty_groups()
    {
        var tracker = new ScannedRockPageBatchTracker(groupCells: 4);
        tracker.Update([Stub(0, 0), Stub(4, 0)]);

        tracker.TakeDirty(max: 10).Should().HaveCount(2);
    }
}
