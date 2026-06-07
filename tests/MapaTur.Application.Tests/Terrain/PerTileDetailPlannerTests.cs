using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Model 1 per-tile planning, in isolation (no render). <see cref="PerTileDetailPlanner.Plan"/> splits one
/// loaded detail window into an N×N grid of crops and decides each crop's subsample step from screen-space
/// error × roughness, then applies the hard vertex budget. The pure pieces it composes (roughness, SSE,
/// budget) are already tested on their own; these tests prove they are wired together with the right sign:
/// a rougher crop never coarsens below an equally-distant smoother one, a nearer crop never coarsens below an
/// equally-rough farther one, the grid covers every cell, and the result respects the vertex budget.
/// </summary>
public sealed class PerTileDetailPlannerTests
{
    // A small geographic span so the derived cell pitch is a handful of metres (realistic 1 m-ish detail),
    // anchored on its own centre so the world frame puts the raster around the origin.
    private static readonly GeoPoint SouthWest = new(49.2000, 19.9800);
    private static readonly GeoPoint NorthEast = new(49.2100, 19.9900);
    private static readonly MapBounds Bounds = new(SouthWest, NorthEast);
    private static readonly GeoPoint Anchor = new(
        (SouthWest.Latitude + NorthEast.Latitude) / 2.0,
        (SouthWest.Longitude + NorthEast.Longitude) / 2.0);

    private static readonly int[] Steps = { 1, 2, 4, 8 }; // finest → coarsest
    private static readonly RoughnessLodPreset Preset = RoughnessLodPreset.Balanced;

    private const float VerticalExaggeration = 1f;
    private const double FovY = 1.0;            // radians
    private const double ViewportHeight = 800;  // pixels
    private const double MaxErrorPixels = 2.0;
    private const long HugeBudget = long.MaxValue;

    private static DemRaster Build(int side, Func<int, int, float> elevation)
    {
        var samples = new float[side * side];
        for (int r = 0; r < side; r++)
        {
            for (int c = 0; c < side; c++)
            {
                samples[(r * side) + c] = elevation(c, r);
            }
        }

        return new DemRaster(side, side, Bounds, samples);
    }

    // Camera straight above the raster centre at height H (Z is up), so two cells mirrored across the centre
    // are equidistant — distance is held constant and only roughness varies.
    private static Vector3 CameraAbove(float heightMeters) => new(0f, 0f, heightMeters);

    [Fact]
    public void Plan_EvenGrid_PartitionsEveryCellWithNoGapsOrOverlap()
    {
        DemRaster full = Build(16, (_, _) => 100f);

        IReadOnlyList<PerTileLodDecision> plan = PerTileDetailPlanner.Plan(
            full, CameraAbove(3000f), Anchor, VerticalExaggeration,
            gridN: 4, Steps, FovY, ViewportHeight, MaxErrorPixels, Preset, HugeBudget);

        plan.Should().HaveCount(16);
        AssertPartitions(plan, full.Columns, full.Rows);
    }

    [Fact]
    public void Plan_GridThatDoesNotDivideEvenly_LastTilesTakeTheRemainder()
    {
        DemRaster full = Build(18, (_, _) => 100f); // 18 / 4 ⇒ widths 4,5,4,5 (still ≥ 2)

        IReadOnlyList<PerTileLodDecision> plan = PerTileDetailPlanner.Plan(
            full, CameraAbove(3000f), Anchor, VerticalExaggeration,
            gridN: 4, Steps, FovY, ViewportHeight, MaxErrorPixels, Preset, HugeBudget);

        plan.Should().HaveCount(16);
        AssertPartitions(plan, full.Columns, full.Rows);
    }

    [Fact]
    public void Plan_NeverEmitsAWindowSmallerThanTwoByTwo()
    {
        // A grid finer than the raster would split into 1-wide cells — the planner must clamp so every crop
        // is a legal (≥ 2×2) mesh raster.
        DemRaster full = Build(5, (_, _) => 100f);

        IReadOnlyList<PerTileLodDecision> plan = PerTileDetailPlanner.Plan(
            full, CameraAbove(3000f), Anchor, VerticalExaggeration,
            gridN: 4, Steps, FovY, ViewportHeight, MaxErrorPixels, Preset, HugeBudget);

        plan.Should().OnlyContain(d => d.Columns >= 2 && d.Rows >= 2);
        AssertPartitions(plan, full.Columns, full.Rows);
    }

    [Fact]
    public void Plan_RougherTile_IsNeverCoarserThanAnEquallyDistantSmoothTile()
    {
        // Left half flat (roughness ~0 ⇒ factor 1), right half a jagged saw-tooth (high roughness ⇒ factor > 1).
        // Camera straight above centre ⇒ mirrored tiles are equidistant, so ONLY roughness differs.
        DemRaster full = Build(64, (c, r) => c >= 32 ? (r % 2 == 0 ? 80f : 0f) : 0f);

        IReadOnlyList<PerTileLodDecision> plan = PerTileDetailPlanner.Plan(
            full, CameraAbove(9000f), Anchor, VerticalExaggeration,
            gridN: 4, Steps, FovY, ViewportHeight, MaxErrorPixels, Preset, HugeBudget);

        // Compare a flat-side crop with its mirror across the vertical centre line (equal distance).
        PerTileLodDecision flat = plan.Single(d => d.RowStart == 0 && d.ColStart == 0);          // far-left, flat
        PerTileLodDecision rough = plan.Single(d => d.RowStart == 0 && d.ColStart == 48);        // far-right, rough

        rough.SubsampleStep.Should().BeLessThanOrEqualTo(flat.SubsampleStep,
            "a rougher tile must keep at least as fine a step as an equally distant smooth tile");
        plan.Select(d => d.SubsampleStep).Distinct().Should().HaveCountGreaterThan(1,
            "roughness should actually move some tiles to a finer step (test is in a sensitive regime)");
        rough.SubsampleStep.Should().BeLessThan(flat.SubsampleStep,
            "in this regime the saw-tooth tile earns a strictly finer step than the flat one");
    }

    [Fact]
    public void Plan_StepNeverDecreasesWithDistance_OnFlatTerrain()
    {
        // Flat terrain ⇒ roughness 0 ⇒ factor 1 everywhere, so ONLY distance drives the step. A wide window
        // (512 cells) spans more than one screen-space-error band, and a low camera off to the west gives a
        // strong near→far gradient across it. Sorted by distance, the chosen step must never get finer as the
        // tile gets farther (nearer ⇒ at least as fine), and the plan must actually contain >1 step.
        DemRaster full = Build(512, (_, _) => 100f);
        var cameraWestAndLow = new Vector3(-1200f, 0f, 80f);

        IReadOnlyList<PerTileLodDecision> plan = PerTileDetailPlanner.Plan(
            full, cameraWestAndLow, Anchor, VerticalExaggeration,
            gridN: 4, Steps, FovY, ViewportHeight, MaxErrorPixels, Preset, HugeBudget);

        var byDistance = plan
            .Select(d => new { d.SubsampleStep, Distance = TileDistance(full, d, cameraWestAndLow) })
            .OrderBy(x => x.Distance)
            .ToList();

        for (int i = 1; i < byDistance.Count; i++)
        {
            byDistance[i].SubsampleStep.Should().BeGreaterThanOrEqualTo(byDistance[i - 1].SubsampleStep,
                "a farther tile must never be rendered finer than a nearer one");
        }

        plan.Select(d => d.SubsampleStep).Distinct().Should().HaveCountGreaterThan(1,
            "distance should actually move some tiles to a coarser step across the window");
    }

    [Fact]
    public void PlanDetailed_ReturnsOneAlignedInfoPerTile_WithMatchingFinalStep()
    {
        DemRaster full = Build(64, (c, r) => c >= 32 ? (r % 2 == 0 ? 80f : 0f) : 0f);

        PerTilePlanResult result = PerTileDetailPlanner.PlanDetailed(
            full, CameraAbove(5000f), Anchor, VerticalExaggeration,
            gridN: 4, Steps, FovY, ViewportHeight, MaxErrorPixels, Preset, HugeBudget);

        result.TileInfos.Should().HaveSameCount(result.Tiles);
        for (int i = 0; i < result.Tiles.Count; i++)
        {
            result.TileInfos[i].FinalStep.Should().Be(result.Tiles[i].SubsampleStep);
            result.TileInfos[i].RoughnessFactor.Should().BeGreaterThanOrEqualTo(1.0);
        }
    }

    private static double TileDistance(DemRaster full, PerTileLodDecision d, Vector3 camera)
    {
        DemRaster crop = full.Crop(d.ColStart, d.RowStart, d.Columns, d.Rows);
        (double min, double max) = crop.GetElevationRange();
        var centre = new GeoPoint((crop.North + crop.South) / 2.0, (crop.West + crop.East) / 2.0);
        Vector3 world = LocalTangentProjection.GeoToWorld(centre, (float)((min + max) / 2.0), Anchor, VerticalExaggeration);
        return Vector3.Distance(camera, world);
    }

    [Fact]
    public void Plan_RespectsTheHardVertexBudget()
    {
        DemRaster full = Build(64, (c, r) => c >= 32 ? (r % 2 == 0 ? 80f : 0f) : 0f);

        IReadOnlyList<PerTileLodDecision> plan = PerTileDetailPlanner.Plan(
            full, CameraAbove(1500f), Anchor, VerticalExaggeration,
            gridN: 4, Steps, FovY, ViewportHeight, MaxErrorPixels, Preset, maxVertices: 4000);

        long totalVertices = plan.Sum(d => VerticesAfter(d));
        totalVertices.Should().BeLessThanOrEqualTo(4000,
            "the hard cap must hold even when roughness wants every tile in HD");
    }

    [Fact]
    public void Plan_TinyBudget_KeepsTheHighestPriorityTileFinerThanTheLowest()
    {
        // Near+rough corner should outrank far+flat corner when the budget forces demotions.
        DemRaster full = Build(64, (c, r) => c >= 32 ? (r % 2 == 0 ? 80f : 0f) : 0f);
        var cameraEastAndHigh = new Vector3(6000f, 0f, 2000f);

        IReadOnlyList<PerTileLodDecision> plan = PerTileDetailPlanner.Plan(
            full, cameraEastAndHigh, Anchor, VerticalExaggeration,
            gridN: 4, Steps, FovY, ViewportHeight, MaxErrorPixels, Preset, maxVertices: 6000);

        PerTileLodDecision farFlat = plan.Single(d => d.RowStart == 0 && d.ColStart == 0);
        PerTileLodDecision nearRough = plan.Single(d => d.RowStart == 48 && d.ColStart == 48);

        nearRough.SubsampleStep.Should().BeLessThanOrEqualTo(farFlat.SubsampleStep,
            "under budget pressure the high-priority (near+rough) tile keeps detail; the low-priority one is demoted first");
    }

    private static long VerticesAfter(PerTileLodDecision d)
    {
        int cols = ((d.Columns + d.SubsampleStep - 1) / d.SubsampleStep);
        int rows = ((d.Rows + d.SubsampleStep - 1) / d.SubsampleStep);
        return (long)cols * rows;
    }

    private static void AssertPartitions(IReadOnlyList<PerTileLodDecision> plan, int columns, int rows)
    {
        var covered = new bool[columns, rows];
        foreach (PerTileLodDecision d in plan)
        {
            for (int r = d.RowStart; r < d.RowStart + d.Rows; r++)
            {
                for (int c = d.ColStart; c < d.ColStart + d.Columns; c++)
                {
                    covered[c, r].Should().BeFalse($"cell ({c},{r}) must be covered by exactly one tile (no overlap)");
                    covered[c, r] = true;
                }
            }
        }

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                covered[c, r].Should().BeTrue($"cell ({c},{r}) must be covered by some tile (no gaps)");
            }
        }
    }
}