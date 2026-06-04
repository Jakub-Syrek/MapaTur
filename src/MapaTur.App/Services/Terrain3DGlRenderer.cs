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
        "uniform vec3 uSunColor;\n" +    // direct-sun colour (warm at sunset, white at noon)
        "uniform vec3 uSkyAmbient;\n" +  // ambient sky-fill colour for shadowed slopes
        "uniform sampler2D uOrtho;\n" +
        "uniform int uUseOrtho;\n" +
        "uniform vec2 uOrthoTexel;\n" + // (1/width, 1/height) of the bound ortho texture
        "uniform float uSharpen;\n" +   // unsharp-mask strength; 0 = off
        "uniform vec3 uFogColor;\n" +
        "uniform float uFogDensity;\n" + // per-metre exponential; 0 = no aerial perspective
        "uniform vec3 uCameraPos;\n" +
        // Cloud-shadow inputs: the SAME field the sea-of-clouds layer draws, so the shadows on the
        // ground line up with the clouds overhead. The terrain fragment projects up along the sun
        // ray to the cloud plane and samples the field there — moving dappled light at any sun angle.
        "uniform float uCloudAltitude;\n" +
        "uniform float uCloudNoiseScale;\n" +
        "uniform vec2 uCloudWind;\n" +
        "uniform float uCloudTime;\n" +
        "uniform float uCloudCoverage;\n" +
        "uniform float uCloudShadow;\n" + // strength 0..1; 0 disables
        // Snow cover: whiten the surface above uSnowLineZ (world-Z), softened over uSnowBandZ, and only
        // on flatter slopes; uSnowStrength (0..1) gates + scales it. The line/band/strength are derived on
        // the CPU from the snow slider + the mesh's Z range, so the snowline lowers as the slider rises.
        "uniform float uSnowStrength;\n" +
        "uniform float uSnowLineZ;\n" +
        "uniform float uSnowBandZ;\n" +
        "out vec4 fragColor;\n" +
        "float hashT(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }\n" +
        "float noiseT(vec2 p){\n" +
        "  vec2 i = floor(p); vec2 f = fract(p);\n" +
        "  f = f * f * (3.0 - 2.0 * f);\n" +
        "  return mix(mix(hashT(i), hashT(i + vec2(1.0,0.0)), f.x),\n" +
        "             mix(hashT(i + vec2(0.0,1.0)), hashT(i + vec2(1.0,1.0)), f.x), f.y);\n" +
        "}\n" +
        "float fbmT(vec2 p){ float v=0.0,a=0.5; for(int i=0;i<5;i++){ v+=a*noiseT(p); p*=2.0; a*=0.5;} return v; }\n" +
        "void main(){\n" +
        // Cloud shadow: march from this fragment toward the sun (uLightDir) up to the cloud plane,
        // sample the identical animated cloud field, and darken the DIRECT-sun term where a cloud
        // blocks the ray. Only when the sun is meaningfully above the horizon (uLightDir.z) — at
        // grazing angles the projection length explodes and shadows would smear.
        "  float sunShadow = 0.0;\n" +
        "  if (uCloudShadow > 0.001 && uCloudCoverage > 0.001 && uLightDir.z > 0.12) {\n" +
        "    float tt = (uCloudAltitude - vWorldPos.z) / uLightDir.z;\n" +
        "    if (tt > 0.0) {\n" +
        "      vec2 cp = vWorldPos.xy + (uLightDir.xy * tt);\n" +
        "      vec2 p = cp * uCloudNoiseScale + uCloudWind * uCloudTime;\n" +
        "      vec2 warp = vec2(fbmT(p * 0.5 + uCloudTime * 0.010),\n" +
        "                       fbmT(p * 0.5 + vec2(5.2, 1.3) + uCloudTime * 0.012));\n" +
        "      float n = fbmT(p + (warp - 0.5) * 1.6);\n" +
        "      float thr = 0.72 - (uCloudCoverage * 0.34);\n" + // match the sea-of-clouds layer threshold
        "      sunShadow = smoothstep(thr, thr + 0.20, n);\n" +
        "    }\n" +
        "  }\n" +
        // COLOURED lighting: shadowed slopes get the cool sky-ambient fill, sun-facing slopes get
        // the warm direct-sun colour scaled by Lambert AND attenuated by any cloud blocking the
        // sun. Tinting (not just dimming) makes the terrain read as genuinely sunlit; the cloud
        // shadow term adds the moving dappled light that sells "sun + clouds" at any time of day.
        "  float lambert = max(0.0, dot(normalize(vNormal), uLightDir));\n" +
        "  float sunlit = lambert * (1.0 - uAmbient) * (1.0 - (sunShadow * uCloudShadow));\n" +
        "  vec3 lightSum = (uSkyAmbient * uAmbient) + (uSunColor * sunlit);\n" +
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
        // Snow on top of the base colour, driven PRIMARILY BY ELEVATION: full above the snowline,
        // fading out over the band just below it. The snowline (uSnowLineZ) sits at the highest peak when
        // the slider is just on and drops to the valley floor at full — so snow always appears on the
        // TOP first and recedes top-LAST. Slope only GENTLY thins it on the sheerest rock faces (mix
        // 0.65..1.0), so steep summits still hold snow (they were wrongly stripped before). Blended toward
        // a cool white; the lighting below shades it (sunlit bright, shadowed cool). NO per-pixel fBm —
        // a 5-octave fbmT per fragment tanked the framerate ("zarywa"); the band already softens the edge.
        "  float snowMix = 0.0;\n" +
        "  if (uSnowStrength > 0.001) {\n" +
        "    float snowH = smoothstep(uSnowLineZ, uSnowLineZ + uSnowBandZ, vWorldPos.z);\n" +
        "    vec3 nrm = normalize(vNormal);\n" +
        "    float slope = mix(0.65, 1.0, smoothstep(0.10, 0.50, nrm.z));\n" +
        // Aspect: sunny SOUTH-facing slopes (+Y is north, so south = -Y) melt off first. The melt is
        // strongest when there's only a little snow and fades to nothing at full cover — so a thin
        // dusting clings to the shaded north faces while a deep pack still blankets every aspect.
        "    float southFacing = max(0.0, -nrm.y);\n" +
        "    float aspectMelt = southFacing * (1.0 - uSnowStrength);\n" +
        "    snowMix = clamp(snowH * slope * (1.0 - aspectMelt), 0.0, 1.0) * uSnowStrength;\n" +
        "    base = mix(base, vec3(0.99, 0.99, 1.0), snowMix);\n" +
        "  }\n" +
        "  vec3 lit = base * lightSum;\n" +
        // Snow stays bright white even in shadow (very high albedo + sky/multiple scattering): lift the
        // lit colour toward white by the snow amount, so ambient-only (shadowed) snow doesn't read grey.
        "  lit = mix(lit, vec3(1.0), snowMix * 0.6);\n" +
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
        "uniform vec2 uCloudDrift;\n" +     // wind drift velocity (scaled by the wind setting)
        "uniform float uCloudDark;\n" +     // storm darkening, 1 = bright, <1 = darker
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
        // Below horizon (looking down past the finite terrain edge / into a top-down view's corners):
        // distant land seen through aerial HAZE, not a flat grey void. The old flat uSkyHorizon*0.72
        // read as a dull grey wall filling most of the screen whenever the camera looked down at the
        // finite DEM patch. Keep it bright right at the horizon line (where haze piles up) and let it
        // deepen only gently further down, so the area beyond the terrain reads as luminous distance.
        // Cross-faded across the horizon line so there's no hard seam.
        "  float below = clamp(-h, 0.0, 1.0);\n" +            // 0 at the horizon, 1 straight down
        "  vec3 skyDown = mix(uSkyHorizon, uSkyHorizon * 0.82, smoothstep(0.0, 0.5, below));\n" +
        "  vec3 sky = mix(skyDown, skyUp, smoothstep(-0.12, 0.06, h));\n" +
        // Cirrus on an INFINITE horizontal layer overhead: perspective-project the view ray
        // onto a constant-world-Z plane by dividing xy by the up component. Classic skybox
        // cloud trick — bands lock to world directions, pan correctly as the camera rotates,
        // and only appear in genuinely upward-looking pixels.
        "  float cloudDensity = 0.0;\n" +
        "  if (h > 0.015) {\n" +
        "    vec2 cloudUv = viewDir.xy / h;\n" +
        "    cloudUv = vec2(cloudUv.x * 0.5, cloudUv.y * 1.6) + (uCloudDrift * uTime);\n" +
        "    float clouds = noise2(cloudUv) * 0.6 + noise2(cloudUv * 2.3) * 0.4;\n" +
        "    float threshold = 0.68 - (uCloudCoverage * 0.28);\n" + // sparser cirrus
        "    cloudDensity = smoothstep(threshold, threshold + 0.16, clouds) * 0.7;\n" +
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
        "  cloudColor *= uCloudDark;\n" +
        "  sky = mix(sky, cloudColor, cloudDensity);\n" +
        // Sun disc + halo. smoothstep gives a soft-edged disc the right pixel size; pow gives
        // the Mie-style fall-off (the "glow") that bleeds well past the disc.
        "  float sunDot = dot(viewDir, uSunDir);\n" +
        "  float sunCore = smoothstep(0.9994, 0.99985, sunDot);\n" +
        "  float sunHalo = pow(max(sunDot, 0.0), 80.0) * 0.55;\n" +
        "  vec3 sun = uSunColor * (sunCore + sunHalo);\n" +
        "  fragColor = vec4(sky + sun, 1.0);\n" +
        "}\n";

    // Cloud-layer ("sea of clouds") program. A large horizontal quad at a fixed world altitude,
    // drawn AFTER the terrain with the depth test on (so peaks above the layer occlude it and the
    // valleys below are veiled) but depth-write off and alpha blending on. The fragment shader
    // samples animated fBm at the fragment's WORLD (x,y) so the cloud field is locked to the world
    // and drifts smoothly — the iconic Tatra temperature-inversion look, peaks poking through fog.
    private const string CloudLayerVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec2 aCorner;\n" + // unit quad corner in [-1,1]
        "uniform mat4 uMvp;\n" +
        "uniform vec2 uCenter;\n" +    // world XY centre of the layer
        "uniform float uHalfExtent;\n" + // world half-size of the quad
        "uniform float uAltitude;\n" + // world Z of the layer
        "out vec2 vWorldXY;\n" +
        "out vec2 vLocal;\n" +
        "void main(){\n" +
        "  vLocal = aCorner;\n" +
        "  vec2 world = uCenter + (aCorner * uHalfExtent);\n" +
        "  vWorldXY = world;\n" +
        "  gl_Position = uMvp * vec4(world, uAltitude, 1.0);\n" +
        "}\n";

    private const string CloudLayerFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vWorldXY;\n" +
        "in vec2 vLocal;\n" +
        "uniform float uTime;\n" +
        "uniform float uCoverage;\n" +
        "uniform vec3 uCloudColor;\n" +
        "uniform float uNoiseScale;\n" + // 1/metres; sets cloud-cell size
        "uniform vec2 uWind;\n" +        // drift velocity in noise-units/sec (varies over time)
        "out vec4 fragColor;\n" +
        "float hashC(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }\n" +
        "float noiseC(vec2 p){\n" +
        "  vec2 i = floor(p); vec2 f = fract(p);\n" +
        "  f = f * f * (3.0 - 2.0 * f);\n" +
        "  return mix(mix(hashC(i), hashC(i + vec2(1.0,0.0)), f.x),\n" +
        "             mix(hashC(i + vec2(0.0,1.0)), hashC(i + vec2(1.0,1.0)), f.x), f.y);\n" +
        "}\n" +
        "float fbmC(vec2 p){ float v=0.0,a=0.5; for(int i=0;i<5;i++){ v+=a*noiseC(p); p*=2.0; a*=0.5;} return v; }\n" +
        "void main(){\n" +
        // World-space sample point translated by the wind (clouds slide downwind).
        "  vec2 p = vWorldXY * uNoiseScale + uWind * uTime;\n" +
        // Domain warp by a SECOND, slowly time-evolving noise field: this is what makes the
        // clouds form and dissipate (shapes morph) instead of merely sliding rigidly — the
        // warp offset drifts in its own slow time so cells grow, merge and tear apart.
        "  vec2 warp = vec2(fbmC(p * 0.5 + uTime * 0.010),\n" +
        "                   fbmC(p * 0.5 + vec2(5.2, 1.3) + uTime * 0.012));\n" +
        "  float n = fbmC(p + (warp - 0.5) * 1.6);\n" +
        "  float thr = 0.72 - (uCoverage * 0.34);\n" + // higher threshold = fewer, sparser low clouds
        "  float a = smoothstep(thr, thr + 0.20, n);\n" +
        // Soft-fade the quad's outer ring so the (finite) sheet doesn't show a hard rectangular
        // edge out toward the horizon.
        "  float edge = smoothstep(1.0, 0.65, max(abs(vLocal.x), abs(vLocal.y)));\n" +
        "  a *= edge * 0.55;\n" + // lower peak opacity so the sheet is lighter / less obtrusive
        "  fragColor = vec4(uCloudColor, a);\n" +
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
    private int sunColorLocation = -1;
    private int skyAmbientLocation = -1;
    private int orthoSamplerLocation = -1;
    private int useOrthoLocation = -1;
    private int orthoTexelLocation = -1;
    private int sharpenLocation = -1;
    private int terrainFogColorLocation = -1;
    private int terrainFogDensityLocation = -1;
    private int terrainCameraPosLocation = -1;
    private int terrainCloudAltitudeLocation = -1;
    private int terrainCloudNoiseScaleLocation = -1;
    private int terrainCloudWindLocation = -1;
    private int terrainCloudTimeLocation = -1;
    private int terrainCloudCoverageLocation = -1;
    private int terrainCloudShadowLocation = -1;
    private int terrainSnowStrengthLocation = -1;
    private int terrainSnowLineZLocation = -1;
    private int terrainSnowBandZLocation = -1;

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
    private int skyCloudDriftLocation = -1;
    private int skyCloudDarkLocation = -1;
    private uint skyVao;
    private uint skyVbo;

    // Cloud-layer ("sea of clouds") program + unit-quad geometry.
    private uint cloudProgram;
    private int cloudMvpLocation = -1;
    private int cloudCenterLocation = -1;
    private int cloudHalfExtentLocation = -1;
    private int cloudAltitudeLocation = -1;
    private int cloudTimeLocation = -1;
    private int cloudCoverageLocation = -1;
    private int cloudColorLocation = -1;
    private int cloudNoiseScaleLocation = -1;
    private int cloudWindLocation = -1;
    private uint cloudVao;
    private uint cloudVbo;

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

    // Viewport-aware ortho streaming (#39) + LRU eviction (#43). Only the ortho cells whose world AABB
    // is inside the view frustum are uploaded; the planner caps resident-cell VRAM at OrthoVramBudgetBytes
    // and evicts the least-recently-rendered cell when a newly-visible one pushes past the budget. CPU
    // bytes are kept (lazy re-upload on re-entry); releasing them + re-decoding from disk is a follow-up.
    //
    // Budget sized to hold the ENTIRE bundled finite-DEM ortho set resident: the 8 Tatry cells are
    // 8192×5462 ≈ 238 MB each (with mips) ≈ 1.9 GB total. A 1 GB budget held only 4 of the 8, so the
    // others were evicted/frustum-culled and drew the un-textured hypsometric GREEN tint. Streaming +
    // eviction only earns its keep for a set far larger than VRAM (future online whole-voivodeship
    // tiles); for a small bundled set StreamOrthoTextures keeps every cell resident (see below).
    private const long OrthoVramBudgetBytes = 3L * 1024 * 1024 * 1024; // ~3 GB resident ortho cap
    private OrthoResidencyPlanner? orthoPlanner;
    // Per-cell world-space AABB (keyed by OrthoTileIndex), unioned from the mesh tiles that sample it.
    private readonly Dictionary<int, (Vector3 Min, Vector3 Max)> orthoCellBounds = new();
    private IReadOnlyList<TerrainMesh3D>? orthoBoundsTiles;
    private readonly List<int> visibleOrthoCells = new();
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
    private byte[]? flipRow; // scratch row for the vertical flip during framebuffer readback

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
        Atmosphere? atmosphere = null,
        IReadOnlyList<TreeInstance>? forest = null)
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
            sunColorLocation = -1;
            skyAmbientLocation = -1;
            orthoSamplerLocation = -1;
            useOrthoLocation = -1;
            orthoTexelLocation = -1;
            sharpenLocation = -1;
            terrainFogColorLocation = -1;
            terrainFogDensityLocation = -1;
            terrainCameraPosLocation = -1;
            terrainCloudAltitudeLocation = -1;
            terrainCloudNoiseScaleLocation = -1;
            terrainCloudWindLocation = -1;
            terrainCloudTimeLocation = -1;
            terrainCloudCoverageLocation = -1;
            terrainCloudShadowLocation = -1;
            terrainSnowStrengthLocation = -1;
            terrainSnowLineZLocation = -1;
            terrainSnowBandZLocation = -1;
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
            skyCloudDriftLocation = -1;
            skyCloudDarkLocation = -1;
            skyVao = 0;
            skyVbo = 0;
            cloudProgram = 0;
            cloudMvpLocation = -1;
            cloudCenterLocation = -1;
            cloudHalfExtentLocation = -1;
            cloudAltitudeLocation = -1;
            cloudTimeLocation = -1;
            cloudCoverageLocation = -1;
            cloudColorLocation = -1;
            cloudNoiseScaleLocation = -1;
            cloudWindLocation = -1;
            cloudVao = 0;
            cloudVbo = 0;
            // Forest program + buffers belonged to the dead context too; drop the IDs and force a rebuild
            // + instance re-upload on the next forest pass.
            forestProgram = 0;
            forestMvpLocation = -1;
            forestTrunkLocation = -1;
            forestFoliageColorLocation = -1;
            forestLightDirLocation = -1;
            forestSunColorLocation = -1;
            forestSkyAmbientLocation = -1;
            forestAmbientLocation = -1;
            forestFogColorLocation = -1;
            forestFogDensityLocation = -1;
            forestCameraPosLocation = -1;
            forestFadeEndLocation = -1;
            forestSnowLocation = -1;
            forestWindDirLocation = -1;
            forestWindAmpLocation = -1;
            forestWindTimeLocation = -1;
            forestLodNearLocation = -1;
            forestLodFarLocation = -1;
            forestVao = 0;
            forestBaseVbo = 0;
            forestInstanceVbo = 0;
            forestInstanceCount = 0;
            forestVertexCount = 0;
            lastForest = null;
            // Impostor atlas (texture + FBO + depth RB + bake VAO) belonged to the dead context too; drop
            // the IDs and re-bake on the next forest pass. (Unsupported flag is intentionally NOT reset —
            // if the FBO was incomplete once, the fresh context will likely be the same.)
            forestAtlasTex = 0;
            forestAtlasFbo = 0;
            forestAtlasDepthRb = 0;
            forestBakeVao = 0;
            forestImpostorProgram = 0;
            forestImpostorMvpLocation = -1;
            forestImpostorCameraPosLocation = -1;
            forestImpostorAtlasLocation = -1;
            forestImpostorGridLocation = -1;
            forestImpostorFogColorLocation = -1;
            forestImpostorFogDensityLocation = -1;
            forestImpostorLodNearLocation = -1;
            forestImpostorLodFarLocation = -1;
            forestImpostorImpFarLocation = -1;
            forestImpostorVao = 0;
            forestImpostorQuadVbo = 0;
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

        // Reclaim any GL textures retired by a previous SetOrthoTextures swap. The actual upload/eviction
        // is viewport-aware and runs in StreamOrthoTextures once the view-projection is known (below).
        ReclaimReleasedOrthoTextures(gl);

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

        // Viewport-aware ortho streaming: upload only the cells whose AABB is in-frustum, evicting the
        // least-recently-rendered ones past the VRAM budget. mvp is the world→clip matrix the frustum
        // test expects (same as Camera3D.ProjectToScreen). Must run before the terrain pass binds cells.
        StreamOrthoTextures(gl, mvp, tiles);

        // ── Live weather ────────────────────────────────────────────────────────────────────────
        // Clouds are not a static dial: the coverage wanders over minutes (sometimes near-clear,
        // sometimes heavy) and the wind direction + speed drift, so the field is never the same
        // twice. Driven off the renderer wall-clock (atmosphereClock), independent of the
        // time-of-day slider, so clouds keep evolving while the user just looks around. Built from
        // a few incommensurate sines (a cheap, tileless "weather noise"). effectiveCoverage feeds
        // both the cirrus sky pass and the sea-of-clouds layer so they wax and wane together.
        float weatherT = (float)atmosphereClock.Elapsed.TotalSeconds;
        float baseCoverage = atmosphere?.CloudCoverage ?? 0f;
        float weatherNoise =
            (MathF.Sin(weatherT * 0.013f) * 0.5f)
            + (MathF.Sin((weatherT * 0.031f) + 1.7f) * 0.3f)
            + (MathF.Sin((weatherT * 0.057f) + 4.2f) * 0.2f); // ~[-1,1]
        // Multiplicative weather variation around the user's base coverage, so a base of 0 stays a
        // dead-clear sky (an additive bump used to leave ~0.35 coverage even at the 0% slider).
        float effectiveCoverage = Math.Clamp(baseCoverage * (1f + (0.6f * weatherNoise)), 0f, 1f);
        // Wind in noise-units/sec: slowly rotating heading + gently pulsing speed, scaled by the
        // user's wind setting (calm → barely drifting, gale → racing). The same setting darkens the
        // clouds toward storm-grey: stormDarken multiplies every cloud colour below.
        float wind = atmosphere?.Wind ?? 0.3f;
        float windScale = 0.35f + (3.0f * wind); // ~0.35× at calm, ~3.35× at full gale
        float windAngle = (MathF.Sin(weatherT * 0.008f) * 0.9f) + (MathF.Sin((weatherT * 0.017f) + 2f) * 0.5f);
        float windSpeed = (0.012f + (0.010f * MathF.Sin(weatherT * 0.005f))) * windScale;
        var windVec = new Vector2(MathF.Cos(windAngle) * windSpeed, MathF.Sin(windAngle) * windSpeed);
        // Storm darkening: high wind dims the clouds toward grey (down to ~40% brightness at gale).
        float stormDarken = 1f - (0.60f * wind);

        // Cloud-layer geometry, computed once and shared by BOTH the sea-of-clouds draw and the
        // terrain's cloud-shadow lookup so the shadows on the ground register with the clouds above.
        float cloudMaxZ = float.NegativeInfinity;
        float terrainMinZ = float.PositiveInfinity;
        for (int i = 0; i < tiles.Count; i++)
        {
            if (tiles[i].MaxElevationZ > cloudMaxZ)
            {
                cloudMaxZ = tiles[i].MaxElevationZ;
            }
            if (tiles[i].MinElevationZ < terrainMinZ)
            {
                terrainMinZ = tiles[i].MinElevationZ;
            }
        }
        TerrainMesh3D geomFrame = tiles[0];

        // Cloud altitude (as a fraction of the mesh's relief height) is no longer fixed: it drifts
        // a little at random AND tracks the sun. Low sun (dawn / dusk / night) pulls the layer DOWN
        // into the valleys — the classic temperature-inversion "sea of clouds" hugging the floor;
        // a high midday sun lifts it so only the highest peaks poke through. A slow weather sine adds
        // a gentle random wander so it never sits at exactly the same height twice.
        float sunHeight = atmosphere?.SunDirection.Z ?? 0.3f; // sin(elevation), ~-0.8..0.9
        float altNoise = (MathF.Sin((weatherT * 0.011f) + 2.0f) * 0.6f) + (MathF.Sin((weatherT * 0.023f) + 0.5f) * 0.4f); // ~[-1,1]
        float altFraction = Math.Clamp(0.50f + (0.20f * sunHeight) + (0.10f * altNoise), 0.28f, 0.78f);
        float cloudAltitude = float.IsNegativeInfinity(cloudMaxZ)
            ? 0f
            : geomFrame.Center.Z + ((cloudMaxZ - geomFrame.Center.Z) * altFraction);
        float cloudHalfExtent = MathF.Max(geomFrame.HorizontalExtent * 4f, 20_000f);
        float cloudNoiseScale = 1f / MathF.Max(geomFrame.HorizontalExtent * 0.5f, 4_000f);
        bool cloudsActive = atmosphere is not null && effectiveCoverage > 0.001f && !float.IsNegativeInfinity(cloudMaxZ);
        // Cloud-shadow darkening of direct sun where a cloud blocks the ray (0 = off).
        const float CloudShadowStrength = 0.55f;

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
            gl.Uniform1(skyTimeLocation, weatherT);
            gl.Uniform1(skyCloudCoverageLocation, effectiveCoverage);
            gl.Uniform2(skyCloudDriftLocation, windVec.X, windVec.Y);
            gl.Uniform1(skyCloudDarkLocation, stormDarken);
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

        // Coloured-light uniforms. With an atmosphere: warm direct-sun colour (boosted a touch so
        // sunlit slopes really glow at golden hour) + a brightened sky-tint ambient so shadows go
        // cool rather than muddy. Without one: both white, so the shader reproduces the legacy
        // grey scalar shade exactly.
        Vector3 sunCol = Vector3.One;
        Vector3 skyAmbient = Vector3.One;
        if (atmosphere is not null)
        {
            // Direct sun: lift toward white a little so even a deep-orange sunset still has enough
            // luminance to light the slopes (pure (1,0.5,0.15) × albedo can read too dark).
            sunCol = Vector3.Lerp(atmosphere.SunColor, Vector3.One, 0.25f) * 1.15f;
            // Ambient fill = a bright, desaturated version of the zenith sky tint so shadowed
            // faces pick up a soft cool cast that contrasts with the warm sun.
            skyAmbient = Vector3.Lerp(atmosphere.SkyZenithColor, Vector3.One, 0.55f);
        }
        gl.Uniform3(sunColorLocation, sunCol.X, sunCol.Y, sunCol.Z);
        gl.Uniform3(skyAmbientLocation, skyAmbient.X, skyAmbient.Y, skyAmbient.Z);

        // Cloud-shadow uniforms: feed the terrain the same cloud field the layer draws so moving
        // clouds throw moving shadows. Coverage 0 (or no atmosphere) disables it via the shader guard.
        gl.Uniform1(terrainCloudAltitudeLocation, cloudAltitude);
        gl.Uniform1(terrainCloudNoiseScaleLocation, cloudNoiseScale);
        gl.Uniform2(terrainCloudWindLocation, windVec.X, windVec.Y);
        gl.Uniform1(terrainCloudTimeLocation, weatherT);
        gl.Uniform1(terrainCloudCoverageLocation, cloudsActive ? effectiveCoverage : 0f);
        gl.Uniform1(terrainCloudShadowLocation, cloudsActive ? CloudShadowStrength : 0f);

        // Snow cover: derive the snowline (world-Z) from the slider + the mesh Z range so it scales with
        // Pion. At snow=0 the line sits ABOVE every peak (no snow); at snow=1 it drops BELOW the valley
        // floor (full snow). The shader whitens above the line, over a soft band, on flat-ish slopes.
        float snowAmount = atmosphere?.SnowAmount ?? 0f;
        float snowMaxZ = float.IsNegativeInfinity(cloudMaxZ) ? 0f : cloudMaxZ;
        float snowMinZ = float.IsPositiveInfinity(terrainMinZ) ? 0f : terrainMinZ;
        float snowRelief = MathF.Max(1f, snowMaxZ - snowMinZ);
        float snowBandZ = snowRelief * 0.15f;
        // Snowline = highest peak when the slider is just on (snow appears on the TOP first), dropping to
        // one band below the valley floor at full (everything covered, floor included). Highest-first,
        // top-last — exactly "the most snow where it's highest".
        float snowLineZ = (snowMaxZ * (1f - snowAmount)) + ((snowMinZ - snowBandZ) * snowAmount);
        gl.Uniform1(terrainSnowStrengthLocation, snowAmount);
        gl.Uniform1(terrainSnowLineZLocation, snowLineZ);
        gl.Uniform1(terrainSnowBandZLocation, snowBandZ);

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

        // Forest: instanced trees, opaque + depth-tested (terrain in front occludes them, they occlude each
        // other). Drawn after the terrain and BEFORE the overlays so trails/route stay readable on top.
        // Phase 3: bake the impostor atlas once up front — independent of the live density (a persisted
        // Forest=0 must not stop the bake), so the atlas is ready the first frame.
        EnsureForestProgram(gl);
        BakeForestAtlas(gl);
        EnsureForestImpostorProgram(gl);
        if (forest is { Count: > 0 })
        {
            EnsureForestInstances(gl, forest);
            // Phase 3 LOD: near trees as the full instanced mesh, far trees as cheap atlas impostors, with
            // a dithered crossfade band between (each pass collapses the instances outside its range, so the
            // expensive mesh fragments never run for distant trees). Both depth-tested + alpha-tested.
            DrawForest(gl, m, camera, atmosphere, windVec, weatherT);
            DrawForestImpostors(gl, m, camera, atmosphere);
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

        // "Sea of clouds" layer: a horizontal translucent sheet at the shared cloud altitude, drawn
        // after the terrain so the depth test lets peaks poke through and veils the valleys. Geometry
        // + field params come from the precomputed cloud locals so the layer matches the shadows the
        // terrain pass already cast. Alpha-blended, depth-write off (must not occlude later overlays).
        if (cloudsActive)
        {
            // Colour: warm-tinted near sunset (toward the horizon hue), bright white when the sun is
            // high, dimmed at night. Built from the atmosphere so it matches the sky.
            float dayness = Math.Clamp(atmosphere!.SunDirection.Z + 0.1f, 0f, 1f);
            Vector3 white = new(0.97f, 0.97f, 0.99f);
            Vector3 tint = Vector3.Lerp(atmosphere.SkyHorizonColor, white, dayness);
            Vector3 cloudCol = tint * (0.35f + (0.65f * dayness)) * stormDarken;

            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.DepthMask(false); // translucent: test against terrain but don't write depth
            gl.UseProgram(cloudProgram);
            gl.UniformMatrix4(cloudMvpLocation, 1, false, m);
            gl.Uniform2(cloudCenterLocation, geomFrame.Center.X, geomFrame.Center.Y);
            gl.Uniform1(cloudHalfExtentLocation, cloudHalfExtent);
            gl.Uniform1(cloudAltitudeLocation, cloudAltitude);
            gl.Uniform1(cloudTimeLocation, weatherT);
            gl.Uniform1(cloudCoverageLocation, effectiveCoverage);
            gl.Uniform3(cloudColorLocation, cloudCol.X, cloudCol.Y, cloudCol.Z);
            gl.Uniform1(cloudNoiseScaleLocation, cloudNoiseScale);
            gl.Uniform2(cloudWindLocation, windVec.X, windVec.Y);
            gl.BindVertexArray(cloudVao);
            gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            gl.BindVertexArray(0);
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);
        }

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

    /// <summary>Width of the last rendered frame (the present colour texture); 0 before the first render.</summary>
    public int PresentWidth => presentWidth;

    /// <summary>Height of the last rendered frame.</summary>
    public int PresentHeight => presentHeight;

    /// <summary>
    /// Reads back the freshly-rendered frame (the MSAA-resolved, single-sample present FBO) into
    /// <paramref name="dst"/> as tightly-packed top-row-first RGBA8 of <paramref name="width"/> ×
    /// <paramref name="height"/>. Use this — not a Skia surface snapshot — to capture frames for video:
    /// it reads the exact GL output, sidestepping the SKGLView back-buffer staleness that returned a
    /// cleared buffer for every frame after the first. Must be called on the GL thread with the context
    /// current and the present FBO still allocated (i.e. right after <see cref="Render"/>). GL's origin is
    /// bottom-left, so rows are flipped to top-first here. Returns false when readback isn't possible.
    /// </summary>
    public bool TryReadPresentFrame(byte[] dst, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(dst);
        GL? g = gl;
        if (g is null || presentFbo == 0 || presentColorTex == 0 || width <= 0 || height <= 0)
        {
            return false;
        }

        int stride = width * 4;
        int needed = stride * height;
        if (dst.Length < needed)
        {
            return false;
        }

        g.BindFramebuffer(FramebufferTarget.ReadFramebuffer, presentFbo);
        g.ReadBuffer(ReadBufferMode.ColorAttachment0);
        g.PixelStore(PixelStoreParameter.PackAlignment, 1);
        g.ReadPixels<byte>(0, 0, (uint)width, (uint)height, PixelFormat.Rgba, PixelType.UnsignedByte, dst.AsSpan(0, needed));
        g.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);

        // glReadPixels returns bottom-row-first; flip to top-first so the encoder sees an upright frame.
        if (flipRow is null || flipRow.Length < stride)
        {
            flipRow = new byte[stride];
        }
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * stride;
            int bottom = (height - 1 - y) * stride;
            Array.Copy(dst, top, flipRow, 0, stride);
            Array.Copy(dst, bottom, dst, top, stride);
            Array.Copy(flipRow, 0, dst, bottom, stride);
        }

        return true;
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
    // Deletes GL textures retired by a SetOrthoTextures swap. Cheap; called every frame.
    private void ReclaimReleasedOrthoTextures(GL g)
    {
        if (pendingOrthoRelease.Count == 0)
        {
            return;
        }

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

    // Viewport-aware ortho streaming + LRU eviction. Computes which cells are in-frustum this frame,
    // asks the residency planner what to upload/evict within the VRAM budget, and applies the plan.
    private void StreamOrthoTextures(GL g, Matrix4x4 viewProjection, IReadOnlyList<TerrainMesh3D> tiles)
    {
        if (orthoTiles.Count == 0)
        {
            orthoPlanner = null;
            return;
        }

        // A new ortho set (or a context-loss rebuild) resets residency: every prior GL texture is gone,
        // so the planner must start from an empty resident set.
        if (orthoDirty)
        {
            orthoDirty = false;
            orthoPlanner = null;
        }

        EnsureOrthoCellBounds(tiles);

        int budgetCells = ComputeOrthoBudgetCells();
        orthoPlanner ??= new OrthoResidencyPlanner(budgetCells);

        // When the whole ortho set fits in the resident budget (the bundled finite-DEM case — the 8
        // Tatry cells now all fit), keep EVERY cell resident: feed them all as "visible" so they upload
        // up front and the planner never evicts. Frustum-culling a small set was the cause of the
        // "light-green" tiles — a cell that briefly left the frustum was dropped (or never uploaded) and
        // its mesh tiles fell back to the un-textured hypsometric green. Viewport streaming + eviction is
        // only needed for a set too large to all fit (future online whole-voivodeship tiles), where this
        // branch is skipped and the frustum/LRU path below runs.
        bool keepAllResident = orthoTiles.Count <= budgetCells;

        // Which cells are drawable this frame? A cell is drawable only if some mesh tile samples it (so
        // it has a world AABB). With keepAllResident every such cell counts as visible; otherwise cull
        // against the view frustum so only on-screen cells are uploaded.
        visibleOrthoCells.Clear();
        for (int idx = 0; idx < orthoTiles.Count; idx++)
        {
            if (orthoCellBounds.TryGetValue(idx, out var aabb) &&
                (keepAllResident || FrustumCuller.IsAabbVisible(viewProjection, aabb.Min, aabb.Max)))
            {
                visibleOrthoCells.Add(idx);
            }
        }

        OrthoResidencyPlan plan = orthoPlanner.Plan(visibleOrthoCells);
        if (plan.ToUpload.Count == 0 && plan.ToEvict.Count == 0)
        {
            return;
        }

        foreach (int idx in plan.ToEvict)
        {
            OrthoTile tile = orthoTiles[idx];
            if (tile.Texture != 0)
            {
                g.DeleteTexture(tile.Texture);
                tile.Texture = 0;
            }
        }

        if (plan.ToUpload.Count > 0)
        {
            // Upload beyond GL_MAX_TEXTURE_SIZE yields a garbage/black texture, so guard the size once.
            Span<int> maxTexSize = stackalloc int[1] { 2048 };
            g.GetInteger(GLEnum.MaxTextureSize, maxTexSize);
            int maxSize = maxTexSize[0];

            // Query the driver's max anisotropy once, outside the upload loop (a per-iteration stackalloc
            // would risk a stack overflow — CA2014).
            const GLEnum maxAnisotropyPName = (GLEnum)0x84FF; // GL_MAX_TEXTURE_MAX_ANISOTROPY_EXT
            Span<float> maxAniso = stackalloc float[1] { 1f };
            g.GetFloat(maxAnisotropyPName, maxAniso);
            float aniso = Math.Clamp(16f, 1f, maxAniso[0] < 1f ? 1f : maxAniso[0]);

            foreach (int idx in plan.ToUpload)
            {
                UploadOrthoCell(g, orthoTiles[idx], maxSize, aniso);
            }

            g.BindTexture(TextureTarget.Texture2D, 0);
        }
    }

    private static void UploadOrthoCell(GL g, OrthoTile tile, int maxSize, float aniso)
    {
        if (tile.Texture != 0)
        {
            return; // already resident
        }
        if (tile.Width > maxSize || tile.Height > maxSize)
        {
            Log.Information("[GL3D] ortho tile {W}x{H} exceeds GL_MAX_TEXTURE_SIZE {Max}; skipping",
                tile.Width, tile.Height, maxSize);
            return;
        }

        const GLEnum anisotropyPName = (GLEnum)0x84FE; // GL_TEXTURE_MAX_ANISOTROPY_EXT
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

    // Resident-cell budget = VRAM budget / per-cell bytes (incl. ~33% for the mip chain), clamped to
    // [1, cellCount]. All cells are the same size in practice, so a count budget tracks the byte budget.
    private int ComputeOrthoBudgetCells()
    {
        long perCell = 0;
        foreach (OrthoTile tile in orthoTiles)
        {
            long bytes = OrthoVramBudget.CellResidentBytes(tile.Width, tile.Height);
            if (bytes > perCell)
            {
                perCell = bytes;
            }
        }

        return OrthoVramBudget.MaxResidentCells(perCell, orthoTiles.Count, OrthoVramBudgetBytes);
    }

    // Computes the world-space AABB of each ortho cell (keyed by OrthoTileIndex) by unioning the vertex
    // bounds of every mesh tile that samples it. Recomputed only when the tile set reference changes.
    private void EnsureOrthoCellBounds(IReadOnlyList<TerrainMesh3D> tiles)
    {
        if (ReferenceEquals(orthoBoundsTiles, tiles) && orthoCellBounds.Count > 0)
        {
            return;
        }

        orthoCellBounds.Clear();
        foreach (TerrainMesh3D tile in tiles)
        {
            int idx = tile.OrthoTileIndex;
            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);
            foreach (Vector3 v in tile.Vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }

            if (orthoCellBounds.TryGetValue(idx, out var existing))
            {
                min = Vector3.Min(min, existing.Min);
                max = Vector3.Max(max, existing.Max);
            }

            orthoCellBounds[idx] = (min, max);
        }

        orthoBoundsTiles = tiles;
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
        sunColorLocation = g.GetUniformLocation(program, "uSunColor");
        skyAmbientLocation = g.GetUniformLocation(program, "uSkyAmbient");
        orthoSamplerLocation = g.GetUniformLocation(program, "uOrtho");
        useOrthoLocation = g.GetUniformLocation(program, "uUseOrtho");
        orthoTexelLocation = g.GetUniformLocation(program, "uOrthoTexel");
        sharpenLocation = g.GetUniformLocation(program, "uSharpen");
        terrainFogColorLocation = g.GetUniformLocation(program, "uFogColor");
        terrainFogDensityLocation = g.GetUniformLocation(program, "uFogDensity");
        terrainCameraPosLocation = g.GetUniformLocation(program, "uCameraPos");
        terrainCloudAltitudeLocation = g.GetUniformLocation(program, "uCloudAltitude");
        terrainCloudNoiseScaleLocation = g.GetUniformLocation(program, "uCloudNoiseScale");
        terrainCloudWindLocation = g.GetUniformLocation(program, "uCloudWind");
        terrainCloudTimeLocation = g.GetUniformLocation(program, "uCloudTime");
        terrainCloudCoverageLocation = g.GetUniformLocation(program, "uCloudCoverage");
        terrainCloudShadowLocation = g.GetUniformLocation(program, "uCloudShadow");
        terrainSnowStrengthLocation = g.GetUniformLocation(program, "uSnowStrength");
        terrainSnowLineZLocation = g.GetUniformLocation(program, "uSnowLineZ");
        terrainSnowBandZLocation = g.GetUniformLocation(program, "uSnowBandZ");

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
        skyCloudDriftLocation = g.GetUniformLocation(skyProgram, "uCloudDrift");
        skyCloudDarkLocation = g.GetUniformLocation(skyProgram, "uCloudDark");

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

        // Cloud-layer program — horizontal quad at altitude, fBm-alpha "sea of clouds".
        uint cvs = CompileShader(g, ShaderType.VertexShader, CloudLayerVertexShaderSource);
        uint cfs = CompileShader(g, ShaderType.FragmentShader, CloudLayerFragmentShaderSource);
        cloudProgram = g.CreateProgram();
        g.AttachShader(cloudProgram, cvs);
        g.AttachShader(cloudProgram, cfs);
        g.LinkProgram(cloudProgram);
        g.GetProgram(cloudProgram, ProgramPropertyARB.LinkStatus, out int cloudLinked);
        if (cloudLinked == 0)
        {
            string log = g.GetProgramInfoLog(cloudProgram);
            throw new InvalidOperationException("Cloud-layer shader link failed: " + log);
        }
        g.DetachShader(cloudProgram, cvs);
        g.DetachShader(cloudProgram, cfs);
        g.DeleteShader(cvs);
        g.DeleteShader(cfs);
        cloudMvpLocation = g.GetUniformLocation(cloudProgram, "uMvp");
        cloudCenterLocation = g.GetUniformLocation(cloudProgram, "uCenter");
        cloudHalfExtentLocation = g.GetUniformLocation(cloudProgram, "uHalfExtent");
        cloudAltitudeLocation = g.GetUniformLocation(cloudProgram, "uAltitude");
        cloudTimeLocation = g.GetUniformLocation(cloudProgram, "uTime");
        cloudCoverageLocation = g.GetUniformLocation(cloudProgram, "uCoverage");
        cloudColorLocation = g.GetUniformLocation(cloudProgram, "uCloudColor");
        cloudNoiseScaleLocation = g.GetUniformLocation(cloudProgram, "uNoiseScale");
        cloudWindLocation = g.GetUniformLocation(cloudProgram, "uWind");

        // Unit quad as a triangle strip: (-1,-1) (1,-1) (-1,1) (1,1).
        Span<float> quad = stackalloc float[8] { -1f, -1f,  1f, -1f,  -1f, 1f,  1f, 1f };
        cloudVao = g.GenVertexArray();
        g.BindVertexArray(cloudVao);
        cloudVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, cloudVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), quad, BufferUsageARB.StaticDraw);
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

    // ── Forest (instanced trees) ─────────────────────────────────────────────────────────────────
    // Phase 1: opaque "cross-triangle" conifers — two perpendicular vertical triangles per tree, so each
    // reads as a 3D-ish spruce from any orbit angle without per-frame camera-facing maths. One 6-vertex
    // base mesh is drawn INSTANCED (glDrawArraysInstanced) against a per-tree (position, scale, yaw)
    // buffer, so tens of thousands of trees cost a single draw call. Lit cheaply from the atmosphere,
    // fogged, and cut off past a fade radius. Later phases swap the placeholder triangles for a baked
    // spruce mesh + octahedral impostors (near=mesh, far=impostor).
    private const string ForestVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec3 aPos;\n" +          // model space: base at z=0, apex up
        "layout(location=1) in vec3 aNormal;\n" +       // outward horizontal normal (for lighting)
        "layout(location=2) in float aFoliage;\n" +     // 0 = trunk-ish, 1 = foliage
        "layout(location=3) in vec3 aInstPos;\n" +      // per-instance world position
        "layout(location=4) in vec2 aInstScaleRot;\n" + // per-instance x=scale, y=yaw
        "uniform mat4 uMvp;\n" +
        "uniform vec2 uWindDir;\n" +   // normalized sway direction (world XY)
        "uniform float uWindAmp;\n" +  // metres of apex sway
        "uniform float uWindTime;\n" +
        "uniform vec3 uCameraPos;\n" +  // for the LOD crossfade distance
        "uniform float uLodNear;\n" +   // below: full mesh
        "uniform float uLodFar;\n" +    // above: collapsed (impostor takes over); band crossfades
        "out vec3 vNormal;\n" +
        "out float vFoliage;\n" +
        "out vec3 vWorldPos;\n" +
        "out float vHeight;\n" +
        "out float vLodAlpha;\n" + // 1 near → 0 at uLodFar (dithered out across the band)
        "void main(){\n" +
        "  float s = aInstScaleRot.x; float yaw = aInstScaleRot.y;\n" +
        "  float cs = cos(yaw); float sn = sin(yaw);\n" +
        "  vec3 p = aPos * s;\n" +
        "  vec3 rp = vec3((p.x * cs) - (p.y * sn), (p.x * sn) + (p.y * cs), p.z);\n" + // yaw about world-up
        "  vec3 world = aInstPos + rp;\n" +
        // Wind: sway scales with height (apex moves, base stays), phase offset per tree so they don't
        // all wave in lock-step. uWindAmp/uWindDir/uWindTime come from the live weather.
        "  float heightF = clamp(aPos.z / 28.0, 0.0, 1.0);\n" +
        "  float sway = uWindAmp * heightF * sin(uWindTime + ((aInstPos.x + aInstPos.y) * 0.05));\n" +
        "  world.xy += uWindDir * sway;\n" +
        "  vec3 n = vec3((aNormal.x * cs) - (aNormal.y * sn), (aNormal.x * sn) + (aNormal.y * cs), aNormal.z);\n" +
        "  vNormal = n; vFoliage = aFoliage; vWorldPos = world; vHeight = heightF;\n" +
        // LOD: full mesh below uLodNear, crossfaded to nothing by uLodFar. Collapse far instances to a
        // clipped point so their (expensive, overdrawing) fragments never run — that's the impostor's job.
        "  float lodDist = length(aInstPos - uCameraPos);\n" +
        "  vLodAlpha = 1.0 - smoothstep(uLodNear, uLodFar, lodDist);\n" +
        "  gl_Position = uMvp * vec4(world, 1.0);\n" +
        "  if (lodDist > uLodFar) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); }\n" + // NDC z>w ⇒ clipped
        "}\n";

    private const string ForestFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec3 vNormal;\n" +
        "in float vFoliage;\n" +
        "in vec3 vWorldPos;\n" +
        "in float vHeight;\n" +
        "in float vLodAlpha;\n" +
        "uniform float uSnow;\n" +     // snow amount 0..1 — dusts the foliage white toward the top
        "uniform vec3 uTrunk;\n" +
        "uniform vec3 uFoliageColor;\n" +
        "uniform vec3 uLightDir;\n" +   // unit direction toward the sun
        "uniform vec3 uSunColor;\n" +
        "uniform vec3 uSkyAmbient;\n" +
        "uniform float uAmbient;\n" +
        "uniform vec3 uFogColor;\n" +
        "uniform float uFogDensity;\n" +
        "uniform vec3 uCameraPos;\n" +
        "uniform float uFadeEnd;\n" + // discard past this view distance (world units)
        "out vec4 fragColor;\n" +
        "void main(){\n" +
        "  float dist = length(vWorldPos - uCameraPos);\n" +
        "  if (dist > uFadeEnd) discard;\n" +
        // LOD crossfade: screen-door dither out the mesh as it recedes (vLodAlpha 1→0), so it dissolves
        // into the impostor over the band instead of popping. Hash threshold on the pixel coord.
        "  float dth = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453);\n" +
        "  if (dth > vLodAlpha) discard;\n" +
        "  vec3 base = mix(uTrunk, uFoliageColor, smoothstep(0.0, 0.15, vFoliage));\n" +
        // Snow-laden conifer: dust the foliage toward white as snow rises, more toward the top (where
        // it settles) so the forest reads as a natural winter scene rather than green trees on white.
        "  if (uSnow > 0.001) {\n" +
        "    float dust = clamp(uSnow * (0.25 + (0.55 * vHeight)), 0.0, 0.8);\n" +
        "    base = mix(base, vec3(0.93, 0.95, 0.98), dust);\n" +
        "  }\n" +
        // Wrap lighting: the cross-cards are 2-sided, so use a half-Lambert so the shaded side stays a
        // dimmer green rather than crushing to black. Gives the tree a sunlit/shadow side = volume.
        "  float ndl = dot(normalize(vNormal), uLightDir);\n" +
        "  float wrap = clamp((ndl * 0.5) + 0.5, 0.0, 1.0);\n" +
        "  vec3 light = (uSkyAmbient * uAmbient) + (uSunColor * (1.0 - uAmbient) * (0.35 + (0.65 * wrap)));\n" +
        "  vec3 lit = base * light;\n" +
        "  float fog = 1.0 - exp(-dist * uFogDensity);\n" +
        "  fragColor = vec4(mix(lit, uFogColor, fog), 1.0);\n" +
        "}\n";

    // Trees fade out (hard cutoff for Phase 1) past this distance from the eye, in WORLD units (≈ real
    // metres horizontally). Keeps the per-fragment tree cost bounded to the near field.
    private const float ForestFadeEndMeters = 20000f;

    // Phase 3 LOD: full instanced MESH below ForestLodNearMeters, full IMPOSTOR billboards past
    // ForestLodFarMeters, dithered crossfade across the band (no pop). Impostors collapse past
    // ForestFadeEndMeters (sub-pixel — not worth a quad). The mesh's per-fragment overdraw is the
    // expensive part, so collapsing far mesh instances to a clipped point is where the perf win comes from.
    private const float ForestLodNearMeters = 2500f;
    private const float ForestLodFarMeters = 5500f;

    private uint forestProgram;
    private int forestMvpLocation = -1;
    private int forestTrunkLocation = -1;
    private int forestFoliageColorLocation = -1;
    private int forestLightDirLocation = -1;
    private int forestSunColorLocation = -1;
    private int forestSkyAmbientLocation = -1;
    private int forestAmbientLocation = -1;
    private int forestFogColorLocation = -1;
    private int forestFogDensityLocation = -1;
    private int forestCameraPosLocation = -1;
    private int forestFadeEndLocation = -1;
    private int forestSnowLocation = -1;
    private int forestWindDirLocation = -1;
    private int forestWindAmpLocation = -1;
    private int forestWindTimeLocation = -1;
    private int forestLodNearLocation = -1;
    private int forestLodFarLocation = -1;
    private uint forestVao;
    private uint forestBaseVbo;
    private uint forestInstanceVbo;
    private int forestInstanceCount;
    private int forestVertexCount;
    private IReadOnlyList<TreeInstance>? lastForest;

    // ── Forest impostor atlas (Phase 3) ──────────────────────────────────────────────────────────
    // Baked ONCE at the first forest pass: the conifer mesh rendered into one RGBA texture from a grid
    // of hemi-octahedral view directions (upper dome — trees are seen from above-ish). Far trees can
    // then be drawn as cheap camera-facing billboards that sample the cell matching the eye→tree angle,
    // instead of paying for the full instanced tiered mesh. Step 1 = bake + debug-blit the atlas to a
    // screen corner to eyeball the silhouettes; the draw/LOD passes come in steps 2–3.
    private const int ForestAtlasGrid = 8;                                  // 8×8 = 64 baked view dirs
    private const int ForestAtlasCell = 256;                               // px per baked view
    private const int ForestAtlasSize = ForestAtlasGrid * ForestAtlasCell; // 2048² atlas
    private uint forestAtlasTex;
    private uint forestAtlasFbo;
    private uint forestAtlasDepthRb;
    private uint forestBakeVao;
    private bool forestAtlasUnsupported;

    // Impostor billboards: one camera-facing quad per tree, sampling the baked atlas cell that matches the
    // eye→tree direction (hemi-octahedral encode in the fragment shader). Reuses the per-tree instance
    // buffer (posX,posY,posZ,scale,yaw) — yaw is ignored (the quad faces the camera).
    private const string ForestImpostorVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec2 aCard;\n" +          // base quad corner in [-1,1]
        "layout(location=1) in vec3 aInstPos;\n" +       // per-instance world base
        "layout(location=2) in vec2 aInstScaleRot;\n" +  // x=scale (yaw unused)
        "uniform mat4 uMvp;\n" +
        "uniform vec3 uCameraPos;\n" +
        "uniform float uLodNear;\n" +  // below: collapsed (mesh takes over)
        "uniform float uLodFar;\n" +   // crossfade band upper edge (impostor fully in past it)
        "uniform float uImpostorFar;\n" + // hard far cutoff (beyond it trees are sub-pixel — collapse)
        "out vec2 vCardUv;\n" +   // 0..1 within the card → sub-cell uv
        "out vec3 vToEye;\n" +    // tree→eye direction (world) → cell selection
        "out float vLodAlpha;\n" + // 0 near → 1 past uLodFar (dithered in across the band)
        "void main(){\n" +
        "  float s = aInstScaleRot.x;\n" +
        "  vec3 center = aInstPos + vec3(0.0, 0.0, 14.0 * s);\n" + // atlas framed the tree centred at z=14
        "  vec3 toEye = uCameraPos - center;\n" +
        // Vertical billboard: up = world Z, right = horizontal, perpendicular to the view azimuth, so the
        // tree stays standing and only rotates about Z to face the camera. The atlas cell (chosen per
        // fragment from the full eye direction) supplies the apparent elevation/tilt.
        "  vec3 horiz = vec3(toEye.xy, 0.0);\n" +
        "  float hl = length(horiz);\n" +
        "  vec3 right = hl > 1e-4 ? normalize(vec3(-horiz.y, horiz.x, 0.0)) : vec3(1.0, 0.0, 0.0);\n" +
        "  vec3 up = vec3(0.0, 0.0, 1.0);\n" +
        "  float halfSize = 16.0 * s;\n" + // matches the atlas ortho half-extent
        "  vec3 world = center + (right * (aCard.x * halfSize)) + (up * (aCard.y * halfSize));\n" +
        "  vCardUv = (aCard * 0.5) + 0.5;\n" +
        "  vToEye = toEye;\n" +
        // LOD: fade impostors IN across [uLodNear,uLodFar] (mirror of the mesh's fade-out), then collapse
        // the near ones (mesh's job) and the very-far ones (sub-pixel — not worth the quad).
        "  float lodDist = length(toEye);\n" +
        "  vLodAlpha = smoothstep(uLodNear, uLodFar, lodDist);\n" +
        "  gl_Position = uMvp * vec4(world, 1.0);\n" +
        "  if (lodDist < uLodNear || lodDist > uImpostorFar) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); }\n" +
        "}\n";

    private const string ForestImpostorFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vCardUv;\n" +
        "in vec3 vToEye;\n" +
        "in float vLodAlpha;\n" +
        "uniform sampler2D uAtlas;\n" +
        "uniform float uGrid;\n" +       // ForestAtlasGrid as float
        "uniform vec3 uFogColor;\n" +
        "uniform float uFogDensity;\n" +
        "out vec4 fragColor;\n" +
        // hemi-octahedral encode: a tree→eye direction (z up) → continuous atlas uv∈[0,1]². Inverse of the
        // bake's HemioctDecode, so the chosen cell matches the view the bake rendered.
        "vec2 hemioctEncode(vec3 v){\n" +
        "  v = normalize(v);\n" +
        "  v.z = max(v.z, 0.0);\n" + // clamp to the upper dome (camera dipping below the tree)
        "  float denom = max(abs(v.x) + abs(v.y) + v.z, 1e-6);\n" +
        "  vec2 o = v.xy / denom;\n" +
        "  vec2 e = vec2(o.x + o.y, o.x - o.y);\n" + // [-1,1]
        "  return (e * 0.5) + 0.5;\n" +
        "}\n" +
        "void main(){\n" +
        "  vec2 cellUv = hemioctEncode(vToEye);\n" +
        "  vec2 cell = clamp(floor(cellUv * uGrid), 0.0, uGrid - 1.0);\n" +
        "  vec2 uv = (cell + vCardUv) / uGrid;\n" +
        "  vec4 t = texture(uAtlas, uv);\n" +
        "  if (t.a < 0.5) discard;\n" + // alpha-tested silhouette (no sorting needed — trees are opaque)
        // LOD crossfade: screen-door dither the impostor IN as it approaches the band (mirror of the mesh
        // dithering out), so the swap near→far is a dissolve, not a pop.
        "  float dth = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453);\n" +
        "  if (dth > vLodAlpha) discard;\n" +
        "  float d = length(vToEye);\n" + // distance eye→tree, for aerial-perspective fog
        "  float fog = 1.0 - exp(-d * uFogDensity);\n" +
        "  fragColor = vec4(mix(t.rgb, uFogColor, fog), 1.0);\n" +
        "}\n";

    private uint forestImpostorProgram;
    private int forestImpostorMvpLocation = -1;
    private int forestImpostorCameraPosLocation = -1;
    private int forestImpostorAtlasLocation = -1;
    private int forestImpostorGridLocation = -1;
    private int forestImpostorFogColorLocation = -1;
    private int forestImpostorFogDensityLocation = -1;
    private int forestImpostorLodNearLocation = -1;
    private int forestImpostorLodFarLocation = -1;
    private int forestImpostorImpFarLocation = -1;
    private uint forestImpostorVao;
    private uint forestImpostorQuadVbo;

    private void EnsureForestProgram(GL g)
    {
        if (forestProgram != 0)
        {
            return;
        }

        uint vs = CompileShader(g, ShaderType.VertexShader, ForestVertexShaderSource);
        uint fs = CompileShader(g, ShaderType.FragmentShader, ForestFragmentShaderSource);
        forestProgram = g.CreateProgram();
        g.AttachShader(forestProgram, vs);
        g.AttachShader(forestProgram, fs);
        g.LinkProgram(forestProgram);
        g.GetProgram(forestProgram, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = g.GetProgramInfoLog(forestProgram);
            throw new InvalidOperationException("Forest shader link failed: " + log);
        }
        g.DetachShader(forestProgram, vs);
        g.DetachShader(forestProgram, fs);
        g.DeleteShader(vs);
        g.DeleteShader(fs);
        forestMvpLocation = g.GetUniformLocation(forestProgram, "uMvp");
        forestTrunkLocation = g.GetUniformLocation(forestProgram, "uTrunk");
        forestFoliageColorLocation = g.GetUniformLocation(forestProgram, "uFoliageColor");
        forestLightDirLocation = g.GetUniformLocation(forestProgram, "uLightDir");
        forestSunColorLocation = g.GetUniformLocation(forestProgram, "uSunColor");
        forestSkyAmbientLocation = g.GetUniformLocation(forestProgram, "uSkyAmbient");
        forestAmbientLocation = g.GetUniformLocation(forestProgram, "uAmbient");
        forestFogColorLocation = g.GetUniformLocation(forestProgram, "uFogColor");
        forestFogDensityLocation = g.GetUniformLocation(forestProgram, "uFogDensity");
        forestCameraPosLocation = g.GetUniformLocation(forestProgram, "uCameraPos");
        forestFadeEndLocation = g.GetUniformLocation(forestProgram, "uFadeEnd");
        forestSnowLocation = g.GetUniformLocation(forestProgram, "uSnow");
        forestWindDirLocation = g.GetUniformLocation(forestProgram, "uWindDir");
        forestWindAmpLocation = g.GetUniformLocation(forestProgram, "uWindAmp");
        forestWindTimeLocation = g.GetUniformLocation(forestProgram, "uWindTime");
        forestLodNearLocation = g.GetUniformLocation(forestProgram, "uLodNear");
        forestLodFarLocation = g.GetUniformLocation(forestProgram, "uLodFar");

        // 3-tier conifer: each tier is two crossed vertical triangles (XZ + YZ) of decreasing width going
        // up, so it reads as a tiered spruce from any orbit angle. Interleaved [pos(3), normal(3), foliage].
        // The normal is the card's outward horizontal direction, used for the half-Lambert side shading.
        (float BaseZ, float ApexZ, float HalfWidth)[] tiers =
        {
            (1.5f, 13f, 7.0f),
            (10f, 21f, 5.0f),
            (18f, 28f, 3.0f),
        };
        var vlist = new List<float>(tiers.Length * 2 * 3 * 7);
        void AddVert(float x, float y, float z, float nx, float ny, float nz)
        {
            vlist.Add(x); vlist.Add(y); vlist.Add(z);
            vlist.Add(nx); vlist.Add(ny); vlist.Add(nz);
            vlist.Add(1f); // foliage
        }
        foreach ((float baseZ, float apexZ, float hw) in tiers)
        {
            AddVert(-hw, 0f, baseZ, 0f, 1f, 0f); // XZ triangle (faces ±Y)
            AddVert(hw, 0f, baseZ, 0f, 1f, 0f);
            AddVert(0f, 0f, apexZ, 0f, 1f, 0f);
            AddVert(0f, -hw, baseZ, 1f, 0f, 0f); // YZ triangle (faces ±X)
            AddVert(0f, hw, baseZ, 1f, 0f, 0f);
            AddVert(0f, 0f, apexZ, 1f, 0f, 0f);
        }
        float[] verts = vlist.ToArray();
        forestVertexCount = verts.Length / 7;

        const int stride = 7 * sizeof(float);
        forestVao = g.GenVertexArray();
        g.BindVertexArray(forestVao);
        forestBaseVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, forestBaseVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(verts.Length * sizeof(float)), verts, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(0); // aPos
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        g.EnableVertexAttribArray(1); // aNormal
        g.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        g.EnableVertexAttribArray(2); // aFoliage
        g.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        // Per-instance buffer (filled in EnsureForestInstances) — (posX,posY,posZ, scale, yaw), divisor 1.
        forestInstanceVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, forestInstanceVbo);
        g.EnableVertexAttribArray(3); // aInstPos
        g.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
        g.VertexAttribDivisor(3, 1);
        g.EnableVertexAttribArray(4); // aInstScaleRot
        g.VertexAttribPointer(4, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)(3 * sizeof(float)));
        g.VertexAttribDivisor(4, 1);
        g.BindVertexArray(0);
    }

    // Hemi-octahedral decode: a cell-centre uv∈[0,1]² → a unit direction on the upper (+Z) hemisphere.
    // Paired with the matching encode used by the impostor draw (step 2); the four uv corners map to
    // horizon directions and the centre maps straight up, so encode∘decode round-trips on cell centres.
    private static Vector3 HemioctDecode(float u, float v)
    {
        float ex = (u * 2f) - 1f;
        float ey = (v * 2f) - 1f;
        float tx = (ex + ey) * 0.5f;
        float ty = (ex - ey) * 0.5f;
        var d = new Vector3(tx, ty, 1f - MathF.Abs(tx) - MathF.Abs(ty)); // +Z up
        return Vector3.Normalize(d);
    }

    // Bakes the conifer mesh into the impostor atlas ONCE (no-op after the first success / on failure).
    // Renders one upright unit tree per hemi-octahedral cell through an orthographic camera into an owned
    // FBO, then restores the scene's framebuffer + viewport. Reuses the forest program (wind off, fog/fade
    // off, neutral lighting) so the baked silhouette matches the live mesh.
    private unsafe void BakeForestAtlas(GL g)
    {
        if (forestAtlasUnsupported || forestAtlasTex != 0)
        {
            return; // bake once (or never, if the FBO was incomplete)
        }
        if (forestProgram == 0 || forestBaseVbo == 0 || forestVertexCount == 0)
        {
            return; // program/mesh not ready yet — try again next frame
        }

        // Remember what we interrupt: the scene's bound framebuffer + viewport, restored before returning.
        Span<int> prevFbo = stackalloc int[1];
        g.GetInteger(GLEnum.FramebufferBinding, prevFbo);
        Span<int> prevVp = stackalloc int[4];
        g.GetInteger(GLEnum.Viewport, prevVp);

        // Atlas colour texture (RGBA8) + a depth RB so the conifer's three tiers occlude correctly.
        forestAtlasTex = g.GenTexture();
        g.BindTexture(TextureTarget.Texture2D, forestAtlasTex);
        g.TexImage2D(
            TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
            ForestAtlasSize, ForestAtlasSize, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        g.BindTexture(TextureTarget.Texture2D, 0);

        forestAtlasDepthRb = g.GenRenderbuffer();
        g.BindRenderbuffer(RenderbufferTarget.Renderbuffer, forestAtlasDepthRb);
        g.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent16, ForestAtlasSize, ForestAtlasSize);
        g.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

        forestAtlasFbo = g.GenFramebuffer();
        g.BindFramebuffer(FramebufferTarget.Framebuffer, forestAtlasFbo);
        g.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, forestAtlasTex, 0);
        g.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, forestAtlasDepthRb);
        GLEnum status = g.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            Log.Information("[GL3D] forest atlas FBO incomplete ({Status}) — impostor bake disabled", status);
            g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)prevFbo[0]);
            g.DeleteFramebuffer(forestAtlasFbo);
            g.DeleteTexture(forestAtlasTex);
            g.DeleteRenderbuffer(forestAtlasDepthRb);
            forestAtlasFbo = 0;
            forestAtlasTex = 0;
            forestAtlasDepthRb = 0;
            forestAtlasUnsupported = true;
            return;
        }

        // Bake VAO: the conifer mesh (loc 0,1,2) with the per-instance attrs (loc 3,4) left DISABLED, so a
        // plain (non-instanced) DrawArrays reads their generic constant values — one upright tree at the
        // origin, unit scale, no yaw.
        forestBakeVao = g.GenVertexArray();
        g.BindVertexArray(forestBakeVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, forestBaseVbo);
        const int stride = 7 * sizeof(float);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        g.EnableVertexAttribArray(2);
        g.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));

        // Draw state for the bake: depth-tested, opaque, double-sided (the cross-cards face both ways).
        g.Enable(EnableCap.DepthTest);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
        g.Disable(EnableCap.CullFace);

        g.UseProgram(forestProgram);
        // Generic constants for the disabled instance attribs: one tree at origin, unit scale, no yaw.
        g.VertexAttrib3(3, 0f, 0f, 0f);
        g.VertexAttrib2(4, 1f, 0f);
        // Neutral, fully-lit silhouette: no fog, no fade, no snow, no wind.
        var bakeLight = Vector3.Normalize(new Vector3(0.3f, 0.3f, 0.9f));
        g.Uniform3(forestTrunkLocation, 0.30f, 0.21f, 0.13f);
        g.Uniform3(forestFoliageColorLocation, 0.10f, 0.24f, 0.12f);
        g.Uniform3(forestLightDirLocation, bakeLight.X, bakeLight.Y, bakeLight.Z);
        g.Uniform3(forestSunColorLocation, 1f, 1f, 1f);
        g.Uniform3(forestSkyAmbientLocation, 1f, 1f, 1f);
        g.Uniform1(forestAmbientLocation, 0.5f);
        g.Uniform3(forestFogColorLocation, 0f, 0f, 0f);
        g.Uniform1(forestFogDensityLocation, 0f);
        g.Uniform3(forestCameraPosLocation, 0f, 0f, 0f);
        g.Uniform1(forestFadeEndLocation, 1e9f);
        g.Uniform1(forestSnowLocation, 0f);
        g.Uniform2(forestWindDirLocation, 1f, 0f);
        g.Uniform1(forestWindAmpLocation, 0f);
        g.Uniform1(forestWindTimeLocation, 0f);

        // Clear the whole atlas to TRANSPARENT: the forest fragment shader writes alpha=1 on every tree
        // fragment, so the cleared background stays alpha=0 and the foliage's alpha=1 becomes the silhouette
        // mask the impostor billboards alpha-test against.
        g.Viewport(0, 0, (uint)ForestAtlasSize, (uint)ForestAtlasSize);
        g.ClearColor(0f, 0f, 0f, 0f);
        g.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        // One orthographic view per cell, from the cell's hemi-octahedral direction toward the tree.
        var center = new Vector3(0f, 0f, 14f); // mid-height of the ~28 m tree
        const float halfExtent = 16f;          // ortho box framing the tree (bounding radius ≈ 17)
        const float eyeDist = 80f;
        g.BindVertexArray(forestBakeVao);
        Span<float> mm = stackalloc float[16]; // reused each cell (CA2014: no stackalloc inside the loop)
        for (int jy = 0; jy < ForestAtlasGrid; jy++)
        {
            for (int ix = 0; ix < ForestAtlasGrid; ix++)
            {
                float u = (ix + 0.5f) / ForestAtlasGrid;
                float vv = (jy + 0.5f) / ForestAtlasGrid;
                Vector3 dir = HemioctDecode(u, vv);
                Vector3 eye = center + (dir * eyeDist);
                Vector3 up = MathF.Abs(dir.Z) > 0.99f ? new Vector3(0f, 1f, 0f) : new Vector3(0f, 0f, 1f);
                Matrix4x4 view = Matrix4x4.CreateLookAt(eye, center, up);
                Matrix4x4 proj = Matrix4x4.CreateOrthographic(2f * halfExtent, 2f * halfExtent, 1f, 160f);
                Matrix4x4 mvp = view * proj;
                // Same row-major upload trick as the scene MVP (transpose=false → GL reads column-major).
                mm[0] = mvp.M11; mm[1] = mvp.M12; mm[2] = mvp.M13; mm[3] = mvp.M14;
                mm[4] = mvp.M21; mm[5] = mvp.M22; mm[6] = mvp.M23; mm[7] = mvp.M24;
                mm[8] = mvp.M31; mm[9] = mvp.M32; mm[10] = mvp.M33; mm[11] = mvp.M34;
                mm[12] = mvp.M41; mm[13] = mvp.M42; mm[14] = mvp.M43; mm[15] = mvp.M44;
                g.UniformMatrix4(forestMvpLocation, 1, false, mm);
                g.Viewport(ix * ForestAtlasCell, jy * ForestAtlasCell, (uint)ForestAtlasCell, (uint)ForestAtlasCell);
                g.DrawArrays(PrimitiveType.Triangles, 0, (uint)forestVertexCount);
            }
        }
        g.BindVertexArray(0);

        // Restore the scene's framebuffer + viewport (and clear colour) so the rest of the frame is normal.
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)prevFbo[0]);
        g.Viewport(prevVp[0], prevVp[1], (uint)prevVp[2], (uint)prevVp[3]);
        g.ClearColor(SkyR, SkyG, SkyB, 1f);
        Log.Information("[GL3D] forest impostor atlas baked: {Grid}×{Grid} views @ {Cell}px", ForestAtlasGrid, ForestAtlasGrid, ForestAtlasCell);
    }

    private unsafe void EnsureForestImpostorProgram(GL g)
    {
        if (forestImpostorProgram != 0)
        {
            return;
        }

        uint vs = CompileShader(g, ShaderType.VertexShader, ForestImpostorVertexShaderSource);
        uint fs = CompileShader(g, ShaderType.FragmentShader, ForestImpostorFragmentShaderSource);
        forestImpostorProgram = g.CreateProgram();
        g.AttachShader(forestImpostorProgram, vs);
        g.AttachShader(forestImpostorProgram, fs);
        g.LinkProgram(forestImpostorProgram);
        g.GetProgram(forestImpostorProgram, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = g.GetProgramInfoLog(forestImpostorProgram);
            throw new InvalidOperationException("Forest impostor shader link failed: " + log);
        }
        g.DetachShader(forestImpostorProgram, vs);
        g.DetachShader(forestImpostorProgram, fs);
        g.DeleteShader(vs);
        g.DeleteShader(fs);
        forestImpostorMvpLocation = g.GetUniformLocation(forestImpostorProgram, "uMvp");
        forestImpostorCameraPosLocation = g.GetUniformLocation(forestImpostorProgram, "uCameraPos");
        forestImpostorAtlasLocation = g.GetUniformLocation(forestImpostorProgram, "uAtlas");
        forestImpostorGridLocation = g.GetUniformLocation(forestImpostorProgram, "uGrid");
        forestImpostorFogColorLocation = g.GetUniformLocation(forestImpostorProgram, "uFogColor");
        forestImpostorFogDensityLocation = g.GetUniformLocation(forestImpostorProgram, "uFogDensity");
        forestImpostorLodNearLocation = g.GetUniformLocation(forestImpostorProgram, "uLodNear");
        forestImpostorLodFarLocation = g.GetUniformLocation(forestImpostorProgram, "uLodFar");
        forestImpostorImpFarLocation = g.GetUniformLocation(forestImpostorProgram, "uImpostorFar");

        // Base quad (triangle strip, [-1,1]²) shared by every instance; the instance attribs come from the
        // existing per-tree buffer with divisor 1.
        float[] quad = { -1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f };
        forestImpostorVao = g.GenVertexArray();
        g.BindVertexArray(forestImpostorVao);
        forestImpostorQuadVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, forestImpostorQuadVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), quad, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(0); // aCard
        g.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        // Per-instance (posX,posY,posZ, scale, yaw) — same buffer as the mesh pass, divisor 1.
        g.BindBuffer(BufferTargetARB.ArrayBuffer, forestInstanceVbo);
        g.EnableVertexAttribArray(1); // aInstPos
        g.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
        g.VertexAttribDivisor(1, 1);
        g.EnableVertexAttribArray(2); // aInstScaleRot
        g.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)(3 * sizeof(float)));
        g.VertexAttribDivisor(2, 1);
        g.BindVertexArray(0);
    }

    private void DrawForestImpostors(GL g, ReadOnlySpan<float> mvp, Camera3D camera, Atmosphere? atmosphere)
    {
        if (forestInstanceCount == 0 || forestImpostorProgram == 0 || forestAtlasTex == 0)
        {
            return;
        }

        g.UseProgram(forestImpostorProgram);
        g.UniformMatrix4(forestImpostorMvpLocation, 1, false, mvp);
        Vector3 cam = camera.Position;
        g.Uniform3(forestImpostorCameraPosLocation, cam.X, cam.Y, cam.Z);
        g.Uniform1(forestImpostorGridLocation, (float)ForestAtlasGrid); // float uniform — int overload (glUniform1i) silently no-ops it to 0
        g.Uniform1(forestImpostorLodNearLocation, ForestLodNearMeters);
        g.Uniform1(forestImpostorLodFarLocation, ForestLodFarMeters);
        g.Uniform1(forestImpostorImpFarLocation, ForestFadeEndMeters);
        Vector3 fog = atmosphere?.FogColor ?? Vector3.Zero;
        g.Uniform3(forestImpostorFogColorLocation, fog.X, fog.Y, fog.Z);
        g.Uniform1(forestImpostorFogDensityLocation, atmosphere?.FogDensity ?? 0f);

        g.ActiveTexture(TextureUnit.Texture0);
        g.BindTexture(TextureTarget.Texture2D, forestAtlasTex);
        g.Uniform1(forestImpostorAtlasLocation, 0);

        g.BindVertexArray(forestImpostorVao);
        g.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, (uint)forestInstanceCount);
        g.BindVertexArray(0);
    }

    // Uploads the per-tree instance buffer when the forest list reference changes (placement is rebuilt
    // by the view only on DEM / density change, so this is a rare upload, not per-frame).
    private void EnsureForestInstances(GL g, IReadOnlyList<TreeInstance> forest)
    {
        if (ReferenceEquals(lastForest, forest))
        {
            return;
        }
        lastForest = forest;
        forestInstanceCount = forest.Count;
        if (forestInstanceCount == 0)
        {
            return;
        }

        var data = new float[forestInstanceCount * 5];
        for (int i = 0; i < forestInstanceCount; i++)
        {
            TreeInstance t = forest[i];
            int o = i * 5;
            data[o] = t.Position.X;
            data[o + 1] = t.Position.Y;
            data[o + 2] = t.Position.Z;
            data[o + 3] = t.Scale;
            data[o + 4] = t.RotationRadians;
        }
        g.BindBuffer(BufferTargetARB.ArrayBuffer, forestInstanceVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(data.Length * sizeof(float)), data, BufferUsageARB.StaticDraw);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    private void DrawForest(GL g, ReadOnlySpan<float> mvp, Camera3D camera, Atmosphere? atmosphere, Vector2 windVec, float weatherT)
    {
        if (forestInstanceCount == 0 || forestVertexCount == 0)
        {
            return;
        }

        g.UseProgram(forestProgram);
        g.UniformMatrix4(forestMvpLocation, 1, false, mvp);
        g.Uniform3(forestTrunkLocation, 0.30f, 0.21f, 0.13f);
        g.Uniform3(forestFoliageColorLocation, 0.10f, 0.24f, 0.12f); // dark spruce green

        Vector3 light = atmosphere?.SunDirection ?? new Vector3(0f, 0f, 1f);
        Vector3 sun = Vector3.One;
        Vector3 sky = Vector3.One;
        float ambient = 0.5f;
        if (atmosphere is not null)
        {
            sun = Vector3.Lerp(atmosphere.SunColor, Vector3.One, 0.25f) * 1.1f;
            sky = Vector3.Lerp(atmosphere.SkyZenithColor, Vector3.One, 0.5f);
            ambient = atmosphere.AmbientFactor;
        }
        g.Uniform3(forestLightDirLocation, light.X, light.Y, light.Z);
        g.Uniform3(forestSunColorLocation, sun.X, sun.Y, sun.Z);
        g.Uniform3(forestSkyAmbientLocation, sky.X, sky.Y, sky.Z);
        g.Uniform1(forestAmbientLocation, ambient);

        Vector3 fog = atmosphere?.FogColor ?? Vector3.Zero;
        g.Uniform3(forestFogColorLocation, fog.X, fog.Y, fog.Z);
        g.Uniform1(forestFogDensityLocation, atmosphere?.FogDensity ?? 0f);
        Vector3 cam = camera.Position;
        g.Uniform3(forestCameraPosLocation, cam.X, cam.Y, cam.Z);
        g.Uniform1(forestFadeEndLocation, ForestFadeEndMeters);
        g.Uniform1(forestLodNearLocation, ForestLodNearMeters);
        g.Uniform1(forestLodFarLocation, ForestLodFarMeters);
        g.Uniform1(forestSnowLocation, atmosphere?.SnowAmount ?? 0f);

        // Wind: direction from the live drift vector (fallback +X), amplitude from the wind strength.
        Vector2 dir = windVec.LengthSquared() > 1e-9f ? Vector2.Normalize(windVec) : new Vector2(1f, 0f);
        float amp = (atmosphere?.Wind ?? 0.3f) * 2.0f; // metres of apex sway at full gale
        g.Uniform2(forestWindDirLocation, dir.X, dir.Y);
        g.Uniform1(forestWindAmpLocation, amp);
        g.Uniform1(forestWindTimeLocation, weatherT);

        g.BindVertexArray(forestVao);
        g.DrawArraysInstanced(PrimitiveType.Triangles, 0, (uint)forestVertexCount, (uint)forestInstanceCount);
        g.BindVertexArray(0);
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
            gl.DeleteProgram(cloudProgram);
            gl.DeleteVertexArray(cloudVao);
            gl.DeleteBuffer(cloudVbo);
            skyProgram = 0;
            skyVao = 0;
            skyVbo = 0;
            cloudProgram = 0;
            cloudVao = 0;
            cloudVbo = 0;
            programReady = false;
        }
        if (forestProgram != 0)
        {
            gl.DeleteProgram(forestProgram);
            gl.DeleteVertexArray(forestVao);
            gl.DeleteBuffer(forestBaseVbo);
            gl.DeleteBuffer(forestInstanceVbo);
            forestProgram = 0;
            forestVao = 0;
            forestBaseVbo = 0;
            forestInstanceVbo = 0;
        }
        if (forestAtlasTex != 0)
        {
            gl.DeleteFramebuffer(forestAtlasFbo);
            gl.DeleteTexture(forestAtlasTex);
            gl.DeleteRenderbuffer(forestAtlasDepthRb);
            gl.DeleteVertexArray(forestBakeVao);
            forestAtlasFbo = 0;
            forestAtlasTex = 0;
            forestAtlasDepthRb = 0;
            forestBakeVao = 0;
        }
        if (forestImpostorProgram != 0)
        {
            gl.DeleteProgram(forestImpostorProgram);
            gl.DeleteVertexArray(forestImpostorVao);
            gl.DeleteBuffer(forestImpostorQuadVbo);
            forestImpostorProgram = 0;
            forestImpostorVao = 0;
            forestImpostorQuadVbo = 0;
        }
    }
}