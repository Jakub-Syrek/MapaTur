using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ForestPlacementTests
{
    private const int Cols = 24;
    private const int Rows = 24;

    // Synthetic DEM whose elevation ramps with the row: row r → r * 100 m (0 m … 2300 m). A gentle
    // monotone ramp, so the slope gate never trips and "below the treeline" maps to a known row range.
    private static DemRaster RampRaster()
    {
        var bounds = new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0));
        var samples = new float[Cols * Rows];
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
            {
                samples[(r * Cols) + c] = r * 100f;
            }
        }
        return new DemRaster(Cols, Rows, bounds, samples);
    }

    private static TerrainMesh3D RampMesh() => TerrainMesh3D.Build(RampRaster());

    [Fact]
    public void Generate_PlacesTreesOnlyWithinTheElevationBand()
    {
        var raster = RampRaster();
        var frame = RampMesh();
        var options = new ForestOptions(StrideCells: 2, MinElevationMeters: 250, TreelineMeters: 1500);

        var trees = ForestPlacement.Generate(raster, frame, options);

        trees.Should().NotBeEmpty();
        float exaggeration = frame.VerticalExaggeration;
        trees.Should().OnlyContain(t =>
            t.Position.Z >= (float)(options.MinElevationMeters * exaggeration) - 0.01f &&
            t.Position.Z <= (float)(options.TreelineMeters * exaggeration) + 0.01f);
    }

    [Fact]
    public void Generate_IsDeterministic()
    {
        var raster = RampRaster();
        var frame = RampMesh();
        var options = new ForestOptions(StrideCells: 2);

        var a = ForestPlacement.Generate(raster, frame, options);
        var b = ForestPlacement.Generate(raster, frame, options);

        b.Should().BeEquivalentTo(a, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Generate_LowerDensityYieldsFewerTrees()
    {
        var raster = RampRaster();
        var frame = RampMesh();

        int dense = ForestPlacement.Generate(raster, frame, new ForestOptions(StrideCells: 2, Density: 1.0f)).Count;
        int sparse = ForestPlacement.Generate(raster, frame, new ForestOptions(StrideCells: 2, Density: 0.3f)).Count;

        sparse.Should().BeLessThan(dense);
        sparse.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Generate_PerTreeScaleWithinConfiguredRange()
    {
        var raster = RampRaster();
        var frame = RampMesh();
        var options = new ForestOptions(StrideCells: 3, MinScale: 0.6f, MaxScale: 1.5f);

        var trees = ForestPlacement.Generate(raster, frame, options);

        trees.Should().OnlyContain(t => t.Scale >= options.MinScale - 0.001f && t.Scale <= options.MaxScale + 0.001f);
    }
}