#if WINDOWS
using System.Numerics;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;

using Serilog;

using Silk.NET.OpenGLES;

namespace MapaTur.App.Platforms.Windows;

/// <summary>
/// Real GPU terrain renderer: draws the mesh tiles through OpenGL ES 3.0 (ANGLE) with a depth buffer, on
/// the context SkiaSharp's SKGLView makes current. The GPU does the vertex transform and the depth buffer
/// resolves occlusion, so there is no CPU per-vertex projection, no painter's sort and no tile culling —
/// the full-resolution terrain renders correctly from any angle. Per-tile GPU buffers are uploaded once
/// and cached; only the MVP uniform changes per frame.
/// </summary>
internal sealed unsafe class Terrain3DGlRenderer : IDisposable
{
    private const string VertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec3 aPos;\n" +
        "layout(location=1) in vec4 aColor;\n" +
        "uniform mat4 uMvp;\n" +
        "out vec4 vColor;\n" +
        "void main(){ vColor = aColor; gl_Position = uMvp * vec4(aPos, 1.0); }\n";

    private const string FragmentShaderSource =
        "#version 300 es\n" +
        "precision mediump float;\n" +
        "in vec4 vColor;\n" +
        "out vec4 fragColor;\n" +
        "void main(){ fragColor = vec4(vColor.rgb, 1.0); }\n";

    // Sky clear colour (matches the Skia renderer's lower gradient stop).
    private const float SkyR = 0x6C / 255f;
    private const float SkyG = 0x8E / 255f;
    private const float SkyB = 0xB0 / 255f;

    // Overlays are lifted above the surface so they sit on their own slope yet get occluded by mountains
    // in front of them via the depth test.
    private const float TrailLiftMeters = 6f;
    private const float RouteLiftMeters = 9f;

    private sealed class TileBuffers
    {
        public uint Vao;
        public uint PositionVbo;
        public uint ColorVbo;
        public uint Ebo;
        public int IndexCount;
    }

    // GL line geometry (GL_LINES, 32-bit indices) for trails / route, drawn depth-tested so the terrain
    // occludes them — the fix for overlays "showing through mountains" (GLES can't read depth back, so the
    // occlusion has to happen in the GL pipeline, not in a Skia post-pass).
    private sealed class LineBuffers
    {
        public uint Vao;
        public uint PositionVbo;
        public uint ColorVbo;
        public uint Ebo;
        public int IndexCount;
    }

    private GL? gl;
    private uint program;
    private int mvpLocation = -1;
    private bool programReady;

    private readonly Dictionary<TerrainMesh3D, TileBuffers> tileBuffers = new();
    private IReadOnlyList<TerrainMesh3D>? lastTiles;

    private LineBuffers? trailLines;
    private IReadOnlyList<Trail>? lastTrails;
    private DemRaster? lastTrailRaster;
    private TerrainMesh3D? lastTrailMesh;

    private LineBuffers? routeLines;
    private Route? lastRoute;
    private DemRaster? lastRouteRaster;
    private TerrainMesh3D? lastRouteMesh;

    /// <summary>Draws the terrain and the depth-tested trail/route overlays. Throws on GL/shader failure so the caller can fall back to Skia.</summary>
    public void Render(
        int width,
        int height,
        IReadOnlyList<TerrainMesh3D> tiles,
        Camera3D camera,
        uint framebuffer,
        IReadOnlyList<Trail>? trails,
        DemRaster? raster,
        Route? route)
    {
        gl ??= AngleGl.Get();

        // Render into the SAME framebuffer SkiaSharp presents. After a window resize SkiaSharp allocates a
        // new, non-zero FBO; drawing into the default framebuffer (0) would land off-screen — the symptom
        // being only the sky clear showing after maximising.
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);

        // Resizing the window (e.g. maximise) makes SKGLView recreate the GL context, which invalidates
        // every GPU object ID we cached (shader program, VAOs, VBOs). Detect that — the old program ID is
        // no longer a program in the fresh context — and rebuild from scratch (without deleting the stale
        // IDs, which belong to the dead context). Symptom of NOT handling this: only the sky clear shows.
        if (programReady && !gl.IsProgram(program))
        {
            Log.Information("[GL3D] context lost (program {Program} no longer valid) — rebuilding GPU objects", program);
            tileBuffers.Clear();
            lastTiles = null;
            trailLines = null;
            lastTrails = null;
            lastTrailRaster = null;
            lastTrailMesh = null;
            routeLines = null;
            lastRoute = null;
            lastRouteRaster = null;
            lastRouteMesh = null;
            programReady = false;
            mvpLocation = -1;
        }

        EnsureProgram(gl);

        if (!ReferenceEquals(lastTiles, tiles))
        {
            ReleaseTiles(gl);
            UploadTiles(gl, tiles);
            lastTiles = tiles;
        }

        // Take full ownership of the GL state we rely on. SkiaSharp shares this context and leaves its own
        // clip/raster state behind — notably it enables GL_STENCIL_TEST (and blend/scissor/colour-mask) for
        // its 2D clipping after a surface resize. glClear ignores the stencil test (so the sky still fills),
        // but our terrain draw would be stencil-rejected → only sky shows after maximising. Resetting these
        // each frame makes our render independent of whatever Skia left set.
        gl.Disable(EnableCap.StencilTest);
        gl.Disable(EnableCap.Blend);
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.CullFace); // depth test handles occlusion regardless of winding
        gl.ColorMask(true, true, true, true);
        gl.DepthMask(true);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Less);
        gl.DepthRange(0.0f, 1.0f);

        gl.Viewport(0, 0, (uint)Math.Max(1, width), (uint)Math.Max(1, height));
        gl.ClearColor(SkyR, SkyG, SkyB, 1f);
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        gl.UseProgram(program);

        Matrix4x4 mvp = camera.BuildViewProjection((float)width / Math.Max(1, height));
        // System.Numerics is row-vector/row-major; uploading its fields with transpose=false lets GL read
        // them column-major, i.e. transposed — exactly the column-vector matrix GLSL's uMvp*v expects, so
        // it matches Camera.ProjectToScreen used for the overlays.
        Span<float> m = stackalloc float[16]
        {
            mvp.M11, mvp.M12, mvp.M13, mvp.M14,
            mvp.M21, mvp.M22, mvp.M23, mvp.M24,
            mvp.M31, mvp.M32, mvp.M33, mvp.M34,
            mvp.M41, mvp.M42, mvp.M43, mvp.M44,
        };
        gl.UniformMatrix4(mvpLocation, 1, false, m);

        foreach (TileBuffers tile in tileBuffers.Values)
        {
            gl.BindVertexArray(tile.Vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)tile.IndexCount, DrawElementsType.UnsignedShort, (void*)0);
        }

        // Trails + route as depth-tested GL lines (occluded by the terrain). Same program / MVP / depth state.
        TerrainMesh3D frame = tiles[0];
        DrawTrailLines(gl, trails, raster, frame);
        DrawRouteLine(gl, route, raster, frame);

        gl.BindVertexArray(0);
    }

    private void EnsureProgram(GL g)
    {
        if (programReady)
        {
            return;
        }

        uint vs = CompileShader(g, ShaderType.VertexShader, VertexShaderSource);
        uint fs = CompileShader(g, ShaderType.FragmentShader, FragmentShaderSource);
        program = g.CreateProgram();
        g.AttachShader(program, vs);
        g.AttachShader(program, fs);
        g.LinkProgram(program);
        g.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = g.GetProgramInfoLog(program);
            throw new InvalidOperationException("Terrain shader link failed: " + log);
        }
        g.DetachShader(program, vs);
        g.DetachShader(program, fs);
        g.DeleteShader(vs);
        g.DeleteShader(fs);

        mvpLocation = g.GetUniformLocation(program, "uMvp");
        programReady = true;
    }

    private static uint CompileShader(GL g, ShaderType type, string source)
    {
        uint shader = g.CreateShader(type);
        g.ShaderSource(shader, source);
        g.CompileShader(shader);
        g.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            string log = g.GetShaderInfoLog(shader);
            g.DeleteShader(shader);
            throw new InvalidOperationException($"Terrain {type} compile failed: {log}");
        }
        return shader;
    }

    private void UploadTiles(GL g, IReadOnlyList<TerrainMesh3D> tiles)
    {
        foreach (TerrainMesh3D tile in tiles)
        {
            int vertexCount = tile.Vertices.Length;

            // Positions: tightly packed x,y,z floats.
            var positions = new float[vertexCount * 3];
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 v = tile.Vertices[i];
                positions[(i * 3) + 0] = v.X;
                positions[(i * 3) + 1] = v.Y;
                positions[(i * 3) + 2] = v.Z;
            }

            // Colours: explicit R,G,B,A bytes (the mesh stores 0xAARRGGBB; avoid endianness surprises).
            var colors = new byte[vertexCount * 4];
            for (int i = 0; i < vertexCount; i++)
            {
                uint argb = tile.Colors[i];
                colors[(i * 4) + 0] = (byte)((argb >> 16) & 0xFF);
                colors[(i * 4) + 1] = (byte)((argb >> 8) & 0xFF);
                colors[(i * 4) + 2] = (byte)(argb & 0xFF);
                colors[(i * 4) + 3] = (byte)((argb >> 24) & 0xFF);
            }

            ushort[] indices = tile.Indices;

            var buffers = new TileBuffers { IndexCount = indices.Length };
            buffers.Vao = g.GenVertexArray();
            g.BindVertexArray(buffers.Vao);

            buffers.PositionVbo = g.GenBuffer();
            g.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.PositionVbo);
            g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(positions.Length * sizeof(float)), positions, BufferUsageARB.StaticDraw);
            g.EnableVertexAttribArray(0);
            g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

            buffers.ColorVbo = g.GenBuffer();
            g.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.ColorVbo);
            g.BufferData<byte>(BufferTargetARB.ArrayBuffer, (nuint)colors.Length, colors, BufferUsageARB.StaticDraw);
            g.EnableVertexAttribArray(1);
            g.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, 4, (void*)0);

            buffers.Ebo = g.GenBuffer();
            g.BindBuffer(BufferTargetARB.ElementArrayBuffer, buffers.Ebo);
            g.BufferData<ushort>(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(ushort)), indices, BufferUsageARB.StaticDraw);

            g.BindVertexArray(0);
            tileBuffers[tile] = buffers;
        }
    }

    private void ReleaseTiles(GL g)
    {
        foreach (TileBuffers b in tileBuffers.Values)
        {
            g.DeleteBuffer(b.PositionVbo);
            g.DeleteBuffer(b.ColorVbo);
            g.DeleteBuffer(b.Ebo);
            g.DeleteVertexArray(b.Vao);
        }
        tileBuffers.Clear();
    }

    private void DrawTrailLines(GL g, IReadOnlyList<Trail>? trails, DemRaster? raster, TerrainMesh3D mesh)
    {
        if (trails is null || trails.Count == 0 || raster is null)
        {
            return;
        }

        if (trailLines is null
            || !ReferenceEquals(lastTrails, trails)
            || !ReferenceEquals(lastTrailRaster, raster)
            || !ReferenceEquals(lastTrailMesh, mesh))
        {
            DeleteLine(g, ref trailLines);
            IReadOnlyList<TrailWorldLine> world = Trail3DWorldProjection.ToWorld(trails, raster, mesh, TrailLiftMeters);

            var positions = new List<float>();
            var colors = new List<byte>();
            var indices = new List<uint>();
            foreach (TrailWorldLine line in world)
            {
                (byte r, byte gg, byte b) = PttkRgb(line.Source.PrimaryColor);
                AppendLine(line.World, r, gg, b, positions, colors, indices);
            }

            trailLines = UploadLine(g, positions, colors, indices);
            lastTrails = trails;
            lastTrailRaster = raster;
            lastTrailMesh = mesh;
        }

        DrawLine(g, trailLines);
    }

    private void DrawRouteLine(GL g, Route? route, DemRaster? raster, TerrainMesh3D mesh)
    {
        if (route is null || raster is null)
        {
            return;
        }

        if (routeLines is null
            || !ReferenceEquals(lastRoute, route)
            || !ReferenceEquals(lastRouteRaster, raster)
            || !ReferenceEquals(lastRouteMesh, mesh))
        {
            DeleteLine(g, ref routeLines);
            RouteWorldLine world = Route3DWorldProjection.ToWorld(route, raster, mesh, RouteLiftMeters);

            var positions = new List<float>();
            var colors = new List<byte>();
            var indices = new List<uint>();
            AppendLine(world.World, 0x7C, 0x3A, 0xED, positions, colors, indices); // violet, matches 2D planner

            routeLines = UploadLine(g, positions, colors, indices);
            lastRoute = route;
            lastRouteRaster = raster;
            lastRouteMesh = mesh;
        }

        DrawLine(g, routeLines);
    }

    // Appends one polyline's vertices + GL_LINES index pairs, skipping any segment touching an out-of-DEM
    // (NaN) vertex so the line breaks at the terrain edge instead of spanning the gap.
    private static void AppendLine(IReadOnlyList<Vector3> world, byte r, byte g, byte b, List<float> positions, List<byte> colors, List<uint> indices)
    {
        uint baseIndex = (uint)(positions.Count / 3);
        for (int i = 0; i < world.Count; i++)
        {
            Vector3 v = world[i];
            positions.Add(v.X);
            positions.Add(v.Y);
            positions.Add(v.Z);
            colors.Add(r);
            colors.Add(g);
            colors.Add(b);
            colors.Add(255);
        }

        for (int i = 0; i < world.Count - 1; i++)
        {
            if (!float.IsNaN(world[i].X) && !float.IsNaN(world[i + 1].X))
            {
                indices.Add(baseIndex + (uint)i);
                indices.Add(baseIndex + (uint)i + 1);
            }
        }
    }

    private LineBuffers? UploadLine(GL g, List<float> positions, List<byte> colors, List<uint> indices)
    {
        if (indices.Count == 0)
        {
            return null;
        }

        var buffers = new LineBuffers { IndexCount = indices.Count };
        buffers.Vao = g.GenVertexArray();
        g.BindVertexArray(buffers.Vao);

        float[] positionArray = positions.ToArray();
        buffers.PositionVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.PositionVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(positionArray.Length * sizeof(float)), positionArray, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

        byte[] colorArray = colors.ToArray();
        buffers.ColorVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.ColorVbo);
        g.BufferData<byte>(BufferTargetARB.ArrayBuffer, (nuint)colorArray.Length, colorArray, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, 4, (void*)0);

        uint[] indexArray = indices.ToArray();
        buffers.Ebo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, buffers.Ebo);
        g.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, (nuint)(indexArray.Length * sizeof(uint)), indexArray, BufferUsageARB.StaticDraw);

        g.BindVertexArray(0);
        return buffers;
    }

    private void DrawLine(GL g, LineBuffers? line)
    {
        if (line is null)
        {
            return;
        }

        g.LineWidth(2f); // ANGLE/D3D11 may clamp to 1px; correct occlusion matters more than thickness here
        g.BindVertexArray(line.Vao);
        g.DrawElements(PrimitiveType.Lines, (uint)line.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    private static void DeleteLine(GL g, ref LineBuffers? line)
    {
        if (line is null)
        {
            return;
        }

        g.DeleteBuffer(line.PositionVbo);
        g.DeleteBuffer(line.ColorVbo);
        g.DeleteBuffer(line.Ebo);
        g.DeleteVertexArray(line.Vao);
        line = null;
    }

    private static (byte R, byte G, byte B) PttkRgb(PttkColor color)
    {
        string hex = OsmcSymbolParser.ToHex(color);
        int start = hex.StartsWith('#') ? 1 : 0;
        if (hex.Length - start >= 6
            && byte.TryParse(hex.AsSpan(start, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r)
            && byte.TryParse(hex.AsSpan(start + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g)
            && byte.TryParse(hex.AsSpan(start + 4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
        {
            return (r, g, b);
        }
        return (0x94, 0xA3, 0xB8); // slate fallback (matches the Skia renderer)
    }

    public void Dispose()
    {
        if (gl is null)
        {
            return;
        }

        ReleaseTiles(gl);
        DeleteLine(gl, ref trailLines);
        DeleteLine(gl, ref routeLines);
        if (programReady)
        {
            gl.DeleteProgram(program);
            programReady = false;
        }
    }
}
#endif