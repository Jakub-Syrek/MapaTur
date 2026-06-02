using System.Globalization;

using MapaTur.Domain.Climbing;
using MapaTur.Domain.Pois;

namespace MapaTur.Application.Markers;

/// <summary>
/// Turns a tapped <see cref="MountainPoi"/> or <see cref="ClimbingArea"/> into displayable popup content.
/// Pure formatting: which lines appear, how numbers render, and the name/fallback title — all the
/// presentation decisions worth testing — live here; localized strings come in via
/// <see cref="IMarkerPopupLabels"/>.
/// </summary>
public static class MarkerPopupFormatter
{
    /// <summary>Builds popup content for a mountain POI.</summary>
    public static MarkerPopupContent ForPoi(MountainPoi poi, IMarkerPopupLabels labels)
    {
        ArgumentNullException.ThrowIfNull(poi);
        ArgumentNullException.ThrowIfNull(labels);

        string title = string.IsNullOrWhiteSpace(poi.Name) ? labels.UnnamedPoi : poi.Name;

        var lines = new List<MarkerPopupLine>
        {
            new(labels.CategoryLabel, labels.PoiKindName(poi.Kind)),
        };

        if (poi.ElevationMeters is { } elevation)
        {
            lines.Add(new MarkerPopupLine(labels.ElevationLabel, FormatMetres(elevation)));
        }

        return new MarkerPopupContent(title, lines);
    }

    /// <summary>Builds popup content for a climbing area.</summary>
    public static MarkerPopupContent ForClimbing(ClimbingArea area, IMarkerPopupLabels labels)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(labels);

        string title = string.IsNullOrWhiteSpace(area.Name) ? labels.UnnamedClimbing : area.Name;

        var lines = new List<MarkerPopupLine>
        {
            new(labels.CategoryLabel, labels.ClimbingTypeName(area.Type)),
        };

        if (!string.IsNullOrWhiteSpace(area.Grade))
        {
            lines.Add(new MarkerPopupLine(labels.GradeLabel, area.Grade.Trim()));
        }

        if (area.LengthMeters is { } length)
        {
            lines.Add(new MarkerPopupLine(labels.LengthLabel, FormatMetres(length)));
        }

        if (area.IsBolted is { } bolted)
        {
            lines.Add(new MarkerPopupLine(labels.ProtectionLabel, bolted ? labels.Bolted : labels.Trad));
        }

        return new MarkerPopupContent(title, lines);
    }

    private static string FormatMetres(double metres)
    {
        int rounded = (int)Math.Round(metres, MidpointRounding.AwayFromZero);
        return rounded.ToString(CultureInfo.InvariantCulture) + " m";
    }
}