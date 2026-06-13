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
/// A bounds check is mandatory before sampling: <see cref="DemRaster.SampleBilinear"/> clamps
/// out-of-bounds lon/lat to the edge value, so without it a vertex just outside the window would seat
/// on the edge height. NoData samples are reported as a miss so the caller keeps the base there.
/// </summary>
public sealed class DetailElevationField
{
    /// <summary>The detail DEM covering the near-field window (1 m where covered, base-filled in voids).</summary>
    public DemRaster Raster { get; }

    public DetailElevationField(DemRaster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        Raster = raster;
    }

    /// <summary>
    /// Returns the detail elevation at <paramref name="longitude"/>/<paramref name="latitude"/> when the
    /// point lies inside the detail raster and has real data; false (so the caller uses the base) when it
    /// is outside the window or samples as NoData.
    /// </summary>
    public bool TryGetElevation(double longitude, double latitude, out double elevation)
    {
        elevation = 0.0;
        if (longitude < Raster.West || longitude > Raster.East ||
            latitude < Raster.South || latitude > Raster.North)
        {
            return false;
        }

        double sampled = Raster.SampleBilinear(longitude, latitude);
        if (sampled == Raster.NoDataValue)
        {
            return false;
        }

        elevation = sampled;
        return true;
    }
}