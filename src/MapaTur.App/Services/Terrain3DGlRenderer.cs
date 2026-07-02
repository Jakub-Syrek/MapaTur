// Cross-platform: every TFM where SkiaSharp's SKGLView exposes a live OpenGL ES context
// (Windows ANGLE, Android system GLES, iOS/Mac Catalyst OpenGLES.framework) shares this
// renderer. Library loading lives in PlatformGl so the renderer itself stays GL-only.
using System.Numerics;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;
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
        "layout(location=4) in float aDetail;\n" + // per-vertex mid-freq relief amplitude (m RMS); 0 = none
        "uniform mat4 uMvp;\n" +
        // Wider-coverage TWO-FRAME scheme (so a future re-anchor never disturbs procedural effects):
        //   uModelOffset  → RENDER frame (small, near the camera): drives gl_Position + vWorldPos, which feed the
        //                   VIEW-DEPENDENT terms (view direction, camera distance, fog). Re-anchor moves this.
        //   uStableOffset → STABLE/global frame: drives vStableWorldPos, which feeds ALL procedural sampling
        //                   (noise, ripples, rock/material, cloud field, water shape). Re-anchor MUST NOT move it.
        // Today every mesh shares the scene origin so BOTH offsets are (0,0,0) → vWorldPos == vStableWorldPos == aPos
        // → a strict no-op. They diverge only once the scene re-anchors (P0 step 6), keeping geometry near the
        // camera (precision) while noise stays pinned to the world (no drift).
        "uniform vec3 uModelOffset;\n" +
        "uniform vec3 uStableOffset;\n" +
        "out vec4 vColor;\n" +
        "out vec3 vNormal;\n" +
        "out vec2 vTex;\n" +
        "out vec3 vWorldPos;\n" +
        "out vec3 vStableWorldPos;\n" +
        "out float vDetail;\n" +
        "void main(){ vColor = aColor; vNormal = aNormal; vTex = aTex; vDetail = aDetail; vec3 worldPos = aPos + uModelOffset; vWorldPos = worldPos; vStableWorldPos = aPos + uStableOffset; gl_Position = uMvp * vec4(worldPos, 1.0); }\n";

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
        "in vec3 vWorldPos;\n" +          // RENDER frame — view-dependent terms only (view dir, camera distance, fog)
        "in vec3 vStableWorldPos;\n" +    // STABLE/global frame — all procedural sampling (noise/ripple/rock/cloud/water shape)
        "in float vDetail;\n" +           // per-vertex mid-freq relief amplitude (m RMS) discarded by the coarse LOD; 0 = none
        "uniform vec3 uLightDir;\n" +
        "uniform vec3 uSnowSun;\n" + // sun used for the SNOW-line sun-melt; pinned during a film so the cover holds while lighting sweeps
        "uniform float uAmbient;\n" +
        "uniform vec3 uSunColor;\n" +    // direct-sun colour (warm at sunset, white at noon)
        "uniform vec3 uSkyAmbient;\n" +  // ambient sky-fill colour for shadowed slopes
        "uniform sampler2D uOrtho;\n" +
        "uniform int uUseOrtho;\n" +
        "uniform float uOrthoGlobalFade;\n" +  // 1 = full ortho, 0 = hypsometric ("2D map" mode fade)
        "uniform vec2 uOrthoTexel;\n" + // (1/width, 1/height) of the bound ortho texture
        "uniform vec2 uOrthoMinXY;\n" +     // ortho coverage AABB (world XY about the scene anchor) — beyond it the UV clamps
        "uniform vec2 uOrthoMaxXY;\n" +
        "uniform float uOrthoBlendMeters;\n" + // soft fade ortho→hypsometric at the coverage edge; 0 = no cull (pure ortho)
        "uniform float uSlopeMode;\n" +     // 1 = avalanche slope-steepness map (overrides ortho/hypsometric)
        "uniform vec3 uSlopePalette[8];\n" + // band colours (0-20…80-90°), from SlopePalette
        "uniform float uSharpen;\n" +   // unsharp-mask strength; 0 = off
        "uniform float uDebugUv;\n" +   // DIAGNOSTIC: 1 = render the raw ortho UV as colour (R=U, G=V)
        "uniform float uRockStrength;\n" + // rock-material-on-steep blend strength; 0 = off (pure ortho)
        "uniform vec3 uFogColor;\n" +
        "uniform float uFogDensity;\n" + // per-metre exponential; 0 = no aerial perspective
        "uniform vec3 uCameraPos;\n" +
        // Cascaded Shadow Maps (Krok 5 part 4): 3 cascade depth maps + their light view-projections + the
        // cascade split far-distances. uShadowStrength 0 = off (night / disabled), 1 = full.
        "uniform highp sampler2DShadow uShadowMap0;\n" +
        "uniform highp sampler2DShadow uShadowMap1;\n" +
        "uniform highp sampler2DShadow uShadowMap2;\n" +
        "uniform mat4 uCascadeVp0;\n" +
        "uniform mat4 uCascadeVp1;\n" +
        "uniform mat4 uCascadeVp2;\n" +
        "uniform vec3 uCascadeSplit;\n" +
        "uniform float uShadowStrength;\n" +
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
        "uniform float uSnowSlopeCosBare;\n" + // cos(steep angle): at/below this n.z the face is bare rock
        "uniform float uSnowSlopeCosFull;\n" + // cos(gentle angle): at/above this n.z snow fully holds
        "uniform float uNoonSnowLift;\n" + // extra white-lift for snow at high (noon) sun, 0..~0.30 (NoonLightModel)
                                           // Elevation-zone biomes ("Biomy"): paint the base albedo by alpine zonation — meadow/hala low,
                                           // scree/piargi mid, snow/ice high — from elevation (vWorldPos.z, world-Z = metres×Pion), slope and
                                           // aspect (northness). Mirrors the unit-tested BiomeClassifier; the granite rock material (rockW) and
                                           // the dynamic snow slider still layer on top. uBiomeMode (0/1) gates it; thresholds are world-Z.
        "uniform float uBiomeMode;\n" +
        "uniform float uBiomeScreeSlopeDeg;\n" + // slope at/above which non-rock ground reads as talus
        "uniform float uBiomeMeadowMaxZ;\n" +    // world-Z above which gentle ground stops being meadow
        "uniform float uBiomeSnowZ;\n" +         // aspect-adjusted world-Z snowline
        "uniform float uBiomeIceZ;\n" +          // aspect-adjusted world-Z iceline
        "uniform float uBiomeAspectShiftZ;\n" +  // world-Z the snow/ice lines shift with full N/S aspect
        "uniform vec3 uBiomePalette[5];\n" +     // Meadow, Scree, Rock, Snow, Ice — from BiomePalette
        "uniform float uDebugPoly;\n" +          // 1 = the lake-water fill pass
        "uniform vec2 uLakeCenter;\n" +          // lake centroid (world XY) — for a SMOOTH radial depth (no fan-edge creases)
        "uniform float uLakeRadius;\n" +         // lake max radius (m), for the depth falloff
                                                 // Planar water reflection: a pre-pass renders the terrain mirrored about the lake plane into a texture;
                                                 // the lake mesh then samples it (screen-space, ripple-distorted) so the real peaks reflect in the water.
        "uniform float uReflectionPass;\n" +     // 1 while rendering the mirrored reflection texture (clip below water)
        "uniform float uWaterClipZ;\n" +         // world-Z of the lake plane; in the reflection pass, fragments below it are discarded
        "uniform sampler2D uReflectionTex;\n" +  // the mirrored-terrain reflection texture (sampled by the lake mesh)
        "uniform float uReflectionEnabled;\n" +  // 1 = sample the real reflection; 0 = use the cheap sky-gradient fallback
        "uniform vec2 uViewportPx;\n" +          // main viewport size in pixels (for the screen-space reflection UV)
        "uniform float uContourSpacingZ;\n" +    // world-Z between contour lines (= interval m × Pion); >0 when on
        "uniform vec3 uContourColor;\n" +        // warstwice line tint
        "uniform float uContourStrength;\n" +    // 0 disables the contour overlay
        "uniform float uContourWidthPx;\n" +     // contour half-width in pixels (fwidth-based AA, constant on screen)
        "uniform float uContourMajorSpacingZ;\n" + // world-Z between RED index (major) lines (= 100 m × Pion)
        "uniform vec3 uContourMajorColor;\n" +     // index (major) line tint — red
                                                   // Trail/route decal: an RGBA8 "painted-distance" mask (RGB = nearest line colour, A = coverage) over a
                                                   // world-XY window. Sampled by THIS fragment's stable world-XY, so trails are painted INTO the surface on
                                                   // BOTH the coarse base and the streamed 1 m detail (same shader) — never floating, never occluded. Like the
                                                   // contours above. uTrailStrength 0 = off.
        "uniform sampler2D uTrailMask;\n" +
        "uniform float uTrailStrength;\n" +
        "uniform sampler2D uWaterMask;\n" +   // parallel R8 watercourse distance field (same window as uTrailMask)
        "uniform float uWaterStrength;\n" +   // 0 = no water layer this frame
        "uniform vec2 uTrailMaskMinXY;\n" +   // world-XY of the mask window's min corner (= uv 0,0)
        "uniform vec2 uTrailMaskSizeXY;\n" +  // world-XY extent of the mask window (metres)
        "uniform float uTrailMaxDist;\n" +    // distance-field reach (m): metric dist = (1 - A) * uTrailMaxDist
        "uniform float uTrailHalfWidth;\n" +  // on-surface half-width (m) the line is drawn at from the distance
        "out vec4 fragColor;\n" +
        // p is always an INTEGER lattice point (noiseT calls this only at floor(p) + a {0,1} offset). For terrain
        // far from the scene's fixed anchor (uStableOffset is always 0 — see CameraRelativeTerrainOrigin — so
        // vStableWorldPos is METRES FROM ANCHOR, unbounded, easily thousands of metres for a Tatra-wide view),
        // dot(p, c) blows straight past the GLSL ES sin()/cos() spec's guaranteed-precision window of
        // [-8192, 8192] radians — at just ~75-184 m from the anchor with these hash constants. Past that, sin()'s
        // ULP-quantised phase jitters by several degrees per representable float, which reads as visible
        // aliasing/regular banding, not smooth pseudo-random noise — exactly the "ratrak"/grid look on far
        // terrain. Wrapping the lattice coordinate into [0,16) BEFORE the dot product keeps every hash lookup's
        // sin() argument comfortably inside the guaranteed-precision range (16*(127.1+311.7) ≈ 7021 rad < 8192),
        // at the cost of the noise field repeating every 16 lattice cells — for the finest caller (sc=0.35, the
        // rock/detail-normal blocks) that is a ~46 m world-space tile, large next to their ~2.9 m wavelength.
        "float hashT(vec2 p){ vec2 pw = mod(p, 16.0); return fract(sin(dot(pw, vec2(127.1, 311.7))) * 43758.5453); }\n" +
        "float noiseT(vec2 p){\n" +
        "  vec2 i = floor(p); vec2 f = fract(p);\n" +
        "  f = f * f * (3.0 - 2.0 * f);\n" +
        "  return mix(mix(hashT(i), hashT(i + vec2(1.0,0.0)), f.x),\n" +
        "             mix(hashT(i + vec2(0.0,1.0)), hashT(i + vec2(1.0,1.0)), f.x), f.y);\n" +
        "}\n" +
        "float fbmT(vec2 p){ float v=0.0,a=0.5; for(int i=0;i<5;i++){ v+=a*noiseT(p); p*=2.0; a*=0.5;} return v; }\n" +
        // B-spline bicubic via 4 bilinear fetches — used ONLY when the ortho is MAGNIFIED (camera close enough
        // that one texel covers >1 screen px). Plain bilinear magnification renders each ~1-4 m ortho texel as a
        // hard-edged square ("pixeloza z bliska"); the cubic kernel replaces those edges with a smooth ramp for
        // 3 extra fetches. textureLod(0) keeps the taps defined inside the magnification branch (magnified ⇒
        // mip 0 anyway; implicit-derivative texture() there would be undefined in non-uniform flow).
        "vec4 cubicW(float v){\n" +
        "  vec4 n = vec4(1.0, 2.0, 3.0, 4.0) - v;\n" +
        "  vec4 s = n * n * n;\n" +
        "  float x = s.x;\n" +
        "  float y = s.y - 4.0 * x;\n" +
        "  float z = s.z - 4.0 * y - 6.0 * x;\n" +
        "  float w = 6.0 - x - y - z;\n" +
        "  return vec4(x, y, z, w) * (1.0 / 6.0);\n" +
        "}\n" +
        "vec3 texBicubic(sampler2D t, vec2 uv, vec2 ts){\n" +
        "  vec2 coord = uv * ts - 0.5;\n" +
        "  vec2 fxy = fract(coord);\n" +
        "  coord -= fxy;\n" +
        "  vec4 xc = cubicW(fxy.x);\n" +
        "  vec4 yc = cubicW(fxy.y);\n" +
        "  vec4 cx = coord.xxyy + vec2(-0.5, 1.5).xyxy;\n" +
        "  vec4 s = vec4(xc.xz + xc.yw, yc.xz + yc.yw);\n" +
        "  vec4 off = cx + vec4(xc.yw, yc.yw) / s;\n" +
        "  off *= vec4(1.0 / ts.x, 1.0 / ts.x, 1.0 / ts.y, 1.0 / ts.y);\n" +
        "  vec3 s00 = textureLod(t, vec2(off.x, off.z), 0.0).rgb;\n" +
        "  vec3 s10 = textureLod(t, vec2(off.y, off.z), 0.0).rgb;\n" +
        "  vec3 s01 = textureLod(t, vec2(off.x, off.w), 0.0).rgb;\n" +
        "  vec3 s11 = textureLod(t, vec2(off.y, off.w), 0.0).rgb;\n" +
        "  float sx = s.x / (s.x + s.y);\n" +
        "  float sy = s.z / (s.z + s.w);\n" +
        "  return mix(mix(s11, s01, sx), mix(s10, s00, sx), sy);\n" +
        "}\n" +
        // Cascaded Shadow Maps (Krok 5 part 4): 3x3 PCF of one cascade (hardware depth compare), then pick the
        // cascade by camera-space view distance, project the ABSOLUTE world position into its light space, and
        // compare with a slope-scaled bias. Returns 1 = fully lit, →0 = shadowed (scaled by uShadowStrength).
        "float pcfShadow(highp sampler2DShadow sm, vec2 uv, float depthRef){\n" +
        "  float t = 1.0 / 1024.0;\n" +
        "  float s = 0.0;\n" +
        "  for (int x = -1; x <= 1; x++) {\n" +
        "    for (int y = -1; y <= 1; y++) {\n" +
        "      s += texture(sm, vec3(uv + (vec2(float(x), float(y)) * t), depthRef));\n" +
        "    }\n" +
        "  }\n" +
        "  return s / 9.0;\n" +
        "}\n" +
        "float csmShadow(float viewDist, vec3 worldPos, float ndotl){\n" +
        "  if (uShadowStrength < 0.001) return 1.0;\n" +
        "  int ci = (viewDist < uCascadeSplit.x) ? 0 : ((viewDist < uCascadeSplit.y) ? 1 : 2);\n" +
        "  mat4 vp = (ci == 0) ? uCascadeVp0 : ((ci == 1) ? uCascadeVp1 : uCascadeVp2);\n" +
        "  vec4 lc = vp * vec4(worldPos, 1.0);\n" +
        "  vec3 p = lc.xyz / lc.w;\n" +
        "  p = (p * 0.5) + 0.5;\n" +
        "  if (p.z >= 1.0 || p.x < 0.0 || p.x > 1.0 || p.y < 0.0 || p.y > 1.0) return 1.0;\n" +
        "  float bias = max(0.0025 * (1.0 - ndotl), 0.0007);\n" +
        "  float d = p.z - bias;\n" +
        "  float lit = (ci == 0) ? pcfShadow(uShadowMap0, p.xy, d)\n" +
        "            : ((ci == 1) ? pcfShadow(uShadowMap1, p.xy, d) : pcfShadow(uShadowMap2, p.xy, d));\n" +
        "  return mix(1.0, lit, uShadowStrength);\n" +
        "}\n" +
        "void main(){\n" +
        // Reflection pre-pass: we're rendering the terrain MIRRORED about the lake plane into the reflection
        // texture. Discard anything below the waterline so only the above-water peaks end up in the reflection.
        "  if (uReflectionPass > 0.5 && vWorldPos.z < uWaterClipZ) { discard; }\n" +
        // Lake water (drawn as the polygon fill, uDebugPoly=1): depth-tinted bottom (turquoise rim → navy centre
        // via vColor.r), Fresnel mix with a sky-gradient reflection, gentle ripples + a tight sun glint, and a
        // depth-faded alpha (shallow shore semi-transparent). Flat normal → no patchwork; polygon → no spill.
        "  if (uDebugPoly > 0.5) {\n" +
        "    vec3 viewW = normalize(uCameraPos - vWorldPos);\n" +
        "    float depthF = 1.0 - smoothstep(uLakeRadius * 0.15, uLakeRadius * 0.95, distance(vStableWorldPos.xy, uLakeCenter));\n" + // SMOOTH radial (stable frame: lake shape pinned to the world)

        "    float tW = uCloudTime;\n" +
        "    vec2 wp = (vStableWorldPos.xy * 0.045) + (uCloudWind * tW * 0.5) + (tW * vec2(0.08, 0.05));\n" +
        "    float we = 0.55;\n" +
        "    float wx = noiseT(wp + vec2(we, 0.0)) - noiseT(wp - vec2(we, 0.0));\n" +
        "    float wy = noiseT(wp + vec2(0.0, we)) - noiseT(wp - vec2(0.0, we));\n" +
        "    float rippleFade = mix(0.4, 1.0, 1.0 - smoothstep(600.0, 2600.0, length(uCameraPos - vWorldPos)));\n" +
        "    vec3 wn = normalize(vec3(vec2(wx, wy) * 0.085 * rippleFade, 1.0));\n" + // more surface tilt → reads as a wind-rippled lake, not smooth resin
        "    vec3 bottomCol = mix(vec3(0.18, 0.38, 0.40), vec3(0.03, 0.08, 0.16), depthF);\n" + // darker own-colour (less blue paint), keep turquoise rim hue
        "    vec3 reflFlat = reflect(-viewW, vec3(0.0, 0.0, 1.0));\n" +
        "    float skyAmt = smoothstep(-0.05, 0.50, reflFlat.z);\n" +
        "    vec3 reflCol = mix(vec3(0.12, 0.15, 0.18), mix(uSkyAmbient, vec3(0.42, 0.64, 0.82), 0.62), skyAmt);\n" + // sky-gradient FALLBACK
                                                                                                                      // Real mirrored-terrain reflection: sample the pre-pass texture at this fragment's screen position +
                                                                                                                      // a small ripple-driven wobble, tinted toward water-blue so it reads as a LAKE, not a glass mirror.
        "    if (uReflectionEnabled > 0.5) {\n" +
        "      vec2 rUv = (gl_FragCoord.xy / uViewportPx) + (vec2(wx, wy) * 0.020 * rippleFade);\n" + // stronger ripple wobble breaks the mesh/LOD traces in the reflection
        "      vec3 mtn = texture(uReflectionTex, clamp(rUv, 0.001, 0.999)).rgb;\n" +
        "      reflCol = mix(mtn, vec3(0.12, 0.28, 0.38), 0.06);\n" + // less blue wash → punchier, sharper (glossier) mirror
        "    }\n" +
        "    float fresW = clamp(pow(1.0 - max(dot(wn, viewW), 0.0), 5.0), 0.20, 0.99);\n" + // sharper Fresnel: clearer water face-on, stronger mirror at grazing → glossier contrast
        "    vec3 wcol = mix(bottomCol, reflCol, fresW);\n" +
        // Sun glint: a TIGHT specular where the RIPPLED surface (wn, not the flat plane) reflects the sun to the
        // eye → concentrated sparkles along the sun path, NOT one hard streak (that was the old bug). Sun-up gate
        // kills it at night; high power keeps each sparkle small; modest intensity avoids a blown-out blob.
        "    vec3 sd = normalize(uLightDir);\n" +
        "    float sunUp = smoothstep(0.0, 0.12, sd.z);\n" +
        // Sun glint doubled in strength + a broader soft sheen underneath, so lake water visibly SHINES
        // ("błyszczenie wody w jeziorach") instead of reading as a matte tinted plate. Both driven by the sun
        // (not the camera azimuth) and gated by sunUp, so night water stays calm.
        "    float glint = pow(max(dot(wn, normalize(viewW + sd)), 0.0), 200.0);\n" +
        "    float sheen = pow(max(dot(wn, normalize(viewW + sd)), 0.0), 24.0);\n" +
        "    wcol += vec3(1.0, 0.96, 0.86) * (glint * 0.85 * sunUp);\n" +
        "    wcol += vec3(0.55, 0.62, 0.66) * (sheen * 0.22 * sunUp);\n" +
        "    wcol = clamp(wcol, 0.0, 1.0);\n" +
        "    float waterAlpha = mix(0.15, 0.60, smoothstep(0.0, 0.5, depthF));\n" + // shore kept glassy (0.15, unchanged); deep ~27% less opaque

        "    fragColor = vec4(wcol, waterAlpha);\n" +
        "    return;\n" +
        "  }\n" +
        // Cloud shadow: march from this fragment toward the sun (uLightDir) up to the cloud plane,
        // sample the identical animated cloud field, and darken the DIRECT-sun term where a cloud
        // blocks the ray. Only when the sun is meaningfully above the horizon (uLightDir.z) — at
        // grazing angles the projection length explodes and shadows would smear.
        "  float sunShadow = 0.0;\n" +
        "  if (uCloudShadow > 0.001 && uCloudCoverage > 0.001 && uLightDir.z > 0.12) {\n" +
        "    float tt = (uCloudAltitude - vWorldPos.z) / uLightDir.z;\n" +
        "    if (tt > 0.0) {\n" +
        "      vec2 cp = vStableWorldPos.xy + (uLightDir.xy * tt);\n" +
        "      vec2 p = cp * uCloudNoiseScale + uCloudWind * uCloudTime;\n" +
        "      vec2 warp = vec2(fbmT(p * 0.5 + uCloudTime * 0.010),\n" +
        "                       fbmT(p * 0.5 + vec2(5.2, 1.3) + uCloudTime * 0.012));\n" +
        "      float n = fbmT(p + (warp - 0.5) * 1.6);\n" +
        "      float thr = 0.62 - (uCloudCoverage * 0.42);\n" + // match the cumulus / sea-of-clouds layer threshold
        "      sunShadow = smoothstep(thr, thr + 0.20, n);\n" +
        "    }\n" +
        "  }\n" +
        // COLOURED lighting: shadowed slopes get the cool sky-ambient fill, sun-facing slopes get
        // the warm direct-sun colour scaled by Lambert AND attenuated by any cloud blocking the
        // sun. Tinting (not just dimming) makes the terrain read as genuinely sunlit; the cloud
        // shadow term adds the moving dappled light that sells "sun + clouds" at any time of day.
        // Rock material (steep faces): compute the slope weight + a triplanar granite noise field BEFORE
        // lighting, then tilt the shading normal by the noise gradient (tangent detail normal) so the granite
        // CATCHES THE SUN — sun-lit bumps + shaded crevices instead of a flat plate. rk/rockW are reused for
        // the albedo blend below. Gentle ground keeps rockW=0 (shN = vNormal), so its lighting is unchanged.
        "  vec3 shN = normalize(vNormal);\n" +
        "  float rockSlopeDeg = degrees(acos(clamp(shN.z, 0.0, 1.0)));\n" +
        // 55→75°: granite claims only NEAR-VERTICAL faces, where the top-down ortho drape is genuinely
        // smeared. The old 40→65° band also swallowed 40–60° slopes that have crisp, real rock texture in
        // the imagery (Orla Perć read worse WITH the material than with plain ortho — user 2026-07-02);
        // the Slovak big north faces are 65°+ so they keep their granite rescue.
        "  float rockW = (uSlopeMode < 0.5) ? smoothstep(55.0, 75.0, rockSlopeDeg) * uRockStrength : 0.0;\n" +
        "  float rk = 0.0;\n" +
        "  if (rockW > 0.001) {\n" +
        "    vec3 an = abs(shN); float bw = an.x + an.y + an.z + 0.0001;\n" +
        "    float sc = 0.35;\n" + // cycles per metre (~3 m granite blotches); 2nd octave at 2.7x
        "    float nA = noiseT(vStableWorldPos.yz * sc) + 0.5 * noiseT(vStableWorldPos.yz * sc * 2.7);\n" +
        "    float nB = noiseT(vStableWorldPos.zx * sc) + 0.5 * noiseT(vStableWorldPos.zx * sc * 2.7);\n" +
        "    float nC = noiseT(vStableWorldPos.xy * sc) + 0.5 * noiseT(vStableWorldPos.xy * sc * 2.7);\n" +
        "    rk = clamp((((((nA * an.x) + (nB * an.y) + (nC * an.z)) / bw) / 1.5) - 0.5) * 1.55 + 0.5, 0.0, 1.0);\n" +
        // Detail normal: central-difference the noise on the dominant world plane, project the tilt into the
        // surface tangent (subtract the normal component), and bend the shading normal by it. Bounded by rockW.
        "    vec2 dp = (an.z >= an.x && an.z >= an.y) ? vStableWorldPos.xy : ((an.x >= an.y) ? vStableWorldPos.yz : vStableWorldPos.zx);\n" +
        "    float e = 0.8;\n" +
        "    float gx = noiseT((dp + vec2(e, 0.0)) * sc) - noiseT((dp - vec2(e, 0.0)) * sc);\n" +
        "    float gy = noiseT((dp + vec2(0.0, e)) * sc) - noiseT((dp - vec2(0.0, e)) * sc);\n" +
        "    vec3 bvec = vec3(-gx, -gy, 0.0);\n" +
        "    bvec = bvec - shN * dot(bvec, shN);\n" +
        "    shN = normalize(shN + (0.6 * rockW) * bvec);\n" +
        "  }\n" +
        // Mid-frequency DETAIL (fix B): a coarse LOD tile box-averaged away the sub-cell bumps; vDetail carries the
        // REAL z16 residual RMS (metres) per vertex — it is 0 on flat ground, on the finest z16 (relief already in
        // geometry) and on old tiles, so this is a strict no-op there. Where vDetail>0 we shade the tile AS IF it
        // still had those bumps: central-difference a stable-world noise field, project the tilt into the surface
        // tangent (same trick as the rock block), and bend the SHADING normal by the REAL amplitude. Look-only —
        // it never touches geometry, biome/snow bands (those read vNormal directly), depth or the reflection clip.
        // Triplanar-style plane pick (mirrors the rock block's `dp`): sampling noise ALWAYS on world XY stretches
        // it 1/cos(slope) along the fall line on anything but near-flat ground — on real Tatra bowls/slopes that
        // reads as regular parallel grooves ("jak ślady ratraka"), not organic texture. Picking whichever plane
        // the shading normal is LEAST perpendicular to keeps the noise's true scale on any slope.
        "  if (vDetail > 0.01 && uReflectionPass < 0.5) {\n" +
        "    float dsc = 0.13;\n" +   // bigger "stones" (~7.7 m wavelength) than the rock block's fine granite blotches
        "    float de = 1.5;\n" +
        "    vec3 anD = abs(shN);\n" +
        "    vec2 dpD = (anD.z >= anD.x && anD.z >= anD.y) ? vStableWorldPos.xy : ((anD.x >= anD.y) ? vStableWorldPos.yz : vStableWorldPos.zx);\n" +
        "    float gxD = (noiseT((dpD + vec2(de, 0.0)) * dsc) + 0.5 * noiseT((dpD + vec2(de, 0.0)) * dsc * 2.7))\n" +
        "              - (noiseT((dpD - vec2(de, 0.0)) * dsc) + 0.5 * noiseT((dpD - vec2(de, 0.0)) * dsc * 2.7));\n" +
        "    float gyD = (noiseT((dpD + vec2(0.0, de)) * dsc) + 0.5 * noiseT((dpD + vec2(0.0, de)) * dsc * 2.7))\n" +
        "              - (noiseT((dpD - vec2(0.0, de)) * dsc) + 0.5 * noiseT((dpD - vec2(0.0, de)) * dsc * 2.7));\n" +
        "    vec3 bvecD = vec3(-gxD, -gyD, 0.0);\n" +
        "    bvecD = bvecD - shN * dot(bvecD, shN);\n" +
        "    float dStr = clamp(vDetail * 0.35, 0.0, 0.85);\n" + // TEST: gain/cap raised ~7x to match the rock block's visible strength for measured 0.5-2 m RMS
        "    shN = normalize(shN + dStr * bvecD);\n" +
        "  }\n" +
        "  float lambert = max(0.0, dot(shN, uLightDir));\n" +
        "  float sunlit = lambert * (1.0 - uAmbient) * (1.0 - (sunShadow * uCloudShadow));\n" +
        // CSM: attenuate the direct-sun term where the terrain is in its own shadow (cascade chosen by view
        // distance in the render frame; lookup uses the absolute world pos against the absolute light matrix).
        "  sunlit *= csmShadow(length(vWorldPos - uCameraPos), vStableWorldPos, lambert);\n" +
        "  vec3 lightSum = (uSkyAmbient * uAmbient) + (uSunColor * sunlit);\n" +
        // Ambient FLOOR: steep faces turned from the sun (lambert=0) otherwise collapse to lightSum≈0 → near-BLACK
        // (the "czarne dziury/kropki" — proven: an unlit render has 0 black px). max() lifts ONLY the deepest
        // shadows to a cool sky-fill minimum, leaving every brighter sun/shadow gradient (the 3D relief) intact.
        "  lightSum = max(lightSum, uSkyAmbient * 0.45);\n" +
        "  vec3 base;\n" +
        "  if (uUseOrtho == 1) {\n" +
        "    vec3 c = texture(uOrtho, vTex).rgb;\n" +
        // MAGNIFICATION smoothing: when one ortho texel spans more than a screen pixel (close camera), swap
        // the bilinear fetch for bicubic so texels stop reading as hard squares. Minified ground (footprint
        // >= 1 texel) keeps the plain fetch — mips + anisotropy already handle it, and bicubic of mip 0
        // would shimmer there. The unconditional fetch above keeps implicit derivatives defined.
        "    vec2 otsF = vec2(textureSize(uOrtho, 0));\n" +
        "    vec2 ofp = fwidth(vTex) * otsF;\n" +
        "    if (max(ofp.x, ofp.y) < 1.0) { c = texBicubic(uOrtho, vTex, otsF); }\n" +
        "    if (uSharpen > 0.0) {\n" +
        // 4-tap unsharp mask: crisp up edges that mip/aniso minification softens. Clamped to [0,1].
        // Texel size comes from THIS cell's textureSize (otsF), NOT a global uniform: with per-cell
        // resolution tiers (OrthoDistanceTier) neighbouring cells differ 4x in texel size, and one shared
        // texel value sharpened some cells and no-opped others — a visible contrast/colour step exactly on
        // the cell seam ("szycie kafli — inna kolorystyka").
        "      vec2 oTexel = 1.0 / otsF;\n" +
        "      vec3 blur = (texture(uOrtho, vTex + vec2(oTexel.x, 0.0)).rgb\n" +
        "                 + texture(uOrtho, vTex - vec2(oTexel.x, 0.0)).rgb\n" +
        "                 + texture(uOrtho, vTex + vec2(0.0, oTexel.y)).rgb\n" +
        "                 + texture(uOrtho, vTex - vec2(0.0, oTexel.y)).rgb) * 0.25;\n" +
        "      c = clamp(c + (uSharpen * (c - blur)), 0.0, 1.0);\n" +
        "    }\n" +
        // Coverage blend: beyond the ortho's geographic coverage, fade ortho -> hypsometric (vColor) over
        // uOrthoBlendMeters; fully outside = hypsometric. Stable world frame so it's camera-relative-correct.
        "    vec2 cd = min(vStableWorldPos.xy - uOrthoMinXY, uOrthoMaxXY - vStableWorldPos.xy);\n" +
        "    float ow = (uOrthoBlendMeters > 0.0) ? clamp(min(cd.x, cd.y) / uOrthoBlendMeters, 0.0, 1.0) : 1.0;\n" +
        "    ow *= uOrthoGlobalFade;\n" +      // "2D map" mode: fade the whole photo to hypsometric
        "    base = mix(vColor.rgb, c, ow);\n" +
        "  } else {\n" +
        "    base = vColor.rgb;\n" +
        "  }\n" +
        // Elevation-zone biomes: replace the base albedo with the alpine zonation material (meadow → scree →
        // snow → ice up the slope) when "Biomy" is on. Mirrors BiomeClassifier with smooth band blends:
        // elevation drives meadow→scree→snow→ice on gentle ground (aspect lowers the snow/ice lines on the
        // cold north faces), and medium-steep ground reads as scree (talus). The granite rock material below
        // (rockW) still paints the steep faces on top, and the dynamic snow slider layers over it.
        "  if (uBiomeMode > 0.5 && uSlopeMode < 0.5) {\n" +
        "    vec3 bn = normalize(vNormal);\n" +
        "    float northness = clamp(bn.y, -1.0, 1.0);\n" +
        "    float biomeSlopeDeg = degrees(acos(clamp(bn.z, 0.0, 1.0)));\n" +
        // ABSOLUTE world-Z (stable frame), same reason as snow: uBiome*Z thresholds are absolute, so comparing
        // them against the camera-relative vWorldPos.z made the biome bands drift with the look-at (camera tilt).
        "    float effZ = vStableWorldPos.z + (northness * uBiomeAspectShiftZ);\n" +
        "    float bandZ = max(20.0, uBiomeAspectShiftZ * 0.4);\n" +
        "    vec3 meadow = uBiomePalette[0];\n" +
        "    vec3 scree = uBiomePalette[1];\n" +
        "    vec3 snowC = uBiomePalette[3];\n" +
        "    vec3 iceC = uBiomePalette[4];\n" +
        "    vec3 bcol = meadow;\n" +
        "    bcol = mix(bcol, scree, smoothstep(uBiomeMeadowMaxZ - bandZ, uBiomeMeadowMaxZ + bandZ, vStableWorldPos.z));\n" +
        "    float toSnow = smoothstep(uBiomeSnowZ - bandZ, uBiomeSnowZ + bandZ, effZ);\n" +
        "    bcol = mix(bcol, snowC, toSnow);\n" +
        "    bcol = mix(bcol, iceC, smoothstep(uBiomeIceZ - bandZ, uBiomeIceZ + bandZ, effZ));\n" +
        // Medium-steep ground below the snowline is talus (piargi), not meadow — but don't override snowy benches.
        "    float screeBySlope = smoothstep(uBiomeScreeSlopeDeg - 6.0, uBiomeScreeSlopeDeg + 6.0, biomeSlopeDeg);\n" +
        "    bcol = mix(bcol, scree, screeBySlope * (1.0 - toSnow));\n" +
        "    base = bcol;\n" +
        "  }\n" +
        // Granite albedo on rocky fragments — the slope weight + triplanar noise (rk) were computed above
        // (with the detail normal), so here we only tint the base toward the stone colour with a sharp
        // light/dark spread for visible grain.
        "  if (rockW > 0.001) {\n" +
        "    vec3 rockCol = vec3(0.46, 0.43, 0.40) * (0.52 + 0.92 * rk);\n" +
        "    base = mix(base, rockCol, rockW);\n" +
        "  }\n" +
        // Avalanche slope-steepness map: replace the base colour with the band colour for this fragment's
        // slope angle (n.z = cos(slope)). Banding mirrors SlopeClassification; the lighting below still
        // shades it so the relief reads. Snow is skipped in this mode (it would mask the colours).
        "  if (uSlopeMode > 0.5) {\n" +
        "    vec3 sn = normalize(vNormal);\n" +
        "    float slopeDeg = degrees(acos(clamp(sn.z, 0.0, 1.0)));\n" +
        "    int band = slopeDeg < 20.0 ? 0 : int(min(floor((slopeDeg - 20.0) / 10.0) + 1.0, 7.0));\n" +
        "    base = uSlopePalette[band];\n" +
        "  }\n" +
        // SNOW — a UNIFIED PHYSICAL model. Rather than multiplying ad-hoc "melt" terms, the warming
        // influences RAISE the local snowline, exactly as in nature: the line sits HIGHER on warm, sun-hit,
        // wind-scoured ground and LOWER in cold, shaded, sheltered hollows. Snow lies where the terrain
        // rises above that local line. The physical inputs (Twoje: temperatura↦wysokość, nasłonecznienie):
        //   • Elevation  → uSnowLineZ (base snowline = 0°C isotherm; the slider sets its height).
        //   • Insolation → aspect (south faces warmer) + sun incidence (faces square to the sun warmer).
        //   • Wind/curvature → low-freq noise scours ridges / loads hollows (proxy until real DEM curvature).
        // Steep-face SHEDDING is kept SEPARATE and MECHANICAL (gravity/avalanche, NOT temperature). Every
        // input is in the STABLE/absolute world frame, so the snow never changes when only the camera tilts.
        "  float snowMix = 0.0;\n" +
        "  if (uSnowStrength > 0.001 && uSlopeMode < 0.5) {\n" +
        "    vec3 nrm = normalize(vNormal);\n" +
        // Insolation: south-facing (+Y north → south = -nrm.y) and faces square to the sun absorb more
        // energy → warmer → local snowline higher. Sun incidence is gated by the sun being up (uLightDir.z).
        "    float southness = max(0.0, -nrm.y);\n" +
        "    float sunInc = max(0.0, dot(nrm, uSnowSun)) * clamp(uSnowSun.z, 0.0, 1.0);\n" +
        // Wind / curvature proxy: low-freq noise (2 cheap taps, not the 5-octave fbm that tanked FPS).
        "    float snowN = (noiseT(vStableWorldPos.xy * 0.012) * 0.6) + (noiseT(vStableWorldPos.xy * 0.030) * 0.4);\n" +
        // Warming weakens as the pack deepens: at the full slider every aspect is buried (uniform line →
        // solid white); a thin cover differentiates strongly by aspect/sun/wind (natural spring patchiness).
        "    float warmGate = 1.0 - uSnowStrength;\n" +
        // Effective LOCAL snowline (m a.s.l.). The weights are the physical knobs, in metres of snowline lift:
        //   aspect 260 m · sun-incidence 160 m · wind/curvature ±150 m.
        "    float effLine = uSnowLineZ + ((((southness * 260.0) + (sunInc * 160.0)) + ((snowN - 0.5) * 300.0)) * warmGate);\n" +
        "    float snowH = smoothstep(effLine, effLine + uSnowBandZ, vStableWorldPos.z);\n" +
        // Mechanical shedding (NOT temperature): snow can't cling to steep faces / sharp ridges → bare rock.
        // n.z = cos(slope): 1 flat, 0 vertical. A crisp cut on the steeps leaves the sharp ridges bare.
        "    float slopeShed = smoothstep(uSnowSlopeCosBare, uSnowSlopeCosFull, nrm.z);\n" +
        // Deep snow BRIDGES small steep bumps (glacier-smooth, fewer rock specks); a thin cover still bares
        // every steep face. Lift the shed toward full-hold as the pack deepens (only the sharpest aretes stay bare).
        "    slopeShed = slopeShed + ((1.0 - slopeShed) * uSnowStrength * 0.5);\n" +
        "    snowMix = clamp(snowH * slopeShed, 0.0, 1.0) * uSnowStrength;\n" +
        // NB: the snow albedo is NOT baked into `base` here — snow gets its own dedicated bright/cool
        // lighting below (after `lit = base * lightSum`) so shadowed faces don't grey out.
        "  }\n" +
        "  vec3 lit = base * lightSum;\n" +
        // Snow shading (dedicated): high albedo + sky/multiple scattering keeps snow BRIGHT and COOL-BLUE in
        // shadow (real snow shadows are blue, not grey), driven by the sun (not the camera) so orbiting never
        // changes it, and scaling with uSkyAmbient so night snow dims. WINTER FORM: the sun↔shadow contrast
        // is deepened so the snow shows its 3-D shape instead of a flat white sheet — the ambient floor is
        // pulled DOWN (×0.65) for darker-but-still-blue shadows, and the direct-sun term is boosted (×1.4) so
        // lit slopes pop to bright white. The two knobs: floor down / sun up = more relief, but watch for grey.
        "  if (snowMix > 0.001) {\n" +
        "    vec3 snowAlbedo = vec3(0.96, 0.98, 1.0);\n" +
        "    vec3 snowLit = snowAlbedo * ((uSkyAmbient * 0.65) + (uSunColor * sunlit * 1.4));\n" +
        "    snowLit = mix(snowLit, vec3(1.0), uNoonSnowLift * 0.5);\n" +   // intense midday → extra pop toward pure white
        "    lit = mix(lit, min(snowLit, vec3(1.0)), snowMix);\n" +
        "  }\n" +
        "  float dist = length(vWorldPos - uCameraPos);\n" +
        "  float fogAmount = 1.0 - exp(-dist * uFogDensity);\n" +
        // Snow keeps NEAR detail crisp, but DISTANT snowfields pick up cool aerial perspective — they fade
        // into the horizon haze like the real range, instead of staying a hard white cut-out. Only a mild
        // reduction (was a near-full block), so close snow is still sharp while far snow reads as luminous distance.
        "  fogAmount *= (1.0 - 0.35 * snowMix);\n" +
        // Contour lines (warstwice): tint the surface near each iso-elevation level, computed from THIS
        // fragment's elevation so the line lies exactly on whatever LOD is drawn (coarse base OR 1 m detail) —
        // no float, no rock poke-through. fwidth keeps it a constant pixel width; applied pre-fog so it fades.
        "  if (uContourStrength > 0.001 && uReflectionPass < 0.5) {\n" +
        // Minor lines. fwidth(cz) = contour levels spanned by one pixel; fade the lines out once they crowd
        // below a few px so dense 5 m contours in the distance don't smear into a solid tint.
        "    float cz = vStableWorldPos.z / uContourSpacingZ;\n" +
        "    float wC = max(fwidth(cz), 1e-5);\n" +
        "    float dC = min(fract(cz), 1.0 - fract(cz));\n" +
        "    float minorL = (1.0 - smoothstep(0.0, wC * uContourWidthPx, dC)) * (1.0 - smoothstep(0.3, 0.6, wC));\n" +
        "    lit = mix(lit, uContourColor, minorL * uContourStrength);\n" +
        // Major (index) lines every 100 m — red, a touch bolder, drawn over the minor so they win at 100 m.
        "    float mz = vStableWorldPos.z / uContourMajorSpacingZ;\n" +
        "    float wM = max(fwidth(mz), 1e-5);\n" +
        "    float dM = min(fract(mz), 1.0 - fract(mz));\n" +
        "    float majorL = (1.0 - smoothstep(0.0, wM * uContourWidthPx * 1.6, dM)) * (1.0 - smoothstep(0.3, 0.6, wM));\n" +
        "    lit = mix(lit, uContourMajorColor, majorL * uContourStrength);\n" +
        "  }\n" +
        // Trail decal (DISTANCE FIELD): the mask stores, per texel, the distance to the nearest painted line
        // (A=255 on the line → 0 at uTrailMaxDist) in RGB=line colour. Addressed by the STABLE world-XY so it stays
        // glued under any LOD. Reconstruct the metric distance and draw a THIN line analytically. The AA BAND
        // (fwidth) was already screen-constant, but the line's CORE width (uTrailHalfWidth) was a fixed 1.6 m in
        // WORLD space — at typical viewing distance that shrinks below a screen pixel and the line reads as faint/
        // gone, even though it's technically still "crisp". fwidth(distM) is exactly this fragment's local
        // metres-per-pixel (the same quantity a screen-constant line needs), so floor the half-width at
        // DecalMinHalfWidthPx pixels' worth of that scale — true-scale 1.6 m up close (where it's already several
        // px), growing only once distance would shrink it thinner than that floor. No new uniform needed.
        "  if ((uTrailStrength > 0.001 || uWaterStrength > 0.001) && uReflectionPass < 0.5) {\n" +
        "    vec2 tuv = (vStableWorldPos.xy - uTrailMaskMinXY) / uTrailMaskSizeXY;\n" +
        "    if (tuv.x >= 0.0 && tuv.x <= 1.0 && tuv.y >= 0.0 && tuv.y <= 1.0) {\n" +
        "      vec4 tc = texture(uTrailMask, tuv);\n" +
        "      float distM = (1.0 - tc.a) * uTrailMaxDist;\n" +    // metres to the nearest line (= uTrailMaxDist when A=0)
        "      float aa = max(fwidth(distM), 0.001);\n" +          // one pixel of distance change → screen-constant AA
        "      float halfWidthM = max(uTrailHalfWidth, aa * 1.1);\n" + // 1.1 px floor: true 1.6 m scale near, constant-px legible far
        "      float coverage = 1.0 - smoothstep(halfWidthM - aa, halfWidthM + aa, distM);\n" +
        // Cartographic CASING: a light rim around the colour core, from the same distance field. Without it a
        // BLACK trail painted onto dark shadowed rock is invisible (czarny szlak w żlebie Kulczyńskiego "hidden
        // under the relief" — it was drawn, just black-on-black; the 3D line overlay z-fights away exactly
        // there, so the decal must carry it alone). The rim also crisps every other colour on busy ortho.
        "      float rimW = halfWidthM * 2.3;\n" +
        "      float rim = clamp((1.0 - smoothstep(rimW - aa, rimW + aa, distM)) - coverage, 0.0, 1.0);\n" +
        // WATER decal v2: streams/rivers painted into the surface (same window, own R8 distance field).
        // v1 was near-invisible in practice: the dark tint vanished on dark forest ortho, and a pow-96 Blinn
        // on the ROCK normal only fired where the slope happened to face the half-vector ("wodospady suche").
        // v2: (a) LIGHTER cold tint with an ambient floor so canopy crossings still read as water,
        // (b) the specular normal is the terrain normal pulled hard toward +Z — water lies FLAT in its bed,
        // so the ribbon catches the sun reliably, (c) two-term specular: a broad sheen (always glossy) plus
        // a tight sparkle modulated by a two-sine ripple drifting on the cloud clock, so the stream glitters
        // even from a resting camera; the ripple fades out once its ~3 m wavelength drops under a pixel
        // (otherwise it aliases into fizz at distance). Runs BEFORE the trail line/rim so a trail crossing
        // a stream stays readable on top.
        "      if (uWaterStrength > 0.001) {\n" +
        "        float wA = texture(uWaterMask, tuv).r;\n" +
        "        if (wA > 0.003) {\n" +
        "          float wDist = (1.0 - wA) * uTrailMaxDist;\n" +
        "          float wHalf = max(3.2, aa * 1.8);\n" +
        "          float wCov = 1.0 - smoothstep(wHalf - aa, wHalf + aa, wDist);\n" +
        "          if (wCov > 0.001) {\n" +
        "            vec3 waterTint = vec3(0.24, 0.46, 0.60) * max(lightSum, vec3(0.40));\n" +
        "            lit = mix(lit, waterTint, wCov * 0.85 * uWaterStrength);\n" +
        "            vec3 wN = normalize(mix(shN, vec3(0.0, 0.0, 1.0), 0.6));\n" +
        "            vec3 wV = normalize(uCameraPos - vWorldPos);\n" +
        "            vec3 wH = normalize(uLightDir + wV);\n" +
        "            float wNdH = max(dot(wN, wH), 0.0);\n" +
        // Water reflects the SKY even with no direct sun — that is why a stream in a shaded cirque still
        // reads silver-bright. Without this floor the whole effect was scaled by sun elevation, so at golden
        // hour every NW-facing fall was a matte dark stripe ("siklawa sucha"). Fresnel-ish: brighter at
        // grazing view angles, like a real water sheet.
        "            float fres = pow(1.0 - max(dot(wN, wV), 0.0), 2.0);\n" +
        "            lit += vec3(0.58, 0.68, 0.78) * ((0.10 + 0.40 * fres) * wCov * uWaterStrength);\n" +
        // ×3 so the sun glint stays usable down to golden hour (elevation ~20° → full strength).
        "            float wSunUp = clamp(uLightDir.z * 3.0, 0.0, 1.0);\n" +
        "            float rippleFade = clamp(3.0 / max(aa, 0.001), 0.0, 1.0);\n" +
        "            float ripple = 0.6 + 0.4 * rippleFade\n" +
        "                * sin(dot(vStableWorldPos.xy, vec2(0.55, 0.83)) * 2.1 - uCloudTime * 2.6)\n" +
        "                * sin(dot(vStableWorldPos.xy, vec2(-0.71, 0.40)) * 1.7 + uCloudTime * 1.9);\n" +
        "            float wSheen = pow(wNdH, 18.0) * 0.4;\n" +
        "            float wSpark = pow(wNdH, 130.0) * 1.5 * ripple;\n" +
        "            lit += vec3(1.0, 0.98, 0.92) * ((wSheen + wSpark) * wCov * wSunUp * uWaterStrength);\n" +
        "          }\n" +
        "        }\n" +
        "      }\n" +
        "      lit = mix(lit, vec3(0.94, 0.94, 0.90), rim * 0.55 * uTrailStrength);\n" +
        "      lit = mix(lit, tc.rgb, coverage * uTrailStrength);\n" +
        "    }\n" +
        "  }\n" +
        "  fragColor = vec4(mix(lit, uFogColor, fogAmount), 1.0);\n" +
        // DIAGNOSTIC overlay: render the raw ortho UV as colour (R=U, G=V). A clean smooth gradient per cell = UV
        // is fine → flat bands are texture sampling (mip/aniso/content). A striped/sawtooth pattern = UV is broken.
        "  if (uDebugUv > 1.5 && uUseOrtho == 1) {\n" +
        // DIAGNOSTIC clamp viz: RED where U is pinned to a cell edge, GREEN where V is — i.e. the ortho UV
        // clamped (out-of-coverage edge-texel stretch). If the stripe band lights up here, it is the clamp.
        "    float cu = (vTex.x <= 0.003 || vTex.x >= 0.997) ? 1.0 : 0.0;\n" +
        "    float cv = (vTex.y <= 0.003 || vTex.y >= 0.997) ? 1.0 : 0.0;\n" +
        "    fragColor = vec4(cu, cv, 0.0, 1.0);\n" +
        "  } else if (uDebugUv > 0.5 && uUseOrtho == 1) {\n" +
        "    fragColor = vec4(vTex.x, vTex.y, 0.0, 1.0);\n" +
        "  }\n" +
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
        "uniform float uSunGlowIntensity;\n" + // forward-scatter glow strength (swells near horizon, 0 at noon/night)
        "uniform float uSunGlowWidth;\n" +     // angular spread of the glow halo (wider near horizon)
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
        "    cloudUv = vec2(cloudUv.x * 0.42, cloudUv.y * 2.0) + (uCloudDrift * uTime);\n" +
        "    float clouds = fbm(cloudUv * 1.25);\n" + // 5-octave fBm: soft WISPY cirrus, no value-noise grid (kills the angular/pixelated bands)
        "    float threshold = 0.48 - (uCloudCoverage * 0.34);\n" + // lower base + stronger coverage pull = much more cloud
        "    cloudDensity = smoothstep(threshold, threshold + 0.30, clouds) * 0.8;\n" + // wide band = feathered (pierzaste) edges, not hard angular ones
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
        // Bigger, brighter disc (~4° → ~2.5° soft edge, was ~2° → ~1°) with an over-bright core, plus a
        // wider Mie halo (pow 80 → 55, 0.55 → 0.72) so the sun reads as a real, radiant sun rather than a dot.
        "  float sunCore = smoothstep(0.99993, 0.99997, sunDot);\n" + // small, natural disc (~0.7° — was a huge 0.9992 blob)
        "  float sunHalo = pow(max(sunDot, 0.0), 400.0) * 0.72;\n" + // much tighter halo (was 55) so it doesn't saturate a big blob; brightness unchanged
                                                                     // Forward-scatter glow ("poświata pod słońcem"): a broad warm bloom around the sun that swells as
                                                                     // it nears the horizon. uSunGlowWidth lowers the exponent (broader spread); the bloom pools BELOW
                                                                     // the sun (forward scatter sinks toward the horizon) and is scaled by uSunGlowIntensity — which the
                                                                     // Atmosphere model drives strong at golden hour and to nil at noon / night.
        "  float glowExp = mix(170.0, 100.0, clamp(uSunGlowWidth, 0.0, 1.0));\n" + // very tight forward-scatter glow so the Sun reads natural-sized
        "  float belowSun = clamp((uSunDir.z - viewDir.z) * 2.0 + 0.5, 0.0, 1.0);\n" +
        "  float glow = pow(max(sunDot, 0.0), glowExp) * uSunGlowIntensity * (0.7 + 0.6 * belowSun);\n" +
        "  vec3 sun = uSunColor * (sunCore * 1.2 + sunHalo * 0.6 + glow * 0.5);\n" + // halo+glow weighted down so the saturated white blob stays small (Sun reads natural)
        "  fragColor = vec4(sky + sun, 1.0);\n" +
        "}\n";

    // Night-sky star pass: each catalog star is one point sprite placed by its world-space direction
    // (X east, Y north, Z up). gl_Position uses w=0 so the projection pins it to the sky at infinity —
    // immune to camera translation AND to the terrain's camera-relative model frame. Drawn after the sky
    // gradient, before the depth-tested terrain pass occludes whatever sits behind a ridge. Brightness +
    // point size come from apparent magnitude; the whole pass fades in with uNightFactor so it only shows
    // once the sun is down.
    private const string StarVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec3 aDir;\n" +   // unit world direction to the star
        "layout(location=1) in float aMag;\n" +  // apparent magnitude (smaller = brighter)
        "uniform mat4 uViewProj;\n" +            // forward view-projection (same matrix as the terrain pass)
        "out float vMag;\n" +
        "out float vUp;\n" +                     // world-up component -> discard below the horizon
        "void main(){\n" +
        "  vMag = aMag;\n" +
        "  vUp = aDir.z;\n" +
        "  vec4 clip = uViewProj * vec4(aDir, 0.0);\n" +    // direction at infinity: immune to camera translation + the camera-relative terrain frame
        "  gl_Position = vec4(clip.xy, clip.w, clip.w);\n" + // pin depth to the far plane (z=w -> NDC.z=1); a raw w=0 lands NDC.z>1 and the whole sky gets far-clipped
        "  gl_PointSize = mix(3.0, 9.0, clamp((6.0 - aMag) / 7.5, 0.0, 1.0));\n" + // brighter stars draw larger
        "}\n";

    private const string StarFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in float vMag;\n" +
        "in float vUp;\n" +
        "uniform float uNightFactor;\n" + // 0 by day .. 1 deep night (same curve as the sky shader)
        "uniform float uStarsOn;\n" +     // 1 = stars enabled, 0 = panel toggle off (reserved for step F)
        "out vec4 fragColor;\n" +
        "void main(){\n" +
        "  if (vUp <= 0.0) discard;\n" +                     // below the horizon — never visible
        "  vec2 pc = gl_PointCoord * 2.0 - 1.0;\n" +
        "  float r2 = dot(pc, pc);\n" +
        "  if (r2 > 1.0) discard;\n" +                        // round dot, not a square
        "  float soft = 1.0 - smoothstep(0.1, 1.0, r2);\n" +  // soft falloff to the sprite edge
        "  float bright = clamp((6.5 - vMag) / 5.5, 0.4, 1.0);\n" + // lift the floor so fainter catalog stars still read
        "  float alpha = bright * soft * uNightFactor * uStarsOn;\n" +
        "  fragColor = vec4(vec3(1.0, 0.96, 0.90), alpha);\n" + // faintly warm white; additive blend adds col*alpha
        "}\n";

    // Night-sky Moon pass: ONE point sprite at the lunar world direction (driven by the uMoonDir uniform, so
    // no vertex buffer — any bound VAO works). Same w=0 + z=w far-plane pin as the stars. The fragment paints
    // a phased disc: a terminator ellipse whose lit side faces the Sun (uTermDir = bright-limb screen dir,
    // uIlluminated = lit fraction), with faint earthshine on the dark side.
    private const string MoonVertexShaderSource =
        "#version 300 es\n" +
        "uniform mat4 uViewProj;\n" +
        "uniform vec3 uMoonDir;\n" +  // unit world direction to the Moon (X east, Y north, Z up)
        "uniform float uSizePx;\n" +
        "void main(){\n" +
        "  vec4 clip = uViewProj * vec4(uMoonDir, 0.0);\n" +
        "  gl_Position = vec4(clip.xy, clip.w, clip.w);\n" + // pin to far plane (same fix as the stars)
        "  gl_PointSize = uSizePx;\n" +
        "}\n";

    private const string MoonFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "uniform vec2 uTermDir;\n" +      // unit screen-space direction toward the bright limb
        "uniform float uIlluminated;\n" + // lit fraction 0..1 (0=new, 1=full)
        "uniform float uNightFactor;\n" +
        "out vec4 fragColor;\n" +
        "void main(){\n" +
        "  vec2 pc = gl_PointCoord * 2.0 - 1.0;\n" +
        "  pc.y = -pc.y;\n" +                                  // gl_PointCoord is top-left origin; flip to screen-up
        "  float r = length(pc);\n" +
        "  if (r > 1.0) discard;\n" +                          // round disc
        "  float along = dot(pc, uTermDir);\n" +               // toward bright limb (+1) .. dark limb (-1)
        "  float across = length(pc - (along * uTermDir));\n" +
        "  float boundary = (1.0 - 2.0 * uIlluminated) * sqrt(max(0.0, 1.0 - across * across));\n" + // terminator ellipse
        "  float lit = smoothstep(boundary - 0.06, boundary + 0.06, along);\n" +
        "  float bright = mix(0.05, 1.0, lit);\n" +            // faint earthshine on the dark side
        "  float edge = smoothstep(1.0, 0.90, r);\n" +         // soft limb
        "  fragColor = vec4(vec3(0.97, 0.96, 0.90) * bright, edge * uNightFactor);\n" +
        "}\n";

    // Post-process pass: a fullscreen triangle (reusing the sky VAO's location-0 vec2 clip attribute) that
    // samples the resolved scene texture and writes it to the post FBO. The vertex stage turns clip-space
    // [-1,1] into [0,1] UVs; the fragment stage is currently a pass-through (bloom / god rays will extend it).
    // Depth test is disabled for this stage, so gl_Position.z is irrelevant.
    private const string PostVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec2 aClip;\n" +
        "out vec2 vUv;\n" +
        "void main(){ vUv = (aClip * 0.5) + 0.5; gl_Position = vec4(aClip, 0.0, 1.0); }\n";

    private const string PostFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vUv;\n" +
        "uniform sampler2D uTex;\n" +
        "out vec4 fragColor;\n" +
        "void main(){ fragColor = texture(uTex, vUv); }\n";

    // Bloom bright-pass: keep only the part of each pixel above the luminance threshold (soft knee via the
    // over-threshold ratio) so the sun disc / luminous sky / lit snow pass through and everything else goes
    // black. Output feeds the blur. Reuses PostVertexShaderSource.
    private const string BloomBrightFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vUv;\n" +
        "uniform sampler2D uTex;\n" +
        "uniform float uThreshold;\n" +
        "out vec4 fragColor;\n" +
        "void main(){\n" +
        "  vec3 c = texture(uTex, vUv).rgb;\n" +
        "  float l = dot(c, vec3(0.2126, 0.7152, 0.0722));\n" +
        "  float k = max(l - uThreshold, 0.0) / max(l, 1e-4);\n" +
        "  fragColor = vec4(c * k, 1.0);\n" +
        "}\n";

    // Bloom blur: linear-sampled 9-tap Gaussian (weights sum to 1). uDir is the one-axis texel step
    // (1/width,0) for the horizontal pass, (0,1/height) for the vertical pass.
    private const string BloomBlurFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vUv;\n" +
        "uniform sampler2D uTex;\n" +
        "uniform vec2 uDir;\n" +
        "out vec4 fragColor;\n" +
        "void main(){\n" +
        "  vec3 s = texture(uTex, vUv).rgb * 0.2270270270;\n" +
        "  s += texture(uTex, vUv + uDir * 1.3846153846).rgb * 0.3162162162;\n" +
        "  s += texture(uTex, vUv - uDir * 1.3846153846).rgb * 0.3162162162;\n" +
        "  s += texture(uTex, vUv + uDir * 3.2307692308).rgb * 0.0702702703;\n" +
        "  s += texture(uTex, vUv - uDir * 3.2307692308).rgb * 0.0702702703;\n" +
        "  fragColor = vec4(s, 1.0);\n" +
        "}\n";

    // Bloom composite: the blurred bright buffer (half-res, linear-upsampled) added additively over the
    // full-res scene, scaled by the Atmosphere-driven intensity.
    private const string BloomCompositeFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vUv;\n" +
        "uniform sampler2D uScene;\n" +
        "uniform sampler2D uBloom;\n" +
        "uniform sampler2D uGodray;\n" +
        "uniform float uIntensity;\n" +        // bloom
        "uniform float uGodrayIntensity;\n" +
        "out vec4 fragColor;\n" +
        "void main(){\n" +
        "  vec3 sc = texture(uScene, vUv).rgb;\n" +
        "  vec3 bl = texture(uBloom, vUv).rgb;\n" +
        "  vec3 gr = texture(uGodray, vUv).rgb;\n" +
        "  fragColor = vec4(sc + (bl * uIntensity) + (gr * uGodrayIntensity), 1.0);\n" +
        "}\n";

    // God rays (crepuscular rays): screen-space radial blur of the bright-pass mask toward the sun's
    // on-screen position. Where dark terrain occludes the path to the sun the accumulation stops, so light
    // streams out only through the gaps — the classic post-process light-shaft look. Reuses PostVertexShaderSource.
    private const string GodrayFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vUv;\n" +
        "uniform sampler2D uTex;\n" +   // bright-pass mask (sun/sky bright, terrain black)
        "uniform vec2 uSunUv;\n" +      // sun position in this texture's UV space
        "out vec4 fragColor;\n" +
        "void main(){\n" +
        "  const int N = 24;\n" +
        "  vec2 delta = (vUv - uSunUv) * (1.0 / float(N));\n" + // density 1.0; marches toward the sun
        "  vec2 uv = vUv;\n" +
        "  vec3 col = texture(uTex, uv).rgb;\n" +
        "  float decay = 1.0;\n" +
        "  for (int i = 0; i < N; i++) {\n" +
        "    uv -= delta;\n" +
        "    col += texture(uTex, uv).rgb * decay * 0.5;\n" + // weight 0.5
        "    decay *= 0.93;\n" +
        "  }\n" +
        "  fragColor = vec4(col * 0.5, 1.0);\n" + // exposure
        "}\n";

    // Shadow depth pass: transform the terrain vertex (absolute world aPos) by a cascade's light
    // view-projection and write depth only. No colour output — the FBO has just a depth texture.
    private const string ShadowDepthVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec3 aPos;\n" +
        "uniform mat4 uLightVp;\n" +
        "void main(){ gl_Position = uLightVp * vec4(aPos, 1.0); }\n";

    private const string ShadowDepthFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "void main(){}\n";

    // Cloud-layer ("sea of clouds") program. A large horizontal quad at a fixed world altitude,
    // drawn AFTER the terrain with the depth test on (so peaks above the layer occlude it and the
    // valleys below are veiled) but depth-write off and alpha blending on. The fragment shader
    // samples animated fBm at the fragment's WORLD (x,y) so the cloud field is locked to the world
    // and drifts smoothly — the iconic Tatra temperature-inversion look, peaks poking through fog.
    private const string CloudLayerVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec2 aCorner;\n" + // tessellated grid vertex in [-1,1]
        "uniform mat4 uMvp;\n" +
        "uniform vec2 uCenter;\n" +    // world XY centre of the layer
        "uniform float uHalfExtent;\n" + // world half-size of the quad
        "uniform float uAltitude;\n" + // base world Z of the layer
        "uniform float uDispScale;\n" + // 1/metres — wavelength of the surface undulation
        "uniform float uDispAmp;\n" +   // metres of vertical lap (grows with wind)
        "uniform vec2 uWind;\n" +       // drift (waves slide downwind)
        "uniform float uTime;\n" +
        "out vec2 vWorldXY;\n" +
        "out vec2 vLocal;\n" +
        "out float vCrest;\n" + // signed surface height (−1 trough … +1 crest) for billow shading
        "float hashV(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }\n" +
        "float noiseV(vec2 p){\n" +
        "  vec2 i = floor(p); vec2 f = fract(p);\n" +
        "  f = f * f * (3.0 - 2.0 * f);\n" +
        "  return mix(mix(hashV(i), hashV(i + vec2(1.0,0.0)), f.x),\n" +
        "             mix(hashV(i + vec2(0.0,1.0)), hashV(i + vec2(1.0,1.0)), f.x), f.y);\n" +
        "}\n" +
        "float fbmV(vec2 p){ float v=0.0,a=0.5; for(int i=0;i<4;i++){ v+=a*noiseV(p); p*=2.04; a*=0.5;} return v; }\n" +
        "void main(){\n" +
        "  vLocal = aCorner;\n" +
        "  vec2 world = uCenter + (aCorner * uHalfExtent);\n" +
        "  vWorldXY = world;\n" +
        // Undulate the cloud surface vertically with a wind-drifting fBm. The amplitude grows with the
        // wind, so a calm day is a near-flat sea while a gale heaves the cloud crests up the slopes — the
        // perfectly level waterline becomes a living, wind-driven boundary that laps onto the terrain.
        "  vec2 q = (world * uDispScale) + (uWind * uTime * 0.6);\n" +
        "  float n = (fbmV(q) - 0.5) * 2.0;\n" + // ~[-1,1]
        "  vCrest = n;\n" +
        "  gl_Position = uMvp * vec4(world, uAltitude + (n * uDispAmp), 1.0);\n" +
        "}\n";

    private const string CloudLayerFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vWorldXY;\n" +
        "in vec2 vLocal;\n" +
        "in float vCrest;\n" +
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
        "  float thr = 0.62 - (uCoverage * 0.42);\n" + // lower threshold = more, denser low clouds
        "  float a = smoothstep(thr, thr + 0.20, n);\n" +
        // Soft-fade the quad's outer ring so the (finite) sheet doesn't show a hard rectangular
        // edge out toward the horizon.
        "  float edge = smoothstep(1.0, 0.65, max(abs(vLocal.x), abs(vLocal.y)));\n" +
        "  a *= edge * (0.45 + (uCoverage * 0.5));\n" + // opacity tracks coverage: a light veil when scattered, a near-solid deck at 100%
                                                        // Billow shading from the surface height: crests catch the light (brighter, a touch denser),
                                                        // troughs fall into shade — turns the flat veil into a rolling 3D sea of clouds.
        "  a = clamp(a * (0.88 + (0.34 * max(vCrest, 0.0))), 0.0, 1.0);\n" +
        "  vec3 lit = uCloudColor * (0.80 + (0.28 * clamp(vCrest, -1.0, 1.0)));\n" +
        "  fragColor = vec4(lit, a);\n" +
        "}\n";

    // ── Cumulus puffs (Tier 2 clouds) ────────────────────────────────────────────────────────────────
    // Scattered camera-facing billboards drawn ABOVE the terrain — puffy white cumulus in a blue sky, the
    // look in the user's reference photos (not the inversion "sea of clouds" sheet, which stays separate).
    // Each puff is a single quad with a fully procedural fragment (no atlas / no bake): a cauliflower density
    // field, a lit-top → shaded-base gradient and a sun-side "silver lining" rim — the cheap volume cues.
    // Vertical billboard (up = world Z) so the flat cumulus base + rounded top read correctly. Drifts with
    // the wind in the vertex shader; depth-tested so foreground peaks occlude clouds behind them.
    private const string CumulusVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec2 aCard;\n" +        // quad corner [-1,1]
        "layout(location=1) in vec3 aOffset;\n" +      // puff offset from the field centre (x,y) + vertical (z)
        "layout(location=2) in vec2 aSizeSeed;\n" +    // x=radius(m), y=seed
        "uniform mat4 uMvp;\n" +
        "uniform vec3 uCameraPos;\n" +
        "uniform vec2 uFieldCenter;\n" +   // world XY the field is centred on (scene centre)
        "uniform float uBaseAltitude;\n" + // world Z of the cumulus condensation base
        "uniform vec2 uDrift;\n" +         // wind * time (m) — clouds slide downwind
        "out vec2 vCard;\n" +
        "out float vSeed;\n" +
        "out vec3 vWorldPos;\n" +
        "void main(){\n" +
        "  float s = aSizeSeed.x;\n" +
        "  vSeed = aSizeSeed.y;\n" +
        "  vec2 drifted = aOffset.xy + uDrift;\n" +
        // Wrap the field into a 32 km torus around the scene centre so cumulus drift downwind forever without
        // the finite field sliding off one side (matches BuildCumulusField's 16 km radius).
        "  drifted = mod(drifted + 16000.0, 32000.0) - 16000.0;\n" +
        "  vec3 center = vec3(uFieldCenter + drifted, uBaseAltitude + aOffset.z);\n" +
        "  vec3 toEye = uCameraPos - center;\n" +
        "  vec3 horiz = vec3(toEye.xy, 0.0);\n" +
        "  float hl = length(horiz);\n" +
        "  vec3 right = hl > 1e-4 ? normalize(vec3(-horiz.y, horiz.x, 0.0)) : vec3(1.0, 0.0, 0.0);\n" +
        "  vec3 up = vec3(0.0, 0.0, 1.0);\n" +
        // Cumulus are broader than tall: widen the horizontal axis, and lift the quad so its centre sits
        // above the base (so the flat bottom lands ~on the condensation level).
        "  vec3 world = center + (right * (aCard.x * s * 1.4)) + (up * ((aCard.y * 0.85 + 0.55) * s));\n" +
        "  vWorldPos = world;\n" +
        "  vCard = aCard;\n" +
        "  gl_Position = uMvp * vec4(world, 1.0);\n" +
        "}\n";

    private const string CumulusFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vCard;\n" +
        "in float vSeed;\n" +
        "in vec3 vWorldPos;\n" +
        "uniform vec3 uCameraPos;\n" +
        "uniform vec3 uSunDir;\n" +
        "uniform vec3 uCloudLit;\n" +    // sun-lit top colour
        "uniform vec3 uCloudShadow;\n" + // shaded base colour
        "uniform vec3 uFogColor;\n" +
        "uniform float uFogDensity;\n" +
        "uniform float uOpacity;\n" +
        "out vec4 fragColor;\n" +
        "float h21(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }\n" +
        "float n2(vec2 p){ vec2 i=floor(p), f=fract(p); f=f*f*(3.0-2.0*f);\n" +
        "  return mix(mix(h21(i), h21(i+vec2(1,0)), f.x), mix(h21(i+vec2(0,1)), h21(i+vec2(1,1)), f.x), f.y); }\n" +
        "float fbm(vec2 p){ float v=0.0, a=0.55; for(int i=0;i<4;i++){ v+=a*n2(p); p=p*2.03+vec2(1.7,9.2); a*=0.5; } return v; }\n" +
        "void main(){\n" +
        "  vec2 c = vCard;\n" +
        // Cauliflower edge: a seeded fBm pushes the round silhouette in and out → lumpy puffs, not a disc.
        "  float lump = fbm(c * 2.2 + vec2(vSeed * 17.0, vSeed * 9.0));\n" +
        "  float r = length(vec2(c.x, (c.y + 0.12) * 1.08));\n" +
        "  float field = (1.0 - r) + ((lump - 0.5) * 0.95);\n" +
        "  float baseFlat = smoothstep(-1.0, -0.32, c.y);\n" + // soft flat bottom (condensation level)
        "  float density = smoothstep(0.16, 0.60, field) * baseFlat;\n" +
        "  density *= smoothstep(1.0, 0.78, max(abs(c.x), abs(c.y)));\n" + // fade out at the quad rim so the cauliflower never reaches a hard rectangular edge
        "  if (density < 0.02) { discard; }\n" +
        // Lit top → shaded base (the puff's own vertical axis stands in for the sun-from-above term).
        "  float topLit = smoothstep(-0.45, 0.9, c.y + (lump - 0.5) * 0.7);\n" +
        "  vec3 col = mix(uCloudShadow, uCloudLit, topLit);\n" +
        // Silver lining: looking toward the sun through a thin cloud edge → a bright forward-scatter rim.
        "  vec3 viewDir = normalize(vWorldPos - uCameraPos);\n" +
        "  float toSun = max(dot(viewDir, uSunDir), 0.0);\n" +
        "  col += uCloudLit * (pow(toSun, 5.0) * smoothstep(0.5, 0.95, r) * 1.7);\n" +
        // Aerial-perspective fog, matching the terrain, so distant clouds melt into the horizon haze.
        "  float dist = length(vWorldPos - uCameraPos);\n" +
        "  col = mix(col, uFogColor, 1.0 - exp(-dist * uFogDensity));\n" +
        "  fragColor = vec4(col, clamp(density, 0.0, 1.0) * uOpacity);\n" +
        "}\n";

    // ── Sauron's tower (easter egg) ───────────────────────────────────────────────────────────────────
    // A dark tapered spire on Świnica topped by a glowing eye that blooms like a small sun (the bloom pass
    // turns the full-bright emissive orb into a sun). Flat sun-lit dark stone for the tower; emissive orb for
    // the eye. One non-instanced position+normal+colour+emissive mesh, built once. Toggled from the menu.
    private const string SauronVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec3 aPos;\n" +
        "layout(location=1) in vec3 aNormal;\n" +
        "layout(location=2) in vec3 aColor;\n" +
        "layout(location=3) in float aEmissive;\n" +
        "uniform mat4 uViewProj;\n" +
        "uniform vec3 uModelOffset;\n" +    // Świnica's world position (base of the tower)
        "uniform float uVerticalScale;\n" + // terrain vertical exaggeration, so the tower scales with the relief
        "uniform vec3 uCameraPos;\n" +
        "out vec3 vNormal;\n" +
        "out vec3 vColor;\n" +
        "out float vEmissive;\n" +
        "out vec3 vWorldPos;\n" +
        "out vec2 vCard;\n" +
        "void main(){\n" +
        "  vEmissive = aEmissive;\n" +
        "  vColor = aColor;\n" +
        "  vCard = vec2(0.0);\n" +
        "  vec3 world;\n" +
        "  if (aEmissive > 0.5) {\n" +
        // The eye is a camera-facing billboard centred above the tower top; aPos.xy carries the quad card.
        "    vec2 card = aPos.xy;\n" +
        "    vCard = card;\n" +
        "    vec3 center = vec3(0.0, 0.0, 400.0 * uVerticalScale) + uModelOffset;\n" + // between the two flanking spires
        "    vec3 toCam = normalize(uCameraPos - center);\n" +
        "    vec3 right = normalize(cross(vec3(0.0, 0.0, 1.0), toCam));\n" +
        "    vec3 up = normalize(cross(toCam, right));\n" +
        "    world = center + (right * (card.x * 40.0)) + (up * (card.y * 26.0));\n" + // wide Eye, sized to the slim tower
        "    vNormal = toCam;\n" +
        "  } else {\n" +
        "    world = vec3(aPos.x, aPos.y, aPos.z * uVerticalScale) + uModelOffset;\n" +
        "    vNormal = aNormal;\n" +
        "  }\n" +
        "  vWorldPos = world;\n" +
        "  gl_Position = uViewProj * vec4(world, 1.0);\n" +
        "}\n";

    private const string SauronFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec3 vNormal;\n" +
        "in vec3 vColor;\n" +
        "in float vEmissive;\n" +
        "in vec3 vWorldPos;\n" +
        "in vec2 vCard;\n" +
        "uniform vec3 uSunDir;\n" +
        "uniform vec3 uSunColor;\n" +
        "uniform float uAmbient;\n" +
        "uniform vec3 uCameraPos;\n" +
        "uniform vec3 uFogColor;\n" +
        "uniform float uFogDensity;\n" +
        "uniform float uEyePulse;\n" +
        "uniform float uTime;\n" +
        "out vec4 fragColor;\n" +
        "float h21(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }\n" +
        "float n2(vec2 p){ vec2 i=floor(p), f=fract(p); f=f*f*(3.0-2.0*f);\n" +
        "  return mix(mix(h21(i), h21(i+vec2(1,0)), f.x), mix(h21(i+vec2(0,1)), h21(i+vec2(1,1)), f.x), f.y); }\n" +
        "float fbm(vec2 p){ float v=0.0, a=0.5; for(int i=0;i<4;i++){ v+=a*n2(p); p=p*2.0; a*=0.5; } return v; }\n" +
        "void main(){\n" +
        "  if (vEmissive < 0.5) {\n" + // ── the tower (opaque, sun-lit dark stone) ──
        "    float lambert = max(dot(normalize(vNormal), uSunDir), 0.0);\n" +
        "    vec3 col = vColor * uSunColor * (uAmbient + ((1.0 - uAmbient) * lambert));\n" +
        "    float dist = length(vWorldPos - uCameraPos);\n" +
        "    col = mix(col, uFogColor, 1.0 - exp(-dist * uFogDensity));\n" +
        "    fragColor = vec4(col, 1.0);\n" +
        "    return;\n" +
        "  }\n" +
        // ── the Eye of Sauron (camera-facing, additive) ──
        "  vec2 c = vCard;\n" +
        // Eye SOCKET — a wide horizontal almond; the sclera (conjunctiva) fills it, the iris floats on top.
        "  vec2 socket = vec2(c.x / 0.97, c.y / 0.60);\n" +
        "  float rs = length(socket);\n" +
        "  float socketMask = smoothstep(1.06, 0.86, rs);\n" + // 1 inside the almond, feathered rim
        "  float flame  = fbm((c * 3.2) + vec2(0.0, -uTime * 1.7));\n" +   // flames licking upward
        "  float flame2 = fbm((c * 6.5) + vec2(uTime * 0.4, -uTime * 2.6));\n" +
        // GAZE — the iris + pupil drift so the eye visibly looks around within the (fixed) conjunctiva.
        "  vec2 gaze = vec2(0.24 * sin(uTime * 0.7), 0.07 * sin((uTime * 0.53) + 1.3));\n" +
        "  vec2 ce = c - gaze;\n" +
        "  vec2 iris = vec2(ce.x / 0.52, ce.y / 0.50);\n" +
        "  float ri = length(iris);\n" +
        // Sclera (conjunctiva): a glowing pale-amber membrane filling the socket, veined by the flame noise.
        "  vec3 sclera = vec3(1.7, 1.15, 0.45) * (0.45 + (0.55 * flame2));\n" +
        // Fiery iris: white-hot core → orange → deep-red rim, churned by the flame. HDR (>1) so it blooms.
        "  vec3 hot = vec3(3.0, 2.4, 1.2);\n" +
        "  vec3 mid = vec3(2.6, 0.9, 0.12);\n" +
        "  vec3 rim = vec3(1.6, 0.18, 0.02);\n" +
        "  vec3 irisCol = mix(hot, mid, smoothstep(0.0, 0.55, ri));\n" +
        "  irisCol = mix(irisCol, rim, smoothstep(0.55, 1.0, ri));\n" +
        "  irisCol *= 0.6 + (0.8 * flame);\n" +
        "  float irisMask = smoothstep(1.02, 0.72, ri);\n" +
        "  vec3 col = mix(sclera, irisCol, irisMask);\n" +
        // Vertical slit pupil, moving with the gaze.
        "  float slit = (1.0 - smoothstep(0.035, 0.10, abs(ce.x))) * (1.0 - smoothstep(0.40, 0.60, abs(ce.y)));\n" +
        "  col = mix(col, vec3(0.01, 0.0, 0.0), slit * irisMask);\n" +
        "  col *= socketMask;\n" +                                      // confine the eyeball to the socket
        "  float halo = exp(-rs * 1.5) * (0.5 + (0.6 * flame2));\n" +   // soft surrounding glow (the sun-like bloom)
        "  col += vec3(1.6, 0.55, 0.12) * halo * (1.0 - socketMask);\n" +
        "  float alpha = clamp(max(socketMask, halo), 0.0, 1.0);\n" +
        "  if (alpha < 0.01) { discard; }\n" +
        "  fragColor = vec4(col * uEyePulse, alpha);\n" + // additive blend → blazing eye + halo, no fog
        "}\n";

    // ── Eagles soaring over Orla Perć (easter egg) ──────────────────────────────────────────────────────
    // Instanced camera-facing billboards: each eagle circles on a thermal (the vertex shader does the orbit +
    // the billboard), the fragment paints a dark soaring silhouette with slowly flapping, swept-back wings.
    private const string EagleVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec2 aCard;\n" +     // quad corner [-1,1]
        "layout(location=1) in vec4 aOrbit;\n" +    // orbit centre world (x,y,z) + radius
        "layout(location=2) in vec4 aMotion;\n" +   // phase, angularSpeed, size, flapPhase
        "uniform mat4 uViewProj;\n" +
        "uniform vec3 uCameraPos;\n" +
        "uniform float uTime;\n" +
        "out vec2 vCard;\n" +
        "out float vFlapPhase;\n" +
        "void main(){\n" +
        "  float ang = (uTime * aMotion.y) + aMotion.x;\n" +
        "  vec3 pos = vec3(aOrbit.x + (aOrbit.w * cos(ang)), aOrbit.y + (aOrbit.w * sin(ang)), aOrbit.z);\n" +
        "  vec3 toCam = normalize(uCameraPos - pos);\n" +
        "  vec3 right = normalize(cross(vec3(0.0, 0.0, 1.0), toCam));\n" +
        "  vec3 up = normalize(cross(toCam, right));\n" +
        "  float s = aMotion.z;\n" +
        "  vec3 world = pos + (right * (aCard.x * s)) + (up * (aCard.y * s * 0.55));\n" + // wingspan wider than fore-aft
        "  vCard = aCard;\n" +
        "  vFlapPhase = aMotion.w;\n" +
        "  gl_Position = uViewProj * vec4(world, 1.0);\n" +
        "}\n";

    private const string EagleFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vCard;\n" +
        "in float vFlapPhase;\n" +
        "uniform float uTime;\n" +
        "uniform vec3 uEagleColor;\n" +
        "out vec4 fragColor;\n" +
        "void main(){\n" +
        "  vec2 c = vCard;\n" +              // x = wingspan, y = fore (+) / aft (-)
        "  float ax = abs(c.x);\n" +
        "  float flap = sin((uTime * 6.0) + vFlapPhase);\n" +
        // Wing centreline: swept back toward the tail + flap lifting the tips; thickness tapers to the tip.
        "  float centre = (-0.18 * ax * ax) + ((0.04 + 0.32 * flap) * ax);\n" +
        "  float halfT = max(0.012, 0.15 * (1.0 - (ax / 0.98)));\n" +
        "  float wing = (ax < 0.98) ? (1.0 - smoothstep(halfT * 0.55, halfT, abs(c.y - centre))) : 0.0;\n" +
        "  float body = 1.0 - smoothstep(0.85, 1.0, length(vec2(c.x / 0.12, (c.y - 0.05) / 0.36)));\n" +
        "  float tail = (ax < 0.06) ? (smoothstep(-0.58, -0.50, c.y) * (1.0 - smoothstep(-0.34, -0.28, c.y))) : 0.0;\n" +
        "  float a = clamp(max(max(wing, body), tail), 0.0, 1.0);\n" +
        "  if (a < 0.03) { discard; }\n" +
        "  fragColor = vec4(uEagleColor, a);\n" +
        "}\n";

    // Fragment shader for the line/ribbon program (trails/route/roads): vertex colour + the SAME aerial-
    // perspective fog the terrain uses, so distant lines fade into the horizon haze instead of staying vivid
    // bright lines over the hazed far terrain (the "szlaki w niebie" look on a wide trail download). highp so
    // the world-space distance stays precise at tens-of-km ranges.
    private const string FragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec4 vColor;\n" +
        "in vec3 vWorldPos;\n" +
        "uniform vec3 uCameraPos;\n" +
        "uniform vec3 uFogColor;\n" +
        "uniform float uFogDensity;\n" +
        "uniform float uMaxDist;\n" + // hard cull radius (m) so the far trail network isn't drawn at the horizon
        "uniform float uGhostFade;\n" + // 1 = normal pass; <1 = the X-RAY pass (DepthFunc GREATER: only occluded fragments)
        "out vec4 fragColor;\n" +
        "void main(){\n" +
        "  float dist = length(vWorldPos - uCameraPos);\n" +
        // Cull trails past uMaxDist outright (the distant web that floats + parallaxes at the horizon), and
        // fade the last stretch toward the horizon haze so the cull edge isn't a hard line.
        "  if (dist > uMaxDist) { discard; }\n" +
        "  float edge = smoothstep(uMaxDist * 0.75, uMaxDist, dist);\n" +
        "  float fog = max(1.0 - exp(-dist * uFogDensity), edge);\n" +
        // Carry the per-vertex alpha through (opaque trails/roads upload a=255 → 1.0). The translucent dashed
        // route uploads a<255 so the trail it lies on shows through it; blending is enabled only for that draw.
        // uGhostFade dims the whole ribbon on the X-ray pass, so a trail buried inside a gully/behind a
        // buttress still reads as a faint "behind rock" ghost instead of vanishing.
        "  fragColor = vec4(mix(vColor.rgb, uFogColor, fog), vColor.a * uGhostFade);\n" +
        "}\n";

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
        "out vec3 vWorldPos;\n" +
        "void main(){\n" +
        "  vColor = aColor;\n" +
        "  vWorldPos = aPos;\n" +
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
        // Depth bias toward the camera so the line wins the z-fight with the 1 m detail it lies on — but it MUST
        // fade with distance or a ridge in front can't occlude the line. The old `* gl_Position.w` made it a
        // CONSTANT NDC bias: at a far plane of tens of km the NDC-z of distant terrain bunches near 1, so the
        // same bias that wins a near z-fight ALSO punched trails THROUGH faraway ridges ("szlaki widać przez
        // skały"). Subtracting a constant in CLIP space (NO `* w`) makes the NDC bias = C/w — strong up close
        // where the 1 m detail actually z-fights, ~0 far away where ridges must occlude. Plus 10 m TrailLift seating.
        // DEAD END — documented, do not keep tuning this constant (docs/HANDOFF-2026-06-27-trails-decal-plan.md):
        // "clip-space z -= 0.09 -> 0.14 = 'szlaki przez skały' regression... local rise only ~2 m within 3 m —
        // no tall local folds to clear, yet the line is still buried. A bias big enough to help also punches real
        // ridges. Single global value can't separate the two. STOP." The real fix is the uTrailMask DECAL
        // (painted into the terrain shader, like contour lines — never floats, never z-fights, because it IS the
        // surface fragment), not this line-overlay's depth bias. Left at the last-known value; do not raise it
        // further chasing occlusion — that is the decal's job.
        "  gl_Position.z -= 0.09;\n" +
        "}\n";

    private const float TrailHalfWidthPx = 0.7f;   // very thin trails — the line should read as a delicate thread, not a ribbon
    private const float TrailBlackHalfWidthPx = 1.15f; // black trails are drawn a touch thicker: a thin black thread on the dark terrain is nearly invisible, so widen it for legibility
    private const float RouteHalfWidthPx = 2.6f;
    private const float RoadHalfWidthPx = 1.8f;

    // The planned route is a DASHED, SEMI-TRANSPARENT violet line lying ON its trail (conflated onto the trail's
    // polyline): the dashes + ~60% alpha let the trail show through, so the route reads as a highlight over the
    // trail rather than painting it out. Drawn with alpha blending.
    private const byte RouteAlpha = 0x99; // ~60% opacity

    // Applying DrawRouteLine's proven pattern to trails too (docs/HANDOFF-2026-06-27-trails-decal-plan.md step 3,
    // never actually done for DrawTrailLines — only the route got it): the trail is now primarily carried by the
    // uTrailMask DECAL (painted into the terrain surface, immune to z-fighting). This line overlay stays as a
    // crisp on-top accent, but where it loses the near-field z-fight against real terrain relief (the documented
    // dead end this session re-confirmed — see the LineVertexShaderSource comment), it must not punch a hole:
    // alpha + depth-write-off lets the decal's own trail colour, already drawn as part of the terrain underneath,
    // show through instead of bare rock. 0xE0 (~88%) stays much more solid than the route's 60% "highlight" look
    // — trails are the primary navigation aid, not an accent — this only softens the failure mode, it doesn't
    // hide the line.
    private const byte TrailOverlayAlpha = 0xE0;
    private const int RouteDashSegments = 3; // ~15 m mark …
    private const int RouteGapSegments = 2;  // … then ~10 m gap over the ~5 m densified route segments

    // The route is ALSO painted INTO the surface decal (so it adheres + stays visible up close, where the floating
    // line is occluded by the streamed detail). It is conflated onto the trail, so it shares the trail's geometry;
    // the decal recolours the trail's distance-field texels toward violet along ~12 m dashes (≈60% blend, so the
    // trail shows through ⇒ translucent), leaving ~8 m gaps as the trail colour ⇒ dashed. Off-trail stretches are
    // written straight into the field so the dash is visible on bare terrain too.
    private const float RouteDecalDashMeters = 12f;
    private const float RouteDecalGapMeters = 8f;
    private const float RouteDecalBlend = 0.6f; // mix the trail texel 60% toward violet (translucent over the trail)
    // How far (world m) each dash recolours around the line — TIGHT (just the drawn half-width + ~1 texel) so the
    // dashes don't bleed across the 8 m gaps (a dash recolours ≈ dash + 2×radius; keep 2×radius well under the gap).
    private const float RouteDecalPaintRadiusMeters = TrailDecalHalfWidthMeters + 0.8f; // ≈2.4 m → ~3 m visible gap

    // Road ribbon colour: light grey, matching the 2D road layer and distinct from the PTTK trail palette.
    private const byte RoadR = 0xE5;
    private const byte RoadG = 0xE7;
    private const byte RoadB = 0xEB;

    // Sky clear colour (matches the Skia renderer's lower gradient stop).
    private const float SkyR = 0x6C / 255f;
    private const float SkyG = 0x8E / 255f;
    private const float SkyB = 0xB0 / 255f;

    // Overlays drape on the surface. Occlusion + the z-fight with the 1 m detail they lie on is handled by
    // the CLIP-space depth bias in the line shader (gl_Position.z -= 0.09: strong up close, ~0 far, so the
    // line wins the near z-fight but real ridges in front still occlude it — no "szlaki przez skały"). So the
    // WORLD-space lift only needs to be a hair of clearance, NOT a big offset: a large lift (was 13–18 m) made
    // the line visibly float a dozen-plus metres above steep walls up close (Orla Perć/Zawrat) — exactly the
    // "szlaki latają" complaint. Kept tiny + ordered (exposed slightly above trail so its dots sit on top of a
    // coincident trail line). Occlusion clearance is the depth bias's job, not the lift's.
    private const float TrailLiftMeters = 0.5f;           // basically on the surface now — overlays seat on the rendered mesh (step-aware) and the clip-space depth bias keeps them drawn on top, so the lift only needs to be a hair to avoid exact coplanarity. Was 2 m (read as ~1 m off the rock).
    private const float RouteLiftMeters = 0.8f;
    private const float RoadLiftMeters = 0.3f;
    private const float ExposedRouteLiftMeters = 1.0f;    // a touch above trails so the dots sit on a coincident trail line (Orla Perć is both a red trail and a demanding route)

    // Trail/route decal mask: a fine DISTANCE-FIELD texture over the near-field (detail) window. Square max
    // dimension. The field stores the metric distance to the nearest line out to TrailMaskMaxDistanceMeters; the
    // shader narrows it analytically to a thin crisp line, so the texture can be coarse and the line stays sharp.
    // 4096 over the ~4.6 km detail window ≈ 1.1 m/texel — fine enough for a continuous field; the band (8 m) spans
    // ~7 texels so there are no dot/gaps. The single RGBA8 4096² scratch is ~67 MB, allocated ONCE (reused).
    private const int TrailMaskTextureSize = 4096;
    private const float TrailMaskMaxDistanceMeters = 8.0f;   // distance-field reach (m): band ≥ ~4 texels → continuous
    // Was 1.0 (fully opaque) — the trail line then reads as a flat, textureless ribbon, which now stands out
    // starkly against the surrounding terrain's real shaded relief (fix B / NativeMicroDetail). 0.8 keeps the
    // trail clearly legible (it's still ~80% its own colour at full coverage) while letting a hint of the
    // underlying lit/detailed terrain show through, instead of erasing it outright.
    private const float TrailDecalStrength = 0.8f;           // blend strength of the decal over the surface colour
    private const float TrailDecalHalfWidthMeters = 1.6f;    // on-surface half-width of the drawn line (world m)
    private const float ExposedRouteHalfWidthPx = 2.4f;   // fat dots that clearly punctuate over the thin (0.7 px) trail line beneath
    // Bright orange for the exposed / guide routes (sac_scale demanding / via_ferrata) — lighter/yellower than the PTTK red so it pops where it runs along a red trail.
    private const byte ExposedR = 0xFF, ExposedG = 0x8C, ExposedB = 0x00;

    // Contour lines (warstwice): drawn IN the terrain shader from each fragment's elevation, so they lie on
    // whatever LOD is rendered (coarse base OR 1 m detail) — no float, no rock poke-through, crisp at any zoom.
    private const double ContourIntervalMeters = 5.0;        // minor contour spacing (m)
    private const double ContourMajorIntervalMeters = 100.0; // red index (major) contour spacing (m)
    private const float ContourStrengthOn = 0.4f;      // line tint strength when the layer is on (0 = off) — subtle so the 1 m detail/ortho stays dominant
    private const float ContourWidthPx = 0.7f;         // line half-width in pixels (fwidth-based AA)
    private const byte ContourR = 158, ContourG = 104, ContourB = 60;                // minor line — warm topo brown
    private const byte ContourMajorR = 206, ContourMajorG = 52, ContourMajorB = 40;  // major index line — red

    private sealed class TileBuffers
    {
        public uint Vao;
        public uint PositionVbo;
        public uint ColorVbo;
        public uint NormalVbo;
        public uint TexVbo;
        public uint DetailVbo;
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

    // ── Per-pass GPU timing (diagnostic) ───────────────────────────────────────────────────────────
    // GL_TIME_ELAPSED timer queries, one per (pass × frame-in-flight). Results are read back GpuFramesInFlight
    // frames LATE so the CPU never stalls on the GPU. Desktop/ANGLE only (GL_EXT_disjoint_timer_query); on mobile
    // GLES the extension is absent so this fails safe to a no-op (matching the cumulus/sauron/msaa "unsupported"
    // pattern). Emits a throttled "[GL3D] [PassTimes]" line so we can see WHERE a heavy frame's time actually goes.
    private enum GpuPass { Shadow, Reflection, Terrain, LakesForest, Lines, Clouds, Post, Count }
    private const int GpuFramesInFlight = 3;
    private const QueryTarget GlTimeElapsedExt = (QueryTarget)0x88BF; // GL_TIME_ELAPSED_EXT
    private const GLEnum GlGpuDisjointExt = (GLEnum)0x8FBB;           // GL_GPU_DISJOINT_EXT
    private uint[]? gpuQueries;                                       // [(int)GpuPass.Count * GpuFramesInFlight]
    private readonly double[] lastPassMs = new double[(int)GpuPass.Count];
    private bool gpuTimersSupported;
    private bool gpuTimersProbed;
    private int gpuFrameSlot = -1;
    private long gpuFrameCount;
    private long lastPassTimesLogTick;

    private uint program;
    private int mvpLocation = -1;
    private int modelOffsetLocation = -1;
    private int stableOffsetLocation = -1;
    private int debugPolyLocation = -1;
    private int lakeCenterLocation = -1;
    private int lakeRadiusLocation = -1;

    // Planar water reflection: uniform locations + a half-resolution colour target (texture + depth RB) the
    // pre-pass renders the mirrored terrain into, which the lake mesh then samples. Behind ReflectionEnabled.
    private int reflectionPassLocation = -1;
    private int waterClipZLocation = -1;
    private int reflectionTexLocation = -1;
    private int reflectionEnabledLocation = -1;
    private int viewportPxLocation = -1;
    private uint reflectionFbo;
    private uint reflectionColorTex;
    private uint reflectionDepthRb;
    private int reflectionTexW;
    private int reflectionTexH;
    private bool reflectionUnsupported;

    /// <summary>Representative lake elevation (m a.s.l.) for the single reflection plane — Morskie Oko.</summary>
    private const float ReflectionLakeElevationM = 1395f + 4f; // matches the water mesh's waterElev

    /// <summary>When <c>true</c>, lakes get a real planar reflection of the terrain (else a cheap gradient).</summary>
    public bool ReflectionEnabled { get; set; } = true;

    // Wider-coverage P0 step 6: render the terrain + lake water in a camera-relative frame (origin = the look-at
    // target) so vertices and the view translation stay small (float precision for far/streamed scene origins).
    // mvpRender = Translate(R)·mvp with uModelOffset = -R cancel EXACTLY (the on-screen image is identical), while
    // uStableOffset stays 0 so all procedural sampling (vStableWorldPos) keeps the absolute world frame — no
    // noise drift. The reflection pre-pass and the line/forest programs keep the absolute frame and co-render.
    // KILL-SWITCH: set false to fall straight back to the absolute-frame behaviour.
    private const bool CameraRelativeTerrainOrigin = true;

    // DEBUG: Morskie Oko real outline from OSM (way 27952583), to check how the polygon aligns with the ortho.
    // Lake-water draw ranges: one per in-view lake — offset+count into the shared water VBO, plus the lake's
    // world-XY centroid + radius for the shader's smooth radial depth. Rebuilt each frame by BuildLakeWater.
    private readonly struct LakeDraw
    {
        public LakeDraw(int vertexOffset, int vertexCount, Vector2 center, float radius)
        {
            VertexOffset = vertexOffset; VertexCount = vertexCount; Center = center; Radius = radius;
        }

        public int VertexOffset { get; }
        public int VertexCount { get; }
        public Vector2 Center { get; }
        public float Radius { get; }
    }

    private readonly List<LakeDraw> lakeDraws = new();
    private uint debugPolyVao;
    private uint debugPolyVbo;
    private int debugPolyVertexCount;
    private float[]? debugPolyFloats;
    private int lightDirLocation = -1;
    private int snowSunLocation = -1;

    /// <summary>
    /// When set, the SNOW line uses this fixed sun direction instead of the live one — so a route film can
    /// sweep the time (and thus the lighting) without melting/reforming the snow cover the user set. Null =
    /// snow follows the live sun (normal behaviour).
    /// </summary>
    public Vector3? SnowSunOverride { get; set; }
    private int ambientLocation = -1;
    private int sunColorLocation = -1;
    private int skyAmbientLocation = -1;
    private int orthoSamplerLocation = -1;
    private int useOrthoLocation = -1;
    private int orthoGlobalFadeLocation = -1;
    private int orthoTexelLocation = -1;
    private int sharpenLocation = -1;
    private int debugUvLocation = -1;
    private int orthoMinXyLocation = -1;
    private int orthoMaxXyLocation = -1;
    private int orthoBlendLocation = -1;
    private System.Numerics.Vector2 orthoCoverageMin;
    private System.Numerics.Vector2 orthoCoverageMax;
    private MapaTur.Domain.Geography.MapBounds? orthoCoverageGeo; // null = no cull (pure ortho everywhere)
    private float orthoCoverageBlendMeters;

    /// <summary>Ortho coverage geographic bounds + soft edge-blend width. The renderer converts the bounds to
    /// world XY each frame (via the tiles' anchor) and the shader fades ortho→hypsometric beyond them — fixing
    /// the stretched-edge "strata" bands where a base is wider than its ortho. Null bounds disables the cull.</summary>
    public void SetOrthoCoverageGeoBounds(MapaTur.Domain.Geography.MapBounds? geoBounds, float blendMeters)
    {
        orthoCoverageGeo = geoBounds;
        orthoCoverageBlendMeters = blendMeters;
    }
    private int slopeModeLocation = -1;
    private int rockStrengthLocation = -1;
    private int slopePaletteLocation = -1;
    private int biomeModeLocation = -1;
    private int biomeScreeSlopeLocation = -1;
    private int biomeMeadowMaxZLocation = -1;
    private int biomeSnowZLocation = -1;
    private int biomeIceZLocation = -1;
    private int biomeAspectShiftZLocation = -1;
    private int biomePaletteLocation = -1;

    // The slope-band palette flattened to 8×RGB, built once from the unit-tested SlopePalette and uploaded
    // as the uSlopePalette uniform array. Single source of truth shared with SlopeClassification.
    private static readonly float[] SlopePaletteFloats = BuildSlopePaletteFloats();

    private static float[] BuildSlopePaletteFloats()
    {
        IReadOnlyList<Vector3> colors = SlopePalette.All;
        var flat = new float[colors.Count * 3];
        for (int i = 0; i < colors.Count; i++)
        {
            flat[(i * 3) + 0] = colors[i].X;
            flat[(i * 3) + 1] = colors[i].Y;
            flat[(i * 3) + 2] = colors[i].Z;
        }
        return flat;
    }

    // The biome material palette flattened to 5×RGB (Meadow, Scree, Rock, Snow, Ice), built once from the
    // unit-tested BiomePalette and uploaded as uBiomePalette. Single source of truth shared with BiomeClassifier.
    private static readonly float[] BiomePaletteFloats = BuildBiomePaletteFloats();

    private static float[] BuildBiomePaletteFloats()
    {
        IReadOnlyList<Vector3> colors = BiomePalette.All;
        var flat = new float[colors.Count * 3];
        for (int i = 0; i < colors.Count; i++)
        {
            flat[(i * 3) + 0] = colors[i].X;
            flat[(i * 3) + 1] = colors[i].Y;
            flat[(i * 3) + 2] = colors[i].Z;
        }
        return flat;
    }
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
    private int terrainContourSpacingZLocation = -1;
    private int terrainContourColorLocation = -1;
    private int terrainContourMajorSpacingZLocation = -1;
    private int terrainContourMajorColorLocation = -1;
    private int terrainContourStrengthLocation = -1;
    private int terrainContourWidthPxLocation = -1;
    private int trailMaskSamplerLocation = -1;
    private int trailMaskStrengthLocation = -1;
    private int trailMaskMinXYLocation = -1;
    private int trailMaskSizeXYLocation = -1;
    private int trailMaskMaxDistLocation = -1;
    private int trailMaskHalfWidthLocation = -1;
    private int terrainSnowBandZLocation = -1;
    private int terrainSnowSlopeCosBareLocation = -1;
    private int terrainSnowSlopeCosFullLocation = -1;
    private int terrainNoonSnowLiftLocation = -1;

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
    private int skySunGlowIntensityLocation = -1;
    private int skySunGlowWidthLocation = -1;
    private uint skyVao;
    private uint skyVbo;

    // Night-sky star program: catalog stars drawn as point sprites in the sky pass (after the gradient).
    // The VBO holds (dir.xyz, magnitude) per above-horizon star and is re-uploaded only when the Julian-Date
    // inputs change (slider hour / date / observer location), not every frame.
    private uint starProgram;
    private int starViewProjLocation = -1;
    private int starNightFactorLocation = -1;
    private int starStarsOnLocation = -1;
    private uint starVao;
    private uint starVbo;
    private int starCount;
    private bool starBufferReady;
    private int lastStarDateKey;
    private double lastStarLocalHour = double.NaN;
    private double lastStarLat = double.NaN;
    private double lastStarLon = double.NaN;
    private float[]? starScratch;

    // Night-sky Moon program: one phased disc point sprite (no VBO — position comes from the uMoonDir uniform).
    private uint moonProgram;
    private int moonViewProjLocation = -1;
    private int moonDirLocation = -1;
    private int moonSizeLocation = -1;
    private int moonTermDirLocation = -1;
    private int moonIlluminatedLocation = -1;
    private int moonNightFactorLocation = -1;

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
    private int cloudDispScaleLocation = -1;
    private int cloudDispAmpLocation = -1;
    private uint cloudVao;
    private uint cloudVbo;
    private uint cloudIbo;
    private int cloudIndexCount;

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
        public required byte[] Rgba; // bytes at the CURRENTLY uploaded (or about-to-be-uploaded) resolution
        public int Width;
        public int Height;
        public uint Texture; // 0 until uploaded

        // The cell's retained "master" copy at OrthoDistanceTier.NearCapPx — kept separately from Rgba so a
        // cell that was downsampled to the far tier can be rebuilt back to near without re-decoding from disk
        // when the camera approaches it. Never mutated after SetOrthoTextures.
        public required byte[] MasterRgba;
        public int MasterWidth;
        public int MasterHeight;

        // The resolution cap Rgba/Width/Height currently reflect; 0 = never assigned/uploaded yet. Compared
        // against OrthoDistanceTier.DesiredCapPx every frame to detect a tier change.
        public int UploadedCapPx;
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
    // Throttle for the [Mem] line: emit at most once per this interval (so it tracks the film without spamming).
    private long lastMemLogTick;
    private const long MemLogIntervalMs = 3000;
    private uint lineProgram;
    private int lineMvpLocation = -1;
    private int lineViewportLocation = -1;
    private int lineHalfPxLocation = -1;
    private int lineFogColorLocation = -1;
    private int lineFogDensityLocation = -1;
    private int lineCameraPosLocation = -1;
    private int lineMaxDistLocation = -1;
    private int lineGhostFadeLocation = -1;

    // Cumulus puffs (Tier 2 clouds): one instanced billboard program + a static per-puff buffer generated once.
    private uint cumulusProgram;
    private int cumulusMvpLocation = -1;
    private int cumulusCameraPosLocation = -1;
    private int cumulusFieldCenterLocation = -1;
    private int cumulusBaseAltitudeLocation = -1;
    private int cumulusDriftLocation = -1;
    private int cumulusSunDirLocation = -1;
    private int cumulusCloudLitLocation = -1;
    private int cumulusCloudShadowLocation = -1;
    private int cumulusFogColorLocation = -1;
    private int cumulusFogDensityLocation = -1;
    private int cumulusOpacityLocation = -1;
    private uint cumulusVao;
    private uint cumulusQuadVbo;
    private uint cumulusInstanceVbo;
    private int cumulusInstanceCount;
    private Vector2 cumulusDriftAccum;        // integrated downwind drift of the cumulus field (m), accumulated per frame
    private float lastCumulusDriftSeconds = -1f;
    private bool cumulusUnsupported; // a cumulus shader/link failure disables clouds only, never the whole engine

    // Sauron's tower (easter egg): one program + one static mesh (tower cone + emissive eye orb), placed on Świnica.
    private uint sauronProgram;
    private int sauronViewProjLocation = -1;
    private int sauronModelOffsetLocation = -1;
    private int sauronVerticalScaleLocation = -1;
    private int sauronSunDirLocation = -1;
    private int sauronSunColorLocation = -1;
    private int sauronAmbientLocation = -1;
    private int sauronCameraPosLocation = -1;
    private int sauronFogColorLocation = -1;
    private int sauronFogDensityLocation = -1;
    private int sauronEyePulseLocation = -1;
    private int sauronTimeLocation = -1;
    private uint sauronVao;
    private uint sauronVbo;
    private int sauronVertexCount;
    private int sauronTowerVertexCount; // the tower verts come first; the last 6 are the eye billboard quad
    private bool sauronUnsupported; // a shader/link failure disables the easter egg only, never the whole engine
    private static readonly GeoPoint SwinicaLocation = new(49.219417, 20.009306);

    // Eagles soaring over Orla Perć (easter egg): one instanced billboard program, instances rebuilt per frame
    // (orbit centres are geo points along the ridge, projected into the current world frame).
    private uint eagleProgram;
    private int eagleViewProjLocation = -1;
    private int eagleCameraPosLocation = -1;
    private int eagleTimeLocation = -1;
    private int eagleColorLocation = -1;
    private uint eagleVao;
    private uint eagleQuadVbo;
    private uint eagleInstanceVbo;
    private int eagleInstanceCount;
    private bool eagleUnsupported;
    // Orbit centres strung along the Orla Perć ridge (Świnica → Granaty); a few eagles thermal over each.
    private static readonly GeoPoint[] EagleOrbitCenters =
    {
        new(49.2205, 20.0125),
        new(49.2270, 20.0285),
        new(49.2335, 20.0460),
    };

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

    // Post-process target: a second colour TEXTURE + FBO. After the scene is resolved into presentColorTex,
    // the post stage runs fullscreen passes (currently a pass-through; bloom / god rays build on this) that
    // sample presentColorTex and write here, and the caller then wraps THIS texture instead. Full-res so the
    // pass-through stays pixel-identical; the blur pyramid (bloom) will use its own smaller buffers. Falls
    // back to presentColorTex (postUnsupported) if the framebuffer is ever incomplete, so the scene is never
    // lost. Reuses the sky pass's fullscreen-triangle VAO/VBO (same location-0 vec2 clip attribute).
    private uint postFbo;
    private uint postColorTex;
    private int postWidth;
    private int postHeight;
    private bool postUnsupported;
    private readonly bool postProcessEnabled = true; // compile-time kill-switch; bloom/god-rays will make it runtime-settable
    private uint postProgram;
    private int postTexLocation = -1;
    private bool postStageLogged; // log the post stage's FBO status once, not every frame

    // Bloom: two half-resolution colour buffers (ping-pong) for the bright-pass + separable Gaussian blur,
    // composited additively over the full-res scene into postColorTex. Half-res keeps it cheap and gives the
    // soft spread for free on upsample. Falls back to the plain pass-through if these can't be created.
    private uint bloomBrightFbo, bloomBrightTex;            // shared bright-pass output (bloom blur AND god rays read it)
    private uint bloomFboA, bloomTexA, bloomFboB, bloomTexB; // bloom blur ping-pong; A holds the final bloom
    private uint godrayFbo, godrayTex;                       // god-ray radial-blur output
    private int bloomWidth, bloomHeight; // half of the present size
    private bool bloomUnsupported;
    private uint bloomBrightProgram, bloomBlurProgram, bloomCompositeProgram, godrayProgram;
    private int bloomBrightTexLoc = -1, bloomBrightThresholdLoc = -1;
    private int bloomBlurTexLoc = -1, bloomBlurDirLoc = -1;
    private int godrayTexLoc = -1, godraySunUvLoc = -1;
    private int bloomCompSceneLoc = -1, bloomCompBloomLoc = -1, bloomCompIntensityLoc = -1;
    private int bloomCompGodrayLoc = -1, bloomCompGodrayIntensityLoc = -1;
    private bool bloomStageLogged;
    private bool godrayStageLogged;

    // Cascaded Shadow Maps (Krok 5): per-cascade depth textures rendered from the sun's POV, sampled in the
    // terrain shader (part 4). Cascades cover near→far slices of the camera frustum (CascadeShadowSplits),
    // each fit by an orthographic light matrix (CascadeLightMatrix). aPos is absolute world, so the depth
    // pass transforms it straight by the cascade light matrix — no model/stable offset needed.
    private const int ShadowCascadeCount = 3;
    private const int ShadowMapSize = 1024; // mobile-friendly (was 2048); raise later if quality needs it
    private const float ShadowMaxDistance = 15000f; // cap cascade far so texels stay dense over visible terrain
    private const float ShadowSplitLambda = 0.85f;
    private readonly uint[] shadowFbos = new uint[ShadowCascadeCount];
    private readonly uint[] shadowDepthTex = new uint[ShadowCascadeCount];
    private readonly Matrix4x4[] cascadeLightVp = new Matrix4x4[ShadowCascadeCount];
    private readonly float[] cascadeSplitFar = new float[ShadowCascadeCount];
    private bool shadowMapsAllocated;
    private bool shadowUnsupported;
    private readonly bool shadowsEnabled = true; // re-enabled after the unit-0 sampler-collision fix; device perf test
    private uint shadowDepthProgram;
    private int shadowLightVpLoc = -1;
    private bool shadowPassLogged;
    // Shadow-sampling uniforms on the terrain program (part 4) + per-frame active flag + tuning strength.
    private int shadowMap0Loc = -1, shadowMap1Loc = -1, shadowMap2Loc = -1;
    private int cascadeVp0Loc = -1, cascadeVp1Loc = -1, cascadeVp2Loc = -1;
    private int cascadeSplitLoc = -1, shadowStrengthLoc = -1;
    private bool shadowsActiveThisFrame;
    private const float ShadowStrength = 0.7f;

    private readonly Dictionary<TerrainMesh3D, TileBuffers> tileBuffers = new();
    private IReadOnlyList<TerrainMesh3D>? lastTiles;
    // Deferred mesh upload: a detail reload can bring ~100 new tiles; uploading them all in the swap frame froze
    // it ~300 ms. Instead enqueue here and upload a few per frame (DrainTileUploads) — the detail fills in over a
    // few frames with no single-frame freeze. Reused (cached) tiles are already resident ⇒ never enqueued.
    private readonly List<TerrainMesh3D> pendingTileUploads = new();
    private const int TileUploadBudgetPerFrame = 6;
    // Per-swap render-thread cost breakdown (the frame after a detail reload re-runs these over the new tile list).
    private bool dbgTileSwapFrame;
    private double dbgSwapSyncMs, dbgSwapOrthoMs, dbgSwapLakeMs;
    // Frame-gap watchdog: catches stalls the per-pass timers miss (GC pauses, CPU starvation by the off-thread build).
    private readonly System.Diagnostics.Stopwatch frameClock = System.Diagnostics.Stopwatch.StartNew();
    private long dbgLastFrameMs;
    private int dbgLastGen2;

    private LineBuffers? trailLines;
    private LineBuffers? trailLinesBlack; // black trails drawn in a second pass at a thicker width (legibility on dark terrain)
    private IReadOnlyList<Trail>? lastTrails;
    private DemRaster? lastTrailRaster;
    private TerrainMesh3D? lastTrailMesh;
    private DetailElevationField? lastTrailDetail;

    // Near-parallel duplicate trails (OSM relation + underlying way) deduped ONCE per distinct input set and reused
    // for the decal mask, the trail line overlay AND the route conflation — so only one of a duplicate pair is drawn
    // and the route lands on it. Keyed on the input ref so it is NOT recomputed every frame (the deduped ref is then
    // a stable cache key for the mask/line/route caches below — no churn, no per-frame allocation).
    private IReadOnlyList<Trail>? dedupInputTrails;
    private IReadOnlyList<Trail>? dedupResultTrails;

    private LineBuffers? routeLines;
    private Route? lastRoute;
    private IReadOnlyList<Trail>? lastRouteTrails;
    private DemRaster? lastRouteRaster;
    private TerrainMesh3D? lastRouteMesh;
    private DetailElevationField? lastRouteDetail;

    private LineBuffers? roadLines;
    private IReadOnlyList<Trail>? lastRoads;
    private DemRaster? lastRoadRaster;
    private TerrainMesh3D? lastRoadMesh;
    private DetailElevationField? lastRoadDetail;

    private LineBuffers? exposedLines;
    private IReadOnlyList<Trail>? lastExposed;
    private DemRaster? lastExposedRaster;
    private TerrainMesh3D? lastExposedMesh;
    private DetailElevationField? lastExposedDetail;

    // Trail/route decal (Option A): a painted-distance texture sampled by the terrain shader so trails are drawn
    // INTO the surface (base + detail) instead of as a floating line overlay. The line overlays are still drawn
    // alongside (far field, outside the decal window).
    //
    // The mask is addressed by ABSOLUTE world-XY in the shader, so it stays valid as the 1 m detail streams within
    // its window — it does NOT need rebuilding when `detail`/`mesh` change (that churned the key on nearly every
    // detail stream, and each rebuild allocates ~48 MB → GC spiral, multi-GB heap, multi-minute stalls). So the
    // cache key is ONLY (trails/roads/exposed/route refs) + the QUANTIZED mask window (min-corner + size snapped to
    // a 500 m grid): rebuild when the lines change or the window jumps a whole grid cell — rare. The rasterisation
    // scratch buffers are held as fields and reused across rebuilds (reallocated only on a dimension change).
    private const float TrailMaskWindowQuantMeters = 500f; // snap window min/size to this grid so it rebuilds rarely
    private uint trailMaskTex;
    private bool trailMaskValid;
    private float trailMaskMinX, trailMaskMinY, trailMaskSizeX, trailMaskSizeY;
    private IReadOnlyList<Trail>? lastMaskTrails;
    private IReadOnlyList<Trail>? lastMaskRoads;
    private IReadOnlyList<Trail>? lastMaskExposed;
    private IReadOnlyList<Trail>? lastMaskWaterways;
    private IReadOnlyList<MapaTur.Application.Waterways.Waterfall>? lastMaskWaterfalls;
    private Route? lastMaskRoute; // route is in the decal too (dashed translucent on the trail) → part of the key
    private DemRaster? lastMaskRaster;

    /// <summary>Watercourse polylines (waterway=river|stream), painted into the terrain as a shiny water decal.</summary>
    public IReadOnlyList<Trail>? Waterways { get; set; }

    /// <summary>Waterfall points rendered as bright foam accents on their streams.</summary>
    public IReadOnlyList<MapaTur.Application.Waterways.Waterfall>? Waterfalls { get; set; }

    // Parallel single-channel water distance field (unit 6) — drives the wet tint + specular glint.
    private uint waterMaskTex;
    private bool waterMaskValid;
    private long lastMaskSkipLogTick;

    // The mask builder has three SILENT early-outs (no raster / no lines / degenerate window) that all present
    // identically on screen: "decal po prostu nie ma". One throttled line names the exit instead.
    private void LogMaskSkipThrottled(string reason)
    {
        long now = Environment.TickCount64;
        if (now - lastMaskSkipLogTick >= 5000)
        {
            lastMaskSkipLogTick = now;
            Log.Information("[GL3D] [TrailMask] skip: {Reason}", reason);
        }
    }
    private int waterMaskSamplerLocation = -1;
    private int waterMaskStrengthLocation = -1;
    private bool haveMaskWindowKey;
    private float lastMaskKeyMinX, lastMaskKeyMinY, lastMaskKeySizeX, lastMaskKeySizeY; // quantized window key
    private byte[]? maskRgbaScratch;
    private int[]? maskPriorityScratch;
    private float[]? maskDistanceScratch;
    private int maskScratchTexels = -1;

    private LineBuffers? cableLines;
    private CableCarLine? lastCableCarBuilt;
    private TerrainMesh3D? lastCableMesh;
    private DetailElevationField? lastCableDetail;
    private float lastCableExaggeration = -1f;

    /// <summary>Aerialway line to overlay (sagging cables + station masts), or null for none. Drawn only
    /// when <see cref="ShowCableCar"/> is set; reuses the absolute-frame line ribbon pipeline.</summary>
    public CableCarLine? CableCar { get; set; }

    /// <summary>Whether the cable-car overlay is drawn this frame.</summary>
    public bool ShowCableCar { get; set; }

    /// <summary>Whether the contour-line (warstwice) overlay is drawn this frame.</summary>
    public bool ShowContours { get; set; }

    /// <summary>Whether the trail/route decal (painted into the terrain surface) is built + drawn this frame.
    /// ON by default. The mask is addressed by absolute world-XY, so it survives detail streaming inside its
    /// window — the rebuild is keyed only on (trail/route refs + the quantized window) and reuses its scratch
    /// buffers, so it is rare and allocation-free (no more GC spiral). The line overlays are drawn regardless,
    /// so the far field outside the decal window still shows trails.</summary>
    public bool ShowTrailDecal { get; set; } = true;

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
    /// When <c>false</c>, the orthophoto drape is suppressed even if cells are uploaded — the terrain falls
    /// back to its hypsometric (elevation-tinted) shading. The textures stay resident so toggling back on is
    /// instant. Driven by the premium menu's "Ortofoto" switch.
    /// </summary>
    public bool OrthoEnabled { get; set; } = true;

    /// <summary>
    /// Global ortho ↔ hypsometric blend: 1 = full orthophoto (normal 3D view), 0 = pure hypsometric
    /// colours. Driven per frame by the "2D map" mode so the photo fades out as the camera climbs into
    /// the top-down map view and fades back on descent. Multiplies into the per-fragment coverage blend.
    /// </summary>
    public float OrthoGlobalFade { get; set; } = 1f;

    /// <summary>Geographic extent covered by the streamed 1 m detail (null = none). Lakes inside keep the
    /// proven legacy water seating (their fine basin is real); lakes outside are seated/skipped against the
    /// loaded coarse raster so a water plane can't poke through a coarse-filled basin as dark slivers.</summary>
    public MapaTur.Domain.Geography.MapBounds? LakeFineBounds { get; set; }

    /// <summary>
    /// Baked z13-z16 tile index, when a baked pyramid is loaded — lets trail/route/road line seating sample the
    /// SAME real elevation data the baked-streaming renderer actually draws (MapaTur.Application.Terrain.
    /// Trail3DWorldProjection.ToWorld's bakedIndex parameter), instead of the static coarse base raster that's
    /// otherwise the only source while baked streaming is active (the legacy per-tile DetailElevationField never
    /// populates then — confirmed regression: lines seated "independently of the terrain"). Null = old behaviour.
    /// </summary>
    public MapaTur.Application.Terrain.BakedTileAvailabilityIndex? BakedElevationIndex { get; set; }

    /// <summary>
    /// When <c>true</c>, the terrain is shaded by the avalanche slope-steepness palette (overriding the
    /// ortho/hypsometric base and suppressing snow). Driven by the premium menu's "Mapa nachylenia" switch.
    /// </summary>
    public bool SlopeMapEnabled { get; set; }

    /// <summary>
    /// Strength [0,1] of the rock material blended onto steep faces, where a top-down orthophoto smears
    /// (no data for near-vertical walls). 0 = pure ortho; 1 = full rock on the steepest faces. Slope-driven
    /// in the shader (gentle = ortho, steep = rock). Default on; a future "Materiały/Skały" slider can drive it.
    /// </summary>
    public float RockStrength { get; set; } = 1f;

    /// <summary>
    /// When <c>true</c>, the terrain base albedo is painted by elevation-zone biomes (meadow/hala, scree/piargi,
    /// snow, ice) from elevation + slope + aspect — the unit-tested <see cref="BiomeClassifier"/> mirrored in the
    /// shader. The granite rock material and the dynamic snow slider still layer on top. Driven by the premium
    /// menu's "Biomy" switch; off by default (an A/B material mode over the ortho/hypsometric base).
    /// </summary>
    public bool BiomeMaterialEnabled { get; set; }

    /// <summary>
    /// Whether MSAA anti-aliasing is used. <c>false</c> (the "Wydajność" quality profile) draws straight
    /// into the present FBO — jaggier edges, but skips the multisample resolve for more headroom.
    /// </summary>
    public bool MsaaEnabled { get; set; } = true;

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
                // Retain a "master" copy at OrthoDistanceTier.NearCapPx (area/box average from the source) —
                // the highest resolution any cell can reach. Which cells actually GET that resolution vs the
                // coarser far tier is decided per-frame in StreamOrthoTextures from camera distance (a cell
                // right under the camera should not be as coarse as one 50 km away); Rgba/Width/Height start
                // equal to the master and UploadedCapPx=0 so the very first StreamOrthoTextures call picks the
                // correct tier before anything is ever uploaded to the GPU.
                (byte[] master, int mw, int mh) =
                    OrthoCellDownsampler.Downsample(rgba, w, h, OrthoDistanceTier.NearCapPx);
                orthoTiles.Add(new OrthoTile
                {
                    Rgba = master,
                    Width = mw,
                    Height = mh,
                    MasterRgba = master,
                    MasterWidth = mw,
                    MasterHeight = mh,
                    UploadedCapPx = 0,
                });
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
        IReadOnlyList<TreeInstance>? forest = null,
        DetailElevationField? detail = null,
        DateOnly? localDate = null,
        IReadOnlyList<Trail>? exposedRoutes = null,
        bool showSauronTower = false,
        bool showEagles = false,
        bool animateAtmosphere = true)
    {
        gl ??= PlatformGl.Get();

        // Frame-gap watchdog: a long gap since the previous frame = a stall the per-pass timers don't see.
        // Logging the Gen2 delta + heap distinguishes a GC pause from off-thread-build CPU starvation.
        long nowFrameMs = frameClock.ElapsedMilliseconds;
        long frameGap = nowFrameMs - dbgLastFrameMs;
        int gen2Now = GC.CollectionCount(2);
        if (dbgLastFrameMs > 0 && frameGap > 150)
        {
            Log.Information(
                "[GL3D] frame gap {Gap}ms (gen2 +{Gen2Delta}, totalGen2={Gen2}, heap={HeapMB}MB, pendingUploads={Pending})",
                frameGap, gen2Now - dbgLastGen2, gen2Now, GC.GetTotalMemory(false) / (1024 * 1024), pendingTileUploads.Count);
        }

        dbgLastFrameMs = nowFrameMs;
        dbgLastGen2 = gen2Now;

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
            lastTrailDetail = null;
            dedupInputTrails = null;
            dedupResultTrails = null;
            routeLines = null;
            lastRoute = null;
            lastRouteTrails = null;
            lastRouteRaster = null;
            lastRouteMesh = null;
            lastRouteDetail = null;
            roadLines = null;
            lastRoads = null;
            lastRoadRaster = null;
            lastRoadMesh = null;
            lastRoadDetail = null;
            exposedLines = null;
            lastExposed = null;
            lastExposedRaster = null;
            lastExposedMesh = null;
            lastExposedDetail = null;
            cableLines = null;
            lastCableCarBuilt = null;
            lastCableMesh = null;
            lastCableExaggeration = -1f;
            cumulusProgram = 0;
            cumulusInstanceCount = 0;
            cumulusUnsupported = false;
            sauronProgram = 0;
            sauronVertexCount = 0;
            sauronUnsupported = false;
            eagleProgram = 0;
            eagleInstanceCount = 0;
            eagleUnsupported = false;
            programReady = false;
            mvpLocation = -1;
            modelOffsetLocation = -1;
            stableOffsetLocation = -1;
            lightDirLocation = -1;
            ambientLocation = -1;
            sunColorLocation = -1;
            skyAmbientLocation = -1;
            orthoSamplerLocation = -1;
            useOrthoLocation = -1;
            orthoGlobalFadeLocation = -1;
            orthoTexelLocation = -1;
            sharpenLocation = -1;
            debugUvLocation = -1;
            orthoMinXyLocation = -1;
            orthoMaxXyLocation = -1;
            orthoBlendLocation = -1;
            slopeModeLocation = -1;
            rockStrengthLocation = -1;
            slopePaletteLocation = -1;
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
            terrainContourSpacingZLocation = -1;
            terrainContourColorLocation = -1;
            terrainContourMajorSpacingZLocation = -1;
            terrainContourMajorColorLocation = -1;
            terrainContourStrengthLocation = -1;
            terrainContourWidthPxLocation = -1;
            terrainSnowBandZLocation = -1;
            terrainSnowSlopeCosBareLocation = -1;
            terrainSnowSlopeCosFullLocation = -1;
            terrainNoonSnowLiftLocation = -1;
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
            skySunGlowIntensityLocation = -1;
            skySunGlowWidthLocation = -1;
            skyVao = 0;
            skyVbo = 0;
            starProgram = 0;
            starViewProjLocation = -1;
            starNightFactorLocation = -1;
            starStarsOnLocation = -1;
            starVao = 0;
            starVbo = 0;
            starCount = 0;
            starBufferReady = false;
            moonProgram = 0;
            moonViewProjLocation = -1;
            moonDirLocation = -1;
            moonSizeLocation = -1;
            moonTermDirLocation = -1;
            moonIlluminatedLocation = -1;
            moonNightFactorLocation = -1;
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
            cloudDispScaleLocation = -1;
            cloudDispAmpLocation = -1;
            cloudVao = 0;
            cloudVbo = 0;
            cloudIbo = 0;
            cloudIndexCount = 0;
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
            // Post-process FBO / colour texture / program — same context-loss handling: drop stale IDs.
            postFbo = 0;
            postColorTex = 0;
            postWidth = 0;
            postHeight = 0;
            postUnsupported = false;
            postProgram = 0;
            postTexLocation = -1;
            postStageLogged = false;
            // Bloom + god-ray buffers/programs — same context-loss handling.
            bloomBrightFbo = bloomBrightTex = 0;
            bloomFboA = bloomTexA = bloomFboB = bloomTexB = 0;
            godrayFbo = godrayTex = 0;
            bloomWidth = 0;
            bloomHeight = 0;
            bloomUnsupported = false;
            bloomBrightProgram = 0;
            bloomBlurProgram = 0;
            bloomCompositeProgram = 0;
            godrayProgram = 0;
            bloomBrightTexLoc = bloomBrightThresholdLoc = -1;
            bloomBlurTexLoc = bloomBlurDirLoc = -1;
            godrayTexLoc = godraySunUvLoc = -1;
            bloomCompSceneLoc = bloomCompBloomLoc = bloomCompIntensityLoc = -1;
            bloomCompGodrayLoc = bloomCompGodrayIntensityLoc = -1;
            bloomStageLogged = false;
            godrayStageLogged = false;
            // Shadow cascades + depth program — same context-loss handling.
            for (int i = 0; i < ShadowCascadeCount; i++)
            {
                shadowFbos[i] = 0;
                shadowDepthTex[i] = 0;
            }
            shadowMapsAllocated = false;
            shadowUnsupported = false;
            shadowDepthProgram = 0;
            shadowLightVpLoc = -1;
            shadowPassLogged = false;
            shadowMap0Loc = shadowMap1Loc = shadowMap2Loc = -1;
            cascadeVp0Loc = cascadeVp1Loc = cascadeVp2Loc = -1;
            cascadeSplitLoc = shadowStrengthLoc = -1;
            shadowsActiveThisFrame = false;
            // The planar-reflection target belonged to the dead context — drop the handles so it's rebuilt fresh.
            reflectionFbo = 0;
            reflectionColorTex = 0;
            reflectionDepthRb = 0;
            reflectionTexW = 0;
            reflectionTexH = 0;
            reflectionUnsupported = false;
            // The trail-decal mask texture belonged to the dead context; drop the handle and clear the cache keys
            // so EnsureTrailMask rebuilds + re-uploads it against the fresh context on the next frame. (The scratch
            // CPU buffers survive the context loss and are reused — only the GL texture is gone.)
            trailMaskTex = 0;
            trailMaskValid = false;
            waterMaskTex = 0;
            waterMaskValid = false;
            lastMaskTrails = null;
            lastMaskRoads = null;
            lastMaskExposed = null;
            lastMaskWaterways = null;
            lastMaskWaterfalls = null;
            lastMaskRoute = null;
            lastMaskRaster = null;
            haveMaskWindowKey = false;
            // GPU timer query names belonged to the dead context — drop them (do NOT delete, like the other stale
            // IDs above) so EnsureGpuTimers re-Gens against the fresh context.
            gpuQueries = null;
            gpuTimersProbed = false;
            gpuFrameSlot = -1;
            gpuFrameCount = 0;
        }

        BeginGpuFrame(gl);

        EnsureProgram(gl);

        dbgTileSwapFrame = !ReferenceEquals(lastTiles, tiles);
        if (dbgTileSwapFrame)
        {
            // Incremental: keep the reused base tiles' VBOs, swap only the look-at detail patch (see SyncTiles).
            // SyncTiles now only evicts gone tiles + QUEUES new ones; the upload itself is spread over frames below.
            var swSync = System.Diagnostics.Stopwatch.StartNew();
            SyncTiles(gl, tiles);
            lastTiles = tiles;
            dbgSwapSyncMs = swSync.Elapsed.TotalMilliseconds;
        }

        DrainTileUploads(gl); // upload a few queued tiles per frame so a reload never freezes one frame

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
        if (dbgTileSwapFrame)
        {
            var swOrtho = System.Diagnostics.Stopwatch.StartNew();
            StreamOrthoTextures(gl, mvp, tiles, camera.Position);
            dbgSwapOrthoMs = swOrtho.Elapsed.TotalMilliseconds;
        }
        else
        {
            StreamOrthoTextures(gl, mvp, tiles, camera.Position);
        }

        // Cascaded Shadow Maps depth pass (Krok 5): render terrain depth from the sun's POV into the cascade
        // shadow maps before the sky/terrain passes. Self-contained — restores the bound FBO + viewport.
        GpuBegin(gl, GpuPass.Shadow);
        RenderShadowMaps(gl, camera, atmosphere?.SunDirection ?? Vector3.Zero, (float)width / Math.Max(1, height), vpWidth, vpHeight);
        GpuEnd(gl);

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
        // dead-clear sky (an additive bump used to leave ~0.35 coverage even at the 0% slider). The wobble
        // FADES OUT as the slider nears 100% so a full slider is a steady, fully-overcast sky — the weather
        // dip used to leave only a sparse field (~40% of the puffs) even at 100%. Variety stays in the mid range.
        float weatherFadeT = Math.Clamp((baseCoverage - 0.70f) / 0.30f, 0f, 1f);
        float weatherFade = 1f - (weatherFadeT * weatherFadeT * (3f - (2f * weatherFadeT)));
        float effectiveCoverage = Math.Clamp(baseCoverage * (1f + (0.6f * weatherNoise * weatherFade)), 0f, 1f);
        // Wind in noise-units/sec: slowly rotating heading + gently pulsing speed, scaled by the
        // user's wind setting (calm → barely drifting, gale → racing). The same setting darkens the
        // clouds toward storm-grey: stormDarken multiplies every cloud colour below.
        float wind = atmosphere?.Wind ?? 0.3f;
        float storm = atmosphere?.Storm ?? 0f;
        float windScale = 0.35f + (3.0f * wind); // ~0.35× at calm, ~3.35× at full gale
        float windAngle = (MathF.Sin(weatherT * 0.008f) * 0.9f) + (MathF.Sin((weatherT * 0.017f) + 2f) * 0.5f);
        float windSpeed = (0.012f + (0.010f * MathF.Sin(weatherT * 0.005f))) * windScale;
        var windVec = new Vector2(MathF.Cos(windAngle) * windSpeed, MathF.Sin(windAngle) * windSpeed);
        // Cloud darkening is driven SOLELY by the Storm slider now (wind only drifts the clouds): no storm =
        // full-brightness white clouds, a full storm drags them to ~20% (charcoal thundercloud).
        float stormDarken = 1f - (0.80f * storm);

        // Lightning. The Storm slider sets BOTH how often bolts strike and how bright the flash is. Time is
        // diced into windows whose length shrinks with storm (frequent strikes in a heavy storm); each window
        // is hashed to decide whether a bolt fires and when, then the flash is a sharp attack + exponential
        // decay with a fast re-strike flicker. Folded into the cloud colours + terrain ambient below (no extra
        // shader uniforms): the dark thundercloud briefly lights up blue-white and the ground flashes with it.
        float lightningFlash = 0f;
        if (animateAtmosphere && storm > 0.001f)
        {
            float windowLen = 7.0f - (5.5f * storm);                 // ~7 s between strikes (light) → ~1.5 s (heavy)
            float win = MathF.Floor(weatherT / windowLen);
            float strikeProb = 0.45f + (0.55f * storm);
            if (Hash01((win * 1.37f) + 0.5f) < strikeProb)
            {
                float jitter = Hash01((win * 2.11f) + 4.3f);         // where in the window the bolt strikes
                float localT = weatherT - ((win + (jitter * 0.7f)) * windowLen);
                if (localT >= 0f && localT < 1.2f)
                {
                    float envelope = MathF.Exp(-localT * 7.5f);          // sharp attack, ~250 ms tail
                    float flicker = 0.6f + (0.4f * MathF.Sin(localT * 55f)); // bolt re-strike flicker
                    lightningFlash = Math.Clamp(envelope * flicker, 0f, 1f) * (0.55f + (0.45f * storm));
                }
            }
        }
        var lightningTint = new Vector3(0.80f, 0.85f, 1.0f);

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

        // ── Cloud REGIME: "above" (cumulus float over the peaks) ↔ "inversion" (a low sea of clouds wraps the
        // ridges, peaks poking through). A slow, minutes-scale wander picks the lean, biased toward inversion
        // when the sun is low — the physical dawn/dusk sea of clouds — so the time-of-day slider is a lever and
        // the regime also drifts on its own. seaGate fades the inversion sheet in/out; cumulus do the inverse.
        float invNoise = (MathF.Sin(weatherT * 0.020f) * 0.6f) + (MathF.Sin((weatherT * 0.034f) + 1.3f) * 0.4f); // ~[-1,1]
        float lowSun = Math.Clamp(1f - (sunHeight * 2.2f), 0f, 1f); // 1 near/below the horizon, 0 high noon
        float invRaw = (0.55f * (0.5f + (0.5f * invNoise))) + (0.45f * lowSun);
        float invT = Math.Clamp((invRaw - 0.30f) / 0.40f, 0f, 1f);
        float inversion = invT * invT * (3f - (2f * invT)); // smoothstep — lingers at both regimes
        // The cloud SLIDER (not the weather noise) now drives the deck height, per the user's ask: above ~80%
        // it drops onto the peaks as a low ~2000 m overcast; below that it parks high above them. lowDeck ramps
        // in over [0.78, 1.0]; liftClear raises the deck as the sky clears. Weather (inversion/altNoise) still
        // adds the "czasem" wander on top so the same slider value isn't a fixed height.
        float cloudSlider = baseCoverage;
        float lowDeck = Math.Clamp((cloudSlider - 0.78f) / 0.22f, 0f, 1f);
        lowDeck = lowDeck * lowDeck * (3f - (2f * lowDeck)); // smoothstep — 0 below ~80%, 1 at 100%
        float liftClear = 1f - cloudSlider;
        float seaGate = Math.Max(inversion, lowDeck); // a high slider forces the low sheet on, no inversion needed

        // Inversion pulls the sheet DOWN into the valleys (peaks poke through); a high slider drops it the same
        // way (lowDeck ~2000 m onto the ridges), while a clear sky lifts it above the peaks (liftClear).
        float altFraction = Math.Clamp(
            0.62f + (0.45f * liftClear) - (0.40f * inversion) - (0.40f * lowDeck) - (0.30f * storm) + (0.06f * altNoise),
            -0.25f, 1.20f); // floor BELOW the frame centre so a full overcast / storm deck can sink into the valleys (~1500 m and lower)
        float cloudAltitude = float.IsNegativeInfinity(cloudMaxZ)
            ? 0f
            : geomFrame.Center.Z + ((cloudMaxZ - geomFrame.Center.Z) * altFraction);
        float cloudHalfExtent = MathF.Max(geomFrame.HorizontalExtent * 4f, 20_000f);
        float cloudNoiseScale = 1f / MathF.Max(geomFrame.HorizontalExtent * 0.5f, 4_000f);
        float seaCoverage = effectiveCoverage * seaGate; // the sea-of-clouds sheet only forms during inversion
        bool cloudsActive = animateAtmosphere && atmosphere is not null && effectiveCoverage > 0.001f && !float.IsNegativeInfinity(cloudMaxZ);
        // Cumulus sit over the ridges (bases ~at peak level, tops rising into the sky) — a VISIBLE level for a
        // terrain-facing camera, NOT tied to the inversion (which would lift them off the top of the screen).
        // The regime variety comes from their opacity (they thin as inversion deepens) + the sea-of-clouds sheet.
        // Above ~80% the slider (lowDeck) drops the cumulus down onto the ridges with the sea sheet, so the
        // whole low sky fills in — not a thin band of puffs parked high above an otherwise clear view.
        float cumulusBase = float.IsNegativeInfinity(cloudMaxZ)
            ? 0f
            : geomFrame.Center.Z + ((cloudMaxZ - geomFrame.Center.Z) * (0.62f - (0.40f * lowDeck) - (0.25f * storm))) + (350f * (1f - (0.7f * lowDeck)));
        // Keep the cumulus opaque even as an inversion deepens at a high slider (lowDeck cancels the thinning),
        // so a 100% storm sky stays packed rather than fading to the bare sea sheet.
        float cumulusOpacity = 1.0f * (1f - (0.35f * inversion * (1f - lowDeck)));
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
            gl.Uniform1(skySunGlowIntensityLocation, atmosphere.SunGlowIntensity);
            gl.Uniform1(skySunGlowWidthLocation, atmosphere.SunGlowWidth);
            gl.BindVertexArray(skyVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            gl.BindVertexArray(0);

            // Stars on top of the gradient (depth-write/-test still off): catalog point sprites pinned to the
            // sky, gated to night so they fade in only after sunset. The forward view-projection + w=0 keeps
            // them fixed to the celestial sphere as the camera orbits, and the depth-tested terrain pass that
            // follows paints over any that sit behind a ridge.
            float starNightFactor = Math.Clamp(-atmosphere.SunDirection.Z * 3f, 0f, 1f);
            if (localDate is { } starDate && tiles.Count > 0 && starNightFactor > 0.001f)
            {
                EnsureStarBuffer(gl, starDate, atmosphere.TimeOfDayHours, tiles[0].ProjectionAnchor);
                if (starCount > 0)
                {
                    Span<float> starVp = stackalloc float[16]
                    {
                        mvp.M11, mvp.M12, mvp.M13, mvp.M14,
                        mvp.M21, mvp.M22, mvp.M23, mvp.M24,
                        mvp.M31, mvp.M32, mvp.M33, mvp.M34,
                        mvp.M41, mvp.M42, mvp.M43, mvp.M44,
                    };
                    gl.Enable(EnableCap.Blend);
                    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); // additive: stars add light to the sky
                    gl.UseProgram(starProgram);
                    gl.UniformMatrix4(starViewProjLocation, 1, false, starVp);
                    gl.Uniform1(starNightFactorLocation, starNightFactor);
                    gl.Uniform1(starStarsOnLocation, 1f);
                    gl.BindVertexArray(starVao);
                    gl.DrawArrays(PrimitiveType.Points, 0, (uint)starCount);
                    gl.BindVertexArray(0);
                    gl.Disable(EnableCap.Blend);
                }
            }

            // Moon: one phased disc at the lunar direction, drawn after the stars (sits on top) and gated to
            // night the same way. The Sun direction orients the lit limb; the disc is culled below the horizon.
            if (localDate is { } moonDate && tiles.Count > 0 && starNightFactor > 0.001f)
            {
                GeoPoint moonAnchor = tiles[0].ProjectionAnchor;
                MoonSky moon = NightSky.MoonForLocalDate(
                    moonDate.Year, moonDate.Month, moonDate.Day, atmosphere.TimeOfDayHours,
                    moonAnchor.Latitude, moonAnchor.Longitude);
                if (moon.MoonDirection.Z > 0f)
                {
                    // Bright-limb screen direction: project the Moon dir and a point nudged toward the Sun.
                    Vector2 moonNdc = ProjectDirectionNdc(moon.MoonDirection, mvp);
                    Vector3 towardSun = Vector3.Normalize(moon.SunDirection - moon.MoonDirection);
                    Vector2 sunNdc = ProjectDirectionNdc(Vector3.Normalize(moon.MoonDirection + (towardSun * 0.05f)), mvp);
                    Vector2 termDir = sunNdc - moonNdc;
                    termDir = termDir.LengthSquared() > 1e-9f ? Vector2.Normalize(termDir) : new Vector2(1f, 0f);

                    Span<float> moonVp = stackalloc float[16]
                    {
                        mvp.M11, mvp.M12, mvp.M13, mvp.M14,
                        mvp.M21, mvp.M22, mvp.M23, mvp.M24,
                        mvp.M31, mvp.M32, mvp.M33, mvp.M34,
                        mvp.M41, mvp.M42, mvp.M43, mvp.M44,
                    };
                    gl.Enable(EnableCap.Blend);
                    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); // additive: the lit disc adds light
                    gl.UseProgram(moonProgram);
                    gl.UniformMatrix4(moonViewProjLocation, 1, false, moonVp);
                    gl.Uniform3(moonDirLocation, moon.MoonDirection.X, moon.MoonDirection.Y, moon.MoonDirection.Z);
                    gl.Uniform1(moonSizeLocation, 44f);
                    gl.Uniform2(moonTermDirLocation, termDir.X, termDir.Y);
                    gl.Uniform1(moonIlluminatedLocation, moon.IlluminatedFraction);
                    gl.Uniform1(moonNightFactorLocation, starNightFactor);
                    gl.BindVertexArray(skyVao); // any bound VAO; the vertex shader uses uMoonDir, not attributes
                    gl.DrawArrays(PrimitiveType.Points, 0, 1);
                    gl.BindVertexArray(0);
                    gl.Disable(EnableCap.Blend);
                }
            }
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
        // Scene-origin offsets (wider-coverage P0). Today every mesh shares the scene origin, so BOTH are zero
        // for the whole terrain program (terrain tiles, lake water, reflection pre-pass) — a strict no-op until
        // the scene re-anchors. uModelOffset = render frame (gl_Position/vWorldPos); uStableOffset = stable frame
        // for procedural sampling (vStableWorldPos). Set once here; not changed by any pass.
        gl.Uniform3(modelOffsetLocation, 0f, 0f, 0f);
        gl.Uniform3(stableOffsetLocation, 0f, 0f, 0f);
        gl.Uniform1(debugUvLocation, 0f); // UV/clamp viz off
        // Ortho coverage AABB + soft edge blend. Convert the coverage geo-bounds to world XY via the tiles'
        // anchor; beyond it the ortho UV clamps into stretched edge texels (strata bands) → the shader fades to
        // hypsometric instead. No coverage bounds (or no tiles) → blend 0 = no cull (pure ortho).
        float orthoBlend = 0f;
        if (orthoCoverageGeo is { } covGeo && tiles.Count > 0 && orthoCoverageBlendMeters > 0f)
        {
            Vector3 sw = tiles[0].GeoToWorld(covGeo.SouthWest, 0f);
            Vector3 ne = tiles[0].GeoToWorld(covGeo.NorthEast, 0f);
            orthoCoverageMin = new System.Numerics.Vector2(Math.Min(sw.X, ne.X), Math.Min(sw.Y, ne.Y));
            orthoCoverageMax = new System.Numerics.Vector2(Math.Max(sw.X, ne.X), Math.Max(sw.Y, ne.Y));
            orthoBlend = orthoCoverageBlendMeters;
        }
        gl.Uniform2(orthoMinXyLocation, orthoCoverageMin.X, orthoCoverageMin.Y);
        gl.Uniform2(orthoMaxXyLocation, orthoCoverageMax.X, orthoCoverageMax.Y);
        gl.Uniform1(orthoBlendLocation, orthoBlend);

        // Per-pixel lighting: the Atmosphere instance, when provided, overrides the per-tile baked
        // light direction + ambient so the time-of-day slider drives shading live. Without an
        // atmosphere the renderer falls back to the mesh-bake values (legacy behaviour).
        // Sky-ambient is dialled to half strength: the full atmosphere ambient washed out local slope
        // contrast ("ambient won over direct light"), flattening the relief. Halving it lets the direct sun
        // model the terrain forms again — verified on device via a pinned A/B — without crushing shadows.
        const float AmbientStrengthScale = 0.5f;

        TerrainMesh3D lightFrame = tiles[0];
        Vector3 light = atmosphere?.SunDirection ?? lightFrame.LightDirection;
        float ambient = (atmosphere?.AmbientFactor ?? lightFrame.AmbientFactor) * AmbientStrengthScale;
        // A lightning strike briefly lights the whole landscape: lift the ambient floor with the flash.
        ambient = Math.Min(ambient + (lightningFlash * 0.6f), 1.4f);
        gl.Uniform3(lightDirLocation, light.X, light.Y, light.Z);
        // Snow-line sun: pinned (film) so the cover holds while the lighting sun sweeps; else follows the sun.
        Vector3 snowSun = SnowSunOverride ?? light;
        gl.Uniform3(snowSunLocation, snowSun.X, snowSun.Y, snowSun.Z);
        gl.Uniform1(ambientLocation, ambient);

        // Coloured-light uniforms. With an atmosphere: warm direct-sun colour (boosted a touch so
        // sunlit slopes really glow at golden hour) + a brightened sky-tint ambient so shadows go
        // cool rather than muddy. Without one: both white, so the shader reproduces the legacy
        // grey scalar shade exactly.
        Vector3 sunCol = Vector3.One;
        Vector3 skyAmbient = Vector3.One;
        if (atmosphere is not null)
        {
            // Direct sun: lift toward white only a little (0.15) so the sun keeps more of its warmth —
            // golden-hour slopes (and the snow alpenglow that reuses uSunColor) read genuinely warm — while
            // the small lift + ×1.15 still keep a deep-orange sunset luminous enough to light the slopes.
            sunCol = Vector3.Lerp(atmosphere.SunColor, Vector3.One, 0.15f) * 1.15f;
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
        gl.Uniform1(terrainCloudShadowLocation, (cloudsActive ? CloudShadowStrength : 0f) * seaGate);

        // Snow cover: the snowline (world-Z) + soft band scale with the mesh Z range (so they track Pion),
        // and the slope-angle cosines hold snow on gentle ground while baring rock on steep faces. All four
        // come from the pure, unit-tested SnowModel — the single source of truth the shader mirrors.
        float snowAmount = atmosphere?.SnowAmount ?? 0f;
        float snowMaxZ = float.IsNegativeInfinity(cloudMaxZ) ? 0f : cloudMaxZ;
        float snowMinZ = float.IsPositiveInfinity(terrainMinZ) ? 0f : terrainMinZ;
        SnowShadingParameters snow = SnowModel.Compute(snowAmount, snowMinZ, snowMaxZ);
        gl.Uniform1(terrainSnowStrengthLocation, snowAmount);
        gl.Uniform1(terrainSnowLineZLocation, snow.LineZ);

        // Contour lines (warstwice) — shader overlay; spacing in world-Z (interval m × the mesh's Pion) so it
        // tracks exaggeration. Strength 0 when the layer is toggled off.
        float contourExag = lightFrame.VerticalExaggeration > 0f ? lightFrame.VerticalExaggeration : 1f;
        gl.Uniform1(terrainContourSpacingZLocation, (float)ContourIntervalMeters * contourExag);
        gl.Uniform3(terrainContourColorLocation, ContourR / 255f, ContourG / 255f, ContourB / 255f);
        gl.Uniform1(terrainContourMajorSpacingZLocation, (float)ContourMajorIntervalMeters * contourExag);
        gl.Uniform3(terrainContourMajorColorLocation, ContourMajorR / 255f, ContourMajorG / 255f, ContourMajorB / 255f);
        gl.Uniform1(terrainContourStrengthLocation, ShowContours ? ContourStrengthOn : 0f);
        gl.Uniform1(terrainContourWidthPxLocation, ContourWidthPx);

        // Trail decal off by default (also keeps the reflection pre-pass below from painting it); the real
        // strength + bound mask texture are set after the shadow block, just before the main terrain draw.
        gl.Uniform1(trailMaskSamplerLocation, 5);
        gl.Uniform1(trailMaskStrengthLocation, 0f);
        gl.Uniform1(waterMaskSamplerLocation, 6);
        gl.Uniform1(waterMaskStrengthLocation, 0f);

        gl.Uniform1(terrainSnowBandZLocation, snow.BandZ);
        gl.Uniform1(terrainSnowSlopeCosBareLocation, snow.SlopeCosBare);
        gl.Uniform1(terrainSnowSlopeCosFullLocation, snow.SlopeCosFull);

        // Midday brightness: at high (noon) sun the light is intense, so lift snow further toward bright
        // white instead of the flat grey it showed before. Derived from the sun elevation by the pure,
        // unit-tested NoonLightModel; 0 at low sun so golden-hour snow keeps its warm/cool tint. (float —
        // SnowWhiteLift returns float, so Uniform1 hits the GLSL float uniform, not the int overload.)
        float noonSnowLift = atmosphere is null ? 0f : NoonLightModel.SnowWhiteLift(atmosphere.SunDirection.Z);
        gl.Uniform1(terrainNoonSnowLiftLocation, noonSnowLift);

        // Slope-steepness ("avalanche") map mode: a flag + the band palette (from the unit-tested
        // SlopePalette). The shader recolours each fragment by its slope angle when the flag is on.
        gl.Uniform1(slopeModeLocation, SlopeMapEnabled ? 1f : 0f);
        gl.Uniform1(rockStrengthLocation, RockStrength);
        gl.Uniform3(slopePaletteLocation, (uint)SlopeClassification.BandCount, SlopePaletteFloats);

        // Elevation-zone biomes ("Biomy"): the boundary thresholds are real elevations in metres, so convert
        // the vertical ones to world-Z (× the mesh's vertical exaggeration / Pion) to match vWorldPos.z, then
        // upload the palette. Slope threshold stays an angle. Mirrors the unit-tested BiomeClassifier/BiomePalette.
        BiomeThresholds biome = BiomeThresholds.Default;
        float biomeExaggeration = lightFrame.VerticalExaggeration;
        gl.Uniform1(biomeModeLocation, BiomeMaterialEnabled ? 1f : 0f);
        gl.Uniform1(biomeScreeSlopeLocation, (float)biome.ScreeSlopeDegrees);
        gl.Uniform1(biomeMeadowMaxZLocation, (float)biome.MeadowMaxElevationM * biomeExaggeration);
        gl.Uniform1(biomeSnowZLocation, (float)biome.SnowElevationM * biomeExaggeration);
        gl.Uniform1(biomeIceZLocation, (float)biome.IceElevationM * biomeExaggeration);
        gl.Uniform1(biomeAspectShiftZLocation, (float)biome.AspectElevationShiftM * biomeExaggeration);
        gl.Uniform3(biomePaletteLocation, (uint)BiomePalette.All.Count, BiomePaletteFloats);

        // Aerial perspective: when the atmosphere is bound, distant fragments blend toward
        // uFogColor with an exponential ramp. uFogDensity = 0 disables the blend (legacy path).
        Vector3 fogColor = atmosphere?.FogColor ?? Vector3.Zero;
        float fogDensity = atmosphere?.FogDensity ?? 0f;
        Vector3 cameraWorldPos = camera.Position;
        gl.Uniform3(terrainFogColorLocation, fogColor.X, fogColor.Y, fogColor.Z);
        gl.Uniform1(terrainFogDensityLocation, fogDensity);
        gl.Uniform3(terrainCameraPosLocation, cameraWorldPos.X, cameraWorldPos.Y, cameraWorldPos.Z);

        // ── Planar water reflection pre-pass ───────────────────────────────────────────────────────
        // Render the terrain MIRRORED about the lake plane into a half-res texture, clipping everything below
        // the waterline, then restore the scene framebuffer. The lake mesh samples this texture (screen-space,
        // ripple-distorted) so the real peaks reflect in the water. One representative plane height is used
        // (Morskie Oko), so other lakes would reflect approximately. Behind ReflectionEnabled.
        gl.Uniform2(viewportPxLocation, (float)vpWidth, (float)vpHeight);
        gl.Uniform1(reflectionPassLocation, 0f);
        gl.Uniform1(reflectionEnabledLocation, 0f);
        bool reflectionDrawn = false;
        GpuBegin(gl, GpuPass.Reflection);
        if (ReflectionEnabled && tiles.Count > 0 && EnsureReflectionTarget(gl, vpWidth, vpHeight))
        {
            float reflExaggeration = tiles[0].VerticalExaggeration;
            float waterZ = ReflectionLakeElevationM * reflExaggeration;

            // Reflection matrix: mirror world-Z about the lake plane (z → 2*waterZ − z), then the normal MVP.
            Matrix4x4 reflectMatrix = Matrix4x4.Identity;
            reflectMatrix.M33 = -1f;
            reflectMatrix.M43 = 2f * waterZ;
            Matrix4x4 reflMvp = reflectMatrix * mvp;

            gl.BindFramebuffer(FramebufferTarget.Framebuffer, reflectionFbo);
            gl.Viewport(0, 0, (uint)reflectionTexW, (uint)reflectionTexH);
            gl.ClearColor(SkyR, SkyG, SkyB, 1f);
            gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
            gl.Uniform1(reflectionPassLocation, 1f);
            gl.Uniform1(waterClipZLocation, waterZ);
            m[0] = reflMvp.M11; m[1] = reflMvp.M12; m[2] = reflMvp.M13; m[3] = reflMvp.M14;
            m[4] = reflMvp.M21; m[5] = reflMvp.M22; m[6] = reflMvp.M23; m[7] = reflMvp.M24;
            m[8] = reflMvp.M31; m[9] = reflMvp.M32; m[10] = reflMvp.M33; m[11] = reflMvp.M34;
            m[12] = reflMvp.M41; m[13] = reflMvp.M42; m[14] = reflMvp.M43; m[15] = reflMvp.M44;
            gl.UniformMatrix4(mvpLocation, 1, false, m);
            // Reflect with the SAME ortho/biome shading as the main view, so the reflected peaks match their
            // real colours.
            bool reflAnyOrtho = orthoTiles.Count > 0 && OrthoEnabled;
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.Uniform1(orthoSamplerLocation, 0);
            uint reflBound = 0;
            foreach (KeyValuePair<TerrainMesh3D, TileBuffers> entry in tileBuffers)
            {
                OrthoTile? ot = null;
                if (reflAnyOrtho)
                {
                    int idx = entry.Key.OrthoTileIndex;
                    if ((uint)idx < (uint)orthoTiles.Count && orthoTiles[idx].Texture != 0)
                    {
                        ot = orthoTiles[idx];
                    }
                }
                if (ot is not null)
                {
                    if (ot.Texture != reflBound)
                    {
                        gl.BindTexture(TextureTarget.Texture2D, ot.Texture);
                        reflBound = ot.Texture;
                    }
                    gl.Uniform2(orthoTexelLocation, ot.Width > 0 ? 1f / ot.Width : 0f, ot.Height > 0 ? 1f / ot.Height : 0f);
                    gl.Uniform1(sharpenLocation, OrthoSharpenStrength);
                    gl.Uniform1(useOrthoLocation, 1);
                    gl.Uniform1(orthoGlobalFadeLocation, OrthoGlobalFade);
                }
                else
                {
                    gl.Uniform1(useOrthoLocation, 0);
                }
                gl.BindVertexArray(entry.Value.Vao);
                gl.DrawElements(PrimitiveType.Triangles, (uint)entry.Value.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }

            // Restore the scene framebuffer + viewport + the main MVP, and reset the reflection-pass flag.
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, useMsaa ? msaaFbo : presentFbo);
            gl.Viewport(0, 0, (uint)vpWidth, (uint)vpHeight);
            gl.Uniform1(reflectionPassLocation, 0f);
            m[0] = mvp.M11; m[1] = mvp.M12; m[2] = mvp.M13; m[3] = mvp.M14;
            m[4] = mvp.M21; m[5] = mvp.M22; m[6] = mvp.M23; m[7] = mvp.M24;
            m[8] = mvp.M31; m[9] = mvp.M32; m[10] = mvp.M33; m[11] = mvp.M34;
            m[12] = mvp.M41; m[13] = mvp.M42; m[14] = mvp.M43; m[15] = mvp.M44;
            gl.UniformMatrix4(mvpLocation, 1, false, m);
            reflectionDrawn = true;
        }
        GpuEnd(gl); // Reflection

        if (reflectionDrawn)
        {
            gl.ActiveTexture(TextureUnit.Texture1);
            gl.BindTexture(TextureTarget.Texture2D, reflectionColorTex);
            gl.Uniform1(reflectionTexLocation, 1);
            gl.Uniform1(reflectionEnabledLocation, 1f);
        }

        // Camera-relative terrain frame (P0 step 6). The reflection pre-pass above ran in the ABSOLUTE frame
        // (uModelOffset=0, absolute reflMvp) — leave it untouched. From here on the main terrain draw + lake water
        // use the render frame: mvpRender = Translate(R)·mvp and uModelOffset = -R cancel exactly (image identical),
        // uCameraPos shifts by -R so the view-dependent terms stay correct, and uStableOffset stays 0 so procedural
        // sampling keeps the absolute world. Line/forest programs keep their own absolute MVP and co-render.
        if (CameraRelativeTerrainOrigin)
        {
            Vector3 r = camera.Target;
            Matrix4x4 mvpRender = Matrix4x4.CreateTranslation(r) * mvp;
            m[0] = mvpRender.M11; m[1] = mvpRender.M12; m[2] = mvpRender.M13; m[3] = mvpRender.M14;
            m[4] = mvpRender.M21; m[5] = mvpRender.M22; m[6] = mvpRender.M23; m[7] = mvpRender.M24;
            m[8] = mvpRender.M31; m[9] = mvpRender.M32; m[10] = mvpRender.M33; m[11] = mvpRender.M34;
            m[12] = mvpRender.M41; m[13] = mvpRender.M42; m[14] = mvpRender.M43; m[15] = mvpRender.M44;
            gl.UniformMatrix4(mvpLocation, 1, false, m);
            gl.Uniform3(modelOffsetLocation, -r.X, -r.Y, -r.Z);
            gl.Uniform3(terrainCameraPosLocation, cameraWorldPos.X - r.X, cameraWorldPos.Y - r.Y, cameraWorldPos.Z - r.Z);
        }

        // CSM shadow sampling (part 4): bind the 3 cascade depth maps (units 2/3/4) + upload their light
        // matrices + split distances. Strength is 0 unless RenderShadowMaps actually rendered this frame
        // (sun above horizon, geometry ready), so night / fallback degrades to no shadows cleanly.
        if (shadowStrengthLoc >= 0)
        {
            // ALWAYS pin the shadow samplers to units 2/3/4. Left at their default (unit 0) the
            // sampler2DShadow uniforms collide with uOrtho (sampler2D, unit 0): two sampler types on one
            // texture image unit makes the program invalid to USE, and Adreno rejects the WHOLE terrain draw
            // (GL_INVALID_OPERATION) → terrain vanishes. Desktop GL tolerated it; the device did not. Must be
            // set even when shadows are off (csmShadow early-returns before sampling, so empty units are fine).
            gl.Uniform1(shadowMap0Loc, 2);
            gl.Uniform1(shadowMap1Loc, 3);
            gl.Uniform1(shadowMap2Loc, 4);
            if (shadowsActiveThisFrame)
            {
                gl.ActiveTexture(TextureUnit.Texture2);
                gl.BindTexture(TextureTarget.Texture2D, shadowDepthTex[0]);
                gl.ActiveTexture(TextureUnit.Texture3);
                gl.BindTexture(TextureTarget.Texture2D, shadowDepthTex[1]);
                gl.ActiveTexture(TextureUnit.Texture4);
                gl.BindTexture(TextureTarget.Texture2D, shadowDepthTex[2]);
                gl.ActiveTexture(TextureUnit.Texture0);
                UploadMatrix(gl, cascadeVp0Loc, cascadeLightVp[0]);
                UploadMatrix(gl, cascadeVp1Loc, cascadeLightVp[1]);
                UploadMatrix(gl, cascadeVp2Loc, cascadeLightVp[2]);
                gl.Uniform3(cascadeSplitLoc, cascadeSplitFar[0], cascadeSplitFar[1], cascadeSplitFar[2]);
                gl.Uniform1(shadowStrengthLoc, ShadowStrength);
            }
            else
            {
                gl.Uniform1(shadowStrengthLoc, 0f);
            }
        }

        // Trail/route decal (Option A): build/refresh the painted-distance mask and bind it on unit 5 so the
        // terrain shader paints trails INTO the surface (base AND streamed detail — one shader). Main pass only
        // (the reflection pre-pass kept strength 0). tiles[0] is the representative mesh frame for the projection.
        // Only build/bind the mask when the decal is enabled — the build is expensive (large allocations) and
        // would otherwise run every frame the streaming cache key churns. Off ⇒ uTrailStrength stays 0 (set above).
        // Dedup near-parallel duplicate trails ONCE here; the same deduped reference feeds the decal mask, the
        // trail line overlay and the route conflation, so only one of a duplicate pair is drawn and the route
        // lands on it. Cached by input ref → not recomputed per frame.
        IReadOnlyList<Trail>? dedupedTrails = EnsureDedupedTrails(trails);

        if (ShowTrailDecal && tiles.Count > 0)
        {
            // camera.Target (the look-at point), NOT camera.Position: an elevated/oblique scenic view can have the
            // camera itself far from what's actually on screen — centring on Position would leave the visible
            // ground outside the window while the camera floats miles away looking down at it.
            EnsureTrailMask(gl, dedupedTrails, roads, exposedRoutes, route, raster, tiles[0], detail, camera.Target);
            if (trailMaskValid && trailMaskTex != 0)
            {
                gl.ActiveTexture(TextureUnit.Texture5);
                gl.BindTexture(TextureTarget.Texture2D, trailMaskTex);
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.Uniform2(trailMaskMinXYLocation, trailMaskMinX, trailMaskMinY);
                gl.Uniform2(trailMaskSizeXYLocation, trailMaskSizeX, trailMaskSizeY);
                gl.Uniform1(trailMaskMaxDistLocation, TrailMaskMaxDistanceMeters);
                gl.Uniform1(trailMaskHalfWidthLocation, TrailDecalHalfWidthMeters);
                gl.Uniform1(trailMaskStrengthLocation, TrailDecalStrength);
            }

            if (waterMaskValid && waterMaskTex != 0)
            {
                gl.ActiveTexture(TextureUnit.Texture6);
                gl.BindTexture(TextureTarget.Texture2D, waterMaskTex);
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.Uniform1(waterMaskStrengthLocation, 1f);
            }
        }

        // Drape the ortho: bind each mesh tile's own cell texture (OrthoTileIndex) so a multi-cell ortho
        // stays sharp. Without textures the shader uses the hypsometric tint.
        bool anyOrtho = orthoTiles.Count > 0 && OrthoEnabled;
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.Uniform1(orthoSamplerLocation, 0);
        uint boundTexture = 0;
        GpuBegin(gl, GpuPass.Terrain);
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
                gl.Uniform1(orthoGlobalFadeLocation, OrthoGlobalFade);
            }
            else
            {
                gl.Uniform1(useOrthoLocation, 0);
            }

            gl.BindVertexArray(tile.Vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)tile.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        }
        GpuEnd(gl); // Terrain

        GpuBegin(gl, GpuPass.LakesForest);
        // Lake water: real OSM outlines (MountainLakeData) for every tarn within the loaded terrain, each at its
        // own elevation, drawn over the terrain. Blended, depth-test ON so the basin clips it where the bed rises
        // above the water plane. Depth-write is ON (not off): a lake's triangles are all coplanar at one plane Z,
        // so with DepthFunc=Less the FIRST triangle at a pixel writes that depth and any OVERLAPPING coplanar
        // triangle (same Z, not less) is rejected — each water pixel blends exactly ONCE, killing the bright
        // double-blend seams that survive ear-clipping. Each lake is shaded with its own centroid + radius.
        if (dbgTileSwapFrame)
        {
            var swLake = System.Diagnostics.Stopwatch.StartNew();
            BuildLakeWater(gl, tiles, raster);
            dbgSwapLakeMs = swLake.Elapsed.TotalMilliseconds;
            double swapTotal = dbgSwapSyncMs + dbgSwapOrthoMs + dbgSwapLakeMs;
            if (swapTotal > 30.0) // only log genuine hitch frames
            {
                Log.Information(
                    "[GL3D] tile-swap render hitch: sync={Sync:F0}ms ortho={Ortho:F0}ms lake={Lake:F0}ms total={Total:F0}ms ({Tiles} tiles)",
                    dbgSwapSyncMs, dbgSwapOrthoMs, dbgSwapLakeMs, swapTotal, tiles.Count);
            }
        }
        else
        {
            BuildLakeWater(gl, tiles, raster);
        }

        if (debugPolyVertexCount > 0 && lakeDraws.Count > 0)
        {
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.DepthMask(true);
            gl.Uniform1(debugPolyLocation, 1f);
            gl.BindVertexArray(debugPolyVao);
            foreach (LakeDraw lake in lakeDraws)
            {
                gl.Uniform2(lakeCenterLocation, lake.Center.X, lake.Center.Y);
                gl.Uniform1(lakeRadiusLocation, lake.Radius);
                gl.DrawArrays(PrimitiveType.Triangles, lake.VertexOffset, (uint)lake.VertexCount);
            }
            gl.Uniform1(debugPolyLocation, 0f);
            gl.BindVertexArray(0);
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);
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
        GpuEnd(gl); // LakesForest

        // Trails + route as depth-tested screen-space ribbons (occluded by the terrain). Switch to the line
        // program; it shares the depth state and the same MVP, plus the viewport for the pixel expansion.
        // CRITICAL: the terrain draw above may have left m = the CAMERA-RELATIVE mvpRender (Translate(target)·mvp,
        // paired with the terrain's uModelOffset=-target so it cancels). The line program draws ABSOLUTE world
        // positions with NO compensating offset, so feeding it mvpRender translates every ribbon by camera.Target
        // — including Target.Z (~1–2 km), so trails "fly with the camera" above the terrain. Restore the ABSOLUTE
        // mvp into m before the line MVP upload.
        m[0] = mvp.M11; m[1] = mvp.M12; m[2] = mvp.M13; m[3] = mvp.M14;
        m[4] = mvp.M21; m[5] = mvp.M22; m[6] = mvp.M23; m[7] = mvp.M24;
        m[8] = mvp.M31; m[9] = mvp.M32; m[10] = mvp.M33; m[11] = mvp.M34;
        m[12] = mvp.M41; m[13] = mvp.M42; m[14] = mvp.M43; m[15] = mvp.M44;
        GpuBegin(gl, GpuPass.Lines);
        gl.UseProgram(lineProgram);
        gl.UniformMatrix4(lineMvpLocation, 1, false, m);
        gl.Uniform2(lineViewportLocation, (float)Math.Max(1, width), (float)Math.Max(1, height));
        // Same aerial-perspective fog as the terrain (absolute world frame, like the forest program) so distant
        // trails fade into the horizon haze instead of floating as bright lines over the hazed far terrain.
        gl.Uniform3(lineFogColorLocation, fogColor.X, fogColor.Y, fogColor.Z);
        gl.Uniform1(lineFogDensityLocation, fogDensity);
        gl.Uniform3(lineCameraPosLocation, cameraWorldPos.X, cameraWorldPos.Y, cameraWorldPos.Z);
        // Cull radius scales with zoom: close in, only nearby trails; zoomed out, reach farther — but never
        // the whole 27×42 km network, which is what floated + parallaxed at the horizon.
        gl.Uniform1(lineMaxDistLocation, (camera.Distance * 1.6f) + 4000f);
        TerrainMesh3D frame = tiles[0];
        // REVERTED: disabling depth-test entirely also killed REAL occlusion (a trail on the far side of an
        // actual intervening ridge became visible "through" the mountain — confirmed regression). The correct
        // fix is a small CLIP-SPACE depth bias in the line vertex shader (see uDepthBiasClip below / the +Z bias
        // in the trail/route vertex shader), which only wins the depth-test tie against the surface the line is
        // directly seated on, while still losing to genuinely different, more-in-front geometry. Depth test stays
        // enabled here.
        // X-RAY (ghost) pass FIRST: reversed depth test draws ONLY the fragments the terrain occludes, heavily
        // faded — a trail inside a gully (żleb Kulczyńskiego) or briefly behind a ridge reads as a faint
        // "behind rock" ghost instead of vanishing. Clearly dimmer than the solid pass, so this cannot recreate
        // the old full-brightness "szlaki przez skały" regression. Depth writes off (ghosts must not pollute
        // the depth buffer); blending on for the fade.
        gl.Uniform1(lineGhostFadeLocation, 0.30f);
        gl.DepthFunc(DepthFunction.Greater);
        gl.DepthMask(false);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        // Re-assert DepthMask(false) after each call: DrawTrailLines/DrawRouteLine restore it to true
        // internally, and a ghost fragment writing its (farther) depth would corrupt later passes.
        DrawTrailLines(gl, dedupedTrails, raster, frame, detail);
        gl.DepthMask(false);
        DrawExposedRoutes(gl, exposedRoutes, raster, frame, detail);
        gl.DepthMask(false);
        DrawRouteLine(gl, route, dedupedTrails, raster, frame, detail);
        gl.DepthMask(true);
        gl.DepthFunc(DepthFunction.Less);

        // Solid pass: normal depth test (real ridges still occlude into the ghost above, near z-fights handled
        // by the clip-space bias in the vertex shader).
        gl.Uniform1(lineGhostFadeLocation, 1f);
        DrawRoadLines(gl, roads, raster, frame, detail);
        // Use the DEDUPED trails for both the overlay and the route conflation: one of a duplicate pair is drawn,
        // and the route is re-laid onto that single remaining trail (so it can't snap to the dropped copy beside it).
        DrawTrailLines(gl, dedupedTrails, raster, frame, detail);
        DrawExposedRoutes(gl, exposedRoutes, raster, frame, detail);
        DrawRouteLine(gl, route, dedupedTrails, raster, frame, detail);
        DrawCableCar(gl, frame, raster, detail);

        gl.BindVertexArray(0);
        GpuEnd(gl); // Lines

        GpuBegin(gl, GpuPass.Clouds);
        // "Sea of clouds" layer: a horizontal translucent sheet at the shared cloud altitude, drawn
        // after the terrain so the depth test lets peaks poke through and veils the valleys. Geometry
        // + field params come from the precomputed cloud locals so the layer matches the shadows the
        // terrain pass already cast. Alpha-blended, depth-write off (must not occlude later overlays).
        if (cloudsActive && seaCoverage > 0.001f)
        {
            // Colour: warm-tinted near sunset (toward the horizon hue), bright white when the sun is
            // high, dimmed at night. Built from the atmosphere so it matches the sky.
            float dayness = Math.Clamp(atmosphere!.SunDirection.Z + 0.1f, 0f, 1f);
            Vector3 white = new(0.97f, 0.97f, 0.99f);
            Vector3 tint = Vector3.Lerp(atmosphere.SkyHorizonColor, white, dayness);
            Vector3 cloudCol = (tint * (0.55f + (0.45f * dayness)) * stormDarken) + (lightningTint * lightningFlash);

            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            gl.DepthMask(false); // translucent: test against terrain but don't write depth
            gl.UseProgram(cloudProgram);
            gl.UniformMatrix4(cloudMvpLocation, 1, false, m);
            gl.Uniform2(cloudCenterLocation, geomFrame.Center.X, geomFrame.Center.Y);
            gl.Uniform1(cloudHalfExtentLocation, cloudHalfExtent);
            gl.Uniform1(cloudAltitudeLocation, cloudAltitude);
            gl.Uniform1(cloudTimeLocation, weatherT);
            gl.Uniform1(cloudCoverageLocation, seaCoverage);
            gl.Uniform3(cloudColorLocation, cloudCol.X, cloudCol.Y, cloudCol.Z);
            gl.Uniform1(cloudNoiseScaleLocation, cloudNoiseScale);
            gl.Uniform2(cloudWindLocation, windVec.X, windVec.Y);
            // Surface undulation: ~2.8 km wavelength; amplitude grows from a gentle calm-day swell to a
            // gale that heaves crests hundreds of metres up the slopes.
            gl.Uniform1(cloudDispScaleLocation, 1f / 2800f);
            gl.Uniform1(cloudDispAmpLocation, 70f + (340f * wind));
            gl.BindVertexArray(cloudVao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)cloudIndexCount, DrawElementsType.UnsignedInt, (void*)0);
            gl.BindVertexArray(0);
            gl.DepthMask(true);
            gl.Disable(EnableCap.Blend);
        }

        // Cumulus puffs (Tier 2): scattered camera-facing billboards above the deck — puffy white clouds in the
        // blue sky. Drawn after the terrain (depth-tested so peaks occlude them) with the ABSOLUTE mvp (m was
        // restored to absolute before the line pass). A shader failure disables clouds only.
        if (cloudsActive && !cumulusUnsupported)
        {
            try
            {
                EnsureCumulusProgram(gl);
            }
            catch (Exception ex)
            {
                cumulusUnsupported = true;
                Log.Warning(ex, "[GL3D] cumulus clouds disabled (shader/link failure)");
            }

            if (!cumulusUnsupported && cumulusOpacity > 0.02f)
            {
                // Cumulus drift downwind at a REAL world speed (metres), so the wind slider visibly moves them.
                Vector2 windDir = windVec.LengthSquared() > 1e-9f ? Vector2.Normalize(windVec) : new Vector2(1f, 0f);
                float windWorldSpeed = 3f + (28f * wind); // m/s: a gentle drift when calm → racing storm clouds at full wind
                // INTEGRATE velocity across frames (∫ v·dt) — NOT windDir·speed·t. The wind heading slowly rotates
                // and the slider changes speed; multiplying the CURRENT direction/speed by the WHOLE elapsed time
                // teleported the entire field by speed·t on any change ("the wind broke"). dt is clamped so a pause
                // (clouds toggled off, a stall) can't accumulate into a jump either. The vertex shader wraps the
                // field into a torus, so the accumulator can grow without the puffs sliding off the scene.
                float driftDt = lastCumulusDriftSeconds < 0f ? 0f : Math.Clamp(weatherT - lastCumulusDriftSeconds, 0f, 0.1f);
                lastCumulusDriftSeconds = weatherT;
                cumulusDriftAccum += windDir * (windWorldSpeed * driftDt);
                Vector2 cumDrift = cumulusDriftAccum;
                // The "Zachmurzenie" slider (effectiveCoverage) sets HOW MANY cumulus draw: 0 % = clear sky,
                // 100 % = the full field.
                int cumCount = (int)MathF.Round(cumulusInstanceCount * Math.Clamp(effectiveCoverage, 0f, 1f));
                DrawCumulus(gl, m, camera, atmosphere!, new Vector2(geomFrame.Center.X, geomFrame.Center.Y),
                    cumulusBase, cumDrift, cumulusOpacity, cumCount, fogColor, fogDensity, stormDarken, lightningFlash);
            }
        }

        GpuEnd(gl); // Clouds

        // Sauron's tower easter egg on Świnica — drawn into the scene BEFORE the post-process so the bloom pass
        // turns its eye into a small glowing sun. Depth-tested, so ridges in front occlude the lower tower.
        if (showSauronTower && atmosphere is not null && raster is not null && !sauronUnsupported)
        {
            try
            {
                EnsureSauronProgram(gl);
            }
            catch (Exception ex)
            {
                sauronUnsupported = true;
                Log.Warning(ex, "[GL3D] Sauron tower disabled (shader/link failure)");
            }

            if (!sauronUnsupported)
            {
                float seat = (float)raster.SampleBilinear(SwinicaLocation.Longitude, SwinicaLocation.Latitude);
                Vector3 baseWorld = geomFrame.GeoToWorld(SwinicaLocation, seat);
                DrawSauron(gl, m, camera, atmosphere, baseWorld, geomFrame.VerticalExaggeration, weatherT, fogColor, fogDensity);
            }
        }

        // Eagles soaring over Orla Perć — depth-tested billboards, drawn into the scene before the post-process.
        if (showEagles && animateAtmosphere && raster is not null && !eagleUnsupported)
        {
            try
            {
                EnsureEagleProgram(gl);
            }
            catch (Exception ex)
            {
                eagleUnsupported = true;
                Log.Warning(ex, "[GL3D] eagles disabled (shader/link failure)");
            }

            if (!eagleUnsupported)
            {
                DrawEagles(gl, m, camera, geomFrame, raster, weatherT);
            }
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

        // Post-process stage (pass-through today; bloom / god rays build on it). Reads the resolved scene
        // and returns the texture the caller wraps for Skia — falls back to presentColorTex if unavailable.
        // God rays: project the sun to screen space; draw only when it is in front / on-frame. Strength
        // rides the same low-sun curve as the glow (peaks at golden hour, nil at noon/night).
        (bool sunVisible, Vector2 sunUv) = atmosphere is not null
            ? SunScreenProjection.Project(camera, atmosphere.SunDirection, vpWidth, vpHeight)
            : (false, Vector2.Zero);
        // With animated effects off, skip the bloom blur + god-ray passes (their cost is only worth it for the
        // glowing/golden look) — a flat composite keeps the still scene cheap.
        float godrayIntensity = (animateAtmosphere && sunVisible && atmosphere is not null) ? atmosphere.SunGlowIntensity * 1.3f : 0f;
        float bloomIntensity = animateAtmosphere ? (atmosphere?.BloomIntensity ?? 0f) : 0f;

        GpuBegin(gl, GpuPass.Post);
        uint finalTex = RunPostProcess(
            gl, presentColorTex, vpWidth, vpHeight,
            atmosphere?.BloomThreshold ?? 1f, bloomIntensity,
            sunUv.X, sunUv.Y, godrayIntensity);
        GpuEnd(gl);

        // Unbind everything before returning. The caller will re-establish whatever framebuffer Skia
        // expects (via GRContext.ResetContext) before sampling the texture we just produced.
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return finalTex;
    }

    // Probes once for the desktop GL_EXT_disjoint_timer_query extension and allocates the query ring. Fails safe
    // (timing off) on mobile GLES where the extension is absent — same detect-once/latch pattern as the cumulus/
    // sauron/msaa "unsupported" flags, so the build + render loop are unaffected on devices without it.
    private unsafe void EnsureGpuTimers(GL g)
    {
        if (gpuTimersProbed)
        {
            return;
        }

        gpuTimersProbed = true;
        try
        {
            gpuTimersSupported = g.IsExtensionPresent("GL_EXT_disjoint_timer_query");
            if (!gpuTimersSupported)
            {
                Log.Information("[GL3D] per-pass GPU timers off (no GL_EXT_disjoint_timer_query)");
                return;
            }

            gpuQueries = new uint[(int)GpuPass.Count * GpuFramesInFlight];
            fixed (uint* p = gpuQueries)
            {
                g.GenQueries((uint)gpuQueries.Length, p);
            }
        }
        catch (Exception ex)
        {
            gpuTimersSupported = false;
            gpuQueries = null;
            Log.Warning(ex, "[GL3D] per-pass GPU timers disabled (query setup failed)");
        }
    }

    // Top of each frame: read back the ring slot we are ABOUT to overwrite (it holds results from GpuFramesInFlight
    // frames ago, so they are ready without a stall), store the per-pass ms, then advance the slot. Throttled
    // ~every 3 s, emits the breakdown to the [GL3D] Serilog channel. The 32-bit nanosecond result wraps only at
    // ~4.3 s — far above any single pass — so the core (non-EXT) GetQueryObject read is exact here.
    private unsafe void BeginGpuFrame(GL g)
    {
        EnsureGpuTimers(g);
        if (!gpuTimersSupported || gpuQueries is null)
        {
            return;
        }

        gpuFrameSlot = (gpuFrameSlot + 1) % GpuFramesInFlight;

        // Only read once every query name has been ended at least once (after the first full ring lap) — reading
        // an as-yet-unused query is an error.
        if (gpuFrameCount >= GpuFramesInFlight)
        {
            int disjoint = 0;
            g.GetInteger(GlGpuDisjointExt, &disjoint);
            bool bogus = disjoint != 0; // GPU clock changed / preempted ⇒ this lap's counts are unreliable

            for (int p = 0; p < (int)GpuPass.Count; p++)
            {
                uint q = gpuQueries[(p * GpuFramesInFlight) + gpuFrameSlot];
                if (q == 0)
                {
                    continue;
                }

                uint avail = 0;
                g.GetQueryObject(q, QueryObjectParameterName.ResultAvailable, &avail);
                if (avail == 0)
                {
                    continue; // not ready ⇒ keep the previous value, never stall
                }

                uint ns = 0;
                g.GetQueryObject(q, QueryObjectParameterName.Result, &ns);
                if (!bogus)
                {
                    lastPassMs[p] = ns / 1_000_000.0;
                }
            }

            long now = Environment.TickCount64;
            if (now - lastPassTimesLogTick >= MemLogIntervalMs)
            {
                lastPassTimesLogTick = now;
                double sum = 0;
                for (int p = 0; p < (int)GpuPass.Count; p++)
                {
                    sum += lastPassMs[p];
                }

                Log.Information(
                    "[GL3D] [PassTimes] shadow={Shadow:F2} refl={Refl:F2} terrain={Terrain:F2} lakeforest={Lake:F2} lines={Lines:F2} clouds={Clouds:F2} post={Post:F2} sumGpu={Sum:F2}ms",
                    lastPassMs[(int)GpuPass.Shadow], lastPassMs[(int)GpuPass.Reflection], lastPassMs[(int)GpuPass.Terrain],
                    lastPassMs[(int)GpuPass.LakesForest], lastPassMs[(int)GpuPass.Lines], lastPassMs[(int)GpuPass.Clouds],
                    lastPassMs[(int)GpuPass.Post], sum);
            }
        }

        gpuFrameCount++;
    }

    // Begin a pass's GPU timer (into this frame's ring slot). Only ONE GL_TIME_ELAPSED query may be active at a
    // time, so GpuBegin/GpuEnd must bracket SEQUENTIAL, non-nesting passes — which every pass below is.
    private void GpuBegin(GL g, GpuPass pass)
    {
        if (gpuTimersSupported && gpuQueries is not null)
        {
            g.BeginQuery(GlTimeElapsedExt, gpuQueries[((int)pass * GpuFramesInFlight) + gpuFrameSlot]);
        }
    }

    private void GpuEnd(GL g)
    {
        if (gpuTimersSupported && gpuQueries is not null)
        {
            g.EndQuery(GlTimeElapsedExt);
        }
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
    /// Ensures the planar-reflection target (half-resolution colour texture + depth RB) exists at the given
    /// size. Returns false if reflections are unsupported (incomplete FBO) so the caller skips the pre-pass.
    /// </summary>
    private bool EnsureReflectionTarget(GL g, int width, int height)
    {
        if (reflectionUnsupported)
        {
            return false;
        }

        // Half-res: the reflection is ripple-distorted and only seen on the lake, so full res is wasted.
        int w = Math.Max(16, width / 2);
        int h = Math.Max(16, height / 2);
        if (reflectionFbo != 0 && reflectionTexW == w && reflectionTexH == h)
        {
            return true;
        }

        if (reflectionFbo != 0)
        {
            g.DeleteFramebuffer(reflectionFbo);
            g.DeleteTexture(reflectionColorTex);
            g.DeleteRenderbuffer(reflectionDepthRb);
            reflectionFbo = 0;
            reflectionColorTex = 0;
            reflectionDepthRb = 0;
        }

        reflectionColorTex = g.GenTexture();
        g.BindTexture(TextureTarget.Texture2D, reflectionColorTex);
        unsafe
        {
            g.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        }
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        g.BindTexture(TextureTarget.Texture2D, 0);

        reflectionDepthRb = g.GenRenderbuffer();
        g.BindRenderbuffer(RenderbufferTarget.Renderbuffer, reflectionDepthRb);
        g.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent16, (uint)w, (uint)h);
        g.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);

        reflectionFbo = g.GenFramebuffer();
        g.BindFramebuffer(FramebufferTarget.Framebuffer, reflectionFbo);
        g.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, reflectionColorTex, 0);
        g.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, reflectionDepthRb);
        GLEnum status = g.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        g.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            Log.Information("[GL3D] reflection framebuffer incomplete ({Status}) — planar reflection disabled", status);
            g.DeleteFramebuffer(reflectionFbo);
            g.DeleteTexture(reflectionColorTex);
            g.DeleteRenderbuffer(reflectionDepthRb);
            reflectionFbo = 0;
            reflectionColorTex = 0;
            reflectionDepthRb = 0;
            reflectionUnsupported = true;
            return false;
        }

        reflectionTexW = w;
        reflectionTexH = h;
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
    /// Ensures the post-process target (a single full-resolution colour texture + FBO) matches the given
    /// size. Mirrors <see cref="EnsurePresentTarget"/> but needs no depth — the post passes are fullscreen and
    /// depth-test-free. Returns false (and latches <see cref="postUnsupported"/> for the session) when the
    /// framebuffer is incomplete, so the caller transparently falls back to the present texture.
    /// </summary>
    private bool EnsurePostBuffers(GL g, int width, int height)
    {
        if (postUnsupported)
        {
            return false;
        }

        if (postFbo != 0 && postWidth == width && postHeight == height)
        {
            return true;
        }

        g.DeleteFramebuffer(postFbo);
        g.DeleteTexture(postColorTex);

        postColorTex = g.GenTexture();
        g.BindTexture(TextureTarget.Texture2D, postColorTex);
        g.TexImage2D(
            TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
            (uint)width, (uint)height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, null);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        g.BindTexture(TextureTarget.Texture2D, 0);

        postFbo = g.GenFramebuffer();
        g.BindFramebuffer(FramebufferTarget.Framebuffer, postFbo);
        g.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, postColorTex, 0);

        GLEnum status = g.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        g.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            Log.Information("[GL3D] post framebuffer incomplete ({Status}) — post-process disabled this session", status);
            g.DeleteFramebuffer(postFbo);
            g.DeleteTexture(postColorTex);
            postFbo = 0;
            postColorTex = 0;
            postUnsupported = true;
            return false;
        }

        postWidth = width;
        postHeight = height;
        return true;
    }

    /// <summary>Uploads a System.Numerics matrix to a uniform with the same row-major (transpose=false)
    /// convention as uMvp, so GLSL's M*v matches Vector4.Transform(v, M).</summary>
    private static void UploadMatrix(GL g, int location, Matrix4x4 m)
    {
        if (location < 0)
        {
            return;
        }
        Span<float> a = stackalloc float[16]
        {
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44,
        };
        g.UniformMatrix4(location, 1, false, a);
    }

    /// <summary>Creates a clamped, linear-filtered RGBA8 colour texture of the given size.</summary>
    private static uint MakeColorTexture(GL g, int w, int h)
    {
        uint tex = g.GenTexture();
        g.BindTexture(TextureTarget.Texture2D, tex);
        g.TexImage2D(
            TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
            (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        g.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    /// <summary>Creates a colour-only FBO wrapping <paramref name="colorTex"/> and returns its completeness status.</summary>
    private static uint MakeColorFbo(GL g, uint colorTex, out GLEnum status)
    {
        uint fbo = g.GenFramebuffer();
        g.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        g.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colorTex, 0);
        status = g.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        return fbo;
    }

    /// <summary>Links a fullscreen post-process program (the shared post vertex shader + the given fragment).</summary>
    private static uint BuildPostProgram(GL g, string fragmentSource, string name)
    {
        uint vs = CompileShader(g, ShaderType.VertexShader, PostVertexShaderSource);
        uint fs = CompileShader(g, ShaderType.FragmentShader, fragmentSource);
        uint prog = g.CreateProgram();
        g.AttachShader(prog, vs);
        g.AttachShader(prog, fs);
        g.LinkProgram(prog);
        g.GetProgram(prog, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = g.GetProgramInfoLog(prog);
            throw new InvalidOperationException(name + " shader link failed: " + log);
        }
        g.DetachShader(prog, vs);
        g.DetachShader(prog, fs);
        g.DeleteShader(vs);
        g.DeleteShader(fs);
        return prog;
    }

    /// <summary>
    /// Ensures the two half-resolution bloom ping-pong buffers match the present size. Half-res (via
    /// <see cref="PostProcessBufferSizing.Downsample"/>) keeps the blur cheap and soft. Latches
    /// <see cref="bloomUnsupported"/> on incompleteness so the caller falls back to the plain pass-through.
    /// </summary>
    private bool EnsureBloomBuffers(GL g, int fullWidth, int fullHeight)
    {
        if (bloomUnsupported)
        {
            return false;
        }
        (int bw, int bh) = PostProcessBufferSizing.Downsample(fullWidth, fullHeight, 2);
        if (bloomBrightFbo != 0 && bloomWidth == bw && bloomHeight == bh)
        {
            return true;
        }

        g.DeleteFramebuffer(bloomBrightFbo);
        g.DeleteTexture(bloomBrightTex);
        g.DeleteFramebuffer(bloomFboA);
        g.DeleteTexture(bloomTexA);
        g.DeleteFramebuffer(bloomFboB);
        g.DeleteTexture(bloomTexB);
        g.DeleteFramebuffer(godrayFbo);
        g.DeleteTexture(godrayTex);

        bloomBrightTex = MakeColorTexture(g, bw, bh);
        bloomBrightFbo = MakeColorFbo(g, bloomBrightTex, out GLEnum statusBright);
        bloomTexA = MakeColorTexture(g, bw, bh);
        bloomFboA = MakeColorFbo(g, bloomTexA, out GLEnum statusA);
        bloomTexB = MakeColorTexture(g, bw, bh);
        bloomFboB = MakeColorFbo(g, bloomTexB, out GLEnum statusB);
        godrayTex = MakeColorTexture(g, bw, bh);
        godrayFbo = MakeColorFbo(g, godrayTex, out GLEnum statusGod);
        g.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        if (statusBright != GLEnum.FramebufferComplete || statusA != GLEnum.FramebufferComplete
            || statusB != GLEnum.FramebufferComplete || statusGod != GLEnum.FramebufferComplete)
        {
            Log.Information("[GL3D] post-effect framebuffer incomplete (bright={Br}, A={A}, B={B}, god={G}) — bloom/god-rays off this session", statusBright, statusA, statusB, statusGod);
            g.DeleteFramebuffer(bloomBrightFbo);
            g.DeleteTexture(bloomBrightTex);
            g.DeleteFramebuffer(bloomFboA);
            g.DeleteTexture(bloomTexA);
            g.DeleteFramebuffer(bloomFboB);
            g.DeleteTexture(bloomTexB);
            g.DeleteFramebuffer(godrayFbo);
            g.DeleteTexture(godrayTex);
            bloomBrightFbo = bloomBrightTex = bloomFboA = bloomTexA = bloomFboB = bloomTexB = godrayFbo = godrayTex = 0;
            bloomUnsupported = true;
            return false;
        }

        bloomWidth = bw;
        bloomHeight = bh;
        return true;
    }

    /// <summary>
    /// Runs the post-process stage and returns the texture to present. When bloom intensity is meaningful
    /// it does bright-pass → separable blur (half-res ping-pong) → additive composite into postColorTex;
    /// otherwise (or if any buffer/program is unavailable) it pass-throughs the scene unchanged. Always
    /// returns a valid texture so the scene is never lost.
    /// </summary>
    private uint RunPostProcess(
        GL g, uint sourceTex, int width, int height,
        float bloomThreshold, float bloomIntensity, float sunUvX, float sunUvY, float godrayIntensity)
    {
        if (!postProcessEnabled || postProgram == 0 || sourceTex == 0 || width <= 0 || height <= 0)
        {
            return sourceTex;
        }
        if (!EnsurePostBuffers(g, width, height))
        {
            return sourceTex;
        }

        g.Disable(EnableCap.DepthTest);
        g.DepthMask(false);
        g.Disable(EnableCap.Blend);
        g.BindVertexArray(skyVao); // reuse the sky pass's fullscreen triangle for every post pass

        bool buffersReady = bloomBrightProgram != 0 && bloomBlurProgram != 0 && godrayProgram != 0
            && bloomCompositeProgram != 0 && EnsureBloomBuffers(g, width, height);
        bool wantBloom = bloomIntensity > 0.001f;
        bool wantGodray = godrayIntensity > 0.001f;

        if (buffersReady && (wantBloom || wantGodray))
        {
            // Bright-pass (shared by bloom + god rays): full-res scene → half-res bloomBrightTex.
            g.BindFramebuffer(FramebufferTarget.Framebuffer, bloomBrightFbo);
            g.Viewport(0, 0, (uint)bloomWidth, (uint)bloomHeight);
            g.UseProgram(bloomBrightProgram);
            g.ActiveTexture(TextureUnit.Texture0);
            g.BindTexture(TextureTarget.Texture2D, sourceTex);
            g.Uniform1(bloomBrightTexLoc, 0);
            g.Uniform1(bloomBrightThresholdLoc, bloomThreshold);
            g.DrawArrays(PrimitiveType.Triangles, 0, 3);

            if (wantBloom)
            {
                // Separable blur: bright → B (horizontal) → A (vertical). A holds the final bloom.
                g.BindFramebuffer(FramebufferTarget.Framebuffer, bloomFboB);
                g.UseProgram(bloomBlurProgram);
                g.BindTexture(TextureTarget.Texture2D, bloomBrightTex);
                g.Uniform1(bloomBlurTexLoc, 0);
                g.Uniform2(bloomBlurDirLoc, 1f / bloomWidth, 0f);
                g.DrawArrays(PrimitiveType.Triangles, 0, 3);

                g.BindFramebuffer(FramebufferTarget.Framebuffer, bloomFboA);
                g.BindTexture(TextureTarget.Texture2D, bloomTexB);
                g.Uniform2(bloomBlurDirLoc, 0f, 1f / bloomHeight);
                g.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }

            if (wantGodray)
            {
                // Radial blur of the bright mask toward the sun's screen position → godrayTex.
                g.BindFramebuffer(FramebufferTarget.Framebuffer, godrayFbo);
                g.UseProgram(godrayProgram);
                g.BindTexture(TextureTarget.Texture2D, bloomBrightTex);
                g.Uniform1(godrayTexLoc, 0);
                g.Uniform2(godraySunUvLoc, sunUvX, sunUvY);
                g.DrawArrays(PrimitiveType.Triangles, 0, 3);
            }

            // Composite: scene + bloom*intensity + godray*intensity → postColorTex. Inactive terms get a
            // zero intensity so their (possibly stale) buffer contributes nothing.
            g.BindFramebuffer(FramebufferTarget.Framebuffer, postFbo);
            g.Viewport(0, 0, (uint)width, (uint)height);
            g.UseProgram(bloomCompositeProgram);
            g.ActiveTexture(TextureUnit.Texture0);
            g.BindTexture(TextureTarget.Texture2D, sourceTex);
            g.Uniform1(bloomCompSceneLoc, 0);
            g.ActiveTexture(TextureUnit.Texture1);
            g.BindTexture(TextureTarget.Texture2D, bloomTexA);
            g.Uniform1(bloomCompBloomLoc, 1);
            g.ActiveTexture(TextureUnit.Texture2);
            g.BindTexture(TextureTarget.Texture2D, godrayTex);
            g.Uniform1(bloomCompGodrayLoc, 2);
            g.Uniform1(bloomCompIntensityLoc, wantBloom ? bloomIntensity : 0f);
            g.Uniform1(bloomCompGodrayIntensityLoc, wantGodray ? godrayIntensity : 0f);
            g.DrawArrays(PrimitiveType.Triangles, 0, 3);
            g.BindTexture(TextureTarget.Texture2D, 0);
            g.ActiveTexture(TextureUnit.Texture1);
            g.BindTexture(TextureTarget.Texture2D, 0);
            g.ActiveTexture(TextureUnit.Texture0);
            g.BindTexture(TextureTarget.Texture2D, 0);

            if (wantBloom && !bloomStageLogged)
            {
                Log.Information("[GL3D] post-process: bloom active {W}x{H} (half {BW}x{BH})", width, height, bloomWidth, bloomHeight);
                bloomStageLogged = true;
            }
            if (wantGodray && !godrayStageLogged)
            {
                Log.Information("[GL3D] post-process: god rays active {W}x{H} sunUv={U},{V}", width, height, sunUvX, sunUvY);
                godrayStageLogged = true;
            }
        }
        else
        {
            // Pass-through (effects off / unavailable): scene → postColorTex unchanged.
            g.BindFramebuffer(FramebufferTarget.Framebuffer, postFbo);
            g.Viewport(0, 0, (uint)width, (uint)height);
            g.UseProgram(postProgram);
            g.ActiveTexture(TextureUnit.Texture0);
            g.BindTexture(TextureTarget.Texture2D, sourceTex);
            g.Uniform1(postTexLocation, 0);
            g.DrawArrays(PrimitiveType.Triangles, 0, 3);
            g.BindTexture(TextureTarget.Texture2D, 0);

            if (!postStageLogged)
            {
                Log.Information("[GL3D] post-process stage active (pass-through) {W}x{H}", width, height);
                postStageLogged = true;
            }
        }

        g.BindVertexArray(0);
        return postColorTex;
    }

    /// <summary>
    /// Allocates the cascade depth textures + depth-only FBOs once. Each is a DEPTH_COMPONENT24 texture set
    /// up for hardware comparison (sampler2DShadow) so part 4 can PCF-filter it. Latches
    /// <see cref="shadowUnsupported"/> on incompleteness so the caller skips the shadow pass for the session.
    /// </summary>
    private bool EnsureShadowMaps(GL g)
    {
        if (shadowUnsupported)
        {
            return false;
        }
        if (shadowMapsAllocated)
        {
            return true;
        }

        for (int i = 0; i < ShadowCascadeCount; i++)
        {
            uint tex = g.GenTexture();
            g.BindTexture(TextureTarget.Texture2D, tex);
            g.TexImage2D(
                TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24,
                ShadowMapSize, ShadowMapSize, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)GLEnum.CompareRefToTexture);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)GLEnum.Lequal);
            g.BindTexture(TextureTarget.Texture2D, 0);

            uint fbo = g.GenFramebuffer();
            g.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            g.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, tex, 0);
            GLEnum status = g.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            g.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            if (status != GLEnum.FramebufferComplete)
            {
                Log.Information("[GL3D] shadow framebuffer incomplete ({Status}) cascade {C} — shadows off this session", status, i);
                g.DeleteTexture(tex);
                g.DeleteFramebuffer(fbo);
                for (int j = 0; j < i; j++)
                {
                    g.DeleteFramebuffer(shadowFbos[j]);
                    g.DeleteTexture(shadowDepthTex[j]);
                    shadowFbos[j] = 0;
                    shadowDepthTex[j] = 0;
                }
                shadowUnsupported = true;
                return false;
            }
            shadowFbos[i] = fbo;
            shadowDepthTex[i] = tex;
        }
        shadowMapsAllocated = true;
        return true;
    }

    /// <summary>
    /// Renders the terrain depth from the sun's POV into each cascade's shadow map and stores the cascade
    /// light matrices + split far-distances for the terrain shader (part 4). Self-contained: saves and
    /// restores the bound framebuffer + viewport, so it can run before the sky/terrain passes without
    /// disturbing them. Skips at night (sun at/below horizon) and before geometry is uploaded. The terrain
    /// vertex (absolute world aPos) is transformed straight by the cascade light matrix.
    /// </summary>
    private void RenderShadowMaps(GL g, Camera3D camera, Vector3 sunDirection, float aspectRatio, int vpWidth, int vpHeight)
    {
        shadowsActiveThisFrame = false;
        if (!shadowsEnabled || shadowDepthProgram == 0 || tileBuffers.Count == 0)
        {
            return;
        }
        Vector3 sun = sunDirection.LengthSquared() > 1e-8f ? Vector3.Normalize(sunDirection) : Vector3.Zero;
        if (sun.Z <= 0.02f) // sun at/below the horizon → no shadows (night)
        {
            return;
        }
        if (!EnsureShadowMaps(g))
        {
            return;
        }

        Span<int> prevFbo = stackalloc int[1];
        g.GetInteger(GLEnum.FramebufferBinding, prevFbo);

        float near = camera.NearPlane;
        float far = MathF.Min(camera.FarPlane, ShadowMaxDistance);
        IReadOnlyList<float> splits = CascadeShadowSplits.FarDistances(near, far, ShadowCascadeCount, ShadowSplitLambda);

        g.Enable(EnableCap.DepthTest);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
        g.UseProgram(shadowDepthProgram);

        Span<float> lm = stackalloc float[16];
        float sliceNear = near;
        for (int c = 0; c < ShadowCascadeCount; c++)
        {
            float sliceFar = splits[c];
            Matrix4x4 lightVp = CascadeLightMatrix.Build(camera, aspectRatio, sliceNear, sliceFar, sun, depthPadding: 2000f);
            cascadeLightVp[c] = lightVp;
            cascadeSplitFar[c] = sliceFar;

            g.BindFramebuffer(FramebufferTarget.Framebuffer, shadowFbos[c]);
            g.Viewport(0, 0, ShadowMapSize, ShadowMapSize);
            g.Clear((uint)ClearBufferMask.DepthBufferBit);

            // Same row-major upload as uMvp (transpose=false): GL reads it column-major, so GLSL's
            // uLightVp * v matches Vector4.Transform(v, lightVp) that the matrix tests pin.
            lm[0] = lightVp.M11; lm[1] = lightVp.M12; lm[2] = lightVp.M13; lm[3] = lightVp.M14;
            lm[4] = lightVp.M21; lm[5] = lightVp.M22; lm[6] = lightVp.M23; lm[7] = lightVp.M24;
            lm[8] = lightVp.M31; lm[9] = lightVp.M32; lm[10] = lightVp.M33; lm[11] = lightVp.M34;
            lm[12] = lightVp.M41; lm[13] = lightVp.M42; lm[14] = lightVp.M43; lm[15] = lightVp.M44;
            g.UniformMatrix4(shadowLightVpLoc, 1, false, lm);

            foreach (KeyValuePair<TerrainMesh3D, TileBuffers> entry in tileBuffers)
            {
                g.BindVertexArray(entry.Value.Vao);
                g.DrawElements(PrimitiveType.Triangles, (uint)entry.Value.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }
            sliceNear = sliceFar;
        }

        g.BindVertexArray(0);
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)prevFbo[0]);
        g.Viewport(0, 0, (uint)vpWidth, (uint)vpHeight);
        shadowsActiveThisFrame = true;

        if (!shadowPassLogged)
        {
            Log.Information("[GL3D] shadow pass: {Cascades} cascades {Size}px, far {FarM}m, splits {S0}/{S1}/{S2}",
                ShadowCascadeCount, ShadowMapSize, far, cascadeSplitFar[0], cascadeSplitFar[1], cascadeSplitFar[2]);
            shadowPassLogged = true;
        }
    }

    /// <summary>
    /// Creates / resizes the off-screen multisampled colour+depth FBO. Returns false (and leaves nothing
    /// bound to change) when MSAA isn't usable, so the caller renders directly into Skia's FBO instead.
    /// </summary>
    private bool EnsureMsaaTarget(GL g, int width, int height)
    {
        if (msaaUnsupported || !MsaaEnabled)
        {
            return false; // quality profile turned anti-aliasing off → draw straight into the present FBO
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
    // cameraWorldPos additionally drives the per-cell near/far resolution tier (OrthoDistanceTier).
    private void StreamOrthoTextures(GL g, Matrix4x4 viewProjection, IReadOnlyList<TerrainMesh3D> tiles, Vector3 cameraWorldPos)
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

        LogMemoryUsage(tiles);

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

        // Ortho cells are ~16 km across — the camera can be standing right on top of one and 60 km from
        // another, yet both got the same flat resolution cap. Re-evaluate every VISIBLE cell's tier from its
        // CURRENT distance to the camera every frame (cheap: one clamp + one distance per cell); force a
        // re-download-from-master + re-upload only for the cells whose tier actually changed (rare — the
        // hysteresis band in OrthoDistanceTier keeps this from flapping at the boundary).
        var tierChanged = new List<int>();
        foreach (int idx in visibleOrthoCells)
        {
            if (!orthoCellBounds.TryGetValue(idx, out var aabb))
            {
                continue;
            }

            OrthoTile t = orthoTiles[idx];
            Vector3 nearest = Vector3.Clamp(cameraWorldPos, aabb.Min, aabb.Max);
            float distance = Vector3.Distance(cameraWorldPos, nearest);
            int desiredCap = OrthoDistanceTier.DesiredCapPx(t.UploadedCapPx, distance);
            if (desiredCap == t.UploadedCapPx)
            {
                continue;
            }

            (t.Rgba, t.Width, t.Height) = desiredCap >= t.MasterWidth && desiredCap >= t.MasterHeight
                ? (t.MasterRgba, t.MasterWidth, t.MasterHeight)
                : OrthoCellDownsampler.Downsample(t.MasterRgba, t.MasterWidth, t.MasterHeight, desiredCap);
            t.UploadedCapPx = desiredCap;
            if (t.Texture != 0)
            {
                g.DeleteTexture(t.Texture);
                t.Texture = 0;
            }

            tierChanged.Add(idx);
        }

        if (plan.ToUpload.Count == 0 && plan.ToEvict.Count == 0 && tierChanged.Count == 0)
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

        if (plan.ToUpload.Count > 0 || tierChanged.Count > 0)
        {
            // Upload beyond GL_MAX_TEXTURE_SIZE yields a garbage/black texture, so guard the size once.
            Span<int> maxTexSize = stackalloc int[1] { 2048 };
            g.GetInteger(GLEnum.MaxTextureSize, maxTexSize);
            int maxSize = maxTexSize[0];

            // Query the driver's max anisotropy once, outside the upload loop (a per-iteration stackalloc
            // would risk a stack overflow — CA2014). STAGE-0 max-fidelity: request the driver's ACTUAL max
            // anisotropy (not a fixed 16) so the ortho drape is as sharp as the GPU allows at grazing angles.
            const GLEnum maxAnisotropyPName = (GLEnum)0x84FF; // GL_MAX_TEXTURE_MAX_ANISOTROPY_EXT
            Span<float> maxAniso = stackalloc float[1] { 1f };
            g.GetFloat(maxAnisotropyPName, maxAniso);
            float aniso = maxAniso[0] < 1f ? 1f : maxAniso[0];

            // Union, not concatenation: a cell freshly promoted from ToUpload (first-ever appearance,
            // UploadedCapPx starts 0) is ALSO caught by the tier check above, so it lands in both lists —
            // UploadOrthoCell is idempotent (skips once Texture != 0) either way, but dedupe to avoid a
            // pointless second log line.
            var toUploadNow = new HashSet<int>(plan.ToUpload);
            toUploadNow.UnionWith(tierChanged);
            foreach (int idx in toUploadNow)
            {
                OrthoTile cell = orthoTiles[idx];
                // Report the bound ortho cell's source pixel size + the anisotropy actually applied, so the
                // near-peak texture resolution is readable from the log (Stage-0 quality proof).
                Log.Information(
                    "[GL3D] ortho cell uploaded: {W}x{H}px, mipmapped + anisotropy x{Aniso} (driver max x{MaxAniso})",
                    cell.Width, cell.Height, aniso, maxAniso[0]);
                UploadOrthoCell(g, cell, maxSize, aniso);
            }

            g.BindTexture(TextureTarget.Texture2D, 0);
        }
    }

    // Throttled one-line memory snapshot: resident ortho cells + estimated ortho MB (CPU bytes + GPU texture
    // incl. ~33% mips), the resident drawable tile count + its estimated geometry MB, and the managed heap MB.
    // The route film OOMs at its lowest point; this makes the run-up readable from the log so a regression in
    // the ortho/baked budgets is visible without a debugger.
    private void LogMemoryUsage(IReadOnlyList<TerrainMesh3D> tiles)
    {
        long now = Environment.TickCount64;
        if (now - lastMemLogTick < MemLogIntervalMs)
        {
            return;
        }

        lastMemLogTick = now;

        long orthoCpuBytes = 0;
        long orthoGpuBytes = 0;
        foreach (OrthoTile tile in orthoTiles)
        {
            orthoCpuBytes += tile.Rgba.LongLength;
            orthoGpuBytes += OrthoVramBudget.CellResidentBytes(tile.Width, tile.Height);
        }

        long tileGeometryBytes = 0;
        for (int i = 0; i < tiles.Count; i++)
        {
            tileGeometryBytes += tiles[i].EstimatedGpuBytes;
        }

        const double Mb = 1024.0 * 1024.0;
        Log.Information(
            "[Mem] ortho {Cells} cells ~{OrthoMb:F0}MB (cpu {CpuMb:F0}+gpu {GpuMb:F0}) | tiles {Tiles} ~{TileMb:F0}MB | heap {HeapMb:F0}MB",
            orthoTiles.Count,
            (orthoCpuBytes + orthoGpuBytes) / Mb,
            orthoCpuBytes / Mb,
            orthoGpuBytes / Mb,
            tiles.Count,
            tileGeometryBytes / Mb,
            GC.GetTotalMemory(forceFullCollection: false) / Mb);
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
        // seamlessly at the shared seam. The mobile cells are power-of-two so GenerateMipmap halves cleanly.
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
            // Per-tile world AABB is precomputed at mesh construction (off-thread) — no per-swap vertex re-scan.
            Vector3 min = tile.WorldMin;
            Vector3 max = tile.WorldMax;
            if (orthoCellBounds.TryGetValue(idx, out var existing))
            {
                min = Vector3.Min(min, existing.Min);
                max = Vector3.Max(max, existing.Max);
            }

            orthoCellBounds[idx] = (min, max);
        }

        orthoBoundsTiles = tiles;
    }

    // DEBUG: builds a flat triangle-fan from the Morskie Oko OSM outline at the lake level, in the terrain vertex
    // layout (only position matters; the shader paints it flat magenta). Lets us eyeball polygon-vs-ortho alignment.
    // Builds the lake-water mesh for every named lake whose centroid sits within the loaded terrain (from the
    // bundled MountainLakeData / OSM outlines). Each lake's ring is ear-clipped into a flat fan-free mesh at its
    // own water elevation and appended to one shared VBO; per-lake (offset, count, centroid, radius) ranges are
    // recorded in lakeDraws so the draw loop can shade each with its own smooth radial depth.
    // Lake-water rebuild cache: the mesh depends only on (tiles, raster, LakeFineBounds) — with the
    // OSM-wide table (171 tarns vs the old 12) re-ear-clipping every lake EVERY FRAME would burn CPU/GC,
    // so the geometry is rebuilt only when the terrain it is seated against actually changes.
    private object? lakeWaterTilesRef;
    private object? lakeWaterRasterRef;
    private MapaTur.Domain.Geography.MapBounds? lakeWaterFineBounds;

    private unsafe void BuildLakeWater(GL g, IReadOnlyList<TerrainMesh3D> tiles, MapaTur.Domain.Terrain.DemRaster? raster)
    {
        if (ReferenceEquals(tiles, lakeWaterTilesRef)
            && ReferenceEquals(raster, lakeWaterRasterRef)
            && Equals(LakeFineBounds, lakeWaterFineBounds))
        {
            return; // same terrain inputs — the uploaded VBO and lakeDraws are still valid
        }

        lakeWaterTilesRef = tiles;
        lakeWaterRasterRef = raster;
        lakeWaterFineBounds = LakeFineBounds;

        lakeDraws.Clear();
        debugPolyVertexCount = 0;
        if (tiles.Count == 0)
        {
            return;
        }

        // Only lakes within the loaded terrain extent — so water never floats over un-loaded ground (empty in
        // the Beskidy base view; Morskie Oko + neighbours in the Tatra LOD demo). Union of all tile bounds.
        MapBounds extent = tiles[0].Bounds;
        for (int i = 1; i < tiles.Count; i++) { extent = extent.Union(tiles[i].Bounds); }

        const int stride = 12;
        var verts = new List<float>(4096);
        foreach (MountainLake lake in MountainLakeData.WithinBounds(extent))
        {
            IReadOnlyList<GeoPoint> ring = lake.Outline;
            int m = ring.Count;
            if (m < 3)
            {
                continue;
            }
            // Seat the plane against the terrain ACTUALLY LOADED at the lake. Inside the fine-detail window
            // (LakeFineBounds) the basin is real → proven legacy seating. Elsewhere, a coarse base may have
            // FILLED the basin (slopes average into the subsampled cells) — a blind plane then pokes through
            // the shifted slopes inside the outline as chains of dark slivers ("dziury") → seat from the
            // raster sample, or skip the lake at this LOD (the streamed 1 m detail restores it up close).
            double cLat = 0, cLon = 0;
            for (int i = 0; i < m; i++) { cLat += ring[i].Latitude; cLon += ring[i].Longitude; }
            var centroidGeo = new MapaTur.Domain.Geography.GeoPoint(cLat / m, cLon / m);
            float waterElevM;
            if (raster is null || (LakeFineBounds is { } fine && fine.Contains(centroidGeo)))
            {
                waterElevM = (float)lake.ElevationMeters + 4f; // legacy: just above the (accurate) bed
            }
            else
            {
                double terrain = raster.SampleBilinear(centroidGeo.Longitude, centroidGeo.Latitude);
                if (terrain == raster.NoDataValue)
                {
                    terrain = double.NaN;
                }

                float? seat = MapaTur.Application.Terrain.LakeWaterSeating.Seat(lake.ElevationMeters, terrain);
                if (seat is null)
                {
                    continue; // basin filled at this LOD — skip (no dark slivers through coarse slopes)
                }

                waterElevM = seat.Value;
            }

            var w2 = new Vector2[m];
            var w3 = new Vector3[m];
            for (int i = 0; i < m; i++)
            {
                Vector3 wv = tiles[0].GeoToWorld(ring[i], waterElevM);
                w3[i] = wv; w2[i] = new Vector2(wv.X, wv.Y);
            }

            // Ear-clip into NON-OVERLAPPING triangles (a centroid fan overlaps itself in concave bays → bright rays).
            List<int> tris = EarClipXy(w2);
            if (tris.Count == 0)
            {
                continue;
            }

            int startVertex = verts.Count / stride;
            foreach (int idx in tris)
            {
                Vector3 w = w3[idx];
                verts.Add(w.X); verts.Add(w.Y); verts.Add(w.Z);
                verts.Add(0f); verts.Add(0f); verts.Add(0f); verts.Add(1f);
                verts.Add(0f); verts.Add(0f); verts.Add(1f);
                verts.Add(0f); verts.Add(0f);
            }

            float cx = 0, cy = 0;
            for (int i = 0; i < m; i++) { cx += w2[i].X; cy += w2[i].Y; }
            cx /= m; cy /= m;
            var center = new Vector2(cx, cy);
            float maxR = 1f;
            for (int i = 0; i < m; i++) { maxR = MathF.Max(maxR, Vector2.Distance(center, w2[i])); }

            lakeDraws.Add(new LakeDraw(startVertex, tris.Count, center, maxR));
        }

        int n = verts.Count;
        debugPolyVertexCount = n / stride;
        if (n == 0)
        {
            return;
        }
        if (debugPolyFloats is null || debugPolyFloats.Length < n)
        {
            debugPolyFloats = new float[n];
        }
        verts.CopyTo(debugPolyFloats);
        float[] buf = debugPolyFloats;

        if (debugPolyVao == 0)
        {
            debugPolyVao = g.GenVertexArray();
            debugPolyVbo = g.GenBuffer();
            g.BindVertexArray(debugPolyVao);
            g.BindBuffer(BufferTargetARB.ArrayBuffer, debugPolyVbo);
            int sb = stride * sizeof(float);
            g.EnableVertexAttribArray(0); g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, (uint)sb, (void*)0);
            g.EnableVertexAttribArray(1); g.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, (uint)sb, (void*)(3 * sizeof(float)));
            g.EnableVertexAttribArray(2); g.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, (uint)sb, (void*)(7 * sizeof(float)));
            g.EnableVertexAttribArray(3); g.VertexAttribPointer(3, 2, VertexAttribPointerType.Float, false, (uint)sb, (void*)(10 * sizeof(float)));
        }
        else
        {
            g.BindVertexArray(debugPolyVao);
            g.BindBuffer(BufferTargetARB.ArrayBuffer, debugPolyVbo);
        }
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(buf, 0, n), BufferUsageARB.DynamicDraw);
        g.BindVertexArray(0);
    }

    // Ear-clipping triangulation of a simple (possibly concave) polygon. Returns a flat list of vertex indices
    // (triples) into the input ring. O(n²); fine for a few-hundred-point lake outline rebuilt occasionally.
    private static List<int> EarClipXy(Vector2[] poly)
    {
        int nn = poly.Length;
        var tris = new List<int>();
        if (nn < 3)
        {
            return tris;
        }

        // Signed area → ensure CCW order (ear test below assumes CCW).
        double area = 0;
        for (int i = 0; i < nn; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % nn];
            area += (a.X * b.Y) - (b.X * a.Y);
        }
        var order = new List<int>();
        if (area < 0)
        {
            for (int i = nn - 1; i >= 0; i--) { order.Add(i); }
        }
        else
        {
            for (int i = 0; i < nn; i++) { order.Add(i); }
        }

        int guard = 0;
        int maxGuard = nn * nn;
        while (order.Count > 3 && guard++ < maxGuard)
        {
            bool clipped = false;
            int cnt = order.Count;
            for (int i = 0; i < cnt; i++)
            {
                int i0 = order[(i - 1 + cnt) % cnt];
                int i1 = order[i];
                int i2 = order[(i + 1) % cnt];
                Vector2 a = poly[i0], b = poly[i1], c = poly[i2];
                // Convex corner? cross of (b-a, c-b) > 0 for CCW.
                float cross = ((b.X - a.X) * (c.Y - b.Y)) - ((b.Y - a.Y) * (c.X - b.X));
                if (cross <= 0f)
                {
                    continue; // reflex
                }
                bool empty = true;
                for (int j = 0; j < cnt; j++)
                {
                    int k = order[j];
                    if (k == i0 || k == i1 || k == i2) { continue; }
                    if (PointInTri(poly[k], a, b, c)) { empty = false; break; }
                }
                if (!empty)
                {
                    continue;
                }
                tris.Add(i0); tris.Add(i1); tris.Add(i2);
                order.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped)
            {
                break; // degenerate / self-intersecting — bail with what we have
            }
        }
        if (order.Count == 3)
        {
            tris.Add(order[0]); tris.Add(order[1]); tris.Add(order[2]);
        }
        return tris;
    }

    private static bool PointInTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = ((p.X - b.X) * (a.Y - b.Y)) - ((a.X - b.X) * (p.Y - b.Y));
        float d2 = ((p.X - c.X) * (b.Y - c.Y)) - ((b.X - c.X) * (p.Y - c.Y));
        float d3 = ((p.X - a.X) * (c.Y - a.Y)) - ((c.X - a.X) * (p.Y - a.Y));
        bool hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
        bool hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
        return !(hasNeg && hasPos);
    }

    // Projects a world DIRECTION (point at infinity) to normalized device coords (x,y in [-1,1]), matching the
    // star/Moon vertex shaders' w=0 transform. Used to derive the Moon's bright-limb screen direction.
    private static Vector2 ProjectDirectionNdc(Vector3 direction, Matrix4x4 viewProjection)
    {
        Vector4 clip = Vector4.Transform(new Vector4(direction, 0f), viewProjection);
        float w = MathF.Abs(clip.W) < 1e-6f ? 1e-6f : clip.W;
        return new Vector2(clip.X / w, clip.Y / w);
    }

    // Rebuilds + uploads the star VBO when the Julian-Date inputs change (slider hour, date, or observer
    // location); otherwise leaves the cached buffer untouched. Packs only above-horizon stars (4 floats each:
    // dir.xyz + magnitude). Cheap — the bundled catalog is ~30 stars — but the cache keeps it off the hot path.
    private void EnsureStarBuffer(GL g, DateOnly localDate, double localHour, GeoPoint anchor)
    {
        int dateKey = (localDate.Year * 10000) + (localDate.Month * 100) + localDate.Day;
        if (starBufferReady
            && dateKey == lastStarDateKey
            && Math.Abs(localHour - lastStarLocalHour) < 1e-4
            && anchor.Latitude == lastStarLat
            && anchor.Longitude == lastStarLon)
        {
            return;
        }

        IReadOnlyList<(Vector3 Direction, float Magnitude)> dirs = NightSky.StarDirectionsForLocalDate(
            StarCatalogData.Bundled, localDate.Year, localDate.Month, localDate.Day, localHour,
            anchor.Latitude, anchor.Longitude);

        if (starScratch is null || starScratch.Length < dirs.Count * 4)
        {
            starScratch = new float[dirs.Count * 4];
        }

        int n = 0;
        for (int i = 0; i < dirs.Count; i++)
        {
            (Vector3 dir, float mag) = dirs[i];
            if (dir.Z <= 0f)
            {
                continue; // below the horizon — never drawn
            }

            starScratch[(n * 4) + 0] = dir.X;
            starScratch[(n * 4) + 1] = dir.Y;
            starScratch[(n * 4) + 2] = dir.Z;
            starScratch[(n * 4) + 3] = mag;
            n++;
        }
        starCount = n;
        Log.Information("[Stars] build hour={Hour:F2} anchor=({Lat:F3},{Lon:F3}) catalog={Cat} aboveHorizon={N}", localHour, anchor.Latitude, anchor.Longitude, dirs.Count, n);

        g.BindVertexArray(starVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, starVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(n * 4 * sizeof(float)), starScratch.AsSpan(0, n * 4), BufferUsageARB.DynamicDraw);
        g.BindVertexArray(0);

        lastStarDateKey = dateKey;
        lastStarLocalHour = localHour;
        lastStarLat = anchor.Latitude;
        lastStarLon = anchor.Longitude;
        starBufferReady = true;
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
        modelOffsetLocation = g.GetUniformLocation(program, "uModelOffset");
        stableOffsetLocation = g.GetUniformLocation(program, "uStableOffset");
        debugPolyLocation = g.GetUniformLocation(program, "uDebugPoly");
        lakeCenterLocation = g.GetUniformLocation(program, "uLakeCenter");
        lakeRadiusLocation = g.GetUniformLocation(program, "uLakeRadius");
        reflectionPassLocation = g.GetUniformLocation(program, "uReflectionPass");
        waterClipZLocation = g.GetUniformLocation(program, "uWaterClipZ");
        reflectionTexLocation = g.GetUniformLocation(program, "uReflectionTex");
        reflectionEnabledLocation = g.GetUniformLocation(program, "uReflectionEnabled");
        viewportPxLocation = g.GetUniformLocation(program, "uViewportPx");
        lightDirLocation = g.GetUniformLocation(program, "uLightDir");
        snowSunLocation = g.GetUniformLocation(program, "uSnowSun");
        ambientLocation = g.GetUniformLocation(program, "uAmbient");
        sunColorLocation = g.GetUniformLocation(program, "uSunColor");
        skyAmbientLocation = g.GetUniformLocation(program, "uSkyAmbient");
        orthoSamplerLocation = g.GetUniformLocation(program, "uOrtho");
        useOrthoLocation = g.GetUniformLocation(program, "uUseOrtho");
        orthoGlobalFadeLocation = g.GetUniformLocation(program, "uOrthoGlobalFade");
        orthoTexelLocation = g.GetUniformLocation(program, "uOrthoTexel");
        slopeModeLocation = g.GetUniformLocation(program, "uSlopeMode");
        slopePaletteLocation = g.GetUniformLocation(program, "uSlopePalette");
        sharpenLocation = g.GetUniformLocation(program, "uSharpen");
        debugUvLocation = g.GetUniformLocation(program, "uDebugUv");
        orthoMinXyLocation = g.GetUniformLocation(program, "uOrthoMinXY");
        orthoMaxXyLocation = g.GetUniformLocation(program, "uOrthoMaxXY");
        orthoBlendLocation = g.GetUniformLocation(program, "uOrthoBlendMeters");
        rockStrengthLocation = g.GetUniformLocation(program, "uRockStrength");
        biomeModeLocation = g.GetUniformLocation(program, "uBiomeMode");
        biomeScreeSlopeLocation = g.GetUniformLocation(program, "uBiomeScreeSlopeDeg");
        biomeMeadowMaxZLocation = g.GetUniformLocation(program, "uBiomeMeadowMaxZ");
        biomeSnowZLocation = g.GetUniformLocation(program, "uBiomeSnowZ");
        biomeIceZLocation = g.GetUniformLocation(program, "uBiomeIceZ");
        biomeAspectShiftZLocation = g.GetUniformLocation(program, "uBiomeAspectShiftZ");
        biomePaletteLocation = g.GetUniformLocation(program, "uBiomePalette");
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
        terrainContourSpacingZLocation = g.GetUniformLocation(program, "uContourSpacingZ");
        terrainContourColorLocation = g.GetUniformLocation(program, "uContourColor");
        terrainContourMajorSpacingZLocation = g.GetUniformLocation(program, "uContourMajorSpacingZ");
        terrainContourMajorColorLocation = g.GetUniformLocation(program, "uContourMajorColor");
        terrainContourStrengthLocation = g.GetUniformLocation(program, "uContourStrength");
        terrainContourWidthPxLocation = g.GetUniformLocation(program, "uContourWidthPx");
        trailMaskSamplerLocation = g.GetUniformLocation(program, "uTrailMask");
        waterMaskSamplerLocation = g.GetUniformLocation(program, "uWaterMask");
        waterMaskStrengthLocation = g.GetUniformLocation(program, "uWaterStrength");
        trailMaskStrengthLocation = g.GetUniformLocation(program, "uTrailStrength");
        trailMaskMinXYLocation = g.GetUniformLocation(program, "uTrailMaskMinXY");
        trailMaskSizeXYLocation = g.GetUniformLocation(program, "uTrailMaskSizeXY");
        trailMaskMaxDistLocation = g.GetUniformLocation(program, "uTrailMaxDist");
        trailMaskHalfWidthLocation = g.GetUniformLocation(program, "uTrailHalfWidth");
        terrainSnowBandZLocation = g.GetUniformLocation(program, "uSnowBandZ");
        terrainSnowSlopeCosBareLocation = g.GetUniformLocation(program, "uSnowSlopeCosBare");
        terrainSnowSlopeCosFullLocation = g.GetUniformLocation(program, "uSnowSlopeCosFull");
        terrainNoonSnowLiftLocation = g.GetUniformLocation(program, "uNoonSnowLift");
        shadowMap0Loc = g.GetUniformLocation(program, "uShadowMap0");
        shadowMap1Loc = g.GetUniformLocation(program, "uShadowMap1");
        shadowMap2Loc = g.GetUniformLocation(program, "uShadowMap2");
        cascadeVp0Loc = g.GetUniformLocation(program, "uCascadeVp0");
        cascadeVp1Loc = g.GetUniformLocation(program, "uCascadeVp1");
        cascadeVp2Loc = g.GetUniformLocation(program, "uCascadeVp2");
        cascadeSplitLoc = g.GetUniformLocation(program, "uCascadeSplit");
        shadowStrengthLoc = g.GetUniformLocation(program, "uShadowStrength");

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
        skySunGlowIntensityLocation = g.GetUniformLocation(skyProgram, "uSunGlowIntensity");
        skySunGlowWidthLocation = g.GetUniformLocation(skyProgram, "uSunGlowWidth");

        // Fullscreen triangle: 3 vertices, each xy in clip space, covering NDC [-1,1]^2 with one extra
        // vertex outside the rect so the rasteriser fills the full screen without re-clipping a quad.
        Span<float> tri = stackalloc float[6] { -1f, -1f, 3f, -1f, -1f, 3f };
        skyVao = g.GenVertexArray();
        g.BindVertexArray(skyVao);
        skyVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, skyVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(tri.Length * sizeof(float)), tri, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        g.BindVertexArray(0);

        // Star program — catalog point sprites for the night sky. Own VAO/VBO; the VBO is filled lazily by
        // EnsureStarBuffer (one (dir.xyz, mag) record per above-horizon star). The attribute layout is set up
        // now against starVbo so later re-uploads only need a BufferData, not a re-bind of the pointers.
        uint stv = CompileShader(g, ShaderType.VertexShader, StarVertexShaderSource);
        uint stf = CompileShader(g, ShaderType.FragmentShader, StarFragmentShaderSource);
        starProgram = g.CreateProgram();
        g.AttachShader(starProgram, stv);
        g.AttachShader(starProgram, stf);
        g.LinkProgram(starProgram);
        g.GetProgram(starProgram, ProgramPropertyARB.LinkStatus, out int starLinked);
        if (starLinked == 0)
        {
            string log = g.GetProgramInfoLog(starProgram);
            throw new InvalidOperationException("Star shader link failed: " + log);
        }
        g.DetachShader(starProgram, stv);
        g.DetachShader(starProgram, stf);
        g.DeleteShader(stv);
        g.DeleteShader(stf);
        starViewProjLocation = g.GetUniformLocation(starProgram, "uViewProj");
        starNightFactorLocation = g.GetUniformLocation(starProgram, "uNightFactor");
        starStarsOnLocation = g.GetUniformLocation(starProgram, "uStarsOn");
        starVao = g.GenVertexArray();
        g.BindVertexArray(starVao);
        starVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, starVbo);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(3 * sizeof(float)));
        g.BindVertexArray(0);
        starCount = 0;
        starBufferReady = false;

        // Moon program — a single phased-disc point sprite (no VAO/VBO of its own; the draw binds skyVao and the
        // vertex shader positions the point from the uMoonDir uniform).
        uint mvs = CompileShader(g, ShaderType.VertexShader, MoonVertexShaderSource);
        uint mfs = CompileShader(g, ShaderType.FragmentShader, MoonFragmentShaderSource);
        moonProgram = g.CreateProgram();
        g.AttachShader(moonProgram, mvs);
        g.AttachShader(moonProgram, mfs);
        g.LinkProgram(moonProgram);
        g.GetProgram(moonProgram, ProgramPropertyARB.LinkStatus, out int moonLinked);
        if (moonLinked == 0)
        {
            string log = g.GetProgramInfoLog(moonProgram);
            throw new InvalidOperationException("Moon shader link failed: " + log);
        }
        g.DetachShader(moonProgram, mvs);
        g.DetachShader(moonProgram, mfs);
        g.DeleteShader(mvs);
        g.DeleteShader(mfs);
        moonViewProjLocation = g.GetUniformLocation(moonProgram, "uViewProj");
        moonDirLocation = g.GetUniformLocation(moonProgram, "uMoonDir");
        moonSizeLocation = g.GetUniformLocation(moonProgram, "uSizePx");
        moonTermDirLocation = g.GetUniformLocation(moonProgram, "uTermDir");
        moonIlluminatedLocation = g.GetUniformLocation(moonProgram, "uIlluminated");
        moonNightFactorLocation = g.GetUniformLocation(moonProgram, "uNightFactor");

        // Post-process program — fullscreen pass-through (foundation for bloom / god rays). Reuses the
        // fullscreen triangle above (skyVao) for its draw.
        uint pvs = CompileShader(g, ShaderType.VertexShader, PostVertexShaderSource);
        uint pfs = CompileShader(g, ShaderType.FragmentShader, PostFragmentShaderSource);
        postProgram = g.CreateProgram();
        g.AttachShader(postProgram, pvs);
        g.AttachShader(postProgram, pfs);
        g.LinkProgram(postProgram);
        g.GetProgram(postProgram, ProgramPropertyARB.LinkStatus, out int postLinked);
        if (postLinked == 0)
        {
            string log = g.GetProgramInfoLog(postProgram);
            throw new InvalidOperationException("Post-process shader link failed: " + log);
        }
        g.DetachShader(postProgram, pvs);
        g.DetachShader(postProgram, pfs);
        g.DeleteShader(pvs);
        g.DeleteShader(pfs);
        postTexLocation = g.GetUniformLocation(postProgram, "uTex");

        // Bloom programs (bright-pass, separable blur, composite) — all share the post vertex shader.
        bloomBrightProgram = BuildPostProgram(g, BloomBrightFragmentShaderSource, "Bloom bright-pass");
        bloomBrightTexLoc = g.GetUniformLocation(bloomBrightProgram, "uTex");
        bloomBrightThresholdLoc = g.GetUniformLocation(bloomBrightProgram, "uThreshold");
        bloomBlurProgram = BuildPostProgram(g, BloomBlurFragmentShaderSource, "Bloom blur");
        bloomBlurTexLoc = g.GetUniformLocation(bloomBlurProgram, "uTex");
        bloomBlurDirLoc = g.GetUniformLocation(bloomBlurProgram, "uDir");
        bloomCompositeProgram = BuildPostProgram(g, BloomCompositeFragmentShaderSource, "Bloom composite");
        bloomCompSceneLoc = g.GetUniformLocation(bloomCompositeProgram, "uScene");
        bloomCompBloomLoc = g.GetUniformLocation(bloomCompositeProgram, "uBloom");
        bloomCompIntensityLoc = g.GetUniformLocation(bloomCompositeProgram, "uIntensity");
        bloomCompGodrayLoc = g.GetUniformLocation(bloomCompositeProgram, "uGodray");
        bloomCompGodrayIntensityLoc = g.GetUniformLocation(bloomCompositeProgram, "uGodrayIntensity");
        godrayProgram = BuildPostProgram(g, GodrayFragmentShaderSource, "God rays");
        godrayTexLoc = g.GetUniformLocation(godrayProgram, "uTex");
        godraySunUvLoc = g.GetUniformLocation(godrayProgram, "uSunUv");

        // Shadow depth program — depth-only pass for Cascaded Shadow Maps (own vertex/fragment shaders).
        uint shvs = CompileShader(g, ShaderType.VertexShader, ShadowDepthVertexShaderSource);
        uint shfs = CompileShader(g, ShaderType.FragmentShader, ShadowDepthFragmentShaderSource);
        shadowDepthProgram = g.CreateProgram();
        g.AttachShader(shadowDepthProgram, shvs);
        g.AttachShader(shadowDepthProgram, shfs);
        g.LinkProgram(shadowDepthProgram);
        g.GetProgram(shadowDepthProgram, ProgramPropertyARB.LinkStatus, out int shadowLinked);
        if (shadowLinked == 0)
        {
            string log = g.GetProgramInfoLog(shadowDepthProgram);
            throw new InvalidOperationException("Shadow depth shader link failed: " + log);
        }
        g.DetachShader(shadowDepthProgram, shvs);
        g.DetachShader(shadowDepthProgram, shfs);
        g.DeleteShader(shvs);
        g.DeleteShader(shfs);
        shadowLightVpLoc = g.GetUniformLocation(shadowDepthProgram, "uLightVp");

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
        cloudDispScaleLocation = g.GetUniformLocation(cloudProgram, "uDispScale");
        cloudDispAmpLocation = g.GetUniformLocation(cloudProgram, "uDispAmp");

        // Tessellated grid (was a flat 4-vertex quad) so the vertex shader can heave the cloud surface
        // vertically into a rolling, wind-drifting sea — the depth test then carves a living, wavy
        // waterline against the peaks instead of a perfectly level one.
        const int cloudRes = 128;                 // cells per side
        const int cloudVpr = cloudRes + 1;        // vertices per row
        var cloudVerts = new float[cloudVpr * cloudVpr * 2];
        int vi = 0;
        for (int j = 0; j < cloudVpr; j++)
        {
            float v = ((j / (float)cloudRes) * 2f) - 1f;
            for (int i = 0; i < cloudVpr; i++)
            {
                cloudVerts[vi++] = ((i / (float)cloudRes) * 2f) - 1f;
                cloudVerts[vi++] = v;
            }
        }
        var cloudIndices = new uint[cloudRes * cloudRes * 6];
        int ii = 0;
        for (int j = 0; j < cloudRes; j++)
        {
            for (int i = 0; i < cloudRes; i++)
            {
                uint a = (uint)((j * cloudVpr) + i);
                uint b = a + 1;
                uint c = a + (uint)cloudVpr;
                uint d = c + 1;
                cloudIndices[ii++] = a; cloudIndices[ii++] = c; cloudIndices[ii++] = b;
                cloudIndices[ii++] = b; cloudIndices[ii++] = c; cloudIndices[ii++] = d;
            }
        }
        cloudIndexCount = cloudIndices.Length;

        cloudVao = g.GenVertexArray();
        g.BindVertexArray(cloudVao);
        cloudVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, cloudVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(cloudVerts.Length * sizeof(float)), cloudVerts, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        cloudIbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, cloudIbo);
        g.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, (nuint)(cloudIndices.Length * sizeof(uint)), cloudIndices, BufferUsageARB.StaticDraw);
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
        lineFogColorLocation = g.GetUniformLocation(lineProgram, "uFogColor");
        lineFogDensityLocation = g.GetUniformLocation(lineProgram, "uFogDensity");
        lineCameraPosLocation = g.GetUniformLocation(lineProgram, "uCameraPos");
        lineMaxDistLocation = g.GetUniformLocation(lineProgram, "uMaxDist");
        lineGhostFadeLocation = g.GetUniformLocation(lineProgram, "uGhostFade");

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

    private void UploadTile(GL g, TerrainMesh3D tile)
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

        uint[] indices = tile.Indices;

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

        // Per-vertex mid-frequency detail amplitude (m RMS): one float at attribute location 4, baked into the
        // VAO so the main terrain draw carries it with no per-tile bind. 0 on the finest/live tiles (no-op shading).
        float[] detail = tile.Detail;
        buffers.DetailVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.DetailVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(detail.Length * sizeof(float)), detail, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(4);
        g.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, sizeof(float), (void*)0);

        buffers.Ebo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ElementArrayBuffer, buffers.Ebo);
        g.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), indices, BufferUsageARB.StaticDraw);

        g.BindVertexArray(0);
        tileBuffers[tile] = buffers;

        // The CPU vertex buffers are now in GPU VBOs and nothing reads them again (this is their only reader),
        // so return the big arrays to the pool — the next tile rebuild rents them instead of churning the LOH.
        tile.ReturnBuffersToPool();
    }

    private void ReleaseTiles(GL g)
    {
        foreach (TileBuffers b in tileBuffers.Values)
        {
            ReleaseTileBuffers(g, b);
        }
        tileBuffers.Clear();
    }

    private static void ReleaseTileBuffers(GL g, TileBuffers b)
    {
        g.DeleteBuffer(b.PositionVbo);
        g.DeleteBuffer(b.ColorVbo);
        g.DeleteBuffer(b.NormalVbo);
        g.DeleteBuffer(b.TexVbo);
        g.DeleteBuffer(b.DetailVbo);
        g.DeleteBuffer(b.Ebo);
        g.DeleteVertexArray(b.Vao);
    }

    // Incremental tile residency. The base tiles are REUSED across detail reloads (same TerrainMesh3D refs;
    // only the look-at detail patch is rebuilt — see MapPageViewModel: `new List(lodBaseTiles); AddRange(...)`).
    // The old guard released + re-uploaded EVERY tile whenever the list reference changed, re-pushing the whole
    // base VBO set on each reload — the visible "detal doładowuje się ~2 s przy ruchu" hitch. Now keep the VBOs
    // for tiles still present (the whole base), release only the gone (previous detail) tiles, upload only new.
    private void SyncTiles(GL g, IReadOnlyList<TerrainMesh3D> tiles)
    {
        var wanted = new HashSet<TerrainMesh3D>(tiles); // TerrainMesh3D is a sealed class → reference identity

        if (tileBuffers.Count > 0)
        {
            List<TerrainMesh3D>? gone = null;
            foreach (TerrainMesh3D existing in tileBuffers.Keys)
            {
                if (!wanted.Contains(existing))
                {
                    (gone ??= new List<TerrainMesh3D>()).Add(existing);
                }
            }

            if (gone is not null)
            {
                foreach (TerrainMesh3D t in gone)
                {
                    ReleaseTileBuffers(g, tileBuffers[t]); // t came from tileBuffers.Keys → always present
                    tileBuffers.Remove(t);
                }
            }
        }

        // Enqueue new (non-resident) tiles; DrainTileUploads pushes a few per frame so a big reload never freezes
        // one frame. Recomputed each swap, so a tile that left the window before it uploaded is simply dropped.
        pendingTileUploads.Clear();
        foreach (TerrainMesh3D t in tiles)
        {
            if (!tileBuffers.ContainsKey(t))
            {
                pendingTileUploads.Add(t);
            }
        }
    }

    // Upload at most TileUploadBudgetPerFrame queued tiles this frame — call every frame, not just on a swap.
    private void DrainTileUploads(GL g)
    {
        int budget = TileUploadBudgetPerFrame;
        while (budget > 0 && pendingTileUploads.Count > 0)
        {
            int last = pendingTileUploads.Count - 1;
            TerrainMesh3D t = pendingTileUploads[last];
            pendingTileUploads.RemoveAt(last);
            if (!tileBuffers.ContainsKey(t))
            {
                UploadTile(g, t);
                budget--;
            }
        }
    }

    // De-duplicates near-parallel duplicate trails ONCE per distinct input set (OSM relation + underlying way),
    // caching the result keyed on the input reference. Returns the same cached instance until `trails` changes, so
    // the deduped reference is stable — safe to use as the ReferenceEquals cache key for the mask / line / route
    // caches without churning a rebuild every frame. The dedup itself is O(total samples) (spatial-hashed), so even
    // the rare recompute on a new trail set stays off the per-frame hot path.
    private IReadOnlyList<Trail>? EnsureDedupedTrails(IReadOnlyList<Trail>? trails)
    {
        if (trails is null || trails.Count == 0)
        {
            dedupInputTrails = trails;
            dedupResultTrails = trails;
            return trails;
        }

        if (ReferenceEquals(dedupInputTrails, trails) && dedupResultTrails is not null)
        {
            return dedupResultTrails;
        }

        dedupInputTrails = trails;
        dedupResultTrails = TrailDeduplicator.Deduplicate(trails);
        return dedupResultTrails;
    }

    // Builds (when needed) the painted-distance trail mask and uploads it to a GL texture. The mask is addressed
    // by ABSOLUTE world-XY in the shader, so it survives detail streaming inside its window — so it is rebuilt ONLY
    // when the trail/road/exposed inputs change OR the (quantized) window moves a whole grid cell, NOT on every
    // detail/mesh swap (that churned a ~48 MB rebuild per stream → GC spiral). Projects the densified world lines
    // (lift = 0, detail = null — the mask ignores Z, so seating it on the detail is wasted work), maps every layer
    // to a colour+priority via TrailMaskInput, rasterises into reused scratch buffers, and uploads RGBA8 (linear,
    // clamped, no mips — trails are thin, mipmapping would dissolve them). The route is NOT in the mask.
    private void EnsureTrailMask(
        GL g,
        IReadOnlyList<Trail>? trails,
        IReadOnlyList<Trail>? roads,
        IReadOnlyList<Trail>? exposed,
        Route? route,
        DemRaster? raster,
        TerrainMesh3D mesh,
        DetailElevationField? detail,
        Vector3 cameraWorldPos)
    {
        // Window first (cheap — pure geometry off detail/mesh). The seated/Z work and the 48 MB raster only run
        // when the key below actually changes, so this early stage is what runs on a churning detail stream.
        bool windowOk = TryComputeMaskWindow(detail, mesh, cameraWorldPos, out float rawMinX, out float rawMinY, out float rawSizeX, out float rawSizeY);

        // Land a finished BACKGROUND build first — the GL thread only pays the two TexImage2D uploads.
        // The projection + SDF paint used to run right here, synchronously: every window jump during a
        // flight was a ~3 s frame stall (measured 20:12–20:23: 37 rebuilds ≙ 43 frame gaps ~3000 ms,
        // "straszliwie zarywa"). The old texture stays live (and correctly positioned — its window
        // uniforms are the old ones) while the replacement builds.
        if (trailMaskBuildTask is { IsCompleted: true } doneTask)
        {
            trailMaskBuildTask = null;
            TrailMaskBuildResult? built = trailMaskBuildResult;
            trailMaskBuildResult = null;
            if (doneTask.IsFaulted)
            {
                Log.Warning(doneTask.Exception?.GetBaseException(), "[GL3D] [TrailMask] background build failed");
            }
            else if (built is not null)
            {
                if (built.Mask is null)
                {
                    // Snapshot had nothing to paint — decal off (same semantics as the old sync early-out).
                    trailMaskValid = false;
                    waterMaskValid = false;
                }
                else
                {
                    UploadTrailMask(g, built);
                    Log.Information(
                        "[GL3D] [TrailMask] rebuilt {W}x{H} lines={Lines} water={WaterLines} falls={Falls} waterTexels={WaterTexels} window=({MinX:F0},{MinY:F0} {SizeX:F0}x{SizeY:F0}m)",
                        built.Mask.Width, built.Mask.Height, built.LineCount, built.WaterLineCount, built.FallCount,
                        built.WaterTexels, built.MinX, built.MinY, built.SizeX, built.SizeY);
                }

                // Commit the window key either way so an empty window does not re-kick every frame.
                lastMaskKeyMinX = built.KeyMinX;
                lastMaskKeyMinY = built.KeyMinY;
                lastMaskKeySizeX = built.KeySizeX;
                lastMaskKeySizeY = built.KeySizeY;
                haveMaskWindowKey = true;
            }
        }

        // The route IS in the decal (dashed translucent violet ON the trail), so a route change must rebuild — but
        // the route reference is stable across detail streams, so this still does NOT churn while streaming. Keyed
        // on the deduped trails / roads / exposed / waterways / route refs + raster + the quantized window.
        bool linesUnchanged = ReferenceEquals(lastMaskTrails, trails)
            && ReferenceEquals(lastMaskRoads, roads)
            && ReferenceEquals(lastMaskExposed, exposed)
            && ReferenceEquals(lastMaskWaterways, Waterways)
            && ReferenceEquals(lastMaskWaterfalls, Waterfalls)
            && ReferenceEquals(lastMaskRoute, route)
            && ReferenceEquals(lastMaskRaster, raster);

        // Quantize the window to a coarse grid so detail streaming (which nudges the window every tile) does NOT
        // move the key. Only a whole-cell jump (or a line-set change) triggers a rebuild. min floors, max ceils →
        // the quantized window always contains the raw window, so no decal edge is cropped.
        float keyMinX = Quantize(rawMinX);
        float keyMinY = Quantize(rawMinY);
        float keySizeX = QuantizeUp(rawMinX + rawSizeX) - keyMinX;
        float keySizeY = QuantizeUp(rawMinY + rawSizeY) - keyMinY;
        bool windowUnchanged = haveMaskWindowKey
            && keyMinX == lastMaskKeyMinX && keyMinY == lastMaskKeyMinY
            && keySizeX == lastMaskKeySizeX && keySizeY == lastMaskKeySizeY;

        if (windowOk && linesUnchanged && windowUnchanged && trailMaskValid)
        {
            return; // nothing relevant changed — keep the cached texture (detail may have streamed; mask is absolute)
        }

        if (trailMaskBuildTask is not null)
        {
            return; // one build in flight; if inputs changed again, the key mismatch re-kicks after it lands
        }

        // These refs define what the build ABOUT TO START represents (not what is on screen) — the early-out
        // above compares against them so the same inputs never kick twice.
        lastMaskTrails = trails;
        lastMaskRoads = roads;
        lastMaskExposed = exposed;
        lastMaskWaterways = Waterways;
        lastMaskWaterfalls = Waterfalls;
        lastMaskRoute = route;
        lastMaskRaster = raster;

        if (raster is null || !windowOk)
        {
            trailMaskValid = false; // data truly gone — decal off (NOT the "still building" case)
            waterMaskValid = false;
            LogMaskSkipThrottled($"raster={(raster is null ? "null" : "ok")} windowOk={windowOk}");
            return;
        }

        if (keySizeX <= 1f || keySizeY <= 1f)
        {
            LogMaskSkipThrottled($"degenerate window {keySizeX:F0}x{keySizeY:F0}m");
            return;
        }

        // Snapshot EVERYTHING the worker needs — it must not read renderer properties (GL-thread state).
        // The snapshots are immutable lists/records; mesh/raster are read-only data.
        var trailsSnap = trails;
        var roadsSnap = roads;
        var exposedSnap = exposed;
        var routeSnap = route;
        var waterSnap = Waterways;
        var fallsSnap = Waterfalls;
        var rasterSnap = raster;
        var meshSnap = mesh;
        float kMinX = keyMinX, kMinY = keyMinY, kSizeX = keySizeX, kSizeY = keySizeY;
        trailMaskBuildTask = Task.Run(() =>
        {
            trailMaskBuildResult = BuildTrailMaskCpu(
                trailsSnap, roadsSnap, exposedSnap, routeSnap, waterSnap, fallsSnap,
                rasterSnap, meshSnap, kMinX, kMinY, kSizeX, kSizeY);
        });
    }

    /// <summary>Everything a landed background mask build hands to the GL thread. Mask null = nothing to paint.</summary>
    private sealed record TrailMaskBuildResult(
        TrailMask? Mask, int LineCount, int WaterLineCount, int FallCount, int WaterTexels,
        float MinX, float MinY, float SizeX, float SizeY,
        float KeyMinX, float KeyMinY, float KeySizeX, float KeySizeY);

    private Task? trailMaskBuildTask;
    private TrailMaskBuildResult? trailMaskBuildResult; // written by the task, read by the GL thread after IsCompleted

    // CPU part of the mask rebuild (projection + rasterisation), safe off the GL thread. The scratch buffers
    // are reused across builds; exclusive use is guaranteed by the single-flight task + the upload happening
    // on the GL thread BEFORE the next task can start (both sequenced through EnsureTrailMask).
    private TrailMaskBuildResult? BuildTrailMaskCpu(
        IReadOnlyList<Trail>? trails,
        IReadOnlyList<Trail>? roads,
        IReadOnlyList<Trail>? exposed,
        Route? route,
        IReadOnlyList<Trail>? waterways,
        IReadOnlyList<MapaTur.Application.Waterways.Waterfall>? waterfalls,
        DemRaster raster,
        TerrainMesh3D mesh,
        float keyMinX, float keyMinY, float keySizeX, float keySizeY)
    {
        // Build the painted lines from the QUANTIZED window so the texture aligns with the cache key (and the
        // window covers the snapped grid cell). detail = null: seating only changes Z, which the mask drops.
        IReadOnlyList<TrailWorldLine>? trailsWorld =
            trails is { Count: > 0 } ? Trail3DWorldProjection.ToWorld(trails, raster, mesh, 0f, detail: null) : null;
        IReadOnlyList<TrailWorldLine>? roadsWorld =
            roads is { Count: > 0 } ? Trail3DWorldProjection.ToWorld(roads, raster, mesh, 0f, detail: null) : null;
        IReadOnlyList<TrailWorldLine>? exposedWorld =
            exposed is { Count: > 0 } ? Trail3DWorldProjection.ToWorld(exposed, raster, mesh, 0f, detail: null) : null;
        IReadOnlyList<TrailWorldLine>? waterWorld =
            waterways is { Count: > 0 } w ? Trail3DWorldProjection.ToWorld(w, raster, mesh, 0f, detail: null) : null;

        IReadOnlyList<MaskPolyline> lines = TrailMaskInput.Build(trailsWorld, roadsWorld, exposedWorld, waterWorld);

        // Water field input: the same watercourse polylines, plus waterfall FOAM accents — a small X of two
        // crossing segments at each waterfall node, painted near-white into the RGBA decal (high priority) and
        // into the water field (so the foam glints hardest). Built in world space off the same mesh frame.
        List<MaskPolyline>? waterFieldLines = null;
        if (waterWorld is { Count: > 0 })
        {
            waterFieldLines = new List<MaskPolyline>(waterWorld.Count);
            foreach (var line in waterWorld)
            {
                (byte wr, byte wg, byte wb) = TrailMaskInput.WaterColor;
                waterFieldLines.Add(new MaskPolyline(line.World, wr, wg, wb, TrailMaskInput.WaterPriority));
            }
        }

        if (waterfalls is { Count: > 0 } falls)
        {
            waterFieldLines ??= new List<MaskPolyline>();
            var allLines = new List<MaskPolyline>(lines);
            (byte fr, byte fg, byte fb) = TrailMaskInput.FoamColor;
            const float foamHalf = 10f; // ~20 m foam splash across the fall — smaller was invisible on bright rock
            foreach (var fall in falls)
            {
                Vector3 c = mesh.GeoToWorld(fall.Position, 0f);
                var segA = new[] { new Vector3(c.X - foamHalf, c.Y - foamHalf, 0f), new Vector3(c.X + foamHalf, c.Y + foamHalf, 0f) };
                var segB = new[] { new Vector3(c.X - foamHalf, c.Y + foamHalf, 0f), new Vector3(c.X + foamHalf, c.Y - foamHalf, 0f) };
                allLines.Add(new MaskPolyline(segA, fr, fg, fb, TrailMaskInput.FoamPriority));
                allLines.Add(new MaskPolyline(segB, fr, fg, fb, TrailMaskInput.FoamPriority));
                waterFieldLines.Add(new MaskPolyline(segA, fr, fg, fb, TrailMaskInput.FoamPriority));
                waterFieldLines.Add(new MaskPolyline(segB, fr, fg, fb, TrailMaskInput.FoamPriority));
            }

            lines = allLines;
        }

        // Route → a MaskRoute the builder paints as the dashed translucent violet ON the trail. Conflate it onto the
        // SAME deduped trails the decal/overlay use (so it shares the trail geometry), Z ignored (lift 0, no detail).
        MaskRoute? maskRoute = null;
        if (route is not null)
        {
            RouteWorldLine routeWorld = Route3DWorldProjection.ToWorld(route, raster, mesh, 0f, detail: null, followTrails: trails);
            if (routeWorld.World.Count >= 2)
            {
                (byte rr, byte rg, byte rb) = TrailMaskInput.RouteColor;
                maskRoute = new MaskRoute(
                    routeWorld.World, rr, rg, rb,
                    RouteDecalDashMeters, RouteDecalGapMeters, RouteDecalBlend, RouteDecalPaintRadiusMeters);
            }
        }

        if (lines.Count == 0 && maskRoute is null)
        {
            return new TrailMaskBuildResult(null, 0, 0, 0, 0, keyMinX, keyMinY, keySizeX, keySizeY, keyMinX, keyMinY, keySizeX, keySizeY);
        }

        // Texture sized to the window aspect (max side = TrailMaskTextureSize) so metres-per-texel match in X/Y.
        int texW, texH;
        if (keySizeX >= keySizeY)
        {
            texW = TrailMaskTextureSize;
            texH = Math.Max(1, (int)MathF.Round(TrailMaskTextureSize * keySizeY / keySizeX));
        }
        else
        {
            texH = TrailMaskTextureSize;
            texW = Math.Max(1, (int)MathF.Round(TrailMaskTextureSize * keySizeX / keySizeY));
        }

        var request = new TrailMaskRequest
        {
            WorldMinX = keyMinX,
            WorldMinY = keyMinY,
            WorldSizeX = keySizeX,
            WorldSizeY = keySizeY,
            Width = texW,
            Height = texH,
            MaxDistanceMeters = TrailMaskMaxDistanceMeters,
            Lines = lines,
            WaterLines = (IReadOnlyList<MaskPolyline>?)waterFieldLines ?? Array.Empty<MaskPolyline>(),
            Route = maskRoute,
        };

        // Reuse the scratch buffers across rebuilds — reallocate only when the texel count changes (no per-rebuild
        // multi-MB churn). At 4096² this is one ~67 MB rgba + ~33 MB priority + ~33 MB distance, allocated ONCE.
        int texels = texW * texH;
        if (maskRgbaScratch is null || maskScratchTexels != texels)
        {
            maskRgbaScratch = new byte[texels * 4];
            maskPriorityScratch = new int[texels];
            maskDistanceScratch = new float[texels];
            maskScratchTexels = texels;
        }

        TrailMask mask = TrailMaskBuilder.Build(request, maskRgbaScratch, maskPriorityScratch!, maskDistanceScratch!);

        int waterTexels = 0;
        if (mask.Water is { } wf)
        {
            for (int i = 0; i < wf.Length; i++)
            {
                if (wf[i] > 0)
                {
                    waterTexels++;
                }
            }
        }

        return new TrailMaskBuildResult(
            mask, lines.Count, waterFieldLines?.Count ?? 0, waterfalls?.Count ?? 0, waterTexels,
            keyMinX, keyMinY, keySizeX, keySizeY, keyMinX, keyMinY, keySizeX, keySizeY);
    }

    // GL-thread half of a landed build: two TexImage2D uploads + window uniforms state. Cheap (~10 ms at 4096²).
    private void UploadTrailMask(GL g, TrailMaskBuildResult built)
    {
        TrailMask mask = built.Mask!;
        if (trailMaskTex == 0)
        {
            trailMaskTex = g.GenTexture();
        }

        g.ActiveTexture(TextureUnit.Texture5);
        g.BindTexture(TextureTarget.Texture2D, trailMaskTex);
        g.TexImage2D<byte>(
            TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
            (uint)mask.Width, (uint)mask.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, mask.Rgba);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

        // Water distance field — a parallel R8 texture on unit 6 (units 0-5 are taken: ortho/refl/CSM×3/trail
        // mask). Drives the shader's wet tint + specular glint independently of the RGBA colour winner.
        waterMaskValid = false;
        if (mask.Water is { } waterField)
        {
            if (waterMaskTex == 0)
            {
                waterMaskTex = g.GenTexture();
            }

            g.ActiveTexture(TextureUnit.Texture6);
            g.BindTexture(TextureTarget.Texture2D, waterMaskTex);
            g.PixelStore(PixelStoreParameter.UnpackAlignment, 1); // tightly-packed single-channel rows
            g.TexImage2D<byte>(
                TextureTarget.Texture2D, 0, (int)InternalFormat.R8,
                (uint)mask.Width, (uint)mask.Height, 0,
                PixelFormat.Red, PixelType.UnsignedByte, waterField);
            g.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            waterMaskValid = true;
        }

        g.ActiveTexture(TextureUnit.Texture0);

        trailMaskMinX = built.MinX;
        trailMaskMinY = built.MinY;
        trailMaskSizeX = built.SizeX;
        trailMaskSizeY = built.SizeY;
        trailMaskValid = true;
    }

    // The mask window in absolute world-XY: the near-field detail window when streaming (fine resolution where
    // occlusion bit hardest), else the whole mesh world extent (so a base-only view still shows the decal). Derived
    // purely from detail/mesh geometry (no lines) so it can be computed BEFORE the cache check decides to rebuild.
    // Half-extent of the camera-centred mask window used whenever DetailElevationField is unavailable (i.e.
    // ALWAYS under baked-tile streaming — the legacy per-tile detail system that populates DetailElevationField
    // never runs while it's active, so `detail` is permanently null there; see MapPageViewModel.OnDetailFocusAsync
    // early-return). Before this fix TryComputeMaskWindow's null-detail branch fell back to the MESH'S WHOLE
    // extent (tens of km for a Tatra-wide load) — stretching the fixed TrailMaskTextureSize (4096 px) SDF over
    // that gives metres-per-texel far too coarse to resolve a trail crisply, which is a real (if different)
    // contributor to "szlak znika/jest kanciasty" alongside the 3D-line elevation-seating bug. A window this size
    // matches the finest baked-detail ring radius (QuadtreeTileSelectorOptions.DefaultFinestRingRadiusMeters =
    // 2500 m) with headroom, giving ≈1.95 m/texel (8000/4096) instead of tens of metres/texel.
    private const float TrailMaskCameraWindowHalfExtentMeters = 4000f;

    private static bool TryComputeMaskWindow(
        DetailElevationField? detail,
        TerrainMesh3D mesh,
        Vector3 cameraWorldPos,
        out float minX, out float minY, out float sizeX, out float sizeY)
    {
        minX = minY = sizeX = sizeY = 0f;

        if (detail is not null)
        {
            DemRaster r = detail.Raster;
            Vector3 sw = mesh.GeoToWorld(new GeoPoint(r.South, r.West), 0f);
            Vector3 ne = mesh.GeoToWorld(new GeoPoint(r.North, r.East), 0f);
            minX = MathF.Min(sw.X, ne.X);
            minY = MathF.Min(sw.Y, ne.Y);
            sizeX = MathF.Abs(ne.X - sw.X);
            sizeY = MathF.Abs(ne.Y - sw.Y);
            return sizeX > 1f && sizeY > 1f;
        }

        // No legacy detail field (the baked-streaming case — see the constant's comment above): a small window
        // CENTRED ON THE CAMERA, not the whole mesh, so the fixed-resolution SDF stays sharp where it's actually
        // being looked at. NO clamp to the mesh extent: under baked streaming `mesh` is tiles[0] — an arbitrary
        // member of a churning tile list, NOT the scene — and clamping to it emptied the window whenever that
        // tile drifted >4 km from the camera, silently killing the whole decal (trails AND water) mid-flight.
        // A window hanging past real geometry is harmless: texels over nothing are simply never sampled.
        float half = TrailMaskCameraWindowHalfExtentMeters;
        minX = cameraWorldPos.X - half;
        minY = cameraWorldPos.Y - half;
        sizeX = half * 2f;
        sizeY = half * 2f;
        return true;
    }

    // Snap helpers for the coarse mask-window grid so detail streaming (which nudges the window every tile) does
    // not churn the rebuild cache key — only a whole-cell jump does. The min-corner floors and the size ceils, so
    // the quantized window always CONTAINS the raw window (max-corner = min + size never crops the detail it covers).
    private static float Quantize(float meters) =>
        MathF.Floor(meters / TrailMaskWindowQuantMeters) * TrailMaskWindowQuantMeters;

    private static float QuantizeUp(float meters) =>
        MathF.Ceiling(meters / TrailMaskWindowQuantMeters) * TrailMaskWindowQuantMeters;

    private void DrawTrailLines(GL g, IReadOnlyList<Trail>? trails, DemRaster? raster, TerrainMesh3D mesh, DetailElevationField? detail)
    {
        if (trails is null || trails.Count == 0 || raster is null)
        {
            return;
        }

        // The detail field is part of the cache key: as the 1 m window streams with the look-at point a new
        // field arrives, and the seated trail heights must be rebuilt against it (else they'd stay on the
        // stale window's surface).
        if (trailLines is null
            || !ReferenceEquals(lastTrails, trails)
            || !ReferenceEquals(lastTrailRaster, raster)
            || !ReferenceEquals(lastTrailMesh, mesh)
            || !ReferenceEquals(lastTrailDetail, detail))
        {
            DeleteLine(g, ref trailLines);
            DeleteLine(g, ref trailLinesBlack);
            IReadOnlyList<TrailWorldLine> world = Trail3DWorldProjection.ToWorld(trails, raster, mesh, TrailLiftMeters, detail, BakedElevationIndex);

            // Black trails go in their own ribbon so they can be drawn thicker (a thin black line is nearly
            // invisible on the dark terrain); every other colour stays on the delicate-thread width.
            var ribbon = new RibbonBuilder();
            var ribbonBlack = new RibbonBuilder();
            foreach (TrailWorldLine line in world)
            {
                (byte r, byte gg, byte b) = PttkRgb(line.Source.PrimaryColor);
                if (line.Source.PrimaryColor == PttkColor.Black)
                {
                    ribbonBlack.Append(line.World, r, gg, b, TrailOverlayAlpha);
                }
                else
                {
                    ribbon.Append(line.World, r, gg, b, TrailOverlayAlpha);
                }
            }

            trailLines = UploadLine(g, ribbon);
            trailLinesBlack = UploadLine(g, ribbonBlack);
            lastTrails = trails;
            lastTrailRaster = raster;
            lastTrailMesh = mesh;
            lastTrailDetail = detail;
        }

        // Alpha-blend so a fragment that loses the near-field z-fight (see TrailOverlayAlpha) reveals the
        // decal's own trail colour underneath instead of bare terrain. Depth-test stays ON (real ridges must
        // still occlude); depth-write off so the translucent ribbon doesn't block cable car / later overlays.
        g.Enable(EnableCap.Blend);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        g.DepthMask(false);
        DrawLine(g, trailLines, TrailHalfWidthPx);
        DrawLine(g, trailLinesBlack, TrailBlackHalfWidthPx);
        g.DepthMask(true);
    }

    private void DrawRoadLines(GL g, IReadOnlyList<Trail>? roads, DemRaster? raster, TerrainMesh3D mesh, DetailElevationField? detail)
    {
        if (roads is null || roads.Count == 0 || raster is null)
        {
            return;
        }

        if (roadLines is null
            || !ReferenceEquals(lastRoads, roads)
            || !ReferenceEquals(lastRoadRaster, raster)
            || !ReferenceEquals(lastRoadMesh, mesh)
            || !ReferenceEquals(lastRoadDetail, detail))
        {
            DeleteLine(g, ref roadLines);
            // Roads are unmarked Trail polylines; reuse the trail world projection, draw them all one grey.
            IReadOnlyList<TrailWorldLine> world = Trail3DWorldProjection.ToWorld(roads, raster, mesh, RoadLiftMeters, detail, BakedElevationIndex);

            var ribbon = new RibbonBuilder();
            foreach (TrailWorldLine line in world)
            {
                ribbon.Append(line.World, RoadR, RoadG, RoadB);
            }

            roadLines = UploadLine(g, ribbon);
            lastRoads = roads;
            lastRoadRaster = raster;
            lastRoadMesh = mesh;
            lastRoadDetail = detail;
        }

        DrawLine(g, roadLines, RoadHalfWidthPx);
    }

    // FAR-FIELD FALLBACK for the route. The route is primarily painted INTO the surface decal now (dashed translucent
    // violet ON the trail — see EnsureTrailMask's route pass), which adheres to the base AND the streamed detail and
    // can't be occluded. That decal covers the whole mesh when base-only, and the near-field detail window while
    // streaming. This dashed translucent line fills the route in BEYOND that window (far field), where it lies on the
    // smooth base and is not occluded; up close it may be hidden by the detail terrain, but there the decal already
    // shows the route — so near and far stay consistent. Same conflation + violet/dash/alpha as before.
    private void DrawRouteLine(GL g, Route? route, IReadOnlyList<Trail>? trails, DemRaster? raster, TerrainMesh3D mesh, DetailElevationField? detail)
    {
        if (route is null || raster is null)
        {
            return;
        }

        if (routeLines is null
            || !ReferenceEquals(lastRoute, route)
            || !ReferenceEquals(lastRouteTrails, trails)
            || !ReferenceEquals(lastRouteRaster, raster)
            || !ReferenceEquals(lastRouteMesh, mesh)
            || !ReferenceEquals(lastRouteDetail, detail))
        {
            DeleteLine(g, ref routeLines);
            // followTrails: re-lay the route onto the SAME polyline as the trail it traverses (conflation), so the
            // line lies ON the trail instead of beside it — matching the decal. Seated/densified on the detail.
            RouteWorldLine world = Route3DWorldProjection.ToWorld(route, raster, mesh, RouteLiftMeters, detail, followTrails: trails, bakedIndex: BakedElevationIndex);

            var ribbon = new RibbonBuilder();
            // DASHED + SEMI-TRANSPARENT: the route is a violet highlight lying ON its trail (conflated onto the
            // trail's polyline), with the trail showing through the dashes and the ~60% alpha. Violet matches the
            // 2D planner. The route is NOT painted into the surface decal — it's only this translucent overlay.
            ribbon.AppendDashed(world.World, 0x7C, 0x3A, 0xED, RouteDashSegments, RouteGapSegments, RouteAlpha);

            routeLines = UploadLine(g, ribbon);
            lastRoute = route;
            lastRouteTrails = trails;
            lastRouteRaster = raster;
            lastRouteMesh = mesh;
            lastRouteDetail = detail;
        }

        // Alpha-blend just the route so the trail beneath shows through (other overlays stay opaque). Depth-test
        // stays on (the terrain still occludes it); depth-write off so the translucent dashes don't block the
        // cable car / later overlays. Restore the opaque state afterwards.
        g.Enable(EnableCap.Blend);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        g.DepthMask(false);
        DrawLine(g, routeLines, RouteHalfWidthPx);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
    }

    private void DrawExposedRoutes(GL g, IReadOnlyList<Trail>? exposed, DemRaster? raster, TerrainMesh3D mesh, DetailElevationField? detail)
    {
        if (exposed is null || exposed.Count == 0 || raster is null)
        {
            return;
        }

        if (exposedLines is null
            || !ReferenceEquals(lastExposed, exposed)
            || !ReferenceEquals(lastExposedRaster, raster)
            || !ReferenceEquals(lastExposedMesh, mesh)
            || !ReferenceEquals(lastExposedDetail, detail))
        {
            DeleteLine(g, ref exposedLines);
            // Exposed routes are Trail polylines (sac_scale / via_ferrata); reuse the trail world projection
            // (which densifies + seats them on the 1 m detail), then draw DASHED so they read as dotted lines.
            IReadOnlyList<TrailWorldLine> world = Trail3DWorldProjection.ToWorld(exposed, raster, mesh, ExposedRouteLiftMeters, detail, BakedElevationIndex);

            var ribbon = new RibbonBuilder();
            foreach (TrailWorldLine line in world)
            {
                // dash=1 / gap=3 over the ~5 m densified segments → ~5 m mark every ~20 m: clearly separated dots
                // sitting on top of (and punctuating) any trail the exposed route runs along.
                ribbon.AppendDashed(line.World, ExposedR, ExposedG, ExposedB, dashSegments: 1, gapSegments: 3);
            }

            exposedLines = UploadLine(g, ribbon);
            lastExposed = exposed;
            lastExposedRaster = raster;
            lastExposedMesh = mesh;
            lastExposedDetail = detail;
        }

        DrawLine(g, exposedLines, ExposedRouteHalfWidthPx);
    }

    private const float CableHalfWidthPx = 2.2f;     // drawn cable ribbon half-width
    private const float CableMastHeightM = 30f;      // station mast height the cable attaches to
    private const float CableSagFraction = 0.03f;    // mid-span droop as a fraction of the span's horizontal length
    private const int CableSegments = 28;            // catenary samples per span
    private const byte CableR = 0x20, CableG = 0x20, CableB = 0x24;       // near-black cable
    private const byte StationR = 0xD0, StationG = 0x40, StationB = 0x30; // red station mast
    private const int CabinsPerSpan = 2;             // gondolas per span (a counterweighted pair: one up, one down)
    private const float CabinSpeed = 0.045f;         // one-way trips per second (≈22 s per ascent — visibly moving)
    private const float CabinHangM = 12f;            // how far the cabin hangs below the cable
    private const float CabinBodyM = 7f;             // half-length of the little horizontal cabin body bar
    private const float CabinHalfWidthPx = 3.0f;     // cabin ribbon half-width (a touch fatter than the cable)
    private const byte CabinR = 0xF5, CabinG = 0xC8, CabinB = 0x20;       // bright yellow gondola

    // Aerialway overlay (e.g. Kasprowy Wierch): sagging cables between station masts, drawn with the same
    // Thin iso-elevation contour lines (warstwice) draped on the relief, seated + lifted like trails and
    // drawn through the same absolute-frame ribbon pipeline. Built by marching squares from the base raster
    // (sub-sampled if large, so generation stays cheap and the curves stay clean), cached until it changes.
    // absolute-frame ribbon pipeline as the trails (the line MVP is already restored to absolute above).
    private void DrawCableCar(GL g, TerrainMesh3D mesh, DemRaster? raster, DetailElevationField? detail)
    {
        if (!ShowCableCar || CableCar is null || CableCar.Stations.Count < 2)
        {
            return;
        }

        float exaggeration = mesh.VerticalExaggeration;
        IReadOnlyList<CableCarStation> st = CableCar.Stations;

        // Seat each station on the ACTUAL rendered terrain (the 1 m detail where it covers the station, else the
        // base DEM) — NOT its hand-authored "approximate" elevation. With the hardcoded value the mast base sat
        // at (elevation − terrain)×exaggeration off the ground, so the masts floated/sank over the valley.
        var top = new float[st.Count]; // world cable-attachment height = ground + mast
        for (int i = 0; i < st.Count; i++)
        {
            top[i] = SeatGroundElevation(st[i], raster, detail) + CableMastHeightM;
        }

        // Cables + masts (static): rebuilt only when the line, mesh, exaggeration or the detail surface changes.
        if (cableLines is null
            || !ReferenceEquals(lastCableCarBuilt, CableCar)
            || !ReferenceEquals(lastCableMesh, mesh)
            || !ReferenceEquals(lastCableDetail, detail)
            || lastCableExaggeration != exaggeration)
        {
            DeleteLine(g, ref cableLines);
            var ribbon = new RibbonBuilder();

            // Cable: each consecutive station pair is a span; it hangs from one mast top to the next with a sag
            // proportional to the span's horizontal length, so it droops over the valley.
            for (int i = 0; i + 1 < st.Count; i++)
            {
                Vector3 lower = mesh.GeoToWorld(st[i].Location, top[i]);
                Vector3 upper = mesh.GeoToWorld(st[i + 1].Location, top[i + 1]);
                float horiz = new Vector2(upper.X - lower.X, upper.Y - lower.Y).Length();
                float sagWorld = horiz * CableSagFraction * exaggeration;
                ribbon.Append(CableCarGeometry.SampleCable(lower, upper, sagWorld, CableSegments), CableR, CableG, CableB);
            }

            // Station masts: a vertical post from the seated terrain up to the cable attachment height.
            for (int i = 0; i < st.Count; i++)
            {
                Vector3 baseW = mesh.GeoToWorld(st[i].Location, top[i] - CableMastHeightM);
                Vector3 topW = mesh.GeoToWorld(st[i].Location, top[i]);
                ribbon.Append(new[] { baseW, topW }, StationR, StationG, StationB);
            }

            cableLines = UploadLine(g, ribbon);
            lastCableCarBuilt = CableCar;
            lastCableMesh = mesh;
            lastCableDetail = detail;
            lastCableExaggeration = exaggeration;
        }

        DrawLine(g, cableLines, CableHalfWidthPx);

        // Moving gondolas (animated, NOT cached — they move every frame): a counterweighted pair per span shuttles
        // along the cable; each hangs a short cabin (a hanger + a little body bar) below the cable at its position.
        double seconds = atmosphereClock.Elapsed.TotalSeconds;
        var cabins = new RibbonBuilder();
        bool any = false;
        for (int i = 0; i + 1 < st.Count; i++)
        {
            Vector3 lower = mesh.GeoToWorld(st[i].Location, top[i]);
            Vector3 upper = mesh.GeoToWorld(st[i + 1].Location, top[i + 1]);
            float horiz = new Vector2(upper.X - lower.X, upper.Y - lower.Y).Length();
            float sagWorld = horiz * CableSagFraction * exaggeration;
            for (int k = 0; k < CabinsPerSpan; k++)
            {
                float t = CableCarGeometry.CabinParameter(seconds, CabinSpeed, k, CabinsPerSpan);
                Vector3 onCable = CableCarGeometry.PointOnSpan(lower, upper, sagWorld, t);
                var bot = new Vector3(onCable.X, onCable.Y, onCable.Z - (CabinHangM * exaggeration));
                float body = CabinBodyM * exaggeration;
                cabins.Append(new[] { onCable, bot }, CabinR, CabinG, CabinB);                                  // hanger
                cabins.Append(new[] { new Vector3(bot.X - body, bot.Y, bot.Z), new Vector3(bot.X + body, bot.Y, bot.Z) }, CabinR, CabinG, CabinB); // body bar
                any = true;
            }
        }

        if (any)
        {
            LineBuffers? cabinBuf = UploadLine(g, cabins);
            DrawLine(g, cabinBuf, CabinHalfWidthPx);
            DeleteLine(g, ref cabinBuf);
        }
    }

    // Terrain elevation under a station: the 1 m detail where it covers the point (matches the drawn surface near
    // the camera), else the base DEM, else the station's hand-authored elevation as a last resort.
    private static float SeatGroundElevation(CableCarStation s, DemRaster? raster, DetailElevationField? detail)
    {
        if (detail is not null && detail.TryGetElevation(s.Location.Longitude, s.Location.Latitude, out double de))
        {
            return (float)de;
        }

        if (raster is not null)
        {
            double v = raster.SampleBilinear(s.Location.Longitude, s.Location.Latitude);
            if (!double.IsNaN(v) && v != raster.NoDataValue)
            {
                return (float)v;
            }
        }

        return (float)s.ElevationMeters;
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

        public void Append(IReadOnlyList<Vector3> world, byte r, byte g, byte b, byte a = 255)
        {
            for (int i = 0; i < world.Count - 1; i++)
            {
                Vector3 p0 = world[i];
                Vector3 c = world[i + 1];
                if (float.IsNaN(p0.X) || float.IsNaN(c.X))
                {
                    continue;
                }

                uint v = (uint)(Positions.Count / 3);
                AddVertex(p0, c, +1f, r, g, b, a);
                AddVertex(p0, c, -1f, r, g, b, a);
                AddVertex(c, p0, -1f, r, g, b, a);
                AddVertex(c, p0, +1f, r, g, b, a);
                Indices.Add(v + 0);
                Indices.Add(v + 1);
                Indices.Add(v + 2);
                Indices.Add(v + 2);
                Indices.Add(v + 1);
                Indices.Add(v + 3);
            }
        }

        /// <summary>
        /// Appends a DASHED ribbon: emits <paramref name="dashSegments"/> consecutive segments, then skips
        /// <paramref name="gapSegments"/>, repeating along the (densified) polyline — so the line reads as a
        /// row of dots/dashes. With ~5 m densification, dash=1/gap=2 gives ~5 m marks every ~15 m.
        /// </summary>
        public void AppendDashed(IReadOnlyList<Vector3> world, byte r, byte g, byte b, int dashSegments, int gapSegments, byte a = 255)
        {
            int period = Math.Max(1, dashSegments + gapSegments);
            for (int i = 0; i < world.Count - 1; i++)
            {
                if (i % period >= dashSegments)
                {
                    continue; // gap
                }

                Vector3 p0 = world[i];
                Vector3 c = world[i + 1];
                if (float.IsNaN(p0.X) || float.IsNaN(c.X))
                {
                    continue;
                }

                uint v = (uint)(Positions.Count / 3);
                AddVertex(p0, c, +1f, r, g, b, a);
                AddVertex(p0, c, -1f, r, g, b, a);
                AddVertex(c, p0, -1f, r, g, b, a);
                AddVertex(c, p0, +1f, r, g, b, a);
                Indices.Add(v + 0);
                Indices.Add(v + 1);
                Indices.Add(v + 2);
                Indices.Add(v + 2);
                Indices.Add(v + 1);
                Indices.Add(v + 3);
            }
        }

        private void AddVertex(Vector3 pos, Vector3 other, float side, byte r, byte g, byte b, byte a)
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
            Colors.Add(a);
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
        // Alpha-to-coverage: the silhouette mask (t.a) and the LOD crossfade (vLodAlpha) both feed the
        // output alpha, which the MSAA stage turns into a smooth coverage mask — soft conifer edges (no
        // alpha-test stair-stepping) AND a dithered-free LOD dissolve. The colour-mask keeps the
        // framebuffer alpha opaque so this never makes the trees translucent in the Skia composite.
        "  float a = t.a * vLodAlpha;\n" +
        "  if (a < 0.02) discard;\n" + // skip fully-transparent fragments (saves depth writes)
        "  float d = length(vToEye);\n" + // distance eye→tree, for aerial-perspective fog
        "  float fog = 1.0 - exp(-d * uFogDensity);\n" +
        "  fragColor = vec4(mix(t.rgb, uFogColor, fog), a);\n" +
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
        // Trilinear minification (mips generated after the bake) so distant impostors don't shimmer/alias;
        // MAX_LEVEL is capped after the bake to keep cells from merging into their neighbours at high mips.
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
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

        // Quality: mipmap + anisotropy the baked atlas so distant impostors don't shimmer. The atlas is a
        // grid, so high mips would merge neighbouring cells — cap MAX_LEVEL so the smallest sampled cell
        // stays ~16 px (atlas 2048 → level 4 = 128 px → 16 px/cell), where cells are still distinct.
        g.BindTexture(TextureTarget.Texture2D, forestAtlasTex);
        g.GenerateMipmap(TextureTarget.Texture2D);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, 4);
        const GLEnum maxAnisoPName = (GLEnum)0x84FF;   // GL_MAX_TEXTURE_MAX_ANISOTROPY_EXT
        const GLEnum anisoPName = (GLEnum)0x84FE;      // GL_TEXTURE_MAX_ANISOTROPY_EXT
        Span<float> maxAniso = stackalloc float[1] { 1f };
        g.GetFloat(maxAnisoPName, maxAniso);
        g.TexParameter(TextureTarget.Texture2D, (TextureParameterName)anisoPName, Math.Clamp(8f, 1f, maxAniso[0] < 1f ? 1f : maxAniso[0]));
        g.BindTexture(TextureTarget.Texture2D, 0);

        // Restore the scene's framebuffer + viewport (and clear colour) so the rest of the frame is normal.
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)prevFbo[0]);
        g.Viewport(prevVp[0], prevVp[1], (uint)prevVp[2], (uint)prevVp[3]);
        g.ClearColor(SkyR, SkyG, SkyB, 1f);
        Log.Information("[GL3D] forest impostor atlas baked: {Grid}×{Grid} views @ {Cell}px (mipmapped)", ForestAtlasGrid, ForestAtlasGrid, ForestAtlasCell);
    }

    // Builds the static per-puff instance buffer ONCE: scattered cumulus clusters (each a handful of puffs)
    // as (offsetX, offsetY, offsetZ, radius, seed), local to the field centre. Fixed seed → a stable cloudscape.
    // Deterministic hash → [0,1) for a scalar (lightning strike timing). fract(sin(x)·k), the classic GLSL hash.
    private static float Hash01(float x)
    {
        double s = Math.Sin((x * 12.9898) + 78.233) * 43758.5453;
        return (float)(s - Math.Floor(s));
    }

    private static float[] BuildCumulusField()
    {
        var rng = new Random(20260613);
        const int clusters = 150;         // dense field so a 100% slider reads as heavy overcast, not scattered puffs
        const float fieldRadius = 16000f; // m around the scene centre
        const float deckSpread = 2800f;   // taller vertical spread so cumulus sit at clearly DIFFERENT heights
        var data = new List<float>(clusters * 9 * 5);
        for (int ci = 0; ci < clusters; ci++)
        {
            float cx = ((float)rng.NextDouble() * 2f - 1f) * fieldRadius;
            float cy = ((float)rng.NextDouble() * 2f - 1f) * fieldRadius;
            float cz = (float)rng.NextDouble() * deckSpread;
            // ~30% of the cumulus are noticeably BIGGER (more, larger puffs) — a varied sky with the odd towering
            // cloud rather than a uniform field of same-size blobs.
            bool big = rng.NextDouble() < 0.30;
            int puffs = big ? 5 + rng.Next(5) : 3 + rng.Next(4);                                          // big: 5..9, normal: 3..6
            float clusterScale = (big ? 680f : 360f) + ((float)rng.NextDouble() * (big ? 950f : 480f));   // big: 680..1630 m, normal: 360..840 m
            for (int p = 0; p < puffs; p++)
            {
                float ox = cx + (((float)rng.NextDouble() * 2f - 1f) * clusterScale * 1.3f);
                float oy = cy + (((float)rng.NextDouble() * 2f - 1f) * clusterScale * 1.3f);
                float oz = cz + ((float)rng.NextDouble() * clusterScale * 0.6f); // puffs pile upward
                float size = clusterScale * (0.6f + ((float)rng.NextDouble() * 0.7f));
                float seed = (float)rng.NextDouble() * 10f;
                data.Add(ox); data.Add(oy); data.Add(oz); data.Add(size); data.Add(seed);
            }
        }
        return data.ToArray();
    }

    private unsafe void EnsureCumulusProgram(GL g)
    {
        if (cumulusProgram != 0)
        {
            return;
        }

        uint vs = CompileShader(g, ShaderType.VertexShader, CumulusVertexShaderSource);
        uint fs = CompileShader(g, ShaderType.FragmentShader, CumulusFragmentShaderSource);
        cumulusProgram = g.CreateProgram();
        g.AttachShader(cumulusProgram, vs);
        g.AttachShader(cumulusProgram, fs);
        g.LinkProgram(cumulusProgram);
        g.GetProgram(cumulusProgram, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = g.GetProgramInfoLog(cumulusProgram);
            throw new InvalidOperationException("Cumulus shader link failed: " + log);
        }
        g.DetachShader(cumulusProgram, vs);
        g.DetachShader(cumulusProgram, fs);
        g.DeleteShader(vs);
        g.DeleteShader(fs);
        cumulusMvpLocation = g.GetUniformLocation(cumulusProgram, "uMvp");
        cumulusCameraPosLocation = g.GetUniformLocation(cumulusProgram, "uCameraPos");
        cumulusFieldCenterLocation = g.GetUniformLocation(cumulusProgram, "uFieldCenter");
        cumulusBaseAltitudeLocation = g.GetUniformLocation(cumulusProgram, "uBaseAltitude");
        cumulusDriftLocation = g.GetUniformLocation(cumulusProgram, "uDrift");
        cumulusSunDirLocation = g.GetUniformLocation(cumulusProgram, "uSunDir");
        cumulusCloudLitLocation = g.GetUniformLocation(cumulusProgram, "uCloudLit");
        cumulusCloudShadowLocation = g.GetUniformLocation(cumulusProgram, "uCloudShadow");
        cumulusFogColorLocation = g.GetUniformLocation(cumulusProgram, "uFogColor");
        cumulusFogDensityLocation = g.GetUniformLocation(cumulusProgram, "uFogDensity");
        cumulusOpacityLocation = g.GetUniformLocation(cumulusProgram, "uOpacity");

        float[] quad = { -1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f }; // triangle strip [-1,1]²
        cumulusVao = g.GenVertexArray();
        g.BindVertexArray(cumulusVao);
        cumulusQuadVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, cumulusQuadVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), quad, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(0); // aCard
        g.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        float[] instances = BuildCumulusField();
        cumulusInstanceCount = instances.Length / 5;
        cumulusInstanceVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, cumulusInstanceVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(instances.Length * sizeof(float)), instances, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(1); // aOffset (x,y,z)
        g.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)0);
        g.VertexAttribDivisor(1, 1);
        g.EnableVertexAttribArray(2); // aSizeSeed
        g.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), (void*)(3 * sizeof(float)));
        g.VertexAttribDivisor(2, 1);
        g.BindVertexArray(0);
    }

    // Draws the scattered cumulus billboards above the terrain. Alpha-blended, depth-tested (foreground peaks
    // occlude clouds behind them), depth-write off. mvp must be the ABSOLUTE scene mvp (puffs are absolute world).
    private void DrawCumulus(GL g, ReadOnlySpan<float> mvp, Camera3D camera, Atmosphere atmosphere,
        Vector2 fieldCenter, float baseAltitude, Vector2 drift, float opacity, int drawCount, Vector3 fogColor,
        float fogDensity, float cloudDark, float lightningFlash)
    {
        // drawCount lets the weather slider thin the field out: only the first N puffs are drawn (the clusters
        // are at random positions, so the tail is a random spatial subset → fewer/more clouds, not "a wedge").
        drawCount = Math.Clamp(drawCount, 0, cumulusInstanceCount);
        if (cumulusProgram == 0 || drawCount == 0 || opacity <= 0.001f)
        {
            return;
        }

        // Lit-top / shaded-base colours from the sun, matching the sky + sea-of-clouds tinting. The storm
        // slider (cloudDark) drives them toward charcoal; a lightning strike (lightningFlash) lights them
        // blue-white for a frame or two so the dark thundercloud flickers.
        float dayness = Math.Clamp(atmosphere.SunDirection.Z + 0.1f, 0f, 1f);
        Vector3 white = new(1.0f, 0.99f, 0.97f);
        var lightning = new Vector3(0.80f, 0.85f, 1.0f) * lightningFlash;
        Vector3 lit = (Vector3.Lerp(atmosphere.SkyHorizonColor * 1.2f, white, dayness) * cloudDark) + lightning;
        Vector3 shadow = (lit * (0.45f + (0.1f * dayness))) + (lightning * 0.7f);
        shadow = new Vector3(shadow.X * 0.92f, shadow.Y * 0.97f, shadow.Z * 1.08f); // cool the underside

        g.Enable(EnableCap.Blend);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        g.DepthMask(false);
        g.UseProgram(cumulusProgram);
        g.UniformMatrix4(cumulusMvpLocation, 1, false, mvp);
        Vector3 cam = camera.Position;
        g.Uniform3(cumulusCameraPosLocation, cam.X, cam.Y, cam.Z);
        g.Uniform2(cumulusFieldCenterLocation, fieldCenter.X, fieldCenter.Y);
        g.Uniform1(cumulusBaseAltitudeLocation, baseAltitude);
        g.Uniform2(cumulusDriftLocation, drift.X, drift.Y);
        Vector3 sun = atmosphere.SunDirection;
        g.Uniform3(cumulusSunDirLocation, sun.X, sun.Y, sun.Z);
        g.Uniform3(cumulusCloudLitLocation, lit.X, lit.Y, lit.Z);
        g.Uniform3(cumulusCloudShadowLocation, shadow.X, shadow.Y, shadow.Z);
        g.Uniform3(cumulusFogColorLocation, fogColor.X, fogColor.Y, fogColor.Z);
        g.Uniform1(cumulusFogDensityLocation, fogDensity);
        g.Uniform1(cumulusOpacityLocation, opacity);
        g.BindVertexArray(cumulusVao);
        g.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, (uint)drawCount);
        g.BindVertexArray(0);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
    }

    // Builds Sauron's tower + glowing eye as a triangle list of [pos.xyz, normal.xyz, colour.rgb, emissive]
    // vertices in LOCAL space (tower base at z=0, +z up). The eye is one emissive orb; the tower a tapered
    // cone with a four-pronged crown. Placed/scaled at draw time via uModelOffset + uVerticalScale.
    private static float[] BuildSauronMesh()
    {
        var v = new List<float>(8192);
        var stone = new Vector3(0.05f, 0.05f, 0.07f);
        var eye = new Vector3(1.0f, 0.50f, 0.10f);

        void Add(Vector3 p, Vector3 n, Vector3 c, float e)
        {
            v.Add(p.X); v.Add(p.Y); v.Add(p.Z);
            v.Add(n.X); v.Add(n.Y); v.Add(n.Z);
            v.Add(c.X); v.Add(c.Y); v.Add(c.Z);
            v.Add(e);
        }
        void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 col, float e)
        {
            Vector3 n = Vector3.Cross(b - a, c - a);
            n = n.LengthSquared() > 1e-9f ? Vector3.Normalize(n) : Vector3.UnitZ;
            Add(a, n, col, e);
            Add(b, n, col, e);
            Add(c, n, col, e);
        }

        // ── A monumental FORTRESS carved out of the WHOLE summit (Barad-dûr): a very wide, deeply-buried,
        // many-tiered, ribbed mass sprawling with asymmetric side-turrets + overhanging galleries, narrowing
        // only near a massive crown that bears the Eye. The mountain IS the base — the lowest tiers sink far
        // into the rock. Seeded RNG fixes the irregular "built & rebuilt over millennia" silhouette.
        const int sides = 22;
        var rng = new Random(31337);

        // Frustum side wall between two radii over a z span, centred at (cx,cy) so turrets reuse it.
        void Wall(float cx, float cy, float rBot, float rTop, float zBot, float zTop, int n)
        {
            for (int s = 0; s < n; s++)
            {
                float a0 = (float)(s * 2.0 * Math.PI / n);
                float a1 = (float)((s + 1) * 2.0 * Math.PI / n);
                var b0 = new Vector3(cx + (rBot * MathF.Cos(a0)), cy + (rBot * MathF.Sin(a0)), zBot);
                var b1 = new Vector3(cx + (rBot * MathF.Cos(a1)), cy + (rBot * MathF.Sin(a1)), zBot);
                var t0 = new Vector3(cx + (rTop * MathF.Cos(a0)), cy + (rTop * MathF.Sin(a0)), zTop);
                var t1 = new Vector3(cx + (rTop * MathF.Cos(a1)), cy + (rTop * MathF.Sin(a1)), zTop);
                Tri(b0, b1, t1, stone, 0f);
                Tri(b0, t1, t0, stone, 0f);
            }
        }

        // A horizontal terrace/gallery ring (can OVERHANG the tier above for a balcony lip).
        void Ledge(float rInner, float rOuter, float z)
        {
            for (int s = 0; s < sides; s++)
            {
                float a0 = (float)(s * 2.0 * Math.PI / sides);
                float a1 = (float)((s + 1) * 2.0 * Math.PI / sides);
                var i0 = new Vector3(rInner * MathF.Cos(a0), rInner * MathF.Sin(a0), z);
                var i1 = new Vector3(rInner * MathF.Cos(a1), rInner * MathF.Sin(a1), z);
                var o0 = new Vector3(rOuter * MathF.Cos(a0), rOuter * MathF.Sin(a0), z);
                var o1 = new Vector3(rOuter * MathF.Cos(a1), rOuter * MathF.Sin(a1), z);
                Tri(i0, o0, o1, stone, 0f);
                Tri(i0, o1, i1, stone, 0f);
            }
        }

        // A vertical buttress rib protruding from a tier face (the gothic vertical-rib texture).
        void Rib(float angle, float r, float zBot, float zTop, float protrude, float halfWidth)
        {
            var tang = new Vector3(-MathF.Sin(angle), MathF.Cos(angle), 0f) * halfWidth;
            var outward = new Vector3(MathF.Cos(angle), MathF.Sin(angle), 0f);
            var fBot = new Vector3(r * MathF.Cos(angle), r * MathF.Sin(angle), zBot);
            var fTop = new Vector3(r * MathF.Cos(angle), r * MathF.Sin(angle), zTop);
            var oBot = fBot + (outward * protrude);
            var oTop = fTop + (outward * protrude);
            Tri(oBot - tang, oBot + tang, oTop + tang, stone, 0f); // outer face
            Tri(oBot - tang, oTop + tang, oTop - tang, stone, 0f);
            Tri(fBot + tang, oBot + tang, oTop + tang, stone, 0f); // side cheeks
            Tri(fBot + tang, oTop + tang, fTop + tang, stone, 0f);
            Tri(fBot - tang, oTop - tang, oBot - tang, stone, 0f);
            Tri(fBot - tang, fTop - tang, oTop - tang, stone, 0f);
        }

        // A forked horn angling out + up from (cx,cy,atZ), pointing along `dir`.
        void Prong(float cx, float cy, float atZ, float baseR2, float dir, float reach, float rise, float halfWidth)
        {
            var rim = new Vector3(cx + (baseR2 * MathF.Cos(dir)), cy + (baseR2 * MathF.Sin(dir)), atZ);
            var tip = new Vector3(cx + ((baseR2 + reach) * MathF.Cos(dir)), cy + ((baseR2 + reach) * MathF.Sin(dir)), atZ + rise);
            var tangent = new Vector3(-MathF.Sin(dir), MathF.Cos(dir), 0f) * halfWidth;
            var p0 = rim + tangent;
            var p1 = rim - tangent;
            Tri(p0, p1, tip, stone, 0f);
            Tri(p1, p0, tip, stone, 0f); // both windings → visible from either side
        }

        // Stacked tiers — a slender TOWER roughly the Eye's width (a gentle taper, NOT a pyramid). The lowest
        // tier is buried in Świnica. { rBottom, rTop, zBottom, zTop }.
        float[][] tiers =
        {
            new[] { 55f, 52f, -70f, 30f },
            new[] { 49f, 47f, 30f, 110f },
            new[] { 44f, 42f, 110f, 190f },
            new[] { 41f, 38f, 190f, 270f },
            new[] { 37f, 35f, 270f, 345f }, // crown level
        };
        for (int t = 0; t < tiers.Length; t++)
        {
            float rBot = tiers[t][0], rTop = tiers[t][1], zBot = tiers[t][2], zTop = tiers[t][3];
            Wall(0f, 0f, rBot, rTop, zBot, zTop, sides);
            if (t + 1 < tiers.Length)
            {
                Ledge(tiers[t + 1][0], rTop + 4f, zTop); // a slim gallery lip
            }
            if (t is >= 1 and <= 3)
            {
                const int ribs = 10;
                float rAvg = (rBot + rTop) * 0.5f;
                for (int rIdx = 0; rIdx < ribs; rIdx++)
                {
                    Rib((float)(rIdx * 2.0 * Math.PI / ribs), rAvg, zBot, zTop, 3f, 2.2f);
                }
            }
        }

        // A few small asymmetric side-turrets for the "built over millennia" detail, kept slim so the silhouette
        // stays a tower. Seeded → fixed but irregular.
        for (int k = 0; k < 5; k++)
        {
            int ti = 1 + rng.Next(3);
            float attachR = tiers[ti][1];
            float ang = (float)(rng.NextDouble() * 2.0 * Math.PI);
            float tcx = attachR * 0.9f * MathF.Cos(ang);
            float tcy = attachR * 0.9f * MathF.Sin(ang);
            float tBaseR = 5f + (rng.NextSingle() * 6f);
            float tTop = tiers[ti][3] + 30f + (rng.NextSingle() * 70f);
            Wall(tcx, tcy, tBaseR, tBaseR * 0.5f, tiers[ti][2] - 10f, tTop, 8);
            Prong(tcx, tcy, tTop, tBaseR * 0.5f, ang, 7f, 18f, 2.5f);
        }

        // Two tall VERTICAL spires flanking the Eye — the Eye sits BETWEEN them — running straight down the
        // sides (perpendicular), capped to a point above the Eye.
        const float spireR = 7f, spireBot = 150f, spireTop = 432f, spireCapZ = 474f;
        for (int side = -1; side <= 1; side += 2)
        {
            float fx = side * 43f; // just beyond the Eye's edge so the Eye is framed between them
            Wall(fx, 0f, spireR + 2f, spireR, spireBot, spireTop, 9);
            for (int s = 0; s < 9; s++)
            {
                float a0 = (float)(s * 2.0 * Math.PI / 9);
                float a1 = (float)((s + 1) * 2.0 * Math.PI / 9);
                var b0 = new Vector3(fx + (spireR * MathF.Cos(a0)), spireR * MathF.Sin(a0), spireTop);
                var b1 = new Vector3(fx + (spireR * MathF.Cos(a1)), spireR * MathF.Sin(a1), spireTop);
                Tri(b0, b1, new Vector3(fx, 0f, spireCapZ), stone, 0f); // pointed cap
            }
        }

        // Eye of Sauron: a single camera-facing billboard quad. aPos.xy carries the card [-1,1]; the vertex
        // shader expands it to face the camera around the tower-top centre, the fragment paints the eye. MUST
        // be the LAST 6 vertices — DrawSauron draws the tower opaque, then this quad additively.
        Span<Vector2> card = stackalloc Vector2[6]
        {
            new(-1f, -1f), new(1f, -1f), new(1f, 1f),
            new(-1f, -1f), new(1f, 1f), new(-1f, 1f),
        };
        foreach (Vector2 cc in card)
        {
            Add(new Vector3(cc.X, cc.Y, 0f), Vector3.UnitZ, eye, 1f);
        }
        return v.ToArray();
    }

    private unsafe void EnsureSauronProgram(GL g)
    {
        if (sauronProgram != 0)
        {
            return;
        }

        uint vs = CompileShader(g, ShaderType.VertexShader, SauronVertexShaderSource);
        uint fs = CompileShader(g, ShaderType.FragmentShader, SauronFragmentShaderSource);
        sauronProgram = g.CreateProgram();
        g.AttachShader(sauronProgram, vs);
        g.AttachShader(sauronProgram, fs);
        g.LinkProgram(sauronProgram);
        g.GetProgram(sauronProgram, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = g.GetProgramInfoLog(sauronProgram);
            throw new InvalidOperationException("Sauron shader link failed: " + log);
        }
        g.DetachShader(sauronProgram, vs);
        g.DetachShader(sauronProgram, fs);
        g.DeleteShader(vs);
        g.DeleteShader(fs);
        sauronViewProjLocation = g.GetUniformLocation(sauronProgram, "uViewProj");
        sauronModelOffsetLocation = g.GetUniformLocation(sauronProgram, "uModelOffset");
        sauronVerticalScaleLocation = g.GetUniformLocation(sauronProgram, "uVerticalScale");
        sauronSunDirLocation = g.GetUniformLocation(sauronProgram, "uSunDir");
        sauronSunColorLocation = g.GetUniformLocation(sauronProgram, "uSunColor");
        sauronAmbientLocation = g.GetUniformLocation(sauronProgram, "uAmbient");
        sauronCameraPosLocation = g.GetUniformLocation(sauronProgram, "uCameraPos");
        sauronFogColorLocation = g.GetUniformLocation(sauronProgram, "uFogColor");
        sauronFogDensityLocation = g.GetUniformLocation(sauronProgram, "uFogDensity");
        sauronEyePulseLocation = g.GetUniformLocation(sauronProgram, "uEyePulse");
        sauronTimeLocation = g.GetUniformLocation(sauronProgram, "uTime");

        float[] mesh = BuildSauronMesh();
        sauronVertexCount = mesh.Length / 10;
        sauronTowerVertexCount = sauronVertexCount - 6; // last 6 verts are the eye billboard quad
        sauronVao = g.GenVertexArray();
        g.BindVertexArray(sauronVao);
        sauronVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, sauronVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(mesh.Length * sizeof(float)), mesh, BufferUsageARB.StaticDraw);
        const int stride = 10 * sizeof(float);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        g.EnableVertexAttribArray(2);
        g.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        g.EnableVertexAttribArray(3);
        g.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)(9 * sizeof(float)));
        g.BindVertexArray(0);
    }

    // Draws Sauron's tower at baseWorld (Świnica). Depth-tested so ridges occlude the lower tower; the eye is
    // emissive + unfogged so the bloom pass turns it into a small glowing sun.
    private void DrawSauron(GL g, ReadOnlySpan<float> viewProj, Camera3D camera, Atmosphere atmosphere,
        Vector3 baseWorld, float verticalScale, float weatherT, Vector3 fogColor, float fogDensity)
    {
        if (sauronProgram == 0 || sauronVertexCount == 0)
        {
            return;
        }

        g.Enable(EnableCap.DepthTest);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
        g.UseProgram(sauronProgram);
        g.UniformMatrix4(sauronViewProjLocation, 1, false, viewProj);
        g.Uniform3(sauronModelOffsetLocation, baseWorld.X, baseWorld.Y, baseWorld.Z);
        g.Uniform1(sauronVerticalScaleLocation, verticalScale);
        Vector3 sun = atmosphere.SunDirection;
        g.Uniform3(sauronSunDirLocation, sun.X, sun.Y, sun.Z);
        Vector3 sc = atmosphere.SunColor;
        g.Uniform3(sauronSunColorLocation, sc.X, sc.Y, sc.Z);
        g.Uniform1(sauronAmbientLocation, Math.Clamp(atmosphere.AmbientFactor + 0.15f, 0.2f, 1f));
        Vector3 cam = camera.Position;
        g.Uniform3(sauronCameraPosLocation, cam.X, cam.Y, cam.Z);
        g.Uniform3(sauronFogColorLocation, fogColor.X, fogColor.Y, fogColor.Z);
        g.Uniform1(sauronFogDensityLocation, fogDensity);
        g.Uniform1(sauronTimeLocation, weatherT);
        // The eye flickers like a flame and never drops below bright, so it always blazes.
        float pulse = 1.4f + (0.35f * MathF.Sin(weatherT * 2.2f)) + (0.15f * MathF.Sin(weatherT * 7.3f));
        g.Uniform1(sauronEyePulseLocation, pulse);
        g.BindVertexArray(sauronVao);
        // Tower: opaque, depth-written (set up by the caller above).
        g.DrawArrays(PrimitiveType.Triangles, 0, (uint)sauronTowerVertexCount);
        // Eye: ADDITIVE so it blazes and haloes regardless of the bloom pass; depth-tested (ridges/tower
        // occlude it) but no depth write so it doesn't carve a hole.
        g.Enable(EnableCap.Blend);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
        g.DepthMask(false);
        g.DrawArrays(PrimitiveType.Triangles, sauronTowerVertexCount, 6u);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
        g.BindVertexArray(0);
    }

    private unsafe void EnsureEagleProgram(GL g)
    {
        if (eagleProgram != 0)
        {
            return;
        }

        uint vs = CompileShader(g, ShaderType.VertexShader, EagleVertexShaderSource);
        uint fs = CompileShader(g, ShaderType.FragmentShader, EagleFragmentShaderSource);
        eagleProgram = g.CreateProgram();
        g.AttachShader(eagleProgram, vs);
        g.AttachShader(eagleProgram, fs);
        g.LinkProgram(eagleProgram);
        g.GetProgram(eagleProgram, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = g.GetProgramInfoLog(eagleProgram);
            throw new InvalidOperationException("Eagle shader link failed: " + log);
        }
        g.DetachShader(eagleProgram, vs);
        g.DetachShader(eagleProgram, fs);
        g.DeleteShader(vs);
        g.DeleteShader(fs);
        eagleViewProjLocation = g.GetUniformLocation(eagleProgram, "uViewProj");
        eagleCameraPosLocation = g.GetUniformLocation(eagleProgram, "uCameraPos");
        eagleTimeLocation = g.GetUniformLocation(eagleProgram, "uTime");
        eagleColorLocation = g.GetUniformLocation(eagleProgram, "uEagleColor");

        float[] quad = { -1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f }; // triangle strip [-1,1]²
        eagleVao = g.GenVertexArray();
        g.BindVertexArray(eagleVao);
        eagleQuadVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, eagleQuadVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), quad, BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);

        eagleInstanceVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, eagleInstanceVbo);
        const int stride = 8 * sizeof(float);
        g.EnableVertexAttribArray(1); // aOrbit (cx,cy,cz,radius)
        g.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, (void*)0);
        g.VertexAttribDivisor(1, 1);
        g.EnableVertexAttribArray(2); // aMotion (phase,speed,size,flapPhase)
        g.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, (void*)(4 * sizeof(float)));
        g.VertexAttribDivisor(2, 1);
        g.BindVertexArray(0);
    }

    // Draws eagles thermalling over the Orla Perć ridge. Instances are rebuilt each frame (the orbit centres are
    // geo points projected into the current world frame); the vertex shader does the circling + camera-facing.
    private unsafe void DrawEagles(GL g, ReadOnlySpan<float> viewProj, Camera3D camera, TerrainMesh3D frame, DemRaster raster, float timeSeconds)
    {
        if (eagleProgram == 0)
        {
            return;
        }

        var data = new List<float>(EagleOrbitCenters.Length * 3 * 8);
        int idx = 0;
        foreach (GeoPoint c in EagleOrbitCenters)
        {
            float ground = (float)raster.SampleBilinear(c.Longitude, c.Latitude);
            for (int e = 0; e < 3; e++)
            {
                float radius = 130f + (e * 55f) + ((idx % 2) * 35f);
                float alt = ground + 110f + (e * 40f); // soaring well above the crest
                Vector3 w = frame.GeoToWorld(c, alt);
                float phase = idx * 1.7f;
                float speed = 0.05f + (0.018f * (idx % 3)); // rad/s — a slow thermal circle
                float size = 20f + ((idx % 3) * 4f);
                float flapPhase = idx * 2.3f;
                data.Add(w.X); data.Add(w.Y); data.Add(w.Z); data.Add(radius);
                data.Add(phase); data.Add(speed); data.Add(size); data.Add(flapPhase);
                idx++;
            }
        }

        eagleInstanceCount = idx;
        float[] arr = data.ToArray();

        g.Enable(EnableCap.Blend);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        g.Enable(EnableCap.DepthTest);
        g.DepthMask(false); // soft silhouettes: test against terrain but don't write depth
        g.UseProgram(eagleProgram);
        g.BindVertexArray(eagleVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, eagleInstanceVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(arr.Length * sizeof(float)), arr, BufferUsageARB.DynamicDraw);
        g.UniformMatrix4(eagleViewProjLocation, 1, false, viewProj);
        Vector3 cam = camera.Position;
        g.Uniform3(eagleCameraPosLocation, cam.X, cam.Y, cam.Z);
        g.Uniform1(eagleTimeLocation, timeSeconds);
        g.Uniform3(eagleColorLocation, 0.05f, 0.045f, 0.04f);
        g.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, (uint)eagleInstanceCount);
        g.BindVertexArray(0);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
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

        // Alpha-to-coverage smooths the alpha-tested silhouette edges (and the LOD fade) via MSAA. Keep the
        // framebuffer alpha channel masked off so the partial fragment alpha doesn't make trees translucent
        // when Skia composites the present texture.
        g.Enable(EnableCap.SampleAlphaToCoverage);
        g.ColorMask(true, true, true, false);
        g.BindVertexArray(forestImpostorVao);
        g.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, (uint)forestInstanceCount);
        g.BindVertexArray(0);
        g.ColorMask(true, true, true, true);
        g.Disable(EnableCap.SampleAlphaToCoverage);
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
        DeleteLine(gl, ref trailLinesBlack);
        DeleteLine(gl, ref routeLines);
        DeleteLine(gl, ref roadLines);
        DeleteLine(gl, ref exposedLines);
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
        gl.DeleteFramebuffer(postFbo);
        gl.DeleteTexture(postColorTex);
        postFbo = 0;
        postColorTex = 0;
        gl.DeleteFramebuffer(bloomBrightFbo);
        gl.DeleteTexture(bloomBrightTex);
        gl.DeleteFramebuffer(bloomFboA);
        gl.DeleteTexture(bloomTexA);
        gl.DeleteFramebuffer(bloomFboB);
        gl.DeleteTexture(bloomTexB);
        gl.DeleteFramebuffer(godrayFbo);
        gl.DeleteTexture(godrayTex);
        bloomBrightFbo = bloomBrightTex = bloomFboA = bloomTexA = bloomFboB = bloomTexB = godrayFbo = godrayTex = 0;
        for (int i = 0; i < ShadowCascadeCount; i++)
        {
            gl.DeleteFramebuffer(shadowFbos[i]);
            gl.DeleteTexture(shadowDepthTex[i]);
            shadowFbos[i] = 0;
            shadowDepthTex[i] = 0;
        }

        if (gpuQueries is not null)
        {
            fixed (uint* p = gpuQueries)
            {
                gl.DeleteQueries((uint)gpuQueries.Length, p);
            }

            gpuQueries = null;
        }
        shadowMapsAllocated = false;
        gl.DeleteFramebuffer(reflectionFbo);
        gl.DeleteTexture(reflectionColorTex);
        gl.DeleteRenderbuffer(reflectionDepthRb);
        reflectionFbo = 0;
        reflectionColorTex = 0;
        reflectionDepthRb = 0;
        reflectionTexW = 0;
        reflectionTexH = 0;
        gl.DeleteTexture(trailMaskTex);
        trailMaskTex = 0;
        trailMaskValid = false;
        gl.DeleteTexture(waterMaskTex);
        waterMaskTex = 0;
        waterMaskValid = false;
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
            gl.DeleteProgram(postProgram);
            gl.DeleteProgram(bloomBrightProgram);
            gl.DeleteProgram(bloomBlurProgram);
            gl.DeleteProgram(bloomCompositeProgram);
            gl.DeleteProgram(godrayProgram);
            gl.DeleteProgram(shadowDepthProgram);
            gl.DeleteVertexArray(skyVao);
            gl.DeleteBuffer(skyVbo);
            gl.DeleteProgram(starProgram);
            gl.DeleteVertexArray(starVao);
            gl.DeleteBuffer(starVbo);
            gl.DeleteProgram(moonProgram);
            gl.DeleteProgram(cloudProgram);
            gl.DeleteVertexArray(cloudVao);
            gl.DeleteBuffer(cloudVbo);
            gl.DeleteBuffer(cloudIbo);
            gl.DeleteProgram(cumulusProgram);
            gl.DeleteVertexArray(cumulusVao);
            gl.DeleteBuffer(cumulusQuadVbo);
            gl.DeleteBuffer(cumulusInstanceVbo);
            skyProgram = 0;
            postProgram = 0;
            postTexLocation = -1;
            bloomBrightProgram = 0;
            bloomBlurProgram = 0;
            bloomCompositeProgram = 0;
            godrayProgram = 0;
            shadowDepthProgram = 0;
            skyVao = 0;
            skyVbo = 0;
            starProgram = 0;
            starVao = 0;
            starVbo = 0;
            starCount = 0;
            starBufferReady = false;
            moonProgram = 0;
            cloudProgram = 0;
            cloudVao = 0;
            cloudVbo = 0;
            cloudIbo = 0;
            cumulusProgram = 0;
            cumulusInstanceCount = 0;
            sauronProgram = 0;
            sauronVertexCount = 0;
            sauronUnsupported = false;
            eagleProgram = 0;
            eagleInstanceCount = 0;
            eagleUnsupported = false;
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