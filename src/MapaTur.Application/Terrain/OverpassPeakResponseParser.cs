using System.Globalization;
using System.Text.Json;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Parses an Overpass <c>[out:json]</c> response of <c>natural=peak</c> elements into <see cref="OsmPeak"/>s.
/// Reads node positions directly and way/relation positions from <c>center</c>; prefers the Polish name
/// (<c>name:pl</c>) over the default <c>name</c>; de-duplicates by element id. Pure and deterministic.
/// </summary>
public static class OverpassPeakResponseParser
{
    public static IReadOnlyList<OsmPeak> Parse(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Overpass peak response is not valid JSON.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("elements", out JsonElement elements)
                || elements.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Overpass peak response is missing the 'elements' array.");
            }

            var seenIds = new HashSet<long>();
            var peaks = new List<OsmPeak>();
            foreach (JsonElement element in elements.EnumerateArray())
            {
                if (TryBuildPeak(element, out OsmPeak peak) && seenIds.Add(peak.Id))
                {
                    peaks.Add(peak);
                }
            }

            return peaks;
        }
    }

    private static bool TryBuildPeak(JsonElement element, out OsmPeak peak)
    {
        peak = default;

        if (!element.TryGetProperty("id", out JsonElement idElement) || idElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (!element.TryGetProperty("tags", out JsonElement tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryReadPosition(element, out GeoPoint position))
        {
            return false;
        }

        string name = StringTag(tags, "name:pl") ?? StringTag(tags, "name") ?? string.Empty;
        double? elevation = DoubleTag(tags, "ele");

        peak = new OsmPeak(idElement.GetInt64(), name, position, elevation);
        return true;
    }

    private static bool TryReadPosition(JsonElement element, out GeoPoint position)
    {
        if (TryReadLatLon(element, out position))
        {
            return true;
        }

        if (element.TryGetProperty("center", out JsonElement center) && center.ValueKind == JsonValueKind.Object)
        {
            return TryReadLatLon(center, out position);
        }

        position = default;
        return false;
    }

    private static bool TryReadLatLon(JsonElement element, out GeoPoint position)
    {
        if (element.TryGetProperty("lat", out JsonElement latEl) && latEl.ValueKind == JsonValueKind.Number
            && element.TryGetProperty("lon", out JsonElement lonEl) && lonEl.ValueKind == JsonValueKind.Number)
        {
            try
            {
                position = new GeoPoint(latEl.GetDouble(), lonEl.GetDouble());
                return true;
            }
            catch (ArgumentException)
            {
                // Out-of-range coordinate — skip this element.
            }
        }

        position = default;
        return false;
    }

    private static string? StringTag(JsonElement tags, string key)
    {
        if (tags.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    private static double? DoubleTag(JsonElement tags, string key)
    {
        string? raw = StringTag(tags, key);
        if (raw is null)
        {
            return null;
        }

        // Parse the leading numeric run so values such as "1894 m" still read as 1894.
        int end = 0;
        while (end < raw.Length && (char.IsDigit(raw[end]) || raw[end] is '.' or '-' or '+'))
        {
            end++;
        }

        return double.TryParse(raw[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }
}