using System.Globalization;

namespace MapaTur.Application.Terrain;

/// <summary>
/// One catalog star: equatorial position (J2000), apparent visual magnitude (lower = brighter), and an
/// optional proper name + constellation for labelling. Faint field stars carry no name.
/// </summary>
public readonly record struct Star(double RaHours, double DecDegrees, double Magnitude, string? Name, string? Constellation)
{
    /// <summary>True when the star has a proper name worth labelling.</summary>
    public bool HasName => !string.IsNullOrEmpty(Name);
}

/// <summary>
/// Parses the bundled bright-star catalog (CSV: <c>raHours, decDegrees, magnitude, name?, constellation?</c>).
/// Lenient on purpose — comment lines (<c>#</c>), blanks and malformed rows are skipped — so a hand-edited or
/// script-generated catalog can never crash the night-sky renderer.
/// </summary>
public static class StarCatalog
{
    /// <summary>Parses catalog CSV text into stars, skipping comments, blank lines and malformed rows.</summary>
    public static IReadOnlyList<Star> Parse(string csv)
    {
        var stars = new List<Star>();
        if (string.IsNullOrEmpty(csv))
        {
            return stars;
        }

        foreach (string rawLine in csv.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] f = line.Split(',');
            if (f.Length < 3
                || !double.TryParse(f[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double ra)
                || !double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double dec)
                || !double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double mag))
            {
                continue;
            }

            string? name = f.Length > 3 && f[3].Trim().Length > 0 ? f[3].Trim() : null;
            string? constellation = f.Length > 4 && f[4].Trim().Length > 0 ? f[4].Trim() : null;
            stars.Add(new Star(ra, dec, mag, name, constellation));
        }

        return stars;
    }
}