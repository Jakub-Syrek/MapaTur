using System.Text;

using FluentAssertions;

using MapaTur.Infrastructure.Tracks;

namespace MapaTur.Infrastructure.Tests.Tracks;

public sealed class GpxParserTests
{
    private static readonly string SampleGpxPath = Path.Combine(AppContext.BaseDirectory, "testdata", "tracks", "sample-tatry.gpx");

    [Fact]
    public async Task ParseAsync_ReturnsOneTrackPerTrkAndRte()
    {
        var parser = new GpxParser();
        await using var stream = File.OpenRead(SampleGpxPath);

        var tracks = await parser.ParseAsync(stream, fallbackName: "fallback");

        // One <trk> and one <rte> in the fixture.
        tracks.Should().HaveCount(2);
        tracks[0].Name.Should().Be("Grań pozaszlak");
        tracks[1].Name.Should().Be("Podejscie rte");
    }

    [Fact]
    public async Task ParseAsync_ConcatenatesAllSegmentsIntoOneTrack()
    {
        var parser = new GpxParser();
        await using var stream = File.OpenRead(SampleGpxPath);

        var tracks = await parser.ParseAsync(stream, fallbackName: "fallback");

        // 3 points in the first <trkseg> + 1 in the second = 4 (INCLUDING the one without <time>).
        tracks[0].Points.Should().HaveCount(4);
    }

    [Fact]
    public async Task ParseAsync_ParsesLatLonFromAttributes()
    {
        var parser = new GpxParser();
        await using var stream = File.OpenRead(SampleGpxPath);

        var tracks = await parser.ParseAsync(stream, fallbackName: "fallback");

        var first = tracks[0].Points[0];
        first.Position.Latitude.Should().BeApproximately(49.2326, 1e-6);
        first.Position.Longitude.Should().BeApproximately(19.9819, 1e-6);
        first.Position.ElevationMeters.Should().BeApproximately(1985.0, 0.001);
    }

    [Fact]
    public async Task ParseAsync_KeepsTimelessPoint_DefaultingTimestampToEpoch()
    {
        var parser = new GpxParser();
        await using var stream = File.OpenRead(SampleGpxPath);

        var tracks = await parser.ParseAsync(stream, fallbackName: "fallback");

        // Third point of the first track has no <time>; it must be kept (GPX planning exports omit time).
        var timeless = tracks[0].Points[2];
        timeless.Timestamp.Should().Be(DateTimeOffset.UnixEpoch);
        timeless.Position.ElevationMeters.Should().BeApproximately(2095.0, 0.001);
    }

    [Fact]
    public async Task ParseAsync_LeavesElevationNullWhenMissing()
    {
        var parser = new GpxParser();
        await using var stream = File.OpenRead(SampleGpxPath);

        var tracks = await parser.ParseAsync(stream, fallbackName: "fallback");

        // Fourth point (second segment) has no <ele>.
        tracks[0].Points[3].Position.ElevationMeters.Should().BeNull();
    }

    [Fact]
    public async Task ParseAsync_FallsBackToNameWhenTrackUnnamed()
    {
        const string gpx = """
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
              <trk>
                <trkseg>
                  <trkpt lat="49.10" lon="19.90"/>
                  <trkpt lat="49.11" lon="19.91"/>
                </trkseg>
              </trk>
            </gpx>
            """;
        var parser = new GpxParser();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(gpx));

        var tracks = await parser.ParseAsync(stream, fallbackName: "my-file");

        tracks.Should().HaveCount(1);
        tracks[0].Name.Should().Be("my-file");
    }

    [Fact]
    public async Task ParseAsync_IsNamespaceAgnostic_ParsesGpx10AndNoNamespace()
    {
        // GPX 1.0 namespace + a plain no-namespace variant must both parse (real-world files vary).
        const string gpx10 = """
            <gpx version="1.0" xmlns="http://www.topografix.com/GPX/1/0">
              <trk><name>ten</name><trkseg>
                <trkpt lat="49.10" lon="19.90"><ele>1000</ele></trkpt>
                <trkpt lat="49.12" lon="19.92"><ele>1100</ele></trkpt>
              </trkseg></trk>
            </gpx>
            """;
        var parser = new GpxParser();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(gpx10));

        var tracks = await parser.ParseAsync(stream, fallbackName: "fallback");

        tracks.Should().HaveCount(1);
        tracks[0].Name.Should().Be("ten");
        tracks[0].Points.Should().HaveCount(2);
    }

    [Fact]
    public async Task ParseAsync_ThrowsOnMalformedXml()
    {
        var parser = new GpxParser();
        using var stream = new MemoryStream("not-xml"u8.ToArray());

        var act = async () => await parser.ParseAsync(stream, fallbackName: "fallback");

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ParseAsync_ThrowsOnWrongRoot()
    {
        var parser = new GpxParser();
        using var stream = new MemoryStream("<kml xmlns=\"x\"/>"u8.ToArray());

        var act = async () => await parser.ParseAsync(stream, fallbackName: "fallback");

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ParseAsync_SkipsTrackWithFewerThanTwoPoints()
    {
        // A <trk> with a single valid point cannot form a polyline; the parser drops it
        // rather than throwing (Track requires >= 1 point but a 1-point track is useless off-trail).
        const string gpx = """
            <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
              <trk><name>lonely</name><trkseg>
                <trkpt lat="49.10" lon="19.90"/>
              </trkseg></trk>
              <trk><name>good</name><trkseg>
                <trkpt lat="49.10" lon="19.90"/>
                <trkpt lat="49.11" lon="19.91"/>
              </trkseg></trk>
            </gpx>
            """;
        var parser = new GpxParser();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(gpx));

        var tracks = await parser.ParseAsync(stream, fallbackName: "fallback");

        tracks.Should().HaveCount(1);
        tracks[0].Name.Should().Be("good");
    }
}