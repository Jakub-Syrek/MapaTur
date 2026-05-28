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
    private static readonly SKColor ClimbingFillColor = new(0xE1, 0x1D, 0x48); // red, matches 2D climbing layer
    private static readonly SKColor ClimbingOutlineColor = new(0x1F, 0x29, 0x37);
    private const float ClimbingMarkerRadiusPx = 5.5f;

    private TerrainMesh3D? cachedMesh;
    private SKPoint[] pointScratch = Array.Empty<SKPoint>();
    private SKColor[] cachedColors = Array.Empty<SKColor>();

    // Two ping-pong index buffers sized exactly to the current frame's visible
    // triangle count. SkiaSharp 3.119 has no Span overload for SKVertices.CreateCopy,
    // so the array MUST be exact-length (trailing slots would form spurious triangles).
    // Ping-pong lets the previous frame's array be GC'd while Skia is still consuming
    // it instead of fighting for the same buffer.
    private ushort[] indexBufferA = Array.Empty<ushort>();
    private ushort[] indexBufferB = Array.Empty<ushort>();
    private bool useBufferA = true;

    private SKPaint? meshPaint;
    private SKPaint? trailPaint;
    private SKPaint? routePaint;
    private SKPaint? climbingFillPaint;
    private SKPaint? climbingOutlinePaint;
    private SKPaint? skyPaint;
    private SKShader? skyShader;
    private int cachedSkyWidth;
    private int cachedSkyHeight;

    // Reused SKPath per PTTK colour — built fresh each frame via Reset() (no GC),
    // collapses 38k+ DrawLine calls per frame down to one DrawPath per colour group.
    private readonly Dictionary<SKColor, SKPath> trailPathCache = new();

    /// <summary>
    /// Clears the canvas with a sky gradient and draws the projected terrain mesh
    /// using the supplied scratch buffer for the projection step.
    /// </summary>
    public void Render(
        SKCanvas canvas,
        int viewportWidth,
        int viewportHeight,
        TerrainMesh3D mesh,
        Camera3D camera,
        Terrain3DFrameScratch scratch,
        IReadOnlyList<ProjectedTrail>? trails = null,
        ProjectedRoute? route = null,
        IReadOnlyList<ProjectedClimbingArea>? climbingAreas = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(scratch);

        DrawSky(canvas, viewportWidth, viewportHeight);

        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return;
        }

        ProjectedTerrainFrame frame = Terrain3DProjection.Project(mesh, camera, viewportWidth, viewportHeight, scratch);
        if (frame.VisibleIndexCount > 0)
        {
            DrawMesh(canvas, mesh, frame);
        }

        if (trails is not null)
        {
            DrawTrails(canvas, trails);
        }
        if (route is not null)
        {
            DrawRoute(canvas, route.Value);
        }
        if (climbingAreas is not null)
        {
            DrawClimbingAreas(canvas, climbingAreas);
        }
    }

    private void DrawMesh(SKCanvas canvas, TerrainMesh3D mesh, ProjectedTerrainFrame frame)
    {
        int vertexCount = frame.VertexCount;
        EnsureMeshBuffers(mesh, vertexCount);

        for (int i = 0; i < vertexCount; i++)
        {
            var v = frame.ScreenVertices[i];
            pointScratch[i] = float.IsNaN(v.X) ? SKPoint.Empty : new SKPoint(v.X, v.Y);
        }

        SKColor[] colors = cachedColors;

        // Positions and colors buffers are reused at full mesh size — indices reference
        // entries in [0, vertexCount) so any trailing slots are inert. Indices MUST be
        // exact length though: SKVertices walks every entry to form a triangle, so a
        // stale tail would render spurious geometry. Ping-pong between two slots so the
        // previous frame's array (still referenced by an in-flight SKVertices) can be
        // GC'd or reused without conflict.
        int needed = frame.VisibleIndexCount;
        ushort[] current = useBufferA ? indexBufferA : indexBufferB;
        if (current.Length != needed)
        {
            current = new ushort[needed];
            if (useBufferA) indexBufferA = current; else indexBufferB = current;
        }
        Array.Copy(frame.VisibleIndices, current, needed);
        useBufferA = !useBufferA;
        ushort[] visibleIndices = current;

        using var vertices = SKVertices.CreateCopy(
            SKVertexMode.Triangles,
            pointScratch,
            null,
            colors,
            visibleIndices);

        // For vertex-colour-only meshes we want the per-vertex colours to win.
        // Skia blends paint colour × vertex colour via the supplied mode. Modulate
        // with a white paint = identity (vertex colour unchanged), which is the
        // standard recipe and robust across SkiaSharp versions.
        meshPaint ??= new SKPaint { IsAntialias = true, Color = SKColors.White };
        canvas.DrawVertices(vertices, SKBlendMode.Modulate, meshPaint);
    }

    private void EnsureMeshBuffers(TerrainMesh3D mesh, int vertexCount)
    {
        // Mesh colors are computed once at mesh build time and never change.
        // Point + color arrays must match in length (SKVertices requirement), so we
        // size both to the mesh exactly and refresh only when a new DEM loads.
        if (ReferenceEquals(cachedMesh, mesh) && cachedColors.Length == vertexCount)
        {
            return;
        }

        var convertedColors = new SKColor[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            convertedColors[i] = new SKColor(mesh.Colors[i]);
        }
        cachedColors = convertedColors;
        pointScratch = new SKPoint[vertexCount];
        cachedMesh = mesh;
    }

    private void DrawTrails(SKCanvas canvas, IReadOnlyList<ProjectedTrail> trails)
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
            AppendPolylineToPath(path, projected.ScreenPoints);
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

    private static void AppendPolylineToPath(SKPath path, IReadOnlyList<System.Numerics.Vector3?> pts)
    {
        bool penDown = false;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            if (p is null)
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

    private void DrawRoute(SKCanvas canvas, ProjectedRoute route)
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
            canvas.DrawLine(a.Value.X, a.Value.Y, b.Value.X, b.Value.Y, routePaint);
        }
    }

    private void DrawClimbingAreas(SKCanvas canvas, IReadOnlyList<ProjectedClimbingArea> areas)
    {
        if (areas.Count == 0)
        {
            return;
        }

        climbingFillPaint ??= new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = ClimbingFillColor,
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
            canvas.DrawCircle(screen.Value.X, screen.Value.Y, ClimbingMarkerRadiusPx, climbingFillPaint);
            canvas.DrawCircle(screen.Value.X, screen.Value.Y, ClimbingMarkerRadiusPx, climbingOutlinePaint);
        }
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
        skyPaint = null;
        skyShader = null;
    }
}
