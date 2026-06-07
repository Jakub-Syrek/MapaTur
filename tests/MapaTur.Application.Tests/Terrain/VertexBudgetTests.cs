using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Model 1 safety net: roughness may push many tiles toward HD, but the detail vertex budget is a HARD cap
/// (so FPS can't blow up and a whole valley can't go HD). <see cref="VertexBudget.ConstrainToBudget"/> keeps
/// every tile at its desired LOD while the total fits; when it doesn't, it demotes the LOWEST-priority tiles
/// first — sharp/near tiles keep detail, smooth/far ones step down — until the budget holds.
/// </summary>
public sealed class VertexBudgetTests
{
    // Level 0 = finest (100 verts), level 1 = coarse (10 verts).
    private static readonly long[] Cost = { 100, 10 };

    private static TileBudgetEntry Tile(double priority) => new(DesiredLevel: 0, priority, Cost);

    [Fact]
    public void ConstrainToBudget_UnderBudget_KeepsEveryTileAtItsDesiredLevel()
    {
        var tiles = new[] { Tile(1), Tile(2), Tile(3) }; // total 300

        IReadOnlyList<int> levels = VertexBudget.ConstrainToBudget(tiles, maxVertices: 1000);

        levels.Should().Equal(0, 0, 0);
    }

    [Fact]
    public void ConstrainToBudget_OverBudget_DemotesTheLowestPriorityTilesFirst()
    {
        // Three tiles want level 0 (300 verts) but the budget is 150. Demoting one (100→10) saves 90;
        // two demotions ⇒ 120 ≤ 150. The lowest-priority two step down; the highest-priority keeps HD.
        var tiles = new[] { Tile(1.0), Tile(2.0), Tile(3.0) };

        IReadOnlyList<int> levels = VertexBudget.ConstrainToBudget(tiles, maxVertices: 150);

        // The two lowest-priority tiles are demoted; the highest-priority (sharp) one keeps HD.
        levels.Should().Equal(1, 1, 0);
    }

    [Fact]
    public void ConstrainToBudget_BudgetTooSmallForEvenTheCoarsest_DemotesEverythingAndStops()
    {
        var tiles = new[] { Tile(1.0), Tile(2.0) }; // coarsest total = 20

        IReadOnlyList<int> levels = VertexBudget.ConstrainToBudget(tiles, maxVertices: 5);

        // Nothing can demote past the coarsest — it stops gracefully instead of looping.
        levels.Should().Equal(1, 1);
    }

    [Fact]
    public void ConstrainToBudget_RespectsADesiredLevelCoarserThanFinest()
    {
        var tiles = new[] { new TileBudgetEntry(DesiredLevel: 1, Priority: 1.0, Cost) }; // already coarse (10)

        IReadOnlyList<int> levels = VertexBudget.ConstrainToBudget(tiles, maxVertices: 1000);

        // A tile already below its finest is not promoted by the budget pass.
        levels.Should().Equal(1);
    }
}