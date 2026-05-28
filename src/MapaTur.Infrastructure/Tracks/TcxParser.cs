using System.Globalization;
using System.Xml.Linq;
using MapaTur.Application.Tracks;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Tracks;

namespace MapaTur.Infrastructure.Tracks;

/// <summary>
/// Parser for Garmin Training Center XML version 2. Implements <see cref="ITcxParser"/>.
/// </summary>
public sealed class TcxParser : ITcxParser
{
    private static readonly XNamespace Tcx = "http://www.garmin.com/xmlschemas/TrainingCenterDatabase/v2";

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
            throw new InvalidDataException("TCX document is not well-formed XML.", ex);
        }

        if (document.Root is null || document.Root.Name != Tcx + "TrainingCenterDatabase")
        {
            throw new InvalidDataException("Root element must be <TrainingCenterDatabase> in the TCX v2 namespace.");
        }

        var tracks = new List<Track>();
        var activities = document.Root.Element(Tcx + "Activities")?.Elements(Tcx + "Activity") ?? [];

        foreach (var activity in activities)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string name = activity.Element(Tcx + "Id")?.Value?.Trim() ?? fallbackName;
            var points = ExtractTrackpoints(activity).ToList();

            if (points.Count > 0)
            {
                tracks.Add(new Track(Guid.NewGuid(), name, points));
            }
        }

        return tracks;
    }

    private static IEnumerable<TrackPoint> ExtractTrackpoints(XElement activity)
    {
        foreach (var trackpoint in activity.Descendants(Tcx + "Trackpoint"))
        {
            var position = trackpoint.Element(Tcx + "Position");
            if (position is null)
            {
                continue;
            }

            if (!TryParseDouble(position.Element(Tcx + "LatitudeDegrees")?.Value, out double latitude)
                || !TryParseDouble(position.Element(Tcx + "LongitudeDegrees")?.Value, out double longitude))
            {
                continue;
            }

            double? elevation = TryParseDouble(trackpoint.Element(Tcx + "AltitudeMeters")?.Value, out double parsedElevation)
                ? parsedElevation
                : null;

            if (!TryParseTimestamp(trackpoint.Element(Tcx + "Time")?.Value, out DateTimeOffset timestamp))
            {
                continue;
            }

            int? heartRate = TryParseInt(
                trackpoint.Element(Tcx + "HeartRateBpm")?.Element(Tcx + "Value")?.Value,
                out int parsedHeartRate)
                ? parsedHeartRate
                : null;

            int? cadence = TryParseInt(trackpoint.Element(Tcx + "Cadence")?.Value, out int parsedCadence)
                ? parsedCadence
                : null;

            GeoPoint geoPoint;
            try
            {
                geoPoint = new GeoPoint(latitude, longitude, elevation);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            yield return new TrackPoint(geoPoint, timestamp, heartRate, cadence);
        }
    }

    private static bool TryParseDouble(string? text, out double value)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseInt(string? text, out int value)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseTimestamp(string? text, out DateTimeOffset value)
    {
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }
}