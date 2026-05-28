using System.Xml;
using System.Xml.Linq;

using FluentAssertions;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;
using MapaTur.Infrastructure.Routing;

namespace MapaTur.Infrastructure.Tests.Routing;

public sealed class GpxWriterTests
{
    private static readonly XNamespace Gpx = "http://www.topografix.com/GPX/1/1";

    [Fact]
    public async Task WriteAsync_ProducesValidGpx11Document()
    {
        var route = BuildSampleRoute();
        var writer = new GpxWriter();
        using var memory = new MemoryStream();

        await writer.WriteAsync(route, memory, "Test track");

        memory.Position = 0;
        var doc = XDocument.Load(memory);

        doc.Root.Should().NotBeNull();
        doc.Root!.Name.Should().Be(Gpx + "gpx");
        doc.Root.Attribute("version")?.Value.Should().Be("1.1");
        doc.Root.Attribute("creator")?.Value.Should().Be("MapaTur");
    }

    [Fact]
    public async Task WriteAsync_EmitsOneTrkptPerPolylinePoint()
    {
        var route = BuildSampleRoute();
        var writer = new GpxWriter();
        using var memory = new MemoryStream();

        await writer.WriteAsync(route, memory, "Test track");

        memory.Position = 0;
        var doc = XDocument.Load(memory);
        var trackPoints = doc.Descendants(Gpx + "trkpt").ToList();

        trackPoints.Should().HaveCount(route.ToPolyline().Count);
    }

    [Fact]
    public async Task WriteAsync_SerialisesCoordinatesInInvariantCulture()
    {
        var route = BuildSampleRoute();
        var writer = new GpxWriter();
        using var memory = new MemoryStream();

        await writer.WriteAsync(route, memory, "Test track");

        memory.Position = 0;
        var doc = XDocument.Load(memory);
        var firstPoint = doc.Descendants(Gpx + "trkpt").First();

        firstPoint.Attribute("lat")!.Value.Should().Be("49.2326000");
        firstPoint.Attribute("lon")!.Value.Should().Be("19.9819000");
        firstPoint.Element(Gpx + "ele")!.Value.Should().Be("1000.00");
    }

    [Fact]
    public async Task WriteAsync_OmitsElevationElementWhenAbsent()
    {
        var segments = new List<RouteSegment>
        {
            new(
                From: new GeoPoint(49.0, 19.0),
                To: new GeoPoint(49.001, 19.001),
                DistanceMeters: 130,
                AscentMeters: 0,
                DescentMeters: 0,
                DurationSeconds: 60),
        };
        var route = new Route(segments);
        var writer = new GpxWriter();
        using var memory = new MemoryStream();

        await writer.WriteAsync(route, memory, "Elevationless");

        memory.Position = 0;
        var doc = XDocument.Load(memory);
        doc.Descendants(Gpx + "ele").Should().BeEmpty();
    }

    private static Route BuildSampleRoute()
    {
        var segments = new List<RouteSegment>
        {
            new(
                From: new GeoPoint(49.2326, 19.9819, elevationMeters: 1000),
                To: new GeoPoint(49.2310, 19.9850, elevationMeters: 1050),
                DistanceMeters: 287,
                AscentMeters: 50,
                DescentMeters: 0,
                DurationSeconds: 250),
            new(
                From: new GeoPoint(49.2310, 19.9850, elevationMeters: 1050),
                To: new GeoPoint(49.2290, 19.9880, elevationMeters: 1100),
                DistanceMeters: 311,
                AscentMeters: 50,
                DescentMeters: 0,
                DurationSeconds: 280),
        };
        return new Route(segments);
    }
}