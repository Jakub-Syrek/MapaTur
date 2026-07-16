using System;
using System.Collections.Generic;
using System.Linq;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="TwoLevelDetailResidencyPolicy"/>: coordinates the det05 (5 cm, fine) and det25
/// (25 cm, coarse) detail rings against ONE shared VRAM budget, finest-wins (det05 &gt; det25 &gt; base). The fine
/// ring is COVERAGE-GATED — 5 cm cells are only wanted where the source tiles exist (07-14 coverage map: 5 cm is
/// a partial strip). The coarse ring is wider and always kept UNDER the fine ring; the budget reserves that
/// fallback first, so 5 cm is never funded without 25 cm beneath it and the base never shows through on a level
/// swap — even in the real regime where a fine cell is ~4× a coarse cell. Pure logic, no GL.
/// </summary>
public sealed class TwoLevelDetailResidencyPolicyTests
{
    private static readonly GeoPoint Focus = new(49.20, 20.00);

    private static readonly OrthoDetailGrid Det25 = new(resMeters: 0.25);                              // 1024 m cells
    private static readonly OrthoDetailGrid Det05 = new(resMeters: 0.05, coverageTiles: 16, pitchTiles: 6); // ~410 m cells

    private const long Mb = 1024L * 1024L;
    private const int Backing = 4; // coarse cells reserved to cover the fine ring (2×2 under 1024 m cells)

    private static DetailLevelSpec Fine(long cellMb, int cap, Func<int, int, bool>? cov = null) =>
        new(Det05, new OrthoDetailResidencyPolicy(Det05, ringRadiusMeters: 400, fastMotionSpeedMps: 25, prefetchLeadMeters: 100),
            cellMb * Mb, cap, cov ?? ((_, _) => true));

    private static DetailLevelSpec Coarse(long cellMb, int cap, Func<int, int, bool>? cov = null) =>
        new(Det25, new OrthoDetailResidencyPolicy(Det25, ringRadiusMeters: 1500, fastMotionSpeedMps: 25, prefetchLeadMeters: 400),
            cellMb * Mb, cap, cov);

    private static bool Covers(MapBounds b, GeoPoint p) =>
        p.Latitude >= b.SouthWest.Latitude && p.Latitude <= b.NorthEast.Latitude
        && p.Longitude >= b.SouthWest.Longitude && p.Longitude <= b.NorthEast.Longitude;

    private static GeoPoint Centre(OrthoDetailGrid g, int key)
    {
        var (ci, cj) = g.CellFromKey(key);
        MapBounds b = g.CellBounds(ci, cj);
        return new GeoPoint((b.SouthWest.Latitude + b.NorthEast.Latitude) / 2, (b.SouthWest.Longitude + b.NorthEast.Longitude) / 2);
    }

    [Fact]
    public void Plan_WithAmpleBudget_FillsBothRings()
    {
        var policy = new TwoLevelDetailResidencyPolicy(Fine(cellMb: 100, cap: 4), Coarse(cellMb: 50, cap: 12), 2000 * Mb, Backing);

        TwoLevelDesired d = policy.Plan(Focus, 0, 0, baseResidentBytes: 0);

        d.FineCells.Should().NotBeEmpty();
        d.CoarseCells.Should().NotBeEmpty();
        d.FineCells.Should().OnlyHaveUniqueItems();
        d.CoarseCells.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Plan_ProductionSqueeze_NeverStarvesTheCoarseFallback()
    {
        // Real regime: fine ≈4× the coarse cell (det05 8192² ≈357 MB, det25 4096² ≈89 MB), 3 GB shared budget,
        // base ortho ≈1.9 GB. WITHOUT the reserve, fine ate the remainder and left 0 coarse cells (base showed
        // through). With the reserve the coarse fallback survives while fine still gets funded.
        var policy = new TwoLevelDetailResidencyPolicy(Fine(cellMb: 357, cap: 4), Coarse(cellMb: 89, cap: 16), 3072 * Mb, Backing);

        TwoLevelDesired d = policy.Plan(Focus, 0, 0, baseResidentBytes: 1900 * Mb);

        d.FineCells.Should().NotBeEmpty("det05 is still funded from what remains after the reserve");
        d.CoarseCells.Count.Should().BeGreaterThanOrEqualTo(Backing, "the fallback under the fine ring is reserved, never starved to 0");
    }

    [Fact]
    public void Plan_FinePrioritised_ButCoarseReducedUnderPressure()
    {
        var fine = Fine(cellMb: 100, cap: 3);
        var ample = new TwoLevelDetailResidencyPolicy(fine, Coarse(cellMb: 50, cap: 16), 5000 * Mb, Backing).Plan(Focus, 0, 0, 0);
        var tight = new TwoLevelDetailResidencyPolicy(fine, Coarse(cellMb: 50, cap: 16), 900 * Mb, Backing).Plan(Focus, 0, 0, 0);

        tight.FineCells.Should().NotBeEmpty("5 cm still gets some budget");
        tight.CoarseCells.Count.Should().BeLessThan(ample.CoarseCells.Count, "det25 absorbs the squeeze");
        tight.CoarseCells.Should().NotBeEmpty();
    }

    [Fact]
    public void Plan_WhereNoFineCoverage_FineIsEmpty_ButCoarseStillCoversTheArea()
    {
        var policy = new TwoLevelDetailResidencyPolicy(
            Fine(cellMb: 100, cap: 4, cov: (_, _) => false), Coarse(cellMb: 50, cap: 12), 2000 * Mb, Backing);

        TwoLevelDesired d = policy.Plan(Focus, 0, 0, 0);

        d.FineCells.Should().BeEmpty("5 cm is only wanted where the source tiles exist");
        d.CoarseCells.Should().NotBeEmpty("25 cm is the fallback and must still be resident");
    }

    [Fact]
    public void Plan_FineCoverageGate_KeepsOnlyCoveredCells()
    {
        static bool Cov(int ci, int cj) => (ci % 2) == 0;
        var policy = new TwoLevelDetailResidencyPolicy(Fine(cellMb: 100, cap: 8, cov: Cov), Coarse(cellMb: 50, cap: 12), 4000 * Mb, Backing);

        TwoLevelDesired d = policy.Plan(Focus, 0, 0, 0);

        d.FineCells.Should().OnlyContain(k => Det05.CellFromKey(k).Ci % 2 == 0);
        d.FineCells.Should().NotBeEmpty("some covered cells are in range");
    }

    [Fact]
    public void Plan_CoverageEdge_BackfillsToTheCap_InsteadOfSilentUnderFill()
    {
        // The NEAREST fine cell (the focus cell) is uncovered; covered cells sit just beyond it. Coverage must be
        // applied BEFORE the cap, so the fine ring fills its cap from the farther covered cells instead of
        // returning one short. (Cap-then-filter — the bug the review caught — would drop the focus cell and stop.)
        var (fci, fcj) = Det05.CellForPoint(Focus);
        bool Cov(int ci, int cj) => !(ci == fci && cj == fcj);
        int focusKey = Det05.CellKey(fci, fcj);
        var policy = new TwoLevelDetailResidencyPolicy(Fine(cellMb: 100, cap: 3, cov: Cov), Coarse(cellMb: 50, cap: 12), 5000 * Mb, Backing);

        TwoLevelDesired d = policy.Plan(Focus, 0, 0, 0);

        d.FineCells.Should().HaveCount(3, "the cap is filled from covered cells beyond the uncovered focus cell");
        d.FineCells.Should().NotContain(focusKey, "the uncovered focus cell is excluded");
    }

    [Fact]
    public void Plan_EveryFineCell_SitsUnderSomeCoarseCell_WithAmpleBudget()
    {
        var policy = new TwoLevelDetailResidencyPolicy(Fine(cellMb: 100, cap: 4), Coarse(cellMb: 50, cap: 12), 4000 * Mb, Backing);

        TwoLevelDesired d = policy.Plan(Focus, 0, 0, 0);
        var coarseBounds = d.CoarseCells.Select(k => { var (ci, cj) = Det25.CellFromKey(k); return Det25.CellBounds(ci, cj); }).ToList();

        d.FineCells.Should().OnlyContain(fk => coarseBounds.Any(b => Covers(b, Centre(Det05, fk))));
    }

    [Fact]
    public void Plan_EveryFineCell_SitsUnderSomeCoarseCell_EvenWhenSqueezed()
    {
        // The no-hole invariant must survive the budget squeeze, not only the ample case.
        var policy = new TwoLevelDetailResidencyPolicy(Fine(cellMb: 357, cap: 4), Coarse(cellMb: 89, cap: 16), 3072 * Mb, Backing);

        TwoLevelDesired d = policy.Plan(Focus, 0, 0, baseResidentBytes: 1900 * Mb);
        var coarseBounds = d.CoarseCells.Select(k => { var (ci, cj) = Det25.CellFromKey(k); return Det25.CellBounds(ci, cj); }).ToList();

        d.FineCells.Should().NotBeEmpty();
        d.FineCells.Should().OnlyContain(fk => coarseBounds.Any(b => Covers(b, Centre(Det05, fk))));
    }

    [Fact]
    public void Plan_Teleport_ReturnsCellsForTheNewFocusOnly()
    {
        var policy = new TwoLevelDetailResidencyPolicy(Fine(cellMb: 100, cap: 4), Coarse(cellMb: 50, cap: 12), 4000 * Mb, Backing);

        TwoLevelDesired a = policy.Plan(new GeoPoint(49.20, 20.00), 0, 0, 0);
        TwoLevelDesired b = policy.Plan(new GeoPoint(49.28, 20.25), 0, 0, 0); // ~18 km away

        a.FineCells.Should().NotIntersectWith(b.FineCells);
        a.CoarseCells.Should().NotIntersectWith(b.CoarseCells);
    }

    [Fact]
    public void Plan_WhenBaseFillsTheBudget_BothLevelsEmpty()
    {
        var policy = new TwoLevelDetailResidencyPolicy(Fine(cellMb: 100, cap: 4), Coarse(cellMb: 50, cap: 12), 1000 * Mb, Backing);

        TwoLevelDesired d = policy.Plan(Focus, 0, 0, baseResidentBytes: 1000 * Mb);

        d.FineCells.Should().BeEmpty();
        d.CoarseCells.Should().BeEmpty();
    }

    [Fact]
    public void Plan_FastMotion_SuppressesBothRings()
    {
        var policy = new TwoLevelDetailResidencyPolicy(Fine(cellMb: 100, cap: 4), Coarse(cellMb: 50, cap: 12), 4000 * Mb, Backing);

        TwoLevelDesired d = policy.Plan(Focus, velEastMps: 40, velNorthMps: 0, baseResidentBytes: 0);

        d.FineCells.Should().BeEmpty();
        d.CoarseCells.Should().BeEmpty();
    }
}
