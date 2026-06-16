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
    private const float PoiMarkerLiftMeters = 25f;
    private const float PeakMarkerLiftMeters = 40f;

    private readonly Marker3DOverlayProjector<ClimbingArea, ProjectedClimbingArea> climbingProjector =
        new(
            (areas, raster, mesh, lift) => Climbing3DProjection.ToWorld(areas, raster!, mesh, lift),
            (source, screen) => new ProjectedClimbingArea(source, screen));

    private readonly Marker3DOverlayProjector<MountainPoi, ProjectedPoi> poiProjector =
        new(
            (pois, raster, mesh, lift) => Poi3DProjection.ToWorld(pois, raster!, mesh, lift),
            (source, screen) => new ProjectedPoi(source, screen));

    private readonly Marker3DOverlayProjector<TerrainPeak, ProjectedPeak> peakProjector =
        new(
            (peaks, _, mesh, lift) => Peak3DProjection.ToWorld(peaks, mesh, lift),
            (source, screen) => new ProjectedPeak(source, screen));

    // GPS marker: prefer the OS-reported altitude when present (UserLocation3DProjection takes care
    // of that), otherwise fall back to a DEM lookup. Lift higher than POI/climbing so the dot
    // visibly hovers above the ground on flat sections instead of merging with the mesh.
    private const float UserLocationMarkerLiftMeters = 20f;
    private readonly Marker3DOverlayProjector<UserLocation, ProjectedUserLocation> userLocationProjector =
        new(
            (fixes, raster, mesh, lift) => UserLocation3DProjection.ToWorld(fixes, raster, mesh, lift),
            (source, screen) => new ProjectedUserLocation(source, screen));
    // Reused one-element buffer so a fix update doesn't allocate a fresh list per frame; the
    // projector compares by reference so we only swap the contained UserLocation when it changes.
    private readonly UserLocation[] userLocationBuffer = new UserLocation[1];

    // Cached projected markers + surface size from the last paint, so a screen tap can be mapped back
    // to the marker under it (the projectors own the per-frame buffers; we only keep references).
    private IReadOnlyList<ProjectedClimbingArea>? lastProjectedClimbing;
    private IReadOnlyList<ProjectedPoi>? lastProjectedPois;
    private int lastSurfacePixelWidth;
    private int lastSurfacePixelHeight;

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
        // Repaint only when the 3D view is on screen and there's live atmosphere to animate —
        // otherwise this is a no-op so 2D mode pays nothing.
        if (IsVisible && Atmosphere is not null)
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
    // A scripted camera flight along the Orla Perć ridge (Zawrat → Krzyżne, W→E), weaving from side
    // to side ("slalom") over the peaks. Geo waypoints are sampled against the live DEM so the path
    // hugs the real terrain at whatever vertical exaggeration is set; a Catmull-Rom spline smooths
    // the ridge line and an ease-in/out keeps the start/stop gentle. Drives the camera directly
    // (free-fly via Target + derived orbit angles), bypassing the orbit controller + its clamps.
    // Leg 1 (slow, slalom): the classic Orla Perć ridge. Leg 2 (the long hops read as a fast
    // transfer): drop over Dolina Białej Wody, over Rysy, across the border to Gerlach, then along
    // the Velická-valley rim to the Łomnica finale — the Slovak side now carries 1 m DMR 5.0 detail,
    // so the grand finale flies over real LiDAR walls. Equal TIME per spline segment makes the short
    // Orla Perć hops majestic and the multi-kilometre valley hops cinematic-fast, by construction.
    private static readonly (double Lat, double Lon)[] OrlaPercWaypoints =
    {
        (49.2193, 20.0179), // Zawrat (pass — flight start)
        (49.2205, 20.0233), // Mały Kozi Wierch
        (49.2222, 20.0294), // Kozi Wierch (2291 m)
        (49.2235, 20.0337), // Kozie Czuby
        (49.2249, 20.0389), // Zadni Granat
        (49.2258, 20.0436), // Skrajny Granat
        (49.2270, 20.0506), // Buczynowe Turnie
        (49.2283, 20.0586), // Krzyżne (end of the Orla Perć leg)
        (49.2120, 20.0750), // over Dolina Białej Wody (the transfer begins)
        (49.1900, 20.0850), // Żabia Grań
        (49.1795, 20.0881), // Rysy
        (49.1700, 20.1100), // Vysoká / Ciężka dolina (across the border)
        (49.1641, 20.1343), // Gerlach (2655 m)
        (49.1750, 20.1650), // rim of Dolina Wielicka (Polski Grzebień side)
        (49.1830, 20.1900), // Sławkowski Szczyt
        (49.1956, 20.2117), // Łomnica (2634 m — flight end)
    };

    // Real-metre clearance the LOCAL camera floor keeps the eye above the terrain directly beneath it: the
    // eye auto-lifts to stay ~this far over the ground as you fly. Added inside the vertical exaggeration so
    // it stays a true value at any Pion setting. 5 m = skim just above the surface (immersive low fly-over).
    private const double CameraClearanceMeters = 5.0;

    // Hard altitude ceiling (metres above sea level) the camera EYE can rise to. Multiplied by the
    // exaggeration to world-Z, so it is a fixed real altitude at any Pion. The camera cannot ascend
    // above this (raise / zoom-out is capped), keeping the view over the terrain rather than in space.
    private const double CameraCeilingMeters = 8_000.0;

    private const double FlightDurationSeconds = 50.0; // ~3.3 s per spline segment, matching the old Orla Perć pace over the extended route
    private const float FlightSlalomAmplitude = 950f;  // world-metres of side-to-side weave (large so it reads at the stand-off distance)
    private const float FlightSlalomWeaves = 3.0f;     // number of left-right swings along the ridge
    private const float FlightCameraHeight = 2600f;    // world-Z above the ridge — far enough above the (2.6×-exaggerated) spiky crest to never dive into a face
    private const float FlightCameraBack = 2600f;      // big stand-off so the whole ridge frames up sharply instead of one magnified, pixelated face
    private const double FlightCancelDragPx = 30.0;    // cumulative drag (px) before a touch cancels the fly-through

    private const float FlightSunDrop = 5.5f; // hours the time-of-day advances over the flight (12.5h → 18h: midday into golden hour)

    private IDispatcherTimer? flightTimer;
    private Vector3[]? flightPath;
    private double flightElapsedSeconds;
    private bool flightActive;
    // Atmosphere snapshot at flight start + the live, time-swept atmosphere used while flying so the
    // sun visibly lowers into golden hour over the course of the flight.
    private float flightStartTime;
    private float flightBaseCloud;
    private float flightBaseWind;
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

        flightPath = pts;
        flightElapsedSeconds = 0;
        flightActive = true;
        IsFlying = true;
        // Cinematic time arc independent of the slider: the flight always sweeps from midday into
        // golden hour (12.5h → 18h) so the sun visibly lowers and the light warms over the flight,
        // never tipping into night. Cloud + wind come from the user's settings.
        Atmosphere? a = Atmosphere;
        flightStartTime = 12.5f;
        flightBaseCloud = a?.CloudCoverage ?? 0.35f;
        flightBaseWind = a?.Wind ?? 0.3f;
        flightAtmosphere = new Atmosphere(flightStartTime, flightBaseCloud, flightBaseWind);
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
        }
    }

    // Show/hide the view's own on-screen chrome (altitude pad, pan/tilt pad) so a fly-through fills
    // the screen. The host page hides its toolbar + sliders off the IsFlying bind.
    private void SetChromeVisible(bool visible)
    {
        AltitudePad.IsVisible = visible;
        PanTiltPad.IsVisible = visible;
    }

    private void OnFlightTick(object? sender, EventArgs e)
    {
        if (!flightActive || flightPath is null || flightPath.Length < 2)
        {
            StopFlight();
            return;
        }

        flightElapsedSeconds += 0.033;
        double raw = flightElapsedSeconds / FlightDurationSeconds;
        bool finished = raw >= 1.0;
        if (finished)
        {
            raw = 1.0;
        }

        // LINEAR progress (constant ground speed). A smoothstep ease-out made the camera crawl to a
        // near-halt over the last third — which read as "the flight stopped in the middle". Constant
        // speed keeps it obviously moving the whole way.
        float p = (float)raw;
        // Sweep the time-of-day forward so the sun lowers into golden hour as the flight progresses.
        flightAtmosphere = new Atmosphere(flightStartTime + (FlightSunDrop * p), flightBaseCloud, flightBaseWind);
        Vector3 here = SampleFlightPath(p);
        Vector3 ahead = SampleFlightPath(MathF.Min(1f, p + 0.025f));

        Vector3 tangent = ahead - here;
        tangent.Z = 0f;
        tangent = tangent.LengthSquared() > 1e-4f ? Vector3.Normalize(tangent) : new Vector3(1f, 0f, 0f);
        var perp = new Vector3(-tangent.Y, tangent.X, 0f); // horizontal, perpendicular to the ridge

        float slalom = MathF.Sin((float)(raw * Math.PI * 2.0 * FlightSlalomWeaves)) * FlightSlalomAmplitude;
        // Trail behind the ridge point (−tangent) and ride higher so the camera frames a wider sweep
        // of the ridge ahead — keeps named peaks in view instead of filling the screen with one face.
        Vector3 cameraPos = here - (tangent * FlightCameraBack) + (perp * slalom) + new Vector3(0f, 0f, FlightCameraHeight);

        // Look a little further along the ridge so the camera always faces the direction of travel.
        Vector3 lookAt = SampleFlightPath(MathF.Min(1f, p + 0.06f));
        ApplyFreeCamera(cameraPos, lookAt);
        Canvas.InvalidateSurface();

        // On reaching the end: cleanly stop AND restore the UI (the old code only flipped
        // flightActive + stopped the timer, leaving IsFlying=true so the chrome stayed hidden and
        // the camera sat frozen — which looked exactly like "the flight died").
        if (finished)
        {
            StopFlight();
        }
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
            view.Canvas.InvalidateSurface();
        }
    }

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
            return;
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

        // LOCAL camera floor — kept CameraClearanceMeters (real) above the terrain DIRECTLY UNDER THE EYE,
        // sampled live from the DEM at the eye's own ground position. Sampling under the EYE (not the look
        // target) is a HARD no-tunnelling guarantee: the camera can never drop below the ground it is
        // physically over. (Sampling under the target let the eye sink below a ridge between it and the
        // valley it was aimed at — "I can go under the map".) To sit 100 m above a particular valley, move
        // the camera OVER that valley (zoom / pan); the floor then follows that terrain. Clearance is added
        // in REAL metres (inside the exaggeration) so it stays a true 100 m at any Pion. Refreshed per frame.
        if (Raster is { } floorRaster)
        {
            GeoPoint eyeGeo = frame.WorldToGeo(Camera.Position);
            double groundElev = floorRaster.SampleBilinear(eyeGeo.Longitude, eyeGeo.Latitude);
            if (groundElev > floorRaster.NoDataValue)
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
        controller.ClampToBounds();

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

        IReadOnlyList<ProjectedClimbingArea>? projectedClimbing = null;
        if (ClimbingAreas is { Count: > 0 } areas && Raster is not null)
        {
            projectedClimbing = climbingProjector.Project(
                areas, Raster, frame, Camera, e.Info.Width, e.Info.Height, ClimbingMarkerLiftMeters);
        }

        IReadOnlyList<ProjectedPoi>? projectedPois = null;
        if (Pois is { Count: > 0 } pois && Raster is not null)
        {
            projectedPois = poiProjector.Project(
                pois, Raster, frame, Camera, e.Info.Width, e.Info.Height, PoiMarkerLiftMeters);
            occlusionMarkers += projectedPois.Count;
            var sw = DebugEnabled ? System.Diagnostics.Stopwatch.StartNew() : null;
            projectedPois = HideOccludedPois(projectedPois, frame);
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
            projectedPeaks = HideOccludedPeaks(projectedPeaks, frame);
            if (sw is not null) { occlusionMs += sw.Elapsed.TotalMilliseconds; }
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
        if (UseGlRenderer && TryRenderTerrainGl(canvas, tiles, e.Info.Width, e.Info.Height))
        {
            // GL already drew the (depth-occluded) trails + route; Skia only adds the markers/labels on top.
            // POI text labels only when the camera is close — a far view of 1000+ POIs is a wall of text.
            bool poiLabelsVisible = Camera.Distance < Services.Terrain3DCanvasRenderer.PoiLabelMaxDistanceWorld;
            renderer.DrawOverlays(canvas, null, null, projectedClimbing, projectedPois, projectedPeaks, projectedUserLocation, poiLabelsVisible);
            DrawNightLights(canvas, projectedPois);
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
        renderer.RenderTiles(canvas, e.Info.Width, e.Info.Height, tiles, Camera, frameScratch, null, projectedTrails, projectedRoute, projectedClimbing, projectedPois, projectedPeaks, projectedUserLocation);
        DrawNightLights(canvas, projectedPois);
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
    private IReadOnlyList<ProjectedPeak> HideOccludedPeaks(IReadOnlyList<ProjectedPeak> peaks, TerrainMesh3D frame)
    {
        if (Raster is not { } raster)
        {
            return peaks;
        }

        System.Numerics.Vector3 cam = Camera.Position;
        int n = peaks.Count;

        // Each marker's occlusion is an independent, read-only DEM raycast, so fan the march out across
        // cores for large marker sets — this is the per-frame cost that dominated a POI-heavy Tatra view.
        // keep[] preserves list order (declutter / draw order unchanged); small sets stay sequential to
        // avoid thread-pool overhead.
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

        var visible = new List<ProjectedPeak>(n);
        for (int i = 0; i < n; i++)
        {
            if (keep[i])
            {
                visible.Add(peaks[i]);
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
    private IReadOnlyList<ProjectedPoi> HideOccludedPois(IReadOnlyList<ProjectedPoi> pois, TerrainMesh3D frame)
    {
        if (Raster is not { } raster)
        {
            return pois;
        }

        System.Numerics.Vector3 cam = Camera.Position;
        float poiLift = PoiMarkerLiftMeters;
        int n = pois.Count;

        // As HideOccludedPeaks: independent read-only raycasts, fanned out across cores for large sets,
        // list order preserved.
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

        var visible = new List<ProjectedPoi>(n);
        for (int i = 0; i < n; i++)
        {
            if (keep[i])
            {
                visible.Add(pois[i]);
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

    // Mouse drag on Windows: MAUI's PanGestureRecognizer is touch/pen only, so we drive orbit/pan from raw
    // pointer events. Left button = orbit, right button = pan. 0 = not dragging.
    private int mouseDragButton;
    private Windows.Foundation.Point lastPointerPosition;

    // Keyboard-step constants tuned to feel close to one drag-pixel of the gesture
    // recognisers (controller.OrbitSensitivity = 0.005 rad/px, PanSensitivity = 0.001 m/px/m).
    private const float KeyOrbitPixelStep = 16f;
    private const float KeyPanPixelStep = 24f;
    private const float KeyZoomFactor = 1.1f;
    private const float KeyTiltPixelStep = 10f; // ~2.9° per repeat — view pitch (R/F, PgUp/PgDn)

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

            // Focusing a SwapChainPanel to capture keys proved unreliable, so also listen at the window
            // root (XamlRoot.Content), which always receives KeyDown. The handler is gated on this view's
            // IsVisible so it only drives the camera while 3D mode is on. handledEventsToo:true so it fires
            // even though focus-navigation marks the arrow keys handled.
            keyboardRoot = element.XamlRoot?.Content as Microsoft.UI.Xaml.UIElement;
            keyboardRoot?.AddHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, keyDownHandler, handledEventsToo: true);

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
            wheelTarget.PointerPressed -= OnPlatformPointerPressed;
            wheelTarget.PointerMoved -= OnPlatformPointerMoved;
            wheelTarget.PointerReleased -= OnPlatformPointerReleased;
            wheelTarget.PointerCaptureLost -= OnPlatformPointerReleased;
            wheelTarget = null;
        }

        if (keyboardRoot is not null && keyDownHandler is not null)
        {
            keyboardRoot.RemoveHandler(Microsoft.UI.Xaml.UIElement.KeyDownEvent, keyDownHandler);
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
        }

        var props = e.GetCurrentPoint(element).Properties;
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
        ((Microsoft.UI.Xaml.UIElement)sender).ReleasePointerCapture(e.Pointer);
    }

    private void OnPlatformKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // The handler also listens at the window root, so ignore keys unless 3D mode is actually showing.
        if (!IsVisible)
        {
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

            // A / D orbit (swing azimuth); W / S move forward / backward on the ground plane (dolly through
            // the scene), matching the FPS convention the user asked for.
            case Windows.System.VirtualKey.A:
                controller.ApplyOrbit(-KeyOrbitPixelStep, 0f);
                break;
            case Windows.System.VirtualKey.D:
                controller.ApplyOrbit(KeyOrbitPixelStep, 0f);
                break;
            case Windows.System.VirtualKey.W:
                controller.ApplyPan(0f, KeyPanPixelStep);
                break;
            case Windows.System.VirtualKey.S:
                controller.ApplyPan(0f, -KeyPanPixelStep);
                break;

            // Vertical pan (raise / lower the camera target).
            case Windows.System.VirtualKey.Q:
                controller.ApplyVertical(KeyPanPixelStep);
                break;
            case Windows.System.VirtualKey.E:
                controller.ApplyVertical(-KeyPanPixelStep);
                break;

            // F9 starts the cinematic fly-through (Orla Perć → Gerlach → Łomnica) — same entry point
            // as the Widok panel's 🎬 button, handy for demos and for driving the app programmatically.
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
            IReadOnlyList<TreeInstance>? forest = EnsureForest(tiles);
            uint terrainTextureId = glRenderer.Render(width, height, tiles, Camera, Trails, Raster, Route, Roads, EffectiveAtmosphere, forest, DetailElevation);
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

        var decoded = new List<(byte[] Rgba, int Width, int Height)>(paths.Count);
        foreach (string path in paths)
        {
            if (DecodeOrtho(path) is { } tile)
            {
                decoded.Add(tile);
            }
        }

        if (decoded.Count != paths.Count)
        {
            cachedOrthoSignature = null;
            cachedOrthoDecoded = null;
            renderer.SetOrthoTextures(Array.Empty<(byte[], int, int)>());
            return;
        }

        cachedOrthoSignature = signature;
        cachedOrthoDecoded = decoded;
        renderer.SetOrthoTextures(decoded);
    }

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