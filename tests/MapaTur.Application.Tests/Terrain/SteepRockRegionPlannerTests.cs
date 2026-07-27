using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class SteepRockRegionPlannerTests
{
    private static readonly GeoPoint Anchor = new(49.25, 19.95);

    [Fact]
    public void should_return_no_regions_for_ground_where_ortho_projection_is_not_stretched()
    {
        // Arrange
        DemRaster raster = CreateRaster((east, north) => 2000f + (east * 0.1f));

        // Act
        IReadOnlyList<SteepRockRegion> regions = SteepRockRegionPlanner.Plan(
            raster,
            Anchor,
            Options());

        // Assert
        regions.Should().BeEmpty();
    }

    [Fact]
    public void should_detect_a_steep_face_and_point_its_normal_out_of_the_slope()
    {
        // Arrange
        float gradient = MathF.Tan(70f * MathF.PI / 180f);
        DemRaster raster = CreateRaster((east, north) => 2000f + (east * gradient));

        // Act
        IReadOnlyList<SteepRockRegion> regions = SteepRockRegionPlanner.Plan(
            raster,
            Anchor,
            Options());

        // Assert
        regions.Should().OnlyContain(region =>
            region.OutwardNormal.X < -0.9f
            && MathF.Abs(region.OutwardNormal.Y) < 0.1f
            && region.WidthMeters >= 8f
            && region.HeightMeters >= 20f);
    }

    [Fact]
    public void should_keep_opposing_faces_as_separate_region_directions()
    {
        // Arrange
        float gradient = MathF.Tan(68f * MathF.PI / 180f);
        DemRaster raster = CreateRaster((east, north) => 2200f + (MathF.Abs(east) * gradient));

        // Act
        IReadOnlyList<SteepRockRegion> regions = SteepRockRegionPlanner.Plan(
            raster,
            Anchor,
            Options() with { BlockSizeMeters = 18f });

        // Assert
        regions.Should().Contain(region => region.OutwardNormal.X < -0.8f);
        regions.Should().Contain(region => region.OutwardNormal.X > 0.8f);
    }

    [Fact]
    public void should_ignore_one_cell_spikes_that_would_create_isolated_rock_stamps()
    {
        // Arrange
        DemRaster raster = CreateRaster((east, north) =>
            MathF.Abs(east) < 0.6f && MathF.Abs(north) < 0.6f ? 2050f : 2000f);

        // Act
        IReadOnlyList<SteepRockRegion> regions = SteepRockRegionPlanner.Plan(
            raster,
            Anchor,
            Options());

        // Assert
        regions.Should().BeEmpty();
    }

    [Fact]
    public void should_anchor_region_centres_on_the_original_dem_surface()
    {
        // Arrange
        float gradient = MathF.Tan(65f * MathF.PI / 180f);
        DemRaster raster = CreateRaster((east, north) => 2100f + (east * gradient));

        // Act
        SteepRockRegion region = SteepRockRegionPlanner.Plan(raster, Anchor, Options())[0];
        GeoPoint geo = LocalTangentProjection.WorldToGeo(region.Center, Anchor);
        double expectedElevation = raster.SampleBilinear(geo.Longitude, geo.Latitude);

        // Assert
        region.Center.Z.Should().BeApproximately((float)expectedElevation, 0.2f);
    }

    private static SteepRockRegionOptions Options() => new(
        MinimumSlopeDegrees: 55f,
        BlockSizeMeters: 24f,
        MinimumSteepCoverageFraction: 0.25f,
        MinimumWidthMeters: 8f,
        MinimumHeightMeters: 8f,
        BorderOverlapMeters: 3f);

    private static DemRaster CreateRaster(Func<float, float, float> elevation)
    {
        const int columns = 41;
        const int rows = 41;
        const float spacingMeters = 1f;
        double halfWidth = ((columns - 1) * spacingMeters) * 0.5;
        double halfHeight = ((rows - 1) * spacingMeters) * 0.5;
        double latitudeDegrees = halfHeight / LocalTangentProjection.MetersPerLatDegree;
        double longitudeDegrees = halfWidth
            / (LocalTangentProjection.MetersPerLatDegree * Math.Cos(Anchor.Latitude * Math.PI / 180.0));
        var bounds = new MapBounds(
            new GeoPoint(Anchor.Latitude - latitudeDegrees, Anchor.Longitude - longitudeDegrees),
            new GeoPoint(Anchor.Latitude + latitudeDegrees, Anchor.Longitude + longitudeDegrees));
        var samples = new float[columns * rows];
        for (int row = 0; row < rows; row++)
        {
            float north = (float)(halfHeight - (row * spacingMeters));
            for (int column = 0; column < columns; column++)
            {
                float east = (float)(-halfWidth + (column * spacingMeters));
                samples[(row * columns) + column] = elevation(east, north);
            }
        }

        return new DemRaster(columns, rows, bounds, samples);
    }
}
