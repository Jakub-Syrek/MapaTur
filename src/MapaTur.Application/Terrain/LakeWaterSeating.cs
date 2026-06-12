namespace MapaTur.Application.Terrain;

/// <summary>
/// Seats a lake's water plane against the terrain ACTUALLY LOADED at the lake (not blindly at OSM
/// elevation). A fine DEM records the water surface as flat ground at the waterline, so terrain ≈ lake
/// elevation and the proven legacy seating (elevation + lift) applies. A coarse whole-range base FILLS
/// small basins (surrounding slopes average into the subsampled cells) — a blind plane then pokes through
/// the shifted slopes inside the outline as chains of dark slivers along valleys; such lakes are skipped
/// at that LOD (the streamed 1 m detail restores them up close). A basin the coarse base OVER-deepens
/// would leave the shoreline hanging in the air, so the plane sinks to the loaded bed instead.
/// </summary>
public static class LakeWaterSeating
{
    /// <summary>
    /// Decides the water-plane elevation for one lake, or <c>null</c> to skip it at this LOD.
    /// </summary>
    /// <param name="lakeElevationMeters">The lake's nominal waterline (OSM/gazetteer elevation).</param>
    /// <param name="terrainElevationMeters">Loaded-terrain elevation sampled at the lake's centroid; NaN = no sample.</param>
    /// <param name="liftMeters">Lift above the seat so the plane clears the bed (legacy 4 m).</param>
    /// <param name="fillSkipToleranceMeters">Terrain above the waterline by more than this ⇒ basin filled ⇒ skip.</param>
    /// <param name="sinkToleranceMeters">Terrain below the waterline by more than this ⇒ sink the plane to the bed.</param>
    public static float? Seat(
        double lakeElevationMeters,
        double terrainElevationMeters,
        float liftMeters = 4f,
        double fillSkipToleranceMeters = 10.0,
        double sinkToleranceMeters = 15.0)
    {
        if (double.IsNaN(terrainElevationMeters))
        {
            return (float)(lakeElevationMeters + liftMeters);
        }

        double rise = terrainElevationMeters - lakeElevationMeters;
        if (rise > fillSkipToleranceMeters)
        {
            return null; // basin filled at this LOD — a plane here only pokes through shifted slopes
        }

        if (rise < -sinkToleranceMeters)
        {
            return (float)(terrainElevationMeters + liftMeters); // over-deepened — sink to the loaded bed
        }

        return (float)(lakeElevationMeters + liftMeters); // terrain ≈ waterline — proven legacy seating
    }
}