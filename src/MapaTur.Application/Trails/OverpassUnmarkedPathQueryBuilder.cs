using System.Globalization;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Trails;

/// <summary>
/// Buduje zapytanie Overpass o NIEZNAKOWANE ścieżki (perci): wszystkie <c>highway=path|footway|track</c>
/// w boxie, z geometrią inline, jako samodzielne ways. To celowo szeroka siatka — 2026-08-05 (Rohacze)
/// zmierzono, że zejście z Nohavicy, grań Otrhance i łącznik na Zadną Zábrať istnieją w OSM wyłącznie
/// jako ways bez <c>osmc:symbol</c> i bez relacji <c>route=hiking</c> (część nawet bez <c>sac_scale</c>,
/// więc zapytanie o trasy eksponowane też ich nie łapało). Nakładka ze szlakami znakowanymi jest
/// nieszkodliwa: te ways trafiają do osobnego magazynu i wchodzą do grafu TYLKO jako pozaszlaki
/// (kara kosztu, opt-in), więc planowanie domyślne zostaje na relacjach.
/// </summary>
public static class OverpassUnmarkedPathQueryBuilder
{
    /// <summary>Zapytanie o ścieżki/dukty w boxie, geometria inline.</summary>
    /// <param name="bounds">Box geograficzny.</param>
    /// <param name="timeoutSeconds">Serwerowy timeout zapytania. Domyślnie 120 s (box bywa całotatrzański).</param>
    public static string BuildUnmarkedPathsQuery(MapBounds bounds, int timeoutSeconds = 120)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeoutSeconds, 1);

        string south = bounds.SouthWest.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string west = bounds.SouthWest.Longitude.ToString("F6", CultureInfo.InvariantCulture);
        string north = bounds.NorthEast.Latitude.ToString("F6", CultureInfo.InvariantCulture);
        string east = bounds.NorthEast.Longitude.ToString("F6", CultureInfo.InvariantCulture);

        return $$"""
            [out:json][timeout:{{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}}];
            (
              way["highway"~"^(path|footway|track)$"]({{south}},{{west}},{{north}},{{east}});
            );
            out geom;
            """;
    }
}