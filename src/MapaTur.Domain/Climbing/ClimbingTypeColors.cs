namespace MapaTur.Domain.Climbing;

/// <summary>
/// Canonical marker colours per <see cref="ClimbingType"/>, exposed as 24-bit RGB hex
/// strings (e.g. <c>"E11D48"</c>) so 2D and 3D renderers stay visually consistent.
/// </summary>
/// <remarks>
/// Palette chosen for max distinguishability under a hillshade backdrop:
/// <list type="bullet">
///   <item><description>Sport — red (#E11D48), our project default.</description></item>
///   <item><description>Trad — blue (#2563EB).</description></item>
///   <item><description>Multi-pitch — purple (#9333EA).</description></item>
///   <item><description>Boulder — orange (#F97316).</description></item>
///   <item><description>Crag — slate (#475569).</description></item>
///   <item><description>Cliff — neutral grey (#6B7280).</description></item>
///   <item><description>Unspecified — same as sport (fallback red).</description></item>
/// </list>
/// </remarks>
public static class ClimbingTypeColors
{
    /// <summary>Returns the canonical 6-char RGB hex (no <c>#</c>) for the type.</summary>
    public static string ToHex(ClimbingType type) => type switch
    {
        ClimbingType.SportRoute => "E11D48",
        ClimbingType.TradRoute => "2563EB",
        ClimbingType.MultiPitch => "9333EA",
        ClimbingType.Boulder => "F97316",
        ClimbingType.Crag => "475569",
        ClimbingType.Cliff => "6B7280",
        _ => "E11D48",
    };
}