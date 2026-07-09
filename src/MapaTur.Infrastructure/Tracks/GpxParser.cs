using System.Globalization;
using System.Xml.Linq;

using MapaTur.Application.Tracks;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Tracks;

namespace MapaTur.Infrastructure.Tracks;

/// <summary>
/// Parser for the GPS Exchange Format (GPX 1.0 and 1.1). Implements <see cref="IGpxParser"/>.
///
/// Matching is done by element <em>local name</em> (namespace-agnostic) so files with the 1.0
/// namespace, the 1.1 namespace, a vendor namespace, or none at all all parse. Coordinates come
/// from the <c>lat</c>/<c>lon</c> attributes of each point; elevation and time are optional.
/// </summary>
public sealed class GpxParser : IGpxParser
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Track>> ParseAsync(Stream stream, string fallbackName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackName);

        XDocument document;
        try
        {
            document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException("GPX document is not well-formed XML.", ex);
        }

        if (document.Root is null || !string.Equals(document.Root.Name.LocalName, "gpx", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Root element must be <gpx>.");
        }

        var tracks = new List<Track>();

        // Each <trk> or <rte> becomes one track. Order is preserved as it appears in the document.
        foreach (var container in document.Root.Elements().Where(IsTrackContainer))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string name = ChildValue(container, "name")?.Trim() is { Length: > 0 } trackName ? trackName : fallbackName;
            var points = ExtractPoints(container).ToList();

            // A single point cannot form an off-trail polyline; drop it rather than fail the whole import.
            if (points.Count >= 2)
            {
                tracks.Add(new Track(Guid.NewGuid(), name, points));
            }
        }

        return tracks;
    }

    private static bool IsTrackContainer(XElement element) =>
        string.Equals(element.Name.LocalName, "trk", StringComparison.Ordinal)
        || string.Equals(element.Name.LocalName, "rte", StringComparison.Ordinal);

    private static bool IsTrackPoint(XElement element) =>
        string.Equals(element.Name.LocalName, "trkpt", StringComparison.Ordinal)
        || string.Equals(element.Name.LocalName, "rtept", StringComparison.Ordinal);

    private static IEnumerable<TrackPoint> ExtractPoints(XElement container)
    {
        // Descendants() so all <trkseg> segments under a <trk> flatten into one ordered sequence.
        foreach (var point in container.Descendants().Where(IsTrackPoint))
        {
            if (!TryParseDouble((string?)point.Attribute("lat"), out double latitude)
                || !TryParseDouble((string?)point.Attribute("lon"), out double longitude))
            {
                continue;
            }

            double? elevation = TryParseDouble(ChildValue(point, "ele"), out double parsedElevation)
                ? parsedElevation
                : null;

            // GPX planning exports frequently omit <time>; keep the point and default to the Unix epoch.
            DateTimeOffset timestamp = TryParseTimestamp(ChildValue(point, "time"), out DateTimeOffset parsedTime)
                ? parsedTime
                : DateTimeOffset.UnixEpoch;

            GeoPoint geoPoint;
            try
            {
                geoPoint = new GeoPoint(latitude, longitude, elevation);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            yield return new TrackPoint(geoPoint, timestamp);
        }
    }

    private static string? ChildValue(XElement element, string localName) =>
        element.Elements().FirstOrDefault(child => string.Equals(child.Name.LocalName, localName, StringComparison.Ordinal))?.Value;

    private static bool TryParseDouble(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool TryParseTimestamp(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
}