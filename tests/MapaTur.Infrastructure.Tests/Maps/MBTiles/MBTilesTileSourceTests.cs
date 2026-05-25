using FluentAssertions;
using MapaTur.Domain.Maps;
using MapaTur.Infrastructure.Maps.MBTiles;
using Microsoft.Data.Sqlite;

namespace MapaTur.Infrastructure.Tests.Maps.MBTiles;

public sealed class MBTilesTileSourceTests : IDisposable
{
    private readonly string archivePath;

    public MBTilesTileSourceTests()
    {
        archivePath = Path.Combine(Path.GetTempPath(), $"mapatur-test-{Guid.NewGuid():N}.mbtiles");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (File.Exists(archivePath))
        {
            try
            {
                File.Delete(archivePath);
            }
            catch (IOException)
            {
                // Best-effort cleanup; temp folder will be cleaned by OS eventually.
            }
        }
    }

    [Fact]
    public void Constructor_ThrowsWhenFileMissing()
    {
        var act = () => new MBTilesTileSource(archivePath);

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void GetMetadata_ReturnsValuesFromMetadataTable()
    {
        CreateMBTilesFile(metadata: new()
        {
            ["name"] = "Tatry Test",
            ["format"] = "png",
            ["minzoom"] = "8",
            ["maxzoom"] = "16",
            ["bounds"] = "19.5,49.0,20.5,49.5",
            ["attribution"] = "© OpenStreetMap contributors",
        });

        using var source = new MBTilesTileSource(archivePath);
        var metadata = source.GetMetadata();

        metadata.Name.Should().Be("Tatry Test");
        metadata.Format.Should().Be(TileFormat.Png);
        metadata.MinZoomLevel.Should().Be(8);
        metadata.MaxZoomLevel.Should().Be(16);
        metadata.Attribution.Should().Be("© OpenStreetMap contributors");
        metadata.Bounds.Should().NotBeNull();
        metadata.Bounds!.Value.SouthWest.Latitude.Should().BeApproximately(49.0, 1e-9);
        metadata.Bounds.Value.NorthEast.Longitude.Should().BeApproximately(20.5, 1e-9);
    }

    [Fact]
    public async Task GetTileAsync_ReturnsBytesWhenTileExists()
    {
        byte[] payload = [0x89, 0x50, 0x4E, 0x47]; // PNG magic header
        CreateMBTilesFile(
            metadata: new() { ["name"] = "T", ["format"] = "png" },
            tiles:
            [
                // Zoom 10, x=550, y=350 (XYZ) -> TMS row = (1<<10) - 1 - 350 = 673
                new TileRow(10, 550, 673, payload),
            ]);

        using var source = new MBTilesTileSource(archivePath);
        byte[]? result = await source.GetTileAsync(new TileCoordinate(10, 550, 350));

        result.Should().NotBeNull();
        result.Should().Equal(payload);
    }

    [Fact]
    public async Task GetTileAsync_ReturnsNullWhenTileAbsent()
    {
        CreateMBTilesFile(metadata: new() { ["name"] = "Empty", ["format"] = "png" });

        using var source = new MBTilesTileSource(archivePath);
        byte[]? result = await source.GetTileAsync(new TileCoordinate(10, 1, 1));

        result.Should().BeNull();
    }

    [Fact]
    public void GetMetadata_ThrowsWhenMetadataTableMissing()
    {
        CreateRawSqliteFile();

        var act = () => new MBTilesTileSource(archivePath);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task GetTileAsync_ThrowsAfterDispose()
    {
        CreateMBTilesFile(metadata: new() { ["name"] = "T", ["format"] = "png" });

        var source = new MBTilesTileSource(archivePath);
        source.Dispose();

        var act = async () => await source.GetTileAsync(new TileCoordinate(0, 0, 0));

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    private void CreateMBTilesFile(Dictionary<string, string> metadata, IReadOnlyList<TileRow>? tiles = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = archivePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE metadata (name TEXT, value TEXT);
                CREATE TABLE tiles (zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, tile_data BLOB);
                """;
            create.ExecuteNonQuery();
        }

        foreach (var (key, value) in metadata)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO metadata (name, value) VALUES ($n, $v);";
            insert.Parameters.AddWithValue("$n", key);
            insert.Parameters.AddWithValue("$v", value);
            insert.ExecuteNonQuery();
        }

        if (tiles is not null)
        {
            foreach (var row in tiles)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO tiles (zoom_level, tile_column, tile_row, tile_data) VALUES ($z, $x, $y, $d);";
                insert.Parameters.AddWithValue("$z", row.ZoomLevel);
                insert.Parameters.AddWithValue("$x", row.Column);
                insert.Parameters.AddWithValue("$y", row.TmsRow);
                insert.Parameters.AddWithValue("$d", row.Payload);
                insert.ExecuteNonQuery();
            }
        }
    }

    private void CreateRawSqliteFile()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = archivePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE placeholder (id INTEGER);";
        command.ExecuteNonQuery();
    }

    private sealed record TileRow(int ZoomLevel, int Column, int TmsRow, byte[] Payload);
}
