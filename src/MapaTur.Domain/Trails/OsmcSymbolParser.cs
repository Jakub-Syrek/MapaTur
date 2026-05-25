namespace MapaTur.Domain.Trails;

/// <summary>
/// Parses the OSM <c>osmc:symbol</c> tag into a <see cref="TrailMarking"/>.
/// The tag follows the format <c>waycolor:background:foreground:text:textcolor</c>.
/// Only the first component (waycolor) is required for our purposes.
/// </summary>
public static class OsmcSymbolParser
{
    /// <summary>
    /// Parses the raw tag value. Returns <see cref="PttkColor.None"/> when the colour
    /// is missing, unrecognised, or the input is malformed.
    /// </summary>
    /// <param name="osmcSymbol">Raw <c>osmc:symbol</c> tag value, may be null or whitespace.</param>
    /// <returns>A trail marking with the parsed colour.</returns>
    public static TrailMarking Parse(string? osmcSymbol)
    {
        if (string.IsNullOrWhiteSpace(osmcSymbol))
        {
            return new TrailMarking(PttkColor.None);
        }

        string colourToken = osmcSymbol.Split(':', 2)[0].Trim();
        var color = colourToken.ToLowerInvariant() switch
        {
            "red" => PttkColor.Red,
            "blue" => PttkColor.Blue,
            "green" => PttkColor.Green,
            "yellow" => PttkColor.Yellow,
            "black" => PttkColor.Black,
            _ => PttkColor.None,
        };

        return new TrailMarking(color, osmcSymbol);
    }

    /// <summary>
    /// Maps a PTTK colour to its hex RGB representation used by the renderer.
    /// Hex values follow the conventional PTTK paint palette.
    /// </summary>
    /// <param name="color">PTTK colour.</param>
    /// <returns>Hex string in the form <c>#RRGGBB</c>.</returns>
    public static string ToHex(PttkColor color) => color switch
    {
        PttkColor.Red => "#DC2626",
        PttkColor.Blue => "#2563EB",
        PttkColor.Green => "#16A34A",
        PttkColor.Yellow => "#FACC15",
        PttkColor.Black => "#0F172A",
        _ => "#94A3B8",
    };
}
