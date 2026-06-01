// Cross-platform: every TFM where SkiaSharp's SKGLView exposes a live OpenGL ES context
// (Windows ANGLE, Android system GLES, iOS/Mac Catalyst OpenGLES.framework) shares this
// renderer. Library loading lives in PlatformGl so the renderer itself stays GL-only.
using System.Numerics;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;

using Serilog;

using Silk.NET.OpenGLES;

namespace MapaTur.App.Services;

/// <summary>
/// Real GPU terrain renderer: draws the mesh tiles through OpenGL ES 3.0 (ANGLE) with a depth buffer, on
/// the context SkiaSharp's SKGLView makes current. The GPU does the vertex transform and the depth buffer
/// resolves occlusion, so there is no CPU per-vertex projection, no painter's sort and no tile culling —
/// the full-resolution terrain renders correctly from any angle. Per-tile GPU buffers are uploaded once
/// and cached; only the MVP uniform changes per frame.
/// </summary>
internal sealed unsafe class Terrain3DGlRenderer : IDisposable
{
    // Terrain vertex shader: carries the UNSHADED base colour, world-space normal, UV and world-space
    // position to the fragment stage. Position is needed so the fragment can compute an exponential-fog
    // (aerial-perspective) blend against the camera position without re-deriving it from depth.
    private const string VertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec3 aPos;\n" +
        "layout(location=1) in vec4 aColor;\n" +
        "layout(location=2) in vec3 aNormal;\n" +
        "layout(location=3) in vec2 aTex;\n" +
        "uniform mat4 uMvp;\n" +
        "out vec4 vColor;\n" +
        "out vec3 vNormal;\n" +
        "out vec2 vTex;\n" +
        "out vec3 vWorldPos;\n" +
        "void main(){ vColor = aColor; vNormal = aNormal; vTex = aTex; vWorldPos = aPos; gl_Position = uMvp * vec4(aPos, 1.0); }\n";

    // Per-pixel Lambert lighting + exponential-fog aerial perspective. shade = ambient + (1-ambient) *
    // max(0, dot(N, L)). When an ortho image is bound (uUseOrtho=1) the surface colour is sampled from it
    // (with optional unsharp), otherwise the hypsometric base tint. After the surface colour is computed
    // it is blended toward uFogColor by an exponential function of view distance — distant ridges fade
    // into the horizon haze the way they do in golden-hour photos. uFogDensity=0 disables the blend.
    private const string TerrainFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "precision highp sampler2D;\n" +
        "in vec4 vColor;\n" +
        "in vec3 vNormal;\n" +
        "in vec2 vTex;\n" +
        "in vec3 vWorldPos;\n" +
        "uniform vec3 uLightDir;\n" +
        "uniform float uAmbient;\n" +
        "uniform sampler2D uOrtho;\n" +
        "uniform int uUseOrtho;\n" +
        "uniform vec2 uOrthoTexel;\n" + // (1/width, 1/height) of the bound ortho texture
        "uniform float uSharpen;\n" +   // unsharp-mask strength; 0 = off
        "uniform vec3 uFogColor;\n" +
        "uniform float uFogDensity;\n" + // per-metre exponential; 0 = no aerial perspective
        "uniform vec3 uCameraPos;\n" +
        "out vec4 fragColor;\n" +
        "void main(){\n" +
        "  float lambert = max(0.0, dot(normalize(vNormal), uLightDir));\n" +
        "  float shade = uAmbient + ((1.0 - uAmbient) * lambert);\n" +
        "  vec3 base;\n" +
        "  if (uUseOrtho == 1) {\n" +
        "    vec3 c = texture(uOrtho, vTex).rgb;\n" +
        "    if (uSharpen > 0.0) {\n" +
        // 4-tap unsharp mask: boost the centre over the local average to crisp up edges that
        // mipmap/anisotropic minification softens. Cheap (4 extra taps) and clamped to [0,1].
        "      vec3 blur = (texture(uOrtho, vTex + vec2(uOrthoTexel.x, 0.0)).rgb\n" +
        "                 + texture(uOrtho, vTex - vec2(uOrthoTexel.x, 0.0)).rgb\n" +
        "                 + texture(uOrtho, vTex + vec2(0.0, uOrthoTexel.y)).rgb\n" +
        "                 + texture(uOrtho, vTex - vec2(0.0, uOrthoTexel.y)).rgb) * 0.25;\n" +
        "      c = clamp(c + (uSharpen * (c - blur)), 0.0, 1.0);\n" +
        "    }\n" +
        "    base = c;\n" +
        "  } else {\n" +
        "    base = vColor.rgb;\n" +
        "  }\n" +
        "  vec3 lit = base * shade;\n" +
        "  float dist = length(vWorldPos - uCameraPos);\n" +
        "  float fogAmount = 1.0 - exp(-dist * uFogDensity);\n" +
        "  fragColor = vec4(mix(lit, uFogColor, fogAmount), 1.0);\n" +
        "}\n";

    // Sky pass: a fullscreen triangle whose fragment shader reconstructs a world-space view
    // direction from screen NDC via the inverse view-projection matrix, then evaluates a smoothly
    // mixed horizon-to-zenith gradient plus a sun disc with a Mie-style scattering halo around it.
    // Rendered FIRST each frame with depth-write disabled; the depth-tested terrain pass then
    // composites on top, leaving sky visible only where there's no geometry. This is what gives
    // the golden-hour "sun behind the ridge" look — the sun pokes out wherever the silhouette ends.
    private const string SkyVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec2 aClip;\n" + // clip-space xy in [-1,1]; one triangle covers the screen
        "out vec2 vClip;\n" +
        "void main(){ vClip = aClip; gl_Position = vec4(aClip, 1.0, 1.0); }\n";

    private const string SkyFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vClip;\n" +
        "uniform mat4 uInvViewProj;\n" +
        "uniform vec3 uCameraPos;\n" +
        "uniform vec3 uSunDir;\n" +
        "uniform vec3 uSunColor;\n" +
        "uniform vec3 uSkyZenith;\n" +
        "uniform vec3 uSkyHorizon;\n" +
        "uniform float uTime;\n" +          // seconds since renderer start; drives cloud drift
        "uniform float uCloudCoverage;\n" + // 0 = clear, 1 = overcast
        "out vec4 fragColor;\n" +
        // 2D value-noise + fractal Brownian motion. Hash-based, no texture lookups — costs ~5
        // sin() + ~40 lerps per cloud pixel. Adreno 830 chews through this without breaking a
        // sweat (sky pass is fullscreen but tiny pixel cost; <0.5 ms on a phone).
        "float hash21(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }\n" +
        "float noise2(vec2 p){\n" +
        "  vec2 i = floor(p); vec2 f = fract(p);\n" +
        "  f = f * f * (3.0 - 2.0 * f);\n" +
        "  return mix(\n" +
        "    mix(hash21(i + vec2(0.0, 0.0)), hash21(i + vec2(1.0, 0.0)), f.x),\n" +
        "    mix(hash21(i + vec2(0.0, 1.0)), hash21(i + vec2(1.0, 1.0)), f.x),\n" +
        "    f.y);\n" +
        "}\n" +
        "float fbm(vec2 p){\n" +
        "  float v = 0.0; float a = 0.5;\n" +
        "  for (int i = 0; i < 5; i++) { v += a * noise2(p); p *= 2.0; a *= 0.5; }\n" +
        "  return v;\n" +
        "}\n" +
        "void main(){\n" +
        // Unproject the screen NDC to a far-plane world point, then build a view direction
        // from camera to that point. invViewProj handles aspect/fov/orientation in one matrix.
        "  vec4 farPoint = uInvViewProj * vec4(vClip, 1.0, 1.0);\n" +
        "  vec3 viewDir = normalize((farPoint.xyz / farPoint.w) - uCameraPos);\n" +
        // WORLD-SPACE sky dome. Vertical tone follows the world-up component of the view ray
        // (viewDir.z), so the zenith is always world +Z and the dome stays anchored to the
        // world no matter how the camera orbits — clouds sit "above the terrain", not "at the
        // top of the screen". h in [-1,1]: +1 straight up, 0 at the horizon, -1 straight down.
        "  float h = viewDir.z;\n" +
        "  vec3 skyUp = mix(uSkyHorizon, uSkyZenith, pow(clamp(h, 0.0, 1.0), 0.45));\n" +
        // Below horizon (looking down past the terrain edge / into a top-down view's corners):
        // a darkened horizon tone reading as distant ground haze, NOT blue sky and NOT clouds.
        // Cross-faded across the horizon line so there's no hard seam.
        "  vec3 skyDown = uSkyHorizon * 0.72;\n" +
        "  vec3 sky = mix(skyDown, skyUp, smoothstep(-0.12, 0.06, h));\n" +
        // Cirrus on an INFINITE horizontal layer overhead: perspective-project the view ray
        // onto a constant-world-Z plane by dividing xy by the up component. Classic skybox
        // cloud trick — bands lock to world directions, pan correctly as the camera rotates,
        // and only appear in genuinely upward-looking pixels.
        "  float cloudDensity = 0.0;\n" +
        "  if (h > 0.015) {\n" +
        "    vec2 cloudUv = viewDir.xy / h;\n" +
        "    cloudUv = vec2(cloudUv.x * 0.5, cloudUv.y * 1.6) + uTime * vec2(0.012, 0.005);\n" +
        "    float clouds = noise2(cloudUv) * 0.6 + noise2(cloudUv * 2.3) * 0.4;\n" +
        "    float threshold = 0.60 - (uCloudCoverage * 0.32);\n" +
        "    cloudDensity = smoothstep(threshold, threshold + 0.16, clouds) * 0.8;\n" +
        // Fade clouds out near the horizon (h -> 0) where the overhead-plane projection
        // stretches to infinity and would smear into a hard band.
        "    cloudDensity *= smoothstep(0.015, 0.18, h);\n" +
        "  }\n" +
        // Cloud colour: noon -> bright lit-from-above white; sunset -> warm pink-orange built
        // from a boosted horizon tint; night -> dim cool blue-grey for silhouettes.
        "  vec3 cloudHot = clamp(uSkyHorizon * 2.0 + vec3(0.25, 0.10, 0.05), 0.0, 1.5);\n" +
        "  vec3 cloudBright = vec3(1.0, 0.99, 0.97);\n" +
        "  vec3 cloudNight = vec3(0.18, 0.20, 0.28);\n" +
        "  float sunHeight = clamp(uSunDir.z, 0.0, 1.0);\n" +
        "  float nightFactor = clamp(-uSunDir.z * 3.0, 0.0, 1.0);\n" +
        "  vec3 cloudColor = mix(cloudHot, cloudBright, sunHeight);\n" +
        "  cloudColor = mix(cloudColor, cloudNight, nightFactor);\n" +
        "  sky = mix(sky, cloudColor, cloudDensity);\n" +
        // Sun disc + halo. smoothstep gives a soft-edged disc the right pixel size; pow gives
        // the Mie-style fall-off (the "glow") that bleeds well past the disc.
        "  float sunDot = dot(viewDir, uSunDir);\n" +
        "  float sunCore = smoothstep(0.9994, 0.99985, sunDot);\n" +
        "  float sunHalo = pow(max(sunDot, 0.0), 80.0) * 0.55;\n" +
        "  vec3 sun = uSunColor * (sunCore + sunHalo);\n" +
        "  fragColor = vec4(sky + sun, 1.0);\n" +
        "}\n";

    // Flat fragment shader for the line/ribbon program (trails/route): no lighting, just the vertex colour.
    private const string FragmentShaderSource =
        "#version 300 es\n" +
        "precision mediump float;\n" +
        "in vec4 vColor;\n" +
        "out vec4 fragColor;\n" +
        "void main(){ fragColor = vec4(vColor.rgb, 1.0); }\n";

    // Line ribbon shader: expands each segment to a quad of constant SCREEN-pixel width (ANGLE/D3D11
    // can't do wide GL lines), so trails/route stay a few px thick at any zoom. Still depth-tested.
    private const string LineVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec3 aPos;\n" +
        "layout(location=1) in vec4 aColor;\n" +
        "layout(location=2) in vec3 aOther;\n" +
        "layout(location=3) in float aSide;\n" +
        "uniform mat4 uMvp;\n" +
        "uniform vec2 uViewport;\n" +
        "uniform float uHalfPx;\n" +
        "out vec4 vColor;\n" +
        "void main(){\n" +
        "  vColor = aColor;\n" +
        "  vec4 clipA = uMvp * vec4(aPos, 1.0);\n" +
        "  vec4 clipB = uMvp * vec4(aOther, 1.0);\n" +
        "  if (clipA.w <= 0.0) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }\n" +
        "  vec2 ndcA = clipA.xy / clipA.w;\n" +
        "  vec2 ndcB = clipB.w > 0.0 ? clipB.xy / clipB.w : ndcA;\n" +
        "  vec2 sA = ndcA * uViewport * 0.5;\n" +
        "  vec2 sB = ndcB * uViewport * 0.5;\n" +
        "  vec2 dir = sB - sA;\n" +
        "  float len = length(dir);\n" +
        "  vec2 nrm = len > 0.0001 ? vec2(-dir.y, dir.x) / len : vec2(0.0, 0.0);\n" +
        "  vec2 offNdc = (nrm * uHalfPx * aSide) / (uViewport * 0.5);\n" +
        "  gl_Position = clipA;\n" +
        "  gl_Position.xy += offNdc * clipA.w;\n" +
        "}\n";

    private const float TrailHalfWidthPx = 1.6f;
    private const float RouteHalfWidthPx = 2.6f;
    private const float RoadHalfWidthPx = 1.8f;

    // Road ribbon colour: light grey, matching the 2D road layer and distinct from the PTTK trail palette.
    private const byte RoadR = 0xE5;
    private const byte RoadG = 0xE7;
    private const byte RoadB = 0xEB;

    // Sky clear colour (matches the Skia renderer's lower gradient stop).
    private const float SkyR = 0x6C / 255f;
    private const float SkyG = 0x8E / 255f;
    private const float SkyB = 0xB0 / 255f;

    // Overlays are lifted above the surface so they sit on their own slope yet get occluded by mountains
    // in front of them via the depth test.
    private const float TrailLiftMeters = 6f;
    private const float RouteLiftMeters = 9f;
    private const float RoadLiftMeters = 4f;

    private sealed class TileBuffers
    {
        public uint Vao;
        public uint PositionVbo;
        public uint ColorVbo;
        public uint NormalVbo;
        public uint TexVbo;
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
        public uint OtherVbo;
        public uint SideVbo;
        public uint Ebo;
        public int IndexCount;
    }

    private GL? gl;
    private uint program;
    private int mvpLocation = -1;
    private int lightDirLocation = -1;
    private int ambientLocation = -1;
    private int orthoSamplerLocation = -1;
    private int useOrthoLocation = -1;
    private int orthoTexelLocation = -1;
    private int sharpenLocation = -1;
    private int terrainFogColorLocation = -1;
    private int terrainFogDensityLocation = -1;
    private int terrainCameraPosLocation = -1;

    // Sky / atmospheric program: drawn as a fullscreen triangle BEFORE the terrain pass, with the
    // depth-write disabled so the depth-tested terrain composes on top. Owns its own program +
    // single 6-float VBO (one triangle covering NDC).
    private uint skyProgram;
    private int skyInvViewProjLocation = -1;
    private int skyCameraPosLocation = -1;
    private int skySunDirLocation = -1;
    private int skySunColorLocation = -1;
    private int skyZenithLocation = -1;
    private int skyHorizonLocation = -1;
    private int skyTimeLocation = -1;
    private int skyCloudCoverageLocation = -1;
    private uint skyVao;
    private uint skyVbo;

    // Wall-clock seconds since the renderer was constructed; drives the cirrus drift in the sky
    // shader. Started lazily so a Disposed renderer doesn't leak the stopwatch into the next one.
    private readonly System.Diagnostics.Stopwatch atmosphereClock = System.Diagnostics.Stopwatch.StartNew();

    // Unsharp-mask strength applied to the ortho in the fragment shader (0 = off). Crisps up edges softened
    // by mipmap/anisotropic minification; kept mild so it doesn't ring.
    private const float OrthoSharpenStrength = 0.6f;

    // Optional ortho-photo textures draped over the terrain, one per mesh ortho-cell (indexed by
    // TerrainMesh3D.OrthoTileIndex). A single full-extent ortho is just a 1-element list. CPU bytes are
    // kept so textures survive a GL context loss. Uploaded lazily on the GL thread.
    private sealed class OrthoTile
    {
        public required byte[] Rgba;
        public int Width;
        public int Height;
        public uint Texture; // 0 until uploaded
    }
    private readonly List<OrthoTile> orthoTiles = new();
    // Old tiles whose GL textures still need deleting on the GL thread (set when textures are swapped).
    private readonly List<OrthoTile> pendingOrthoRelease = new();
    private bool orthoDirty;
    private uint lineProgram;
    private int lineMvpLocation = -1;
    private int lineViewportLocation = -1;
    private int lineHalfPxLocation = -1;
    private bool programReady;

    // Off-screen multisampled target. We render the terrain into our own MSAA colour+depth renderbuffers
    // and blit-resolve into a single-sampled colour TEXTURE that the caller hands to SkiaSharp as an
    // SKImage. That texture-based hand-off is what lets the same path work on Windows (where Skia hands
    // us an intermediate FBO) AND Android (where it hands us FBO 0 and re-paints over anything we draw
    // there). Degrades gracefully: if MSAA can't be set up we draw straight into the present FBO.
    private const int RequestedSamples = 4;
    private uint msaaFbo;
    private uint msaaColorRb;
    private uint msaaDepthRb;
    private int msaaWidth;
    private int msaaHeight;
    private int msaaSamples; // 0 = not yet probed
    private bool msaaUnsupported;

    // Single-sampled present target: a colour TEXTURE we own, attached to its own FBO together with a
    // depth renderbuffer. The colour texture is what the caller wraps into SKImage.FromTexture and draws
    // through Skia — sidestepping the FBO 0 collision on Android where Skia would otherwise re-paint over
    // our output. Depth RB is only used by the non-MSAA path (drawing directly into this FBO).
    private uint presentFbo;
    private uint presentColorTex;
    private uint presentDepthRb;
    private int presentWidth;
    private int presentHeight;
    private bool presentUnsupported;

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

    private LineBuffers? roadLines;
    private IReadOnlyList<Trail>? lastRoads;
    private DemRaster? lastRoadRaster;
    private TerrainMesh3D? lastRoadMesh;

    /// <summary>
    /// Sets (or clears, when <paramref name="rgba"/> is null) the ortho-photo texture draped over the terrain.
    /// <paramref name="rgba"/> is tightly-packed top-row-first RGBA8 (row 0 = north, matching the mesh UVs).
    /// The actual GL upload happens on the next <see cref="Render"/> call, on the GL thread.
    /// </summary>
    public void SetOrthoTexture(byte[]? rgba, int width, int height)
    {
        if (rgba is not null && width > 0 && height > 0)
        {
            SetOrthoTextures(new[] { (rgba, width, height) });
        }
        else
        {
            SetOrthoTextures(Array.Empty<(byte[], int, int)>());
        }
    }

    /// <summary>
    /// Sets the ortho textures, one per mesh ortho-cell (order = OrthoTileIndex). An empty list clears the
    /// ortho (terrain falls back to the hypsometric tint). Each entry is tightly-packed top-row-first RGBA8.
    /// Upload happens on the next <see cref="Render"/> call, on the GL thread.
    /// </summary>
    public void SetOrthoTextures(IReadOnlyList<(byte[] Rgba, int Width, int Height)> textures)
    {
        // GL handles from the previous set are deleted on the next EnsureOrthoTextures (no context here).
        pendingOrthoRelease.AddRange(orthoTiles);
        orthoTiles.Clear();
        foreach (var (rgba, w, h) in textures)
        {
            if (rgba is not null && w > 0 && h > 0)
            {
                orthoTiles.Add(new OrthoTile { Rgba = rgba, Width = w, Height = h });
            }
        }
        orthoDirty = true;
    }

    /// <summary>
    /// Draws the terrain and the depth-tested trail/route overlays into an owned GL colour texture, and
    /// returns the texture handle so the caller can compose it through Skia (<see cref="SkiaSharp.SKImage.FromTexture(SkiaSharp.GRContext, SkiaSharp.GRBackendTexture, SkiaSharp.GRSurfaceOrigin, SkiaSharp.SKColorType, SkiaSharp.SKAlphaType)"/>).
    /// Returns 0 if a present target couldn't be allocated. Throws on GL/shader failure so the caller can
    /// fall back to Skia.
    /// </summary>
    public uint Render(
        int width,
        int height,
        IReadOnlyList<TerrainMesh3D> tiles,
        Camera3D camera,
        IReadOnlyList<Trail>? trails,
        DemRaster? raster,
        Route? route,
        IReadOnlyList<Trail>? roads = null,
        Atmosphere? atmosphere = null)
    {
        gl ??= PlatformGl.Get();

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
            roadLines = null;
            lastRoads = null;
            lastRoadRaster = null;
            lastRoadMesh = null;
            programReady = false;
            mvpLocation = -1;
            lightDirLocation = -1;
            ambientLocation = -1;
            orthoSamplerLocation = -1;
            useOrthoLocation = -1;
            orthoTexelLocation = -1;
            sharpenLocation = -1;
            terrainFogColorLocation = -1;
            terrainFogDensityLocation = -1;
            terrainCameraPosLocation = -1;
            // Sky program + fullscreen triangle VAO belonged to the dead context too.
            skyProgram = 0;
            skyInvViewProjLocation = -1;
            skyCameraPosLocation = -1;
            skySunDirLocation = -1;
            skySunColorLocation = -1;
            skyZenithLocation = -1;
            skyHorizonLocation = -1;
            skyTimeLocation = -1;
            skyCloudCoverageLocation = -1;
            skyVao = 0;
            skyVbo = 0;
            // The ortho texture IDs belonged to the dead context; drop the handles (don't GL-delete the
            // stale ones) but keep the CPU bytes so they re-upload on the next EnsureOrthoTextures.
            pendingOrthoRelease.Clear();
            foreach (OrthoTile t in orthoTiles)
            {
                t.Texture = 0;
            }
            if (orthoTiles.Count > 0)
            {
                orthoDirty = true;
            }
            // The MSAA renderbuffers/FBO belonged to the dead context; drop the cached IDs and re-probe.
            msaaFbo = 0;
            msaaColorRb = 0;
            msaaDepthRb = 0;
            msaaWidth = 0;
            msaaHeight = 0;
            msaaSamples = 0;
            msaaUnsupported = false;
            // Same story for the present FBO / colour texture / depth RB — drop the stale IDs.
            presentFbo = 0;
            presentColorTex = 0;
            presentDepthRb = 0;
            presentWidth = 0;
            presentHeight = 0;
            presentUnsupported = false;
        }

        EnsureProgram(gl);

        if (!ReferenceEquals(lastTiles, tiles))
        {
            ReleaseTiles(gl);
            UploadTiles(gl, tiles);
            lastTiles = tiles;
        }

        EnsureOrthoTextures(gl);

        int vpWidth = Math.Max(1, width);
        int vpHeight = Math.Max(1, height);

        // Present target (colour texture + depth RB) is mandatory: that's the texture the caller wraps as an
        // SKImage. If it can't be allocated, bail — the Skia fallback will paint instead.
        if (!EnsurePresentTarget(gl, vpWidth, vpHeight))
        {
            return 0;
        }

        // Render into our multisampled FBO when available (anti-aliased terrain edges), else straight into the
        // present FBO (which has its own depth RB). We never bind FBO 0 here: on Android that *is* the on-screen
        // surface and Skia's compositor would paint over anything we drew there.
        bool useMsaa = EnsureMsaaTarget(gl, vpWidth, vpHeight);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, useMsaa ? msaaFbo : presentFbo);

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

        gl.Viewport(0, 0, (uint)vpWidth, (uint)vpHeight);
        // Sky clear is a safety floor in case the sky pass is skipped (no atmosphere set) — the
        // atmospheric pass paints over the whole frame anyway, so the colour is irrelevant when
        // atmosphere != null. Always clear depth so the test stays sane between frames.
        gl.ClearColor(SkyR, SkyG, SkyB, 1f);
        gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        Matrix4x4 mvp = camera.BuildViewProjection((float)width / Math.Max(1, height));

        // Sky pass FIRST: fullscreen triangle, no depth write, no depth test — the depth-tested
        // terrain pass that follows composites on top, so the sky shows through wherever there's
        // no geometry. Skipping the pass when atmosphere is null preserves the legacy flat clear.
        if (atmosphere is not null)
        {
            Matrix4x4.Invert(mvp, out Matrix4x4 invMvp);
            Span<float> invMvpData = stackalloc float[16]
            {
                invMvp.M11, invMvp.M12, invMvp.M13, invMvp.M14,
                invMvp.M21, invMvp.M22, invMvp.M23, invMvp.M24,
                invMvp.M31, invMvp.M32, invMvp.M33, invMvp.M34,
                invMvp.M41, invMvp.M42, invMvp.M43, invMvp.M44,
            };
            gl.DepthMask(false);
            gl.Disable(EnableCap.DepthTest);
            gl.UseProgram(skyProgram);
            gl.UniformMatrix4(skyInvViewProjLocation, 1, false, invMvpData);
            Vector3 camPos = camera.Position;
            gl.Uniform3(skyCameraPosLocation, camPos.X, camPos.Y, camPos.Z);
            Vector3 sunDir = atmosphere.SunDirection;
            gl.Uniform3(skySunDirLocation, sunDir.X, sunDir.Y, sunDir.Z);
            Vector3 sunColor = atmosphere.SunColor;
            gl.Uniform3(skySunColorLocation, sunColor.X, sunColor.Y, sunColor.Z);
            Vector3 zen = atmosphere.SkyZenithColor;
            gl.Uniform3(skyZenithLocation, zen.X, zen.Y, zen.Z);
            Vector3 hor = atmosphere.SkyHorizonColor;
            gl.Uniform3(skyHorizonLocation, hor.X, hor.Y, hor.Z);
            gl.Uniform1(skyTimeLocation, (float)atmosphereClock.Elapsed.TotalSeconds);
            gl.Uniform1(skyCloudCoverageLocation, atmosphere.CloudCoverage);
            gl.BindVertexArray(skyVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.BindVertexArray(0);
            // Restore depth state for the terrain pass.
            gl.Enable(EnableCap.DepthTest);
            gl.DepthMask(true);
        }

        gl.UseProgram(program);
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

        // Per-pixel lighting: the Atmosphere instance, when provided, overrides the per-tile baked
        // light direction + ambient so the time-of-day slider drives shading live. Without an
        // atmosphere the renderer falls back to the mesh-bake values (legacy behaviour).
        TerrainMesh3D lightFrame = tiles[0];
        Vector3 light = atmosphere?.SunDirection ?? lightFrame.LightDirection;
        float ambient = atmosphere?.AmbientFactor ?? lightFrame.AmbientFactor;
        gl.Uniform3(lightDirLocation, light.X, light.Y, light.Z);
        gl.Uniform1(ambientLocation, ambient);

        // Aerial perspective: when the atmosphere is bound, distant fragments blend toward
        // uFogColor with an exponential ramp. uFogDensity = 0 disables the blend (legacy path).
        Vector3 fogColor = atmosphere?.FogColor ?? Vector3.Zero;
        float fogDensity = atmosphere?.FogDensity ?? 0f;
        Vector3 cameraWorldPos = camera.Position;
        gl.Uniform3(terrainFogColorLocation, fogColor.X, fogColor.Y, fogColor.Z);
        gl.Uniform1(terrainFogDensityLocation, fogDensity);
        gl.Uniform3(terrainCameraPosLocation, cameraWorldPos.X, cameraWorldPos.Y, cameraWorldPos.Z);

        // Drape the ortho: bind each mesh tile's own cell texture (OrthoTileIndex) so a multi-cell ortho
        // stays sharp. Without textures the shader uses the hypsometric tint.
        bool anyOrtho = orthoTiles.Count > 0;
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.Uniform1(orthoSamplerLocation, 0);
        uint boundTexture = 0;
        foreach (KeyValuePair<TerrainMesh3D, TileBuffers> entry in tileBuffers)
        {
            TileBuffers tile = entry.Value;
            OrthoTile? ot = null;
            if (anyOrtho)
            {
                int idx = entry.Key.OrthoTileIndex;
                if ((uint)idx < (uint)orthoTiles.Count && orthoTiles[idx].Texture != 0)
                {
                    ot = orthoTiles[idx];
                }
            }

            if (ot is not null)
            {
                if (ot.Texture != boundTexture)
                {
                    gl.BindTexture(TextureTarget.Texture2D, ot.Texture);
                    boundTexture = ot.Texture;
                }
                gl.Uniform2(orthoTexelLocation, ot.Width > 0 ? 1f / ot.Width : 0f, ot.Height > 0 ? 1f / ot.Height : 0f);
                gl.Uniform1(sharpenLocation, OrthoSharpenStrength);
                gl.Uniform1(useOrthoLocation, 1);
            }
            else
            {
                gl.Uniform1(useOrthoLocation, 0);
            }

            gl.BindVertexArray(tile.Vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)tile.IndexCount, DrawElementsType.UnsignedShort, (void*)0);
        }

        // Trails + route as depth-tested screen-space ribbons (occluded by the terrain). Switch to the line
        // program; it shares the depth state and the same MVP, plus the viewport for the pixel expansion.
        gl.UseProgram(lineProgram);
        gl.UniformMatrix4(lineMvpLocation, 1, false, m);
        gl.Uniform2(lineViewportLocation, (float)Math.Max(1, width), (float)Math.Max(1, height));
        TerrainMesh3D frame = tiles[0];
        DrawRoadLines(gl, roads, raster, frame);
        DrawTrailLines(gl, trails, raster, frame);
        DrawRouteLine(gl, route, raster, frame);

        gl.BindVertexArray(0);

        if (useMsaa)
        {
            // Resolve the multisampled colour into our present FBO's colour texture. That texture is what
            // the caller hands to SkiaSharp as an SKImage; Skia then composes it into its surface during
            // its own draw pass — no FBO 0 collision on Android, no special-cased "intermediate FBO" on
            // Windows, same code path everywhere.
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, msaaFbo);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, presentFbo);
            gl.BlitFramebuffer(
                0, 0, vpWidth, vpHeight,
                0, 0, vpWidth, vpHeight,
                (uint)ClearBufferMask.ColorBufferBit,
                BlitFramebufferFilter.Nearest);
        }

        // Unbind everything before returning. The caller will re-establish whatever framebuffer Skia
        // expects (via GRContext.ResetContext) before sampling the texture we just produced.
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return presentColorTex;
    }

    /// <summary>
    /// Creates / resizes the single-sampled colour-texture FBO we return to the caller. Returns false (and
    /// sets <see cref="presentUnsupported"/> for the session) when the framebuffer is incomplete — the
    /// caller then falls back to Skia. Texture is RGBA8, linear filtering, clamp-to-edge — matching what
    /// SkiaSharp expects when wrapping it as an SKImage.
    /// </summary>
    private bool EnsurePresentTarget(GL g, int width, int height)
    {
        if (presentUnsupported)
        {
            return false;
        }

        if (presentFbo != 0 && presentWidth == width && presentHeight == height)
        {
            return true;
        }

        // (Re)allocate for the new size. Deleting 0 is a no-op so this also handles first-time creation.
        g.DeleteFramebuffer(presentFbo);
        g.DeleteTexture(presentColorTex);
        g.DeleteRenderbuffer(presentDepthRb);

        presentColorTex = g.GenTexture();
        g.BindTexture(TextureTarget.Texture2D, presentColorTex);
        g.TexImage2D(
            TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
            (uint)width, (uint)height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        g.BindTexture(TextureTarget.Texture2D, 0);

        // Depth RB only used by the non-MSAA path (when we draw straight into presentFbo). Allocating it
        // unconditionally keeps the FBO shape stable and is cheap on modern mobile GPUs.
        presentDepthRb = g.GenRenderbuffer();
        g.BindRenderbuffer(RenderbufferTarget.Renderbuffer, presentDepthRb);
        g.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)width, (uint)height);
        g.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

        presentFbo = g.GenFramebuffer();
        g.BindFramebuffer(FramebufferTarget.Framebuffer, presentFbo);
        g.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, presentColorTex, 0);
        g.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, presentDepthRb);

        GLEnum status = g.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        g.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            Log.Information("[GL3D] present framebuffer incomplete ({Status}) — falling back to Skia", status);
            g.DeleteFramebuffer(presentFbo);
            g.DeleteTexture(presentColorTex);
            g.DeleteRenderbuffer(presentDepthRb);
            presentFbo = 0;
            presentColorTex = 0;
            presentDepthRb = 0;
            presentUnsupported = true;
            return false;
        }

        presentWidth = width;
        presentHeight = height;
        return true;
    }

    /// <summary>
    /// Creates / resizes the off-screen multisampled colour+depth FBO. Returns false (and leaves nothing
    /// bound to change) when MSAA isn't usable, so the caller renders directly into Skia's FBO instead.
    /// </summary>
    private bool EnsureMsaaTarget(GL g, int width, int height)
    {
        if (msaaUnsupported)
        {
            return false;
        }

        if (msaaSamples == 0)
        {
            Span<int> maxSamplesQuery = stackalloc int[1];
            g.GetInteger(GLEnum.MaxSamples, maxSamplesQuery);
            int maxSamples = maxSamplesQuery[0];
            msaaSamples = Math.Clamp(RequestedSamples, 1, Math.Max(1, maxSamples));
            if (msaaSamples < 2)
            {
                msaaUnsupported = true;
                return false;
            }
        }

        if (msaaFbo != 0 && msaaWidth == width && msaaHeight == height)
        {
            return true;
        }

        // (Re)allocate for the new size. Deleting 0 is a no-op, so this also handles first-time creation.
        g.DeleteFramebuffer(msaaFbo);
        g.DeleteRenderbuffer(msaaColorRb);
        g.DeleteRenderbuffer(msaaDepthRb);

        msaaColorRb = g.GenRenderbuffer();
        g.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msaaColorRb);
        g.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)msaaSamples, InternalFormat.Rgba8, (uint)width, (uint)height);

        msaaDepthRb = g.GenRenderbuffer();
        g.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msaaDepthRb);
        g.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)msaaSamples, InternalFormat.DepthComponent24, (uint)width, (uint)height);

        msaaFbo = g.GenFramebuffer();
        g.BindFramebuffer(FramebufferTarget.Framebuffer, msaaFbo);
        g.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, msaaColorRb);
        g.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, msaaDepthRb);

        GLEnum status = g.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        g.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            Log.Information("[GL3D] MSAA framebuffer incomplete ({Status}) — falling back to non-AA terrain", status);
            g.DeleteFramebuffer(msaaFbo);
            g.DeleteRenderbuffer(msaaColorRb);
            g.DeleteRenderbuffer(msaaDepthRb);
            msaaFbo = 0;
            msaaColorRb = 0;
            msaaDepthRb = 0;
            msaaUnsupported = true;
            return false;
        }

        msaaWidth = width;
        msaaHeight = height;
        return true;
    }

    /// <summary>Reclaims swapped-out textures and uploads any not-yet-uploaded ortho cells (GL thread).</summary>
    private void EnsureOrthoTextures(GL g)
    {
        // Always reclaim handles from a previous texture set, even if nothing new is pending.
        if (pendingOrthoRelease.Count > 0)
        {
            foreach (OrthoTile old in pendingOrthoRelease)
            {
                if (old.Texture != 0)
                {
                    g.DeleteTexture(old.Texture);
                    old.Texture = 0;
                }
            }
            pendingOrthoRelease.Clear();
        }

        if (!orthoDirty)
        {
            return;
        }
        orthoDirty = false;

        // Upload beyond GL_MAX_TEXTURE_SIZE yields a garbage/black texture, so guard the size once.
        Span<int> maxTexSize = stackalloc int[1] { 2048 };
        g.GetInteger(GLEnum.MaxTextureSize, maxTexSize);
        int maxSize = maxTexSize[0];

        // Query the driver's max anisotropy once, outside the upload loop (a per-iteration stackalloc would
        // risk a stack overflow — CA2014).
        const GLEnum maxAnisotropyPName = (GLEnum)0x84FF; // GL_MAX_TEXTURE_MAX_ANISOTROPY_EXT
        const GLEnum anisotropyPName = (GLEnum)0x84FE;    // GL_TEXTURE_MAX_ANISOTROPY_EXT
        Span<float> maxAniso = stackalloc float[1] { 1f };
        g.GetFloat(maxAnisotropyPName, maxAniso);
        float aniso = Math.Clamp(16f, 1f, maxAniso[0] < 1f ? 1f : maxAniso[0]);

        foreach (OrthoTile tile in orthoTiles)
        {
            if (tile.Texture != 0)
            {
                continue; // already uploaded
            }
            if (tile.Width > maxSize || tile.Height > maxSize)
            {
                Log.Information("[GL3D] ortho tile {W}x{H} exceeds GL_MAX_TEXTURE_SIZE {Max}; skipping",
                    tile.Width, tile.Height, maxSize);
                continue;
            }

            tile.Texture = g.GenTexture();
            g.BindTexture(TextureTarget.Texture2D, tile.Texture);
            g.TexImage2D<byte>(
                TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
                (uint)tile.Width, (uint)tile.Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, tile.Rgba);

            // Trilinear (mipmapped) minification + anisotropy — the ortho is seen at grazing angles where
            // plain bilinear shimmers and smears into blocks. ClampToEdge so adjacent cell textures meet
            // seamlessly at the shared seam.
            g.GenerateMipmap(TextureTarget.Texture2D);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            g.TexParameter(TextureTarget.Texture2D, (TextureParameterName)anisotropyPName, aniso);
        }

        g.BindTexture(TextureTarget.Texture2D, 0);
    }

    private void EnsureProgram(GL g)
    {
        if (programReady)
        {
            return;
        }

        uint vs = CompileShader(g, ShaderType.VertexShader, VertexShaderSource);
        uint fs = CompileShader(g, ShaderType.FragmentShader, TerrainFragmentShaderSource);
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
        mvpLocation = g.GetUniformLocation(program, "uMvp");
        lightDirLocation = g.GetUniformLocation(program, "uLightDir");
        ambientLocation = g.GetUniformLocation(program, "uAmbient");
        orthoSamplerLocation = g.GetUniformLocation(program, "uOrtho");
        useOrthoLocation = g.GetUniformLocation(program, "uUseOrtho");
        orthoTexelLocation = g.GetUniformLocation(program, "uOrthoTexel");
        sharpenLocation = g.GetUniformLocation(program, "uSharpen");
        terrainFogColorLocation = g.GetUniformLocation(program, "uFogColor");
        terrainFogDensityLocation = g.GetUniformLocation(program, "uFogDensity");
        terrainCameraPosLocation = g.GetUniformLocation(program, "uCameraPos");

        // Sky program — single triangle covering the screen, fragment-shader-only atmospheric model.
        uint sks = CompileShader(g, ShaderType.VertexShader, SkyVertexShaderSource);
        uint skf = CompileShader(g, ShaderType.FragmentShader, SkyFragmentShaderSource);
        skyProgram = g.CreateProgram();
        g.AttachShader(skyProgram, sks);
        g.AttachShader(skyProgram, skf);
        g.LinkProgram(skyProgram);
        g.GetProgram(skyProgram, ProgramPropertyARB.LinkStatus, out int skyLinked);
        if (skyLinked == 0)
        {
            string log = g.GetProgramInfoLog(skyProgram);
            throw new InvalidOperationException("Sky shader link failed: " + log);
        }
        g.DetachShader(skyProgram, sks);
        g.DetachShader(skyProgram, skf);
        g.DeleteShader(sks);
        g.DeleteShader(skf);
        skyInvViewProjLocation = g.GetUniformLocation(skyProgram, "uInvViewProj");
        skyCameraPosLocation = g.GetUniformLocation(skyProgram, "uCameraPos");
        skySunDirLocation = g.GetUniformLocation(skyProgram, "uSunDir");
        skySunColorLocation = g.GetUniformLocation(skyProgram, "uSunColor");
        skyZenithLocation = g.GetUniformLocation(skyProgram, "uSkyZenith");
        skyHorizonLocation = g.GetUniformLocation(skyProgram, "uSkyHorizon");
        skyTimeLocation = g.GetUniformLocation(skyProgram, "uTime");
        skyCloudCoverageLocation = g.GetUniformLocation(skyProgram, "uCloudCoverage");

        // Fullscreen triangle: 3 vertices, each xy in clip space, covering NDC [-1,1]^2 with one extra
        // vertex outside the rect so the rasteriser fills the full screen without re-clipping a quad.
        Span<float> tri = stackalloc float[6] { -1f, -1f,  3f, -1f,  -1f,  3f };
        skyVao = g.GenVertexArray();
        g.BindVertexArray(skyVao);
        skyVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, skyVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(tri.Length * sizeof(float)), tri, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        g.BindVertexArray(0);

        // Line ribbon program (reuses the same fragment shader).
        uint lvs = CompileShader(g, ShaderType.VertexShader, LineVertexShaderSource);
        uint lfs = CompileShader(g, ShaderType.FragmentShader, FragmentShaderSource);
        lineProgram = g.CreateProgram();
        g.AttachShader(lineProgram, lvs);
        g.AttachShader(lineProgram, lfs);
        g.LinkProgram(lineProgram);
        g.GetProgram(lineProgram, ProgramPropertyARB.LinkStatus, out int lineLinked);
        if (lineLinked == 0)
        {
            string log = g.GetProgramInfoLog(lineProgram);
            throw new InvalidOperationException("Line shader link failed: " + log);
        }
        g.DetachShader(lineProgram, lvs);
        g.DetachShader(lineProgram, lfs);
        lineMvpLocation = g.GetUniformLocation(lineProgram, "uMvp");
        lineViewportLocation = g.GetUniformLocation(lineProgram, "uViewport");
        lineHalfPxLocation = g.GetUniformLocation(lineProgram, "uHalfPx");

        g.DeleteShader(vs);
        g.DeleteShader(fs);
        g.DeleteShader(lvs);
        g.DeleteShader(lfs);

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

            // Colours: the UNSHADED base tint as explicit R,G,B,A bytes (the mesh stores 0xAARRGGBB; avoid
            // endianness surprises). The fragment shader applies Lambert shading per pixel from the normal.
            var colors = new byte[vertexCount * 4];
            for (int i = 0; i < vertexCount; i++)
            {
                uint argb = tile.BaseColors[i];
                colors[(i * 4) + 0] = (byte)((argb >> 16) & 0xFF);
                colors[(i * 4) + 1] = (byte)((argb >> 8) & 0xFF);
                colors[(i * 4) + 2] = (byte)(argb & 0xFF);
                colors[(i * 4) + 3] = (byte)((argb >> 24) & 0xFF);
            }

            // Normals: tightly packed x,y,z floats in the mesh's world frame (X east, Y north, Z up).
            var normals = new float[vertexCount * 3];
            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 n = tile.Normals[i];
                normals[(i * 3) + 0] = n.X;
                normals[(i * 3) + 1] = n.Y;
                normals[(i * 3) + 2] = n.Z;
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

            buffers.NormalVbo = g.GenBuffer();
            g.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.NormalVbo);
            g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(normals.Length * sizeof(float)), normals, BufferUsageARB.StaticDraw);
            g.EnableVertexAttribArray(2);
            g.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

            float[] texCoords = tile.TexCoords;
            buffers.TexVbo = g.GenBuffer();
            g.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.TexVbo);
            g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(texCoords.Length * sizeof(float)), texCoords, BufferUsageARB.StaticDraw);
            g.EnableVertexAttribArray(3);
            g.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

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
            g.DeleteBuffer(b.NormalVbo);
            g.DeleteBuffer(b.TexVbo);
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

            var ribbon = new RibbonBuilder();
            foreach (TrailWorldLine line in world)
            {
                (byte r, byte gg, byte b) = PttkRgb(line.Source.PrimaryColor);
                ribbon.Append(line.World, r, gg, b);
            }

            trailLines = UploadLine(g, ribbon);
            lastTrails = trails;
            lastTrailRaster = raster;
            lastTrailMesh = mesh;
        }

        DrawLine(g, trailLines, TrailHalfWidthPx);
    }

    private void DrawRoadLines(GL g, IReadOnlyList<Trail>? roads, DemRaster? raster, TerrainMesh3D mesh)
    {
        if (roads is null || roads.Count == 0 || raster is null)
        {
            return;
        }

        if (roadLines is null
            || !ReferenceEquals(lastRoads, roads)
            || !ReferenceEquals(lastRoadRaster, raster)
            || !ReferenceEquals(lastRoadMesh, mesh))
        {
            DeleteLine(g, ref roadLines);
            // Roads are unmarked Trail polylines; reuse the trail world projection, draw them all one grey.
            IReadOnlyList<TrailWorldLine> world = Trail3DWorldProjection.ToWorld(roads, raster, mesh, RoadLiftMeters);

            var ribbon = new RibbonBuilder();
            foreach (TrailWorldLine line in world)
            {
                ribbon.Append(line.World, RoadR, RoadG, RoadB);
            }

            roadLines = UploadLine(g, ribbon);
            lastRoads = roads;
            lastRoadRaster = raster;
            lastRoadMesh = mesh;
        }

        DrawLine(g, roadLines, RoadHalfWidthPx);
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

            var ribbon = new RibbonBuilder();
            ribbon.Append(world.World, 0x7C, 0x3A, 0xED); // violet, matches 2D planner

            routeLines = UploadLine(g, ribbon);
            lastRoute = route;
            lastRouteRaster = raster;
            lastRouteMesh = mesh;
        }

        DrawLine(g, routeLines, RouteHalfWidthPx);
    }

    // Builds screen-space ribbon geometry: each polyline segment becomes a 4-vertex quad (2 triangles).
    // Each vertex carries its own position, the segment's OTHER endpoint and a ±1 side; the line shader
    // offsets it perpendicular to the on-screen segment by the pixel half-width. Segments touching an
    // out-of-DEM (NaN) vertex are skipped so the ribbon breaks at the terrain edge.
    private sealed class RibbonBuilder
    {
        public readonly List<float> Positions = new();
        public readonly List<byte> Colors = new();
        public readonly List<float> Others = new();
        public readonly List<float> Sides = new();
        public readonly List<uint> Indices = new();

        public void Append(IReadOnlyList<Vector3> world, byte r, byte g, byte b)
        {
            for (int i = 0; i < world.Count - 1; i++)
            {
                Vector3 a = world[i];
                Vector3 c = world[i + 1];
                if (float.IsNaN(a.X) || float.IsNaN(c.X))
                {
                    continue;
                }

                uint v = (uint)(Positions.Count / 3);
                AddVertex(a, c, +1f, r, g, b);
                AddVertex(a, c, -1f, r, g, b);
                AddVertex(c, a, -1f, r, g, b);
                AddVertex(c, a, +1f, r, g, b);
                Indices.Add(v + 0);
                Indices.Add(v + 1);
                Indices.Add(v + 2);
                Indices.Add(v + 2);
                Indices.Add(v + 1);
                Indices.Add(v + 3);
            }
        }

        private void AddVertex(Vector3 pos, Vector3 other, float side, byte r, byte g, byte b)
        {
            Positions.Add(pos.X);
            Positions.Add(pos.Y);
            Positions.Add(pos.Z);
            Others.Add(other.X);
            Others.Add(other.Y);
            Others.Add(other.Z);
            Sides.Add(side);
            Colors.Add(r);
            Colors.Add(g);
            Colors.Add(b);
            Colors.Add(255);
        }
    }

    private LineBuffers? UploadLine(GL g, RibbonBuilder ribbon)
    {
        if (ribbon.Indices.Count == 0)
        {
            return null;
        }

        var buffers = new LineBuffers { IndexCount = ribbon.Indices.Count };
        buffers.Vao = g.GenVertexArray();
        g.BindVertexArray(buffers.Vao);

        float[] positions = ribbon.Positions.ToArray();
        buffers.PositionVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.PositionVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(positions.Length * sizeof(float)), positions, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

        byte[] colors = ribbon.Colors.ToArray();
        buffers.ColorVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.ColorVbo);
        g.BufferData<byte>(BufferTargetARB.ArrayBuffer, (nuint)colors.Length, colors, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, 4, (void*)0);

        float[] others = ribbon.Others.ToArray();
        buffers.OtherVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.OtherVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(others.Length * sizeof(float)), others, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(2);
        g.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

        float[] sides = ribbon.Sides.ToArray();
        buffers.SideVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.SideVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(sides.Length * sizeof(float)), sides, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(3);
        g.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, sizeof(float), (void*)0);

        uint[] indices = ribbon.Indices.ToArray();
        buffers.Ebo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, buffers.Ebo);
        g.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), indices, BufferUsageARB.StaticDraw);

        g.BindVertexArray(0);
        return buffers;
    }

    private void DrawLine(GL g, LineBuffers? line, float halfWidthPx)
    {
        if (line is null)
        {
            return;
        }

        g.Uniform1(lineHalfPxLocation, halfWidthPx);
        g.BindVertexArray(line.Vao);
        g.DrawElements(PrimitiveType.Triangles, (uint)line.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    private static void DeleteLine(GL g, ref LineBuffers? line)
    {
        if (line is null)
        {
            return;
        }

        g.DeleteBuffer(line.PositionVbo);
        g.DeleteBuffer(line.ColorVbo);
        g.DeleteBuffer(line.OtherVbo);
        g.DeleteBuffer(line.SideVbo);
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
        DeleteLine(gl, ref roadLines);
        gl.DeleteFramebuffer(msaaFbo);
        gl.DeleteRenderbuffer(msaaColorRb);
        gl.DeleteRenderbuffer(msaaDepthRb);
        msaaFbo = 0;
        msaaColorRb = 0;
        msaaDepthRb = 0;
        gl.DeleteFramebuffer(presentFbo);
        gl.DeleteTexture(presentColorTex);
        gl.DeleteRenderbuffer(presentDepthRb);
        presentFbo = 0;
        presentColorTex = 0;
        presentDepthRb = 0;
        foreach (OrthoTile t in orthoTiles)
        {
            if (t.Texture != 0)
            {
                gl.DeleteTexture(t.Texture);
                t.Texture = 0;
            }
        }
        foreach (OrthoTile t in pendingOrthoRelease)
        {
            if (t.Texture != 0)
            {
                gl.DeleteTexture(t.Texture);
                t.Texture = 0;
            }
        }
        pendingOrthoRelease.Clear();
        if (programReady)
        {
            gl.DeleteProgram(program);
            gl.DeleteProgram(lineProgram);
            gl.DeleteProgram(skyProgram);
            gl.DeleteVertexArray(skyVao);
            gl.DeleteBuffer(skyVbo);
            skyProgram = 0;
            skyVao = 0;
            skyVbo = 0;
            programReady = false;
        }
    }
}