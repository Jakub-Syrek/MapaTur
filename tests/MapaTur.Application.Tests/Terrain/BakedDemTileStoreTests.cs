using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Round-trip pinning for <see cref="BakedDemTileStore"/>: a baked tile written to the binary format and
/// read back must be identical in every field (zoom/address, dimensions, bounds, NoData sentinel and every
/// height sample). The baked cache is the offline foundation later LOD stages stream instead of mosaicking
/// and repairing at render time, so its IO must be lossless and stable.
/// </summary>
public sealed class BakedDemTileStoreTests
{
    private static BakedDemTile SampleTile()
    {
        var bounds = new MapBounds(new GeoPoint(49.1, 19.9), new GeoPoint(49.2, 20.1));
        var heights = new float[6 * 4];
        for (int i = 0; i < heights.Length; i++)
        {
            heights[i] = 1000f + (i * 1.5f);
        }

        heights[5] = -9999f; // a NoData cell survives the round-trip verbatim
        return new BakedDemTile(16, 36210, 22550, 6, 4, bounds, -9999.0, heights);
    }

    [Fact]
    public void WriteThenRead_RoundTripsEveryField()
    {
        BakedDemTile original = SampleTile();

        using var stream = new MemoryStream();
        BakedDemTileStore.Write(stream, original);
        stream.Position = 0;
        BakedDemTile restored = BakedDemTileStore.Read(stream);

        restored.Zoom.Should().Be(original.Zoom);
        restored.TileX.Should().Be(original.TileX);
        restored.TileY.Should().Be(original.TileY);
        restored.Columns.Should().Be(original.Columns);
        restored.Rows.Should().Be(original.Rows);
        restored.NoDataValue.Should().Be(original.NoDataValue);
        restored.Bounds.Should().Be(original.Bounds);
    }

    [Fact]
    public void WriteThenRead_RoundTripsEveryHeightSample()
    {
        BakedDemTile original = SampleTile();

        using var stream = new MemoryStream();
        BakedDemTileStore.Write(stream, original);
        stream.Position = 0;
        BakedDemTile restored = BakedDemTileStore.Read(stream);

        restored.Heights.Should().Equal(original.Heights);
    }

    [Fact]
    public void Read_RejectsAStreamWithoutTheMagicHeader()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var act = () => BakedDemTileStore.Read(stream);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void RelativePathFor_LaysTilesOutByZoomThenXThenY()
    {
        string path = BakedDemTileStore.RelativePathFor(new DemTileKey(15, 18105, 11275));

        path.Should().Be(Path.Combine("15", "18105", "11275.bdt"));
    }
}