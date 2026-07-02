using System.IO;
using System.Linq;

using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour of <see cref="BakedTileAvailabilityIndex"/>: a one-shot scan of the on-disk baked pyramid
/// (<c>{root}/{z}/{x}/{y}.bdt</c>) into an in-memory <see cref="DemTileKey"/> set, backing the
/// <see cref="QuadtreeTileSelector"/>'s <c>IsBaked</c> predicate and exposing the coarsest-level roots the
/// selector descends from. Filesystem-backed but otherwise pure (no GL, no network).
/// </summary>
public sealed class BakedTileAvailabilityIndexTests
{
    private static string NewTempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mapatur-baked-index-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Writes a real (tiny) baked tile so the index scans actual files, not just empty directories.
    private static void WriteTile(string root, DemTileKey key)
    {
        var bounds = new MapBounds(new GeoPoint(49.0, 20.0), new GeoPoint(49.1, 20.1));
        var heights = new float[4];
        for (int i = 0; i < heights.Length; i++)
        {
            heights[i] = i + 1f;
        }

        var tile = new BakedDemTile(key.Zoom, key.X, key.Y, 2, 2, bounds, -9999.0, heights);
        string path = Path.Combine(root, BakedDemTileStore.RelativePathFor(key));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream fs = File.Create(path);
        BakedDemTileStore.Write(fs, tile);
    }

    [Fact]
    public void MissingRoot_IsEmpty_AndIsBakedAlwaysFalse()
    {
        string root = Path.Combine(Path.GetTempPath(), "mapatur-baked-index-tests", "does-not-exist", Path.GetRandomFileName());

        var index = BakedTileAvailabilityIndex.Scan(root);

        index.Count.Should().Be(0);
        index.IsBaked(new DemTileKey(16, 1, 2)).Should().BeFalse();
        index.Roots.Should().BeEmpty();
    }

    [Fact]
    public void Scan_FindsEveryBakedTile_AcrossZoomLevels()
    {
        string root = NewTempRoot();
        WriteTile(root, new DemTileKey(13, 4480, 2816));
        WriteTile(root, new DemTileKey(16, 35843, 22531));
        WriteTile(root, new DemTileKey(16, 35844, 22531));

        var index = BakedTileAvailabilityIndex.Scan(root);

        index.Count.Should().Be(3);
        index.IsBaked(new DemTileKey(13, 4480, 2816)).Should().BeTrue();
        index.IsBaked(new DemTileKey(16, 35843, 22531)).Should().BeTrue();
        index.IsBaked(new DemTileKey(16, 35844, 22531)).Should().BeTrue();
        index.IsBaked(new DemTileKey(16, 99999, 1)).Should().BeFalse();
    }

    [Fact]
    public void Roots_AreTheDistinctTilesAtTheCoarsestPresentZoom()
    {
        string root = NewTempRoot();
        // Two z13 tiles + some finer ones. The coarsest present zoom is 13, so the roots are exactly those z13 tiles.
        WriteTile(root, new DemTileKey(13, 4480, 2816));
        WriteTile(root, new DemTileKey(13, 4481, 2816));
        WriteTile(root, new DemTileKey(14, 8960, 5632));
        WriteTile(root, new DemTileKey(16, 35843, 22531));

        var index = BakedTileAvailabilityIndex.Scan(root);

        index.MinZoom.Should().Be(13);
        index.Roots.Should().BeEquivalentTo(new[]
        {
            new DemTileKey(13, 4480, 2816),
            new DemTileKey(13, 4481, 2816),
        });
    }

    [Fact]
    public void Scan_IgnoresNonBdtFiles()
    {
        string root = NewTempRoot();
        WriteTile(root, new DemTileKey(16, 35843, 22531));
        // A stray file with the wrong extension in the tree must not be counted.
        string strayDir = Path.Combine(root, "16", "35843");
        Directory.CreateDirectory(strayDir);
        File.WriteAllText(Path.Combine(strayDir, "notes.txt"), "ignore me");

        var index = BakedTileAvailabilityIndex.Scan(root);

        index.Count.Should().Be(1);
    }
}