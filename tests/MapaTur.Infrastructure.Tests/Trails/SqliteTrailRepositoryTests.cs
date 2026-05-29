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

    [Fact]
    public async Task UpsertAsync_WithSimplification_ReducesStoredVertexCount()
    {
        using var repository = new SqliteTrailRepository(databasePath, simplificationEpsilonMeters: 10.0);

        // 30 colinear points along a ~500 m chord → all interior points sit on the chord,
        // so 10 m Douglas–Peucker should collapse them to just the two endpoints.
        var geometry = new List<GeoPoint>();
        for (int i = 0; i < 30; i++)
        {
            geometry.Add(new GeoPoint(49.20 + (i * 0.00016), 19.50));
        }
        var trail = new Trail(
            id: 99,
            name: "Long straight trail",
            markings: [new TrailMarking(PttkColor.Red, "red:white:red_stripe")],
            geometry: geometry);

        await repository.UpsertAsync([trail]);
        var fetched = await repository.FindIntersectingAsync(
            new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0)));

        fetched.Should().HaveCount(1);
        fetched[0].Geometry.Count.Should().BeLessThan(geometry.Count);
        fetched[0].Geometry[0].Should().Be(geometry[0]);
        fetched[0].Geometry[^1].Should().Be(geometry[^1]);
    }

    [Fact]
    public async Task FindIntersectingAsync_WithReadTimeEpsilon_AppliesAdditionalSimplification()
    {
        // Write with no simplification, read with epsilon=50 m. The interior points of
        // a zig-zag whose deviations are all <50 m should disappear at read time.
        using var repository = new SqliteTrailRepository(databasePath, simplificationEpsilonMeters: 0.0);

        var geometry = new List<GeoPoint>();
        for (int i = 0; i < 40; i++)
        {
            // Long chord south->north; tiny east/west wobble of ±~10 m (well below 50 m).
            double wobble = (i % 2 == 0) ? 0.0 : 0.0001;
            geometry.Add(new GeoPoint(49.20 + (i * 0.00018), 19.50 + wobble));
        }
        var trail = new Trail(
            id: 7,
            name: "Wobbly trail",
            markings: [new TrailMarking(PttkColor.Green, "green:white:green_stripe")],
            geometry: geometry);
        await repository.UpsertAsync([trail]);

        var withoutSimplification = await repository.FindIntersectingAsync(
            new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0)));
        var withSimplification = await repository.FindIntersectingAsync(
            new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0)),
            simplificationEpsilonMeters: 50.0);

        withoutSimplification.Single().Geometry.Should().HaveCount(40);
        withSimplification.Single().Geometry.Count.Should().BeLessThan(40);
        withSimplification.Single().Geometry[0].Should().Be(geometry[0]);
        withSimplification.Single().Geometry[^1].Should().Be(geometry[^1]);
    }

    [Fact]
    public async Task FindIntersectingAsync_ReadTimeEpsilonZero_LeavesGeometryUntouched()
    {
        using var repository = new SqliteTrailRepository(databasePath, simplificationEpsilonMeters: 0.0);
        var trail = CreateTrail(id: 11, color: PttkColor.Red);
        await repository.UpsertAsync([trail]);

        var fetched = await repository.FindIntersectingAsync(
            new MapBounds(new GeoPoint(48.0, 19.0), new GeoPoint(50.0, 21.0)),
            simplificationEpsilonMeters: 0.0);

        fetched.Single().Geometry.Should().HaveCount(trail.Geometry.Count);
    }

    [Fact]
    public async Task UpsertAsync_WithSimplification_PreservesShortPolylines()
    {
        // Trail with only 2 points must not be touched regardless of epsilon.
        using var repository = new SqliteTrailRepository(databasePath, simplificationEpsilonMeters: 100.0);

        var trail = new Trail(
            id: 5,
            name: "Two-point trail",
            markings: [new TrailMarking(PttkColor.Blue, "blue:white:blue_stripe")],
            geometry: [new GeoPoint(49.20, 19.50), new GeoPoint(49.21, 19.51)]);

        await repository.UpsertAsync([trail]);
        var fetched = await repository.FindIntersectingAsync(
            new MapBounds(new GeoPoint(49.0, 19.0), new GeoPoint(50.0, 20.0)));

        fetched.Should().HaveCount(1);
        fetched[0].Geometry.Should().HaveCount(2);
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