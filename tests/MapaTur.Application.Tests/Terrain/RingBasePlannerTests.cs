using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Ring-LOD base planner: assigns each plan tile of the whole-range base a subsample step from its distance
/// to the focus (the LOD demo entry point) — native step in the near ring, coarser steps further out. The
/// point: the streamed 1 m detail window sits near the focus, and its boundary must meet a base grid as fine
/// as the source allows, or the base's blunted/shifted ridge pokes out past the window edge as a "duplicated
/// ridge". Distance is measured to the tile's NEAREST owned cell (not its centre), so any tile touching a
/// finer ring is promoted whole. Output composes with the proven crack-free
/// <see cref="TerrainMesh3D.BuildAdaptiveTiles"/> (absolute-grid sampling + welded edges).
/// </summary>
public sealed class RingBasePlannerTests
{
    // 1200×900 cells at 15 m/cell ≈ 18×13.5 km. tileSideCells=300 → an exact 4×3 tile grid, so each
    // tile's ring membership is easy to reason about by hand. Focus inside tile (0,0):
    //   tile (1,0)'s nearest cell is 150 cells = 2250 m from the focus (inside near ring, centre outside),
    //   tile (2,0)'s nearest cell is 450 cells = 6750 m (mid ring),
    //   tile (3,0)'s nearest cell is 750 cells = 11250 m (far ring).
    private const int Columns = 1200;
    private const int Rows = 900;
    private const int FocusCol = 150;
    private const int FocusRow = 150;
    private const double CellMeters = 15.0;
    private const double NearRadius = 3000.0;
    private const double MidRadius = 9000.0;
    private const int TileSide = 300;

    private static IReadOnlyList<PerTileLodDecision> PlanDefault() =>
        RingBasePlanner.Plan(Columns, Rows, FocusCol, FocusRow, CellMeters, NearRadius, MidRadius, TileSide);

    private static PerTileLodDecision TileAt(IReadOnlyList<PerTileLodDecision> plan, int colStart, int rowStart) =>
        plan.Single(t => t.ColStart == colStart && t.RowStart == rowStart);

    [Fact]
    public void Plan_TileContainingFocus_GetsFinestStep()
    {
        IReadOnlyList<PerTileLodDecision> plan = PlanDefault();

        TileAt(plan, 0, 0).SubsampleStep.Should().Be(1, "the focus tile is the near ring by definition");
    }

    [Fact]
    public void Plan_TileTouchingNearRing_IsPromotedWholeToFinestStep()
    {
        IReadOnlyList<PerTileLodDecision> plan = PlanDefault();

        // Tile (1,0): centre is 4500 m out (past the near radius) but its west edge is 2250 m from the
        // focus — nearest-cell semantics must promote it, or the ring boundary cuts through the detail window.
        TileAt(plan, TileSide, 0).SubsampleStep.Should().Be(1, "a tile is as fine as the finest ring it touches");
    }

    [Fact]
    public void Plan_TileInMidRing_GetsMidStep()
    {
        IReadOnlyList<PerTileLodDecision> plan = PlanDefault();

        TileAt(plan, 2 * TileSide, 0).SubsampleStep.Should().Be(2, "nearest cell at 6750 m sits between the radii");
    }

    [Fact]
    public void Plan_TileBeyondMidRadius_GetsFarStep()
    {
        IReadOnlyList<PerTileLodDecision> plan = PlanDefault();

        TileAt(plan, 3 * TileSide, 0).SubsampleStep.Should().Be(4, "nearest cell at 11250 m is past the mid radius");
    }

    [Fact]
    public void Plan_FinerTile_CarriesCoarserNeighbourStepOnSharedEdge()
    {
        IReadOnlyList<PerTileLodDecision> plan = PlanDefault();

        // Tile (2,0) is step 2; its east neighbour (3,0) is step 4 — the builder welds that edge by ratio 4/2.
        TileAt(plan, 2 * TileSide, 0).EdgeStepEast.Should().Be(4, "edges carry the grid neighbour's step for welding");
    }

    [Fact]
    public void Plan_Tiles_PartitionTheRasterExactly()
    {
        IReadOnlyList<PerTileLodDecision> plan = PlanDefault();

        var covered = new bool[Columns * Rows];
        bool overlap = false;
        foreach (PerTileLodDecision t in plan)
        {
            for (int r = t.RowStart; r < t.RowStart + t.Rows; r++)
            {
                for (int c = t.ColStart; c < t.ColStart + t.Columns; c++)
                {
                    int i = (r * Columns) + c;
                    overlap |= covered[i];
                    covered[i] = true;
                }
            }
        }

        (!overlap && covered.All(x => x)).Should().BeTrue("owned crops must tile the raster exactly once (no gap, no overlap)");
    }

    [Fact]
    public void Plan_MidRadiusNotBeyondNearRadius_Throws()
    {
        Action act = () => RingBasePlanner.Plan(Columns, Rows, FocusCol, FocusRow, CellMeters, nearRadiusMeters: 3000.0, midRadiusMeters: 3000.0, TileSide);

        act.Should().Throw<ArgumentException>("the rings must be strictly nested");
    }

    private static readonly int[] MidTileCut = { 450 };
    private static readonly int[] SliverColumnCuts = { 450, 1 };
    private static readonly int[] SliverRowCuts = { 899 };

    [Fact]
    public void Plan_ForcedColumnCut_BecomesATileBoundary()
    {
        // 450 falls inside the regular 300-cell grid's second column — an ortho CELL boundary there must
        // split the tile, or a block straddling the cell clamps its far-side UV ("strata" stripes).
        IReadOnlyList<PerTileLodDecision> plan = RingBasePlanner.Plan(
            Columns, Rows, FocusCol, FocusRow, CellMeters, NearRadius, MidRadius, TileSide,
            forcedColumnCuts: MidTileCut, forcedRowCuts: MidTileCut);

        plan.Where(t => t.ColStart == 450).Should().NotBeEmpty("a forced cut must start a tile column");
    }

    [Fact]
    public void Plan_WithForcedCuts_StillPartitionsTheRasterExactly()
    {
        IReadOnlyList<PerTileLodDecision> plan = RingBasePlanner.Plan(
            Columns, Rows, FocusCol, FocusRow, CellMeters, NearRadius, MidRadius, TileSide,
            forcedColumnCuts: SliverColumnCuts, forcedRowCuts: SliverRowCuts);

        var covered = new bool[Columns * Rows];
        bool overlap = false;
        foreach (PerTileLodDecision t in plan)
        {
            for (int r = t.RowStart; r < t.RowStart + t.Rows; r++)
            {
                for (int c = t.ColStart; c < t.ColStart + t.Columns; c++)
                {
                    int i = (r * Columns) + c;
                    overlap |= covered[i];
                    covered[i] = true;
                }
            }
        }

        (!overlap && covered.All(x => x)).Should().BeTrue("forced cuts must keep the exact partition (no gap, no overlap)");
    }
}