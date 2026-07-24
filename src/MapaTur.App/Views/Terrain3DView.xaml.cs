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

    /// <summary>Bindable CONTACT-grade sampler: the REAL baked surface (z17→z16) with no virtual-tile
    /// synthesis — for high-frequency scattered probes (fireball contact, fire targeting, flight AGL).
    /// See <see cref="SampleContactGround"/>. Null falls back to <see cref="FineElevationSampler"/>.</summary>
    public static readonly BindableProperty ContactElevationSamplerProperty = BindableProperty.Create(
        nameof(ContactElevationSampler),
        typeof(Func<double, double, double?>),
        typeof(Terrain3DView));

    public Func<double, double, double?>? ContactElevationSampler
    {
        get => (Func<double, double, double?>?)GetValue(ContactElevationSamplerProperty);
        set => SetValue(ContactElevationSamplerProperty, value);
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

    public static readonly BindableProperty ShowDebugMarkersProperty = BindableProperty.Create(
        nameof(ShowDebugMarkers), typeof(bool), typeof(Terrain3DView), false);

    /// <summary>When on (bound to the "LOD diagnostics" DEBUG toggle), draws the dragon foot-placement probe
    /// markers (RED origin / GREEN bind-centre / BLUE foot anchor / YELLOW target rock) + the [DragonSeat] log.</summary>
    public bool ShowDebugMarkers
    {
        get => (bool)GetValue(ShowDebugMarkersProperty);
        set => SetValue(ShowDebugMarkersProperty, value);
    }

    public static readonly BindableProperty ShowAiDragonsProperty = BindableProperty.Create(
        nameof(ShowAiDragons), typeof(bool), typeof(Terrain3DView), false,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).OnShowAiDragonsChanged((bool)n));

    /// <summary>Ambient flock of autonomous dragons circling the nearby peaks (and drifting toward the player's
    /// dragon when it flies near). Off hides + stops them.</summary>
    public bool ShowAiDragons
    {
        get => (bool)GetValue(ShowAiDragonsProperty);
        set => SetValue(ShowAiDragonsProperty, value);
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

    /// <summary>Whether the catalogued climbing-route overlay (topo lines + name labels on Mnich) is drawn.
    /// Off hides the visible lines/names; the routes still feed the climb session's guaranteed holds.</summary>
    public static readonly BindableProperty ShowClimbingRoutesProperty = BindableProperty.Create(
        nameof(ShowClimbingRoutes), typeof(bool), typeof(Terrain3DView), true,
        propertyChanged: (b, o, n) => ((Terrain3DView)b).Canvas.InvalidateSurface());

    public bool ShowClimbingRoutes
    {
        get => (bool)GetValue(ShowClimbingRoutesProperty);
        set => SetValue(ShowClimbingRoutesProperty, value);
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
        // Desktop 30 fps (2026-07-23): the 66 ms (~15 fps) cadence was THE perceived frame rate whenever the
        // camera sat still with atmosphere on — the HUD read "13 FPS" on an idle RTX 5080 because nothing else
        // invalidated. After the pano-streaming pass sumGpu is 10–23 ms, so 33 ms comfortably fits. Mobile
        // keeps 66 ms (battery). Walk/dragon vsync loop is untouched (it self-invalidates per frame).
        animationTimer.Interval = TimeSpan.FromMilliseconds(OperatingSystem.IsWindows() ? 33 : 66);
        animationTimer.Tick += OnAnimationTick;
        animationTimer.Start();

        // Camera-state autosave: a low-frequency diff against the last serialized camera. Captures
        // any camera change (gesture, gizmo, button, keyboard) without scattering save calls across
        // every input handler. Only writes when the serialized state actually changed.
        cameraSaveTimer = Dispatcher.CreateTimer();
        cameraSaveTimer.Interval = TimeSpan.FromMilliseconds(1200);
        cameraSaveTimer.Tick += OnCameraSaveTick;
        cameraSaveTimer.Start();

        // BENCH harness (2026-07-23, perf/pano-streaming): MAPATUR_BENCH_F9=<runs> auto-runs the deterministic
        // F9 Orla Perć flight <runs> times back-to-back (run 1 = cold in-process caches, run 2+ = warm) and
        // quits, emitting [Bench] log markers so a script can slice the log per run and diff metrics between
        // builds on an identical camera path. MP4 capture is suppressed for clean numbers.
        if (int.TryParse(Environment.GetEnvironmentVariable("MAPATUR_BENCH_F9"), out int benchRuns) && benchRuns > 0)
        {
            benchRunsRemaining = benchRuns;
            benchTotalRuns = benchRuns;
            benchTimer = Dispatcher.CreateTimer();
            benchTimer.Interval = TimeSpan.FromSeconds(2);
            benchTimer.Tick += OnBenchTick;
            benchTimer.Start();
            Serilog.Log.Information("[Bench] armed: {Runs} F9 runs, waiting for scene", benchRuns);
        }
    }

    // BENCH harness state — see the constructor arm. Runs the flight only once the world exists and a minimum
    // settle time has passed (the same streaming warm-up a user gets before pressing F9 by hand).
    private readonly IDispatcherTimer? benchTimer;
    private int benchRunsRemaining;
    private readonly int benchTotalRuns;
    private int benchTicks;

    private void OnBenchTick(object? sender, EventArgs e)
    {
        benchTicks++;
        if (flightActive || benchRunsRemaining <= 0)
        {
            return;
        }

        // Scene-ready gate: world frame + raster present, and ≥20 s settle so base streaming matches a human run.
        if (WorldFrame is null || Raster is null || benchTicks < 10)
        {
            return;
        }

        int runIndex = benchTotalRuns - benchRunsRemaining + 1;
        benchRunsRemaining--;
        Serilog.Log.Information(
            "[Bench] run {Run}/{Total} start ({Kind})", runIndex, benchTotalRuns, runIndex == 1 ? "cold" : "warm");
        StartOrlaPercFlight();
        recordingRequested = false; // no MP4 capture during bench — NVENC would skew the numbers
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

            // TEST HARNESS: sync pozy dla agenta (czyta %TEMP%\mapatur-pose.txt podczas testu manualnego).
            try { System.IO.File.WriteAllText(HarnessPosePath, serialized); }
            catch (System.IO.IOException) { }

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
#if WINDOWS
        if (vsyncLoopActive)
        {
            // The walk/dragon vsync loop already invalidates once per composed frame — a second invalidate
            // from this 15 fps timer painted the whole scene AGAIN between sim ticks (measured: 127 paints
            // per 58 ticks, ~half the UI thread burned on duplicate ~32 ms paints).
            return;
        }
#endif
        if (IsVisible && ((Atmosphere is not null && AtmosphereEffectsEnabled) || ShowAiDragons))
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

    // ── KONTRAKT-ORTO §4: anchor camera presets (MAPATUR_CAM_PRESET) ─────────────────────────────
    // Deterministic start-up views of the four anchor spots, so render changes can be verified with
    // passive before/after screenshots WITHOUT the user driving the camera. Applied once, when the
    // world frame is first available. docs/ORTO-CONTRACT.md defines the anchor list.
    private bool cameraPresetApplied;

    private static readonly Dictionary<string, (GeoPoint Geo, float DistanceMeters)> CameraPresets =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["mo"] = (new GeoPoint(49.1997, 20.0716), 900f),        // (a) MO shelter — 5 cm showcase
            ["mnich"] = (new GeoPoint(49.19253, 20.05485), 500f),   // (b) Mnich face up close
            ["dolinka"] = (new GeoPoint(49.1875, 20.0435), 700f),   // (c) Dolinka za Mnichem — survey seam
            ["panorama"] = (new GeoPoint(49.19253, 20.05485), 6000f), // (d) massif-scale tone coherence
            ["rysy"] = (new GeoPoint(49.1795, 20.0870), 900f)       // Czarny Staw pod Rysami — shadow blue-cast check
        };

    private void ApplyCameraPresetFromEnv()
    {
        if (cameraPresetApplied || WorldFrame is null)
        {
            return;
        }

        cameraPresetApplied = true; // one shot — even when unset, so the lookup runs once
        string? preset = Environment.GetEnvironmentVariable("MAPATUR_CAM_PRESET");
        if (string.IsNullOrWhiteSpace(preset))
        {
            return;
        }

        if (!CameraPresets.TryGetValue(preset.Trim(), out (GeoPoint Geo, float DistanceMeters) anchor))
        {
            Serilog.Log.Warning(
                "[CamPreset] unknown preset '{Preset}' — expected one of: {Known}",
                preset, string.Join(", ", CameraPresets.Keys));
            return;
        }

        FocusOnGeo(anchor.Geo, anchor.DistanceMeters);
        Serilog.Log.Information(
            "[CamPreset] applied '{Preset}' — target ({Lat:F5},{Lon:F5}) distance {D:F0} m",
            preset, anchor.Geo.Latitude, anchor.Geo.Longitude, anchor.DistanceMeters);
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

            // BENCH harness: mark the run boundary; OnBenchTick starts the next run (warm) on its next tick.
            // After the last run, quit so the log ends exactly at the benchmark's edge.
            if (benchTimer is not null)
            {
                int doneRun = benchTotalRuns - benchRunsRemaining;
                Serilog.Log.Information("[Bench] run {Run}/{Total} complete", doneRun, benchTotalRuns);
                if (benchRunsRemaining <= 0)
                {
                    Serilog.Log.Information("[Bench] all runs complete — quitting");
                    benchTimer.Stop();
                    Microsoft.Maui.Controls.Application.Current?.Quit();
                }
            }
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
    private float dragonFlapCyclePrev;  // last tick's wrapped flap cycle — detects the down-stroke crossing for the whoosh
    private float dragonLastFlapActivity; // pose-side flap activity stashed for the flight-bed wing-flutter level
    private float dragonClipTimePrev;   // last tick's wrapped clip time (animated variant) — fallback whoosh cue
    private string? dragonWingBoneName; // wing TIP bone (largest posed horizontal reach) — the synced whoosh tracker
    private bool dragonWingBoneSearched;
    private float dragonWingZPrev;      // wing tip's model-space Z last tick (velocity for the up/down-stroke gate)
    private bool dragonWingArmed;       // upstroke seen → the next downstroke onset fires the whoosh
    private float dragonNextRoarSeconds; // countdown to the next soar roar
    private int dragonRoarCounter;      // deterministic stride for the roar cadence
    private readonly Services.DragonAudioService dragonAudio = new(); // procedural breath/flap/boom/hiss/roar (desktop-only inside)
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
    private float dragonPrevYawCommand;        // previous tick's shaped yaw command — the entry stroke fires on the GATE crossing
    private const float DragonStrokeCommandGate = 0.6f; // |YawCommand| threshold that commits a turn (a tap never reaches it)
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
    private float dragonSeatLogAccum;          // ~1 Hz throttle for the [DragonSeat] probe diagnostic
    private const float DragonAnimStrokeDelaySeconds = 0.15f; // "dokończenie ruchu oboma"
    private const float DragonAnimStrokeFlapSeconds = 0.45f;  // the single outer-wing beat
    private const float DragonAnimStrokeRaiseDeg = 26f;       // wing lifts…
    private const float DragonAnimStrokeSlamDeg = 78f;        // …then slams down through level
    private const float DragonAnimStrokeInnerScale = 0.32f;   // the INNER wing echoes the beat lightly (natural asymmetry)

    // Foot bones used to seat the perched dragon's SOLES on the summit (per variant; posed positions).
    private static readonly string[] DragonAnimatedFootBones = ["l_ball.163", "r_ball.175", "l_toeA.164", "r_toeA.176"];
    private static readonly string[] DragonClassicFootBones = ["Foot.L", "Foot.R"];

    // Head/mouth bones — fire spawns from the POSED head (which the animation nods/scans), not a fixed body point.
    private static readonly string[] DragonAnimatedMouthBones = ["head.25", "jaw_01.26"];
    private static readonly string[] DragonClassicMouthBones = ["Head.001", "SkullControl"];
    private const float DragonSnoutOffsetMeters = 5f;      // snout tip ahead of the head bone, along the aim
    private Vector3? dragonMouthWorld;                     // posed head world position (exaggerated Z) for the fire muzzle
#pragma warning disable CS0414 // KEPT for the next session's perch-seating work (see docs/HANDOFF-2026-07-09) — do NOT delete
    private float? dragonPerchGroundElev; // rendered-mesh elevation under the perch, sampled once (feet sit on the DRAWN rock)
#pragma warning restore CS0414
    private const float DragonFootPadMeters = 0.5f; // sink the measured foot bones this far so soles/claws touch (tune)

    // ── Fire breath (F held = stream of fireballs from the mouth) ──────────────────────────────────────────
    // Balls simulate in REAL metres (like the flight body): position/velocity real, Z exaggerated only when
    // building the render sprites. A ball dies on TTL or bursts (short expanding flash) on terrain contact.
    // The SIM that populates these lives in the desktop-only (#if WINDOWS) region — on other TFMs the
    // compiler sees write-less members and raises CS0649/CS0169; that is by design, not a bug.
#pragma warning disable CS0649, CS0169
    private struct DragonFireball
    {
        public System.Numerics.Vector2 XY;
        public float Elevation;
        public System.Numerics.Vector2 VelocityXY;
        public float VelocityZ;
        public float Age;
        public float Seed;
        public int TargetDragon; // index into aiFlock this ball homes onto (−1 = unguided, flies straight)
    }

    // Fire billboard kind — matches the fragment-shader branch in Terrain3DGlRenderer.
    private enum FireKind { Flame = 0, Flash = 1, Shock = 2, Ember = 3, Puff = 4, Smoke = 5, Steam = 6 }

    // An explosion particle (flash / expanding fireball puff / shock ring / arcing ember). Real metres; Z is
    // exaggerated only when the render sprite is built.
    private struct FireParticle
    {
        public Vector3 Pos;
        public Vector3 Vel;
        public float Age;
        public float Life;
        public float Seed;
        public float Size0; // start radius (m)
        public float Size1; // end radius (m)
        public FireKind Kind;
    }

    private readonly List<DragonFireball> dragonFireballs = [];
    private readonly List<FireParticle> dragonFireParticles = [];
#pragma warning disable IDE0044 // mutated (dragonBurstCounter++); readonly would break the build (CS0191)
    private int dragonBurstCounter;
#pragma warning restore IDE0044
#pragma warning restore CS0649, CS0169
    private readonly List<MapaTur.App.Services.Terrain3DGlRenderer.FireballSprite> dragonFireSprites = [];       // additive
    private readonly List<MapaTur.App.Services.Terrain3DGlRenderer.FireballSprite> dragonFireSmokeSprites = []; // straight-alpha (2nd pass)

    // ── DRAGON-FOOT PLACEMENT PROBE (KNOWN OPEN ITEM, see docs/HANDOFF) ──────────────────────────────────────
    // Diagnostic markers drawn AFTER the final dragon transform, one per candidate anchor, so we can SEE on the
    // rendered rock which point actually sits at the visible claws. Legend: RED=model origin, GREEN=bind-bounds
    // centre (where the code plants worldPos), BLUE=posed foot-bone anchor (the drawn feet), YELLOW=target
    // rendered-mesh point (where the feet SHOULD land). Cleared when not perched.
    private readonly List<MapaTur.App.Services.Terrain3DGlRenderer.DebugMarker> dragonDebugMarkers = [];
    private readonly List<MapaTur.App.Services.Terrain3DGlRenderer.DebugMarker> climbMarkers = []; // climb session hold dots (overlay)

    // Climb auto-belay dressed as real gear: a quickdraw on every bolt + the sagging rope, built by
    // ClimbProtectionGeometry each walk tick and drawn by the renderer's depth-tested climb-gear pass.
    private readonly List<Vector3> climbAnchorScratch = [];
    private readonly List<MapaTur.Application.Terrain.ClimbProtectionGeometry.Quickdraw> climbQuickdraws = [];
    private readonly List<Vector3> climbRopePoints = [];
    private readonly List<MapaTur.App.Services.Terrain3DGlRenderer.GearRibbon> climbGearRibbons = [];
    private readonly List<MapaTur.App.Services.Terrain3DGlRenderer.GearRing> climbGearRings = [];
    private readonly List<List<Vector3>> climbSlingPool = []; // reused 2-point sling polylines, one per quickdraw

    // Catalogued climbing topo: each massif (Mnich, Mnich Małołącki, …) anchors independently once the
    // terrain around ITS summit is loaded — feeds the guaranteed-hold routes to the climb controller, and
    // overlays a HIGHLIGHTED line + name label per route (the proposed passage), drawn through the
    // climb-gear ribbon pass + the Skia label overlay.
    private readonly Dictionary<string, IReadOnlyList<MapaTur.Application.Terrain.WorldClimbingRoute>> climbingMassifWorld = [];
    private readonly List<MapaTur.Application.Terrain.WorldClimbingRoute> climbingAllWorldRoutes = [];
    // Provisional anchors: snapped top still misses the catalogued elevation (coarse DEM smooths the
    // needle away at startup) → re-snap while the 1 m terrain streams in, keep the best so far.
    private readonly Dictionary<string, float> climbingMassifMissMeters = [];
    private long climbingResnapNotBeforeTicks;

    // CALIBRATION grid (temporary tooling): labelled marker chessboard pinned to a FIXED geo origin near
    // Mnich. The user screenshots the wall from the topo-photo viewpoint; the grid labels visible in that
    // shot let us transcribe route lines from the purchased topo into summit-relative offsets. Columns
    // A.. run west→east every 20 m, rows 0.. run south→north. Disable after calibration.
    private bool climbCalibMarkersVisible; // OFF by default (calibration tool only); the 'M' key toggles it on when needed
    // Centred on the CORRECTED Mnich needle. DENSE (5 m) so route bends can be pinned to markers, and
    // placed on the DETAIL surface ONLY (fine 1 m sampler, never the coarse base — the base is ~130 m
    // below the needle, "całkiem inne miejsce"). Rebuilt as the fine terrain streams in until coverage
    // is stable, so no half is left empty. Labels every 20 m show the (dx,dy) offset from the summit.
    private static readonly GeoPoint ClimbCalibOrigin = new(49.192532, 20.054851);
    private const float ClimbCalibStepMeters = 5f;
    private const int ClimbCalibColumns = 35; // x from -70 (west) to +100 (east)
    private const int ClimbCalibRows = 39;    // y from -110 (south) to +80 (north)
    private const float ClimbCalibMinX = -70f;
    private const float ClimbCalibMinY = -110f;
    private long climbCalibNextBuildTicks;
    private bool climbDemDumped;
    private readonly List<MapaTur.App.Services.Terrain3DGlRenderer.DebugMarker> climbCalibMarkers = [];
    private readonly List<(string Text, Vector3 World)> climbCalibLabels = [];
    private readonly List<MapaTur.App.Services.Terrain3DGlRenderer.DebugMarker> debugMarkersRender = [];
    private float climbCalibExaggeration = float.NaN;
    private readonly List<MapaTur.App.Services.Terrain3DGlRenderer.GearRibbon> climbRouteRibbons = [];
    private readonly List<(string Text, Vector3 World, SKColor Color)> climbRouteLabels = [];
    private readonly List<MapaTur.App.Services.Terrain3DGlRenderer.GearRibbon> renderGearRibbons = []; // topo + session gear, rebuilt per paint
    private float climbRouteOverlayExaggeration = float.NaN; // rebuild the overlay when the Pion slider changes
    private readonly MapaTur.App.Services.GripClimbController gripClimb = new(); // hold-by-hold climbing (C toggles)
    private bool walkClimbToggleQueued;
    private bool walkBelayReleaseQueued; // X — deliberately drop the auto-belay (hanging → free fall)

    // ── AI DRAGON FLOCK ── a small mixed-species flock that circles the nearby peaks (ambient) and drifts toward
    // the player's dragon when it flies close. Each member owns its OWN posed model instance (independent pose),
    // its DragonFlight body (same physics as the player) and a DragonAiPilot policy. Updated from the render.
    // Kept to 3 (perf — more stutters). Three DIFFERENT species, each colour-tinted, for variety.
    private const int AiFlockCount = 3;
    private const float AiFlockOrbitRadiusMeters = 300f;
    private const float AiFlockCruiseHeightMeters = 130f;      // orbit altitude above the home peak
    private const float AiFlockReactRadiusMeters = 900f;       // player nearer than this → the orbit drifts onto him
    private const float AiFlockModelSizeMeters = 30f;          // clearly visible dragons (bigger than the ridden 24 m)

    private enum AiFlockKind { Animated, Prowler, Static }

    private sealed class AiFlockDragon
    {
        public required MapaTur.Application.Terrain.DragonFlight Flight { get; init; }
        public required MapaTur.Application.Terrain.DragonAiPilot Pilot { get; init; }
        public required MapaTur.Application.Terrain.SkinnedModel Model { get; init; }
        public required AiFlockKind Kind { get; init; }
        public int AnimClip { get; init; } = -1;                                // Animated: baked "flying" clip
        public MapaTur.Application.Terrain.ProwlerDragonRig? Rig { get; init; }  // Prowler: procedural wing beat
        public byte[]? TextureBytes { get; init; }
        public Vector3 Tint { get; init; } = Vector3.One;
        public System.Numerics.Vector2 HomePeakXY { get; set; }
        public float AnimTime { get; set; }
        public float FlapPhase { get; set; }
        public float TailPhase { get; set; }
        public bool Alive { get; set; } = true; // a fireball hit kills it → skipped everywhere (kept in the list so ball target indices stay stable)
    }

    // Per-member colour tints (multiplied over the hide) — clearly distinct so the flock reads as different dragons.
    private static readonly Vector3[] AiFlockTints =
    {
        new(0.55f, 0.75f, 1.25f), // icy blue
        new(1.25f, 0.95f, 0.5f),  // gold
        new(1.2f, 0.5f, 1.1f),    // violet
    };

    private readonly List<AiFlockDragon> aiFlock = [];
    private readonly List<MapaTur.App.Services.Terrain3DGlRenderer.AiDragonInstance> aiFlockInstances = [];
    // The three species, staged once on load; each becomes one flock member (own posed instance).
    private MapaTur.Application.Terrain.SkinnedModel? aiModelAnimated;
    private int aiClipAnimated = -1;
    private MapaTur.Application.Terrain.SkinnedModel? aiModelProwler;
    private MapaTur.Application.Terrain.SkinnedModel? aiModelRed;
    private bool aiFlockModelsReady;
    private bool aiFlockLoading;
    private readonly System.Diagnostics.Stopwatch aiFlockClock = new();
    private double aiFlockLastSeconds;
    private bool dragonFireHeld;
    // Mutated every frame in StepDragonFire (-= dt / ++ / =). IDE0044 misfires "make readonly" here, and obeying
    // it (as `dotnet format` did on this branch) makes the build fail CS0191 — the vicious cycle that left the
    // committed branch un-buildable and running a stale exe. Keep them mutable; suppress the false positive.
#pragma warning disable IDE0044, CS0169 // CS0169: the only writer (StepDragonFire) is desktop-only (#if WINDOWS)
    private float dragonFireCooldown; // stream-rate countdown while F is held
    private int dragonFireCounter;    // increments per spawned ball (flicker seed)
#pragma warning restore IDE0044, CS0169
    private const float DragonFireCooldownSeconds = 0.034f; // very dense stream while F is held (balls FUSE into one jet)
    private const int DragonFireMaxBalls = 88;              // hard cap so a held stream / point-blank burst can't flood fill
    private const float DragonFireSpeedMetersPerSecond = 105f; // muzzle speed on top of the dragon's own — a diving dragon must NOT catch its own breath
    private const float DragonFireTtlSeconds = 2.2f;
    private const float DragonFireMuzzleOffsetMeters = 11f;  // roughly the head, ahead of the body centre
    private const float DragonFireRadiusMeters = 5f; // big — the dense stream fuses into one wide continuous jet
    // Auto-aim: a fireball locks onto the AI dragon whose bearing is within this cone of the aim, then homes on
    // it (steering toward its live position) and bursts on contact.
    private static readonly float DragonFireLockConeCos = MathF.Cos(10f * MathF.PI / 180f); // ±10°
    private const float DragonFireHomingPerSecond = 3.2f;   // how fast the ball re-aims onto a moving target
    private const float DragonFireHitRadiusMeters = 14f;    // burst when this close to the locked dragon
    private MapaTur.Application.Terrain.DragonFlightPhase dragonPrevPhase = MapaTur.Application.Terrain.DragonFlightPhase.Flying;
    private Matrix4x4 dragonWorldMatrix = Matrix4x4.Identity;
    private Matrix4x4 dragonNormalMatrix = Matrix4x4.Identity;

    // ── 3rd-person walk avatar (KayKit "Adventurers" Rogue_Hooded → hiker.glb; loaded once per walk session) ──
    private MapaTur.Application.Terrain.SkinnedModel? humanoidModel3D;
    private bool humanoidModelLoading;
    private int humanoidIdleAnimIndex = -1;        // resolved at load; base pose for the procedural climb + fallback
    private MapaTur.Application.Terrain.HumanoidAnimator? humanoidAnimator;
    private float humanoidClimbPhase;              // drives the alternating arm-reach while climbing / the hang sway
    private const float ClimbCadenceHz = 1.1f;     // arm-plant beats per second while climbing
    private const float ClimbReachDegrees = 55f;   // ⚠ TUNE visually: how far the arms reach up the wall
    private Matrix4x4 humanoidWorldMatrix = Matrix4x4.Identity;
    private Matrix4x4 humanoidNormalMatrix = Matrix4x4.Identity;
    private System.Numerics.Vector2 walkPrevXY;    // last-tick walker XY → ground speed (drives clip + anti-skate)
    private readonly bool walkThirdPerson = true;  // draw the 3D avatar + follow camera (vs the 1st-person ciupagas)
    private bool walkShootQueued;                  // F pressed → fire the crossbow on the next tick
    private int humanoidShootAnimIndex = -1;       // "1H_Ranged_Shoot" clip index (−1 = model has no ranged clip)
    private float walkCamBack = WalkCamBackMeters;  // 3rd-person boom length behind the walker (mouse wheel zooms it)
    private float walkCamYawOffset;                 // free-look (RMB while climbing): camera orbit yaw off the heading
    private float walkCamPitchFree;                 // free-look extra pitch
    private bool walkRmbHeld;                        // right button held → free-look camera (heading + climb unchanged)

    // Crossbow bolts in flight. Positions are world metres (X,Y horizontal + Z real elevation); the renderer draws
    // one static arrow model per entry through the reusable world/normal matrix lists (rebuilt each walk tick).
    private MapaTur.Application.Terrain.SkinnedModel? arrowModel3D;
    private bool arrowModelLoading;
    private readonly List<ArrowProjectile> arrows = new();
    private readonly List<Matrix4x4> arrowWorlds = new();
    private readonly List<Matrix4x4> arrowNormals = new();

    private struct ArrowProjectile
    {
        public Vector3 Pos; // X,Y world metres (unexaggerated horizontal) + Z real elevation metres
        public Vector3 Vel; // metres / second (real)
        public float Age;   // seconds since it was loosed
    }
    private const float DragonModelSizeMeters = 24f; // target max extent of the dragon in world metres
    private const float DragonFlapLiftMeters = 1.6f; // rises on the down-stroke, sinks on the up-stroke
    // Model-orientation tuning (glTF bone/axis frames vary — adjusted by eye):
    private static readonly float DragonYawOffset = MathF.PI / 2f; // model head is +Z; after Y-up→Z-up remap, +90° aims it along +X (head forward)
    private const float DragonDropMeters = 1f; // slight seat below the flight point (centring now does the heavy lifting)
    // The ANIMATED model's bind bounds are pulled DOWN by its long legs/tail, so bounds-centring leaves the
    // BODY well above the flight point ("jest 10 m nade mną") — seat that variant much lower. Per-variant,
    // because the classic model's centring is already right.
    private const float DragonAnimatedDropMeters = 11f;

    // 3rd-person avatar + follow-camera tuning (real metres; the exaggeration is applied only when placing in world).
    private const float HumanoidHeightMeters = 1.8f;              // target model height (tallest bind extent → 1.8 m)
    private static readonly float HumanoidYawOffset = MathF.PI / 2f; // ⚠ TUNE visually (same Y-up→Z-up basis as DragonYawOffset)
    private const float WalkCamBackMeters = 4.0f;               // default eye distance behind the walker (wheel adjusts)
    private const float WalkCamBackMinMeters = 1.6f;            // closest wheel-zoom (over-the-shoulder)
    private const float WalkCamBackMaxMeters = 12f;             // farthest wheel-zoom
    private const float WalkCamHeightMeters = 2.6f;             // eye height above the feet (over the head → looks past it)
    private const float WalkCamMaxUpRadians = 1.20f;            // ~69° — crane the gaze up at a wall / peak above you
    private const float WalkCamMaxDownRadians = 0.90f;          // ~51° — look down at your feet / the drop
    private const float WalkCamGroundMarginMeters = 0.6f;       // keep the eye at least this far above the ground under it

    // Crossbow bolt (F fires it).
    private const float ArrowLengthMeters = 0.9f;
    private const float ArrowSpeedMetersPerSecond = 55f;             // fast bolt
    private const float ArrowGravityMetersPerSecondSquared = 6f;     // mild — barely drops over its short life
    private const float ArrowLifetimeSeconds = 2.0f;
    private const float ArrowSpawnHeightMeters = 1.3f;              // chest / crossbow height above the feet
    private const int MaxArrowsInFlight = 16;
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

    // Cinematic breath orbit: hold the fire key ≥ the delay and the chase cam starts a slow sideways sweep
    // around the dragon (a sustained jet reads far better from the side); release → ease back behind the
    // tail the SHORT way. The offset bends only the EYE azimuth — the heading chase above is untouched.
    private float dragonFireHoldSeconds;
    private float dragonFireOrbitAngle;
    private const float DragonFireOrbitDelaySeconds = 2f;   // hold this long before the orbit engages
    private const float DragonFireOrbitRadPerSec = 0.45f;   // slow sweep — a full circle in ~14 s
    private const float DragonFireOrbitRampSeconds = 1.5f;  // speed eases in after the delay (no jerk)
    private const float DragonFireOrbitReturnPerSec = 2.5f; // release → settle back in ~0.5 s

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

    // CONTACT-grade ground sample (metres) — the REAL baked surface (z17→z16), never the virtual z18/z19
    // synthesis. For high-frequency, positionally-scattered probes: fireball contact, fire-target probing,
    // flight AGL (dragon physics + audio). Those need ±0.35 m accuracy at most, and routing them through the
    // fine sampler made every fire stream spawn dozens of background tile SYNTHESES per tick over the cold
    // ground ahead of the dragon ("ogień strasznie laguje", 2026-07-11). Falls back to the fine sampler when
    // the contact one is not wired (pre-bake scenes), then to the coarse base — same chain as walk ground.
    private float? SampleContactGround(System.Numerics.Vector2 xy)
    {
        if (WorldFrame is not { } frame)
        {
            return null;
        }

        GeoPoint geo = frame.WorldToGeo(new Vector3(xy.X, xy.Y, 0f));
        if (ContactElevationSampler is { } contact && contact(geo.Longitude, geo.Latitude) is { } contactElev)
        {
            return (float)contactElev;
        }

        if (ContactElevationSampler is null
            && FineElevationSampler is { } fine && fine(geo.Longitude, geo.Latitude) is { } fineElev)
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

        // F7→F8 position continuity: capture the dragon's position/heading BEFORE the exit below tears it down, so
        // the walker appears where the dragon was (facing the way it flew) — not at the far chase-cam eye.
        System.Numerics.Vector2? carryXY = null;
        float? carryHeading = null;
        float? fromDragonElev = null;
        if (dragon is { } dragonFrom)
        {
            carryXY = dragonFrom.PositionXY;
            carryHeading = dragonFrom.HeadingRadians;
            fromDragonElev = dragonFrom.ElevationMeters;
        }

        StopFlight(); // never walk during a cinematic fly-through
        if (dragonActive)
        {
            IsDragonFlightActive = false; // walk and dragon are exclusive
        }

        var startXY = carryXY ?? new System.Numerics.Vector2(Camera.Position.X, Camera.Position.Y);
        walker = new MapaTur.Application.Terrain.WalkPhysics(startXY, SampleWalkGround);
        walkPrevXY = startXY;
        MapaTur.App.Services.GripClimbController.PreloadClimberModel(); // ready before the first wall grab
        Serilog.Log.Information(
            "[Walk] enter from={From} startXY=({X:F0},{Y:F0}) ground={G} feet={F:F0} grounded={Gr} dragonElev={DE}",
            carryXY is not null ? "dragon" : "orbit", startXY.X, startXY.Y,
            SampleWalkGround(startXY) is { } gsample ? gsample.ToString("F0") : "null",
            walker.FeetElevation, walker.IsGrounded, fromDragonElev is { } de ? de.ToString("F0") : "-");

        Vector3 viewDir = Camera.Target - Camera.Position;
        walkHeadingRadians = carryHeading ?? MathF.Atan2(viewDir.Y, viewDir.X);
        walkLookPitchRadians = 0f; // start looking at the horizon
        walkFwd = walkBack = walkStrafeLeft = walkStrafeRight = walkRun = false;
        walkJumpQueued = false;
        walkRmbHeld = false;
        walkCamYawOffset = 0f;
        walkCamPitchFree = 0f;
        walkShootQueued = false;
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

#if WINDOWS
        StartVsyncLoop(); // same vsync-paced loop as dragon flight (the timer above is created, never started)
#else
        walkTimer.Start();
#endif
        LoadHumanoidModelAsync(); // fire-and-forget; the follow camera runs even before the avatar streams in
        LoadArrowModelAsync();    // crossbow bolt mesh (F fires it)
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

        arrows.Clear();
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

        // Fire the crossbow (F): the animator plays the one-shot ranged clip; here we just loose the bolt.
        bool shootThisTick = walkShootQueued && humanoidShootAnimIndex >= 0;
        walkShootQueued = false;
        if (shootThisTick && arrowModel3D is not null && arrows.Count < MaxArrowsInFlight)
        {
            // Loose a bolt where the camera aims (heading + look pitch), from chest height just in front of the walker.
            float aim = Math.Clamp(walkLookPitchRadians, -WalkCamMaxDownRadians, WalkCamMaxUpRadians);
            float ca = MathF.Cos(aim);
            var dir = new Vector3(ca * ch, ca * sh, MathF.Sin(aim));
            arrows.Add(new ArrowProjectile
            {
                Pos = new Vector3(w.PositionXY.X + (ch * 0.5f), w.PositionXY.Y + (sh * 0.5f), w.FeetElevation + ArrowSpawnHeightMeters),
                Vel = dir * ArrowSpeedMetersPerSecond,
                Age = 0f,
            });
        }

        // X — deliberate belay release. While the grip session owns the body its own pitons travel with it
        // (no removal API — by design), so X applies only to WalkPhysics: hanging on the rope → free fall.
        if (walkBelayReleaseQueued)
        {
            walkBelayReleaseQueued = false;
            if (gripClimb.IsActive)
            {
                Serilog.Log.Information("[Walk] belay release (X) ignored — a grip session is active (let go first: C/Space)");
            }
            else if (w.Pitons.Count > 0 || w.IsRoped)
            {
                bool wasRoped = w.IsRoped;
                w.ReleaseProtection();
                Serilog.Log.Information(
                    "[Walk] belay released (X) at ({X:F0},{Y:F0}) feet={Feet:F0} m{Fall}",
                    w.PositionXY.X, w.PositionXY.Y, w.FeetElevation,
                    wasRoped ? " — free fall from the rope" : " (gear dropped)");
            }
        }

        // Hold-by-hold climbing: C grabs the wall ahead (or lets go). While a session is active it is the
        // ONLY owner of the body — WalkPhysics is not stepped, just mirrored for the camera/HUD/rope.
        bool releaseClimb = jump;
        if (walkClimbToggleQueued)
        {
            walkClimbToggleQueued = false;
            if (gripClimb.IsActive)
            {
                releaseClimb = true;
            }
            else if (gripClimb.TryEnter(w, SampleWalkGround, walkHeadingRadians))
            {
                Serilog.Log.Information(
                    "[Climb] climb.session_started at ({X:F0},{Y:F0}) elev={E:F0} m",
                    w.PositionXY.X, w.PositionXY.Y, w.FeetElevation);
            }
        }

        if (gripClimb.IsActive)
        {
            var climbIntent = new System.Numerics.Vector2(
                (walkStrafeRight ? 1f : 0f) - (walkStrafeLeft ? 1f : 0f),
                (walkFwd ? 1f : 0f) - (walkBack ? 1f : 0f));
            gripClimb.Tick(dt, climbIntent, releaseClimb, frame.VerticalExaggeration, w);
        }
        else
        {
            // hangHeld stays FALSE: the legacy continuous ciupaga-climb (LMB glue-to-slope + sinusoidal
            // arm wave) is replaced by the hold-by-hold ClimbSession on C. Mixing both let the walker
            // enter the old climb at the same wall and fight the session for the body. The rope arrest
            // in WalkPhysics still protects falls after a session lets go.
            w.Step(dt, wish, speed, jump, hangHeld: false);
        }

        float exaggeration = frame.VerticalExaggeration;

        // Third person: how far the walker actually moved this tick (the walk gate can block a wished step, so
        // ground speed — not the input — drives the clip and, later, guards foot-skate). Then pose + seat the
        // avatar; it may still be streaming in on the first frames, so the camera below runs regardless.
        float groundSpeed = dt > 1e-4f ? (w.PositionXY - walkPrevXY).Length() / dt : 0f;
        walkPrevXY = w.PositionXY;
        if (humanoidModel3D is { } hm)
        {
            if (gripClimb.IsActive && gripClimb.HasPose)
            {
                // The climb controller already posed + skinned the model from the whole-body solve;
                // just take its transforms instead of the walk seat/pose path.
                humanoidWorldMatrix = gripClimb.HumanoidWorldMatrix;
                humanoidNormalMatrix = gripClimb.HumanoidRotationMatrix;
            }
            else
            {
                PoseAndSeatHumanoid(hm, w, exaggeration, dt, groundSpeed, shootThisTick);
            }
        }

        AdvanceAndBuildArrows(exaggeration, dt);
        BuildClimbProtection(w, exaggeration, new Vector2(ch, sh));
        climbMarkers.Clear();
        gripClimb.AppendHoldMarkers(climbMarkers, exaggeration);

        // Third-person camera: the eye sits a fixed boom BEHIND and ABOVE the walker (so it never dives into the
        // ground), and the GAZE pitches freely with the look input — so you can crane the view UP at a wall or peak
        // above you, or DOWN at your feet, not just flat ahead. Mouse drag / R + PgUp / PgDn feed walkLookPitchRadians
        // (+ = up). WASD stays heading-relative and the mouse still turns the heading (the whole rig turns with it).
        // Free-look (RMB while climbing) orbits the camera by walkCamYawOffset around the climber; when released it
        // eases back behind the heading. The CLIMB direction (ch/sh from the heading) is untouched.
        if (!walkRmbHeld)
        {
            walkCamYawOffset *= 0.85f;
            walkCamPitchFree *= 0.85f;
        }

        float camYaw = walkHeadingRadians + walkCamYawOffset;
        float cc = MathF.Cos(camYaw), sc = MathF.Sin(camYaw);
        var eye = new Vector3(
            w.PositionXY.X - (cc * walkCamBack),
            w.PositionXY.Y - (sc * walkCamBack),
            (w.FeetElevation + WalkCamHeightMeters) * exaggeration);
        if (SampleWalkGround(new System.Numerics.Vector2(eye.X, eye.Y)) is float camGround)
        {
            float minEyeZ = (camGround * exaggeration) + WalkCamGroundMarginMeters;
            if (eye.Z < minEyeZ)
            {
                eye.Z = minEyeZ;
            }
        }

        float aimPitch = Math.Clamp(walkLookPitchRadians + walkCamPitchFree, -WalkCamMaxDownRadians, WalkCamMaxUpRadians);
        float cosAim = MathF.Cos(aimPitch);
        var lookDir = new Vector3(cosAim * cc, cosAim * sc, MathF.Sin(aimPitch));
        ApplyFreeCamera(eye, eye + (lookDir * WalkLookDistanceMeters));

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

        // F8→F7 position continuity: capture the walker's position/heading BEFORE the exit below tears walk down, so
        // the dragon launches from where the walker stood, facing the way it walked — not the orbit look-at point.
        System.Numerics.Vector2? carryXY = null;
        float? carryHeading = null;
        if (walker is { } walkerFrom)
        {
            carryXY = walkerFrom.PositionXY;
            carryHeading = walkHeadingRadians;
        }

        StopFlight();
        if (walkActive)
        {
            IsWalkModeActive = false; // dragon and walk are exclusive
        }

        var startXY = carryXY ?? new System.Numerics.Vector2(Camera.Target.X, Camera.Target.Y);
        Vector3 viewDir = Camera.Target - Camera.Position;
        float heading = carryHeading ?? MathF.Atan2(viewDir.Y, viewDir.X);
        // Contact-grade ground for the flight physics: per-tick AGL/terrain-follow over ground far ahead of
        // the camera must never trigger virtual-tile synthesis (landing seats on the FINE scan separately).
        dragon = new MapaTur.Application.Terrain.DragonFlight(startXY, heading, SampleContactGround);

        dragonMouseDx = dragonMouseDy = 0f;
        dragonW = dragonS = dragonA = dragonD = false;
        dragonPitchUp = dragonPitchDown = dragonYawLeft = dragonYawRight = false;
        dragonRmbHeld = false;
        dragonCamPitch = 0f;
        dragonCamAzimuth = heading; // start the lazy chase cam in sync (no entry swing)
        dragonFireHoldSeconds = 0f;
        dragonFireOrbitAngle = 0f;
        dragonFlapPhase = 0f;
        dragonFlapCyclePrev = 0f;
        dragonClipTimePrev = 0f;
        dragonWingBoneSearched = false; // re-resolve per flight (the variant can change between entries)
        dragonWingBoneName = null;
        dragonWingZPrev = 0f;
        dragonWingArmed = false;
        dragonNextRoarSeconds = 6f; // first soar cry shortly after the wings settle
        dragonDetailTick = 0;
        dragonActive = true;
        // Defer blocking gen2 collections while flying — with a multi-GB heap they pause 100–700 ms
        // (visible hitches). Background/gen0/gen1 still run; Interactive is restored on exit.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        dragonPerfDts.Clear();
        dragonClock.Restart();
        dragonLastSeconds = 0.0;
        if (dragonTimer is null)
        {
            dragonTimer = Dispatcher.CreateTimer();
            dragonTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60 fps
            dragonTimer.Tick += OnDragonTick;
        }

#if WINDOWS
        // Vsync-paced loop instead of the 16 ms DispatcherTimer (created above only so the shared
        // `dragonTimer?.Stop()` guards keep compiling — it is never started here): the timer BEATS against
        // the ~16.7 ms composition clock (a dropped/doubled sim frame every ~¼ s — the "stiff" flight), and
        // its UI-priority ticks jitter. CompositionTarget.Rendering fires once per composed frame → one sim
        // step + one paint per display refresh (dt comes from the stopwatch, so 60/120/144 Hz all integrate).
        StartVsyncLoop();
#else
        dragonTimer.Start();
#endif
        LoadDragonModelAsync(); // fire-and-forget; the procedural Skia dragon shows until the 3D model is ready
        FocusForKeyboard(); // pull keyboard focus onto the canvas so Space flaps and can't "click" a focused button
        dragonAudio.PlayRoar(0.6f); // announce the flight
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

    // Loads the KayKit avatar (hiker.glb) once per session, off the UI thread, and resolves the locomotion clip
    // indices the walk tick plays (Idle / Walking_A / Running_A). Fire-and-forget from EnterWalkMode; until it's
    // ready the follow camera runs and nothing is drawn where the avatar will be.
    private async void LoadHumanoidModelAsync()
    {
        if (humanoidModel3D is not null || humanoidModelLoading)
        {
            return;
        }

        humanoidModelLoading = true;
        try
        {
            // The realistic climber (Mixamo-rigged walk build with the original textures, local-only data)
            // is the DEFAULT avatar; the bundled KayKit hiker is the fallback when the file is absent.
            MapaTur.Application.Terrain.SkinnedModel model;
            string climberWalkPath = Path.Combine(
                Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "models", "RockClimber_Walk.glb");
            if (File.Exists(climberWalkPath))
            {
                model = await Task.Run(() => MapaTur.Application.Terrain.SkinnedModel.Load(climberWalkPath)).ConfigureAwait(false);
                Serilog.Log.Information("[Walk] avatar = realistic climber ({Path})", climberWalkPath);
            }
            else
            {
                await using Stream s = await Microsoft.Maui.Storage.FileSystem.OpenAppPackageFileAsync("hiker.glb").ConfigureAwait(false);
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms).ConfigureAwait(false);
                model = MapaTur.Application.Terrain.SkinnedModel.LoadGlb(ms.ToArray());
                Serilog.Log.Information("[Walk] avatar = bundled hiker (no climber walk model found)");
            }

            int idle = -1, walk = -1, run = -1, shoot = -1, jumpIdle = -1, jumpLand = -1;
            for (int i = 0; i < model.Animations.Count; i++)
            {
                string name = model.Animations[i].Name;
                if (idle < 0 && string.Equals(name, "Idle", StringComparison.OrdinalIgnoreCase)) { idle = i; }
                else if (walk < 0 && string.Equals(name, "Walking_A", StringComparison.OrdinalIgnoreCase)) { walk = i; }
                else if (run < 0 && string.Equals(name, "Running_A", StringComparison.OrdinalIgnoreCase)) { run = i; }
                else if (shoot < 0 && string.Equals(name, "1H_Ranged_Shoot", StringComparison.OrdinalIgnoreCase)) { shoot = i; }
                else if (jumpIdle < 0 && string.Equals(name, "Jump_Idle", StringComparison.OrdinalIgnoreCase)) { jumpIdle = i; }
                else if (jumpLand < 0 && string.Equals(name, "Jump_Land", StringComparison.OrdinalIgnoreCase)) { jumpLand = i; }
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                humanoidModel3D = model;
                humanoidIdleAnimIndex = idle >= 0 ? idle : 0;
                humanoidShootAnimIndex = shoot; // -1 → F does nothing (no ranged clip)
                humanoidAnimator = new MapaTur.Application.Terrain.HumanoidAnimator(
                    new MapaTur.Application.Terrain.HumanoidAnimator.Clips(
                        Idle: humanoidIdleAnimIndex,
                        Walk: walk >= 0 ? walk : humanoidIdleAnimIndex,
                        Run: run,
                        JumpIdle: jumpIdle,
                        JumpLand: jumpLand,
                        Shoot: shoot),
                    model.Animations.Select(x => x.Duration).ToList());
                Canvas.InvalidateSurface();
            });
            Serilog.Log.Information(
                "[Walk] humanoid model loaded: {Prims} prims, extent={Ext:F2}, anims={N}, idle={Idle} walk={Walk} run={Run} shoot={Shoot} jIdle={JI} jLand={JL}, tex={Tex}",
                model.Primitives.Count, model.LocalExtent, model.Animations.Count, idle, walk, run, shoot, jumpIdle, jumpLand,
                model.BaseColorImageBytes is { Length: > 0 } bytes ? $"{bytes.Length / 1024}kB" : "none");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Walk] humanoid model load failed — no 3rd-person avatar this session");
        }
        finally
        {
            humanoidModelLoading = false;
        }
    }

    // Loads the crossbow bolt mesh (arrow.glb — a static, unskinned KayKit prop) once per session. Posed to its
    // bind pose immediately so its geometry/bounds are ready for the per-arrow world matrices.
    private async void LoadArrowModelAsync()
    {
        if (arrowModel3D is not null || arrowModelLoading)
        {
            return;
        }

        arrowModelLoading = true;
        try
        {
            await using Stream s = await Microsoft.Maui.Storage.FileSystem.OpenAppPackageFileAsync("arrow.glb").ConfigureAwait(false);
            using var ms = new MemoryStream();
            await s.CopyToAsync(ms).ConfigureAwait(false);
            var model = MapaTur.Application.Terrain.SkinnedModel.LoadGlb(ms.ToArray());
            model.Pose(0, 0f); // static mesh → hold bind pose so PosedPositions/bounds are ready to draw

            MainThread.BeginInvokeOnMainThread(() => arrowModel3D = model);
            Serilog.Log.Information(
                "[Walk] arrow model loaded: {Prims} prims, extent={Ext:F2}, tex={Tex}",
                model.Primitives.Count, model.LocalExtent,
                model.BaseColorImageBytes is { Length: > 0 } bytes ? $"{bytes.Length / 1024}kB" : "none");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Walk] arrow model load failed — F shows the shoot motion but no bolt flies");
        }
        finally
        {
            arrowModelLoading = false;
        }
    }

    // Advances every live bolt (ballistic, mild gravity), drops those that expire or hit the ground, and rebuilds
    // the per-arrow world/normal matrices. The arrow mesh's shaft is its local +Y axis, aligned to the bolt's
    // flight direction in render space (Z exaggerated), scaled to ArrowLengthMeters, pivoted on its centre.
    private void AdvanceAndBuildArrows(float exaggeration, float dt)
    {
        arrowWorlds.Clear();
        arrowNormals.Clear();
        if (arrowModel3D is not { } model)
        {
            arrows.Clear();
            return;
        }

        float scale = ArrowLengthMeters / MathF.Max(0.001f, model.LocalExtent);
        Matrix4x4 centre = Matrix4x4.CreateTranslation(-((model.BoundsMin + model.BoundsMax) * 0.5f));
        Matrix4x4 scaleM = Matrix4x4.CreateScale(scale);

        for (int i = arrows.Count - 1; i >= 0; i--)
        {
            ArrowProjectile a = arrows[i];
            a.Vel.Z -= ArrowGravityMetersPerSecondSquared * dt;
            a.Pos += a.Vel * dt;
            a.Age += dt;

            bool hitGround = SampleWalkGround(new System.Numerics.Vector2(a.Pos.X, a.Pos.Y)) is float g && a.Pos.Z <= g;
            if (a.Age > ArrowLifetimeSeconds || hitGround)
            {
                arrows.RemoveAt(i);
                continue;
            }

            arrows[i] = a;

            // Orient the shaft (local +Y) along the flight direction, in render space (Z exaggerated).
            var d = new Vector3(a.Vel.X, a.Vel.Y, a.Vel.Z * exaggeration);
            d = d.LengthSquared() > 1e-6f ? Vector3.Normalize(d) : Vector3.UnitX;
            Vector3 up = MathF.Abs(d.Z) < 0.99f ? Vector3.UnitZ : Vector3.UnitX;
            Vector3 ax = Vector3.Normalize(Vector3.Cross(up, d)); // local X image
            Vector3 az = Vector3.Cross(d, ax);                    // local Z image
            var rot = new Matrix4x4(
                ax.X, ax.Y, ax.Z, 0f,
                d.X, d.Y, d.Z, 0f,   // local Y (shaft) → flight direction
                az.X, az.Y, az.Z, 0f,
                0f, 0f, 0f, 1f);

            var worldPos = new Vector3(a.Pos.X, a.Pos.Y, a.Pos.Z * exaggeration);
            arrowWorlds.Add(centre * scaleM * rot * Matrix4x4.CreateTranslation(worldPos));
            arrowNormals.Add(rot);
        }
    }

    // Poses the avatar via the HumanoidAnimator (crossfaded Idle/Walk/Run + Jump/Land + shoot, speed-matched) and
    // builds its model→world matrix: scale the tallest bind extent to HumanoidHeightMeters, remap glTF Y-up → world
    // Z-up, yaw to the walk heading, and seat the LOWEST posed vertex (the soles) on the feet elevation.
    private void PoseAndSeatHumanoid(
        MapaTur.Application.Terrain.SkinnedModel model, MapaTur.Application.Terrain.WalkPhysics w,
        float exaggeration, float dt, float groundSpeed, bool shootRequested)
    {
        if (w.IsClimbing || w.IsHanging)
        {
            PoseClimb(model, w, dt); // procedural arm-reach on the wall (no baked climb clip)
        }
        else if (humanoidAnimator is { } anim)
        {
            MapaTur.Application.Terrain.HumanoidAnimator.Blend b =
                anim.Update(dt, groundSpeed, w.IsGrounded, w.VerticalVelocity, shootRequested);
            model.PoseBlend(b.ClipA, b.TimeA, b.ClipB, b.TimeB, b.Weight);
        }
        else
        {
            model.Pose(humanoidIdleAnimIndex >= 0 ? humanoidIdleAnimIndex : 0, 0f);
        }

        (Vector3 pmin, Vector3 pmax) = model.GetPosedBounds();
        Vector3 posedCenter = (pmin + pmax) * 0.5f;
        var footPivot = new Vector3(posedCenter.X, pmin.Y, posedCenter.Z); // lowest posed point = soles
        float scale = HumanoidHeightMeters / MathF.Max(0.001f, model.LocalExtent);
        Matrix4x4 remap = Matrix4x4.CreateRotationX(MathF.PI / 2f); // glTF Y-up → world Z-up
        Matrix4x4 yawRot = Matrix4x4.CreateRotationZ(walkHeadingRadians + HumanoidYawOffset);
        Matrix4x4 rot = remap * yawRot;
        humanoidNormalMatrix = rot;

        var worldPos = new Vector3(w.PositionXY.X, w.PositionXY.Y, w.FeetElevation * exaggeration);
        humanoidWorldMatrix =
            Matrix4x4.CreateTranslation(-footPivot) * Matrix4x4.CreateScale(scale) * rot * Matrix4x4.CreateTranslation(worldPos);
    }

    // Procedural climb pose (KayKit has no climb clip): an idle base with the arms reaching up the wall. Climbing =
    // the two arms plant ALTERNATELY at ClimbCadenceHz (like the ciupaga beats); hanging = both arms up with a slow
    // sway (self-arrest). SetFrame + overlays + Skin (the dragon wing-beat pattern), so no baked clip is needed.
    private void PoseClimb(MapaTur.Application.Terrain.SkinnedModel model, MapaTur.Application.Terrain.WalkPhysics w, float dt)
    {
        model.SetFrame(humanoidIdleAnimIndex >= 0 ? humanoidIdleAnimIndex : 0, 0f); // arms-down base; overlays raise them

        if (w.IsClimbing)
        {
            humanoidClimbPhase += dt * ClimbCadenceHz;
            float s = MathF.Sin(humanoidClimbPhase * 2f * MathF.PI);
            ApplyArmReach(model, MathF.Max(0f, s), MathF.Max(0f, -s)); // alternate left / right plant
        }
        else
        {
            humanoidClimbPhase += dt * 0.5f;
            float sway = 0.15f * MathF.Sin(humanoidClimbPhase * 2f * MathF.PI);
            ApplyArmReach(model, 0.85f + sway, 0.85f - sway); // hang: both arms up, gentle sway
        }

        model.Skin();
    }

    // Overlays an up-reach on each arm (0 = down, 1 = fully raised). ⚠ ClimbReachAxis/sign are a first guess — if the
    // arms bend the wrong way, flip the axis or sign here (this is the one thing that needs a visual check).
    private void ApplyArmReach(MapaTur.Application.Terrain.SkinnedModel model, float leftAmount, float rightAmount)
    {
        float reach = ClimbReachDegrees * (MathF.PI / 180f);
        var axis = Vector3.UnitX;
        model.RotateBoneOverlay("upperarm.l", Quaternion.CreateFromAxisAngle(axis, -reach * leftAmount));
        model.RotateBoneOverlay("upperarm.r", Quaternion.CreateFromAxisAngle(axis, -reach * rightAmount));
        model.RotateBoneOverlay("lowerarm.l", Quaternion.CreateFromAxisAngle(axis, -reach * 0.5f * leftAmount));
        model.RotateBoneOverlay("lowerarm.r", Quaternion.CreateFromAxisAngle(axis, -reach * 0.5f * rightAmount));
    }

    // Rendered sizes/colours of the auto-belay gear. The rope is drawn a touch thicker than a real 10 mm line
    // so it stays readable from the third-person boom; body height is NOT scaled by the vertical exaggeration
    // (only terrain elevations are), so the harness sits a fixed 1 m above the feet.
    private const float HarnessHeightMeters = 1.0f;
    private const float HarnessForwardOffsetMeters = 0.18f; // tie-in at the FRONT of the harness (belly side, toward the wall) — never out of the back
    private const float RopeHalfWidthMeters = 0.013f;
    private const float SlingHalfWidthMeters = 0.011f;
    private const float CarabinerRadiusMeters = 0.045f;
    private static readonly Vector3 RopeColor = new(0.85f, 0.18f, 0.14f);          // classic red lead rope
    private static readonly Vector3 SlingColor = new(0.92f, 0.80f, 0.12f);         // bright nylon sling
    private static readonly Vector3 CarabinerColor = new(0.78f, 0.80f, 0.84f);     // bolt-end biner — bare aluminium
    private static readonly Vector3 RopeCarabinerColor = new(0.86f, 0.68f, 0.22f); // rope-end bent gate — anodised gold
    private static readonly Vector3 BoltColor = new(0.46f, 0.47f, 0.50f);          // steel hanger on the rock

    // Dresses the climb auto-belay as real gear: a quickdraw (bolt hanger + sling + two carabiners) hanging from
    // every planted anchor, and the rope sagging through the BOTTOM carabiners to the climber's harness.
    // ClimbProtectionGeometry does the maths; the renderer's climb-gear pass draws the ribbons + rings.
    // Rebuilt every walk tick from WalkPhysics (ClimbSession mirrors its pitons there).
    private void BuildClimbProtection(MapaTur.Application.Terrain.WalkPhysics w, float exaggeration, Vector2 facing)
    {
        climbGearRibbons.Clear();
        climbGearRings.Clear();
        if (w.Pitons.Count == 0)
        {
            return;
        }

        climbAnchorScratch.Clear();
        foreach (MapaTur.Application.Terrain.WalkPhysics.PitonPoint piton in w.Pitons)
        {
            climbAnchorScratch.Add(new Vector3(piton.PositionXY.X, piton.PositionXY.Y, piton.Elevation * exaggeration));
        }

        // The rope ties in at the FRONT of the harness — offset from the body axis toward the wall (the
        // climber faces it), so from the chase camera the rope disappears in front of the hips, not the back.
        var harness = new Vector3(
            w.PositionXY.X + (facing.X * HarnessForwardOffsetMeters),
            w.PositionXY.Y + (facing.Y * HarnessForwardOffsetMeters),
            (w.FeetElevation * exaggeration) + HarnessHeightMeters);
        MapaTur.Application.Terrain.ClimbProtectionGeometry.Build(climbAnchorScratch, harness, climbQuickdraws, climbRopePoints);

        climbGearRibbons.Add(new(climbRopePoints, RopeColor, RopeHalfWidthMeters));
        for (int i = 0; i < climbQuickdraws.Count; i++)
        {
            MapaTur.Application.Terrain.ClimbProtectionGeometry.Quickdraw quickdraw = climbQuickdraws[i];
            if (climbSlingPool.Count <= i)
            {
                climbSlingPool.Add(new List<Vector3>(2));
            }

            List<Vector3> sling = climbSlingPool[i];
            sling.Clear();
            sling.Add(quickdraw.TopCarabiner);
            sling.Add(quickdraw.BottomCarabiner);
            climbGearRibbons.Add(new(sling, SlingColor, SlingHalfWidthMeters));

            climbGearRings.Add(new(quickdraw.Anchor, BoltColor, 0.030f, 0f, 1f)); // solid disc = the bolt hanger
            climbGearRings.Add(new(quickdraw.TopCarabiner, CarabinerColor, CarabinerRadiusMeters, 0.55f, 0.72f));
            climbGearRings.Add(new(quickdraw.BottomCarabiner, RopeCarabinerColor, CarabinerRadiusMeters, 0.55f, 0.72f));
        }
    }

    private const float RouteLineLiftMeters = 0.1f;    // sit basically ON the rock (was 0.45 → visibly levitating up close)
    private const float RouteLineHalfPixels = 1.6f;    // SCREEN-space half-width: a thin thread at any zoom (not a fat world tube)
    private const float RouteLineSampleStepMeters = 2.0f;
    private const float RouteLabelMaxDistanceMeters = 3000f;

    // Anchors each catalogued massif's topo onto the DEM once the terrain around its summit is loaded.
    // The seed prefers the app's live OSM peak data (name match) over the catalogue coordinate; the
    // two-stage snap (tight grid max + hill-climb) then finds the DEM tower top without wandering onto a
    // higher neighbouring slope. Every anchored massif feeds its world routes to the climb controller
    // (guaranteed hold ladders) and the highlighted passage lines + name labels. Unanchored massifs are
    // retried every frame (one ground sample each); the overlay re-seats when the Pion slider changes.
    private void EnsureClimbingRoutes()
    {
        if (WorldFrame is not { } frame)
        {
            return;
        }

        if (climbingAllWorldRoutes.Count > 0
            && MathF.Abs(frame.VerticalExaggeration - climbRouteOverlayExaggeration) > 0.001f)
        {
            BuildClimbRouteOverlay(climbingAllWorldRoutes, frame); // Pion slider moved — re-seat the lines
        }

        bool allFinal = climbingMassifWorld.Count == MapaTur.Application.Terrain.TatraClimbingRoutes.Massifs.Count
            && climbingMassifMissMeters.Count == 0;
        if (allFinal)
        {
            return; // every massif anchored on terrain matching its catalogued height
        }

        // Provisional re-snaps are throttled — a full snap sweep is ~tens of ms, not a per-frame cost.
        bool resnapDue = Environment.TickCount64 >= climbingResnapNotBeforeTicks;
        bool anyProvisionalProcessed = false;

        foreach (MapaTur.Application.Terrain.TatraClimbingRoutes.ClimbingMassif massif
            in MapaTur.Application.Terrain.TatraClimbingRoutes.Massifs)
        {
            bool anchored = climbingMassifWorld.ContainsKey(massif.Name);
            bool provisional = climbingMassifMissMeters.ContainsKey(massif.Name);
            if (anchored && !provisional)
            {
                continue; // final
            }

            if (anchored && provisional && !resnapDue)
            {
                continue;
            }

            Vector3 seedWorld = frame.GeoToWorld(massif.Summit, 0f);
            var seedXY = new Vector2(seedWorld.X, seedWorld.Y);
            if (SampleWalkGround(seedXY) is null)
            {
                continue; // no terrain around this massif yet (different region or still streaming)
            }

            if (provisional)
            {
                anyProvisionalProcessed = true;
            }

            Vector2 summitXY = MapaTur.Application.Terrain.TatraClimbingRoutes.SnapToLocalMaximum(
                SampleWalkGround, seedXY, radiusMeters: 120f, stepMeters: 2f,
                targetElevationMeters: massif.SummitElevationMeters);
            float? snappedElevation = SampleWalkGround(summitXY);
            float miss = massif.SummitElevationMeters is { } target && snappedElevation is { } got
                ? MathF.Abs(got - target)
                : 0f;

            // Re-snap of a provisional anchor: only take a MEANINGFUL improvement (less flicker).
            if (anchored && provisional && miss >= climbingMassifMissMeters[massif.Name] - 3f)
            {
                if (miss <= 20f)
                {
                    climbingMassifMissMeters.Remove(massif.Name); // close enough — freeze as is
                }

                continue;
            }

            climbingMassifWorld[massif.Name] =
                MapaTur.Application.Terrain.TatraClimbingRoutes.BuildWorldRoutes(massif.Routes, summitXY);
            if (miss > 20f)
            {
                climbingMassifMissMeters[massif.Name] = miss; // provisional: coarse terrain, keep re-snapping
            }
            else
            {
                climbingMassifMissMeters.Remove(massif.Name);
            }

            climbingAllWorldRoutes.Clear();
            foreach (IReadOnlyList<MapaTur.Application.Terrain.WorldClimbingRoute> routes in climbingMassifWorld.Values)
            {
                climbingAllWorldRoutes.AddRange(routes);
            }

            gripClimb.SetClimbingRoutes(climbingAllWorldRoutes);
            BuildClimbRouteOverlay(climbingAllWorldRoutes, frame);
            Serilog.Log.Information(
                "[Climb] {Massif} topo {Kind}: summit world=({X:F0},{Y:F0}) snap={Snap:F1} m "
                + "(seed elev={SeedElev:F0} m → top elev={TopElev:F0} m, drift=({Dx:F0},{Dy:F0})), {Count} routes",
                massif.Name, anchored ? "RE-anchored" : miss > 20f ? "anchored PROVISIONALLY" : "anchored",
                summitXY.X, summitXY.Y, Vector2.Distance(summitXY, seedXY),
                SampleWalkGround(seedXY) ?? float.NaN, snappedElevation ?? float.NaN,
                summitXY.X - seedXY.X, summitXY.Y - seedXY.Y, massif.Routes.Count);

            // While the top misses the catalogued height, list the DEM's actual prominent tops around
            // the seed — the log then SHOWS where this terrain currently puts its summits.
            if (miss > 20f)
            {
                foreach ((Vector2 top, float topElev) in MapaTur.Application.Terrain.TatraClimbingRoutes
                    .ListProminentTops(SampleWalkGround, seedXY, radiusMeters: 500f, stepMeters: 4f, maxCount: 6))
                {
                    GeoPoint topGeo = frame.WorldToGeo(new Vector3(top.X, top.Y, 0f));
                    Serilog.Log.Information(
                        "[Climb]   prominent top near {Massif}: elev={Elev:F0} m at world=({X:F0},{Y:F0}) "
                        + "geo=({Lat:F6},{Lon:F6}) offset=({Dx:F0},{Dy:F0})",
                        massif.Name, topElev, top.X, top.Y, topGeo.Latitude, topGeo.Longitude,
                        top.X - seedXY.X, top.Y - seedXY.Y);
                }
            }

            if (massif.Name == "Mnich" && miss <= 20f)
            {
                DumpMnichDemField(frame, summitXY); // probe the real east-face geometry, once, when finally on the needle
            }
        }

        if (anyProvisionalProcessed)
        {
            climbingResnapNotBeforeTicks = Environment.TickCount64 + 3000;
        }

        BuildClimbCalibrationGrid(frame);
    }

    // Builds the calibration marker grid on the SAME surface the routes are drawn on (SampleWalkGround:
    // fine 1 m detail where streamed, coarse base otherwise) — so markers and routes always coincide,
    // never "in a different place". Rebuilt every few seconds so markers LIFT onto the detail as it
    // streams in (and follow the Pion slider). Full coverage: the base underlies the whole massif.
    private void BuildClimbCalibrationGrid(MapaTur.Application.Terrain.TerrainMesh3D frame)
    {
        if (!climbCalibMarkersVisible)
        {
            return;
        }

        float exaggeration = frame.VerticalExaggeration;
        bool pionChanged = MathF.Abs(exaggeration - climbCalibExaggeration) > 0.001f;
        if (!pionChanged && climbCalibMarkers.Count > 0 && Environment.TickCount64 < climbCalibNextBuildTicks)
        {
            return; // throttle — but keep refreshing so markers track the streaming surface
        }

        Vector3 originWorld = frame.GeoToWorld(ClimbCalibOrigin, 0f);
        if (SampleWalkGround(new Vector2(originWorld.X, originWorld.Y)) is null)
        {
            return; // no terrain here at all yet
        }

        var markers = new List<MapaTur.App.Services.Terrain3DGlRenderer.DebugMarker>();
        var labels = new List<(string, Vector3)>();
        int onDetail = 0;
        // Markers live on the EAST-FACE surface itself (same base-line→summit fan as the routes), so every
        // node lands ON the narrow wall where the routes are — not scattered over the surrounding slopes.
        // uu = position across the face (0 south → 1 north), vv = height (0 base → 1 summit).
        const int uCount = 27, vCount = 24;
        for (int iu = 0; iu < uCount; iu++)
        {
            float uu = iu / (float)(uCount - 1);
            (float bx, float by) = EastFaceBase(uu);
            for (int iv = 0; iv < vCount; iv++)
            {
                float vv = 0.04f + (iv / (float)(vCount - 1)) * 0.94f; // skip the exact summit (all converge)
                float dx = bx * (1f - vv);
                float dy = by * (1f - vv);
                var xy = new Vector2(originWorld.X + dx, originWorld.Y + dy);
                if (SampleWalkGround(xy) is not { } ground)
                {
                    continue;
                }

                if (SampleFineGroundOnly(xy) is not null)
                {
                    onDetail++;
                }

                var at = new Vector3(xy.X, xy.Y, (ground + 1.0f) * exaggeration);
                // Labelled yellow node every ~4th u/v carries its (dx,dy) offset from the summit; the fine
                // dots between (smaller, magenta/cyan) give dense reference for pinning route bends.
                bool labelled = iu % 4 == 0 && iv % 4 == 0;
                Vector3 colour = labelled
                    ? new Vector3(1f, 0.9f, 0.15f)
                    : (iu + iv) % 2 == 0 ? new Vector3(1f, 0.35f, 1f) : new Vector3(0.25f, 1f, 1f);
                markers.Add(new(at, colour, labelled ? 1.0f : 0.5f));
                if (labelled)
                {
                    labels.Add(($"{dx:+0;-0},{dy:+0;-0}", at + new Vector3(0f, 0f, 2.5f * exaggeration)));
                }
            }
        }

        climbCalibMarkers.Clear();
        climbCalibMarkers.AddRange(markers);
        climbCalibLabels.Clear();
        climbCalibLabels.AddRange(labels);
        climbCalibExaggeration = exaggeration;
        climbCalibNextBuildTicks = Environment.TickCount64 + 3000;
        Serilog.Log.Information(
            "[Climb] calibration face-grid: {Count} markers on the east face ({Detail} on fine detail), fineWired={Wired}",
            markers.Count, onDetail, FineElevationSampler is not null);
    }

    // East-face base line (dx east, dy north) — MUST match TatraClimbingRoutes' route base line, so the
    // calibration markers land on exactly the same face surface the routes are drawn on.
    private static (float X, float Y) EastFaceBase(float u)
    {
        (float X, float Y) bs = (42f, -80f), bc = (51f, 0f), bn = (36f, 55f);
        return u <= 0.5f
            ? (bs.X + (bc.X - bs.X) * (u / 0.5f), bs.Y + (bc.Y - bs.Y) * (u / 0.5f))
            : (bc.X + (bn.X - bc.X) * ((u - 0.5f) / 0.5f), bc.Y + (bn.Y - bc.Y) * ((u - 0.5f) / 0.5f));
    }

    // Fine 1 m DETAIL elevation ONLY (no base fallback) — used to count how many grid nodes have detail.
    private float? SampleFineGroundOnly(Vector2 xy)
    {
        if (WorldFrame is not { } frame || FineElevationSampler is not { } fine)
        {
            return null;
        }

        GeoPoint geo = frame.WorldToGeo(new Vector3(xy.X, xy.Y, 0f));
        return fine(geo.Longitude, geo.Latitude) is { } e ? (float)e : (float?)null;
    }

    // One-shot terrain probe: dumps the ground-elevation field around the Mnich anchor to a CSV so the
    // real east-face geometry (width, base run, orientation, steepness) can drive a faithful reproduction
    // of the topo lines instead of guessed parallel offsets. Also logs east/north transect summaries.
    private void DumpMnichDemField(MapaTur.Application.Terrain.TerrainMesh3D frame, Vector2 summitXY)
    {
        if (climbDemDumped)
        {
            return;
        }

        climbDemDumped = true;
        try
        {
            string path = System.IO.Path.Combine(AppContext.BaseDirectory, "mnich-dem-dump.csv");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Mnich east-face DEM probe. dx=east offset (m), dy=north offset (m) from summit.");
            sb.AppendLine($"# summit world=({summitXY.X:F1},{summitXY.Y:F1}) elev={SampleWalkGround(summitXY):F1} m");
            sb.AppendLine("dx,dy,elev");
            for (float dy = -140f; dy <= 140f; dy += 4f)
            {
                for (float dx = -80f; dx <= 160f; dx += 4f)
                {
                    float? e = SampleWalkGround(new Vector2(summitXY.X + dx, summitXY.Y + dy));
                    sb.AppendLine($"{dx:F0},{dy:F0},{(e.HasValue ? e.Value.ToString("F1") : "")}");
                }
            }

            System.IO.File.WriteAllText(path, sb.ToString());
            Serilog.Log.Information("[Climb] DEM dump written: {Path}", path);

            // Compact east transect (dy=0): elevation every 8 m out to +120 east — the face drop profile.
            var east = new System.Text.StringBuilder();
            for (float dx = 0f; dx <= 120f; dx += 8f)
            {
                east.Append($"{dx:F0}m={SampleWalkGround(new Vector2(summitXY.X + dx, summitXY.Y)):F0} ");
            }

            Serilog.Log.Information("[Climb] Mnich east transect (dy=0): {Profile}", east.ToString());
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[Climb] DEM dump failed");
        }
    }

    // Labels for the calibration grid (drawn with the same projection as the route labels).
    private void DrawClimbCalibrationLabels(SKCanvas canvas, int width, int height)
    {
        if (!climbCalibMarkersVisible || climbCalibLabels.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        Matrix4x4 viewProjection = Camera.BuildViewProjection((float)width / Math.Max(1, height));
        using var textHalo = new SKPaint
        { IsAntialias = true, Color = new SKColor(0, 0, 0, 220), Style = SKPaintStyle.Stroke, StrokeWidth = 3f };
        using var textFill = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255) };
        using var font = new SKFont { Size = 11f, Embolden = true };

        foreach ((string text, Vector3 world) in climbCalibLabels)
        {
            if (Vector3.Distance(world, Camera.Position) > 4000f)
            {
                continue;
            }

            if (Camera.ProjectToScreen(world, viewProjection, width, height) is not { } screen)
            {
                continue;
            }

            canvas.DrawText(text, screen.X, screen.Y, SKTextAlign.Center, font, textHalo);
            canvas.DrawText(text, screen.X, screen.Y, SKTextAlign.Center, font, textFill);
        }
    }

    // The visible topo: one highlighted passage line per route (thin screen-space thread seated ON the DEM)
    // and a name+grade label anchored ON its OWN line (staggered up the wall so a name maps to its line).
    // Each route gets a DISTINCT colour (golden-angle hue spread) so 16+ routes don't share 5 palette colours.
    private void BuildClimbRouteOverlay(
        IReadOnlyList<MapaTur.Application.Terrain.WorldClimbingRoute> world, MapaTur.Application.Terrain.TerrainMesh3D frame)
    {
        climbRouteRibbons.Clear();
        climbRouteLabels.Clear();
        float exaggeration = frame.VerticalExaggeration;
        int index = 0;
        foreach (MapaTur.Application.Terrain.WorldClimbingRoute route in world)
        {
            var line = new List<Vector3>();
            for (int segment = 0; segment + 1 < route.PathXY.Count; segment++)
            {
                Vector2 a = route.PathXY[segment];
                Vector2 b = route.PathXY[segment + 1];
                int steps = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b) / RouteLineSampleStepMeters));
                for (int s = segment == 0 ? 0 : 1; s <= steps; s++)
                {
                    Vector2 xy = Vector2.Lerp(a, b, s / (float)steps);
                    if (SampleWalkGround(xy) is { } ground)
                    {
                        line.Add(new Vector3(xy.X, xy.Y, (ground + RouteLineLiftMeters) * exaggeration));
                    }
                }
            }

            if (line.Count < 2)
            {
                index++;
                continue;
            }

            (Vector3 lineColor, SKColor labelColor) = DistinctRouteColor(index);
            climbRouteRibbons.Add(new(line, lineColor, RouteLineHalfPixels, RopeTwist: false, ScreenSpace: true));

            // Label sits ON the line, at a staggered height (so neighbouring routes' names don't overlap),
            // lifted just a touch — you can trace name → its own line instead of a name floating up top.
            float frac = 0.30f + (0.11f * ((index * 3) % 5));
            int li = Math.Clamp((int)(line.Count * frac), 0, line.Count - 1);
            Vector3 labelAnchor = line[li] + new Vector3(0f, 0f, 0.5f * exaggeration);
            climbRouteLabels.Add(($"{route.Name} ({route.Grade})", labelAnchor, labelColor));
            index++;
        }

        climbRouteOverlayExaggeration = exaggeration;
    }

    // A distinct vivid colour per route from a golden-angle hue spread — maximally separable so adjacent
    // routes never share a colour. Returns the line RGB (0..1) and the matching label SKColor.
    private static (Vector3 Line, SKColor Label) DistinctRouteColor(int index)
    {
        float hue = (index * 137.508f) % 360f;
        (float r, float g, float b) = HsvToRgb(hue, 0.72f, 1.0f);
        return (new Vector3(r, g, b), new SKColor((byte)(r * 255), (byte)(g * 255), (byte)(b * 255)));
    }

    private static (float R, float G, float B) HsvToRgb(float hueDegrees, float s, float v)
    {
        float c = v * s;
        float x = c * (1f - MathF.Abs(((hueDegrees / 60f) % 2f) - 1f));
        float m = v - c;
        (float r, float g, float b) = (hueDegrees / 60f) switch
        {
            < 1f => (c, x, 0f),
            < 2f => (x, c, 0f),
            < 3f => (0f, c, x),
            < 4f => (0f, x, c),
            < 5f => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return (r + m, g + m, b + m);
    }

    // Name + grade labels over the topo lines (Skia overlay, same projection as the hold outlines).
    // Only near the massif — beyond RouteLabelMaxDistanceMeters the lines alone mark the routes.
    private void DrawClimbingRouteLabels(SKCanvas canvas, int width, int height)
    {
        if (!ShowClimbingRoutes || climbRouteLabels.Count == 0 || width <= 0 || height <= 0)
        {
            return;
        }

        Matrix4x4 viewProjection = Camera.BuildViewProjection((float)width / Math.Max(1, height));
        using var textHalo = new SKPaint
        { IsAntialias = true, Color = new SKColor(0, 0, 0, 210), Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f };
        using var textFill = new SKPaint { IsAntialias = true };
        using var font = new SKFont { Size = 14f, Embolden = true };

        foreach ((string text, Vector3 world, SKColor color) in climbRouteLabels)
        {
            if (Vector3.Distance(world, Camera.Position) > RouteLabelMaxDistanceMeters)
            {
                continue;
            }

            if (Camera.ProjectToScreen(world, viewProjection, width, height) is not { } screen)
            {
                continue;
            }

            textFill.Color = color;
            canvas.DrawText(text, screen.X, screen.Y, SKTextAlign.Center, font, textHalo);
            canvas.DrawText(text, screen.X, screen.Y, SKTextAlign.Center, font, textFill);
        }
    }

    // Grip-stamina HUD: a small colour-coded bar (green → amber → red, shrinking as grip drains) shown while
    // climbing / hanging / roped, or while grip is recovering. Bar-only to stay independent of the SkiaSharp text API.
    private void DrawClimbStaminaHud(SKCanvas canvas, int width, int height)
    {
        if (!walkActive || walker is not { } w)
        {
            return;
        }

        float frac = w.GripStaminaFraction;
        if (!(w.IsClimbing || w.IsHanging || w.IsRoped || frac < 0.999f))
        {
            return; // full grip and not on a wall → nothing to show
        }

        float barW = MathF.Min(320f, width * 0.28f);
        const float barH = 16f;
        float x = (width - barW) * 0.5f;
        float y = height - 64f;

        using (var bg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0, 0, 0, 150) })
        {
            canvas.DrawRoundRect(x - 3f, y - 3f, barW + 6f, barH + 6f, 6f, 6f, bg);
        }

        SKColor col = frac > 0.5f ? new SKColor(70, 200, 90)
            : frac > 0.25f ? new SKColor(235, 185, 45)
            : new SKColor(225, 70, 55);
        using (var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = col })
        {
            canvas.DrawRoundRect(x, y, MathF.Max(0f, barW * frac), barH, 5f, 5f, fill);
        }

        using var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(255, 255, 255, 130) };
        canvas.DrawRoundRect(x, y, barW, barH, 5f, 5f, border);
    }

    // ── AI DRAGON FLOCK ─────────────────────────────────────────────────────────────────────────────────────
    // The flock is UPDATED from inside the render (UpdateAiFlock, dt from aiFlockClock) and REPAINTED by the
    // existing animationTimer / dragon-timer — it deliberately owns NO timer of its own. A third invalidation
    // loop stacking on the dragon+atmosphere timers crashed the WinUI compositor (APPCRASH in Microsoft.UI.Xaml,
    // 0xc0000005, only ever while flying with the flock on).
    private void OnShowAiDragonsChanged(bool on)
    {
        if (on)
        {
            aiFlockClock.Restart();
            aiFlockLastSeconds = 0.0;
            if (aiFlock.Count == 0 && !aiFlockLoading)
            {
                LoadAiFlockAsync();
            }
        }
        else
        {
            aiFlockInstances.Clear();
        }

        Canvas.InvalidateSurface();
    }

    // Loads the THREE flock species once (animated dragon, prowler, static red flyer), off the UI thread. The
    // animated one plays its baked "flying" clip; the prowler flaps procedurally; the red one is a rigid glider.
    // Spawn is deferred to the UI thread once the terrain frame is ready.
    private async void LoadAiFlockAsync()
    {
        if (aiFlockLoading || aiFlockModelsReady)
        {
            return;
        }

        aiFlockLoading = true;
        try
        {
            MapaTur.Application.Terrain.SkinnedModel animated = await LoadGlbAsync("dragon-animated.glb").ConfigureAwait(false);
            MapaTur.Application.Terrain.SkinnedModel prowler = await LoadGlbAsync("prowler-dragon.glb").ConfigureAwait(false);
            MapaTur.Application.Terrain.SkinnedModel red = await LoadGlbAsync("red-flying-dragon.glb").ConfigureAwait(false);

            int flying = -1;
            for (int i = 0; i < animated.Animations.Count; i++)
            {
                if (string.Equals(animated.Animations[i].Name, "flying", StringComparison.OrdinalIgnoreCase))
                {
                    flying = i;
                }
            }

            if (flying < 0 && animated.Animations.Count > 0)
            {
                flying = 0;
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (!ShowAiDragons)
                {
                    return; // toggled off mid-load
                }

                aiModelAnimated = animated;
                aiClipAnimated = flying;
                aiModelProwler = prowler;
                aiModelRed = red;
                aiFlockModelsReady = true;
                Serilog.Log.Information(
                    "[AiDragon] species loaded — animated(ext={A:F2},clip={C},tex={AT}) prowler(ext={P:F2},tex={PT}) red(ext={R:F2},tex={RT})",
                    animated.LocalExtent, flying, animated.BaseColorImageBytes?.Length ?? 0,
                    prowler.LocalExtent, prowler.BaseColorImageBytes?.Length ?? 0,
                    red.LocalExtent, red.BaseColorImageBytes?.Length ?? 0);
                TrySpawnAiFlock(); // spawns now if the terrain frame is ready, else on the next tick
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "[AiDragon] flock load failed");
        }
        finally
        {
            aiFlockLoading = false;
        }
    }

    private static async Task<MapaTur.Application.Terrain.SkinnedModel> LoadGlbAsync(string asset)
    {
        await using Stream s = await Microsoft.Maui.Storage.FileSystem.OpenAppPackageFileAsync(asset).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms).ConfigureAwait(false);
        return MapaTur.Application.Terrain.SkinnedModel.LoadGlb(ms.ToArray());
    }

    // Places the 3-species flock over the nearest peaks. Deferred until BOTH the models are loaded and the
    // terrain frame exists (peaks → world XY + ground sampling need it).
    private void TrySpawnAiFlock()
    {
        if (!aiFlockModelsReady || aiModelAnimated is null || aiModelProwler is null || aiModelRed is null
            || WorldFrame is not { } frame)
        {
            return;
        }

        aiFlock.Clear();
        List<System.Numerics.Vector2> centers = PickFlockCenters(frame, AiFlockCount);
        for (int i = 0; i < AiFlockCount; i++)
        {
            System.Numerics.Vector2 home = centers[i];
            float ground = SampleContactGround(home) ?? 0f;
            int dir = (i % 2 == 0) ? 1 : -1;

            var start = home + new System.Numerics.Vector2(AiFlockOrbitRadiusMeters, 0f);
            float heading = dir > 0 ? MathF.PI / 2f : -MathF.PI / 2f;
            // AI dragons orbit far from the camera — their per-tick ground probes must stay contact-grade.
            var flight = new MapaTur.Application.Terrain.DragonFlight(start, heading, SampleContactGround);
            var pilot = new MapaTur.Application.Terrain.DragonAiPilot
            {
                CircleCenter = home,
                CircleRadiusMeters = AiFlockOrbitRadiusMeters,
                TargetAltitudeMeters = ground + AiFlockCruiseHeightMeters,
                Direction = dir,
            };

            // One member per species (0 = animated, 1 = prowler, 2 = red glider), each colour-tinted.
            AiFlockKind kind = i switch { 0 => AiFlockKind.Animated, 1 => AiFlockKind.Prowler, _ => AiFlockKind.Static };
            MapaTur.Application.Terrain.SkinnedModel model = kind switch
            {
                AiFlockKind.Animated => aiModelAnimated,
                AiFlockKind.Prowler => aiModelProwler,
                _ => aiModelRed,
            };
            MapaTur.Application.Terrain.ProwlerDragonRig? rig = null;
            if (kind == AiFlockKind.Prowler)
            {
                rig = new MapaTur.Application.Terrain.ProwlerDragonRig(model);
                if (rig.MissingBones.Count > 0)
                {
                    Serilog.Log.Warning("[ProwlerRig] missing bones (won't animate): {Missing}", string.Join(", ", rig.MissingBones));
                }
            }
            else if (kind == AiFlockKind.Static)
            {
                model.Skin(); // no per-frame posing — populate PosedPositions from bind once (else it draws collapsed)
            }

            aiFlock.Add(new AiFlockDragon
            {
                Flight = flight,
                Pilot = pilot,
                Model = model,
                Kind = kind,
                AnimClip = kind == AiFlockKind.Animated ? aiClipAnimated : -1,
                Rig = rig,
                TextureBytes = model.BaseColorImageBytes,
                Tint = AiFlockTints[i % AiFlockTints.Length],
                HomePeakXY = home,
                AnimTime = i * 1.7f,
                FlapPhase = i * 1.3f,
                TailPhase = i * 0.7f,
            });
        }

        Serilog.Log.Information("[AiDragon] flock spawned: {N} dragons (animated/prowler/red)", aiFlock.Count);
        Canvas.InvalidateSurface();
    }

    // The nearest peaks to the camera target become orbit centres; if there aren't enough, pad with a ring
    // around the target so the flock still appears on featureless terrain.
    private List<System.Numerics.Vector2> PickFlockCenters(TerrainMesh3D frame, int count)
    {
        var target = new System.Numerics.Vector2(Camera.Target.X, Camera.Target.Y);
        var centers = new List<System.Numerics.Vector2>();
        if (Peaks is { Count: > 0 } peaks)
        {
            centers = peaks
                .Select(p =>
                {
                    Vector3 w = frame.GeoToWorld(p.Location, 0f);
                    return new System.Numerics.Vector2(w.X, w.Y);
                })
                .OrderBy(xy => System.Numerics.Vector2.DistanceSquared(xy, target))
                .Take(count)
                .ToList();
        }

        for (int i = centers.Count; i < count; i++)
        {
            float ang = i * (MathF.PI * 2f / count);
            centers.Add(target + (new System.Numerics.Vector2(MathF.Cos(ang), MathF.Sin(ang)) * 1200f));
        }

        return centers;
    }

    private double aiFlockLogAccum;

    // Advances + poses the flock. Called ONCE per rendered frame from the paint path (no timer of its own — see
    // OnShowAiDragonsChanged); dt comes from aiFlockClock so it's correct at any repaint rate.
    private void UpdateAiFlock()
    {
        if (!ShowAiDragons || WorldFrame is not { } frame)
        {
            aiFlockInstances.Clear();
            return;
        }

        if (aiFlock.Count == 0)
        {
            TrySpawnAiFlock(); // waiting for the terrain frame / models
            return;
        }

        double now = aiFlockClock.Elapsed.TotalSeconds;
        var dt = (float)Math.Clamp(now - aiFlockLastSeconds, 0.0, 0.1);
        aiFlockLastSeconds = now;
        float exagg = frame.VerticalExaggeration;

        // React to the player: when the ridden dragon (F7) flies near a member's home peak, its orbit centre
        // drifts toward the player, so the flock swings over to circle him.
        System.Numerics.Vector2? player = dragonActive && dragon is { } pd ? pd.PositionXY : null;

        aiFlockLogAccum += dt;
        bool logNow = aiFlockLogAccum >= 1.0;
        if (logNow)
        {
            aiFlockLogAccum = 0.0;
        }

        aiFlockInstances.Clear();
        foreach (AiFlockDragon m in aiFlock)
        {
            if (!m.Alive)
            {
                continue; // shot down — no update, no render
            }

            System.Numerics.Vector2 center = m.HomePeakXY;
            if (player is { } pp)
            {
                float distToHome = System.Numerics.Vector2.Distance(pp, m.HomePeakXY);
                if (distToHome < AiFlockReactRadiusMeters)
                {
                    float t = 1f - (distToHome / AiFlockReactRadiusMeters); // nearer → stronger pull
                    center = System.Numerics.Vector2.Lerp(m.HomePeakXY, pp, t * 0.85f);
                }
            }

            m.Pilot.CircleCenter = center;
            (float yaw, float pitch, float throttle) = m.Pilot.Compute(
                m.Flight.PositionXY, m.Flight.HeadingRadians, m.Flight.ElevationMeters, m.Flight.SpeedMetersPerSecond);
            m.Flight.Step(dt, yaw, pitch, throttle);

            // Pose this member by species: animated plays its baked "flying" clip; prowler flaps procedurally;
            // the red flyer is a rigid glider (bind pose). Effort follows pitch (faster beat on a climb).
            float pace = Math.Clamp(1f + (0.5f * m.Flight.PitchRadians), 0.6f, 1.7f);
            switch (m.Kind)
            {
                case AiFlockKind.Animated when m.AnimClip >= 0 && m.AnimClip < m.Model.Animations.Count:
                    m.AnimTime += dt * pace;
                    float dur = m.Model.Animations[m.AnimClip].Duration;
                    m.Model.SetFrame(m.AnimClip, dur > 0.01f ? m.AnimTime % dur : 0f);
                    m.Model.Skin();
                    break;
                case AiFlockKind.Prowler when m.Rig is { } rig:
                    m.FlapPhase += dt * 3.0f * pace;
                    m.TailPhase += dt * 2.4f;
                    rig.Pose(m.FlapPhase, m.TailPhase);
                    break;
                default:
                    break; // Static: bind pose, no per-frame skinning
            }

            Matrix4x4 world = BuildAiDragonMatrix(m.Model, m.Flight, exagg, out Matrix4x4 normal);
            aiFlockInstances.Add(new(m.Model, world, normal, m.Tint, m.TextureBytes));

            if (logNow)
            {
                float distToCenter = System.Numerics.Vector2.Distance(m.Flight.PositionXY, m.Pilot.CircleCenter);
                Serilog.Log.Information(
                    "[AiFlock] {Kind} pos=({X:F0},{Y:F0}) elev={E:F0} target={T:F0} distCtr={D:F0} head={H:F2} speed={S:F0}",
                    m.Kind, m.Flight.PositionXY.X, m.Flight.PositionXY.Y, m.Flight.ElevationMeters,
                    m.Pilot.TargetAltitudeMeters, distToCenter, m.Flight.HeadingRadians, m.Flight.SpeedMetersPerSecond);
            }
        }
    }

    // Model→world matrix for an AI dragon, same convention as the ridden dragon (glTF Y-up→Z-up remap, yaw
    // offset, pitch/roll signs), pivoting on the bind-bounds centre (flight framing; the flock never perches).
    private Matrix4x4 BuildAiDragonMatrix(
        MapaTur.Application.Terrain.SkinnedModel model, MapaTur.Application.Terrain.DragonFlight d, float exagg, out Matrix4x4 normal)
    {
        float scale = AiFlockModelSizeMeters / MathF.Max(0.001f, model.LocalExtent);
        Vector3 boundsCenter = (model.BoundsMin + model.BoundsMax) * 0.5f;
        Matrix4x4 center = Matrix4x4.CreateTranslation(-boundsCenter);
        Matrix4x4 remap = Matrix4x4.CreateRotationX(MathF.PI / 2f);
        Matrix4x4 bank = Matrix4x4.CreateRotationY(DragonRollSign * d.RollRadians);
        Matrix4x4 climb = Matrix4x4.CreateRotationX(DragonPitchSign * d.PitchRadians);
        Matrix4x4 yawRot = Matrix4x4.CreateRotationZ(d.HeadingRadians + DragonYawOffset);
        Matrix4x4 rot = remap * bank * climb * yawRot;
        normal = rot;
        var worldPos = new Vector3(d.PositionXY.X, d.PositionXY.Y, d.ElevationMeters * exagg);
        return center * Matrix4x4.CreateScale(scale) * rot * Matrix4x4.CreateTranslation(worldPos);
    }

    // Leaves dragon flight and frames its final spot from an orbit vantage.
    private void ExitDragonFlight()
    {
        if (!dragonActive)
        {
            return;
        }

        dragonActive = false;
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Interactive;
        dragonTimer?.Stop();
        dragonW = dragonS = dragonA = dragonD = false;
        dragonPitchUp = dragonPitchDown = dragonYawLeft = dragonYawRight = false;
        dragonRmbHeld = false;
        dragonCamPitch = 0f;
        dragonFireHeld = false;
        dragonAudio.Silence(); // HARD-stop every looped layer (fire + wind/wing/ground) + ringing one-shots — a
                               // single SetFlightBed(0,0,0) only fades 15%/call, so the loops used to play on
        dragonFireHoldSeconds = 0f;
        dragonFireOrbitAngle = 0f;
        dragonPerchGroundElev = null;
        dragonFireballs.Clear();
        dragonFireSprites.Clear();
#if WINDOWS
        glRenderer?.SetFireLights(0, fireLightPosOut, fireLightColOut, fireLightInvR2Out); // no glow past the flight
#endif

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

    // Rolling 5 s frame-time window (see the call site in OnDragonTick). Sorting ~600 floats every 5 s is noise.
    private readonly List<float> dragonPerfDts = new(1024);
    private double dragonPerfWindowStart;
    private int dragonPerfGc0;
    private int dragonPerfGc1;
    private int dragonPerfGc2;

    // Paint-stage breakdown for the same window (prep = projections before GL, gl = whole TryRenderTerrainGl,
    // ovl = Skia overlays after it; renderMax = the glRenderer.Render call inside gl). Sum→avg + max.
    private double dragonPaintPrepSum;
    private double dragonPaintGlSum;
    private double dragonPaintOverlaySum;
    private double dragonPaintPrepMax;
    private double dragonPaintGlMax;
    private double dragonPaintOverlayMax;
    private double dragonPaintRenderMax;
    private int dragonPaintCount;

    private static double PerfMs(long t0, long t1)
        => (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private void RecordDragonPaint(double prepMs, double glMs, double overlayMs)
    {
        dragonPaintPrepSum += prepMs;
        dragonPaintGlSum += glMs;
        dragonPaintOverlaySum += overlayMs;
        dragonPaintPrepMax = Math.Max(dragonPaintPrepMax, prepMs);
        dragonPaintGlMax = Math.Max(dragonPaintGlMax, glMs);
        dragonPaintOverlayMax = Math.Max(dragonPaintOverlayMax, overlayMs);
        dragonPaintCount++;
    }

    private void TrackDragonFrameTime(float dt)
    {
        if (dragonPerfDts.Count == 0)
        {
            dragonPerfWindowStart = dragonClock.Elapsed.TotalSeconds;
            dragonPerfGc0 = GC.CollectionCount(0);
            dragonPerfGc1 = GC.CollectionCount(1);
            dragonPerfGc2 = GC.CollectionCount(2);
        }

        dragonPerfDts.Add(dt);
        double windowSeconds = dragonClock.Elapsed.TotalSeconds - dragonPerfWindowStart;
        if (windowSeconds < 5.0 || dragonPerfDts.Count < 10)
        {
            return;
        }

        dragonPerfDts.Sort();
        int n = dragonPerfDts.Count;
        float worst = dragonPerfDts[n - 1] * 1000f;
        float p95 = dragonPerfDts[(int)(n * 0.95f)] * 1000f;
        float avg = (float)(windowSeconds / n) * 1000f;
        int spikes = 0;
        for (int i = n - 1; i >= 0 && dragonPerfDts[i] > 0.025f; i--)
        {
            spikes++;
        }

        int paints = Math.Max(1, dragonPaintCount);
        Serilog.Log.Information(
            "[DragonPerf] avg={Avg:F1}ms ({Fps:F0} fps) p95={P95:F1}ms worst={Worst:F0}ms spikes>25ms={Spikes} gc={G0}/{G1}/{G2} ticks={N} "
            + "| paint prep={PrA:F1}/{PrM:F0} gl={GlA:F1}/{GlM:F0} ovl={OvA:F1}/{OvM:F0} renderMax={RndM:F0} (avg/max ms, {P} paints)",
            avg, 1000f / Math.Max(0.1f, avg), p95, worst, spikes,
            GC.CollectionCount(0) - dragonPerfGc0,
            GC.CollectionCount(1) - dragonPerfGc1,
            GC.CollectionCount(2) - dragonPerfGc2, n,
            dragonPaintPrepSum / paints, dragonPaintPrepMax,
            dragonPaintGlSum / paints, dragonPaintGlMax,
            dragonPaintOverlaySum / paints, dragonPaintOverlayMax,
            dragonPaintRenderMax, dragonPaintCount);
        dragonPerfDts.Clear();
        dragonPaintPrepSum = dragonPaintGlSum = dragonPaintOverlaySum = 0;
        dragonPaintPrepMax = dragonPaintGlMax = dragonPaintOverlayMax = dragonPaintRenderMax = 0;
        dragonPaintCount = 0;
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

        // Frame-time telemetry: under the vsync loop this tick runs once per composed frame, so dt IS the
        // effective frame time. Every ~5 s one line separates the failure modes: high avg = uniformly too
        // slow, spikes with gc2>0 = collector pauses, spikes with gc2=0 = streaming/upload hitches.
        TrackDragonFrameTime(dt);

        // Steer: right-drag/A,D/←→ are the ROLL command — the physics banks the dragon and flies the turn
        // THROUGH the bank (tan(roll)/speed; released = self-levels). ↑↓ pitch; W/S throttle. The ←→ PRESS
        // additionally fires a turn-entry wing stroke (below).
        // Pitch is inverted from the naive sign: ↑ climbs (dragon noses up, camera drops behind-below), ↓ dives.
        float pitch = Math.Clamp(
            (dragonMouseDy * DragonMousePitchPerPixel)
            - (dragonPitchUp ? 1f : 0f) + (dragonPitchDown ? 1f : 0f),
            -1f, 1f);
        float throttle = (dragonW ? 1f : 0f) - (dragonS ? 1f : 0f);

        // ── TURN-ENTRY STROKE (commitment gate): the moment a turn is COMMITTED, one hard beat of the outer
        // wing shoves the body in (TurnImpulse: heading jerk + lateral push). It used to fire on the arrow's
        // rising EDGE — a 100 ms correction tap got the whole impulse ("szarpanie przy małych skrętach").
        // Now it arms only when the SHAPED command (attack-ramped inside DragonFlight) crosses the gate —
        // i.e. the key was genuinely held (~0.2 s) or the mouse swerved hard — and re-arms after release.
        const float TwoPi = 2f * MathF.PI;
        const float DownStart = MathF.PI / 2f;
        const float DownEnd = 3f * MathF.PI / 2f;
        float cyclePos = ((dragonFlapPhase % TwoPi) + TwoPi) % TwoPi;
        float cmdNow = d.YawCommand;
        bool strokeCommitted = MathF.Abs(cmdNow) >= DragonStrokeCommandGate
            && MathF.Abs(dragonPrevYawCommand) < DragonStrokeCommandGate;
        dragonPrevYawCommand = cmdNow;
        bool animatedStroke = dragonRig is null && dragonModel3D is not null && dragonFlyingAnimIndex >= 0;

        // ANIMATED variant: timed stroke — a short "finish the current motion with both wings" beat, then the
        // outer wing's single big flap (posed in the render block) with the shove fired at its slam.
        if (animatedStroke)
        {
            if (dragonAnimStrokeTimer < 0f && strokeCommitted
                && d.Phase == MapaTur.Application.Terrain.DragonFlightPhase.Flying)
            {
                dragonAnimStrokeTimer = 0f;
                dragonAnimStrokeDir = MathF.Sign(cmdNow);
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

        if (!animatedStroke && dragonTurnStrokeDir == 0f && strokeCommitted
            && d.Phase == MapaTur.Application.Terrain.DragonFlightPhase.Flying)
        {
            dragonTurnStrokeDir = MathF.Sign(cmdNow); // command sign carries the old arrow mapping (← = +yaw = left)
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

        dragonLastFlapActivity = flapActivity; // the flight-bed audio (wing flutter) rides this next tick
        dragonFlapPhase += dt * 3.2f * flapActivity;
        // One whoosh per wing-beat, fired at the down-stroke's fastest sweep (sin crossing zero downward =
        // the wrapped cycle passing π). Compared on the wrapped cycle so the ever-growing phase can't
        // overflow the test; a folded/perched wing barely advances the phase, so it stays silent for free.
        // CLASSIC variant only — the animated variant's wings play the baked clip and IGNORE this phase, so
        // its whoosh is cued off the clip loop instead (see the SetFrame site below).
        float flapCycleNow = ((dragonFlapPhase % TwoPi) + TwoPi) % TwoPi;
        if (dragonFlyingAnimIndex < 0 && dragonFlapCyclePrev < MathF.PI && flapCycleNow >= MathF.PI)
        {
            dragonAudio.PlayFlap(flapActivity);
        }

        dragonFlapCyclePrev = flapCycleNow;
        // Occasional soar roar: a deep cry every ~9–17 s of free flight (deterministic stride).
        if (phase == MapaTur.Application.Terrain.DragonFlightPhase.Flying)
        {
            dragonNextRoarSeconds -= dt;
            if (dragonNextRoarSeconds <= 0f)
            {
                dragonAudio.PlayRoar(0.4f);
                dragonNextRoarSeconds = 9f + (8f * Frac(++dragonRoarCounter * 0.61803399f));
            }
        }
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
                float clipTime = duration > 0.01f ? dragonAnimTime % duration : 0f;
                model3D.SetFrame(clip, clipTime);

                // Flap whoosh SYNCED to the posed wings (the clip ignores dragonFlapPhase, and the loop-wrap
                // point is wherever the clip happens to start — audibly off-beat). Track the wing TIP bone's
                // vertical velocity in MODEL space (isolated from the body's bank/pitch): a clear upstroke
                // arms, the moment the downstroke starts fires — the whoosh's ~0.2 s ramp then peaks right in
                // the middle of the sweep. Thresholds scale with the model extent (variants differ 100×).
                if (!dragonWingBoneSearched)
                {
                    dragonWingBoneSearched = true;
                    float best = 0f;
                    foreach (string name in model3D.BoneNames)
                    {
                        if (name.Contains("wing", StringComparison.OrdinalIgnoreCase)
                            && model3D.GetBonePosedPosition(name) is { } p)
                        {
                            float reach = MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y));
                            if (reach > best)
                            {
                                best = reach;
                                dragonWingBoneName = name;
                            }
                        }
                    }

                    Serilog.Log.Information(
                        "[Dragon] flap-sound wing bone: {Bone} (reach={R:F2})",
                        dragonWingBoneName ?? "(none — clip-wrap fallback)", best);
                }

                if (!idlePhase && dragonWingBoneName is { } wingBone && dt > 1e-4f
                    && model3D.GetBonePosedPosition(wingBone) is { } wingPos)
                {
                    float wingVel = (wingPos.Z - dragonWingZPrev) / dt; // model units/s
                    dragonWingZPrev = wingPos.Z;
                    float velThreshold = model3D.LocalExtent * 0.3f;
                    if (wingVel > velThreshold)
                    {
                        dragonWingArmed = true; // clear upstroke → ready
                    }
                    else if (dragonWingArmed && wingVel < -velThreshold)
                    {
                        dragonWingArmed = false; // downstroke begins → whoosh
                        dragonAudio.PlayFlap(0.4f + (0.6f * Math.Clamp(pace - 0.55f, 0f, 1f)));
                    }
                }
                else if (!idlePhase && dragonWingBoneName is null && clipTime < dragonClipTimePrev)
                {
                    // No wing bone in this rig → coarse fallback: one whoosh per clip loop.
                    dragonAudio.PlayFlap(0.4f + (0.6f * Math.Clamp(pace - 0.55f, 0f, 1f)));
                }

                dragonClipTimePrev = clipTime;

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
            Matrix4x4 remap = Matrix4x4.CreateRotationX(MathF.PI / 2f); // glTF Y-up → world Z-up
            Matrix4x4 bank = Matrix4x4.CreateRotationY(DragonRollSign * d.RollRadians);
            Matrix4x4 climb = Matrix4x4.CreateRotationX(DragonPitchSign * d.PitchRadians);
            Matrix4x4 yawRot = Matrix4x4.CreateRotationZ(d.HeadingRadians + DragonYawOffset);
            Matrix4x4 rot = remap * bank * climb * yawRot;
            dragonNormalMatrix = rot;
            float drop = dragonLoadedVariant == 1 ? DragonAnimatedDropMeters : DragonDropMeters;

            // Posed foot anchor (model-local): horizontal = centroid of the foot bones, vertical = the LOWEST
            // foot bone (the sole). Measured: pivoting on the bind-bounds centre planted the feet ~4.7 m off in
            // XY (the tail/wings bias the AABB sideways; the baked clip adds root motion), while Z was already
            // fine. On the ground we pivot on THIS point instead, so the soles sit on the target rock.
            Vector3 footSum = Vector3.Zero;
            int footN = 0;
            float feetY = float.PositiveInfinity;
            foreach (string bone in dragonLoadedVariant == 1 ? DragonAnimatedFootBones : DragonClassicFootBones)
            {
                if (model3D.GetBonePosedPosition(bone) is { } footPos)
                {
                    footSum += footPos;
                    footN++;
                    if (footPos.Y < feetY)
                    {
                        feetY = footPos.Y;
                    }
                }
            }

            Vector3 footCentroidLocal = footN > 0 ? footSum / footN : boundsCenter;
            if (!float.IsFinite(feetY))
            {
                feetY = model3D.BoundsMin.Y;
            }

            var footPivotLocal = new Vector3(footCentroidLocal.X, feetY, footCentroidLocal.Z);

            // Flight: body hangs at the flight point, pivot on the bind centre (camera frames the body — do NOT
            // disturb). Perched: pivot on the foot anchor and drop the sole onto the DRAWN rock
            // (SampleRenderedMeshElevation — the only height that matches what's on screen). Blend by the legs so
            // the touchdown slides smoothly and free flight / the chase camera stay exactly as before.
            float flightZ = (d.ElevationMeters - drop + flapLift) * exaggeration3D;
            Vector3 pivot = boundsCenter;
            float worldZ = flightZ;
            float rockZWorld = flightZ;
            if (dragonLegsDown > 0.001f)
            {
                rockZWorld = (SampleRenderedMeshElevation(d.PositionXY.X, d.PositionXY.Y) ?? d.ElevationMeters)
                    * exaggeration3D;
                pivot = Vector3.Lerp(boundsCenter, footPivotLocal, dragonLegsDown);
                worldZ = flightZ + ((rockZWorld - flightZ) * dragonLegsDown);
            }

            Matrix4x4 center = Matrix4x4.CreateTranslation(-pivot);
            var worldPos = new Vector3(d.PositionXY.X, d.PositionXY.Y, worldZ);
            dragonWorldMatrix = center * Matrix4x4.CreateScale(scale) * rot * Matrix4x4.CreateTranslation(worldPos);

            // Fire muzzle: the POSED head bone's world position, so fire streams from the mouth as the head nods/
            // scans (not a fixed body-relative point). Null → StepDragonFire falls back to the old offset.
            dragonMouthWorld = null;
            foreach (string bone in dragonLoadedVariant == 1 ? DragonAnimatedMouthBones : DragonClassicMouthBones)
            {
                if (model3D.GetBonePosedPosition(bone) is { } bonePos)
                {
                    dragonMouthWorld = Vector3.Transform(bonePos, dragonWorldMatrix);
                    break;
                }
            }

            // ── FOOT-PLACEMENT PROBE ── markers drawn AFTER the final transform (same matrix the GPU uses:
            // row-vector .NET uploaded un-transposed == GLSL uModel*vec4), so a CPU dot lands exactly where the
            // GPU draws that model point. After the foot-pivot fix, BLUE (foot anchor) should sit on YELLOW
            // (target rock). Only while landing/perched, so free flight isn't cluttered.
            dragonDebugMarkers.Clear();
            if (ShowDebugMarkers
                && phase is MapaTur.Application.Terrain.DragonFlightPhase.Flare
                or MapaTur.Application.Terrain.DragonFlightPhase.Touchdown
                or MapaTur.Application.Terrain.DragonFlightPhase.Perched)
            {
                Vector3 mOrigin = Vector3.Transform(Vector3.Zero, dragonWorldMatrix);
                Vector3 mCenter = Vector3.Transform(boundsCenter, dragonWorldMatrix);
                Vector3 mFeet = Vector3.Transform(footCentroidLocal, dragonWorldMatrix);
                var mTarget = new Vector3(d.PositionXY.X, d.PositionXY.Y, rockZWorld);

                const float markerRadius = 1.1f;
                dragonDebugMarkers.Add(new(mOrigin, new Vector3(1f, 0.15f, 0.15f), markerRadius));  // RED    model origin
                dragonDebugMarkers.Add(new(mCenter, new Vector3(0.2f, 1f, 0.2f), markerRadius));    // GREEN  bind centre (= worldPos)
                dragonDebugMarkers.Add(new(mFeet, new Vector3(0.3f, 0.55f, 1f), markerRadius));     // BLUE   posed foot anchor (drawn feet)
                dragonDebugMarkers.Add(new(mTarget, new Vector3(1f, 0.9f, 0.15f), markerRadius));   // YELLOW target rendered rock

                dragonSeatLogAccum += dt;
                if (dragonSeatLogAccum >= 1f)
                {
                    dragonSeatLogAccum = 0f;
                    float feetTargetDxy = MathF.Sqrt(
                        ((mFeet.X - mTarget.X) * (mFeet.X - mTarget.X)) + ((mFeet.Y - mTarget.Y) * (mFeet.Y - mTarget.Y)));
                    Serilog.Log.Information(
                        "[DragonSeat] phase={Ph} feetN={N} exagg={Ex:F2} origin=({OX:F1},{OY:F1},{OZ:F1}) center=({CX:F1},{CY:F1},{CZ:F1}) feet=({FX:F1},{FY:F1},{FZ:F1}) target=({TX:F1},{TY:F1},{TZ:F1}) feet↔target dXY={DXY:F2} dZ={DZ:F2}",
                        phase, footN, exaggeration3D, mOrigin.X, mOrigin.Y, mOrigin.Z, mCenter.X, mCenter.Y, mCenter.Z,
                        mFeet.X, mFeet.Y, mFeet.Z, mTarget.X, mTarget.Y, mTarget.Z, feetTargetDxy, mFeet.Z - mTarget.Z);
                }
            }
        }
        else
        {
            dragonDebugMarkers.Clear();
        }

        // ── Fire breath ── spawn while F is held (streamed on a cooldown), fly the balls forward, burst on
        // terrain, and build this frame's render sprites (Z exaggerated only here).
#if WINDOWS // fire-breath sim is desktop-only (the whole F7 mode never activates on mobile)
        StepDragonFire(d, dt, frame.VerticalExaggeration);

        // Audio 2.0 flight bed: wind ∝ speed (boosted in a bank), wing flutter ∝ flap activity, ground rush ∝
        // low-pass proximity × speed. Zeroed outside free flight, so the perch and the landing glide go quiet.
        if (d.Phase == MapaTur.Application.Terrain.DragonFlightPhase.Flying)
        {
            float bedSpeed = d.SpeedMetersPerSecond;
            float bedWind = MathF.Pow(Math.Clamp((bedSpeed - 22f) / 100f, 0f, 1f), 1.4f)
                * (1f + (0.35f * MathF.Abs(MathF.Sin(d.RollRadians))));
            float bedWing = Math.Clamp(dragonLastFlapActivity / 1.4f, 0f, 1f);
            float bedAgl = SampleContactGround(d.PositionXY) is float bedGround ? d.ElevationMeters - bedGround : 999f;
            float bedRush = Math.Clamp(1f - ((bedAgl - 24f) / 36f), 0f, 1f) * Math.Clamp(bedSpeed / 70f, 0f, 1f);
            dragonAudio.SetFlightBed(bedWind, bedWing, bedRush);
        }
        else
        {
            dragonAudio.SetFlightBed(0f, 0f, 0f);
        }
#endif

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

        // Cinematic breath orbit: after DragonFireOrbitDelaySeconds of held fire the eye starts a slow
        // sideways sweep around the dragon; releasing eases it back behind the tail the SHORT way (wrap
        // first — a long hold can wind up full laps and must not unwind them all).
        if (dragonFireHeld)
        {
            dragonFireHoldSeconds += dt;
            float ramp = Math.Clamp(
                (dragonFireHoldSeconds - DragonFireOrbitDelaySeconds) / DragonFireOrbitRampSeconds, 0f, 1f);
            dragonFireOrbitAngle += DragonFireOrbitRadPerSec * ramp * dt;
        }
        else
        {
            dragonFireHoldSeconds = 0f;
            dragonFireOrbitAngle = WrapAngleRad(dragonFireOrbitAngle);
            dragonFireOrbitAngle -= dragonFireOrbitAngle * Math.Clamp(DragonFireOrbitReturnPerSec * dt, 0f, 1f);
            if (MathF.Abs(dragonFireOrbitAngle) < 0.005f)
            {
                dragonFireOrbitAngle = 0f;
            }
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
        // The orbit bends only the EYE's azimuth (worldFwd/heading chase stay untouched), and the farther
        // round it goes the more the camera looks AT the dragon — a side shot with the full 30 m look-ahead
        // would push the beast out of frame.
        float eyeAz = dragonCamAzimuth + dragonFireOrbitAngle;
        float ceh = MathF.Cos(eyeAz), seh = MathF.Sin(eyeAz);
        Vector3 eyeFwd = Vector3.Normalize(new Vector3(cp * ceh, cp * seh, sp * exagg));
        float lookAhead = DragonChaseLookAheadMeters
            * MathF.Cos(Math.Clamp(MathF.Abs(WrapAngleRad(dragonFireOrbitAngle)), 0f, MathF.PI / 2f));
        Vector3 eye = dragonWorld - (eyeFwd * chaseDistance) + new Vector3(0f, 0f, chaseHeight * exagg);
        Vector3 lookAt = dragonWorld + (worldFwd * lookAhead);
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

        // Per-hand strike (thrust into the rock). RIGHT axe swings on a left-click; while self-arresting BOTH
        // axes hold fully planted; while CLIMBING the two axes drive in ALTERNATELY (left, right, left…) — the
        // "wbijasz lewy prawy i idziesz pod górę" gait.
        float strikeRight = 0f;
        float strikeLeft = 0f;
        if (walkSwinging)
        {
            float p = (float)((t - walkSwingStartSeconds) / CiupagaSwingSeconds);
            if (p >= 1f)
            {
                walkSwinging = false;
            }
            else
            {
                strikeRight = SwingStrike(Math.Clamp(p, 0f, 1f));
            }
        }

        if (walker is { IsClimbing: true })
        {
            // Two alternating plants: each hand runs a plant-and-recover envelope, offset half a cycle.
            const float climbCyclesPerSec = 1.7f;
            float beat = t * climbCyclesPerSec;
            strikeRight = MathF.Max(strikeRight, SwingStrike(beat - MathF.Floor(beat)));
            float leftBeat = beat + 0.5f;
            strikeLeft = SwingStrike(leftBeat - MathF.Floor(leftBeat));
        }
        else if (walker is { IsHanging: true })
        {
            strikeRight = 1f;   // both axes buried into the rock, holding the hang
            strikeLeft = 1f;
        }

        // RIGHT axe (primary right-hand viewmodel).
        DrawOneCiupaga(canvas, width, height, u, bobX, bobY, swayDeg, strikeRight);

        // LEFT axe: the same drawing mirrored horizontally about the screen centre → a symmetric left-hand tool.
        canvas.Save();
        canvas.Scale(-1f, 1f, width * 0.5f, 0f);
        DrawOneCiupaga(canvas, width, height, u, bobX, bobY, swayDeg, strikeLeft);
        canvas.Restore();
    }

    // Draws ONE first-person ciupaga in the lower-right held pose (mirror the canvas to get the left-hand one).
    private void DrawOneCiupaga(SKCanvas canvas, int width, int height, float u, float bobX, float bobY, float swayDeg, float strike)
    {
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
        if (!TryApplyPinnedCamera() && !TryApplyEnvPose() && !TryRestoreCamera(frame))
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
        long perfT0 = System.Diagnostics.Stopwatch.GetTimestamp(); // dragon-mode paint breakdown (cheap, always)
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
            // Eye ~1.7 m over the ground: the fly path's ≥5 m near clips the ground at your feet. Pull near up
            // to the boots, cap far to a walker's horizon. Back at the ORIGINAL 0.3/16000 (a smaller near
            // crushed far-Z precision and made distant clouds z-fight/flicker). Wall clipping is handled by the
            // CLIMB (attach + climb steep faces, never jump INTO them), so the near stays at the safe value.
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
        long perfT1 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (UseGlRenderer && TryRenderTerrainGl(canvas, tiles, e.Info.Width, e.Info.Height))
        {
            long perfT2 = System.Diagnostics.Stopwatch.GetTimestamp();
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
            DrawAiDragonMarkers(canvas, e.Info.Width, e.Info.Height);
            if (!walkThirdPerson) { DrawWalkViewmodel(canvas, e.Info.Width, e.Info.Height); }
            DrawClimbStaminaHud(canvas, e.Info.Width, e.Info.Height);
            DrawClimbSelectionOverlay(canvas, e.Info.Width, e.Info.Height);
            DrawClimbingRouteLabels(canvas, e.Info.Width, e.Info.Height);
            DrawClimbCalibrationLabels(canvas, e.Info.Width, e.Info.Height);
            DrawDragon(canvas, e.Info.Width, e.Info.Height);
            if (dragonActive)
            {
                long perfT3 = System.Diagnostics.Stopwatch.GetTimestamp();
                RecordDragonPaint(PerfMs(perfT0, perfT1), PerfMs(perfT1, perfT2), PerfMs(perfT2, perfT3));
            }

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
            ServiceTestHarness(e, frame); // harness działa też na ścieżce GL (metoda kończy się tym returnem)
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
        ServiceTestHarness(e, frame);
    }

    // ── TEST HARNESS (2026-07-24, na prośbę usera): deterministyczne testy bez rąk na klawiaturze ──
    // MAPATUR_START_POSE="tx;ty;tz;dist;az;pitch" — start w dokładnej pozie (format pinned-camera);
    // MAPATUR_CHROME=0 — panele schowane od startu; MAPATUR_SHOT_DIR=<dir> — F10 lub
    // MAPATUR_AUTOSHOT_SEC=n zapisuje PNG bieżącej klatki Z POZIOMU APKI (działa mimo blokady ekranu);
    // sync pozy: co ~1,2 s bieżąca poza + geo celu ląduje w %TEMP%\mapatur-pose.txt — agent czyta,
    // gdzie user patrzy podczas testu manualnego, i może wystartować drugą instancję w TEJ pozie.
    private static readonly string? HarnessStartPose = Environment.GetEnvironmentVariable("MAPATUR_START_POSE");
    private static readonly bool HarnessChromeOff = Environment.GetEnvironmentVariable("MAPATUR_CHROME") == "0";
    private static readonly string? HarnessShotDir = Environment.GetEnvironmentVariable("MAPATUR_SHOT_DIR");
    private static readonly int HarnessAutoshotSec =
        int.TryParse(Environment.GetEnvironmentVariable("MAPATUR_AUTOSHOT_SEC"), out int s) ? s : 0;
    internal static readonly string HarnessPosePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mapatur-pose.txt");
    private bool harnessChromeApplied;
    private bool harnessPoseApplied;
    private double harnessLastShotMs;
    private double harnessLastPoseMs;
    private volatile bool harnessShotRequested;

    private bool TryApplyEnvPose()
    {
        if (harnessPoseApplied || string.IsNullOrEmpty(HarnessStartPose))
        {
            return false;
        }

        string[] parts = HarnessStartPose.Split(';');
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (parts.Length == 6
            && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, ci, out float tx)
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
            harnessPoseApplied = true;
            Serilog.Log.Information("[Harness] start-poza z env: {Pose}", HarnessStartPose);
            return true;
        }

        return false;
    }

    private void ServiceTestHarness(SKPaintGLSurfaceEventArgs e, TerrainMesh3D frame)
    {
        if (HarnessChromeOff && !harnessChromeApplied)
        {
            harnessChromeApplied = true;
            SetChromeVisible(false);
        }

        double nowMs = Environment.TickCount64; // recordClock tyka tylko przy nagrywaniu — tu potrzebny zawsze
        if (nowMs - harnessLastPoseMs >= 2000)
        {
            harnessLastPoseMs = nowMs;
            try { System.IO.File.WriteAllText(HarnessPosePath, SerializeCamera(frame)); }
            catch (System.IO.IOException) { }
        }

        if (harnessLastShotMs == 0)
        {
            harnessLastShotMs = nowMs; // pierwsza fotka dopiero po pełnym interwale (nie ekran ładowania)
        }

        bool due = HarnessAutoshotSec > 0 && nowMs - harnessLastShotMs >= HarnessAutoshotSec * 1000.0;
        if (HarnessShotDir is null || (!harnessShotRequested && !due))
        {
            return;
        }

        harnessShotRequested = false;
        harnessLastShotMs = nowMs;
        try
        {
            using SkiaSharp.SKImage snap = e.Surface.Snapshot();
            using SkiaSharp.SKData png = snap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            System.IO.Directory.CreateDirectory(HarnessShotDir);
            string name = $"shot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
            using System.IO.FileStream fs = System.IO.File.Create(System.IO.Path.Combine(HarnessShotDir, name));
            png.SaveTo(fs);
            Serilog.Log.Information("[Harness] zrzut {Name} | poza {Pose}", name, SerializeCamera(frame));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "[Harness] zapis zrzutu nieudany");
        }
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

    // Locator markers for the AI flock (the dragons are small + wander, so they're hard to spot). ON screen: a
    // little downward chevron floats over each dragon. OFF screen / behind the camera: an arrow pins to the
    // screen edge pointing the way to it — same idea as the navigation "namierzanie" reticle. Colour = the
    // dragon's tint, so a marker maps to its dragon.
    private void DrawAiDragonMarkers(SKCanvas canvas, int width, int height)
    {
        if (!ShowAiDragons || aiFlock.Count == 0 || WorldFrame is not { } frame || width <= 0 || height <= 0)
        {
            return;
        }

        float exagg = frame.VerticalExaggeration;
        Matrix4x4 vp = Camera.BuildViewProjection((float)width / Math.Max(1, height));
        float cx = width * 0.5f, cy = height * 0.5f;
        const float edgeMargin = 48f;

        foreach (AiFlockDragon m in aiFlock)
        {
            if (!m.Alive)
            {
                continue; // shot down — no locator marker
            }

            var world = new Vector4(m.Flight.PositionXY.X, m.Flight.PositionXY.Y, m.Flight.ElevationMeters * exagg, 1f);
            Vector4 clip = Vector4.Transform(world, vp);
            SKColor color = TintToColor(m.Tint);

            bool inFront = clip.W > 0.0001f;
            if (inFront)
            {
                float sx = ((clip.X / clip.W) + 1f) * 0.5f * width;
                float sy = (1f - (clip.Y / clip.W)) * 0.5f * height;
                float ndcZ = clip.Z / clip.W;
                if (sx >= 0f && sx <= width && sy >= 0f && sy <= height && ndcZ is >= 0f and <= 1f)
                {
                    DrawDragonOnScreenMarker(canvas, sx, sy, color);
                    continue;
                }
            }

            // Off-screen (or behind): screen-space direction from the centre. Behind the camera (W<0) the sign of
            // X/Y flips, so mirror it to keep the arrow pointing the true way.
            float dx = inFront ? clip.X / clip.W : -clip.X;
            float dy = inFront ? clip.Y / clip.W : -clip.Y;
            float vX = dx, vY = -dy; // NDC y-up → screen y-down
            float len = MathF.Sqrt((vX * vX) + (vY * vY));
            if (len < 1e-4f)
            {
                continue;
            }

            vX /= len;
            vY /= len;
            float halfW = (width * 0.5f) - edgeMargin, halfH = (height * 0.5f) - edgeMargin;
            float reach = MathF.Min(halfW / MathF.Max(1e-4f, MathF.Abs(vX)), halfH / MathF.Max(1e-4f, MathF.Abs(vY)));
            DrawDragonEdgeArrow(canvas, cx + (vX * reach), cy + (vY * reach), MathF.Atan2(vY, vX), color);
        }
    }

    private static SKColor TintToColor(Vector3 tint) => new(
        (byte)(Math.Clamp(tint.X, 0f, 1f) * 255f),
        (byte)(Math.Clamp(tint.Y, 0f, 1f) * 255f),
        (byte)(Math.Clamp(tint.Z, 0f, 1f) * 255f));

    // A small downward chevron hovering just above the on-screen dragon.
    private static void DrawDragonOnScreenMarker(SKCanvas canvas, float x, float y, SKColor color)
    {
        float topY = y - 34f; // float above the dragon
        using var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var outline = new SKPaint { Color = new SKColor(0x10, 0x10, 0x10, 0xCC), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };
        using var path = new SKPath();
        path.MoveTo(x - 9f, topY);
        path.LineTo(x + 9f, topY);
        path.LineTo(x, topY + 13f); // point down at the dragon
        path.Close();
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, outline);
    }

    // A triangular arrow pinned to the screen edge, pointing (angle radians) toward an off-screen dragon.
    private static void DrawDragonEdgeArrow(SKCanvas canvas, float x, float y, float angle, SKColor color)
    {
        using var fill = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var halo = new SKPaint { Color = new SKColor(0x10, 0x10, 0x10, 0x99), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f };
        canvas.Save();
        canvas.Translate(x, y);
        canvas.RotateRadians(angle);
        using var path = new SKPath();
        path.MoveTo(16f, 0f);    // tip points along +X (the rotation aims it)
        path.LineTo(-10f, 9f);
        path.LineTo(-10f, -9f);
        path.Close();
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, halo);
        canvas.Restore();
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
                && !MapaTur.Application.Terrain.TerrainOcclusion.IsVisibleFine(cameraPos, world, SampleWalkGround, frame.VerticalExaggeration))
            {
                continue; // behind the detailed rock — hidden like the peak + POI labels
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

            // Sequential: the fine march samples the detail elevation delegate, which is not guaranteed
            // thread-safe (the LOD stream can mutate it). The recompute is throttled, so this is cheap.
            var keep = new bool[n];
            for (int i = 0; i < n; i++)
            {
                keep[i] = IsPeakVisible(peaks[i], cam, frame);
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

    private bool IsPeakVisible(ProjectedPeak p, System.Numerics.Vector3 cam, TerrainMesh3D frame)
    {
        if (p.ScreenPosition is null)
        {
            return false; // off-screen — it won't draw, so skip its raycast
        }

        // March against the DETAILED rendered surface (SampleWalkGround), not the coarse raster — a name
        // behind the detailed rock (Mnich needle) must hide even though the coarse DEM there is far lower.
        System.Numerics.Vector3 world = frame.GeoToWorld(p.Source.Location, (float)p.Source.ElevationMeters);
        return MapaTur.Application.Terrain.TerrainOcclusion.IsVisibleFine(
            cam, world, SampleWalkGround, frame.VerticalExaggeration);
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

            // Sequential (fine-march delegate is not thread-safe), like HideOccludedPeaks.
            var keep = new bool[n];
            for (int i = 0; i < n; i++)
            {
                keep[i] = IsPoiVisible(pois[i], cam, raster, frame, poiLift);
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

    private bool IsPoiVisible(ProjectedPoi p, System.Numerics.Vector3 cam, DemRaster raster, TerrainMesh3D frame, float poiLift)
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

        // March against the detailed rendered surface so a hut/POI behind the rock hides (see IsPeakVisible).
        System.Numerics.Vector3 world = frame.GeoToWorld(p.Source.Position, (float)ground + poiLift);
        return MapaTur.Application.Terrain.TerrainOcclusion.IsVisibleFine(
            cam, world, SampleWalkGround, frame.VerticalExaggeration);
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

#if WINDOWS
    private bool vsyncLoopActive;
    private EventHandler<object>? vsyncRenderingHandler;

    /// <summary>
    /// Starts the per-composed-frame loop that drives the walk/dragon simulation + repaint. One sim step per
    /// display refresh, phase-locked to the compositor — no DispatcherTimer↔vsync beat. Idempotent; the
    /// handler unhooks itself once neither mode is active, so exits need no explicit stop.
    /// </summary>
    private void StartVsyncLoop()
    {
        if (vsyncLoopActive)
        {
            return;
        }

        vsyncRenderingHandler ??= OnVsyncRendering;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += vsyncRenderingHandler;
        vsyncLoopActive = true;
    }

    private void StopVsyncLoop()
    {
        if (!vsyncLoopActive)
        {
            return;
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= vsyncRenderingHandler;
        vsyncLoopActive = false;
    }

    private void OnVsyncRendering(object? sender, object e)
    {
        if (dragonActive)
        {
            OnDragonTick(this, EventArgs.Empty);
        }
        else if (walkActive)
        {
            OnWalkTick(this, EventArgs.Empty);
        }
        else
        {
            StopVsyncLoop();
        }
    }
#endif

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
        int delta = e.GetCurrentPoint((Microsoft.UI.Xaml.UIElement)sender).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        if (dragonActive)
        {
            return; // no wheel-zoom while flying the dragon
        }

        // One wheel notch = 120 units; ~10% per notch. Scroll up = zoom in (closer).
        if (walkActive)
        {
            // Walk mode: the wheel dollies the 3rd-person camera in/out behind the walker.
            walkCamBack = Math.Clamp(
                walkCamBack * MathF.Pow(1f / 1.1f, delta / 120f), WalkCamBackMinMeters, WalkCamBackMaxMeters);
            Canvas.InvalidateSurface();
            e.Handled = true;
            return;
        }

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

        // Walk mode: RIGHT-drag = look around; while climbing, LEFT = pick a hold (the PoC's manual
        // mouse mode). Otherwise the LEFT button falls through to the UI — the legacy ciupaga swing
        // retired with the hold-by-hold takeover.
        if (walkActive)
        {
            lastPointerPosition = e.GetCurrentPoint(element).Position;
            if (props.IsLeftButtonPressed && gripClimb.IsActive)
            {
                var clickAt = e.GetCurrentPoint(element).Position;
                TryClimbHoldClick((float)clickAt.X, (float)clickAt.Y, element);
                e.Handled = true;
                return;
            }

            if (props.IsRightButtonPressed)
            {
                walkRmbHeld = true;
                element.CapturePointer(e.Pointer); // capture so the release reliably clears the hold
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

    // Builds a view ray from a click position (camera basis + vertical FOV; walk camera never rolls,
    // so world-Z is a safe up reference) and hands it to the climb controller's hold picking.
    private void TryClimbHoldClick(float pixelX, float pixelY, Microsoft.UI.Xaml.UIElement element)
    {
        if (WorldFrame is not { } frame || element is not Microsoft.UI.Xaml.FrameworkElement fe
            || fe.ActualWidth < 1 || fe.ActualHeight < 1)
        {
            return;
        }

        Vector3 forward = Vector3.Normalize(Camera.Target - Camera.Position);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        Vector3 up = Vector3.Cross(right, forward);
        float ndcX = (float)((2.0 * pixelX / fe.ActualWidth) - 1.0);
        float ndcY = (float)(1.0 - (2.0 * pixelY / fe.ActualHeight));
        float tanY = MathF.Tan(Camera.FieldOfViewYRadians * 0.5f);
        float tanX = tanY * (float)(fe.ActualWidth / fe.ActualHeight);
        Vector3 direction = Vector3.Normalize(
            forward + (right * (ndcX * tanX)) + (up * (ndcY * tanY)));
        gripClimb.HandleClick(Camera.Position, direction, frame.VerticalExaggeration);
    }

    // Two-click climbing UI: outlines every hold the SELECTED limb can take (green = the strict solver
    // approves, orange = permissive-only) with a small risk % under each, plus a white ring on the
    // selected limb's current hold. Drawn on the Skia overlay after the HUD.
    private void DrawClimbSelectionOverlay(SKCanvas canvas, int width, int height)
    {
        if (!gripClimb.IsActive || WorldFrame is not { } frame || gripClimb.SelectedLimb is null)
        {
            return;
        }

        float exaggeration = frame.VerticalExaggeration;
        Matrix4x4 viewProjection = Camera.BuildViewMatrix() * Camera.BuildProjectionMatrix((float)width / height);
        using var ringOk = new SKPaint
        { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = new SKColor(90, 255, 130) };
        using var ringRisky = new SKPaint
        { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = new SKColor(255, 175, 60) };
        using var ringSelected = new SKPaint
        { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, Color = SKColors.White };
        using var textFill = new SKPaint { IsAntialias = true, Color = SKColors.White };
        using var textHalo = new SKPaint
        { IsAntialias = true, Color = new SKColor(0, 0, 0, 200), Style = SKPaintStyle.Stroke, StrokeWidth = 3f };
        using var statFont = new SKFont { Size = 12f };

        foreach (GripClimbController.ClimbCandidateInfo candidate in gripClimb.Candidates)
        {
            Vector3 world = candidate.Hold.Position;
            world.Z *= exaggeration;
            if (Camera.ProjectToScreen(world, viewProjection, width, height) is not { } screen)
            {
                continue;
            }

            SKPaint ring = candidate.PlannerOk ? ringOk : ringRisky;
            canvas.DrawCircle(screen.X, screen.Y, 10f, ring);
            string stat = $"{candidate.RiskPercent:0}%";
            canvas.DrawText(stat, screen.X, screen.Y + 24f, SKTextAlign.Center, statFont, textHalo);
            canvas.DrawText(stat, screen.X, screen.Y + 24f, SKTextAlign.Center, statFont, textFill);
        }

        if (gripClimb.SelectedHoldPosition is { } selectedPos)
        {
            selectedPos.Z *= exaggeration;
            if (Camera.ProjectToScreen(selectedPos, viewProjection, width, height) is { } screen)
            {
                canvas.DrawCircle(screen.X, screen.Y, 13f, ringSelected);
                string limbLabel = gripClimb.SelectedLimb switch
                {
                    MapaTur.Climbing.ClimbLimb.LeftHand => "L ręka",
                    MapaTur.Climbing.ClimbLimb.RightHand => "P ręka",
                    MapaTur.Climbing.ClimbLimb.LeftFoot => "L noga",
                    _ => "P noga",
                };
                canvas.DrawText(limbLabel, screen.X, screen.Y - 18f, SKTextAlign.Center, statFont, textHalo);
                canvas.DrawText(limbLabel, screen.X, screen.Y - 18f, SKTextAlign.Center, statFont, textFill);
            }
        }
    }

    private void OnPlatformPointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var element = (Microsoft.UI.Xaml.UIElement)sender;
        var point = e.GetCurrentPoint(element);
        var props = point.Properties;

        // Walk mode is driven from the LIVE button flags, not mouseDragButton: pressing a SECOND mouse button (right
        // while the left is already held climbing) does not reliably fire PointerPressed on Windows — it arrives
        // here with both flags set. LEFT alone = climb (no look). RIGHT = turn the head. LEFT+RIGHT = free-look
        // (orbit the camera without changing the climb heading, so you keep climbing while you look around).
        if (walkActive)
        {
            if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed)
            {
                return; // hover, no button → ignore
            }

            float wdx = (float)(point.Position.X - lastPointerPosition.X);
            float wdy = (float)(point.Position.Y - lastPointerPosition.Y);
            lastPointerPosition = point.Position;

            if (props.IsRightButtonPressed && props.IsLeftButtonPressed)
            {
                walkRmbHeld = true;
                walkCamYawOffset -= wdx * WalkMouseLookRadiansPerPixel;
                walkCamPitchFree = Math.Clamp(walkCamPitchFree - (wdy * WalkMouseLookRadiansPerPixel), -1.2f, 1.2f);
            }
            else if (props.IsRightButtonPressed)
            {
                walkRmbHeld = true;
                walkHeadingRadians -= wdx * WalkMouseLookRadiansPerPixel;
                walkLookPitchRadians = Math.Clamp(
                    walkLookPitchRadians - (wdy * WalkMouseLookRadiansPerPixel),
                    -WalkMaxLookPitchRadians,
                    WalkMaxLookPitchRadians);
            }
            else
            {
                walkRmbHeld = false; // left-only: keep the baseline fresh (left is the climb hold, not a look)
            }

            Canvas.InvalidateSurface();
            e.Handled = true;
            return;
        }

        if (mouseDragButton == 0)
        {
            return;
        }

        if (!props.IsLeftButtonPressed && !props.IsRightButtonPressed)
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

        if (mouseDragButton == 1)
        {
            // Left-drag orbits around the focus (camera circles the scene).
            controller.ApplyOrbit(dx, -dy);
        }
        else
        {
            // Right-drag looks around in place — the camera stays put and turns its view direction.
            controller.ApplyLookAround(dx, dy);
        }

        Canvas.InvalidateSurface();
        e.Handled = true;
    }

    private void OnPlatformPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var released = (Microsoft.UI.Xaml.UIElement)sender;
        var relProps = e.GetCurrentPoint(released).Properties;

        // Only drop the hold whose button actually came up (RMB = free-look; LMB is free since the
        // legacy ciupaga swing/hold retired with the hold-by-hold climbing takeover).
        if (!relProps.IsRightButtonPressed)
        {
            walkRmbHeld = false;
            if (mouseDragButton == 2)
            {
                mouseDragButton = 0;
            }
        }

        if (!relProps.IsLeftButtonPressed && !relProps.IsRightButtonPressed)
        {
            mouseDragButton = 0;
            released.ReleasePointerCapture(e.Pointer);
        }

        dragonRmbHeld = false; // release the dragon attitude hold → pitch auto-levels again
    }

    private object? _lastKeyDownArgs;
    private void OnPlatformKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // DEDUP (2026-07-21): this handler is registered on BOTH this element AND the window root
        // (handledEventsToo:true, see ~6500/6511) so it can receive keys the focus system pre-marks handled —
        // but the SAME KeyDown args then bubble through twice, firing us twice per press, so TOGGLE keys (9, 0)
        // cancel themselves out (net no change). Ignore the second fire of the same args object.
        if (ReferenceEquals(e, _lastKeyDownArgs)) { return; }
        _lastKeyDownArgs = e;
        // The handler also listens at the window root, so ignore keys unless 3D mode is actually showing.
        if (!IsVisible)
        {
            return;
        }

        // F1–F6: baked-shadow debug views (2026-07-11) — F1 final, F2 albedo, F3 lowLuma sub-mask,
        // F4 cool/cyan sub-mask, F5 combined mask, F6 corrected albedo. SPLIT masks so we see which condition
        // misfires. Number keys 1/2/3 set the compensation strength 0 / 0.5 / 1.0.
        if (glRenderer is { } r)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.F1: r.DebugTerrainView = 0f; e.Handled = true; return;
                case Windows.System.VirtualKey.F2: r.DebugTerrainView = 1f; e.Handled = true; return;
                case Windows.System.VirtualKey.F3: r.DebugTerrainView = 2f; e.Handled = true; return;
                case Windows.System.VirtualKey.F4: r.DebugTerrainView = 3f; e.Handled = true; return;
                case Windows.System.VirtualKey.F5: r.DebugTerrainView = 4f; e.Handled = true; return;
                case Windows.System.VirtualKey.F6: r.DebugTerrainView = 5f; e.Handled = true; return;
                case Windows.System.VirtualKey.Number1: r.BakedShadowComp = 0f; Serilog.Log.Information("[BakedShadow] comp=0"); e.Handled = true; return;
                case Windows.System.VirtualKey.Number2: r.BakedShadowComp = 0.5f; Serilog.Log.Information("[BakedShadow] comp=0.5"); e.Handled = true; return;
                case Windows.System.VirtualKey.Number3: r.BakedShadowComp = 1.0f; Serilog.Log.Information("[BakedShadow] comp=1.0"); e.Handled = true; return;
                // '0' toggles the hi-res ortho detail overlay (A/B): OFF = the plain base ortho everywhere.
                case Windows.System.VirtualKey.Number0:
                    r.OrthoDetailEnabled = !r.OrthoDetailEnabled;
                    Serilog.Log.Information("[OrthoDetailSlice] overlay {State}", r.OrthoDetailEnabled ? "ON" : "OFF");
                    e.Handled = true; return;
                // '9' cycles the detail colour variant: raw detail <-> the base ortho's de-blue transform.
                case Windows.System.VirtualKey.Number9:
                    r.OrthoDetailColorMode = r.OrthoDetailColorMode == 0 ? 1 : 0;
                    Serilog.Log.Information("[OrthoDetailSlice] colour = {Mode}", r.OrthoDetailColorMode == 1 ? "TONE-FROM-BASE (detail = fine frequencies only)" : "RAW");
                    e.Handled = true; return;
                case Windows.System.VirtualKey.F10:
                    harnessShotRequested = true; // TEST HARNESS: zrzut biezacej klatki z poziomu apki
                    e.Handled = true; return;
                // '7' — A/B tier det1m (krok 3): przełącza WYŁĄCZNIE uniform użycia — dane zostają rezydentne
                // na GPU, więc porównanie panoramy nie jest skażone streamingiem (warunek testu).
                case Windows.System.VirtualKey.Number7:
                    r.Det1mEnabled = !r.Det1mEnabled;
                    Serilog.Log.Information("[Det1m] A/B: {State}", r.Det1mEnabled ? "ON" : "OFF");
                    e.Handled = true; return;
                // '8' toggles the cell-boundary outline (diagnostics).
                case Windows.System.VirtualKey.Number8:
                    r.OrthoDetailDebugBounds = !r.OrthoDetailDebugBounds;
                    Serilog.Log.Information("[OrthoDetailSlice] cell-bounds outline {State}", r.OrthoDetailDebugBounds ? "ON" : "OFF");
                    e.Handled = true; return;
                // 'M' toggles the climbing-route calibration marker grid (clean view ↔ calibration).
                case Windows.System.VirtualKey.M:
                    climbCalibMarkersVisible = !climbCalibMarkersVisible;
                    if (!climbCalibMarkersVisible)
                    {
                        climbCalibMarkers.Clear();
                        climbCalibLabels.Clear();
                    }
                    else
                    {
                        climbCalibNextBuildTicks = 0; // force an immediate rebuild
                    }

                    Serilog.Log.Information("[Climb] calibration markers {State}", climbCalibMarkersVisible ? "ON" : "OFF");
                    e.Handled = true; return;
            }
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
            case Windows.System.VirtualKey.PageDown:
                walkLookPitchRadians = Math.Clamp(walkLookPitchRadians - WalkKeyTurnRadians, -WalkMaxLookPitchRadians, WalkMaxLookPitchRadians);
                break;
            case Windows.System.VirtualKey.F:
                walkShootQueued = true; // F = fire the crossbow (the avatar plays its one-shot ranged clip)
                break;
            case Windows.System.VirtualKey.C:
                // C = grab the wall ahead (hold-by-hold climbing) / let go. Key auto-repeat must NOT
                // toggle the session on/off in a loop while held — accept only the initial press.
                if (!e.KeyStatus.WasKeyDown)
                {
                    walkClimbToggleQueued = true;
                }

                break;
            case Windows.System.VirtualKey.X:
                // X = wypnij się: drop the auto-belay (pitons + rope). Hanging on the rope this is a
                // deliberate free fall; on the ground it just clears the gear. Initial press only.
                if (!e.KeyStatus.WasKeyDown)
                {
                    walkBelayReleaseQueued = true;
                }

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
                Serilog.Log.Information("[Dragon] Space (flap/takeoff)");
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
#endif // WINDOWS — pause the keyboard/mouse region: the two helpers below are platform-clean AND used by
    // android-visible callers (the dragon pose/camera code), so they must compile on every TFM.

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

    /// <summary>Fractional part — the deterministic hash workhorse of the dragon sim (all TFMs).</summary>
    private static float Frac(float x) => x - MathF.Floor(x);
#if WINDOWS // — resume the desktop-only region (fire-breath sim, keyboard handlers, audio hooks)

    // Picks the AI dragon whose bearing from the muzzle sits inside the ±10° lock cone AND is most aligned with
    // the aim (so a fireball auto-homes onto it). Returns its aiFlock index, or −1 when the flock is off or
    // nothing is in the cone. All in real metres.
    private int AcquireFireballTarget(System.Numerics.Vector2 muzzleXY, float muzzleElev, Vector3 aimUnit)
    {
        if (!ShowAiDragons || aiFlock.Count == 0)
        {
            return -1;
        }

        float bestDot = DragonFireLockConeCos; // only a bearing inside the cone can beat this
        int best = -1;
        for (int i = 0; i < aiFlock.Count; i++)
        {
            AiFlockDragon t = aiFlock[i];
            if (!t.Alive)
            {
                continue;
            }

            var to = new Vector3(
                t.Flight.PositionXY.X - muzzleXY.X,
                t.Flight.PositionXY.Y - muzzleXY.Y,
                t.Flight.ElevationMeters - muzzleElev);
            float len = to.Length();
            if (len < 1f)
            {
                continue;
            }

            float dot = Vector3.Dot(aimUnit, to / len);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = i;
            }
        }

        return best;
    }

    // A fireball hit kills the dragon: flag it dead (skipped by update/render/markers/targeting) and throw a
    // fat fireball burst at its spot for the kill flash. Kept in aiFlock so other in-flight balls' target
    // indices don't shift under them.
    private void KillAiDragon(int index)
    {
        if (index < 0 || index >= aiFlock.Count || !aiFlock[index].Alive)
        {
            return;
        }

        AiFlockDragon dead = aiFlock[index];
        dead.Alive = false;
        dragonAudio.PlayRoar(0.9f); // victory roar over the kill
        // The kill blast itself is spawned by the hitting ball via SpawnFireBurst(power 2.2) at the impact point.
        Serilog.Log.Information(
            "[DragonFire] killed dragon #{Idx} ({Kind}) at ({X:F0},{Y:F0}) — {Alive} left",
            index, dead.Kind, dead.Flight.PositionXY.X, dead.Flight.PositionXY.Y, aiFlock.Count(a => a.Alive));
    }

    // ── B2: reduce this tick's fire sprites to ≤8 point lights for the terrain/dragon/smoke shaders ──
    // Score = intensity·r² (energy proxy). Greedy: take the strongest sprite, absorb everything within its
    // merge radius into a weighted-average position, repeat. Colour = warm orange × intensity × a
    // deterministic per-light flicker. Positions stay in the sprites' frame (absolute world, exaggerated Z).
    private readonly List<(Vector3 Pos, float R, float Score, float Seed)> fireLightScratch = new(96);
    private readonly Vector3[] fireLightPosOut = new Vector3[8];
    private readonly Vector3[] fireLightColOut = new Vector3[8];
    private readonly float[] fireLightInvR2Out = new float[8];

    // B4: session-persistent scorch splats (charred ground under fireball hits). A ring of 24 — the oldest
    // mark is simply overwritten. Param = (radius², strength) as the terrain shader consumes it.
    private readonly System.Numerics.Vector2[] dragonScorchPos = new System.Numerics.Vector2[24];
    private readonly System.Numerics.Vector2[] dragonScorchParam = new System.Numerics.Vector2[24];
    private int dragonScorchNext;
    private int dragonScorchCount;
    private bool dragonScorchDirty;

    private void PushFireLights(float timeSeconds)
    {
        if (glRenderer is not { } renderer)
        {
            return;
        }

        fireLightScratch.Clear();
        foreach (Services.Terrain3DGlRenderer.FireballSprite s in dragonFireSprites)
        {
            if (s.Kind > 1.5f && s.Kind < 2.5f)
            {
                continue; // Shock: a thin ring, no real emission (smoke/steam never land in this list)
            }

            float score = s.Intensity * s.RadiusMeters * s.RadiusMeters;
            if (score > 0.05f)
            {
                fireLightScratch.Add((s.WorldPos, s.RadiusMeters, score, s.Seed));
            }
        }

        int count = 0;
        while (count < 8 && fireLightScratch.Count > 0)
        {
            int best = 0;
            for (int i = 1; i < fireLightScratch.Count; i++)
            {
                if (fireLightScratch[i].Score > fireLightScratch[best].Score)
                {
                    best = i;
                }
            }

            (Vector3 pos, float r, float score, float seed) = fireLightScratch[best];
            fireLightScratch.RemoveAt(best);
            Vector3 acc = pos * score;
            float accScore = score;
            float accR = r;
            float mergeR = MathF.Max(2.5f * r, 18f);
            for (int i = fireLightScratch.Count - 1; i >= 0; i--)
            {
                (Vector3 Pos, float R, float Score, float Seed) c = fireLightScratch[i];
                if (Vector3.DistanceSquared(c.Pos, pos) <= mergeR * mergeR)
                {
                    acc += c.Pos * c.Score;
                    accScore += c.Score;
                    accR = MathF.Max(accR, c.R);
                    fireLightScratch.RemoveAt(i);
                }
            }

            Vector3 center = acc / MathF.Max(1e-3f, accScore);
            float intensity = Math.Clamp(accScore / MathF.Max(1f, accR * accR), 0f, 2.2f);
            float flicker = 0.8f + (0.25f * MathF.Sin(timeSeconds * (11f + seed)));
            fireLightPosOut[count] = center;
            fireLightColOut[count] = new Vector3(1.0f, 0.58f, 0.24f) * (intensity * flicker);
            float reach = 3f * MathF.Max(3f, accR);
            fireLightInvR2Out[count] = 1f / (reach * reach);
            count++;
        }

        renderer.SetFireLights(count, fireLightPosOut, fireLightColOut, fireLightInvR2Out);
        if (dragonScorchDirty)
        {
            renderer.SetScorchMarks(dragonScorchCount, dragonScorchPos, dragonScorchParam);
            dragonScorchDirty = false;
        }
    }

    // Advances the fire-breath simulation one tick: spawns a ball from the mouth while F is held, flies the
    // balls forward (auto-homing onto a locked dragon, else a touch of hot-gas buoyancy), bursts on contact,
    // and rebuilds the render sprite list (world coords, Z exaggerated).
    private void StepDragonFire(MapaTur.Application.Terrain.DragonFlight d, float dt, float exaggeration)
    {
        dragonAudio.SetFireActive(dragonFireHeld); // looped roar rides the held key (idempotent per state)
        dragonFireCooldown -= dt;
        if (dragonFireHeld && dragonFireCooldown <= 0f)
        {
            float cp = MathF.Cos(d.PitchRadians), sp = MathF.Sin(d.PitchRadians);
            float chh = MathF.Cos(d.HeadingRadians), shh = MathF.Sin(d.HeadingRadians);
            var fwdXY = new System.Numerics.Vector2(cp * chh, cp * shh);
            float speed = d.SpeedMetersPerSecond + DragonFireSpeedMetersPerSecond;
            System.Numerics.Vector2 muzzleXY;
            float muzzleElev;
            if (dragonMouthWorld is { } mw)
            {
                // From the posed HEAD (follows the animation), + a snout offset along the aim.
                muzzleXY = new System.Numerics.Vector2(mw.X, mw.Y) + (fwdXY * DragonSnoutOffsetMeters);
                muzzleElev = (mw.Z / MathF.Max(0.001f, exaggeration)) + (sp * DragonSnoutOffsetMeters);
            }
            else
            {
                muzzleXY = d.PositionXY + (fwdXY * DragonFireMuzzleOffsetMeters);
                muzzleElev = d.ElevationMeters + (sp * DragonFireMuzzleOffsetMeters) + 1f;
            }

            var aim = new Vector3(cp * chh, cp * shh, sp); // unit forward

            // CATCH-UP emitter: spawn every ball the cooldown owes this tick (one-per-tick both thinned the
            // jet at low fps AND dropped the deficit). Each ball is born `lateBy` seconds in the past and
            // pre-advanced along its direction by that much — MINUS this tick's vel·dt, which the
            // integration loop below adds right back. Without that subtraction a newborn got a full vel·dt
            // shove before its first render (≈ vel × frame-time of visible gap — "ogień 5 m przed pyskiem").
            while (dragonFireCooldown <= 0f)
            {
                float lateBy = -dragonFireCooldown; // 0..dt — how long ago this ball nominally left the mouth

                // Muzzle cone (~3.5°) + per-shot speed scatter (deterministic golden-ratio hash, no state) so the
                // jet is a pressurized SPRAY that separates along its length, not a laser of identical clones.
                // The target LOCK still uses the clean aim, not the jittered direction.
                int shot = dragonFireCounter++;
                float hgr = shot * 0.61803399f;
                float coneAngle = (hgr - MathF.Floor(hgr)) * (2f * MathF.PI);
                float rj = MathF.Sqrt((hgr * 1.37f) - MathF.Floor(hgr * 1.37f));
                var up0 = MathF.Abs(aim.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX;
                var ax = Vector3.Normalize(Vector3.Cross(aim, up0));
                var ay = Vector3.Cross(aim, ax);
                Vector3 dir = Vector3.Normalize(aim + (((ax * MathF.Cos(coneAngle)) + (ay * MathF.Sin(coneAngle))) * MathF.Tan(0.10f * rj)));
                float spd = speed * (0.78f + (0.45f * rj)); // ±22% → balls string out along the jet

                float preRoll = (lateBy - dt) * spd; // integration below adds vel·dt → net advance = lateBy·spd
                dragonFireballs.Add(new DragonFireball
                {
                    XY = muzzleXY + (new System.Numerics.Vector2(dir.X, dir.Y) * preRoll),
                    Elevation = muzzleElev + (dir.Z * preRoll),
                    VelocityXY = new System.Numerics.Vector2(dir.X, dir.Y) * spd,
                    VelocityZ = dir.Z * spd,
                    Seed = (shot * 0.731f) % 10f,
                    Age = lateBy - dt, // the loop's +dt lands it at exactly lateBy old on first render
                    TargetDragon = AcquireFireballTarget(muzzleXY, muzzleElev, aim), // lock onto a dragon in the ±10° cone
                });
                // Irregular emission rhythm (±35%): a metronome cadence + low-discrepancy jitters read as a
                // machine — a real breath sputters. Deterministic per shot, so replays stay stable.
                dragonFireCooldown += DragonFireCooldownSeconds * (0.65f + (0.7f * Frac(shot * 0.917f)));
            }
        }

        dragonFireSprites.Clear();
        dragonFireSmokeSprites.Clear();
        if (dragonFireballs.Count > 0)
        {
            RebuildLakeCache(); // one conversion per tick; balls test it for a water hit → steam
        }

        // ── Stream balls (the jet from the mouth) ── fly forward, home onto a locked dragon, and on contact or
        // terrain hand off to a staged explosion BURST (spawned into the particle pool; the ball itself is removed).
        for (int i = dragonFireballs.Count - 1; i >= 0; i--)
        {
            DragonFireball ball = dragonFireballs[i];
            ball.Age += dt;
            bool explode = false;
            float power = 1.1f;

            if (ball.TargetDragon >= 0 && ball.TargetDragon < aiFlock.Count && aiFlock[ball.TargetDragon].Alive)
            {
                AiFlockDragon t = aiFlock[ball.TargetDragon];
                var to = new Vector3(
                    t.Flight.PositionXY.X - ball.XY.X,
                    t.Flight.PositionXY.Y - ball.XY.Y,
                    t.Flight.ElevationMeters - ball.Elevation);
                float dist = to.Length();
                if (dist <= DragonFireHitRadiusMeters)
                {
                    explode = true;
                    power = 2.2f; // a kill = a big blast
                    KillAiDragon(ball.TargetDragon);
                }
                else
                {
                    var vel = new Vector3(ball.VelocityXY.X, ball.VelocityXY.Y, ball.VelocityZ);
                    float ballSpeed = vel.Length();
                    Vector3 curDir = ballSpeed > 1e-3f ? vel / ballSpeed : to / dist;
                    Vector3 newDir = Vector3.Normalize(
                        curDir + (((to / dist) - curDir) * Math.Clamp(DragonFireHomingPerSecond * dt, 0f, 1f)));
                    ball.VelocityXY = new System.Numerics.Vector2(newDir.X, newDir.Y) * ballSpeed;
                    ball.VelocityZ = newDir.Z * ballSpeed;
                }
            }
            else
            {
                // Curl-writhe: an un-guided ball snakes instead of flying dead-straight, so the jet braids like
                // a living flame. Deterministic per ball (age + seed), ramping in just past the muzzle.
                float sp2 = ball.VelocityXY.Length();
                if (sp2 > 1e-3f)
                {
                    var fwd2 = ball.VelocityXY / sp2;
                    var perp = new System.Numerics.Vector2(-fwd2.Y, fwd2.X);
                    float tt = ball.Age, sd = ball.Seed;
                    float lat = (MathF.Sin((tt * 8.5f) + (sd * 6.283f)) * 0.65f) + (MathF.Sin((tt * 19f) + (sd * 2.7f)) * 0.35f);
                    float ver = (MathF.Cos((tt * 7.0f) + (sd * 4.1f)) * 0.60f) + (MathF.Sin((tt * 23f) + (sd * 1.3f)) * 0.25f);
                    float amp = 42f * MathF.Min(1f, tt * 4f);
                    ball.VelocityXY += perp * (lat * amp * dt);
                    ball.VelocityZ += ((ver * amp) + 2.5f) * dt;
                }
            }

            ball.XY += ball.VelocityXY * dt;
            ball.Elevation += ball.VelocityZ * dt;

            bool hitWater = false;
            if (!explode)
            {
                if (TryLakeWaterElevation(ball.XY) is { } waterElev && ball.Elevation <= waterElev + 1.5f)
                {
                    explode = true;
                    hitWater = true; // quench on the lake surface → steam
                    ball.Elevation = waterElev + 0.3f;
                }
                else if (SampleContactGround(ball.XY) is { } ground && ball.Elevation <= ground + 0.5f)
                {
                    explode = true;
                    ball.Elevation = ground + 1f;
                }
            }

            if (explode)
            {
                var burstPos = new Vector3(ball.XY.X, ball.XY.Y, ball.Elevation);
                // Audio distance against the EXAGGERATED world (camera lives there), so volume matches what's on screen.
                float burstDist = Vector3.Distance(
                    Camera.Position, new Vector3(ball.XY.X, ball.XY.Y, ball.Elevation * exaggeration));
                if (hitWater)
                {
                    SpawnSteamBurst(burstPos, 1.4f);
                    dragonAudio.PlaySteam(burstDist);
                }
                else
                {
                    SpawnFireBurst(burstPos, power);
                    dragonAudio.PlayExplosion(power, burstDist);
                    // B4: a permanent charred splat under the blast (kills = bigger craters).
                    float scorchR = 6f + (4f * power);
                    dragonScorchPos[dragonScorchNext] = ball.XY;
                    dragonScorchParam[dragonScorchNext] = new System.Numerics.Vector2(scorchR * scorchR, 0.75f);
                    dragonScorchNext = (dragonScorchNext + 1) % dragonScorchPos.Length;
                    dragonScorchCount = Math.Min(dragonScorchCount + 1, dragonScorchPos.Length);
                    dragonScorchDirty = true;
                }

                dragonFireballs.RemoveAt(i);
                continue;
            }

            if (ball.Age >= DragonFireTtlSeconds)
            {
                SpawnBurnoutSmoke(ball);
                dragonFireballs.RemoveAt(i);
                continue;
            }

            dragonFireballs[i] = ball;

            // Flame sprite (kind 0): tighter hot muzzle → wide blossom (a cone), velocity-stretched into a tongue.
            // Per-ball size jitter (from the seed) so the jet isn't a string of identical blobs.
            float life = ball.Age / DragonFireTtlSeconds;
            float sizeJitter = 0.65f + (0.75f * Frac(ball.Seed * 1.7f)); // 0.65–1.4× — a wider spread kills the uniform look
            float radius = DragonFireRadiusMeters * sizeJitter * (0.58f + (1.9f * life)) * (1f - (0.2f * life * life)); // fatter at birth — neighbours must OVERLAP to fuse
            float attack = MathF.Min(1f, ball.Age * 30f);
            float s = Math.Clamp((life - 0.6f) / 0.4f, 0f, 1f);
            float intensity = attack * (1f - (s * s * (3f - (2f * s))));

            var ballWorld = new Vector3(ball.XY.X, ball.XY.Y, ball.Elevation * exaggeration);
            float distToCam = Vector3.Distance(Camera.Position, ballWorld);
            if (distToCam < 8f)
            {
                intensity *= distToCam / 8f; // only extreme point-blank is dimmed (don't gut the stream at the mouth)
            }

            var ballVel = new Vector3(ball.VelocityXY.X, ball.VelocityXY.Y, ball.VelocityZ);
            dragonFireSprites.Add(new MapaTur.App.Services.Terrain3DGlRenderer.FireballSprite(
                ballWorld, radius, intensity, ball.Seed, (float)FireKind.Flame, ballVel));
        }

        while (dragonFireballs.Count > DragonFireMaxBalls)
        {
            dragonFireballs.RemoveAt(0); // cap: drop the oldest
        }

        StepFireParticles(dt, exaggeration);
        PushFireLights((float)dragonClock.Elapsed.TotalSeconds); // B2: fire → ≤8 point lights for the shaders
    }

    // Spawns a staged explosion at pos (real metres): a sub-frame FLASH, an expanding SHOCK ring, rolling
    // fireball PUFFS, and arcing EMBERS — power scales stream(1.1) / dragon-kill(2.2). Pure additive kinds; the
    // smoke + ground scorch layers are Phase 2b.
    private void SpawnFireBurst(Vector3 pos, float power)
    {
        dragonFireParticles.Add(new FireParticle
        {
            Pos = pos, Kind = FireKind.Flash, Life = 0.10f, Size0 = 2f * power, Size1 = 9f * power,
            Seed = (dragonBurstCounter++ * 0.731f) % 10f,
        });
        dragonFireParticles.Add(new FireParticle
        {
            Pos = pos, Kind = FireKind.Shock, Life = 0.30f, Size0 = 0.5f * power, Size1 = 15f * power,
            Seed = (dragonBurstCounter++ * 0.731f) % 10f,
        });
        for (int k = 0; k < 2; k++)
        {
            float ang = k * 2.4f;
            var off = new Vector3(MathF.Cos(ang), MathF.Sin(ang), 0f) * (1.5f * power);
            dragonFireParticles.Add(new FireParticle
            {
                Pos = pos + off, Vel = new Vector3(0f, 0f, 4f), Kind = FireKind.Puff, Life = 0.55f,
                Size0 = 3f * power, Size1 = 9f * power, Seed = (dragonBurstCounter++ * 0.731f) % 10f,
            });
        }

        int embers = (int)MathF.Round(9f * power);
        for (int j = 0; j < embers; j++)
        {
            float gi = j + 0.5f;
            float phi = gi * 2.399963f;                        // golden angle
            float y = 1f - ((gi / embers) * 0.85f);            // biased up (hemisphere)
            float rad = MathF.Sqrt(MathF.Max(0f, 1f - (y * y)));
            var dir = new Vector3(MathF.Cos(phi) * rad, MathF.Sin(phi) * rad, y);
            float spd = 17f + (17f * Frac(gi * 0.618f));
            dragonFireParticles.Add(new FireParticle
            {
                Pos = pos, Vel = dir * spd, Kind = FireKind.Ember, Life = 0.4f + (0.5f * Frac(gi * 0.31f)),
                Size0 = 0.7f, Size1 = 0.25f, Seed = (dragonBurstCounter++ * 0.731f) % 10f,
            });
        }

        // Rising SMOKE puffs that outlast the flame (straight-alpha, second pass).
        int smoke = (int)MathF.Round(4f * power);
        for (int m = 0; m < smoke; m++)
        {
            float sa = m * 1.7f;
            var soff = new Vector3(MathF.Cos(sa), MathF.Sin(sa), 0f) * (1.2f * power);
            dragonFireParticles.Add(new FireParticle
            {
                Pos = pos + soff,
                Vel = new Vector3((Frac(sa) * 2f) - 1f, (Frac(sa * 1.7f) * 2f) - 1f, 3.5f),
                Kind = FireKind.Smoke, Life = 1.6f + (0.8f * Frac(sa)),
                Size0 = 4f * power, Size1 = 13f * power, Seed = (dragonBurstCounter++ * 0.731f) % 10f,
            });
        }
    }

    // A stream ball that burns out MID-AIR (TTL, no impact) leaves soot behind: the flame tongue dies, the
    // unburnt carbon keeps gliding on a fraction of the jet momentum, stalls and rises (the smoke pass's
    // per-axis drag does the stall). This is what makes a sustained breath develop a drifting plume past the
    // tip of the jet — impact/quench smoke is SpawnFireBurst / SpawnSteamBurst's job.
    private void SpawnBurnoutSmoke(DragonFireball ball)
    {
        int n = Frac(ball.Seed * 2.6f) > 0.6f ? 2 : 1; // every ~3rd tongue sheds a double puff
        for (int m = 0; m < n; m++)
        {
            float sa = (ball.Seed * 7.3f) + (m * 1.7f);
            var jitter = new Vector3((Frac(sa) * 3f) - 1.5f, (Frac(sa * 1.9f) * 3f) - 1.5f, Frac(sa * 3.1f) * 1.5f);
            dragonFireParticles.Add(new FireParticle
            {
                Pos = new Vector3(ball.XY.X, ball.XY.Y, ball.Elevation) + jitter,
                Vel = (new Vector3(ball.VelocityXY.X, ball.VelocityXY.Y, ball.VelocityZ) * 0.22f)
                    + new Vector3(0f, 0f, 3.2f),
                Kind = FireKind.Smoke,
                Life = 1.5f + (0.9f * Frac(sa * 1.3f)),
                Size0 = 5f,
                Size1 = 15f,
                Seed = (dragonBurstCounter++ * 0.731f) % 10f,
            });
        }
    }

    // A fireball quenched on water: a brief hiss FLASH + a fat cloud of rising white STEAM (routed to the alpha
    // pass; billows bigger and lasts longer than soot smoke).
    private void SpawnSteamBurst(Vector3 pos, float power)
    {
        dragonFireParticles.Add(new FireParticle
        {
            Pos = pos, Kind = FireKind.Flash, Life = 0.08f, Size0 = 2f * power, Size1 = 6f * power,
            Seed = (dragonBurstCounter++ * 0.731f) % 10f,
        });
        int puffs = (int)MathF.Round(7f * power);
        for (int m = 0; m < puffs; m++)
        {
            float sa = m * 1.3f;
            var soff = new Vector3(MathF.Cos(sa), MathF.Sin(sa), 0f) * (2f * power);
            dragonFireParticles.Add(new FireParticle
            {
                Pos = pos + soff,
                Vel = new Vector3(((Frac(sa) * 2f) - 1f) * 2.5f, ((Frac(sa * 1.7f) * 2f) - 1f) * 2.5f, 7f), // rises fast
                Kind = FireKind.Steam, Life = 1.8f + (1.0f * Frac(sa)),
                Size0 = 3f * power, Size1 = 17f * power, Seed = (dragonBurstCounter++ * 0.731f) % 10f,
            });
        }
    }

    // In-frame lake outlines converted to world XY, cached once per fire tick (dragonLakeCache) so a flying ball
    // can point-in-polygon test cheaply for a water hit.
    private readonly List<(System.Numerics.Vector2[] Poly, float Elev)> dragonLakeCache = [];

    private object? dragonLakeCacheFrame; // the WorldFrame the cache was built against (lake outlines are static per frame)

    private void RebuildLakeCache()
    {
        if (WorldFrame is not { } frame)
        {
            dragonLakeCache.Clear();
            dragonLakeCacheFrame = null;
            return;
        }

        if (ReferenceEquals(dragonLakeCacheFrame, frame))
        {
            return; // hot path: this used to re-walk every lake outline EVERY tick while any fireball flew
        }

        dragonLakeCacheFrame = frame;
        dragonLakeCache.Clear();

        foreach (MapaTur.Application.Terrain.MountainLake lake in MapaTur.Application.Terrain.MountainLakeData.WithinBounds(frame.Bounds))
        {
            int n = lake.Outline.Count;
            if (n < 3)
            {
                continue;
            }

            var poly = new System.Numerics.Vector2[n];
            for (int k = 0; k < n; k++)
            {
                Vector3 wp = frame.GeoToWorld(lake.Outline[k], 0f);
                poly[k] = new System.Numerics.Vector2(wp.X, wp.Y);
            }

            dragonLakeCache.Add((poly, (float)lake.ElevationMeters));
        }
    }

    // Water elevation (real metres) if worldXY is inside a cached lake outline, else null — a fireball reaching
    // that height over the lake hisses into steam instead of a fire burst.
    private float? TryLakeWaterElevation(System.Numerics.Vector2 p)
    {
        foreach ((System.Numerics.Vector2[] poly, float elev) in dragonLakeCache)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if (((poly[i].Y > p.Y) != (poly[j].Y > p.Y))
                    && (p.X < (((poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y)) + poly[i].X)))
                {
                    inside = !inside;
                }
            }

            if (inside)
            {
                return elev;
            }
        }

        return null;
    }

    // Advances the explosion particle pool and builds its render sprites (kind-tagged for the shader).
    private void StepFireParticles(float dt, float exaggeration)
    {
        for (int i = dragonFireParticles.Count - 1; i >= 0; i--)
        {
            FireParticle pt = dragonFireParticles[i];
            pt.Age += dt;
            if (pt.Age >= pt.Life)
            {
                dragonFireParticles.RemoveAt(i);
                continue;
            }

            switch (pt.Kind)
            {
                case FireKind.Ember:
                    pt.Vel.Z -= 22f * dt;                       // gravity
                    pt.Vel *= MathF.Max(0f, 1f - (1.5f * dt));  // drag
                    pt.Pos += pt.Vel * dt;
                    break;
                case FireKind.Puff:
                    pt.Pos += pt.Vel * dt;
                    break;
                case FireKind.Smoke:
                case FireKind.Steam:
                    pt.Vel.Z *= MathF.Max(0f, 1f - (0.6f * dt)); // rise then stall
                    pt.Vel.X *= MathF.Max(0f, 1f - (0.5f * dt));
                    pt.Vel.Y *= MathF.Max(0f, 1f - (0.5f * dt));
                    // A3 macro-swirl: a gentle analytic curl (deterministic per position/age/seed) so the
                    // plume corkscrews as it rises instead of drifting straight — matches the in-shader swirl.
                    pt.Vel.X += MathF.Cos(((pt.Pos.X - pt.Pos.Y) * 0.05f) + (pt.Age * 1.1f) + pt.Seed) * 0.8f * dt;
                    pt.Vel.Y += MathF.Sin(((pt.Pos.X + pt.Pos.Y) * 0.05f) + (pt.Age * 1.2f) + pt.Seed) * 0.8f * dt;
                    pt.Pos += pt.Vel * dt;
                    break;
                default:
                    break; // Flash / Shock: fixed in place
            }

            dragonFireParticles[i] = pt;

            float u = pt.Age / pt.Life;
            var world = new Vector3(pt.Pos.X, pt.Pos.Y, pt.Pos.Z * exaggeration);
            float distToCam = Vector3.Distance(Camera.Position, world);
            float camClamp = distToCam < 8f ? distToCam / 8f : 1f;

            if (pt.Kind is FireKind.Smoke or FireKind.Steam)
            {
                // Smoke/steam grow LINEARLY over their long life and fade in then out; routed to the alpha pass.
                float radiusS = pt.Size0 + ((pt.Size1 - pt.Size0) * u);
                float intensityS = MathF.Min(1f, u * 6f) * (1f - u) * camClamp;
                dragonFireSmokeSprites.Add(new MapaTur.App.Services.Terrain3DGlRenderer.FireballSprite(
                    world, radiusS, intensityS, pt.Seed, (float)pt.Kind, Vector3.Zero));
                continue;
            }

            float ease = 1f - ((1f - u) * (1f - u) * (1f - u)); // ease-out-cubic expansion
            float radius = pt.Size0 + ((pt.Size1 - pt.Size0) * ease);
            float intensity = pt.Kind switch
            {
                FireKind.Flash => 1f - u,
                FireKind.Shock => (1f - u) * (1f - u),
                FireKind.Ember => 1f - (u * u),
                FireKind.Puff => MathF.Min(1f, pt.Age * 15f) * (1f - (u * u)),
                _ => 1f - u,
            };
            intensity *= camClamp;

            Vector3 vel = pt.Kind == FireKind.Ember ? pt.Vel : Vector3.Zero;
            dragonFireSprites.Add(new MapaTur.App.Services.Terrain3DGlRenderer.FireballSprite(
                world, radius, intensity, pt.Seed, (float)pt.Kind, vel));
        }

        while (dragonFireParticles.Count > 256)
        {
            dragonFireParticles.RemoveAt(0);
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
            glRenderer.ThrottleReflection = dragonActive || walkActive; // continuous modes: reflection every 2nd frame
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
            // Push the 3rd-person walk avatar into the same GL pass (visible only while walking AND the model has
            // streamed in). Walk and dragon are mutually exclusive, so at most one of these is ever visible.
            bool humanoid3DVisible = walkActive && humanoidModel3D is not null;
            glRenderer.SetHumanoid(
                gripClimb is { IsActive: true, HasPose: true, ActiveModel: { } climberModel } ? climberModel : humanoidModel3D,
                humanoidWorldMatrix, humanoidNormalMatrix, dragonLight, humanoid3DVisible);
            glRenderer.SetArrows(
                walkActive ? arrowModel3D : null,
                walkActive ? arrowWorlds : null,
                walkActive ? arrowNormals : null,
                dragonLight);
            glRenderer.SetFireballs(dragonActive && dragonFireSprites.Count > 0 ? dragonFireSprites : null);
            glRenderer.SetFireSmoke(dragonActive && dragonFireSmokeSprites.Count > 0 ? dragonFireSmokeSprites : null);
            debugMarkersRender.Clear();
            // The calibration grid is for placing route lines — it is pure clutter during an actual climb
            // session (the climb draws its own hold markers), so hide it while gripClimb is active.
            if (climbCalibMarkers.Count > 0 && !gripClimb.IsActive)
            {
                debugMarkersRender.AddRange(climbCalibMarkers);
            }

            if (dragonActive && dragonDebugMarkers.Count > 0)
            {
                debugMarkersRender.AddRange(dragonDebugMarkers);
            }

            glRenderer.SetDebugMarkers(debugMarkersRender.Count > 0 ? debugMarkersRender : null);
            // Climb hold dots go through the DEPTH-TESTED pass so the climber draws over them and the routes
            // read under them (drawn between the route lines and the climber).
            ApplyCameraPresetFromEnv(); // KONTRAKT-ORTO §4: one-shot anchor view once the frame exists
            glRenderer.SetClimbHoldMarkers(walkActive && climbMarkers.Count > 0 ? climbMarkers : null);
            // Sculpted rock skin around the climber: buffer lives in climb space, the shader applies the
            // Pion exaggeration — so a slider move needs no geometry rebuild.
            glRenderer.SetClimbRockSkin(
                walkActive ? gripClimb.RockSkin : null, WorldFrame?.VerticalExaggeration ?? 1f);
            EnsureClimbingRoutes();
            // Topo passage lines render through the same climb-gear ribbon pass as the rope/quickdraws —
            // always visible (not only while walking), with the session gear appended on top of them.
            renderGearRibbons.Clear();
            if (climbRouteRibbons.Count > 0 && ShowClimbingRoutes)
            {
                renderGearRibbons.AddRange(climbRouteRibbons);
            }

            if (walkActive && climbGearRibbons.Count > 0)
            {
                renderGearRibbons.AddRange(climbGearRibbons);
            }

            glRenderer.SetClimbGear(
                renderGearRibbons.Count > 0 ? renderGearRibbons : null,
                walkActive && climbGearRings.Count > 0 ? climbGearRings : null);
            UpdateAiFlock(); // advance + pose the flock in step with this frame (no separate timer — WinUI-safe)
            glRenderer.SetAiDragons(ShowAiDragons && aiFlockInstances.Count > 0 ? aiFlockInstances : null);
            double dbgPreRenderMs = dbgSwapPaintActive ? dbgPaintWatch.Elapsed.TotalMilliseconds : 0;
            long perfRenderT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            uint terrainTextureId = glRenderer.Render(width, height, tiles, Camera, Trails, Raster, Route, Roads, EffectiveAtmosphere, forest, DetailElevation, ShowNightSky ? DateOnly.FromDateTime(DateTime.Now) : null, ExposedRoutes, ShowSauronTower, ShowEagles, AtmosphereEffectsEnabled, OffTrailTracks);
            if (dragonActive)
            {
                dragonPaintRenderMax = Math.Max(dragonPaintRenderMax, PerfMs(perfRenderT0, System.Diagnostics.Stopwatch.GetTimestamp()));
            }
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
            // final output FBO (post-processed, always LDR) — BEFORE Skia touches GL state below. Reading
            // the GL output (not a Skia surface snapshot) avoids the back-buffer staleness that blacked
            // out every frame after the first.
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
        catch (Exception ex)
        {
            // One failure → drop to the proven Skia path for the rest of the session. Logged loudly: a lazy
            // shader compile/link failure (e.g. the fire program on first breath) lands here, and a silent
            // catch made that indistinguishable from "3D just went flat".
            Serilog.Log.Warning(ex, "[GL3D] GL render failed — dropping to the Skia path for this session");
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
        TryLoadOrthoDetailPoc(renderer, paths);
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

    // The accepted 5 cm HighResolution Morskie-Oko demo: det05 + det25 mosaics → the sharp hut (yesterday's
    // state). Decodes off the UI thread; absent files = silent no-op.
    // Wire the det25 (25 cm) streaming: an OrthoTileDecodeCache in front of the SkiaSharp WebP decode of the
    // on-disk tile pyramid (dem/ortho-detail/tatry/det25/<i>/<j>.webp), the OrthoDetailCellComposer that assembles
    // cells from it, and the ring policy. The renderer then composes/uploads cells off-thread and binds them
    // per-draw. baseFill is null (holes stay transparent → the shader resolves them against the base ortho).
    private void SetupOrthoDetailStreaming(Services.Terrain3DGlRenderer renderer, IReadOnlyList<string>? basePaths)
    {
        string? demDir = basePaths is { Count: > 0 } ? System.IO.Path.GetDirectoryName(basePaths[0]) : null;
        string? tilesDir = demDir is null ? null : System.IO.Path.Combine(demDir, "ortho-detail", "tatry", "det25");
        if (tilesDir is null || !System.IO.Directory.Exists(tilesDir))
        {
            string alt = System.IO.Path.Combine(
                Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "dem", "ortho-detail", "tatry", "det25");
            if (System.IO.Directory.Exists(alt))
            {
                tilesDir = alt;
            }
        }

        if (tilesDir is null || !System.IO.Directory.Exists(tilesDir))
        {
            Serilog.Log.Information("[OrthoDetailStream] det25 tiles dir not found ({Dir}) — streaming off", tilesDir);
            return;
        }

        string dir = tilesDir;
        var grid = new MapaTur.Application.Terrain.OrthoDetailGrid();
        var cache = new MapaTur.Application.Terrain.OrthoTileDecodeCache(
            (i, j) =>
            {
                string p = System.IO.Path.Combine(
                    dir, i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{j}.webp");
                if (DecodeOrtho(p) is not { Width: 512, Height: 512 } t)
                {
                    return null;
                }

                MapaTur.Application.Terrain.OrthoNodata.ZeroAlphaOnBlack(t.Rgba); // nodata GUGiK → punch-through
                return t.Rgba;
            },
            // 2 GB desktop / 384 MB phone (2026-07-20): det25 cell = 64 decoded 512² tiles ≈ 64 MB; 2 GB holds
            // ~30 cells so the coarse ring never re-decodes on a pan (same stutter cause as det05, smaller scale).
            maxBytes: OperatingSystem.IsWindows() ? 2048L << 20 : 384L << 20);
        var composer = new MapaTur.Application.Terrain.OrthoDetailCellComposer(grid, cache.Get, baseFill: null);
        // det1m (krok 3): rezydentny tier 1 m z prebake'u — ładowany raz na starcie, A/B klawiszem '7'.
        renderer.Det1mPackDir = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(dir) ?? dir, "opk", "det1m");
        // det25 (krok 6): strony .opk zamiast compose — pierwsza wizyta czyta prebake, nie dekoduje WebP.
        renderer.Det25OpkDir = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(dir) ?? dir, "opk", "det25");
        // TIER REBALANCE (2026-07-23): desktop ring 1500 → 5000 m so the 28-cell det25 midground actually has
        // candidates to fill (a 1500 m ring holds ~7 cells — the raised cap was starved at the source).
        var policy = new MapaTur.Application.Terrain.OrthoDetailResidencyPolicy(
            grid, ringRadiusMeters: OperatingSystem.IsWindows() ? 5000.0 : 1500.0,
            fastMotionSpeedMps: 25.0, prefetchLeadMeters: 400.0);
        renderer.Det25GpuCacheDir = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(dir) ?? dir, "gpu-cache", System.IO.Path.GetFileName(dir) + "-25");
        renderer.SetOrthoDetailStreaming(grid, policy, composer, cache);
        Serilog.Log.Information("[OrthoDetailStream] det25 streaming wired from {Dir}", dir);
    }

    // Wire the det05 (5 cm) SECOND streamed level on unit 11, coverage-gated: a tile decode cache over the det05
    // pyramid, the composer, and the covered-cell set (_coverage.txt = cells with ≥95% source, so 5 cm only
    // streams where the tiles exist — the 07-14 map showed 5 cm is a partial strip). Behind MAPATUR_DET05_STREAM=1.
    private bool SetupDet05Streaming(Services.Terrain3DGlRenderer renderer, IReadOnlyList<string>? basePaths)
    {
        string? demDir = basePaths is { Count: > 0 } ? System.IO.Path.GetDirectoryName(basePaths[0]) : null;
        string? tilesDir = demDir is null ? null : System.IO.Path.Combine(demDir, "ortho-detail", "tatry", "det05");
        if (tilesDir is null || !System.IO.Directory.Exists(tilesDir))
        {
            string alt = System.IO.Path.Combine(
                Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "dem", "ortho-detail", "tatry", "det05");
            if (System.IO.Directory.Exists(alt))
            {
                tilesDir = alt;
            }
        }

        if (tilesDir is null || !System.IO.Directory.Exists(tilesDir))
        {
            Serilog.Log.Information("[OrthoDetail05] det05 tiles dir not found — falling back to the static 5 cm showcase");
            return false;
        }

        string dir = tilesDir;
        var coveredKeys = new HashSet<int>();
        string covFile = System.IO.Path.Combine(dir, "_coverage.txt");
        if (System.IO.File.Exists(covFile))
        {
            foreach (string line in System.IO.File.ReadLines(covFile))
            {
                if (int.TryParse(line.Trim(), out int k))
                {
                    coveredKeys.Add(k);
                }
            }
        }

        if (coveredKeys.Count == 0)
        {
            Serilog.Log.Information(
                "[OrthoDetail05] no coverage cells in {File} — falling back to the static 5 cm showcase", covFile);
            return false;
        }

        var grid = new MapaTur.Application.Terrain.OrthoDetailGrid(resMeters: 0.05, coverageTiles: 16, pitchTiles: 6);
        // DESHADOW PREVIEW (env-gated, 2026-07-21): with MAPATUR_DET05_DESHADOW_PREVIEW=1, serve the corrected
        // tile from a sibling deshadow dir when it exists, else fall back to the original det05 tile.
        //   MAPATUR_DET05_DESHADOW_DIR selects the override dir (default "det05-deshadow" = Rysy PoC rollback;
        //   set to "det05-deshadow-mo-v2" for the whole-cirque V2 preview). Fallback is ALWAYS raw det05.
        // Coverage (_coverage.txt) stays the ORIGINAL det05's; sources are untouched; env-unset = identical
        // behaviour. VIEW in OrthoDetailColorMode=0 (key 9): these tiles are de-shadowed ON DISK — mode 1 (the
        // shader de-blue) would double-correct. This is a PREVIEW overlay, NOT yet a production shader-bypass layer.
        string? dsDir = Environment.GetEnvironmentVariable("MAPATUR_DET05_DESHADOW_PREVIEW") == "1"
            ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(dir) ?? dir,
                Environment.GetEnvironmentVariable("MAPATUR_DET05_DESHADOW_DIR") ?? "det05-deshadow")
            : null;
        if (dsDir is not null)
        {
            // These tiles are de-shadowed ON DISK. H3 (2026-07-23): the old global RAW (colour mode 0) also
            // stripped the shader de-blue from det25/base — the whole DISTANCE rendered with the raw blue cast
            // ("pół Morskiego Oka na niebiesko"). Now the split is per-layer: the streamed det05 array renders
            // RAW (no double-correction on the V2-baked cells) while det25/base/mosaic keep the mode-1 de-blue.
            // User can still toggle to compare (keys 9/1/F2/F1).
            renderer.OrthoDetailColorMode = 1;
            renderer.Det05ArrayRawColor = true;
            renderer.BakedShadowComp = 0f;
            Serilog.Log.Information(
                "[OrthoDetail05] DESHADOW PREVIEW ON — overlay {DsDir} over fallback {Dir}; per-layer colour: det05 array RAW, det25/base de-blue (mode 1)",
                dsDir, dir);
        }
        int dsHits = 0, dsFallback = 0;
        var cache = new MapaTur.Application.Terrain.OrthoTileDecodeCache(
            (i, j) =>
            {
                if (dsDir is not null)
                {
                    string pd = System.IO.Path.Combine(
                        dsDir, i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{j}.webp");
                    if (System.IO.File.Exists(pd)
                        && DecodeOrtho(pd) is { Width: 512, Height: 512 } td)
                    {
                        int served = System.Threading.Interlocked.Increment(ref dsHits);
                        if ((served + dsFallback) % 256 == 0)
                        {
                            Serilog.Log.Information(
                                "[OrthoDetail05] deshadow-preview served: {Hits} from det05-deshadow, {Fallback} fallback det05",
                                served, dsFallback);
                        }
                        MapaTur.Application.Terrain.OrthoNodata.ZeroAlphaOnBlack(td.Rgba); // nodata GUGiK → punch-through
                        return td.Rgba;
                    }
                }
                string p = System.IO.Path.Combine(
                    dir, i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{j}.webp");
                if (dsDir is not null) { System.Threading.Interlocked.Increment(ref dsFallback); }
                if (DecodeOrtho(p) is not { Width: 512, Height: 512 } t)
                {
                    return null;
                }

                MapaTur.Application.Terrain.OrthoNodata.ZeroAlphaOnBlack(t.Rgba); // nodata GUGiK → punch-through
                return t.Rgba;
            },
            // 16 GB desktop (2026-07-20, "mając 64 GB RAM"): a det05 cell = 256 decoded 512² tiles ≈ 268 MB;
            // 16 GB holds ~60 cells = the WHOLE Morskie-Oko cirque decoded. Root cause of the pan stutter was
            // this cache at 2 GB (~7 cells): every camera revisit re-DECODED 256 WebP tiles (~1.4 s/cell, 767
            // re-composes in one session). Cached, a revisit re-composes from RAM (memcpy, ~ms) — no re-decode.
            // Phone stays tight (RAM-constrained). This is system RAM, not the 4.29 GB per-GPU-resource limit.
            maxBytes: OperatingSystem.IsWindows() ? 16384L << 20 : 512L << 20);
        var composer = new MapaTur.Application.Terrain.OrthoDetailCellComposer(grid, cache.Get, baseFill: null);
        bool Coverage(int ci, int cj) => coveredKeys.Contains(grid.CellKey(ci, cj));
        // BC1 GPU-cell cache (2026-07-23, ZASADA 11): composed+encoded cells persist next to the tile data —
        // every revisit is a ~15 ms read instead of a 3–5 s WebP decode storm. Keyed by the SOURCE dir name,
        // so the deshadow-preview overlay and raw det05 never share cached pixels.
        renderer.Det05GpuCacheDir = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(dir) ?? dir, "gpu-cache",
            dsDir is not null ? System.IO.Path.GetFileName(dsDir) + "-over-det05" : System.IO.Path.GetFileName(dir));
        // det05 (krok 6): strony .opk zamiast compose — jak det25.
        renderer.Det05OpkDir = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(dir) ?? dir, "opk", "det05");
        renderer.SetOrthoDetail05Streaming(grid, composer, cache, Coverage);
        Serilog.Log.Information("[OrthoDetail05] det05 streaming wired from {Dir} ({N} covered cells)", dir, coveredKeys.Count);
        return true;
    }

    private void LoadOrthoDetailMosaics(Services.Terrain3DGlRenderer renderer, IReadOnlyList<string>? basePaths)
    {
        var candidates = new List<string>();
        if (basePaths is { Count: > 0 })
        {
            string? d = System.IO.Path.GetDirectoryName(basePaths[0]);
            if (!string.IsNullOrEmpty(d))
            {
                candidates.Add(System.IO.Path.Combine(d, "ortho-detail", "morskie-oko"));
                candidates.Add(System.IO.Path.Combine(d, "dem", "ortho-detail", "morskie-oko"));
            }
        }
        candidates.Add(System.IO.Path.Combine(Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "dem", "ortho-detail", "morskie-oko"));

        string? dir = candidates.FirstOrDefault(c => System.IO.File.Exists(System.IO.Path.Combine(c, "mosaics.json")));
        if (dir is null)
        {
            Serilog.Log.Information("[OrthoDetailPoc] mosaics.json not found (looked in {Dirs})", string.Join(" ; ", candidates));
            return;
        }

        string mosaicsDir = dir;
        Task.Run(() =>
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(
                    System.IO.File.ReadAllText(System.IO.Path.Combine(mosaicsDir, "mosaics.json")));
                System.Text.Json.JsonElement levels = doc.RootElement.GetProperty("levels");
                System.Text.Json.JsonElement d05 = levels.GetProperty("det05");

                // det05 ONLY: the 5 cm Morskie-Oko showcase stays the static mosaic on unit 11. det25 is no longer
                // a static mosaic here — it is STREAMED per-draw over the whole massif (SetupOrthoDetailStreaming).
                var t05 = DecodeOrtho(System.IO.Path.Combine(mosaicsDir, d05.GetProperty("file").GetString() ?? "det05_mosaic.png"));
                if (t05 is not { } m05)
                {
                    Serilog.Log.Warning("[OrthoDetailPoc] det05 mosaic decode failed");
                    return;
                }

                static MapaTur.Domain.Geography.GeoPoint Sw(System.Text.Json.JsonElement e) =>
                    new(e.GetProperty("south").GetDouble(), e.GetProperty("west").GetDouble());
                static MapaTur.Domain.Geography.GeoPoint Ne(System.Text.Json.JsonElement e) =>
                    new(e.GetProperty("north").GetDouble(), e.GetProperty("east").GetDouble());

                renderer.SetOrthoDetailDet05Mosaic(m05.Rgba, m05.Width, m05.Height, Sw(d05), Ne(d05));
                Serilog.Log.Information("[OrthoDetailPoc] loaded 5cm Morskie-Oko det05 {W05}x{H05} from {Dir}",
                    m05.Width, m05.Height, mosaicsDir);
                MainThread.BeginInvokeOnMainThread(() => Canvas.InvalidateSurface());
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[OrthoDetailPoc] load failed");
            }
        });
    }

    // R3 vertical slice — feature flag MAPATUR_ORTHO_SLICE=1, DEFAULT OFF. Composes TWO adjacent detail cells
    // (53,29)+(54,29) from the fetched det25 tiles via the REAL OrthoDetailGrid/OrthoDetailAssembler and hands
    // them to the renderer's detail-overlay path — so we can judge the detail↔detail and detail↔base transitions,
    // UV/mip/filtering, upload, and the raw-vs-de-blued colour variants in-app before any full streaming wiring.
    // Off the UI thread; a normal run (no flag) loads no detail at all.
    private bool orthoDetailPocLoaded;
    private void TryLoadOrthoDetailPoc(Services.Terrain3DGlRenderer renderer, IReadOnlyList<string>? basePaths)
    {
        if (orthoDetailPocLoaded)
        {
            return;
        }

        orthoDetailPocLoaded = true; // one attempt regardless of outcome
        if (Environment.GetEnvironmentVariable("MAPATUR_ORTHO_SLICE") != "1")
        {
            // DEFAULT: stream det25 (25 cm) cells over the whole massif on unit 10 (SetupOrthoDetailStreaming),
            // UNDER the accepted static 5 cm Morskie-Oko det05 showcase on unit 11 (finest-wins, unit 11 untouched).
            // MAPATUR_ORTHO_SLICE=1 keeps the old static 2-cell debug slice instead.
            SetupOrthoDetailStreaming(renderer, basePaths);
            // DEFAULT (2026-07-18, user OK after A/B): stream 5 cm det05 over the whole fetched area on
            // unit 11 (coverage-gated). Verified it fully covers the Morskie-Oko showcase cells, so nothing
            // regresses — MO stays 5 cm and the sharpness now extends across the fetched Tatra strip. Falls
            // back to the static MO mosaic where the streamed tiles/coverage aren't present (fresh installs /
            // mobile without the ~15 GB det05 sync). MAPATUR_DET05_STREAM=0 forces the static showcase.
            bool wantStream = Environment.GetEnvironmentVariable("MAPATUR_DET05_STREAM") != "0";
            if (!wantStream || !SetupDet05Streaming(renderer, basePaths))
            {
                LoadOrthoDetailMosaics(renderer, basePaths); // accepted static 5 cm Morskie-Oko showcase (fallback)
            }

            return;
        }

        string? demDir = basePaths is { Count: > 0 } ? System.IO.Path.GetDirectoryName(basePaths[0]) : null;
        string? tilesDir = demDir is null
            ? null : System.IO.Path.Combine(demDir, "ortho-detail", "tatry", "det25");
        if (tilesDir is null || !System.IO.Directory.Exists(tilesDir))
        {
            Serilog.Log.Warning("[OrthoDetailSlice] det25 tiles dir not found: {Dir}", tilesDir);
            return;
        }

        Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var grid = new MapaTur.Application.Terrain.OrthoDetailGrid();
                var asm = new MapaTur.Application.Terrain.OrthoDetailAssembler(grid);
                int decoded = 0, missing = 0;

                // Cell pair (default a TEXTURED moraine/rock pair just south of Morskie Oko — NOT the smooth
                // lake). Override with MAPATUR_ORTHO_SLICE_CELLS="ci1,cj1,ci2,cj2" to re-point without a rebuild.
                int a0 = 53, a1 = 30, b0 = 54, b1 = 30;
                string? cellsSpec = Environment.GetEnvironmentVariable("MAPATUR_ORTHO_SLICE_CELLS");
                if (cellsSpec is not null)
                {
                    string[] parts = cellsSpec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 4
                        && int.TryParse(parts[0], out int p0) && int.TryParse(parts[1], out int p1)
                        && int.TryParse(parts[2], out int p2) && int.TryParse(parts[3], out int p3))
                    {
                        (a0, a1, b0, b1) = (p0, p1, p2, p3);
                    }
                }

                byte[]? Provide(int i, int j)
                {
                    string p = System.IO.Path.Combine(
                        tilesDir, i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{j}.webp");
                    if (DecodeOrtho(p) is { } t)
                    {
                        System.Threading.Interlocked.Increment(ref decoded);
                        return t.Rgba;
                    }

                    System.Threading.Interlocked.Increment(ref missing);
                    return null;
                }

                byte[] ca = asm.Compose(a0, a1, Provide, null);
                byte[] cb = asm.Compose(b0, b1, Provide, null);
                MapaTur.Domain.Geography.MapBounds ba = grid.CellBounds(a0, a1);
                MapaTur.Domain.Geography.MapBounds bb = grid.CellBounds(b0, b1);
                renderer.SetOrthoDetailPoc(
                    ca, grid.CellPx, grid.CellPx, ba.SouthWest, ba.NorthEast,
                    cb, grid.CellPx, grid.CellPx, bb.SouthWest, bb.NorthEast);
                renderer.OrthoDetailEnabled = true;
                sw.Stop();
                long vramMb = (2L * grid.CellPx * grid.CellPx * 4 * 4 / 3) / (1024 * 1024);
                Serilog.Log.Information(
                    "[OrthoDetailSlice] cells ({A0},{A1})+({B0},{B1}) composed in {Ms}ms | tiles decoded={D} missing={M} " +
                    "| geo W{W:F4} S{S:F4} E{E:F4} N{N:F4} | cellPx={Px} resident=2 vram~{Vram}MB | keys: 0=on/off 9=colour 8=bounds",
                    a0, a1, b0, b1, sw.ElapsedMilliseconds, decoded, missing,
                    ba.SouthWest.Longitude, ba.SouthWest.Latitude, bb.NorthEast.Longitude, ba.NorthEast.Latitude,
                    grid.CellPx, vramMb);
                MainThread.BeginInvokeOnMainThread(() => Canvas.InvalidateSurface());
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[OrthoDetailSlice] failed");
            }
        });
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