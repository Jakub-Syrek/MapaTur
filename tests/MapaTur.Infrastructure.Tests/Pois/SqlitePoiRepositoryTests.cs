using FluentAssertions;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Pois;
using MapaTur.Infrastructure.Pois;

using Microsoft.Data.Sqlite;

namespace MapaTur.Infrastructure.Tests.Pois;

public sealed class SqlitePoiRepositoryTests : IDisposable
{
    private readonly string databasePath;

    public SqlitePoiRepositoryTests()
    {
        databasePath = Path.Combine(Path.GetTempPath(), $"mapatur-pois-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (File.Exists(databasePath))
        {
            try
            {
                File.Delete(databasePath);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task UpsertAsync_ThenFind_RoundTripsPoi()
    {
        using var repository = new SqlitePoiRepository(databasePath);
        var hut = new MountainPoi(1, "Schronisko Murowaniec", new GeoPoint(49.243, 20.010), PoiKind.Hut, 1500);

        await repository.UpsertAsync([hut]);
        var fetched = await repository.FindIntersectingAsync(
            new MapBounds(new GeoPoint(49.0, 19.5), new GeoPoint(49.5, 20.5)));

        fetched.Should().HaveCount(1);
        fetched[0].Id.Should().Be(1);
        fetched[0].Name.Should().Be("Schronisko Murowaniec");
        fetched[0].Kind.Should().Be(PoiKind.Hut);
        fetched[0].ElevationMeters.Should().Be(1500);
    }

    [Fact]
    public async Task FindIntersectingAsync_FiltersByBoundingBox()
    {
        using var repository = new SqlitePoiRepository(databasePath);

        var tatry = new MountainPoi(1, "Tatra hut", new GeoPoint(49.25, 20.0), PoiKind.Hut);
        var bieszczady = new MountainPoi(2, "Bieszczady shelter", new GeoPoint(49.1, 22.6), PoiKind.Shelter);

        await repository.UpsertAsync([tatry, bieszczady]);

        var found = await repository.FindIntersectingAsync(
            new MapBounds(new GeoPoint(49.2, 19.9), new GeoPoint(49.3, 20.1)));

        found.Should().HaveCount(1);
        found[0].Id.Should().Be(1);
    }

    [Fact]
    public async Task UpsertAsync_OverwritesExistingPoiWithSameId()
    {
        using var repository = new SqlitePoiRepository(databasePath);

        await repository.UpsertAsync([new MountainPoi(7, "Old name", new GeoPoint(49.25, 20.0), PoiKind.Chalet)]);
        await repository.UpsertAsync([new MountainPoi(7, "New name", new GeoPoint(49.25, 20.0), PoiKind.WildernessHut)]);

        var found = await repository.FindIntersectingAsync(
            new MapBounds(new GeoPoint(49.0, 19.5), new GeoPoint(49.5, 20.5)));

        found.Should().HaveCount(1);
        found[0].Name.Should().Be("New name");
        found[0].Kind.Should().Be(PoiKind.WildernessHut);
    }

    [Fact]
    public async Task FindIntersectingAsync_PreservesNullElevation()
    {
        using var repository = new SqlitePoiRepository(databasePath);
        await repository.UpsertAsync([new MountainPoi(3, "No-elevation viewpoint", new GeoPoint(49.25, 20.0), PoiKind.Viewpoint)]);

        var found = await repository.FindIntersectingAsync(
            new MapBounds(new GeoPoint(49.0, 19.5), new GeoPoint(49.5, 20.5)));

        found.Should().HaveCount(1);
        found[0].ElevationMeters.Should().BeNull();
    }
}
