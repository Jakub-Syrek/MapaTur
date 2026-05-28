using FluentAssertions;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Tracks;

namespace MapaTur.Domain.Tests.Tracks;

public sealed class TrackTests
{
    [Fact]
    public void Constructor_RejectsEmptyPointList()
    {
        var act = () => new Track(Guid.NewGuid(), "Empty", []);

        act.Should().Throw<ArgumentException>().WithParameterName("points");
    }

    [Fact]
    public void StartedAtAndEndedAt_ReadFromFirstAndLastPoint()
    {
        var start = new DateTimeOffset(2026, 3, 12, 7, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 3, 12, 9, 0, 0, TimeSpan.Zero);
        var track = new Track(Guid.NewGuid(), "T",
        [
            new TrackPoint(new GeoPoint(49.0, 19.0), start),
            new TrackPoint(new GeoPoint(49.1, 19.1), end),
        ]);

        track.StartedAt.Should().Be(start);
        track.EndedAt.Should().Be(end);
    }

    [Fact]
    public void ComputeDistanceMeters_SumsConsecutiveHaversineDistances()
    {
        var track = new Track(Guid.NewGuid(), "T",
        [
            new TrackPoint(new GeoPoint(49.2326, 19.9819), DateTimeOffset.UnixEpoch),
            new TrackPoint(new GeoPoint(49.2310, 19.9850), DateTimeOffset.UnixEpoch.AddMinutes(5)),
            new TrackPoint(new GeoPoint(49.2290, 19.9880), DateTimeOffset.UnixEpoch.AddMinutes(10)),
        ]);

        double distance = track.ComputeDistanceMeters();

        // Two short segments in the Tatras, roughly 250m + 250m
        distance.Should().BeGreaterThan(300.0).And.BeLessThan(700.0);
    }
}