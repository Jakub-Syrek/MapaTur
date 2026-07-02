using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="TileResidencyPlanner"/>: the pure residency diff between what is resident and what
/// the quadtree selector now wants (both near→far). ToLoad = desired−resident, near→far, capped to the per-call
/// load budget; ToEvict = resident−desired, farthest-first (reverse of the resident near→far order). No state,
/// no IO, deterministic.
/// </summary>
public sealed class TileResidencyPlannerTests
{
    // All tiles share a zoom/Y; X stands in for "which tile", so the lists read clearly as near→far sequences.
    private static DemTileKey T(int x) => new(14, x, 100);

    [Fact]
    public void Plan_NullArguments_Throw()
    {
        FluentActions.Invoking(() => TileResidencyPlanner.Plan(null!, new[] { T(1) }, 4))
            .Should().Throw<System.ArgumentNullException>();
        FluentActions.Invoking(() => TileResidencyPlanner.Plan(new[] { T(1) }, null!, 4))
            .Should().Throw<System.ArgumentNullException>();
    }

    [Fact]
    public void Plan_NonPositiveLoadBudget_Throws()
    {
        FluentActions.Invoking(() => TileResidencyPlanner.Plan(System.Array.Empty<DemTileKey>(), new[] { T(1) }, 0))
            .Should().Throw<System.ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Plan_FromEmptyResident_LoadsAllDesiredNearToFar_WithinBudget()
    {
        var desired = new[] { T(1), T(2), T(3) };

        TileResidencyDiff diff = TileResidencyPlanner.Plan(System.Array.Empty<DemTileKey>(), desired, maxConcurrentLoads: 8);

        diff.ToLoad.Should().Equal(T(1), T(2), T(3)); // exact near→far order preserved
        diff.ToEvict.Should().BeEmpty();
    }

    [Fact]
    public void Plan_ToLoad_ExcludesAlreadyResidentTiles()
    {
        var resident = new[] { T(1), T(2) };
        var desired = new[] { T(1), T(2), T(3), T(4) };

        TileResidencyDiff diff = TileResidencyPlanner.Plan(resident, desired, maxConcurrentLoads: 8);

        diff.ToLoad.Should().Equal(T(3), T(4)); // only the missing ones, still near→far
        diff.ToEvict.Should().BeEmpty();
    }

    [Fact]
    public void Plan_ToLoad_IsCappedToMaxConcurrentLoads_KeepingNearestFirst()
    {
        var desired = new[] { T(1), T(2), T(3), T(4), T(5) };

        TileResidencyDiff diff = TileResidencyPlanner.Plan(System.Array.Empty<DemTileKey>(), desired, maxConcurrentLoads: 2);

        diff.ToLoad.Should().Equal(T(1), T(2)); // nearest two only; the rest wait for a later call
    }

    [Fact]
    public void Plan_ToEvict_IsResidentMinusDesired_FarthestFirst()
    {
        // resident near→far = 1,2,3,4 ; desired keeps only 1,2 → evict 3,4 but farthest (4) first.
        var resident = new[] { T(1), T(2), T(3), T(4) };
        var desired = new[] { T(1), T(2) };

        TileResidencyDiff diff = TileResidencyPlanner.Plan(resident, desired, maxConcurrentLoads: 8);

        diff.ToLoad.Should().BeEmpty();
        diff.ToEvict.Should().Equal(T(4), T(3)); // farthest-first = reverse of the resident near→far order
    }

    [Fact]
    public void Plan_LoadAndEvictTogether_OnAShiftedSelection()
    {
        // Camera moved on: 1,2 fell out of view, 5,6 came in; 3,4 stay.
        var resident = new[] { T(1), T(2), T(3), T(4) };
        var desired = new[] { T(3), T(4), T(5), T(6) };

        TileResidencyDiff diff = TileResidencyPlanner.Plan(resident, desired, maxConcurrentLoads: 8);

        diff.ToLoad.Should().Equal(T(5), T(6));   // new desired, near→far
        diff.ToEvict.Should().Equal(T(2), T(1));  // dropped resident, farthest-first
    }

    [Fact]
    public void Plan_ResidentEqualsDesired_ProducesEmptyDiffs()
    {
        var tiles = new[] { T(1), T(2), T(3) };

        TileResidencyDiff diff = TileResidencyPlanner.Plan(tiles, tiles, maxConcurrentLoads: 8);

        diff.ToLoad.Should().BeEmpty();
        diff.ToEvict.Should().BeEmpty();
    }

    [Fact]
    public void Plan_IsDeterministic()
    {
        var resident = new[] { T(1), T(2), T(3), T(4) };
        var desired = new[] { T(2), T(3), T(5), T(6), T(7) };

        TileResidencyDiff a = TileResidencyPlanner.Plan(resident, desired, maxConcurrentLoads: 2);
        TileResidencyDiff b = TileResidencyPlanner.Plan(resident, desired, maxConcurrentLoads: 2);

        a.ToLoad.Should().Equal(b.ToLoad);
        a.ToEvict.Should().Equal(b.ToEvict);
    }

    [Fact]
    public void Plan_IgnoresDuplicateKeysInInputs()
    {
        var resident = new[] { T(1), T(1), T(2) };   // duplicate resident
        var desired = new[] { T(2), T(3), T(3) };    // duplicate desired

        TileResidencyDiff diff = TileResidencyPlanner.Plan(resident, desired, maxConcurrentLoads: 8);

        diff.ToLoad.Should().Equal(T(3));  // T(3) scheduled once despite appearing twice
        diff.ToEvict.Should().Equal(T(1)); // T(1) evicted once despite appearing twice
    }
}