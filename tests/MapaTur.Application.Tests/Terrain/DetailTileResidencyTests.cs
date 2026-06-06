using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="DetailTileResidency.Plan"/>, the streaming bookkeeping for LOD detail
/// tiles: as the camera moves, the desired ring (<see cref="DetailTileRing"/>) shifts, and the streamer must
/// know which tiles to START loading, which to EVICT (free their GPU buffers), and which are already
/// resident and stay. Pure set arithmetic — the actual load/build/upload and the per-frame budget live in
/// the streamer. See the LOD architecture plan.
/// </summary>
public sealed class DetailTileResidencyTests
{
    private static DemTileKey T(int x, int y) => new(16, x, y);

    [Fact]
    public void Plan_LoadsDesiredTilesThatAreNotYetResident()
    {
        var current = new[] { T(0, 0) };
        var desired = new[] { T(0, 0), T(1, 0) };

        var plan = DetailTileResidency.Plan(current, desired);

        plan.ToLoad.Should().BeEquivalentTo(new[] { T(1, 0) });
    }

    [Fact]
    public void Plan_EvictsResidentTilesNoLongerDesired()
    {
        var current = new[] { T(0, 0), T(9, 9) };
        var desired = new[] { T(0, 0) };

        var plan = DetailTileResidency.Plan(current, desired);

        plan.ToEvict.Should().BeEquivalentTo(new[] { T(9, 9) });
    }

    [Fact]
    public void Plan_KeepsTilesThatAreBothResidentAndDesired()
    {
        var current = new[] { T(0, 0), T(1, 0) };
        var desired = new[] { T(1, 0), T(2, 0) };

        var plan = DetailTileResidency.Plan(current, desired);

        plan.ToKeep.Should().BeEquivalentTo(new[] { T(1, 0) });
        plan.ToLoad.Should().BeEquivalentTo(new[] { T(2, 0) });
        plan.ToEvict.Should().BeEquivalentTo(new[] { T(0, 0) });
    }

    [Fact]
    public void Plan_FromEmpty_LoadsEverythingDesired_EvictsNothing()
    {
        var plan = DetailTileResidency.Plan(System.Array.Empty<DemTileKey>(), new[] { T(0, 0), T(1, 0) });

        plan.ToLoad.Should().BeEquivalentTo(new[] { T(0, 0), T(1, 0) });
        plan.ToEvict.Should().BeEmpty();
    }

    [Fact]
    public void Plan_UnchangedRing_LoadsAndEvictsNothing()
    {
        var ring = new[] { T(0, 0), T(1, 0) };

        var plan = DetailTileResidency.Plan(ring, ring);

        plan.ToLoad.Should().BeEmpty();
        plan.ToEvict.Should().BeEmpty();
        plan.ToKeep.Should().BeEquivalentTo(ring);
    }
}