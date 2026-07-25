using System;
using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>Behaviour of the ortho-colour scatter: <see cref="OrthoScatterClassifier"/> (colour → class) and
/// <see cref="TerrainScatter"/> (green → trees, grey → stones, gated by treeline/slope, deterministic).</summary>
public sealed class TerrainScatterTests
{
    private const int Cols = 24;
    private const int Rows = 24;

    // Flat DEM at a fixed elevation (slope 0 → trees eligible when below the treeline).
    private static DemRaster FlatRaster(float elevation)
    {
        var bounds = new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(49.1, 19.1));
        var samples = new float[Cols * Rows];
        Array.Fill(samples, elevation);
        return new DemRaster(Cols, Rows, bounds, samples);
    }

    private static Func<Vector2, Vector3?> Ortho(Vector3 rgb) => _ => rgb;

    private static readonly Vector3 Green = new(0.30f, 0.60f, 0.30f);
    private static readonly Vector3 Grey = new(0.50f, 0.50f, 0.50f);
    private static readonly Vector3 Blue = new(0.20f, 0.30f, 0.60f);
    private static readonly Vector3 White = new(0.95f, 0.96f, 0.97f);
    private static readonly Vector3 Dark = new(0.10f, 0.10f, 0.10f);

    [Theory]
    [InlineData(0.30f, 0.60f, 0.30f, ScatterClass.Vegetation)]
    [InlineData(0.50f, 0.50f, 0.50f, ScatterClass.RockScree)]
    [InlineData(0.20f, 0.30f, 0.60f, ScatterClass.Water)]
    [InlineData(0.95f, 0.96f, 0.97f, ScatterClass.Snow)]
    [InlineData(0.10f, 0.10f, 0.10f, ScatterClass.Bare)]
    public void Classify_MapsColourToClass(float r, float g, float b, ScatterClass expected)
    {
        OrthoScatterClassifier.Classify(new Vector3(r, g, b)).Should().Be(expected);
    }

    [Fact]
    public void GreenOrtho_BelowTreeline_PlacesTrees_NotRocks()
    {
        var raster = FlatRaster(1000f);
        var frame = TerrainMesh3D.Build(raster);

        var (trees, rocks) = TerrainScatter.Generate(raster, frame, Ortho(Green), new ScatterOptions());

        trees.Should().NotBeEmpty("green pixels below the treeline grow trees");
        rocks.Should().BeEmpty("green is not rock");
    }

    [Fact]
    public void GreyOrtho_PlacesRocks_NotTrees()
    {
        var raster = FlatRaster(1000f);
        var frame = TerrainMesh3D.Build(raster);

        var (trees, rocks) = TerrainScatter.Generate(raster, frame, Ortho(Grey), new ScatterOptions());

        rocks.Should().NotBeEmpty("desaturated grey is scree → stones");
        trees.Should().BeEmpty("grey is not vegetation");
    }

    [Fact]
    public void WaterOrtho_PlacesNothing()
    {
        var raster = FlatRaster(1000f);
        var frame = TerrainMesh3D.Build(raster);

        var (trees, rocks) = TerrainScatter.Generate(raster, frame, Ortho(Blue), new ScatterOptions());

        trees.Should().BeEmpty();
        rocks.Should().BeEmpty("water is a hard exclusion");
    }

    [Fact]
    public void GreenOrtho_AboveTreeline_PlacesNoTrees()
    {
        var raster = FlatRaster(2000f); // above the 1550 m treeline
        var frame = TerrainMesh3D.Build(raster);

        var (trees, _) = TerrainScatter.Generate(raster, frame, Ortho(Green), new ScatterOptions());

        trees.Should().BeEmpty("nothing tall grows above the treeline");
    }

    [Fact]
    public void Generate_IsDeterministic()
    {
        var raster = FlatRaster(1000f);
        var frame = TerrainMesh3D.Build(raster);

        var a = TerrainScatter.Generate(raster, frame, Ortho(Green), new ScatterOptions());
        var b = TerrainScatter.Generate(raster, frame, Ortho(Green), new ScatterOptions());

        b.Trees.Should().BeEquivalentTo(a.Trees, o => o.WithStrictOrdering());
    }

    [Fact]
    public void LowerDensity_YieldsFewerTrees()
    {
        var raster = FlatRaster(1000f);
        var frame = TerrainMesh3D.Build(raster);

        int dense = TerrainScatter.Generate(raster, frame, Ortho(Green), new ScatterOptions(TreeDensity: 1.0f)).Trees.Count;
        int sparse = TerrainScatter.Generate(raster, frame, Ortho(Green), new ScatterOptions(TreeDensity: 0.3f)).Trees.Count;

        sparse.Should().BeLessThan(dense);
        sparse.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TreeTint_LeansTowardTheLocalOrthoColour()
    {
        var raster = FlatRaster(1000f);
        var frame = TerrainMesh3D.Build(raster);

        var (trees, _) = TerrainScatter.Generate(raster, frame, Ortho(Green), new ScatterOptions());

        trees.Should().OnlyContain(t => t.Tint != Vector3.One, "each instance is tinted from its ortho pixel");
        trees[0].Tint.Y.Should().BeGreaterThan(trees[0].Tint.X, "a green pixel tints the tree greener");
    }
}
