using FluentAssertions;

using MapaTur.Infrastructure.Tracks;

namespace MapaTur.Infrastructure.Tests.Tracks;

public sealed class TcxParserTests
{
    private static readonly string SampleTcxPath = Path.Combine(AppContext.BaseDirectory, "testdata", "tracks", "sample-tatry.tcx");

    [Fact]
    public async Task ParseAsync_ReturnsOneTrackForSingleActivity()
    {
        var parser = new TcxParser();
        await using var stream = File.OpenRead(SampleTcxPath);

        var tracks = await parser.ParseAsync(stream, fallbackName: "fallback");

        tracks.Should().HaveCount(1);
        tracks[0].Name.Should().Be("2026-03-12T07:30:00Z");
    }

    [Fact]
    public async Task ParseAsync_SkipsTrackpointsWithoutPosition()
    {
        var parser = new TcxParser();
        await using var stream = File.OpenRead(SampleTcxPath);

        var tracks = await parser.ParseAsync(stream, fallbackName: "fallback");

        // Sample TCX has 5 trackpoints, one of which lacks a Position element.
        tracks[0].Points.Should().HaveCount(4);
    }

    [Fact]
    public async Task ParseAsync_PreservesElevationAndHeartRate()
    {
        var parser = new TcxParser();
        await using var stream = File.OpenRead(SampleTcxPath);

        var tracks = await parser.ParseAsync(stream, fallbackName: "fallback");

        var first = tracks[0].Points[0];
        first.Position.ElevationMeters.Should().BeApproximately(1985.0, 0.001);
        first.HeartRateBpm.Should().Be(105);

        var second = tracks[0].Points[1];
        second.HeartRateBpm.Should().Be(120);

        var third = tracks[0].Points[2];
        third.HeartRateBpm.Should().BeNull();
    }

    [Fact]
    public async Task ParseAsync_ComputesElevationProfile()
    {
        var parser = new TcxParser();
        await using var stream = File.OpenRead(SampleTcxPath);

        var tracks = await parser.ParseAsync(stream, fallbackName: "fallback");
        var profile = tracks[0].ComputeElevationProfile();

        // Sample elevations after filtering: 1985, 2040, 2095, 2065
        profile.MinElevationMeters.Should().Be(1985.0);
        profile.MaxElevationMeters.Should().Be(2095.0);
        profile.TotalAscentMeters.Should().BeApproximately(110.0, 0.001); // 55 + 55
        profile.TotalDescentMeters.Should().BeApproximately(30.0, 0.001);
    }

    [Fact]
    public async Task ParseAsync_ThrowsOnMalformedXml()
    {
        var parser = new TcxParser();
        using var stream = new MemoryStream("not-xml"u8.ToArray());

        var act = async () => await parser.ParseAsync(stream, fallbackName: "fallback");

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ParseAsync_ThrowsOnWrongRoot()
    {
        var parser = new TcxParser();
        using var stream = new MemoryStream("<root xmlns=\"x\"/>"u8.ToArray());

        var act = async () => await parser.ParseAsync(stream, fallbackName: "fallback");

        await act.Should().ThrowAsync<InvalidDataException>();
    }
}