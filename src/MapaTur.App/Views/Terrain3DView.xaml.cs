using System.Numerics;

using MapaTur.App.Localization;
using MapaTur.App.Services;
using MapaTur.App.Services.Media;
using MapaTur.Application.Maps;
using MapaTur.Application.Markers;
using MapaTur.Application.Media;
using MapaTur.Application.Terrain;
using MapaTur.Domain.Climbing;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Location;
using MapaTur.Domain.Pois;
using MapaTur.Domain.Routing;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;

using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace MapaTur.App.Views;

/// <summary>
/// 3D terrain preview rendered with SkiaSharp. Single-finger drag orbits the
/// camera, two-finger drag pans the focal point in the ground plane, and pinch
/// zooms in/out. Bind <see cref="Tiles"/>; the camera is owned by the view and
/// auto-framed on tile change.
/// </summary>
public partial class Terrain3DView : ContentView
{
    /// <summary>
    /// Bindable set of terrain mesh tiles to render (a high-resolution DEM is split into ≤65 536-vertex
    /// tiles, each its own SKVertices). All tiles share one world frame; tile 0 defines the overlay
    /// coordinate system. Setting it auto-frames the camera.
    /// </summary>
    public static readonly BindableProperty TilesProperty = BindableProperty.Create(
        nameof(Tiles),
        typeof(IReadOnlyList<TerrainMesh3D>),
        typeof(Terrain3DView),
        propertyChanged: OnTilesChanged);

    public IReadOnlyList<TerrainMesh3D>? Tiles
    {
        get => (IReadOnlyList<TerrainMesh3D>?)GetValue(TilesProperty);
        set => SetValue(TilesProperty, value);
    }

    /// <summary>The world frame for overlays + framing: the first tile (all tiles share the frame), or null.</summary>
    private TerrainMesh3D? WorldFrame => Tiles is { Count: > 0 } tiles ? tiles[0] : null;

    /// <summary>Bindable DEM raster used to look up elevations along overlay trails.</summary>
    public static readonly BindableProperty RasterProperty = BindableProperty.Create(
        nameof(Raster),
        typeof(DemRaster),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public DemRaster? Raster
    {
        get => (DemRaster?)GetValue(RasterProperty);
        set => SetValue(RasterProperty, value);
    }

    /// <summary>Bindable sampler of the TRUE rendered surface (baked 1 m tiles) for the camera's
    /// anti-tunnelling floor: (lon, lat) → elevation metres, or null off-coverage. The coarse
    /// <see cref="Raster"/> understates ridges by metres (box-average), so the floor clips into the drawn
    /// 1 m terrain without this. Null = coarse-only floor (pre-bake scenes).</summary>
    public static readonly BindableProperty FineElevationSamplerProperty = BindableProperty.Create(
        nameof(FineElevationSampler),
        typeof(Func<double, double, double?>),
        typeof(Terrain3DView));

    public Func<double, double, double?>? FineElevationSampler
    {
        get => (Func<double, double, double?>?)GetValue(FineElevationSamplerProperty);
        set => SetValue(FineElevationSamplerProperty, value);
    }

    /// <summary>Bindable 1 m LOD detail field: when present, trail/road/route vertices inside its window
    /// seat on the detail surface instead of the coarse base, so overlays don't float over the carved-deeper
    /// near-field terrain. Null until the LOD pipeline has built a detail patch.</summary>
    public static readonly BindableProperty DetailElevationProperty = BindableProperty.Create(
        nameof(DetailElevation),
        typeof(DetailElevationField),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public DetailElevationField? DetailElevation
    {
        get => (DetailElevationField?)GetValue(DetailElevationProperty);
        set => SetValue(DetailElevationProperty, value);
    }

    /// <summary>Bindable trails overlay rendered on top of the terrain.</summary>
    public static readonly BindableProperty TrailsProperty = BindableProperty.Create(
        nameof(Trails),
        typeof(IReadOnlyList<Trail>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<Trail>? Trails
    {
        get => (IReadOnlyList<Trail>?)GetValue(TrailsProperty);
        set => SetValue(TrailsProperty, value);
    }

    /// <summary>Bindable roads overlay (unmarked Trail polylines) drawn as grey ribbons under the trails.</summary>
    public static readonly BindableProperty RoadsProperty = BindableProperty.Create(
        nameof(Roads),
        typeof(IReadOnlyList<Trail>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<Trail>? Roads
    {
        get => (IReadOnlyList<Trail>?)GetValue(RoadsProperty);
        set => SetValue(RoadsProperty, value);
    }

    /// <summary>Bindable watercourse polylines (waterway=river|stream), painted into the terrain as a shiny water decal.</summary>
    public static readonly BindableProperty WaterwaysProperty = BindableProperty.Create(
        nameof(Waterways),
        typeof(IReadOnlyList<Trail>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<Trail>? Waterways
    {
        get => (IReadOnlyList<Trail>?)GetValue(WaterwaysProperty);
        set => SetValue(WaterwaysProperty, value);
    }

    /// <summary>Bindable waterfall points rendered as bright foam accents on their streams.</summary>
    public static readonly BindableProperty WaterfallsProperty = BindableProperty.Create(
        nameof(Waterfalls),
        typeof(IReadOnlyList<MapaTur.Application.Waterways.Waterfall>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<MapaTur.Application.Waterways.Waterfall>? Waterfalls
    {
        get => (IReadOnlyList<MapaTur.Application.Waterways.Waterfall>?)GetValue(WaterfallsProperty);
        set => SetValue(WaterfallsProperty, value);
    }

    /// <summary>Bindable user-imported off-trail ("pozaszlaki") tracks drawn as distinct hot-magenta ribbons.</summary>
    public static readonly BindableProperty OffTrailTracksProperty = BindableProperty.Create(
        nameof(OffTrailTracks),
        typeof(IReadOnlyList<Trail>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<Trail>? OffTrailTracks
    {
        get => (IReadOnlyList<Trail>?)GetValue(OffTrailTracksProperty);
        set => SetValue(OffTrailTracksProperty, value);
    }

    /// <summary>Bindable exposed/guide routes (Trail polylines) drawn as dotted lines, distinct from marked trails.</summary>
    public static readonly BindableProperty ExposedRoutesProperty = BindableProperty.Create(
        nameof(ExposedRoutes),
        typeof(IReadOnlyList<Trail>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<Trail>? ExposedRoutes
    {
        get => (IReadOnlyList<Trail>?)GetValue(ExposedRoutesProperty);
        set => SetValue(ExposedRoutesProperty, value);
    }

    /// <summary>Bindable planned route rendered as a distinct violet polyline on top of trails.</summary>
    public static readonly BindableProperty RouteProperty = BindableProperty.Create(
        nameof(Route),
        typeof(Route),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public Route? Route
    {
        get => (Route?)GetValue(RouteProperty);
        set => SetValue(RouteProperty, value);
    }

    /// <summary>Bindable climbing areas rendered as red circular markers above the mesh.</summary>
    public static readonly BindableProperty ClimbingAreasProperty = BindableProperty.Create(
        nameof(ClimbingAreas),
        typeof(IReadOnlyList<ClimbingArea>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<ClimbingArea>? ClimbingAreas
    {
        get => (IReadOnlyList<ClimbingArea>?)GetValue(ClimbingAreasProperty);
        set => SetValue(ClimbingAreasProperty, value);
    }

    /// <summary>Bindable mountain POIs rendered as colour-coded circular markers above the mesh.</summary>
    public static readonly BindableProperty PoisProperty = BindableProperty.Create(
        nameof(Pois),
        typeof(IReadOnlyList<MountainPoi>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<MountainPoi>? Pois
    {
        get => (IReadOnlyList<MountainPoi>?)GetValue(PoisProperty);
        set => SetValue(PoisProperty, value);
    }

    /// <summary>Bindable summits rendered as gold mountain glyphs with elevation labels above the mesh.</summary>
    public static readonly BindableProperty PeaksProperty = BindableProperty.Create(
        nameof(Peaks),
        typeof(IReadOnlyList<TerrainPeak>),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public IReadOnlyList<TerrainPeak>? Peaks
    {
        get => (IReadOnlyList<TerrainPeak>?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    /// <summary>Whether summit glyphs + elevation labels are drawn (premium menu "Nazwy szczytów").</summary>
    public static readonly BindableProperty ShowPeakNamesProperty = BindableProperty.Create(
        nameof(ShowPeakNames), typeof(bool), typeof(Terrain3DView), true,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public bool ShowPeakNames
    {
        get => (bool)GetValue(ShowPeakNamesProperty);
        set => SetValue(ShowPeakNamesProperty, value);
    }

    public static readonly BindableProperty ShowSauronTowerProperty = BindableProperty.Create(
        nameof(ShowSauronTower), typeof(bool), typeof(Terrain3DView), false,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    /// <summary>Easter egg: a dark tower with the glowing Eye of Sauron on Świnica.</summary>
    public bool ShowSauronTower
    {
        get => (bool)GetValue(ShowSauronTowerProperty);
        set => SetValue(ShowSauronTowerProperty, value);
    }

    public static readonly BindableProperty ShowEaglesProperty = BindableProperty.Create(
        nameof(ShowEagles), typeof(bool), typeof(Terrain3DView), false,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    /// <summary>Easter egg: eagles soaring over the Orla Perć ridge.</summary>
    public bool ShowEagles
    {
        get => (bool)GetValue(ShowEaglesProperty);
        set => SetValue(ShowEaglesProperty, value);
    }

    public static readonly BindableProperty AtmosphereEffectsEnabledProperty = BindableProperty.Create(
        nameof(AtmosphereEffectsEnabled), typeof(bool), typeof(Terrain3DView), true,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    /// <summary>Animated atmosphere (clouds/lightning/eagles/bloom) + the continuous repaint that drives it.
    /// Off → the view stops auto-redrawing while the camera is still, so the GPU idles (battery saver).</summary>
    public bool AtmosphereEffectsEnabled
    {
        get => (bool)GetValue(AtmosphereEffectsEnabledProperty);
        set => SetValue(AtmosphereEffectsEnabledProperty, value);
    }

    public static readonly BindableProperty UserLocationFreshnessProperty = BindableProperty.Create(
        nameof(UserLocationFreshness), typeof(LocationFreshness), typeof(Terrain3DView), LocationFreshness.Live,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    /// <summary>Freshness of the GPS fix — the marker fades + its halo widens when Stale/Lost.</summary>
    public LocationFreshness UserLocationFreshness
    {
        get => (LocationFreshness)GetValue(UserLocationFreshnessProperty);
        set => SetValue(UserLocationFreshnessProperty, value);
    }

    /// <summary>Whether the night-sky pass (stars + name labels + constellation lines) is drawn after dusk.</summary>
    public static readonly BindableProperty ShowNightSkyProperty = BindableProperty.Create(
        nameof(ShowNightSky), typeof(bool), typeof(Terrain3DView), true,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public bool ShowNightSky
    {
        get => (bool)GetValue(ShowNightSkyProperty);
        set => SetValue(ShowNightSkyProperty, value);
    }

    /// <summary>Maximum camera distance (metres) at which summit name labels are shown — peaks farther than
    /// this are culled, so the user can trim distant label clutter via a slider. Default 15 km.</summary>
    public static readonly BindableProperty PeakLabelRadiusMetersProperty = BindableProperty.Create(
        nameof(PeakLabelRadiusMeters), typeof(double), typeof(Terrain3DView), 15000.0,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public double PeakLabelRadiusMeters
    {
        get => (double)GetValue(PeakLabelRadiusMetersProperty);
        set => SetValue(PeakLabelRadiusMetersProperty, value);
    }

    /// <summary>Whether the Kasprowy Wierch cable-car overlay (sagging cables + station masts) is drawn.</summary>
    public static readonly BindableProperty ShowCableCarProperty = BindableProperty.Create(
        nameof(ShowCableCar), typeof(bool), typeof(Terrain3DView), false,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public bool ShowCableCar
    {
        get => (bool)GetValue(ShowCableCarProperty);
        set => SetValue(ShowCableCarProperty, value);
    }

    /// <summary>Whether the contour-line (warstwice) overlay is draped on the 3D relief.</summary>
    public static readonly BindableProperty ShowContoursProperty = BindableProperty.Create(
        nameof(ShowContours), typeof(bool), typeof(Terrain3DView), true,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public bool ShowContours
    {
        get => (bool)GetValue(ShowContoursProperty);
        set => SetValue(ShowContoursProperty, value);
    }

    /// <summary>Whether the on-screen camera control pads (altitude + pan/tilt) are shown. Set false in the
    /// immersive landscape mode so a phone screenshot of the scene is free of UI chrome.</summary>
    public static readonly BindableProperty ControlsVisibleProperty = BindableProperty.Create(
        nameof(ControlsVisible), typeof(bool), typeof(Terrain3DView), true,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).ApplyControlsVisibility((bool)n));

    public bool ControlsVisible
    {
        get => (bool)GetValue(ControlsVisibleProperty);
        set => SetValue(ControlsVisibleProperty, value);
    }

    private void ApplyControlsVisibility(bool visible)
    {
        AltitudePad.IsVisible = visible;
        PanTiltPad.IsVisible = visible;
    }

    /// <summary>
    /// Whether the orthophoto drape is shown. When false the terrain falls back to hypsometric shading
    /// (premium menu "Ortofoto"). Applied to the GL renderer each frame; textures stay resident.
    /// </summary>
    public static readonly BindableProperty ShowOrthoProperty = BindableProperty.Create(
        nameof(ShowOrtho), typeof(bool), typeof(Terrain3DView), true,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public bool ShowOrtho
    {
        get => (bool)GetValue(ShowOrthoProperty);
        set => SetValue(ShowOrthoProperty, value);
    }

    // Geographic extent the LOD ortho covers; the renderer fades ortho→hypsometric beyond it (kills the
    // stretched-edge "strata" bands on a base wider than the ortho). Null = no cull. Bound from the VM.
    public static readonly BindableProperty LodOrthoCoverageBoundsProperty = BindableProperty.Create(
        nameof(LodOrthoCoverageBounds), typeof(MapaTur.Domain.Geography.MapBounds?), typeof(Terrain3DView), null,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public MapaTur.Domain.Geography.MapBounds? LodOrthoCoverageBounds
    {
        get => (MapaTur.Domain.Geography.MapBounds?)GetValue(LodOrthoCoverageBoundsProperty);
        set => SetValue(LodOrthoCoverageBoundsProperty, value);
    }

    // Geographic extent of the CURRENT streamed 1 m detail window (null = none). The renderer keeps the
    // legacy lake-water seating inside it (fine basins are real there) and seats/skips lakes against the
    // coarse base elsewhere, so water planes can't poke through coarse-filled basins. Bound from the VM.
    public static readonly BindableProperty LodDetailBoundsProperty = BindableProperty.Create(
        nameof(LodDetailBounds), typeof(MapaTur.Domain.Geography.MapBounds?), typeof(Terrain3DView), null,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public MapaTur.Domain.Geography.MapBounds? LodDetailBounds
    {
        get => (MapaTur.Domain.Geography.MapBounds?)GetValue(LodDetailBoundsProperty);
        set => SetValue(LodDetailBoundsProperty, value);
    }

    // Baked z13-z16 tile index, when a baked pyramid is loaded — passed straight through to the GL renderer's
    // BakedElevationIndex so trail/route/road line seating can sample the SAME real elevation data actually
    // rendered, instead of the static coarse base (see Terrain3DGlRenderer.BakedElevationIndex's doc comment).
    public static readonly BindableProperty BakedElevationIndexProperty = BindableProperty.Create(
        nameof(BakedElevationIndex), typeof(MapaTur.Application.Terrain.BakedTileAvailabilityIndex), typeof(Terrain3DView), null,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public MapaTur.Application.Terrain.BakedTileAvailabilityIndex? BakedElevationIndex
    {
        get => (MapaTur.Application.Terrain.BakedTileAvailabilityIndex?)GetValue(BakedElevationIndexProperty);
        set => SetValue(BakedElevationIndexProperty, value);
    }

    // Surface-ownership mask (BaseCoverageMaskBuilder): where the resident hole-free z16 detail fully covers
    // the ground, the renderer DISCARDS base-skin fragments — the box-averaged base otherwise sits metres
    // ABOVE the true surface on convex slopes and depth-buries the streamed detail ("lotnisko" burial).
    public static readonly BindableProperty BaseCoverageMaskProperty = BindableProperty.Create(
        nameof(BaseCoverageMask), typeof(MapaTur.Application.Terrain.BaseCoverageMask), typeof(Terrain3DView), null,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public MapaTur.Application.Terrain.BaseCoverageMask? BaseCoverageMask
    {
        get => (MapaTur.Application.Terrain.BaseCoverageMask?)GetValue(BaseCoverageMaskProperty);
        set => SetValue(BaseCoverageMaskProperty, value);
    }

    /// <summary>
    /// Whether the rock material is blended onto steep faces (premium menu "Skały"). When false the steep
    /// walls keep the raw orthophoto (which smears) — useful for an A/B of the blend.
    /// </summary>
    public static readonly BindableProperty RockMaterialEnabledProperty = BindableProperty.Create(
        nameof(RockMaterialEnabled), typeof(bool), typeof(Terrain3DView), true,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public bool RockMaterialEnabled
    {
        get => (bool)GetValue(RockMaterialEnabledProperty);
        set => SetValue(RockMaterialEnabledProperty, value);
    }

    /// <summary>
    /// Whether the base albedo is painted by elevation-zone biomes (premium menu "Biomy"): meadow/hala low,
    /// scree/piargi mid, snow/ice high — from elevation + slope + aspect. Off by default (an A/B material mode).
    /// </summary>
    public static readonly BindableProperty BiomeMaterialEnabledProperty = BindableProperty.Create(
        nameof(BiomeMaterialEnabled), typeof(bool), typeof(Terrain3DView), false,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public bool BiomeMaterialEnabled
    {
        get => (bool)GetValue(BiomeMaterialEnabledProperty);
        set => SetValue(BiomeMaterialEnabledProperty, value);
    }

    /// <summary>Whether MSAA anti-aliasing is used (premium menu render-quality profile).</summary>
    public static readonly BindableProperty MsaaEnabledProperty = BindableProperty.Create(
        nameof(MsaaEnabled), typeof(bool), typeof(Terrain3DView), true,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public bool MsaaEnabled
    {
        get => (bool)GetValue(MsaaEnabledProperty);
        set => SetValue(MsaaEnabledProperty, value);
    }

    /// <summary>Whether the avalanche slope-steepness map shading is active (premium menu "Mapa nachylenia").</summary>
    public static readonly BindableProperty SlopeMapEnabledProperty = BindableProperty.Create(
        nameof(SlopeMapEnabled), typeof(bool), typeof(Terrain3DView), false,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public bool SlopeMapEnabled
    {
        get => (bool)GetValue(SlopeMapEnabledProperty);
        set => SetValue(SlopeMapEnabledProperty, value);
    }

    /// <summary>Whether the per-frame debug stats string is computed (premium menu debug overlay).</summary>
    public static readonly BindableProperty DebugEnabledProperty = BindableProperty.Create(
        nameof(DebugEnabled), typeof(bool), typeof(Terrain3DView), false);

    public bool DebugEnabled
    {
        get => (bool)GetValue(DebugEnabledProperty);
        set => SetValue(DebugEnabledProperty, value);
    }

    /// <summary>Live one-line render stats (FPS · tiles · trees · camera distance) shown by the debug HUD.</summary>
    public static readonly BindableProperty DebugStatsProperty = BindableProperty.Create(
        nameof(DebugStats), typeof(string), typeof(Terrain3DView), string.Empty);

    public string DebugStats
    {
        get => (string)GetValue(DebugStatsProperty);
        set => SetValue(DebugStatsProperty, value);
    }

    private readonly System.Diagnostics.Stopwatch frameClock = System.Diagnostics.Stopwatch.StartNew();
    private long lastFrameMs;
    private double smoothedFps;
    private int debugStatCounter;

    // Per-frame marker-occlusion cost, surfaced in the debug HUD so the single-threaded → parallel win
    // (and any future regression) is observable on-device. Only measured while DebugEnabled.
    private double lastOcclusionMs;
    private int lastOcclusionMarkers;

    // Above this many on-screen markers the per-marker occlusion raycast fans out across cores; below it
    // the thread overhead isn't worth it and we stay sequential. A clear Tatra view with POIs + peaks
    // pushes hundreds of markers, which is where the single-threaded march dominated the frame.
    private const int OcclusionParallelThreshold = 64;

    // "2D map" mode: climbing to the altitude ceiling morphs the 3D view into a top-down hypsometric
    // map (pitch to nadir, ortho faded out) for fast repositioning; descending restores the pitch the
    // user had on entry — at the new location. The policy is pure (TopDownMapMode, unit-tested); this
    // view feeds it the REAL eye altitude + frame delta and applies the resulting pitch + ortho fade.
    private readonly MapaTur.Application.Terrain.TopDownMapMode mapMode = new();
    private readonly System.Diagnostics.Stopwatch mapModeClock = System.Diagnostics.Stopwatch.StartNew();
    private double mapModeLastSeconds;
    private const float NadirPitchRadians = (MathF.PI / 2f) - 0.02f; // matches Terrain3DController.MaxPitch

    // Altitude ceiling while the "2D map" mode is active: high enough that holding "raise" past the 3D
    // ceiling keeps zooming the map out until the whole range fits, while "lower" zooms back in and
    // finally drops through the exit altitude into the restored 3D view.
    private const double MapModeCeilingMeters = 60_000.0;

    // Smoothed FPS + scene counts, refreshed a few times a second while the debug HUD is on.
    private void UpdateDebugStats(int tileCount)
    {
        long now = frameClock.ElapsedMilliseconds;
        long dt = now - lastFrameMs;
        lastFrameMs = now;
        if (dt is > 0 and < 1000)
        {
            double fps = 1000.0 / dt;
            smoothedFps = smoothedFps <= 0 ? fps : (smoothedFps * 0.9) + (fps * 0.1);
        }
        if (++debugStatCounter >= 12)
        {
            debugStatCounter = 0;
            int trees = cachedForest?.Count ?? 0;
            DebugStats = $"{smoothedFps:F0} FPS · kafle {tileCount} · drzewa {trees} · cam {Camera.Distance / 1000.0:F1} km · occ {lastOcclusionMs:F1} ms/{lastOcclusionMarkers}";
        }
    }

    /// <summary>
    /// Bindable current GPS fix of the device. A null value hides the marker. The view wraps the
    /// fix in a one-element list internally so the existing <c>Marker3DOverlayProjector</c> caching
    /// machinery applies — only re-projects when the fix reference (not its contents) changes.
    /// </summary>
    public static readonly BindableProperty UserLocationProperty = BindableProperty.Create(
        nameof(UserLocation),
        typeof(UserLocation),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public UserLocation? UserLocation
    {
        get => (UserLocation?)GetValue(UserLocationProperty);
        set => SetValue(UserLocationProperty, value);
    }

    /// <summary>Bindable path to an ortho-photo image draped over the terrain (GPU path only). Null = hypsometric tint.</summary>
    public static readonly BindableProperty OrthoTexturePathProperty = BindableProperty.Create(
        nameof(OrthoTexturePath),
        typeof(string),
        typeof(Terrain3DView),
        propertyChanged: OnOrthoTexturePathChanged);

    public string? OrthoTexturePath
    {
        get => (string?)GetValue(OrthoTexturePathProperty);
        set => SetValue(OrthoTexturePathProperty, value);
    }

    private static void OnOrthoTexturePathChanged(BindableObject bindable, object oldValue, object newValue)
    {
        var view = (Terrain3DView)bindable;
        view.orthoPathDirty = true;
        view.Canvas.InvalidateSurface();
    }

    /// <summary>Bindable ortho tiles (row-major), one per mesh cell. Takes precedence over the single OrthoTexturePath.</summary>
    public static readonly BindableProperty OrthoTexturePathsProperty = BindableProperty.Create(
        nameof(OrthoTexturePaths),
        typeof(IReadOnlyList<string>),
        typeof(Terrain3DView),
        propertyChanged: OnOrthoTexturePathChanged);

    public IReadOnlyList<string>? OrthoTexturePaths
    {
        get => (IReadOnlyList<string>?)GetValue(OrthoTexturePathsProperty);
        set => SetValue(OrthoTexturePathsProperty, value);
    }

    /// <summary>
    /// Pre-decoded ortho cells supplied directly as RGBA8 bytes (e.g. composited from MBTiles).
    /// Takes precedence over both <see cref="OrthoTexturePaths"/> and <see cref="OrthoTexturePath"/>:
    /// the view uploads these bytes verbatim without touching the filesystem.
    /// </summary>
    public static readonly BindableProperty OrthoTextureCellsProperty = BindableProperty.Create(
        nameof(OrthoTextureCells),
        typeof(IReadOnlyList<OrthoTextureCell>),
        typeof(Terrain3DView),
        propertyChanged: OnOrthoTexturePathChanged);

    public IReadOnlyList<OrthoTextureCell>? OrthoTextureCells
    {
        get => (IReadOnlyList<OrthoTextureCell>?)GetValue(OrthoTextureCellsProperty);
        set => SetValue(OrthoTextureCellsProperty, value);
    }

    /// <summary>
    /// Atmospheric model (time-of-day driven sun + sky + fog) the GPU renderer samples each
    /// frame. When null the renderer falls back to the legacy flat-clear sky and the per-mesh
    /// baked Lambert / ambient — same code path as before atmospherics shipped.
    /// </summary>
    public static readonly BindableProperty AtmosphereProperty = BindableProperty.Create(
        nameof(Atmosphere),
        typeof(Atmosphere),
        typeof(Terrain3DView),
        propertyChanged: OnOverlayDataChanged);

    public Atmosphere? Atmosphere
    {
        get => (Atmosphere?)GetValue(AtmosphereProperty);
        set => SetValue(AtmosphereProperty, value);
    }

    /// <summary>
    /// Forest density [0,1] bound from the "Las" slider. Changing it rebuilds the tree placement
    /// (a different tree count) and repaints.
    /// </summary>
    public static readonly BindableProperty ForestDensityProperty = BindableProperty.Create(
        nameof(ForestDensity),
        typeof(double),
        typeof(Terrain3DView),
        0.6,
        propertyChanged: OnForestDensityChanged);

    public double ForestDensity
    {
        get => (double)GetValue(ForestDensityProperty);
        set => SetValue(ForestDensityProperty, value);
    }

    private static void OnForestDensityChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is Terrain3DView view)
        {
            // Force the forest to be re-placed at the new density on the next paint.
            view.cachedForest = null;
            view.cachedForestTiles = null;
            view.Canvas.InvalidateSurface();
        }
    }

    /// <summary>
    /// Serialized camera state (a DEM-bounds key + orbit params), two-way bound to the view-model
    /// so it persists across restarts. The view writes its current camera here on a debounce and
    /// restores from it in <see cref="FrameMesh"/> when the embedded key matches the loaded DEM.
    /// </summary>
    public static readonly BindableProperty CameraStateProperty = BindableProperty.Create(
        nameof(CameraState),
        typeof(string),
        typeof(Terrain3DView),
        defaultBindingMode: BindingMode.TwoWay);

    public string? CameraState
    {
        get => (string?)GetValue(CameraStateProperty);
        set => SetValue(CameraStateProperty, value);
    }

    /// <summary>
    /// True while a scripted fly-through is running. Two-way bound so the host page can hide the
    /// toolbar / slider chrome for a clean cinematic shot; the view also hides its own on-screen
    /// pads + the fly button while it's set.
    /// </summary>
    public static readonly BindableProperty IsFlyingProperty = BindableProperty.Create(
        nameof(IsFlying),
        typeof(bool),
        typeof(Terrain3DView),
        defaultValue: false,
        defaultBindingMode: BindingMode.OneWayToSource);

    public bool IsFlying
    {
        get => (bool)GetValue(IsFlyingProperty);
        private set => SetValue(IsFlyingProperty, value);
    }

    /// <summary>
    /// Two-way toggle for first-person WALK mode. Bound to the view-model's walk switch/chip AND flipped by the
    /// F8 key, so either source turns it on/off; the property-changed hook enters/leaves walk. In walk mode the
    /// eye is ground-clamped and driven by WASD (move), mouse-drag or Q/E/R/F (look), Space (jump), Shift (run).
    /// </summary>
    public static readonly BindableProperty IsWalkModeActiveProperty = BindableProperty.Create(
        nameof(IsWalkModeActive),
        typeof(bool),
        typeof(Terrain3DView),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay,
        propertyChanged: OnIsWalkModeActiveChanged);

    public bool IsWalkModeActive
    {
        get => (bool)GetValue(IsWalkModeActiveProperty);
        set => SetValue(IsWalkModeActiveProperty, value);
    }

    private static void OnIsWalkModeActiveChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is Terrain3DView view)
        {
            if ((bool)newValue)
            {
                view.EnterWalkMode();
            }
            else
            {
                view.ExitWalkMode();
            }
        }
    }

    /// <summary>Two-way toggle for DRAGON FLIGHT (F7): ride a dragon over the terrain, third-person. Right-drag
    /// steers (yaw + pitch), W/S throttle, A/D bank. Mutually exclusive with walk mode.</summary>
    public static readonly BindableProperty IsDragonFlightActiveProperty = BindableProperty.Create(
        nameof(IsDragonFlightActive),
        typeof(bool),
        typeof(Terrain3DView),
        defaultValue: false,
        defaultBindingMode: BindingMode.TwoWay,
        propertyChanged: OnIsDragonFlightActiveChanged);

    public bool IsDragonFlightActive
    {
        get => (bool)GetValue(IsDragonFlightActiveProperty);
        set => SetValue(IsDragonFlightActiveProperty, value);
    }

    private static void OnIsDragonFlightActiveChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is Terrain3DView view)
        {
            if ((bool)newValue)
            {
                view.EnterDragonFlight();
            }
            else
            {
                view.ExitDragonFlight();
            }
        }
    }

    /// <summary>Which dragon model F7 rides: 0 = classic (solid red, procedural wing rig), 1 = animated
    /// (textured, baked idle/running/flying loops). Switching while airborne swaps the model in place.</summary>
    public static readonly BindableProperty DragonVariantProperty = BindableProperty.Create(
        nameof(DragonVariant),
        typeof(int),
        typeof(Terrain3DView),
        0,
        propertyChanged: OnDragonVariantChanged);

    public int DragonVariant
    {
        get => (int)GetValue(DragonVariantProperty);
        set => SetValue(DragonVariantProperty, value);
    }

    private static void OnDragonVariantChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is Terrain3DView view && (int)newValue != (int)oldValue)
        {
            // Drop the loaded model so the next need (or the active flight, right now) loads the new variant.
            // The procedural Skia dragon covers the gap while the GLB streams in.
            view.dragonModel3D = null;
            view.dragonRig = null;
            view.dragonLoadedVariant = -1;
            if (view.dragonActive)
            {
                view.LoadDragonModelAsync();
            }
        }
    }

    /// <summary>When true (LOD Etap 3), the view reports the camera's ground focus as it moves so the host
    /// can stream the 1 m DETAIL tiles to follow it. The coarse base stays static and framed — only the
    /// detail layer swaps — so a detail reload must NOT reframe the camera.</summary>
    public bool DetailStreamingEnabled { get; set; }

    /// <summary>Raised (debounced by the camera-save timer) with a snapshot of the camera pose while
    /// <see cref="DetailStreamingEnabled"/>. The host resolves the look-at point and decides whether to
    /// reload the detail patch (Krok 4: detail follows the gaze, not the camera target).</summary>
    public event EventHandler<Camera3D>? CameraFocusMoved;

    /// <summary>Camera state mutated by gestures and used by the renderer.</summary>
    public Camera3D Camera { get; } = new Camera3D();

    private readonly Terrain3DController controller;
    private readonly Terrain3DCanvasRenderer renderer = new();
    private readonly Terrain3DFrameScratch frameScratch = new();

    // Stateful overlay projectors own the camera-independent world cache plus reusable screen buffers,
    // so a gesture (which changes only the camera) doesn't re-run ~38k DEM bilinear samples + geo→world
    // cosines per frame, nor allocate per-frame point arrays. They rebuild only when the trails/route,
    // raster or mesh reference changes; per frame they pay just the screen transform into cached buffers.
    private readonly Trail3DOverlayProjector trailProjector = new();
    private readonly Route3DOverlayProjector routeProjector = new();

    // Marker overlays (climbing areas, summits) get the same stateful, zero-per-frame-allocation
    // treatment as trails/routes: the world cache rebuilds only when the items/raster/mesh change,
    // and each frame fills a reused results buffer. One generic projector serves both — they differ
    // only in their world-build (climbing samples the DEM; summits carry their own elevation).
    private const float ClimbingMarkerLiftMeters = 30f;
    // Now that POIs seat on the rendered 1 m detail surface (not the saddle-smoothing coarse base), the lift
    // is purely "hover clear of the ground" — a smaller value sits the pass dot on the pass, not above it.
    private const float PoiMarkerLiftMeters = 10f;
    private const float PeakMarkerLiftMeters = 40f;

    private readonly Marker3DOverlayProjector<ClimbingArea, ProjectedClimbingArea> climbingProjector =
        new(
            (areas, raster, mesh, lift, _) => Climbing3DProjection.ToWorld(areas, raster!, mesh, lift),
            (source, screen) => new ProjectedClimbingArea(source, screen));

    private readonly Marker3DOverlayProjector<MountainPoi, ProjectedPoi> poiProjector =
        new(
            (pois, raster, mesh, lift, detail) => Poi3DProjection.ToWorld(pois, raster!, mesh, lift, detail),
            (source, screen) => new ProjectedPoi(source, screen));

    private readonly Marker3DOverlayProjector<TerrainPeak, ProjectedPeak> peakProjector =
        new(
            (peaks, _, mesh, lift, _) => Peak3DProjection.ToWorld(peaks, mesh, lift),
            (source, screen) => new ProjectedPeak(source, screen));

    // GPS marker: prefer the OS-reported altitude when present (UserLocation3DProjection takes care
    // of that), otherwise fall back to a DEM lookup. Lift higher than POI/climbing so the dot
    // visibly hovers above the ground on flat sections instead of merging with the mesh.
    private const float UserLocationMarkerLiftMeters = 20f;
    // Initialized in the constructor (not inline) so the worldBuilder lambda can capture `this` and read
    // BakedElevationIndex — the dot must seat on the SAME real baked tile the route/trail lines now use, or it
    // diverges from the route on steep ground ("kropka i trasa rozjeżdżają się"). BakedElevationIndex is set once
    // and stable, so capturing it once is correct (the projector's cache never needs to see it change).
    private readonly Marker3DOverlayProjector<UserLocation, ProjectedUserLocation> userLocationProjector;
    // Reused one-element buffer so a fix update doesn't allocate a fresh list per frame; the
    // projector compares by reference so we only swap the contained UserLocation when it changes.
    private readonly UserLocation[] userLocationBuffer = new UserLocation[1];

    // Cached projected markers + surface size from the last paint, so a screen tap can be mapped back
    // to the marker under it (the projectors own the per-frame buffers; we only keep references).
    private IReadOnlyList<ProjectedClimbingArea>? lastProjectedClimbing;
    private IReadOnlyList<ProjectedPoi>? lastProjectedPois;
    private int lastSurfacePixelWidth;
    private int lastSurfacePixelHeight;

    /// <summary>Real backbuffer height (px) of the last 3D paint — the true viewport height for screen-space LOD.
    /// The 2D Mapsui viewport is never laid out in 3D mode, so this is the only valid height source on mobile.
    /// 0 until the first frame is drawn.</summary>
    public int SurfacePixelHeight => lastSurfacePixelHeight;

    /// <summary>Real backbuffer width (px) of the last 3D paint — paired with <see cref="SurfacePixelHeight"/> to
    /// give the true viewport aspect ratio for the baked-tile quadtree selector's frustum cull. 0 until the first
    /// frame is drawn.</summary>
    public int SurfacePixelWidth => lastSurfacePixelWidth;

    // Touch target radius in device-independent units; scaled to surface pixels at hit-test time so a
    // finger tap near a small marker glyph still selects it.
    private const float MarkerTapRadiusDiu = 26f;

    /// <summary>
    /// Raised when the user taps a POI or climbing marker in the 3D view. The argument is ready-to-show
    /// popup content (title + localized detail lines) for the front-most marker under the tap.
    /// </summary>
    public event EventHandler<MarkerPopupContent>? MarkerTapped;

    private double lastOrbitTotalX;
    private double lastOrbitTotalY;
    private double lastTranslateTotalX;
    private double lastTranslateTotalY;
    // Only used on non-Android platforms where Scale is cumulative — Android reads e.Scale as a
    // per-update delta directly. The pragma silences the analyzer "assigned but never used" on the
    // ANDROID partial build where the #else branch is excluded.
#pragma warning disable CS0414, IDE0044
    private double lastPinchScale = 1.0;
#pragma warning restore CS0414, IDE0044

    // Set when OrthoTexturePath changes; the GPU render path decodes the image and hands it to the GL
    // renderer once (off the per-frame path). Lives outside #if WINDOWS because the bindable setter does;
    // non-Windows builds carry the field for parity but never read it (CPU Skia path has no GL ortho).
#pragma warning disable CS0414 // assigned but never used — read only inside #if WINDOWS
    private bool orthoPathDirty;
#pragma warning restore CS0414

    // Continuous-animation tick for the live atmosphere (drifting clouds / wind). The GL terrain
    // only repaints on demand (gesture / property change), but the weather model evolves off the
    // wall-clock, so without a heartbeat the clouds would freeze whenever the user stops touching
    // the screen. ~15 fps is plenty for slow cloud motion and keeps the GPU/thermals modest; the
    // tick only invalidates while the 3D view is actually visible and an atmosphere is bound.
    private readonly IDispatcherTimer animationTimer;

    public Terrain3DView()
    {
        InitializeComponent();
        userLocationProjector = new Marker3DOverlayProjector<UserLocation, ProjectedUserLocation>(
            (fixes, raster, mesh, lift, _) => UserLocation3DProjection.ToWorld(fixes, raster, mesh, lift, BakedElevationIndex),
            (source, screen) => new ProjectedUserLocation(source, screen));
        controller = new Terrain3DController(Camera);
        // 100 ns Stopwatch ticks → microseconds for frame presentation timestamps.
        videoRecorder = new FlythroughRecorder(VideoRecorderFactory.Create(), () => recordClock.Elapsed.Ticks / 10L);
#if WINDOWS
        Canvas.HandlerChanged += OnCanvasHandlerChanged;
#endif

        animationTimer = Dispatcher.CreateTimer();
        animationTimer.Interval = TimeSpan.FromMilliseconds(66); // ~15 fps
        animationTimer.Tick += OnAnimationTick;
        animationTimer.Start();

        // Camera-state autosave: a low-frequency diff against the last serialized camera. Captures
        // any camera change (gesture, gizmo, button, keyboard) without scattering save calls across
        // every input handler. Only writes when the serialized state actually changed.
        cameraSaveTimer = Dispatcher.CreateTimer();
        cameraSaveTimer.Interval = TimeSpan.FromMilliseconds(1200);
        cameraSaveTimer.Tick += OnCameraSaveTick;
        cameraSaveTimer.Start();
    }

    private readonly IDispatcherTimer cameraSaveTimer;
    private string? lastSavedCameraSerialized;

    private void OnCameraSaveTick(object? sender, EventArgs e)
    {
        if (WorldFrame is not { } frame)
        {
            return;
        }
        string serialized = SerializeCamera(frame);
        if (serialized != lastSavedCameraSerialized)
        {
            lastSavedCameraSerialized = serialized;
            CameraState = serialized; // flows to the view-model → settings store

            // LOD Krok 4: report a snapshot of the camera pose so the host can raycast the look-at point and
            // stream the detail patch to the gaze. Snapshot (not the live camera) so the async reload reads a
            // stable pose. The timer debounces; the host gates the actual reload on look-at drift.
            if (DetailStreamingEnabled)
            {
                var pose = new Camera3D
                {
                    Target = Camera.Target,
                    Distance = Camera.Distance,
                    AzimuthRadians = Camera.AzimuthRadians,
                    PitchRadians = Camera.PitchRadians,
                    FieldOfViewYRadians = Camera.FieldOfViewYRadians,
                    NearPlane = Camera.NearPlane,
                    FarPlane = Camera.FarPlane,
                };
                CameraFocusMoved?.Invoke(this, pose);
            }
        }
    }

    // DEM identity key: rounded bounds, so a restored camera is only applied to the same region.
    private static string DemKey(MapBounds b) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{b.SouthWest.Latitude:F3},{b.SouthWest.Longitude:F3},{b.NorthEast.Latitude:F3},{b.NorthEast.Longitude:F3}");

    private string SerializeCamera(TerrainMesh3D frame) => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{DemKey(frame.Bounds)};{Camera.Target.X:R};{Camera.Target.Y:R};{Camera.Target.Z:R};{Camera.Distance:R};{Camera.AzimuthRadians:R};{Camera.PitchRadians:R}");

    // DEBUG (roughness-LOD tuning): when non-empty, the scene comes up at EXACTLY this camera instead of
    // auto-framing or restoring, so every redeploy reproduces one viewpoint for A/B comparison. Capture the
    // 6 numbers from the "LOD camera:" log line. Format: "TargetX;TargetY;TargetZ;Distance;Azimuth;Pitch".
    private const string DebugPinnedCamera = "";

    // Applies the debug pinned camera verbatim (no pitch clamp — reproduce the exact pose). Returns false when
    // unset / unparseable so the caller falls back to restore/auto-frame.
    private bool TryApplyPinnedCamera()
    {
        if (string.IsNullOrEmpty(DebugPinnedCamera))
        {
            return false;
        }

        string[] parts = DebugPinnedCamera.Split(';');
        if (parts.Length != 6)
        {
            return false;
        }

        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (float.TryParse(parts[0], System.Globalization.NumberStyles.Float, ci, out float tx)
            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, ci, out float ty)
            && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, ci, out float tz)
            && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, ci, out float dist)
            && float.TryParse(parts[4], System.Globalization.NumberStyles.Float, ci, out float az)
            && float.TryParse(parts[5], System.Globalization.NumberStyles.Float, ci, out float pitch))
        {
            Camera.Target = new Vector3(tx, ty, tz);
            Camera.Distance = dist;
            Camera.AzimuthRadians = az;
            Camera.PitchRadians = pitch;
            return true;
        }

        return false;
    }

    // Applies a saved camera string IF its DEM key matches the current frame. Returns false (leaving
    // the camera untouched) on any mismatch / parse failure so the caller can auto-frame instead.
    private bool TryRestoreCamera(TerrainMesh3D frame)
    {
        if (string.IsNullOrEmpty(CameraState))
        {
            return false;
        }
        string[] parts = CameraState.Split(';');
        if (parts.Length != 7 || parts[0] != DemKey(frame.Bounds))
        {
            return false;
        }
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (float.TryParse(parts[1], System.Globalization.NumberStyles.Float, ci, out float tx)
            && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, ci, out float ty)
            && float.TryParse(parts[3], System.Globalization.NumberStyles.Float, ci, out float tz)
            && float.TryParse(parts[4], System.Globalization.NumberStyles.Float, ci, out float dist)
            && float.TryParse(parts[5], System.Globalization.NumberStyles.Float, ci, out float az)
            && float.TryParse(parts[6], System.Globalization.NumberStyles.Float, ci, out float pitch))
        {
            Camera.Target = new Vector3(tx, ty, tz);
            Camera.Distance = dist;
            Camera.AzimuthRadians = az;
            // Clamp the restored PITCH into the downward orbit range. A saved look-around pose can leave
            // pitch pointing up at the sky, so the camera would restore into a grey void with no terrain in
            // view (the recurring "szara pustka na starcie"). Forcing it back to [MinPitch, ~89°] means
            // every launch lands looking DOWN at the terrain. (Azimuth/target/distance are kept; the
            // per-frame ClampToBounds then pins the eye over the map at a sane altitude.)
            float maxPitch = (MathF.PI / 2f) - 0.02f;
            Camera.PitchRadians = Math.Clamp(pitch, controller.MinPitchRadians, maxPitch);
            lastSavedCameraSerialized = CameraState; // avoid an immediate redundant re-save
            return true;
        }
        return false;
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        // Repaint only when the 3D view is on screen, there's live atmosphere to animate, AND animated effects
        // are enabled. With effects OFF the view stops auto-redrawing while the camera is still, so the GPU
        // idles instead of rendering ~15 heavy fps forever — the main battery saver on mobile. Gestures and
        // data changes still trigger their own InvalidateSurface, so the map stays responsive.
        if (IsVisible && Atmosphere is not null && AtmosphereEffectsEnabled)
        {
            Canvas.InvalidateSurface();
        }
    }

    /// <summary>
    /// Applies a multiplicative zoom (scale &gt; 1 zooms in, &lt; 1 zooms out)
    /// and re-renders. Public so the host page can hook keyboard or mouse-wheel input.
    /// </summary>
    public void Zoom(float scale)
    {
        controller.ApplyZoom(scale);
        Canvas.InvalidateSurface();
    }

    // Per-click steps for the on-screen control pads, sized so one tap produces a clearly
    // visible move. Pixel-equivalents feed the same controller methods the gestures use
    // (OrbitSensitivity 0.005 rad/px → 28 px ≈ 8°).
    // Per-tap step sizes, kept deliberately SMALL so each repeat tick (the buttons hold-to-repeat
    // at ~25 Hz) moves only a little — the camera glides smoothly while held and a single tap is a
    // fine nudge for precise framing, rather than a coarse jump. Pan / vertical / zoom / tilt are
    // halved from the originals (pan/vertical 22, zoom 1.08, tilt 5) to slow them down at the same
    // 25 Hz cadence; ORBIT is kept fast (a touch above the original 14) per the "make turning faster".
    private const float ButtonOrbitStep = 16f;
    private const float ButtonPanStep = 11f;
    private const float ButtonVerticalStep = 11f;
    private const float ButtonZoomFactor = 1.04f;

    // Tilt + slow-rotate steps are smaller still for the finest control of pitch and heading while
    // looking around the sky / ridgeline.
    private const float ButtonTiltStep = 2.5f;

    // Hold-to-repeat for the on-screen camera pad. A tap fires the action once (immediately on
    // press); holding the button down repeats it at a fixed cadence so the camera glides smoothly
    // instead of needing rapid tapping. The repeat timer only runs while a button is held and
    // re-invokes the last-pressed action; Released (finger lifts / pointer leaves) stops it.
    private Action? heldAction;
    private IDispatcherTimer? holdTimer;

    private void StartHold(Action mutate)
    {
        StopFlight(); // any manual camera control cancels an in-progress fly-through
        mutate();
        Canvas.InvalidateSurface();
        heldAction = mutate;
        if (holdTimer is null)
        {
            holdTimer = Dispatcher.CreateTimer();
            // ~25 Hz: small steps at this cadence read as smooth continuous motion, while a quick
            // tap (press+release inside one interval) still only applies the single immediate step.
            holdTimer.Interval = TimeSpan.FromMilliseconds(40);
            holdTimer.Tick += OnHoldTick;
        }
        holdTimer.Start();
    }

    private void OnHoldTick(object? sender, EventArgs e)
    {
        if (heldAction is null)
        {
            holdTimer?.Stop();
            return;
        }
        heldAction();
        Canvas.InvalidateSurface();
    }

    // Shared Released handler for every pad button: stop repeating when the finger lifts.
    private void OnPadReleased(object? sender, EventArgs e)
    {
        holdTimer?.Stop();
        heldAction = null;
    }

    // Slow-rotate step is ~⅓ of the full button-orbit step so the dedicated arrow-pad rotate
    // buttons feel like a deliberate fine adjustment, not a swipe. ApplyLookAround (in-place
    // rotation, same as the gizmo and 1-finger drag) per user spec: rotation must NEVER also
    // translate the camera.
    private const float SlowRotateStep = 5f;

    private void OnRotateLeftSlowClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyLookAround(-SlowRotateStep, 0f));

    private void OnRotateRightSlowClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyLookAround(SlowRotateStep, 0f));

    private void OnRotateLeftClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyOrbit(-ButtonOrbitStep, 0f));

    private void OnRotateRightClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyOrbit(ButtonOrbitStep, 0f));

    // View-pitch tilt = IN-PLACE rotation (ApplyLookAround), not an orbit: the camera POSITION
    // stays put and only the view direction rotates — "turn your head", the same widget the orbit
    // gizmo uses. ApplyOrbit was wrong here; it circled the camera around the target, sliding it
    // through space, which is exactly what the user said felt broken.
    //  • Look up toward the sky = tilt the gaze up (negative pitch step).
    //  • Look down at the terrain = tilt the gaze down (positive pitch step).
    // ApplyLookAround clamps to LookAroundMinPitchRadians..MaxPitch — wide enough to look well
    // above the horizon at the sky/clouds without the camera ever moving.
    private void OnLookUpClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyLookAround(0f, -ButtonTiltStep));

    private void OnLookDownClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyLookAround(0f, ButtonTiltStep));

    // Pan ▲ moves the focus forward (into the scene), ▼ pulls it back toward the camera.
    private void OnPanUpClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyPan(0f, ButtonPanStep));

    private void OnPanDownClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyPan(0f, -ButtonPanStep));

    private void OnPanLeftClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyPan(-ButtonPanStep, 0f));

    private void OnPanRightClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyPan(ButtonPanStep, 0f));

    private void OnZoomInClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyZoom(ButtonZoomFactor));

    private void OnZoomOutClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyZoom(1f / ButtonZoomFactor));

    // Wys. ▲ / ▼ buttons now move the camera target up/down in world-Z (vertical translation),
    // regardless of camera pitch. The earlier tilt mapping was confusing — users expect "up"
    // to lift the camera straight up. ApplyVertical clamps Target.Z to [-2000, 8000] m so a
    // runaway click can't push the target off the mesh and turn the view into pure sky.
    private void OnRaiseClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyVertical(ButtonVerticalStep));

    private void OnLowerClicked(object? sender, EventArgs e) => StartHold(() => controller.ApplyVertical(-ButtonVerticalStep));

    /// <summary>
    /// Points the camera at a world-space target from a given distance, preserving the current
    /// orbit angle (azimuth/pitch). Used to keep the 3D view framed on the same place the 2D map
    /// was centred on when switching into 3D. Distance is clamped to the controller's zoom range.
    /// </summary>
    /// <param name="target">World-space focal point (X east, Y north, Z up).</param>
    /// <param name="distance">Desired camera distance in metres; clamped to the valid zoom range.</param>
    public void FocusOnWorld(Vector3 target, float distance)
    {
        Camera.Target = target;
        Camera.Distance = Math.Clamp(distance, controller.MinDistance, controller.MaxDistance);
        controller.ClampToBounds(); // keep the eye over the map even if the 2D map was centred off the DEM
        Canvas.InvalidateSurface();
    }

    /// <summary>
    /// Centres the camera on a geographic point (seated on the terrain), at a close framing distance.
    /// Used to fly to the first route stop when the user finishes planning.
    /// </summary>
    public void FocusOnGeo(GeoPoint geo, float distance = 4000f)
    {
        if (WorldFrame is not { } frame)
        {
            return;
        }

        float elevation = Raster is { } raster ? (float)raster.SampleBilinear(geo.Longitude, geo.Latitude) : 0f;
        FocusOnWorld(frame.GeoToWorld(geo, elevation), distance);
    }

    // ── Cinematic fly-through: Orla Perć ────────────────────────────────────────────────────────
    // A scripted Orla Perć round trip — Kasprowy Wierch → the Orla Perć ridge (Zawrat → Krzyżne) → back to
    // Kasprowy — with the time-of-day racing a FULL 24 h cycle in the ~10 s flight, so the day turns to night
    // (stars + Moon flash by at the Krzyżne turnaround) and back again. Geo waypoints are sampled against the
    // live DEM; a Catmull-Rom spline + the camera low-pass smooth the path.
    private static readonly (double Lat, double Lon)[] OrlaPercWaypoints =
    {
        (49.2317, 19.9817), // Kasprowy Wierch (start)
        (49.2290, 20.0050), // Świnica (2301 m)
        (49.2193, 20.0179), // Zawrat (pass — onto the Orla Perć)
        (49.2235, 20.0337), // Kozie Czuby (mid Orla Perć)
        (49.2270, 20.0506), // Buczynowe Turnie
        (49.2283, 20.0586), // Krzyżne (turnaround — the ridge's east end)
        (49.2249, 20.0389), // Zadni Granat (heading back west)
        (49.2205, 20.0233), // Mały Kozi Wierch
        (49.2290, 20.0050), // Świnica
        (49.2317, 19.9817), // Kasprowy Wierch (return — flight end)
    };

    // Real-metre clearance the LOCAL camera floor keeps the eye above the terrain directly beneath it: the
    // eye auto-lifts to stay ~this far over the ground as you fly. Added inside the vertical exaggeration so
    // it stays a true value at any Pion setting. 5 m = skim just above the surface (immersive low fly-over).
    private const double CameraClearanceMeters = 5.0;

    // Hard altitude ceiling (metres above sea level) the camera EYE can rise to. Multiplied by the
    // exaggeration to world-Z, so it is a fixed real altitude at any Pion. The camera cannot ascend
    // above this (raise / zoom-out is capped), keeping the view over the terrain rather than in space.
    private const double CameraCeilingMeters = 8_000.0;

    private const double FlightDurationSeconds = 89.0; // Orla Perć round trip — a full 24 h day↔night cycle plays out slowly over this window
    private const float RouteFilmDurationSeconds = 150f; // user route film: SLOWER than the demo so the 1 m detail streamer keeps up with the camera (less of the film on the coarse base)
    private const double FlightStartPauseSeconds = 14.0; // hold at the start (gaze tilted down, camera close) so the 1 m detail fully streams in before the camera moves
    private const float FlightSlalomAmplitude = 950f;  // world-metres of side-to-side weave (large so it reads at the stand-off distance)
    private const float FlightSlalomWeaves = 2.0f;     // fewer left-right swings — calmer rotation (was 3, read as too jerky)
    private const float FlightCameraHeight = 700f;     // world-Z above the ridge — low + immersive (was 2600, framed too much terrain → stutter); still clears the peaks at the closer stand-off
    private const float FlightCameraBack = 1600f;      // closer stand-off (was 2600) so peaks read bigger, while still framing the ridge ahead

    // Narrower field of view while flying (≈34° vs the default 45°): a gentle telephoto so the peaks fill
    // more of the frame ("zbyt szeroko, bliżej"). Restored to whatever the camera had when the flight ends.
    private const float FlightFieldOfViewYRadians = 0.60f;

    // Drop the look-at point this many world-metres below the ridge so the camera's gaze tilts DOWN onto the
    // near terrain. The 1 m detail streams toward whatever the gaze hits, so a too-high gaze leaves the
    // foreground un-detailed ("detale nie wskakują bo za wysoko patrzy kamera").
    private const float FlightLookDownMeters = 350f;

    // Night finale: how high (world-metres) to lift the look-at in the last stretch so the camera tilts UP off
    // the ridge into the night sky — the brief night exists just to sweep across the Big Dipper + Moon.
    private const float FlightSkyRevealMeters = 4500f;

    // Tighter depth range JUST for the flight. The default 10 m → 1 000 000 m (ratio 1:100 000) wrecks Z-buffer
    // precision, so distant lake-water planes and depth-tested cloud billboards z-fight and SHIMMER as the
    // camera moves. The flight camera sits ~700 m above the ridge (nothing is closer), so a 150 m near + 100 km
    // far won't clip anything and lifts depth precision ~150× — kills the flicker. Restored on stop.
    private const float FlightNearPlane = 150f;
    private const float FlightFarPlane = 100_000f;
    private const double FlightCancelDragPx = 30.0;    // cumulative drag (px) before a touch cancels the fly-through

    // Time-of-day arc (flight progress → hour): a VERY long red morning, the largest stretch in full day, a
    // sizable golden evening, then only a BRIEF night at the very end — just enough to whip the camera up to
    // the Big Dipper + Moon for the finale.
    private static readonly (float P, float Hour)[] FlightTimeKeys =
    {
        (0.00f, 5.0f),   // dawn — start in the pre-sunrise red
        (0.28f, 8.5f),   // … a VERY long morning lingered in sunrise / red light (~28 % of the flight)
        (0.68f, 16.0f),  // … the largest stretch in full day (~40 %)
        (0.90f, 19.0f),  // … a sizable golden evening (~22 %)
        (1.00f, 21.8f),  // … a brief night (~10 %) for the sky reveal — stars, Big Dipper, Moon
    };

    // Piecewise-linear interpolation of FlightTimeKeys at flight progress p∈[0,1].
    private static float FlightTimeOfDay(float p)
    {
        for (int i = 1; i < FlightTimeKeys.Length; i++)
        {
            if (p <= FlightTimeKeys[i].P)
            {
                (float p0, float h0) = FlightTimeKeys[i - 1];
                (float p1, float h1) = FlightTimeKeys[i];
                float t = (p - p0) / MathF.Max(1e-4f, p1 - p0);
                return h0 + ((h1 - h0) * t);
            }
        }
        return FlightTimeKeys[^1].Hour;
    }

    private IDispatcherTimer? flightTimer;
    private Vector3[]? flightPath;
    private double flightElapsedSeconds;
    private readonly System.Diagnostics.Stopwatch flightClock = new(); // real-time clock so the flight honours FlightDurationSeconds regardless of how fast the dispatcher timer actually ticks
    private int flightDetailTick; // drives 1 m detail streaming directly during the flight (incl. the static start pause, which the camera-save timer skips)
    private bool flightActive;
    // Frame-to-frame low-pass of the fly-through camera so abrupt heading changes ease out (see OnFlightTick).
    private Vector3 flightSmoothPos;
    private Vector3 flightSmoothLook;
    private float flightSmoothDescend; // 0 = climbing/flat (camera behind), 1 = descending (camera in front)
    private bool flightSmoothInit;
    private Vector3? flightMarkerWorld; // current point on the route during a film — drawn as a moving dot
    // Route-film start gate: instead of a FIXED start pause, the dot holds at the route start until the 1 m
    // detail covering that start point has actually built (a new DetailElevation that covers it arrives), then
    // starts moving. A safety cap opens the gate anyway so the film can never hang (e.g. streaming off / offline).
    private bool flightBuildGated;       // this flight waits for the start-area build before the dot moves
    private bool flightGateOpen;         // gate satisfied — the moving clock has started
    private GeoPoint flightStartGeo;     // flight start lon/lat the build must cover before the dot moves
    private readonly System.Diagnostics.Stopwatch flightMovingClock = new(); // moving-time; starts when the gate opens, then runs steadily (detail streams live during the film)
    private const double RouteFilmMaxBuildWaitSeconds = 30.0; // safety cap: open the gate regardless after this long
    private float flightSavedFov; // camera FOV before the flight narrowed it (restored on stop)
    private float flightSavedNear; // camera near/far before the flight tightened them for depth precision (restored on stop)
    private float flightSavedFar;
    // Atmosphere snapshot at flight start + the live, time-swept atmosphere used while flying so the
    // sun visibly lowers into golden hour over the course of the flight.
    private float flightBaseCloud;
    private float flightBaseWind;
    private float flightBaseSnow;
    private Vector3? flightBaseSun; // sun at film start — pins the snow line so the cover holds while time sweeps
    // Route-film progress timeline: maps elapsed moving-seconds → path progress, inserting a 3 s HOLD at each
    // user route stop so the film lingers there. Null (built-in demo) = plain constant-speed linear progress.
    private (float Time, float Progress)[]? flightProgressKeys;
    private float flightTotalMovingSeconds = (float)FlightDurationSeconds;
    private const float FlightStopHoldSeconds = 3f;
    private const float RouteFilmInitialHoldSeconds = 5f; // sit at the start so the opening 1 m detail finishes building before the camera moves
    private float flightBaseStorm;
    private Atmosphere? flightAtmosphere;

    // In-app MP4 recording of the cinematic fly-through. The encoder is created per platform (Android:
    // MediaCodec; elsewhere a no-op reporting unsupported). recordClock drives presentation timestamps in
    // microseconds (a Stopwatch tick is 100 ns). Recording is requested when the flight starts and lazily
    // begins on the next paint (when the surface pixel size is known); it's finalized when the flight ends.
    private readonly System.Diagnostics.Stopwatch recordClock = System.Diagnostics.Stopwatch.StartNew();
    private readonly FlythroughRecorder? videoRecorder;
    private bool recordingRequested;
    private byte[]? recordBuffer;

    /// <summary>Raised after a fly-through recording is finalized, with the saved MP4 file path.</summary>
    public event EventHandler<string>? RecordingSaved;

    /// <summary>Raised when any fly-through ends, so the host can release route-film pre-cache state.</summary>
    public event EventHandler? FlightEnded;

    // The atmosphere the renderer should use: the time-swept flight atmosphere while flying, else the
    // bound (slider-driven) one.
    private Atmosphere? EffectiveAtmosphere => flightActive && flightAtmosphere is not null ? flightAtmosphere : Atmosphere;

    /// <summary>Starts the scripted Orla Perć fly-through. No-op until a DEM + raster are loaded.</summary>
    public void StartOrlaPercFlight()
    {
        if (WorldFrame is not { } frame || Raster is null)
        {
            return;
        }

        var pts = new Vector3[OrlaPercWaypoints.Length];
        for (int i = 0; i < OrlaPercWaypoints.Length; i++)
        {
            (double lat, double lon) = OrlaPercWaypoints[i];
            double elev = Raster.SampleBilinear(lon, lat);
            if (double.IsNaN(elev) || elev < 200 || elev > 4000)
            {
                elev = 2000; // fall back if the waypoint lands on a no-data cell
            }
            pts[i] = frame.GeoToWorld(new GeoPoint(lat, lon), (float)elev);
        }

        flightProgressKeys = null; // built-in demo: plain linear progress, no stop holds
        flightTotalMovingSeconds = (float)FlightDurationSeconds;
        flightBuildGated = false; // demo keeps its fixed FlightStartPauseSeconds eye-hold
        flightGateOpen = true;
        BeginFlight(pts);
    }

    /// <summary>Starts a cinematic fly-through ALONG the planned tourist route — and records it to MP4 — so the
    /// user can film their route. Holds 3 s at each <paramref name="stops"/> the user entered. No-op until a DEM
    /// + raster + a planned route exist.</summary>
    /// <param name="stops">The user's ordered route stops; the film pauses at each. Null = no holds.</param>
    /// <param name="detailPrebuilt">True when the host has already built + retained the whole route corridor's
    /// 1 m detail (and suppressed per-move rebuilds): skip the start-build gate and start moving immediately.</param>
    public void StartRouteFlight(IReadOnlyList<GeoPoint>? stops = null, bool detailPrebuilt = false)
    {
        if (WorldFrame is not { } frame || Raster is null || Route is null)
        {
            return;
        }

        // The dot/camera track must be the SAME line the route renderer draws — conflated onto the trail
        // geometry it follows and densified — NOT the planner's raw polyline. The raw polyline runs beside
        // the drawn (conflated) line, so the dot visibly travelled "its own track next to the route".
        // Densify also equalises vertex spacing, which SampleFlightPath's index-based interpolation reads
        // as constant ground speed. Heights: real baked z16 first (what's actually rendered), base fallback.
        IReadOnlyList<GeoPoint> rawPoly = Route.ToPolyline();
        if (rawPoly.Count < 2)
        {
            return;
        }

        IReadOnlyList<GeoPoint> source = Trails is { Count: > 0 } trailsForConflation
            ? MapaTur.Application.Terrain.RouteTrailConflation.Conflate(rawPoly, trailsForConflation)
            : rawPoly;
        IReadOnlyList<GeoPoint> poly = MapaTur.Application.Terrain.GeoPolylineDensifier.Densify(source, 5.0);
        if (poly.Count < 2)
        {
            return;
        }

        var bakedCache = new Dictionary<(int Zoom, int X, int Y), MapaTur.Domain.Terrain.DemRaster?>();
        var pts = new Vector3[poly.Count];
        for (int i = 0; i < poly.Count; i++)
        {
            double elev;
            if (BakedElevationIndex is { } bakedIdx
                && MapaTur.Application.Terrain.Trail3DWorldProjection.TryGetBakedElevation(bakedIdx, bakedCache, poly[i], out double bakedElev))
            {
                elev = bakedElev;
            }
            else
            {
                elev = Raster.SampleBilinear(poly[i].Longitude, poly[i].Latitude);
            }

            if (double.IsNaN(elev) || elev < 200 || elev > 4000)
            {
                elev = 2000;
            }

            pts[i] = frame.GeoToWorld(poly[i], (float)elev);
        }

        // Gate the dot on the START-area 1 m build (not a fixed time) when streaming can actually deliver it.
        // When the host has already PRE-BUILT the whole route corridor (route film), there is nothing left to
        // wait for — open immediately so the camera flies straight over the pre-built detail (no spurious hold).
        flightStartGeo = poly[0];
        flightBuildGated = DetailStreamingEnabled && !detailPrebuilt;
        flightGateOpen = !flightBuildGated;
        // The gate replaces the fixed start hold, so drop the leading hold from the timeline when gating.
        BuildRouteFilmTimeline(poly, stops, flightBuildGated ? 0f : RouteFilmInitialHoldSeconds);
        BeginFlight(pts);

        // If the start is ALREADY covered by the current detail (a recent build), open the gate now rather than
        // waiting for a fresh field — the VM's reload cooldown could otherwise suppress one until the safety cap.
        if (flightBuildGated && DetailElevation is { } current)
        {
            OnFlightDetailArrived(current);
        }
    }

    // Builds the progress timeline that inserts a 3 s hold at each user stop. Each stop maps to its nearest
    // route-polyline vertex (the legs join there) → a progress fraction; between stops the film advances at
    // constant speed (the whole route over FlightDurationSeconds), then lingers FlightStopHoldSeconds.
    private void BuildRouteFilmTimeline(IReadOnlyList<GeoPoint> poly, IReadOnlyList<GeoPoint>? stops, float initialHoldSeconds)
    {
        if (stops is null || stops.Count == 0 || poly.Count < 2)
        {
            // No intermediate stops: hold at the start for initialHoldSeconds (0 when the build gate handles it),
            // then run the route.
            flightProgressKeys = new[]
            {
                (0f, 0f),
                (initialHoldSeconds, 0f),
                (initialHoldSeconds + RouteFilmDurationSeconds, 1f),
            };
            flightTotalMovingSeconds = initialHoldSeconds + RouteFilmDurationSeconds;
            return;
        }

        var pauses = new List<float>();
        foreach (GeoPoint stop in stops)
        {
            int best = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < poly.Count; i++)
            {
                double d = poly[i].HaversineDistanceMetersTo(stop);
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }

            float prog = best / (float)(poly.Count - 1);
            if (prog is > 0.002f and < 0.998f)
            {
                pauses.Add(prog); // skip the very start/end: the start already pauses, the end stops the film
            }
        }

        pauses.Sort();

        // Hold at the start for initialHoldSeconds (0 when the build gate replaces it) before the camera moves.
        var keys = new List<(float Time, float Progress)> { (0f, 0f), (initialHoldSeconds, 0f) };
        float t = initialHoldSeconds, prevP = 0f;
        foreach (float q in pauses)
        {
            if (q <= prevP + 0.0005f)
            {
                continue; // de-dupe coincident stops
            }

            t += (q - prevP) * RouteFilmDurationSeconds; // travel to the stop (slower than the demo)
            keys.Add((t, q));
            t += FlightStopHoldSeconds;                  // hold there
            keys.Add((t, q));
            prevP = q;
        }

        t += (1f - prevP) * RouteFilmDurationSeconds; // run out to the end
        keys.Add((t, 1f));
        flightProgressKeys = keys.ToArray();
        flightTotalMovingSeconds = t;
    }

    // Maps elapsed moving-seconds to path progress via the route-film timeline (constant speed + 3 s holds at
    // stops). Falls back to plain linear progress when there's no timeline (the built-in demo).
    private float ProgressAtMovingTime(float seconds)
    {
        var keys = flightProgressKeys;
        if (keys is null || keys.Length < 2)
        {
            return Math.Clamp(seconds / (float)FlightDurationSeconds, 0f, 1f);
        }

        if (seconds <= 0f)
        {
            return 0f;
        }

        for (int i = 1; i < keys.Length; i++)
        {
            if (seconds <= keys[i].Time)
            {
                (float t0, float p0) = keys[i - 1];
                (float t1, float p1) = keys[i];
                float f = t1 > t0 ? (seconds - t0) / (t1 - t0) : 0f;
                return p0 + ((p1 - p0) * f);
            }
        }

        return 1f;
    }

    // Shared cinematic-flight start (Orla Perć demo OR the planned route): adopt the world path, reset the
    // flight clocks + camera (telephoto FOV, tightened depth), sweep the day arc, hide the chrome + request the
    // MP4 capture, then run the ~30 Hz tick.
    private void BeginFlight(Vector3[] pts)
    {
        flightPath = pts;
        flightElapsedSeconds = 0;
        flightClock.Restart();
        flightMovingClock.Reset(); // starts only when the build gate opens (route film); demo ignores it
        flightDetailTick = 0;
        flightActive = true;
        flightSmoothInit = false; // snap the low-pass to the first frame
        flightSavedFov = Camera.FieldOfViewYRadians;
        Camera.FieldOfViewYRadians = FlightFieldOfViewYRadians; // gentle telephoto for the flight
        flightSavedNear = Camera.NearPlane;
        flightSavedFar = Camera.FarPlane;
        Camera.NearPlane = FlightNearPlane; // tighten the depth range so distant water/clouds stop z-fighting
        Camera.FarPlane = FlightFarPlane;
        IsFlying = true;
        // Cinematic time arc independent of the slider (the non-linear FlightTimeKeys day arc); cloud + wind
        // + snow come from the user's settings (snow was previously dropped to 0 for the flight — the demo
        // melted the snow the user had set; keep it).
        Atmosphere? a = Atmosphere;
        flightBaseCloud = a?.CloudCoverage ?? 0.35f;
        flightBaseWind = a?.Wind ?? 0.3f;
        flightBaseSnow = a?.SnowAmount ?? 0f;
        flightBaseStorm = a?.Storm ?? 0f;
        flightBaseSun = a?.SunDirection; // hold the snow cover at the pre-film sun while the time arc sweeps
        flightAtmosphere = new Atmosphere(FlightTimeOfDay(0f), flightBaseCloud, flightBaseWind, flightBaseSnow, flightBaseStorm);
        SetChromeVisible(false); // clear the screen for a clean cinematic shot
        // Request an MP4 capture of the flight; it starts on the next paint once the surface size is known.
        recordingRequested = videoRecorder?.IsSupported ?? false;

        if (flightTimer is null)
        {
            flightTimer = Dispatcher.CreateTimer();
            flightTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30 fps
            flightTimer.Tick += OnFlightTick;
        }

        flightTimer.Start();
    }

    private void StopFlight()
    {
        bool wasFlying = flightActive || IsFlying;
        flightActive = false;
        flightMarkerWorld = null;
        flightTimer?.Stop();

        // Finalize the MP4 (if one was being captured) and surface its path to the host page.
        recordingRequested = false;
        if (videoRecorder is { IsRecording: true })
        {
            string? saved = videoRecorder.Stop();
            if (!string.IsNullOrEmpty(saved))
            {
                RecordingSaved?.Invoke(this, saved);
            }
        }

        if (wasFlying)
        {
            IsFlying = false;
            SetChromeVisible(true);
            Camera.FieldOfViewYRadians = flightSavedFov; // restore the pre-flight FOV
            Camera.NearPlane = flightSavedNear;
            Camera.FarPlane = flightSavedFar;
            FlightEnded?.Invoke(this, EventArgs.Empty); // host releases route-film pre-cache + resumes streaming
        }
    }

    // Show/hide the view's own on-screen chrome (altitude pad, pan/tilt pad) so a fly-through fills
    // the screen. The host page hides its toolbar + sliders off the IsFlying bind.
    private void SetChromeVisible(bool visible)
    {
        AltitudePad.IsVisible = visible;
        PanTiltPad.IsVisible = visible;
    }

    // Opens the route-film start gate: the start-area detail has built (or the safety cap elapsed), so start the
    // moving clock and let the dot ride the route from the start.
    private void OpenFlightGate()
    {
        if (flightGateOpen)
        {
            return;
        }

        flightGateOpen = true;
        flightMovingClock.Restart();
    }

    // Called when a new 1 m detail field arrives. If a route film is holding at the start, open the gate as soon
    // as the new field covers the start point — that is the "build finished" signal the dot waits on (instead of
    // a fixed timer).
    private void OnFlightDetailArrived(DetailElevationField field)
    {
        if (!flightActive || !flightBuildGated || flightGateOpen)
        {
            return;
        }

        if (field.TryGetElevation(flightStartGeo.Longitude, flightStartGeo.Latitude, out _))
        {
            OpenFlightGate();
        }
    }

    private void OnFlightTick(object? sender, EventArgs e)
    {
        if (!flightActive || flightPath is null || flightPath.Length < 2)
        {
            StopFlight();
            return;
        }

        flightElapsedSeconds = flightClock.Elapsed.TotalSeconds; // real wall-clock time so the flight lasts exactly FlightDurationSeconds

        double moving;
        bool finished;
        if (flightBuildGated)
        {
            // Route film: hold the dot at the start until the start-area 1 m detail has built (the gate is opened
            // by OnFlightDetailArrived) or the safety cap elapses; only then does the moving clock advance. After
            // that the film LIVE-STREAMS detail and advances STEADILY — we no longer pause the camera per build.
            // The old "hold while IsLodDetailBuilding" gate made the film stand still more than it moved; with
            // re-centers now cheap (decoded-tile + roughness caches), the flight aims the build ahead of the camera
            // and the detail streams in as we go, so a steady advance reads as a continuous fly-through.
            if (!flightGateOpen && flightElapsedSeconds >= RouteFilmMaxBuildWaitSeconds)
            {
                OpenFlightGate();
            }

            moving = flightGateOpen ? flightMovingClock.Elapsed.TotalSeconds : 0.0;
            finished = flightGateOpen && moving >= flightTotalMovingSeconds;
        }
        else
        {
            // Demo / non-gated: fixed start pause so the 1 m detail streams in, then advance the path.
            moving = flightElapsedSeconds - FlightStartPauseSeconds;
            finished = moving >= flightTotalMovingSeconds;
        }

        // Constant ground speed, with a 3 s HOLD at each user stop (the route-film timeline). The path point
        // freezes during a hold, so the camera lingers on the stop; the built-in demo has no timeline → plain
        // linear progress. (A smoothstep ease-out once made it crawl to a halt mid-flight; constant speed reads
        // as obviously moving the whole way.)
        float p = finished ? 1f : ProgressAtMovingTime((float)Math.Max(0.0, moving));
        // Time-of-day follows the day arc (long red morning → day → evening → brief night) over the flight.
        flightAtmosphere = new Atmosphere(FlightTimeOfDay(p), flightBaseCloud, flightBaseWind, flightBaseSnow, flightBaseStorm);
        Vector3 here = SampleFlightPath(p);
        Vector3 ahead = SampleFlightPath(MathF.Min(1f, p + 0.025f));
        flightMarkerWorld = here; // the dot rides the route at the point the film is currently passing

        Vector3 tangent = ahead - here;
        tangent.Z = 0f;
        tangent = tangent.LengthSquared() > 1e-4f ? Vector3.Normalize(tangent) : new Vector3(1f, 0f, 0f);
        var perp = new Vector3(-tangent.Y, tangent.X, 0f); // horizontal, perpendicular to the ridge

        float slalom = MathF.Sin(p * MathF.PI * 2f * FlightSlalomWeaves) * FlightSlalomAmplitude;

        // Stand on the DOWN-slope side of the route, looking UP toward the ridge: climb → camera BEHIND, descent
        // → camera IN FRONT (the walker comes toward the lens). Filming a descent from behind (up-slope) just
        // fills the frame with the mountain you're leaving ("góra zasłania"). vClimb<0 = the route drops ahead;
        // ease the front/back swap so undulating ground doesn't whip the camera around.
        float vClimb = ahead.Z - here.Z;
        float descendTarget = Math.Clamp(-vClimb / 120f, 0f, 1f);
        flightSmoothDescend = flightSmoothInit
            ? flightSmoothDescend + ((descendTarget - flightSmoothDescend) * 0.06f)
            : descendTarget;
        float backSign = (flightSmoothDescend * 2f) - 1f; // −1 behind (climb) … +1 in front (descend)
        Vector3 cameraPos = here + (tangent * (FlightCameraBack * backSign)) + (perp * slalom) + new Vector3(0f, 0f, FlightCameraHeight);

        // Gaze toward the UP-slope side so the walker + the ridge above stay framed: ahead when climbing, back
        // toward the descent's headwall when descending.
        float lookAlong = (0.06f * (1f - flightSmoothDescend)) - (0.05f * flightSmoothDescend);
        Vector3 lookAt = SampleFlightPath(Math.Clamp(p + lookAlong, 0f, 1f));
        lookAt.Z -= FlightLookDownMeters; // tilt the gaze down so the 1 m detail streams onto the near terrain, not the far sky

        // Night finale: in the last ~10 % the gaze pitches UP into the sky so the Big Dipper + Moon fill the
        // frame (the day arc puts the brief night here).
        float skyReveal = Math.Clamp((p - 0.90f) / 0.10f, 0f, 1f);
        lookAt.Z += skyReveal * FlightSkyRevealMeters;

        // Low-pass the camera position + look-at frame-to-frame so the sharp path turns (the long E↔W
        // transfers and the slalom) EASE into the rotation instead of whipping the heading around faster
        // than the GPU can stream terrain ("obroty zbyt gwałtowne, fps nie nadąża"). First frame snaps.
        if (!flightSmoothInit)
        {
            flightSmoothPos = cameraPos;
            flightSmoothLook = lookAt;
            flightSmoothInit = true;
        }
        else
        {
            const float follow = 0.10f; // ~0.33 s ease at the 30 Hz tick — gentle, no whip
            flightSmoothPos = Vector3.Lerp(flightSmoothPos, cameraPos, follow);
            flightSmoothLook = Vector3.Lerp(flightSmoothLook, lookAt, follow);
        }

        // Keep the camera ABOVE the terrain under it — never film THROUGH the rock. The camera trails behind
        // the route point (−tangent); descending a slope puts that trailing point UP the face, where the ground
        // is higher than here+height, so the fixed-height camera would punch through the rock (and a "behind"
        // chase shot on a descent looks into the wall). Lift it over whatever ground is directly beneath it.
        flightSmoothPos = LiftCameraAboveTerrain(flightSmoothPos);

        ApplyFreeCamera(flightSmoothPos, flightSmoothLook);

        // Drive the 1 m detail streaming directly. The camera-save timer only raises CameraFocusMoved when the
        // camera CHANGES, so the STATIC start-pause would stream nothing and the flight would set off over
        // un-detailed terrain. Fire ~1×/s ourselves (tick 0 fires immediately, during the pause): the VM gates
        // on look-at drift + cooldown, so a held camera loads exactly one 2 km patch and a moving one keeps the
        // detail patch on the gaze.
        if (DetailStreamingEnabled && flightDetailTick++ % 30 == 0)
        {
            // Stream the 1 m detail to the terrain we're flying TOWARD — the ridge/summit ahead — NOT the
            // cinematic gaze. The display gaze tilts FlightLookDownMeters below the ridge so the foreground
            // streams in, but the streamer raycasts the screen centre and only ever probes LOWER in the frame
            // (LookAtLowerFrameFallbacks), so that down-tilt made the focus land on the slope BELOW an
            // approaching peak: climbing "pod górę" left the summit on the 30 m base ("brakuje detali na
            // szczycie", detail only appeared once the camera rose ABOVE and looked down). Re-aim a
            // streaming-only camera at the ridge point ahead (full elevation) so the patch lands ON the summit
            // before we arrive; its width (∝ camera→focus distance) still reaches back across the foreground.
            Vector3 streamEye = flightSmoothPos;
            // Aim the 1 m detail build well AHEAD of the camera — a patch takes seconds to mesh, so by the time
            // it's ready the camera must be arriving at it, not have flown past (which left the film on the base).
            // While the gate holds at the start, aim the build AT the start so its detail arrives and opens the
            // gate; once moving, aim well ahead (a patch takes seconds to mesh) so it's ready as the camera arrives.
            Vector3 streamFocus = (flightBuildGated && !flightGateOpen)
                ? here
                : SampleFlightPath(MathF.Min(1f, p + 0.14f));
            Vector3 streamOffset = streamEye - streamFocus;
            float streamDist = MathF.Max(1f, streamOffset.Length());
            CameraFocusMoved?.Invoke(this, new Camera3D
            {
                Target = streamFocus,
                Distance = streamDist,
                AzimuthRadians = MathF.Atan2(streamOffset.Y, streamOffset.X),
                PitchRadians = MathF.Asin(Math.Clamp(streamOffset.Z / streamDist, -1f, 1f)),
                FieldOfViewYRadians = Camera.FieldOfViewYRadians,
                NearPlane = Camera.NearPlane,
                FarPlane = Camera.FarPlane,
            });
        }
        Canvas.InvalidateSurface();

        // On reaching the end: cleanly stop AND restore the UI (the old code only flipped
        // flightActive + stopped the timer, leaving IsFlying=true so the chrome stayed hidden and
        // the camera sat frozen — which looked exactly like "the flight died").
        if (finished)
        {
            StopFlight();
        }
    }

    // World-Z the flight camera must clear the ground beneath it by — enough that steep faces behind a
    // descent don't poke into the lens, without yanking the camera miles up on every little rise.
    private const float FlightCameraTerrainClearance = 150f;

    // Raises the camera so it sits at least FlightCameraTerrainClearance above the terrain directly under it
    // (sampled from the base DEM at the camera's lon/lat). Pure clamp: it only ever lifts, never lowers, so a
    // high cinematic vantage is untouched — it just stops the camera burrowing into rock on descents.
    private Vector3 LiftCameraAboveTerrain(Vector3 position)
    {
        if (WorldFrame is not { } frame || Raster is not { } raster)
        {
            return position;
        }

        GeoPoint geo = frame.WorldToGeo(position);
        double terrain = raster.SampleBilinear(geo.Longitude, geo.Latitude);
        if (double.IsNaN(terrain) || terrain < 200 || terrain > 4000)
        {
            return position;
        }

        float minZ = frame.GeoToWorld(geo, (float)terrain).Z + FlightCameraTerrainClearance;
        if (position.Z < minZ)
        {
            position.Z = minZ;
        }

        return position;
    }

    // Places the camera at an exact world position looking at a world point, by converting the
    // pos→target offset into the orbit model (Target + Distance + Azimuth + Pitch) so Camera3D
    // reconstructs the same position. Used by the fly-through for free-camera control.
    private void ApplyFreeCamera(Vector3 position, Vector3 lookAt)
    {
        Vector3 offset = position - lookAt;
        float dist = offset.Length();
        if (dist < 1f)
        {
            dist = 1f;
        }
        Camera.Target = lookAt;
        Camera.Distance = dist;
        Camera.AzimuthRadians = MathF.Atan2(offset.Y, offset.X);
        Camera.PitchRadians = MathF.Asin(Math.Clamp(offset.Z / dist, -1f, 1f));
    }

    // ── WALK MODE (first-person) ─────────────────────────────────────────────────────────────────────────────
    // A ground-clamped first-person camera: WalkPhysics (pure, real-metre gravity/jump/slope-gate) drives a
    // walker over the terrain and this view places the eye on it each tick via ApplyFreeCamera — the same
    // free-camera primitive the fly-through uses. Physics reasons in REAL metres (world XY are unexaggerated,
    // elevation is real); only the eye's world-Z is multiplied by the scene's vertical exaggeration when the
    // camera is placed. Ground is sampled from the true 1 m surface (FineElevationSampler) with the coarse base
    // as fallback. While walk mode is on, OnPaintSurface skips its fly-camera floor/bounds clamp (the walk tick
    // owns the camera; the fly floor would otherwise shove the eye 5 m up off the ground).
    private IDispatcherTimer? walkTimer;
    private readonly System.Diagnostics.Stopwatch walkClock = new();
    private double walkLastSeconds;
    private MapaTur.Application.Terrain.WalkPhysics? walker;
    private bool walkActive;
    private float walkHeadingRadians;   // yaw: the horizontal forward direction the walker faces/moves along
    private float walkLookPitchRadians; // gaze tilt: + looks up, − looks down (movement stays horizontal)
    private bool walkJumpQueued;        // set on a Space press, consumed by the next tick (one jump per press)
    private int walkDetailTick;
    private bool walkSwinging;          // a ciupaga swing (left click) is playing
    private double walkSwingStartSeconds;
    private bool walkLmbDown;           // LEFT mouse held → ciupaga self-arrest (hang) while airborne against rock
    private const float CiupagaSwingSeconds = 0.5f; // one strike + recover
    // Held-movement state, set by the Windows key handlers and polled by the (cross-platform) tick. Plain bools
    // so the tick never references the Windows-only VirtualKey type.
    private bool walkFwd, walkBack, walkStrafeLeft, walkStrafeRight, walkRun;

    private const float WalkMoveSpeedMetersPerSecond = 2.2f;  // ~8 km/h stroll
    private const float WalkRunMultiplier = 2.4f;             // Shift ≈ a 19 km/h jog
    private const float WalkLookDistanceMeters = 250f;        // look-at point ahead of the eye (sets Camera.Distance)
    private const float WalkKeyTurnRadians = 0.045f;          // Q/E yaw + R/F pitch step per key event (OS-repeat = smooth)
    private const float WalkMouseLookRadiansPerPixel = 0.005f; // mouse-drag look sensitivity
    private const float WalkMaxLookPitchRadians = (MathF.PI / 2f) - 0.05f;

    // ── DRAGON FLIGHT (F7) ───────────────────────────────────────────────────────────────────────────────────
    // Ride a dragon over the terrain: DragonFlight (pure arcade physics) drives a chase camera and a big animated
    // Skia dragon drawn from behind. Right-drag steers (yaw + pitch), W/S throttle, A/D bank/turn. The wings flap
    // on a time cycle and the dragon rolls into turns.
    private IDispatcherTimer? dragonTimer;
    private readonly System.Diagnostics.Stopwatch dragonClock = new();
    private double dragonLastSeconds;
    private MapaTur.Application.Terrain.DragonFlight? dragon;
    private bool dragonActive;
    private float dragonFlapPhase;      // wing-flap phase, advanced each tick (faster at speed)
    private int dragonDetailTick;
    private float dragonMouseDx, dragonMouseDy; // right-drag steer accumulated since the last tick
    private bool dragonW, dragonS, dragonA, dragonD;          // WASD: throttle (W/S) + bank (A/D)
    private bool dragonPitchUp, dragonPitchDown;             // ↑/↓ arrows steer pitch (nose up/down)
    private bool dragonYawLeft, dragonYawRight;              // ←/→ arrows steer yaw (turn)
    private bool dragonRmbHeld;                              // right button held → hold the steered attitude (no auto-level)

    // 3D rigged dragon model (loaded per variant from Resources/Raw) + its animation driver. The classic
    // variant (dragon.glb) has no baked clips, so a procedural DragonRig flaps it; the animated variant
    // (dragon-animated.glb) carries baked idle/running/flying loops and plays "flying" via SkinnedModel.Pose.
    // Until a model loads, the procedural Skia dragon is drawn as a fallback. The posed model + its world/
    // normal matrices are computed in the flight tick and pushed to the GL renderer in OnPaintSurface.
    private MapaTur.Application.Terrain.SkinnedModel? dragonModel3D;
    private MapaTur.Application.Terrain.DragonRig? dragonRig; // classic variant only (procedural flap)
    private bool dragonModelLoading;
    private int dragonLoadedVariant = -1;      // which DragonVariant the loaded model belongs to (−1 = none)
    private int dragonFlyingAnimIndex = -1;    // baked "flying" clip index in the animated variant (−1 = none)
    private int dragonIdleAnimIndex = -1;      // baked "idle" clip index (perched loop) in the animated variant
    private float dragonAnimTime;              // playback clock for the baked loop (wrapped by its duration)
    private float dragonTailPhase;

    // Landing-cycle animation state (smoothed toward per-phase targets each tick).
    private float dragonLegsDown;              // 0 = flight tuck … 1 = standing (flare/perch)
    private float dragonWingBrake;             // 0..1 air-brake spread during the flare
    private float dragonBreathePhase;          // chest breathing clock while perched
    private float dragonPerchOrbitAz;          // cinematic orbit azimuth around the perched dragon
    private float dragonFlapBurst;             // 1 → 0 visual overdrive after a Space flap-boost
    private float dragonFlapSprintRemaining;   // radians of the CURRENT stroke to whip through instantly (turn onset)
    private bool dragonArrowWasHeld;           // previous tick's ←→ state — the entry stroke fires on the rising edge
    private float dragonTurnStrokeDir;         // ±1 while a single-stroke turn is in flight (0 = none; CLASSIC rig)
    private float dragonPrevCyclePos;          // previous tick's flap-cycle position — detects the stroke's end
    private const float DragonTurnStrokeImpulseRadians = 0.5f; // heading JUMP delivered by one wing stroke (~29°)

    // ANIMATED variant's single-stroke turn: the baked clip's wings ignore dragonFlapPhase entirely, so its
    // stroke is a timed sequence instead — both wings FINISH the clip's current motion for a beat, then the
    // OUTER wing plays one big procedural flap (clip suppressed on that wing only) and the shove fires with
    // the slam. Timer < 0 = idle.
    private float dragonAnimStrokeTimer = -1f;
    private float dragonAnimStrokeDir;         // ±1 (arrow direction; + = left turn)
    private bool dragonAnimStrokeFired;        // physics impulse fired for this stroke
    private float dragonTraceAccum;            // ~10 Hz trajectory trace throttle
    private bool dragonPerchStreamSent;        // the landing cycle reports its FIXED streaming camera exactly once
#pragma warning disable CS0169, IDE0044 // KEPT for the next session's perch-seating work (see docs/HANDOFF-2026-07-09) — do NOT delete
    private float dragonSeatLogAccum;          // ~1 Hz throttle for the seat diagnostic
#pragma warning restore CS0169, IDE0044
    private const float DragonAnimStrokeDelaySeconds = 0.15f; // "dokończenie ruchu oboma"
    private const float DragonAnimStrokeFlapSeconds = 0.45f;  // the single outer-wing beat
    private const float DragonAnimStrokeRaiseDeg = 26f;       // wing lifts…
    private const float DragonAnimStrokeSlamDeg = 78f;        // …then slams down through level
    private const float DragonAnimStrokeInnerScale = 0.32f;   // the INNER wing echoes the beat lightly (natural asymmetry)

    // Foot bones used to seat the perched dragon's SOLES on the summit (per variant; posed positions).
    private static readonly string[] DragonAnimatedFootBones = ["l_ball.163", "r_ball.175", "l_toeA.164", "r_toeA.176"];
    private static readonly string[] DragonClassicFootBones = ["Foot.L", "Foot.R"];
#pragma warning disable CS0414 // KEPT for the next session's perch-seating work (see docs/HANDOFF-2026-07-09) — do NOT delete
    private float? dragonPerchGroundElev; // rendered-mesh elevation under the perch, sampled once (feet sit on the DRAWN rock)
#pragma warning restore CS0414
    private const float DragonFootPadMeters = 0.5f; // sink the measured foot bones this far so soles/claws touch (tune)

    // ── Fire breath (F held = stream of fireballs from the mouth) ──────────────────────────────────────────
    // Balls simulate in REAL metres (like the flight body): position/velocity real, Z exaggerated only when
    // building the render sprites. A ball dies on TTL or bursts (short expanding flash) on terrain contact.
    private struct DragonFireball
    {
        public System.Numerics.Vector2 XY;
        public float Elevation;
        public System.Numerics.Vector2 VelocityXY;
        public float VelocityZ;
        public float Age;
        public float Seed;
        public bool Exploding;
        public float ExplodeAge;
    }

    private readonly List<DragonFireball> dragonFireballs = [];
    private readonly List<MapaTur.App.Services.Terrain3DGlRenderer.FireballSprite> dragonFireSprites = [];
    private bool dragonFireHeld;
    private readonly float dragonFireCooldown;
    private readonly int dragonFireCounter;
    private const float DragonFireCooldownSeconds = 0.16f;  // stream rate while F is held
    private const float DragonFireSpeedMetersPerSecond = 75f; // muzzle speed on top of the dragon's own
    private const float DragonFireTtlSeconds = 2.4f;
    private const float DragonFireMuzzleOffsetMeters = 11f;  // roughly the head, ahead of the body centre
    private const float DragonFireRadiusMeters = 1.7f;
    private const float DragonFireExplodeSeconds = 0.3f;
    private MapaTur.Application.Terrain.DragonFlightPhase dragonPrevPhase = MapaTur.Application.Terrain.DragonFlightPhase.Flying;
    private Matrix4x4 dragonWorldMatrix = Matrix4x4.Identity;
    private Matrix4x4 dragonNormalMatrix = Matrix4x4.Identity;
    private const float DragonModelSizeMeters = 24f; // target max extent of the dragon in world metres
    private const float DragonFlapLiftMeters = 1.6f; // rises on the down-stroke, sinks on the up-stroke
    // Model-orientation tuning (glTF bone/axis frames vary — adjusted by eye):
    private static readonly float DragonYawOffset = MathF.PI / 2f; // model head is +Z; after Y-up→Z-up remap, +90° aims it along +X (head forward)
    private const float DragonDropMeters = 1f; // slight seat below the flight point (centring now does the heavy lifting)
    // The ANIMATED model's bind bounds are pulled DOWN by its long legs/tail, so bounds-centring leaves the
    // BODY well above the flight point ("jest 10 m nade mną") — seat that variant much lower. Per-variant,
    // because the classic model's centring is already right.
    private const float DragonAnimatedDropMeters = 11f;
    private const float DragonPitchSign = -1f; // model noses DOWN when descending (↑ = dive), matched to the flight
    private const float DragonRollSign = 1f;
    private float dragonCamPitch;              // camera pitch LAGS the dragon's (dragon responds first, camera catches up)
    private const float DragonCamPitchFollow = 2.4f; // per-second lerp of the camera toward the dragon's pitch (diving — the lag looks great)
    private const float DragonCamPitchFollowClimb = 11f; // CLIMBING: near-immediate — with the lag the rising dragon flew INTO the camera
    // Camera YAW also lags (a lazy-tracking chase cam): welded to the heading, a turn spun the WORLD while the
    // dragon sat pinned mid-frame — the artificial look. With the lag the dragon visibly banks and yaws in the
    // frame and the camera swings around after it.
    private float dragonCamAzimuth;
    private const float DragonCamYawFollow = 2.2f;

    private const float DragonChaseDistanceMeters = 13f; // camera behind the dragon (world units) — close, so the beast fills the frame
    private const float DragonChaseHeightMeters = 4.5f;  // and above it (the classic framing the user accepted)
    private const float DragonChaseLookAheadMeters = 30f;
    // CLIMB pull-back: 13 m behind the CENTRE of a 24 m dragon leaves the tail metres from the lens — when the
    // body pitches up it sweeps INTO the camera. Nose-up immediately pushes the eye back + up (per radian of
    // climb pitch), keyed off the dragon's RAW pitch so it reacts the same frame, not after the camera catches up.
    private const float DragonChaseClimbPullbackMeters = 16f;
    private const float DragonChaseClimbRaiseMeters = 6f;

    // Cinematic orbit around the PERCHED dragon (slow pan of the summit + panorama; steering nudges it).
    private const float DragonPerchOrbitRadPerSec = 0.2f; // brisk showcase spin (0.06 crawled — "kamera szybciej")
    private const float DragonPerchOrbitDistanceMeters = 26f;
    private const float DragonPerchOrbitHeightMeters = 7f;

    // Which model wing is the OUTER one for the ANIMATED dragon's turn stroke (l_*/r_* bone naming vs the
    // world was checked in-app): −1 = turn LEFT beats the RIGHT wing, as it must.
    private const float DragonAnimatedTurnMirror = -1f;
    private const float DragonMouseSteerPerPixel = 0.05f; // right-drag pixels → yaw steer input
    private const float DragonMousePitchPerPixel = 0.11f;  // right-drag pixels → pitch steer (stronger, so the mouse climbs/dives clearly)

    // Real elevation (metres) of the terrain under a world-XY point, or null off-coverage. Prefers the true
    // 1 m baked surface (what the walker visually stands on) and falls back to the coarse base DEM.
    private float? SampleWalkGround(System.Numerics.Vector2 xy)
    {
        if (WorldFrame is not { } frame)
        {
            return null;
        }

        GeoPoint geo = frame.WorldToGeo(new Vector3(xy.X, xy.Y, 0f));
        if (FineElevationSampler is { } fine && fine(geo.Longitude, geo.Latitude) is { } fineElev)
        {
            return (float)fineElev;
        }

        if (Raster is { } raster)
        {
            double baseElev = raster.SampleBilinear(geo.Longitude, geo.Latitude);
            if (!double.IsNaN(baseElev) && baseElev > raster.NoDataValue)
            {
                return (float)baseElev;
            }
        }

        return null;
    }

    // Enters first-person walk: spawns the walker on the ground under the current eye, facing where the camera
    // looked, and starts the ~60 Hz walk tick. Needs a built scene; a stray toggle before the DEM loads is undone.
    private void EnterWalkMode()
    {
        if (walkActive)
        {
            return;
        }

        if (WorldFrame is null)
        {
            Serilog.Log.Warning("[Walk] EnterWalkMode cancelled — no world frame (terrain not loaded yet)");
            IsWalkModeActive = false; // no scene yet — cancel the toggle
            return;
        }

        Serilog.Log.Information("[Walk] entering walk mode at eye=({X:F0},{Y:F0})", Camera.Position.X, Camera.Position.Y);
        StopFlight(); // never walk during a cinematic fly-through
        if (dragonActive)
        {
            IsDragonFlightActive = false; // walk and dragon are exclusive
        }

        var startXY = new System.Numerics.Vector2(Camera.Position.X, Camera.Position.Y);
        walker = new MapaTur.Application.Terrain.WalkPhysics(startXY, SampleWalkGround);

        Vector3 viewDir = Camera.Target - Camera.Position;
        walkHeadingRadians = MathF.Atan2(viewDir.Y, viewDir.X);
        walkLookPitchRadians = 0f; // start looking at the horizon
        walkFwd = walkBack = walkStrafeLeft = walkStrafeRight = walkRun = false;
        walkJumpQueued = false;
        walkLmbDown = false;
        walkDetailTick = 0;

        walkActive = true;
        walkClock.Restart();
        walkLastSeconds = 0.0;
        if (walkTimer is null)
        {
            walkTimer = Dispatcher.CreateTimer();
            walkTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60 fps
            walkTimer.Tick += OnWalkTick;
        }

        walkTimer.Start();
        Serilog.Log.Information("[Walk] walk mode ACTIVE (heading={H:F2} rad, feet={F:F0} m)", walkHeadingRadians, walker.FeetElevation);
        Canvas.InvalidateSurface();
    }

    // Leaves walk mode and hands the camera back to the orbit controller cleanly: it frames the spot the walker
    // was standing on from a modest distance behind the walk heading, so exiting doesn't snap through the
    // controller's MinDistance clamp or leave the eye buried at ground level.
    private void ExitWalkMode()
    {
        if (!walkActive)
        {
            return;
        }

        walkActive = false;
        walkTimer?.Stop();
        walkFwd = walkBack = walkStrafeLeft = walkStrafeRight = walkRun = false;

        if (walker is { } w && WorldFrame is { } frame)
        {
            float groundZ = w.FeetElevation * frame.VerticalExaggeration;
            Camera.Target = new Vector3(w.PositionXY.X, w.PositionXY.Y, groundZ);
            Camera.Distance = 400f;
            Camera.AzimuthRadians = walkHeadingRadians + MathF.PI; // camera behind, looking the way you walked
            Camera.PitchRadians = 0.35f; // ~20° down
        }

        walker = null;
        Canvas.InvalidateSurface();
    }

    private void OnWalkTick(object? sender, EventArgs e)
    {
        if (!walkActive || walker is not { } w || WorldFrame is not { } frame)
        {
            walkTimer?.Stop();
            return;
        }

        double now = walkClock.Elapsed.TotalSeconds;
        var dt = (float)Math.Clamp(now - walkLastSeconds, 0.0, 0.1); // clamp so a stalled frame can't teleport
        walkLastSeconds = now;

        // Heading-relative move from the held keys. forward = (cos h, sin h); right = (sin h, −cos h) — facing
        // east (h=0) your right hand points south, so D/→ strafes correctly.
        float ch = MathF.Cos(walkHeadingRadians), sh = MathF.Sin(walkHeadingRadians);
        var forward = new System.Numerics.Vector2(ch, sh);
        var right = new System.Numerics.Vector2(sh, -ch);
        System.Numerics.Vector2 wish = System.Numerics.Vector2.Zero;
        if (walkFwd) { wish += forward; }
        if (walkBack) { wish -= forward; }
        if (walkStrafeRight) { wish += right; }
        if (walkStrafeLeft) { wish -= right; }

        float speed = WalkMoveSpeedMetersPerSecond * (walkRun ? WalkRunMultiplier : 1f);
        bool jump = walkJumpQueued;
        walkJumpQueued = false;

        w.Step(dt, wish, speed, jump, hangHeld: walkLmbDown);

        // Place the eye on the walker (real eye elevation × exaggeration), looking along heading + pitch.
        float exaggeration = frame.VerticalExaggeration;
        var eye = new Vector3(w.PositionXY.X, w.PositionXY.Y, w.EyeElevation * exaggeration);
        float cp = MathF.Cos(walkLookPitchRadians);
        var look = new Vector3(cp * ch, cp * sh, MathF.Sin(walkLookPitchRadians));
        ApplyFreeCamera(eye, eye + (look * WalkLookDistanceMeters));

        // Stream the 1 m detail to the ground just ahead of the walker (~1×/s) so the surface under the feet is
        // the fine baked one, not the coarse base. The VM gates on look-at drift + cooldown, so a standing
        // walker loads one patch and a moving one keeps the patch on the path.
        if (DetailStreamingEnabled && walkDetailTick++ % 60 == 0)
        {
            System.Numerics.Vector2 aheadXY = w.PositionXY + (forward * 120f);
            float aheadGround = (SampleWalkGround(aheadXY) ?? w.FeetElevation) * exaggeration;
            var focus = new Vector3(aheadXY.X, aheadXY.Y, aheadGround);
            Vector3 off = eye - focus;
            float d = MathF.Max(1f, off.Length());
            CameraFocusMoved?.Invoke(this, new MapaTur.Application.Terrain.Camera3D
            {
                Target = focus,
                Distance = d,
                AzimuthRadians = MathF.Atan2(off.Y, off.X),
                PitchRadians = MathF.Asin(Math.Clamp(off.Z / d, -1f, 1f)),
                FieldOfViewYRadians = Camera.FieldOfViewYRadians,
                NearPlane = Camera.NearPlane,
                FarPlane = Camera.FarPlane,
            });
        }

        Canvas.InvalidateSurface();
    }

    // Enters dragon flight: launches a DragonFlight above the current camera focus, facing the way the camera
    // looked, and starts the ~60 Hz flight tick. Needs a built scene; a stray toggle before the DEM loads is undone.
    private void EnterDragonFlight()
    {
        if (dragonActive)
        {
            return;
        }

        if (WorldFrame is null)
        {
            IsDragonFlightActive = false;
            return;
        }

        StopFlight();
        if (walkActive)
        {
            IsWalkModeActive = false; // dragon and walk are exclusive
        }

        var startXY = new System.Numerics.Vector2(Camera.Target.X, Camera.Target.Y);
        Vector3 viewDir = Camera.Target - Camera.Position;
        float heading = MathF.Atan2(viewDir.Y, viewDir.X);
        dragon = new MapaTur.Application.Terrain.DragonFlight(startXY, heading, SampleWalkGround);

        dragonMouseDx = dragonMouseDy = 0f;
        dragonW = dragonS = dragonA = dragonD = false;
        dragonPitchUp = dragonPitchDown = dragonYawLeft = dragonYawRight = false;
        dragonRmbHeld = false;
        dragonCamPitch = 0f;
        dragonCamAzimuth = heading; // start the lazy chase cam in sync (no entry swing)
        dragonFlapPhase = 0f;
        dragonDetailTick = 0;
        dragonActive = true;
        dragonClock.Restart();
        dragonLastSeconds = 0.0;
        if (dragonTimer is null)
        {
            dragonTimer = Dispatcher.CreateTimer();
            dragonTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60 fps
            dragonTimer.Tick += OnDragonTick;
        }

        dragonTimer.Start();
        LoadDragonModelAsync(); // fire-and-forget; the procedural Skia dragon shows until the 3D model is ready
        Serilog.Log.Information("[Dragon] flight ON at ({X:F0},{Y:F0}) heading={H:F2}", startXY.X, startXY.Y, heading);
        Canvas.InvalidateSurface();
    }

    // Loads the selected dragon variant's GLB once, off the UI thread. Classic gets the procedural DragonRig;
    // the animated variant instead resolves its baked "flying" clip and is driven by SkinnedModel.Pose.
    private async void LoadDragonModelAsync()
    {
        if ((dragonModel3D is not null && dragonLoadedVariant == DragonVariant) || dragonModelLoading)
        {
            return;
        }

        dragonModelLoading = true;
        int variant = DragonVariant;
        try
        {
            string asset = variant == 1 ? "dragon-animated.glb" : "dragon.glb";
            await using Stream s = await Microsoft.Maui.Storage.FileSystem.OpenAppPackageFileAsync(asset).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms).ConfigureAwait(false);
            var model = MapaTur.Application.Terrain.SkinnedModel.LoadGlb(ms.ToArray());

            // Animated variant: play the baked "flying" loop (fall back to clip 0 if unnamed) and remember
            // "idle" for the perched state. Classic (no clips) flaps procedurally via DragonRig.
            int flyingIndex = -1;
            int idleIndex = -1;
            MapaTur.Application.Terrain.DragonRig? rig = null;
            if (model.Animations.Count > 0)
            {
                for (int i = 0; i < model.Animations.Count; i++)
                {
                    if (string.Equals(model.Animations[i].Name, "flying", StringComparison.OrdinalIgnoreCase))
                    {
                        flyingIndex = i;
                    }
                    else if (string.Equals(model.Animations[i].Name, "idle", StringComparison.OrdinalIgnoreCase))
                    {
                        idleIndex = i;
                    }
                }

                if (flyingIndex < 0)
                {
                    flyingIndex = 0;
                }
            }
            else
            {
                rig = new MapaTur.Application.Terrain.DragonRig(model);
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                // The user may have flipped the selector during the load — only install a still-wanted model.
                if (DragonVariant != variant)
                {
                    return;
                }

                dragonModel3D = model;
                dragonRig = rig;
                dragonLoadedVariant = variant;
                dragonFlyingAnimIndex = flyingIndex;
                dragonIdleAnimIndex = idleIndex;
                dragonAnimTime = 0f;
                Canvas.InvalidateSurface();
            });
            Serilog.Log.Information(
                "[Dragon] 3D model loaded ({Asset}): {Prims} prims, extent={Ext:F2}, bones={Bones}, anims=[{Anims}], tex={Tex}",
                asset, model.Primitives.Count, model.LocalExtent, model.BoneNames.Count,
                string.Join(", ", model.Animations.Select(a => $"{a.Name}:{a.Duration:F1}s")),
                model.BaseColorImageBytes is { Length: > 0 } bytes ? $"{bytes.Length / 1024}kB" : "none");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Dragon] 3D model load failed — keeping the procedural dragon");
        }
        finally
        {
            dragonModelLoading = false;
        }
    }

    // Leaves dragon flight and frames its final spot from an orbit vantage.
    private void ExitDragonFlight()
    {
        if (!dragonActive)
        {
            return;
        }

        dragonActive = false;
        dragonTimer?.Stop();
        dragonW = dragonS = dragonA = dragonD = false;
        dragonPitchUp = dragonPitchDown = dragonYawLeft = dragonYawRight = false;
        dragonRmbHeld = false;
        dragonCamPitch = 0f;
        dragonFireHeld = false;
        dragonPerchGroundElev = null;
        dragonFireballs.Clear();
        dragonFireSprites.Clear();

        if (dragon is { } d && WorldFrame is { } frame)
        {
            Camera.Target = new Vector3(d.PositionXY.X, d.PositionXY.Y, d.ElevationMeters * frame.VerticalExaggeration);
            Camera.Distance = 900f;
            Camera.AzimuthRadians = d.HeadingRadians + MathF.PI;
            Camera.PitchRadians = 0.4f;
        }

        dragon = null;
        Canvas.InvalidateSurface();
    }

    private void OnDragonTick(object? sender, EventArgs e)
    {
        if (!dragonActive || dragon is not { } d || WorldFrame is not { } frame)
        {
            dragonTimer?.Stop();
            return;
        }

        double now = dragonClock.Elapsed.TotalSeconds;
        var dt = (float)Math.Clamp(now - dragonLastSeconds, 0.0, 0.1);
        dragonLastSeconds = now;

        // Steer: right-drag/A,D/←→ are the ROLL command — the physics banks the dragon and flies the turn
        // THROUGH the bank (tan(roll)/speed; released = self-levels). ↑↓ pitch; W/S throttle. The ←→ PRESS
        // additionally fires a turn-entry wing stroke (below).
        // Pitch is inverted from the naive sign: ↑ climbs (dragon noses up, camera drops behind-below), ↓ dives.
        float pitch = Math.Clamp(
            (dragonMouseDy * DragonMousePitchPerPixel)
            - (dragonPitchUp ? 1f : 0f) + (dragonPitchDown ? 1f : 0f),
            -1f, 1f);
        float throttle = (dragonW ? 1f : 0f) - (dragonS ? 1f : 0f);

        // ── TURN-ENTRY STROKE (←→ rising edge): the moment a turn STARTS, one hard beat of the outer wing
        // shoves the body in (TurnImpulse: lateral push + bank kick). The SUSTAINED turn is then pure banking —
        // no chained strokes; release and press again for another entry beat.
        const float TwoPi = 2f * MathF.PI;
        const float DownStart = MathF.PI / 2f;
        const float DownEnd = 3f * MathF.PI / 2f;
        float cyclePos = ((dragonFlapPhase % TwoPi) + TwoPi) % TwoPi;
        bool arrowHeld = dragonYawLeft ^ dragonYawRight;
        bool arrowPressed = arrowHeld && !dragonArrowWasHeld;
        dragonArrowWasHeld = arrowHeld;
        bool animatedStroke = dragonRig is null && dragonModel3D is not null && dragonFlyingAnimIndex >= 0;

        // ANIMATED variant: timed stroke — a short "finish the current motion with both wings" beat, then the
        // outer wing's single big flap (posed in the render block) with the shove fired at its slam.
        if (animatedStroke)
        {
            if (dragonAnimStrokeTimer < 0f && arrowPressed
                && d.Phase == MapaTur.Application.Terrain.DragonFlightPhase.Flying)
            {
                dragonAnimStrokeTimer = 0f;
                dragonAnimStrokeDir = dragonYawLeft ? 1f : -1f;
                dragonAnimStrokeFired = false;
                Serilog.Log.Information("[DragonStroke] START dir={Dir} (outer wing={Wing})",
                    dragonAnimStrokeDir, (dragonAnimStrokeDir * DragonAnimatedTurnMirror) > 0f ? "LEFT" : "RIGHT");
            }

            if (dragonAnimStrokeTimer >= 0f)
            {
                dragonAnimStrokeTimer += dt;
                float strokeU = (dragonAnimStrokeTimer - DragonAnimStrokeDelaySeconds) / DragonAnimStrokeFlapSeconds;
                if (!dragonAnimStrokeFired && strokeU >= 0.35f)
                {
                    // The slam begins — shove the body with it.
                    d.TurnImpulse(dragonAnimStrokeDir * DragonTurnStrokeImpulseRadians);
                    dragonAnimStrokeFired = true;
                    Serilog.Log.Information("[DragonStroke] SLAM impulse dir={Dir} u={U:F2}", dragonAnimStrokeDir, strokeU);
                }

                if (strokeU >= 1f)
                {
                    // One beat per press — the sustained turn is flown on the bank, not on chained strokes.
                    dragonAnimStrokeTimer = -1f;
                    Serilog.Log.Information("[DragonStroke] END");
                }
            }
        }

        if (!animatedStroke && dragonTurnStrokeDir == 0f && arrowPressed
            && d.Phase == MapaTur.Application.Terrain.DragonFlightPhase.Flying)
        {
            dragonTurnStrokeDir = dragonYawLeft ? 1f : -1f; // matches the old arrow mapping (← = +yaw = left turn)
            if (cyclePos is < DownStart or >= DownEnd)
            {
                // Not in a down-stroke: whip what's left of the recovery instantly so the kick starts NOW.
                dragonFlapSprintRemaining = ((DownStart - cyclePos) + TwoPi) % TwoPi;
            }
            else
            {
                // Pressed mid-down-stroke: the jerk fires immediately off the stroke already in flight.
                d.TurnImpulse(dragonTurnStrokeDir * DragonTurnStrokeImpulseRadians);
            }

            dragonFlapBurst = MathF.Max(dragonFlapBurst, 0.85f);
        }

        if (dragonTurnStrokeDir != 0f)
        {
            bool inDownStroke = cyclePos is >= DownStart and < DownEnd;
            bool prevInDown = dragonPrevCyclePos is >= DownStart and < DownEnd;

            // The instant the wing snaps into its down-stroke, the WHOLE turn lands as one jerk — a heading
            // JUMP with a hard bank (exponential ease-out in the physics), not a rotation smeared over the
            // stroke. The stroke is spent when the wing reaches the bottom of its beat.
            if (inDownStroke && !prevInDown)
            {
                d.TurnImpulse(dragonTurnStrokeDir * DragonTurnStrokeImpulseRadians);
            }

            if (prevInDown && !inDownStroke)
            {
                dragonTurnStrokeDir = 0f;
            }
        }

        dragonPrevCyclePos = cyclePos;

        float yaw = Math.Clamp(
            (dragonMouseDx * DragonMouseSteerPerPixel)
            + (dragonD ? 1f : 0f) - (dragonA ? 1f : 0f)
            + (dragonYawLeft ? 1f : 0f) - (dragonYawRight ? 1f : 0f),
            -1f, 1f);
        dragonMouseDx = dragonMouseDy = 0f;

        // Hold the steered attitude while the right button is down, so a climb/dive set by the mouse doesn't
        // auto-level the instant the mouse stops moving. The yaw input is the ROLL command (banked turns).
        d.Step(dt, yaw, pitch, throttle, holdPitch: dragonRmbHeld);

        // Wing dynamics: in free flight they follow pitch (climb = beat very fast, dive = fold and glide);
        // the landing cycle overrides them per phase (approach glide → braking flare → settle → perched idle).
        var phase = d.Phase;
        float climbFactor = Math.Clamp(d.PitchRadians / 1.2f, -1f, 1f); // + climbing, − diving
        (float flapActivity, float dragonFold) = phase switch
        {
            MapaTur.Application.Terrain.DragonFlightPhase.Approach => (0.7f, 0f),
            MapaTur.Application.Terrain.DragonFlightPhase.Flare => (0.55f, 0f),   // slow DEEP strokes (brake boosts amplitude)
            MapaTur.Application.Terrain.DragonFlightPhase.Touchdown => (0.25f, 0.4f),
            MapaTur.Application.Terrain.DragonFlightPhase.Perched => (0f, 1f),    // wings folded on the body
            MapaTur.Application.Terrain.DragonFlightPhase.Takeoff => (2.7f, 0f),  // powering off the summit
            _ => (Math.Clamp(1f + (1.9f * climbFactor), 0f, 2.9f), Math.Clamp(-d.PitchRadians / 0.55f, 0f, 1f)),
        };

        // Space flap-boost: overdrive the beat briefly so the physics hoist reads as one mighty stroke.
        if (dragonFlapBurst > 0f && phase == MapaTur.Application.Terrain.DragonFlightPhase.Flying)
        {
            flapActivity += 2.4f * dragonFlapBurst;
        }

        dragonFlapBurst = MathF.Max(0f, dragonFlapBurst - (dt / 0.7f));

        // Turn-onset sprint: whip through the queued remainder of the up-stroke at ~22 rad/s (≤0.3 s worst
        // case) so the kicking down-stroke starts effectively NOW, then the normal beat takes over.
        if (dragonFlapSprintRemaining > 0f)
        {
            float sprint = MathF.Min(dragonFlapSprintRemaining, dt * 22f);
            dragonFlapPhase += sprint;
            dragonFlapSprintRemaining -= sprint;
        }

        dragonFlapPhase += dt * 3.2f * flapActivity;
        dragonTailPhase += dt * (phase == MapaTur.Application.Terrain.DragonFlightPhase.Perched ? 1.1f : 3.0f);

        // Smoothed landing blends: legs come out of the tuck through the flare, the air-brake spread fades in
        // and out, the chest breathes on the perch. Entering the perch seeds the cinematic orbit at the current
        // camera azimuth so the hand-off doesn't jump.
        float legsTarget = phase switch
        {
            MapaTur.Application.Terrain.DragonFlightPhase.Approach => 0.3f,
            MapaTur.Application.Terrain.DragonFlightPhase.Flare => 1f,
            MapaTur.Application.Terrain.DragonFlightPhase.Touchdown => 1f,
            MapaTur.Application.Terrain.DragonFlightPhase.Perched => 1f,
            _ => 0f,
        };
        dragonLegsDown += (legsTarget - dragonLegsDown) * Math.Clamp(4f * dt, 0f, 1f);
        float brakeTarget = phase == MapaTur.Application.Terrain.DragonFlightPhase.Flare ? 1f : 0f;
        dragonWingBrake += (brakeTarget - dragonWingBrake) * Math.Clamp(5f * dt, 0f, 1f);
        if (phase == MapaTur.Application.Terrain.DragonFlightPhase.Perched)
        {
            dragonBreathePhase += dt * 1.9f;
            if (dragonPrevPhase != MapaTur.Application.Terrain.DragonFlightPhase.Perched)
            {
                dragonPerchOrbitAz = Camera.AzimuthRadians;
            }
        }

        dragonPrevPhase = phase;

        // Drive the 3D model (once loaded) and build its world/normal matrices from the flight pose. The classic
        // variant flaps via the procedural DragonRig; the animated one plays its baked "flying" loop, with the
        // playback speed following the wing effort (climb = beat faster, dive = glide slower).
        if (dragonModel3D is { } model3D)
        {
            float flapLift = 0f;
            if (dragonRig is { } rig)
            {
                rig.Pose(
                    dragonFlapPhase, dragonTailPhase, d.RollRadians, d.PitchRadians, dragonFold,
                    dragonLegsDown, dragonWingBrake,
                    phase == MapaTur.Application.Terrain.DragonFlightPhase.Perched ? dragonBreathePhase : -1f);
                // Flap lift: rise on the down-stroke (flap → −1), sink on the up-stroke (flap → +1).
                // Free flight only — a landing/perched dragon must sit EXACTLY on its spot.
                if (phase == MapaTur.Application.Terrain.DragonFlightPhase.Flying)
                {
                    flapLift = -MathF.Sin(dragonFlapPhase) * DragonFlapLiftMeters;
                }
            }
            else if (dragonFlyingAnimIndex >= 0 && dragonFlyingAnimIndex < model3D.Animations.Count)
            {
                // Animated variant: perched/settling plays the baked "idle" loop, everything else "flying"
                // (a hard cut, masked by the touchdown moment). Playback pace follows the phase.
                bool idlePhase = phase is MapaTur.Application.Terrain.DragonFlightPhase.Perched
                    or MapaTur.Application.Terrain.DragonFlightPhase.Touchdown;
                int clip = idlePhase && dragonIdleAnimIndex >= 0 ? dragonIdleAnimIndex : dragonFlyingAnimIndex;
                float pace = phase switch
                {
                    MapaTur.Application.Terrain.DragonFlightPhase.Approach => 0.85f,
                    MapaTur.Application.Terrain.DragonFlightPhase.Flare => 0.6f,
                    MapaTur.Application.Terrain.DragonFlightPhase.Touchdown => 1f,
                    MapaTur.Application.Terrain.DragonFlightPhase.Perched => 1f,
                    MapaTur.Application.Terrain.DragonFlightPhase.Takeoff => 1.6f,
                    _ => Math.Clamp(1f + (0.45f * climbFactor), 0.55f, 1.6f),
                };
                if (phase == MapaTur.Application.Terrain.DragonFlightPhase.Flying)
                {
                    pace += 1.5f * dragonFlapBurst; // Space flap-boost quickens the baked beat too
                }

                float duration = model3D.Animations[clip].Duration;
                dragonAnimTime += dt * pace;
                model3D.SetFrame(clip, duration > 0.01f ? dragonAnimTime % duration : 0f);

                // SINGLE-STROKE TURN, animated variant: during the stroke window BOTH wings are taken away
                // from the clip and play the same synced procedural beat — a lift, then a slam down through
                // level — the OUTER wing at full force, the inner one just lightly, so the move reads as one
                // natural asymmetric stroke. ← = right wing slams hard, left barely; → mirrored.
                float strokeWindowU = (dragonAnimStrokeTimer - DragonAnimStrokeDelaySeconds) / DragonAnimStrokeFlapSeconds;
                if (dragonAnimStrokeTimer >= 0f && strokeWindowU > 0f && strokeWindowU < 1f
                    && phase == MapaTur.Application.Terrain.DragonFlightPhase.Flying)
                {
                    bool leftIsOuter = (dragonAnimStrokeDir * DragonAnimatedTurnMirror) > 0f;
                    static float SmoothStep(float a, float b, float x)
                    {
                        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
                        return t * t * (3f - (2f * t));
                    }

                    // Lift first, then the decisive down-slam (probe: local +Z raises the wing on both sides).
                    float angleDeg = (DragonAnimStrokeRaiseDeg * MathF.Sin(MathF.PI * Math.Clamp(strokeWindowU / 0.7f, 0f, 1f)))
                        - (DragonAnimStrokeSlamDeg * SmoothStep(0.35f, 1f, strokeWindowU));
                    static Quaternion Rz(float deg) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg * MathF.PI / 180f);
                    void Beat(bool left, float amplitude)
                    {
                        string shoulder = left ? "l_shoulder.73" : "r_shoulder.114";
                        string clavicle = left ? "l_clavicle.72" : "r_clavicle.113";
                        string forearm = left ? "l_forearm.74" : "r_forearm.115";
                        model3D.BlendBoneTowardBind(clavicle, 1f);
                        model3D.BlendBoneTowardBind(shoulder, 1f);
                        model3D.BlendBoneTowardBind(forearm, 1f);
                        model3D.RotateBoneOverlay(shoulder, Rz(angleDeg * amplitude));
                        model3D.RotateBoneOverlay(clavicle, Rz(angleDeg * 0.5f * amplitude));
                        model3D.RotateBoneOverlay(forearm, Rz(angleDeg * 0.6f * amplitude));
                    }

                    Beat(leftIsOuter, 1f);
                    Beat(!leftIsOuter, DragonAnimStrokeInnerScale);

                    // The slam also HOISTS the torso — same body-bob as a normal wing-beat, keyed to how far
                    // the wing has swung below level ("podnieś tułów tak jak przy ruchu normalnym").
                    flapLift = MathF.Max(0f, -angleDeg) / DragonAnimStrokeSlamDeg * DragonFlapLiftMeters * 1.5f;
                }

                model3D.Skin();
            }

            float exaggeration3D = frame.VerticalExaggeration;
            float scale = DragonModelSizeMeters / MathF.Max(0.001f, model3D.LocalExtent);
            Vector3 boundsCenter = (model3D.BoundsMin + model3D.BoundsMax) * 0.5f;
            Matrix4x4 center = Matrix4x4.CreateTranslation(-boundsCenter); // pivot on the model centre, not its rig root
            Matrix4x4 remap = Matrix4x4.CreateRotationX(MathF.PI / 2f); // glTF Y-up → world Z-up
            Matrix4x4 bank = Matrix4x4.CreateRotationY(DragonRollSign * d.RollRadians);
            Matrix4x4 climb = Matrix4x4.CreateRotationX(DragonPitchSign * d.PitchRadians);
            Matrix4x4 yawRot = Matrix4x4.CreateRotationZ(d.HeadingRadians + DragonYawOffset);
            Matrix4x4 rot = remap * bank * climb * yawRot;
            dragonNormalMatrix = rot;
            float drop = dragonLoadedVariant == 1 ? DragonAnimatedDropMeters : DragonDropMeters;

            // Vertical seat: in flight the body hangs at the flight point; on the ground the feet stand on the
            // spot (the lowest foot bone, blended in via the legs). NOTE — exact "feet planted on the rendered
            // summit" is a KNOWN OPEN ITEM (see docs/HANDOFF): on sharp peaks the feet still read a few metres
            // off because the render mesh / sampler / animation root offset don't reconcile cleanly here.
            float flightSeat = -drop + flapLift;
            float feetY = float.PositiveInfinity;
            foreach (string bone in dragonLoadedVariant == 1 ? DragonAnimatedFootBones : DragonClassicFootBones)
            {
                if (model3D.GetBonePosedPosition(bone) is { } footPos && footPos.Y < feetY)
                {
                    feetY = footPos.Y;
                }
            }

            if (!float.IsFinite(feetY))
            {
                feetY = model3D.BoundsMin.Y;
            }

            float perchSeat = (boundsCenter.Y - feetY) * scale;
            float seat = flightSeat + ((perchSeat - flightSeat) * dragonLegsDown);
            var worldPos = new Vector3(
                d.PositionXY.X, d.PositionXY.Y, (d.ElevationMeters + seat) * exaggeration3D);
            dragonWorldMatrix = center * Matrix4x4.CreateScale(scale) * rot * Matrix4x4.CreateTranslation(worldPos);
        }

        // ── Fire breath ── spawn while F is held (streamed on a cooldown), fly the balls forward, burst on
        // terrain, and build this frame's render sprites (Z exaggerated only here).
        StepDragonFire(d, dt, frame.VerticalExaggeration);

        // Trajectory trace (~10 Hz in flight; perched barely changes, so ~0.5 Hz there — no log flooding).
        dragonTraceAccum += dt;
        if (dragonTraceAccum >= (phase == MapaTur.Application.Terrain.DragonFlightPhase.Perched ? 2f : 0.1f))
        {
            dragonTraceAccum = 0f;
            Serilog.Log.Information(
                "[DragonTrace] pos=({X:F0},{Y:F0}) elev={E:F0} head={H:F2} roll={R:F2} speed={S:F0} yawIn={Yaw:F2} strokeT={St:F2} phase={Ph}",
                d.PositionXY.X, d.PositionXY.Y, d.ElevationMeters, d.HeadingRadians, d.RollRadians,
                d.SpeedMetersPerSecond, yaw, dragonAnimStrokeTimer, d.Phase);
        }

        // Camera. Perched: a slow CINEMATIC ORBIT around the dragon on its summit (the panorama showcase);
        // steering input (right-drag / ←→) nudges the orbit by hand. Every other phase: the chase camera
        // behind + above the dragon along the (world) flight vector, looking ahead of it — its pitch LAGS
        // the dragon's, so a held climb/dive moves the dragon first and the camera catches up.
        float exagg = frame.VerticalExaggeration;
        var dragonWorld = new Vector3(d.PositionXY.X, d.PositionXY.Y, d.ElevationMeters * exagg);

        // 1 m detail streaming. Landing cycle: report ONE FIXED synthetic camera at the landing spot — when the
        // report followed the live orbit, the swinging azimuth flapped the LOD selector z15↔z16 every few
        // seconds (log: the same 4 z16 tiles loaded/evicted in a loop), z16 residency under the perch never
        // settled, the base-ring culling never engaged and the summit stayed a pale coarse hypso spike. A
        // constant report = a stable desired set = z16 fills in and stays. Free flight: lead the dragon as before.
        if (DetailStreamingEnabled)
        {
            bool landingCycle = phase is not MapaTur.Application.Terrain.DragonFlightPhase.Flying;
            if (landingCycle)
            {
                if (!dragonPerchStreamSent)
                {
                    dragonPerchStreamSent = true;
                    CameraFocusMoved?.Invoke(this, new MapaTur.Application.Terrain.Camera3D
                    {
                        Target = new Vector3(d.LandingTargetXY.X, d.LandingTargetXY.Y, d.LandingTargetElevation * exagg),
                        Distance = 140f,
                        AzimuthRadians = d.HeadingRadians + MathF.PI,
                        PitchRadians = 0.5f,
                        FieldOfViewYRadians = Camera.FieldOfViewYRadians,
                        NearPlane = Camera.NearPlane,
                        FarPlane = Camera.FarPlane,
                    });
                }
            }
            else
            {
                dragonPerchStreamSent = false;
                if (dragonDetailTick++ % 45 == 0)
                {
                    Vector3 streamFocus = dragonWorld
                        + (new Vector3(MathF.Cos(d.HeadingRadians), MathF.Sin(d.HeadingRadians), 0f) * 200f);
                    Vector3 streamOff = Camera.Position - streamFocus;
                    float streamDist = MathF.Max(1f, streamOff.Length());
                    CameraFocusMoved?.Invoke(this, new MapaTur.Application.Terrain.Camera3D
                    {
                        Target = streamFocus,
                        Distance = streamDist,
                        AzimuthRadians = MathF.Atan2(streamOff.Y, streamOff.X),
                        PitchRadians = MathF.Asin(Math.Clamp(streamOff.Z / streamDist, -1f, 1f)),
                        FieldOfViewYRadians = Camera.FieldOfViewYRadians,
                        NearPlane = Camera.NearPlane,
                        FarPlane = Camera.FarPlane,
                    });
                }
            }
        }
        if (phase == MapaTur.Application.Terrain.DragonFlightPhase.Perched)
        {
            dragonPerchOrbitAz += dt * (DragonPerchOrbitRadPerSec + (yaw * 1.2f));
            var orbitEye = dragonWorld
                + (new Vector3(MathF.Cos(dragonPerchOrbitAz), MathF.Sin(dragonPerchOrbitAz), 0f) * DragonPerchOrbitDistanceMeters)
                + new Vector3(0f, 0f, DragonPerchOrbitHeightMeters * exagg);
            ApplyFreeCamera(orbitEye, dragonWorld + new Vector3(0f, 0f, 3f * exagg));
            Canvas.InvalidateSurface();
            return;
        }

        // Asymmetric follow: climbing snaps the camera up with the dragon (it would otherwise rise into the
        // lens); diving keeps the cinematic lag (dragon drops away first, camera catches up). The camera YAW
        // chases the heading lazily too — in a turn the dragon banks/yaws visibly in frame first.
        float pitchFollow = d.PitchRadians > dragonCamPitch ? DragonCamPitchFollowClimb : DragonCamPitchFollow;
        dragonCamPitch += (d.PitchRadians - dragonCamPitch) * Math.Clamp(pitchFollow * dt, 0f, 1f);
        dragonCamAzimuth += WrapAngleRad(d.HeadingRadians - dragonCamAzimuth) * Math.Clamp(DragonCamYawFollow * dt, 0f, 1f);
        float ch = MathF.Cos(dragonCamAzimuth), sh = MathF.Sin(dragonCamAzimuth);
        float cp = MathF.Cos(dragonCamPitch), sp = MathF.Sin(dragonCamPitch);
        Vector3 worldFwd = Vector3.Normalize(new Vector3(cp * ch, cp * sh, sp * exagg));
        // Nose-up shoves the eye back + up THE SAME FRAME (raw pitch, no smoothing) — see the consts above.
        float climbPitch = MathF.Max(0f, d.PitchRadians);
        float chaseDistance = DragonChaseDistanceMeters + (climbPitch * DragonChaseClimbPullbackMeters);
        float chaseHeight = DragonChaseHeightMeters + (climbPitch * DragonChaseClimbRaiseMeters);
        Vector3 eye = dragonWorld - (worldFwd * chaseDistance) + new Vector3(0f, 0f, chaseHeight * exagg);
        Vector3 lookAt = dragonWorld + (worldFwd * DragonChaseLookAheadMeters);
        ApplyFreeCamera(eye, lookAt);

        Canvas.InvalidateSurface();
    }

    // Starts a ciupaga swing (left mouse button in walk mode) — a quick strike-and-recover the viewmodel plays.
    // Re-triggering mid-swing restarts it (rapid taps keep chopping).
    private void StartCiupagaSwing()
    {
        walkSwinging = true;
        walkSwingStartSeconds = walkClock.Elapsed.TotalSeconds;
        Canvas.InvalidateSurface();
    }

    // Swing envelope over [0,1]: a fast ease-in to the strike apex (~30 % in), then a slower ease-out recovery.
    // 0 at rest, 1 at the moment the ciupaga bites.
    private static float SwingStrike(float p)
    {
        const float attack = 0.3f;
        if (p < attack)
        {
            float a = p / attack;
            return a * a;
        }

        float r = (p - attack) / (1f - attack);
        float s = 1f - r;
        return s * s;
    }

    // Draws the first-person CIUPAGA (a Podhale highlander's carved walking-axe) as a held-item viewmodel in the
    // lower-right while walking — a procedural Skia drawing (no asset), bobbing with the stride, dropping a touch
    // while airborne, and swinging forward on a left-click strike. Composited LAST, over terrain + overlays.
    private void DrawWalkViewmodel(SKCanvas canvas, int width, int height)
    {
        if (!walkActive || width <= 0 || height <= 0)
        {
            return;
        }

        float u = height / 800f; // the viewmodel is authored in an 800 px-tall frame, scaled to the surface
        var t = (float)walkClock.Elapsed.TotalSeconds;
        bool moving = walkFwd || walkBack || walkStrafeLeft || walkStrafeRight;
        bool airborne = walker is { IsGrounded: false };

        // Stride bob: brisk while moving (faster running), a slow idle sway standing still; a small drop while
        // airborne so the axe lags as you leave the ground.
        float bobFreq = moving ? (walkRun ? 12f : 8f) : 1.6f;
        float bobAmp = (moving ? (walkRun ? 12f : 7f) : 2.5f) * u;
        float bobY = (MathF.Sin(t * bobFreq) * bobAmp) + (airborne ? 26f * u : 0f);
        float bobX = MathF.Cos(t * bobFreq * 0.5f) * bobAmp * 0.5f;
        float swayDeg = MathF.Sin(t * bobFreq * 0.5f) * (moving ? 1.6f : 0.7f);

        // Ciupaga strike (left click): thrust the head down-and-forward, then recover. Ends when the envelope runs out.
        float strike = 0f;
        if (walkSwinging)
        {
            float p = (float)((t - walkSwingStartSeconds) / CiupagaSwingSeconds);
            if (p >= 1f)
            {
                walkSwinging = false;
            }
            else
            {
                strike = SwingStrike(Math.Clamp(p, 0f, 1f));
            }
        }

        // While self-arresting (hanging), hold the ciupaga fully planted into the rock.
        if (walker is { IsHanging: true })
        {
            strike = 1f;
        }

        canvas.Save();
        // Anchor the grip lower-right and lean the shaft up-and-right: head upper-right, butt off the bottom
        // corner — the classic right-hand viewmodel pose. Kept low enough that the head clears the top menu bar.
        // A strike LUNGES the whole tool forward (deep toward the scene centre) and rotates the head down to bite.
        canvas.Translate(
            (width * 0.68f) + bobX - (strike * 105f * u),
            (height * 0.90f) + bobY + (strike * 34f * u));
        canvas.RotateDegrees(30f + swayDeg + (strike * 60f));

        // Local frame: the shaft runs along -Y (up); +X is across it (used for the cylinder gradient).
        float shaftW = 20f * u;
        float topY = -480f * u; // where the axe head mounts
        float buttY = 300f * u; // the bottom of the pole, below the grip toward the corner
        var shaftRect = new SKRect(-shaftW / 2f, topY, shaftW / 2f, buttY);

        // Soft offset shadow so the tool reads against any terrain.
        using (var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 80), Style = SKPaintStyle.Fill })
        {
            canvas.Save();
            canvas.Translate(6f * u, 8f * u);
            canvas.DrawRoundRect(shaftRect, shaftW / 2f, shaftW / 2f, shadow);
            canvas.Restore();
        }

        // Wooden shaft, cylinder-shaded by a cross gradient.
        using (var wood = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill })
        {
            wood.Shader = SKShader.CreateLinearGradient(
                new SKPoint(-shaftW / 2f, 0f),
                new SKPoint(shaftW / 2f, 0f),
                new[]
                {
                    new SKColor(0x2A, 0x19, 0x0E), new SKColor(0x6B, 0x45, 0x25),
                    new SKColor(0x8A, 0x5E, 0x33), new SKColor(0x4A, 0x2E, 0x18), new SKColor(0x22, 0x14, 0x0B),
                },
                new[] { 0f, 0.28f, 0.5f, 0.75f, 1f },
                SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(shaftRect, shaftW / 2f, shaftW / 2f, wood);
        }

        // Specular sheen line down the shaft.
        using (var sheen = new SKPaint { IsAntialias = true, Color = new SKColor(255, 240, 210, 55), Style = SKPaintStyle.Fill })
        {
            canvas.DrawRoundRect(
                new SKRect(-shaftW * 0.22f, topY + (30f * u), -shaftW * 0.05f, buttY - (20f * u)), 3f * u, 3f * u, sheen);
        }

        // White Zakopane (Podhale) folk ornaments carved down the brown shaft — the góralskie zdobienie.
        DrawZakopaneOrnaments(canvas, shaftW, topY, u);

        DrawCiupagaHead(canvas, shaftW, topY, u);
        DrawViewmodelHand(canvas, shaftW, u);

        canvas.Restore();
    }

    // The steel head at the top of the ciupaga: a small hatchet blade sweeping left with a bright cutting edge,
    // a dark collar clamping the shaft, and a stubby poll (hammer nub) on the right.
    private static void DrawCiupagaHead(SKCanvas canvas, float shaftW, float topY, float u)
    {
        float sx = shaftW * 0.5f;

        // Dark collar (obuch socket) clamping the head onto the shaft.
        using (var collar = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x2E, 0x30, 0x34) })
        {
            canvas.DrawRoundRect(new SKRect(-sx * 1.5f, topY + (14f * u), sx * 1.5f, topY + (66f * u)), 4f * u, 4f * u, collar);
        }

        // Slim hatchet blade sweeping left with a concave (crescent) cutting edge — a ciupaga toporek, not a slab.
        using (var blade = new SKPath())
        {
            blade.MoveTo(-sx, topY + (2f * u));
            blade.LineTo(-sx - (58f * u), topY - (28f * u));                                    // upper heel
            blade.QuadTo(-sx - (96f * u), topY + (30f * u), -sx - (54f * u), topY + (86f * u));  // crescent cutting edge
            blade.LineTo(-sx, topY + (70f * u));                                                 // lower heel back to the neck
            blade.Close();

            using var steel = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            steel.Shader = SKShader.CreateLinearGradient(
                new SKPoint(-sx - (96f * u), topY - (28f * u)),
                new SKPoint(-sx, topY + (86f * u)),
                new[] { new SKColor(0xB4, 0xBA, 0xC2), new SKColor(0x71, 0x77, 0x7F), new SKColor(0x2E, 0x32, 0x37) },
                new[] { 0f, 0.5f, 1f },
                SKShaderTileMode.Clamp);
            canvas.DrawPath(blade, steel);
        }

        // Bright honed edge along the cutting curve.
        using (var edge = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.6f * u,
            Color = new SKColor(235, 242, 250, 240),
            StrokeCap = SKStrokeCap.Round,
        })
        using (var edgePath = new SKPath())
        {
            edgePath.MoveTo(-sx - (56f * u), topY - (24f * u));
            edgePath.QuadTo(-sx - (94f * u), topY + (30f * u), -sx - (52f * u), topY + (82f * u));
            canvas.DrawPath(edgePath, edge);
        }

        // Short hammer poll (obuch) on the right of the shaft.
        using var poll = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        poll.Shader = SKShader.CreateLinearGradient(
            new SKPoint(sx, topY),
            new SKPoint(sx + (24f * u), topY + (54f * u)),
            new[] { new SKColor(0x8A, 0x90, 0x97), new SKColor(0x34, 0x38, 0x3D) },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(new SKRect(sx * 0.4f, topY + (20f * u), sx + (24f * u), topY + (60f * u)), 4f * u, 4f * u, poll);
    }

    // A góral leather glove gripping the mid-shaft — warm brown, with distinct fingers curling around the front
    // and a thumb pressing from the near side (NOT an even-ridged racket-handle wrap), plus a forearm to the corner.
    private static void DrawViewmodelHand(SKCanvas canvas, float shaftW, float u)
    {
        var leatherDark = new SKColor(0x3E, 0x2E, 0x20);
        var leatherMid = new SKColor(0x6E, 0x52, 0x36);
        var leatherHi = new SKColor(0x9C, 0x79, 0x52);

        // Wrist / forearm and the back of the hand over the shaft.
        using (var glove = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill })
        {
            glove.Shader = SKShader.CreateLinearGradient(
                new SKPoint(-shaftW, 40f * u),
                new SKPoint(shaftW * 2.2f, 240f * u),
                new[] { leatherMid, leatherDark },
                new[] { 0f, 1f },
                SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(new SKRect(-shaftW * 0.3f, 96f * u, shaftW * 2.5f, 330f * u), 24f * u, 24f * u, glove); // forearm
            canvas.DrawRoundRect(new SKRect(-shaftW * 1.2f, 34f * u, shaftW * 2.0f, 150f * u), 20f * u, 18f * u, glove); // back of hand
        }

        // Fingers curling around the FRONT of the shaft — distinct rounded segments poking out to the left.
        using (var finger = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill })
        {
            finger.Shader = SKShader.CreateLinearGradient(
                new SKPoint(-shaftW * 1.7f, 0f),
                new SKPoint(shaftW * 0.4f, 0f),
                new[] { leatherHi, leatherMid, leatherDark },
                new[] { 0f, 0.55f, 1f },
                SKShaderTileMode.Clamp);
            for (int i = 0; i < 4; i++)
            {
                float y = (2f * u) + (i * 33f * u);
                canvas.DrawRoundRect(new SKRect(-shaftW * 1.5f, y, shaftW * 0.5f, y + (26f * u)), 13f * u, 13f * u, finger);
            }
        }

        // Dark creases separating the fingers.
        using (var crease = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f * u, Color = new SKColor(0x24, 0x1A, 0x11, 170), StrokeCap = SKStrokeCap.Round })
        {
            for (int i = 1; i < 4; i++)
            {
                float y = (2f * u) + (i * 33f * u) - (3.5f * u);
                canvas.DrawLine(-shaftW * 1.4f, y, shaftW * 0.35f, y, crease);
            }
        }

        // Thumb pressing from the near/right side of the shaft.
        using var thumb = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        thumb.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0f, 20f * u),
            new SKPoint(shaftW * 1.4f, 96f * u),
            new[] { leatherHi, leatherDark },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        canvas.DrawRoundRect(new SKRect(shaftW * 0.1f, 22f * u, shaftW * 1.35f, 96f * u), 14f * u, 14f * u, thumb);
    }

    // White Zakopane (Podhale) folk ornaments carved down the brown shaft: alternating rosettes (rozety), chevron
    // bands (zygzaki) and leluja (fir) motifs — stylised góralskie zdobienie in thin white lines + small fills,
    // between the head collar and the hand grip.
    private static void DrawZakopaneOrnaments(SKCanvas canvas, float shaftW, float topY, float u)
    {
        var white = new SKColor(0xF4, 0xF0, 0xE8);
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f * u, Color = white, StrokeCap = SKStrokeCap.Round };
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = white };

        float bandTop = topY + (108f * u);   // just below the head collar
        float bandBottom = -46f * u;          // just above the hand grip
        float spacing = 78f * u;
        int idx = 0;
        for (float y = bandTop; y <= bandBottom; y += spacing, idx++)
        {
            switch (idx % 3)
            {
                case 0: DrawRosette(canvas, y, shaftW * 0.34f, line, fill); break;
                case 1: DrawChevronBand(canvas, y, shaftW, u, line); break;
                default: DrawLeluja(canvas, y, u, line); break;
            }
        }
    }

    // A sun rosette (słoneczko/rozeta): a white ring with a filled centre and eight radial ticks.
    private static void DrawRosette(SKCanvas canvas, float cy, float r, SKPaint line, SKPaint fill)
    {
        canvas.DrawCircle(0f, cy, r, line);
        canvas.DrawCircle(0f, cy, r * 0.3f, fill);
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.PI / 4f;
            float c = MathF.Cos(a), s = MathF.Sin(a);
            canvas.DrawLine(c * r * 0.72f, cy + (s * r * 0.72f), c * r * 1.18f, cy + (s * r * 1.18f), line);
        }
    }

    // A band of three nested white chevrons (zygzak) pointing up across the shaft.
    private static void DrawChevronBand(SKCanvas canvas, float cy, float shaftW, float u, SKPaint line)
    {
        float w = shaftW * 0.4f;
        for (int k = 0; k < 3; k++)
        {
            float off = k * 5f * u;
            using var path = new SKPath();
            path.MoveTo(-w, cy + off);
            path.LineTo(0f, cy + off - (7f * u));
            path.LineTo(w, cy + off);
            canvas.DrawPath(path, line);
        }
    }

    // A leluja (little fir/lily): a central stem with three pairs of up-and-out branches.
    private static void DrawLeluja(SKCanvas canvas, float cy, float u, SKPaint line)
    {
        canvas.DrawLine(0f, cy - (12f * u), 0f, cy + (12f * u), line);
        for (int k = 0; k < 3; k++)
        {
            float yy = cy + (8f * u) - (k * 8f * u);
            canvas.DrawLine(0f, yy, -7f * u, yy - (7f * u), line);
            canvas.DrawLine(0f, yy, 7f * u, yy - (7f * u), line);
        }
    }

    // ── DRAGON VIEWMODEL ─────────────────────────────────────────────────────────────────────────────────────
    // Draws the ridden dragon third-person (from behind + a little above): the great membrane wings beating, the
    // scaled body + spine, the neck reaching forward to a horned head, and the spiked tail sweeping toward us.
    // The whole beast banks with the flight roll and bobs on the wing-beat. Composited last, over the terrain.
    private void DrawDragon(SKCanvas canvas, int width, int height)
    {
        // The 3D rigged model (drawn in the GL pass) replaces this procedural Skia dragon once it's loaded.
        if (!dragonActive || dragon is not { } d || width <= 0 || height <= 0 || dragonModel3D is not null)
        {
            return;
        }

        float u = height / 800f;
        float flap = MathF.Sin(dragonFlapPhase); // −1 (down-stroke) … +1 (up-stroke)
        float bobY = (-flap * 10f * u) + (d.PitchRadians * 55f * u); // lift on the beat + pitch shift in frame

        canvas.Save();
        canvas.Translate(width * 0.5f, (height * 0.6f) + bobY);
        canvas.RotateDegrees(d.RollRadians * 57.2958f * 0.85f); // bank the whole beast into the turn

        float flapDeg = flap * 24f;
        DrawDragonWing(canvas, u, +1f, flapDeg); // right wing (behind the body)
        DrawDragonWing(canvas, u, -1f, flapDeg); // left wing
        DrawDragonNeckHead(canvas, u);
        DrawDragonBodyTail(canvas, u);

        canvas.Restore();
    }

    // One membrane wing, mirrored by <paramref name="side"/> (+1 right, −1 left), rotated at the shoulder by the
    // flap. Filled blood-red membrane with a scalloped trailing edge, then the dark arm + finger bones on top.
    private static void DrawDragonWing(SKCanvas canvas, float u, float side, float flapDeg)
    {
        canvas.Save();
        canvas.Translate(side * 26f * u, -70f * u); // shoulder joint
        canvas.RotateDegrees(-side * flapDeg);      // both wings beat together

        float S(float x) => side * x * u; // mirrored, scaled x
        float Y(float y) => y * u;

        using (var membrane = new SKPath())
        {
            membrane.MoveTo(0f, 0f);              // shoulder
            membrane.LineTo(S(120f), Y(-18f));    // elbow (leading edge)
            membrane.LineTo(S(210f), Y(6f));      // wrist
            membrane.LineTo(S(248f), Y(-66f));    // finger 1 tip
            membrane.QuadTo(S(280f), Y(-24f), S(302f), Y(-6f));  // scallop → f2
            membrane.QuadTo(S(300f), Y(30f), S(286f), Y(56f));   // → f3
            membrane.QuadTo(S(264f), Y(94f), S(224f), Y(110f));  // → f4
            membrane.QuadTo(S(120f), Y(122f), S(28f), Y(118f));  // trailing edge back toward the hip
            membrane.Close();

            using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            fill.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0f, 0f),
                new SKPoint(S(290f), Y(20f)),
                new[] { new SKColor(0x4A, 0x18, 0x18), new SKColor(0x74, 0x24, 0x22), new SKColor(0x39, 0x12, 0x12) },
                new[] { 0f, 0.5f, 1f },
                SKShaderTileMode.Clamp);
            canvas.DrawPath(membrane, fill);
        }

        using (var bone = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6f * u, Color = new SKColor(0x18, 0x0E, 0x0C), StrokeCap = SKStrokeCap.Round })
        {
            canvas.DrawLine(0f, 0f, S(120f), Y(-18f), bone);       // humerus
            canvas.DrawLine(S(120f), Y(-18f), S(210f), Y(6f), bone); // forearm
            bone.StrokeWidth = 4f * u;
            canvas.DrawLine(S(210f), Y(6f), S(248f), Y(-66f), bone); // finger 1
            canvas.DrawLine(S(210f), Y(6f), S(302f), Y(-6f), bone);  // finger 2
            canvas.DrawLine(S(210f), Y(6f), S(286f), Y(56f), bone);  // finger 3
            canvas.DrawLine(S(210f), Y(6f), S(224f), Y(110f), bone); // finger 4
        }

        canvas.Restore();
    }

    // The neck reaching forward (up-screen) from the shoulders to a small horned head, with a row of spine spikes.
    private static void DrawDragonNeckHead(SKCanvas canvas, float u)
    {
        var hide = new SKColor(0x22, 0x20, 0x1D);
        var hideDk = new SKColor(0x12, 0x11, 0x0F);

        using (var neck = new SKPath())
        {
            neck.MoveTo(-20f * u, -78f * u);
            neck.CubicTo(-16f * u, -170f * u, -10f * u, -250f * u, -8f * u, -312f * u); // left side up to head
            neck.LineTo(8f * u, -312f * u);
            neck.CubicTo(10f * u, -250f * u, 16f * u, -170f * u, 20f * u, -78f * u);    // right side back down
            neck.Close();
            using var np = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            np.Shader = SKShader.CreateLinearGradient(
                new SKPoint(-20f * u, 0f), new SKPoint(20f * u, 0f),
                new[] { hideDk, hide, hideDk }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp);
            canvas.DrawPath(neck, np);
        }

        // Spine spikes down the neck.
        DrawSpineSpikes(canvas, u, -300f, -90f, 26f, 5f);

        // Head: a small blunt skull with two back-swept horns.
        using (var head = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x1C, 0x1A, 0x17) })
        {
            canvas.DrawRoundRect(new SKRect(-16f * u, -344f * u, 16f * u, -300f * u), 10f * u, 10f * u, head);
            // snout tapering forward (away)
            using var snout = new SKPath();
            snout.MoveTo(-11f * u, -338f * u);
            snout.LineTo(0f, -366f * u);
            snout.LineTo(11f * u, -338f * u);
            snout.Close();
            canvas.DrawPath(snout, head);
        }

        using (var horn = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5f * u, Color = new SKColor(0x0E, 0x0C, 0x0A), StrokeCap = SKStrokeCap.Round })
        using (var hL = new SKPath())
        using (var hR = new SKPath())
        {
            hL.MoveTo(-12f * u, -340f * u);
            hL.QuadTo(-34f * u, -344f * u, -40f * u, -318f * u);
            hR.MoveTo(12f * u, -340f * u);
            hR.QuadTo(34f * u, -344f * u, 40f * u, -318f * u);
            canvas.DrawPath(hL, horn);
            canvas.DrawPath(hR, horn);
        }
    }

    // The scaled body hump and the spiked tail sweeping down toward the camera, with a spade fin at the tip.
    private static void DrawDragonBodyTail(SKCanvas canvas, float u)
    {
        var hide = new SKColor(0x26, 0x23, 0x20);
        var hideDk = new SKColor(0x11, 0x10, 0x0E);
        var belly = new SKColor(0x3C, 0x37, 0x2F);

        // Tail first (nearest) so the body overlaps its root.
        using (var tail = new SKPath())
        {
            tail.MoveTo(-34f * u, 96f * u);
            tail.CubicTo(-30f * u, 220f * u, -16f * u, 330f * u, -6f * u, 392f * u); // left edge down toward us
            tail.LineTo(6f * u, 392f * u);
            tail.CubicTo(16f * u, 330f * u, 30f * u, 220f * u, 34f * u, 96f * u);    // right edge
            tail.Close();
            using var tp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            tp.Shader = SKShader.CreateLinearGradient(
                new SKPoint(-34f * u, 0f), new SKPoint(34f * u, 0f),
                new[] { hideDk, hide, hideDk }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp);
            canvas.DrawPath(tail, tp);
        }

        DrawSpineSpikes(canvas, u, 110f, 372f, 30f, 7f);

        // Spade fin at the tail tip.
        using (var spade = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x2A, 0x14, 0x14) })
        using (var sp = new SKPath())
        {
            sp.MoveTo(0f, 372f * u);
            sp.LineTo(-26f * u, 410f * u);
            sp.LineTo(0f, 402f * u);
            sp.LineTo(26f * u, 410f * u);
            sp.Close();
            canvas.DrawPath(sp, spade);
        }

        // Body hump over the shoulders/hips.
        using (var body = new SKPath())
        {
            body.MoveTo(-42f * u, -92f * u);
            body.CubicTo(-58f * u, -10f * u, -52f * u, 70f * u, -34f * u, 104f * u);
            body.LineTo(34f * u, 104f * u);
            body.CubicTo(52f * u, 70f * u, 58f * u, -10f * u, 42f * u, -92f * u);
            body.Close();
            using var bp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
            bp.Shader = SKShader.CreateLinearGradient(
                new SKPoint(0f, -92f * u), new SKPoint(0f, 104f * u),
                new[] { hideDk, hide, belly }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp);
            canvas.DrawPath(body, bp);
        }

        // Spine ridge over the body.
        DrawSpineSpikes(canvas, u, -80f, 96f, 34f, 8f);
    }

    // A row of dark back-swept spikes marching along the spine from y=<paramref name="fromY"/> (top) to
    // <paramref name="toY"/> (bottom) at <paramref name="spacing"/>, each <paramref name="size"/> tall (× u).
    private static void DrawSpineSpikes(SKCanvas canvas, float u, float fromY, float toY, float spacing, float size)
    {
        using var spike = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x0C, 0x0B, 0x0A) };
        for (float y = fromY; y <= toY; y += spacing)
        {
            using var path = new SKPath();
            path.MoveTo(-4f * u, y * u);
            path.LineTo(0f, (y - size) * u);   // spike points up-screen (forward)
            path.LineTo(4f * u, y * u);
            path.Close();
            canvas.DrawPath(path, spike);
        }
    }

    private Vector3 SampleFlightPath(float s)
    {
        Vector3[] pts = flightPath!;
        int n = pts.Length;
        float u = s * (n - 1);
        int i = Math.Clamp((int)MathF.Floor(u), 0, n - 2);
        float f = u - i;
        Vector3 p0 = pts[Math.Max(0, i - 1)];
        Vector3 p1 = pts[i];
        Vector3 p2 = pts[i + 1];
        Vector3 p3 = pts[Math.Min(n - 1, i + 2)];
        // Catmull-Rom spline through p1..p2.
        float t2 = f * f;
        float t3 = t2 * f;
        return 0.5f * (
            (2f * p1)
            + ((-p0 + p2) * f)
            + (((2f * p0) - (5f * p1) + (4f * p2) - p3) * t2)
            + ((-p0 + (3f * p1) - (3f * p2) + p3) * t3));
    }

    private const float TeleportViewDistanceMeters = 3200f; // how far back the camera sits after a name-search teleport
    private const float TeleportViewPitchRadians = 0.52f;   // ~30° above the place, looking down at it
    private const float FollowViewDistanceMeters = 600f;    // chase distance the follow camera trails the user by
    private const float FollowViewPitchRadians = 0.5f;      // ~29° down-tilt for the follow ("chase") view

    /// <summary>
    /// Jumps the camera to sit over a named place (from the search picker): centres the orbit target on the
    /// place's ground point and pulls back to a fixed over-the-place vantage, keeping the current heading.
    /// </summary>
    public void TeleportTo(Domain.Routing.RouteWaypoint place)
    {
        ArgumentNullException.ThrowIfNull(place);
        if (WorldFrame is not { } frame)
        {
            return;
        }

        double elevM = place.ElevationMeters
            ?? Raster?.SampleBilinear(place.Location.Longitude, place.Location.Latitude)
            ?? 1500.0;
        if (double.IsNaN(elevM) || elevM < 0.0)
        {
            elevM = 1500.0;
        }

        Camera.Target = frame.GeoToWorld(place.Location, (float)elevM);
        Camera.Distance = TeleportViewDistanceMeters;
        Camera.PitchRadians = TeleportViewPitchRadians;
        Canvas.InvalidateSurface();
    }

    /// <summary>
    /// Follow ("chase") camera: seats the camera behind the user at <paramref name="position"/>, looking
    /// forward along <paramref name="bearingDegrees"/> (the detected travel direction). A null bearing keeps
    /// the current azimuth — the user is standing still, so recenter without spinning the view. Driven by the
    /// follow-camera tracking option on every GPS fix.
    /// </summary>
    public void FollowTo(MapaTur.Domain.Geography.GeoPoint position, double? bearingDegrees)
    {
        if (WorldFrame is not { } frame)
        {
            return;
        }

        double elevM = Raster?.SampleBilinear(position.Longitude, position.Latitude) ?? 1500.0;
        if (double.IsNaN(elevM) || elevM < 0.0)
        {
            elevM = 1500.0;
        }

        Camera.Target = frame.GeoToWorld(position, (float)elevM);
        Camera.Distance = FollowViewDistanceMeters;
        Camera.PitchRadians = FollowViewPitchRadians;
        if (bearingDegrees is { } bearing)
        {
            Camera.AzimuthRadians = MapaTur.Application.Terrain.ChaseCamera.AzimuthRadiansForBearingDegrees(bearing);
        }

        Canvas.InvalidateSurface();
    }

    /// <summary>
    /// Sets up the camera for the loaded mesh: restores the saved camera if one exists for THIS DEM,
    /// otherwise frames the whole terrain. Safety bounds are applied either way.
    /// </summary>
    public void FrameMesh()
    {
        if (WorldFrame is not { } frame)
        {
            return;
        }

        // The camera floor is set per-frame from the LOCAL terrain under the eye (see OnPaintSurface), so a
        // single global "above the tallest peak" value is no longer used — it held the camera too high over
        // the valleys. Leave it unset here; the first paint installs the local floor.
        controller.CameraFloorZ = float.NaN;

        // Lock the camera EYE over the map. The box is the mesh's ACTUAL world rectangle (per-axis, from
        // the bounds corners) — not a square of side 2·HorizontalExtent, which (HorizontalExtent = max of
        // the two half-spans) overshot the short axis and let the camera start off the map. The eye-clamp
        // (Terrain3DController.ClampCameraOverMap) keeps Camera.Position inside this box so an orbit / pan /
        // zoom / tilt can't fly the camera off the terrain into empty grey space.
        Vector3 cornerSw = frame.GeoToWorld(frame.Bounds.SouthWest, 0f);
        Vector3 cornerNe = frame.GeoToWorld(frame.Bounds.NorthEast, 0f);
        controller.MinTargetX = MathF.Min(cornerSw.X, cornerNe.X);
        controller.MaxTargetX = MathF.Max(cornerSw.X, cornerNe.X);
        controller.MinTargetY = MathF.Min(cornerSw.Y, cornerNe.Y);
        controller.MaxTargetY = MathF.Max(cornerSw.Y, cornerNe.Y);

        // Cap zoom-out so pinching out can't fly the camera kilometres past the map edge into grey space.
        controller.MaxDistance = Math.Max(frame.HorizontalExtent * 3f, 12_000f);

        // A debug pinned camera (roughness-LOD tuning) wins over everything so redeploys reproduce one view;
        // otherwise restore the camera saved for this DEM; if none (or a different region), auto-frame.
        if (!TryApplyPinnedCamera() && !TryRestoreCamera(frame))
        {
            Camera.Target = Vector3.Zero;
            Camera.Distance = Math.Max(frame.HorizontalExtent * 2.5f, 5_000f);
            Camera.AzimuthRadians = MathF.PI / 4f;
            Camera.PitchRadians = MathF.PI / 4f;
        }

        // A restored / freshly-framed camera may sit outside the new bounds — pull the eye back over the map.
        controller.ClampToBounds();

        Canvas.InvalidateSurface();
    }

    private static void OnTilesChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not Terrain3DView view)
        {
            return;
        }

        // LOD Etap 3 detail swap: the coarse base is already framed and the camera roams it; only the 1 m
        // detail tiles changed, so DON'T reframe (that would yank the camera) — just repaint.
        if (view.DetailStreamingEnabled)
        {
            view.Canvas.InvalidateSurface();
        }
        else
        {
            view.FrameMesh();
        }
    }

    private static void OnOverlayDataChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is Terrain3DView view)
        {
            // A fresh 1 m detail field can release a route film's start gate (only DetailElevation carries this type).
            if (newValue is DetailElevationField field)
            {
                view.OnFlightDetailArrived(field);
            }

            view.Canvas.InvalidateSurface();
        }
    }

    // Swap-paint breakdown (2026-07-05): the renderer's own hitch log covers only Render() (~0.3 s of the
    // measured ~1.1 s first-swap frame gap) — these attribute the rest of the PAINT handler (marker
    // projection/occlusion prep vs the GL render vs the Skia overlay draw) on the frame the tile set changes.
    private IReadOnlyList<TerrainMesh3D>? dbgLastPaintTiles;
    private readonly System.Diagnostics.Stopwatch dbgPaintWatch = new();

    private void OnPaintSurface(object? sender, SKPaintGLSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;

        if (Tiles is not { Count: > 0 } tiles || WorldFrame is not { } frame)
        {
            // The view can paint BEFORE the DEM finishes auto-loading. canvas.Clear() with no
            // argument leaves the surface transparent → underlying page background bleeds through
            // and reads as solid white on mobile. Fill with the sky colour so the empty 3D scene
            // looks like a placeholder rather than a blank page.
            canvas.Clear(new SkiaSharp.SKColor(0x6C, 0x8E, 0xB0));

#if WINDOWS || ANDROID
            // Shader warm-up behind the startup overlay: the first REAL frame used to pay ~1.0 s of
            // compile+link for every GL program (measured setup=1030 ms) at the exact moment the loading
            // overlay lifts. The placeholder paints run while the overlay is still up — compile now.
            // ResetContext hands the compile-touched GL state back to Skia.
            if (UseGlRenderer && !glDisabled && Canvas.GRContext is { } warmCtx)
            {
                try
                {
                    (glRenderer ??= new Services.Terrain3DGlRenderer()).WarmUp();
                    warmCtx.ResetContext();
                }
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "[GL3D] shader warm-up failed (the real frame will retry)");
                }
            }
#endif
            return;
        }

        bool dbgSwapPaint = !ReferenceEquals(dbgLastPaintTiles, tiles);
        dbgLastPaintTiles = tiles;
        dbgSwapPaintActive = dbgSwapPaint; // lets TryRenderTerrainGl attribute its own pre/render/post split
        if (dbgSwapPaint)
        {
            dbgPaintWatch.Restart();
        }

        if (DebugEnabled)
        {
            UpdateDebugStats(tiles.Count);
        }

        // Fit the clip planes to the scene each frame (distance changes with zoom). A scene radius
        // padded past the mesh half-extent covers the diagonal corners + vertical relief. Used by both
        // the GPU and Skia paths, and by overlay projection, so they stay consistent.
        var (near, far) = CameraClipPlanes.Fit(Camera.Distance, frame.HorizontalExtent * 1.25f);
        // NEAR PLANE — pin it close to the eye, independent of `far`/`distance`. CameraClipPlanes.Fit
        // derives near from (distance − sceneRadius); since sceneRadius is the WHOLE mesh (~22 km), a
        // camera orbiting far from the scene centre gets a near of hundreds of metres → it clips every
        // bit of terrain near the camera. At low altitude the whole scene is close, so ALL of it falls
        // in front of that near plane and the view goes grey, leaving only distant peaks (the reported
        // altitude-dependent "close terrain vanishes into grey"). Capping near to a few metres makes
        // low-altitude flight render the ground under the camera at any distance/altitude.
        const float MaxNearMeters = 5f;
        near = MathF.Min(near, MaxNearMeters);
        // The far/near ratio only matters for the translucent sea-of-clouds layer, which z-fights the
        // peak silhouettes when depth precision collapses at huge ratios. Apply the precision-preserving
        // floor ONLY when the cloud layer is actually drawn; with clouds off keep near tight so the
        // foreground always renders. (far/3000 ≈ 8–16 m, i.e. larger than MaxNearMeters, so this only
        // ever pushes near OUT for the cloud case — never re-introduces the low-altitude clipping.)
        bool cloudsActiveForClip = (EffectiveAtmosphere?.CloudCoverage ?? 0f) > 0.001f;
        if (cloudsActiveForClip)
        {
            near = MathF.Max(near, far / 3000f);
        }
        Camera.NearPlane = near;
        Camera.FarPlane = far;

        // WALK MODE near plane: the eye is only ~1.7 m over the ground, so the fly path's ≥5 m near (≈10 m with
        // clouds) clips the ground right at your feet — looking down (especially mid-jump) then sees THROUGH it to
        // the terrain's underside ("rzeczy pod teksturą / dół"). Pull the near right up to the boots and cap the
        // far to a walker's horizon so depth precision stays high with such a tiny near.
        if (walkActive)
        {
            Camera.NearPlane = 0.3f;
            Camera.FarPlane = MathF.Min(far, 16_000f);
        }

        // LOCAL camera floor — kept CameraClearanceMeters (real) above the terrain DIRECTLY UNDER THE EYE,
        // sampled live from the DEM at the eye's own ground position. Sampling under the EYE (not the look
        // target) is a HARD no-tunnelling guarantee: the camera can never drop below the ground it is
        // physically over. (Sampling under the target let the eye sink below a ridge between it and the
        // valley it was aimed at — "I can go under the map".) To sit 100 m above a particular valley, move
        // the camera OVER that valley (zoom / pan); the floor then follows that terrain. Clearance is added
        // in REAL metres (inside the exaggeration) so it stays a true 100 m at any Pion. Refreshed per frame.
        // Walk mode owns the camera (eye pinned to ground + eye height by the walk tick); the fly-camera floor
        // would shove it 5 m up off the surface, so skip it while walking.
        if (!walkActive && Raster is { } floorRaster)
        {
            GeoPoint eyeGeo = frame.WorldToGeo(Camera.Position);
            double groundElev = floorRaster.SampleBilinear(eyeGeo.Longitude, eyeGeo.Latitude);
            if (groundElev <= floorRaster.NoDataValue)
            {
                groundElev = double.MinValue;
            }

            // ANTI-TUNNELLING (2026-07-03, "wjazd w powierzchnię mapy zdarza się często"): the coarse raster
            // above is box-averaged 30 m data that understates ridges by metres, while the RENDERED surface
            // is the baked 1 m z16 — and a single sample under the eye misses the wall the camera is flying
            // TOWARD. Probe the TRUE baked surface at the eye, ahead of it and on a small ring, and let the
            // floor track the HIGHEST of all samples. Falls back to the coarse sample where nothing is baked.
            if (FineElevationSampler is { } fineSampler)
            {
                Vector3 towardTarget = Camera.Target - Camera.Position;
                foreach (System.Numerics.Vector2 probe in MapaTur.Application.Terrain.CameraFloorProbe.ProbePoints(
                    new System.Numerics.Vector2(Camera.Position.X, Camera.Position.Y),
                    new System.Numerics.Vector2(towardTarget.X, towardTarget.Y),
                    aheadMeters: 35f,
                    ringRadiusMeters: 12f))
                {
                    GeoPoint probeGeo = frame.WorldToGeo(new Vector3(probe.X, probe.Y, 0f));
                    if (fineSampler(probeGeo.Longitude, probeGeo.Latitude) is { } fineElev && fineElev > groundElev)
                    {
                        groundElev = fineElev;
                    }
                }
            }

            if (groundElev > double.MinValue)
            {
                controller.CameraFloorZ = (float)((groundElev + CameraClearanceMeters) * frame.VerticalExaggeration);
            }
        }

        // Hard altitude ceiling: the eye may not rise above CameraCeilingMeters of REAL altitude (× Pion to
        // world-Z), so raise / zoom-out can't fly the camera off above the scene. Combined with the floor it
        // pins the camera into a sane vertical band over the map. In "2D map" mode the ceiling LIFTS to
        // MapModeCeilingMeters: keeping the raise pad pressed past the 3D ceiling becomes the map's
        // zoom-out (and lowering zooms back in, all the way down through the exit altitude into 3D).
        double ceilingMeters = mapMode.IsActive ? MapModeCeilingMeters : CameraCeilingMeters;
        controller.CameraCeilingZ = (float)(ceilingMeters * frame.VerticalExaggeration);

        // The legacy MaxTargetElevation was a FIXED 8000 *world units* — at Pion 3.4× that is only ~2353 m of
        // REAL altitude, BELOW the peaks, so "raise"/zoom-out hit it long before the ceiling and the camera
        // couldn't even climb above the mountains ("4 km is very low"). Scale the look-point cap with the
        // exaggeration too, with headroom above the ceiling, so the eye-ceiling above is the real limiter.
        controller.MaxTargetElevation = (float)((ceilingMeters + 2_000.0) * frame.VerticalExaggeration);

        // ENFORCE the dynamic floor + bounds EVERY frame, not just on the gestures that opt in. ApplyOrbit
        // deliberately skips the floor (to avoid juddering the distance on small pitch changes), so a rotation
        // — especially orbiting to the far side — used to swing the eye UNDER the terrain, where the
        // (un-culled, double-sided) surface shows its textured underside ("mountains textured from inside,
        // from both sides"). Re-applying the limit here, after the floor is sampled for the current eye
        // position, keeps the eye above the map however it got there (orbit, a restored stale camera, …), so
        // the camera can never see the inside of the terrain and there is nothing in there to render.
        // Skipped in walk mode, where the walk tick's ground-clamp is the authority on the eye's height —
        // AND in dragon flight: the chase camera sits ~13 m behind the dragon, far under the orbit
        // controller's MinDistance (150 m), so this clamp silently shoved the eye out to 150 m every frame
        // ("smok zawsze daleko"). The dragon keeps its own 30 m swoop clearance above terrain.
        if (!walkActive && !dragonActive)
        {
            controller.ClampToBounds();
        }

        // "2D map" mode: feed the policy the REAL eye altitude (world-Z ÷ Pion). While the morph is in
        // flight (Blend > 0) the mode OWNS the pitch — swinging it from the saved entry pitch to nadir on
        // the way up, and back to the exact saved pitch on the way down, so the user re-enters 3D at the
        // new location under the angle they were looking from. The ortho ↔ hypsometric fade follows the
        // same eased blend in the GL renderer (OrthoGlobalFade).
        double mapNow = mapModeClock.Elapsed.TotalSeconds;
        double mapDt = Math.Clamp(mapNow - mapModeLastSeconds, 0.0, 0.1);
        mapModeLastSeconds = mapNow;
        mapMode.Update(
            Camera.Position.Z / frame.VerticalExaggeration, mapDt, Camera.PitchRadians, Camera.AzimuthRadians);
        // Map-mode speed boost: pan, raise/lower (the map's zoom) AND rotation run ×2 at full map view,
        // ramping with the blend so the hand-off is seamless. PanSensitivity scales ApplyPan + ApplyVertical.
        controller.PanSensitivity = 0.001f * (1f + mapMode.Blend);
        controller.OrbitSensitivity = 0.005f * (1f + mapMode.Blend);
        if (mapMode.Blend > 0f)
        {
            float t = mapMode.Blend;
            float eased = t * t * (3f - (2f * t)); // smoothstep — no pitch snap at either end
            Camera.PitchRadians = mapMode.SavedPitchRadians + ((NadirPitchRadians - mapMode.SavedPitchRadians) * eased);
            // The azimuth stays LIVE in map mode: rotating the map (the ↻↺ pads / orbit drag) is a
            // deliberate orientation choice, so descending keeps it — the pitch override above is what
            // guarantees the entry GAZE TILT comes back; only the heading follows the user's map rotation.

            // FOLD the rig once fully in map view: target on the ground plane, ALL altitude in Distance.
            // Entry leaves the altitude split between Target.Z (raised by the Wys pads) and the orbit
            // distance — pinch-in then only shrinks the distance and BOTTOMS OUT at the sky-high target
            // ("opuszczanie nie działa"). Folded, the pads and pinch turn the SAME dial, so descending
            // always passes the exit altitude and lands back in 3D.
            if (mapMode.IsActive && t >= 0.999f)
            {
                float eyeZ = Camera.Position.Z;
                Camera.Target = new Vector3(Camera.Target.X, Camera.Target.Y, 0f);
                Camera.Distance = Math.Clamp(eyeZ, controller.MinDistance, controller.MaxDistance);
            }

            controller.ClampToBounds();
        }

        // Project the MARKER overlays (climbing / POI / peaks / GPS) up front — the GL path draws these as
        // Skia labels composited on top of the terrain, so it needs them before presenting. Trails + the
        // route are deliberately NOT projected here: on the GL path the GPU draws them as depth-tested
        // ribbons, so a screen projection would be pure waste discarded every frame. They're projected
        // lazily in the Skia fallback below, right before use. The stateful projectors reuse their world
        // cache + screen buffers, so during a gesture this is just the per-frame screen transform, against
        // the shared world frame (tile 0), with the same camera, so GL and Skia line up.
        double occlusionMs = 0;
        int occlusionMarkers = 0;

        // Occlusion cache gate: the per-marker DEM raycast (in HideOccluded*) is the dominant per-frame CPU
        // cost, yet a still camera under the 15 fps repaint timer re-ran it every frame for nothing. Decide
        // ONCE per paint whether to recompute — only when the eye moves past the threshold or the LOD frame
        // changes; otherwise both passes reuse the cached visibility (zero raycasts).
        System.Numerics.Vector3 occlusionCam = Camera.Position;
        bool occlusionRecompute = !hasOcclusionCache
            || !ReferenceEquals(lastOcclusionFrame, frame)
            || System.Numerics.Vector3.DistanceSquared(occlusionCam, lastOcclusionCamPos) > OcclusionCacheMoveThresholdSq;

        IReadOnlyList<ProjectedClimbingArea>? projectedClimbing = null;
        if (ClimbingAreas is { Count: > 0 } areas && Raster is not null)
        {
            projectedClimbing = climbingProjector.Project(
                areas, Raster, frame, Camera, e.Info.Width, e.Info.Height, ClimbingMarkerLiftMeters,
                maxDistanceMeters: (float)PeakLabelRadiusMeters); // obey the "zasięg" slider, like POI + peaks
        }

        IReadOnlyList<ProjectedPoi>? projectedPois = null;
        if (Pois is { Count: > 0 } pois && Raster is not null)
        {
            projectedPois = poiProjector.Project(
                pois, Raster, frame, Camera, e.Info.Width, e.Info.Height, PoiMarkerLiftMeters,
                detail: DetailElevation, // seat on the rendered 1 m surface so pass dots don't float over saddles
                maxDistanceMeters: (float)PeakLabelRadiusMeters); // obey the "zasięg" slider, same as peak + lake labels
            occlusionMarkers += projectedPois.Count;
            var sw = DebugEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            projectedPois = HideOccludedPois(projectedPois, frame, occlusionRecompute);
            if (sw is not null) { occlusionMs += sw.Elapsed.TotalMilliseconds; }
        }

        // Peaks carry their own DEM elevation, so projection needs no raster lookup.
        IReadOnlyList<ProjectedPeak>? projectedPeaks = null;
        if (ShowPeakNames && Peaks is { Count: > 0 } peaks)
        {
            projectedPeaks = peakProjector.Project(
                peaks, null, frame, Camera, e.Info.Width, e.Info.Height, PeakMarkerLiftMeters,
                maxDistanceMeters: (float)PeakLabelRadiusMeters);
            occlusionMarkers += projectedPeaks.Count;
            var sw = DebugEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            projectedPeaks = HideOccludedPeaks(projectedPeaks, frame, occlusionRecompute);
            if (sw is not null) { occlusionMs += sw.Elapsed.TotalMilliseconds; }
        }

        if (occlusionRecompute)
        {
            lastOcclusionCamPos = occlusionCam;
            lastOcclusionFrame = frame;
            hasOcclusionCache = true;
        }

        if (DebugEnabled)
        {
            lastOcclusionMs = occlusionMs;
            lastOcclusionMarkers = occlusionMarkers;
        }

        // Single GPS fix: wrap into our reusable one-element buffer so the projector keeps its
        // world-cache hit when only the contained data shifts (it compares list reference).
        ProjectedUserLocation? projectedUserLocation = null;
        if (UserLocation is { } fix)
        {
            userLocationBuffer[0] = fix;
            IReadOnlyList<ProjectedUserLocation> projected = userLocationProjector.Project(
                userLocationBuffer, Raster, frame, Camera, e.Info.Width, e.Info.Height, UserLocationMarkerLiftMeters);
            if (projected.Count > 0)
            {
                projectedUserLocation = projected[0];
            }
        }

        // Remember the projected markers + surface pixel size so a tap can hit-test against them.
        lastProjectedClimbing = projectedClimbing;
        lastProjectedPois = projectedPois;
        lastSurfacePixelWidth = e.Info.Width;
        lastSurfacePixelHeight = e.Info.Height;

#if WINDOWS || ANDROID
        // GPU engine: GL draws the depth-buffered terrain into a colour TEXTURE we own, which Skia then
        // composes into the surface via DrawImage. Sidesteps the FBO-0 collision on Android (where Skia
        // would re-paint over anything we drew into its on-screen FBO) and lets the same code path work on
        // Windows. Any GL/shader/wrapper failure disables it for the session and falls through to Skia.
        double dbgPrepMs = dbgSwapPaint ? dbgPaintWatch.Elapsed.TotalMilliseconds : 0;
        if (UseGlRenderer && TryRenderTerrainGl(canvas, tiles, e.Info.Width, e.Info.Height))
        {
            double dbgGlMs = dbgSwapPaint ? dbgPaintWatch.Elapsed.TotalMilliseconds - dbgPrepMs : 0;
            // GL already drew the (depth-occluded) trails + route; Skia only adds the markers/labels on top.
            // POI text labels only when the camera is close — a far view of 1000+ POIs is a wall of text.
            bool poiLabelsVisible = Camera.Distance < Services.Terrain3DCanvasRenderer.PoiLabelMaxDistanceWorld;
            renderer.UserLocationFreshness = UserLocationFreshness;
            renderer.DrawOverlays(canvas, null, null, projectedClimbing, projectedPois, projectedPeaks, projectedUserLocation, poiLabelsVisible);
            DrawLakeLabelsOverScene(canvas, frame, e.Info.Width, e.Info.Height);
            DrawStarLabelsOverScene(canvas, frame, e.Info.Width, e.Info.Height);
            DrawNightLights(canvas, projectedPois);
            DrawFlightMarker(canvas, e.Info.Width, e.Info.Height);
            DrawWalkViewmodel(canvas, e.Info.Width, e.Info.Height);
            DrawDragon(canvas, e.Info.Width, e.Info.Height);
            if (dbgSwapPaint)
            {
                double dbgTotalMs = dbgPaintWatch.Elapsed.TotalMilliseconds;
                if (dbgTotalMs > 100)
                {
                    // prep = marker projection + occlusion before the GL call; gl = TryRenderTerrainGl
                    // (its internal split is the renderer's own hitch line); skia = the overlay draw above.
                    Serilog.Log.Information(
                        "[GL3D] swap paint breakdown: prep={Prep:F0} gl={Gl:F0} skia={Skia:F0} total={Total:F0}ms",
                        dbgPrepMs, dbgGlMs, dbgTotalMs - dbgPrepMs - dbgGlMs, dbgTotalMs);
                }
            }

            // Recording capture for this path happens inside TryRenderTerrainGl (GL FBO readback), not here.
            return;
        }
#endif

        // Skia (CPU) fallback only — reached when the GL renderer is off or has failed for the session.
        // Project the trail + route overlays NOW (the GL path above draws them on the GPU and never needs
        // these), right before the Skia renderer consumes them.
        IReadOnlyList<ProjectedTrail>? projectedTrails = null;
        if (Trails is { Count: > 0 } trailsList && Raster is not null)
        {
            projectedTrails = trailProjector.Project(
                trailsList, Raster, frame, Camera, e.Info.Width, e.Info.Height, detail: DetailElevation);
        }

        ProjectedRoute? projectedRoute = null;
        if (Route is not null && Raster is not null)
        {
            projectedRoute = routeProjector.Project(
                Route, Raster, frame, Camera, e.Info.Width, e.Info.Height, detail: DetailElevation);
        }

        // depthMap = null disables trail / route / climbing occlusion: trails are drawn always on top
        // of the mesh (the visual the user wants) and it drops a per-frame depth-grid fill.
        renderer.UserLocationFreshness = UserLocationFreshness;
        renderer.RenderTiles(canvas, e.Info.Width, e.Info.Height, tiles, Camera, frameScratch, null, projectedTrails, projectedRoute, projectedClimbing, projectedPois, projectedPeaks, projectedUserLocation);
        DrawNightLights(canvas, projectedPois);
        DrawWalkViewmodel(canvas, e.Info.Width, e.Info.Height);
        DrawDragon(canvas, e.Info.Width, e.Info.Height);
        ServiceRecording(e);
    }

    // Per-frame recording service: lazily starts the recording once the surface size is known, then
    // reads back the freshly-composited frame and feeds it to the encoder. No-op when not recording.
    private void ServiceRecording(SKPaintGLSurfaceEventArgs e)
    {
        if (videoRecorder is null)
        {
            return;
        }

        if (recordingRequested && !videoRecorder.IsRecording)
        {
            videoRecorder.TryStart(e.Info.Width, e.Info.Height, RecordingFrameRate, BuildRecordingOutputPath());
        }

        if (!videoRecorder.IsRecording)
        {
            return;
        }

        CaptureSurfaceFrame(e.Surface, videoRecorder.FrameWidth, videoRecorder.FrameHeight);
    }

    private const int RecordingFrameRate = 30;

    // GL-path recording capture: lazily starts the recording, then reads the just-rendered frame straight
    // from the GL renderer's present FBO (reliable, unlike a Skia surface snapshot which returned a stale
    // back-buffer for every frame after the first). Terrain + GL-drawn trails/route are captured; Skia
    // overlays (peak labels, markers) are not part of this readback.
    private void CaptureGlFrameForRecording(int width, int height)
    {
#if WINDOWS || ANDROID
        if (videoRecorder is null || glRenderer is null)
        {
            return;
        }

        if (recordingRequested && !videoRecorder.IsRecording)
        {
            videoRecorder.TryStart(width, height, RecordingFrameRate, BuildRecordingOutputPath());
        }

        if (!videoRecorder.IsRecording)
        {
            return;
        }

        int w = videoRecorder.FrameWidth;
        int h = videoRecorder.FrameHeight;
        int needed = w * h * 4;
        if (recordBuffer is null || recordBuffer.Length < needed)
        {
            recordBuffer = new byte[needed];
        }

        if (glRenderer.TryReadPresentFrame(recordBuffer, w, h))
        {
            videoRecorder.CaptureFrame(recordBuffer);
        }
#endif
    }

    // CPU (Skia) fallback capture: snapshot the composited surface and read it back. Used only when the
    // GL renderer isn't active (rare); the GL path uses CaptureGlFrameForRecording instead.
    private void CaptureSurfaceFrame(SKSurface surface, int width, int height)
    {
        if (videoRecorder is null || width <= 0 || height <= 0)
        {
            return;
        }

        int needed = width * height * 4;
        if (recordBuffer is null || recordBuffer.Length < needed)
        {
            recordBuffer = new byte[needed];
        }

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(recordBuffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            using SKImage image = surface.Snapshot();
            if (image.ReadPixels(info, handle.AddrOfPinnedObject(), width * 4, 0, 0))
            {
                videoRecorder.CaptureFrame(recordBuffer);
            }
        }
        finally
        {
            handle.Free();
        }
    }

    private static string BuildRecordingOutputPath()
    {
        string name = $"OrlaPerc_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
        return System.IO.Path.Combine(FileSystem.Current.CacheDirectory, name);
    }

    // Warm window lights that switch on in every refuge (hut / wilderness hut / chalet / shelter)
    // once the sun drops below the horizon — a small, atmospheric night-time touch. Drawn additively
    // over the finished frame so they glow against the dark terrain. Fades in across dusk via the
    // sun elevation and stays off entirely in daylight (zero cost: the loop early-returns).
    private void DrawNightLights(SKCanvas canvas, IReadOnlyList<ProjectedPoi>? pois)
    {
        if (pois is null || pois.Count == 0 || EffectiveAtmosphere is not { } atmo)
        {
            return;
        }

        // nightFactor: 0 above ~6° sun elevation, ramping to 1 once the sun is ~6° below the
        // horizon — so lights warm up through dusk rather than snapping on at the exact sunset.
        float sunUp = atmo.SunDirection.Z; // sin(elevation)
        float nightFactor = Math.Clamp((0.10f - sunUp) / 0.20f, 0f, 1f);
        if (nightFactor <= 0.01f)
        {
            return;
        }

        byte haloAlpha = (byte)(nightFactor * 150f);
        byte glowAlpha = (byte)(nightFactor * 230f);
        byte coreAlpha = (byte)(nightFactor * 255f);
        const float haloRadius = 16f; // soft outer bloom
        const float glowRadius = 7f;  // warm body
        const float coreRadius = 2.6f; // hot near-white centre

        using var haloPaint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };
        using var glowPaint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };
        using var corePaint = new SKPaint { IsAntialias = true, BlendMode = SKBlendMode.Plus };

        foreach (ProjectedPoi poi in pois)
        {
            if (!IsLitRefuge(poi.Source.Kind) || poi.ScreenPosition is not { } sp)
            {
                continue;
            }

            float x = sp.X;
            float y = sp.Y;
            // Three additive layers: a wide soft bloom, a warmer body, and a hot near-white core,
            // so several nearby lights blend into a glowing hamlet against the dark night terrain.
            haloPaint.Shader = SKShader.CreateRadialGradient(
                new SKPoint(x, y), haloRadius,
                new[] { new SKColor(0xFF, 0xC8, 0x70, haloAlpha), new SKColor(0xFF, 0xA0, 0x30, 0) },
                null, SKShaderTileMode.Clamp);
            canvas.DrawCircle(x, y, haloRadius, haloPaint);

            glowPaint.Shader = SKShader.CreateRadialGradient(
                new SKPoint(x, y), glowRadius,
                new[] { new SKColor(0xFF, 0xDC, 0x88, glowAlpha), new SKColor(0xFF, 0xB0, 0x40, 0) },
                null, SKShaderTileMode.Clamp);
            canvas.DrawCircle(x, y, glowRadius, glowPaint);

            corePaint.Color = new SKColor(0xFF, 0xF4, 0xCC, coreAlpha);
            canvas.DrawCircle(x, y, coreRadius, corePaint);
        }

        haloPaint.Shader = null;
        glowPaint.Shader = null;
    }

    // Drops peak markers whose summit is hidden behind a ridge from the camera. The GL terrain is
    // depth-buffered but these Skia labels are drawn on top with no depth test, so without this a peak
    // label "punches through" the ridge in front of it. Off-screen markers are skipped (won't draw).
    // Projects tonight's bundled named stars and draws their names over the GL scene — only at night (so the
    // labels track the GL star pass, which is gated the same way) and only for the few stars above the horizon
    // and in frame. Uses the shared world frame (tile 0) anchor + the time-of-day slider hour, exactly like the
    // Moving "you are here" dot during a route film: projects the current route point to screen and draws a
    // pulsing violet dot (matching the route line) with a white ring + halo, on top of the scene so it reads
    // even when the spot is tucked behind a near ridge. Drawn only while the flight is running.
    private void DrawFlightMarker(SKCanvas canvas, int width, int height)
    {
        if (flightMarkerWorld is not { } world)
        {
            return;
        }

        var viewProjection = Camera.BuildViewProjection((float)width / Math.Max(1, height));
        if (Camera.ProjectToScreen(world, viewProjection, width, height) is not { } s)
        {
            return; // behind the camera / off-screen
        }

        float pulse = 1f + (0.22f * MathF.Sin((float)flightElapsedSeconds * 4.5f));
        float r = 8f * pulse;
        using var halo = new SKPaint { Color = new SKColor(0x7C, 0x3A, 0xED, 0x55), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var fill = new SKPaint { Color = new SKColor(0x7C, 0x3A, 0xED), IsAntialias = true, Style = SKPaintStyle.Fill };
        using var ring = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f };
        canvas.DrawCircle(s.X, s.Y, r * 2.1f, halo);
        canvas.DrawCircle(s.X, s.Y, r, fill);
        canvas.DrawCircle(s.X, s.Y, r, ring);
    }

    // GL star buffer, so each name sits on its dot.
    private void DrawStarLabelsOverScene(SKCanvas canvas, TerrainMesh3D frame, int width, int height)
    {
        if (!ShowNightSky || EffectiveAtmosphere is not { } atmo || atmo.SunDirection.Z >= 0f)
        {
            return; // night sky off, or daytime — the GL stars (and so their labels) are invisible
        }

        var anchor = frame.ProjectionAnchor;
        DateTime now = DateTime.Now;
        var viewProjection = Camera.BuildViewProjection((float)width / Math.Max(1, height));
        IReadOnlyList<StarLabel> labels = StarLabelProjector.ProjectForLocalDate(
            StarCatalogData.Bundled, now.Year, now.Month, now.Day, atmo.TimeOfDayHours,
            anchor.Latitude, anchor.Longitude, viewProjection, width, height);
        renderer.DrawConstellationLines(canvas, ConstellationLines.ResolveScreenSegments(labels));
        renderer.DrawStarLabels(canvas, labels);

        // Moon name + phase label (helps locate the thin crescent), projected the same way as the stars and
        // lifted clear of the disc.
        MoonSky moon = NightSky.MoonForLocalDate(
            now.Year, now.Month, now.Day, atmo.TimeOfDayHours, anchor.Latitude, anchor.Longitude);
        IReadOnlyList<StarLabel> moonHit = StarLabelProjector.Project(
            new[] { (moon.MoonDirection, 0f, string.Format(System.Globalization.CultureInfo.CurrentUICulture, AppStrings.MoonLabelFormat, moon.IlluminatedFraction * 100f)) },
            viewProjection, width, height);
        if (moonHit.Count > 0)
        {
            StarLabel m = moonHit[0];
            renderer.DrawStarLabels(canvas, new[] { new StarLabel(m.Name, m.ScreenX, m.ScreenY - 26f, m.Magnitude) });
        }
    }

    // Lake-name labels: project each NAMED tarn's outline centroid (lifted just above the water) and reuse the
    // star-label text pass so the name sits over the lake. Shares the peak-names ("Nazwy") toggle, the range
    // slider, AND the DEM occlusion raycast — so a lake name behind a ridge hides exactly like a peak / POI name.
    private void DrawLakeLabelsOverScene(SKCanvas canvas, TerrainMesh3D frame, int width, int height)
    {
        if (!ShowPeakNames)
        {
            return;
        }

        const float lift = 6f;
        var viewProjection = Camera.BuildViewProjection((float)width / Math.Max(1, height));
        Vector3 cameraPos = Camera.Position;
        var occlusionRaster = Raster;
        float radiusSquared = (float)PeakLabelRadiusMeters * (float)PeakLabelRadiusMeters; // obey the "zasięg" slider, same as peak labels
        var labels = new List<StarLabel>();
        foreach (var lake in MapaTur.Application.Terrain.MountainLakeData.WithinBounds(frame.Bounds))
        {
            int n = lake.Outline.Count;
            if (n == 0 || string.IsNullOrEmpty(lake.Name))
            {
                continue;
            }

            double cLat = 0, cLon = 0;
            for (int i = 0; i < n; i++)
            {
                cLat += lake.Outline[i].Latitude;
                cLon += lake.Outline[i].Longitude;
            }

            var centroid = new MapaTur.Domain.Geography.GeoPoint(cLat / n, cLon / n);
            Vector3 world = frame.GeoToWorld(centroid, (float)lake.ElevationMeters + lift);
            if (Vector3.DistanceSquared(cameraPos, world) > radiusSquared)
            {
                continue; // beyond the range slider — drop it, exactly like peak labels
            }

            if (occlusionRaster is not null
                && !MapaTur.Application.Terrain.TerrainOcclusion.IsVisible(cameraPos, world, occlusionRaster, frame.ProjectionAnchor, frame.VerticalExaggeration))
            {
                continue; // behind a ridge / rock — hidden like the peak + POI labels
            }

            Vector3? screen = Camera.ProjectToScreen(world, viewProjection, width, height);
            if (screen is { } s && s.X >= 0f && s.X <= width && s.Y >= 0f && s.Y <= height)
            {
                labels.Add(new StarLabel(lake.Name, s.X, s.Y, 0f));
            }
        }

        if (labels.Count > 0)
        {
            renderer.DrawStarLabels(canvas, labels);
        }
    }

    // Occlusion cache (perf). The per-marker DEM raycast is a pure function of the eye position + terrain
    // frame, but the ~15 fps repaint timer used to re-run it EVERY frame even for a perfectly still camera —
    // the cost that dominated a POI-heavy Tatra view. Now we recompute only when the eye moves past a small
    // threshold or the LOD frame changes (decided once per paint in OnPaintSurface); a static view reuses the
    // cached visibility and does ZERO raycasts while the clouds keep drifting. We cache the VISIBLE marker
    // locations (stable per marker across frames) and re-filter the freshly-projected list each frame.
    private const float OcclusionCacheMoveThresholdSq = 15f * 15f;
    private System.Numerics.Vector3 lastOcclusionCamPos;
    private TerrainMesh3D? lastOcclusionFrame;
    private bool hasOcclusionCache;
    private readonly HashSet<MapaTur.Domain.Geography.GeoPoint> visiblePeakLocations = new();
    private readonly HashSet<MapaTur.Domain.Geography.GeoPoint> visiblePoiLocations = new();

    private IReadOnlyList<ProjectedPeak> HideOccludedPeaks(IReadOnlyList<ProjectedPeak> peaks, TerrainMesh3D frame, bool recompute)
    {
        if (Raster is not { } raster)
        {
            return peaks;
        }

        if (recompute)
        {
            System.Numerics.Vector3 cam = Camera.Position;
            int n = peaks.Count;

            // Each marker's occlusion is an independent, read-only DEM raycast, so fan the march out across
            // cores for large marker sets. keep[] preserves list order; small sets stay sequential.
            var keep = new bool[n];
            if (n >= OcclusionParallelThreshold)
            {
                System.Threading.Tasks.Parallel.For(0, n, i => keep[i] = IsPeakVisible(peaks[i], cam, raster, frame));
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    keep[i] = IsPeakVisible(peaks[i], cam, raster, frame);
                }
            }

            visiblePeakLocations.Clear();
            for (int i = 0; i < n; i++)
            {
                if (keep[i])
                {
                    visiblePeakLocations.Add(peaks[i].Source.Location);
                }
            }
        }

        // Re-filter the freshly-projected list (current screen positions) by the cached visibility. A marker
        // that just entered the frame defaults to hidden until the next recompute (errs on the safe side —
        // never flashes a name through a ridge), which the small move-threshold resolves within a step or two.
        var visible = new List<ProjectedPeak>(peaks.Count);
        foreach (ProjectedPeak p in peaks)
        {
            if (p.ScreenPosition is not null && visiblePeakLocations.Contains(p.Source.Location))
            {
                visible.Add(p);
            }
        }

        return visible;
    }

    private static bool IsPeakVisible(ProjectedPeak p, System.Numerics.Vector3 cam, DemRaster raster, TerrainMesh3D frame)
    {
        if (p.ScreenPosition is null)
        {
            return false; // off-screen — it won't draw, so skip its raycast
        }

        System.Numerics.Vector3 world = frame.GeoToWorld(p.Source.Location, (float)p.Source.ElevationMeters);
        return MapaTur.Application.Terrain.TerrainOcclusion.IsVisible(
            cam, world, raster, frame.ProjectionAnchor, frame.VerticalExaggeration);
    }

    // As HideOccludedPeaks, for POIs — a hut / parking / viewpoint behind a ridge hides its marker too.
    private IReadOnlyList<ProjectedPoi> HideOccludedPois(IReadOnlyList<ProjectedPoi> pois, TerrainMesh3D frame, bool recompute)
    {
        if (Raster is not { } raster)
        {
            return pois;
        }

        if (recompute)
        {
            System.Numerics.Vector3 cam = Camera.Position;
            float poiLift = PoiMarkerLiftMeters;
            int n = pois.Count;

            // As HideOccludedPeaks: independent read-only raycasts, fanned out across cores for large sets.
            var keep = new bool[n];
            if (n >= OcclusionParallelThreshold)
            {
                System.Threading.Tasks.Parallel.For(0, n, i => keep[i] = IsPoiVisible(pois[i], cam, raster, frame, poiLift));
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    keep[i] = IsPoiVisible(pois[i], cam, raster, frame, poiLift);
                }
            }

            visiblePoiLocations.Clear();
            for (int i = 0; i < n; i++)
            {
                if (keep[i])
                {
                    visiblePoiLocations.Add(pois[i].Source.Position);
                }
            }
        }

        var visible = new List<ProjectedPoi>(pois.Count);
        foreach (ProjectedPoi p in pois)
        {
            if (p.ScreenPosition is not null && visiblePoiLocations.Contains(p.Source.Position))
            {
                visible.Add(p);
            }
        }

        return visible;
    }

    private static bool IsPoiVisible(ProjectedPoi p, System.Numerics.Vector3 cam, DemRaster raster, TerrainMesh3D frame, float poiLift)
    {
        if (p.ScreenPosition is null)
        {
            return false; // off-screen — won't draw
        }

        double ground = raster.SampleBilinear(p.Source.Position.Longitude, p.Source.Position.Latitude);
        if (ground <= raster.NoDataValue)
        {
            return true; // no terrain sample — don't hide it
        }

        System.Numerics.Vector3 world = frame.GeoToWorld(p.Source.Position, (float)ground + poiLift);
        return MapaTur.Application.Terrain.TerrainOcclusion.IsVisible(
            cam, world, raster, frame.ProjectionAnchor, frame.VerticalExaggeration);
    }

    // Refuges that "light up" at night: everything with a roof a hiker could be inside. Viewpoints
    // (lookout towers / panoramas) are excluded — nobody's home to switch a lamp on.
    private static bool IsLitRefuge(Domain.Pois.PoiKind kind) => kind is
        Domain.Pois.PoiKind.Hut or
        Domain.Pois.PoiKind.WildernessHut or
        Domain.Pois.PoiKind.Chalet or
        Domain.Pois.PoiKind.Shelter;

    /// <summary>
    /// 1-finger drag on the mesh = PAN. World moves under the finger (Cesium / Google Maps
    /// convention). Translation only — rotation lives on the orbit-gizmo widget so a drag never
    /// has a hidden second meaning. ApplyPan inverts the deltas internally so the world tracks
    /// the finger rather than runs away from it.
    /// </summary>
    private void OnMeshPan(object? sender, PanUpdatedEventArgs e)
    {
        // On Windows the mouse is driven by the raw pointer handlers (OnPlatformPointer*); the touch
        // PanGestureRecognizer would otherwise fight them. Touch platforms keep using this gesture.
        if (DeviceInfo.Current.Platform == DevicePlatform.WinUI)
        {
            return;
        }

        switch (e.StatusType)
        {
            case GestureStatus.Started:
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                lastOrbitTotalX = 0;
                lastOrbitTotalY = 0;
                return;
            case GestureStatus.Running:
                // During a fly-through, ignore tiny touches/jitter (a resting finger must not kill
                // the demo); only a deliberate drag past the threshold takes manual control.
                if (IsFlying)
                {
                    if (Math.Abs(e.TotalX) + Math.Abs(e.TotalY) < FlightCancelDragPx)
                    {
                        return;
                    }
                    StopFlight();
                }
                float dx = (float)(e.TotalX - lastOrbitTotalX);
                float dy = (float)(e.TotalY - lastOrbitTotalY);
                lastOrbitTotalX = e.TotalX;
                lastOrbitTotalY = e.TotalY;
                // Drag-to-pan: ApplyPan moves the camera target along world axes derived from
                // camera azimuth. Drag finger left (dx<0) -> world tracks finger left -> camera
                // moves right -> target shifts +right -> pass -dx. Vertically the screen Y axis
                // is inverted relative to the controller's "forward" vector (screen Y grows down,
                // forward grows up the screen) so dy comes through with the same sign as dx.
                controller.ApplyPan(-dx, dy);
                Canvas.InvalidateSurface();
                return;
        }
    }

    private void OnTranslatePan(object? sender, PanUpdatedEventArgs e)
    {
        if (DeviceInfo.Current.Platform == DevicePlatform.WinUI)
        {
            return;
        }

        switch (e.StatusType)
        {
            case GestureStatus.Started:
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
                lastTranslateTotalX = 0;
                lastTranslateTotalY = 0;
                return;
            case GestureStatus.Running:
                float dx = (float)(e.TotalX - lastTranslateTotalX);
                float dy = (float)(e.TotalY - lastTranslateTotalY);
                lastTranslateTotalX = e.TotalX;
                lastTranslateTotalY = e.TotalY;
                // Drag-to-pan: world tracks the fingers, so invert deltas.
                controller.ApplyPan(-dx, -dy);
                Canvas.InvalidateSurface();
                return;
        }
    }

    // pinchActive used to mediate the 2-finger pan vs pinch race. The Cesium-style gesture set
    // doesn't bind 2-finger pan at all, so the flag is no longer read — kept (with pragma) only
    // because OnPinch still sets it, in case we later restore a 2-finger gesture that needs to
    // know when zoom is in progress.
#pragma warning disable CS0414, IDE0044
    private bool pinchActive;
#pragma warning restore CS0414, IDE0044

    private void OnPinch(object? sender, PinchGestureUpdatedEventArgs e)
    {
        if (e.Status == GestureStatus.Started)
        {
            StopFlight(); // a pinch-zoom cancels an in-progress fly-through
            pinchActive = true;
            lastPinchScale = 1.0;
            return;
        }
        if (e.Status == GestureStatus.Completed || e.Status == GestureStatus.Canceled)
        {
            pinchActive = false;
            return;
        }
        if (e.Status != GestureStatus.Running)
        {
            return;
        }

        // Platform divergence in PinchGestureUpdatedEventArgs.Scale:
        //   - Windows / macOS / iOS report CUMULATIVE scale relative to gesture start, so per-frame
        //     delta = current / lastReported.
        //   - Android reports PER-UPDATE delta directly (each update is the multiplier vs. the
        //     previous one, typically 0.97..1.03 even mid-gesture).
        // Confirmed against `adb logcat`: 10 consecutive Running updates each at 0.987 — would mean
        // cumulative -1.3% total if treated as cumulative, but actually mean -1.3% PER STEP.
        // Trust the platform-native interpretation rather than try to unify in one direction.
        double perFrame;
#if ANDROID
        perFrame = e.Scale;
#else
        if (lastPinchScale <= 0)
        {
            lastPinchScale = e.Scale;
        }
        perFrame = e.Scale / lastPinchScale;
        lastPinchScale = e.Scale;
#endif

        // Small boost so a tiny finger-spread (perFrame ~ 1.02) still produces a visible zoom step on a
        // phone screen, without making pinch lurch. 2.5 was too fast ("zoom shoots off"); 1.5 is gentle.
        double boosted = Math.Pow(perFrame, 1.5);
        controller.ApplyZoom((float)boosted);
        Canvas.InvalidateSurface();
    }

    /// <summary>Raised when a tap lands on bare terrain (no marker under the finger): the tapped point
    /// in WGS-84, resolved by raycasting the tap pixel into the DEM. The host decides what it means —
    /// MapPage routes it into the same tap-to-plan waypoint flow the 2D map uses.</summary>
    public event EventHandler<GeoPoint>? TerrainTapped;

    /// <summary>
    /// A single tap on the terrain: hit-test the cached projected markers first and raise
    /// <see cref="MarkerTapped"/> for the front-most hit; otherwise raycast the tap pixel into the
    /// terrain and raise <see cref="TerrainTapped"/> with the WGS-84 point (3D tap-to-plan).
    /// </summary>
    private void OnCanvasTapped(object? sender, TappedEventArgs e)
    {
        if ((MarkerTapped is null && TerrainTapped is null) || IsFlying)
        {
            return;
        }

        if (lastSurfacePixelWidth <= 0 || lastSurfacePixelHeight <= 0)
        {
            return;
        }

        Point? position = e.GetPosition(Canvas);
        if (position is not { } pt)
        {
            return;
        }

        // GetPosition reports device-independent units; the projected screen positions are in surface
        // pixels. Scale the tap up by the per-axis pixel density and use the same scale for the radius.
        double diuWidth = Canvas.Width;
        double diuHeight = Canvas.Height;
        if (diuWidth <= 0 || diuHeight <= 0)
        {
            return;
        }

        float scaleX = (float)(lastSurfacePixelWidth / diuWidth);
        float scaleY = (float)(lastSurfacePixelHeight / diuHeight);
        float tapX = (float)pt.X * scaleX;
        float tapY = (float)pt.Y * scaleY;
        float radiusPx = MarkerTapRadiusDiu * MathF.Max(scaleX, scaleY);

        MarkerPopupContent? content = HitTestMarkers(tapX, tapY, radiusPx);
        if (content is { } popup)
        {
            MarkerTapped?.Invoke(this, popup);
            return;
        }

        // No marker under the finger → resolve the tapped TERRAIN point (tap-to-plan). The same
        // tested ray machinery as the look-at: pixel ray → DEM march → world hit → WGS-84.
        if (TerrainTapped is not null && WorldFrame is { } tapFrame && Raster is { } tapRaster)
        {
            System.Numerics.Vector3? hit = MapaTur.Application.Terrain.LookAtPoint.ResolveAt(
                Camera, tapX, tapY, lastSurfacePixelWidth, lastSurfacePixelHeight,
                tapRaster, tapFrame.ProjectionAnchor, tapFrame.VerticalExaggeration);
            if (hit is { } world)
            {
                TerrainTapped.Invoke(this, tapFrame.WorldToGeo(world));
            }
        }
    }

    // Hit-tests climbing areas and POIs independently, then returns popup content for whichever winner
    // is front-most (closest to the camera). Null when nothing is within the radius.
    private MarkerPopupContent? HitTestMarkers(float tapX, float tapY, float radiusPx)
    {
        ClimbingArea? climbing = null;
        float climbingDepth = float.PositiveInfinity;
        if (lastProjectedClimbing is { Count: > 0 } climbingList)
        {
            var positions = new Vector3?[climbingList.Count];
            for (int i = 0; i < climbingList.Count; i++)
            {
                positions[i] = climbingList[i].ScreenPosition;
            }

            if (MarkerHitTester.HitTest(positions, tapX, tapY, radiusPx) is { } hit)
            {
                climbing = climbingList[hit].Source;
                climbingDepth = climbingList[hit].ScreenPosition!.Value.Z;
            }
        }

        MountainPoi? poi = null;
        float poiDepth = float.PositiveInfinity;
        if (lastProjectedPois is { Count: > 0 } poiList)
        {
            var positions = new Vector3?[poiList.Count];
            for (int i = 0; i < poiList.Count; i++)
            {
                positions[i] = poiList[i].ScreenPosition;
            }

            if (MarkerHitTester.HitTest(positions, tapX, tapY, radiusPx) is { } hit)
            {
                poi = poiList[hit].Source;
                poiDepth = poiList[hit].ScreenPosition!.Value.Z;
            }
        }

        if (poi is not null && (climbing is null || poiDepth <= climbingDepth))
        {
            return MarkerPopupFormatter.ForPoi(poi, MarkerPopupLabels.Instance);
        }

        if (climbing is not null)
        {
            return MarkerPopupFormatter.ForClimbing(climbing, MarkerPopupLabels.Instance);
        }

        return null;
    }

    /// <summary>
    /// Puts keyboard focus on the 3D canvas so the arrow keys (pan) and WASD (orbit) work without the user
    /// first having to click it. Called by the host page when 3D mode is entered. No-op off Windows.
    /// </summary>
    public void FocusForKeyboard()
    {
#if WINDOWS
        // Focus() lives on UIElement; the platform view is a SwapChainPanel (not a Control).
        wheelTarget?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
#endif
    }

#if WINDOWS
    private Microsoft.UI.Xaml.UIElement? wheelTarget;
    private Microsoft.UI.Xaml.UIElement? keyboardRoot;
    private Microsoft.UI.Xaml.Input.KeyEventHandler? keyDownHandler;
    private Microsoft.UI.Xaml.Input.KeyEventHandler? keyUpHandler; // walk mode: release held movement keys

    // Mouse drag on Windows: MAUI's PanGestureRecognizer is touch/pen only, so we drive orbit/pan from raw
    // pointer events. Left button = orbit, right button = pan. 0 = not dragging.
    private int mouseDragButton;
    private Windows.Foundation.Point lastPointerPosition;
    private long lastF8ToggleMs; // debounce for the F8 walk toggle (dual listener + key-repeat)
    private long lastF7ToggleMs; // debounce for the F7 dragon-flight toggle

    // Keyboard-step constants tuned to feel close to one drag-pixel of the gesture
    // recognisers (controller.PanSensitivity = 0.001 m/px/m).
    private const float KeyPanPixelStep = 24f;
    private const float KeyZoomFactor = 1.1f;
    private const float KeyTiltPixelStep = 10f; // ~2.9° per repeat — view pitch (R/F, PgUp/PgDn)
    private const float KeyYawPixelStep = 20f; // rotate-in-place (look-around) yaw per repeat (Q/E)

    private void OnCanvasHandlerChanged(object? sender, EventArgs e)
    {
        DetachWheelHandler();

        // A new handler means a new platform view and a fresh GL context, so every GPU object the renderer
        // cached (program, VAOs, FBOs, textures) belongs to the dead context. Toggling 2D⇄3D recycles the
        // SKGLView handler this way; the IsProgram context-loss check inside the renderer doesn't always catch
        // it, leaving only the sky clear ("blue screen"). Drop the renderer so the next paint rebuilds clean.
        // Don't Dispose() here — that issues GL deletes with no context current; the old context's objects are
        // freed when it dies. Just release our reference and re-enable GL in case a failure had disabled it.
        glRenderer = null;
        glDisabled = false;
        // The fresh renderer has no ortho texture yet (it's pushed imperatively, not per frame), so re-flag it
        // for re-upload; trails/route/roads/peaks re-upload on their own since they're projected each frame.
        orthoPathDirty = true;

        if (Canvas.Handler?.PlatformView is Microsoft.UI.Xaml.UIElement element)
        {
            wheelTarget = element;
            wheelTarget.PointerWheelChanged += OnPointerWheelChanged;

            // Make the platform view focusable so keyboard events route here. The SKGLView platform view is a
            // SwapChainPanel (a UIElement, NOT a Control), so IsTabStop/Focus must be set on UIElement —
            // the old `is Control` cast silently failed and no key ever reached us.
            element.IsTabStop = true;

            // Subscribe via AddHandler with handledEventsToo:true rather than "+= KeyDown".
            // Character keys (WASD) bubble up from the focused child already marked Handled,
            // so a plain CLR subscription only ever sees the arrow keys; AddHandler with
            // handledEventsToo receives the event regardless and lets WASD orbit work too.
            keyDownHandler ??= OnPlatformKeyDown;
            element.AddHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, keyDownHandler, handledEventsToo: true);
            // KeyUp too, so walk mode can release held movement keys (WASD/arrows/Shift). Same handledEventsToo
            // path as KeyDown — the focus system marks these handled before they reach us.
            keyUpHandler ??= OnPlatformKeyUp;
            element.AddHandler(Microsoft.UI.Xaml.UIElement.KeyUpEvent, keyUpHandler, handledEventsToo: true);

            // Focusing a SwapChainPanel to capture keys proved unreliable, so also listen at the window
            // root (XamlRoot.Content), which always receives KeyDown. The handler is gated on this view's
            // IsVisible so it only drives the camera while 3D mode is on. handledEventsToo:true so it fires
            // even though focus-navigation marks the arrow keys handled.
            keyboardRoot = element.XamlRoot?.Content as Microsoft.UI.Xaml.UIElement;
            keyboardRoot?.AddHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, keyDownHandler, handledEventsToo: true);
            keyboardRoot?.AddHandler(Microsoft.UI.Xaml.UIElement.KeyUpEvent, keyUpHandler, handledEventsToo: true);

            element.PointerPressed += OnPlatformPointerPressed;
            element.PointerMoved += OnPlatformPointerMoved;
            element.PointerReleased += OnPlatformPointerReleased;
            element.PointerCaptureLost += OnPlatformPointerReleased;
        }
    }

    private void DetachWheelHandler()
    {
        if (wheelTarget is not null)
        {
            wheelTarget.PointerWheelChanged -= OnPointerWheelChanged;
            if (keyDownHandler is not null)
            {
                wheelTarget.RemoveHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, keyDownHandler);
            }
            if (keyUpHandler is not null)
            {
                wheelTarget.RemoveHandler(Microsoft.UI.Xaml.UIElement.KeyUpEvent, keyUpHandler);
            }
            wheelTarget.PointerPressed -= OnPlatformPointerPressed;
            wheelTarget.PointerMoved -= OnPlatformPointerMoved;
            wheelTarget.PointerReleased -= OnPlatformPointerReleased;
            wheelTarget.PointerCaptureLost -= OnPlatformPointerReleased;
            wheelTarget = null;
        }

        if (keyboardRoot is not null && keyDownHandler is not null)
        {
            keyboardRoot.RemoveHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, keyDownHandler);
            if (keyUpHandler is not null)
            {
                keyboardRoot.RemoveHandler(Microsoft.UI.Xaml.UIElement.KeyUpEvent, keyUpHandler);
            }
            keyboardRoot = null;
        }
    }

    private void OnPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (walkActive || dragonActive)
        {
            return; // no orbit-zoom while walking or flying the dragon
        }

        int delta = e.GetCurrentPoint((Microsoft.UI.Xaml.UIElement)sender).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        // One wheel notch = 120 units; ~10% per notch.
        float scale = MathF.Pow(1.1f, delta / 120f);
        controller.ApplyZoom(scale);
        Canvas.InvalidateSurface();
        e.Handled = true;
    }

    private void OnPlatformPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var element = (Microsoft.UI.Xaml.UIElement)sender;

        // Clicking the canvas grabs keyboard focus so subsequent KeyDown events route here.
        // Focus() is on UIElement (the platform view is a SwapChainPanel, not a Control).
        element.Focus(Microsoft.UI.Xaml.FocusState.Pointer);

        // If the window-root key handler wasn't attached at handler-change time (XamlRoot not ready yet),
        // attach it now that the view is definitely live — guarantees keyboard works after any interaction.
        if (keyboardRoot is null && keyDownHandler is not null
            && element.XamlRoot?.Content is Microsoft.UI.Xaml.UIElement root)
        {
            keyboardRoot = root;
            keyboardRoot.AddHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, keyDownHandler, handledEventsToo: true);
            keyboardRoot.AddHandler(Microsoft.UI.Xaml.UIElement.KeyUpEvent, keyUpHandler, handledEventsToo: true);
        }

        var props = e.GetCurrentPoint(element).Properties;

        // Dragon flight: RIGHT-drag steers (yaw + pitch). Capture so the moves flow even off the canvas.
        if (dragonActive)
        {
            mouseDragButton = props.IsRightButtonPressed ? 2 : 0;
            if (mouseDragButton != 0)
            {
                dragonRmbHeld = true; // hold the steered attitude while the button is down
                lastPointerPosition = e.GetCurrentPoint(element).Position;
                element.CapturePointer(e.Pointer);
                e.Handled = true;
            }

            return;
        }

        // Walk mode: LEFT = swing the ciupaga (a strike), RIGHT-drag = look around. So the left button never
        // starts a look-drag while walking.
        if (walkActive)
        {
            if (props.IsLeftButtonPressed)
            {
                walkLmbDown = true; // held = ciupaga self-arrest (hang) while airborne against rock
                StartCiupagaSwing();
                element.CapturePointer(e.Pointer); // capture so the release reliably clears the hang
                mouseDragButton = 0;
                e.Handled = true;
                return;
            }

            mouseDragButton = props.IsRightButtonPressed ? 2 : 0;
            if (mouseDragButton != 0)
            {
                lastPointerPosition = e.GetCurrentPoint(element).Position;
                element.CapturePointer(e.Pointer);
                e.Handled = true;
            }

            return;
        }

        mouseDragButton = props.IsLeftButtonPressed ? 1 : props.IsRightButtonPressed ? 2 : 0;
        if (mouseDragButton != 0)
        {
            lastPointerPosition = e.GetCurrentPoint(element).Position;
            element.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void OnPlatformPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (mouseDragButton == 0)
        {
            return;
        }

        var element = (Microsoft.UI.Xaml.UIElement)sender;
        var point = e.GetCurrentPoint(element);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
        {
            mouseDragButton = 0;
            return;
        }

        float dx = (float)(point.Position.X - lastPointerPosition.X);
        float dy = (float)(point.Position.Y - lastPointerPosition.Y);
        lastPointerPosition = point.Position;

        if (dragonActive)
        {
            // Right-drag accumulates a steer the flight tick consumes (dx → yaw, dy → pitch).
            dragonMouseDx += dx;
            dragonMouseDy += dy;
            e.Handled = true;
            return;
        }

        if (walkActive)
        {
            // Walk mode: drag turns the head (yaw) and tilts the gaze (pitch) in place — movement stays on WASD.
            // Drag right → turn right (heading decreases); drag up → look up (pitch increases).
            walkHeadingRadians -= dx * WalkMouseLookRadiansPerPixel;
            walkLookPitchRadians = Math.Clamp(
                walkLookPitchRadians - (dy * WalkMouseLookRadiansPerPixel),
                -WalkMaxLookPitchRadians,
                WalkMaxLookPitchRadians);
            Canvas.InvalidateSurface();
            e.Handled = true;
            return;
        }

        if (mouseDragButton == 1)
        {
            // Left-drag orbits around the focus (camera circles the scene).
            controller.ApplyOrbit(dx, -dy);
        }
        else
        {
            // Right-drag looks around in place — the camera stays put and turns its view direction.
            // dy is NOT inverted here (unlike orbit): dragging up looks up.
            controller.ApplyLookAround(dx, dy);
        }

        Canvas.InvalidateSurface();
        e.Handled = true;
    }

    private void OnPlatformPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        mouseDragButton = 0;
        walkLmbDown = false; // releasing the left button lets the ciupaga go — the hang drops
        dragonRmbHeld = false; // release the dragon attitude hold → pitch auto-levels again
        ((Microsoft.UI.Xaml.UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    private void OnPlatformKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // The handler also listens at the window root, so ignore keys unless 3D mode is actually showing.
        if (!IsVisible)
        {
            return;
        }

        // F8 toggles first-person walk mode (works both in and out of walk). Flip the two-way bindable so the
        // view-model's walk chip stays in sync; the property-changed hook does the Enter/Exit.
        if (e.Key == Windows.System.VirtualKey.F8)
        {
            // Debounce: the handler is attached to BOTH the canvas and the window root (handledEventsToo), and OS
            // key-repeat fires while held — either can flip the toggle twice per physical press (on→off→on = stuck
            // on). Collapse anything within the window to ONE toggle, so a fresh press always flips the state.
            long nowMs = Environment.TickCount64;
            if (nowMs - lastF8ToggleMs < 350)
            {
                e.Handled = true;
                return;
            }

            lastF8ToggleMs = nowMs;
            Serilog.Log.Information(
                "[Walk] F8 pressed → toggling {From}->{To} (frame={HasFrame}, tiles={Tiles})",
                IsWalkModeActive, !IsWalkModeActive, WorldFrame is not null, Tiles?.Count ?? 0);
            IsWalkModeActive = !IsWalkModeActive;
            e.Handled = true;
            return;
        }

        // F7 toggles dragon flight (same debounce as F8).
        if (e.Key == Windows.System.VirtualKey.F7)
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - lastF7ToggleMs < 350)
            {
                e.Handled = true;
                return;
            }

            lastF7ToggleMs = nowMs;
            Serilog.Log.Information("[Dragon] F7 pressed → toggling {From}->{To}", IsDragonFlightActive, !IsDragonFlightActive);
            IsDragonFlightActive = !IsDragonFlightActive;
            e.Handled = true;
            return;
        }

        // Dragon flight: WASD/arrows steer throttle + bank (right-drag steers the rest).
        if (dragonActive)
        {
            HandleDragonKeyDown(e);
            return;
        }

        // While walking, WASD/arrows move, Space jumps, Shift runs, Q/E/R/F look — a separate binding set.
        if (walkActive)
        {
            HandleWalkKeyDown(e);
            return;
        }

        bool handled = true;
        switch (e.Key)
        {
            // Pan with the arrow keys on the ground plane — "move the camera through space".
            // ↑ moves the focus forward (into the scene), ↓ pulls it back, ← / → strafe left / right,
            // matching the on-screen Przesuń pad.
            case Windows.System.VirtualKey.Up:
                controller.ApplyPan(0f, KeyPanPixelStep);
                break;
            case Windows.System.VirtualKey.Down:
                controller.ApplyPan(0f, -KeyPanPixelStep);
                break;
            case Windows.System.VirtualKey.Left:
                controller.ApplyPan(-KeyPanPixelStep, 0f);
                break;
            case Windows.System.VirtualKey.Right:
                controller.ApplyPan(KeyPanPixelStep, 0f);
                break;

            // A / D strafe left / right (lateral pan, same as ← / →); W / S move forward / backward on the ground
            // plane (dolly through the scene) — standard FPS movement (WASD moves, mouse drag orbits/rotates).
            case Windows.System.VirtualKey.A:
                controller.ApplyPan(-KeyPanPixelStep, 0f);
                break;
            case Windows.System.VirtualKey.D:
                controller.ApplyPan(KeyPanPixelStep, 0f);
                break;
            case Windows.System.VirtualKey.W:
                controller.ApplyPan(0f, KeyPanPixelStep);
                break;
            case Windows.System.VirtualKey.S:
                controller.ApplyPan(0f, -KeyPanPixelStep);
                break;

            // Q / E rotate the view IN PLACE (look-around): the camera stays put and turns its gaze left / right
            // ("turn my head", not circle the target). Q looks left, E looks right.
            case Windows.System.VirtualKey.Q:
                controller.ApplyLookAround(-KeyYawPixelStep, 0f);
                break;
            case Windows.System.VirtualKey.E:
                controller.ApplyLookAround(KeyYawPixelStep, 0f);
                break;

            // T / G raise / lower the camera (vertical pan), same as the on-screen altitude pad.
            case Windows.System.VirtualKey.T:
                controller.ApplyVertical(KeyPanPixelStep);
                break;
            case Windows.System.VirtualKey.G:
                controller.ApplyVertical(-KeyPanPixelStep);
                break;

            // F9 starts the cinematic grand-tour fly-through (Orla Perć ridge → Western Tatras → Gerlach
            // finale, time-swept midday→night) — same entry point as the Widok panel's 🎬 button, for demos.
            case Windows.System.VirtualKey.F9:
                StartOrlaPercFlight();
                break;

            // View pitch — tilt the gaze in place (same ApplyLookAround as the ⊺/ꓕ pad buttons, so the
            // camera position stays put). R / PgUp look up, F / PgDn look down.
            case Windows.System.VirtualKey.R:
            case Windows.System.VirtualKey.PageUp:
                controller.ApplyLookAround(0f, -KeyTiltPixelStep);
                break;
            case Windows.System.VirtualKey.F:
            case Windows.System.VirtualKey.PageDown:
                controller.ApplyLookAround(0f, KeyTiltPixelStep);
                break;

            // Zoom in / out with +/- (both numpad and main-row variants).
            case Windows.System.VirtualKey.Add:
            case (Windows.System.VirtualKey)187:  // VK_OEM_PLUS
                controller.ApplyZoom(KeyZoomFactor);
                break;
            case Windows.System.VirtualKey.Subtract:
            case (Windows.System.VirtualKey)189:  // VK_OEM_MINUS
                controller.ApplyZoom(1f / KeyZoomFactor);
                break;

            default:
                handled = false;
                break;
        }

        if (handled)
        {
            Canvas.InvalidateSurface();
            e.Handled = true;
        }
    }

    // Walk-mode key DOWN: set held-movement state (polled by the tick), queue a jump, or step the look angles.
    // Movement/run keys are level-triggered (held) via the matching KeyUp; jump and look are per-press (OS key
    // repeat makes a held Q/E/R/F turn smoothly).
    private void HandleWalkKeyDown(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.W:
            case Windows.System.VirtualKey.Up:
                walkFwd = true;
                break;
            case Windows.System.VirtualKey.S:
            case Windows.System.VirtualKey.Down:
                walkBack = true;
                break;
            case Windows.System.VirtualKey.A:
            case Windows.System.VirtualKey.Left:
                walkStrafeLeft = true;
                break;
            case Windows.System.VirtualKey.D:
            case Windows.System.VirtualKey.Right:
                walkStrafeRight = true;
                break;
            case Windows.System.VirtualKey.Shift:
            case Windows.System.VirtualKey.LeftShift:
            case Windows.System.VirtualKey.RightShift:
                walkRun = true;
                break;
            case Windows.System.VirtualKey.Space:
                walkJumpQueued = true;
                break;

            // Look with the keyboard (in addition to mouse-drag): Q/E yaw, R/F (and PgUp/PgDn) pitch.
            case Windows.System.VirtualKey.Q:
                walkHeadingRadians += WalkKeyTurnRadians;
                break;
            case Windows.System.VirtualKey.E:
                walkHeadingRadians -= WalkKeyTurnRadians;
                break;
            case Windows.System.VirtualKey.R:
            case Windows.System.VirtualKey.PageUp:
                walkLookPitchRadians = Math.Clamp(walkLookPitchRadians + WalkKeyTurnRadians, -WalkMaxLookPitchRadians, WalkMaxLookPitchRadians);
                break;
            case Windows.System.VirtualKey.F:
            case Windows.System.VirtualKey.PageDown:
                walkLookPitchRadians = Math.Clamp(walkLookPitchRadians - WalkKeyTurnRadians, -WalkMaxLookPitchRadians, WalkMaxLookPitchRadians);
                break;
        }

        e.Handled = true;
    }

    // Dragon-flight key DOWN: WASD/arrows set the throttle (W/S) and bank/turn (A/D) held state; the right-drag
    // supplies the finer yaw/pitch steering. Polled by the flight tick.
    private void HandleDragonKeyDown(Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.W:
                dragonW = true;
                break;
            case Windows.System.VirtualKey.S:
                dragonS = true;
                break;
            case Windows.System.VirtualKey.A:
                dragonA = true;
                break;
            case Windows.System.VirtualKey.D:
                dragonD = true;
                break;
            case Windows.System.VirtualKey.Up:
                dragonPitchUp = true;
                break;
            case Windows.System.VirtualKey.Down:
                dragonPitchDown = true;
                break;
            case Windows.System.VirtualKey.Left:
                if (!dragonYawLeft)
                {
                    Serilog.Log.Information("[DragonKey] DOWN Left");
                }

                dragonYawLeft = true;
                break;
            case Windows.System.VirtualKey.Right:
                if (!dragonYawRight)
                {
                    Serilog.Log.Information("[DragonKey] DOWN Right");
                }

                dragonYawRight = true;
                break;

            // L = land on the nearest summit (or the terrain ahead); Space = take off from the perch,
            // or — in the air — one hard wing-beat that hoists the dragon upward. F breathes fire.
            case Windows.System.VirtualKey.L:
                // L toggles the landing cycle: perched → take off, airborne → land.
                if (dragon is { } dl && dl.BeginTakeoff())
                {
                    Serilog.Log.Information("[Dragon] takeoff from perch (L)");
                }
                else
                {
                    BeginDragonLanding();
                }
                break;
            case Windows.System.VirtualKey.F:
                dragonFireHeld = true;
                break;
            case Windows.System.VirtualKey.Space:
                if (dragon is { } d2)
                {
                    if (d2.BeginTakeoff())
                    {
                        Serilog.Log.Information("[Dragon] takeoff from perch");
                    }
                    else if (d2.FlapBoost())
                    {
                        // Visual burst: restart the stroke from the wings-up top so the boost reads as ONE
                        // powerful down-beat, and briefly overdrive the beat rate.
                        dragonFlapPhase = MathF.PI / 2f;
                        dragonFlapBurst = 1f;
                    }
                }
                break;
        }

        e.Handled = true;
    }

    // Elevation (real metres) of the actual DRAWN terrain under a world-XY point: finds the rendered mesh
    // triangle containing the point and barycentric-interpolates its world Z (÷ exaggeration). This is what's
    // on screen regardless of LOD, so the perched dragon's feet land on the visible rock. O(triangles), but
    // a bbox reject leaves only the 1-few tiles under the point — call once when perched and cache.
    private float? SampleRenderedMeshElevation(float worldX, float worldY)
    {
        if (Tiles is not { Count: > 0 } tiles)
        {
            return null;
        }

        float bestZ = float.NaN;
        foreach (TerrainMesh3D tile in tiles)
        {
            if (worldX < tile.WorldMin.X || worldX > tile.WorldMax.X
                || worldY < tile.WorldMin.Y || worldY > tile.WorldMax.Y)
            {
                continue;
            }

            Vector3[] verts = tile.Vertices;
            uint[] indices = tile.Indices;
            for (int i = 0; i + 2 < indices.Length; i += 3)
            {
                Vector3 a = verts[indices[i]];
                Vector3 b = verts[indices[i + 1]];
                Vector3 c = verts[indices[i + 2]];

                float den = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
                if (MathF.Abs(den) < 1e-9f)
                {
                    continue;
                }

                float u = (((b.Y - c.Y) * (worldX - c.X)) + ((c.X - b.X) * (worldY - c.Y))) / den;
                float v = (((c.Y - a.Y) * (worldX - c.X)) + ((a.X - c.X) * (worldY - c.Y))) / den;
                float w = 1f - u - v;
                if (u < -0.001f || v < -0.001f || w < -0.001f)
                {
                    continue;
                }

                float z = (u * a.Z) + (v * b.Z) + (w * c.Z);
                if (float.IsNaN(bestZ) || z > bestZ)
                {
                    bestZ = z; // highest surface where tiles overlap = the top one drawn
                }
            }
        }

        if (float.IsNaN(bestZ))
        {
            return null;
        }

        float exagg = WorldFrame?.VerticalExaggeration ?? 1f;
        return bestZ / MathF.Max(0.001f, exagg);
    }

    /// <summary>Wraps an angle to (−π, π] — for shortest-way angular chasing (the lazy chase cam).</summary>
    private static float WrapAngleRad(float radians)
    {
        while (radians <= -MathF.PI)
        {
            radians += 2f * MathF.PI;
        }

        while (radians > MathF.PI)
        {
            radians -= 2f * MathF.PI;
        }

        return radians;
    }

    // Advances the fire-breath simulation one tick: spawns a ball from the mouth while F is held, flies the
    // balls forward with a touch of hot-gas buoyancy, bursts them on terrain contact, and rebuilds the render
    // sprite list (world coords, Z exaggerated).
    private void StepDragonFire(MapaTur.Application.Terrain.DragonFlight d, float dt, float exaggeration)
    {
        dragonFireCooldown -= dt;
        if (dragonFireHeld && dragonFireCooldown <= 0f)
        {
            float cp = MathF.Cos(d.PitchRadians), sp = MathF.Sin(d.PitchRadians);
            float chh = MathF.Cos(d.HeadingRadians), shh = MathF.Sin(d.HeadingRadians);
            var fwdXY = new System.Numerics.Vector2(cp * chh, cp * shh);
            float speed = d.SpeedMetersPerSecond + DragonFireSpeedMetersPerSecond;
            dragonFireballs.Add(new DragonFireball
            {
                XY = d.PositionXY + (fwdXY * DragonFireMuzzleOffsetMeters),
                Elevation = d.ElevationMeters + (sp * DragonFireMuzzleOffsetMeters) + 1f,
                VelocityXY = fwdXY * speed,
                VelocityZ = sp * speed,
                Seed = (dragonFireCounter++ * 0.731f) % 10f,
            });
            dragonFireCooldown = DragonFireCooldownSeconds;
        }

        dragonFireSprites.Clear();
        for (int i = dragonFireballs.Count - 1; i >= 0; i--)
        {
            DragonFireball ball = dragonFireballs[i];
            if (ball.Exploding)
            {
                ball.ExplodeAge += dt;
                if (ball.ExplodeAge >= DragonFireExplodeSeconds)
                {
                    dragonFireballs.RemoveAt(i);
                    continue;
                }
            }
            else
            {
                ball.Age += dt;
                ball.XY += ball.VelocityXY * dt;
                ball.Elevation += ball.VelocityZ * dt;
                ball.VelocityZ += 1.5f * dt; // hot gas drifts up a touch
                if (ball.Age >= DragonFireTtlSeconds)
                {
                    dragonFireballs.RemoveAt(i);
                    continue;
                }

                if (SampleWalkGround(ball.XY) is { } ground && ball.Elevation <= ground + 0.5f)
                {
                    ball.Exploding = true;
                    ball.Elevation = ground + 1f;
                }
            }

            dragonFireballs[i] = ball;

            float radius = DragonFireRadiusMeters + (ball.Age * 0.9f) + (ball.Exploding ? ball.ExplodeAge * 22f : 0f);
            float intensity = ball.Exploding
                ? 1f - (ball.ExplodeAge / DragonFireExplodeSeconds)
                : MathF.Min(1f, ball.Age * 8f) * MathF.Min(1f, (DragonFireTtlSeconds - ball.Age) / 0.5f);
            dragonFireSprites.Add(new MapaTur.App.Services.Terrain3DGlRenderer.FireballSprite(
                new Vector3(ball.XY.X, ball.XY.Y, ball.Elevation * exaggeration), radius, intensity, ball.Seed));
        }
    }

    // Picks the landing spot — the nearest OSM summit within range, else the terrain straight ahead — seats it
    // on the TRUE rendered surface (fine sampler → base DEM) and starts the autopilot landing cycle.
    private void BeginDragonLanding()
    {
        if (dragon is not { Phase: MapaTur.Application.Terrain.DragonFlightPhase.Flying } d
            || WorldFrame is not { } frame)
        {
            return;
        }

        const float PeakSearchRadiusMeters = 800f;
        const float FallbackAheadMeters = 150f;

        var pos = d.PositionXY;
        System.Numerics.Vector2? target = null;
        string? name = null;
        float bestDistance = PeakSearchRadiusMeters;
        if (Peaks is { Count: > 0 } peaks)
        {
            foreach (MapaTur.Application.Terrain.TerrainPeak peak in peaks)
            {
                Vector3 world = frame.GeoToWorld(peak.Location, 0f);
                var xy = new System.Numerics.Vector2(world.X, world.Y);
                float distance = System.Numerics.Vector2.Distance(pos, xy);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    target = xy;
                    name = peak.Name;
                }
            }
        }

        if (target is null)
        {
            float ch = MathF.Cos(d.HeadingRadians), sh = MathF.Sin(d.HeadingRadians);
            target = pos + (new System.Numerics.Vector2(ch, sh) * FallbackAheadMeters);
            name = null;
        }

        // The dragon must stand ON the rendered 1 m rock, so the surface sample wins over any tagged elevation.
        if (SampleWalkGround(target.Value) is not { } groundElevation)
        {
            Serilog.Log.Information("[Dragon] landing refused — no terrain under the target");
            return;
        }

        // Snap to the LOCAL HIGHEST point: the OSM peak node (or the ahead-fallback) often sits a few metres
        // off the true rendered summit, which read as "landing below the top". Scan a small grid around the
        // spot on the fine surface and perch on its highest sample.
        for (float dx = -12f; dx <= 12f; dx += 4f)
        {
            for (float dy = -12f; dy <= 12f; dy += 4f)
            {
                var probe = new System.Numerics.Vector2(target.Value.X + dx, target.Value.Y + dy);
                if (SampleWalkGround(probe) is { } probeElevation && probeElevation > groundElevation)
                {
                    groundElevation = probeElevation;
                    target = probe;
                }
            }
        }

        if (d.BeginLanding(target.Value, groundElevation))
        {
            Serilog.Log.Information(
                "[Dragon] landing → {Name} ({X:F0},{Y:F0}) elev={Elev:F0} dist={Dist:F0}m",
                name ?? "terrain ahead", target.Value.X, target.Value.Y, groundElevation,
                System.Numerics.Vector2.Distance(pos, target.Value));
        }
    }

    private void OnPlatformKeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (dragonActive)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.W:
                    dragonW = false;
                    break;
                case Windows.System.VirtualKey.S:
                    dragonS = false;
                    break;
                case Windows.System.VirtualKey.A:
                    dragonA = false;
                    break;
                case Windows.System.VirtualKey.D:
                    dragonD = false;
                    break;
                case Windows.System.VirtualKey.Up:
                    dragonPitchUp = false;
                    break;
                case Windows.System.VirtualKey.Down:
                    dragonPitchDown = false;
                    break;
                case Windows.System.VirtualKey.Left:
                    Serilog.Log.Information("[DragonKey] UP Left");
                    dragonYawLeft = false;
                    break;
                case Windows.System.VirtualKey.Right:
                    Serilog.Log.Information("[DragonKey] UP Right");
                    dragonYawRight = false;
                    break;
                case Windows.System.VirtualKey.F:
                    dragonFireHeld = false;
                    break;
            }

            e.Handled = true;
            return;
        }

        if (!walkActive)
        {
            return;
        }

        switch (e.Key)
        {
            case Windows.System.VirtualKey.W:
            case Windows.System.VirtualKey.Up:
                walkFwd = false;
                break;
            case Windows.System.VirtualKey.S:
            case Windows.System.VirtualKey.Down:
                walkBack = false;
                break;
            case Windows.System.VirtualKey.A:
            case Windows.System.VirtualKey.Left:
                walkStrafeLeft = false;
                break;
            case Windows.System.VirtualKey.D:
            case Windows.System.VirtualKey.Right:
                walkStrafeRight = false;
                break;
            case Windows.System.VirtualKey.Shift:
            case Windows.System.VirtualKey.LeftShift:
            case Windows.System.VirtualKey.RightShift:
                walkRun = false;
                break;
        }

        e.Handled = true;
    }
#endif  // WINDOWS — mouse-wheel + keyboard region close

#if WINDOWS || ANDROID
    // ── Real GPU terrain engine (OpenGL ES on the SKGLView context) ───────────────────────────────
    // The renderer draws the terrain into a colour TEXTURE it owns and returns the texture handle;
    // we wrap that texture as an SKImage and let SkiaSharp DrawImage compose it. That hand-off works
    // identically on Windows (ANGLE) and Android — historically Android's SKGLView exposes the
    // on-screen FBO as 0 and Skia's compositor would repaint over anything we drew into it. With
    // the texture bridge there's no FBO-0 collision: Skia samples our texture as part of its own
    // draw pass. Any GL/shader/wrapper failure disables the GPU path for the session.
    private static readonly bool UseGlRenderer = true;

    private Services.Terrain3DGlRenderer? glRenderer;
    private bool glDisabled;

    // Decoded ortho pixels cached by path so re-entering 3D (which rebuilds the renderer) re-uploads from
    // memory instead of decoding the large PNG from disk every time.
    // Decoded ortho tiles cached by their path signature, so re-entering 3D (which rebuilds the renderer)
    // re-uploads from memory instead of decoding the large PNGs from disk every time.
    private string? cachedOrthoSignature;
    private List<(byte[] Rgba, int Width, int Height)>? cachedOrthoDecoded;

    // Forest (instanced trees) placed once per mesh from the DEM + ortho frame, cached by the tiles
    // reference (the placement is a CPU scan — don't redo it every frame). Phase 1: fixed density; a
    // "Las" slider will drive it next.
    private IReadOnlyList<TreeInstance>? cachedForest;
    private IReadOnlyList<TerrainMesh3D>? cachedForestTiles;

    private IReadOnlyList<TreeInstance>? EnsureForest(IReadOnlyList<TerrainMesh3D> tiles)
    {
        if (Raster is null || tiles.Count == 0)
        {
            return null;
        }
        if (ReferenceEquals(cachedForestTiles, tiles) && cachedForest is not null)
        {
            return cachedForest;
        }

        // 3D forest rendering disabled per user request — the trees looked poor and added nothing. The
        // ForestPlacement helper + GL pass are left dormant; no trees are ever placed, so none are drawn.
        cachedForest = System.Array.Empty<TreeInstance>();
        cachedForestTiles = tiles;
        return cachedForest;
    }

    private bool TryRenderTerrainGl(SKCanvas canvas, IReadOnlyList<TerrainMesh3D> tiles, int width, int height)
    {
        if (glDisabled)
        {
            return false;
        }

        // No GR context means SKGLView hasn't finished initialising its GL backend yet — Skia's wrapping of
        // a GL texture as an SKImage needs that context to bind it. Bail this frame so the Skia fallback
        // paints instead; next paint will retry. GRContext lives on the SKGLView, not on SKCanvas.
        GRContext? grContext = Canvas.GRContext;
        if (grContext is null)
        {
            return false;
        }

        try
        {
            glRenderer ??= new Services.Terrain3DGlRenderer();
            glRenderer.OrthoEnabled = ShowOrtho; // premium menu "Ortofoto" toggle (textures stay resident)
            // "2D map" mode fade: 1 = full ortho (normal 3D), 0 = pure hypsometric (top-down map view).
            float mapT = mapMode.Blend;
            glRenderer.OrthoGlobalFade = 1f - (mapT * mapT * (3f - (2f * mapT)));
            glRenderer.MsaaEnabled = MsaaEnabled; // premium menu render-quality profile (AA on/off)
            glRenderer.SlopeMapEnabled = SlopeMapEnabled; // premium menu "Mapa nachylenia" (slope-steepness shading)
            glRenderer.RockStrength = RockMaterialEnabled ? 1f : 0f; // premium menu "Skały" (rock material on steep faces)
            glRenderer.BiomeMaterialEnabled = BiomeMaterialEnabled; // premium menu "Biomy" (elevation-zone material)
            // Ortho coverage cull: fade ortho → hypsometric beyond where the bundled ortho actually covers, so a
            // base wider than the ortho doesn't stretch clamped edge texels into "strata" bands. Null → no cull.
            glRenderer.SetOrthoCoverageGeoBounds(LodOrthoCoverageBounds, 300f);
            glRenderer.LakeFineBounds = LodDetailBounds; // lakes inside the 1 m detail keep legacy seating
            glRenderer.BakedElevationIndex = BakedElevationIndex; // trail/route/road lines seat on the REAL baked tile, not the static base
            glRenderer.BaseCoverageMask = BaseCoverageMask; // surface ownership: discard base-skin pixels over resident full z16 detail
            glRenderer.Waterways = Waterways;   // stream/river polylines → shiny water decal in the terrain shader
            glRenderer.Waterfalls = Waterfalls; // waterfall points → bright foam accents on their streams
            glRenderer.ShowCableCar = ShowCableCar; // "🚠 Kolejka" layer toggle
            glRenderer.CableCar = MapaTur.Application.Terrain.CableCarData.Kasprowy; // Kasprowy Wierch aerialway
            glRenderer.ShowContours = ShowContours; // "Warstwice" layer toggle — thin iso-elevation lines on the relief

            // Push a changed ortho image to the GL renderer once (it uploads on the GL thread next Render).
            if (orthoPathDirty)
            {
                orthoPathDirty = false;
                if (OrthoTextureCells is { Count: > 0 } cells)
                {
                    // MBTiles-composited textures: bytes already RGBA8, skip the PNG decoder.
                    ApplyOrthoTextureCells(glRenderer, cells);
                }
                else
                {
                    IReadOnlyList<string>? orthoPaths = OrthoTexturePaths is { Count: > 0 }
                        ? OrthoTexturePaths
                        : (OrthoTexturePath is { Length: > 0 } single ? new[] { single } : null);
                    ApplyOrthoTextures(glRenderer, orthoPaths);
                }
            }

            // GL draws the terrain AND the depth-tested trail/route lines (so the terrain occludes them)
            // into a colour texture it owns and returns the texture handle. A 0 handle means the present
            // FBO couldn't be allocated this frame; fall back to Skia. The optional Atmosphere drives the
            // sky pass and the terrain fragment shader's aerial-perspective blend; passing null skips both.
            // Today's local date drives the night-sky star pass (with the time-of-day slider as the local
            // hour); the stars fade in only once the slider puts the sun below the horizon.
            IReadOnlyList<TreeInstance>? forest = EnsureForest(tiles);
            // During a film the time arc sweeps the sun; pin the snow line to the pre-film sun so the cover the
            // user set doesn't melt/reform mid-shot (only the lighting moves). Off-film = snow follows the sun.
            glRenderer.SnowSunOverride = flightActive ? flightBaseSun : null;
            // Push the ridden 3D dragon (F7) into the GL pass so it draws depth-tested in the scene. Visible only
            // while flying AND the model is loaded (until then the procedural Skia dragon shows). Light = the
            // scene's terrain light so it shades consistently.
            bool dragon3DVisible = dragonActive && dragonModel3D is not null;
            Vector3 dragonLight = tiles.Count > 0 ? tiles[0].LightDirection : new Vector3(0.4f, 0.4f, 1f);
            glRenderer.SetDragon(dragonModel3D, dragonWorldMatrix, dragonNormalMatrix, dragonLight, dragon3DVisible);
            glRenderer.SetFireballs(dragonActive && dragonFireSprites.Count > 0 ? dragonFireSprites : null);
            double dbgPreRenderMs = dbgSwapPaintActive ? dbgPaintWatch.Elapsed.TotalMilliseconds : 0;
            uint terrainTextureId = glRenderer.Render(width, height, tiles, Camera, Trails, Raster, Route, Roads, EffectiveAtmosphere, forest, DetailElevation, ShowNightSky ? DateOnly.FromDateTime(DateTime.Now) : null, ExposedRoutes, ShowSauronTower, ShowEagles, AtmosphereEffectsEnabled, OffTrailTracks);
            if (dbgSwapPaintActive)
            {
                double afterRenderMs = dbgPaintWatch.Elapsed.TotalMilliseconds;
                if (afterRenderMs > 100)
                {
                    // pre = property pushes + ortho apply + forest cache; render = the GL frame itself
                    // (its internal split is the renderer's hitch line); everything after is Skia compose.
                    Serilog.Log.Information(
                        "[GL3D] swap gl-block: pre={Pre:F0} render={Render:F0}ms",
                        dbgPreRenderMs, afterRenderMs - dbgPreRenderMs);
                }
            }

            if (terrainTextureId == 0)
            {
                return false;
            }

            // Capture the freshly-rendered frame for video recording directly from the GL renderer's
            // present FBO — BEFORE Skia touches GL state below. Reading the GL output (not a Skia surface
            // snapshot) avoids the back-buffer staleness that blacked out every frame after the first.
            CaptureGlFrameForRecording(width, height);

            // Tell Skia we touched GL state, then wrap our texture as an SKImage and let Skia compose it.
            // BottomLeft origin: GL textures have their origin at the bottom-left of the image. RGBA8 +
            // Premul matches how our shader writes the colour (vec3 * alpha=1.0). Skia owns nothing here —
            // releaseProc:null leaves the texture's lifetime in our hands so we can reuse it next frame.
            grContext.ResetContext();
            var glInfo = new GRGlTextureInfo((uint)0x0DE1u, terrainTextureId, (uint)0x8058u); // GL_TEXTURE_2D, GL_RGBA8
            using var backendTexture = new GRBackendTexture(width, height, mipmapped: false, glInfo);
            using SKImage? terrainImage = SKImage.FromTexture(
                grContext,
                backendTexture,
                GRSurfaceOrigin.BottomLeft,
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
            if (terrainImage is null)
            {
                return false;
            }
            canvas.DrawImage(terrainImage, 0, 0);
            return true;
        }
        catch (Exception)
        {
            // One failure → drop to the proven Skia path for the rest of the session.
            glDisabled = true;
            glRenderer?.Dispose();
            glRenderer = null;
            return false;
        }
    }

    // Decodes the ortho tiles to tightly-packed top-row-first RGBA8 (row 0 = north, matching the mesh UVs)
    // and hands the set to the GL renderer. A null/empty list clears the ortho (back to the hypsometric
    // tint). If any tile fails to decode the whole set is abandoned, so textures never mis-align to cells.
    private void ApplyOrthoTextures(Services.Terrain3DGlRenderer renderer, IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            cachedOrthoSignature = null;
            cachedOrthoDecoded = null;
            renderer.SetOrthoTextures(Array.Empty<(byte[], int, int)>());
            return;
        }

        string signature = string.Join("|", paths);
        if (signature == cachedOrthoSignature && cachedOrthoDecoded is not null)
        {
            renderer.SetOrthoTextures(cachedOrthoDecoded);
            return;
        }

        // OFF-THREAD DECODE (2026-07-05): decoding the 8 bundled ortho PNGs (8192×5462 each) + the master
        // downsample took a MEASURED 31 s synchronously on this (paint/UI) thread — the window froze solid
        // behind the startup overlay (no frames, no progress animation; the log goes silent for the whole
        // span). Same pattern as the far-tier compute: kick a background task, keep painting (terrain shows
        // hypsometric until the pixels arrive), poll for the result on later paints. A path-set change mid-
        // decode is detected by signature — the stale result is dropped and the new decode starts.
        if (orthoDecodeTask is { } task && orthoDecodeSignature == signature)
        {
            if (!task.IsCompleted)
            {
                orthoPathDirty = true; // keep polling on subsequent paints
                return;
            }

            orthoDecodeTask = null;
            List<(byte[] Rgba, int Width, int Height)>? decoded = task.IsCompletedSuccessfully ? task.Result : null;
            if (decoded is null || decoded.Count != paths.Count)
            {
                cachedOrthoSignature = null;
                cachedOrthoDecoded = null;
                renderer.SetOrthoTextures(Array.Empty<(byte[], int, int)>());
                return;
            }

            cachedOrthoSignature = signature;
            cachedOrthoDecoded = decoded;
            renderer.SetOrthoTextures(decoded);
            return;
        }

        orthoDecodeSignature = signature;
        var pathsCopy = paths.ToList();
        orthoDecodeTask = Task.Run(() =>
        {
            // Cells decode in PARALLEL (independent files, pure decode+resize) — sequential took ~40 s for
            // the 8 bundled cells, which is how long the terrain stayed hypsometric after the scene reveal.
            // Indexed slots keep the list order = the mesh ortho-cell order (OrthoTileIndex is positional).
            var slots = new (byte[] Rgba, int Width, int Height)?[pathsCopy.Count];
            // MaxDegreeOfParallelism 3 (RAM step 1, 2026-07-06): one source cell decodes to a 16384×10923
            // RGBA buffer = 683 MB TRANSIENT before the master downsample discards it — all 8 in parallel
            // spiked ~7 GB of short-lived LOH at every scene load (measured heap 12-16 GB). Three at a time
            // caps the spike at ~2.6 GB, and the wall time barely moves (the 8-wide run did not scale
            // linearly anyway: decode is memory-bandwidth-bound).
            System.Threading.Tasks.Parallel.For(
                0, pathsCopy.Count, new ParallelOptions { MaxDegreeOfParallelism = 3 }, i =>
            {
                if (DecodeOrtho(pathsCopy[i]) is { } tile)
                {
                    // Pre-shrink to the master cap HERE so SetOrthoTextures' own downsample is a no-op —
                    // the whole heavy lift stays off the paint thread, and the renderer's MasterRgba ends up
                    // REFERENCING these same arrays (factor-1 Downsample returns its input), so the view
                    // cache adds no duplicate copy.
                    slots[i] = MapaTur.Application.Terrain.OrthoCellDownsampler.Downsample(
                        tile.Rgba, tile.Width, tile.Height,
                        MapaTur.Application.Terrain.OrthoDistanceTier.NearCapPx);
                }
            });

            var decoded = new List<(byte[] Rgba, int Width, int Height)>(pathsCopy.Count);
            foreach ((byte[] Rgba, int Width, int Height)? slot in slots)
            {
                if (slot.HasValue)
                {
                    decoded.Add(slot.Value);
                }
            }

            return decoded;
        });
        // Wake the paint loop when the pixels are ready (a still camera would otherwise not repaint).
        orthoDecodeTask.ContinueWith(
            _ => MainThread.BeginInvokeOnMainThread(() => Canvas.InvalidateSurface()),
            TaskScheduler.Default);
        orthoPathDirty = true; // poll again next paint
    }

    // In-flight background ortho decode + the path signature it was started for (stale results are dropped).
    private Task<List<(byte[] Rgba, int Width, int Height)>>? orthoDecodeTask;
    private string? orthoDecodeSignature;

    // True while the current paint is the tile-swap paint (set in OnPaintSurface) — TryRenderTerrainGl
    // uses it to log its own pre/render/post split on exactly that frame.
    private bool dbgSwapPaintActive;

    // Skips DecodeOrtho entirely: pre-composited cells (e.g. from an MBTiles archive) already carry
    // RGBA8 pixels and just need to flow through to SetOrthoTextures in row-major order.
    private void ApplyOrthoTextureCells(Services.Terrain3DGlRenderer renderer, IReadOnlyList<OrthoTextureCell> cells)
    {
        var decoded = new List<(byte[] Rgba, int Width, int Height)>(cells.Count);
        foreach (OrthoTextureCell cell in cells)
        {
            decoded.Add((cell.Rgba, cell.Width, cell.Height));
        }
        cachedOrthoSignature = null; // bypass the path-keyed cache; cells are the source of truth now
        cachedOrthoDecoded = null;
        renderer.SetOrthoTextures(decoded);
    }

    private static (byte[] Rgba, int Width, int Height)? DecodeOrtho(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = System.IO.File.OpenRead(path);
            using var codec = SkiaSharp.SKCodec.Create(stream);
            if (codec is null)
            {
                return null;
            }

            var info = new SkiaSharp.SKImageInfo(
                codec.Info.Width,
                codec.Info.Height,
                SkiaSharp.SKColorType.Rgba8888,
                SkiaSharp.SKAlphaType.Unpremul);
            using var bitmap = new SkiaSharp.SKBitmap(info);
            if (codec.GetPixels(info, bitmap.GetPixels()) != SkiaSharp.SKCodecResult.Success)
            {
                return null;
            }

            return (bitmap.Bytes, info.Width, info.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }
#endif
}