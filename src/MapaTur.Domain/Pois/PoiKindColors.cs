namespace MapaTur.Domain.Pois;

/// <summary>
/// Canonical marker colours per <see cref="PoiKind"/>, exposed as 24-bit RGB hex strings
/// (e.g. <c>"DC2626"</c>) so the 2D map and 3D view render POIs consistently. Mirrors
/// <see cref="MapaTur.Domain.Climbing.ClimbingTypeColors"/>.
/// </summary>
/// <remarks>
/// Palette chosen for distinguishability over a hillshade backdrop and against the climbing
/// palette so the two overlays don't blur together:
/// <list type="bullet">
///   <item><description>Hut — red (#DC2626): the prominent staffed refuge.</description></item>
///   <item><description>Wilderness hut — orange (#EA580C).</description></item>
///   <item><description>Chalet — amber/brown (#B45309).</description></item>
///   <item><description>Shelter — teal (#0D9488).</description></item>
///   <item><description>Viewpoint — blue (#2563EB).</description></item>
/// </list>
/// </remarks>
public static class PoiKindColors
{
    /// <summary>Returns the canonical 6-char RGB hex (no <c>#</c>) for the kind.</summary>
    public static string ToHex(PoiKind kind) => kind switch
    {
        PoiKind.Hut => "DC2626",
        PoiKind.WildernessHut => "EA580C",
        PoiKind.Chalet => "B45309",
        PoiKind.Shelter => "0D9488",
        PoiKind.Viewpoint => "2563EB",
        _ => "DC2626",
    };
}