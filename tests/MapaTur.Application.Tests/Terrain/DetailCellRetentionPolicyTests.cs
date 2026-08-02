using MapaTur.Application.Terrain;

using Xunit;

namespace MapaTur.Application.Tests.Terrain;

public class DetailCellRetentionPolicyTests
{
    private const int Cap = 192;

    [Fact]
    public void should_evict_nothing_when_pool_is_small_and_nothing_is_starved()
    {
        Assert.Equal(0, DetailCellRetentionPolicy.EvictionCount(
            residentCells: 20, hardCapCells: Cap, starvedCells: 0, freeLayers: 172, staleCells: 0));
    }

    [Fact]
    public void should_evict_down_to_the_hard_cap()
    {
        Assert.Equal(8, DetailCellRetentionPolicy.EvictionCount(
            residentCells: 200, hardCapCells: Cap, starvedCells: 0, freeLayers: 0, staleCells: 0));
    }

    [Fact]
    public void should_evict_for_starved_cells_that_have_no_free_layer()
    {
        Assert.Equal(5, DetailCellRetentionPolicy.EvictionCount(
            residentCells: Cap, hardCapCells: Cap, starvedCells: 7, freeLayers: 2, staleCells: 0));
    }

    [Fact]
    public void should_release_stale_cells_even_when_the_pool_is_exactly_at_the_cap()
    {
        // The measured hole: viewing the Roháče the view wanted ZERO 5 cm cells, yet 192 stayed resident
        // (~7.5 GB of a 16 GB card) because eviction only fired on cap overflow or starvation — both zero.
        Assert.Equal(150, DetailCellRetentionPolicy.EvictionCount(
            residentCells: Cap, hardCapCells: Cap, starvedCells: 0, freeLayers: 0, staleCells: 150));
    }

    [Fact]
    public void should_take_the_largest_reason_not_their_sum()
    {
        // Stale cells and over-cap cells overlap — evicting their sum would over-evict live ground.
        Assert.Equal(60, DetailCellRetentionPolicy.EvictionCount(
            residentCells: 200, hardCapCells: Cap, starvedCells: 3, freeLayers: 0, staleCells: 60));
    }

    [Fact]
    public void should_never_return_negative()
    {
        Assert.Equal(0, DetailCellRetentionPolicy.EvictionCount(
            residentCells: 10, hardCapCells: Cap, starvedCells: 0, freeLayers: 100, staleCells: 0));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(100, false)]
    [InlineData(600, true)]
    public void should_call_a_cell_stale_only_after_a_long_absence_from_the_desired_set(
        long ticksSinceDesired, bool expected)
    {
        Assert.Equal(expected, DetailCellRetentionPolicy.IsStale(ticksSinceDesired));
    }
}