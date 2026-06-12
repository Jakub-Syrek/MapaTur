using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Lake-water seating vs the LOADED terrain. The water plane used to sit blindly at OSM elevation + lift;
/// on a coarse whole-range base the subsample FILLS small basins (surrounding slopes average in), so the
/// plane poked through shifted slopes inside the outline — chains of dark slivers along valleys ("dziury").
/// Seat() decides per lake, from the terrain elevation actually loaded at the lake's centroid:
///   - basin FILLED at this LOD (terrain well above the waterline) → null = skip the lake entirely;
///   - basin OVER-DEEPENED/shifted (terrain well below the waterline) → sink the plane to the loaded bed,
///     so the shoreline doesn't hang in the air;
///   - terrain ≈ waterline (fine DEM records the water surface as ground) → legacy seating (elev + lift).
/// </summary>
public sealed class LakeWaterSeatingTests
{
    [Fact]
    public void Seat_TerrainMatchesWaterline_UsesLegacyElevationPlusLift()
    {
        // Fine DEM (1 m / z13 window): LiDAR records the lake surface as flat ground at the waterline.
        float? seat = LakeWaterSeating.Seat(lakeElevationMeters: 1395.0, terrainElevationMeters: 1396.0);

        seat.Should().Be(1399f, "terrain ≈ waterline → the proven legacy seating (elev + 4 m lift)");
    }

    [Fact]
    public void Seat_BasinFilledByCoarseBase_SkipsTheLake()
    {
        // Whole-Tatra base (~60-90 m cells): surrounding slopes average into the tarn → terrain at the
        // centroid rises tens of metres above the waterline. The plane would only show as slivers poking
        // through shifted slopes — the artifact. Skip; the streamed 1 m detail restores the lake up close.
        float? seat = LakeWaterSeating.Seat(lakeElevationMeters: 1670.0, terrainElevationMeters: 1720.0);

        seat.Should().BeNull("a filled basin cannot hold water at this LOD");
    }

    [Fact]
    public void Seat_BasinOverDeepened_SinksThePlaneToTheLoadedBed()
    {
        // Coarse cell dips below the true waterline (valley averaged in): a plane at OSM elevation would
        // hang in the air past the shoreline. Sink it just above the loaded bed instead (a smaller puddle).
        float? seat = LakeWaterSeating.Seat(lakeElevationMeters: 1670.0, terrainElevationMeters: 1640.0);

        seat.Should().Be(1644f, "over-deepened basin → bed + 4 m lift");
    }

    [Fact]
    public void Seat_NoTerrainSample_KeepsLegacySeating()
    {
        // NoData / no raster: no information — keep the proven legacy behaviour.
        float? seat = LakeWaterSeating.Seat(lakeElevationMeters: 1395.0, terrainElevationMeters: double.NaN);

        seat.Should().Be(1399f);
    }

    [Fact]
    public void Seat_SmallDeviationsWithinTolerance_KeepLegacySeating()
    {
        // ±tolerances: fine-DEM noise (a few metres) must not change the user-approved water look.
        LakeWaterSeating.Seat(1670.0, 1676.0).Should().Be(1674f, "within the fill tolerance (10 m)");
        LakeWaterSeating.Seat(1670.0, 1660.0).Should().Be(1674f, "within the sink tolerance (15 m)");
    }
}