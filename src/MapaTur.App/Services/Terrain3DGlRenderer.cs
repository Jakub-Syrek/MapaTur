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
    // Kept in its own renderer so the photogrammetric path adds only narrow integration points here. This
    // materially reduces merge conflicts with the parallel ortho-streaming work in this already-large class.
    private readonly PhotogrammetricRockGlLayer photogrammetricRock = new();

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
                                         // Dragon-fire dynamic lights (B2): ≤8 point lights reduced CPU-side from the fire sprites. Positions
                                         // are ABSOLUTE world with exaggerated Z (the sprites' frame). uFireCount is a FLOAT on purpose —
                                         // Uniform1(loc, int) against a GLSL float is a silent no-op on this stack (see silknet lesson).
        "uniform float uFireCount;\n" +
        "uniform vec3 uFirePos[8];\n" +
        "uniform vec3 uFireColor[8];\n" +  // colour × intensity × flicker, premultiplied
        "uniform float uFireInvR2[8];\n" + // 1/(3R)² per light
                                           // B4 scorch splats (fireball ground hits): xy = world XY, param.x = radius², param.y = strength.
        "uniform float uScorchCount;\n" +
        "uniform vec2 uScorchPos[24];\n" +
        "uniform vec2 uScorchParam[24];\n" +
        "uniform vec3 uSkyAmbient;\n" +  // ambient sky-fill colour for shadowed slopes
        "uniform sampler2D uOrtho;\n" +
        "uniform int uUseOrtho;\n" +
        "uniform float uOrthoGlobalFade;\n" +  // 1 = full ortho, 0 = hypsometric ("2D map" mode fade)
        "uniform vec2 uOrthoTexel;\n" + // (1/width, 1/height) of the bound ortho texture
        "uniform vec2 uOrthoMinXY;\n" +     // ortho coverage AABB (world XY about the scene anchor) — beyond it the UV clamps
        "uniform vec2 uOrthoMaxXY;\n" +
        "uniform float uOrthoBlendMeters;\n" + // soft fade ortho→hypsometric at the coverage edge; 0 = no cull (pure ortho)
                                               // Hi-res ortho DETAIL overlay (PoC, docs/PLAN-ortho-highres-poc.md): up to two extra resident textures
                                               // (det25 ~0.25 m, det05 ~0.05 m) drape over the base ortho colour INSIDE their world-space AABB, finest
                                               // last (det05 wins where both cover). Sampled in the STABLE frame (vStableWorldPos.xy), same as the base
                                               // ortho coverage test, so the overlay stays pinned when the camera tilts (§C.1). uUseDet* fold in the
                                               // PoC master flag on the CPU side, so 0 = strict no-op (base ortho unchanged).
        "uniform sampler2D uOrthoDet25;\n" +
        "uniform sampler2D uOrthoDet05;\n" +
        "uniform int uUseDet25;\n" +
        "uniform int uUseDet05;\n" +
        "uniform vec2 uDet25MinXY;\n" +
        "uniform vec2 uDet25MaxXY;\n" +
        "uniform vec2 uDet05MinXY;\n" +
        "uniform vec2 uDet05MaxXY;\n" +
        // Streamed det05 as a TEXTURE ARRAY (2026-07-20, "10% hires"): the old per-draw single-cell bind
        // meant a terrain tile straddling 2×2 det05 cells showed 5 cm only on its intersection with the
        // ONE chosen cell — 9 resident cells, 1 rendered per tile. The array holds EVERY resident cell as
        // a layer; the fragment picks its best-centred containing cell from the AABB list, so all resident
        // detail paints everywhere it exists. Slot i = layer i; min>max marks an empty slot.
        // TWO slices (units 12 + 13): one 12-layer 8192² array with mips is ≈4.295 GB — past the 32-bit
        // per-resource ceiling that silently killed the context on 07-20. Slice A carries layers 0..7,
        // slice B the rest; the fragment picks the slice from its slot index (best < 8).
        "uniform mediump sampler2DArray uOrthoDet05Arr;\n" +
        "uniform mediump sampler2DArray uOrthoDet05ArrB;\n" +
        // O(1) cell→slot lookup. 384 entries at 50% load replace 192 AABBs + 192 scalar alphas, so the
        // fragment-uniform footprint does not grow while the hot fragment path stops scanning all 192 cells.
        "uniform ivec4 uDet05CellHash[384];\n" + // (ci,cj,arraySlot,minLod<<8|alphaByte), empty slot has arraySlot=-1
        "uniform int uDet05HashSeed;\n" +
        "uniform vec2 uDet05GridMinXmaxY;\n" + // NW origin in stable world metres
        "uniform vec2 uDet05GridPitch;\n" +    // positive cell-origin step: east, south
        "uniform vec2 uDet05CellSize;\n" +     // positive coverage size: east, south
        "uniform mediump sampler2DArray uOrthoDet05ArrC;\n" + // trzecia tablica (unit 7) — 3×64 = 192 cele
        "uniform int uDet05ArrLayers;\n" + // warstw NA TABLICĘ; slot→(tablica, warstwa) = (slot/L, slot%L)
        "uniform int uUseDet05Arr;\n" +
        "uniform float uDetailBlendMeters;\n" + // soft edge fade of the detail AABB back to the base ortho
        "uniform int uOrthoDetailColorMode;\n" + // 0 = raw detail, 1 = base de-blue transform (R3 slice A/B)
        "uniform int uToneHarm;\n" +  // 1 = harmonizacja tonu (krok 2 prawa) czynna; 0 = SAMO de-blue (diagnostyka MAPATUR_ORTHO_TONE=0)
        "uniform int uToneDebug;\n" + // 1 = zamiast koloru rysuj MAPĘ korekty tonu (MAPATUR_ORTHO_TONE_DEBUG=1)
                                      // H3 (2026-07-23): per-layer colour split for the deshadow preview. The STREAMED det05 cells can carry
                                      // data-side-corrected (V2) tiles that must render RAW (a second shader de-blue = double correction),
                                      // while det25/base/mosaic still need the mode-1 de-blue. 1 = det05 ARRAY skips the mode-1 transform.
        "uniform int uOrthoDet05ArrRaw;\n" +
        // det1m (krok 3): rezydentna warstwa 1 m/px między det25 a bazą — array 4096² BC1 (unit 14) +
        // maska pokrycia R8 (unit 15, filtrowana liniowo = miękki brzeg 512 m). Dobór slice'a O(1) z
        // regularnej siatki (bez pętli po AABB). uUseDet1m = wyłącznie A/B — dane zostają rezydentne.
        "uniform highp sampler2DArray uOrthoDet1m;\n" + // GLES: sampler2DArray nie ma domyślnej precyzji
        "uniform highp sampler2D uOrthoDet1mCov;\n" +
        // det25 ARRAY (krok 4): per-fragment wybór celi jak det05 — koniec patchworku per-tile bind.
        "uniform highp sampler2DArray uOrthoDet25Arr;\n" +
        "uniform ivec4 uDet25CellHash[256];\n" + // 128 cells at 50% load; same bounded lookup as det05
        "uniform int uDet25HashSeed;\n" +
        "uniform vec2 uDet25GridMinXmaxY;\n" +
        "uniform vec2 uDet25GridPitch;\n" +
        "uniform vec2 uDet25CellSize;\n" +
        "uniform int uUseDet25Arr;\n" +
        "uniform int uUseDet1m;\n" +
        "uniform vec2 uDet1mMinXmaxY;\n" +   // (minX świata, maxY świata) — v rośnie na południe jak wiersze tekstury
        "uniform vec2 uDet1mInvSize;\n" +
        "uniform ivec2 uDet1mGridDim;\n" +
        "uniform int uDet1mSliceIdx[160];\n" +
        "uniform int uDet1mDebug;\n" + // 1 = klasyfikacja danych det1m (env MAPATUR_DET1M_DEBUG): czerwony=opaque black, zolty=a0, magenta=brak slice'a
        "uniform int uOrthoDetailDebugBounds;\n" + // 1 = outline detail cell AABB edges (diagnostics)
        "uniform vec2 uDet25EyeXY;\n" +      // world-XY of the streaming focus (ring centre) for the det25 range fade
        "uniform float uDet25FadeInner;\n" + // det25 at full strength within this metric radius of the focus
        "uniform float uDet25FadeOuter;\n" + // det25 faded to base by this radius — smooths the hard ring-frontier pop
        "uniform float uSlopeMode;\n" +     // 1 = avalanche slope-steepness map (overrides ortho/hypsometric)
        "uniform vec3 uSlopePalette[8];\n" + // band colours (0-20…80-90°), from SlopePalette
        "uniform float uSharpen;\n" +   // unsharp-mask strength; 0 = off
        "uniform float uDebugUv;\n" +   // DIAGNOSTIC: 1 = render the raw ortho UV as colour (R=U, G=V)
        "uniform float uDebugTerrainView;\n" +   // DIAGNOSTIC: 0=final 1=albedo 2=baked-shadow mask 3=corrected albedo 4=lightSum
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
        "uniform float uShadowTexel;\n" + // 1/ShadowMapSize — keeps the PCF radius true at any map size
        "uniform float uAoStrength;\n" +  // curvature-AO multiplier strength (0 = off)
        "uniform float uBakedShadowComp;\n" +  // baked-shadow (dark ortho in shade) de-cyan + lift strength (0 = off)
                                               // Cloud-shadow inputs: the SAME field the sea-of-clouds layer draws, so the shadows on the
                                               // ground line up with the clouds overhead. The terrain fragment projects up along the sun
                                               // ray to the cloud plane and samples the field there — moving dappled light at any sun angle.
        "uniform float uCloudAltitude;\n" +
        "uniform float uCloudNoiseScale;\n" +
        "uniform vec2 uCloudWind;\n" +
        "uniform vec2 uCloudShadowOffset;\n" + // sheet's slider-seeded field offset (ground shadows re-roll with the sky)
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
                                           // Perennial firn (lodowczyki): world-Z line/band of the ~2000 m REAL altitude gate + strength.
        "uniform float uFirnLineZ;\n" +
        "uniform float uFirnBandZ;\n" +
        "uniform float uFirnDropZ;\n" + // how far full concavity pulls the line DOWN (runout tongues), world-Z
        "uniform float uFirnStrength;\n" +
        "uniform vec4 uFirnSites[12];\n" + // curated glacieret sites: world XY + reach (m); WHERE comes from data
        "uniform float uFirnSiteCount;\n" +
        "uniform float uFirnChannelOn;\n" + // channel texels present in the water mask (static firn streams count)
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
        "uniform sampler2D uBaseCover;\n" +   // surface-ownership mask (unit 8): 255 = full-detail z16 ground
        "uniform vec2 uBaseCoverMinXY;\n" +   // world-XY of the mask window's min corner
        "uniform vec2 uBaseCoverSizeXY;\n" +  // world-XY extent of the mask window (metres)
        "uniform float uBaseCoverOn;\n" +     // 1 = mask valid this frame
        "uniform float uIsBaseSkin;\n" +      // 1 while drawing a BASE tile (set per mesh in the tile loop)
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
        // ★ UN-PREMULTIPLY PUNCH-THROUGH (2026-07-25 — jasna piła + ciemna nitka na granicy pokrycia).
        // Texel przezroczysty DXT1a dekoduje się jako RGBA(0,0,0,0) (Bc1Encoder: indeks 3 = przezroczysta CZERŃ),
        // a prebakowane mipy alfa-ważone zapisują RGB=0, gdy cały blok jest pusty.
        // KAŻDE filtrowanie przy granicy pokrycia (bilinear, mip, bicubic) rozcieńcza więc kolor CZERNIĄ
        // proporcjonalnie do (1−a) — stąd (a) ciemna nitka w wyświetlanym samplu, (b) po podaniu takiego
        // sampla do prawa tonu delta<0 ⇒ `dc − delta` PODBIJA jasność ⇒ jasne pasmo (zmierzone: kolor linii
        // 165,167,162 przy terenie 95,99,94). Odzyskanie średniej PO POKRYTYCH texelach jest DOKŁADNE:
        // rgb_f = Σ wᵢ·cᵢ (przezroczyste wnoszą 0), a_f = Σ wᵢ·aᵢ = Σ_pokryte wᵢ ⇒ rgb_f / a_f = ta średnia.
        // Poza granicą pokrycia (a→0) wynik i tak jest mnożony przez dcs.a, więc dzielenie nie ma wpływu.
        "vec3 unpremulPunch(vec4 s){ return clamp(s.rgb / max(s.a, 0.00393), 0.0, 1.0); }\n" + // 1/255
        "vec4 texBicubic(sampler2D t, vec2 uv, vec2 ts){\n" +
        "  vec2 coord = uv * ts - 0.5;\n" +
        "  vec2 fxy = fract(coord);\n" +
        "  coord -= fxy;\n" +
        "  vec4 xc = cubicW(fxy.x);\n" +
        "  vec4 yc = cubicW(fxy.y);\n" +
        "  vec4 cx = coord.xxyy + vec2(-0.5, 1.5).xyxy;\n" +
        "  vec4 s = vec4(xc.xz + xc.yw, yc.xz + yc.yw);\n" +
        "  vec4 off = cx + vec4(xc.yw, yc.yw) / s;\n" +
        "  off *= vec4(1.0 / ts.x, 1.0 / ts.x, 1.0 / ts.y, 1.0 / ts.y);\n" +
        // RGBA (nie RGB): alfa musi przejść przez te same wagi, żeby caller mógł zrobić un-premultiply.
        "  vec4 s00 = textureLod(t, vec2(off.x, off.z), 0.0);\n" +
        "  vec4 s10 = textureLod(t, vec2(off.y, off.z), 0.0);\n" +
        "  vec4 s01 = textureLod(t, vec2(off.x, off.w), 0.0);\n" +
        "  vec4 s11 = textureLod(t, vec2(off.y, off.w), 0.0);\n" +
        "  float sx = s.x / (s.x + s.y);\n" +
        "  float sy = s.z / (s.z + s.w);\n" +
        "  return mix(mix(s11, s01, sx), mix(s10, s00, sx), sy);\n" +
        "}\n" +
        // Array-layer twin of texBicubic (same weights, fetches one layer of the det05 array).
        "vec4 texBicubicArr(mediump sampler2DArray t, vec2 uv, float layer, vec2 ts){\n" +
        "  vec2 coord = uv * ts - 0.5;\n" +
        "  vec2 fxy = fract(coord);\n" +
        "  coord -= fxy;\n" +
        "  vec4 xc = cubicW(fxy.x);\n" +
        "  vec4 yc = cubicW(fxy.y);\n" +
        "  vec4 cx = coord.xxyy + vec2(-0.5, 1.5).xyxy;\n" +
        "  vec4 s = vec4(xc.xz + xc.yw, yc.xz + yc.yw);\n" +
        "  vec4 off = cx + vec4(xc.yw, yc.yw) / s;\n" +
        "  off *= vec4(1.0 / ts.x, 1.0 / ts.x, 1.0 / ts.y, 1.0 / ts.y);\n" +
        "  vec4 s00 = textureLod(t, vec3(off.x, off.z, layer), 0.0);\n" +
        "  vec4 s10 = textureLod(t, vec3(off.y, off.z, layer), 0.0);\n" +
        "  vec4 s01 = textureLod(t, vec3(off.x, off.w, layer), 0.0);\n" +
        "  vec4 s11 = textureLod(t, vec3(off.y, off.w, layer), 0.0);\n" +
        "  float sx = s.x / (s.x + s.y);\n" +
        "  float sy = s.z / (s.z + s.w);\n" +
        "  return mix(mix(s11, s01, sx), mix(s10, s00, sx), sy);\n" +
        "}\n" +
        // ABSOLUTE de-blue — the HARD RULE ("ORTO bez cieni na KAŻDEJ warstwie", user 07-16/20). Removes the
        // sky-light blue cast burnt into shadowed GUGiK ortho, PER PIXEL, independent of the base. The 07-20
        // "conditional tone harmonisation" experiment dropped this and blue bled back through shadowed rock
        // (Rysy / Czarny Staw). Design constraints learnt the hard way: (a) LUMA-GATE — do nothing in crushed
        // black, or WebP chroma noise gets amplified into the "zielona breja" za Mnichem; (b) NEVER ADD GREEN
        // (the old §3.13 `G += ex` was the green-paint bug) — only pull blue DOWN to the R/G level and mildly
        // desaturate toward neutral; (c) gated by blue-excess, so lit ground and true dark-green forest
        // (ex≈0) are untouched → the MO showcase is a no-op here. Per-pixel + identical everywhere = seam-safe.
        "vec3 deblueShadow(vec3 dc){\n" +
        "  float ex = max(0.0, dc.b - max(dc.r, dc.g));\n" +               // blue excess = shadow sky cast
        "  float lum = dot(dc, vec3(0.299, 0.587, 0.114));\n" +
        "  float lift = smoothstep(0.05, 0.16, lum);\n" +                  // crushed black untouched (no noise amp)
        "  dc.b = clamp(dc.b - (0.85 * ex * lift), 0.0, 1.0);\n" +         // pull blue to R/G level (NO green added)
        "  float sw = smoothstep(0.005, 0.06, ex);\n" +
        "  float grey = (dc.r + dc.g + dc.b) / 3.0;\n" +
        "  return mix(dc, vec3(grey), 0.35 * sw * lift);\n" +             // mild desat toward neutral in shadow
        "}\n" +
        // Hash must remain bit-identical to DetailCellSlotHash.Hash. The CPU selects a seed whose probe chain
        // is <=12; fragment cost is therefore cap-independent (192/128 residents no longer mean 192/128 AABB
        // tests per pixel). The nearest lattice cell is the normal one-hit path; 3×3 is only the fixed fallback
        // when that cell is still loading but an overlapping neighbour is resident.
        "uint detailCellHash(ivec2 c, uint seed){\n" +
        "  uint h = uint(c.x) * 0x9E3779B1u ^ uint(c.y) * 0x85EBCA77u ^ seed * 0xC2B2AE3Du;\n" +
        "  h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;\n" +
        "  return h;\n" +
        "}\n" +
        "ivec2 lookupDet05Cell(ivec2 c){\n" +
        "  uint start = detailCellHash(c, uint(uDet05HashSeed)) % 384u;\n" +
        "  for (int p = 0; p < 12; p++) {\n" +
        "    ivec4 e = uDet05CellHash[int((start + uint(p)) % 384u)];\n" +
        "    if (e.z < 0) return ivec2(-1, 0);\n" +
        "    if (all(equal(e.xy, c))) return e.zw;\n" +
        "  }\n" +
        "  return ivec2(-1, 0);\n" +
        "}\n" +
        "vec4 det05CellBounds(ivec2 c){\n" +
        "  vec2 nw = uDet05GridMinXmaxY + vec2(float(c.x) * uDet05GridPitch.x, -float(c.y) * uDet05GridPitch.y);\n" +
        "  return vec4(nw.x, nw.y - uDet05CellSize.y, nw.x + uDet05CellSize.x, nw.y);\n" +
        "}\n" +
        // Same colour law as applyOrthoDetail mode 1 (conditional tone harmonisation) — KONTRAKT-ORTO §1.
        "vec3 applyOrthoDet05Array(vec2 wxy, vec3 baseC, float blendM){\n" +
        "  if (uUseDet05Arr != 1) return baseC;\n" +
        "  vec2 wdx = dFdx(wxy), wdy = dFdy(wxy);\n" + // gradienty świata PRZED wyborem celi — patrz applyOrthoDet25Arr
        "  vec2 cp = vec2((wxy.x - uDet05GridMinXmaxY.x) / uDet05GridPitch.x,\n" +
        "                 (uDet05GridMinXmaxY.y - wxy.y) / uDet05GridPitch.y);\n" +
        "  ivec2 nearCell = max(ivec2(0), ivec2(floor(cp - 0.5 * (uDet05CellSize / uDet05GridPitch) + vec2(0.5))));\n" +
        "  ivec2 bestHit = lookupDet05Cell(nearCell); ivec2 bestCell = nearCell;\n" +
        "  vec4 bestBb = det05CellBounds(nearCell);\n" +
        "  bool nearContains = bestHit.x >= 0 && wxy.x >= bestBb.x && wxy.y >= bestBb.y && wxy.x <= bestBb.z && wxy.y <= bestBb.w;\n" +
        "  if (!nearContains) {\n" +
        "    bestHit = ivec2(-1, 0); float bestEdge = -1e30;\n" +
        "    for (int oy = -1; oy <= 1; oy++) { for (int ox = -1; ox <= 1; ox++) {\n" +
        "      ivec2 cc = nearCell + ivec2(ox, oy); if (cc.x < 0 || cc.y < 0) continue;\n" +
        "      ivec2 hit = lookupDet05Cell(cc); if (hit.x < 0) continue;\n" +
        "      vec4 cb = det05CellBounds(cc); vec2 cd0 = min(wxy - cb.xy, cb.zw - wxy);\n" +
        "      float edge = min(cd0.x, cd0.y); if (edge >= 0.0 && edge > bestEdge) { bestEdge = edge; bestHit = hit; bestCell = cc; bestBb = cb; }\n" +
        "    }}\n" +
        "  }\n" +
        "  if (bestHit.x < 0) return baseC;\n" +
        "  int best = bestHit.x; int minimumLod = bestHit.y >> 8; float promoteAlpha = float(bestHit.y & 255) / 255.0;\n" +
        "  vec2 mn = bestBb.xy; vec2 mx = bestBb.zw;\n" +
        "  vec2 sp = mx - mn;\n" +
        "  vec2 uv = vec2((wxy.x - mn.x) / sp.x, (mx.y - wxy.y) / sp.y);\n" +
        "  vec2 ts = vec2(textureSize(uOrthoDet05Arr, 0).xy);\n" +
        // slot → (tablica, warstwa): trzy równe tablice po uDet05ArrLayers warstw (3×64 = 192 cele).
        // Trzecia istnieje, bo JEDNA tekstura nie może przekroczyć 4 GiB (32-bitowe pole rozmiaru —
        // to był sufit „białych dziur" z 07-20), a 192 cele to 8 GiB; unit 7 był jedynym wolnym.
        "  int ai = best / uDet05ArrLayers;\n" +
        "  float lz = float(best - (ai * uDet05ArrLayers));\n" +
        "  vec2 gx = vec2(wdx.x / sp.x, -wdx.y / sp.y);\n" +
        "  vec2 gy = vec2(wdy.x / sp.x, -wdy.y / sp.y);\n" +
        "  float implicitLod = max(0.0, log2(max(length(gx * ts), length(gy * ts))));\n" +
        "  float sampleLod = max(float(minimumLod), implicitLod);\n" +
        "  vec4 dcs = minimumLod == 0\n" +
        "    ? (ai == 0 ? textureGrad(uOrthoDet05Arr, vec3(uv, lz), gx, gy)\n" +
        "       : (ai == 1 ? textureGrad(uOrthoDet05ArrB, vec3(uv, lz), gx, gy)\n" +
        "                  : textureGrad(uOrthoDet05ArrC, vec3(uv, lz), gx, gy)))\n" +
        "    : (ai == 0 ? textureLod(uOrthoDet05Arr, vec3(uv, lz), sampleLod)\n" +
        "       : (ai == 1 ? textureLod(uOrthoDet05ArrB, vec3(uv, lz), sampleLod)\n" +
        "                  : textureLod(uOrthoDet05ArrC, vec3(uv, lz), sampleLod)));\n" +
        "  vec3 dc = unpremulPunch(dcs);\n" + // czerń przezroczystych texeli NIE może rozcieńczać koloru przy granicy pokrycia
        "  vec2 fp = (abs(gx) + abs(gy)) * ts;\n" + // fwidth z gradów świata (fwidth(uv) na linii przełączenia cel = śmieci)

        "  if (minimumLod == 0 && max(fp.x, fp.y) < 1.0) { dc = unpremulPunch(ai == 0 ? texBicubicArr(uOrthoDet05Arr, uv, lz, ts)\n" +
        "                                        : (ai == 1 ? texBicubicArr(uOrthoDet05ArrB, uv, lz, ts)\n" +
        "                                                   : texBicubicArr(uOrthoDet05ArrC, uv, lz, ts))); }\n" +
        "  if (uOrthoDetailColorMode == 1 && uOrthoDet05ArrRaw == 0) {\n" + // H3: V2-baked cells render RAW while det25/base keep de-blue
        "    dc = deblueShadow(dc);\n" +                                   // (1) HARD RULE: absolute blue-cast removal
        "    float toneLod = max(float(minimumLod), max(0.0, log2(max(ts.x / (mx.x - mn.x), ts.y / (mx.y - mn.y)))) + 3.0);\n" + // ~8 m/texel: mikrocienie skał to nie szew ekspozycji (kontrast! 07-24)
        "    vec4 tRaw = ai == 0 ? textureLod(uOrthoDet05Arr, vec3(uv, lz), toneLod)\n" +
        "              : (ai == 1 ? textureLod(uOrthoDet05ArrB, vec3(uv, lz), toneLod)\n" +
        "                         : textureLod(uOrthoDet05ArrC, vec3(uv, lz), toneLod));\n" +
        "    float toneA = tRaw.a; vec3 dRaw = unpremulPunch(tRaw);\n" + // ton z POKRYTYCH texeli, nie z czerni brzegu
        "    vec3 delta = deblueShadow(dRaw) - deblueShadow(baseC);\n" +   // (2) both de-blued → delta = pure exposure seam (never re-adds blue)
        "    float mism = smoothstep(0.16, 0.35, max(abs(delta.r), max(abs(delta.g), abs(delta.b)))) * float(uToneHarm) * smoothstep(0.02, 0.12, toneA);\n" + // próg w górę: kontrast był wypłukiwany (luma std 39.5→24.7, zmierzone 07-24); uToneHarm=0 → diagnostyczne wyłączenie SAMEJ harmonizacji (de-blue zostaje)
        "    dc = clamp(dc - (delta * mism), 0.0, 1.0);\n" +               //     harmonise only the survey exposure seam
        "    if (uToneDebug == 1) { float corr = -(delta.r + delta.g + delta.b) * mism / 3.0;\n" + // mapa korekty: czerwony = ROZJAŚNIENIE, niebieski = przyciemnienie
        "      dc = vec3(clamp(corr * 3.0, 0.0, 1.0), 0.12, clamp(-corr * 3.0, 0.0, 1.0)); }\n" +
        "  }\n" +
        "  vec2 cd = min(wxy - mn, mx - wxy);\n" +
        // Cele det05 są ROZŁĄCZNE — fade po odległości od AABB robiłby SIATKĘ szwów (na wspólnej krawędzi
        // min(cd)=0 ⇒ w=0 ⇒ baza przebija wzdłuż każdej granicy celi). Krycie daje alfa danych + fade promocji.
        "  float w = dcs.a * promoteAlpha;\n" +
        "  vec3 outc = mix(baseC, dc, w);\n" +
        "  if (uOrthoDetailDebugBounds == 1) {\n" +
        "    float edge = min(cd.x, cd.y);\n" +
        "    if (edge >= 0.0 && edge < 3.0) { outc = vec3(1.0, 0.0, 1.0); }\n" +
        "  }\n" +
        "  return outc;\n" +
        "}\n" +
        // Hi-res ortho detail overlay: replace the base ortho colour with a finer texture where the fragment's
        // stable world XY falls inside [mn,mx], fading back over uDetailBlendMeters at the AABB edge (no hard
        // seam / UV-clamp stripe). `use` is a uniform so the early-out is uniform control flow — the implicit-LOD
        // fetch below stays derivative-valid; bicubic kicks in when magnified (close camera).
        // det1m: tier między bazą a det25. Fallback konstrukcyjny: poza siatką / cov≈0 / slice==-1 → baseC
        // (nigdy czerń). Kolor: PEŁNY dwustopniowy law jak zatwierdzone ścieżki (KONTRAKT-ORTO §1) — bez
        // niego panorama to patchwork STYLÓW warstw (werdykt usera 2026-07-24).
        "ivec2 lookupDet25Cell(ivec2 c){\n" +
        "  uint start = detailCellHash(c, uint(uDet25HashSeed)) % 256u;\n" +
        "  for (int p = 0; p < 12; p++) {\n" +
        "    ivec4 e = uDet25CellHash[int((start + uint(p)) % 256u)];\n" +
        "    if (e.z < 0) return ivec2(-1, 0);\n" +
        "    if (all(equal(e.xy, c))) return e.zw;\n" +
        "  }\n" +
        "  return ivec2(-1, 0);\n" +
        "}\n" +
        "vec4 det25CellBounds(ivec2 c){\n" +
        "  vec2 nw = uDet25GridMinXmaxY + vec2(float(c.x) * uDet25GridPitch.x, -float(c.y) * uDet25GridPitch.y);\n" +
        "  return vec4(nw.x, nw.y - uDet25CellSize.y, nw.x + uDet25CellSize.x, nw.y);\n" +
        "}\n" +
        // det25 array: nearest resident containing cell wins, now through the same bounded hash lookup.
        // Kolor: ten sam dwustopniowy law. Alpha = fade-in promocji (anty-pop).
        "vec3 applyOrthoDet25Arr(vec2 wxy, vec3 baseC){\n" +
        "  if (uUseDet25Arr == 0) { return baseC; }\n" +
        // KROPKOWANE LINIE PRZEŁĄCZEŃ CEL (2026-07-24, zrzuty usera: jasne przerywane proste przez całą
        // mapę, prostopadłe, najjaśniejsze na wodzie): per-fragment wybór celi ⇒ na Voronoi-granicy
        // best-cell uv SKACZE między fragmentami quadu, implicit-LOD dostaje śmieciowe pochodne i sampluje
        // najgłębsze mipy (uśredniony JASNY kolor celi). Fix: gradienty ze ŚWIATA (wxy — ciągłe przez
        // granice; liczone tu, w uniform flow) i textureGrad zamiast texture. Dane były czyste — pomiary
        // (dekod chainów, edge-step, porównanie nakładek) wykluczyły bake/assembler.
        "  vec2 wdx = dFdx(wxy), wdy = dFdy(wxy);\n" +
        "  vec2 cp = vec2((wxy.x - uDet25GridMinXmaxY.x) / uDet25GridPitch.x,\n" +
        "                 (uDet25GridMinXmaxY.y - wxy.y) / uDet25GridPitch.y);\n" +
        "  ivec2 nearCell = max(ivec2(0), ivec2(floor(cp - 0.5 * (uDet25CellSize / uDet25GridPitch) + vec2(0.5))));\n" +
        "  ivec2 bestHit = lookupDet25Cell(nearCell); ivec2 bestCell = nearCell;\n" +
        "  vec4 bb = det25CellBounds(nearCell);\n" +
        "  bool nearContains = bestHit.x >= 0 && wxy.x >= bb.x && wxy.y >= bb.y && wxy.x <= bb.z && wxy.y <= bb.w;\n" +
        "  if (!nearContains) {\n" +
        "    bestHit = ivec2(-1, 0); float bestD = 1e30;\n" +
        "    for (int oy = -1; oy <= 1; oy++) { for (int ox = -1; ox <= 1; ox++) {\n" +
        "      ivec2 cc = nearCell + ivec2(ox, oy); if (cc.x < 0 || cc.y < 0) continue;\n" +
        "      ivec2 hit = lookupDet25Cell(cc); if (hit.x < 0) continue;\n" +
        "      vec4 cb = det25CellBounds(cc); if (wxy.x < cb.x || wxy.y < cb.y || wxy.x > cb.z || wxy.y > cb.w) continue;\n" +
        "      vec2 cen = 0.5 * (cb.xy + cb.zw); float d = dot(wxy - cen, wxy - cen);\n" +
        "      if (d < bestD) { bestD = d; bestHit = hit; bestCell = cc; bb = cb; }\n" +
        "    }}\n" +
        "  }\n" +
        "  if (bestHit.x < 0) { return baseC; }\n" +
        "  int best = bestHit.x; float promoteAlpha = float(bestHit.y & 255) / 255.0;\n" +
        "  vec2 sp = bb.zw - bb.xy;\n" +
        "  vec2 uv = vec2((wxy.x - bb.x) / sp.x, (bb.w - wxy.y) / sp.y);\n" +
        "  vec2 gx = vec2(wdx.x / sp.x, -wdx.y / sp.y);\n" +
        "  vec2 gy = vec2(wdy.x / sp.x, -wdy.y / sp.y);\n" +
        "  vec4 dcs = textureGrad(uOrthoDet25Arr, vec3(uv, float(best)), gx, gy);\n" + // DXT1a: a=0 = brak pokrycia
        "  vec3 dc = unpremulPunch(dcs);\n" + // czerń przezroczystych texeli NIE może rozcieńczać koloru przy granicy pokrycia
        "  if (uOrthoDetailColorMode == 1) {\n" + // dwustopniowy law (wzorzec applyOrthoDet05Array, KONTRAKT-ORTO §1)
        "    dc = deblueShadow(dc);\n" +                                   // (1) HARD RULE: absolute blue-cast removal
        "    vec2 ts = vec2(textureSize(uOrthoDet25Arr, 0).xy);\n" +
        "    float toneLod = max(0.0, log2(max(ts.x / (bb.z - bb.x), ts.y / (bb.w - bb.y)))) + 3.0;\n" + // ~8 m/texel — patrz det05Array
        "    vec4 tRaw = textureLod(uOrthoDet25Arr, vec3(uv, float(best)), toneLod);\n" +
        "    float toneA = tRaw.a; vec3 dRaw = unpremulPunch(tRaw);\n" + // ton z POKRYTYCH texeli, nie z czerni brzegu
        "    vec3 delta = deblueShadow(dRaw) - deblueShadow(baseC);\n" +   // (2) both de-blued → delta = pure exposure seam
        "    float mism = smoothstep(0.16, 0.35, max(abs(delta.r), max(abs(delta.g), abs(delta.b)))) * float(uToneHarm) * smoothstep(0.02, 0.12, toneA);\n" + // próg w górę: kontrast był wypłukiwany (luma std 39.5→24.7, zmierzone 07-24); uToneHarm=0 → diagnostyczne wyłączenie SAMEJ harmonizacji (de-blue zostaje)
        "    dc = clamp(dc - (delta * mism), 0.0, 1.0);\n" +               //     harmonise only the survey exposure seam
        "    if (uToneDebug == 1) { float corr = -(delta.r + delta.g + delta.b) * mism / 3.0;\n" + // mapa korekty: czerwony = ROZJAŚNIENIE, niebieski = przyciemnienie
        "      dc = vec3(clamp(corr * 3.0, 0.0, 1.0), 0.12, clamp(-corr * 3.0, 0.0, 1.0)); }\n" +
        "  }\n" +
        "  vec2 cd = min(wxy - bb.xy, bb.zw - wxy);\n" +
        "  float wgt = clamp(min(cd.x, cd.y) / max(uDetailBlendMeters, 0.001), 0.0, 1.0) * dcs.a * promoteAlpha;\n" +
        "  return mix(baseC, dc, wgt);\n" +
        "}\n" +
        "vec3 applyOrthoDet1m(vec2 wxy, vec3 baseC){\n" +
        "  if (uUseDet1m == 0) { return baseC; }\n" +
        "  vec2 wdx = dFdx(wxy), wdy = dFdy(wxy);\n" + // gradienty świata — fract(cellUv) skacze na granicach komórek
        "  vec2 uv = vec2((wxy.x - uDet1mMinXmaxY.x) * uDet1mInvSize.x, (uDet1mMinXmaxY.y - wxy.y) * uDet1mInvSize.y);\n" +
        "  if (uv.x <= 0.0 || uv.x >= 1.0 || uv.y <= 0.0 || uv.y >= 1.0) { return baseC; }\n" +
        "  float cov = texture(uOrthoDet1mCov, uv).r;\n" +
        "  if (cov <= 0.01) { return baseC; }\n" +
        "  ivec2 g = ivec2(clamp(int(uv.x * float(uDet1mGridDim.x)), 0, uDet1mGridDim.x - 1),\n" +
        "                  clamp(int(uv.y * float(uDet1mGridDim.y)), 0, uDet1mGridDim.y - 1));\n" +
        "  int slice = uDet1mSliceIdx[(g.y * uDet1mGridDim.x) + g.x];\n" +
        "  if (slice < 0) { if (uDet1mDebug == 1) { return vec3(1.0, 0.0, 1.0); } return baseC; }\n" + // debug: magenta = brak slice'a mimo cov
        "  vec2 cellUv = fract(uv * vec2(uDet1mGridDim));\n" +
        "  vec2 gsc = uDet1mInvSize * vec2(uDet1mGridDim);\n" +
        "  vec2 gx = vec2(wdx.x * gsc.x, -wdx.y * gsc.y);\n" +
        "  vec2 gy = vec2(wdy.x * gsc.x, -wdy.y * gsc.y);\n" +
        "  vec4 dcs = textureGrad(uOrthoDet1m, vec3(cellUv, float(slice)), gx, gy);\n" + // DXT1a: a=0 = brak pokrycia
        "  if (uDet1mDebug == 1) {\n" + // klasyfikacja danych: skad czern? (opaque-black przechodzi bramke alfa)
        "    if (dcs.a < 0.05) { return vec3(1.0, 1.0, 0.0); }\n" +          // zolty: punch-through (a=0)
        "    if (dot(dcs.rgb, dcs.rgb) < 0.0004) { return vec3(1.0, 0.0, 0.0); }\n" + // czerwony: OPAQUE BLACK w danych
        "    return vec3(0.0, 0.6, 0.0);\n" +                                // zielony: zdrowe krycie
        "  }\n" +
        "  vec3 dc = unpremulPunch(dcs);\n" + // czerń przezroczystych texeli NIE może rozcieńczać koloru przy granicy pokrycia
        "  if (uOrthoDetailColorMode == 1) {\n" + // dwustopniowy law (wzorzec applyOrthoDet05Array, KONTRAKT-ORTO §1)
        "    dc = deblueShadow(dc);\n" +                                   // (1) HARD RULE: absolute blue-cast removal
        "    vec2 ts = vec2(textureSize(uOrthoDet1m, 0).xy);\n" +
        "    vec2 cellM = vec2(1.0 / uDet1mInvSize.x, 1.0 / uDet1mInvSize.y) / vec2(uDet1mGridDim);\n" + // komorka w metrach
        "    float toneLod = max(0.0, log2(max(ts.x / cellM.x, ts.y / cellM.y))) + 3.0;\n" + // ~8 m/texel — patrz det05Array
        "    vec4 tRaw = textureLod(uOrthoDet1m, vec3(cellUv, float(slice)), toneLod);\n" +
        "    float toneA = tRaw.a; vec3 dRaw = unpremulPunch(tRaw);\n" + // ton z POKRYTYCH texeli, nie z czerni brzegu
        "    vec3 delta = deblueShadow(dRaw) - deblueShadow(baseC);\n" +   // (2) both de-blued → delta = pure exposure seam
        "    float mism = smoothstep(0.16, 0.35, max(abs(delta.r), max(abs(delta.g), abs(delta.b)))) * float(uToneHarm) * smoothstep(0.02, 0.12, toneA);\n" + // próg w górę: kontrast był wypłukiwany (luma std 39.5→24.7, zmierzone 07-24); uToneHarm=0 → diagnostyczne wyłączenie SAMEJ harmonizacji (de-blue zostaje)
        "    dc = clamp(dc - (delta * mism), 0.0, 1.0);\n" +               //     harmonise only the survey exposure seam
        "    if (uToneDebug == 1) { float corr = -(delta.r + delta.g + delta.b) * mism / 3.0;\n" + // mapa korekty: czerwony = ROZJAŚNIENIE, niebieski = przyciemnienie
        "      dc = vec3(clamp(corr * 3.0, 0.0, 1.0), 0.12, clamp(-corr * 3.0, 0.0, 1.0)); }\n" +
        "  }\n" +
        "  return mix(baseC, dc, cov * dcs.a);\n" +
        "}\n" +
        "vec3 applyOrthoDetail(sampler2D tex, int use, vec2 mn, vec2 mx, float blendM, vec2 wxy, vec3 baseC, float rangeFade){\n" +
        "  if (use != 1) return baseC;\n" +
        "  vec2 uv = vec2((wxy.x - mn.x) / (mx.x - mn.x), (mx.y - wxy.y) / (mx.y - mn.y));\n" + // v=0 at north, matches base ortho UV
        "  vec2 ts = vec2(textureSize(tex, 0));\n" +
        "  vec4 dcs = texture(tex, uv);\n" +
        "  vec3 dc = unpremulPunch(dcs);\n" + // czerń przezroczystych texeli NIE może rozcieńczać koloru przy granicy pokrycia
        "  vec2 fp = fwidth(uv) * ts;\n" +
        "  if (max(fp.x, fp.y) < 1.0) { dc = unpremulPunch(texBicubic(tex, uv, ts)); }\n" +
        // Colour variant, mode 1 (DEFAULT — the no-burnt-shadows hard rule): neutralise the sky cast of
        // burnt-in flight shadows by DESATURATING toward the RGB mean, gated by the blue excess. This is the
        // documented PROPER method from the §3.11 r1-c3 rollback (TILE-PRODUCTION): the old §3.13 shift
        // (G += 0.35·ex) does not remove the cast — it PRODUCES green (the uniform "green paint" patch in
        // front of Mnich). The gate is the RAW data's blue excess, so a legitimately dark-green forest
        // (G > B, ex≈0) and lit ground are untouched; luma is preserved (no washing), the transform is
        // per-pixel and identical everywhere (seam-safe). Mode 0 = raw detail (diagnostics, key '9').
        "  if (uOrthoDetailColorMode == 1) {\n" +
        // Two-step colour law (2026-07-20, after the Rysy blue-bleed verdict). (1) ABSOLUTE de-blue — the
        // hard rule, removes the shadow sky-cast per pixel on this layer too. (2) CONDITIONAL tone
        // harmonisation — pulls the (already de-blued) detail toward the (already de-blued) base ONLY where
        // their low-frequency tone still deviates (survey EXPOSURE seam, not colour cast). Below threshold
        // it is an exact identity, so the MO showcase renders verbatim and stays sharp at every distance;
        // the earlier "tone-from-base" law that reduced distant views to bare base is NOT reinstated.
        "    dc = deblueShadow(dc);\n" +                                   // (1) HARD RULE: absolute blue-cast removal
        "    float toneLod = max(0.0, log2(max(ts.x / (mx.x - mn.x), ts.y / (mx.y - mn.y)))) + 3.0;\n" + // ~8 m/texel: mikrocienie skał to nie szew ekspozycji (kontrast! 07-24)
        "    vec4 tRaw = textureLod(tex, uv, toneLod);\n" +
        "    float toneA = tRaw.a; vec3 dRaw = unpremulPunch(tRaw);\n" + // ton z POKRYTYCH texeli, nie z czerni brzegu
        "    vec3 delta = deblueShadow(dRaw) - deblueShadow(baseC);\n" + // (2) both de-blued → delta = pure exposure seam
        "    float mism = smoothstep(0.16, 0.35, max(abs(delta.r), max(abs(delta.g), abs(delta.b)))) * float(uToneHarm) * smoothstep(0.02, 0.12, toneA);\n" + // próg w górę: kontrast był wypłukiwany (luma std 39.5→24.7, zmierzone 07-24); uToneHarm=0 → diagnostyczne wyłączenie SAMEJ harmonizacji (de-blue zostaje)
        "    dc = clamp(dc - (delta * mism), 0.0, 1.0);\n" +               //     harmonise only the survey exposure seam
        "    if (uToneDebug == 1) { float corr = -(delta.r + delta.g + delta.b) * mism / 3.0;\n" + // mapa korekty: czerwony = ROZJAŚNIENIE, niebieski = przyciemnienie
        "      dc = vec3(clamp(corr * 3.0, 0.0, 1.0), 0.12, clamp(-corr * 3.0, 0.0, 1.0)); }\n" +
        "  }\n" +
        "  vec2 cd = min(wxy - mn, mx - wxy);\n" +                    // >0 inside the AABB on both axes
        "  float w = clamp(min(cd.x, cd.y) / max(blendM, 0.001), 0.0, 1.0) * rangeFade * dcs.a;\n" + // ×alpha: holes (a=0) keep base/coarser tier
        "  vec3 outc = mix(baseC, dc, w);\n" +
        // Diagnostics: outline each detail cell's AABB edge (magenta, ~3 m band) so cell boundaries + the
        // detail↔detail and detail↔base transitions are visible while judging the slice.
        "  if (uOrthoDetailDebugBounds == 1) {\n" +
        "    float edge = min(cd.x, cd.y);\n" +
        "    if (edge >= 0.0 && edge < 3.0) { outc = vec3(1.0, 0.0, 1.0); }\n" +
        "  }\n" +
        "  return outc;\n" +
        "}\n" +
        // Cascaded Shadow Maps: 12-tap Poisson-disc PCF (hardware depth compare) with a per-pixel rotation
        // (interleaved gradient noise) so the disc never bands — soft, natural penumbra instead of the old
        // 3×3 box "ladder". Cascade picked by view distance; the last 10% of each cascade's range
        // CROSS-FADES into the next one, so the quality step at a split is a blend, not a visible seam.
        // uShadowTexel = 1/ShadowMapSize (desktop 2048, phone 1024) — set from the C# constant.
        "const vec2 POISSON12[12] = vec2[](\n" +
        "  vec2(-0.326, -0.406), vec2(-0.840, -0.074), vec2(-0.696,  0.457), vec2(-0.203,  0.621),\n" +
        "  vec2( 0.962, -0.195), vec2( 0.473, -0.480), vec2( 0.519,  0.767), vec2( 0.185, -0.893),\n" +
        "  vec2( 0.507,  0.064), vec2( 0.896,  0.412), vec2(-0.322, -0.933), vec2(-0.792, -0.598));\n" +
        "float pcfShadow(highp sampler2DShadow sm, vec2 uv, float depthRef, vec2 rot){\n" +
        "  float radius = uShadowTexel * 1.5;\n" +
        "  float s = 0.0;\n" +
        "  for (int i = 0; i < 12; i++) {\n" +
        "    vec2 o = POISSON12[i];\n" +
        "    vec2 ro = vec2((o.x * rot.x) - (o.y * rot.y), (o.x * rot.y) + (o.y * rot.x));\n" +
        "    s += texture(sm, vec3(uv + (ro * radius), depthRef));\n" +
        "  }\n" +
        "  return s / 12.0;\n" +
        "}\n" +
        // One cascade's lit factor for a world position (1 = lit). Out-of-map → 1 so the caller's blend
        // toward the coarser cascade degrades gracefully at the fine map's edge.
        "float cascadeLit(int ci, vec3 worldPos, float ndotl, vec2 rot){\n" +
        "  mat4 vp = (ci == 0) ? uCascadeVp0 : ((ci == 1) ? uCascadeVp1 : uCascadeVp2);\n" +
        "  vec4 lc = vp * vec4(worldPos, 1.0);\n" +
        "  vec3 p = lc.xyz / lc.w;\n" +
        "  p = (p * 0.5) + 0.5;\n" +
        "  if (p.z >= 1.0 || p.x < 0.0 || p.x > 1.0 || p.y < 0.0 || p.y > 1.0) return 1.0;\n" +
        "  float bias = max(0.0025 * (1.0 - ndotl), 0.0007);\n" +
        "  float d = p.z - bias;\n" +
        "  return (ci == 0) ? pcfShadow(uShadowMap0, p.xy, d, rot)\n" +
        "       : ((ci == 1) ? pcfShadow(uShadowMap1, p.xy, d, rot) : pcfShadow(uShadowMap2, p.xy, d, rot));\n" +
        "}\n" +
        "float csmShadow(float viewDist, vec3 worldPos, float ndotl){\n" +
        "  if (uShadowStrength < 0.001) return 1.0;\n" +
        // Per-pixel disc rotation from interleaved gradient noise — stable per screen position.
        "  float ang = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715)))) * 6.2831853;\n" +
        "  vec2 rot = vec2(cos(ang), sin(ang));\n" +
        "  int ci = (viewDist < uCascadeSplit.x) ? 0 : ((viewDist < uCascadeSplit.y) ? 1 : 2);\n" +
        "  float lit = cascadeLit(ci, worldPos, ndotl, rot);\n" +
        "  float splitFar = (ci == 0) ? uCascadeSplit.x : ((ci == 1) ? uCascadeSplit.y : uCascadeSplit.z);\n" +
        "  float fadeStart = splitFar * 0.9;\n" +
        "  if (ci < 2 && viewDist > fadeStart) {\n" +
        "    float f = smoothstep(fadeStart, splitFar, viewDist);\n" +
        "    lit = mix(lit, cascadeLit(ci + 1, worldPos, ndotl, rot), f);\n" +
        "  }\n" +
        "  return mix(1.0, lit, uShadowStrength);\n" +
        "}\n" +
        "void main(){\n" +
        // Reflection pre-pass: we're rendering the terrain MIRRORED about the lake plane into the reflection
        // texture. Discard anything below the waterline so only the above-water peaks end up in the reflection.
        "  if (uReflectionPass > 0.5 && vWorldPos.z < uWaterClipZ) { discard; }\n" +
        // SURFACE OWNERSHIP (2026-07-03): the box-averaged BASE SKIN sits 0.5–4 m ABOVE the true z16 surface
        // on convex slopes, so — always drawn — it depth-buried the streamed 1 m detail there: whole slopes
        // rendered as the smooth base dome ("lotnisko obok ostrej grani") while the fine meshes were shaded
        // for nothing underneath. Where the coverage mask (BaseCoverageMaskBuilder: hole-free resident z16
        // union, eroded one texel = conservative) marks full-detail ground, base-skin fragments are DISCARDED
        // per-pixel — ownership independent of base tile sizes and of how the resident set is distributed
        // (the per-base-tile culling variant almost never triggered: culled 0-1/340). Main pass only: the
        // water reflection keeps the base (a metres-high skin difference is invisible in a mirrored lake).
        "  if (uIsBaseSkin > 0.5 && uBaseCoverOn > 0.5 && uReflectionPass < 0.5) {\n" +
        "    vec2 cuv = (vStableWorldPos.xy - uBaseCoverMinXY) / uBaseCoverSizeXY;\n" +
        "    if (cuv.x > 0.0 && cuv.x < 1.0 && cuv.y > 0.0 && cuv.y < 1.0\n" +
        "        && texture(uBaseCover, cuv).r > 0.5) { discard; }\n" +
        "  }\n" +
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
        // vStableWorldPos.z, NOT vWorldPos.z: the render frame is camera-relative, so the ray length to the
        // cloud deck changed as the camera tilted and the shadow pattern "swam" — the same latent bug the
        // snow line had (snowH vs render-frame z), now fixed on this last remaining site.
        "    float tt = (uCloudAltitude - vStableWorldPos.z) / uLightDir.z;\n" +
        "    if (tt > 0.0) {\n" +
        // uCloudShadowOffset = the sheet's slider-seeded field offset, so ground shadows re-roll WITH the
        // clouds overhead when the coverage slider moves (the two patterns used to be unrelated).
        "      vec2 cp = (vStableWorldPos.xy - uCloudShadowOffset) + (uLightDir.xy * tt);\n" +
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
        // 45→60° (2026-07-16, was 55→75): the old ramp reached FULL granite only at ~75°, so a 50–70° wall —
        // the back of Mnich — showed mostly the smeared top-down ortho (and the §3.13 de-blue turns that
        // bluish smear GREEN: "painted with shitty green paint"). No top-down ortho, 25 cm or otherwise, has
        // real pixels on a near-vertical face — the wall belongs to the granite, period (hard baseline rule).
        // 45° keeps meadows/scree on ortho (Tatra ground steeper than that IS rock); full claim by 60°.
        // Historic intent of the old values: granite claims only NEAR-VERTICAL faces, where the ortho drape is genuinely
        // smeared. The old 40→65° band also swallowed 40–60° slopes that have crisp, real rock texture in
        // the imagery (Orla Perć read worse WITH the material than with plain ortho — user 2026-07-02);
        // the Slovak big north faces are 65°+ so they keep their granite rescue.
        "  float rockW = (uSlopeMode < 0.5) ? smoothstep(45.0, 60.0, rockSlopeDeg) * uRockStrength : 0.0;\n" +
        // NESTED granite (v7, 2026-07-03, matched to user reference photos — Orla Perć chimney close-up +
        // Granaty wall): real Tatra rock is MULTI-SCALE and follows the FALL LINE, not any single motif.
        // Two nested Voronoi layers on the triplanar plane:
        //   COARSE (18–40 m) = wall facets/buttresses — strong tonal patches (bleached vs grey faces, the
        //     dominant read of the Granaty photo) + strong per-facet ANGULAR normal tilt;
        //   FINE (3–8 m, decorrelated per coarse facet, stretched ~2x ALONG THE FALL LINE) = blocks —
        //     vertically elongated like the chimney joints, each with its own smaller tone + facet, and
        //     deep dark crack seams at the borders (the visible black fracture lines of the close-up).
        // No domes and no global rotation — the reference is angular, and orientation comes from the
        // fall-line anisotropy alone.
        "  float rk = 0.0;\n" +
        "  if (rockW > 0.001) {\n" +
        "    vec3 anR = abs(shN);\n" +
        "    int plR = (anR.z >= anR.x && anR.z >= anR.y) ? 0 : ((anR.x >= anR.y) ? 1 : 2);\n" +
        "    vec2 rp = (plR == 0) ? vStableWorldPos.xy : ((plR == 1) ? vStableWorldPos.yz : vStableWorldPos.zx);\n" +
        // Fall-line anisotropy: on steep faces one plane axis is world-Z; shrinking it stretches the fine
        // cells vertically (blocks elongated down the gully). Flat ground stays isotropic.
        "    vec2 anisoF = (plR == 0) ? vec2(1.0) : ((plR == 1) ? vec2(1.0, 0.5) : vec2(0.5, 1.0));\n" +
        // COARSE facets. Cell sizes are CONSTANT: a spatially-varying size with floor(coord/size) on
        // absolute coords makes cell indices sweep along the size-noise contours — dense wavy tone bands
        // ("regularne pasy poziome", the stripe artifact that plagued v4–v7 regardless of the pattern).
        // Size variety comes from the Voronoi jitter itself (~2:1 spread).
        "    float csz = 26.0;\n" +
        "    vec2 cwp = rp + (vec2(noiseT(rp * 0.03), noiseT(rp * 0.026 + 7.7)) - 0.5) * (csz * 0.5);\n" +
        "    vec2 cg = floor(cwp / csz); vec2 cf = fract(cwp / csz);\n" +
        "    float cF1 = 8.0; float cF2 = 8.0; vec2 cidC = cg;\n" +
        "    for (int oy = -1; oy <= 1; oy++) { for (int ox = -1; ox <= 1; ox++) {\n" +
        "      vec2 oc = vec2(float(ox), float(oy)); vec2 cid = cg + oc;\n" +
        "      vec2 sv = oc + vec2(hashT(cid * 1.7 + 3.1), hashT(cid * 2.3 + 9.4)) - cf;\n" +
        "      float d = length(sv);\n" +
        "      if (d < cF1) { cF2 = cF1; cF1 = d; cidC = cid; } else if (d < cF2) { cF2 = d; }\n" +
        "    } }\n" +
        "    float toneC = hashT(cidC * 2.63 + 1.37);\n" +
        "    float emC = (cF2 - cF1) * csz;\n" +
        // FINE blocks: offset by a per-facet vector so the subdivision does not continue across facets.
        "    float fsz = 5.0;\n" + // constant — see the csz comment (varying size = stripe artifact)
        "    vec2 fwp = (rp + vec2(hashT(cidC * 4.9 + 0.7), hashT(cidC * 6.1 + 8.2)) * 37.0) * anisoF;\n" +
        "    fwp += (vec2(noiseT(rp * 0.11), noiseT(rp * 0.09 + 4.4)) - 0.5) * (fsz * 0.7);\n" +
        "    vec2 fg = floor(fwp / fsz); vec2 ff = fract(fwp / fsz);\n" +
        "    float fF1 = 8.0; float fF2 = 8.0; vec2 cidF = fg;\n" +
        "    for (int oy = -1; oy <= 1; oy++) { for (int ox = -1; ox <= 1; ox++) {\n" +
        "      vec2 oc = vec2(float(ox), float(oy)); vec2 cid = fg + oc;\n" +
        "      vec2 sv = oc + vec2(hashT(cid * 3.7 + 1.9), hashT(cid * 5.3 + 6.8)) - ff;\n" +
        "      float d = length(sv);\n" +
        "      if (d < fF1) { fF2 = fF1; fF1 = d; cidF = cid; } else if (d < fF2) { fF2 = d; }\n" +
        "    } }\n" +
        "    float toneF = hashT(cidF * 2.11 + 5.9);\n" +
        "    float emF = (fF2 - fF1) * fsz;\n" +
        // Cracks: fine joints are the visible dark fracture lines (depth varies by patch); coarse borders
        // are BROAD soft tone breaks, not lines.
        "    float aaF = max(fwidth(emF) * 1.8, 0.06);\n" +
        "    float crack = (1.0 - smoothstep(0.16, 0.16 + aaF, emF)) * mix(0.45, 1.0, noiseT(rp * 0.07));\n" +
        "    float aaC = max(fwidth(emC) * 1.8, 0.10);\n" +
        "    crack = max(crack, (1.0 - smoothstep(0.55, 0.55 + aaC + 0.9, emC)) * 0.5);\n" +
        // Tone: dominant coarse facet contrast + weathering BLEACH on some facets (the near-white faces in
        // the reference) + smaller per-block variation + micro grain.
        // ALBEDO contrast compressed ~2× + brighter base (2026-07-03): with snow OFF the painted seams/tones
        // read as a regular UNNATURAL grid; at full snow the ~50% white blend compressed them to a faint
        // trace and the rock "łapał zajebistą szarość" — so the bare rock now ships pre-compressed the same
        // way. The block STRUCTURE stays carried by the facet normals (light), not by painted lines.
        "    float bleach = smoothstep(0.68, 0.95, toneC) * 0.15;\n" +
        "    float micro = 0.5 * noiseT(vStableWorldPos.xy * 1.1) + 0.5 * noiseT(vStableWorldPos.yz * 1.1);\n" +
        "    rk = clamp(0.62 + (toneC - 0.5) * 0.24 + (toneF - 0.5) * 0.12 + bleach + (micro - 0.5) * 0.08 - crack * 0.22, 0.0, 1.0);\n" +
        // Angular facet normals: strong constant tilt per coarse facet + smaller per-block tilt.
        "    vec3 tcC = vec3(hashT(cidC * 3.3 + 6.1) - 0.5, hashT(cidC * 4.7 + 2.2) - 0.5, (hashT(cidC * 7.9 + 0.4) - 0.5) * 0.6);\n" +
        "    vec3 tcF = vec3(hashT(cidF * 6.7 + 3.8) - 0.5, hashT(cidF * 8.1 + 7.5) - 0.5, 0.0);\n" +
        "    vec3 tilt = tcC * 0.8 + tcF * 0.5;\n" +
        "    tilt = tilt - shN * dot(tilt, shN);\n" +
        "    shN = normalize(shN + tilt * (0.6 * rockW));\n" +
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
        // HEMISPHERIC ambient (2026-07-03, "bez światła te bryły się chowają"): a flat ambient is
        // direction-independent, so in shade the granite facets' normal tilts contributed NOTHING and the
        // block structure vanished. Skylight comes from above — modulating the ambient by the SHADING normal's
        // up-ness keeps the facets differentiating surfaces even with zero direct sun.
        "  float skyVis = 0.55 + (0.45 * clamp(shN.z, 0.0, 1.0));\n" +
        "  vec3 lightSum = (uSkyAmbient * uAmbient * skyVis) + (uSunColor * sunlit);\n" +
        // Dragon-fire glow (B2): additive point-light loop, injected BEFORE the ambient floor so the floor
        // still guards against black. Squared soft attenuation + a wrap-diffuse (floor 0.25) so the glow
        // licks around edges instead of cutting off at the terminator. Constant loop bound + break = ANGLE-safe.
        "  vec3 fireGlow = vec3(0.0);\n" +
        "  for (int fi = 0; fi < 8; fi++) {\n" +
        "    if (float(fi) >= uFireCount) { break; }\n" +
        "    vec3 dF = uFirePos[fi] - vStableWorldPos;\n" +
        "    float attF = 1.0 / (1.0 + (dot(dF, dF) * uFireInvR2[fi]));\n" +
        "    attF *= attF;\n" +
        "    float wrapF = max((dot(shN, normalize(dF)) + 0.25) / 1.25, 0.0);\n" +
        "    fireGlow += uFireColor[fi] * (attF * wrapF);\n" +
        "  }\n" +
        "  lightSum += fireGlow;\n" +
        // Ambient FLOOR: steep faces turned from the sun (lambert=0) otherwise collapse to lightSum≈0 → near-BLACK
        // (the "czarne dziury/kropki" — proven: an unlit render has 0 black px). max() lifts ONLY the deepest
        // shadows to a cool sky-fill minimum. The floor is hemispheric too (0.30–0.50 by up-ness instead of a
        // flat 0.45): a flat floor PRESSED every below-threshold face to one brightness, erasing the relief
        // exactly where the sun doesn't reach.
        "  lightSum = max(lightSum, uSkyAmbient * (0.30 + (0.20 * clamp(shN.z, 0.0, 1.0))));\n" +
        // Curvature AO baked into the colour attribute's alpha (TerrainCurvatureAo, floored at 0.4 so it
        // darkens, never blackens): gully/bowl floors receive less sky than open ridges. Applied AFTER the
        // anti-black floor by design — an enclosed floor SHOULD sit below the open-ground floor; the bake's
        // own MinAo is the readability guarantee. uAoStrength scales the effect (0 = off).
        "  lightSum *= mix(1.0, vColor.a, uAoStrength);\n" +
        "  vec3 base;\n" +
        "  if (uUseOrtho == 1) {\n" +
        "    vec3 c = texture(uOrtho, vTex).rgb;\n" +
        // MAGNIFICATION smoothing: when one ortho texel spans more than a screen pixel (close camera), swap
        // the bilinear fetch for bicubic so texels stop reading as hard squares. Minified ground (footprint
        // >= 1 texel) keeps the plain fetch — mips + anisotropy already handle it, and bicubic of mip 0
        // would shimmer there. The unconditional fetch above keeps implicit derivatives defined.
        "    vec2 otsF = vec2(textureSize(uOrtho, 0));\n" +
        "    vec2 ofp = fwidth(vTex) * otsF;\n" +
        "    if (max(ofp.x, ofp.y) < 1.0) { c = texBicubic(uOrtho, vTex, otsF).rgb; }\n" + // baza jest kryjąca — bez un-premultiply
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
        // Hi-res detail overlay ON TOP of the base ortho colour (before coverage/biome/rock so everything
        // downstream — coverage fade, biomes, granite, lighting, AO — applies to the detailed colour). det25
        // first then det05 so the finest wins where both AABBs overlap.
        // H5b (2026-07-23): the MIRROR skips the det25/det05 layers entirely — at half-res with the 2 % ripple
        // wobble and blue tint the reflected walls cannot resolve 25/5 cm anyway, while sampling them (16-slot
        // AABB walk + bicubic) doubled the mirror's fragment cost (refl 13.8 → 26.9 ms once 16 cells were
        // resident). The mirror shows base ortho; the REAL view keeps every detail layer.
        "    if (uReflectionPass < 0.5) {\n" +
        "      c = applyOrthoDet1m(vStableWorldPos.xy, c);\n" + // najgrubszy tier pierwszy — det25/det05 wygrywają nad nim
        "      float det25Fade = 1.0 - smoothstep(uDet25FadeInner, uDet25FadeOuter, length(vStableWorldPos.xy - uDet25EyeXY));\n" +
        "      c = applyOrthoDetail(uOrthoDet25, uUseDet25, uDet25MinXY, uDet25MaxXY, uDetailBlendMeters, vStableWorldPos.xy, c, det25Fade);\n" + // stary per-tile path (fallback RGBA)
        "      c = applyOrthoDet25Arr(vStableWorldPos.xy, c);\n" + // krok 4: per-fragment det25 (BC1 array)
        "      c = applyOrthoDetail(uOrthoDet05, uUseDet05, uDet05MinXY, uDet05MaxXY, uDetailBlendMeters, vStableWorldPos.xy, c, 1.0);\n" + // static 5 cm mosaic fallback (non-streaming installs)
        "      c = applyOrthoDet05Array(vStableWorldPos.xy, c, uDetailBlendMeters);\n" + // streamed det05: every resident cell paints (KONTRAKT-ORTO)
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
        // COOL granite grey (2026-07-03): the warm brown-grey albedo read as mud; at full snow the steep
        // faces picked up the snow pass's cool-blue blend and "łapały zajebistą szarość" (user) — so the
        // rock wears that cool grey permanently, snow or not.
        "    vec3 rockCol = vec3(0.44, 0.46, 0.49) * (0.52 + 0.92 * rk);\n" +
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
        // REAL DEM curvature (baked AO in vColor.a) replaces the old random noise: seasonal snow
        // ACCUMULATES in concave gullies/couloirs (avalanche loading → line drops, holds steep) and is
        // WIND-SCOURED off convex ridges. A mapped stream channel (the firn water mask) is a full gully.
        "    float sConcave = clamp((1.0 - vColor.a) / 0.6, 0.0, 1.0);\n" +
        "    if (uFirnChannelOn > 0.5) {\n" +
        "      vec2 suv = (vStableWorldPos.xy - uTrailMaskMinXY) / uTrailMaskSizeXY;\n" +
        "      if (suv.x >= 0.0 && suv.x <= 1.0 && suv.y >= 0.0 && suv.y <= 1.0) {\n" +
        "        float swA = texture(uWaterMask, suv).r;\n" +
        "        if (swA > 0.003) { sConcave = max(sConcave, 1.0 - smoothstep(10.0, 32.0, (1.0 - swA) * uTrailMaxDist)); }\n" +
        "      }\n" +
        "    }\n" +
        // Warming weakens as the pack deepens: at the full slider every aspect is buried (uniform line →
        // solid white); a thin cover differentiates strongly by aspect/sun/wind (natural spring patchiness).
        "    float warmGate = 1.0 - uSnowStrength;\n" +
        // Effective LOCAL snowline (m a.s.l.). The weights are the physical knobs, in metres of snowline lift:
        //   aspect 260 m · sun-incidence 160 m · wind/curvature ±150 m.
        "    float effLine = uSnowLineZ + ((((southness * 260.0) + (sunInc * 160.0)) - (sConcave * 500.0) + ((1.0 - sConcave) * 120.0)) * warmGate);\n" +
        "    float snowH = smoothstep(effLine, effLine + uSnowBandZ, vStableWorldPos.z);\n" +
        // Mechanical shedding (NOT temperature): snow can't cling to steep faces / sharp ridges → bare rock.
        // n.z = cos(slope): 1 flat, 0 vertical. A crisp cut on the steeps leaves the sharp ridges bare.
        "    float slopeShed = smoothstep(uSnowSlopeCosBare - (0.30 * sConcave), uSnowSlopeCosFull - (0.20 * sConcave), nrm.z);\n" +
        // Deep snow BRIDGES small steep bumps (glacier-smooth, fewer rock specks); a thin cover still bares
        // every steep face. Lift the shed toward full-hold as the pack deepens (only the sharpest aretes stay bare).
        "    slopeShed = slopeShed + ((1.0 - slopeShed) * uSnowStrength * 0.5);\n" +
        "    snowMix = clamp(snowH * slopeShed, 0.0, 1.0) * uSnowStrength;\n" +
        // NB: the snow albedo is NOT baked into `base` here — snow gets its own dedicated bright/cool
        // lighting below (after `lit = base * lightSum`) so shadowed faces don't grey out.
        "  }\n" +
        // PERENNIAL FIRN ("lodowczyki") — mirrors the unit-tested PerennialFirn: above ~2000 m REAL the
        // snow presence stops being a function of altitude; N-facing / wall-enclosed cirques (Mięguszowiecki
        // Kocioł, Bandzioch, pod Rysami) hold multi-year patches EVEN AT SNOW SLIDER 0 (summer) — local mass
        // balance: wind/avalanche deposition + wall shade + latent-heat buffering. Sheltering is a WEIGHTED
        // SUM of northness and concavity (a flat Bandzioch floor has no own northness — its WALLS shade it,
        // carried by the concavity term = 1−AO from the vertex alpha). Independent of uSnowStrength.
        // V2 (photo-matched at Czarny Staw pod Rysami): CONCAVITY-LED — real patches are bright tongues in
        // couloirs/enclosed floors; bare northness alone stays UNDER the patch threshold (v1 glazed whole N
        // faces milky). The effective line sinks with concavity (uFirnDropZ — avalanche runout tongues live
        // lower than open ground), and the final smoothstep sharpens the film into discrete patches.
        // V3: the WHERE is DATA (FirnSiteData — the documented glacieret sites; feathered radial mask),
        // the procedure only shapes the tongue INSIDE a site (v2 placed plausible patches on the wrong
        // faces — real ones are a short curated list, like the lakes/summits gazetteers).
        "  if (uFirnStrength > 0.001 && uSlopeMode < 0.5 && uFirnSiteCount > 0.5) {\n" +
        "    float fSite = 0.0;\n" +
        "    float fCap = 1.0;\n" +
        "    for (int i = 0; i < 12; i++) {\n" +
        "      if (float(i) >= uFirnSiteCount) { break; }\n" +
        "      vec4 st = uFirnSites[i];\n" +
        "      float d = distance(vStableWorldPos.xy, st.xy);\n" +
        "      float m = 1.0 - smoothstep(st.z * 0.55, st.z, d);\n" +
        "      if (m > fSite) {\n" +
        "        fSite = m;\n" +
        "        fCap = 1.0 - smoothstep(st.w - 80.0, st.w, vStableWorldPos.z);\n" + // tongues live LOW: fade under the site cap; crags higher on the same wall stay bare
        "      }\n" +
        "    }\n" +
        "    if (fSite * fCap > 0.01) {\n" +
        "      vec3 nrmF = normalize(vNormal);\n" +
        "      float fNorth = max(0.0, nrmF.y);\n" +
        "      float fSouth = max(0.0, -nrmF.y);\n" +
        "      float fConcave = clamp((1.0 - vColor.a) / 0.6, 0.0, 1.0);\n" +
        // Amplified channel response (mirrors PerennialFirn): narrow gullies read only ~0.2-0.3 on
        // vertex-scale AO; safe to boost because the site mask + cap already confine the firn.
        "      float fBroad = smoothstep(0.20, 0.55, fConcave);\n" + // deep enclosed nooks only (raw AO)
                                                                     // Channel prior: the real tongues lie ALONG the mapped watercourses they feed (stream
                                                                     // proximity from the water-decal distance field = a full deposition channel).
                                                                     // Stream channel (the couloir slots): the PRIMARY driver — the real tongues are here, NOT on
                                                                     // the broad sun-melted apron. fChannel = 1 within ~25 m of a mapped stream, else 0.
        "      float fChannel = 0.0;\n" +
        "      if (uFirnChannelOn > 0.5) {\n" +
        "        vec2 fuv = (vStableWorldPos.xy - uTrailMaskMinXY) / uTrailMaskSizeXY;\n" +
        "        if (fuv.x >= 0.0 && fuv.x <= 1.0 && fuv.y >= 0.0 && fuv.y <= 1.0) {\n" +
        "          float fwA = texture(uWaterMask, fuv).r;\n" +
        "          if (fwA > 0.003) {\n" +
        "            float fwDist = (1.0 - fwA) * uTrailMaxDist;\n" +
        "            float fEdgeN = noiseT(vStableWorldPos.xy * 0.09);\n" + // ~10 m ragged edge
        "            float fSpread = clamp(((uFirnLineZ - vStableWorldPos.z) / 300.0) + 0.25, 0.0, 1.0);\n" + // splay wide downhill (avalanche fan), pinch up top
        "            float fOuter = 12.0 + (28.0 * fSpread) + (10.0 * fEdgeN);\n" +
        "            fChannel = (1.0 - smoothstep(8.0, fOuter, fwDist)) * (0.45 + (0.55 * fBroad));\n" + // wider in the concave couloir, pinches on a convex spur
        "          }\n" +
        "        }\n" +
        "      }\n" +
        "      float fDepo = max(fChannel, fBroad);\n" +
        "      float fLine = uFirnLineZ - (uFirnDropZ * fDepo);\n" +
        "      float fAlt = smoothstep(fLine - (uFirnBandZ * 0.5), fLine + (uFirnBandZ * 0.5), vStableWorldPos.z);\n" +
        "      float fShelter = clamp(fChannel + (fBroad * fNorth * 0.5) + (0.10 * fNorth) - (0.85 * fSouth), 0.0, 1.0);\n" +
        "      float fHold = smoothstep(uSnowSlopeCosBare - (0.25 * fDepo), uSnowSlopeCosFull - (0.20 * fDepo), nrmF.z);\n" + // channel firn is bed-anchored: holds far steeper (mirrors PerennialFirn)
        "      snowMix = max(snowMix, smoothstep(0.45, 0.72, fAlt * fShelter) * fHold * fSite * fCap * uFirnStrength);\n" +
        "    }\n" +
        "  }\n" +
        // B4 scorch: charred splats where fireballs hit the ground — an ALBEDO burn (light still plays over
        // it), session-persistent, ≤24 uniform splats (no texture plumbing; both terrain paths + the water
        // reflection get them for free). d² vs r² smoothstep = no sqrt; the char never goes pure black.
        "  float scorch = 0.0;\n" +
        "  for (int si = 0; si < 24; si++) {\n" +
        "    if (float(si) >= uScorchCount) { break; }\n" +
        "    vec2 dS = uScorchPos[si] - vStableWorldPos.xy;\n" +
        "    float d2S = dot(dS, dS);\n" +
        "    scorch += uScorchParam[si].y * (1.0 - smoothstep(uScorchParam[si].x * 0.2, uScorchParam[si].x, d2S));\n" +
        "  }\n" +
        "  base = mix(base, base * vec3(0.16, 0.14, 0.13), clamp(scorch, 0.0, 0.85));\n" +
        // BAKED-SHADOW ALBEDO correction (2026-07-11, user spec): the ortho carries the aerial photo's shadow
        // in its ALBEDO (a shadow baked into the texture, with the photo's exact shape at noon). Correcting the
        // final `lit` only brightens the symptom; the fix must edit the ALBEDO *before* lighting, so the render
        // lights corrected ground instead of amplifying a baked hole. Detect it from `base` alone: LOW luma AND
        // a COOL (cyan/teal) tint that normal dark ROCK lacks — normal shadow-rock is neutral (R≈G≈B), a baked
        // photo shadow crushes R and lifts G/B. Correct = neutralise toward grey + lift luma. `uBakedShadowComp`
        // = strength (0 disables). The mask + corrected albedo are exposed to the debug views (uDebugTerrainView).
        "  float baseLuma = dot(base, vec3(0.299, 0.587, 0.114));\n" +
        "  float bsCoolness = ((base.g + base.b) * 0.5) - base.r;\n" +                       // cyan/teal amount (R deficit)
        "  float bsDark = 1.0 - smoothstep(0.12, 0.48, baseLuma);\n" +                       // 1 where albedo DARK
        "  float bsCoolGate = smoothstep(0.04, 0.14, bsCoolness);\n" +                       // was this a COOL baked shadow (rock ≈0)?
        "  float bsMask = bsDark * bsCoolGate * uBakedShadowComp;\n" +
        // Correct toward the LOCAL MATERIAL — scree/rock is WARM-NEUTRAL, NOT green (user 2026-07-11: this is a
        // rock cirque, not forest). Pull the cool G,B excess down toward R's warm level (removes the cyan/teal
        // cast, keeps the material's own warmth), then LIFT the luminance so the baked shadow reads as *darker
        // scree/rock*, not a green carpet or a dark colour blob. All gated to dark+cool → sunlit ground, bright
        // ortho and genuinely-neutral dark rock are untouched. Colour of the BLUE forest shadows stays the
        // ortho de-blue's job (§3.13, self-gating, idempotent). uBakedShadowComp = strength (F6 cycles 0/0.5/1.0).
        "  vec3 bsWarm = vec3(base.r, mix(base.g, base.r, 0.7 * bsMask), mix(base.b, base.r, 0.85 * bsMask));\n" +
        "  vec3 bsCorrected = clamp(bsWarm * (1.0 + (1.3 * bsMask)), 0.0, 1.0);\n" +
        "  vec3 baseCorrected = bsCorrected;\n" +
        "  vec3 lit = baseCorrected * lightSum;\n" +
        // Snow shading (dedicated): high albedo + sky/multiple scattering keeps snow BRIGHT and COOL-BLUE in
        // shadow (real snow shadows are blue, not grey), driven by the sun (not the camera) so orbiting never
        // changes it, and scaling with uSkyAmbient so night snow dims. WINTER FORM: the sun↔shadow contrast
        // is deepened so the snow shows its 3-D shape instead of a flat white sheet — the ambient floor is
        // pulled DOWN (×0.65) for darker-but-still-blue shadows, and the direct-sun term is boosted (×1.4) so
        // lit slopes pop to bright white. The two knobs: floor down / sun up = more relief, but watch for grey.
        "  if (snowMix > 0.001) {\n" +
        // 2026-07-07: at snow 100% the cover blew out to a flat white sheet — snow starts near-white (albedo
        // ~1.0) so the ×1.4 sun boost + noon lift already clip it to 1.0, and the ACES exposure 1.15 (66bcb4a)
        // then has no headroom left (dark terrain does, snow doesn't). Give snow headroom BELOW 1.0 pre-tonemap:
        // lower albedo (0.96→0.88) + gentler sun boost (1.4→1.15) + smaller noon lift — the ACES roll-off keeps
        // the highlight detail instead of a solid white clip. Still bright, still cool-blue in shadow.
        "    vec3 snowAlbedo = vec3(0.80, 0.82, 0.86);\n" +
        "    vec3 snowLit = snowAlbedo * ((uSkyAmbient * 0.45) + (uSunColor * sunlit * 1.15));\n" +   // ambient 0.65→0.45: deeper shadow = 3-D relief, not a flat white sheet
        "    snowLit += snowAlbedo * fireGlow * 0.8;\n" + // snow has its own lighting path — the fire glow must reach it too
        "    snowLit = mix(snowLit, vec3(0.92), uNoonSnowLift * 0.30);\n" +   // intense midday → pop toward bright (not pure) white
        "    lit = mix(lit, min(snowLit, vec3(1.0)), snowMix);\n" +
        "  }\n" +
        "  float dist = length(vWorldPos - uCameraPos);\n" +
        "  float fogAmount = 1.0 - exp(-dist * uFogDensity);\n" +
        // Snow keeps NEAR detail crisp, but DISTANT snowfields pick up cool aerial perspective — they fade
        // into the horizon haze like the real range, instead of staying a hard white cut-out. Only a mild
        // reduction (was a near-full block), so close snow is still sharp while far snow reads as luminous distance.
        "  fogAmount *= (1.0 - 0.65 * snowMix);\n" +   // 0.35→0.65 (2026-07-07): snow was fading into the bright uFogColor = a milky white haze over the whole cover; let snow keep its own tone
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
                                                                   // one pixel of distance change → screen-constant AA. CLAMPED: under heavy minification (the decal
                                                                   // window now spans 8.5 km, so its far parts are many texels per screen pixel) fwidth explodes and
                                                                   // the AA band + the px width floor ballooned trails into ~30 m ribbons ("super grube"). The cap
                                                                   // bounds halfWidth at ~2.6× base; past that the mipmapped mask just fades the line out with distance.
        "      float aa = clamp(fwidth(distM), 0.001, uTrailHalfWidth * 1.6);\n" +
        "      float halfWidthM = max(uTrailHalfWidth, aa * 1.1);\n" + // 1.1 px floor: true 1.6 m scale near, constant-px legible far
        "      float coverage = 1.0 - smoothstep(halfWidthM - aa, halfWidthM + aa, distM);\n" +
        // Cartographic CASING: a light rim around the colour core, from the same distance field. Without it a
        // BLACK trail painted onto dark shadowed rock is invisible (czarny szlak w żlebie Kulczyńskiego "hidden
        // under the relief" — it was drawn, just black-on-black; the 3D line overlay z-fights away exactly
        // there, so the decal must carry it alone). The rim also crisps every other colour on busy ortho.
        "      float rimW = halfWidthM * 2.0;\n" + // tighter casing (was 2.3) — with the halved core the wide rim read as sloppy halo
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
        // Casing ONLY under DARK lines: it exists so a BLACK trail stays readable on dark shadowed rock (żleb
        // Kulczyńskiego); under yellow/green/red it read as a sloppy grey halo ("obwódki są chujowe").
        "      float lineLum = dot(tc.rgb, vec3(0.299, 0.587, 0.114));\n" +
        "      float rimNeed = 1.0 - smoothstep(0.16, 0.40, lineLum);\n" +
        "      lit = mix(lit, vec3(0.94, 0.94, 0.90), rim * 0.55 * uTrailStrength * rimNeed);\n" +
        "      lit = mix(lit, tc.rgb, coverage * uTrailStrength);\n" +
        "    }\n" +
        "  }\n" +
        "  fragColor = vec4(mix(lit, uFogColor, fogAmount), 1.0);\n" +
        // DEBUG VIEWS (2026-07-11, user request F1–F5): isolate the baked-shadow pipeline stages so we can SEE
        // whether the cyan lives in the albedo or the lighting. 0=final, 1=albedo, 2=baked-shadow mask,
        // 3=corrected albedo, 4=lightSum. Pre-fog, pre-overlay so each stage is raw.
        "  if (uDebugTerrainView > 0.5) {\n" +
        "    if (uDebugTerrainView < 1.5) { fragColor = vec4(base, 1.0); }\n" +                    // F2 albedo
        "    else if (uDebugTerrainView < 2.5) { fragColor = vec4(vec3(bsDark), 1.0); }\n" +       // F3 lowLuma sub-mask
        "    else if (uDebugTerrainView < 3.5) { fragColor = vec4(vec3(bsCoolGate), 1.0); }\n" +   // F4 cool/cyan sub-mask
        "    else if (uDebugTerrainView < 4.5) { fragColor = vec4(vec3(bsDark * bsCoolGate), 1.0); }\n" + // F5 combined mask (pre-comp)
        "    else { fragColor = vec4(baseCorrected, 1.0); }\n" +                                    // F6 corrected albedo
        "    return;\n" +
        "  }\n" +
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
        "uniform vec2 uCloudSeed;\n" +      // slider-derived pattern offset: moving the slider re-rolls the cirrus field
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
        // CIRRUS REMOVED (2026-07-05, user): the procedural high wisps ("postrzępione wysokie") clashed with
        // the volumetric cumulus + sea-of-clouds look — the sky stays a clean gradient; ALL clouds now come
        // from the cumulus billboards and the inversion sheet, both on the 1500 m deck and slider-gated.
        // (uCloudCoverage/uCloudSeed/uCloudDrift/uCloudDark stay declared; the driver reports their locations
        // as -1 and the renderer's uniform pushes become silent no-ops.)
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

    // ACES filmic tonemap (Narkowicz fit) shared by BOTH final passes — the composite and this
    // pass-through — so the scene's character does not flip when bloom/god rays toggle. Linear clamp
    // burned the sun to flat white and crushed shadows to black; the filmic shoulder/toe keeps both
    // readable. uTonemap is the A/B lever (0 = legacy linear), uExposure the pre-curve gain.
    private const string AcesGlsl =
        "uniform float uTonemap;\n" +
        "uniform float uExposure;\n" +
        "vec3 aces(vec3 x){\n" +
        "  x *= uExposure;\n" +
        "  return clamp((x * ((2.51 * x) + 0.03)) / ((x * ((2.43 * x) + 0.59)) + 0.14), 0.0, 1.0);\n" +
        "}\n";

    private const string PostFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vUv;\n" +
        "uniform sampler2D uTex;\n" +
        "out vec4 fragColor;\n" +
        AcesGlsl +
        "void main(){\n" +
        "  vec3 c = texture(uTex, vUv).rgb;\n" +
        "  fragColor = vec4(mix(c, aces(c), uTonemap), 1.0);\n" +
        "}\n";

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
        AcesGlsl +
        "void main(){\n" +
        "  vec3 sc = texture(uScene, vUv).rgb;\n" +
        "  vec3 bl = texture(uBloom, vUv).rgb;\n" +
        "  vec3 gr = texture(uGodray, vUv).rgb;\n" +
        "  vec3 hdr = sc + (bl * uIntensity) + (gr * uGodrayIntensity);\n" + // tonemap AFTER bloom/rays add light — the filmic shoulder rolls the sum off
        "  fragColor = vec4(mix(hdr, aces(hdr), uTonemap), 1.0);\n" +
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

    // B3 heat haze: refract the resolved scene wherever the half-res heat mask says hot air rises — a
    // noise-gradient offset scrolling UP (convection) scaled by local heat, with a slight chromatic split.
    // Runs FIRST in the post chain so the bloom blooms the already-distorted image. Reuses PostVertexShaderSource.
    private const string HazeFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec2 vUv;\n" +
        "uniform sampler2D uScene;\n" +
        "uniform sampler2D uHeat;\n" +
        "uniform float uTime;\n" +
        "uniform float uHazeStrength;\n" +
        "out vec4 fragColor;\n" +
        "float hz(vec2 p){ vec3 p3=fract(vec3(p.xyx)*0.1031); p3+=dot(p3,p3.yzx+33.33); return fract((p3.x+p3.y)*p3.z); }\n" +
        "float vnz(vec2 p){ vec2 i=floor(p),f=fract(p); vec2 u=f*f*(3.0-2.0*f);\n" +
        "  return mix(mix(hz(i),hz(i+vec2(1,0)),u.x), mix(hz(i+vec2(0,1)),hz(i+vec2(1,1)),u.x), u.y); }\n" +
        "void main(){\n" +
        "  float heat = texture(uHeat, vUv).r;\n" +
        "  if (heat < 0.004) { fragColor = vec4(texture(uScene, vUv).rgb, 1.0); return; }\n" +
        "  vec2 q = vUv * vec2(64.0, 40.0);\n" +
        "  q.y -= uTime * 3.0;\n" + // the ripple pattern climbs — hot air convects upward
        "  vec2 grad = vec2(vnz(q) - vnz(q + vec2(1.3, 0.0)), vnz(q + 5.7) - vnz(q + vec2(0.0, 1.3) + 5.7));\n" +
        "  vec2 offs = grad * (uHazeStrength * min(heat, 1.6));\n" +
        "  vec3 col;\n" +
        "  col.g = texture(uScene, vUv + offs).g;\n" +
        "  col.r = texture(uScene, vUv + (offs * 1.15)).r;\n" + // slight chromatic split sells the refraction
        "  col.b = texture(uScene, vUv + (offs * 0.85)).b;\n" +
        "  fragColor = vec4(col, 1.0);\n" +
        "}\n";

    // Shadow depth pass: transform the terrain vertex (absolute world aPos) by a cascade's light
    // view-projection and write depth only. No colour output — the FBO has just a depth texture.
    // Carries the world position through so the fragment stage can apply SURFACE OWNERSHIP: without it the
    // base skin — 0.5–4 m ABOVE the true z16 surface on convex slopes — won the depth-to-sun everywhere it
    // was drawn, so the SHADOW SHAPES came from the smooth base while the main pass showed the detailed rock
    // ("cień generowany na bazie", user-diagnosed). The same coverage mask the main pass uses now discards
    // base-skin fragments here too — shadows are cast by whatever surface actually OWNS the ground.
    private const string ShadowDepthVertexShaderSource =
        "#version 300 es\n" +
        "layout(location=0) in vec3 aPos;\n" +
        "uniform mat4 uLightVp;\n" +
        "out vec3 vWorldPos;\n" +
        "void main(){ vWorldPos = aPos; gl_Position = uLightVp * vec4(aPos, 1.0); }\n";

    private const string ShadowDepthFragmentShaderSource =
        "#version 300 es\n" +
        "precision highp float;\n" +
        "in vec3 vWorldPos;\n" +
        "uniform sampler2D uBaseCover;\n" +
        "uniform vec2 uBaseCoverMinXY;\n" +
        "uniform vec2 uBaseCoverSizeXY;\n" +
        "uniform float uBaseCoverOn;\n" +
        "uniform float uIsBaseSkin;\n" +
        "void main(){\n" +
        "  if (uIsBaseSkin > 0.5 && uBaseCoverOn > 0.5) {\n" +
        "    vec2 cuv = (vWorldPos.xy - uBaseCoverMinXY) / uBaseCoverSizeXY;\n" +
        "    if (cuv.x > 0.0 && cuv.x < 1.0 && cuv.y > 0.0 && cuv.y < 1.0\n" +
        "        && texture(uBaseCover, cuv).r > 0.5) { discard; }\n" +
        "  }\n" +
        "}\n";

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
        "uniform float uCoverage;\n" +     // Zachmurzenie slider: per-instance hash gate (which puffs exist)
        "uniform float uMemberSeed;\n" +   // re-rolled per ~8% slider step — moving the slider swaps WHICH puffs show
        "out vec2 vCard;\n" +
        "out float vSeed;\n" +
        "out vec3 vWorldPos;\n" +
        "void main(){\n" +
        // Membership gate: a stable per-puff hash + slider-stepped seed vs the coverage threshold. A hidden
        // puff collapses to a zero-area quad (no fragments). This replaces the old \"draw the first N
        // instances\" count — raising/lowering the slider now materialises DIFFERENT puffs in scattered
        // places instead of growing/shrinking the same fixed field.
        "  float member = fract(fract(sin(dot(aOffset.xy, vec2(12.9898, 78.233))) * 43758.5453) + uMemberSeed);\n" +
        "  float show = step(member, uCoverage);\n" +
        "  float s = aSizeSeed.x * show;\n" +
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
        "uniform sampler2D uSceneDepth;\n" + // scene depth blitted just before the ghost pass (see EnsureGhostDepthTarget)
        "uniform float uSceneDepthOn;\n" +   // 1 = uSceneDepth is valid this frame; 0 = no depth info (ungated ghost)
        "uniform vec2 uDepthNearFar;\n" +    // ACTIVE projection near/far (metres) — needed to linearize depths
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
        // ROCK-THICKNESS GATE (2026-07-03): DepthFunc(GREATER) alone cannot tell "3 m behind a rib" from
        // "behind the whole massif" — every occluded trail bled through distant slopes as dotted twins
        // ("jakby były dwa szlaki"). With the scene depth available we measure HOW FAR behind the visible
        // surface the fragment lies, in metres: ghost full when buried < 25 m (żleb Kulczyńskiego: the trail
        // sits just past the near wall), gone past 60 m (another slope entirely). Depth-buffer values invert
        // through the ACTUAL projection mapping (System.Numerics D3D-style clip z in [0,1] → window depth in
        // [0.5,1]): ndc = 2*D - 1, linear = f*n / (f - ndc*(f-n)) — exact for both samples, so the convention
        // cancels out of neither (verified: D=0.5 → near, D=1 → far).
        "  float ghostGate = 1.0;\n" +
        "  if (uGhostFade < 0.999 && uSceneDepthOn > 0.5) {\n" +
        "    vec2 duv = gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0));\n" +
        "    float n = uDepthNearFar.x; float f = uDepthNearFar.y;\n" +
        "    float ndcS = texture(uSceneDepth, duv).r * 2.0 - 1.0;\n" +
        "    float ndcF = gl_FragCoord.z * 2.0 - 1.0;\n" +
        "    float linS = (f * n) / (f - ndcS * (f - n));\n" +
        "    float linF = (f * n) / (f - ndcF * (f - n));\n" +
        "    ghostGate = 1.0 - smoothstep(25.0, 60.0, linF - linS);\n" +
        "  }\n" +
        "  fragColor = vec4(mix(vColor.rgb, uFogColor, fog), vColor.a * uGhostFade * ghostGate);\n" +
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
        "uniform vec3 uCameraPos;\n" +
        "uniform float uMaxDist;\n" +
        "out vec4 vColor;\n" +
        "out vec3 vWorldPos;\n" +
        "void main(){\n" +
        "  vColor = aColor;\n" +
        "  vWorldPos = aPos;\n" +
        // The full trail network spans ~27×42 km. Fragment-stage distance discard still rasterized every
        // far ribbon and cost ~28 ms in the F9 flight. Reject a segment before projection/rasterization when
        // both endpoints are outside the same radius; the fragment gate remains for the soft edge and for a
        // segment crossing into the radius.
        "  float distA = distance(aPos, uCameraPos);\n" +
        "  float distB = distance(aOther, uCameraPos);\n" +
        "  if (min(distA, distB) > uMaxDist) { gl_Position = vec4(2.0, 2.0, 2.0, 1.0); return; }\n" +
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
    private const float GhostWidthScale = 0.65f;   // x-ray ghost draws narrower than the solid line ("cieńsze nieco")
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

    // User-imported off-trail ("pozaszlaki") track ribbon: a distinct hot magenta so it never reads as a PTTK
    // trail (red/blue/green/yellow/black), the violet route, or the grey roads. Solid-ish with alpha so it
    // softens the near-field z-fight the same way trails do; width sits between a trail thread and a road.
    private const byte OffTrailR = 0xFF;
    private const byte OffTrailG = 0x3D;
    private const byte OffTrailB = 0xAE;
    private const byte OffTrailAlpha = 0xE0;
    private const float OffTrailHalfWidthPx = 1.5f;

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
    private const float OffTrailLiftMeters = 0.7f;        // a hair above trails so a coincident imported track sits on top
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
    // 0 = trail decal DISABLED (2026-07-03, user decision). The surface-painted band self-occludes on the
    // rough 1 m relief at ANY oblique view — it chopped into fat dashes riding beside the ribbon, reading as
    // a phantom second trail ("jakby były dwa szlaki"; verified by the magenta bisection + an own-screenshot
    // A/B with the decal hard-gated off: dashes gone, single clean ribbon). The ribbon (thin line + thicker
    // black variant) is THE trail representation; the mask build stays alive because the water field ships
    // in the same texture. Restore a value > 0 only with a fix for oblique self-occlusion.
    private const float TrailDecalStrength = 0f;             // blend strength of the decal over the surface colour
    private const float TrailDecalHalfWidthMeters = 0.8f;    // on-surface half-width (world m) — halved 2026-07-02 ("muszą być węższe o połowę co najmniej"); 1.6 read as a fat band with its 2.3× rim at close zoom
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
    private enum GpuPass { Shadow, Reflection, Terrain, LakesForest, Dragons, Lines, Clouds, Post, Count }
    private const int GpuFramesInFlight = 3;
    private const QueryTarget GlTimeElapsedExt = (QueryTarget)0x88BF; // GL_TIME_ELAPSED_EXT
    private const GLEnum GlGpuDisjointExt = (GLEnum)0x8FBB;           // GL_GPU_DISJOINT_EXT
    private uint[]? gpuQueries;                                       // [(int)GpuPass.Count * GpuFramesInFlight]
    private readonly double[] lastPassMs = new double[(int)GpuPass.Count];
    // CPU wall-time twins of the GPU pass timers (command-recording cost) + the pre-pass "setup" bucket
    // (uploads / Ensure* work between Render() entry and the first GpuBegin). Always on — cheap timestamps.
    private readonly double[] lastPassCpuMs = new double[(int)GpuPass.Count];
    private GpuPass cpuPassActive;
    private long passCpuStartTs;
    private long renderStartTs;
    private bool renderFirstPassSeen;
    private double renderSetupCpuMs;
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

    // H5 (2026-07-23): mirror-content cull radius. Reflections at half-res + ripple wobble resolve only the
    // near walls; beyond this the reflected terrain is sub-pixel at the water horizon. 8 km is generous for
    // every Tatra tarn (Morskie Oko's far wall is < 2 km) while cutting the whole-massif tile ring ~10×.
    private const float ReflectionMaxDistanceMeters = 8000f;
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

    /// <summary>
    /// When <c>true</c> (the view sets it during the continuous walk/dragon modes), the mirrored-terrain
    /// reflection pre-pass runs every SECOND frame and the water samples the previous frame's texture in
    /// between. Measured cost of the pass: ~8–12 ms GPU + ~5–7 ms CPU per frame; the ripple-distorted lake
    /// reflection reads perfectly well one frame stale at 20–60 fps.
    /// </summary>
    public bool ThrottleReflection { get; set; }

    private bool reflectionValidLastFrame; // last frame left a valid reflection texture (reuse gate)

    /// <summary>
    /// When enabled for a continuous camera mode, cascaded shadow maps refresh every second frame.
    /// The skipped frame reuses both the previous depth maps and their matching light matrices.
    /// </summary>
    public bool ThrottleShadows { get; set; }

    private bool shadowValidLastFrame;

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
    private int debugTerrainViewLocation = -1;

    /// <summary>Baked-shadow debug view: 0=final, 1=albedo, 2=mask, 3=corrected albedo, 4=lightSum (F1–F5).</summary>
    public float DebugTerrainView { get; set; }
    private int orthoMinXyLocation = -1;
    private int orthoMaxXyLocation = -1;
    private int orthoBlendLocation = -1;
    // Hi-res ortho detail overlay (PoC) — uniform locations + two resident mosaic textures with lon/lat AABB.
    private int det25SamplerLocation = -1;
    private int det05SamplerLocation = -1;
    private int useDet25Location = -1;
    private int useDet05Location = -1;
    private int det25MinXyLocation = -1;
    private int det25MaxXyLocation = -1;
    private int det05MinXyLocation = -1;
    private int det05MaxXyLocation = -1;
    private int det05ArrSamplerLocation = -1;   // sampler2DArray slice A on unit 12 (layers 0..7)
    private int det05ArrBSamplerLocation = -1;  // sampler2DArray slice B on unit 13
    private int det05ArrCSamplerLocation = -1;  // sampler2DArray slice C on unit 7 (trzecia tablica — 192 cele)
    private int det05ArrALoc = -1;              // uDet05ArrLayers — warstw NA TABLICĘ (mapping slot→(tablica, warstwa))
    private int det05CellHashLoc = -1;
    private int det05HashSeedLoc = -1;
    private int det05GridOriginLoc = -1, det05GridPitchLoc = -1, det05CellSizeLoc = -1;
    private int useDet05ArrLocation = -1;
    private int detailBlendLocation = -1;
    private int detailColorModeLocation = -1;
    private int toneHarmLoc = -1;               // diagnostyka: 0 = wyłącz SAMĄ harmonizację tonu (de-blue zostaje)
    private int toneDebugLoc = -1;              // diagnostyka: mapa korekty tonu zamiast koloru
    private int det05ArrRawLocation = -1;       // H3: per-layer colour split (det05 array RAW vs det25/base de-blue)
    private int detailDebugBoundsLocation = -1;
    private int det25EyeXyLocation = -1;
    private int det25FadeInnerLocation = -1;
    private int det25FadeOuterLocation = -1;
    private uint orthoDet25Texture;
    private uint orthoDet05Texture;
    private bool det25GeoSet;
    private bool det05GeoSet;
    private MapaTur.Domain.Geography.GeoPoint det25GeoSw, det25GeoNe, det05GeoSw, det05GeoNe;
    private byte[]? pendingDet25Rgba; private int pendingDet25W, pendingDet25H;
    private byte[]? pendingDet05Rgba; private int pendingDet05W, pendingDet05H;
    private volatile bool orthoDetailUploadPending;
    private const float OrthoDetailBlendMeters = 8f; // soft edge fade of the detail AABB back to the base ortho
    /// <summary>PoC master switch for the hi-res ortho detail overlay. When false the shader path is a strict no-op.</summary>
    public bool OrthoDetailEnabled { get; set; } = true;

    /// <summary>
    /// Detail colour variant: 0 = raw detail, 1 = the base ortho's de-blue transform (§3.13 — the ACCEPTED
    /// shadow correction: burnt-in flight shadows must never reach the screen, our CSM generates the shadows).
    /// DEFAULT = 1 (corrected) — the HARD rule (2026-07-16): every ortho layer, present and future, ships with
    /// the shadow correction ON; raw (0, key '9') exists only as a diagnostic A/B view. The raw det25/det05
    /// overlay shipping uncorrected next to the corrected base is exactly the blue-vs-green patchwork the rule
    /// exists to prevent.
    /// </summary>
    public int OrthoDetailColorMode { get; set; } = 1;

    /// <summary>H3 (2026-07-23): when true, the STREAMED det05 array cells skip the mode-1 shader de-blue —
    /// for the deshadow preview whose cells are data-side corrected (V2) and must not be corrected twice —
    /// while det25/base/mosaic keep the full mode-1 transform. False = original single-mode behaviour.</summary>
    public bool Det05ArrayRawColor { get; set; }

    /// <summary>Diagnostics: outline the detail cell AABB edges (magenta) so cell boundaries are visible.</summary>
    public bool OrthoDetailDebugBounds { get; set; }

    // ── Hi-res ortho detail STREAMING (ARCHITEKTURA-STREAMING, produkcja .opk-only) ─────────────────────────
    // det25/det05 read GPU-ready BC1+mip chains from prebaked .opk packages off the paint thread, then use the
    // existing bounded strip-upload on GL. Runtime never decodes WebP, composes RGBA cells, encodes BC1 or writes
    // a cache. Missing/corrupt packs degrade to the lower resident tier; they never start image production.
    private MapaTur.Application.Terrain.OrthoDetailGrid? det25Grid;
    private MapaTur.Application.Terrain.OrthoDetailResidencyPolicy? det25Policy;
    private readonly Dictionary<int, DetailCellGpu> det25Cells = new();
    private readonly List<int> det25UploadQueue = new();
    private long det25FrameTick;
    private long det25ResidentBytes;
    private uint det25BoundTexture;             // last cell texture bound to unit 10 in the per-tile loop (dedup)
    private Vector3 det25PrevTarget;
    private bool det25PrevTargetValid;
    private double det25PrevClockMs = -1;
    private int det25LastDesired;               // diagnostics: desired cell count last Update
    private long det05MosaicResidentBytes;      // VRAM of the static 5 cm MO mosaic — part of the SHARED budget
    private double det25EyeLat, det25EyeLon;     // diagnostics: camera focus this frame
    private int det25FocusCi, det25FocusCj;      // diagnostics: focus cell (which the ring centres on)
    private MapaTur.Domain.Geography.GeoPoint? det25FocusOverride; // MAPATUR_DET25_FOCUS=lat,lon — force the ring focus (perf measurement over a known-covered spot regardless of camera)
    private int det25ReadInFlight;
    private const int DetailMaxConcurrentReads = 4;
    // Desktop caps raised 2026-07-20 ("pół Mnicha rozmyte — nie starcza puli"): a 64 GB / discrete-GPU
    // desktop can hold far more detail than the old one-size caps; phones keep the conservative values.
    // TIER REBALANCE (2026-07-23, user: "po co mi hi res na 10% ekranu skoro wszędzie indziej mam rozmytą
    // kupę?"): the budget used to buy a perfect 5 cm puddle (16 × 357 MB, 800 m) while the MIDGROUND of every
    // panorama (1–5 km) fell to the ~2–4 m/px base. 25 cm at 2 km already out-resolves the screen, so the whole
    // frame reads "sharp" when det25 covers it: det25 12 → 28 cells / 1.5 → 5 km (2.5 GB), det05 back to 12
    // (4.3 GB) — together with the ~2.8 GB base inside the 9.6 GB hardware-derived ledger.
    private static readonly int Det25HardCapCells = OperatingSystem.IsWindows() ? 128 : 8; // = Det25ArrLayers; 128 cel × 768 m ⇒ ~4,9 km średniego dystansu
    private static readonly double Det25RingRadiusMeters = OperatingSystem.IsWindows() ? 5000.0 : 1500.0;
    private const double Det25FastMotionSpeedMps = 25.0; // above this the ring is suppressed (dragon flight)
    private const double Det25PrefetchLeadMeters = 400.0;
    private const double Det25UploadBudgetMsPerFrame = 4.0; // its own strip-upload budget, on top of base ortho's 6 ms

    // ── Hi-res ortho detail STREAMING — det05 (5 cm) SECOND LEVEL (unit 11) ──────────────────────────────────
    // Parallel to the det25 machinery above but on unit 11, coverage-gated (5 cm exists only on a partial strip),
    // and coordinated with det25 against the ONE shared budget by TwoLevelDetailResidencyPolicy (det05>det25>base,
    // no-hole reserve). Default ON (MAPATUR_DET05_STREAM=0 forces the static MO showcase fallback). When ON,
    // the static mosaic is not loaded and streamed det05 owns unit 11 (finest-wins over det25).
    private bool det05StreamOn;
    private MapaTur.Application.Terrain.OrthoDetailGrid? det05Grid;
    private MapaTur.Application.Terrain.TwoLevelDetailResidencyPolicy? twoLevelPolicy;
    private readonly Dictionary<int, DetailCellGpu> det05Cells = new();
    private readonly List<int> det05UploadQueue = new();
    private long det05ResidentBytes;
    private int det05ReadInFlight;
    private int det05LastDesired;
    private const int Det05CoverageTiles = 16;  // 8192² cell (409.6 m @ 0.05 m) — 128 m margin, seam-safe for z17
    // Desktop: 12 × ≈357 MB ≈ 4.3 GB of cells — split across TWO array textures (see
    // Det05ArraySliceLayers): a SINGLE 12-layer 8192² array with mips is ≈4.295 GB, just past the 32-bit
    // per-RESOURCE ceiling (~4.294 GB) of D3D11/driver size fields — the allocation failed SILENTLY
    // (nothing checked glGetError) and the sticky error poisoned terrain-tile allocations (white holes,
    // looping "doczytywanie", 2026-07-20). The card was never full — a 16 GB GPU died on one oversized
    // resource. Each slice stays ≤8 layers ≈2.86 GB; EnsureDet05Array verifies every allocation.
    // H2 (2026-07-23) raised this 12 → 16; the TIER REBALANCE same day pulls it back to 12 — the freed
    // ~1.4 GB funds the det25 midground (see Det25HardCapCells), which is what a panorama actually shows.
    // The slot list/slices still support 16 if a future budget wants it. Phone untouched.
    // 2026-07-24 („zasięg 5 cm śmiesznie mały", potem „proszę o to już 3 dzień"): 12 → 32 → 48.
    // Kalibrowane, gdy cela kosztowała 357 MB RGBA; z BC1 (ChainSize ≈ 44,7 MB) 48 cel = ~2,1 GB w
    // ledgerze ~9,6 GB. Promień pełnego 5 cm ~600 m (pitch 153,6 m, nearest-cap); dalej kryje det25.
    // 2026-07-25: PRZYWRÓCONE 96 na WYRAŹNE ŻĄDANIE USERA. Wczoraj 21:06 (2317682) agent SAM cofnął próbę
    // 96 do 48, bo zmierzył terrain 18,7 ms / 31 ms sumGpu (~32 FPS) — mimo że user właśnie oglądał stan 96
    // i był dla niego dobry. To był konflikt „obraz vs płynność", którego zasada 3 NIE pozwala rozstrzygać
    // agentowi. NIE OBNIŻAĆ bez werdyktu usera. Docelowo koszt znika po O(1) wyborze celi (krata→slot).
    private static readonly int Det05HardCapCells = OperatingSystem.IsWindows() ? 192 : 3;
    private const int Det05CellHashSize = 384;   // 2× desktop cap; bounded open addressing at 50% load
    private const int DetailCellHashMaxProbe = 12;
    private const byte Det05TailFirstMinimumLod = 2; // 5 cm × 2² = 20 cm: fast first-visible stage
    // c8102e9 runtime gate (2026-07-31): compact L2 tail naprawił I/O (22-88 ms/celę), ale seryjne
    // promowanie 192 slice'ów trwało 35,2 s, a fine stage kolejne 22,0 s. Format i narzędzia offline
    // zostają, natomiast aktywny runtime wraca do pojedynczego pełnego odczytu + promocji na celę.
    private static readonly bool Det05TailFirstRuntimeEnabled = false;

    // BC1 GPU-cell pipeline (2026-07-23, ZASADY 11/13): cells are encoded to BC1+mips OFF-THREAD (once — the
    // disk cache serves every revisit in ~15 ms) and uploaded compressed. 1/8 the bytes end-to-end: a det05
    // cell drops 357 → ~45 MB (VRAM ledger + PCIe + disk alike). Probed once per context; s3tc absent
    // (never on desktop ANGLE) → the RGBA path below still runs unchanged.
    // 0x83F1 = COMPRESSED_RGBA_S3TC_DXT1_EXT (DXT1a, punch-through alpha): ten sam koszt 8 B/blok co RGB,
    // ale texel może być przezroczysty — brzeg pokrycia celi przepuszcza bazę zamiast malować czerń
    // (regresja „czarnych dziur" 2026-07-23; RGB-wariant 0x83F0 porzucony).
    private const uint GlCompressedRgbS3tcDxt1 = 0x83F1;
    private bool det05Bc1On;
    private bool s3tcProbed;

    /// <summary>Katalog produkcyjnych pakietów `.opk` det25. Brak/nieczytelny indeks wyłącza tę
    /// warstwę i odsłania det1m/bazę; runtime nigdy nie komponuje zastępczych cel z WebP.</summary>
    public string? Det25OpkDir { get; set; }

    /// <summary>Katalog produkcyjnych pakietów `.opk` det05 — ten sam kontrakt co
    /// <see cref="Det25OpkDir"/>.</summary>
    public string? Det05OpkDir { get; set; }

    private MapaTur.Application.Terrain.OrthoPackIndex? det25OpkIndex;
    private bool det25OpkProbed;
    private MapaTur.Application.Terrain.OrthoPackIndex? det05OpkIndex;
    private bool det05OpkProbed;

    private bool Det05OpkReady()
    {
        if (!det05OpkProbed)
        {
            det05OpkProbed = true;
            if (!string.IsNullOrEmpty(Det05OpkDir))
            {
                det05OpkIndex = MapaTur.Application.Terrain.OrthoPackIndex.Load(
                    System.IO.Path.Combine(Det05OpkDir, "index.bin"));
                Log.Information("[Det05] .opk page streaming {State} ({Dir}: {Cells} grup)",
                    det05OpkIndex is null ? "OFF — index.bin nieczytelny/brak, fallback do niższego LOD" : "ON",
                    Det05OpkDir, det05OpkIndex?.Cells.Count ?? 0);
            }
        }

        return det05OpkIndex is not null && det05Bc1On;
    }

    // Krok 6: strony .opk są jedyną produkcyjną ścieżką det25, gdy indeks jest czytelny i GPU ma s3tc
    // (BC1 chain idzie przez CompressedTexSubImage; brak s3tc → RGBA compose jak dotąd).
    private bool Det25OpkReady()
    {
        if (!det25OpkProbed)
        {
            det25OpkProbed = true;
            if (!string.IsNullOrEmpty(Det25OpkDir))
            {
                det25OpkIndex = MapaTur.Application.Terrain.OrthoPackIndex.Load(
                    System.IO.Path.Combine(Det25OpkDir, "index.bin"));
                Log.Information("[Det25] .opk page streaming {State} ({Dir}: {Cells} grup)",
                    det25OpkIndex is null ? "OFF — index.bin nieczytelny/brak, fallback do det1m/bazy" : "ON",
                    Det25OpkDir, det25OpkIndex?.Cells.Count ?? 0);
            }
        }

        return det25OpkIndex is not null && det05Bc1On;
    }

    // Coverage-gate z index.bin (handoff pkt 8): cela, której okno nie zawiera ŻADNEGO kafla pokrycia,
    // jest pusta z definicji — zero I/O, zero prób dekodu (przedtem: 1088 misses nad SK).
    private bool Det25WindowCovered(int ci, int cj)
    {
        if (det25OpkIndex is null || det25Grid is null)
        {
            return false;
        }

        return det25OpkIndex.WindowHasCoverage(
            ci, cj, det25Grid.PitchTiles, det25Grid.CoverageTiles);
    }

    /// <summary>True, gdy streaming detalu orto nie ma nic w locie (kolejki uploadu i compose puste) —
    /// bramka „scena dobudowana" dla startu demo F9/benchu (start w trakcie dociągania ścierwi film
    /// i zakłamuje pomiar zarywania).</summary>
    public bool DetailStreamingIdle =>
        det25ReadInFlight == 0 && det05ReadInFlight == 0
        && det25UploadQueue.Count == 0 && det05UploadQueue.Count == 0;

    /// <summary>Postęp cachowania detalu: ile cel jest już rezydentnych z tylu ŻĄDANYCH, per warstwa
    /// (2026-07-25). <see cref="DetailStreamingIdle"/> mówi tylko „nic nie jest w locie W TEJ CHWILI" —
    /// bywa prawdziwe w środku napełniania, między zakolejkowaniem partii a startem następnej, więc
    /// bramka lotu F9 potrafiła na nim wystartować w trakcie cachowania (user: „freeze, a potem lagi,
    /// bo wciąż cachuje, a kropka już leci"). To jest miara, którą widać i którą da się pokazać.</summary>
    public (int Det05Resident, int Det05Desired, int Det25Resident, int Det25Desired) DetailStreamingProgress
    {
        get
        {
            int r05 = 0;
            foreach (DetailCellGpu c in det05Cells.Values)
            {
                if (c.LayerReady)
                {
                    r05++;
                }
            }

            int r25 = 0;
            foreach (DetailCellGpu c in det25Cells.Values)
            {
                if (c.LayerReady)
                {
                    r25++;
                }
            }

            return (r05, det05LastDesired, r25, det25LastDesired);
        }
    }

    private void ProbeS3tc(GL gl)
    {
        if (s3tcProbed)
        {
            return;
        }

        s3tcProbed = true;
        string ext = gl.GetStringS(StringName.Extensions) ?? string.Empty;
        det05Bc1On = ext.Contains("texture_compression_s3tc", StringComparison.OrdinalIgnoreCase)
            || ext.Contains("compressed_texture_s3tc", StringComparison.OrdinalIgnoreCase)
            || ext.Contains("texture_compression_dxt1", StringComparison.OrdinalIgnoreCase);
        Log.Information("[Det05] BC1 pipeline {State} (s3tc {Probe})", det05Bc1On ? "ON" : "OFF — RGBA fallback",
            det05Bc1On ? "present" : "absent");
    }

    /// <summary>Layers per det05 array texture — keeps every single GPU resource ≈2.86 GB, safely under
    /// the 32-bit (~4.29 GB) per-resource ceiling. Must match the shader's slice constant (best &lt; 8).</summary>
    // BC1: 24 warstwy 8192² z mipami ≈ 1,07 GB/array — daleko od sufitu ~4,29 GB/resource. RGBA-fallback
    // (bez s3tc) zostaje przy 8 (24×357 MB przebiłoby sufit) — patrz EnsureDet05Array.
    // 64 warstwy 8192² BC1+mipy = 2,73 GiB na tablicę — bezpiecznie pod TWARDYM sufitem 4 GiB na JEDNĄ
    // teksturę (32-bitowe pole rozmiaru; 96 warstw = dokładnie 4 GiB = przepełnienie → „białe dziury" 07-20).
    // TRZY takie tablice = 192 cele = 8,0 GiB. Liczba ustalona z userem 2026-07-25 — nie zmieniać bez pytania.
    private const int Det05ArraySliceLayers = 64;
    private int det05LayersA; // faktyczne warstwy slice'a A po alokacji (mapping slot→(array, warstwa) + uniform uDet05ArrA)
    // H2 (2026-07-23): 600 → 800 m with the 16-cell cap — 16 cells of ~410 m span tile an 800 m ring, so the
    // 5 cm reflector reaches the far side of a cirque like Morskie Oko instead of stopping mid-lake.
    // 2000 → 3200 m: przy CELACH ROZŁĄCZNYCH promień 2 km wysycał się już na 84 celach (3,6 GB), więc to
    // pierścień, a nie pamięć, był ogranicznikiem. 3,2 km ⇒ ~192 cele = pełny budżet 8 GB.
    private static readonly double Det05RingRadiusMeters = OperatingSystem.IsWindows() ? 3200.0 : 350.0;
    private static readonly int Det05CoarseBackingCells = OperatingSystem.IsWindows() ? 6 : 4; // det25 cells reserved to back the det05 ring (no-hole)

    // det05 cell TEXTURE ARRAYS (units 12 + 13): allocated lazily on first upload (TexStorage3D, error-
    // CHECKED — the 07-20 lesson), layer per resident cell, per-fragment cell pick in the shader. Global
    // layer index L maps to slice A (L < Det05ArraySliceLayers) or slice B (L − slice). Shader slot list
    // is fixed at 16; smaller caps simply leave the rest as sentinels.
    private uint det05ArrayTexture;             // slice A (layers 0..7)
    private uint det05ArrayTextureB;            // slice B; 0 when the cap fits slice A
    private uint det05ArrayTextureC;            // slice C (unit 7); 0 gdy cap mieści się w A+B
    private readonly Stack<int> det05FreeLayers = new();
    private long det05ArrayUniformsTick = -1;   // per-frame guard for the AABB uniform upload

    /// <summary>One streamed det25 detail cell on the GPU. <see cref="Texture"/> is 0 (never sampled by the draw
    /// path) until the strip-upload completes and promotes the staging texture — mirrors the base-ortho tile.</summary>
    private sealed class DetailCellGpu
    {
        public required int Key { get; init; }
        public required int Ci { get; init; }
        public required int Cj { get; init; }
        public required MapaTur.Domain.Geography.MapBounds Bounds { get; init; }
        public required int Px { get; init; }
        public uint Texture;                 // promoted, drawable; 0 until fully uploaded
        public uint StagingTexture;          // allocated empty, filled row-by-row; 0 = no upload in progress
        public int Layer = -1;               // det05 ARRAY path: assigned array layer (-1 = none)
        public bool LayerReady;              // det05 ARRAY path: layer fully uploaded — safe to reference in the AABB list
        public byte MinimumLod;               // 2 after tail-first promote, 0 after the 5/10 cm pages arrive
        public bool FullLoadFinished;         // full read succeeded or failed; prevents retry loops after tail-ready
        public byte ReadMinimumLod;           // stage currently owned by Compose
        public byte PendingMinimumLod;        // stage currently owned by PendingBc1/upload queue
        public double ReadRequestedMs;        // request→GPU-ready latency for the current tail/fine stage
        public double PromoteMs;             // det05 ARRAY path: frame-clock ms of the promote (drives the fade-in)
        public int UploadedRows;
        public int UploadLevel;              // det05 ARRAY path: mip level currently strip-uploading (0 = base)
        public byte[]? Pending;              // composed buffer awaiting strip-upload
        public byte[]? PendingMips;          // det05 ARRAY path: worker-built mip chain (levels 1..N, packed)
        public byte[]? PendingBc1;           // full GPU-ready BC1 chain (L0..1×1)
        public byte[]? Rented;               // legacy upload buffer; never populated by production .opk streaming
        public byte[]? RentedMips;           // legacy upload buffer; never populated by production .opk streaming
        public byte[]? RentedBc1;            // pooled BC1-chain buffer (owner: this cell until returned)
        public bool FromOpk;
        public long ResidentBytesLedger;     // what this cell added to det05ResidentBytes at promote (path-dependent)
        public Task<byte[]?>? Compose;       // off-thread composition in flight
        public long DesiredTick;             // last frame the cell was desired (LRU eviction key)
        public bool Empty;                   // compose returned null (outside the fetched footprint) — no texture
        public double ComposeMs;             // wall-clock of the off-thread compose (decode + assemble)
        public double UploadMs;              // cumulative GL strip-upload time (sliced across frames)
        public double MipmapMs;              // GenerateMipmap time at completion
    }

    // Releases the cell's compose buffer back to the shared pool and clears both ownership fields. Invariant:
    // `Rented` tracks the buffer the cell OWNS from the compose kick all the way through the strip-upload
    // (harvest re-points it at the composer's result). Legal call sites: the post-mipmap promote (the final
    // TexSubImage2D copies synchronously — the buffer is dead the moment that call returns) and cell disposal.
    // NEVER while `Compose` is alive — the task is still writing into the buffer (the 2026-07-15 eviction
    // lesson); the guard below turns such a call into a deliberate drop-on-the-floor (GC reclaims it, the
    // pool refills by allocation) rather than a use-after-return corruption.
    // ANTI-CHURN eviction guard (2026-07-23, user: "każde drgnięcie myszką wyładowuje kafle i tracą ostrość"):
    // the LRU victim pick was blind to VISIBILITY — an orbit sweep rotated the desired ring, the cap evicted
    // cells that were still ON SCREEN, and the mouse's return forced a multi-second recompose (blur). The pick
    // now prefers off-screen victims; only when every evictable cell is visible does it fall back to plain LRU
    // (the hard layer/texture cap must still win — a full slot stack would stall uploads otherwise).
    private Matrix4x4 lastTerrainMvp;
    private bool lastTerrainMvpValid;
    // Diagnostyka atrybucji czarnych trójkątów przy łączeniu orto: MAPATUR_NO_FRUSTUM_CULL=1 wyłącza cull
    // drawów głównego passu (rozstrzyga kandydata „cull wycina kafle za sylwetką" w jeden relaunch).
    private static readonly bool frustumCullOff =
        Environment.GetEnvironmentVariable("MAPATUR_NO_FRUSTUM_CULL") == "1";

    // BISEKCJA WARSTW (2026-07-24): MAPATUR_KILL=det1m,det25arr,det05arr,mosaic,baseskin — wyłącza wskazane
    // ścieżki na starcie; kamera wznawia ten sam kadr, więc seria relanchy ze zrzutami wskazuje warstwę
    // malującą czarne trójkąty MECHANICZNIE (pięć hipotez obalonych pomiarami — koniec teorii).
    private static readonly HashSet<string> killLayers = new(
        (Environment.GetEnvironmentVariable("MAPATUR_KILL") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        StringComparer.OrdinalIgnoreCase);

    // MAPATUR_DET1M_DEBUG=1 — klasyfikacja danych det1m w shaderze (czerwony=opaque black przechodzący
    // bramkę alfa, żółty=punch-through a=0, magenta=cov>0 bez slice'a, zielony=zdrowe krycie).
    private static readonly bool det1mDebug =
        Environment.GetEnvironmentVariable("MAPATUR_DET1M_DEBUG") == "1";

    // Diagnostyka jasnej linii na granicy pokrycia (2026-07-25): MAPATUR_ORTHO_TONE=0 wyłącza SAM krok (2)
    // prawa koloru (warunkową harmonizację tonu), zostawiając de-blue — izoluje hipotezę „głęboki mip tonu
    // zanieczyszczony czernią texeli bez pokrycia ⇒ delta ujemna ⇒ ROZJAŚNIENIE brzegu". MAPATUR_ORTHO_TONE_DEBUG=1
    // rysuje mapę tej korekty (czerwony = rozjaśnienie, niebieski = przyciemnienie).
    private static readonly bool toneHarmOff =
        Environment.GetEnvironmentVariable("MAPATUR_ORTHO_TONE") == "0";

    private static readonly bool toneDebug =
        Environment.GetEnvironmentVariable("MAPATUR_ORTHO_TONE_DEBUG") == "1";
    private TerrainMesh3D? detailAnchorMesh;

    private bool CellVisibleLastFrame(MapaTur.Domain.Geography.MapBounds b)
    {
        if (!lastTerrainMvpValid || detailAnchorMesh is not { } anchor)
        {
            return false;
        }

        Vector3 sw = anchor.GeoToWorld(b.SouthWest, 0f);
        Vector3 ne = anchor.GeoToWorld(b.NorthEast, 0f);
        // Geo bounds carry no Z — test a generous elevation band (0..3500 world units covers Tatry at any
        // exaggeration in use). Conservative = protects a little extra, which is exactly the anti-churn goal.
        var min = new Vector3(Math.Min(sw.X, ne.X), Math.Min(sw.Y, ne.Y), 0f);
        var max = new Vector3(Math.Max(sw.X, ne.X), Math.Max(sw.Y, ne.Y), 3500f);
        return MapaTur.Application.Terrain.FrustumCuller.IsAabbVisible(lastTerrainMvp, min, max);
    }

    private static void ReleaseCellBuffer(DetailCellGpu cell)
    {
        byte[]? owned = cell.Rented;
        byte[]? ownedMips = cell.RentedMips;
        byte[]? ownedBc1 = cell.RentedBc1;
        cell.Rented = null;
        cell.Pending = null;
        cell.RentedMips = null;
        cell.PendingMips = null;
        cell.RentedBc1 = null;
        cell.PendingBc1 = null;
        if (owned is not null && cell.Compose is null)
        {
            MapaTur.Application.Terrain.MeshBufferPool.Shared.Return(owned);
        }

        if (ownedMips is not null && cell.Compose is null)
        {
            MapaTur.Application.Terrain.MeshBufferPool.Shared.Return(ownedMips);
        }

        if (ownedBc1 is not null && cell.Compose is null)
        {
            MapaTur.Application.Terrain.MeshBufferPool.Shared.Return(ownedBc1);
        }
    }
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
    private int terrainCloudShadowOffsetLocation = -1;
    private int terrainCloudTimeLocation = -1;
    private int terrainCloudCoverageLocation = -1;
    private int terrainCloudShadowLocation = -1;
    private int terrainSnowStrengthLocation = -1;
    private int terrainSnowLineZLocation = -1;
    private int terrainFirnLineZLocation = -1;
    private int terrainFirnBandZLocation = -1;
    private int terrainFirnDropZLocation = -1;
    private int terrainFirnSitesLocation = -1;
    private int firnChannelOnLocation = -1;
    private int terrainFirnSiteCountLocation = -1;
    private readonly float[] firnSiteScratch = new float[12 * 4];
    private int terrainFirnStrengthLocation = -1;
    // Perennial-firn coverage strength (0 = off; the instant rollback lever).
    private const float FirnStrength = 0.85f;
    private int terrainContourSpacingZLocation = -1;
    private int terrainContourColorLocation = -1;
    private int terrainContourMajorSpacingZLocation = -1;
    private int terrainContourMajorColorLocation = -1;
    private int terrainContourStrengthLocation = -1;
    private int terrainContourWidthPxLocation = -1;
    private int trailMaskSamplerLocation = -1;
    private int baseCoverSamplerLocation = -1;
    private int baseCoverMinXYLocation = -1;
    private int baseCoverSizeXYLocation = -1;
    private int baseCoverOnLocation = -1;
    private int isBaseSkinLocation = -1;
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
    private int skyCloudSeedLocation = -1;
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

        // Persistent far-tier buffer + the in-flight off-thread box-average producing it. Computed at most
        // ONCE per cell (~1/16 of the master bytes): the synchronous per-tier-change downsample of a ~180 MB
        // master on the GL thread was ~1 s of the measured first-swap hitch (4 far cells at scene start).
        // While FarCompute runs the cell keeps rendering whatever it has (master texture, or nothing on its
        // very first appearance); every later demotion is a pointer swap via FarRgba.
        public byte[]? FarRgba;
        public int FarWidth;
        public int FarHeight;
        public Task<(byte[] Rgba, int Width, int Height)>? FarCompute;

        // STRIP-SLICED upload state (anti-freeze): the texture is allocated empty up front and filled a few
        // MB of rows per frame via TexSubImage2D; Texture stays 0 (cell renders hypsometric/previous) until
        // the last strip lands and the mip chain is generated. A monolithic TexImage2D of all cells in one
        // frame was the measured 6–14 s "Not Responding" at scene start.
        public uint StagingTexture; // allocated, partially filled; 0 = no upload in progress
        public int UploadedRows;    // rows already pushed into StagingTexture
    }
    private readonly List<OrthoTile> orthoTiles = new();
    // Old tiles whose GL textures still need deleting on the GL thread (set when textures are swapped).
    private readonly List<OrthoTile> pendingOrthoRelease = new();
    private bool orthoDirty;
    // Cells awaiting the strip-sliced upload (indices into orthoTiles), drained a time-budgeted few MB per
    // frame — see DrainOrthoUploads.
    private readonly List<int> orthoUploadQueue = new();

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
    // Desktop 9 GB (2026-07-20): the raised caps (12 × 357 MB det05 + 12 × 89 MB det25 ≈ 5.4 GB) plus the
    // ~2 GB base set fit a 16 GB card comfortably. The 07-20 white-holes incident was NOT this ledger —
    // it was a single >4.29 GB resource (see Det05ArraySliceLayers); the ledger allocates nothing itself.
    // H2 (2026-07-23, KONTRAKT-ORTO "budżety z hardware'u raz na starcie"): the desktop budget now DERIVES
    // from the card's dedicated VRAM (60 %, clamped 4–12 GB) instead of a flat const — a 16 GB card gets
    // ~9.5 GB, an 8 GB card degrades to 4.8 GB instead of overcommitting. Phone path unchanged.
    // ★★ BUDŻET PODNIESIONY NA POLECENIE USERA (2026-07-25) — NIE ZMIENIAĆ BEZ PYTANIA (zasada 19).
    // 0,60 → 0,78 i sufit 12 → 14 GiB. Powód: 192 cele det05 to 8,0 GiB, plus baza ~2,8 + det25 ~0,35
    // + det1m 0,58 = ~11,7 GiB. Stary clamp dawał 9,6 GiB i CICHO dusił cap — user miał 16 GB VRAM stojące
    // odłogiem i pół jeziora w 5 cm. Ta stała ma historię cichych zmian (07-20 flat 9 GB → 07-23 60% VRAM
    // → dziś); każda kolejna wymaga pytania.
    private static readonly long OrthoVramBudgetBytes = OperatingSystem.IsWindows()
        ? Math.Clamp((long)(QueryDedicatedVramBytes() * 0.78), 4L << 30, 14L << 30)
        : 3L * 1024 * 1024 * 1024;

    // Dedicated VRAM from the display-class registry (HardwareInformation.qwMemorySize — the only reliable
    // source on Windows; Win32_VideoController.AdapterRAM is a uint32 capped at 4 GB). 0 → the clamp floor.
    private static long QueryDedicatedVramBytes()
    {
        try
        {
            using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            long best = 0;
            foreach (string sub in cls?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                try
                {
                    using var k = cls!.OpenSubKey(sub);
                    if (k?.GetValue("HardwareInformation.qwMemorySize") is long qw && qw > best)
                    {
                        best = qw;
                    }
                }
                catch (System.Security.SecurityException)
                {
                    // The class key mixes adapter subkeys with ACL-protected ones ("Properties") — a protected
                    // subkey must not abort the whole scan (2026-07-23: it silently floored the budget to 4 GB).
                }
            }

            Log.Information("[VRAM] dedicated {GB:F1} GB → ortho budget {BGB:F1} GB",
                best / (1024.0 * 1024 * 1024), Math.Clamp((long)(best * 0.60), 4L << 30, 12L << 30) / (1024.0 * 1024 * 1024));
            return best;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[VRAM] registry query failed — budget falls to the 4 GB clamp floor");
            return 0;
        }
    }
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
    private int lineSceneDepthLocation = -1;
    private int lineSceneDepthOnLocation = -1;
    private int lineDepthNearFarLocation = -1;

    // Ghost-depth target: a full-res DEPTH TEXTURE the scene depth is blitted into just before the x-ray
    // ghost pass, so the line shader can measure how many metres of rock sit between the visible surface
    // and an occluded trail fragment (the rock-thickness gate). Depth-only FBO; latched off when incomplete.
    private uint ghostDepthFbo;
    private uint ghostDepthTex;
    private int ghostDepthWidth;
    private int ghostDepthHeight;
    private bool ghostDepthUnsupported;

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
    private int cumulusCoverageLocation = -1;
    private int cumulusMemberSeedLocation = -1;
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

    // HDR scene targets (desktop, A0 of the hyper-real fire plan): with GL_EXT_color_buffer_float the
    // scene/resolve/present chain is RGBA16F and the bloom mips are R11F_G11F_B10F, so emission > 1 (the
    // white-hot fire core, T⁴ energy) survives to the bloom bright-pass and the ACES composite instead of
    // clipping to flat white in an 8-bit buffer. postColorTex STAYS Rgba8 — it is the LDR hand-off the view
    // wraps as a GL_RGBA8 SKImage, and the composite/pass-through (both run ACES) is the only HDR→LDR step.
    // hdrUnsupported latches on a missing extension OR any incomplete HDR framebuffer; every Ensure* then
    // retries/reallocs the plain Rgba8 layout, so the worst case is exactly the pre-HDR pipeline.
    private bool hdrProbed;          // extension probed once per context
    private bool hdrUnsupported;     // latched: stay on the LDR Rgba8 pipeline for this context
    private bool presentIsHdr;       // format of the CURRENT allocation (realloc when the want changes)
    private bool msaaIsHdr;
    private bool bloomIsHdr;
    private uint lastPresentedFbo;   // FBO holding the frame Render() returned (postFbo when post ran) — the recorder reads THIS, always LDR

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
    private int bloomCompTonemapLoc = -1, bloomCompExposureLoc = -1;
    private int postTonemapLoc = -1, postExposureLoc = -1;
    // Filmic look (C-package 2026-07-05): 1 = full ACES (0 = legacy linear, kept as the instant A/B
    // rollback); exposure is the pre-curve gain compensating ACES's mid-tone dip.
    private const float TonemapStrength = 1f;

    // Diagnostyka koloru (2026-07-25, user: „wciąż mam wrażenie mocnego prześwietlenia kolorów").
    // ZMIERZONE na pozie Szpiglasowego, render kontra BAZA NA DYSKU (jej własne źródło):
    //   mediana luma 92,7 → 102,5 (+11%), p99 210,4 → 162,1 (−23%), kontrast 37,2 → 28,3 (−24%),
    //   nasycenie 0,168 → 0,132 (−21%), przepalonych pikseli 0,00%.
    // Czyli nic nie jest przepalone — to podpis KRZYWEJ: podniesione półtony, ścięte światła, wyprany kolor.
    // Ekspozycja jest już neutralna (1.0, obniżona 07-07), więc zostaje sama krzywa ACES.
    // MAPATUR_TONEMAP=0..1 pozwala to zważyć NA OBRAZIE bez rebuildu (0 = liniowo, 1 = pełne ACES).
    private static readonly float TonemapStrengthEff =
        float.TryParse(Environment.GetEnvironmentVariable("MAPATUR_TONEMAP"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float t)
            ? Math.Clamp(t, 0f, 1f)
            : TonemapStrength;
    // 2026-07-07: 1.15 → 1.0. The +15% pre-curve exposure (with the ×1.15 sun-colour boost = ~×1.32 on lit
    // ground) pushed sunlit terrain and snow into ACES's shoulder and BLEW OUT the colour — the "wszystko
    // przepalone, bez kolorów, za jasno" the user reported across BOTH ortho sources. Neutral 1.0 keeps the
    // deep GUGiK/ZBGIS colour; ACES still rolls off genuine highlights.
    private const float TonemapExposure = 1.0f;
    private int bloomCompGodrayLoc = -1, bloomCompGodrayIntensityLoc = -1;
    private bool bloomStageLogged;
    private bool godrayStageLogged;

    // Cascaded Shadow Maps (Krok 5): per-cascade depth textures rendered from the sun's POV, sampled in the
    // terrain shader (part 4). Cascades cover near→far slices of the camera frustum (CascadeShadowSplits),
    // each fit by an orthographic light matrix (CascadeLightMatrix). aPos is absolute world, so the depth
    // pass transforms it straight by the cascade light matrix — no model/stable offset needed.
    private const int ShadowCascadeCount = 3;
    // Desktop 2048 (A-package 2026-07-05: crisp near-shadow edges; per-cascade caster culling freed the
    // budget), phones keep the mobile-friendly 1024. The shader reads the texel size via uShadowTexel,
    // so the two never drift apart.
    private static readonly int ShadowMapSize = OperatingSystem.IsWindows() ? 2048 : 1024;
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
    private int shadowBaseCoverLoc = -1;
    private int shadowBaseCoverMinXYLoc = -1;
    private int shadowBaseCoverSizeXYLoc = -1;
    private int shadowBaseCoverOnLoc = -1;
    private int shadowIsBaseSkinLoc = -1;
    private bool shadowPassLogged;
    // Shadow-sampling uniforms on the terrain program (part 4) + per-frame active flag + tuning strength.
    private int shadowMap0Loc = -1, shadowMap1Loc = -1, shadowMap2Loc = -1;
    private int cascadeVp0Loc = -1, cascadeVp1Loc = -1, cascadeVp2Loc = -1;
    private int cascadeSplitLoc = -1, shadowStrengthLoc = -1, shadowTexelLoc = -1;
    private int aoStrengthLoc = -1;
    private int bakedShadowCompLoc = -1;

    /// <summary>Strength of the baked-shadow (dark ortho in shade) de-cyan + lift compensation. 0 = OFF by
    /// default: the per-fragment (dark+cool) detection was PROVEN not to discriminate baked shadow from
    /// ordinary dark terrain (2026-07-11 split-mask debug — the cirque-shadow albedo is neutral, not cool;
    /// the "cool" is the whole-ortho green bias on LIT scree). Kept behind the debug keys for experiments;
    /// the real de-shadow is an image-based ortho preprocess (spatial illumination estimate), not this.</summary>
    public float BakedShadowComp { get; set; }
    // Curvature-AO strength (B-package 2026-07-05); 0 = instant off for A/B comparison.
    private const float AoStrength = 0.6f;
    private bool shadowsActiveThisFrame;
    private const float ShadowStrength = 0.7f;

    private readonly Dictionary<TerrainMesh3D, TileBuffers> tileBuffers = new();
    private IReadOnlyList<TerrainMesh3D>? lastTiles;
    // Deferred mesh upload: a detail reload can bring ~100 new tiles; uploading them all in the swap frame froze
    // it ~300 ms. Instead enqueue here and upload a few per frame (DrainTileUploads) — the detail fills in over a
    // few frames with no single-frame freeze. Reused (cached) tiles are already resident ⇒ never enqueued.
    private readonly List<TerrainMesh3D> pendingTileUploads = new();
    // TIME-budgeted (2026-07-03): the old fixed 6-tiles-per-frame budget froze the frame anyway when the
    // queued tiles were BIG (a few multi-MB base tiles = hundreds of ms each — the measured 12 s start-of-
    // scene gap at pendingUploads=146). The drain now uploads until the per-frame time budget is spent,
    // always at least one tile per frame so the queue can never stall.
    private const double TileUploadBudgetMsPerFrame = 6.0;
    // BYTE budget on top of the time budget (2026-07-11): the ms clock measures the CHEAP client-side
    // glBufferData call, while the actual PCIe transfer + driver sync bites at swap — the F9 demo showed 34
    // pending tiles draining in ~2 "on-budget" frames (≈50 MB/frame) as 200–320 ms frame gaps. ~8 MB/frame
    // ≈ 2-3 detail tiles keeps the real transfer inside a vsync-ish slice; the min-one-tile rule still
    // guarantees the queue drains.
    private const long TileUploadBudgetBytesPerFrame = 8L * 1024 * 1024;
    // Per-swap render-thread cost breakdown (the frame after a detail reload re-runs these over the new tile list).
    private bool dbgTileSwapFrame;
    private double dbgSwapSyncMs, dbgSwapOrthoMs, dbgSwapLakeMs;
    // Swap-frame breakdown (2026-07-05): sync+ortho+lake explained only ~1.1 of the measured ~2.1 s first
    // swap; these checkpoints attribute the rest (drain, shadow depth pass, terrain-pass CPU walk) so the
    // next optimisation aims at a number, not a guess. A single watch read at each stage — negligible cost,
    // and only ever read on swap frames.
    private readonly System.Diagnostics.Stopwatch dbgSwapWatch = new();
    private double dbgSwapDrainMs, dbgSwapShadowMs, dbgSwapTerrainMs;
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

    // In-flight OFF-THREAD trail ribbon build + the inputs it was started for. The world projection (1 m
    // densification + seating on the baked tiles) plus ribbon assembly for the full 560-trail network
    // measured 5-7 s ON THE GL THREAD the first time trails bound to a scene ("lines=7454ms" in the swap
    // breakdown) — the whole visible app froze for it. Same pattern as the ortho decode / far-tier
    // downsample: build on a background task (ToWorld keeps a per-CALL tile cache and the availability
    // index opens its own stream per read, so the off-thread call is safe), keep drawing the previous
    // ribbon — or nothing on the first scene — and upload when the result lands. A stale result (any
    // input reference changed while building) is dropped and the build re-kicked.
    private Task<(RibbonBuilder Ribbon, RibbonBuilder Black)>? trailBuildTask;
    private IReadOnlyList<Trail>? trailBuildTrails;
    private DemRaster? trailBuildRaster;
    private TerrainMesh3D? trailBuildMesh;
    private DetailElevationField? trailBuildDetail;

    // Same async-build state for the route ribbon (conflation + seating measured ~2 s on the GL thread).
    private Task<RibbonBuilder>? routeBuildTask;
    private Route? routeBuildRoute;
    private IReadOnlyList<Trail>? routeBuildTrails;
    private DemRaster? routeBuildRaster;
    private TerrainMesh3D? routeBuildMesh;
    private DetailElevationField? routeBuildDetail;

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

    private LineBuffers? offTrailLines;
    private IReadOnlyList<Trail>? lastOffTrailTracks;
    private DemRaster? lastOffTrailRaster;
    private TerrainMesh3D? lastOffTrailMesh;
    private DetailElevationField? lastOffTrailDetail;

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

    /// <summary>Surface-ownership mask from the VM (see <see cref="MapaTur.Application.Terrain.BaseCoverageMaskBuilder"/>):
    /// where it marks resident full-detail z16 ground, BASE-SKIN fragments are discarded so the box-averaged
    /// base can't depth-bury the streamed detail on convex slopes. Null = no discard. Re-uploaded (R8, unit 8)
    /// whenever the reference changes.</summary>
    public MapaTur.Application.Terrain.BaseCoverageMask? BaseCoverageMask { get; set; }
    private MapaTur.Application.Terrain.BaseCoverageMask? uploadedBaseCoverageMask;
    private uint baseCoverTex;
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

    // Static high-alpine stream polylines (MountainStreamData, generated from OSM) as Trail objects —
    // used when the LIVE waterways layer is empty, which it usually is: the streams were baked into the
    // ortho for performance, so the layer has no data yet the firn channel prior still needs the REAL
    // channel geometry. They fill only the water FIELD (the firn prior); the water DECAL stays off for
    // them (uWaterStrength gated on the live layer), so nothing double-paints over the ortho-baked look.
    private static IReadOnlyList<Trail>? staticFirnStreams;

    private static IReadOnlyList<Trail> StaticFirnStreams
    {
        get
        {
            if (staticFirnStreams is null)
            {
                var list = new List<Trail>();
                long id = -1;
                foreach (MapaTur.Domain.Geography.GeoPoint[] seg in MapaTur.Application.Terrain.MountainStreamData.NearFirnSites)
                {
                    list.Add(new Trail(id--, string.Empty, Array.Empty<MapaTur.Domain.Trails.TrailMarking>(), seg));
                }

                staticFirnStreams = list;
            }

            return staticFirnStreams;
        }
    }

    /// <summary>The live waterways layer when it has data, else the static firn-stream gazetteer.</summary>
    private IReadOnlyList<Trail> EffectiveWaterways => Waterways is { Count: > 0 } w ? w : StaticFirnStreams;

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

    /// <summary>Enables prebaked RMP2 geometry when a catalog is present; missing/not-ready pages stay DEM.</summary>
    public bool PhotogrammetricRockEnabled { get; set; } = true;

    public void SetPhotogrammetricRockRoot(string? root) =>
        photogrammetricRock.Configure(
            root,
            Math.Clamp(OrthoVramBudgetBytes / 32, 128L << 20, 512L << 20));

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
    /// PoC: supply the two hi-res ortho DETAIL mosaics (det25 ~0.25 m, det05 ~0.05 m) as decoded RGBA8 plus
    /// their geographic AABB (SW/NE). Pixels are stored and uploaded to GL on the next paint (off the caller's
    /// thread). Safe to call from a background decode task — the volatile flag is set last.
    /// </summary>
    public void SetOrthoDetailPoc(
        byte[] det25Rgba, int det25W, int det25H, MapaTur.Domain.Geography.GeoPoint det25Sw, MapaTur.Domain.Geography.GeoPoint det25Ne,
        byte[] det05Rgba, int det05W, int det05H, MapaTur.Domain.Geography.GeoPoint det05Sw, MapaTur.Domain.Geography.GeoPoint det05Ne)
    {
        pendingDet25Rgba = det25Rgba; pendingDet25W = det25W; pendingDet25H = det25H;
        det25GeoSw = det25Sw; det25GeoNe = det25Ne;
        pendingDet05Rgba = det05Rgba; pendingDet05W = det05W; pendingDet05H = det05H;
        det05GeoSw = det05Sw; det05GeoNe = det05Ne;
        det05MosaicResidentBytes = OrthoVramBudget.CellResidentBytes(det05W, det05H);
        orthoDetailUploadPending = true;
    }

    // Uploads a pending detail mosaic to a resident GL texture (full image, mip chain, clamp-to-edge so
    // out-of-AABB samples clamp instead of wrap, anisotropy). One-time on load — the mosaics are static.
    private unsafe uint UploadDetailMosaic(GL g, byte[] rgba, int w, int h, float aniso)
    {
        uint tex = g.GenTexture();
        g.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* p = rgba)
        {
            g.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)w, (uint)h, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        g.GenerateMipmap(TextureTarget.Texture2D);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        if (aniso > 1f)
        {
            g.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE /* GL_TEXTURE_MAX_ANISOTROPY_EXT */, aniso);
        }
        g.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    private void EnsureOrthoDetail(GL g)
    {
        if (!orthoDetailUploadPending)
        {
            return;
        }

        orthoDetailUploadPending = false;
        const GLEnum maxAnisotropyPName = (GLEnum)0x84FF;
        Span<float> maxAniso = stackalloc float[1] { 1f };
        g.GetFloat(maxAnisotropyPName, maxAniso);
        float aniso = maxAniso[0] < 1f ? 1f : maxAniso[0];

        if (pendingDet25Rgba is { } r25 && pendingDet25W > 0 && pendingDet25H > 0)
        {
            if (orthoDet25Texture != 0) { g.DeleteTexture(orthoDet25Texture); }
            orthoDet25Texture = UploadDetailMosaic(g, r25, pendingDet25W, pendingDet25H, aniso);
            det25GeoSet = true;
        }
        if (pendingDet05Rgba is { } r05 && pendingDet05W > 0 && pendingDet05H > 0)
        {
            if (orthoDet05Texture != 0) { g.DeleteTexture(orthoDet05Texture); }
            orthoDet05Texture = UploadDetailMosaic(g, r05, pendingDet05W, pendingDet05H, aniso);
            det05GeoSet = true;
        }
        pendingDet25Rgba = null;
        pendingDet05Rgba = null;
        Log.Information("[OrthoDetailPoc] uploaded det25 tex={T25} {W25}x{H25}, det05 tex={T05} {W05}x{H05}, aniso x{A}",
            orthoDet25Texture, pendingDet25W, pendingDet25H, orthoDet05Texture, pendingDet05W, pendingDet05H, aniso);
    }

    // Binds the detail mosaics (units 9/11) and sets their world-space AABB + gate uniforms ONCE per frame,
    // before both the reflection and terrain passes (which share this program). Geo AABB → world via the same
    // per-tile anchor the base ortho coverage uses (LocalTangentProjection). A disabled/absent layer → uUse*=0
    // = strict no-op. Restores the active unit to 0 so the per-tile ortho binding below is unaffected.
    private void BindAndSetOrthoDetail(GL gl, IReadOnlyList<TerrainMesh3D> tiles)
    {
        bool en = OrthoDetailEnabled && tiles.Count > 0;
        int use25 = (en && orthoDet25Texture != 0 && det25GeoSet) ? 1 : 0;
        int use05 = (en && orthoDet05Texture != 0 && det05GeoSet && !killLayers.Contains("mosaic")) ? 1 : 0;
        if (use25 == 1)
        {
            Vector3 sw = tiles[0].GeoToWorld(det25GeoSw, 0f);
            Vector3 ne = tiles[0].GeoToWorld(det25GeoNe, 0f);
            gl.ActiveTexture(TextureUnit.Texture9); // 10 belongs to uOrthoDet25Arr (array type) — never share
            gl.BindTexture(TextureTarget.Texture2D, orthoDet25Texture);
            gl.Uniform1(det25SamplerLocation, 9);
            gl.Uniform2(det25MinXyLocation, Math.Min(sw.X, ne.X), Math.Min(sw.Y, ne.Y));
            gl.Uniform2(det25MaxXyLocation, Math.Max(sw.X, ne.X), Math.Max(sw.Y, ne.Y));
        }
        if (use05 == 1)
        {
            Vector3 sw = tiles[0].GeoToWorld(det05GeoSw, 0f);
            Vector3 ne = tiles[0].GeoToWorld(det05GeoNe, 0f);
            gl.ActiveTexture(TextureUnit.Texture11);
            gl.BindTexture(TextureTarget.Texture2D, orthoDet05Texture);
            gl.Uniform1(det05SamplerLocation, 11);
            gl.Uniform2(det05MinXyLocation, Math.Min(sw.X, ne.X), Math.Min(sw.Y, ne.Y));
            gl.Uniform2(det05MaxXyLocation, Math.Max(sw.X, ne.X), Math.Max(sw.Y, ne.Y));
        }
        gl.Uniform1(useDet25Location, use25);
        gl.Uniform1(useDet05Location, use05);
        gl.Uniform1(detailBlendLocation, OrthoDetailBlendMeters);
        gl.Uniform1(detailColorModeLocation, OrthoDetailColorMode);
        gl.Uniform1(toneHarmLoc, toneHarmOff ? 0 : 1);
        gl.Uniform1(toneDebugLoc, toneDebug ? 1 : 0);
        gl.Uniform1(det05ArrRawLocation, Det05ArrayRawColor ? 1 : 0);
        gl.Uniform1(useDet1mLoc, det1mReady && Det1mEnabled ? 1 : 0); // A/B = wyłącznie ten uniform; dane rezydentne
        gl.Uniform1(det1mDebugLoc, det1mDebug ? 1 : 0);
        gl.Uniform1(detailDebugBoundsLocation, OrthoDetailDebugBounds ? 1 : 0);
        gl.ActiveTexture(TextureUnit.Texture0);
    }

    /// <summary>Enables per-draw det25 streaming from prebaked `.opk` packages.</summary>
    public void SetOrthoDetailStreaming(
        MapaTur.Application.Terrain.OrthoDetailGrid grid,
        MapaTur.Application.Terrain.OrthoDetailResidencyPolicy policy)
    {
        det25Grid = grid;
        det25Policy = policy;

        string? focusEnv = Environment.GetEnvironmentVariable("MAPATUR_DET25_FOCUS");
        if (focusEnv is not null)
        {
            string[] p = focusEnv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 2
                && double.TryParse(p[0], System.Globalization.CultureInfo.InvariantCulture, out double flat)
                && double.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out double flon))
            {
                det25FocusOverride = new MapaTur.Domain.Geography.GeoPoint(flat, flon);
                Log.Information("[OrthoDetailStream] focus OVERRIDE (perf measurement) → {Lat},{Lon}", flat, flon);
            }
        }

        Log.Information("[OrthoDetailStream] det25 streaming ON — ring {R}m, cap {C} cells, cellPx {Px}",
            Det25RingRadiusMeters, Det25HardCapCells, grid.CellPx);
    }

    /// <summary>Stages ONLY the det05 (5 cm) mosaic (the accepted Morskie-Oko showcase on unit 11), leaving det25
    /// to the streamer. Mirrors <see cref="SetOrthoDetailPoc"/> but does not touch the det25 slot.</summary>
    public void SetOrthoDetailDet05Mosaic(
        byte[] det05Rgba, int det05W, int det05H,
        MapaTur.Domain.Geography.GeoPoint det05Sw, MapaTur.Domain.Geography.GeoPoint det05Ne)
    {
        pendingDet05Rgba = det05Rgba; pendingDet05W = det05W; pendingDet05H = det05H;
        det05GeoSw = det05Sw; det05GeoNe = det05Ne;
        det05MosaicResidentBytes = OrthoVramBudget.CellResidentBytes(det05W, det05H);
        orthoDetailUploadPending = true;
    }

    /// <summary>Enables the SECOND streamed level, det05 (5 cm) on unit 11, behind MAPATUR_DET05_STREAM=1. The two
    /// levels share ONE budget via <see cref="MapaTur.Application.Terrain.TwoLevelDetailResidencyPolicy"/> (det05 >
    /// det25 > base, coverage-gated to <paramref name="coverage"/>). The det25 streamer stays exactly as-is; when
    /// this is on the static 5 cm mosaic is not loaded and streamed det05 owns unit 11.</summary>
    public void SetOrthoDetail05Streaming(
        MapaTur.Application.Terrain.OrthoDetailGrid grid,
        Func<int, int, bool> coverage)
    {
        if (det25Grid is null || det25Policy is null)
        {
            Log.Warning("[OrthoDetail05] det25 streaming must be set up first — det05 streaming NOT enabled");
            return;
        }

        det05Grid = grid;

        // BC1 chain (jedyna produkcyjna ścieżka det05-stream — desktop ANGLE zawsze ma s3tc): cela 8192²
        // to ~45 MB, nie 357 MB RGBA. Stara arytmetyka dusiła near-cap i CAŁY zasięg 5 cm do „kałuży".
        // (RGBA-fallback miałby zaniżony ledger — akceptowane: nie występuje na wspieranym desktopie.)
        long det05CellBytes = MapaTur.Application.Terrain.Bc1MipChain.ByteSize(grid.CellPx);
        long det25CellBytes = MapaTur.Application.Terrain.Bc1MipChain.ByteSize(det25Grid.CellPx);
        var fine = new MapaTur.Application.Terrain.DetailLevelSpec(
            grid,
            new MapaTur.Application.Terrain.OrthoDetailResidencyPolicy(grid, Det05RingRadiusMeters, Det25FastMotionSpeedMps, prefetchLeadMeters: 120),
            det05CellBytes, Det05HardCapCells, coverage);
        var coarse = new MapaTur.Application.Terrain.DetailLevelSpec(
            det25Grid, det25Policy, det25CellBytes, Det25HardCapCells, Coverage: null);
        twoLevelPolicy = new MapaTur.Application.Terrain.TwoLevelDetailResidencyPolicy(
            fine, coarse, OrthoVramBudgetBytes, Det05CoarseBackingCells);
        det05StreamOn = true;
        Log.Information("[OrthoDetail05] det05 streaming ON — ring {R}m, cap {C} cells, cellPx {Px} (two-level, coverage-gated)",
            Det05RingRadiusMeters, Det05HardCapCells, grid.CellPx);
    }

    // Kick/harvest/evict the det05 (5 cm) ring for the desired set the two-level policy chose. Mirrors the det25
    // path (bounded off-thread compose, non-blocking harvest, LRU evict) on the det05 collections + unit 11.
    private void StreamDet05(GL gl, IReadOnlyList<int> desired)
    {
        if (det05Grid is null || !det05Bc1On || !Det05OpkReady())
        {
            return;
        }

        det05LastDesired = desired.Count;
        var desiredSet = new HashSet<int>(desired);

        foreach (int key in desired)
        {
            if (det05Cells.TryGetValue(key, out DetailCellGpu? cell))
            {
                cell.DesiredTick = det25FrameTick;
            }
            else
            {
                (int ci, int cj) = det05Grid.CellFromKey(key);
                det05Cells[key] = new DetailCellGpu
                {
                    Key = key,
                    Ci = ci,
                    Cj = cj,
                    Bounds = det05Grid.CellBounds(ci, cj),
                    Px = det05Grid.CellPx,
                    DesiredTick = det25FrameTick,
                };
            }
        }

        if (!Det05TailFirstRuntimeEnabled)
        {
            // Product fallback after the rejected compact-tail gate: one complete cell transaction. The
            // worker still reads ready GPU pages only (.opk); it merely assembles L2+tail and L0/L1 into
            // one chain before the existing bounded upload promotes the layer.
            foreach (int key in desired)
            {
                if (det05ReadInFlight >= DetailMaxConcurrentReads)
                {
                    break;
                }

                if (det05Cells.TryGetValue(key, out DetailCellGpu? cell)
                    && !cell.Empty && !cell.LayerReady
                    && cell.Compose is null && cell.Pending is null && cell.PendingBc1 is null)
                {
                    KickDet05Read(cell, minimumLod: 0);
                }
            }
        }
        else
        {
            // Experimental Stage A: tail first. Kept behind a default-off gate so the compact layout
            // remains directly testable without making the rejected 57 s panorama path the product default.
            bool allDesiredTailsReady = true;
            foreach (int key in desired)
            {
                if (det05ReadInFlight >= DetailMaxConcurrentReads)
                {
                    allDesiredTailsReady = false;
                    continue;
                }

                if (det05Cells.TryGetValue(key, out DetailCellGpu? cell)
                    && !cell.Empty && !cell.LayerReady)
                {
                    allDesiredTailsReady = false;
                    if (cell.Compose is null && cell.Pending is null && cell.PendingBc1 is null)
                    {
                        KickDet05Read(cell, Det05TailFirstMinimumLod);
                    }
                }
            }

            // Experimental Stage B: once the desired set is tail-ready, fill exact 5/10 cm pages.
            if (allDesiredTailsReady)
            {
                foreach (int key in desired)
                {
                    if (det05ReadInFlight >= DetailMaxConcurrentReads)
                    {
                        break;
                    }

                    if (det05Cells.TryGetValue(key, out DetailCellGpu? cell)
                        && cell.LayerReady && !cell.FullLoadFinished && !cell.Empty
                        && cell.Compose is null && cell.Pending is null && cell.PendingBc1 is null)
                    {
                        KickDet05Read(cell, minimumLod: 0);
                    }
                }
            }
        }

        foreach (DetailCellGpu cell in det05Cells.Values)
        {
            if (cell.Compose is { IsCompleted: true } done)
            {
                cell.Compose = null;
                det05ReadInFlight = Math.Max(0, det05ReadInFlight - 1);
                byte[]? buf = done.IsCompletedSuccessfully ? done.Result : null;
                if (buf is null)
                {
                    if (cell.LayerReady && cell.ReadMinimumLod == 0)
                    {
                        cell.FullLoadFinished = true; // keep the valid tail; do not retry a corrupt/missing fine page forever
                    }
                    else
                    {
                        cell.Empty = true;
                    }

                    ReleaseCellBuffer(cell); // the pooled destination goes straight back — nothing to upload
                }
                else if (det05Bc1On)
                {
                    // BC1 path: the task's payload IS the compressed chain (== RentedBc1); nothing else to keep.
                    cell.PendingBc1 = buf;
                    cell.PendingMinimumLod = cell.ReadMinimumLod;
                    cell.UploadLevel = cell.PendingMinimumLod;
                    cell.UploadedRows = 0;
                    if (!det05UploadQueue.Contains(cell.Key))
                    {
                        det05UploadQueue.Add(cell.Key);
                    }
                }
                else
                {
                    if (cell.Rented is { } r && !ReferenceEquals(buf, r))
                    {
                        MapaTur.Application.Terrain.MeshBufferPool.Shared.Return(r);
                    }

                    cell.Pending = buf;
                    cell.Rented = buf; // ownership continues through the strip-upload to promote/dispose
                    cell.PendingMips = cell.RentedMips; // worker-built chain rides along to the strip-upload
                    if (!det05UploadQueue.Contains(cell.Key))
                    {
                        det05UploadQueue.Add(cell.Key);
                    }
                }
            }
        }

        // ★ GŁODZENIE ŻĄDANYCH CEL (2026-07-25, user: „jak się przesunę, to nie odświeża się przede mną
        // niska rozdzielczość"). Eksmisja była wyzwalana WYŁĄCZNIE przekroczeniem limitu liczby cel, więc
        // gdy pula zapełniła się dokładnie do capa (over = 0), NIC nigdy nie było eksmitowane: cele z
        // poprzedniej pozycji trzymały wszystkie warstwy tablicy, a nowo ŻĄDANE nie miały gdzie wejść i
        // czekały w nieskończoność (objaw w logu: resident 182 / desired 140 / queue 0, wolny 1 slot).
        // Teraz eksmisja rusza także wtedy, gdy żądana cela nie ma warstwy, a pula wolnych warstw jest pusta.
        int starved = 0;
        foreach (int key in desired)
        {
            if (det05Cells.TryGetValue(key, out DetailCellGpu? need) && need.Layer < 0)
            {
                starved++;
            }
        }

        int over = Math.Max(
            det05Cells.Count - Math.Max(0, Det05HardCapCells),
            starved - det05FreeLayers.Count);
        while (over > 0)
        {
            // ANTI-CHURN (ZASADA 9): prefer an OFF-SCREEN victim; a cell the camera still sees is evicted only
            // when nothing else is evictable (the hard layer cap must still win, or uploads would stall).
            int victim = 0; long oldest = long.MaxValue; bool found = false;
            int victimAny = 0; long oldestAny = long.MaxValue; bool foundAny = false;
            foreach (DetailCellGpu c in det05Cells.Values)
            {
                if (desiredSet.Contains(c.Key) || c.Compose is not null)
                {
                    continue; // never evict a cell mid-compose (orphaned Task = heap balloon)
                }

                if (c.DesiredTick < oldestAny)
                {
                    oldestAny = c.DesiredTick; victimAny = c.Key; foundAny = true;
                }

                if (CellVisibleLastFrame(c.Bounds))
                {
                    continue;
                }

                if (c.DesiredTick < oldest)
                {
                    oldest = c.DesiredTick; victim = c.Key; found = true;
                }
            }

            if (!found && foundAny)
            {
                victim = victimAny; found = true; // all evictables on screen — plain LRU keeps the cap honest
            }

            if (!found)
            {
                break;
            }

            DisposeDet05Cell(gl, det05Cells[victim]);
            det05Cells.Remove(victim);
            over--;
        }

        DrainDet05Uploads(gl);
    }

    private void KickDet05Read(DetailCellGpu cell, byte minimumLod)
    {
        if (det05Grid is null)
        {
            return;
        }

        int ci = cell.Ci, cj = cell.Cj;
        DetailCellGpu capture = cell;
        det05ReadInFlight++;
        int cellPx = det05Grid.CellPx;
        byte[] rentedBc1 = MapaTur.Application.Terrain.MeshBufferPool.Shared.RentBytes(
            MapaTur.Application.Terrain.Bc1MipChain.ByteSize(cellPx));
        cell.RentedBc1 = rentedBc1;
        cell.ReadMinimumLod = minimumLod;
        cell.ReadRequestedMs = frameClock.ElapsedMilliseconds;
        string opkDir = Det05OpkDir!;
        int pitch = det05Grid.PitchTiles, coverage = det05Grid.CoverageTiles;
        int groupTiles = det05OpkIndex!.TilesPerCell;
        bool tailAlreadyReady = cell.LayerReady;
        cell.Compose = Task.Run(() =>
        {
            var swc = System.Diagnostics.Stopwatch.StartNew();
            bool ok;
            if (minimumLod > 0)
            {
                ok = MapaTur.Application.Terrain.OrthoPageWindowAssembler.TryAssembleTailWindow(
                    opkDir, ci, cj, pitch, coverage, groupTiles, minimumLod, rentedBc1, out _);
            }
            else if (tailAlreadyReady)
            {
                ok = MapaTur.Application.Terrain.OrthoPageWindowAssembler.TryAssembleFineWindow(
                    opkDir, ci, cj, pitch, coverage, groupTiles, rentedBc1, out _);
            }
            else
            {
                ok = MapaTur.Application.Terrain.OrthoPageWindowAssembler.TryAssembleDet25Window(
                    opkDir, ci, cj, pitch, coverage, groupTiles, rentedBc1, out _);
            }

            capture.ComposeMs = swc.Elapsed.TotalMilliseconds;
            capture.FromOpk = ok;
            return ok ? rentedBc1 : null;
        });
    }

    private void DisposeDet05Cell(GL gl, DetailCellGpu cell)
    {
        // Capture liveness BEFORE the safety-net nulls Compose: a live task is still WRITING into the pooled
        // buffer, so it must be dropped on the floor (GC), never returned — returning it would hand the pool
        // an array another cell rents while the orphaned task keeps writing (silent pixel corruption).
        bool composeAlive = cell.Compose is not null;
        if (composeAlive)
        {
            det05ReadInFlight = Math.Max(0, det05ReadInFlight - 1);
            cell.Compose = null;
        }

        if (cell.LayerReady)
        {
            // Subtract exactly what the promote added (BC1 chain vs RGBA+mips differ 8×) — see the ledger field.
            det05ResidentBytes -= cell.ResidentBytesLedger != 0
                ? cell.ResidentBytesLedger
                : OrthoVramBudget.CellResidentBytes(cell.Px, cell.Px);
            cell.ResidentBytesLedger = 0;
        }

        if (cell.Layer >= 0)
        {
            det05FreeLayers.Push(cell.Layer); // the array layer is reused; nothing to delete
            cell.Layer = -1;
        }

        cell.LayerReady = false;
        if (composeAlive)
        {
            // deliberate drop — the orphaned task owns every buffer now (it may still be writing into them)
            cell.Rented = null; cell.Pending = null;
            cell.RentedMips = null; cell.PendingMips = null;
            cell.RentedBc1 = null; cell.PendingBc1 = null;
        }
        else
        {
            ReleaseCellBuffer(cell);
        }

        cell.UploadedRows = 0;
        cell.UploadLevel = 0;
        det05UploadQueue.Remove(cell.Key);
    }

    // Allocates the det05 cell ARRAYS once (TexStorage3D — immutable storage, full mip chain), split into
    // ≤Det05ArraySliceLayers-layer slices so no single GPU resource crosses the 32-bit ~4.29 GB ceiling
    // (the 07-20 white-holes incident: one 12-layer 4.295 GB array failed SILENTLY and its sticky GL error
    // poisoned terrain-tile allocations). Every allocation is glGetError-VERIFIED; on failure the cap
    // degrades to whatever allocated instead of poisoning the context.
    private unsafe void EnsureDet05Array(GL gl)
    {
        if (det05ArrayTexture != 0 || det05Grid is null)
        {
            return;
        }

        ProbeS3tc(gl); // decide BC1 vs RGBA once, BEFORE the storage is allocated (format is immutable)
        int px = det05Grid.CellPx;
        int wanted = Math.Max(1, Det05HardCapCells);
        int perArray = det05Bc1On ? Det05ArraySliceLayers : 8; // RGBA: 8×357 MB ≈ 2,86 GB — tuż pod sufitem/resource
        int layersA = Math.Min(wanted, perArray);
        int layersB = Math.Min(Math.Max(0, wanted - layersA), perArray);
        int layersC = Math.Min(Math.Max(0, wanted - layersA - layersB), perArray);

        while (gl.GetError() != GLEnum.NoError) { } // drain stale errors so the checks below test OUR calls

        det05ArrayTexture = AllocateDet05Slice(gl, px, layersA, "A");
        if (det05ArrayTexture == 0)
        {
            return; // loud log inside; next frame retries — det25/base carry the view meanwhile
        }

        // WARSTWY MUSZĄ BYĆ RÓWNE we wszystkich tablicach: shader mapuje slot→(tablica, warstwa) jako
        // (slot/L, slot%L). Jeśli któraś tablica się nie zaalokuje, ucinamy pulę do wielokrotności L,
        // zamiast dopuścić rozjazd mapowania (cichy sampling nie tej celi).
        int allocated = layersA;
        if (layersB > 0)
        {
            det05ArrayTextureB = AllocateDet05Slice(gl, px, layersB, "B");
            if (det05ArrayTextureB != 0 && layersB == layersA)
            {
                allocated += layersB;
                if (layersC > 0)
                {
                    det05ArrayTextureC = AllocateDet05Slice(gl, px, layersC, "C");
                    if (det05ArrayTextureC != 0 && layersC == layersA)
                    {
                        allocated += layersC;
                    }
                }
            }
        }

        det05LayersA = layersA; // warstw NA TABLICĘ → uniform uDet05ArrLayers (bind raz na klatkę)
        det05FreeLayers.Clear();
        for (int i = allocated - 1; i >= 0; i--)
        {
            det05FreeLayers.Push(i);
        }

        Log.Information(
            "[Det05] cell ARRAYS allocated: {Px}px, {N} warstw/tablicę × {Arrays} tablice = {Alloc} cel ({GB:F1} GB with mips, {Fmt}), glGetError clean — per-fragment cell pick",
            px, layersA, allocated / Math.Max(1, layersA), allocated,
            allocated * (det05Bc1On
                ? (double)MapaTur.Application.Terrain.Bc1MipChain.ByteSize(px)
                : OrthoVramBudget.CellResidentBytes(px, px)) / (1024.0 * 1024.0 * 1024.0),
            det05Bc1On ? "BC1" : "RGBA");
    }

    // One ≤2.9 GB slice, error-checked: returns 0 (and deletes the name) if the driver refused the
    // storage or any parameter call — the caller degrades gracefully instead of sampling a corpse.
    private uint AllocateDet05Slice(GL gl, int px, int layers, string name)
    {
        uint levels = (uint)(Math.ILogB(px) + 1);
        uint tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2DArray, tex);
        // BC1 storage when the extension is present (1/8 VRAM; uploads are CompressedTexSubImage3D of the
        // worker-encoded chain). RGBA8 otherwise — every downstream path branches on det05Bc1On.
        gl.TexStorage3D(TextureTarget.Texture2DArray, levels,
            det05Bc1On ? (SizedInternalFormat)GlCompressedRgbS3tcDxt1 : SizedInternalFormat.Rgba8,
            (uint)px, (uint)px, (uint)layers);
        GLEnum error = gl.GetError();
        if (error != GLEnum.NoError)
        {
            gl.BindTexture(TextureTarget.Texture2DArray, 0);
            gl.DeleteTexture(tex);
            Log.Warning(
                "[Det05] slice {Name} TexStorage3D({Px}px × {Layers}) FAILED: GL 0x{Err:X} — degrading (no crash, coarser tiers carry the view)",
                name, px, layers, (int)error);
            return 0;
        }

        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        const GLEnum maxAnisotropyPName = (GLEnum)0x84FF;
        Span<float> maxAniso = stackalloc float[1] { 1f };
        gl.GetFloat(maxAnisotropyPName, maxAniso);
        if (maxAniso[0] > 1f)
        {
            gl.TexParameter(TextureTarget.Texture2DArray, (TextureParameterName)0x84FE, maxAniso[0]);
        }

        gl.BindTexture(TextureTarget.Texture2DArray, 0);
        return tex;
    }

    // ── det25 ARRAY path (krok 4): per-tile bind → texture array + per-fragment wybór (wzorzec det05).
    // Per-tile bind pokazywał JEDNĄ celę na kafel terenu (patchwork na dużych/odległych kaflach). BC1 czyni
    // array tanim: 32 × 4096² z mipami ≈ 342 MB w JEDNEJ teksturze. Aktywne tylko na ścieżce BC1 (desktop);
    // fallback RGBA zostaje na starym per-tile bindzie.
    private uint det25ArrayTexture;
    private readonly Stack<int> det25ArrFreeLayers = new();
    // 128 warstw 4096² BC1+mipy = 1,43 GiB — 8× taniej niż cela det05, a to WŁAŚNIE ta warstwa ma trzymać
    // średni dystans. Przy 32 celach det25 sięgał ~2,4 km i między nim a horyzontem została goła baza
    // („ostro blisko, ostro daleko, breja pomiędzy" — user 2026-07-25). 128 cel = ~4,9 km.
    private const int Det25ArrLayers = 128;
    private const int Det25CellHashSize = 256;
    private int det25ArrSamplerLoc = -1, det25CellHashLoc = -1, det25HashSeedLoc = -1, useDet25ArrLoc = -1;
    private int det25GridOriginLoc = -1, det25GridPitchLoc = -1, det25CellSizeLoc = -1;
    private long det25ArrUniformsTick = -1;

    private static (Vector2 Origin, Vector2 Pitch, Vector2 Size) DetailGridWorld(
        MapaTur.Application.Terrain.OrthoDetailGrid grid,
        TerrainMesh3D anchorMesh)
    {
        MapaTur.Domain.Geography.MapBounds b00 = grid.CellBounds(0, 0);
        MapaTur.Domain.Geography.MapBounds b10 = grid.CellBounds(1, 0);
        MapaTur.Domain.Geography.MapBounds b01 = grid.CellBounds(0, 1);
        var nw00 = new MapaTur.Domain.Geography.GeoPoint(b00.NorthEast.Latitude, b00.SouthWest.Longitude);
        var nw10 = new MapaTur.Domain.Geography.GeoPoint(b10.NorthEast.Latitude, b10.SouthWest.Longitude);
        var nw01 = new MapaTur.Domain.Geography.GeoPoint(b01.NorthEast.Latitude, b01.SouthWest.Longitude);
        Vector3 origin = anchorMesh.GeoToWorld(nw00, 0f);
        Vector3 east = anchorMesh.GeoToWorld(nw10, 0f);
        Vector3 south = anchorMesh.GeoToWorld(nw01, 0f);
        Vector3 sw = anchorMesh.GeoToWorld(b00.SouthWest, 0f);
        Vector3 ne = anchorMesh.GeoToWorld(b00.NorthEast, 0f);
        return (
            new Vector2(origin.X, origin.Y),
            new Vector2(MathF.Abs(east.X - origin.X), MathF.Abs(south.Y - origin.Y)),
            new Vector2(MathF.Abs(ne.X - sw.X), MathF.Abs(ne.Y - sw.Y)));
    }

    // Once per frame: upload the bounded cell→array-slot hash. Unit 10 is owned by the det25 array.
    private unsafe void BindDet25ArrOncePerFrame(GL gl, TerrainMesh3D anchorMesh)
    {
        if (det25ArrayTexture == 0 || det25Grid is null || det25ArrUniformsTick == det25FrameTick)
        {
            return;
        }

        det25ArrUniformsTick = det25FrameTick;
        Span<MapaTur.Application.Terrain.DetailCellSlot> resident = stackalloc MapaTur.Application.Terrain.DetailCellSlot[Det25ArrLayers];
        int ready = 0;
        double nowMs = frameClock.ElapsedMilliseconds;
        foreach (DetailCellGpu cell in det25Cells.Values)
        {
            if (!cell.LayerReady || cell.Layer < 0 || cell.Layer >= Det25ArrLayers)
            {
                continue;
            }

            byte alpha = (byte)Math.Round(Math.Clamp((nowMs - cell.PromoteMs) / 300.0, 0.0, 1.0) * 255.0);
            resident[ready++] = new(cell.Ci, cell.Cj, cell.Layer, alpha, cell.MinimumLod);
        }

        if (ready > 0)
        {
            Span<int> hash = stackalloc int[Det25CellHashSize * 4];
            (uint seed, _) = MapaTur.Application.Terrain.DetailCellSlotHash.Fill(
                resident[..ready], hash, DetailCellHashMaxProbe);
            (Vector2 origin, Vector2 pitch, Vector2 size) = DetailGridWorld(det25Grid, anchorMesh);
            gl.ActiveTexture(TextureUnit.Texture10);
            gl.BindTexture(TextureTarget.Texture2DArray, det25ArrayTexture);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.Uniform1(det25ArrSamplerLoc, 10);
            fixed (int* p = hash) { gl.Uniform4(det25CellHashLoc, Det25CellHashSize, p); }
            gl.Uniform1(det25HashSeedLoc, unchecked((int)seed));
            gl.Uniform2(det25GridOriginLoc, origin.X, origin.Y);
            gl.Uniform2(det25GridPitchLoc, pitch.X, pitch.Y);
            gl.Uniform2(det25CellSizeLoc, size.X, size.Y);
            gl.Uniform1(useDet25ArrLoc, 1);
        }
        else
        {
            gl.Uniform1(useDet25ArrLoc, 0);
        }
    }

    private unsafe void EnsureDet25Array(GL gl)
    {
        if (det25ArrayTexture != 0 || !det05Bc1On || killLayers.Contains("det25arr"))
        {
            return;
        }

        while (gl.GetError() != GLEnum.NoError) { }
        det25ArrayTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2DArray, det25ArrayTexture);
        gl.TexStorage3D(TextureTarget.Texture2DArray, 13,
            (SizedInternalFormat)GlCompressedRgbS3tcDxt1, 4096, 4096, Det25ArrLayers);
        GLEnum err = gl.GetError();
        if (err != GLEnum.NoError)
        {
            Log.Warning("[Det25Arr] TexStorage3D odmówił (GL 0x{E:X}) — zostaje per-tile bind", (int)err);
            gl.DeleteTexture(det25ArrayTexture);
            det25ArrayTexture = 0;
            return;
        }

        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.BindTexture(TextureTarget.Texture2DArray, 0);
        det25ArrFreeLayers.Clear();
        for (int i = Det25ArrLayers - 1; i >= 0; i--)
        {
            det25ArrFreeLayers.Push(i);
        }

        Log.Information("[Det25Arr] array {L}×4096² BC1+mipy = {MB} MB VRAM — per-fragment wybór celi",
            Det25ArrLayers, (long)Det25ArrLayers * MapaTur.Application.Terrain.Bc1MipChain.ByteSize(4096) / (1024 * 1024));
    }

    // ── det1m RESIDENT TIER (krok 3, ARCHITEKTURA-STREAMING §3 + ANEKS A) ────────────────────────────
    // Warstwa 1 m/px domykająca lukę 2-8 km panoramy: ~54 pakiety .opk (4096² BC1, pełne mipy) ładowane
    // RAZ na starcie z prebake'u i REZYDENTNE NA STAŁE — żaden ruch/obrót nie może ich wybić. A/B (klawisz)
    // przełącza WYŁĄCZNIE uniform uUseDet1m — dane zostają na GPU, porównanie nieskażone streamingiem.
    // Brak strony/pakietu ⇒ maska pokrycia gate'uje do bazy (nigdy czerń, nigdy stop renderera).
    public bool Det1mEnabled { get; set; } = true;

    /// <summary>Katalog pakietów det1m (`opk/det1m`). Null = warstwa wyłączona.</summary>
    public string? Det1mPackDir { get; set; }

    private sealed record Det1mLoad(
        int PiMin, int PjMin, int GridW, int GridH,
        List<(int Pi, int Pj, byte[] Chain, ulong CovBits)> Slices,
        MapaTur.Domain.Geography.MapBounds GridGeo);

    private uint det1mArrayTexture;
    private uint det1mCovTexture;
    private bool det1mKicked;
    private bool det1mReady;
    private Task<Det1mLoad?>? det1mLoadTask;
    private Det1mLoad? det1mLoaded;
    private int det1mUploadCursor;
    private readonly int[] det1mSliceIdx = new int[160];
    private int det1mSamplerLoc = -1, det1mCovLoc = -1, useDet1mLoc = -1,
        det1mMinXyLoc = -1, det1mInvSizeLoc = -1, det1mGridDimLoc = -1, det1mSliceIdxLoc = -1,
        det1mDebugLoc = -1;

    private const double Det25TileDlon = 512 * 0.25 / (111320.0 * 0.65935); // cos(49.25°) = 0.65935 — anchor 19.5/49.4 jak dane
    private const double Det25TileDlat = 512 * 0.25 / 111320.0;

    private static Det1mLoad? LoadDet1mPacks(string dir)
    {
        OrthoPackIndex? idx = OrthoPackIndex.Load(System.IO.Path.Combine(dir, "index.bin"));
        if (idx is null || idx.Cells.Count == 0)
        {
            Log.Warning("[Det1m] brak/uszkodzony index.bin w {Dir} — warstwa wyłączona (fallback: baza)", dir);
            return null;
        }

        int piMin = idx.Cells.Min(c => c.Ci), piMax = idx.Cells.Max(c => c.Ci);
        int pjMin = idx.Cells.Min(c => c.Cj), pjMax = idx.Cells.Max(c => c.Cj);
        int gridW = piMax - piMin + 1, gridH = pjMax - pjMin + 1;
        if (gridW * gridH > 160)
        {
            Log.Warning("[Det1m] siatka {W}×{H} przekracza slot uniformów (160) — warstwa wyłączona", gridW, gridH);
            return null;
        }

        var slices = new List<(int, int, byte[], ulong)>(idx.Cells.Count);
        int chainSize = MapaTur.Application.Terrain.Bc1MipChain.ByteSize(4096);
        int level0 = MapaTur.Application.Terrain.Bc1Encoder.EncodedSize(4096, 4096);
        foreach (OrthoPackIndex.CellEntry c in idx.Cells)
        {
            using OrthoPagePack? pack = OrthoPagePack.Open(System.IO.Path.Combine(dir, $"{c.Ci}_{c.Cj}.opk"), 4096);
            if (pack is null)
            {
                Log.Warning("[Det1m] pakiet ({Pi},{Pj}) nie otwiera się — pomijam (baza pokryje)", c.Ci, c.Cj);
                continue;
            }

            byte[] chain = new byte[chainSize];
            ulong cov = 0;
            bool tailOk = pack.TryReadPage(OrthoPagePack.TailPageId, out byte[] tail)
                && tail.Length == chainSize - level0;
            if (tailOk)
            {
                System.Buffer.BlockCopy(tail, 0, chain, level0, tail.Length);
            }

            foreach (OrthoPagePack.Entry e in pack.Entries)
            {
                if (e.PageId == OrthoPagePack.TailPageId || !pack.TryReadPage(e.PageId, out byte[] page))
                {
                    continue; // strona zła/nieobecna → bit pokrycia zostaje 0 → shader pokazuje bazę
                }

                int lx = e.PageId / 8, ly = e.PageId % 8;
                // Wklej mip0 strony (512² BC1 = 128×128 bloków) w level0 celi (1024×1024 bloków).
                const int PageBlocks = 128, CellBlocks = 1024, RowBytes = PageBlocks * 8;
                for (int row = 0; row < PageBlocks; row++)
                {
                    System.Buffer.BlockCopy(page, row * RowBytes, chain,
                        (((((ly * PageBlocks) + row) * CellBlocks) + (lx * PageBlocks)) * 8), RowBytes);
                }

                cov |= 1UL << ((ly * 8) + lx);
            }

            if (cov != 0 && tailOk)
            {
                slices.Add((c.Ci, c.Cj, chain, cov));
            }
        }

        if (slices.Count == 0)
        {
            return null;
        }

        // Geo AABB siatki z mapowania kafli det25 (anchor 19.5/49.4 — spójny z pipeline'em danych):
        // pakiet = 32×32 kafli det25.
        double lon0 = 19.5 + (piMin * 32 * Det25TileDlon);
        double lon1 = 19.5 + ((piMax + 1) * 32 * Det25TileDlon);
        double lat1 = 49.4 - (pjMin * 32 * Det25TileDlat);
        double lat0 = 49.4 - ((pjMax + 1) * 32 * Det25TileDlat);
        var geo = new MapaTur.Domain.Geography.MapBounds(
            new MapaTur.Domain.Geography.GeoPoint(lat0, lon0),
            new MapaTur.Domain.Geography.GeoPoint(lat1, lon1));
        Log.Information("[Det1m] wczytano {N} pakietów, siatka {W}×{H}", slices.Count, gridW, gridH);
        return new Det1mLoad(piMin, pjMin, gridW, gridH, slices, geo);
    }

    // Per frame: kick → harvest (alokacja po sprawdzeniu limitów GPU + log realnego VRAM) → budżetowany
    // upload przez ring PBO (jeden slice na klatkę trzyma się budżetu 6 ms). Wołane ze StreamOrthoDetail.
    private unsafe void PumpDet1m(GL gl)
    {
        if (Det1mPackDir is null || killLayers.Contains("det1m"))
        {
            return;
        }

        if (!det1mKicked)
        {
            det1mKicked = true;
            string dir = Det1mPackDir;
            det1mLoadTask = Task.Run(() => LoadDet1mPacks(dir));
        }

        if (det1mLoaded is null && det1mLoadTask is { IsCompleted: true } t)
        {
            det1mLoadTask = null;
            det1mLoaded = t.IsCompletedSuccessfully ? t.Result : null;
            if (det1mLoaded is null)
            {
                Det1mPackDir = null; // twardy fallback: warstwy nie ma, baza pokrywa — nie ponawiamy co klatkę
                return;
            }
        }

        // Alokacja GPU (także po utracie kontekstu — tekstury giną, det1mLoaded w RAM zostaje).
        if (det1mLoaded is not null && det1mArrayTexture == 0)
        {
            // WARUNEK TESTU 2: limity GPU/ANGLE PRZED alokacją + raport realnego VRAM (array + mipy).
            Span<int> maxTex = stackalloc int[1];
            Span<int> maxLayers = stackalloc int[1];
            gl.GetInteger(GLEnum.MaxTextureSize, maxTex);
            gl.GetInteger((GLEnum)0x88FF, maxLayers); // GL_MAX_ARRAY_TEXTURE_LAYERS
            int layers = det1mLoaded.Slices.Count;
            long vram = (long)layers * MapaTur.Application.Terrain.Bc1MipChain.ByteSize(4096);
            if (maxTex[0] < 4096 || maxLayers[0] < layers)
            {
                Log.Warning("[Det1m] limit GPU (maxTex={T}, maxLayers={L}) < wymagane (4096, {N}) — warstwa wyłączona",
                    maxTex[0], maxLayers[0], layers);
                det1mLoaded = null; Det1mPackDir = null;
                return;
            }

            while (gl.GetError() != GLEnum.NoError) { }
            det1mArrayTexture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2DArray, det1mArrayTexture);
            gl.TexStorage3D(TextureTarget.Texture2DArray, 13,
                (SizedInternalFormat)GlCompressedRgbS3tcDxt1, 4096, 4096, (uint)layers);
            GLEnum err = gl.GetError();
            if (err != GLEnum.NoError)
            {
                Log.Warning("[Det1m] TexStorage3D odmówił (GL 0x{E:X}) — warstwa wyłączona, baza pokrywa", (int)err);
                gl.DeleteTexture(det1mArrayTexture); det1mArrayTexture = 0;
                det1mLoaded = null; Det1mPackDir = null;
                return;
            }

            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            // Maska pokrycia (R8, strona=512 m, filtrowana liniowo = miękki brzeg) + indeks slice'ów siatki.
            Array.Fill(det1mSliceIdx, -1);
            int covW = det1mLoaded.GridW * 8, covH = det1mLoaded.GridH * 8;
            byte[] covPix = new byte[covW * covH];
            for (int s = 0; s < det1mLoaded.Slices.Count; s++)
            {
                (int pi, int pj, _, ulong bits) = det1mLoaded.Slices[s];
                int gx = pi - det1mLoaded.PiMin, gy = pj - det1mLoaded.PjMin;
                det1mSliceIdx[(gy * det1mLoaded.GridW) + gx] = s;
                for (int b = 0; b < 64; b++)
                {
                    if ((bits & (1UL << b)) != 0)
                    {
                        int lx = b % 8, ly = b / 8;
                        covPix[(((gy * 8) + ly) * covW) + ((gx * 8) + lx)] = 255;
                    }
                }
            }

            det1mCovTexture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, det1mCovTexture);
            fixed (byte* cp = covPix)
            {
                gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.R8, (uint)covW, (uint)covH, 0,
                    PixelFormat.Red, PixelType.UnsignedByte, cp);
            }

            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            gl.BindTexture(TextureTarget.Texture2D, 0);
            det1mUploadCursor = 0;
            det1mReady = false;
            Log.Information(
                "[Det1m] array {Layers}×4096² BC1+mipy = {MB} MB VRAM (maxTex={T}, maxLayers={L}) + maska {Cw}×{Ch}",
                layers, vram / (1024 * 1024), maxTex[0], maxLayers[0], covW, covH);
        }

        // Budżetowany upload: jeden slice na klatkę (chain ~10,7 MB: level0 jednym chunkiem PBO, mipy drugim).
        if (det1mLoaded is not null && det1mArrayTexture != 0 && det1mUploadCursor < det1mLoaded.Slices.Count)
        {
            EnsureUploadPbos(gl, (nuint)OrthoUploadBytesPerChunk);
            (int _, int _, byte[] chain, _) = det1mLoaded.Slices[det1mUploadCursor];
            gl.BindTexture(TextureTarget.Texture2DArray, det1mArrayTexture);
            int off = 0;
            for (int lv = 0, px = 4096; px >= 1; lv++, px >>= 1)
            {
                int bytes = MapaTur.Application.Terrain.Bc1Encoder.EncodedSize(px, px);
                if (StageChunkInPbo(gl, chain, off, bytes))
                {
                    gl.CompressedTexSubImage3D(TextureTarget.Texture2DArray, lv, 0, 0, det1mUploadCursor,
                        (uint)px, (uint)px, 1, (InternalFormat)GlCompressedRgbS3tcDxt1, (uint)bytes, (void*)0);
                    gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
                }
                else
                {
                    fixed (byte* p = &chain[off])
                    {
                        gl.CompressedTexSubImage3D(TextureTarget.Texture2DArray, lv, 0, 0, det1mUploadCursor,
                            (uint)px, (uint)px, 1, (InternalFormat)GlCompressedRgbS3tcDxt1, (uint)bytes, p);
                    }
                }

                off += bytes;
            }

            gl.BindTexture(TextureTarget.Texture2DArray, 0);
            det1mUploadCursor++;
            if (det1mUploadCursor == det1mLoaded.Slices.Count)
            {
                det1mReady = true;
                Log.Information("[Det1m] REZYDENTNE: {N}/{N} slice'ów wgranych — warstwa żywa", det1mUploadCursor);
            }
        }
    }

    // H1 (2026-07-23): PIXEL_UNPACK PBO ring for the det05 strip uploads. TexSubImage3D from CLIENT memory
    // makes ANGLE copy the chunk synchronously on the GL thread and sync the real transfer at swap (the
    // "bites at swap" 150–300 ms gaps). From a PBO the call is a GPU-side transfer the driver overlaps with
    // rendering. Ring of 3 so consecutive chunks never stall on each other's in-flight DMA.
    private readonly uint[] uploadPbo = new uint[3];
    private int uploadPboIndex;
    private nuint uploadPboSize;
    private bool uploadPboBroken; // MapBufferRange refused once → stay on the direct path (no per-frame retries)

    private unsafe void EnsureUploadPbos(GL gl, nuint size)
    {
        if (uploadPbo[0] != 0 && uploadPboSize >= size)
        {
            return;
        }

        for (int i = 0; i < uploadPbo.Length; i++)
        {
            if (uploadPbo[i] != 0) { gl.DeleteBuffer(uploadPbo[i]); }
            uploadPbo[i] = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, uploadPbo[i]);
            gl.BufferData(BufferTargetARB.PixelUnpackBuffer, size, null, BufferUsageARB.StreamDraw);
        }

        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
        uploadPboSize = size;
        Log.Information("[Upload] PBO ring ready: {N} × {MB} MB", uploadPbo.Length, size / (1024 * 1024));
    }

    // Copies one chunk into the next ring PBO and leaves it BOUND as PIXEL_UNPACK, so the caller's
    // TexSubImage sources from PBO offset 0 (pass pixels = null) and MUST unbind afterwards. False = map
    // refused → caller uses the direct client-memory path (and we latch off to avoid per-frame map churn).
    private unsafe bool StageChunkInPbo(GL gl, byte[] src, long srcOffset, int bytes)
    {
        if (uploadPboBroken)
        {
            return false;
        }

        uploadPboIndex = (uploadPboIndex + 1) % uploadPbo.Length;
        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, uploadPbo[uploadPboIndex]);
        gl.BufferData(BufferTargetARB.PixelUnpackBuffer, uploadPboSize, null, BufferUsageARB.StreamDraw); // orphan
        void* ptr = gl.MapBufferRange(
            BufferTargetARB.PixelUnpackBuffer, 0, (nuint)bytes,
            (uint)(MapBufferAccessMask.WriteBit | MapBufferAccessMask.InvalidateBufferBit));
        if (ptr == null)
        {
            gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
            uploadPboBroken = true;
            Log.Warning("[Upload] MapBufferRange refused — PBO path latched OFF, direct uploads");
            return false;
        }

        fixed (byte* s = &src[srcOffset])
        {
            System.Buffer.MemoryCopy(s, ptr, bytes, bytes);
        }

        gl.UnmapBuffer(BufferTargetARB.PixelUnpackBuffer);
        return true;
    }

    private unsafe void DrainDet05Uploads(GL gl)
    {
        if (det05UploadQueue.Count == 0)
        {
            return;
        }

        EnsureDet05Array(gl);
        if (det05ArrayTexture == 0)
        {
            return;
        }

        double start = frameClock.ElapsedMilliseconds;
        uint bound = 0;
        bool promotedA = false, promotedB = false, promotedC = false;
        while (det05UploadQueue.Count > 0)
        {
            int key = det05UploadQueue[0];
            if (!det05Cells.TryGetValue(key, out DetailCellGpu? cell)
                || (cell.Pending is null && cell.PendingBc1 is null))
            {
                det05UploadQueue.RemoveAt(0);
                continue;
            }

            byte[]? rgba = cell.Pending; // RGBA fallback payload (null on the BC1 path)
            byte[]? bc1 = cell.PendingBc1; // BC1 chain payload (null on the RGBA path)

            int w = cell.Px, h = cell.Px;
            if (cell.Layer < 0)
            {
                if (!det05FreeLayers.TryPop(out int layer))
                {
                    break; // every layer occupied — eviction frees one on a later frame
                }

                cell.Layer = layer;
                cell.UploadedRows = 0;
                cell.UploadLevel = cell.PendingMinimumLod;
            }

            // Global layer → (tablica, warstwa lokalna): TA SAMA arytmetyka co w shaderze (slot/L, slot%L).
            int ai = cell.Layer / Math.Max(1, det05LayersA);
            uint target = ai == 0 ? det05ArrayTexture : (ai == 1 ? det05ArrayTextureB : det05ArrayTextureC);
            int z = cell.Layer - (ai * Math.Max(1, det05LayersA));
            if (target == 0)
            {
                det05UploadQueue.RemoveAt(0); // slice B refused by the driver — cell waits for an A slot
                det05FreeLayers.Push(cell.Layer);
                cell.Layer = -1;
                continue;
            }

            if (bound != target)
            {
                gl.BindTexture(TextureTarget.Texture2DArray, target);
                bound = target;
            }

            // Strip-upload the assigned layer LEVEL BY LEVEL (H1): the GL thread never calls GenerateMipmap
            // for det05 (under ANGLE that regenerated the WHOLE multi-GB array per promote → the 150–300 ms
            // gaps). Chunks go through the PBO ring so each (Compressed)TexSubImage is an async GPU-side copy.
            // A partial layer is never sampled (LayerReady gates the AABB slot list). BC1 path: UploadedRows
            // counts 4-px BLOCK rows and the payload is the packed chain; RGBA fallback counts pixel rows.
            EnsureUploadPbos(gl, (nuint)OrthoUploadBytesPerChunk);
            byte[]? mips = cell.PendingMips;
            int totalLevels = bc1 is not null || mips is not null ? Math.ILogB(w) + 1 : 1;
            int uploadEndLevel = cell.LayerReady && cell.PendingMinimumLod == 0
                ? Det05TailFirstMinimumLod
                : totalLevels;
            while (cell.UploadLevel < uploadEndLevel)
            {
                int lv = cell.UploadLevel;
                int lPx = Math.Max(1, w >> lv);
                if (bc1 is not null)
                {
                    int blockRows = Math.Max(1, (lPx + 3) / 4);
                    int rowBytesBlk = Math.Max(1, lPx / 4) * 8; // one 4-px-high block strip
                    int lvOffset = 0;
                    for (int j = 0; j < lv; j++)
                    {
                        lvOffset += MapaTur.Application.Terrain.Bc1Encoder.EncodedSize(Math.Max(1, w >> j), Math.Max(1, w >> j));
                    }

                    int rowsPerChunk = Math.Max(1, OrthoUploadBytesPerChunk / rowBytesBlk);
                    int rows = Math.Min(rowsPerChunk, blockRows - cell.UploadedRows);
                    int bytes = Math.Min(rows * rowBytesBlk,
                        MapaTur.Application.Terrain.Bc1Encoder.EncodedSize(lPx, lPx) - (cell.UploadedRows * rowBytesBlk));
                    int yoff = cell.UploadedRows * 4;
                    uint height = (uint)Math.Min(lPx - yoff, rows * 4);
                    long srcOff = lvOffset + ((long)cell.UploadedRows * rowBytesBlk);
                    if (StageChunkInPbo(gl, bc1, srcOff, bytes))
                    {
                        gl.CompressedTexSubImage3D(TextureTarget.Texture2DArray, lv, 0, yoff, z,
                            (uint)lPx, height, 1, (InternalFormat)GlCompressedRgbS3tcDxt1, (uint)bytes, (void*)0);
                        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
                    }
                    else
                    {
                        fixed (byte* p = &bc1[srcOff])
                        {
                            gl.CompressedTexSubImage3D(TextureTarget.Texture2DArray, lv, 0, yoff, z,
                                (uint)lPx, height, 1, (InternalFormat)GlCompressedRgbS3tcDxt1, (uint)bytes, p);
                        }
                    }

                    cell.UploadedRows += rows;
                    if (cell.UploadedRows >= blockRows)
                    {
                        cell.UploadLevel++;
                        cell.UploadedRows = 0;
                    }
                }
                else
                {
                    byte[] srcBuf = lv == 0 ? rgba! : mips!;
                    long lvOffset = 0;
                    for (int j = 1; j < lv; j++)
                    {
                        long p = w >> j;
                        lvOffset += p * p * 4;
                    }

                    int rowBytes = lPx * 4;
                    int rowsPerChunk = Math.Max(1, OrthoUploadBytesPerChunk / Math.Max(1, rowBytes));
                    int rows = Math.Min(rowsPerChunk, lPx - cell.UploadedRows);
                    long srcOff = lvOffset + ((long)cell.UploadedRows * rowBytes);
                    int bytes = rows * rowBytes;
                    if (StageChunkInPbo(gl, srcBuf, srcOff, bytes))
                    {
                        gl.TexSubImage3D(TextureTarget.Texture2DArray, lv, 0, cell.UploadedRows, z,
                            (uint)lPx, (uint)rows, 1, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)0);
                        gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
                    }
                    else
                    {
                        fixed (byte* p = &srcBuf[srcOff])
                        {
                            gl.TexSubImage3D(TextureTarget.Texture2DArray, lv, 0, cell.UploadedRows, z,
                                (uint)lPx, (uint)rows, 1, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                        }
                    }

                    cell.UploadedRows += rows;
                    if (cell.UploadedRows >= lPx)
                    {
                        cell.UploadLevel++;
                        cell.UploadedRows = 0;
                    }
                }

                if (frameClock.ElapsedMilliseconds - start >= OrthoUploadBudgetMsPerFrame)
                {
                    break;
                }
            }

            if (cell.UploadLevel >= uploadEndLevel)
            {
                bool firstPromotion = !cell.LayerReady;
                cell.LayerReady = true;
                cell.MinimumLod = cell.PendingMinimumLod;
                if (cell.MinimumLod == 0)
                {
                    cell.FullLoadFinished = true;
                }

                if (firstPromotion)
                {
                    cell.PromoteMs = frameClock.ElapsedMilliseconds;
                }

                if (bc1 is null && mips is null)
                {
                    // no chain → slice-wide fallback below (per TABLICA, nie per cela)
                    if (ai == 0) { promotedA = true; } else if (ai == 1) { promotedB = true; } else { promotedC = true; }
                }

                // Per-cell resident-bytes ledger: BC1 cells cost 1/8 of RGBA — record what THIS cell added so
                // dispose subtracts the same number regardless of which path uploaded it.
                long residentBytes = bc1 is not null
                    ? MapaTur.Application.Terrain.Bc1MipChain.ByteSize(w)
                    : OrthoVramBudget.CellResidentBytes(w, h);
                if (firstPromotion)
                {
                    cell.ResidentBytesLedger = residentBytes;
                    det05ResidentBytes += residentBytes;
                }

                byte completedMinimumLod = cell.MinimumLod;
                double readyMs = frameClock.ElapsedMilliseconds - cell.ReadRequestedMs;
                ReleaseCellBuffer(cell); // the final upload has copied — recycle the pooled buffers
                det05UploadQueue.RemoveAt(0);
                Log.Information(
                    "[OrthoLat] det05 cell ({Ci},{Cj}) {Stage} {Ready:F0}ms request-to-GPU ({Read:F0}ms read) | layer {Layer} ({Slice}) resident ({Levels} levels, {Fmt})",
                    cell.Ci, cell.Cj, completedMinimumLod == 0 ? "full-ready" : "tail-ready",
                    readyMs, cell.ComposeMs, cell.Layer, ai == 0 ? "A" : ai == 1 ? "B" : "C",
                    uploadEndLevel - completedMinimumLod, bc1 is not null ? "BC1" : "RGBA");
            }

            if (frameClock.ElapsedMilliseconds - start >= OrthoUploadBudgetMsPerFrame)
            {
                break;
            }
        }

        // FALLBACK ONLY (cells promoted without a worker-built chain — should not happen in practice): the old
        // slice-wide GenerateMipmap, kept so such a layer never samples garbage mips. Logged loudly.
        if (promotedA || promotedB || promotedC)
        {
            double t0 = frameClock.ElapsedMilliseconds;
            if (promotedA)
            {
                gl.BindTexture(TextureTarget.Texture2DArray, det05ArrayTexture);
                gl.GenerateMipmap(TextureTarget.Texture2DArray);
            }

            if (promotedB && det05ArrayTextureB != 0)
            {
                gl.BindTexture(TextureTarget.Texture2DArray, det05ArrayTextureB);
                gl.GenerateMipmap(TextureTarget.Texture2DArray);
            }

            if (promotedC && det05ArrayTextureC != 0)
            {
                gl.BindTexture(TextureTarget.Texture2DArray, det05ArrayTextureC);
                gl.GenerateMipmap(TextureTarget.Texture2DArray);
            }

            GLEnum mipError = gl.GetError();
            Log.Warning(
                "[Det05] FALLBACK slice-wide mip regen (no worker chain) {Ms:F0} ms, GL 0x{Err:X}",
                frameClock.ElapsedMilliseconds - t0, (int)mipError);
        }

        gl.BindTexture(TextureTarget.Texture2DArray, 0);
    }

    // Streamed det05, ARRAY path (2026-07-20): ONE per-frame upload of every resident cell's world AABB
    // (slot = array layer) + the array on unit 12 — the FRAGMENT picks its cell, so all resident detail
    // paints everywhere it exists. Replaces the per-draw single-cell bind whose "one cell per terrain
    // tile" was the root cause of the 10%-hires patchwork (a tile straddling 2×2 cells showed 5 cm only
    // on the intersection with its centre cell). Kept per-tile signature/call-site; the frame-tick guard
    // makes every call after the first a no-op.
    private unsafe void BindDet05ForTile(GL gl, TerrainMesh3D mesh, TerrainMesh3D anchorMesh)
    {
        if (det05Grid is null || det05ArrayTexture == 0 || killLayers.Contains("det05arr"))
        {
            gl.Uniform1(useDet05ArrLocation, 0);
            return;
        }

        if (det05ArrayUniformsTick == det25FrameTick)
        {
            return; // uniforms for this frame are already up
        }

        det05ArrayUniformsTick = det25FrameTick;
        Span<MapaTur.Application.Terrain.DetailCellSlot> resident =
            stackalloc MapaTur.Application.Terrain.DetailCellSlot[Det05HardCapCells];
        int ready = 0;
        double nowMs = frameClock.ElapsedMilliseconds;
        foreach (DetailCellGpu cell in det05Cells.Values)
        {
            if (!cell.LayerReady || cell.Layer < 0 || cell.Layer >= Det05HardCapCells)
            {
                continue;
            }

            byte alpha = (byte)Math.Round(Math.Clamp((nowMs - cell.PromoteMs) / 300.0, 0.0, 1.0) * 255.0);
            resident[ready++] = new(cell.Ci, cell.Cj, cell.Layer, alpha, cell.MinimumLod);
        }

        if (ready > 0)
        {
            Span<int> hash = stackalloc int[Det05CellHashSize * 4];
            (uint seed, _) = MapaTur.Application.Terrain.DetailCellSlotHash.Fill(
                resident[..ready], hash, DetailCellHashMaxProbe);
            (Vector2 origin, Vector2 pitch, Vector2 size) = DetailGridWorld(det05Grid, anchorMesh);
            gl.ActiveTexture(TextureUnit.Texture12);
            gl.BindTexture(TextureTarget.Texture2DArray, det05ArrayTexture);
            gl.ActiveTexture(TextureUnit.Texture13);
            // Tablice B i C aliasują A, gdy sterownik ich odmówił — sampler musi być kompletny na każdym
            // sterowniku, a gałąź i tak nie zostanie wzięta (mapowanie ucina pulę do zaalokowanych tablic).
            gl.BindTexture(TextureTarget.Texture2DArray, det05ArrayTextureB != 0 ? det05ArrayTextureB : det05ArrayTexture);
            gl.ActiveTexture(TextureUnit.Texture7);
            gl.BindTexture(TextureTarget.Texture2DArray, det05ArrayTextureC != 0 ? det05ArrayTextureC : det05ArrayTexture);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.Uniform1(det05ArrSamplerLocation, 12);
            gl.Uniform1(det05ArrBSamplerLocation, 13);
            gl.Uniform1(det05ArrCSamplerLocation, 7);
            gl.Uniform1(det05ArrALoc, det05LayersA);
            fixed (int* p = hash)
            {
                gl.Uniform4(det05CellHashLoc, Det05CellHashSize, p);
            }
            gl.Uniform1(det05HashSeedLoc, unchecked((int)seed));
            gl.Uniform2(det05GridOriginLoc, origin.X, origin.Y);
            gl.Uniform2(det05GridPitchLoc, pitch.X, pitch.Y);
            gl.Uniform2(det05CellSizeLoc, size.X, size.Y);
            gl.Uniform1(useDet05ArrLocation, 1);
        }
        else
        {
            gl.Uniform1(useDet05ArrLocation, 0);
        }
    }

    // The detail focus follows the LOOK RAY but clamped to the NEAR FIELD and low-passed in time
    // (2026-07-20, user: "bliski obiekt ma mieć orto, góry w tle nie; każde drgnięcie myszką
    // wyładowuje detale"). Raw camera.Target is the centre-screen ray hit — aiming at a background
    // ridge threw the whole detail ring kilometres away (foreground starved), and every mouse twitch
    // moved the focus (ring churn + the det25 fade circle visibly sweeping the terrain). Clamping the
    // focus to ≤ this distance keeps detail funding the terrain that actually fills the screen.
    private const float DetailFocusMaxMeters = 800f;
    private const float DetailFocusSmoothTau = 0.45f;   // seconds; jitter dies, intent survives
    private const float DetailFocusSnapMeters = 2500f;  // a teleport/jump snaps instead of gliding
    private Vector3 detailFocusSmoothed;
    private bool detailFocusValid;

    // Per frame: pick the desired det25 cell ring, kick bounded `.opk` reads off-thread,
    // harvest completed ones, evict LRU past the shared budget, and strip-upload a bounded slice. No GL binds here
    // (those are per-draw in BindDet25ForTile) — this just keeps the resident set + GPU textures current.
    private void StreamOrthoDetail(GL gl, IReadOnlyList<TerrainMesh3D> tiles, Vector3 cameraPosition, Vector3 cameraTarget)
    {
        if (!OrthoDetailEnabled || det25Grid is null || det25Policy is null || tiles.Count == 0)
        {
            return;
        }

        det25FrameTick++;
        ProbeS3tc(gl);
        PumpDet1m(gl); // rezydentny tier 1 m: kick ładowania, alokacja po limitach GPU, budżetowany upload
        if (!det05Bc1On || !Det25OpkReady())
        {
            return;
        }

        // ROTATION-INVARIANT residency (2026-07-23, ZASADA 9 — user: "drgnięcie myszką wyładowuje kafle",
        // reported three times): the ring used to chase the LOOK point (camera + view ray ≤800 m), so an orbit
        // swept the ring centre in a circle and churned the resident set on every twitch. The ring is now
        // centred on the CAMERA POSITION — a pure rotation changes NOTHING in the desired set, by construction.
        // Affordable only since BC1: the full 360° det25 disc (~80 × 11 MB) + det05 disc (16 × 45 MB) ≈ 1.6 GB.
        Vector3 focusRaw = cameraPosition;

        // Time smoothing: the ring and the fade circle must not follow mouse jitter. Exponential glide
        // with a snap for genuine relocations (teleport, preset, route jump).
        double frameDtSec = det25PrevClockMs >= 0
            ? Math.Clamp((frameClock.ElapsedMilliseconds - det25PrevClockMs) / 1000.0, 0.001, 0.25)
            : 0.016;
        if (!detailFocusValid || Vector3.Distance(focusRaw, detailFocusSmoothed) > DetailFocusSnapMeters)
        {
            detailFocusSmoothed = focusRaw;
            detailFocusValid = true;
        }
        else
        {
            float k = 1f - MathF.Exp((float)(-frameDtSec / DetailFocusSmoothTau));
            detailFocusSmoothed += (focusRaw - detailFocusSmoothed) * k;
        }

        // Focus + velocity from the smoothed near-field look point (NOT the raw target — see above).
        detailAnchorMesh = tiles[0]; // geo→world anchor for the eviction visibility guard
        MapaTur.Domain.Geography.GeoPoint eye = det25FocusOverride ?? tiles[0].WorldToGeo(detailFocusSmoothed);
        det25EyeLat = eye.Latitude; det25EyeLon = eye.Longitude;
        (det25FocusCi, det25FocusCj) = det25Grid.CellForPoint(eye);

        // Range-fade uniforms (shared terrain program is active here, after BindAndSetOrthoDetail): det25 is full
        // within ~0.62× the ring radius of the focus and feathers to the base ortho by ~1.05× — so the streaming
        // frontier (a det25 tile beside a not-yet-resident base tile) blends smoothly instead of a hard pop.
        Vector3 eyeWorld = tiles[0].GeoToWorld(eye, 0f);
        if (det25EyeXyLocation >= 0)
        {
            gl.Uniform2(det25EyeXyLocation, eyeWorld.X, eyeWorld.Y);
            gl.Uniform1(det25FadeInnerLocation, (float)(Det25RingRadiusMeters * 0.62));
            gl.Uniform1(det25FadeOuterLocation, (float)(Det25RingRadiusMeters * 1.05));
        }

        double nowMs = frameClock.ElapsedMilliseconds;
        double dt = det25PrevClockMs >= 0 ? Math.Max(0.001, (nowMs - det25PrevClockMs) / 1000.0) : 0.016;
        double velE = 0, velN = 0;
        if (det25PrevTargetValid && det25FocusOverride is null)
        {
            // Velocity from the SMOOTHED focus, not the raw target — a look-around used to read as
            // 100+ m/s "motion" and flap the fast-motion ring suppression on and off.
            velE = (detailFocusSmoothed.X - det25PrevTarget.X) / dt; // world X = east metres (local tangent)
            velN = (detailFocusSmoothed.Y - det25PrevTarget.Y) / dt; // world Y = north metres
        }

        det25PrevTarget = detailFocusSmoothed; det25PrevTargetValid = true; det25PrevClockMs = nowMs;

        // Base ortho already resident in the SHARED 3 GB budget → cap the detail ring on what is left.
        long baseBytes = 0;
        foreach (OrthoTile t in orthoTiles)
        {
            baseBytes += OrthoVramBudget.CellResidentBytes(t.Width, t.Height);
        }

        // The static 5 cm Morskie-Oko mosaic (unit 11) lives in the SAME 3 GB budget — count it so the det25 ring
        // can never overcommit VRAM against base + showcase (the design-panel shared-ledger rule).
        if (orthoDet05Texture != 0)
        {
            baseBytes += det05MosaicResidentBytes;
        }

        // BC1 (jedyna produkcyjna ścieżka desktop) kosztuje ChainSize ≈ 1/8 RGBA — near-cap liczony ze
        // STAREJ arytmetyki RGBA dusił zasięg detalu przy pustym VRAM („zasięg 5 cm śmiesznie mały").
        long cellBytes = MapaTur.Application.Terrain.Bc1MipChain.ByteSize(det25Grid.CellPx);
        IReadOnlyList<int> desired;
        IReadOnlyList<int> fineDesired = Array.Empty<int>();
        int nearCap;
        if (det05StreamOn && twoLevelPolicy is not null)
        {
            // Two levels share ONE budget: the policy funds the det05 (fine) ring first (coverage-gated, with a
            // reserved det25 backing so 5 cm always has 25 cm under it), then det25 (coarse) from the remainder.
            // Hysteresis: hand the policy the CURRENTLY resident keys, so a small camera move never swaps
            // boundary cells (each swap = evict + a 2–3 s / 340 MB recompose the user sees as a reload).
            var residentFine = new HashSet<int>();
            foreach (DetailCellGpu c in det05Cells.Values)
            {
                if (c.LayerReady) { residentFine.Add(c.Key); }
            }

            var residentCoarse = new HashSet<int>();
            foreach (DetailCellGpu c in det25Cells.Values)
            {
                if (c.Texture != 0 || c.LayerReady) { residentCoarse.Add(c.Key); }
            }

            MapaTur.Application.Terrain.TwoLevelDesired plan = twoLevelPolicy.Plan(
                eye, velE, velN, baseBytes, residentFine, residentCoarse);
            fineDesired = plan.FineCells;
            desired = plan.CoarseCells;
            nearCap = Det25HardCapCells;
        }
        else
        {
            nearCap = OrthoVramBudget.SharedDetailNearCap(baseBytes, cellBytes, OrthoVramBudgetBytes, Det25HardCapCells);
            desired = det25Policy.DesiredCells(eye, velE, velN, nearCap);
        }

        det25LastDesired = desired.Count;
        var desiredSet = new HashSet<int>(desired);

        // Touch desired residents; TRACK newly-desired cells (compose is kicked separately, rate-limited below).
        foreach (int key in desired)
        {
            if (det25Cells.TryGetValue(key, out DetailCellGpu? cell))
            {
                cell.DesiredTick = det25FrameTick;
            }
            else
            {
                (int ci, int cj) = det25Grid.CellFromKey(key);
                var created = new DetailCellGpu
                {
                    Key = key,
                    Ci = ci,
                    Cj = cj,
                    Bounds = det25Grid.CellBounds(ci, cj),
                    Px = det25Grid.CellPx,
                    DesiredTick = det25FrameTick,
                };
                if (Det25OpkReady() && !Det25WindowCovered(ci, cj))
                {
                    created.Empty = true; // coverage-gate z index.bin — poza pokryciem baza kryje, zero I/O
                }

                det25Cells[key] = created;
            }
        }

        // Kick bounded off-thread `.opk` reads for waiting cells, nearest-first.
        foreach (int key in desired)
        {
            if (det25ReadInFlight >= DetailMaxConcurrentReads)
            {
                break;
            }

            if (det25Cells.TryGetValue(key, out DetailCellGpu? cell)
                && cell.Compose is null && !cell.Empty
                && cell.Texture == 0 && cell.StagingTexture == 0 && cell.Pending is null
                && !cell.LayerReady && cell.PendingBc1 is null) // array path (krok 4): warstwa gotowa/w drodze ≠ re-kick
            {
                int ci = cell.Ci, cj = cell.Cj;
                DetailCellGpu capture = cell;
                det25ReadInFlight++;
                int cellPx25 = det25Grid.CellPx;
                byte[] rentedBc1 = MapaTur.Application.Terrain.MeshBufferPool.Shared.RentBytes(
                    MapaTur.Application.Terrain.Bc1MipChain.ByteSize(cellPx25));
                cell.RentedBc1 = rentedBc1;
                string opkDir = Det25OpkDir!;
                int pitch = det25Grid.PitchTiles, coverage = det25Grid.CoverageTiles;
                int groupTiles = det25OpkIndex!.TilesPerCell;
                cell.Compose = Task.Run(() =>
                {
                    var swc = System.Diagnostics.Stopwatch.StartNew();
                    bool ok = MapaTur.Application.Terrain.OrthoPageWindowAssembler.TryAssembleDet25Window(
                        opkDir, ci, cj, pitch, coverage, groupTiles, rentedBc1, out _);
                    capture.ComposeMs = swc.Elapsed.TotalMilliseconds;
                    capture.FromOpk = ok;
                    return ok ? rentedBc1 : null;
                });
            }
        }

        // Harvest completed composes (non-blocking) → queue for strip-upload, or mark empty (base shows through).
        foreach (DetailCellGpu cell in det25Cells.Values)
        {
            if (cell.Compose is { IsCompleted: true } done)
            {
                cell.Compose = null;
                det25ReadInFlight = Math.Max(0, det25ReadInFlight - 1);
                byte[]? buf = done.IsCompletedSuccessfully ? done.Result : null;
                if (buf is null)
                {
                    cell.Empty = true; // outside the fetched footprint — no texture, never recomposed
                    ReleaseCellBuffer(cell); // the pooled destination goes straight back — nothing to upload
                }
                else if (det05Bc1On)
                {
                    // BC1 path: the payload IS the compressed chain (== RentedBc1).
                    cell.PendingBc1 = buf;
                    if (!det25UploadQueue.Contains(cell.Key))
                    {
                        det25UploadQueue.Add(cell.Key);
                    }
                }
                else
                {
                    if (cell.Rented is { } r && !ReferenceEquals(buf, r))
                    {
                        // The composer ignored the destination (allocating fallback) — recycle the unused rent.
                        MapaTur.Application.Terrain.MeshBufferPool.Shared.Return(r);
                    }

                    cell.Pending = buf;
                    cell.Rented = buf; // ownership continues through the strip-upload to promote/dispose
                    if (!det25UploadQueue.Contains(cell.Key))
                    {
                        det25UploadQueue.Add(cell.Key);
                    }
                }
            }
        }

        EvictDet25ToBudget(gl, desiredSet, nearCap);
        DrainDet25Uploads(gl);

        if (det05StreamOn)
        {
            StreamDet05(gl, fineDesired);
        }
    }

    // Evict least-recently-desired NON-desired cells until the resident count fits the near-cap (n ≤ ~20, rare).
    private void EvictDet25ToBudget(GL gl, HashSet<int> desired, int nearCap)
    {
        int over = det25Cells.Count - Math.Max(0, nearCap);
        while (over > 0)
        {
            // ANTI-CHURN (ZASADA 9): same off-screen-first victim pick as det05 — a mouse twitch must not
            // blur the midground the user is looking at.
            int victim = 0; long oldest = long.MaxValue; bool found = false;
            int victimAny = 0; long oldestAny = long.MaxValue; bool foundAny = false;
            foreach (DetailCellGpu c in det25Cells.Values)
            {
                if (desired.Contains(c.Key) || c.Compose is not null)
                {
                    continue; // never evict a cell mid-compose — it would orphan the running Task (heap balloon)
                }

                if (c.DesiredTick < oldestAny)
                {
                    oldestAny = c.DesiredTick; victimAny = c.Key; foundAny = true;
                }

                if (CellVisibleLastFrame(c.Bounds))
                {
                    continue;
                }

                if (c.DesiredTick < oldest)
                {
                    oldest = c.DesiredTick; victim = c.Key; found = true;
                }
            }

            if (!found && foundAny)
            {
                victim = victimAny; found = true;
            }

            if (!found)
            {
                break; // everything left is desired or still composing — cannot evict below the ring
            }

            DisposeDet25Cell(gl, det25Cells[victim]);
            det25Cells.Remove(victim);
            over--;
        }
    }

    private void DisposeDet25Cell(GL gl, DetailCellGpu cell)
    {
        // Capture liveness BEFORE the safety-net nulls Compose (see DisposeDet05Cell): a live task still
        // writes into the pooled buffer — it must be dropped, never returned to the pool.
        bool composeAlive = cell.Compose is not null;
        if (composeAlive)
        {
            // Evicting a cell mid-compose: release its concurrency slot (the running task's result is discarded),
            // else the in-flight counter LEAKS up to the cap and the streamer stops kicking — the ring stalls
            // half-filled and the near field drops back to the coarse base (the "why is 25 cm blurry" bug).
            det25ReadInFlight = Math.Max(0, det25ReadInFlight - 1);
            cell.Compose = null;
        }

        if (cell.Texture != 0)
        {
            gl.DeleteTexture(cell.Texture);
            // Subtract exactly what the promote added (BC1 chain vs RGBA differ 8× — see the ledger field).
            det25ResidentBytes -= cell.ResidentBytesLedger != 0
                ? cell.ResidentBytesLedger
                : OrthoVramBudget.CellResidentBytes(cell.Px, cell.Px);
            cell.ResidentBytesLedger = 0;
            cell.Texture = 0;
        }

        // Array path (krok 4): warstwa wraca do puli, ledger schodzi po tej samej liczbie co promote.
        if (cell.LayerReady)
        {
            det25ResidentBytes -= cell.ResidentBytesLedger;
            cell.ResidentBytesLedger = 0;
            cell.LayerReady = false;
        }

        if (cell.Layer >= 0)
        {
            det25ArrFreeLayers.Push(cell.Layer);
            cell.Layer = -1;
        }

        if (cell.StagingTexture != 0)
        {
            gl.DeleteTexture(cell.StagingTexture);
            cell.StagingTexture = 0;
        }

        if (composeAlive)
        {
            // deliberate drop — the orphaned task owns every buffer now
            cell.Rented = null; cell.Pending = null;
            cell.RentedBc1 = null; cell.PendingBc1 = null;
        }
        else
        {
            ReleaseCellBuffer(cell); // nulls Pending (queue guard reads it) and recycles the pooled buffer
        }

        cell.UploadedRows = 0;
        det25UploadQueue.Remove(cell.Key);
    }

    // Strip-sliced upload of composed det25 cells, time-budgeted like DrainOrthoUploads so a 67 MB cell never
    // freezes a frame: allocate the staging texture empty, TexSubImage2D rows in ~24 MB chunks across frames, then
    // GenerateMipmap + promote so the draw path starts sampling it (a partially filled texture is never sampled).
    private unsafe void DrainDet25Uploads(GL gl)
    {
        if (det25UploadQueue.Count == 0)
        {
            return;
        }

        double start = frameClock.ElapsedMilliseconds;
        const GLEnum maxAnisotropyPName = (GLEnum)0x84FF;
        Span<float> maxAniso = stackalloc float[1] { 1f };
        gl.GetFloat(maxAnisotropyPName, maxAniso);
        float aniso = maxAniso[0] < 1f ? 1f : maxAniso[0];

        while (det25UploadQueue.Count > 0)
        {
            int key = det25UploadQueue[0];
            if (!det25Cells.TryGetValue(key, out DetailCellGpu? cell)
                || (cell.Pending is null && cell.PendingBc1 is null))
            {
                det25UploadQueue.RemoveAt(0);
                continue;
            }

            byte[]? rgba = cell.Pending;
            byte[]? bc1 = cell.PendingBc1;
            int w = cell.Px, h = cell.Px;
            if (bc1 is not null)
            {
                EnsureDet25Array(gl);
            }

            bool arr = bc1 is not null && det25ArrayTexture != 0; // krok 4: BC1 → warstwa arraya (per-fragment wybór)
            if (arr)
            {
                if (cell.Layer < 0)
                {
                    if (!det25ArrFreeLayers.TryPop(out int layer))
                    {
                        break; // warstwy zajęte — eviction zwolni w późniejszej klatce
                    }

                    cell.Layer = layer;
                    cell.UploadedRows = 0;
                    cell.UploadLevel = 0;
                }

                gl.BindTexture(TextureTarget.Texture2DArray, det25ArrayTexture);
            }
            else if (cell.StagingTexture == 0)
            {
                cell.StagingTexture = gl.GenTexture();
                cell.UploadedRows = 0;
                cell.UploadLevel = 0;
                gl.BindTexture(TextureTarget.Texture2D, cell.StagingTexture);
                if (bc1 is not null)
                {
                    // Immutable BC1 storage with the full mip chain — filled level-by-level below.
                    gl.TexStorage2D(TextureTarget.Texture2D, (uint)(Math.ILogB(w) + 1),
                        (SizedInternalFormat)GlCompressedRgbS3tcDxt1, (uint)w, (uint)h);
                }
                else
                {
                    gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8, (uint)w, (uint)h, 0,
                        PixelFormat.Rgba, PixelType.UnsignedByte, null);
                }
            }
            else
            {
                gl.BindTexture(TextureTarget.Texture2D, cell.StagingTexture);
            }

            double upStart = frameClock.ElapsedMilliseconds;
            bool complete;
            if (bc1 is not null)
            {
                int totalLevels = Math.ILogB(w) + 1;
                while (cell.UploadLevel < totalLevels)
                {
                    int lv = cell.UploadLevel;
                    int lPx = Math.Max(1, w >> lv);
                    int blockRows = Math.Max(1, (lPx + 3) / 4);
                    int rowBytesBlk = Math.Max(1, lPx / 4) * 8;
                    int lvOffset = 0;
                    for (int j = 0; j < lv; j++)
                    {
                        int p = Math.Max(1, w >> j);
                        lvOffset += MapaTur.Application.Terrain.Bc1Encoder.EncodedSize(p, p);
                    }

                    int rowsPerChunk = Math.Max(1, OrthoUploadBytesPerChunk / rowBytesBlk);
                    int rows = Math.Min(rowsPerChunk, blockRows - cell.UploadedRows);
                    int bytes = Math.Min(rows * rowBytesBlk,
                        MapaTur.Application.Terrain.Bc1Encoder.EncodedSize(lPx, lPx) - (cell.UploadedRows * rowBytesBlk));
                    int yoff = cell.UploadedRows * 4;
                    uint height = (uint)Math.Min(lPx - yoff, rows * 4);
                    long srcOff = lvOffset + ((long)cell.UploadedRows * rowBytesBlk);
                    fixed (byte* p = &bc1[srcOff])
                    {
                        if (arr)
                        {
                            gl.CompressedTexSubImage3D(TextureTarget.Texture2DArray, lv, 0, yoff, cell.Layer,
                                (uint)lPx, height, 1, (InternalFormat)GlCompressedRgbS3tcDxt1, (uint)bytes, p);
                        }
                        else
                        {
                            gl.CompressedTexSubImage2D(TextureTarget.Texture2D, lv, 0, yoff,
                                (uint)lPx, height, (InternalFormat)GlCompressedRgbS3tcDxt1, (uint)bytes, p);
                        }
                    }

                    cell.UploadedRows += rows;
                    if (cell.UploadedRows >= blockRows)
                    {
                        cell.UploadLevel++;
                        cell.UploadedRows = 0;
                    }

                    if (frameClock.ElapsedMilliseconds - start >= Det25UploadBudgetMsPerFrame)
                    {
                        break;
                    }
                }

                complete = cell.UploadLevel >= totalLevels;
            }
            else
            {
                int rowBytes = w * 4;
                int rowsPerChunk = Math.Max(1, OrthoUploadBytesPerChunk / Math.Max(1, rowBytes));
                while (cell.UploadedRows < h)
                {
                    int rows = Math.Min(rowsPerChunk, h - cell.UploadedRows);
                    fixed (byte* p = &rgba![(long)cell.UploadedRows * rowBytes])
                    {
                        gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, cell.UploadedRows, (uint)w, (uint)rows,
                            PixelFormat.Rgba, PixelType.UnsignedByte, p);
                    }

                    cell.UploadedRows += rows;
                    if (frameClock.ElapsedMilliseconds - start >= Det25UploadBudgetMsPerFrame)
                    {
                        break;
                    }
                }

                complete = cell.UploadedRows >= h;
            }

            cell.UploadMs += frameClock.ElapsedMilliseconds - upStart;

            if (complete && arr)
            {
                // Array path (krok 4): warstwa gotowa — slot AABB w BindDet25ForTile ją podniesie; brak
                // per-cell tekstur, mipy z chaina, ledger = rozmiar chaina.
                cell.LayerReady = true;
                cell.PromoteMs = frameClock.ElapsedMilliseconds;
                long rb = MapaTur.Application.Terrain.Bc1MipChain.ByteSize(w);
                cell.ResidentBytesLedger = rb;
                det25ResidentBytes += rb;
                ReleaseCellBuffer(cell);
                det25UploadQueue.RemoveAt(0);
                Log.Information("[Det25] cell ({Ci},{Cj}) {Src} {C:F0}ms | ARR layer {Layer} resident (BC1)",
                    cell.Ci, cell.Cj, "opk-read",
                    cell.ComposeMs, cell.Layer);
            }
            else if (complete)
            {
                double mmStart = frameClock.ElapsedMilliseconds;
                if (bc1 is null)
                {
                    gl.GenerateMipmap(TextureTarget.Texture2D); // RGBA fallback only — BC1 chains carry their mips
                }

                cell.MipmapMs = frameClock.ElapsedMilliseconds - mmStart;
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                if (aniso > 1f)
                {
                    gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, aniso);
                }

                cell.Texture = cell.StagingTexture;
                cell.StagingTexture = 0;
                long residentBytes = bc1 is not null
                    ? MapaTur.Application.Terrain.Bc1MipChain.ByteSize(w)
                    : OrthoVramBudget.CellResidentBytes(w, h);
                cell.ResidentBytesLedger = residentBytes;
                det25ResidentBytes += residentBytes;
                ReleaseCellBuffer(cell); // the final upload has copied — recycle the pooled buffer
                det25UploadQueue.RemoveAt(0);
                Log.Information("[Det25] cell ({Ci},{Cj}) opk-read {C:F0}ms{Cache} | upload {U:F1}ms | mipmap {M:F1}ms | {Px}px ({Fmt})",
                    cell.Ci, cell.Cj, cell.ComposeMs, string.Empty,
                    cell.UploadMs, cell.MipmapMs, w, bc1 is not null ? "BC1" : "RGBA");
            }

            if (frameClock.ElapsedMilliseconds - start >= Det25UploadBudgetMsPerFrame)
            {
                break;
            }
        }

        gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    // Per terrain draw: bind the nearest resident det25 cell (containing the tile's geographic centre) to unit 9
    // and set its world-space AABB, or gate det25 OFF for this tile. CellForPoint's CellContains invariant means
    // the picked cell fully covers a z17-sized tile, so the tile never straddles a boundary; overlap texels are
    // bit-identical between neighbours (assembler 1:1 placement) so tiles picking different cells still align.
    // Raz na klatkę: bind + uniformy siatki det1m (świat liczony z anchor — jak AABB det05). Gate tick jak
    // w BindDet05ForTile. useDet1m idzie w głównym bloku uniformów (A/B działa też zanim ta ścieżka wykona).
    private long det1mUniformsTick = -1;

    private unsafe void BindDet1mOncePerFrame(GL gl, TerrainMesh3D anchorMesh)
    {
        if (!det1mReady || det1mLoaded is null || det1mUniformsTick == det25FrameTick)
        {
            return;
        }

        det1mUniformsTick = det25FrameTick;
        MapaTur.Domain.Geography.MapBounds b = det1mLoaded.GridGeo;
        Vector3 sw = anchorMesh.GeoToWorld(b.SouthWest, 0f);
        Vector3 ne = anchorMesh.GeoToWorld(b.NorthEast, 0f);
        float minX = MathF.Min(sw.X, ne.X), maxX = MathF.Max(sw.X, ne.X);
        float minY = MathF.Min(sw.Y, ne.Y), maxY = MathF.Max(sw.Y, ne.Y);
        gl.ActiveTexture(TextureUnit.Texture14);
        gl.BindTexture(TextureTarget.Texture2DArray, det1mArrayTexture);
        gl.ActiveTexture(TextureUnit.Texture15);
        gl.BindTexture(TextureTarget.Texture2D, det1mCovTexture);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.Uniform1(det1mSamplerLoc, 14);
        gl.Uniform1(det1mCovLoc, 15);
        gl.Uniform2(det1mMinXyLoc, minX, maxY);
        gl.Uniform2(det1mInvSizeLoc, 1f / MathF.Max(1f, maxX - minX), 1f / MathF.Max(1f, maxY - minY));
        gl.Uniform2(det1mGridDimLoc, det1mLoaded.GridW, det1mLoaded.GridH);
        fixed (int* p = det1mSliceIdx)
        {
            gl.Uniform1(det1mSliceIdxLoc, 160, p);
        }
    }

    private void BindDet25ForTile(GL gl, TerrainMesh3D mesh, TerrainMesh3D anchorMesh)
    {
        BindDet1mOncePerFrame(gl, anchorMesh); // tier det1m współdzieli miejsce bindów detali (gate per klatkę)
        BindDet25ArrOncePerFrame(gl, anchorMesh); // krok 4: sloty arraya det25
        if (det25ArrayTexture != 0)
        {
            // Array aktywny: stary per-tile bind wyłączony. uOrthoDet25 siedzi NA STAŁE na unit 9 (pin przy
            // linku) — unit 10 należy wyłącznie do sampler2DArray, więc żaden stan warstw nie tworzy
            // konfliktu typów na unicie.
            gl.Uniform1(useDet25Location, 0);
            return;
        }

        if (det25Grid is null)
        {
            gl.Uniform1(useDet25Location, 0);
            return;
        }

        MapaTur.Domain.Geography.MapBounds b = mesh.Bounds;
        var centre = new MapaTur.Domain.Geography.GeoPoint(
            (b.SouthWest.Latitude + b.NorthEast.Latitude) * 0.5,
            (b.SouthWest.Longitude + b.NorthEast.Longitude) * 0.5);
        (int ci, int cj) = det25Grid.CellForPoint(centre);
        int key = det25Grid.CellKey(ci, cj);

        if (det25Cells.TryGetValue(key, out DetailCellGpu? cell) && cell.Texture != 0)
        {
            if (cell.Texture != det25BoundTexture)
            {
                gl.ActiveTexture(TextureUnit.Texture9); // 10 belongs to uOrthoDet25Arr (array type) — never share
                gl.BindTexture(TextureTarget.Texture2D, cell.Texture);
                gl.ActiveTexture(TextureUnit.Texture0);
                det25BoundTexture = cell.Texture;
            }

            Vector3 sw = anchorMesh.GeoToWorld(cell.Bounds.SouthWest, 0f);
            Vector3 ne = anchorMesh.GeoToWorld(cell.Bounds.NorthEast, 0f);
            gl.Uniform1(det25SamplerLocation, 9);
            gl.Uniform2(det25MinXyLocation, Math.Min(sw.X, ne.X), Math.Min(sw.Y, ne.Y));
            gl.Uniform2(det25MaxXyLocation, Math.Max(sw.X, ne.X), Math.Max(sw.Y, ne.Y));
            gl.Uniform1(useDet25Location, 1);
        }
        else
        {
            gl.Uniform1(useDet25Location, 0);
        }
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
        bool animateAtmosphere = true,
        IReadOnlyList<Trail>? offTrailTracks = null)
    {
        // Watch restarts at the very ENTRY (not at swap detection below): the measured first-swap frame had
        // ~950 ms between Render() entry and the old restart point — the setup segment (PlatformGl, watchdog,
        // BeginGpuFrame query readback, EnsureProgram) was invisible to the breakdown. Unconditional restart
        // is a few ns; the segments are only ever read on swap frames.
        dbgSwapWatch.Restart();
        renderStartTs = System.Diagnostics.Stopwatch.GetTimestamp(); // "setup" CPU bucket runs until the first GpuBegin
        renderFirstPassSeen = false;
        ghostDepthFrameValid = false; // last frame's depth resolve is stale the moment we start drawing
        hazeMaskValidThisFrame = false; // no fire this frame → no haze (a stale mask must never shimmer)

        gl ??= PlatformGl.Get();

        // Frame-gap watchdog: a long gap since the previous frame = a stall the per-pass timers don't see.
        // Logging the Gen2 delta + heap distinguishes a GC pause from off-thread-build CPU starvation.
        long nowFrameMs = frameClock.ElapsedMilliseconds;
        long frameGap = nowFrameMs - dbgLastFrameMs;
        int gen2Now = GC.CollectionCount(2);
        if (dbgLastFrameMs > 0 && frameGap > 150)
        {
            Log.Information(
                "[GL3D] frame gap {Gap}ms (gen2 +{Gen2Delta}, totalGen2={Gen2}, heap={HeapMB}MB, pendingUploads={Pending}, tileUp={TileMs:F0}ms/{TileN})",
                frameGap, gen2Now - dbgLastGen2, gen2Now, GC.GetTotalMemory(false) / (1024 * 1024), pendingTileUploads.Count,
                uploadTileDataMs, uploadTileDataCount);
        }

        uploadTileDataMs = 0; uploadTileDataCount = 0; // per-frame attribution window

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
            offTrailLines = null;
            lastOffTrailTracks = null;
            lastOffTrailRaster = null;
            lastOffTrailMesh = null;
            lastOffTrailDetail = null;
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
            cumulusCoverageLocation = -1;
            cumulusMemberSeedLocation = -1;
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
            debugTerrainViewLocation = -1;
            orthoMinXyLocation = -1;
            orthoMaxXyLocation = -1;
            orthoBlendLocation = -1;
            det25SamplerLocation = -1;
            det05SamplerLocation = -1;
            useDet25Location = -1;
            useDet05Location = -1;
            det25MinXyLocation = -1;
            det25MaxXyLocation = -1;
            det05MinXyLocation = -1;
            det05MaxXyLocation = -1;
            det05ArrSamplerLocation = -1;
            det05ArrBSamplerLocation = -1;
            det05ArrALoc = -1; det05ArrCSamplerLocation = -1;
            det05CellHashLoc = -1; det05HashSeedLoc = -1;
            det05GridOriginLoc = -1; det05GridPitchLoc = -1; det05CellSizeLoc = -1;
            useDet05ArrLocation = -1;
            detailBlendLocation = -1;
            detailColorModeLocation = -1;
            det05ArrRawLocation = -1;
            det25ArrSamplerLoc = -1; det25CellHashLoc = -1; det25HashSeedLoc = -1; useDet25ArrLoc = -1;
            det25GridOriginLoc = -1; det25GridPitchLoc = -1; det25CellSizeLoc = -1;
            det25ArrayTexture = 0; det25ArrFreeLayers.Clear(); // kontekst padł — cele wrócą przez desired/kick
            det1mSamplerLoc = -1; det1mCovLoc = -1; useDet1mLoc = -1;
            det1mMinXyLoc = -1; det1mInvSizeLoc = -1; det1mGridDimLoc = -1; det1mSliceIdxLoc = -1;
            det1mDebugLoc = -1;
            det1mArrayTexture = 0; det1mCovTexture = 0; det1mReady = false; det1mUploadCursor = 0; // kontekst padł — realokacja w PumpDet1m
            detailDebugBoundsLocation = -1;
            det25EyeXyLocation = -1;
            det25FadeInnerLocation = -1;
            det25FadeOuterLocation = -1;
            slopeModeLocation = -1;
            rockStrengthLocation = -1;
            slopePaletteLocation = -1;
            terrainFogColorLocation = -1;
            terrainFogDensityLocation = -1;
            terrainCameraPosLocation = -1;
            terrainCloudAltitudeLocation = -1;
            terrainCloudNoiseScaleLocation = -1;
            terrainCloudWindLocation = -1;
            terrainCloudShadowOffsetLocation = -1;
            terrainCloudTimeLocation = -1;
            terrainCloudCoverageLocation = -1;
            terrainCloudShadowLocation = -1;
            terrainSnowStrengthLocation = -1;
            terrainSnowLineZLocation = -1;
            terrainFirnLineZLocation = terrainFirnBandZLocation = terrainFirnStrengthLocation = -1;
            terrainFirnDropZLocation = -1;
            terrainFirnSitesLocation = terrainFirnSiteCountLocation = -1;
            firnChannelOnLocation = -1;
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
            skyCloudSeedLocation = -1;
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
            dragonProgram = 0; // context lost → rebuild the skinned-dragon program + buffers on next draw
            dragonVao = 0;
            dragonTexture = 0; // texture id belongs to the dead context — re-decode/upload on next draw
            dragonTextureModel = null;
            aiDragonTextures.Clear(); // AI-flock texture ids belong to the dead context — re-decode on next draw
            fireProgram = 0; // fireball pass rebuilds with the dragon
            fireVao = 0;
            // Heat-haze resources belonged to the dead context — drop handles, re-probe fresh.
            fireHeatProgram = 0;
            hazeProgram = 0;
            hazeMaskFbo = hazeMaskTex = 0;
            hazeMaskW = hazeMaskH = 0;
            hazeColorFbo = hazeColorTex = 0;
            hazeColorW = hazeColorH = 0;
            hazeUnsupported = false;
            hazeMaskValidThisFrame = false;
            hazeStageLogged = false;
            sceneFboThisFrame = 0;
            markerProgram = 0; // debug-marker pass rebuilds with the dragon
            markerVao = 0;
            gearRibbonProgram = 0; // climb-gear pass rebuilds the same way
            gearRingProgram = 0;
            gearRibbonVao = 0;
            gearRingVao = 0;
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
            orthoUploadQueue.Clear();
            foreach (OrthoTile t in orthoTiles)
            {
                t.Texture = 0;
                t.StagingTexture = 0; // half-filled staging died with the context too
                t.UploadedRows = 0;
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
            // HDR state belongs to the dead context — re-probe the extension and re-pick formats fresh.
            hdrProbed = false;
            hdrUnsupported = false;
            presentIsHdr = false;
            msaaIsHdr = false;
            bloomIsHdr = false;
            lastPresentedFbo = 0;
            // Ghost-depth FBO / texture (x-ray rock-thickness gate) — same context-loss handling.
            ghostDepthFbo = 0;
            ghostDepthTex = 0;
            ghostDepthWidth = 0;
            ghostDepthHeight = 0;
            ghostDepthUnsupported = false;
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
            bloomCompTonemapLoc = bloomCompExposureLoc = -1;
            postTonemapLoc = postExposureLoc = -1;
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
            cascadeSplitLoc = shadowStrengthLoc = shadowTexelLoc = -1;
            aoStrengthLoc = -1;
            bakedShadowCompLoc = -1;
            shadowsActiveThisFrame = false;
            shadowValidLastFrame = false;
            // The planar-reflection target belonged to the dead context — drop the handles so it's rebuilt fresh.
            reflectionFbo = 0;
            reflectionColorTex = 0;
            reflectionDepthRb = 0;
            reflectionTexW = 0;
            reflectionTexH = 0;
            reflectionUnsupported = false;
            reflectionValidLastFrame = false;
            // The trail-decal mask texture belonged to the dead context; drop the handle and clear the cache keys
            // so EnsureTrailMask rebuilds + re-uploads it against the fresh context on the next frame. (The scratch
            // CPU buffers survive the context loss and are reused — only the GL texture is gone.)
            trailMaskTex = 0;
            trailMaskValid = false;
            waterMaskTex = 0;
            waterMaskValid = false;
            // Surface-ownership mask texture — same context-loss handling: drop the handle, force re-upload.
            baseCoverTex = 0;
            uploadedBaseCoverageMask = null;
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
        double dbgSetupMs = dbgTileSwapFrame ? dbgSwapWatch.Elapsed.TotalMilliseconds : 0;
        if (dbgTileSwapFrame)
        {
            // Incremental: keep the reused base tiles' VBOs, swap only the look-at detail patch (see SyncTiles).
            // SyncTiles now only evicts gone tiles + QUEUES new ones; the upload itself is spread over frames below.
            var swSync = System.Diagnostics.Stopwatch.StartNew();
            SyncTiles(gl, tiles);
            lastTiles = tiles;
            dbgSwapSyncMs = swSync.Elapsed.TotalMilliseconds;
        }

        double dbgDrainStart = dbgTileSwapFrame ? dbgSwapWatch.Elapsed.TotalMilliseconds : 0;
        DrainTileUploads(gl); // upload a few queued tiles per frame so a reload never freezes one frame
        if (dbgTileSwapFrame)
        {
            dbgSwapDrainMs = dbgSwapWatch.Elapsed.TotalMilliseconds - dbgDrainStart;
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
        // The MSAA probe can be the first to discover float RTs don't work (latching hdrUnsupported) — the
        // present target allocated a moment ago would still be RGBA16F, and the resolve blit requires both
        // sides to match. Re-ensure so the whole chain drops to Rgba8 together, before anything is drawn.
        if (presentIsHdr != WantHdrTargets(gl) && !EnsurePresentTarget(gl, vpWidth, vpHeight))
        {
            return 0;
        }

        gl.BindFramebuffer(FramebufferTarget.Framebuffer, useMsaa ? msaaFbo : presentFbo);
        sceneFboThisFrame = useMsaa ? msaaFbo : presentFbo; // mid-scene side passes (heat mask) restore THIS

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

        // RMP2 residency runs before every geometry pass: it only harvests completed worker I/O and uploads
        // at most two pages, so the render thread never opens a file or produces mesh/material data.
        photogrammetricRock.PrepareFrame(
            gl,
            camera,
            vpWidth,
            vpHeight,
            PhotogrammetricRockEnabled);

        // Cascaded Shadow Maps depth pass (Krok 5): render terrain depth from the sun's POV into the cascade
        // shadow maps before the sky/terrain passes. Self-contained — restores the bound FBO + viewport.
        double dbgShadowStart = dbgTileSwapFrame ? dbgSwapWatch.Elapsed.TotalMilliseconds : 0;
        GpuBegin(gl, GpuPass.Shadow);
        RenderShadowMaps(gl, camera, atmosphere?.SunDirection ?? Vector3.Zero, (float)width / Math.Max(1, height), vpWidth, vpHeight);
        GpuEnd(gl);
        if (dbgTileSwapFrame)
        {
            dbgSwapShadowMs = dbgSwapWatch.Elapsed.TotalMilliseconds - dbgShadowStart;
        }

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
        // DECK AT 1500 m (2026-07-05, user): the slider-driven clouds live on a FIXED real-altitude deck of
        // ~1500 m (× exaggeration — world Z is elevation × Pion, same convention as the camera floor), well
        // below the Tatra ridge line so peaks always stand above the layer. The old peak-relative altFraction
        // regime (deck riding 0.2–1.2 of the relief) is gone; what remains is a gentle wander (altNoise) plus
        // a SLIDER-SEEDED offset — every slider position re-rolls its own deck height a little, so raising /
        // lowering coverage never replays the identical sky (the "większa randomizacja" ask).
        float cloudSlider = baseCoverage;
        float lowDeck = Math.Clamp((cloudSlider - 0.78f) / 0.22f, 0f, 1f);
        lowDeck = lowDeck * lowDeck * (3f - (2f * lowDeck)); // smoothstep — 0 below ~80%, 1 at 100%
        float seaGate = Math.Max(inversion, lowDeck); // a high slider forces the low sheet on, no inversion needed

        // Slider-derived pseudo-random seeds: incommensurate sines of the RAW slider value give each slider
        // position its own stable pattern (same value → same sky, different value → visibly re-rolled sky).
        float sliderWander = (MathF.Sin(cloudSlider * 41.7f) * 0.6f) + (MathF.Sin((cloudSlider * 17.3f) + 1.1f) * 0.4f); // ~[-1,1]
        var cirrusSeed = new Vector2(MathF.Sin(cloudSlider * 37.3f) * 8f, MathF.Cos(cloudSlider * 23.9f) * 8f);
        var sheetSeedOffset = new Vector2(MathF.Sin(cloudSlider * 29.1f) * 6_000f, MathF.Cos(cloudSlider * 43.7f) * 6_000f);

        float exaggeration = geomFrame.VerticalExaggeration;
        const float CloudDeckBaseMeters = 1_500f;
        float cloudAltitude = (CloudDeckBaseMeters + (altNoise * 120f) + (sliderWander * 100f) - (storm * 200f)) * exaggeration;
        float cloudHalfExtent = MathF.Max(geomFrame.HorizontalExtent * 4f, 20_000f);
        float cloudNoiseScale = 1f / MathF.Max(geomFrame.HorizontalExtent * 0.5f, 4_000f);
        float seaCoverage = effectiveCoverage * seaGate; // the sea-of-clouds sheet only forms during inversion
        bool cloudsActive = animateAtmosphere && atmosphere is not null && effectiveCoverage > 0.001f && !float.IsNegativeInfinity(cloudMaxZ);
        // Cumulus condensation bases share the 1500 m deck (they are "the slider's clouds" too), with their
        // own independent wander so the puffs don't ride the exact sheet plane.
        float cumulusBase = (CloudDeckBaseMeters + (altNoise * 80f) + (MathF.Sin((cloudSlider * 31.9f) + 0.7f) * 110f)) * exaggeration;
        // Keep the cumulus opaque even as an inversion deepens at a high slider (lowDeck cancels the thinning),
        // so a 100% storm sky stays packed rather than fading to the bare sea sheet.
        float cumulusOpacity = 1.0f * (1f - (0.35f * inversion * (1f - lowDeck)));
        // Membership seed for the per-instance cumulus gate: re-rolled at every ~8% slider step, so dragging
        // the slider up/down brings DIFFERENT clouds in scattered places, not the same field denser/thinner.
        float cumulusSeed = MathF.Sin(MathF.Floor(cloudSlider * 12f) * 53.7f) * 0.5f + 0.5f;
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
            gl.Uniform2(skyCloudSeedLocation, cirrusSeed.X, cirrusSeed.Y);
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
        gl.Uniform1(debugTerrainViewLocation, DebugTerrainView);
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
        // Hi-res ortho detail overlay (PoC): upload the mosaics once, then bind + set their world AABB / gate
        // uniforms for BOTH the reflection pre-pass and the terrain pass (shared program). No-op when disabled.
        EnsureOrthoDetail(gl);
        BindAndSetOrthoDetail(gl, tiles);
        // Hi-res ortho detail STREAMING: keep the det25 (25 cm) cell ring around the camera composed + uploaded.
        // No per-frame bind — the cells are bound per-draw in the terrain loop (BindDet25ForTile). det05 (5 cm
        // Morskie-Oko showcase) stays the static per-frame mosaic on unit 11 above; det25 replaces the static
        // unit-10 mosaic with N streamed cells. use25 was gated OFF above (no static det25), so the reflection
        // pre-pass shows base+det05 only; the main terrain pass turns det25 on per tile below.
        StreamOrthoDetail(gl, tiles, camera.Position, camera.Target);

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
            // the small lift keeps a deep-orange sunset luminous enough to light the slopes. 2026-07-07: the
            // ×1.15 sun boost is dropped (was compounding with the 1.15 exposure = ~×1.32 on lit ground →
            // blown-out colour); neutral sun keeps the deep tones.
            sunCol = Vector3.Lerp(atmosphere.SunColor, Vector3.One, 0.15f);
            // Ambient fill = a bright, desaturated version of the zenith sky tint so shadowed
            // faces pick up a soft cool cast that contrasts with the warm sun.
            skyAmbient = Vector3.Lerp(atmosphere.SkyZenithColor, Vector3.One, 0.55f);
        }
        gl.Uniform3(sunColorLocation, sunCol.X, sunCol.Y, sunCol.Z);
        gl.Uniform3(skyAmbientLocation, skyAmbient.X, skyAmbient.Y, skyAmbient.Z);
        // B2 fire lights — uploaded BEFORE the reflection pre-pass, so the mirrored terrain in the water
        // glows from the same fire for free (same program, same uniforms).
        UploadFireLights(gl, terrainFireCountLoc, terrainFirePosLoc, terrainFireColorLoc, terrainFireInvR2Loc);
        // B4 scorch splats — same deal: albedo burns show in the reflection too.
        if (terrainScorchCountLoc >= 0)
        {
            gl.Uniform1(terrainScorchCountLoc, (float)scorchCount);
            if (scorchCount > 0)
            {
                if (terrainScorchPosLoc >= 0)
                {
                    gl.Uniform2(terrainScorchPosLoc, (uint)scorchCount, scorchPosFlat.AsSpan(0, scorchCount * 2));
                }

                if (terrainScorchParamLoc >= 0)
                {
                    gl.Uniform2(terrainScorchParamLoc, (uint)scorchCount, scorchParamFlat.AsSpan(0, scorchCount * 2));
                }
            }
        }

        // Cloud-shadow uniforms: feed the terrain the same cloud field the layer draws so moving
        // clouds throw moving shadows. Coverage 0 (or no atmosphere) disables it via the shader guard.
        gl.Uniform1(terrainCloudAltitudeLocation, cloudAltitude);
        gl.Uniform1(terrainCloudNoiseScaleLocation, cloudNoiseScale);
        gl.Uniform2(terrainCloudWindLocation, windVec.X, windVec.Y);
        gl.Uniform2(terrainCloudShadowOffsetLocation, sheetSeedOffset.X, sheetSeedOffset.Y);
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

        // Perennial firn gate in world-Z: the ~2000 m REAL line x Pion (PerennialFirn is the tested mirror).
        float firnExag = lightFrame.VerticalExaggeration > 0f ? lightFrame.VerticalExaggeration : 1f;
        gl.Uniform1(terrainFirnLineZLocation, MapaTur.Application.Terrain.PerennialFirn.LineMeters * firnExag);
        gl.Uniform1(terrainFirnBandZLocation, MapaTur.Application.Terrain.PerennialFirn.BandMeters * firnExag);
        gl.Uniform1(terrainFirnDropZLocation, MapaTur.Application.Terrain.PerennialFirn.RunoutDropMeters * firnExag);
        gl.Uniform1(terrainFirnStrengthLocation, FirnStrength);

        // Curated glacieret sites -> world XY + reach: the WHERE of the firn is data, not procedure.
        var firnSites = MapaTur.Application.Terrain.FirnSiteData.Sites;
        int firnCount = Math.Min(firnSites.Count, 12);
        for (int i = 0; i < firnCount; i++)
        {
            Vector3 sw = lightFrame.GeoToWorld(firnSites[i].Location, 0f);
            firnSiteScratch[(i * 4) + 0] = sw.X;
            firnSiteScratch[(i * 4) + 1] = sw.Y;
            firnSiteScratch[(i * 4) + 2] = (float)firnSites[i].RadiusMeters;
            firnSiteScratch[(i * 4) + 3] = (float)firnSites[i].MaxElevationMeters * firnExag; // tongue cap, world-Z
        }

        fixed (float* fs = firnSiteScratch)
        {
            gl.Uniform4(terrainFirnSitesLocation, 12, fs);
        }

        gl.Uniform1(terrainFirnSiteCountLocation, (float)firnCount);

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
        gl.Uniform1(firnChannelOnLocation, 0f);

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
        // Alternate-frame reflection (ThrottleReflection): on odd frames reuse the previous frame's texture
        // instead of re-rendering the whole mirrored terrain. Only when the target survived at the same size
        // — a resize, context loss or a disabled frame forces a fresh render first.
        if (!TemporalPassCadence.ShouldRefresh(gpuFrameCount, ThrottleReflection, reflectionValidLastFrame)
            && reflectionFbo != 0
            && reflectionTexW == Math.Max(16, vpWidth / 2) && reflectionTexH == Math.Max(16, vpHeight / 2))
        {
            reflectionDrawn = true; // water samples last frame's reflection — no pre-pass this frame
        }
        else if (ReflectionEnabled && tiles.Count > 0 && EnsureReflectionTarget(gl, vpWidth, vpHeight))
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
                // H5 (2026-07-23): the mirror used to draw EVERY resident tile — the whole-Tatra base ring,
                // ~1000 draws ≈ 32 ms GPU warm — with no culling at all. Two-stage cull: (1) XY distance —
                // a half-res, ripple-wobbled reflection resolves only the nearby walls; (2) H7: frustum test
                // against the MIRRORED MVP, so tiles behind/outside the reflected view don't submit vertices
                // (the mirror cost proved DRAW-bound, not fragment-bound — v2-final bench).
                Vector3 wmin = entry.Key.WorldMin, wmax = entry.Key.WorldMax;
                float rdx = Math.Max(0f, Math.Max(wmin.X - cameraWorldPos.X, cameraWorldPos.X - wmax.X));
                float rdy = Math.Max(0f, Math.Max(wmin.Y - cameraWorldPos.Y, cameraWorldPos.Y - wmax.Y));
                if ((rdx * rdx) + (rdy * rdy) > ReflectionMaxDistanceMeters * ReflectionMaxDistanceMeters
                    || !MapaTur.Application.Terrain.FrustumCuller.IsAabbVisible(reflMvp, wmin, wmax))
                {
                    continue;
                }

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

            if (PhotogrammetricRockEnabled)
            {
                photogrammetricRock.DrawMain(
                    gl,
                    reflMvp,
                    camera,
                    light,
                    ambient,
                    sunCol,
                    skyAmbient,
                    fogColor,
                    fogDensity,
                    sceneDepthTexture: 0,
                    maximumDistanceMeters: ReflectionMaxDistanceMeters);
                gl.UseProgram(program);
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
        reflectionValidLastFrame = reflectionDrawn;

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
            gl.Uniform1(aoStrengthLoc, AoStrength);
            gl.Uniform1(bakedShadowCompLoc, BakedShadowComp);
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
                gl.Uniform1(shadowTexelLoc, 1f / ShadowMapSize);
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
                gl.Uniform1(waterMaskStrengthLocation, Waterways is { Count: > 0 } ? 1f : 0f);
                gl.Uniform1(firnChannelOnLocation, 1f);
            }
        }

        // Surface-ownership mask: upload on change, bind on unit 8 (0/1 = ortho, 2-4 = CSM, 5 = trail mask,
        // 6 = water mask, 7 = scene depth). uBaseCoverOn gates the base-skin discard in the shader.
        EnsureBaseCoverageTexture(gl);
        if (baseCoverTex != 0 && uploadedBaseCoverageMask is { } bcm)
        {
            gl.ActiveTexture(TextureUnit.Texture8);
            gl.BindTexture(TextureTarget.Texture2D, baseCoverTex);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.Uniform1(baseCoverSamplerLocation, 8);
            gl.Uniform2(baseCoverMinXYLocation, bcm.WorldMinX, bcm.WorldMinY);
            gl.Uniform2(baseCoverSizeXYLocation, bcm.WorldSizeX, bcm.WorldSizeY);
            gl.Uniform1(baseCoverOnLocation, 1f);
        }
        else
        {
            gl.Uniform1(baseCoverOnLocation, 0f);
        }

        // Drape the ortho: bind each mesh tile's own cell texture (OrthoTileIndex) so a multi-cell ortho
        // stays sharp. Without textures the shader uses the hypsometric tint.
        bool anyOrtho = orthoTiles.Count > 0 && OrthoEnabled;
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.Uniform1(orthoSamplerLocation, 0);
        uint boundTexture = 0;
        det25BoundTexture = 0; // per-draw det25 cell bind (unit 10) dedup resets each frame
        float lastIsBaseSkin = -1f;
        double dbgTerrainStart = dbgTileSwapFrame ? dbgSwapWatch.Elapsed.TotalMilliseconds : 0;
        GpuBegin(gl, GpuPass.Terrain);
        // ANTI-CHURN guard input: remember this frame's frustum so the detail-cell eviction can refuse to
        // evict cells that are still ON SCREEN (see CellVisibleLastFrame).
        lastTerrainMvp = mvp;
        lastTerrainMvpValid = true;
        foreach (KeyValuePair<TerrainMesh3D, TileBuffers> entry in tileBuffers)
        {
            // H7 (2026-07-23): frustum-cull the MAIN terrain draws. The resident set is the whole streamed
            // massif (≈1000 tiles warm) but the camera sees a slice of it; drawing every resident tile scaled
            // the terrain pass 6 → 24 ms as residency grew. Same conservative test the shadow cascades already
            // use; the absolute `mvp` matches the shader (camera-relative offset cancels: vertex + uModelOffset
            // then Translate(R)·mvp ≡ absolute mvp).
            if (!frustumCullOff
                && !MapaTur.Application.Terrain.FrustumCuller.IsAabbVisible(mvp, entry.Key.WorldMin, entry.Key.WorldMax))
            {
                continue;
            }

            TileBuffers tile = entry.Value;
            float isBaseSkin = entry.Key.IsBaseSkin ? 1f : 0f;
            if (isBaseSkin > 0.5f && killLayers.Contains("baseskin"))
            {
                continue; // bisekcja: baza-skóra wyłączona
            }

            if (isBaseSkin != lastIsBaseSkin)
            {
                gl.Uniform1(isBaseSkinLocation, isBaseSkin);
                lastIsBaseSkin = isBaseSkin;
            }
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

            // Per-draw hi-res det25 (25 cm): bind the nearest resident detail cell to unit 10 for this tile.
            BindDet25ForTile(gl, entry.Key, tiles[0]);
            if (det05StreamOn)
            {
                BindDet05ForTile(gl, entry.Key, tiles[0]); // finest-wins 5 cm on unit 11
            }

            gl.BindVertexArray(tile.Vao);
            gl.DrawElements(PrimitiveType.Triangles, (uint)tile.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
        }

        if (PhotogrammetricRockEnabled)
        {
            uint rockSceneDepthTexture = 0;
            uint rockSceneFbo = useMsaa ? msaaFbo : presentFbo;
            if (photogrammetricRock.HasDrawablePages
                && ResolveSceneDepthToGhost(gl, rockSceneFbo, width, height))
            {
                rockSceneDepthTexture = ghostDepthTex;
            }

            photogrammetricRock.DrawMain(
                gl,
                mvp,
                camera,
                light,
                ambient,
                sunCol,
                skyAmbient,
                fogColor,
                fogDensity,
                rockSceneDepthTexture,
                maximumDistanceMeters: float.PositiveInfinity);
            if (rockSceneDepthTexture != 0)
            {
                // RMP2 writes replacement depth after the terrain snapshot. Later soft-particle and ghost-line
                // consumers must resolve again so their depth texture also includes the rock geometry.
                ghostDepthFrameValid = false;
            }

            // The following cleanup uniforms belong to the terrain program, not the RMP2 program.
            gl.UseProgram(program);
            if (reflectionDrawn)
            {
                // RMP2 temporarily owns unit 1 for the resolved terrain depth. Restore the lake reflection
                // binding expected by the shared terrain/water program before later water draws.
                gl.ActiveTexture(TextureUnit.Texture1);
                gl.BindTexture(TextureTarget.Texture2D, reflectionColorTex);
                gl.ActiveTexture(TextureUnit.Texture0);
            }
        }
        GpuEnd(gl); // Terrain
        gl.Uniform1(useDet25Location, 0); // det25 is per-tile terrain only — don't leak it into lake/forest draws
        if (det05StreamOn)
        {
            gl.Uniform1(useDet05ArrLocation, 0); // det05 array is terrain-only too — no leak into lake/forest draws
        }
        if (dbgTileSwapFrame)
        {
            dbgSwapTerrainMs = dbgSwapWatch.Elapsed.TotalMilliseconds - dbgTerrainStart;
        }

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
            // The breakdown is logged at the END of Render (the line pass below rebuilds trail ribbons on a
            // tile swap — measuring only up to here hid ~0.9 s of the first-swap gap).
        }
        else
        {
            BuildLakeWater(gl, tiles, raster);
        }

        double dbgAfterLakeMs = dbgTileSwapFrame ? dbgSwapWatch.Elapsed.TotalMilliseconds : 0;

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

        // The ridden dragon (F7): a rigged, CPU-skinned model drawn opaque + depth-tested in the ABSOLUTE world
        // frame, so the terrain occludes it correctly. Its own program/uniforms, so it doesn't disturb the shared
        // `m` MVP buffer the line pass restores below.
        GpuBegin(gl, GpuPass.Dragons); // 4 CPU-skinned models + fire — was the UNTIMED gap between passes
        DrawDragon(gl, mvp);
        DrawAiDragons(gl, mvp); // autonomous flock, same depth-tested scene
        DrawArrows(gl, mvp); // crossbow bolts in flight
        // Climbing layer order (back → front): rock skin (the sculpted wall) → route lines → hold dots →
        // the climber. So the routes read UNDER the hold points, and the climber is drawn LAST → always
        // over the points (user's request); the skin is part of the wall itself, so everything overlays it.
        DrawClimbRockSkin(gl, mvp, atmosphere?.SunDirection ?? new Vector3(0.35f, 0.2f, 0.91f));
        DrawClimbGear(gl, mvp, camera, vpHeight); // auto-belay rope + quickdraws + route topo lines (depth-tested)
        DrawClimbHoldMarkers(gl, mvp, camera); // climb hold dots (depth-tested, depth-write off)
        DrawHumanoid(gl, mvp); // 3rd-person avatar LAST — drawn over the hold dots
        // Soft particles (B1): resolve the scene depth NOW — terrain AND both dragons are in, so the fire
        // fades into rock and dragon bellies instead of a hard sprite cut. The x-ray line gate below reuses
        // this same resolve (fire writes no depth, so it stays valid).
        ResolveSceneDepthToGhost(gl, useMsaa ? msaaFbo : presentFbo, vpWidth, vpHeight);
        DrawFireballs(gl, mvp, camera); // breath fire right after its dragon (additive, depth-tested)
        DrawDebugMarkers(gl, mvp, camera); // diagnostic dots (always-on-top) — dragon-foot placement probe
        GpuEnd(gl);

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
        // X-RAY (ghost) pass: reversed depth test draws ONLY the fragments the terrain occludes, faded and
        // NARROWER than the solid line — a trail dipping behind a rib (żleb Kulczyńskiego) reads as a thin
        // faint hint instead of vanishing. Runs ONLY with the ROCK-THICKNESS GATE available (scene depth
        // blitted to ghostDepthTex; the line shader shows the ghost only where the trail lies < 25–60 m
        // behind the visible surface). Without the gate the reversed test would draw trails behind whole
        // massifs as dotted twins ("jakby były dwa szlaki" — the 2026-07-03 saga), so no depth ⇒ NO ghost.
        // Depth writes off (ghosts must not pollute the depth buffer); blending on for the fade.
        uint sceneFbo = useMsaa ? msaaFbo : presentFbo;
        // Usually already resolved this frame (the fire's soft-particle fade runs first and nothing since
        // writes depth); the call is then a no-op returning true. Draws fresh only when fire skipped it.
        bool ghostDepthOk = ResolveSceneDepthToGhost(gl, sceneFbo, width, height);
        gl.Uniform1(lineSceneDepthOnLocation, ghostDepthOk ? 1f : 0f);
        if (ghostDepthOk)
        {
            gl.ActiveTexture(TextureUnit.Texture7);
            gl.BindTexture(TextureTarget.Texture2D, ghostDepthTex);
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.Uniform1(lineSceneDepthLocation, 7);
            gl.Uniform2(lineDepthNearFarLocation, camera.NearPlane, camera.FarPlane);
            gl.Uniform1(lineGhostFadeLocation, 0.30f);
            gl.DepthFunc(DepthFunction.Greater);
            gl.DepthMask(false);
            gl.Enable(EnableCap.Blend);
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            // Re-assert DepthMask(false) after each call: DrawTrailLines/DrawRouteLine restore it to true
            // internally, and a ghost fragment writing its (farther) depth would corrupt later passes.
            DrawTrailLines(gl, dedupedTrails, raster, frame, detail, GhostWidthScale);
            gl.DepthMask(false);
            DrawExposedRoutes(gl, exposedRoutes, raster, frame, detail, GhostWidthScale);
            gl.DepthMask(false);
            DrawRouteLine(gl, route, dedupedTrails, raster, frame, detail, GhostWidthScale);
            gl.DepthMask(true);
            gl.DepthFunc(DepthFunction.Less);
        }

        // Solid pass: normal depth test (real ridges still occlude into the ghost above, near z-fights handled
        // by the clip-space bias in the vertex shader).
        gl.Uniform1(lineGhostFadeLocation, 1f);
        DrawRoadLines(gl, roads, raster, frame, detail);
        // Use the DEDUPED trails for both the overlay and the route conflation: one of a duplicate pair is drawn,
        // and the route is re-laid onto that single remaining trail (so it can't snap to the dropped copy beside it).
        DrawTrailLines(gl, dedupedTrails, raster, frame, detail);
        DrawExposedRoutes(gl, exposedRoutes, raster, frame, detail);
        DrawRouteLine(gl, route, dedupedTrails, raster, frame, detail);
        DrawOffTrailLines(gl, offTrailTracks, raster, frame, detail);
        DrawCableCar(gl, frame, raster, detail);

        gl.BindVertexArray(0);
        GpuEnd(gl); // Lines
        double dbgAfterLinesMs = dbgTileSwapFrame ? dbgSwapWatch.Elapsed.TotalMilliseconds : 0;

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
            // Slider-seeded field offset: the sheet's noise is anchored to the centre, so shifting the centre
            // by a slider-derived few km re-rolls the whole undulation pattern as the slider moves.
            gl.Uniform2(cloudCenterLocation, geomFrame.Center.X + sheetSeedOffset.X, geomFrame.Center.Y + sheetSeedOffset.Y);
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
                // The "Zachmurzenie" slider sets the per-puff hash threshold (WHICH and how many puffs exist);
                // the stepped member seed re-rolls the membership as the slider moves.
                DrawCumulus(gl, m, camera, atmosphere!, new Vector2(geomFrame.Center.X, geomFrame.Center.Y),
                    cumulusBase, cumDrift, cumulusOpacity, Math.Clamp(effectiveCoverage, 0f, 1f), cumulusSeed,
                    fogColor, fogDensity, stormDarken, lightningFlash);
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
        float bloomThreshold = atmosphere?.BloomThreshold ?? 1f;
        // Dragon fire must glow — force bloom on (day OR night, where it's normally gated off). On the HDR
        // chain the white-hot core carries real > 1 energy, so it clears the atmosphere's own threshold
        // honestly; only the LDR fallback (core clamped at 1.0) still needs the threshold dropped under it.
        if (fireballs is { Count: > 0 })
        {
            bloomIntensity = MathF.Max(bloomIntensity, 0.6f);
            if (!presentIsHdr)
            {
                bloomThreshold = MathF.Min(bloomThreshold, 0.72f);
            }
        }

        GpuBegin(gl, GpuPass.Post);
        uint finalTex = RunPostProcess(
            gl, presentColorTex, vpWidth, vpHeight,
            bloomThreshold, bloomIntensity,
            sunUv.X, sunUv.Y, godrayIntensity);
        GpuEnd(gl);

        // Where the frame we just returned actually lives — the recorder reads THIS (post-processed, and
        // always LDR; the raw present FBO is RGBA16F under HDR, which UNSIGNED_BYTE ReadPixels can't read).
        lastPresentedFbo = finalTex == postColorTex && postFbo != 0 ? postFbo : presentFbo;

        // Unbind everything before returning. The caller will re-establish whatever framebuffer Skia
        // expects (via GRContext.ResetContext) before sampling the texture we just produced.
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        if (dbgTileSwapFrame)
        {
            double swapElapsed = dbgSwapWatch.Elapsed.TotalMilliseconds;
            if (swapElapsed > 30.0) // only log genuine hitch frames
            {
                // lines = the lake→lines span (trail/route ribbon rebuild against the new tile set);
                // tail = clouds + post + everything after; other = the un-checkpointed gaps before lake
                // (present/MSAA alloc, sky, uniforms). If "other" dominates, the next checkpoint goes THERE.
                double linesMs = Math.Max(0, dbgAfterLinesMs - dbgAfterLakeMs);
                double tailMs = Math.Max(0, swapElapsed - dbgAfterLinesMs);
                double accounted = dbgSetupMs + dbgSwapSyncMs + dbgSwapDrainMs + dbgSwapOrthoMs
                    + dbgSwapShadowMs + dbgSwapTerrainMs + dbgSwapLakeMs + linesMs + tailMs;
                Log.Information(
                    "[GL3D] tile-swap render hitch: setup={Setup:F0} sync={Sync:F0} drain={Drain:F0} ortho={Ortho:F0} "
                    + "shadow={Shadow:F0} terrain={Terrain:F0} lake={Lake:F0} lines={Lines:F0} tail={Tail:F0} "
                    + "other={Other:F0} elapsed={Elapsed:F0}ms ({Tiles} tiles)",
                    dbgSetupMs, dbgSwapSyncMs, dbgSwapDrainMs, dbgSwapOrthoMs, dbgSwapShadowMs, dbgSwapTerrainMs,
                    dbgSwapLakeMs, linesMs, tailMs, Math.Max(0, swapElapsed - accounted), swapElapsed, tiles.Count);
            }
        }

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

                double sumCpu = renderSetupCpuMs;
                for (int p = 0; p < (int)GpuPass.Count; p++)
                {
                    sumCpu += lastPassCpuMs[p];
                }

                Log.Information(
                    "[GL3D] [PassTimes] shadow={Shadow:F2} refl={Refl:F2} terrain={Terrain:F2} lakeforest={Lake:F2} dragons={Dragons:F2} lines={Lines:F2} clouds={Clouds:F2} post={Post:F2} sumGpu={Sum:F2}ms "
                    + "| cpu setup={CSet:F1} sh={CSh:F1} rf={CRf:F1} tr={CTr:F1} lf={CLf:F1} dr={CDr:F1} ln={CLn:F1} cl={CCl:F1} po={CPo:F1} sumCpu={CSum:F1}ms",
                    lastPassMs[(int)GpuPass.Shadow], lastPassMs[(int)GpuPass.Reflection], lastPassMs[(int)GpuPass.Terrain],
                    lastPassMs[(int)GpuPass.LakesForest], lastPassMs[(int)GpuPass.Dragons], lastPassMs[(int)GpuPass.Lines], lastPassMs[(int)GpuPass.Clouds],
                    lastPassMs[(int)GpuPass.Post], sum,
                    renderSetupCpuMs,
                    lastPassCpuMs[(int)GpuPass.Shadow], lastPassCpuMs[(int)GpuPass.Reflection], lastPassCpuMs[(int)GpuPass.Terrain],
                    lastPassCpuMs[(int)GpuPass.LakesForest], lastPassCpuMs[(int)GpuPass.Dragons], lastPassCpuMs[(int)GpuPass.Lines],
                    lastPassCpuMs[(int)GpuPass.Clouds], lastPassCpuMs[(int)GpuPass.Post], sumCpu);
            }
        }

        gpuFrameCount++;
    }

    // Begin a pass's GPU timer (into this frame's ring slot). Only ONE GL_TIME_ELAPSED query may be active at a
    // time, so GpuBegin/GpuEnd must bracket SEQUENTIAL, non-nesting passes — which every pass below is.
    // Also stamps CPU wall-time per pass (command-recording cost — GPU numbers alone hid a 30 ms CPU wall),
    // and the first GpuBegin of a frame closes the "setup" bucket (uploads/ensure work before any pass).
    private void GpuBegin(GL g, GpuPass pass)
    {
        long ts = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!renderFirstPassSeen)
        {
            renderFirstPassSeen = true;
            renderSetupCpuMs = (ts - renderStartTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        cpuPassActive = pass;
        passCpuStartTs = ts;
        if (gpuTimersSupported && gpuQueries is not null)
        {
            g.BeginQuery(GlTimeElapsedExt, gpuQueries[((int)pass * GpuFramesInFlight) + gpuFrameSlot]);
        }
    }

    private void GpuEnd(GL g)
    {
        lastPassCpuMs[(int)cpuPassActive] =
            (System.Diagnostics.Stopwatch.GetTimestamp() - passCpuStartTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
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
    /// it reads the exact GL output (the post-processed frame when the post chain ran), sidestepping the
    /// SKGLView back-buffer staleness that returned a cleared buffer for every frame after the first. Must
    /// be called on the GL thread with the context current, right after <see cref="Render"/> (it reads the
    /// FBO that produced the returned texture). GL's origin is bottom-left, so rows are flipped to
    /// top-first here. Returns false when readback isn't possible.
    /// </summary>
    public bool TryReadPresentFrame(byte[] dst, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(dst);
        GL? g = gl;
        // Read the FBO holding the frame Render() actually returned — the post FBO when the post chain ran
        // (so the capture includes bloom/tonemap), else the present FBO. Under HDR the raw present FBO is
        // RGBA16F, which this UNSIGNED_BYTE readback can't read — but then the post chain is guaranteed on,
        // so lastPresentedFbo is the Rgba8 post target.
        uint readFbo = lastPresentedFbo;
        if (readFbo == presentFbo && presentIsHdr)
        {
            return false;
        }

        if (g is null || readFbo == 0 || width <= 0 || height <= 0)
        {
            return false;
        }

        int stride = width * 4;
        int needed = stride * height;
        if (dst.Length < needed)
        {
            return false;
        }

        g.BindFramebuffer(FramebufferTarget.ReadFramebuffer, readFbo);
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
    /// Probes GL_EXT_color_buffer_float once per context and answers whether the scene targets should be
    /// HDR this frame. HDR additionally requires a live post chain: the view wraps whatever Render()
    /// returns as a GL_RGBA8 SKImage, so a float texture may only ever reach it through the ACES
    /// composite/pass-through into the Rgba8 <see cref="postColorTex"/>.
    /// </summary>
    private bool WantHdrTargets(GL g)
    {
        if (!hdrProbed)
        {
            hdrProbed = true;
            hdrUnsupported = !g.IsExtensionPresent("GL_EXT_color_buffer_float");
            Log.Information(
                hdrUnsupported
                    ? "[GL3D] HDR scene targets OFF (no GL_EXT_color_buffer_float) — staying on the Rgba8 pipeline"
                    : "[GL3D] HDR scene targets ON (RGBA16F scene/present, R11F_G11F_B10F bloom mips)");
        }

        return !hdrUnsupported && postProcessEnabled && postProgram != 0 && !postUnsupported;
    }

    /// <summary>
    /// Creates / resizes the single-sampled colour-texture FBO we return to the caller. Returns false (and
    /// sets <see cref="presentUnsupported"/> for the session) when the framebuffer is incomplete — the
    /// caller then falls back to Skia. RGBA16F when HDR is active (an incomplete HDR attempt latches
    /// <see cref="hdrUnsupported"/> and retries as Rgba8), else RGBA8; linear filtering, clamp-to-edge.
    /// Under HDR the caller-facing texture is postColorTex (Rgba8) — Skia never sees the float texture.
    /// </summary>
    private bool EnsurePresentTarget(GL g, int width, int height)
    {
        if (presentUnsupported)
        {
            return false;
        }

        bool hdr = WantHdrTargets(g);
        if (presentFbo != 0 && presentWidth == width && presentHeight == height && presentIsHdr == hdr)
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
            TextureTarget.Texture2D, 0, (int)(hdr ? InternalFormat.Rgba16f : InternalFormat.Rgba8),
            (uint)width, (uint)height, 0,
            PixelFormat.Rgba, hdr ? PixelType.HalfFloat : PixelType.UnsignedByte, null);
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
            g.DeleteFramebuffer(presentFbo);
            g.DeleteTexture(presentColorTex);
            g.DeleteRenderbuffer(presentDepthRb);
            presentFbo = 0;
            presentColorTex = 0;
            presentDepthRb = 0;
            if (hdr)
            {
                // The float target is the experiment — drop to the proven Rgba8 layout, don't kill GL.
                Log.Information("[GL3D] HDR present framebuffer incomplete ({Status}) — falling back to Rgba8 targets", status);
                hdrUnsupported = true;
                return EnsurePresentTarget(g, width, height);
            }

            Log.Information("[GL3D] present framebuffer incomplete ({Status}) — falling back to Skia", status);
            presentUnsupported = true;
            return false;
        }

        presentWidth = width;
        presentHeight = height;
        presentIsHdr = hdr;
        return true;
    }

    // Uploads the surface-ownership mask (R8, NEAREST — the discard wants exact texel edges, not blended
    // half-values) when the VM hands a new one. Null mask just flips uploadedBaseCoverageMask so the caller
    // sets uBaseCoverOn = 0; the texture object is reused across uploads.
    private void EnsureBaseCoverageTexture(GL gl)
    {
        if (ReferenceEquals(this.uploadedBaseCoverageMask, BaseCoverageMask))
        {
            return;
        }

        MapaTur.Application.Terrain.BaseCoverageMask? mask = BaseCoverageMask;
        if (mask is null)
        {
            this.uploadedBaseCoverageMask = null;
            return;
        }

        if (this.baseCoverTex == 0)
        {
            this.baseCoverTex = gl.GenTexture();
        }

        gl.ActiveTexture(TextureUnit.Texture8);
        gl.BindTexture(TextureTarget.Texture2D, this.baseCoverTex);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1); // tightly-packed single-channel rows
        fixed (byte* p = mask.Coverage)
        {
            gl.TexImage2D(
                TextureTarget.Texture2D, 0, (int)InternalFormat.R8,
                (uint)mask.Width, (uint)mask.Height, 0,
                PixelFormat.Red, PixelType.UnsignedByte, p);
        }

        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        gl.BindTexture(TextureTarget.Texture2D, 0);
        gl.ActiveTexture(TextureUnit.Texture0);
        this.uploadedBaseCoverageMask = mask;
    }

    /// <summary>
    /// Resolves (blits) the scene depth into <see cref="ghostDepthTex"/> ONCE per frame and leaves the scene
    /// FBO bound. Two consumers share the single resolve: the fire's soft-particle fade (called right after
    /// the dragons, BEFORE DrawFireballs, so fire fades against rock AND dragon bodies) and the x-ray line
    /// gate later (fire doesn't write depth, so the resolve stays valid). Returns false when depth is
    /// unavailable — consumers then fall back to their hard-edged behaviour.
    /// </summary>
    private bool ResolveSceneDepthToGhost(GL g, uint sceneFbo, int width, int height)
    {
        if (ghostDepthFrameValid)
        {
            return true;
        }

        if (sceneFbo == 0 || !EnsureGhostDepthTarget(g, Math.Max(1, width), Math.Max(1, height)))
        {
            return false;
        }

        // MSAA path: BlitFramebuffer resolves the multisampled depth to the single-sample texture
        // (formats match: DepthComponent24 → DepthComponent24, NEAREST — both blit requirements).
        g.BindFramebuffer(FramebufferTarget.ReadFramebuffer, sceneFbo);
        g.BindFramebuffer(FramebufferTarget.DrawFramebuffer, ghostDepthFbo);
        g.BlitFramebuffer(
            0, 0, width, height, 0, 0, width, height,
            (uint)ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        g.BindFramebuffer(FramebufferTarget.Framebuffer, sceneFbo);
        ghostDepthFrameValid = true;
        return true;
    }

    /// <summary>
    /// Ensures the ghost-depth target (a full-resolution DEPTH texture + depth-only FBO) matches the given
    /// size. The scene depth (msaa or present) is blitted into it just before the x-ray ghost pass so the
    /// line shader can read "how deep behind the visible surface" each occluded fragment lies (the
    /// rock-thickness gate). Format matches the scene depth (DepthComponent24) — a blit requirement.
    /// Returns false (and latches <see cref="ghostDepthUnsupported"/>) when incomplete; the ghost pass then
    /// falls back to the ungated x-ray rather than losing the feature.
    /// </summary>
    private bool EnsureGhostDepthTarget(GL g, int width, int height)
    {
        if (ghostDepthUnsupported || width <= 0 || height <= 0)
        {
            return false;
        }

        if (ghostDepthFbo != 0 && ghostDepthWidth == width && ghostDepthHeight == height)
        {
            return true;
        }

        g.DeleteFramebuffer(ghostDepthFbo);
        g.DeleteTexture(ghostDepthTex);

        ghostDepthTex = g.GenTexture();
        g.BindTexture(TextureTarget.Texture2D, ghostDepthTex);
        g.TexImage2D(
            TextureTarget.Texture2D, 0, (int)InternalFormat.DepthComponent24,
            (uint)width, (uint)height, 0,
            PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
        // NEAREST + no compare mode: the shader reads raw depth values (sampler2D.r), not a shadow lookup.
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        g.BindTexture(TextureTarget.Texture2D, 0);

        ghostDepthFbo = g.GenFramebuffer();
        g.BindFramebuffer(FramebufferTarget.Framebuffer, ghostDepthFbo);
        g.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, ghostDepthTex, 0);
        // Depth-only FBO: tell GL there is deliberately no colour output (some drivers flag incompleteness
        // otherwise).
        g.DrawBuffers(1, stackalloc GLEnum[] { GLEnum.None });
        g.ReadBuffer(GLEnum.None);

        GLEnum status = g.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        g.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete)
        {
            Log.Information("[GL3D] ghost-depth framebuffer incomplete ({Status}) — x-ray rock-thickness gate disabled this session", status);
            g.DeleteFramebuffer(ghostDepthFbo);
            g.DeleteTexture(ghostDepthTex);
            ghostDepthFbo = 0;
            ghostDepthTex = 0;
            ghostDepthUnsupported = true;
            return false;
        }

        ghostDepthWidth = width;
        ghostDepthHeight = height;
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

    /// <summary>
    /// Creates a clamped, linear-filtered colour texture of the given size — RGBA8, or the alpha-less
    /// R11F_G11F_B10F when <paramref name="hdr"/> (the bloom/god-ray mips: cheaper than RGBA16F, and every
    /// reader only ever samples .rgb; they are render-target-only, never blitted, so the missing alpha is safe).
    /// </summary>
    private static uint MakeColorTexture(GL g, int w, int h, bool hdr = false)
    {
        uint tex = g.GenTexture();
        g.BindTexture(TextureTarget.Texture2D, tex);
        g.TexImage2D(
            TextureTarget.Texture2D, 0, (int)(hdr ? InternalFormat.R11fG11fB10f : InternalFormat.Rgba8),
            (uint)w, (uint)h, 0,
            hdr ? PixelFormat.Rgb : PixelFormat.Rgba,
            hdr ? PixelType.UnsignedInt10f11f11fRev : PixelType.UnsignedByte, null);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        g.BindTexture(TextureTarget.Texture2D, 0);
        return tex;
    }

    /// <summary>Half-res R-channel heat mask for the haze (R16F on the HDR chain — heat sums past 1 — else R8).</summary>
    private bool EnsureHazeMask(GL g, int w, int h)
    {
        if (hazeUnsupported)
        {
            return false;
        }

        bool hdr = WantHdrTargets(g);
        if (hazeMaskFbo != 0 && hazeMaskW == w && hazeMaskH == h && hazeMaskIsHdr == hdr)
        {
            return true;
        }

        g.DeleteFramebuffer(hazeMaskFbo);
        g.DeleteTexture(hazeMaskTex);
        hazeMaskTex = g.GenTexture();
        g.BindTexture(TextureTarget.Texture2D, hazeMaskTex);
        g.TexImage2D(
            TextureTarget.Texture2D, 0, (int)(hdr ? InternalFormat.R16f : InternalFormat.R8),
            (uint)w, (uint)h, 0, PixelFormat.Red, hdr ? PixelType.HalfFloat : PixelType.UnsignedByte, null);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        g.BindTexture(TextureTarget.Texture2D, 0);
        hazeMaskFbo = MakeColorFbo(g, hazeMaskTex, out GLEnum maskStatus);
        g.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (maskStatus != GLEnum.FramebufferComplete)
        {
            Log.Information("[GL3D] haze mask framebuffer incomplete ({Status}) — heat haze off this session", maskStatus);
            g.DeleteFramebuffer(hazeMaskFbo);
            g.DeleteTexture(hazeMaskTex);
            hazeMaskFbo = hazeMaskTex = 0;
            hazeUnsupported = true;
            return false;
        }

        hazeMaskW = w;
        hazeMaskH = h;
        hazeMaskIsHdr = hdr;
        return true;
    }

    /// <summary>Full-res haze output — the distorted scene the rest of the post chain reads. Format matches present.</summary>
    private bool EnsureHazeColor(GL g, int w, int h)
    {
        if (hazeUnsupported)
        {
            return false;
        }

        bool hdr = WantHdrTargets(g);
        if (hazeColorFbo != 0 && hazeColorW == w && hazeColorH == h && hazeColorIsHdr == hdr)
        {
            return true;
        }

        g.DeleteFramebuffer(hazeColorFbo);
        g.DeleteTexture(hazeColorTex);
        hazeColorTex = g.GenTexture();
        g.BindTexture(TextureTarget.Texture2D, hazeColorTex);
        g.TexImage2D(
            TextureTarget.Texture2D, 0, (int)(hdr ? InternalFormat.Rgba16f : InternalFormat.Rgba8),
            (uint)w, (uint)h, 0, PixelFormat.Rgba, hdr ? PixelType.HalfFloat : PixelType.UnsignedByte, null);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        g.BindTexture(TextureTarget.Texture2D, 0);
        hazeColorFbo = MakeColorFbo(g, hazeColorTex, out GLEnum colStatus);
        g.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (colStatus != GLEnum.FramebufferComplete)
        {
            Log.Information("[GL3D] haze colour framebuffer incomplete ({Status}) — heat haze off this session", colStatus);
            g.DeleteFramebuffer(hazeColorFbo);
            g.DeleteTexture(hazeColorTex);
            hazeColorFbo = hazeColorTex = 0;
            hazeUnsupported = true;
            return false;
        }

        hazeColorW = w;
        hazeColorH = h;
        hazeColorIsHdr = hdr;
        return true;
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
        bool hdr = WantHdrTargets(g);
        (int bw, int bh) = PostProcessBufferSizing.Downsample(fullWidth, fullHeight, 2);
        if (bloomBrightFbo != 0 && bloomWidth == bw && bloomHeight == bh && bloomIsHdr == hdr)
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

        bloomBrightTex = MakeColorTexture(g, bw, bh, hdr);
        bloomBrightFbo = MakeColorFbo(g, bloomBrightTex, out GLEnum statusBright);
        bloomTexA = MakeColorTexture(g, bw, bh, hdr);
        bloomFboA = MakeColorFbo(g, bloomTexA, out GLEnum statusA);
        bloomTexB = MakeColorTexture(g, bw, bh, hdr);
        bloomFboB = MakeColorFbo(g, bloomTexB, out GLEnum statusB);
        godrayTex = MakeColorTexture(g, bw, bh, hdr);
        godrayFbo = MakeColorFbo(g, godrayTex, out GLEnum statusGod);
        g.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        if (statusBright != GLEnum.FramebufferComplete || statusA != GLEnum.FramebufferComplete
            || statusB != GLEnum.FramebufferComplete || statusGod != GLEnum.FramebufferComplete)
        {
            g.DeleteFramebuffer(bloomBrightFbo);
            g.DeleteTexture(bloomBrightTex);
            g.DeleteFramebuffer(bloomFboA);
            g.DeleteTexture(bloomTexA);
            g.DeleteFramebuffer(bloomFboB);
            g.DeleteTexture(bloomTexB);
            g.DeleteFramebuffer(godrayFbo);
            g.DeleteTexture(godrayTex);
            bloomBrightFbo = bloomBrightTex = bloomFboA = bloomTexA = bloomFboB = bloomTexB = godrayFbo = godrayTex = 0;
            if (hdr)
            {
                // Float bloom mips failed → this frame runs the tonemapped pass-through (no bloom), and the
                // whole chain reallocates as Rgba8 next frame. Bloom itself stays available.
                Log.Information("[GL3D] HDR bloom mips incomplete (bright={Br}, A={A}, B={B}, god={G}) — falling back to Rgba8 targets", statusBright, statusA, statusB, statusGod);
                hdrUnsupported = true;
                return false;
            }

            Log.Information("[GL3D] post-effect framebuffer incomplete (bright={Br}, A={A}, B={B}, god={G}) — bloom/god-rays off this session", statusBright, statusA, statusB, statusGod);
            bloomUnsupported = true;
            return false;
        }

        bloomWidth = bw;
        bloomHeight = bh;
        bloomIsHdr = hdr;
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
            if (presentIsHdr)
            {
                // No LDR hand-off exists, yet the scene texture is float — Skia's GL_RGBA8 wrap of it will
                // fail for this one frame. Latch LDR and force the scene chain to reallocate next frame.
                Log.Information("[GL3D] post buffers unavailable while present is HDR — reverting to Rgba8 targets");
                hdrUnsupported = true;
                presentWidth = 0;
                msaaWidth = 0;
            }

            return sourceTex;
        }

        g.Disable(EnableCap.DepthTest);
        g.DepthMask(false);
        g.Disable(EnableCap.Blend);
        g.BindVertexArray(skyVao); // reuse the sky pass's fullscreen triangle for every post pass

        // B3 heat haze FIRST: distort the resolved scene by the heat mask, then let the whole chain below
        // (bright pass, blur, composite) read the DISTORTED image — the bloom shimmers with the air.
        if (hazeMaskValidThisFrame && hazeProgram != 0 && !hazeUnsupported && EnsureHazeColor(g, width, height))
        {
            g.BindFramebuffer(FramebufferTarget.Framebuffer, hazeColorFbo);
            g.Viewport(0, 0, (uint)width, (uint)height);
            g.UseProgram(hazeProgram);
            g.ActiveTexture(TextureUnit.Texture0);
            g.BindTexture(TextureTarget.Texture2D, sourceTex);
            g.Uniform1(hazeSceneLoc, 0);
            g.ActiveTexture(TextureUnit.Texture1);
            g.BindTexture(TextureTarget.Texture2D, hazeMaskTex);
            g.Uniform1(hazeHeatLoc, 1);
            g.ActiveTexture(TextureUnit.Texture0);
            g.Uniform1(hazeTimeLoc, (float)(frameClock.ElapsedMilliseconds % 100_000) / 1000f);
            g.Uniform1(hazeStrengthLoc, HazeStrength);
            g.DrawArrays(PrimitiveType.Triangles, 0, 3);
            sourceTex = hazeColorTex;
            if (!hazeStageLogged)
            {
                hazeStageLogged = true;
                Log.Information("[GL3D] post-process: heat haze active {W}x{H} (mask {MW}x{MH})", width, height, hazeMaskW, hazeMaskH);
            }
        }

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
            g.Uniform1(bloomCompTonemapLoc, TonemapStrengthEff);
            g.Uniform1(bloomCompExposureLoc, TonemapExposure);
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
            g.Uniform1(postTonemapLoc, TonemapStrengthEff);
            g.Uniform1(postExposureLoc, TonemapExposure);
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
                (uint)ShadowMapSize, (uint)ShadowMapSize, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
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
        if (!TemporalPassCadence.ShouldRefresh(gpuFrameCount, ThrottleShadows, shadowValidLastFrame)
            && shadowMapsAllocated)
        {
            shadowsActiveThisFrame = true;
            return;
        }

        shadowsActiveThisFrame = false;
        shadowValidLastFrame = false;
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

        // Surface ownership in the shadow map too: bind the SAME coverage mask (unit 8) so base-skin
        // fragments over resident full-detail ground don't cast the shadows — the smooth base sits metres
        // ABOVE the z16 rock and otherwise owns the depth-to-sun everywhere ("cień generowany na bazie").
        EnsureBaseCoverageTexture(g);
        if (baseCoverTex != 0 && uploadedBaseCoverageMask is { } shadowBcm)
        {
            g.ActiveTexture(TextureUnit.Texture8);
            g.BindTexture(TextureTarget.Texture2D, baseCoverTex);
            g.ActiveTexture(TextureUnit.Texture0);
            g.Uniform1(shadowBaseCoverLoc, 8);
            g.Uniform2(shadowBaseCoverMinXYLoc, shadowBcm.WorldMinX, shadowBcm.WorldMinY);
            g.Uniform2(shadowBaseCoverSizeXYLoc, shadowBcm.WorldSizeX, shadowBcm.WorldSizeY);
            g.Uniform1(shadowBaseCoverOnLoc, 1f);
        }
        else
        {
            g.Uniform1(shadowBaseCoverOnLoc, 0f);
        }

        Span<float> lm = stackalloc float[16];
        float sliceNear = near;
        for (int c = 0; c < ShadowCascadeCount; c++)
        {
            float sliceFar = splits[c];
            Matrix4x4 lightVp = CascadeLightMatrix.Build(camera, aspectRatio, sliceNear, sliceFar, sun, depthPadding: 2000f);
            cascadeLightVp[c] = lightVp;
            cascadeSplitFar[c] = sliceFar;

            g.BindFramebuffer(FramebufferTarget.Framebuffer, shadowFbos[c]);
            g.Viewport(0, 0, (uint)ShadowMapSize, (uint)ShadowMapSize);
            g.Clear((uint)ClearBufferMask.DepthBufferBit);

            // Same row-major upload as uMvp (transpose=false): GL reads it column-major, so GLSL's
            // uLightVp * v matches Vector4.Transform(v, lightVp) that the matrix tests pin.
            lm[0] = lightVp.M11; lm[1] = lightVp.M12; lm[2] = lightVp.M13; lm[3] = lightVp.M14;
            lm[4] = lightVp.M21; lm[5] = lightVp.M22; lm[6] = lightVp.M23; lm[7] = lightVp.M24;
            lm[8] = lightVp.M31; lm[9] = lightVp.M32; lm[10] = lightVp.M33; lm[11] = lightVp.M34;
            lm[12] = lightVp.M41; lm[13] = lightVp.M42; lm[14] = lightVp.M43; lm[15] = lightVp.M44;
            g.UniformMatrix4(shadowLightVpLoc, 1, false, lm);

            float lastShadowIsBase = -1f;
            foreach (KeyValuePair<TerrainMesh3D, TileBuffers> entry in tileBuffers)
            {
                // Per-cascade caster cull (2026-07-05, FPS #3): every tile used to draw into every cascade
                // (~870 meshes × 3 ≈ 2600 draws; shadow was the most expensive pass at ~12 ms). The cascade's
                // light matrix is an ortho box around its view slice (+2000 m depth padding toward the sun),
                // so a tile whose AABB misses that box cannot contribute any depth the map will read —
                // cascade 0 covers ~900 m and needs a handful of tiles, not the whole scene.
                if (!FrustumCuller.IsAabbVisible(lightVp, entry.Key.WorldMin, entry.Key.WorldMax))
                {
                    continue;
                }

                float isBase = entry.Key.IsBaseSkin ? 1f : 0f;
                if (isBase != lastShadowIsBase)
                {
                    g.Uniform1(shadowIsBaseSkinLoc, isBase);
                    lastShadowIsBase = isBase;
                }

                g.BindVertexArray(entry.Value.Vao);
                g.DrawElements(PrimitiveType.Triangles, (uint)entry.Value.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }

            if (PhotogrammetricRockEnabled
                && photogrammetricRock.ShouldDrawShadowDetail(
                    sliceFar,
                    camera.FieldOfViewYRadians,
                    ShadowMapSize,
                    minimumReliefTexels: 1.25f))
            {
                photogrammetricRock.DrawShadow(g, lightVp);
                // The isolated layer owns a separate quantized-position shader. Restore the terrain depth
                // program before the next cascade uploads its matrix and draws regular terrain.
                g.UseProgram(shadowDepthProgram);
            }
            sliceNear = sliceFar;
        }

        g.BindVertexArray(0);
        g.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)prevFbo[0]);
        g.Viewport(0, 0, (uint)vpWidth, (uint)vpHeight);
        shadowsActiveThisFrame = true;
        shadowValidLastFrame = true;

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

        // The resolve blit requires the multisampled colour format to match the present texture exactly,
        // so the MSAA renderbuffer follows the same HDR want as the present target.
        bool hdr = WantHdrTargets(g);
        if (msaaFbo != 0 && msaaWidth == width && msaaHeight == height && msaaIsHdr == hdr)
        {
            return true;
        }

        // (Re)allocate for the new size. Deleting 0 is a no-op, so this also handles first-time creation.
        g.DeleteFramebuffer(msaaFbo);
        g.DeleteRenderbuffer(msaaColorRb);
        g.DeleteRenderbuffer(msaaDepthRb);

        msaaColorRb = g.GenRenderbuffer();
        g.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msaaColorRb);
        g.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)msaaSamples, hdr ? InternalFormat.Rgba16f : InternalFormat.Rgba8, (uint)width, (uint)height);

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
            g.DeleteFramebuffer(msaaFbo);
            g.DeleteRenderbuffer(msaaColorRb);
            g.DeleteRenderbuffer(msaaDepthRb);
            msaaFbo = 0;
            msaaColorRb = 0;
            msaaDepthRb = 0;
            if (hdr)
            {
                // Multisampled float RT unsupported → keep MSAA, drop HDR for the whole chain. The caller
                // (Render) re-ensures the present target so both sides of the resolve stay format-matched.
                Log.Information("[GL3D] HDR MSAA framebuffer incomplete ({Status}) — falling back to Rgba8 targets", status);
                hdrUnsupported = true;
                return EnsureMsaaTarget(g, width, height);
            }

            Log.Information("[GL3D] MSAA framebuffer incomplete ({Status}) — falling back to non-AA terrain", status);
            msaaUnsupported = true;
            return false;
        }

        msaaWidth = width;
        msaaHeight = height;
        msaaIsHdr = hdr;
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

            ResetStagingUpload(g, old); // a swap can retire a cell mid-strip-upload
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

            // Harvest a finished off-thread far-tier compute into the cell's persistent cache. A faulted
            // task is dropped (the scheduler will simply request the compute again next frame).
            if (t.FarCompute is { IsCompleted: true } farDone)
            {
                t.FarCompute = null;
                if (farDone.IsCompletedSuccessfully)
                {
                    (t.FarRgba, t.FarWidth, t.FarHeight) = farDone.Result;
                }
            }

            Vector3 nearest = Vector3.Clamp(cameraWorldPos, aabb.Min, aabb.Max);
            float distance = Vector3.Distance(cameraWorldPos, nearest);
            int desiredCap = OrthoDistanceTier.DesiredCapPx(t.UploadedCapPx, distance);
            switch (OrthoTierScheduler.Decide(
                desiredCap, t.UploadedCapPx, Math.Max(t.MasterWidth, t.MasterHeight),
                hasCachedFar: t.FarRgba is not null, farComputePending: t.FarCompute is not null))
            {
                case OrthoTierAction.SwapToMaster:
                    (t.Rgba, t.Width, t.Height) = (t.MasterRgba, t.MasterWidth, t.MasterHeight);
                    break;
                case OrthoTierAction.SwapToCachedFar:
                    (t.Rgba, t.Width, t.Height) = (t.FarRgba!, t.FarWidth, t.FarHeight);
                    break;
                case OrthoTierAction.StartFarCompute:
                    // Off the GL thread: the box-average of a ~180 MB master takes ~250 ms of pure CPU.
                    // The current texture keeps drawing until the swap happens on a later frame.
                    byte[] master = t.MasterRgba;
                    (int mw, int mh, int cap) = (t.MasterWidth, t.MasterHeight, desiredCap);
                    t.FarCompute = Task.Run(() => OrthoCellDownsampler.Downsample(master, mw, mh, cap));
                    continue;
                default:
                    continue;
            }

            t.UploadedCapPx = desiredCap;
            if (t.Texture != 0)
            {
                g.DeleteTexture(t.Texture);
                t.Texture = 0;
            }

            ResetStagingUpload(g, t); // a resolution change mid-upload restarts the strips at the new size

            tierChanged.Add(idx);
        }

        if (plan.ToUpload.Count == 0 && plan.ToEvict.Count == 0 && tierChanged.Count == 0)
        {
            DrainOrthoUploads(g); // no residency changes this frame — keep feeding the strip queue
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

            ResetStagingUpload(g, tile);
            orthoUploadQueue.Remove(idx);
        }

        if (plan.ToUpload.Count > 0 || tierChanged.Count > 0)
        {
            // Union, not concatenation: a cell freshly promoted from ToUpload (first-ever appearance,
            // UploadedCapPx starts 0) is ALSO caught by the tier check above, so it lands in both lists.
            var toUploadNow = new HashSet<int>(plan.ToUpload);
            toUploadNow.UnionWith(tierChanged);
            foreach (int idx in toUploadNow)
            {
                // Tier not decided yet (first far-tier compute still running off-thread): Rgba still points
                // at the full master — queueing it now would strip-upload ~180 MB that gets thrown away.
                // The cell is (re)queued via tierChanged the frame its swap lands.
                if (orthoTiles[idx].UploadedCapPx == 0)
                {
                    continue;
                }

                if (!orthoUploadQueue.Contains(idx))
                {
                    orthoUploadQueue.Add(idx);
                }
            }
        }

        DrainOrthoUploads(g);
    }

    // Clears a tile's half-finished strip upload (tier change / eviction / new set): the staging texture
    // belongs to the OLD resolution and must be rebuilt from row 0.
    private static void ResetStagingUpload(GL g, OrthoTile tile)
    {
        if (tile.StagingTexture != 0)
        {
            g.DeleteTexture(tile.StagingTexture);
            tile.StagingTexture = 0;
        }

        tile.UploadedRows = 0;
    }

    // Per-frame ortho upload budget: strips totalling at most this much time leave the CPU each frame, so a
    // fresh 8-cell set (~1.7 GB) streams over a couple of seconds of smooth frames instead of the measured
    // 6–14 s single-frame "Not Responding" stall. At least one strip always ships, so the queue never stalls.
    private const double OrthoUploadBudgetMsPerFrame = 6.0;
    private const int OrthoUploadBytesPerChunk = 24 * 1024 * 1024; // ~24 MB of rows per TexSubImage2D call

    private void DrainOrthoUploads(GL g)
    {
        if (orthoUploadQueue.Count == 0)
        {
            return;
        }

        // Driver limits queried only when there is actual work (queue almost always empty).
        Span<int> maxTexSize = stackalloc int[1] { 2048 };
        g.GetInteger(GLEnum.MaxTextureSize, maxTexSize);
        int maxSize = maxTexSize[0];
        const GLEnum maxAnisotropyPName = (GLEnum)0x84FF; // GL_MAX_TEXTURE_MAX_ANISOTROPY_EXT
        const GLEnum anisotropyPName = (GLEnum)0x84FE;    // GL_TEXTURE_MAX_ANISOTROPY_EXT
        Span<float> maxAniso = stackalloc float[1] { 1f };
        g.GetFloat(maxAnisotropyPName, maxAniso);
        float aniso = maxAniso[0] < 1f ? 1f : maxAniso[0];

        long start = frameClock.ElapsedMilliseconds;
        while (orthoUploadQueue.Count > 0)
        {
            int idx = orthoUploadQueue[0];
            if ((uint)idx >= (uint)orthoTiles.Count)
            {
                orthoUploadQueue.RemoveAt(0);
                continue;
            }

            OrthoTile tile = orthoTiles[idx];
            if (tile.Texture != 0)
            {
                orthoUploadQueue.RemoveAt(0); // already resident (e.g. re-queued during a tier flap)
                continue;
            }

            if (tile.Width > maxSize || tile.Height > maxSize)
            {
                Log.Information("[GL3D] ortho tile {W}x{H} exceeds GL_MAX_TEXTURE_SIZE {Max}; skipping",
                    tile.Width, tile.Height, maxSize);
                orthoUploadQueue.RemoveAt(0);
                continue;
            }

            if (tile.StagingTexture == 0)
            {
                // Allocate the full-size texture EMPTY (no bulk transfer) — the strips fill it below.
                tile.StagingTexture = g.GenTexture();
                tile.UploadedRows = 0;
                g.BindTexture(TextureTarget.Texture2D, tile.StagingTexture);
                g.TexImage2D(
                    TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
                    (uint)tile.Width, (uint)tile.Height, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, null);
            }
            else
            {
                g.BindTexture(TextureTarget.Texture2D, tile.StagingTexture);
            }

            int rowBytes = tile.Width * 4;
            int rowsPerChunk = Math.Max(1, OrthoUploadBytesPerChunk / Math.Max(1, rowBytes));
            while (tile.UploadedRows < tile.Height)
            {
                int rows = Math.Min(rowsPerChunk, tile.Height - tile.UploadedRows);
                fixed (byte* p = &tile.Rgba[(long)tile.UploadedRows * rowBytes])
                {
                    g.TexSubImage2D(
                        TextureTarget.Texture2D, 0, 0, tile.UploadedRows,
                        (uint)tile.Width, (uint)rows,
                        PixelFormat.Rgba, PixelType.UnsignedByte, p);
                }

                tile.UploadedRows += rows;
                if (frameClock.ElapsedMilliseconds - start >= OrthoUploadBudgetMsPerFrame)
                {
                    break;
                }
            }

            if (tile.UploadedRows < tile.Height)
            {
                break; // budget spent mid-cell — resume next frame
            }

            // Last strip landed: build the mip chain + sampling params, then promote the texture so the
            // draw path starts using it (a partially filled texture must never be sampled).
            g.GenerateMipmap(TextureTarget.Texture2D);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            g.TexParameter(TextureTarget.Texture2D, (TextureParameterName)anisotropyPName, aniso);
            tile.Texture = tile.StagingTexture;
            tile.StagingTexture = 0;
            orthoUploadQueue.RemoveAt(0);
            Log.Information(
                "[GL3D] ortho cell uploaded (strip-sliced): {W}x{H}px, mipmapped + anisotropy x{Aniso} (driver max x{MaxAniso})",
                tile.Width, tile.Height, aniso, maxAniso[0]);

            if (frameClock.ElapsedMilliseconds - start >= OrthoUploadBudgetMsPerFrame)
            {
                break;
            }
        }

        g.BindTexture(TextureTarget.Texture2D, 0);
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

        // DISTINCT CPU bytes (RAM step 0, 2026-07-06): Rgba usually REFERENCES MasterRgba (near tier) —
        // summing both would double-count. Count each distinct array once, so the log answers "how much
        // RAM do the ortho pixels actually hold" instead of overstating it.
        long orthoCpuBytes = 0;
        long orthoGpuBytes = 0;
        long masterBytes = 0;
        long farBytes = 0;
        foreach (OrthoTile tile in orthoTiles)
        {
            masterBytes += tile.MasterRgba.LongLength;
            farBytes += tile.FarRgba?.LongLength ?? 0;
            if (!ReferenceEquals(tile.Rgba, tile.MasterRgba) && !ReferenceEquals(tile.Rgba, tile.FarRgba))
            {
                orthoCpuBytes += tile.Rgba.LongLength; // a third, transient buffer (tier change mid-swap)
            }

            orthoGpuBytes += OrthoVramBudget.CellResidentBytes(tile.Width, tile.Height);
        }

        orthoCpuBytes += masterBytes + farBytes;

        long tileGeometryBytes = 0;
        for (int i = 0; i < tiles.Count; i++)
        {
            tileGeometryBytes += tiles[i].EstimatedGpuBytes;
        }

        const double Mb = 1024.0 * 1024.0;
        Log.Information(
            "[Mem] ortho {Cells} cells ~{OrthoMb:F0}MB (cpu {CpuMb:F0} [masters {MasterMb:F0} far {FarMb:F0}] + gpu {GpuMb:F0}) | tiles {Tiles} ~{TileMb:F0}MB | heap {HeapMb:F0}MB ws {WsMb:F0}MB",
            orthoTiles.Count,
            (orthoCpuBytes + orthoGpuBytes) / Mb,
            orthoCpuBytes / Mb,
            masterBytes / Mb,
            farBytes / Mb,
            orthoGpuBytes / Mb,
            tiles.Count,
            tileGeometryBytes / Mb,
            GC.GetTotalMemory(forceFullCollection: false) / Mb,
            Environment.WorkingSet / Mb);

        if (det25Grid is not null)
        {
            int resident = 0, staging = 0, reading = 0, empty = 0;
            foreach (DetailCellGpu c in det25Cells.Values)
            {
                if (c.Texture != 0 || c.LayerReady) { resident++; }
                else if (c.StagingTexture != 0 || c.Pending is not null || c.PendingBc1 is not null) { staging++; }
                else if (c.Compose is not null) { reading++; }
                else if (c.Empty) { empty++; }
            }

            Log.Information(
                "[Mem] det25 {Cells} cells ~{Mb:F0}MB (resident {Res} staging {Stg} reading {Read} empty {Emp}) | desired {Des} | queue {Q} inflight {Inf} | eye {Lat:F4},{Lon:F4} cell ({Ci},{Cj})",
                det25Cells.Count, det25ResidentBytes / Mb, resident, staging, reading, empty, det25LastDesired, det25UploadQueue.Count, det25ReadInFlight,
                det25EyeLat, det25EyeLon, det25FocusCi, det25FocusCj);

            if (det05StreamOn)
            {
                int r05 = 0, e05 = 0, c05 = 0;
                foreach (DetailCellGpu c in det05Cells.Values)
                {
                    if (c.LayerReady) { r05++; } // array path: residency = a fully uploaded layer
                    else if (c.Empty) { e05++; }
                    else if (c.Compose is not null || c.Pending is not null) { c05++; }
                }

                Log.Information("[Mem] det05 {Cells} cells ~{Mb:F0}MB (resident {Res} reading {Read} empty {Emp}) | desired {Des} | queue {Q}",
                    det05Cells.Count, det05ResidentBytes / Mb, r05, c05, e05, det05LastDesired, det05UploadQueue.Count);
            }
        }
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

    // In-flight off-thread lake-water build + the inputs it was started for (see BuildLakeWater).
    private Task<(List<LakeDraw> Draws, float[] Verts, int Count)>? lakeBuildTask;
    private object? lakeBuildTilesRef;
    private object? lakeBuildRasterRef;
    private MapaTur.Domain.Geography.MapBounds? lakeBuildFineBounds;

    private unsafe void BuildLakeWater(GL g, IReadOnlyList<TerrainMesh3D> tiles, MapaTur.Domain.Terrain.DemRaster? raster)
    {
        if (ReferenceEquals(tiles, lakeWaterTilesRef)
            && ReferenceEquals(raster, lakeWaterRasterRef)
            && Equals(LakeFineBounds, lakeWaterFineBounds))
        {
            return; // same terrain inputs — the uploaded VBO and lakeDraws are still valid
        }

        // OFF-THREAD build, same poll-and-swap pattern as the ribbons / ortho decode: ear-clipping all the
        // in-extent lake outlines measured ~160 ms on the swap frame — the last chunk of the first-swap
        // hitch. The previous water keeps drawing until the fresh result lands; a stale result (any input
        // changed mid-build) is dropped and the build re-kicked.
        bool taskMatches = lakeBuildTask is not null
            && ReferenceEquals(lakeBuildTilesRef, tiles)
            && ReferenceEquals(lakeBuildRasterRef, raster)
            && Equals(lakeBuildFineBounds, LakeFineBounds);
        if (taskMatches && lakeBuildTask!.IsCompleted)
        {
            if (lakeBuildTask.IsCompletedSuccessfully)
            {
                (List<LakeDraw> draws, float[] built, int count) = lakeBuildTask.Result;
                UploadLakeWater(g, draws, built, count);
                lakeWaterTilesRef = tiles;
                lakeWaterRasterRef = raster;
                lakeWaterFineBounds = LakeFineBounds;
            }

            lakeBuildTask = null; // success consumed the result; a failure re-kicks below next frame
        }
        else if (!taskMatches)
        {
            (IReadOnlyList<TerrainMesh3D> bTiles, MapaTur.Domain.Terrain.DemRaster? bRaster) = (tiles, raster);
            MapaTur.Domain.Geography.MapBounds? bFine = LakeFineBounds;
            lakeBuildTilesRef = tiles;
            lakeBuildRasterRef = raster;
            lakeBuildFineBounds = LakeFineBounds;
            lakeBuildTask = Task.Run(() => BuildLakeWaterCpu(bTiles, bRaster, bFine));
        }
    }

    // GL half of the lake-water swap: replaces the draw list and (re)uploads the vertex buffer.
    private unsafe void UploadLakeWater(GL g, List<LakeDraw> draws, float[] built, int count)
    {
        const int stride = 12;
        lakeDraws.Clear();
        lakeDraws.AddRange(draws);
        debugPolyVertexCount = count / stride;
        if (count == 0)
        {
            return;
        }

        if (debugPolyFloats is null || debugPolyFloats.Length < count)
        {
            debugPolyFloats = new float[count];
        }

        Array.Copy(built, debugPolyFloats, count);
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

        g.BufferData<float>(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(buf, 0, count), BufferUsageARB.DynamicDraw);
        g.BindVertexArray(0);
    }

    // CPU half (background thread): seat + ear-clip every in-extent lake into a vertex list + draw records.
    // Pure — every input is read-only (tile meshes, raster samples, static lake data), so it is safe off-thread.
    private static (List<LakeDraw> Draws, float[] Verts, int Count) BuildLakeWaterCpu(
        IReadOnlyList<TerrainMesh3D> tiles,
        MapaTur.Domain.Terrain.DemRaster? raster,
        MapaTur.Domain.Geography.MapBounds? fineBounds)
    {
        var draws = new List<LakeDraw>();
        if (tiles.Count == 0)
        {
            return (draws, Array.Empty<float>(), 0);
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
            if (raster is null || (fineBounds is { } fine && fine.Contains(centroidGeo)))
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

            draws.Add(new LakeDraw(startVertex, tris.Count, center, maxR));
        }

        return (draws, verts.ToArray(), verts.Count);
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

    /// <summary>
    /// Compiles every shader program (and probes the GPU timers) ahead of the first real frame. Call from
    /// a paint that happens BEFORE the terrain arrives (the startup placeholder), with the GL context
    /// current: the full compile+link of all programs measured ~1.0 s, previously paid on the first scene
    /// swap — exactly when the loading overlay lifts. Idempotent; near-free once everything is ready.
    /// </summary>
    public void WarmUp()
    {
        gl ??= PlatformGl.Get();
        EnsureGpuTimers(gl);
        EnsureProgram(gl);
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
        terrainFireCountLoc = g.GetUniformLocation(program, "uFireCount");
        terrainFirePosLoc = g.GetUniformLocation(program, "uFirePos[0]");
        terrainFireColorLoc = g.GetUniformLocation(program, "uFireColor[0]");
        terrainFireInvR2Loc = g.GetUniformLocation(program, "uFireInvR2[0]");
        terrainScorchCountLoc = g.GetUniformLocation(program, "uScorchCount");
        terrainScorchPosLoc = g.GetUniformLocation(program, "uScorchPos[0]");
        terrainScorchParamLoc = g.GetUniformLocation(program, "uScorchParam[0]");
        orthoSamplerLocation = g.GetUniformLocation(program, "uOrtho");
        useOrthoLocation = g.GetUniformLocation(program, "uUseOrtho");
        orthoGlobalFadeLocation = g.GetUniformLocation(program, "uOrthoGlobalFade");
        orthoTexelLocation = g.GetUniformLocation(program, "uOrthoTexel");
        slopeModeLocation = g.GetUniformLocation(program, "uSlopeMode");
        slopePaletteLocation = g.GetUniformLocation(program, "uSlopePalette");
        sharpenLocation = g.GetUniformLocation(program, "uSharpen");
        debugUvLocation = g.GetUniformLocation(program, "uDebugUv");
        debugTerrainViewLocation = g.GetUniformLocation(program, "uDebugTerrainView");
        orthoMinXyLocation = g.GetUniformLocation(program, "uOrthoMinXY");
        orthoMaxXyLocation = g.GetUniformLocation(program, "uOrthoMaxXY");
        orthoBlendLocation = g.GetUniformLocation(program, "uOrthoBlendMeters");
        det25SamplerLocation = g.GetUniformLocation(program, "uOrthoDet25");
        det05SamplerLocation = g.GetUniformLocation(program, "uOrthoDet05");
        useDet25Location = g.GetUniformLocation(program, "uUseDet25");
        useDet05Location = g.GetUniformLocation(program, "uUseDet05");
        det25MinXyLocation = g.GetUniformLocation(program, "uDet25MinXY");
        det25MaxXyLocation = g.GetUniformLocation(program, "uDet25MaxXY");
        det05MinXyLocation = g.GetUniformLocation(program, "uDet05MinXY");
        det05MaxXyLocation = g.GetUniformLocation(program, "uDet05MaxXY");
        det05ArrSamplerLocation = g.GetUniformLocation(program, "uOrthoDet05Arr");
        det05ArrBSamplerLocation = g.GetUniformLocation(program, "uOrthoDet05ArrB");
        det05ArrCSamplerLocation = g.GetUniformLocation(program, "uOrthoDet05ArrC");
        det05ArrALoc = g.GetUniformLocation(program, "uDet05ArrLayers");
        det05CellHashLoc = g.GetUniformLocation(program, "uDet05CellHash[0]");
        det05HashSeedLoc = g.GetUniformLocation(program, "uDet05HashSeed");
        det05GridOriginLoc = g.GetUniformLocation(program, "uDet05GridMinXmaxY");
        det05GridPitchLoc = g.GetUniformLocation(program, "uDet05GridPitch");
        det05CellSizeLoc = g.GetUniformLocation(program, "uDet05CellSize");
        useDet05ArrLocation = g.GetUniformLocation(program, "uUseDet05Arr");
        detailBlendLocation = g.GetUniformLocation(program, "uDetailBlendMeters");
        detailColorModeLocation = g.GetUniformLocation(program, "uOrthoDetailColorMode");
        toneHarmLoc = g.GetUniformLocation(program, "uToneHarm");
        toneDebugLoc = g.GetUniformLocation(program, "uToneDebug");
        det05ArrRawLocation = g.GetUniformLocation(program, "uOrthoDet05ArrRaw");
        det25ArrSamplerLoc = g.GetUniformLocation(program, "uOrthoDet25Arr");
        det25CellHashLoc = g.GetUniformLocation(program, "uDet25CellHash[0]");
        det25HashSeedLoc = g.GetUniformLocation(program, "uDet25HashSeed");
        det25GridOriginLoc = g.GetUniformLocation(program, "uDet25GridMinXmaxY");
        det25GridPitchLoc = g.GetUniformLocation(program, "uDet25GridPitch");
        det25CellSizeLoc = g.GetUniformLocation(program, "uDet25CellSize");
        useDet25ArrLoc = g.GetUniformLocation(program, "uUseDet25Arr");
        det1mSamplerLoc = g.GetUniformLocation(program, "uOrthoDet1m");
        det1mCovLoc = g.GetUniformLocation(program, "uOrthoDet1mCov");
        useDet1mLoc = g.GetUniformLocation(program, "uUseDet1m");
        det1mMinXyLoc = g.GetUniformLocation(program, "uDet1mMinXmaxY");
        det1mInvSizeLoc = g.GetUniformLocation(program, "uDet1mInvSize");
        det1mGridDimLoc = g.GetUniformLocation(program, "uDet1mGridDim");
        det1mSliceIdxLoc = g.GetUniformLocation(program, "uDet1mSliceIdx[0]");
        det1mDebugLoc = g.GetUniformLocation(program, "uDet1mDebug");
        detailDebugBoundsLocation = g.GetUniformLocation(program, "uOrthoDetailDebugBounds");
        det25EyeXyLocation = g.GetUniformLocation(program, "uDet25EyeXY");
        det25FadeInnerLocation = g.GetUniformLocation(program, "uDet25FadeInner");
        det25FadeOuterLocation = g.GetUniformLocation(program, "uDet25FadeOuter");
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
        terrainCloudShadowOffsetLocation = g.GetUniformLocation(program, "uCloudShadowOffset");
        terrainCloudTimeLocation = g.GetUniformLocation(program, "uCloudTime");
        terrainCloudCoverageLocation = g.GetUniformLocation(program, "uCloudCoverage");
        terrainCloudShadowLocation = g.GetUniformLocation(program, "uCloudShadow");
        terrainSnowStrengthLocation = g.GetUniformLocation(program, "uSnowStrength");
        terrainSnowLineZLocation = g.GetUniformLocation(program, "uSnowLineZ");
        terrainFirnLineZLocation = g.GetUniformLocation(program, "uFirnLineZ");
        terrainFirnBandZLocation = g.GetUniformLocation(program, "uFirnBandZ");
        terrainFirnDropZLocation = g.GetUniformLocation(program, "uFirnDropZ");
        terrainFirnSitesLocation = g.GetUniformLocation(program, "uFirnSites[0]");
        firnChannelOnLocation = g.GetUniformLocation(program, "uFirnChannelOn");
        terrainFirnSiteCountLocation = g.GetUniformLocation(program, "uFirnSiteCount");
        terrainFirnStrengthLocation = g.GetUniformLocation(program, "uFirnStrength");
        terrainContourSpacingZLocation = g.GetUniformLocation(program, "uContourSpacingZ");
        terrainContourColorLocation = g.GetUniformLocation(program, "uContourColor");
        terrainContourMajorSpacingZLocation = g.GetUniformLocation(program, "uContourMajorSpacingZ");
        terrainContourMajorColorLocation = g.GetUniformLocation(program, "uContourMajorColor");
        terrainContourStrengthLocation = g.GetUniformLocation(program, "uContourStrength");
        terrainContourWidthPxLocation = g.GetUniformLocation(program, "uContourWidthPx");
        trailMaskSamplerLocation = g.GetUniformLocation(program, "uTrailMask");
        baseCoverSamplerLocation = g.GetUniformLocation(program, "uBaseCover");
        baseCoverMinXYLocation = g.GetUniformLocation(program, "uBaseCoverMinXY");
        baseCoverSizeXYLocation = g.GetUniformLocation(program, "uBaseCoverSizeXY");
        baseCoverOnLocation = g.GetUniformLocation(program, "uBaseCoverOn");
        isBaseSkinLocation = g.GetUniformLocation(program, "uIsBaseSkin");
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
        shadowTexelLoc = g.GetUniformLocation(program, "uShadowTexel");
        aoStrengthLoc = g.GetUniformLocation(program, "uAoStrength");
        bakedShadowCompLoc = g.GetUniformLocation(program, "uBakedShadowComp");

        // ALWAYS pin every sampler of the terrain program to its home unit AT LINK TIME, before the first
        // draw. A sampler uniform left at its default (unit 0) collides with uOrtho (sampler2D) whenever
        // its TYPE differs (sampler2DArray/sampler2DShadow): two sampler types on one texture image unit
        // make the program invalid to USE, and ANGLE/Adreno reject EVERY draw (GL_INVALID_OPERATION) — the
        // whole terrain vanishes the moment a layer is off/not-yet-ready and its per-frame bind path is
        // skipped (same lesson as the CSM pin in Render). Per-frame paths may re-assert these values but
        // must never move a sampler onto a unit owned by a different sampler type.
        // Unit map (terrain program): 0=uOrtho 1=uReflectionTex 2/3/4=uShadowMap0..2 5=uTrailMask
        // 6=uWaterMask 8=uBaseCover 9=uOrthoDet25 (legacy mosaic) 10=uOrthoDet25Arr 11=uOrthoDet05
        // 12=uOrthoDet05Arr 13=uOrthoDet05ArrB 14=uOrthoDet1m 15=uOrthoDet1mCov 7=uOrthoDet05ArrC.
        // Unit 7 był JEDYNYM wolnym — wszystkie 16 jednostek fragmentu są teraz zajęte. Kolejna tekstura
        // wymaga zwolnienia unitu (kandydat: 9 = legacy mozaika det25, do kasacji w kroku 8).
        g.UseProgram(program);
        g.Uniform1(orthoSamplerLocation, 0);
        g.Uniform1(reflectionTexLocation, 1);
        g.Uniform1(shadowMap0Loc, 2);
        g.Uniform1(shadowMap1Loc, 3);
        g.Uniform1(shadowMap2Loc, 4);
        g.Uniform1(trailMaskSamplerLocation, 5);
        g.Uniform1(waterMaskSamplerLocation, 6);
        g.Uniform1(baseCoverSamplerLocation, 8);
        g.Uniform1(det25SamplerLocation, 9);
        g.Uniform1(det25ArrSamplerLoc, 10);
        g.Uniform1(det05SamplerLocation, 11);
        g.Uniform1(det05ArrSamplerLocation, 12);
        g.Uniform1(det05ArrBSamplerLocation, 13);
        g.Uniform1(det05ArrCSamplerLocation, 7);
        g.Uniform1(det1mSamplerLoc, 14);
        g.Uniform1(det1mCovLoc, 15);
        g.UseProgram(0);

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
        skyCloudSeedLocation = g.GetUniformLocation(skyProgram, "uCloudSeed");
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
        postTonemapLoc = g.GetUniformLocation(postProgram, "uTonemap");
        postExposureLoc = g.GetUniformLocation(postProgram, "uExposure");

        // Bloom programs (bright-pass, separable blur, composite) — all share the post vertex shader.
        bloomBrightProgram = BuildPostProgram(g, BloomBrightFragmentShaderSource, "Bloom bright-pass");
        bloomBrightTexLoc = g.GetUniformLocation(bloomBrightProgram, "uTex");
        bloomBrightThresholdLoc = g.GetUniformLocation(bloomBrightProgram, "uThreshold");
        bloomBlurProgram = BuildPostProgram(g, BloomBlurFragmentShaderSource, "Bloom blur");
        bloomBlurTexLoc = g.GetUniformLocation(bloomBlurProgram, "uTex");
        bloomBlurDirLoc = g.GetUniformLocation(bloomBlurProgram, "uDir");
        bloomCompositeProgram = BuildPostProgram(g, BloomCompositeFragmentShaderSource, "Bloom composite");
        bloomCompSceneLoc = g.GetUniformLocation(bloomCompositeProgram, "uScene");
        bloomCompTonemapLoc = g.GetUniformLocation(bloomCompositeProgram, "uTonemap");
        bloomCompExposureLoc = g.GetUniformLocation(bloomCompositeProgram, "uExposure");
        bloomCompBloomLoc = g.GetUniformLocation(bloomCompositeProgram, "uBloom");
        bloomCompIntensityLoc = g.GetUniformLocation(bloomCompositeProgram, "uIntensity");
        bloomCompGodrayLoc = g.GetUniformLocation(bloomCompositeProgram, "uGodray");
        bloomCompGodrayIntensityLoc = g.GetUniformLocation(bloomCompositeProgram, "uGodrayIntensity");
        godrayProgram = BuildPostProgram(g, GodrayFragmentShaderSource, "God rays");
        godrayTexLoc = g.GetUniformLocation(godrayProgram, "uTex");
        godraySunUvLoc = g.GetUniformLocation(godrayProgram, "uSunUv");
        hazeProgram = BuildPostProgram(g, HazeFragmentShaderSource, "Heat haze");
        hazeSceneLoc = g.GetUniformLocation(hazeProgram, "uScene");
        hazeHeatLoc = g.GetUniformLocation(hazeProgram, "uHeat");
        hazeTimeLoc = g.GetUniformLocation(hazeProgram, "uTime");
        hazeStrengthLoc = g.GetUniformLocation(hazeProgram, "uHazeStrength");

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
        shadowBaseCoverLoc = g.GetUniformLocation(shadowDepthProgram, "uBaseCover");
        shadowBaseCoverMinXYLoc = g.GetUniformLocation(shadowDepthProgram, "uBaseCoverMinXY");
        shadowBaseCoverSizeXYLoc = g.GetUniformLocation(shadowDepthProgram, "uBaseCoverSizeXY");
        shadowBaseCoverOnLoc = g.GetUniformLocation(shadowDepthProgram, "uBaseCoverOn");
        shadowIsBaseSkinLoc = g.GetUniformLocation(shadowDepthProgram, "uIsBaseSkin");

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
        lineSceneDepthLocation = g.GetUniformLocation(lineProgram, "uSceneDepth");
        lineSceneDepthOnLocation = g.GetUniformLocation(lineProgram, "uSceneDepthOn");
        lineDepthNearFarLocation = g.GetUniformLocation(lineProgram, "uDepthNearFar");

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

    // ── DRAGON (rigged skinned model, F7 flight) ─────────────────────────────────────────────────────────────
    private uint dragonProgram;
    private int dragonMvpLoc = -1, dragonModelLoc = -1, dragonNormalLoc = -1;
    private int dragonLightLoc = -1, dragonColorLoc = -1, dragonAmbientLoc = -1;
    private int dragonTexLoc = -1, dragonHasTexLoc = -1, dragonTintLoc = -1;
    private uint dragonVao, dragonPosVbo, dragonNrmVbo, dragonUvVbo, dragonEbo;
    // Base-colour texture decoded from the ACTIVE model's embedded PNG. Cached against the model reference so
    // switching dragon variants re-uploads once; a model without a texture falls back to the solid uColor.
    private uint dragonTexture;
    private MapaTur.Application.Terrain.SkinnedModel? dragonTextureModel;
    private float[] dragonPosScratch = Array.Empty<float>();
    private float[] dragonNrmScratch = Array.Empty<float>();
    private float[] dragonUvScratch = Array.Empty<float>();
    private readonly float[] dragonMat4 = new float[16];
    private readonly float[] dragonMat3 = new float[9];

    private MapaTur.Application.Terrain.SkinnedModel? dragonModel;
    private Matrix4x4 dragonWorld = Matrix4x4.Identity;
    private Matrix4x4 dragonNormalRot = Matrix4x4.Identity;
    private Vector3 dragonLightDir = Vector3.Normalize(new Vector3(0.4f, 0.4f, 1f));
    private bool dragonVisible;

    /// <summary>Sets the dragon drawn this frame: the already-posed CPU-skinned model, its model→world matrix, the
    /// rotation-only matrix for normals, and the world light direction. <paramref name="visible"/> false hides it.</summary>
    public void SetDragon(
        MapaTur.Application.Terrain.SkinnedModel? model, Matrix4x4 world, Matrix4x4 normalRotation, Vector3 lightDir, bool visible)
    {
        dragonModel = model;
        dragonWorld = world;
        dragonNormalRot = normalRotation;
        if (lightDir.LengthSquared() > 1e-6f)
        {
            dragonLightDir = Vector3.Normalize(lightDir);
        }

        dragonVisible = visible;
    }

    private MapaTur.Application.Terrain.SkinnedModel? humanoidModel;
    private Matrix4x4 humanoidWorld = Matrix4x4.Identity;
    private Matrix4x4 humanoidNormalRot = Matrix4x4.Identity;
    private Vector3 humanoidLightDir = Vector3.Normalize(new Vector3(0.4f, 0.4f, 1f));
    private bool humanoidVisible;

    /// <summary>Sets the 3rd-person walk-mode avatar drawn this frame: the already-posed CPU-skinned humanoid, its
    /// model→world matrix, the rotation-only matrix for normals, and the world light direction. Reuses the dragon
    /// shader program, VAO and upload path (walk and dragon flight are mutually exclusive). <paramref name="visible"/>
    /// false hides it.</summary>
    public void SetHumanoid(
        MapaTur.Application.Terrain.SkinnedModel? model, Matrix4x4 world, Matrix4x4 normalRotation, Vector3 lightDir, bool visible)
    {
        humanoidModel = model;
        humanoidWorld = world;
        humanoidNormalRot = normalRotation;
        if (lightDir.LengthSquared() > 1e-6f)
        {
            humanoidLightDir = Vector3.Normalize(lightDir);
        }

        humanoidVisible = visible;
    }

    private MapaTur.Application.Terrain.SkinnedModel? arrowModel;
    private IReadOnlyList<Matrix4x4>? arrowWorlds;
    private IReadOnlyList<Matrix4x4>? arrowNormals;
    private Vector3 arrowLightDir = Vector3.Normalize(new Vector3(0.4f, 0.4f, 1f));

    /// <summary>Sets the crossbow arrows drawn this frame: one static (already-posed) arrow model reused for every
    /// live bolt, with a per-arrow model→world matrix and rotation-only normal matrix. Null/empty hides them.</summary>
    public void SetArrows(
        MapaTur.Application.Terrain.SkinnedModel? model, IReadOnlyList<Matrix4x4>? worlds, IReadOnlyList<Matrix4x4>? normals, Vector3 lightDir)
    {
        arrowModel = model;
        arrowWorlds = worlds;
        arrowNormals = normals;
        if (lightDir.LengthSquared() > 1e-6f)
        {
            arrowLightDir = Vector3.Normalize(lightDir);
        }
    }

    private void EnsureDragonProgram(GL g)
    {
        if (dragonProgram != 0 && g.IsProgram(dragonProgram))
        {
            return;
        }

        const string vs =
            "#version 300 es\n" +
            "layout(location=0) in vec3 aPos;\n" +
            "layout(location=1) in vec3 aNormal;\n" +
            "layout(location=2) in vec2 aUv;\n" +
            "uniform mat4 uMvp;\n" +
            "uniform mat4 uModel;\n" +
            "uniform mat3 uNormal;\n" +
            "out vec3 vN;\n" +
            "out vec2 vUv;\n" +
            "out vec3 vWp;\n" + // world position (exaggerated Z) — the fire-light loop samples it
            "void main(){ vec4 wp = uModel * vec4(aPos, 1.0); vN = uNormal * aNormal; vUv = aUv; vWp = wp.xyz; gl_Position = uMvp * wp; }\n";
        // Textured path (uHasTex=1): albedo from the model's base-colour texture with a MASK-style alpha cutout
        // (the animated dragon's wing membranes are alpha-masked cutouts on double-sided quads). Untextured
        // models keep the solid uColor look. Fire-light loop (B2): the breath and impact blasts light the
        // dragon's own body — same 8-light set as the terrain, wrap-diffuse so the belly catches the glow.
        const string fs =
            "#version 300 es\n" +
            "precision highp float;\n" +
            "in vec3 vN;\n" +
            "in vec2 vUv;\n" +
            "in vec3 vWp;\n" +
            "uniform vec3 uLight;\n" +
            "uniform vec3 uColor;\n" +
            "uniform float uAmbient;\n" +
            "uniform sampler2D uTex;\n" +
            "uniform float uHasTex;\n" +
            "uniform vec3 uTint;\n" + // per-dragon colour multiply (white = unchanged; the AI flock varies it)
            "uniform float uFireCount;\n" +
            "uniform vec3 uFirePos[8];\n" +
            "uniform vec3 uFireColor[8];\n" +
            "uniform float uFireInvR2[8];\n" +
            "out vec4 frag;\n" +
            "void main(){ float d = max(0.0, dot(normalize(vN), normalize(uLight)));" +
            " float sh = uAmbient + (1.0 - uAmbient) * d;" +
            " vec4 tex = texture(uTex, vUv);" +
            " if (uHasTex > 0.5 && tex.a < 0.45) discard;" +
            " vec3 base = mix(uColor, tex.rgb, uHasTex);" +
            " vec3 nrm = normalize(vN);\n" +
            "  vec3 fireGlow = vec3(0.0);\n" +
            "  for (int fi = 0; fi < 8; fi++) {\n" +
            "    if (float(fi) >= uFireCount) { break; }\n" +
            "    vec3 dF = uFirePos[fi] - vWp;\n" +
            "    float attF = 1.0 / (1.0 + (dot(dF, dF) * uFireInvR2[fi]));\n" +
            "    attF *= attF;\n" +
            "    float wrapF = max((dot(nrm, normalize(dF)) + 0.25) / 1.25, 0.0);\n" +
            "    fireGlow += uFireColor[fi] * (attF * wrapF);\n" +
            "  }\n" +
            " frag = vec4(base * uTint * (vec3(sh) + fireGlow), 1.0); }\n";

        uint v = CompileShader(g, ShaderType.VertexShader, vs);
        uint f = CompileShader(g, ShaderType.FragmentShader, fs);
        dragonProgram = g.CreateProgram();
        g.AttachShader(dragonProgram, v);
        g.AttachShader(dragonProgram, f);
        g.LinkProgram(dragonProgram);
        g.GetProgram(dragonProgram, ProgramPropertyARB.LinkStatus, out int linked);
        g.DetachShader(dragonProgram, v);
        g.DetachShader(dragonProgram, f);
        g.DeleteShader(v);
        g.DeleteShader(f);
        if (linked == 0)
        {
            string log = g.GetProgramInfoLog(dragonProgram);
            g.DeleteProgram(dragonProgram);
            dragonProgram = 0;
            throw new InvalidOperationException("Dragon program link failed: " + log);
        }

        dragonMvpLoc = g.GetUniformLocation(dragonProgram, "uMvp");
        dragonModelLoc = g.GetUniformLocation(dragonProgram, "uModel");
        dragonNormalLoc = g.GetUniformLocation(dragonProgram, "uNormal");
        dragonLightLoc = g.GetUniformLocation(dragonProgram, "uLight");
        dragonColorLoc = g.GetUniformLocation(dragonProgram, "uColor");
        dragonAmbientLoc = g.GetUniformLocation(dragonProgram, "uAmbient");
        dragonTexLoc = g.GetUniformLocation(dragonProgram, "uTex");
        dragonHasTexLoc = g.GetUniformLocation(dragonProgram, "uHasTex");
        dragonTintLoc = g.GetUniformLocation(dragonProgram, "uTint");
        dragonFireCountLoc = g.GetUniformLocation(dragonProgram, "uFireCount");
        dragonFirePosLoc = g.GetUniformLocation(dragonProgram, "uFirePos[0]");
        dragonFireColorLoc = g.GetUniformLocation(dragonProgram, "uFireColor[0]");
        dragonFireInvR2Loc = g.GetUniformLocation(dragonProgram, "uFireInvR2[0]");

        dragonVao = g.GenVertexArray();
        dragonPosVbo = g.GenBuffer();
        dragonNrmVbo = g.GenBuffer();
        dragonUvVbo = g.GenBuffer();
        dragonEbo = g.GenBuffer();
        g.BindVertexArray(dragonVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, dragonPosVbo);
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, dragonNrmVbo);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, dragonUvVbo);
        g.EnableVertexAttribArray(2);
        g.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        g.BindVertexArray(0);
    }

    /// <summary>Decodes + uploads the active model's embedded base-colour PNG once (cached per model reference).
    /// Returns true when a texture is bound and ready on texture unit 9.</summary>
    private bool EnsureDragonTexture(GL g, MapaTur.Application.Terrain.SkinnedModel model)
    {
        if (ReferenceEquals(dragonTextureModel, model))
        {
            return dragonTexture != 0;
        }

        // Model changed (variant switch): drop the previous texture before deciding the new path.
        if (dragonTexture != 0)
        {
            g.DeleteTexture(dragonTexture);
            dragonTexture = 0;
        }

        dragonTextureModel = model;
        if (model.BaseColorImageBytes is not { Length: > 0 } bytes)
        {
            return false;
        }

        try
        {
            using var bitmap = SkiaSharp.SKBitmap.Decode(bytes);
            if (bitmap is null)
            {
                return false;
            }

            using var rgba = bitmap.Copy(SkiaSharp.SKColorType.Rgba8888);
            if (rgba is null)
            {
                return false;
            }

            dragonTexture = g.GenTexture();
            g.ActiveTexture(TextureUnit.Texture9);
            g.BindTexture(TextureTarget.Texture2D, dragonTexture);
            unsafe
            {
                g.TexImage2D(
                    TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
                    (uint)rgba.Width, (uint)rgba.Height, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, (void*)rgba.GetPixels());
            }

            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            g.GenerateMipmap(TextureTarget.Texture2D);
            g.ActiveTexture(TextureUnit.Texture0);
            Log.Information("[Dragon] base-colour texture uploaded ({W}x{H})", rgba.Width, rgba.Height);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Dragon] texture decode/upload failed — solid colour fallback");
            dragonTexture = 0;
            return false;
        }
    }

    // ── DRAGON FIRE (procedural fireball billboards, additive) ──────────────────────────────────────────────
    /// <summary>One fire billboard this frame: world position (exaggerated Z), radius (world m), intensity 0..1,
    /// a per-sprite seed, a <paramref name="Kind"/> (0=flame stream, 1=flash, 2=shock ring, 3=ember, 4=puff) that
    /// the fragment branches on, and world <paramref name="Velocity"/> (m/s) for velocity-stretch billboards.</summary>
    public readonly record struct FireballSprite(
        Vector3 WorldPos, float RadiusMeters, float Intensity, float Seed, float Kind, Vector3 Velocity);

    private uint fireProgram;
    private int fireMvpLoc = -1, fireRightLoc = -1, fireUpLoc = -1, fireTimeLoc = -1, fireGainLoc = -1;
    private int fireSceneDepthLoc = -1, fireSoftOnLoc = -1, fireDepthNearFarLoc = -1; // soft particles (B1)
    private int fireCamPosLoc = -1; // A2 raymarch: world-space ray origin

    // B3 heat haze: a half-res heat mask (the fire list re-drawn with a tiny "heat" program, depth-gated in
    // the shader) + a full-res refraction stage at the START of the post chain — bloom sees the distorted
    // image. All failure latches to hazeUnsupported; the feature just turns off.
    private uint fireHeatProgram;
    private int heatMvpLoc = -1, heatRightLoc = -1, heatUpLoc = -1;
    private int heatSceneDepthLoc = -1, heatSoftOnLoc = -1, heatDepthNearFarLoc = -1;
    private uint hazeMaskFbo, hazeMaskTex;
    private int hazeMaskW, hazeMaskH;
    private bool hazeMaskIsHdr;
    private uint hazeColorFbo, hazeColorTex;
    private int hazeColorW, hazeColorH;
    private bool hazeColorIsHdr;
    private uint hazeProgram;
    private int hazeSceneLoc = -1, hazeHeatLoc = -1, hazeTimeLoc = -1, hazeStrengthLoc = -1;
    private bool hazeUnsupported;
    private bool hazeMaskValidThisFrame;
    private bool hazeStageLogged;
    private uint sceneFboThisFrame;   // the FBO the scene is being drawn into (heat-mask pass restores it)
    private int fireListVertexCount;  // vertices uploaded by the last UploadAndDrawFireList (heat pass re-draws them)
    private const float HazeStrength = 0.0045f; // UV offset at full heat ≈ a few px — subtle shimmer, not glass
    private bool ghostDepthFrameValid; // ghostDepthTex holds THIS frame's scene depth (fire + line gate share one resolve)

    // B2 fire lights: ≤8 point lights (reduced CPU-side by the view from the fire sprites), flattened for
    // Uniform3(count, span). Three programs consume the same set: terrain (+ its reflection pass for free),
    // dragon bodies, and the fire program's smoke pass.
    private int fireLightCount;
    private readonly float[] fireLightPosFlat = new float[24];
    private readonly float[] fireLightColorFlat = new float[24];
    private readonly float[] fireLightInvR2Flat = new float[8];
    private int terrainFireCountLoc = -1, terrainFirePosLoc = -1, terrainFireColorLoc = -1, terrainFireInvR2Loc = -1;

    // B4 scorch splats: session-persistent charred marks where fireballs hit the ground (≤24, ring-evicted
    // by the view). Flattened for Uniform2(count, span); uploaded with the fire lights each frame.
    private int scorchCount;
    private readonly float[] scorchPosFlat = new float[48];
    private readonly float[] scorchParamFlat = new float[48];
    private int terrainScorchCountLoc = -1, terrainScorchPosLoc = -1, terrainScorchParamLoc = -1;

    /// <summary>Sets the persistent fire-scorch splats (world XY + radius²/strength pairs). Count 0 clears.</summary>
    public void SetScorchMarks(int count, ReadOnlySpan<Vector2> positions, ReadOnlySpan<Vector2> radius2Strength)
    {
        scorchCount = Math.Clamp(count, 0, 24);
        for (int i = 0; i < scorchCount; i++)
        {
            scorchPosFlat[i * 2] = positions[i].X;
            scorchPosFlat[(i * 2) + 1] = positions[i].Y;
            scorchParamFlat[i * 2] = radius2Strength[i].X;
            scorchParamFlat[(i * 2) + 1] = radius2Strength[i].Y;
        }
    }
    private int dragonFireCountLoc = -1, dragonFirePosLoc = -1, dragonFireColorLoc = -1, dragonFireInvR2Loc = -1;
    private int fireLightsCountLoc = -1, fireLightsPosLoc = -1, fireLightsColorLoc = -1, fireLightsInvR2Loc = -1;

    /// <summary>
    /// Sets this frame's dragon-fire point lights (B2). Positions in ABSOLUTE world with exaggerated Z (the
    /// fire sprites' frame); colour premultiplied by intensity/flicker; invR2 = 1/(3R)² per light. Count 0 hides.
    /// </summary>
    public void SetFireLights(int count, ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> colors, ReadOnlySpan<float> invR2)
    {
        fireLightCount = Math.Clamp(count, 0, 8);
        for (int i = 0; i < fireLightCount; i++)
        {
            fireLightPosFlat[i * 3] = positions[i].X;
            fireLightPosFlat[(i * 3) + 1] = positions[i].Y;
            fireLightPosFlat[(i * 3) + 2] = positions[i].Z;
            fireLightColorFlat[i * 3] = colors[i].X;
            fireLightColorFlat[(i * 3) + 1] = colors[i].Y;
            fireLightColorFlat[(i * 3) + 2] = colors[i].Z;
            fireLightInvR2Flat[i] = invR2[i];
        }
    }

    // Pushes the light set into whichever program is CURRENTLY bound (locations differ per program).
    private void UploadFireLights(GL g, int countLoc, int posLoc, int colorLoc, int invLoc)
    {
        if (countLoc < 0)
        {
            return;
        }

        g.Uniform1(countLoc, (float)fireLightCount);
        if (fireLightCount > 0)
        {
            if (posLoc >= 0)
            {
                g.Uniform3(posLoc, (uint)fireLightCount, fireLightPosFlat.AsSpan(0, fireLightCount * 3));
            }

            if (colorLoc >= 0)
            {
                g.Uniform3(colorLoc, (uint)fireLightCount, fireLightColorFlat.AsSpan(0, fireLightCount * 3));
            }

            if (invLoc >= 0)
            {
                g.Uniform1(invLoc, (uint)fireLightCount, fireLightInvR2Flat.AsSpan(0, fireLightCount));
            }
        }
    }

    // Fire brightness inside the ACES shoulder (A1). Scales the blackbody T⁴ radiance only — the scene
    // exposure is untouched, so terrain/sky read exactly as before. Tuning range ~1.0–3.0.
    private const float FireGain = 1.3f;
    private uint fireVao, fireVbo;
    private IReadOnlyList<FireballSprite>? fireballs;
    private IReadOnlyList<FireballSprite>? fireSmoke;
    private float[] fireScratch = Array.Empty<float>();

    /// <summary>Sets the ADDITIVE fire billboards drawn this frame (flame/flash/shock/ember/puff). Null hides.</summary>
    public void SetFireballs(IReadOnlyList<FireballSprite>? balls) => fireballs = balls;

    /// <summary>Sets the SMOKE billboards (kind 5) drawn in the second, straight-alpha pass after the fire.</summary>
    public void SetFireSmoke(IReadOnlyList<FireballSprite>? smoke) => fireSmoke = smoke;

    private void EnsureFireProgram(GL g)
    {
        if (fireProgram != 0 && g.IsProgram(fireProgram))
        {
            return;
        }

        // Camera-facing quads; the fragment paints a procedural fire ball: white-hot core → orange → red rim,
        // edge licked by animated noise (flames), additive blend so overlapping balls glow hotter.
        const string vs =
            "#version 300 es\n" +
            "layout(location=0) in vec3 aCenter;\n" +
            "layout(location=1) in vec2 aCorner;\n" +
            "layout(location=2) in float aRadius;\n" +
            "layout(location=3) in float aIntensity;\n" +
            "layout(location=4) in float aSeed;\n" +
            "layout(location=5) in float aKind;\n" +
            "layout(location=6) in vec3 aVel;\n" +
            "uniform mat4 uMvp;\n" +
            "uniform vec3 uRight;\n" +
            "uniform vec3 uUp;\n" +
            "out vec2 vUv;\n" +
            "out float vIntensity;\n" +
            "out float vSeed;\n" +
            "flat out float vKind;\n" +
            "out vec3 vWpos;\n" +          // fragment's world position on the billboard plane (ray reconstruction)
            "flat out vec3 vCenter;\n" +   // A2 raymarch: ball centre (world, exaggerated Z)
            "flat out float vRadius;\n" +
            "flat out vec3 vAxis;\n" +     // comet ellipsoid axis = WORLD velocity direction
            "flat out float vStretch;\n" + // elongation along vAxis (1 = sphere)
            "void main(){\n" +
            "  vec2 c = aCorner;\n" +
            // velocity-stretch flame(0) + ember(3) into a comet/tongue: elongate along screen velocity, thin across (~const area)
            "  if (aKind < 0.5 || (aKind > 2.5 && aKind < 3.5)){\n" +
            "    vec2 vp = vec2(dot(aVel,uRight), dot(aVel,uUp)); float vl = length(vp);\n" +
            "    if (vl > 1e-3){ vec2 sd = vp/vl; float st = 1.0 + min(vl*0.010, 1.6);\n" +
            "      vec2 al = sd*dot(c,sd); vec2 pe = c - al; c = al*st + pe*inversesqrt(st); } }\n" +
            // A2: the raymarched kinds (flame 0, puff 4) inflate the quad a touch so the perspective rim of
            // the 3D body never clips against the billboard edge (misses just discard).
            "  if (aKind < 0.5 || (aKind > 3.5 && aKind < 4.5)) { c *= 1.15; }\n" +
            "  vec3 wp = aCenter + ((uRight * c.x) + (uUp * c.y)) * aRadius;\n" +
            "  vUv = aCorner; vIntensity = aIntensity; vSeed = aSeed; vKind = aKind; vWpos = wp;\n" +
            "  vCenter = aCenter; vRadius = aRadius;\n" +
            "  float wvl = length(aVel);\n" +
            "  vAxis = wvl > 1e-3 ? aVel / wvl : vec3(0.0, 0.0, 1.0);\n" +
            "  vStretch = aKind < 0.5 ? (1.0 + min(wvl * 0.010, 1.6)) : 1.0;\n" + // same law as the quad stretch
            "  gl_Position = uMvp * vec4(wp, 1.0); }\n";
        // Domain-warped 2-octave value noise (sin-free hash → no GLES banding) advected UPWARD gives churning gas;
        // a teardrop silhouette with a noise-eroded edge kills the "clean circle" tell. PUFF/FLAME colour is
        // PHYSICAL (A1): heat → Planckian-locus blackbody chromaticity × T⁴ radiance (chroma and brightness
        // separated, so the white-hot zone is only the genuinely hottest core and the body grades yellow →
        // orange → deep red) — real > 1 energy that survives in the HDR scene buffer and feeds the bloom
        // honestly. Premultiplied output + additive One,One so overlapping balls sum LINEARLY (the old
        // SrcAlpha,One squared the falloff and crushed the core).
        const string fs =
            "#version 300 es\n" +
            "precision highp float;\n" +
            "in vec2 vUv;\n" +
            "in float vIntensity;\n" +
            "in float vSeed;\n" +
            "flat in float vKind;\n" +
            "in vec3 vWpos;\n" +
            "flat in vec3 vCenter;\n" +
            "flat in float vRadius;\n" +
            "flat in vec3 vAxis;\n" +
            "flat in float vStretch;\n" +
            "uniform vec3 uCamPos;\n" +    // A2: ray origin (world, exaggerated Z — the sprites' frame)
            "uniform float uTime;\n" +
            "uniform float uFireGain;\n" + // fire brightness inside the ACES shoulder (scene exposure untouched)
            "uniform float uFireCount;\n" +   // B2: the same ≤8 fire lights as the terrain — the SMOKE pass
            "uniform vec3 uFirePos[8];\n" +   // samples them so soot glows orange from inside the blaze
            "uniform vec3 uFireColor[8];\n" +
            "uniform float uFireInvR2[8];\n" +
            "uniform sampler2D uSceneDepth;\n" + // resolved scene depth (soft particles, B1) — same tex as the line gate
            "uniform float uSoftOn;\n" +         // 1 = uSceneDepth valid this frame; 0 = hard-edged fallback
            "uniform vec2 uDepthNearFar;\n" +    // ACTIVE projection near/far (metres) for linearization
            "out vec4 frag;\n" +
            "float h21(vec2 p){ vec3 p3=fract(vec3(p.xyx)*0.1031); p3+=dot(p3,p3.yzx+33.33); return fract((p3.x+p3.y)*p3.z); }\n" +
            "float vn(vec2 p){ vec2 i=floor(p),f=fract(p); vec2 u=f*f*(3.0-2.0*f);\n" +
            "  float a=h21(i),b=h21(i+vec2(1.0,0.0)),c=h21(i+vec2(0.0,1.0)),d=h21(i+vec2(1.0,1.0));\n" +
            "  return mix(mix(a,b,u.x),mix(c,d,u.x),u.y); }\n" +
            "float fbm(vec2 p){ return 0.65*vn(p) + 0.35*vn(p*2.03+7.1); }\n" +
            // 3D value noise + fbm + a cheap swirl field (A2/A3). The volume samples these in WORLD space
            // with NO per-ball offset — neighbouring balls read the same field and fuse into one column.
            "float h31(vec3 p){ p=fract(p*0.1031); p+=dot(p,p.yzx+33.33); return fract((p.x+p.y)*p.z); }\n" +
            "float vn3(vec3 p){ vec3 i=floor(p), f=fract(p); vec3 u=f*f*(3.0-2.0*f);\n" +
            "  return mix(mix(mix(h31(i),h31(i+vec3(1,0,0)),u.x), mix(h31(i+vec3(0,1,0)),h31(i+vec3(1,1,0)),u.x), u.y),\n" +
            "             mix(mix(h31(i+vec3(0,0,1)),h31(i+vec3(1,0,1)),u.x), mix(h31(i+vec3(0,1,1)),h31(i+vec3(1,1,1)),u.x), u.y), u.z); }\n" +
            "float fbm3(vec3 p){ return 0.6*vn3(p) + 0.4*vn3(p*2.07+7.1); }\n" +
            "vec3 swirl3(vec3 p){\n" + // pseudo-curl from finite differences of one scalar field — divergence-poor, cheap (6 taps)
            "  float e=0.35;\n" +
            "  float dx = vn3(p+vec3(e,0,0)) - vn3(p-vec3(e,0,0));\n" +
            "  float dy = vn3(p+vec3(0,e,0)) - vn3(p-vec3(0,e,0));\n" +
            "  float dz = vn3(p+vec3(0,0,e)) - vn3(p-vec3(0,0,e));\n" +
            "  return vec3(dy - dz, dz - dx, dx - dy);\n" +
            "}\n" +
            // Blackbody chromaticity: Kim et al. cubic Planckian-locus fit (CIE xy) → XYZ (Y=1) → linear sRGB.
            // Valid 1667–25000 K; clamped below (deeper reds keep the 1667 K hue, radiance keeps falling).
            "vec3 blackbodyLinear(float T){\n" +
            "  float t=clamp(T,1667.0,10000.0); float u=1000.0/t; float u2=u*u; float u3=u2*u;\n" +
            "  float x = t<=4000.0 ? -0.2661239*u3-0.2343589*u2+0.8776956*u+0.179910\n" +
            "                      : -3.0258469*u3+2.1070379*u2+0.2226347*u+0.240390;\n" +
            "  float x2=x*x; float x3=x2*x;\n" +
            "  float y = t<=2222.0 ? -1.1063814*x3-1.34811020*x2+2.18555832*x-0.20219683\n" +
            "          : t<=4000.0 ? -0.9549476*x3-1.37418593*x2+2.09137015*x-0.16748867\n" +
            "                      :  3.0817580*x3-5.87338670*x2+3.75112997*x-0.37001483;\n" +
            "  float iy=1.0/max(y,1e-4); float X=x*iy; float Z=(1.0-x-y)*iy;\n" +
            "  return max(vec3( 3.2404542*X-1.5371385-0.4985314*Z,\n" +
            "                  -0.9692660*X+1.8760108+0.0415560*Z,\n" +
            "                   0.0556434*X-0.2040259+1.0572252*Z), vec3(0.0));\n" +
            "}\n" +
            // heat 0..1 → emitted radiance. Chroma from the locus (luminance-normalised), brightness from the
            // Stefan–Boltzmann-ish T^3.5 law referenced to 2600 K (≈ radiance 1). heat² so only the densest
            // core reaches the 7000 K white-hot zone — the body stays in the yellow/orange/red band.
            "vec3 fireEmit(float heat){\n" +
            "  float T = mix(1300.0, 7000.0, heat*heat);\n" +
            "  vec3 bb = blackbodyLinear(T);\n" +
            "  vec3 chroma = bb / max(dot(bb, vec3(0.2126,0.7152,0.0722)), 1e-4);\n" +
            "  return chroma * (uFireGain * pow(T/2600.0, 3.5));\n" +
            "}\n" +
            // Soft particles (B1): metres between this fragment and the visible scene surface behind it.
            // Same exact linearization as the line shader's rock-thickness gate (D3D-style window depth).
            "float sceneGapMeters(){\n" +
            "  if (uSoftOn < 0.5) return 1e6;\n" +
            "  vec2 duv = gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0));\n" +
            "  float n = uDepthNearFar.x; float f = uDepthNearFar.y;\n" +
            "  float ndcS = texture(uSceneDepth, duv).r * 2.0 - 1.0;\n" +
            "  float ndcF = gl_FragCoord.z * 2.0 - 1.0;\n" +
            "  float linS = (f * n) / (f - ndcS * (f - n));\n" +
            "  float linF = (f * n) / (f - ndcF * (f - n));\n" +
            "  return linS - linF;\n" +
            "}\n" +
            "void main(){\n" +
            "  float r = length(vUv);\n" +
            // Soft-particle fade: premultiplied kinds scale the WHOLE output (colour+alpha) by how far the
            // fragment floats in front of the scene; the ranges are per kind (a spark dies on contact, smoke
            // dissolves over tens of metres) — kills the hard sprite edge against rock and dragon bellies.
            "  float gapM = sceneGapMeters();\n" +
            // FLASH (kind 1): a sub-frame white pop
            "  if (vKind > 0.5 && vKind < 1.5){ float a=(1.0-smoothstep(0.0,1.0,r))*vIntensity;\n" +
            "    frag=vec4(vec3(1.0,0.95,0.85)*a*2.0, a)*clamp(gapM*0.25,0.0,1.0); return; }\n" +
            // SHOCK (kind 2): a thin expanding ring
            "  if (vKind > 1.5 && vKind < 2.5){ float ring=smoothstep(0.72,0.86,r)*(1.0-smoothstep(0.90,1.0,r));\n" +
            "    float a=ring*vIntensity; frag=vec4(vec3(1.0,0.75,0.45)*a*1.5, a)*clamp(gapM/6.0,0.0,1.0); return; }\n" +
            // EMBER (kind 3): a tiny hot spark point
            "  if (vKind > 2.5 && vKind < 3.5){ float a=(1.0-smoothstep(0.0,0.5,r))*vIntensity;\n" +
            "    vec3 col=mix(vec3(1.0,0.9,0.6), vec3(1.0,0.35,0.08), smoothstep(0.0,0.5,r));\n" +
            "    frag=vec4(col*a*1.8, a)*clamp(gapM/1.5,0.0,1.0); return; }\n" +
            // SMOKE (kind 5) / STEAM (kind 6): straight-alpha, drawn in the SECOND (non-additive) pass; curled
            // disc. Smoke = soot; steam = white vapour (fire quenched on water). Luminance low so it never blooms.
            "  if (vKind > 4.5){ float ang=atan(vUv.y,vUv.x)+vSeed*6.283+sin(uTime*0.6+vSeed*3.0)*0.4;\n" +
            "    float rr=length(vUv)*(1.0+0.12*sin(ang*3.0));\n" +
            "    float a=(1.0-smoothstep(0.35,1.0,rr))*vIntensity;\n" +
            "    vec3 col; if (vKind > 5.5){ col=vec3(0.92,0.95,1.0); a*=0.5; }\n" +
            "    else { col=mix(vec3(0.35,0.18,0.08), vec3(0.09), 1.0-vIntensity); a*=0.55; }\n" +
            // Fire-lit soot (B2): smoke drifting through the blaze glows orange from the near side. Pure
            // attenuation (billboards have no normal), sampled at the billboard's world position.
            "    vec3 fg=vec3(0.0);\n" +
            "    for (int fi = 0; fi < 8; fi++) {\n" +
            "      if (float(fi) >= uFireCount) { break; }\n" +
            "      vec3 dF = uFirePos[fi] - vWpos;\n" +
            "      float attF = 1.0 / (1.0 + (dot(dF, dF) * uFireInvR2[fi]));\n" +
            "      fg += uFireColor[fi] * (attF * attF);\n" +
            "    }\n" +
            "    col = (col * (1.0 + (fg * 1.6))) + (fg * 0.06);\n" +
            "    frag=vec4(col, a*clamp(gapM*0.025,0.0,1.0)); return; }\n" + // straight alpha → fade ALPHA only (40 m)
                                                                             // ── A2+A3: FLAME (0) + PUFF (4) = per-billboard VOLUMETRIC raymarch ────────────────────────
                                                                             // The fragment reconstructs its world ray, intersects the ball's ellipsoid (axis = velocity —
                                                                             // comets are real 3D bodies) and integrates emission–absorption front-to-back. Density is a
                                                                             // WORLD-space fbm3 with a swirling (curl-ish) advection and NO per-ball offset, so neighbouring
                                                                             // balls sample the same medium and FUSE into one coherent turbulent column. Constant loop bound
                                                                             // + break (ANGLE rule); a per-pixel start jitter turns slice banding into fine noise.
            "  vec3 ro = uCamPos;\n" +
            "  vec3 rd = normalize(vWpos - uCamPos);\n" +
            "  vec3 ax = vAxis;\n" +
            "  float raA = vRadius * vStretch;\n" +           // semi-axis along the flight (comet length)
            "  float rcA = vRadius * inversesqrt(vStretch);\n" + // across — area-preserving, matches the quad
            "  vec3 b1 = normalize(abs(ax.z) < 0.9 ? cross(ax, vec3(0.0,0.0,1.0)) : cross(ax, vec3(1.0,0.0,0.0)));\n" +
            "  vec3 b2 = cross(ax, b1);\n" +
            "  vec3 rel = ro - vCenter;\n" +
            "  vec3 roE = vec3(dot(rel,ax)/raA, dot(rel,b1)/rcA, dot(rel,b2)/rcA);\n" +
            "  vec3 rdE = vec3(dot(rd,ax)/raA, dot(rd,b1)/rcA, dot(rd,b2)/rcA);\n" +
            "  float qa = dot(rdE,rdE);\n" +
            "  float qb = 2.0*dot(roE,rdE);\n" +
            "  float qc = dot(roE,roE) - 1.0;\n" +
            "  float disc = (qb*qb) - (4.0*qa*qc);\n" +
            "  if (disc <= 0.0) { discard; }\n" +
            "  float sq = sqrt(disc);\n" +
            "  float tEnter = max((-qb - sq) / (2.0*qa), 0.0);\n" +
            "  float tExit = (-qb + sq) / (2.0*qa);\n" +
            "  if (tExit <= tEnter) { discard; }\n" +
            "  const int STEPS = 20;\n" +
            "  float stepT = (tExit - tEnter) / float(STEPS);\n" +
            "  float tRay = tEnter + (stepT * h21(gl_FragCoord.xy + vSeed));\n" +
            "  vec3 acc = vec3(0.0);\n" +
            "  float tr = 1.0;\n" +
            "  float sig = 2.5 / vRadius;\n" +
            "  for (int i = 0; i < STEPS; i++) {\n" +
            "    vec3 wp = ro + (rd * tRay);\n" +
            "    vec3 relW = wp - vCenter;\n" +
            "    vec3 pE = vec3(dot(relW,ax)/raA, dot(relW,b1)/rcA, dot(relW,b2)/rcA);\n" +
            "    float rn = length(pE);\n" +
            "    float env = 1.0 - smoothstep(0.42, 1.0, rn);\n" + // fatter core → neighbouring balls overlap and fuse into one jet
            "    if (env > 0.01) {\n" +
            "      vec3 q3 = wp * 0.14;\n" +                  // ~7 m features — the SHARED field that fuses the jet
            "      q3 += swirl3(q3 * 0.55) * 1.2;\n" +        // A3: swirling, roughly divergence-free advection
            "      q3.z -= uTime * 1.4;\n" +                  // buoyancy — the field slides down = the gas boils UP
            "      float noi = fbm3(q3);\n" +
            "      float dens = env * clamp((noi * 1.5) - 0.35, 0.0, 1.0) * 1.8;\n" + // erosion tears the rim
            "      if (dens > 0.003) {\n" +
            "        float heat = clamp(dens * (1.45 - (0.55 * rn)), 0.0, 1.0);\n" +  // hot core → cool rim
            "        vec3 emitc = fireEmit(heat);\n" +
            "        float occ = fbm3(q3 + vec3(0.0, 0.0, 0.7));\n" +                 // 1-tap self-shadow from above
            "        emitc *= 0.60 + (0.40 * (1.0 - (0.55 * occ)));\n" +
            "        float aStep = 1.0 - exp(-dens * sig * stepT);\n" +
            "        acc += tr * emitc * aStep;\n" +
            "        tr *= 1.0 - aStep;\n" +
            "        if (tr < 0.02) { break; }\n" +
            "      }\n" +
            "    }\n" +
            "    tRay += stepT;\n" +
            "  }\n" +
            "  float soft = clamp(gapM / 8.0, 0.0, 1.0);\n" + // B1 soft fade rides on top of the volume
            "  float cover = (1.0 - tr) * vIntensity * soft;\n" +
            "  if (cover <= 0.002) { discard; }\n" +
            "  frag = vec4(acc * (vIntensity * soft), cover);\n" +
            "}\n";

        uint v = CompileShader(g, ShaderType.VertexShader, vs);
        uint f = CompileShader(g, ShaderType.FragmentShader, fs);
        fireProgram = g.CreateProgram();
        g.AttachShader(fireProgram, v);
        g.AttachShader(fireProgram, f);
        g.LinkProgram(fireProgram);
        g.GetProgram(fireProgram, ProgramPropertyARB.LinkStatus, out int linked);
        g.DetachShader(fireProgram, v);
        g.DetachShader(fireProgram, f);
        g.DeleteShader(v);
        g.DeleteShader(f);
        if (linked == 0)
        {
            string log = g.GetProgramInfoLog(fireProgram);
            g.DeleteProgram(fireProgram);
            fireProgram = 0;
            throw new InvalidOperationException("Fireball program link failed: " + log);
        }

        fireMvpLoc = g.GetUniformLocation(fireProgram, "uMvp");
        fireRightLoc = g.GetUniformLocation(fireProgram, "uRight");
        fireUpLoc = g.GetUniformLocation(fireProgram, "uUp");
        fireTimeLoc = g.GetUniformLocation(fireProgram, "uTime");
        fireGainLoc = g.GetUniformLocation(fireProgram, "uFireGain");
        fireSceneDepthLoc = g.GetUniformLocation(fireProgram, "uSceneDepth");
        fireSoftOnLoc = g.GetUniformLocation(fireProgram, "uSoftOn");
        fireDepthNearFarLoc = g.GetUniformLocation(fireProgram, "uDepthNearFar");
        fireLightsCountLoc = g.GetUniformLocation(fireProgram, "uFireCount");
        fireLightsPosLoc = g.GetUniformLocation(fireProgram, "uFirePos[0]");
        fireLightsColorLoc = g.GetUniformLocation(fireProgram, "uFireColor[0]");
        fireLightsInvR2Loc = g.GetUniformLocation(fireProgram, "uFireInvR2[0]");
        fireCamPosLoc = g.GetUniformLocation(fireProgram, "uCamPos");

        fireVao = g.GenVertexArray();
        fireVbo = g.GenBuffer();
        g.BindVertexArray(fireVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, fireVbo);
        const int stride = 12 * sizeof(float); // center3 + corner2 + radius + intensity + seed + kind + vel3
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        g.EnableVertexAttribArray(2);
        g.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
        g.EnableVertexAttribArray(3);
        g.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        g.EnableVertexAttribArray(4);
        g.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, stride, (void*)(7 * sizeof(float)));
        g.EnableVertexAttribArray(5);
        g.VertexAttribPointer(5, 1, VertexAttribPointerType.Float, false, stride, (void*)(8 * sizeof(float)));
        g.EnableVertexAttribArray(6);
        g.VertexAttribPointer(6, 3, VertexAttribPointerType.Float, false, stride, (void*)(9 * sizeof(float)));
        g.BindVertexArray(0);

        // B3 heat program: the SAME billboard vertex shader, a tiny fragment that writes scalar "heat" into
        // the half-res mask — upward-biased falloff (convection plume) and the same depth linearization as
        // the soft particles, so occluded fire heats nothing (that is the haze's depth gate).
        const string heatFs =
            "#version 300 es\n" +
            "precision highp float;\n" +
            "in vec2 vUv;\n" +
            "in float vIntensity;\n" +
            "flat in float vKind;\n" +
            "uniform sampler2D uSceneDepth;\n" +
            "uniform float uSoftOn;\n" +
            "uniform vec2 uDepthNearFar;\n" +
            "out vec4 frag;\n" +
            "void main(){\n" +
            "  float heat;\n" +
            "  if (vKind > 4.5) { discard; }\n" +           // smoke/steam never shimmer (and never land here)
            "  else if (vKind > 3.5) { heat = 1.0; }\n" +   // puff
            "  else if (vKind > 2.5) { heat = 0.25; }\n" +  // ember
            "  else if (vKind > 1.5) { discard; }\n" +      // shock ring
            "  else if (vKind > 0.5) { heat = 0.7; }\n" +   // flash
            "  else { heat = 1.0; }\n" +                    // flame
            "  vec2 uvb = vUv;\n" +
            "  if (uvb.y > 0.0) { uvb.y *= 0.55; }\n" +     // top lobe stretched → the heat column rises past the ball
            "  float m = 1.0 - smoothstep(0.15, 1.05, length(uvb));\n" +
            "  if (uSoftOn > 0.5) {\n" +
            "    vec2 duv = gl_FragCoord.xy / vec2(textureSize(uSceneDepth, 0));\n" +
            "    float n = uDepthNearFar.x; float f = uDepthNearFar.y;\n" +
            "    float linS = (f * n) / (f - ((texture(uSceneDepth, duv).r * 2.0 - 1.0) * (f - n)));\n" +
            "    float linF = (f * n) / (f - ((gl_FragCoord.z * 2.0 - 1.0) * (f - n)));\n" +
            "    m *= clamp((linS - linF) / 4.0, 0.0, 1.0);\n" +
            "  }\n" +
            "  frag = vec4(m * vIntensity * heat, 0.0, 0.0, 1.0);\n" +
            "}\n";
        uint hv = CompileShader(g, ShaderType.VertexShader, vs);
        uint hf = CompileShader(g, ShaderType.FragmentShader, heatFs);
        fireHeatProgram = g.CreateProgram();
        g.AttachShader(fireHeatProgram, hv);
        g.AttachShader(fireHeatProgram, hf);
        g.LinkProgram(fireHeatProgram);
        g.GetProgram(fireHeatProgram, ProgramPropertyARB.LinkStatus, out int heatLinked);
        g.DetachShader(fireHeatProgram, hv);
        g.DetachShader(fireHeatProgram, hf);
        g.DeleteShader(hv);
        g.DeleteShader(hf);
        if (heatLinked == 0)
        {
            // Haze is pure garnish — losing it must not take the fire down.
            Log.Warning("[GL3D] fire-heat program link failed — heat haze off this session: {Log}", g.GetProgramInfoLog(fireHeatProgram));
            g.DeleteProgram(fireHeatProgram);
            fireHeatProgram = 0;
            hazeUnsupported = true;
        }
        else
        {
            heatMvpLoc = g.GetUniformLocation(fireHeatProgram, "uMvp");
            heatRightLoc = g.GetUniformLocation(fireHeatProgram, "uRight");
            heatUpLoc = g.GetUniformLocation(fireHeatProgram, "uUp");
            heatSceneDepthLoc = g.GetUniformLocation(fireHeatProgram, "uSceneDepth");
            heatSoftOnLoc = g.GetUniformLocation(fireHeatProgram, "uSoftOn");
            heatDepthNearFarLoc = g.GetUniformLocation(fireHeatProgram, "uDepthNearFar");
        }
    }

    private void DrawFireballs(GL g, Matrix4x4 mvp, Camera3D camera)
    {
        bool hasFire = fireballs is { Count: > 0 };
        bool hasSmoke = fireSmoke is { Count: > 0 };
        if (!hasFire && !hasSmoke)
        {
            return;
        }

        EnsureFireProgram(g);

        // Camera basis for the billboards.
        Vector3 fwd = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitZ));
        if (!float.IsFinite(right.X))
        {
            right = Vector3.UnitX; // looking straight down — any horizontal right works
        }

        Vector3 up = Vector3.Cross(right, fwd);

        g.UseProgram(fireProgram);
        WriteMat4(dragonMat4, mvp);
        g.UniformMatrix4(fireMvpLoc, 1, false, dragonMat4);
        g.Uniform3(fireRightLoc, right.X, right.Y, right.Z);
        g.Uniform3(fireUpLoc, up.X, up.Y, up.Z);
        g.Uniform1(fireTimeLoc, (float)(frameClock.ElapsedMilliseconds % 100_000) / 1000f);
        g.Uniform1(fireGainLoc, FireGain);
        g.Uniform3(fireCamPosLoc, camera.Position.X, camera.Position.Y, camera.Position.Z); // A2: ray origin
        UploadFireLights(g, fireLightsCountLoc, fireLightsPosLoc, fireLightsColorLoc, fireLightsInvR2Loc); // B2: smoke glows from inside the blaze
        // Soft particles: this frame's resolved scene depth (unit 7 — the line pass re-binds it later anyway).
        bool softOk = ghostDepthFrameValid && ghostDepthTex != 0;
        g.Uniform1(fireSoftOnLoc, softOk ? 1f : 0f);
        if (softOk)
        {
            g.ActiveTexture(TextureUnit.Texture7);
            g.BindTexture(TextureTarget.Texture2D, ghostDepthTex);
            g.ActiveTexture(TextureUnit.Texture0);
            g.Uniform1(fireSceneDepthLoc, 7);
            g.Uniform2(fireDepthNearFarLoc, camera.NearPlane, camera.FarPlane);
        }

        // Translucent: depth-TESTED (rocks occlude) but no depth write.
        g.Enable(EnableCap.DepthTest);
        g.DepthMask(false);
        g.Enable(EnableCap.Blend);
        g.Disable(EnableCap.CullFace);
        g.BindVertexArray(fireVao);

        // Pass 1 — additive fire (premultiplied frag + One,One adds light linearly).
        if (hasFire)
        {
            g.BlendFunc(BlendingFactor.One, BlendingFactor.One);
            UploadAndDrawFireList(g, fireballs!);
            RenderHeatMask(g, right, up, camera, softOk); // B3: same VBO content → half-res heat for the haze
        }

        // Pass 2 — smoke, straight alpha OVER the fire (soot occludes, never blooms; drawn last = sits in front).
        if (hasSmoke)
        {
            g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            UploadAndDrawFireList(g, fireSmoke!);
        }

        g.BindVertexArray(0);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        g.DepthMask(true);
    }

    // B3: re-draws the fire vertices STILL SITTING in the fire VBO into the half-res heat mask with the tiny
    // heat program (in-shader depth gate = occluded fire heats nothing). Leaves the fire VAO + blend enabled
    // exactly as found; restores the scene FBO/viewport/program/depth-test before returning.
    private void RenderHeatMask(GL g, Vector3 right, Vector3 up, Camera3D camera, bool depthOk)
    {
        hazeMaskValidThisFrame = false;
        if (hazeUnsupported || fireHeatProgram == 0 || fireListVertexCount <= 0
            || presentWidth <= 0 || sceneFboThisFrame == 0)
        {
            return;
        }

        int hw = Math.Max(16, presentWidth / 2);
        int hh = Math.Max(16, presentHeight / 2);
        if (!EnsureHazeMask(g, hw, hh))
        {
            return;
        }

        g.BindFramebuffer(FramebufferTarget.Framebuffer, hazeMaskFbo);
        g.Viewport(0, 0, (uint)hw, (uint)hh);
        g.ClearColor(0f, 0f, 0f, 0f);
        g.Clear((uint)ClearBufferMask.ColorBufferBit);
        g.UseProgram(fireHeatProgram);
        g.UniformMatrix4(heatMvpLoc, 1, false, dragonMat4); // still holds this draw's MVP
        g.Uniform3(heatRightLoc, right.X, right.Y, right.Z);
        g.Uniform3(heatUpLoc, up.X, up.Y, up.Z);
        g.Uniform1(heatSoftOnLoc, depthOk ? 1f : 0f);
        if (depthOk)
        {
            g.Uniform1(heatSceneDepthLoc, 7); // ghostDepthTex is already bound on unit 7 by the fire pass
            g.Uniform2(heatDepthNearFarLoc, camera.NearPlane, camera.FarPlane);
        }

        g.Disable(EnableCap.DepthTest); // the mask FBO has no depth — the gate lives in the shader
        g.BlendFunc(BlendingFactor.One, BlendingFactor.One); // heat sums like the fire it mirrors
        g.DrawArrays(PrimitiveType.Triangles, 0, (uint)fireListVertexCount);
        hazeMaskValidThisFrame = true;

        // Hand the state back to the fire pass (smoke pass follows with its own blend func).
        g.BindFramebuffer(FramebufferTarget.Framebuffer, sceneFboThisFrame);
        g.Viewport(0, 0, (uint)presentWidth, (uint)presentHeight);
        g.UseProgram(fireProgram);
        g.Enable(EnableCap.DepthTest);
    }

    // Streams a sprite list into the fire VBO (12 floats/vertex) and draws it. Assumes fireProgram + its uniforms
    // are bound, fireVao is bound, and the caller has set the blend func.
    private void UploadAndDrawFireList(GL g, IReadOnlyList<FireballSprite> list)
    {
        int floats = list.Count * 6 * 12;
        if (fireScratch.Length < floats)
        {
            fireScratch = new float[floats];
        }

        Span<(float X, float Y)> corners = stackalloc (float, float)[6]
        {
            (-1f, -1f), (1f, -1f), (1f, 1f),
            (-1f, -1f), (1f, 1f), (-1f, 1f),
        };
        int w = 0;
        foreach (FireballSprite ball in list)
        {
            foreach ((float cx, float cy) in corners)
            {
                fireScratch[w++] = ball.WorldPos.X;
                fireScratch[w++] = ball.WorldPos.Y;
                fireScratch[w++] = ball.WorldPos.Z;
                fireScratch[w++] = cx;
                fireScratch[w++] = cy;
                fireScratch[w++] = ball.RadiusMeters;
                fireScratch[w++] = ball.Intensity;
                fireScratch[w++] = ball.Seed;
                fireScratch[w++] = ball.Kind;
                fireScratch[w++] = ball.Velocity.X;
                fireScratch[w++] = ball.Velocity.Y;
                fireScratch[w++] = ball.Velocity.Z;
            }
        }

        g.BindBuffer(BufferTargetARB.ArrayBuffer, fireVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(fireScratch, 0, floats), BufferUsageARB.DynamicDraw);
        fireListVertexCount = list.Count * 6; // the heat-mask pass re-draws exactly these vertices
        g.DrawArrays(PrimitiveType.Triangles, 0, (uint)(list.Count * 6));
    }

    // ── DEBUG MARKERS (diagnostic solid discs, always-on-top) ───────────────────────────────────────────────
    /// <summary>One diagnostic marker to draw this frame: world position (exaggerated Z), flat RGB colour, and
    /// radius in world metres. Drawn as a camera-facing opaque disc with depth-test OFF, so every marker is
    /// visible regardless of what occludes it — a positional probe (e.g. dragon-foot vs rendered rock).</summary>
    public readonly record struct DebugMarker(Vector3 WorldPos, Vector3 Color, float RadiusMeters);

    private uint markerProgram;
    private int markerMvpLoc = -1, markerRightLoc = -1, markerUpLoc = -1;
    private uint markerVao, markerVbo;
    private IReadOnlyList<DebugMarker>? debugMarkers;
    private float[] markerScratch = Array.Empty<float>();

    /// <summary>Sets the diagnostic markers drawn this frame (null/empty hides the pass).</summary>
    public void SetDebugMarkers(IReadOnlyList<DebugMarker>? markers) => debugMarkers = markers;

    private void EnsureMarkerProgram(GL g)
    {
        if (markerProgram != 0 && g.IsProgram(markerProgram))
        {
            return;
        }

        const string vs =
            "#version 300 es\n" +
            "layout(location=0) in vec3 aCenter;\n" +
            "layout(location=1) in vec2 aCorner;\n" +
            "layout(location=2) in float aRadius;\n" +
            "layout(location=3) in vec3 aColor;\n" +
            "uniform mat4 uMvp;\n" +
            "uniform vec3 uRight;\n" +
            "uniform vec3 uUp;\n" +
            "out vec2 vUv;\n" +
            "out vec3 vColor;\n" +
            "void main(){ vec3 wp = aCenter + ((uRight * aCorner.x) + (uUp * aCorner.y)) * aRadius;\n" +
            "  vUv = aCorner; vColor = aColor; gl_Position = uMvp * vec4(wp, 1.0);\n" +
            "  gl_Position.z -= 0.0015; }\n"; // small clip bias so depth-tested hold dots win the z-fight with the wall they sit on
        const string fs =
            "#version 300 es\n" +
            "precision highp float;\n" +
            "in vec2 vUv;\n" +
            "in vec3 vColor;\n" +
            "out vec4 frag;\n" +
            "void main(){\n" +
            "  float r = length(vUv);\n" +
            "  if (r > 1.0) discard;\n" +
            "  float ring = smoothstep(0.72, 0.82, r) * (1.0 - smoothstep(0.92, 1.0, r));\n" + // dark outline
            "  vec3 col = mix(vColor, vec3(0.04), ring);\n" +
            "  frag = vec4(col, 1.0);\n" +
            "}\n";

        uint v = CompileShader(g, ShaderType.VertexShader, vs);
        uint f = CompileShader(g, ShaderType.FragmentShader, fs);
        markerProgram = g.CreateProgram();
        g.AttachShader(markerProgram, v);
        g.AttachShader(markerProgram, f);
        g.LinkProgram(markerProgram);
        g.GetProgram(markerProgram, ProgramPropertyARB.LinkStatus, out int linked);
        g.DetachShader(markerProgram, v);
        g.DetachShader(markerProgram, f);
        g.DeleteShader(v);
        g.DeleteShader(f);
        if (linked == 0)
        {
            string log = g.GetProgramInfoLog(markerProgram);
            g.DeleteProgram(markerProgram);
            markerProgram = 0;
            throw new InvalidOperationException("Marker program link failed: " + log);
        }

        markerMvpLoc = g.GetUniformLocation(markerProgram, "uMvp");
        markerRightLoc = g.GetUniformLocation(markerProgram, "uRight");
        markerUpLoc = g.GetUniformLocation(markerProgram, "uUp");

        markerVao = g.GenVertexArray();
        markerVbo = g.GenBuffer();
        g.BindVertexArray(markerVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, markerVbo);
        const int stride = 9 * sizeof(float); // center3 + corner2 + radius + color3
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        g.EnableVertexAttribArray(2);
        g.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
        g.EnableVertexAttribArray(3);
        g.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        g.BindVertexArray(0);
    }

    /// <summary>Climb-session hold dots — drawn DEPTH-TESTED (terrain occludes them; they read as sitting
    /// ON the wall) and depth-write OFF so the climber, drawn AFTER, always renders over them.</summary>
    public void SetClimbHoldMarkers(IReadOnlyList<DebugMarker>? markers) => climbHoldMarkers = markers;

    private IReadOnlyList<DebugMarker>? climbHoldMarkers;

    // Always-on-top diagnostic markers (dragon-foot probes, calibration grid): no depth test.
    private void DrawDebugMarkers(GL g, Matrix4x4 mvp, Camera3D camera)
    {
        if (debugMarkers is { Count: > 0 } markers)
        {
            DrawMarkerList(g, mvp, camera, markers, depthTested: false);
        }
    }

    // Climb hold dots: depth-tested (occluded by rock + the climber body), so the climber sits over them
    // and routes read under them. Drawn between the route lines and the climber in the frame.
    private void DrawClimbHoldMarkers(GL g, Matrix4x4 mvp, Camera3D camera)
    {
        if (climbHoldMarkers is { Count: > 0 } markers)
        {
            DrawMarkerList(g, mvp, camera, markers, depthTested: true);
        }
    }

    private void DrawMarkerList(GL g, Matrix4x4 mvp, Camera3D camera, IReadOnlyList<DebugMarker> markers, bool depthTested)
    {
        EnsureMarkerProgram(g);

        Vector3 fwd = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitZ));
        if (!float.IsFinite(right.X))
        {
            right = Vector3.UnitX;
        }

        Vector3 up = Vector3.Cross(right, fwd);

        int floats = markers.Count * 6 * 9;
        if (markerScratch.Length < floats)
        {
            markerScratch = new float[floats];
        }

        Span<(float X, float Y)> corners = stackalloc (float, float)[6]
        {
            (-1f, -1f), (1f, -1f), (1f, 1f),
            (-1f, -1f), (1f, 1f), (-1f, 1f),
        };
        int w = 0;
        foreach (DebugMarker mk in markers)
        {
            foreach ((float cx, float cy) in corners)
            {
                markerScratch[w++] = mk.WorldPos.X;
                markerScratch[w++] = mk.WorldPos.Y;
                markerScratch[w++] = mk.WorldPos.Z;
                markerScratch[w++] = cx;
                markerScratch[w++] = cy;
                markerScratch[w++] = mk.RadiusMeters;
                markerScratch[w++] = mk.Color.X;
                markerScratch[w++] = mk.Color.Y;
                markerScratch[w++] = mk.Color.Z;
            }
        }

        g.UseProgram(markerProgram);
        WriteMat4(dragonMat4, mvp);
        g.UniformMatrix4(markerMvpLoc, 1, false, dragonMat4);
        g.Uniform3(markerRightLoc, right.X, right.Y, right.Z);
        g.Uniform3(markerUpLoc, up.X, up.Y, up.Z);

        // Depth-tested hold dots: rock occludes them, but depth-write OFF so the later climber draws over
        // them. Diagnostic markers: depth-test OFF (always on top).
        if (depthTested)
        {
            g.Enable(EnableCap.DepthTest);
        }
        else
        {
            g.Disable(EnableCap.DepthTest);
        }

        g.DepthMask(false);
        g.Disable(EnableCap.Blend);
        g.Disable(EnableCap.CullFace);
        g.BindVertexArray(markerVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, markerVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(markerScratch, 0, floats), BufferUsageARB.DynamicDraw);
        g.DrawArrays(PrimitiveType.Triangles, 0, (uint)(markers.Count * 6));
        g.BindVertexArray(0);
        g.Enable(EnableCap.DepthTest);
        g.DepthMask(true);
    }

    // ── CLIMB ROCK SKIN (sculpted wall around the climber, depth-tested + written) ─────────────────────────
    private uint climbImprintProgram;
    private int climbImprintMvpLoc = -1, climbImprintZScaleLoc = -1, climbImprintSunLoc = -1;
    private uint climbImprintVao, climbImprintVbo;
    private MapaTur.Application.Terrain.ClimbRockSkin? climbRockSkin;
    private float[]? climbImprintUploaded;
    private float climbImprintZScale = 1f;

    /// <summary>Sets the sculpted rock skin drawn around the active climb session (climb space; 9 floats
    /// per vertex: pos3 + faceNormal3 + tint3; null hides the pass). The buffer is immutable per surface —
    /// re-uploaded only when the array REFERENCE changes (session start / patch growth) — and vertical
    /// exaggeration is applied in the shader (uZScale), so a Pion slider move needs no rebuild.</summary>
    public void SetClimbRockSkin(MapaTur.Application.Terrain.ClimbRockSkin? skin, float zScale)
    {
        climbRockSkin = skin;
        climbImprintZScale = zScale;
    }

    private void EnsureClimbImprintProgram(GL g)
    {
        if (climbImprintProgram != 0 && g.IsProgram(climbImprintProgram))
        {
            return;
        }

        // Positions arrive in climb space (real metres); the shader applies the vertical exaggeration to
        // points (z * uZScale) and to normals with the inverse-transpose rule (z / uZScale) — same law as
        // ClimbSpaceTransform, so facet shading stays correct at any Pion setting.
        const string vs =
            "#version 300 es\n" +
            "layout(location=0) in vec3 aPos;\n" +
            "layout(location=1) in vec3 aNormal;\n" +
            "layout(location=2) in vec3 aColor;\n" +
            "uniform mat4 uMvp;\n" +
            "uniform float uZScale;\n" +
            "out vec3 vNormal;\n" +
            "out vec3 vColor;\n" +
            "void main(){\n" +
            "  vNormal = normalize(vec3(aNormal.xy, aNormal.z / uZScale));\n" +
            "  vColor = aColor;\n" +
            "  gl_Position = uMvp * vec4(aPos.x, aPos.y, aPos.z * uZScale, 1.0);\n" +
            "}\n";
        const string fs =
            "#version 300 es\n" +
            "precision highp float;\n" +
            "in vec3 vNormal;\n" +
            "in vec3 vColor;\n" +
            "uniform vec3 uSunDir;\n" +
            "out vec4 frag;\n" +
            "void main(){\n" +
            "  vec3 n = normalize(vNormal);\n" +
            "  float diff = max(dot(n, uSunDir), 0.0);\n" +
            "  float sky = 0.45 + 0.55 * clamp(n.z, 0.0, 1.0);\n" +          // hemispheric fill: up facets brighter
            "  float day = clamp((uSunDir.z * 3.0) + 0.25, 0.12, 1.0);\n" +  // dusk/night dims with the terrain
            "  vec3 col = (vColor * ((0.30 * sky) + (0.62 * diff)) * day) + (vColor * 0.08);\n" + // ambient floor: never black facets
            "  frag = vec4(col, 1.0);\n" +
            "}\n";

        climbImprintProgram = LinkGearProgram(g, vs, fs);
        climbImprintMvpLoc = g.GetUniformLocation(climbImprintProgram, "uMvp");
        climbImprintZScaleLoc = g.GetUniformLocation(climbImprintProgram, "uZScale");
        climbImprintSunLoc = g.GetUniformLocation(climbImprintProgram, "uSunDir");

        climbImprintVao = g.GenVertexArray();
        climbImprintVbo = g.GenBuffer();
        g.BindVertexArray(climbImprintVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, climbImprintVbo);
        const int stride = 9 * sizeof(float); // pos3 + normal3 + tint3
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        g.EnableVertexAttribArray(2);
        g.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        g.BindVertexArray(0);
    }

    private void DrawClimbRockSkin(GL g, Matrix4x4 mvp, Vector3 sunDirection)
    {
        if (climbRockSkin is not { VertexCount: > 0 } buffer)
        {
            return;
        }

        EnsureClimbImprintProgram(g);
        g.UseProgram(climbImprintProgram);
        WriteMat4(dragonMat4, mvp);
        g.UniformMatrix4(climbImprintMvpLoc, 1, false, dragonMat4);
        g.Uniform1(climbImprintZScaleLoc, MathF.Max(0.0001f, climbImprintZScale));
        Vector3 sun = sunDirection.LengthSquared() > 1e-6f
            ? Vector3.Normalize(sunDirection)
            : Vector3.Normalize(new Vector3(0.35f, 0.2f, 0.91f));
        g.Uniform3(climbImprintSunLoc, sun.X, sun.Y, sun.Z);

        // Real rock: opaque, depth-tested AND depth-written, so hold dots/gear/climber depth-resolve
        // against the imprints exactly like against the terrain. No culling — the soup's winding is
        // outward, but the seat is open toward the wall and grazing views must not see through it.
        g.Enable(EnableCap.DepthTest);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
        g.Disable(EnableCap.CullFace);
        g.BindVertexArray(climbImprintVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, climbImprintVbo);
        if (!ReferenceEquals(climbImprintUploaded, buffer.Interleaved))
        {
            g.BufferData<float>(
                BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<float>(buffer.Interleaved, 0, buffer.VertexCount * 9),
                BufferUsageARB.StaticDraw);
            climbImprintUploaded = buffer.Interleaved;
        }

        g.DrawArrays(PrimitiveType.Triangles, 0, (uint)buffer.VertexCount);
        g.BindVertexArray(0);
    }

    // ── CLIMB GEAR (auto-belay rope + quickdraws, depth-tested world-width geometry) ───────────────────────
    /// <summary>One rope/sling polyline drawn as a camera-facing ribbon. With <paramref name="ScreenSpace"/>
    /// false the width is constant WORLD metres (<paramref name="HalfWidthMeters"/>) with fake-cylinder
    /// shading — a physical rope. With ScreenSpace true the width is a constant SCREEN size: HalfWidthMeters
    /// is then read as half-width in PIXELS, so the line stays a thin thread at any zoom (climbing-route
    /// topo overlay). RopeTwist false = a smooth glowing line instead of the kern-strand pattern.</summary>
    public readonly record struct GearRibbon(
        IReadOnlyList<Vector3> Points, Vector3 Color, float HalfWidthMeters, bool RopeTwist = true, bool ScreenSpace = false);

    /// <summary>One carabiner (ring) or bolt hanger (solid disc, InnerFraction 0) as a camera-facing billboard:
    /// vertical half-axis RadiusMeters, horizontal squeezed by AspectX — the oval of a real biner.</summary>
    public readonly record struct GearRing(Vector3 Center, Vector3 Color, float RadiusMeters, float InnerFraction, float AspectX);

    private uint gearRibbonProgram, gearRingProgram;
    private int gearRibbonMvpLoc = -1, gearRingMvpLoc = -1, gearRingRightLoc = -1, gearRingUpLoc = -1;
    private uint gearRibbonVao, gearRibbonVbo, gearRingVao, gearRingVbo;
    private IReadOnlyList<GearRibbon>? gearRibbons;
    private IReadOnlyList<GearRing>? gearRings;
    private float[] gearScratch = Array.Empty<float>();

    /// <summary>Sets this frame's climb-protection geometry (null/empty hides the pass).</summary>
    public void SetClimbGear(IReadOnlyList<GearRibbon>? ribbons, IReadOnlyList<GearRing>? rings)
    {
        gearRibbons = ribbons;
        gearRings = rings;
    }

    private void EnsureGearPrograms(GL g)
    {
        if (gearRibbonProgram != 0 && g.IsProgram(gearRibbonProgram))
        {
            return;
        }

        // Both shaders subtract a small CLIP-space constant from z (same trick as the trail lines): the bias is
        // C/w in NDC — strong enough up close to win the depth tie against the wall the gear hangs on, and
        // vanishing with distance so the rope never punches through genuinely intervening ridges.
        const string ribbonVs =
            "#version 300 es\n" +
            "layout(location=0) in vec3 aPos;\n" +
            "layout(location=1) in vec3 aOffset;\n" +
            "layout(location=2) in float aU;\n" +
            "layout(location=3) in float aAlong;\n" +
            "layout(location=4) in vec3 aColor;\n" +
            "layout(location=5) in float aSmooth;\n" + // 0 = rope kern twist, 1 = smooth glow line (topo)
            "uniform mat4 uMvp;\n" +
            "out float vU;\n" +
            "out float vAlong;\n" +
            "out vec3 vColor;\n" +
            "out float vSmooth;\n" +
            "void main(){ vU = aU; vAlong = aAlong; vColor = aColor; vSmooth = aSmooth;\n" +
            "  gl_Position = uMvp * vec4(aPos + aOffset, 1.0);\n" +
            "  gl_Position.z -= 0.03; }\n";
        const string ribbonFs =
            "#version 300 es\n" +
            "precision highp float;\n" +
            "in float vU;\n" +
            "in float vAlong;\n" +
            "in vec3 vColor;\n" +
            "in float vSmooth;\n" +
            "out vec4 frag;\n" +
            "void main(){\n" +
            "  float cyl = sqrt(max(0.0, 1.0 - (vU * vU)));\n" +               // fake round cross-section
            "  float twist = 0.88 + 0.12 * sin((vAlong * 42.0) + (vU * 2.4));\n" + // kern strands, ~15 cm pitch
            "  twist = mix(twist, 1.0, vSmooth);\n" +                          // topo lines: no kern pattern
            "  float body = mix(0.40 + 0.60 * cyl, 0.55 + 0.65 * cyl, vSmooth);\n" + // topo: brighter, glowing core
            "  frag = vec4(vColor * body * twist, 1.0);\n" +
            "}\n";

        const string ringVs =
            "#version 300 es\n" +
            "layout(location=0) in vec3 aCenter;\n" +
            "layout(location=1) in vec2 aCorner;\n" +
            "layout(location=2) in float aRadius;\n" +
            "layout(location=3) in float aInner;\n" +
            "layout(location=4) in float aAspect;\n" +
            "layout(location=5) in vec3 aColor;\n" +
            "uniform mat4 uMvp;\n" +
            "uniform vec3 uRight;\n" +
            "uniform vec3 uUp;\n" +
            "out vec2 vUv;\n" +
            "out float vInner;\n" +
            "out vec3 vColor;\n" +
            "void main(){ vUv = aCorner; vInner = aInner; vColor = aColor;\n" +
            "  vec3 wp = aCenter + (uRight * (aCorner.x * aRadius * aAspect)) + (uUp * (aCorner.y * aRadius));\n" +
            "  gl_Position = uMvp * vec4(wp, 1.0);\n" +
            "  gl_Position.z -= 0.03; }\n";
        const string ringFs =
            "#version 300 es\n" +
            "precision highp float;\n" +
            "in vec2 vUv;\n" +
            "in float vInner;\n" +
            "in vec3 vColor;\n" +
            "out vec4 frag;\n" +
            "void main(){\n" +
            "  float r = length(vUv);\n" +
            "  if (r > 1.0 || r < vInner) discard;\n" +                        // vInner 0 → solid disc (bolt hanger)
            "  float rim = smoothstep(0.90, 1.0, r);\n" +
            "  if (vInner > 0.0) rim = max(rim, 1.0 - smoothstep(vInner, vInner + 0.10, r));\n" +
            "  vec3 col = vColor * (0.78 + 0.22 * clamp(vUv.y + 0.5, 0.0, 1.0));\n" + // soft top light
            "  frag = vec4(mix(col, vec3(0.06), rim * 0.75), 1.0);\n" +        // dark rims keep it readable on rock
            "}\n";

        gearRibbonProgram = LinkGearProgram(g, ribbonVs, ribbonFs);
        gearRibbonMvpLoc = g.GetUniformLocation(gearRibbonProgram, "uMvp");
        gearRingProgram = LinkGearProgram(g, ringVs, ringFs);
        gearRingMvpLoc = g.GetUniformLocation(gearRingProgram, "uMvp");
        gearRingRightLoc = g.GetUniformLocation(gearRingProgram, "uRight");
        gearRingUpLoc = g.GetUniformLocation(gearRingProgram, "uUp");

        gearRibbonVao = g.GenVertexArray();
        gearRibbonVbo = g.GenBuffer();
        g.BindVertexArray(gearRibbonVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, gearRibbonVbo);
        const int ribbonStride = 12 * sizeof(float); // pos3 + offset3 + u + along + color3 + smooth
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, ribbonStride, (void*)0);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, ribbonStride, (void*)(3 * sizeof(float)));
        g.EnableVertexAttribArray(2);
        g.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, ribbonStride, (void*)(6 * sizeof(float)));
        g.EnableVertexAttribArray(3);
        g.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, ribbonStride, (void*)(7 * sizeof(float)));
        g.EnableVertexAttribArray(4);
        g.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, ribbonStride, (void*)(8 * sizeof(float)));
        g.EnableVertexAttribArray(5);
        g.VertexAttribPointer(5, 1, VertexAttribPointerType.Float, false, ribbonStride, (void*)(11 * sizeof(float)));

        gearRingVao = g.GenVertexArray();
        gearRingVbo = g.GenBuffer();
        g.BindVertexArray(gearRingVao);
        g.BindBuffer(BufferTargetARB.ArrayBuffer, gearRingVbo);
        const int ringStride = 12 * sizeof(float); // center3 + corner2 + radius + inner + aspect + color3 + pad
        g.EnableVertexAttribArray(0);
        g.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, ringStride, (void*)0);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, ringStride, (void*)(3 * sizeof(float)));
        g.EnableVertexAttribArray(2);
        g.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, ringStride, (void*)(5 * sizeof(float)));
        g.EnableVertexAttribArray(3);
        g.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, ringStride, (void*)(6 * sizeof(float)));
        g.EnableVertexAttribArray(4);
        g.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, ringStride, (void*)(7 * sizeof(float)));
        g.EnableVertexAttribArray(5);
        g.VertexAttribPointer(5, 3, VertexAttribPointerType.Float, false, ringStride, (void*)(8 * sizeof(float)));
        g.BindVertexArray(0);
    }

    private static uint LinkGearProgram(GL g, string vsSource, string fsSource)
    {
        uint v = CompileShader(g, ShaderType.VertexShader, vsSource);
        uint f = CompileShader(g, ShaderType.FragmentShader, fsSource);
        uint program = g.CreateProgram();
        g.AttachShader(program, v);
        g.AttachShader(program, f);
        g.LinkProgram(program);
        g.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
        g.DetachShader(program, v);
        g.DetachShader(program, f);
        g.DeleteShader(v);
        g.DeleteShader(f);
        if (linked == 0)
        {
            string log = g.GetProgramInfoLog(program);
            g.DeleteProgram(program);
            throw new InvalidOperationException("Climb-gear program link failed: " + log);
        }

        return program;
    }

    // Side vector for ribbon point i: perpendicular to both the local tangent and the view direction, so the
    // strip always faces the camera. Falls back to a Z-up side when the rope points straight at the eye.
    private static Vector3 GearSideAt(IReadOnlyList<Vector3> pts, int i, Vector3 viewDir, float halfWidth)
    {
        Vector3 tangent = pts[Math.Min(i + 1, pts.Count - 1)] - pts[Math.Max(i - 1, 0)];
        Vector3 side = Vector3.Cross(tangent, viewDir);
        if (side.LengthSquared() < 1e-10f)
        {
            side = Vector3.Cross(tangent, Vector3.UnitZ);
        }

        if (side.LengthSquared() < 1e-10f)
        {
            side = Vector3.UnitX;
        }

        return Vector3.Normalize(side) * halfWidth;
    }

    private void DrawClimbGear(GL g, Matrix4x4 mvp, Camera3D camera, int viewportHeight)
    {
        bool anyRibbon = gearRibbons is { Count: > 0 };
        bool anyRing = gearRings is { Count: > 0 };
        if (!anyRibbon && !anyRing)
        {
            return;
        }

        EnsureGearPrograms(g);

        // Metres-per-pixel at unit distance: multiply by a point's camera distance to get the world size
        // of one screen pixel there → constant screen-space width for ScreenSpace ribbons (thin at any zoom).
        float pxToWorldAtUnitDist = 2f * MathF.Tan(camera.FieldOfViewYRadians * 0.5f) / Math.Max(1, viewportHeight);
        Vector3 camPos = camera.Position;

        Vector3 fwd = Vector3.Normalize(camera.Target - camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitZ));
        if (!float.IsFinite(right.X))
        {
            right = Vector3.UnitX;
        }

        Vector3 up = Vector3.Cross(right, fwd);
        WriteMat4(dragonMat4, mvp);

        // Real geometry hanging on the wall: opaque, depth-tested and depth-written (occluded by ridges,
        // occludes nothing important itself), unlike the always-on-top debug markers.
        g.Enable(EnableCap.DepthTest);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
        g.Disable(EnableCap.CullFace);

        if (anyRibbon)
        {
            int vertexCount = 0;
            foreach (GearRibbon ribbon in gearRibbons!)
            {
                if (ribbon.Points is { Count: >= 2 } pts)
                {
                    vertexCount += (pts.Count - 1) * 6;
                }
            }

            if (vertexCount > 0)
            {
                int floats = vertexCount * 12;
                if (gearScratch.Length < floats)
                {
                    gearScratch = new float[floats];
                }

                int w = 0;
                void Emit(Vector3 p, Vector3 off, float u, float along, Vector3 c, float smooth)
                {
                    gearScratch[w++] = p.X; gearScratch[w++] = p.Y; gearScratch[w++] = p.Z;
                    gearScratch[w++] = off.X; gearScratch[w++] = off.Y; gearScratch[w++] = off.Z;
                    gearScratch[w++] = u;
                    gearScratch[w++] = along;
                    gearScratch[w++] = c.X; gearScratch[w++] = c.Y; gearScratch[w++] = c.Z;
                    gearScratch[w++] = smooth;
                }

                foreach (GearRibbon ribbon in gearRibbons!)
                {
                    if (ribbon.Points is not { Count: >= 2 } pts)
                    {
                        continue;
                    }

                    float smooth = ribbon.RopeTwist ? 0f : 1f;

                    // ScreenSpace: HalfWidthMeters is read as half-PIXELS; the world half-width at each
                    // vertex = pixels · cameraDistance · pxToWorld, so the strip holds a constant screen size.
                    float HalfWidthAt(Vector3 p) => ribbon.ScreenSpace
                        ? ribbon.HalfWidthMeters * Vector3.Distance(p, camPos) * pxToWorldAtUnitDist
                        : ribbon.HalfWidthMeters;

                    Vector3 prevPos = pts[0];
                    Vector3 prevSide = GearSideAt(pts, 0, fwd, 1f) * HalfWidthAt(prevPos);
                    float prevAlong = 0f;
                    for (int i = 1; i < pts.Count; i++)
                    {
                        Vector3 pos = pts[i];
                        Vector3 side = GearSideAt(pts, i, fwd, 1f) * HalfWidthAt(pos);
                        float along = prevAlong + Vector3.Distance(pos, prevPos);
                        Emit(prevPos, -prevSide, -1f, prevAlong, ribbon.Color, smooth);
                        Emit(prevPos, prevSide, 1f, prevAlong, ribbon.Color, smooth);
                        Emit(pos, side, 1f, along, ribbon.Color, smooth);
                        Emit(prevPos, -prevSide, -1f, prevAlong, ribbon.Color, smooth);
                        Emit(pos, side, 1f, along, ribbon.Color, smooth);
                        Emit(pos, -side, -1f, along, ribbon.Color, smooth);
                        prevPos = pos;
                        prevSide = side;
                        prevAlong = along;
                    }
                }

                g.UseProgram(gearRibbonProgram);
                g.UniformMatrix4(gearRibbonMvpLoc, 1, false, dragonMat4);
                g.BindVertexArray(gearRibbonVao);
                g.BindBuffer(BufferTargetARB.ArrayBuffer, gearRibbonVbo);
                g.BufferData<float>(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(gearScratch, 0, w), BufferUsageARB.DynamicDraw);
                g.DrawArrays(PrimitiveType.Triangles, 0, (uint)vertexCount);
            }
        }

        if (anyRing)
        {
            IReadOnlyList<GearRing> rings = gearRings!;
            int floats = rings.Count * 6 * 12;
            if (gearScratch.Length < floats)
            {
                gearScratch = new float[floats];
            }

            Span<(float X, float Y)> corners = stackalloc (float, float)[6]
            {
                (-1f, -1f), (1f, -1f), (1f, 1f),
                (-1f, -1f), (1f, 1f), (-1f, 1f),
            };
            int w = 0;
            foreach (GearRing ring in rings)
            {
                foreach ((float cx, float cy) in corners)
                {
                    gearScratch[w++] = ring.Center.X;
                    gearScratch[w++] = ring.Center.Y;
                    gearScratch[w++] = ring.Center.Z;
                    gearScratch[w++] = cx;
                    gearScratch[w++] = cy;
                    gearScratch[w++] = ring.RadiusMeters;
                    gearScratch[w++] = ring.InnerFraction;
                    gearScratch[w++] = ring.AspectX;
                    gearScratch[w++] = ring.Color.X;
                    gearScratch[w++] = ring.Color.Y;
                    gearScratch[w++] = ring.Color.Z;
                    gearScratch[w++] = 0f; // pad to the 12-float stride
                }
            }

            g.UseProgram(gearRingProgram);
            g.UniformMatrix4(gearRingMvpLoc, 1, false, dragonMat4);
            g.Uniform3(gearRingRightLoc, right.X, right.Y, right.Z);
            g.Uniform3(gearRingUpLoc, up.X, up.Y, up.Z);
            g.BindVertexArray(gearRingVao);
            g.BindBuffer(BufferTargetARB.ArrayBuffer, gearRingVbo);
            g.BufferData<float>(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(gearScratch, 0, w), BufferUsageARB.DynamicDraw);
            g.DrawArrays(PrimitiveType.Triangles, 0, (uint)(rings.Count * 6));
        }

        g.BindVertexArray(0);
    }

    private void DrawDragon(GL g, Matrix4x4 mvp)
    {
        if (!dragonVisible || dragonModel is not { } model)
        {
            return;
        }

        EnsureDragonProgram(g);
        g.UseProgram(dragonProgram);
        WriteMat4(dragonMat4, mvp);
        g.UniformMatrix4(dragonMvpLoc, 1, false, dragonMat4);
        WriteMat4(dragonMat4, dragonWorld);
        g.UniformMatrix4(dragonModelLoc, 1, false, dragonMat4);
        WriteMat3(dragonMat3, dragonNormalRot);
        g.UniformMatrix3(dragonNormalLoc, 1, false, dragonMat3);
        g.Uniform3(dragonLightLoc, dragonLightDir.X, dragonLightDir.Y, dragonLightDir.Z);
        g.Uniform3(dragonColorLoc, 0.34f, 0.09f, 0.09f); // dark blood-red hide (untextured fallback)
        g.Uniform1(dragonAmbientLoc, 0.38f);
        g.Uniform3(dragonTintLoc, 1f, 1f, 1f); // player dragon: no tint
        UploadFireLights(g, dragonFireCountLoc, dragonFirePosLoc, dragonFireColorLoc, dragonFireInvR2Loc); // B2: breath glow on the body

        // Texture path: bind the model's base colour (unit 9 — 0-8 are owned by terrain/CSM/ghost passes).
        bool hasTex = EnsureDragonTexture(g, model);
        if (hasTex)
        {
            g.ActiveTexture(TextureUnit.Texture9);
            g.BindTexture(TextureTarget.Texture2D, dragonTexture);
            g.ActiveTexture(TextureUnit.Texture0);
        }

        g.Uniform1(dragonTexLoc, 9);
        g.Uniform1(dragonHasTexLoc, hasTex ? 1f : 0f);

        g.Enable(EnableCap.DepthTest);
        g.DepthFunc(DepthFunction.Lequal);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
        g.Disable(EnableCap.CullFace); // model winding varies — draw both faces so it's never see-through
        g.BindVertexArray(dragonVao);
        UploadAndDrawDragonPrimitives(g, model);
        g.BindVertexArray(0);
    }

    // Draws the 3rd-person walk-mode avatar. Reuses the dragon shader program, VAO and upload path, but with NO
    // fire lights (uFireCount=0) and a neutral untextured fallback. The KayKit character is textured (single
    // base-colour atlas), so uHasTex drives the albedo. Texture cached per byte[] via the shared AI-flock cache.
    private void DrawHumanoid(GL g, Matrix4x4 mvp)
    {
        if (!humanoidVisible || humanoidModel is not { } model)
        {
            return;
        }

        EnsureDragonProgram(g);
        g.UseProgram(dragonProgram);
        WriteMat4(dragonMat4, mvp);
        g.UniformMatrix4(dragonMvpLoc, 1, false, dragonMat4);
        WriteMat4(dragonMat4, humanoidWorld);
        g.UniformMatrix4(dragonModelLoc, 1, false, dragonMat4);
        WriteMat3(dragonMat3, humanoidNormalRot);
        g.UniformMatrix3(dragonNormalLoc, 1, false, dragonMat3);
        g.Uniform3(dragonLightLoc, humanoidLightDir.X, humanoidLightDir.Y, humanoidLightDir.Z);
        g.Uniform3(dragonColorLoc, 0.72f, 0.6f, 0.5f); // neutral hide (untextured fallback)
        g.Uniform1(dragonAmbientLoc, 0.42f);
        g.Uniform3(dragonTintLoc, 1f, 1f, 1f);
        g.Uniform1(dragonFireCountLoc, 0f); // the walker is never lit by dragon fire

        g.Uniform1(dragonTexLoc, 9);

        g.Enable(EnableCap.DepthTest);
        g.DepthFunc(DepthFunction.Lequal);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
        g.Disable(EnableCap.CullFace); // model winding varies — draw both faces so it's never see-through
        g.BindVertexArray(dragonVao);

        // Per-primitive base colours: a multi-material rig (the realistic climber) carries a different atlas
        // per body part; single-atlas models (KayKit) fall back to the model-level texture on every primitive.
        foreach (MapaTur.Application.Terrain.SkinnedModel.Primitive p in model.Primitives)
        {
            uint tex = EnsureAiDragonTexture(g, p.BaseColorImageBytes ?? model.BaseColorImageBytes);
            bool hasTex = tex != 0;
            if (hasTex)
            {
                g.ActiveTexture(TextureUnit.Texture9);
                g.BindTexture(TextureTarget.Texture2D, tex);
                g.ActiveTexture(TextureUnit.Texture0);
            }

            g.Uniform1(dragonHasTexLoc, hasTex ? 1f : 0f);
            UploadAndDrawOnePrimitive(g, p);
        }

        g.BindVertexArray(0);
    }

    // Draws the crossbow bolts: reuses the dragon program + VAO + upload path, one draw per live arrow (only a
    // handful are ever in flight). No fire lights; the arrow carries its own base-colour atlas (unit 9).
    private void DrawArrows(GL g, Matrix4x4 mvp)
    {
        if (arrowModel is not { } model || arrowWorlds is not { Count: > 0 } worlds)
        {
            return;
        }

        EnsureDragonProgram(g);
        g.UseProgram(dragonProgram);
        WriteMat4(dragonMat4, mvp);
        g.UniformMatrix4(dragonMvpLoc, 1, false, dragonMat4);
        g.Uniform3(dragonLightLoc, arrowLightDir.X, arrowLightDir.Y, arrowLightDir.Z);
        g.Uniform3(dragonColorLoc, 0.35f, 0.24f, 0.14f); // wood-brown fallback (untextured)
        g.Uniform1(dragonAmbientLoc, 0.45f);
        g.Uniform3(dragonTintLoc, 1f, 1f, 1f);
        g.Uniform1(dragonFireCountLoc, 0f);

        uint tex = EnsureAiDragonTexture(g, model.BaseColorImageBytes);
        bool hasTex = tex != 0;
        if (hasTex)
        {
            g.ActiveTexture(TextureUnit.Texture9);
            g.BindTexture(TextureTarget.Texture2D, tex);
            g.ActiveTexture(TextureUnit.Texture0);
        }

        g.Uniform1(dragonTexLoc, 9);
        g.Uniform1(dragonHasTexLoc, hasTex ? 1f : 0f);

        g.Enable(EnableCap.DepthTest);
        g.DepthFunc(DepthFunction.Lequal);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
        g.Disable(EnableCap.CullFace);
        g.BindVertexArray(dragonVao);
        for (int i = 0; i < worlds.Count; i++)
        {
            WriteMat4(dragonMat4, worlds[i]);
            g.UniformMatrix4(dragonModelLoc, 1, false, dragonMat4);
            Matrix4x4 normal = arrowNormals is { } normals && i < normals.Count ? normals[i] : worlds[i];
            WriteMat3(dragonMat3, normal);
            g.UniformMatrix3(dragonNormalLoc, 1, false, dragonMat3);
            UploadAndDrawDragonPrimitives(g, model);
        }

        g.BindVertexArray(0);
    }

    // Streams one skinned model's CURRENT posed geometry (pos/nrm/uv + indices) into the shared dragon VBOs and
    // draws it. Assumes the dragon program is bound and its per-dragon uniforms (uModel/uNormal/…) are set, and
    // that dragonVao is bound. Shared by the ridden dragon and each AI-flock dragon.
    private void UploadAndDrawDragonPrimitives(GL g, MapaTur.Application.Terrain.SkinnedModel model)
    {
        foreach (MapaTur.Application.Terrain.SkinnedModel.Primitive p in model.Primitives)
        {
            UploadAndDrawOnePrimitive(g, p);
        }
    }

    private void UploadAndDrawOnePrimitive(GL g, MapaTur.Application.Terrain.SkinnedModel.Primitive p)
    {
        {
            int n = p.PosedPositions.Length;
            if (n == 0)
            {
                return;
            }

            if (dragonPosScratch.Length < n * 3)
            {
                dragonPosScratch = new float[n * 3];
                dragonNrmScratch = new float[n * 3];
            }

            if (dragonUvScratch.Length < n * 2)
            {
                dragonUvScratch = new float[n * 2];
            }

            for (int i = 0; i < n; i++)
            {
                Vector3 pos = p.PosedPositions[i];
                Vector3 nrm = p.PosedNormals[i];
                int b = i * 3;
                dragonPosScratch[b] = pos.X;
                dragonPosScratch[b + 1] = pos.Y;
                dragonPosScratch[b + 2] = pos.Z;
                dragonNrmScratch[b] = nrm.X;
                dragonNrmScratch[b + 1] = nrm.Y;
                dragonNrmScratch[b + 2] = nrm.Z;
                int t = i * 2;
                Vector2 uv = i < p.TexCoords.Length ? p.TexCoords[i] : Vector2.Zero;
                dragonUvScratch[t] = uv.X;
                dragonUvScratch[t + 1] = uv.Y;
            }

            g.BindBuffer(BufferTargetARB.ArrayBuffer, dragonPosVbo);
            g.BufferData<float>(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(dragonPosScratch, 0, n * 3), BufferUsageARB.DynamicDraw);
            g.BindBuffer(BufferTargetARB.ArrayBuffer, dragonNrmVbo);
            g.BufferData<float>(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(dragonNrmScratch, 0, n * 3), BufferUsageARB.DynamicDraw);
            g.BindBuffer(BufferTargetARB.ArrayBuffer, dragonUvVbo);
            g.BufferData<float>(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(dragonUvScratch, 0, n * 2), BufferUsageARB.DynamicDraw);
            g.BindBuffer(BufferTargetARB.ElementArrayBuffer, dragonEbo);
            g.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, new ReadOnlySpan<uint>(p.Indices), BufferUsageARB.DynamicDraw);
            g.DrawElements(PrimitiveType.Triangles, (uint)p.Indices.Length, DrawElementsType.UnsignedInt, (void*)0);
        }
    }

    // ── AI DRAGON FLOCK (autonomous dragons; per-member ALREADY-POSED model, per-member colour tint) ─────────
    /// <summary>One flock member to draw this frame: its ALREADY-POSED skinned model (the view poses it once per
    /// flock tick), its matrices, a colour <paramref name="Tint"/> (multiplied over the hide for variety), and
    /// the model's base-colour texture bytes (decoded+cached per distinct byte[]).</summary>
    public readonly record struct AiDragonInstance(
        MapaTur.Application.Terrain.SkinnedModel Model, Matrix4x4 World, Matrix4x4 NormalRot, Vector3 Tint, byte[]? TextureBytes);

    private IReadOnlyList<AiDragonInstance>? aiDragons;
    private readonly Dictionary<byte[], uint> aiDragonTextures = new(ReferenceEqualityComparer.Instance);

    /// <summary>Sets the AI flock drawn this frame (already posed by the view). Null/empty hides the pass.</summary>
    public void SetAiDragons(IReadOnlyList<AiDragonInstance>? dragons) => aiDragons = dragons;

    // Decodes + uploads a model's base-colour texture on first use, cached by the byte[] reference (so the two or
    // three flock species each decode once). Returns 0 when there are no bytes / the decode fails.
    private uint EnsureAiDragonTexture(GL g, byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
        {
            return 0;
        }

        if (aiDragonTextures.TryGetValue(bytes, out uint cached))
        {
            return cached;
        }

        uint tex = 0;
        try
        {
            using SkiaSharp.SKBitmap? bitmap = SkiaSharp.SKBitmap.Decode(bytes);
            using SkiaSharp.SKBitmap? rgba = bitmap?.Copy(SkiaSharp.SKColorType.Rgba8888);
            if (rgba is not null)
            {
                tex = g.GenTexture();
                g.ActiveTexture(TextureUnit.Texture9);
                g.BindTexture(TextureTarget.Texture2D, tex);
                g.TexImage2D(
                    TextureTarget.Texture2D, 0, (int)InternalFormat.Rgba8,
                    (uint)rgba.Width, (uint)rgba.Height, 0,
                    PixelFormat.Rgba, PixelType.UnsignedByte, (void*)rgba.GetPixels());
                g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
                g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                g.GenerateMipmap(TextureTarget.Texture2D);
                g.ActiveTexture(TextureUnit.Texture0);
                Log.Information("[AiDragon] base-colour texture uploaded ({W}x{H})", rgba.Width, rgba.Height);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[AiDragon] texture decode/upload failed — solid colour fallback");
            tex = 0;
        }

        aiDragonTextures[bytes] = tex; // cache even 0 so a bad texture doesn't retry every frame
        return tex;
    }

    private void DrawAiDragons(GL g, Matrix4x4 mvp)
    {
        if (aiDragons is not { Count: > 0 } dragons)
        {
            return;
        }

        EnsureDragonProgram(g);
        g.UseProgram(dragonProgram);
        WriteMat4(dragonMat4, mvp);
        g.UniformMatrix4(dragonMvpLoc, 1, false, dragonMat4);
        g.Uniform3(dragonLightLoc, dragonLightDir.X, dragonLightDir.Y, dragonLightDir.Z);
        g.Uniform3(dragonColorLoc, 0.30f, 0.10f, 0.12f); // untextured fallback hide
        g.Uniform1(dragonAmbientLoc, 0.38f);
        g.Uniform1(dragonTexLoc, 9);
        UploadFireLights(g, dragonFireCountLoc, dragonFirePosLoc, dragonFireColorLoc, dragonFireInvR2Loc); // B2: a hit blast lights its victim

        g.Enable(EnableCap.DepthTest);
        g.DepthFunc(DepthFunction.Lequal);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
        g.Disable(EnableCap.CullFace);
        g.BindVertexArray(dragonVao);
        foreach (AiDragonInstance dragon in dragons)
        {
            if (dragon.Model is not { } model)
            {
                continue;
            }

            uint tex = EnsureAiDragonTexture(g, dragon.TextureBytes);
            bool hasTex = tex != 0;
            if (hasTex)
            {
                g.ActiveTexture(TextureUnit.Texture9);
                g.BindTexture(TextureTarget.Texture2D, tex);
                g.ActiveTexture(TextureUnit.Texture0);
            }

            g.Uniform1(dragonHasTexLoc, hasTex ? 1f : 0f);
            g.Uniform3(dragonTintLoc, dragon.Tint.X, dragon.Tint.Y, dragon.Tint.Z);
            WriteMat4(dragonMat4, dragon.World);
            g.UniformMatrix4(dragonModelLoc, 1, false, dragonMat4);
            WriteMat3(dragonMat3, dragon.NormalRot);
            g.UniformMatrix3(dragonNormalLoc, 1, false, dragonMat3);
            UploadAndDrawDragonPrimitives(g, model);
        }

        g.BindVertexArray(0);
    }

    private static void WriteMat4(float[] m, Matrix4x4 x)
    {
        m[0] = x.M11; m[1] = x.M12; m[2] = x.M13; m[3] = x.M14;
        m[4] = x.M21; m[5] = x.M22; m[6] = x.M23; m[7] = x.M24;
        m[8] = x.M31; m[9] = x.M32; m[10] = x.M33; m[11] = x.M34;
        m[12] = x.M41; m[13] = x.M42; m[14] = x.M43; m[15] = x.M44;
    }

    private static void WriteMat3(float[] m, Matrix4x4 x)
    {
        m[0] = x.M11; m[1] = x.M12; m[2] = x.M13;
        m[3] = x.M21; m[4] = x.M22; m[5] = x.M23;
        m[6] = x.M31; m[7] = x.M32; m[8] = x.M33;
    }

    private void UploadTile(GL g, TerrainMesh3D tile)
    {
        int vertexCount = tile.Vertices.Length;

        // H6 (2026-07-23): positions/normals upload ZERO-COPY straight from the mesh's Vector3[] — the layout
        // IS tightly packed x,y,z floats (System.Numerics.Vector3 = 3 sequential floats), so the old per-tile
        // repack was two fresh LOH arrays and two full copies per tile for nothing. Colours still need the
        // ARGB→RGBA swizzle but into a POOLED buffer. The per-tile BufferData wall time is measured below —
        // uploadTileDataMs feeds the bench so the "gap frames all have pendingUploads>0" hypothesis stays
        // attributed to numbers, not vibes.
        double tUp0 = frameClock.ElapsedMilliseconds;
        System.ReadOnlySpan<float> positions = System.Runtime.InteropServices.MemoryMarshal.Cast<Vector3, float>(
            tile.Vertices.AsSpan());
        System.ReadOnlySpan<float> normalsSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<Vector3, float>(
            tile.Normals.AsSpan());

        byte[] colorsRented = MapaTur.Application.Terrain.MeshBufferPool.Shared.RentBytes(vertexCount * 4);
        for (int i = 0; i < vertexCount; i++)
        {
            uint argb = tile.BaseColors[i];
            colorsRented[(i * 4) + 0] = (byte)((argb >> 16) & 0xFF);
            colorsRented[(i * 4) + 1] = (byte)((argb >> 8) & 0xFF);
            colorsRented[(i * 4) + 2] = (byte)(argb & 0xFF);
            colorsRented[(i * 4) + 3] = (byte)((argb >> 24) & 0xFF);
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
        g.BufferData<byte>(BufferTargetARB.ArrayBuffer, (nuint)(vertexCount * 4), colorsRented.AsSpan(0, vertexCount * 4), BufferUsageARB.StaticDraw);
        g.EnableVertexAttribArray(1);
        g.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, 4, (void*)0);
        MapaTur.Application.Terrain.MeshBufferPool.Shared.Return(colorsRented); // BufferData copied — pool it back

        buffers.NormalVbo = g.GenBuffer();
        g.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.NormalVbo);
        g.BufferData<float>(BufferTargetARB.ArrayBuffer, (nuint)(normalsSpan.Length * sizeof(float)), normalsSpan, BufferUsageARB.StaticDraw);
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
        uploadTileDataMs += frameClock.ElapsedMilliseconds - tUp0;
        uploadTileDataCount++;

        // The CPU vertex buffers are now in GPU VBOs and nothing reads them again (this is their only reader),
        // so return the big arrays to the pool — the next tile rebuild rents them instead of churning the LOH.
        tile.ReturnBuffersToPool();
    }

    // H6 diagnostics: cumulative client-side wall time of UploadTile BufferData batches + count, logged with
    // the frame-gap line so gap frames attribute to either the client call (Map-path fix) or the swap flush
    // (throttle fix). Reset after each log.
    private double uploadTileDataMs;
    private int uploadTileDataCount;

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

        // Enqueue new (non-resident) tiles; DrainTileUploads spends a per-frame TIME budget so a big reload
        // never freezes one frame. Recomputed each swap, so a tile that left the window before it uploaded is
        // simply dropped. The incoming list is already priority-ordered (base safety net first, then the
        // streamed residents nearest-to-attention first), and the drain consumes it FRONT-first — the old
        // tail-first drain uploaded the FARTHEST detail before the ground under the camera.
        pendingTileUploads.Clear();
        foreach (TerrainMesh3D t in tiles)
        {
            if (!tileBuffers.ContainsKey(t))
            {
                pendingTileUploads.Add(t);
            }
        }

        pendingTileUploads.Reverse(); // consume from the END (O(1) RemoveAt) = the list's FRONT priority order
    }

    // Uploads queued tiles until the per-frame time budget is spent (at least one per frame so the queue
    // always advances) — call every frame, not just on a swap.
    private void DrainTileUploads(GL g)
    {
        if (pendingTileUploads.Count == 0)
        {
            return;
        }

        long start = frameClock.ElapsedMilliseconds;
        long uploadedBytes = 0;
        do
        {
            int last = pendingTileUploads.Count - 1;
            TerrainMesh3D t = pendingTileUploads[last];
            pendingTileUploads.RemoveAt(last);
            if (!tileBuffers.ContainsKey(t))
            {
                UploadTile(g, t);
                uploadedBytes += t.EstimatedGpuBytes;
            }
        }
        while (pendingTileUploads.Count > 0
            && uploadedBytes < TileUploadBudgetBytesPerFrame
            && frameClock.ElapsedMilliseconds - start < TileUploadBudgetMsPerFrame);
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
            && ReferenceEquals(lastMaskWaterways, EffectiveWaterways)
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
        lastMaskWaterways = EffectiveWaterways;
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
        var waterSnap = EffectiveWaterways;
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
        // Mipmapped MIN filter: the 8.5 km window is heavily minified at its far edge; sampling the raw
        // level there made the reconstructed distance jump per pixel (fwidth explosion → fat fuzzy ribbons).
        // Averaged mips fade the distance-alpha smoothly instead, so far trails thin out and dissolve.
        g.GenerateMipmap(TextureTarget.Texture2D);
        g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
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
            g.GenerateMipmap(TextureTarget.Texture2D); // same minification story as the RGBA mask above
            g.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
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

    private void DrawTrailLines(GL g, IReadOnlyList<Trail>? trails, DemRaster? raster, TerrainMesh3D mesh, DetailElevationField? detail, float widthScale = 1f)
    {
        if (trails is null || trails.Count == 0 || raster is null)
        {
            return;
        }

        // The detail field is part of the cache key: as the 1 m window streams with the look-at point a new
        // field arrives, and the seated trail heights must be rebuilt against it (else they'd stay on the
        // stale window's surface).
        bool ribbonCurrent = trailLines is not null
            && ReferenceEquals(lastTrails, trails)
            && ReferenceEquals(lastTrailRaster, raster)
            && ReferenceEquals(lastTrailMesh, mesh)
            && ReferenceEquals(lastTrailDetail, detail);
        if (!ribbonCurrent)
        {
            bool taskMatches = trailBuildTask is not null
                && ReferenceEquals(trailBuildTrails, trails)
                && ReferenceEquals(trailBuildRaster, raster)
                && ReferenceEquals(trailBuildMesh, mesh)
                && ReferenceEquals(trailBuildDetail, detail);
            if (taskMatches && trailBuildTask!.IsCompleted)
            {
                if (trailBuildTask.IsCompletedSuccessfully)
                {
                    (RibbonBuilder ribbon, RibbonBuilder ribbonBlack) = trailBuildTask.Result;
                    DeleteLine(g, ref trailLines);
                    DeleteLine(g, ref trailLinesBlack);
                    trailLines = UploadLine(g, ribbon);
                    trailLinesBlack = UploadLine(g, ribbonBlack);
                    lastTrails = trails;
                    lastTrailRaster = raster;
                    lastTrailMesh = mesh;
                    lastTrailDetail = detail;
                }

                trailBuildTask = null; // success consumed the result; a failure simply re-kicks below next frame
            }
            else if (!taskMatches)
            {
                (IReadOnlyList<Trail> bTrails, DemRaster bRaster, TerrainMesh3D bMesh) = (trails, raster, mesh);
                DetailElevationField? bDetail = detail;
                MapaTur.Application.Terrain.BakedTileAvailabilityIndex? bIndex = BakedElevationIndex;
                trailBuildTrails = trails;
                trailBuildRaster = raster;
                trailBuildMesh = mesh;
                trailBuildDetail = detail;
                trailBuildTask = Task.Run(() =>
                {
                    IReadOnlyList<TrailWorldLine> world = Trail3DWorldProjection.ToWorld(
                        bTrails, bRaster, bMesh, TrailLiftMeters, bDetail, bIndex);

                    // Black trails go in their own ribbon so they can be drawn thicker (a thin black line is
                    // nearly invisible on the dark terrain); every other colour stays on the delicate width.
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

                    return (ribbon, ribbonBlack);
                });
            }
        }

        // Alpha-blend so a fragment that loses the near-field z-fight (see TrailOverlayAlpha) reveals the
        // decal's own trail colour underneath instead of bare terrain. Depth-test stays ON (real ridges must
        // still occlude); depth-write off so the translucent ribbon doesn't block cable car / later overlays.
        g.Enable(EnableCap.Blend);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        g.DepthMask(false);
        DrawLine(g, trailLines, TrailHalfWidthPx * widthScale);
        DrawLine(g, trailLinesBlack, TrailBlackHalfWidthPx * widthScale);
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

    // User-imported off-trail ("pozaszlaki") tracks: GPX/TCX polylines the user added to their private layer.
    // Same world-projection + ribbon machinery as roads/trails, but one distinct hot-magenta colour and drawn
    // with alpha + depth-write-off (like the trail overlay) so a fragment losing the near-field z-fight softens
    // instead of punching a hole. Depth-TEST stays on so real ridges in front still occlude the track.
    private void DrawOffTrailLines(GL g, IReadOnlyList<Trail>? tracks, DemRaster? raster, TerrainMesh3D mesh, DetailElevationField? detail)
    {
        if (tracks is null || tracks.Count == 0 || raster is null)
        {
            return;
        }

        if (offTrailLines is null
            || !ReferenceEquals(lastOffTrailTracks, tracks)
            || !ReferenceEquals(lastOffTrailRaster, raster)
            || !ReferenceEquals(lastOffTrailMesh, mesh)
            || !ReferenceEquals(lastOffTrailDetail, detail))
        {
            DeleteLine(g, ref offTrailLines);
            IReadOnlyList<TrailWorldLine> world = Trail3DWorldProjection.ToWorld(tracks, raster, mesh, OffTrailLiftMeters, detail, BakedElevationIndex);

            var ribbon = new RibbonBuilder();
            foreach (TrailWorldLine line in world)
            {
                ribbon.Append(line.World, OffTrailR, OffTrailG, OffTrailB, OffTrailAlpha);
            }

            offTrailLines = UploadLine(g, ribbon);
            lastOffTrailTracks = tracks;
            lastOffTrailRaster = raster;
            lastOffTrailMesh = mesh;
            lastOffTrailDetail = detail;
        }

        g.Enable(EnableCap.Blend);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        g.DepthMask(false);
        DrawLine(g, offTrailLines, OffTrailHalfWidthPx);
        g.DepthMask(true);
    }

    // FAR-FIELD FALLBACK for the route. The route is primarily painted INTO the surface decal now (dashed translucent
    // violet ON the trail — see EnsureTrailMask's route pass), which adheres to the base AND the streamed detail and
    // can't be occluded. That decal covers the whole mesh when base-only, and the near-field detail window while
    // streaming. This dashed translucent line fills the route in BEYOND that window (far field), where it lies on the
    // smooth base and is not occluded; up close it may be hidden by the detail terrain, but there the decal already
    // shows the route — so near and far stay consistent. Same conflation + violet/dash/alpha as before.
    private void DrawRouteLine(GL g, Route? route, IReadOnlyList<Trail>? trails, DemRaster? raster, TerrainMesh3D mesh, DetailElevationField? detail, float widthScale = 1f)
    {
        if (route is null || raster is null)
        {
            return;
        }

        bool routeCurrent = routeLines is not null
            && ReferenceEquals(lastRoute, route)
            && ReferenceEquals(lastRouteTrails, trails)
            && ReferenceEquals(lastRouteRaster, raster)
            && ReferenceEquals(lastRouteMesh, mesh)
            && ReferenceEquals(lastRouteDetail, detail);
        if (!routeCurrent)
        {
            // OFF-THREAD build, same pattern (and reasons) as the trail ribbon above: the route conflation +
            // 1 m seating of a long multi-stop route measured ~2 s on the GL thread at scene start. The old
            // (or absent) dashes keep drawing until the fresh ribbon lands; a stale result is dropped.
            bool taskMatches = routeBuildTask is not null
                && ReferenceEquals(routeBuildRoute, route)
                && ReferenceEquals(routeBuildTrails, trails)
                && ReferenceEquals(routeBuildRaster, raster)
                && ReferenceEquals(routeBuildMesh, mesh)
                && ReferenceEquals(routeBuildDetail, detail);
            if (taskMatches && routeBuildTask!.IsCompleted)
            {
                if (routeBuildTask.IsCompletedSuccessfully)
                {
                    DeleteLine(g, ref routeLines);
                    routeLines = UploadLine(g, routeBuildTask.Result);
                    lastRoute = route;
                    lastRouteTrails = trails;
                    lastRouteRaster = raster;
                    lastRouteMesh = mesh;
                    lastRouteDetail = detail;
                }

                routeBuildTask = null;
            }
            else if (!taskMatches)
            {
                (Route bRoute, DemRaster bRaster, TerrainMesh3D bMesh) = (route, raster, mesh);
                IReadOnlyList<Trail>? bTrails = trails;
                DetailElevationField? bDetail = detail;
                MapaTur.Application.Terrain.BakedTileAvailabilityIndex? bIndex = BakedElevationIndex;
                routeBuildRoute = route;
                routeBuildTrails = trails;
                routeBuildRaster = raster;
                routeBuildMesh = mesh;
                routeBuildDetail = detail;
                routeBuildTask = Task.Run(() =>
                {
                    // followTrails: re-lay the route onto the SAME polyline as the trail it traverses
                    // (conflation), so the line lies ON the trail instead of beside it. Seated on the detail.
                    RouteWorldLine world = Route3DWorldProjection.ToWorld(
                        bRoute, bRaster, bMesh, RouteLiftMeters, bDetail, followTrails: bTrails, bakedIndex: bIndex);

                    var ribbon = new RibbonBuilder();
                    // DASHED + SEMI-TRANSPARENT: a violet highlight lying ON its trail, trail showing through
                    // the dashes and the ~60% alpha. Violet matches the 2D planner.
                    ribbon.AppendDashed(world.World, 0x7C, 0x3A, 0xED, RouteDashSegments, RouteGapSegments, RouteAlpha);
                    return ribbon;
                });
            }
        }

        // Alpha-blend just the route so the trail beneath shows through (other overlays stay opaque). Depth-test
        // stays on (the terrain still occludes it); depth-write off so the translucent dashes don't block the
        // cable car / later overlays. Restore the opaque state afterwards.
        g.Enable(EnableCap.Blend);
        g.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        g.DepthMask(false);
        DrawLine(g, routeLines, RouteHalfWidthPx * widthScale);
        g.DepthMask(true);
        g.Disable(EnableCap.Blend);
    }

    private void DrawExposedRoutes(GL g, IReadOnlyList<Trail>? exposed, DemRaster? raster, TerrainMesh3D mesh, DetailElevationField? detail, float widthScale = 1f)
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

        DrawLine(g, exposedLines, ExposedRouteHalfWidthPx * widthScale);
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
        cumulusCoverageLocation = g.GetUniformLocation(cumulusProgram, "uCoverage");
        cumulusMemberSeedLocation = g.GetUniformLocation(cumulusProgram, "uMemberSeed");

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
        Vector2 fieldCenter, float baseAltitude, Vector2 drift, float opacity, float coverage, float memberSeed,
        Vector3 fogColor, float fogDensity, float cloudDark, float lightningFlash)
    {
        // The whole field is ALWAYS submitted; the vertex shader's per-puff hash gate (coverage + memberSeed)
        // decides which puffs materialise. Replaces the old "draw the first N instances" count, so slider
        // moves re-roll WHICH puffs exist rather than growing/shrinking one fixed field.
        if (cumulusProgram == 0 || cumulusInstanceCount == 0 || coverage <= 0.001f || opacity <= 0.001f)
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
        // Depth-bias the puffs TOWARD the camera so a distant billboard grazing a ridge silhouette wins the
        // depth-test tie CONSISTENTLY instead of dithering pass/fail every frame ("chmury daleko migają" —
        // diagnosed 2026-07-11: at 20-30 km the 24-bit buffer resolves only ~2-5 m, so a coincident puff blinks).
        // PolygonOffset (NOT a clip-space z-bias, which is strong up-close / ~0 far — the trail-line dead-end):
        // its offset scales with the LOCAL depth resolution, so it's strong exactly where the buffer is coarse
        // (far) and negligible up close, so near peaks still occlude. Billboards face the camera (~0 slope) so
        // only the constant `units` term acts. Depth-write is off, but the offset still biases the compared depth.
        g.Enable(EnableCap.PolygonOffsetFill);
        g.PolygonOffset(0f, -8f);
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
        g.Uniform1(cumulusCoverageLocation, coverage);
        g.Uniform1(cumulusMemberSeedLocation, memberSeed);
        g.BindVertexArray(cumulusVao);
        g.DrawArraysInstanced(PrimitiveType.TriangleStrip, 0, 4, (uint)cumulusInstanceCount);
        g.BindVertexArray(0);
        g.Disable(EnableCap.PolygonOffsetFill);
        g.PolygonOffset(0f, 0f);
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
            photogrammetricRock.Dispose(null);
            return;
        }

        photogrammetricRock.Dispose(gl);
        ReleaseTiles(gl);
        DeleteLine(gl, ref trailLines);
        DeleteLine(gl, ref trailLinesBlack);
        DeleteLine(gl, ref routeLines);
        DeleteLine(gl, ref roadLines);
        DeleteLine(gl, ref offTrailLines);
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
        gl.DeleteFramebuffer(ghostDepthFbo);
        gl.DeleteTexture(ghostDepthTex);
        ghostDepthFbo = 0;
        ghostDepthTex = 0;
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
        gl.DeleteTexture(baseCoverTex);
        baseCoverTex = 0;
        uploadedBaseCoverageMask = null;
        if (orthoDet25Texture != 0) { gl.DeleteTexture(orthoDet25Texture); orthoDet25Texture = 0; }
        if (orthoDet05Texture != 0) { gl.DeleteTexture(orthoDet05Texture); orthoDet05Texture = 0; }
        det25GeoSet = false;
        det05GeoSet = false;
        foreach (DetailCellGpu c in det25Cells.Values)
        {
            if (c.Texture != 0) { gl.DeleteTexture(c.Texture); }
            if (c.StagingTexture != 0) { gl.DeleteTexture(c.StagingTexture); }
        }
        det25Cells.Clear();
        det25UploadQueue.Clear();
        det25ResidentBytes = 0;
        det25BoundTexture = 0;
        foreach (DetailCellGpu c in det05Cells.Values)
        {
            if (c.Texture != 0) { gl.DeleteTexture(c.Texture); }
            if (c.StagingTexture != 0) { gl.DeleteTexture(c.StagingTexture); }
        }
        det05Cells.Clear();
        det05UploadQueue.Clear();
        det05ResidentBytes = 0;
        if (det05ArrayTexture != 0)
        {
            gl.DeleteTexture(det05ArrayTexture);
            det05ArrayTexture = 0;
        }

        if (det05ArrayTextureC != 0)
        {
            gl.DeleteTexture(det05ArrayTextureC);
            det05ArrayTextureC = 0;
        }

        if (det05ArrayTextureB != 0)
        {
            gl.DeleteTexture(det05ArrayTextureB);
            det05ArrayTextureB = 0;
        }

        det05FreeLayers.Clear();
        det05ArrayUniformsTick = -1;
        foreach (OrthoTile t in orthoTiles)
        {
            if (t.Texture != 0)
            {
                gl.DeleteTexture(t.Texture);
                t.Texture = 0;
            }

            ResetStagingUpload(gl, t);
        }
        foreach (OrthoTile t in pendingOrthoRelease)
        {
            if (t.Texture != 0)
            {
                gl.DeleteTexture(t.Texture);
                t.Texture = 0;
            }

            ResetStagingUpload(gl, t);
        }
        pendingOrthoRelease.Clear();
        orthoUploadQueue.Clear();
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
        if (markerProgram != 0)
        {
            gl.DeleteProgram(markerProgram);
            gl.DeleteVertexArray(markerVao);
            gl.DeleteBuffer(markerVbo);
            markerProgram = 0;
            markerVao = 0;
            markerVbo = 0;
        }
        if (gearRibbonProgram != 0)
        {
            gl.DeleteProgram(gearRibbonProgram);
            gl.DeleteProgram(gearRingProgram);
            gl.DeleteVertexArray(gearRibbonVao);
            gl.DeleteVertexArray(gearRingVao);
            gl.DeleteBuffer(gearRibbonVbo);
            gl.DeleteBuffer(gearRingVbo);
            gearRibbonProgram = 0;
            gearRingProgram = 0;
            gearRibbonVao = 0;
            gearRingVao = 0;
            gearRibbonVbo = 0;
            gearRingVbo = 0;
        }
        foreach (uint tex in aiDragonTextures.Values)
        {
            if (tex != 0)
            {
                gl.DeleteTexture(tex);
            }
        }

        aiDragonTextures.Clear();
    }
}
