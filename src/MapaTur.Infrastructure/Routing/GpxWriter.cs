using System.Globalization;
using System.Xml;
using MapaTur.Application.Routing;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;

namespace MapaTur.Infrastructure.Routing;

/// <summary>
/// Writes planned routes as GPX 1.1 documents. The route polyline is emitted as a single
/// <c>&lt;trk&gt;</c> element with one <c>&lt;trkseg&gt;</c> child, matching how Garmin and
/// most other consumers expect planned routes recorded after-the-fact.
/// </summary>
public sealed class GpxWriter : IGpxWriter
{
    private const string GpxNamespace = "http://www.topografix.com/GPX/1/1";
    private const string Creator = "MapaTur";

    /// <inheritdoc />
    public async Task WriteAsync(Route route, Stream output, string trackName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackName);

        var settings = new XmlWriterSettings
        {
            Async = true,
            Indent = true,
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };

        await using var writer = XmlWriter.Create(output, settings);

        await writer.WriteStartDocumentAsync().ConfigureAwait(false);
        await writer.WriteStartElementAsync(prefix: null, "gpx", GpxNamespace).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(prefix: null, "version", null, "1.1").ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(prefix: null, "creator", null, Creator).ConfigureAwait(false);

        await WriteMetadataAsync(writer, trackName).ConfigureAwait(false);
        await WriteTrackAsync(writer, route, trackName, cancellationToken).ConfigureAwait(false);

        await writer.WriteEndElementAsync().ConfigureAwait(false);
        await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private static async Task WriteMetadataAsync(XmlWriter writer, string trackName)
    {
        await writer.WriteStartElementAsync(prefix: null, "metadata", GpxNamespace).ConfigureAwait(false);
        await writer.WriteElementStringAsync(prefix: null, "name", GpxNamespace, trackName).ConfigureAwait(false);
        await writer.WriteElementStringAsync(prefix: null, "time", GpxNamespace,
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static async Task WriteTrackAsync(XmlWriter writer, Route route, string trackName, CancellationToken cancellationToken)
    {
        await writer.WriteStartElementAsync(prefix: null, "trk", GpxNamespace).ConfigureAwait(false);
        await writer.WriteElementStringAsync(prefix: null, "name", GpxNamespace, trackName).ConfigureAwait(false);
        await writer.WriteStartElementAsync(prefix: null, "trkseg", GpxNamespace).ConfigureAwait(false);

        foreach (var point in route.ToPolyline())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteTrackPointAsync(writer, point).ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false); // trkseg
        await writer.WriteEndElementAsync().ConfigureAwait(false); // trk
    }

    private static async Task WriteTrackPointAsync(XmlWriter writer, GeoPoint point)
    {
        await writer.WriteStartElementAsync(prefix: null, "trkpt", GpxNamespace).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(prefix: null, "lat", null,
            point.Latitude.ToString("F7", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(prefix: null, "lon", null,
            point.Longitude.ToString("F7", CultureInfo.InvariantCulture)).ConfigureAwait(false);

        if (point.ElevationMeters is { } elevation)
        {
            await writer.WriteElementStringAsync(prefix: null, "ele", GpxNamespace,
                elevation.ToString("F2", CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }
}
