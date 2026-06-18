using System.Numerics;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Trails;

using SkiaSharp;

namespace MapaTur.App.Services;

/// <summary>
/// Draws a <see cref="TerrainMesh3D"/> onto an <see cref="SKCanvas"/> through a
/// <see cref="Camera3D"/>. Math lives in <see cref="Terrain3DProjection"/>; this
/// class is the (untested) SkiaSharp adapter.
/// </summary>
/// <remarks>
/// Hold one instance per viewport. Long-lived caches reuse the mesh-color array
/// (it never changes after build), the per-frame SKPoint scratch buffer, and the
/// SkiaSharp paint/shader objects, eliminating the multi-MB-per-frame allocation
/// churn the original static version produced.
/// </remarks>
public sealed class Terrain3DCanvasRenderer : IDisposable
{
    private static readonly SKColor SkyTopColor = new(0x1A, 0x35, 0x55);
    private static readonly SKColor SkyBottomColor = new(0x6C, 0x8E, 0xB0);
    private static readonly SKColor RouteColor = new(0x7C, 0x3A, 0xED); // violet, matches 2D planner
    private static readonly SKColor ClimbingOutlineColor = new(0x1F, 0x29, 0x37);
    private const float ClimbingMarkerRadiusPx = 5.5f;

    // POI markers share the climbing outline but draw a touch larger so huts/shelters read as
    // distinct, higher-priority landmarks against the crag dots.
    private const float PoiMarkerRadiusPx = 6.5f;

    // Peak markers: a warm-gold mountain glyph with a dark outline, plus an elevation label
    // drawn over a dark halo so it stays legible against both bright snow and dark forest.
    private static readonly SKColor PeakMarkerColor = new(0xFF, 0xD7, 0x4A);
    private static readonly SKColor PeakOutlineColor = new(0x3A, 0x2E, 0x10);
    private static readonly SKColor PeakLabelHaloColor = new(0x10, 0x14, 0x20);

    // GPS marker palette: vivid blue (matches the 2D dot) over an alpha-modulated halo. The halo's
    // alpha drops with accuracy so a precise fix shows a tight, opaque ring while a low-confidence
    // fix dissolves into a soft glow — communicates uncertainty without a numeric overlay.
    private static readonly SKColor UserLocationFill = new(0x25, 0x63, 0xEB);
    private static readonly SKColor UserLocationOutline = new(0xFF, 0xFF, 0xFF);
    private static readonly SKColor UserLocationHalo = new(0x25, 0x63, 0xEB);
    private const float UserLocationDotRadiusPx = 7f;
    private const float UserLocationOutlineWidthPx = 2.5f;
    private const float UserLocationHaloMaxRadiusPx = 26f;
    private const float PeakMarkerHalfWidthPx = 6f;
    private const float PeakMarkerHeightPx = 12f;
    private const float PeakLabelSizePx = 12.5f;
    // Named summits get their name on a line above the elevation, in a slightly larger bold face.
    private const float PeakNameSizePx = 14f;

    /// <summary>Font size (px) for night-sky star-name labels — a touch smaller than peak names.</summary>
    private const float StarLabelSizePx = 13f;
    // Minimum on-screen spacing between peak markers; closer ones are dropped so labels don't overlap.
    private const float MinPeakSeparationPx = 52f;
    private readonly List<SKPoint> drawnPeakAnchors = new();

    // Trail / route / climbing vertices are only culled when their NDC depth
    // exceeds the local mesh depth by more than this much. The minimum-per-bin
    // depth map mixes nearby foreground peaks with background valleys at bin
    // boundaries, so a loose tolerance keeps real valley trails visible while
    // still hiding trails that are clearly *behind* a peak.
    private const float OcclusionEpsilon = 0.03f;

    // Per-tile render state, keyed by tile reference. A high-resolution terrain is rendered as many
    // mesh tiles (each ≤ 65 536 vertices, the ushort-index limit); each keeps its own colour array
    // (built once, never changes), a reusable screen-point buffer, ping-pong index buffers and a
    // cached centroid for back-to-front tile ordering. Tiles are stable across frames until a new DEM
    // loads, at which point stale entries are pruned.
    private sealed class TileRenderState
    {
        public SKColor[] Colors = Array.Empty<SKColor>();
        public SKPoint[] Points = Array.Empty<SKPoint>();

        // Ping-pong index buffers sized exactly to the frame's visible triangle count. SKVertices
        // walks every index, so the array MUST be exact-length; ping-pong lets the previous frame's
        // array stay readable by an in-flight SKVertices while we fill the other.
        public ushort[] IndexA = Array.Empty<ushort>();
        public ushort[] IndexB = Array.Empty<ushort>();
        public bool UseA = true;
        public System.Numerics.Vector3 Centroid;

        // World-space axis-aligned bounding box, used to frustum-cull the tile so off-screen tiles
        // (most of them when zoomed in) skip the expensive per-vertex projection entirely.
        public System.Numerics.Vector3 Min;
        public System.Numerics.Vector3 Max;
    }

    private readonly Dictionary<TerrainMesh3D, TileRenderState> tileStates = new();
    private readonly List<int> tileDrawOrder = new();
    private IReadOnlyList<TerrainMesh3D>? lastTiles;

    private SKPaint? meshPaint;
    private SKPaint? trailPaint;
    private SKPaint? routePaint;
    private SKPaint? climbingFillPaint;
    private SKPaint? climbingOutlinePaint;
    private SKPaint? poiFillPaint;
    private SKPaint? poiOutlinePaint;
    private SKPaint? peakFillPaint;
    private SKPaint? peakOutlinePaint;
    private SKPaint? peakLabelFillPaint;
    private SKPaint? peakLabelHaloPaint;
    private SKFont? peakFont;
    private SKFont? peakNameFont;
    private SKFont? starLabelFont;
    private SKPaint? starLabelFillPaint;
    private SKPaint? starLinePaint;
    private SKPath? peakPath;
    private SKPaint? skyPaint;
    private SKShader? skyShader;
    private int cachedSkyWidth;
    private int cachedSkyHeight;

    // Reused SKPath per PTTK colour — built fresh each frame via Reset() (no GC),
    // collapses 38k+ DrawLine calls per frame down to one DrawPath per colour group.
    private readonly Dictionary<SKColor, SKPath> trailPathCache = new();

    /// <summary>
    /// Clears the canvas with a sky gradient and draws a high-resolution terrain made of one or more
    /// mesh tiles, then the overlays. Tiles are drawn back-to-front (by projected centroid depth) so the
    /// painter's algorithm resolves occlusion between them; each tile's triangles are already depth-sorted
    /// by <see cref="Terrain3DProjection"/>. The supplied scratch is reused across tiles.
    /// </summary>
    public void RenderTiles(
        SKCanvas canvas,
        int viewportWidth,
        int viewportHeight,
        IReadOnlyList<TerrainMesh3D> tiles,
        Camera3D camera,
        Terrain3DFrameScratch scratch,
        ScreenDepthMap? depthMap = null,
        IReadOnlyList<ProjectedTrail>? trails = null,
        ProjectedRoute? route = null,
        IReadOnlyList<ProjectedClimbingArea>? climbingAreas = null,
        IReadOnlyList<ProjectedPoi>? pois = null,
        IReadOnlyList<ProjectedPeak>? peaks = null,
        ProjectedUserLocation? userLocation = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(scratch);

        DrawSky(canvas, viewportWidth, viewportHeight);

        if (viewportWidth <= 0 || viewportHeight <= 0 || tiles.Count == 0)
        {
            return;
        }

        // A new DEM produces an all-new tile set (fresh instances), so drop the previous set's cached
        // colour/point/index buffers in one shot when the list reference changes.
        if (!ReferenceEquals(lastTiles, tiles))
        {
            tileStates.Clear();
            lastTiles = tiles;
        }

        if (depthMap is not null)
        {
            depthMap.Configure(viewportWidth, viewportHeight);
            depthMap.Reset();
        }

        Matrix4x4 viewProjection = camera.BuildViewProjection((float)viewportWidth / viewportHeight);
        OrderTilesBackToFront(tiles, camera, viewProjection, viewportWidth, viewportHeight);

        foreach (int tileIndex in tileDrawOrder)
        {
            TerrainMesh3D tile = tiles[tileIndex];

            // Skip tiles entirely outside the view — when zoomed in this culls most of a
            // high-resolution terrain, so only the visible tiles pay the per-vertex projection cost.
            if (IsTileOffscreen(GetTileState(tile), viewProjection, viewportWidth, viewportHeight))
            {
                continue;
            }

            ProjectedTerrainFrame frame = Terrain3DProjection.Project(tile, camera, viewportWidth, viewportHeight, scratch);
            if (frame.VisibleIndexCount > 0)
            {
                DrawMeshTile(canvas, tile, frame);
            }

            // Accumulate every tile's projected vertices into the shared depth grid so overlay
            // occlusion (when enabled) tests against the whole terrain, not just one tile.
            if (depthMap is not null)
            {
                for (int i = 0; i < frame.VertexCount; i++)
                {
                    var v = frame.ScreenVertices[i];
                    depthMap.Write(v.X, v.Y, v.Z);
                }
            }
        }

        if (trails is not null)
        {
            DrawTrails(canvas, trails, depthMap);
        }
        if (route is not null)
        {
            DrawRoute(canvas, route.Value, depthMap);
        }
        if (climbingAreas is not null)
        {
            DrawClimbingAreas(canvas, climbingAreas, depthMap);
        }
        if (pois is not null)
        {
            DrawPois(canvas, pois, depthMap, camera.Distance < PoiLabelMaxDistanceWorld);
        }
        if (peaks is not null)
        {
            DrawPeaks(canvas, peaks, depthMap);
        }
        // Always drawn last so the user-location dot sits on top of everything else — it's the
        // single most important overlay (where am I?) and must never be hidden behind a label.
        if (userLocation is not null)
        {
            DrawUserLocation(canvas, userLocation.Value, depthMap);
        }
    }

    /// <summary>
    /// Fills <see cref="tileDrawOrder"/> with tile indices sorted far→near by projected centroid depth,
    /// so the painter's algorithm draws distant tiles first. Tiles whose centroid is behind the camera
    /// sort to the front of the list (drawn earliest), matching their likely-occluded role.
    /// </summary>
    private void OrderTilesBackToFront(IReadOnlyList<TerrainMesh3D> tiles, Camera3D camera, Matrix4x4 viewProjection, int width, int height)
    {
        tileDrawOrder.Clear();
        Span<float> depths = tiles.Count <= 256 ? stackalloc float[tiles.Count] : new float[tiles.Count];
        for (int i = 0; i < tiles.Count; i++)
        {
            TileRenderState state = GetTileState(tiles[i]);
            System.Numerics.Vector3? screen = camera.ProjectToScreen(state.Centroid, viewProjection, width, height);
            depths[i] = screen?.Z ?? float.PositiveInfinity; // behind camera → treat as farthest
            tileDrawOrder.Add(i);
        }

        // Sort indices by depth descending (farthest first). Small N (tens of tiles) so an insertion
        // sort is plenty and allocation-free.
        for (int i = 1; i < tileDrawOrder.Count; i++)
        {
            int idx = tileDrawOrder[i];
            float d = depths[idx];
            int j = i - 1;
            while (j >= 0 && depths[tileDrawOrder[j]] < d)
            {
                tileDrawOrder[j + 1] = tileDrawOrder[j];
                j--;
            }
            tileDrawOrder[j + 1] = idx;
        }
    }

    private void DrawMeshTile(SKCanvas canvas, TerrainMesh3D tile, ProjectedTerrainFrame frame)
    {
        TileRenderState state = GetTileState(tile);
        int vertexCount = frame.VertexCount;

        for (int i = 0; i < vertexCount; i++)
        {
            var v = frame.ScreenVertices[i];
            state.Points[i] = float.IsNaN(v.X) ? SKPoint.Empty : new SKPoint(v.X, v.Y);
        }

        int needed = frame.VisibleIndexCount;
        ushort[] current = state.UseA ? state.IndexA : state.IndexB;
        if (current.Length != needed)
        {
            current = new ushort[needed];
            if (state.UseA) state.IndexA = current; else state.IndexB = current;
        }
        Array.Copy(frame.VisibleIndices, current, needed);
        state.UseA = !state.UseA;

        using var vertices = SKVertices.CreateCopy(
            SKVertexMode.Triangles,
            state.Points,
            null,
            state.Colors,
            current);

        // Vertex-colour-only mesh: Modulate × white paint = identity, so the per-vertex hypsometric
        // colours render unchanged (default SrcOver + opaque-black paint would zero them).
        meshPaint ??= new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.DrawVertices(vertices, SKBlendMode.Modulate, meshPaint);
    }

    /// <summary>
    /// Gets (or builds) the cached render state for a tile: its converted colour array, screen-point
    /// buffer and centroid. Built once per tile; pruned when a new DEM replaces the tile set.
    /// </summary>
    private TileRenderState GetTileState(TerrainMesh3D tile)
    {
        if (tileStates.TryGetValue(tile, out var state) && state.Colors.Length == tile.Vertices.Length)
        {
            return state;
        }

        int vertexCount = tile.Vertices.Length;
        var colors = new SKColor[vertexCount];
        var centroid = System.Numerics.Vector3.Zero;
        var min = new System.Numerics.Vector3(float.PositiveInfinity);
        var max = new System.Numerics.Vector3(float.NegativeInfinity);
        for (int i = 0; i < vertexCount; i++)
        {
            colors[i] = new SKColor(tile.Colors[i]);
            System.Numerics.Vector3 v = tile.Vertices[i];
            centroid += v;
            min = System.Numerics.Vector3.Min(min, v);
            max = System.Numerics.Vector3.Max(max, v);
        }
        if (vertexCount > 0)
        {
            centroid /= vertexCount;
        }

        state = new TileRenderState
        {
            Colors = colors,
            Points = new SKPoint[vertexCount],
            Centroid = centroid,
            Min = min,
            Max = max,
        };
        tileStates[tile] = state;
        return state;
    }

    /// <summary>
    /// Conservative frustum cull: projects the tile's 8 world AABB corners and culls only when every
    /// corner is in front of the camera AND they all fall off the same screen edge. If any corner is
    /// behind the camera the tile straddles the near plane, so it's kept (can't safely cull).
    /// </summary>
    private static bool IsTileOffscreen(TileRenderState state, Matrix4x4 viewProjection, int width, int height)
    {
        System.Numerics.Vector3 min = state.Min;
        System.Numerics.Vector3 max = state.Max;
        bool allLeft = true, allRight = true, allAbove = true, allBelow = true;

        for (int corner = 0; corner < 8; corner++)
        {
            var world = new System.Numerics.Vector3(
                (corner & 1) == 0 ? min.X : max.X,
                (corner & 2) == 0 ? min.Y : max.Y,
                (corner & 4) == 0 ? min.Z : max.Z);

            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
            if (clip.W <= 0f)
            {
                return false; // straddles the near plane — keep it
            }

            float invW = 1f / clip.W;
            float sx = (((clip.X * invW) + 1f) * 0.5f) * width;
            float sy = ((1f - (clip.Y * invW)) * 0.5f) * height;

            if (sx >= 0f) allLeft = false;
            if (sx <= width) allRight = false;
            if (sy >= 0f) allAbove = false;
            if (sy <= height) allBelow = false;
        }

        return allLeft || allRight || allAbove || allBelow;
    }

    /// <summary>
    /// Draws only the 2D overlays (trails, route, climbing markers, peak labels) onto the canvas, with no
    /// sky or terrain. Used when the terrain itself is rendered by the GPU (OpenGL) engine and Skia is just
    /// the overlay layer drawn on top. Overlays are projected with the same camera, so they register.
    /// </summary>
    public void DrawOverlays(
        SKCanvas canvas,
        IReadOnlyList<ProjectedTrail>? trails,
        ProjectedRoute? route,
        IReadOnlyList<ProjectedClimbingArea>? climbingAreas,
        IReadOnlyList<ProjectedPoi>? pois,
        IReadOnlyList<ProjectedPeak>? peaks,
        ProjectedUserLocation? userLocation = null,
        bool poiLabelsVisible = true)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        if (trails is not null)
        {
            DrawTrails(canvas, trails, null);
        }
        if (route is not null)
        {
            DrawRoute(canvas, route.Value, null);
        }
        if (climbingAreas is not null)
        {
            DrawClimbingAreas(canvas, climbingAreas, null);
        }
        if (pois is not null)
        {
            DrawPois(canvas, pois, null, poiLabelsVisible);
        }
        if (peaks is not null)
        {
            DrawPeaks(canvas, peaks, null);
        }
        if (userLocation is not null)
        {
            DrawUserLocation(canvas, userLocation.Value, null);
        }
    }

    private void DrawTrails(SKCanvas canvas, IReadOnlyList<ProjectedTrail> trails, ScreenDepthMap? depthMap)
    {
        if (trails.Count == 0)
        {
            return;
        }

        trailPaint ??= new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };

        // Reset every cached path so this frame starts from empty without freeing
        // the underlying SkPath storage.
        foreach (var p in trailPathCache.Values)
        {
            p.Reset();
        }

        // Append each trail's polyline to the SKPath bucketed by its PTTK colour.
        foreach (var projected in trails)
        {
            var color = TrailColor(projected.Source);
            if (!trailPathCache.TryGetValue(color, out var path))
            {
                path = new SKPath();
                trailPathCache[color] = path;
            }
            AppendPolylineToPath(path, projected.ScreenPoints, depthMap);
        }

        // One draw call per colour group — typically ≤7 for the PTTK palette.
        foreach (var (color, path) in trailPathCache)
        {
            if (path.IsEmpty)
            {
                continue;
            }
            trailPaint.Color = color;
            canvas.DrawPath(path, trailPaint);
        }
    }

    private static void AppendPolylineToPath(SKPath path, IReadOnlyList<System.Numerics.Vector3?> pts, ScreenDepthMap? depthMap)
    {
        bool penDown = false;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            // Both off-frustum (null) and behind-mesh vertices break the polyline so
            // the next visible vertex starts a fresh sub-path instead of cutting across.
            if (p is null || (depthMap is not null && depthMap.IsBehind(p, OcclusionEpsilon)))
            {
                penDown = false;
                continue;
            }

            if (!penDown)
            {
                path.MoveTo(p.Value.X, p.Value.Y);
                penDown = true;
            }
            else
            {
                path.LineTo(p.Value.X, p.Value.Y);
            }
        }
    }

    private void DrawRoute(SKCanvas canvas, ProjectedRoute route, ScreenDepthMap? depthMap)
    {
        var pts = route.ScreenPoints;
        if (pts.Count < 2)
        {
            return;
        }

        routePaint ??= new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = RouteColor,
        };

        for (int i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            if (a is null || b is null)
            {
                continue;
            }
            if (depthMap is not null && (depthMap.IsBehind(a, OcclusionEpsilon) || depthMap.IsBehind(b, OcclusionEpsilon)))
            {
                continue;
            }
            canvas.DrawLine(a.Value.X, a.Value.Y, b.Value.X, b.Value.Y, routePaint);
        }
    }

    private void DrawClimbingAreas(SKCanvas canvas, IReadOnlyList<ProjectedClimbingArea> areas, ScreenDepthMap? depthMap)
    {
        if (areas.Count == 0)
        {
            return;
        }

        climbingFillPaint ??= new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        climbingOutlinePaint ??= new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = ClimbingOutlineColor,
        };

        foreach (var marker in areas)
        {
            var screen = marker.ScreenPosition;
            if (screen is null)
            {
                continue;
            }
            if (depthMap is not null && depthMap.IsBehind(screen, OcclusionEpsilon))
            {
                continue;
            }
            climbingFillPaint.Color = ParseClimbingColor(marker.Source.Type);
            canvas.DrawCircle(screen.Value.X, screen.Value.Y, ClimbingMarkerRadiusPx, climbingFillPaint);
            canvas.DrawCircle(screen.Value.X, screen.Value.Y, ClimbingMarkerRadiusPx, climbingOutlinePaint);
        }
    }

    /// <summary>Orbit distance (world units) below which POI text labels are drawn; farther/higher than
    /// this only the marker dot shows, so a wide view of 1000+ POIs isn't a wall of overlapping text.</summary>
    public const float PoiLabelMaxDistanceWorld = 6000f;

    private void DrawPois(SKCanvas canvas, IReadOnlyList<ProjectedPoi> pois, ScreenDepthMap? depthMap, bool showLabels)
    {
        if (pois.Count == 0)
        {
            return;
        }

        poiFillPaint ??= new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
        };
        poiOutlinePaint ??= new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = ClimbingOutlineColor,
        };
        // Reuse the peak label paints/font (created lazily here too, since POIs draw before peaks).
        peakLabelFillPaint ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White };
        peakLabelHaloPaint ??= new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            Color = PeakLabelHaloColor,
        };
        peakFont ??= new SKFont { Size = PeakLabelSizePx };
        peakPath ??= new SKPath();

        foreach (var marker in pois)
        {
            var screen = marker.ScreenPosition;
            if (screen is null)
            {
                continue;
            }
            if (depthMap is not null && depthMap.IsBehind(screen, OcclusionEpsilon))
            {
                continue;
            }
            float x = screen.Value.X;
            float y = screen.Value.Y;
            poiFillPaint.Color = ParsePoiColor(marker.Source.Kind);

            // Viewpoints get a little lookout-tower glyph so they read as "go here for the view"; every
            // other kind is a coloured dot. glyphTopY is where the label sits above.
            float glyphTopY;
            if (marker.Source.Kind == MapaTur.Domain.Pois.PoiKind.Viewpoint)
            {
                glyphTopY = DrawViewpointTower(canvas, x, y);
            }
            else
            {
                canvas.DrawCircle(x, y, PoiMarkerRadiusPx, poiFillPaint);
                canvas.DrawCircle(x, y, PoiMarkerRadiusPx, poiOutlinePaint);
                glyphTopY = y - PoiMarkerRadiusPx;
            }

            // Label only when the camera is close (showLabels) — a far/high view of a wide POI set would
            // otherwise be an unreadable wall of overlapping text. The coloured dot / tower always shows.
            if (showLabels)
            {
                string label = !string.IsNullOrEmpty(marker.Source.Name)
                    ? marker.Source.Name
                    : PoiKindLabel(marker.Source.Kind);
                float labelY = glyphTopY - 4f;
                canvas.DrawText(label, x, labelY, SKTextAlign.Center, peakFont, peakLabelHaloPaint);
                canvas.DrawText(label, x, labelY, SKTextAlign.Center, peakFont, peakLabelFillPaint);
            }
        }
    }

    // Faint constellation lines connecting the in-frame stars of an asterism (e.g. the Big Dipper), drawn
    // under the star-name labels so the names stay legible. Endpoints come from ConstellationLines, which only
    // emits a segment when both its stars are on screen.
    public void DrawConstellationLines(SKCanvas canvas, IReadOnlyList<StarSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (segments is null || segments.Count == 0)
        {
            return;
        }

        starLinePaint ??= new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.2f,
            Color = new SKColor(0x9A, 0xA8, 0xC4, 0x70), // faint cool-pale, semi-transparent
        };

        foreach (StarSegment seg in segments)
        {
            canvas.DrawLine(seg.X1, seg.Y1, seg.X2, seg.Y2, starLinePaint);
        }
    }

    // Night-sky star-name labels: small warm-pale text just above each in-frame star's GL point sprite. The
    // positions come from StarLabelProjector, which mirrors the star shader's projection, so the name tracks
    // its dot. Drawn over the composited scene like the peak labels; the dark halo keeps it legible against
    // the sky gradient. The host only calls this at night with the handful of stars actually in frame.
    public void DrawStarLabels(SKCanvas canvas, IReadOnlyList<StarLabel> labels)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        if (labels is null || labels.Count == 0)
        {
            return;
        }

        starLabelFillPaint ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0xE8, 0xE2, 0xD4) };
        peakLabelHaloPaint ??= new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            Color = PeakLabelHaloColor,
        };
        starLabelFont ??= new SKFont { Size = StarLabelSizePx };

        foreach (StarLabel label in labels)
        {
            float labelY = label.ScreenY - 7f; // sit the name just above the star sprite
            canvas.DrawText(label.Name, label.ScreenX, labelY, SKTextAlign.Center, starLabelFont, peakLabelHaloPaint);
            canvas.DrawText(label.Name, label.ScreenX, labelY, SKTextAlign.Center, starLabelFont, starLabelFillPaint);
        }
    }

    // Draws a small observation-tower glyph (splayed legs + rung, a cabin and a peaked roof) with its base
    // on the projected point. Uses the already-coloured poiFillPaint + poiOutlinePaint. Returns the glyph's
    // top Y so the caller can place the label above it.
    private float DrawViewpointTower(SKCanvas canvas, float x, float y)
    {
        const float baseHalf = 5f;
        const float legTopHalf = 3f;
        const float cabinHalf = 4f;
        float legTopY = y - 11f;
        float cabinTopY = y - 16f;
        float roofApexY = y - 19f;

        // Legs + a mid rung (stroke only).
        peakPath!.Reset();
        peakPath.MoveTo(x - baseHalf, y);
        peakPath.LineTo(x - legTopHalf, legTopY);
        peakPath.MoveTo(x + baseHalf, y);
        peakPath.LineTo(x + legTopHalf, legTopY);
        peakPath.MoveTo(x - 4.2f, y - 5.5f);
        peakPath.LineTo(x + 4.2f, y - 5.5f);
        canvas.DrawPath(peakPath, poiOutlinePaint!);

        // Cabin box (fill + outline).
        peakPath.Reset();
        peakPath.MoveTo(x - cabinHalf, legTopY);
        peakPath.LineTo(x - cabinHalf, cabinTopY);
        peakPath.LineTo(x + cabinHalf, cabinTopY);
        peakPath.LineTo(x + cabinHalf, legTopY);
        peakPath.Close();
        canvas.DrawPath(peakPath, poiFillPaint!);
        canvas.DrawPath(peakPath, poiOutlinePaint!);

        // Peaked roof (fill + outline).
        peakPath.Reset();
        peakPath.MoveTo(x - (cabinHalf + 1f), cabinTopY);
        peakPath.LineTo(x, roofApexY);
        peakPath.LineTo(x + (cabinHalf + 1f), cabinTopY);
        peakPath.Close();
        canvas.DrawPath(peakPath, poiFillPaint!);
        canvas.DrawPath(peakPath, poiOutlinePaint!);

        return roofApexY;
    }

    private static string PoiKindLabel(MapaTur.Domain.Pois.PoiKind kind) => kind switch
    {
        MapaTur.Domain.Pois.PoiKind.Hut => "Hut",
        MapaTur.Domain.Pois.PoiKind.WildernessHut => "Shelter hut",
        MapaTur.Domain.Pois.PoiKind.Chalet => "Chalet",
        MapaTur.Domain.Pois.PoiKind.Shelter => "Shelter",
        MapaTur.Domain.Pois.PoiKind.Viewpoint => "Viewpoint",
        MapaTur.Domain.Pois.PoiKind.Parking => "Parking",
        MapaTur.Domain.Pois.PoiKind.Pass => "Przełęcz",
        _ => "POI",
    };

    private void DrawPeaks(SKCanvas canvas, IReadOnlyList<ProjectedPeak> peaks, ScreenDepthMap? depthMap)
    {
        if (peaks.Count == 0)
        {
            return;
        }

        peakFillPaint ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = PeakMarkerColor };
        peakOutlinePaint ??= new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            StrokeJoin = SKStrokeJoin.Round,
            Color = PeakOutlineColor,
        };
        peakLabelFillPaint ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White };
        peakLabelHaloPaint ??= new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            Color = PeakLabelHaloColor,
        };
        peakFont ??= new SKFont { Size = PeakLabelSizePx };
        peakNameFont ??= new SKFont
        {
            Size = PeakNameSizePx,
            Typeface = SKTypeface.FromFamilyName(
                null,
                SKFontStyleWeight.Bold,
                SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright),
        };
        peakPath ??= new SKPath();

        // De-clutter: skip any peak whose marker lands within MinPeakSeparationPx of an
        // already-drawn one (labels would overlap into mush, e.g. the tightly-packed Orla Perć
        // summits). NAMED summits are drawn in a FIRST pass so they win every cluster — otherwise a
        // slightly higher but unnamed detected maximum next door would suppress "Kozi Wierch" and
        // the flight would show only bare elevations. A second pass fills the gaps with unnamed maxima.
        drawnPeakAnchors.Clear();

        foreach (var peak in peaks)
        {
            if (!string.IsNullOrEmpty(peak.Source.Name))
            {
                TryDrawPeak(canvas, peak, depthMap);
            }
        }
        foreach (var peak in peaks)
        {
            if (string.IsNullOrEmpty(peak.Source.Name))
            {
                TryDrawPeak(canvas, peak, depthMap);
            }
        }
    }

    private void TryDrawPeak(SKCanvas canvas, ProjectedPeak peak, ScreenDepthMap? depthMap)
    {
        var screen = peak.ScreenPosition;
        if (screen is null)
        {
            return;
        }
        if (depthMap is not null && depthMap.IsBehind(screen, OcclusionEpsilon))
        {
            return;
        }

        float x = screen.Value.X;
        float y = screen.Value.Y;

        foreach (SKPoint anchor in drawnPeakAnchors)
        {
            float dx = anchor.X - x;
            float dy = anchor.Y - y;
            if ((dx * dx) + (dy * dy) < MinPeakSeparationPx * MinPeakSeparationPx)
            {
                return;
            }
        }
        drawnPeakAnchors.Add(new SKPoint(x, y));

        // Mountain glyph: base sits on the projected summit point, apex points up.
        peakPath!.Reset();
        peakPath.MoveTo(x - PeakMarkerHalfWidthPx, y);
        peakPath.LineTo(x, y - PeakMarkerHeightPx);
        peakPath.LineTo(x + PeakMarkerHalfWidthPx, y);
        peakPath.Close();
        canvas.DrawPath(peakPath, peakFillPaint!);
        canvas.DrawPath(peakPath, peakOutlinePaint!);

        // Elevation label above the apex; halo first, then fill, for contrast. Prefer the
        // authoritative (gazetteer) height when present — the DEM-sampled seat elevation
        // under-reports sharp summits.
        double labelElevation = peak.Source.LabelElevationMeters ?? peak.Source.ElevationMeters;
        string label = $"{Math.Round(labelElevation)} m";
        float labelY = y - PeakMarkerHeightPx - 4f;
        canvas.DrawText(label, x, labelY, SKTextAlign.Center, peakFont!, peakLabelHaloPaint!);
        canvas.DrawText(label, x, labelY, SKTextAlign.Center, peakFont!, peakLabelFillPaint!);

        // Named summits get their name on the line above the elevation.
        if (!string.IsNullOrEmpty(peak.Source.Name))
        {
            float nameY = labelY - PeakLabelSizePx - 3f;
            canvas.DrawText(peak.Source.Name, x, nameY, SKTextAlign.Center, peakNameFont!, peakLabelHaloPaint!);
            canvas.DrawText(peak.Source.Name, x, nameY, SKTextAlign.Center, peakNameFont!, peakLabelFillPaint!);
        }
    }

    private static SKColor ParseClimbingColor(MapaTur.Domain.Climbing.ClimbingType type)
    {
        string hex = MapaTur.Domain.Climbing.ClimbingTypeColors.ToHex(type);
        return SKColor.TryParse("#" + hex, out var color)
            ? color
            : new SKColor(0xE1, 0x1D, 0x48);
    }

    private static SKColor ParsePoiColor(MapaTur.Domain.Pois.PoiKind kind)
    {
        string hex = MapaTur.Domain.Pois.PoiKindColors.ToHex(kind);
        return SKColor.TryParse("#" + hex, out var color)
            ? color
            : new SKColor(0xDC, 0x26, 0x26);
    }

    private static SKColor TrailColor(Trail trail)
    {
        return SKColor.TryParse(OsmcSymbolParser.ToHex(trail.PrimaryColor), out var color)
            ? color
            : new SKColor(0x94, 0xA3, 0xB8);
    }

    private void DrawSky(SKCanvas canvas, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            canvas.Clear(SkyBottomColor);
            return;
        }

        if (skyShader is null || width != cachedSkyWidth || height != cachedSkyHeight)
        {
            skyShader?.Dispose();
            skyShader = SKShader.CreateLinearGradient(
                new SKPoint(0f, 0f),
                new SKPoint(0f, height),
                new[] { SkyTopColor, SkyBottomColor },
                null,
                SKShaderTileMode.Clamp);

            skyPaint ??= new SKPaint();
            skyPaint.Shader = skyShader;

            cachedSkyWidth = width;
            cachedSkyHeight = height;
        }

        canvas.DrawRect(0, 0, width, height, skyPaint!);
    }

    public void Dispose()
    {
        meshPaint?.Dispose();
        trailPaint?.Dispose();
        routePaint?.Dispose();
        climbingFillPaint?.Dispose();
        climbingOutlinePaint?.Dispose();
        poiFillPaint?.Dispose();
        poiOutlinePaint?.Dispose();
        peakFillPaint?.Dispose();
        peakOutlinePaint?.Dispose();
        peakLabelFillPaint?.Dispose();
        peakLabelHaloPaint?.Dispose();
        peakFont?.Dispose();
        peakNameFont?.Dispose();
        starLabelFont?.Dispose();
        starLabelFillPaint?.Dispose();
        starLinePaint?.Dispose();
        peakPath?.Dispose();
        skyPaint?.Dispose();
        skyShader?.Dispose();
        foreach (var p in trailPathCache.Values)
        {
            p.Dispose();
        }
        trailPathCache.Clear();
        meshPaint = null;
        trailPaint = null;
        routePaint = null;
        climbingFillPaint = null;
        climbingOutlinePaint = null;
        poiFillPaint = null;
        poiOutlinePaint = null;
        peakFillPaint = null;
        peakOutlinePaint = null;
        peakLabelFillPaint = null;
        peakLabelHaloPaint = null;
        peakFont = null;
        peakPath = null;
        skyPaint = null;
        skyShader = null;
        userLocationFillPaint = null;
        userLocationOutlinePaint = null;
        userLocationHaloPaint = null;
    }

    private SKPaint? userLocationFillPaint;
    private SKPaint? userLocationOutlinePaint;
    private SKPaint? userLocationHaloPaint;

    /// <summary>
    /// Draws the current GPS fix: a soft accuracy halo (radius scales with reported accuracy),
    /// a vivid blue dot, and a white outline so the marker stays legible over forest, snow or rock
    /// alike. Always drawn last so it sits above every other overlay. Honours the depth map when
    /// supplied so a fix behind a peak gets occluded just like trails / route do.
    /// </summary>
    private void DrawUserLocation(SKCanvas canvas, ProjectedUserLocation location, ScreenDepthMap? depthMap)
    {
        if (location.ScreenPosition is not { } screen)
        {
            return;
        }
        if (depthMap is not null && depthMap.IsBehind(screen, OcclusionEpsilon))
        {
            return;
        }

        userLocationHaloPaint ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = UserLocationHalo };
        userLocationFillPaint ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = UserLocationFill };
        userLocationOutlinePaint ??= new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = UserLocationOutlineWidthPx,
            Color = UserLocationOutline,
        };

        float x = screen.X;
        float y = screen.Y;

        // Halo radius: scale linearly with the reported accuracy, clamp to a sensible max so a 200 m
        // fix doesn't paint a quarter of the screen blue. Alpha drops as the halo grows so wider
        // (= less certain) halos read as softer, not louder.
        double accuracy = Math.Max(1.0, location.Source.AccuracyMeters);
        float haloRadius = Math.Min(UserLocationHaloMaxRadiusPx, UserLocationDotRadiusPx + (float)accuracy * 0.4f);
        byte haloAlpha = (byte)Math.Max(30, 96 - (int)(accuracy * 1.2));
        userLocationHaloPaint.Color = UserLocationHalo.WithAlpha(haloAlpha);
        canvas.DrawCircle(x, y, haloRadius, userLocationHaloPaint);

        // Inner dot + outline.
        canvas.DrawCircle(x, y, UserLocationDotRadiusPx, userLocationFillPaint);
        canvas.DrawCircle(x, y, UserLocationDotRadiusPx, userLocationOutlinePaint);
    }
}