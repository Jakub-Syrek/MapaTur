using FluentAssertions;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;
using MapaTur.Infrastructure.Trails;
using Microsoft.Data.Sqlite;

namespace MapaTur.Infrastructure.Tests.Trails;

public sealed class SqliteTrailRepositoryTests : IDisposable
{
    private readonly string databasePath;

    public SqliteTrailRepositoryTests()
    {
        databasePath = Path.Combine(Path.GetTempPath(), $"mapatur-trails-{Guid.NewGuid():N}.db");
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
    public async Task UpsertAsync_ThenFind_RoundTripsTrails()
    {
        using var repository = new SqliteTrailRepository(databasePath);
        var trail = CreateTrail(id: 1, color: PttkColor.Red);

        await repository.UpsertAsync([trail]);
        var fetched = await repository.FindIntersectingAsync(
            new MapBounds(new GeoPoint(48.0, 19.0), new GeoPoint(50.0, 21.0)));

        fetched.Should().HaveCount(1);
        fetched[0].Id.Should().Be(1);
        fetched[0].Name.Should().Be(trail.Name);
        fetched[0].Geometry.Should().HaveCount(trail.Geometry.Count);
        fetched[0].PrimaryColor.Should().Be(PttkColor.Red);
    }

    [Fact]
    public async Task FindIntersectingAsync_FiltersByBoundingBox()
    {
        using var repository = new SqliteTrailRepository(databasePath);

        var tatry = CreateTrail(id: 1, color: PttkColor.Red);
        var bieszczady = new Trail(
            id: 2,
            name: "Bieszczady ridge",
            markings: [new TrailMarking(PttkColor.Blue, "blue:white")],
            geometry:
            [
                new GeoPoint(49.1, 22.6),
                new GeoPoint(49.15, 22.65),
            ]);

        await repository.UpsertAsync([tatry, bieszczady]);

        var tatryBounds = new MapBounds(new GeoPoint(49.2, 19.9), new GeoPoint(49.3, 20.1));
        var found = await repository.FindIntersectingAsync(tatryBounds);

        found.Should().HaveCount(1);
        found[0].Id.Should().Be(1);
    }

    [Fact]
    public async Task UpsertAsync_OverwritesExistingTrailWithSameId()
    {
        using var repository = new SqliteTrailRepository(databasePath);

        await repository.UpsertAsync([CreateTrail(id: 1, color: PttkColor.Red)]);
        await repository.UpsertAsync([CreateTrail(id: 1, color: PttkColor.Blue, suffix: " v2")]);

        var found = await repository.FindIntersectingAsync(
            new MapBounds(new GeoPoint(48.0, 19.0), new GeoPoint(50.0, 21.0)));

        found.Should().HaveCount(1);
        found[0].Name.Should().EndWith(" v2");
        found[0].PrimaryColor.Should().Be(PttkColor.Blue);
    }

    private static Trail CreateTrail(long id, PttkColor color, string suffix = "")
    {
        string osmc = color switch
        {
            PttkColor.Red => "red:white:red_stripe",
            PttkColor.Blue => "blue:white:blue_stripe",
            _ => "yellow:white:yellow_stripe",
        };

        return new Trail(
            id: id,
            name: $"Sample trail {id}{suffix}",
            markings: [new TrailMarking(color, osmc)],
            geometry:
            [
                new GeoPoint(49.2326, 19.9819),
                new GeoPoint(49.2310, 19.9850),
                new GeoPoint(49.2290, 19.9880),
            ]);
    }
}
