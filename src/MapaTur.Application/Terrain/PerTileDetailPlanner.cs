using System.Diagnostics;
using System.Numerics;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>One tile's plan: which sub-grid of the loaded window it is, and the subsample step to render it at.</summary>
/// <param name="ColStart">First column (west) of the crop within the loaded window.</param>
/// <param name="RowStart">First row (north) of the crop within the loaded window.</param>
/// <param name="Columns">Crop width in cells.</param>
/// <param name="Rows">Crop height in cells.</param>
/// <param name="SubsampleStep">Decimation stride to render the crop at (1 = full detail, 2/4/8 = coarser).</param>
public sealed record PerTileLodDecision(int ColStart, int RowStart, int Columns, int Rows, int SubsampleStep);

/// <summary>Per-tile diagnostics (aligned 1:1 with <see cref="PerTilePlanResult.Tiles"/>) for on-device tuning.</summary>
/// <param name="FinalStep">Subsample step actually assigned (after the vertex budget).</param>
/// <param name="DesiredStep">Step the SSE×roughness metric asked for, BEFORE the budget could demote it.</param>
/// <param name="RoughnessMeters">Measured roughness of the tile.</param>
/// <param name="RoughnessFactor">Boost applied to its screen-space error (1 = none).</param>
/// <param name="DistanceMeters">Camera distance to the tile centre.</param>
public sealed record PerTileLodInfo(int FinalStep, int DesiredStep, double RoughnessMeters, double RoughnessFactor, double DistanceMeters);

/// <summary>The plan plus a timing breakdown for diagnostics (the decisions are deterministic; the millis are not).</summary>
/// <param name="Tiles">Per-tile decisions, in grid order.</param>
/// <param name="RoughnessMs">Wall-clock spent in the per-tile roughness scan (the historically expensive part).</param>
/// <param name="PlanningMs">Wall-clock spent in the rest of planning (crop/elevation/SSE/budget), excluding roughness.</param>
/// <param name="TileInfos">Per-tile diagnostics aligned with <paramref name="Tiles"/>.</param>
public sealed record PerTilePlanResult(
    IReadOnlyList<PerTileLodDecision> Tiles, double RoughnessMs, double PlanningMs, IReadOnlyList<PerTileLodInfo> TileInfos);

/// <summary>
/// Model 1 per-tile planner: splits one loaded detail window into an N×N grid of crops and picks each crop's
/// subsample step from <c>screen-space error × roughness factor</c> (a sharp/near tile keeps a finer step,
/// a smooth/far one steps down), then applies the hard vertex budget so the total can't blow up FPS. Pure:
/// raster in, per-tile decisions out — the render side (crop → subsample → mesh) consumes the result.
/// Composes the already-tested <see cref="DemRasterRoughness"/>, <see cref="ScreenSpaceLod.RoughnessFactor"/>,
/// <see cref="ScreenSpaceError"/> and <see cref="VertexBudget"/> pieces.
/// </summary>
public static class PerTileDetailPlanner
{
    /// <summary>
    /// Plans the per-tile LOD for <paramref name="full"/> (the loaded detail window).
    /// </summary>
    /// <param name="full">The loaded detail raster to split (NoData/holes already applied).</param>
    /// <param name="cameraPosition">Camera position in the world frame (X east, Y north, Z up).</param>
    /// <param name="projectionAnchor">Shared world-frame origin (the LOD scene anchor).</param>
    /// <param name="verticalExaggeration">Z scale matching the rendered terrain.</param>
    /// <param name="gridN">Requested grid resolution per axis; clamped down so every crop stays ≥ 2×2.</param>
    /// <param name="subsampleStepsFinestFirst">Candidate strides, finest first (e.g. 1, 2, 4, 8).</param>
    /// <param name="fovY">Vertical field of view, in radians.</param>
    /// <param name="viewportHeight">Viewport height, in pixels.</param>
    /// <param name="maxErrorPixels">Per-tile screen-space error budget, in pixels.</param>
    /// <param name="preset">Roughness tuning preset (reference roughness + max boost).</param>
    /// <param name="maxVertices">Hard cap on total detail vertices across all tiles.</param>
    /// <param name="roughnessStride">Stride for the per-tile roughness scan (≥ 1); samples every N-th cell with
    /// native ±1 neighbours, cutting cost ÷ N² without changing the metric scale. 1 scans every cell.</param>
    /// <param name="roughnessNeighborDistance">Neighbour baseline (cells, ≥ 1) for the roughness curvature; a
    /// larger value measures roughness at ridge scale (a fine raster reads ~0 at ±1). 1 = native pitch.</param>
    public static IReadOnlyList<PerTileLodDecision> Plan(
        DemRaster full,
        Vector3 cameraPosition,
        GeoPoint projectionAnchor,
        float verticalExaggeration,
        int gridN,
        IReadOnlyList<int> subsampleStepsFinestFirst,
        double fovY,
        double viewportHeight,
        double maxErrorPixels,
        RoughnessLodPreset preset,
        long maxVertices,
        int roughnessStride = 1,
        int roughnessNeighborDistance = 1)
        => PlanDetailed(
            full, cameraPosition, projectionAnchor, verticalExaggeration, gridN, subsampleStepsFinestFirst,
            fovY, viewportHeight, maxErrorPixels, preset, maxVertices, roughnessStride, roughnessNeighborDistance).Tiles;

    /// <summary>
    /// As <see cref="Plan"/>, but also returns a timing breakdown (roughness vs the rest) for on-device
    /// diagnostics. The <see cref="PerTilePlanResult.Tiles"/> are identical and deterministic; only the
    /// millisecond fields vary run to run.
    /// </summary>
    public static PerTilePlanResult PlanDetailed(
        DemRaster full,
        Vector3 cameraPosition,
        GeoPoint projectionAnchor,
        float verticalExaggeration,
        int gridN,
        IReadOnlyList<int> subsampleStepsFinestFirst,
        double fovY,
        double viewportHeight,
        double maxErrorPixels,
        RoughnessLodPreset preset,
        long maxVertices,
        int roughnessStride = 1,
        int roughnessNeighborDistance = 1)
    {
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(subsampleStepsFinestFirst);
        ArgumentNullException.ThrowIfNull(preset);
        if (subsampleStepsFinestFirst.Count == 0)
        {
            throw new ArgumentException("At least one subsample step is required.", nameof(subsampleStepsFinestFirst));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(gridN, 1);

        var totalTimer = Stopwatch.StartNew();
        double roughnessMs = 0.0;

        // Cell pitch in metres, from the world projection of the window's own east–west extent at mid-latitude.
        double cellMeters = CellSizeMeters(full, projectionAnchor, verticalExaggeration);

        // Grid boundaries, clamped so each segment is at least 2 cells (a legal mesh raster).
        int[] colBounds = GridBoundaries(full.Columns, gridN);
        int[] rowBounds = GridBoundaries(full.Rows, gridN);

        int tileCapacity = (colBounds.Length - 1) * (rowBounds.Length - 1);
        var windows = new List<(int ColStart, int RowStart, int Columns, int Rows)>(tileCapacity);
        var entries = new List<TileBudgetEntry>(tileCapacity);
        var roughnessByTile = new List<double>(tileCapacity);
        var factorByTile = new List<double>(tileCapacity);
        var distanceByTile = new List<double>(tileCapacity);
        var desiredByTile = new List<int>(tileCapacity);

        for (int rb = 0; rb < rowBounds.Length - 1; rb++)
        {
            int r0 = rowBounds[rb];
            int rH = rowBounds[rb + 1] - r0;
            for (int cb = 0; cb < colBounds.Length - 1; cb++)
            {
                int c0 = colBounds[cb];
                int cW = colBounds[cb + 1] - c0;

                DemRaster crop = full.Crop(c0, r0, cW, rH);
                var roughTimer = Stopwatch.StartNew();
                double roughness = DemRasterRoughness.Roughness(crop, stride: roughnessStride, neighborDistance: roughnessNeighborDistance);
                roughnessMs += roughTimer.Elapsed.TotalMilliseconds;
                double factor = ScreenSpaceLod.RoughnessFactor(roughness, preset.ReferenceRoughnessMeters, preset.MaxBoost);

                (double min, double max) = crop.GetElevationRange();
                float elevation = (float)((min + max) / 2.0);
                var centre = new GeoPoint((crop.North + crop.South) / 2.0, (crop.West + crop.East) / 2.0);
                Vector3 world = LocalTangentProjection.GeoToWorld(centre, elevation, projectionAnchor, verticalExaggeration);
                double distance = Vector3.Distance(cameraPosition, world);

                var geometricErrorByLevel = new double[subsampleStepsFinestFirst.Count];
                var costByLevel = new long[subsampleStepsFinestFirst.Count];
                for (int i = 0; i < subsampleStepsFinestFirst.Count; i++)
                {
                    int step = subsampleStepsFinestFirst[i];
                    // Roughness boosts the error so a sharp tile earns a finer level from the same distance.
                    geometricErrorByLevel[i] = cellMeters * step * factor;
                    int cols = (cW + step - 1) / step;
                    int rows = (rH + step - 1) / step;
                    costByLevel[i] = (long)cols * rows;
                }

                int desiredLevel = ScreenSpaceError.SelectLod(
                    geometricErrorByLevel, distance, fovY, viewportHeight, maxErrorPixels);
                // Priority for the budget: on-screen size of one cell, boosted by roughness — sharp/near tiles
                // keep their detail when the budget forces demotions, smooth/far ones step down first.
                double priority = ScreenSpaceError.InPixels(cellMeters, distance, fovY, viewportHeight) * factor;

                windows.Add((c0, r0, cW, rH));
                entries.Add(new TileBudgetEntry(desiredLevel, priority, costByLevel));
                roughnessByTile.Add(roughness);
                factorByTile.Add(factor);
                distanceByTile.Add(distance);
                desiredByTile.Add(desiredLevel);
            }
        }

        IReadOnlyList<int> levels = VertexBudget.ConstrainToBudget(entries, maxVertices);

        var decisions = new List<PerTileLodDecision>(windows.Count);
        var infos = new List<PerTileLodInfo>(windows.Count);
        for (int i = 0; i < windows.Count; i++)
        {
            (int ColStart, int RowStart, int Columns, int Rows) w = windows[i];
            int finalStep = subsampleStepsFinestFirst[levels[i]];
            decisions.Add(new PerTileLodDecision(w.ColStart, w.RowStart, w.Columns, w.Rows, finalStep));
            infos.Add(new PerTileLodInfo(
                finalStep, subsampleStepsFinestFirst[desiredByTile[i]], roughnessByTile[i], factorByTile[i], distanceByTile[i]));
        }

        double totalMs = totalTimer.Elapsed.TotalMilliseconds;
        return new PerTilePlanResult(decisions, roughnessMs, Math.Max(0.0, totalMs - roughnessMs), infos);
    }

    private static double CellSizeMeters(DemRaster raster, GeoPoint anchor, float verticalExaggeration)
    {
        double midLatitude = (raster.North + raster.South) / 2.0;
        Vector3 westWorld = LocalTangentProjection.GeoToWorld(new GeoPoint(midLatitude, raster.West), 0f, anchor, verticalExaggeration);
        Vector3 eastWorld = LocalTangentProjection.GeoToWorld(new GeoPoint(midLatitude, raster.East), 0f, anchor, verticalExaggeration);
        double widthMeters = Math.Abs(eastWorld.X - westWorld.X);
        return widthMeters / (raster.Columns - 1);
    }

    // Splits [0, total) into near-equal segments, clamping the count down so every segment is at least 2 cells.
    private static int[] GridBoundaries(int total, int requestedSegments)
    {
        int segments = Math.Max(1, Math.Min(requestedSegments, total / 2));
        var bounds = new int[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            bounds[i] = (int)((long)i * total / segments);
        }

        return bounds;
    }
}