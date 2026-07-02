using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A single polyline to rasterise into the trail mask. Points are in mesh world space (X east, Y north;
/// Z is ignored). A point with any NaN component breaks the polyline, so the two segments either side of it
/// are not joined — this matches the <c>NaN</c> breaks emitted by
/// <see cref="Trail3DWorldProjection"/>/<see cref="Route3DWorldProjection"/> for trails clipped out of the DEM.
/// </summary>
/// <param name="Points">World-space vertices (metres); Z ignored, NaN splits the line.</param>
/// <param name="R">Line colour red channel.</param>
/// <param name="G">Line colour green channel.</param>
/// <param name="B">Line colour blue channel.</param>
/// <param name="Priority">Higher priority wins the colour where lines overlap (e.g. route over trail).</param>
public readonly record struct MaskPolyline(
    IReadOnlyList<Vector3> Points,
    byte R,
    byte G,
    byte B,
    int Priority);

/// <summary>
/// The planned route, painted INTO the mask as a DASHED, SEMI-TRANSPARENT highlight ON the trail it was conflated
/// onto. After the trail/road/exposed distance field is rasterised, a route pass walks this polyline by cumulative
/// arc length: along <see cref="DashMeters"/> stretches it blends the covered texels' RGB toward <see cref="R"/>/
/// <see cref="G"/>/<see cref="B"/> by <see cref="BlendStrength"/> (so the underlying trail shows through ⇒ translucent),
/// and where the route runs OFF any trail it also writes the distance field so the dashed route is still visible; the
/// <see cref="GapMeters"/> stretches are left as the trail colour ⇒ the line reads as dashed. The shader narrows the
/// same distance field to a thin crisp thread, so the recoloured route inherits the trail decal's crispness.
/// </summary>
/// <param name="Points">World-space route vertices (metres); Z ignored, NaN splits the line.</param>
/// <param name="R">Route tint red channel (violet).</param>
/// <param name="G">Route tint green channel.</param>
/// <param name="B">Route tint blue channel.</param>
/// <param name="DashMeters">Length (world m) of each painted dash along the route.</param>
/// <param name="GapMeters">Length (world m) of each gap between dashes.</param>
/// <param name="BlendStrength">0..1 fraction the covered texels' RGB are mixed toward the route tint (≈0.6 ⇒ translucent).</param>
/// <param name="PaintRadiusMeters">Lateral/longitudinal reach (world m) the route pass recolours around each dash —
/// kept TIGHT (≈ the drawn line half-width + ~1 texel) so the dashes don't bleed across the gaps. Should be well
/// under <see cref="GapMeters"/>/2 or the gaps close up. The off-trail distance written still ramps over the
/// request's full <c>MaxDistanceMeters</c>, so the shader's thin-line reconstruction is unchanged.</param>
public readonly record struct MaskRoute(
    IReadOnlyList<Vector3> Points,
    byte R,
    byte G,
    byte B,
    float DashMeters,
    float GapMeters,
    float BlendStrength,
    float PaintRadiusMeters);

/// <summary>
/// Request for <see cref="TrailMaskBuilder.Build(TrailMaskRequest)"/>: the world-XY window the mask covers, its texel
/// resolution, the distance-field band width (in world metres) and the polylines to paint.
/// </summary>
public sealed record TrailMaskRequest
{
    /// <summary>World X (east, metres) of the window's min corner = texture u=0.</summary>
    public required float WorldMinX { get; init; }

    /// <summary>World Y (north, metres) of the window's min corner = texture v=0.</summary>
    public required float WorldMinY { get; init; }

    /// <summary>Window width in world metres.</summary>
    public required float WorldSizeX { get; init; }

    /// <summary>Window height in world metres.</summary>
    public required float WorldSizeY { get; init; }

    /// <summary>Texture width in texels.</summary>
    public required int Width { get; init; }

    /// <summary>Texture height in texels.</summary>
    public required int Height { get; init; }

    /// <summary>
    /// Max distance (world metres) the distance field stores: A=255 on a line centre, ramping DOWN to A=0 at this
    /// distance and beyond. The field is written for EVERY texel within this distance of a line, so the band is
    /// CONTINUOUS between texels (no gaps/dots) provided it spans ≥ ~4 texels. The shader reconstructs the metric
    /// distance (<c>(1 - A) * MaxDistanceMeters</c>) and narrows it analytically to a thin crisp line — so even a
    /// coarse texture yields a clean continuous thread at any zoom.
    /// </summary>
    public required float MaxDistanceMeters { get; init; }

    /// <summary>Polylines to paint, in world space.</summary>
    public required IReadOnlyList<MaskPolyline> Lines { get; init; }

    /// <summary>
    /// Watercourse polylines painted into the PARALLEL single-channel water distance field
    /// (<see cref="TrailMask.Water"/>) that drives the shader's wet tint + specular glint. These lines should
    /// ALSO be included in <see cref="Lines"/> (with their water colour) so the RGBA decal draws them; the
    /// separate field exists because a trail crossing a stream takes the crossing texel's COLOUR but the
    /// texel must still read as water for the glint. Empty = no water field is produced.
    /// </summary>
    public IReadOnlyList<MaskPolyline> WaterLines { get; init; } = Array.Empty<MaskPolyline>();

    /// <summary>
    /// Optional planned route, painted as a dashed translucent highlight ON the trail AFTER <see cref="Lines"/> are
    /// rasterised. Null = no route in the decal. The route is expected to be conflated onto a trail so it shares the
    /// trail's geometry; the pass recolours the trail's distance-field texels along the dashes and writes the field
    /// where the route runs off-trail.
    /// </summary>
    public MaskRoute? Route { get; init; }
}

/// <summary>
/// An RGBA8 DISTANCE-FIELD texture covering a world-XY window: RGB is the colour of the nearest painted line, and
/// A encodes the (unsigned) distance to that line — 255 on the line centre, ramping linearly DOWN to 0 at
/// <see cref="TrailMaskRequest.MaxDistanceMeters"/> and beyond. Because every texel within that distance is written,
/// the field is CONTINUOUS (no gaps/dots) and bilinear-filters cleanly; outside the band the texture reads 0, i.e.
/// "no line". A terrain shader samples it by fragment world-XY, reconstructs the metric distance and narrows it
/// analytically (fwidth AA) to a thin crisp line on BOTH the coarse base and the streamed detail — never floating,
/// never occluded. Row 0 is the min-Y (south) row, so the texture maps directly to <c>uv = (worldXY - min) / size</c>.
/// </summary>
public sealed class TrailMask
{
    public TrailMask(
        int width, int height, float worldMinX, float worldMinY, float worldSizeX, float worldSizeY, byte[] rgba,
        byte[]? water = null)
    {
        Width = width;
        Height = height;
        WorldMinX = worldMinX;
        WorldMinY = worldMinY;
        WorldSizeX = worldSizeX;
        WorldSizeY = worldSizeY;
        Rgba = rgba;
        Water = water;
    }

    /// <summary>
    /// Optional single-channel water distance field, <c>Width * Height</c> bytes, same window/encoding as
    /// <see cref="Rgba"/>'s alpha (255 on a watercourse centre → 0 at the max distance). Null when the build
    /// had no water lines. Drives the shader's wet tint + glint independently of the RGBA colour winner.
    /// </summary>
    public byte[]? Water { get; }

    public int Width { get; }
    public int Height { get; }
    public float WorldMinX { get; }
    public float WorldMinY { get; }
    public float WorldSizeX { get; }
    public float WorldSizeY { get; }

    /// <summary>Row-major RGBA8 pixels, <c>Width * Height * 4</c> bytes. Row 0 = min Y.</summary>
    public byte[] Rgba { get; }

    /// <summary>Reads the texel at column <paramref name="x"/>, row <paramref name="y"/> (row 0 = min Y).</summary>
    public (byte R, byte G, byte B, byte A) PixelAt(int x, int y)
    {
        var i = ((y * Width) + x) * 4;
        return (Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]);
    }
}

/// <summary>
/// Rasterises trail/road/exposed polylines into a <see cref="TrailMask"/> distance field. Pure CPU, deterministic:
/// for each texel within <see cref="TrailMaskRequest.MaxDistanceMeters"/> of a line it records the nearest line
/// (smaller distance wins; equal distances break to higher priority — i.e. where lines coincide), storing that
/// line's colour in RGB and the normalised distance in A (255 on the centre → 0 at the max distance). Build on
/// trail/window change, not per frame.
/// </summary>
public static class TrailMaskBuilder
{
    // Distances within this many metres count as "coincident" → the higher-priority line wins the colour there
    // (e.g. an exposed route's orange over a trail it runs exactly along), without disturbing the nearest-wins
    // distance field everywhere else.
    private const float CoincidentEpsilonMeters = 0.5f;

    /// <summary>
    /// Allocates the output + scratch buffers and rasterises. Convenient for tests and one-off builds; the
    /// renderer uses the scratch-buffer overload to avoid the ~48 MB allocation on every (now rare) rebuild.
    /// </summary>
    public static TrailMask Build(TrailMaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var texels = request.Width * request.Height;
        var rgba = new byte[texels * 4];
        var bestPriority = new int[texels];
        var bestDistance = new float[texels];
        return Build(request, rgba, bestPriority, bestDistance);
    }

    /// <summary>
    /// Rasterises into caller-provided scratch buffers — no per-call allocation. The buffers must be at least
    /// <c>Width * Height * 4</c> (rgba) and <c>Width * Height</c> (bestPriority/bestDistance); the renderer
    /// holds them as fields and only reallocates when the texture dimensions change. The buffers are reset
    /// internally, so stale contents from a previous build don't leak through. The returned <see cref="TrailMask"/>
    /// wraps the SAME <paramref name="rgba"/> array (no copy) — read/upload it before the next Build overwrites it.
    /// </summary>
    public static TrailMask Build(TrailMaskRequest request, byte[] rgba, int[] bestPriority, float[] bestDistance)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentNullException.ThrowIfNull(bestPriority);
        ArgumentNullException.ThrowIfNull(bestDistance);

        var width = request.Width;
        var height = request.Height;
        var texels = width * height;
        if (rgba.Length < texels * 4)
        {
            throw new ArgumentException($"rgba buffer too small: need {texels * 4}, got {rgba.Length}.", nameof(rgba));
        }

        if (bestPriority.Length < texels || bestDistance.Length < texels)
        {
            throw new ArgumentException("priority/distance buffers too small for the requested dimensions.", nameof(bestPriority));
        }

        // Reset the region we use (buffers may be reused across builds / oversized). Each texel starts "far" (no
        // line); the painter then records the nearest line's distance + colour. A=0 everywhere = no line.
        Array.Clear(rgba, 0, texels * 4);
        Array.Fill(bestDistance, float.PositiveInfinity, 0, texels);
        Array.Fill(bestPriority, int.MinValue, 0, texels);

        var reach = MathF.Max(request.MaxDistanceMeters, 0f);

        var metersPerTexelX = request.WorldSizeX / width;
        var metersPerTexelY = request.WorldSizeY / height;

        foreach (var line in request.Lines)
        {
            var pts = line.Points;
            if (pts is null)
            {
                continue;
            }

            for (var s = 0; s + 1 < pts.Count; s++)
            {
                var a = pts[s];
                var b = pts[s + 1];
                if (!IsFinite(a) || !IsFinite(b))
                {
                    continue; // NaN break — do not bridge the gap.
                }

                PaintSegment(
                    new Vector2(a.X, a.Y),
                    new Vector2(b.X, b.Y),
                    line,
                    request,
                    reach,
                    metersPerTexelX,
                    metersPerTexelY,
                    rgba,
                    bestPriority,
                    bestDistance);
            }
        }

        // Route pass: AFTER the trails are rasterised, lay the dashed translucent route highlight onto the trail it
        // was conflated onto (recolour the trail texels along the dashes; write the field where it runs off-trail).
        if (request.Route is { } route)
        {
            PaintRoute(route, request, reach, metersPerTexelX, metersPerTexelY, rgba, bestPriority, bestDistance);
        }

        // WATER pass: a parallel single-channel distance field over the SAME window. Painted independently of
        // the RGBA priorities, so a trail crossing a stream keeps the trail colour but the texel still reads as
        // water (the shader glints from THIS field, not the colour).
        byte[]? water = null;
        if (request.WaterLines.Count > 0)
        {
            water = new byte[texels];
            foreach (var line in request.WaterLines)
            {
                var pts = line.Points;
                if (pts is null)
                {
                    continue;
                }

                for (var s = 0; s + 1 < pts.Count; s++)
                {
                    var a = pts[s];
                    var b = pts[s + 1];
                    if (!IsFinite(a) || !IsFinite(b))
                    {
                        continue;
                    }

                    PaintWaterSegment(
                        new Vector2(a.X, a.Y), new Vector2(b.X, b.Y),
                        request, reach, metersPerTexelX, metersPerTexelY, water);
                }
            }
        }

        return new TrailMask(width, height, request.WorldMinX, request.WorldMinY, request.WorldSizeX, request.WorldSizeY, rgba, water);
    }

    // Stamps one watercourse segment into the single-channel water field: max-combine of the linear
    // distance ramp (255 on the centre → 0 at reach), so overlapping segments never darken each other.
    private static void PaintWaterSegment(
        Vector2 a,
        Vector2 b,
        TrailMaskRequest request,
        float reach,
        float metersPerTexelX,
        float metersPerTexelY,
        byte[] water)
    {
        if (reach <= 0f)
        {
            return;
        }

        var width = request.Width;
        var height = request.Height;
        var minWorldX = MathF.Min(a.X, b.X) - reach;
        var maxWorldX = MathF.Max(a.X, b.X) + reach;
        var minWorldY = MathF.Min(a.Y, b.Y) - reach;
        var maxWorldY = MathF.Max(a.Y, b.Y) + reach;

        var i0 = Math.Clamp((int)MathF.Floor((minWorldX - request.WorldMinX) / request.WorldSizeX * width), 0, width - 1);
        var i1 = Math.Clamp((int)MathF.Ceiling((maxWorldX - request.WorldMinX) / request.WorldSizeX * width), 0, width - 1);
        var j0 = Math.Clamp((int)MathF.Floor((minWorldY - request.WorldMinY) / request.WorldSizeY * height), 0, height - 1);
        var j1 = Math.Clamp((int)MathF.Ceiling((maxWorldY - request.WorldMinY) / request.WorldSizeY * height), 0, height - 1);

        for (var j = j0; j <= j1; j++)
        {
            var worldY = request.WorldMinY + ((j + 0.5f) * metersPerTexelY);
            for (var i = i0; i <= i1; i++)
            {
                var worldX = request.WorldMinX + ((i + 0.5f) * metersPerTexelX);
                var dist = DistanceToSegment(new Vector2(worldX, worldY), a, b);
                if (dist > reach)
                {
                    continue;
                }

                var idx = (j * width) + i;
                var alpha = (byte)Math.Clamp((int)MathF.Round((1f - (dist / reach)) * 255f), 0, 255);
                if (alpha > water[idx])
                {
                    water[idx] = alpha;
                }
            }
        }
    }

    // Priority sentinel marking a texel the route pass has already claimed, so overlapping route dashes don't
    // re-blend the same texel (which would over-saturate the violet) and so a dash never re-owns a texel a closer
    // dash already took. Far above any line priority.
    private const int RoutePriority = 1_000_000;

    // Walks the route polyline by cumulative arc length and paints the DASH stretches (skips the gaps), so the line
    // reads as dashed. Each segment is subdivided into short steps so a dash/gap boundary inside a long segment is
    // honoured; only the steps whose midpoint falls in a dash are painted.
    private static void PaintRoute(
        MaskRoute route,
        TrailMaskRequest request,
        float reach,
        float metersPerTexelX,
        float metersPerTexelY,
        byte[] rgba,
        int[] bestPriority,
        float[] bestDistance)
    {
        var pts = route.Points;
        if (reach <= 0f || pts is null || pts.Count < 2)
        {
            return;
        }

        float dash = MathF.Max(0.01f, route.DashMeters);
        float gap = MathF.Max(0f, route.GapMeters);
        float period = dash + gap;
        float strength = Math.Clamp(route.BlendStrength, 0f, 1f);
        // TIGHT paint radius (clamped to the field reach) so a dash recolours only its own stretch of the line and
        // doesn't bleed across the gaps. 0 = fall back to a thin ~1.5 m so the route is never invisible.
        float paintRadius = route.PaintRadiusMeters > 0f ? MathF.Min(route.PaintRadiusMeters, reach) : MathF.Min(1.5f, reach);

        // Step the route in small increments so dashes land continuously and a boundary mid-segment is respected.
        // ~half the finer texel pitch (and never coarser than the dash) keeps the painted run gap-free yet cheap.
        float step = MathF.Min(dash, 0.5f * MathF.Min(metersPerTexelX, metersPerTexelY));
        step = MathF.Max(step, 0.25f);

        float arc = 0f; // cumulative arc length along the whole route (drives the dash phase)
        for (var s = 0; s + 1 < pts.Count; s++)
        {
            var pa = pts[s];
            var pb = pts[s + 1];
            if (!IsFinite(pa) || !IsFinite(pb))
            {
                continue; // NaN break — do not bridge the gap (and do not advance the phase across it).
            }

            var a = new Vector2(pa.X, pa.Y);
            var b = new Vector2(pb.X, pb.Y);
            float segLen = Vector2.Distance(a, b);
            if (segLen <= 1e-4f)
            {
                continue;
            }

            Vector2 dir = (b - a) / segLen;
            float t = 0f;
            while (t < segLen)
            {
                float t1 = MathF.Min(segLen, t + step);
                float mid = arc + ((t + t1) * 0.5f);
                bool inDash = period <= 0f || (mid % period) < dash;
                if (inDash)
                {
                    PaintRouteSegment(
                        a + (dir * t),
                        a + (dir * t1),
                        route,
                        strength,
                        paintRadius,
                        request,
                        reach,
                        metersPerTexelX,
                        metersPerTexelY,
                        rgba,
                        bestPriority,
                        bestDistance);
                }

                t = t1;
            }

            arc += segLen;
        }
    }

    // Paints one DASH sub-segment of the route. For each texel within reach: if a trail already wrote the texel,
    // blend its RGB toward the route tint (translucent — the trail shows through) and claim it; if no trail is there
    // (route off-trail), write the distance field in the route tint so the dash is still visible. A texel already
    // claimed by the route pass is left alone (no double-blend, nearest dash keeps it).
    private static void PaintRouteSegment(
        Vector2 a,
        Vector2 b,
        MaskRoute route,
        float strength,
        float paintRadius,
        TrailMaskRequest request,
        float reach,
        float metersPerTexelX,
        float metersPerTexelY,
        byte[] rgba,
        int[] bestPriority,
        float[] bestDistance)
    {
        var width = request.Width;
        var height = request.Height;

        // Expand by the TIGHT paint radius (not the full field reach) so the dash recolours only its own stretch.
        var minWorldX = MathF.Min(a.X, b.X) - paintRadius;
        var maxWorldX = MathF.Max(a.X, b.X) + paintRadius;
        var minWorldY = MathF.Min(a.Y, b.Y) - paintRadius;
        var maxWorldY = MathF.Max(a.Y, b.Y) + paintRadius;

        var i0 = (int)MathF.Floor((minWorldX - request.WorldMinX) / request.WorldSizeX * width);
        var i1 = (int)MathF.Ceiling((maxWorldX - request.WorldMinX) / request.WorldSizeX * width);
        var j0 = (int)MathF.Floor((minWorldY - request.WorldMinY) / request.WorldSizeY * height);
        var j1 = (int)MathF.Ceiling((maxWorldY - request.WorldMinY) / request.WorldSizeY * height);

        i0 = Math.Clamp(i0, 0, width - 1);
        i1 = Math.Clamp(i1, 0, width - 1);
        j0 = Math.Clamp(j0, 0, height - 1);
        j1 = Math.Clamp(j1, 0, height - 1);

        for (var j = j0; j <= j1; j++)
        {
            var worldY = request.WorldMinY + ((j + 0.5f) * metersPerTexelY);
            for (var i = i0; i <= i1; i++)
            {
                var worldX = request.WorldMinX + ((i + 0.5f) * metersPerTexelX);
                var dist = DistanceToSegment(new Vector2(worldX, worldY), a, b);
                if (dist > paintRadius)
                {
                    continue;
                }

                var idx = (j * width) + i;
                if (bestPriority[idx] == RoutePriority && dist >= bestDistance[idx])
                {
                    continue; // a closer (or equal) route dash already owns this texel — don't re-blend/over-paint.
                }

                var p = idx * 4;
                bool hadTrail = float.IsFinite(bestDistance[idx]) && bestPriority[idx] != RoutePriority;
                if (hadTrail)
                {
                    // On-trail: blend the trail colour toward the route tint (translucent), keep the trail's distance
                    // so the thin line stays exactly on the trail centre.
                    rgba[p] = Lerp(rgba[p], route.R, strength);
                    rgba[p + 1] = Lerp(rgba[p + 1], route.G, strength);
                    rgba[p + 2] = Lerp(rgba[p + 2], route.B, strength);
                    // A (distance) unchanged.
                }
                else
                {
                    // Off-trail (or a previous, farther route dash): write the field in the route tint so the dash
                    // is visible on bare terrain. Nearest route dash wins the distance.
                    var alpha = 1f - (dist / reach);
                    rgba[p] = route.R;
                    rgba[p + 1] = route.G;
                    rgba[p + 2] = route.B;
                    rgba[p + 3] = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
                    bestDistance[idx] = dist;
                }

                bestPriority[idx] = RoutePriority;
            }
        }
    }

    private static byte Lerp(byte from, byte to, float t) =>
        (byte)Math.Clamp((int)MathF.Round(from + ((to - from) * t)), 0, 255);

    private static void PaintSegment(
        Vector2 a,
        Vector2 b,
        MaskPolyline line,
        TrailMaskRequest request,
        float reach,
        float metersPerTexelX,
        float metersPerTexelY,
        byte[] rgba,
        int[] bestPriority,
        float[] bestDistance)
    {
        if (reach <= 0f)
        {
            return;
        }

        var width = request.Width;
        var height = request.Height;

        // World-space bounding box of the segment, expanded by the reach, mapped to a texel index window.
        var minWorldX = MathF.Min(a.X, b.X) - reach;
        var maxWorldX = MathF.Max(a.X, b.X) + reach;
        var minWorldY = MathF.Min(a.Y, b.Y) - reach;
        var maxWorldY = MathF.Max(a.Y, b.Y) + reach;

        var i0 = (int)MathF.Floor((minWorldX - request.WorldMinX) / request.WorldSizeX * width);
        var i1 = (int)MathF.Ceiling((maxWorldX - request.WorldMinX) / request.WorldSizeX * width);
        var j0 = (int)MathF.Floor((minWorldY - request.WorldMinY) / request.WorldSizeY * height);
        var j1 = (int)MathF.Ceiling((maxWorldY - request.WorldMinY) / request.WorldSizeY * height);

        i0 = Math.Clamp(i0, 0, width - 1);
        i1 = Math.Clamp(i1, 0, width - 1);
        j0 = Math.Clamp(j0, 0, height - 1);
        j1 = Math.Clamp(j1, 0, height - 1);

        for (var j = j0; j <= j1; j++)
        {
            var worldY = request.WorldMinY + ((j + 0.5f) * metersPerTexelY);
            for (var i = i0; i <= i1; i++)
            {
                var worldX = request.WorldMinX + ((i + 0.5f) * metersPerTexelX);
                var dist = DistanceToSegment(new Vector2(worldX, worldY), a, b);
                if (dist > reach)
                {
                    continue;
                }

                var idx = (j * width) + i;
                // Distance field: the NEAREST line wins the texel. Where two lines are (near-)coincident, the
                // higher-priority one wins the colour (e.g. an exposed route's orange over the trail it follows).
                var prev = bestDistance[idx];
                var wins = dist < prev - CoincidentEpsilonMeters
                    || (dist <= prev + CoincidentEpsilonMeters && line.Priority > bestPriority[idx]);
                if (!wins)
                {
                    continue;
                }

                bestPriority[idx] = line.Priority;
                bestDistance[idx] = dist; // distance to the line whose colour we store → A below stays consistent

                // A encodes the distance: 255 on the centre → 0 at the max distance (and clamped beyond), so the
                // band is continuous and bilinear-filters cleanly. The shader reconstructs metres = (1-A)*maxDist.
                var alpha = 1f - (dist / reach);
                var p = idx * 4;
                rgba[p] = line.R;
                rgba[p + 1] = line.G;
                rgba[p + 2] = line.B;
                rgba[p + 3] = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
            }
        }
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lengthSq = ab.LengthSquared();
        if (lengthSq <= 1e-12f)
        {
            return Vector2.Distance(p, a); // degenerate segment = point
        }

        var t = Math.Clamp(Vector2.Dot(p - a, ab) / lengthSq, 0f, 1f);
        var projection = a + (ab * t);
        return Vector2.Distance(p, projection);
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}