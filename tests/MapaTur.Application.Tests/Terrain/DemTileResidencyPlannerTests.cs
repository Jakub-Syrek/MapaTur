using System.Linq;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of the DEM-tile residency planner — the streamer's load/keep/evict brain for wider 1 m coverage.
/// Each update it's told which tiles the viewport needs; it loads the newly-needed ones, keeps recently-used
/// off-screen tiles until the budget is pressured (hysteresis → no reload thrash when panning back), and evicts
/// least-recently-used off-screen tiles first. It never evicts a tile needed this frame (can't render a hole),
/// even when the needed set alone exceeds the budget. Mirrors OrthoResidencyPlanner, keyed by DemTileKey.
/// </summary>
public sealed class DemTileResidencyPlannerTests
{
    private static DemTileKey T(int x) => new(14, x, 100);

    [Fact]
    public void Ctor_NonPositiveBudget_Throws()
    {
        FluentActions.Invoking(() => new DemTileResidencyPlanner(0))
            .Should().Throw<System.ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Plan_FirstFrame_LoadsAllVisibleTiles()
    {
        var planner = new DemTileResidencyPlanner(maxResidentTiles: 8);

        DemTileResidencyPlan plan = planner.Plan(new[] { T(1), T(2) });

        plan.ToLoad.Should().BeEquivalentTo(new[] { T(1), T(2) });
        plan.ToEvict.Should().BeEmpty();
        planner.Resident.Should().BeEquivalentTo(new[] { T(1), T(2) });
    }

    [Fact]
    public void Plan_AlreadyResidentTiles_AreNotReloaded()
    {
        var planner = new DemTileResidencyPlanner(maxResidentTiles: 8);
        planner.Plan(new[] { T(1), T(2) });

        DemTileResidencyPlan plan = planner.Plan(new[] { T(1), T(2) });

        plan.ToLoad.Should().BeEmpty();
        plan.ToEvict.Should().BeEmpty();
    }

    [Fact]
    public void Plan_OffScreenTilesWithinBudget_AreKept()
    {
        var planner = new DemTileResidencyPlanner(maxResidentTiles: 8);
        planner.Plan(new[] { T(1), T(2) });

        // Pan away — T1/T2 no longer visible, but the budget has room, so they linger (hysteresis).
        DemTileResidencyPlan plan = planner.Plan(new[] { T(3) });

        plan.ToLoad.Should().BeEquivalentTo(new[] { T(3) });
        plan.ToEvict.Should().BeEmpty();
        planner.Resident.Should().BeEquivalentTo(new[] { T(1), T(2), T(3) });
    }

    [Fact]
    public void Plan_OverBudget_EvictsLeastRecentlyUsedOffScreenFirst()
    {
        var planner = new DemTileResidencyPlanner(maxResidentTiles: 3);
        planner.Plan(new[] { T(1), T(2) }); // recency: 1,2
        planner.Plan(new[] { T(3) });       // recency: 1,2,3  (count 3, ok)

        DemTileResidencyPlan plan = planner.Plan(new[] { T(4) }); // count 4 > 3 → evict LRU non-visible = T1

        plan.ToLoad.Should().BeEquivalentTo(new[] { T(4) });
        plan.ToEvict.Should().BeEquivalentTo(new[] { T(1) });
        planner.Resident.Should().BeEquivalentTo(new[] { T(2), T(3), T(4) });
    }

    [Fact]
    public void Plan_VisibleSetExceedsBudget_KeepsAllVisible()
    {
        var planner = new DemTileResidencyPlanner(maxResidentTiles: 2);

        DemTileResidencyPlan plan = planner.Plan(new[] { T(1), T(2), T(3) });

        plan.ToEvict.Should().BeEmpty(); // can't render a hole — keep all needed tiles even over budget
        planner.Resident.Should().BeEquivalentTo(new[] { T(1), T(2), T(3) });
    }

    [Fact]
    public void Plan_RevisitingATile_MarksItRecentlyUsed_SoItSurvivesEviction()
    {
        var planner = new DemTileResidencyPlanner(maxResidentTiles: 3);
        planner.Plan(new[] { T(1), T(2) }); // recency: 1,2
        planner.Plan(new[] { T(1) });       // touch 1 → recency: 2,1  (T1 now most-recent)
        planner.Plan(new[] { T(3) });       // recency: 2,1,3

        DemTileResidencyPlan plan = planner.Plan(new[] { T(4) }); // evict LRU non-visible = T2 (not T1)

        plan.ToEvict.Should().BeEquivalentTo(new[] { T(2) });
        planner.Resident.Should().Contain(T(1));
    }
}