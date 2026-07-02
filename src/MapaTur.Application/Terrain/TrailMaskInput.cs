using System.Globalization;

using MapaTur.Domain.Trails;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Adapts the seated/densified world projections (<see cref="TrailWorldLine"/>) into <see cref="MaskPolyline"/>
/// inputs for <see cref="TrailMaskBuilder"/>: each layer gets its colour and a priority that mirrors the on-screen
/// draw order (roads → trails → exposed), so the painted-distance winner matches the line overlay. The <c>NaN</c>
/// breaks in the world lists are handled by the builder, so they pass straight through.
/// <para>
/// The planned route is deliberately NOT painted into the surface decal: it is drawn as a translucent dashed
/// overlay (DrawRouteLine) ON TOP of the trail so the trail shows through it, rather than baked into the terrain.
/// </para>
/// </summary>
public static class TrailMaskInput
{
    /// <summary>Planned-route colour — violet, matching the 2D planner and the dashed overlay.</summary>
    public static readonly (byte R, byte G, byte B) RouteColor = (0x7C, 0x3A, 0xED);

    /// <summary>Road colour — light grey.</summary>
    public static readonly (byte R, byte G, byte B) RoadColor = (0xE5, 0xE7, 0xEB);

    /// <summary>Exposed-route colour — orange (sac_scale / via_ferrata).</summary>
    public static readonly (byte R, byte G, byte B) ExposedColor = (0xFF, 0x8C, 0x00);

    /// <summary>Watercourse colour — mountain-stream blue (the shader adds the wet glint on top).</summary>
    public static readonly (byte R, byte G, byte B) WaterColor = (0x4F, 0x9E, 0xD9);

    /// <summary>Waterfall foam accent — near-white with a cold cast.</summary>
    public static readonly (byte R, byte G, byte B) FoamColor = (0xEA, 0xF6, 0xFF);

    /// <summary>Watercourses sit under every man-made line (a trail crossing a stream wins the crossing texel).</summary>
    public const int WaterPriority = -1;

    /// <summary>Roads sit under trails.</summary>
    public const int RoadPriority = 0;

    /// <summary>Trails sit over roads.</summary>
    public const int TrailPriority = 1;

    /// <summary>Exposed routes punctuate the trails they run along.</summary>
    public const int ExposedPriority = 2;

    /// <summary>Waterfall foam accents win even over exposed routes — a tiny, high-salience splash.</summary>
    public const int FoamPriority = 3;

    /// <summary>
    /// Builds the ordered polyline set to rasterise. Any argument may be null/empty; layers are appended
    /// low-priority-first so a stable enumeration also reflects draw order. The route is excluded by design
    /// (it is the translucent dashed overlay on top, not part of the surface decal).
    /// </summary>
    public static IReadOnlyList<MaskPolyline> Build(
        IReadOnlyList<TrailWorldLine>? trails = null,
        IReadOnlyList<TrailWorldLine>? roads = null,
        IReadOnlyList<TrailWorldLine>? exposed = null,
        IReadOnlyList<TrailWorldLine>? waterways = null)
    {
        var lines = new List<MaskPolyline>();

        if (waterways is not null)
        {
            foreach (var water in waterways)
            {
                lines.Add(new MaskPolyline(water.World, WaterColor.R, WaterColor.G, WaterColor.B, WaterPriority));
            }
        }

        if (roads is not null)
        {
            foreach (var road in roads)
            {
                lines.Add(new MaskPolyline(road.World, RoadColor.R, RoadColor.G, RoadColor.B, RoadPriority));
            }
        }

        if (trails is not null)
        {
            foreach (var trail in trails)
            {
                var (r, g, b) = TrailColor(trail.Source.PrimaryColor);
                lines.Add(new MaskPolyline(trail.World, r, g, b, TrailPriority));
            }
        }

        if (exposed is not null)
        {
            foreach (var line in exposed)
            {
                lines.Add(new MaskPolyline(line.World, ExposedColor.R, ExposedColor.G, ExposedColor.B, ExposedPriority));
            }
        }

        return lines;
    }

    /// <summary>
    /// Maps a PTTK colour to RGB via the shared <see cref="OsmcSymbolParser.ToHex"/> palette (so the decal
    /// matches the line overlay and the 2D renderer). Falls back to slate for unknown colours.
    /// </summary>
    public static (byte R, byte G, byte B) TrailColor(PttkColor color)
    {
        var hex = OsmcSymbolParser.ToHex(color);
        var start = hex.StartsWith('#') ? 1 : 0;
        if (hex.Length - start >= 6
            && byte.TryParse(hex.AsSpan(start, 2), NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.AsSpan(start + 2, 2), NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.AsSpan(start + 4, 2), NumberStyles.HexNumber, null, out var b))
        {
            return (r, g, b);
        }

        return (0x94, 0xA3, 0xB8);
    }
}