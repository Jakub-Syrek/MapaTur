using FluentAssertions;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Tracks;
using MapaTur.Infrastructure.Tracks;

using Microsoft.Data.Sqlite;

namespace MapaTur.Infrastructure.Tests.Tracks;

public sealed class SqliteTrackRepositoryTests : IDisposable
{
    private readonly string databasePath;

    public SqliteTrackRepositoryTests()
    {
        databasePath = Path.Combine(Path.GetTempPath(), $"mapatur-tracks-{Guid.NewGuid():N}.db");
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

    private static Track MakeTrack(string name, params (double Lat, double Lon, double? Ele)[] points)
    {
        var trackPoints = points
            .Select(p => new TrackPoint(new GeoPoint(p.Lat, p.Lon, p.Ele), DateTimeOffset.UnixEpoch))
            .ToList();
        return new Track(Guid.NewGuid(), name, trackPoints);
    }

    [Fact]
    public async Task AddAsync_ThenGetAll_RoundTripsTrack()
    {
        using var repository = new SqliteTrackRepository(databasePath);
        var track = MakeTrack("Grań pozaszlak", (49.23, 19.98, 1985.0), (49.24, 19.99, 2040.0));

        await repository.AddAsync(track);
        var all = await repository.GetAllAsync();

        all.Should().HaveCount(1);
        all[0].Id.Should().Be(track.Id);
        all[0].Name.Should().Be("Grań pozaszlak");
        all[0].Points.Should().HaveCount(2);
        all[0].Points[0].Position.Latitude.Should().BeApproximately(49.23, 1e-6);
        all[0].Points[0].Position.Longitude.Should().BeApproximately(19.98, 1e-6);
        all[0].Points[0].Position.ElevationMeters.Should().BeApproximately(1985.0, 1e-3);
    }

    [Fact]
    public async Task GetAll_PreservesNullElevation()
    {
        using var repository = new SqliteTrackRepository(databasePath);
        var track = MakeTrack("no-ele", (49.23, 19.98, null), (49.24, 19.99, null));

        await repository.AddAsync(track);
        var all = await repository.GetAllAsync();

        all[0].Points[0].Position.ElevationMeters.Should().BeNull();
    }

    [Fact]
    public async Task GetAll_ReturnsTracksInInsertionOrder()
    {
        using var repository = new SqliteTrackRepository(databasePath);
        await repository.AddAsync(MakeTrack("first", (49.1, 19.1, null), (49.2, 19.2, null)));
        await repository.AddAsync(MakeTrack("second", (49.3, 19.3, null), (49.4, 19.4, null)));
        await repository.AddAsync(MakeTrack("third", (49.5, 19.5, null), (49.6, 19.6, null)));

        var all = await repository.GetAllAsync();

        all.Select(t => t.Name).Should().ContainInOrder("first", "second", "third");
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyTheGivenTrack()
    {
        using var repository = new SqliteTrackRepository(databasePath);
        var keep = MakeTrack("keep", (49.1, 19.1, null), (49.2, 19.2, null));
        var drop = MakeTrack("drop", (49.3, 19.3, null), (49.4, 19.4, null));
        await repository.AddAsync(keep);
        await repository.AddAsync(drop);

        await repository.DeleteAsync(drop.Id);

        var all = await repository.GetAllAsync();
        all.Should().HaveCount(1);
        all[0].Id.Should().Be(keep.Id);
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_IsNoOp()
    {
        using var repository = new SqliteTrackRepository(databasePath);
        await repository.AddAsync(MakeTrack("keep", (49.1, 19.1, null), (49.2, 19.2, null)));

        await repository.DeleteAsync(Guid.NewGuid());

        (await repository.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AddAsync_SameId_ReplacesExisting()
    {
        using var repository = new SqliteTrackRepository(databasePath);
        var id = Guid.NewGuid();
        var v1 = new Track(id, "old", [new TrackPoint(new GeoPoint(49.1, 19.1), DateTimeOffset.UnixEpoch), new TrackPoint(new GeoPoint(49.2, 19.2), DateTimeOffset.UnixEpoch)]);
        var v2 = new Track(id, "new", [new TrackPoint(new GeoPoint(49.3, 19.3), DateTimeOffset.UnixEpoch), new TrackPoint(new GeoPoint(49.4, 19.4), DateTimeOffset.UnixEpoch)]);

        await repository.AddAsync(v1);
        await repository.AddAsync(v2);

        var all = await repository.GetAllAsync();
        all.Should().HaveCount(1);
        all[0].Name.Should().Be("new");
    }

    [Fact]
    public async Task CountAsync_ReflectsNumberOfTracks()
    {
        using var repository = new SqliteTrackRepository(databasePath);
        (await repository.CountAsync()).Should().Be(0);

        await repository.AddAsync(MakeTrack("a", (49.1, 19.1, null), (49.2, 19.2, null)));
        await repository.AddAsync(MakeTrack("b", (49.3, 19.3, null), (49.4, 19.4, null)));

        (await repository.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Data_SurvivesReopeningTheDatabase()
    {
        var track = MakeTrack("persistent", (49.1, 19.1, 1000.0), (49.2, 19.2, 1100.0));
        using (var repository = new SqliteTrackRepository(databasePath))
        {
            await repository.AddAsync(track);
        }

        SqliteConnection.ClearAllPools();

        using var reopened = new SqliteTrackRepository(databasePath);
        var all = await reopened.GetAllAsync();

        all.Should().HaveCount(1);
        all[0].Name.Should().Be("persistent");
        all[0].Points[1].Position.ElevationMeters.Should().BeApproximately(1100.0, 1e-3);
    }
}