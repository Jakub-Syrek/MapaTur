using System.Globalization;

namespace MapaTur.Application.Routing;

/// <summary>
/// Formats a planned route's aggregate stats into the one-line summary a tourist-map planner shows
/// under the stop list: distance, total ascent and estimated walking time, e.g.
/// <c>"22.1 km · +1450 m · 6:30 h"</c>. Uses the invariant culture so the decimal separator is stable
/// across devices and the value is unit-test deterministic.
/// </summary>
public static class RouteSummaryFormatter
{
    /// <summary>
    /// Builds the summary line for a route of <paramref name="distanceMeters"/> length,
    /// <paramref name="ascentMeters"/> total climb and <paramref name="durationSeconds"/> estimated time.
    /// </summary>
    public static string Format(double distanceMeters, double ascentMeters, double durationSeconds)
    {
        string distance = distanceMeters < 1000.0
            ? string.Format(CultureInfo.InvariantCulture, "{0:F0} m", distanceMeters)
            : string.Format(CultureInfo.InvariantCulture, "{0:F1} km", distanceMeters / 1000.0);

        string ascent = string.Format(CultureInfo.InvariantCulture, "+{0:F0} m", ascentMeters);

        int totalMinutes = (int)(durationSeconds / 60.0);
        int hours = totalMinutes / 60;
        int minutes = totalMinutes % 60;
        string time = string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2} h", hours, minutes);

        return $"{distance} · {ascent} · {time}";
    }
}