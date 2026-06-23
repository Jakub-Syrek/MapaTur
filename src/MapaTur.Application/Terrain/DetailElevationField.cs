using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Terrain;

/// <summary>
/// The retained 1 m LOD detail raster, used to seat overlay geometry (trails / roads / route) on the
/// SAME surface the renderer actually draws near the camera.
/// <para>
/// Overlays normally sample the coarse base DEM (kept at its native ~6 m), but the detail mesh carves
/// valleys deeper than the base, so a trail seated on the base floats over the rendered 1 m surface
/// ("szlaki latają"). Within the detail window this field returns the detail elevation instead, which
/// matches the drawn mesh; outside it the caller falls back to the base.
/// </para>
/// <para>
/// CRUCIAL: the per-tile renderer does NOT draw the full 1 m raster — the vertex budget demotes most
/// tiles to a coarser subsample step (step 8 = an 8 m mesh), so the drawn geometry is much smoother than
/// the raw 1 m. Seating overlays on the raw 1 m therefore floats/cuts them against the coarser drawn mesh
/// (a 1 m ridge crest pokes above the 8 m mesh; a 1 m gully sinks below it). When the per-tile
/// <c>plan</c> is supplied this field samples each point on that tile's OWN step grid — i.e. it
/// reconstructs the exact surface <see cref="TerrainMesh3D.BuildAdaptiveTiles"/> renders — so overlays
/// stay glued no matter how coarse the local mesh is. With no plan it falls back to full 1 m bilinear.
/// </para>
/// A bounds check is mandatory before sampling: <see cref="DemRaster.SampleBilinear"/> clamps
/// out-of-bounds lon/lat to the edge value, so without it a vertex just outside the window would seat
/// on the edge height. NoData samples are reported as a miss so the caller keeps the base there.
/// </summary>
public sealed class DetailElevationField
{
    /// <summary>The detail DEM covering the near-field window (1 m where covered, base-filled in voids).</summary>
    public DemRaster Raster { get; }

    /// <summary>Per-tile LOD decisions the renderer used to mesh <see cref="Raster"/>; null = mesh is full 1 m.</summary>
    private readonly IReadOnlyList<PerTileLodDecision>? plan;

    public DetailElevationField(DemRaster raster, IReadOnlyList<PerTileLodDecision>? plan = null)
    {
        ArgumentNullException.ThrowIfNull(raster);
        Raster = raster;
        this.plan = plan is { Count: > 0 } ? plan : null;
    }

    /// <summary>
    /// Returns the elevation of the RENDERED surface at <paramref name="longitude"/>/<paramref name="latitude"/>
    /// (the per-tile subsampled mesh when a plan is set, else full 1 m); false (so the caller uses the base)
    /// when the point is outside the window or samples as NoData.
    /// </summary>
    public bool TryGetElevation(double longitude, double latitude, out double elevation)
    {
        elevation = 0.0;
        if (longitude < Raster.West || longitude > Raster.East ||
            latitude < Raster.South || latitude > Raster.North)
        {
            return false;
        }

        if (plan is null)
        {
            double sampled = Raster.SampleBilinear(longitude, latitude);
            if (sampled == Raster.NoDataValue)
            {
                return false;
            }

            elevation = sampled;
            return true;
        }

        return TrySampleRenderedSurface(longitude, latitude, out elevation);
    }

    /// <summary>
    /// Reconstructs the per-tile rendered surface: maps the point to fractional raster cell coords, finds the
    /// plan tile that owns it, snaps to that tile's subsample-step grid (the actual mesh vertices), and
    /// bilinearly blends over the 4 step-grid corners — NoData-aware, mirroring <see cref="DemRaster.SampleBilinear"/>.
    /// </summary>
    private bool TrySampleRenderedSurface(double longitude, double latitude, out double elevation)
    {
        elevation = 0.0;

        double colFloat = (longitude - Raster.West) / (Raster.East - Raster.West) * (Raster.Columns - 1);
        double rowFloat = (Raster.North - latitude) / (Raster.North - Raster.South) * (Raster.Rows - 1);
        colFloat = Math.Clamp(colFloat, 0.0, Raster.Columns - 1.0);
        rowFloat = Math.Clamp(rowFloat, 0.0, Raster.Rows - 1.0);

        PerTileLodDecision? tile = FindTile(colFloat, rowFloat);
        if (tile is null)
        {
            // No owning tile (e.g. just past the planned grid) — fall back to full 1 m rather than guess.
            double sampled = Raster.SampleBilinear(longitude, latitude);
            if (sampled == Raster.NoDataValue)
            {
                return false;
            }

            elevation = sampled;
            return true;
        }

        int step = Math.Max(1, tile.SubsampleStep);

        // Snap to the tile's step grid (vertices live at ColStart + k·step / RowStart + k·step), then take the
        // grid cell that contains the point. c1/r1 are clamped to the raster edge, matching BuildAdaptiveTiles
        // reading one extra clamped cell past the owned crop.
        int c0 = tile.ColStart + ((int)Math.Floor((colFloat - tile.ColStart) / step)) * step;
        int r0 = tile.RowStart + ((int)Math.Floor((rowFloat - tile.RowStart) / step)) * step;
        c0 = Math.Clamp(c0, 0, Raster.Columns - 1);
        r0 = Math.Clamp(r0, 0, Raster.Rows - 1);
        int c1 = Math.Min(c0 + step, Raster.Columns - 1);
        int r1 = Math.Min(r0 + step, Raster.Rows - 1);

        double dc = c1 > c0 ? (colFloat - c0) / (c1 - c0) : 0.0;
        double dr = r1 > r0 ? (rowFloat - r0) / (r1 - r0) : 0.0;
        dc = Math.Clamp(dc, 0.0, 1.0);
        dr = Math.Clamp(dr, 0.0, 1.0);

        double v00 = Raster[c0, r0];
        double v01 = Raster[c1, r0];
        double v10 = Raster[c0, r1];
        double v11 = Raster[c1, r1];

        double noData = Raster.NoDataValue;
        double w00 = (1.0 - dc) * (1.0 - dr);
        double w01 = dc * (1.0 - dr);
        double w10 = (1.0 - dc) * dr;
        double w11 = dc * dr;

        double weightedSum = 0.0;
        double weightTotal = 0.0;
        if (v00 != noData) { weightedSum += v00 * w00; weightTotal += w00; }
        if (v01 != noData) { weightedSum += v01 * w01; weightTotal += w01; }
        if (v10 != noData) { weightedSum += v10 * w10; weightTotal += w10; }
        if (v11 != noData) { weightedSum += v11 * w11; weightTotal += w11; }

        if (weightTotal <= 0.0)
        {
            return false;
        }

        elevation = weightedSum / weightTotal;
        return true;
    }

    /// <summary>First plan tile whose owned cell range contains the point (boundaries shared; first match wins).</summary>
    private PerTileLodDecision? FindTile(double colFloat, double rowFloat)
    {
        foreach (PerTileLodDecision d in plan!)
        {
            if (colFloat >= d.ColStart && colFloat <= d.ColStart + d.Columns &&
                rowFloat >= d.RowStart && rowFloat <= d.RowStart + d.Rows)
            {
                return d;
            }
        }

        return null;
    }
}