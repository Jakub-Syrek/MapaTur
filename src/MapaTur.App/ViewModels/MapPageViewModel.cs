using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using MapaTur.App.Services;
using MapaTur.Application.Climbing;
using MapaTur.Application.Location;
using MapaTur.Application.Maps;
using MapaTur.Application.Markers;
using MapaTur.Application.Pois;
using MapaTur.Application.Roads;
using MapaTur.Application.Routing;
using MapaTur.Application.Terrain;
using MapaTur.Application.Tracks;
using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Location;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;
using MapaTur.Infrastructure.Terrain;
using MapaTur.Infrastructure.Trails.Overpass;

using Mapsui;
using Mapsui.Projections;

using Microsoft.Extensions.Logging;

using Map = Mapsui.Map;

namespace MapaTur.App.ViewModels;

/// <summary>
/// View model for the main map page. Owns the Mapsui <see cref="Map"/> instance
/// and orchestrates loading of offline tile archives, TCX imports, and trail downloads.
/// </summary>
public sealed partial class MapPageViewModel : ObservableObject
{
    // Default starting viewport: Polish Tatras (Kasprowy Wierch region).
    private const double DefaultCenterLongitude = 19.9819;
    private const double DefaultCenterLatitude = 49.2326;
    private const double DefaultResolution = 152.0; // ~ zoom level 10 in Spherical Mercator

    private readonly IFilePickerService filePicker;
    private readonly IFileSaverService fileSaver;
    private readonly IOfflineMapLoader mapLoader;
    private readonly IMapAutoLoader autoLoader;
    private readonly ITileSourceFactory tileSourceFactory;
    private readonly MBTilesOrthoCompositor? orthoCompositor;
    private readonly I3DSettingsStore settingsStore;
    private readonly IUserLocationService? userLocationService;
    private readonly IUserLocationLayerRenderer? userLocationRenderer;
    private ViewportAwareTrailLayerController? viewportTrailController;
    private readonly ITrackLayerRenderer trackRenderer;
    private readonly ITrailLayerRenderer trailRenderer;
    private readonly IRouteLayerRenderer routeRenderer;
    private readonly IClimbingLayerRenderer climbingRenderer;
    private readonly ImportTcxFileUseCase importTcxFileUseCase;
    private readonly IOverpassClient overpassClient;
    private readonly ITrailRepository trailRepository;
    private readonly IClimbingOverpassClient climbingOverpassClient;
    private readonly IClimbingRepository climbingRepository;
    private readonly IPoiOverpassClient poiOverpassClient;
    private readonly IPoiLayerRenderer poiRenderer;
    private readonly IPoiRepository poiRepository;
    private readonly IRoadOverpassClient roadOverpassClient;
    private readonly IRoadLayerRenderer roadRenderer;
    private readonly PlanRouteUseCase planRouteUseCase;
    private readonly ExportRouteToGpxUseCase exportRouteToGpxUseCase;
    private readonly ILogger<MapPageViewModel> logger;
    private readonly OnlineRegionDemLoader? regionDemLoader;
    private readonly OfflineRegionDownloader? offlineDownloader;

    private readonly List<GeoPoint> waypoints = new(capacity: 2);

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    // Default TRUE — 3D is the headline view and must be what the user sees first, with no flash of
    // the 2D map while the DEM auto-loads. Terrain3DView paints a sky-blue placeholder until the tiles
    // arrive (not the old "biała mapa"). AutoLoadOnStartupAsync falls back to 2D only when no DEM exists.
    [ObservableProperty]
    private bool is3DMode = true;

    /// <summary>True while the 3D view is running a scripted fly-through (two-way bound from the
    /// view). Used to hide the toolbar / slider chrome for a clean cinematic shot.</summary>
    [ObservableProperty]
    private bool is3DFlying;

    /// <summary>Whether to show the 3D on-screen chrome (sliders): only in 3D mode and not mid-flight.</summary>
    public bool Show3DChrome => Is3DMode && !Is3DFlying;

    partial void OnIs3DModeChanged(bool value) => OnPropertyChanged(nameof(Show3DChrome));

    partial void OnIs3DFlyingChanged(bool value) => OnPropertyChanged(nameof(Show3DChrome));

    /// <summary>
    /// Raised after an explicit terrain load (e.g. the GUGiK region) so the host page reframes the 3D
    /// camera onto the new mesh. A freshly loaded region has different bounds than the saved camera, so
    /// without this it stays framed on the old terrain (appearing tiny / off-map until a manual reset).
    /// </summary>
    public event EventHandler? TerrainReframeRequested;

    /// <summary>Current GPS fix or null. Bound by both the 2D map renderer and the 3D view.</summary>
    [ObservableProperty]
    private UserLocation? userLocation;

    /// <summary>True when the location service is actively polling for fixes. Drives the button label.</summary>
    [ObservableProperty]
    private bool isLocationTracking;

    /// <summary>Whether the ☰ actions dropdown is open.</summary>
    [ObservableProperty]
    private bool isMenuOpen;

    /// <summary>
    /// Whether route-planning is active. When false (default), a map tap does nothing; when true, taps
    /// drop waypoints and plan a route. Toggled from the ☰ menu so casual browsing / marker taps never
    /// start a route by accident.
    /// </summary>
    [ObservableProperty]
    private bool isRoutePlanningMode;

    // Enabling planning closes the menu so the map is free to tap; the status line tells the user.
    partial void OnIsRoutePlanningModeChanged(bool value)
    {
        IsMenuOpen = false;
        StatusMessage = value
            ? Localization.AppStrings.StatusRoutePlanningOn
            : Localization.AppStrings.StatusRoutePlanningOff;
    }

    /// <summary>Whether the marker details card is shown.</summary>
    [ObservableProperty]
    private bool isMarkerPopupVisible;

    /// <summary>Title of the marker details card (feature name or localized fallback).</summary>
    [ObservableProperty]
    private string markerPopupTitle = string.Empty;

    /// <summary>Detail lines of the marker details card.</summary>
    [ObservableProperty]
    private IReadOnlyList<MarkerPopupLine>? markerPopupLines;

    /// <summary>Opens the marker details card with the given content (from a 2D or 3D marker tap).</summary>
    public void ShowMarkerPopup(MarkerPopupContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        MarkerPopupTitle = content.Title;
        MarkerPopupLines = content.Lines;
        IsMarkerPopupVisible = true;
    }

    /// <summary>
    /// Which premium-menu section is open: 0 = none (bar only), 1 = Mapa, 2 = Pogoda, 3 = Widok,
    /// 4 = Dane, 5 = Ustawienia. Drives the top-bar chip highlight + which frosted glass panel is shown.
    /// </summary>
    [ObservableProperty]
    private int activeSection;

    /// <summary>
    /// Top-bar chip handler: opens the given section, or closes it if it's already open (tap-again to
    /// dismiss). Accepts the index as a string so the XAML <c>CommandParameter</c> needs no typed literal.
    /// </summary>
    [RelayCommand]
    private void SelectSection(string? index)
    {
        if (!int.TryParse(index, out int target))
        {
            return;
        }
        ActiveSection = ActiveSection == target ? 0 : target;
    }

    /// <summary>Closes any open section panel (scrim tap / explicit close).</summary>
    [RelayCommand]
    private void CloseSection() => ActiveSection = 0;

    /// <summary>
    /// Flips one of the multi-select filter flags by name — the premium menu's "pill" toggles tap this so
    /// each pill needs no per-flag command or two-way plumbing. Explicit switch (no reflection) keeps it
    /// AOT-safe and obvious.
    /// </summary>
    [RelayCommand]
    private void ToggleFlag(string? name)
    {
        switch (name)
        {
            case "TrailRed": TrailColourRedEnabled = !TrailColourRedEnabled; break;
            case "TrailBlue": TrailColourBlueEnabled = !TrailColourBlueEnabled; break;
            case "TrailGreen": TrailColourGreenEnabled = !TrailColourGreenEnabled; break;
            case "TrailYellow": TrailColourYellowEnabled = !TrailColourYellowEnabled; break;
            case "TrailBlack": TrailColourBlackEnabled = !TrailColourBlackEnabled; break;
            case "RegionTatry": RegionTatryEnabled = !RegionTatryEnabled; break;
            case "RegionBeskidy": RegionBeskidyEnabled = !RegionBeskidyEnabled; break;
            case "RegionPieniny": RegionPieninyEnabled = !RegionPieninyEnabled; break;
            case "RegionBieszczady": RegionBieszczadyEnabled = !RegionBieszczadyEnabled; break;
            case "PoiHuts": ShowHuts = !ShowHuts; break;
            case "PoiWilderness": ShowWildernessHuts = !ShowWildernessHuts; break;
            case "PoiChalets": ShowChalets = !ShowChalets; break;
            case "PoiShelters": ShowShelters = !ShowShelters; break;
            case "PoiViewpoints": ShowViewpoints = !ShowViewpoints; break;
        }
    }

    /// <summary>Toggles the ☰ actions dropdown.</summary>
    [RelayCommand]
    private void ToggleMenu() => IsMenuOpen = !IsMenuOpen;

    /// <summary>Toggles route-planning mode (also closes the menu via the change handler).</summary>
    [RelayCommand]
    private void ToggleRoutePlanning() => IsRoutePlanningMode = !IsRoutePlanningMode;

    /// <summary>Closes the marker details card.</summary>
    [RelayCommand]
    private void CloseMarkerPopup() => IsMarkerPopupVisible = false;

    [ObservableProperty]
    private IReadOnlyList<TerrainMesh3D>? terrainTiles;

    /// <summary>
    /// First terrain tile, used as the shared world frame for overlay projection and 2D↔3D camera
    /// sync (every tile carries the full raster's bounds, so any tile defines the same GeoToWorld).
    /// Null when no DEM is loaded.
    /// </summary>
    public TerrainMesh3D? TerrainFrame => TerrainTiles is { Count: > 0 } tiles ? tiles[0] : null;

    [ObservableProperty]
    private DemRaster? terrainRaster;

    /// <summary>
    /// Multiplier applied to elevation when building the 3D mesh. 1.0 = true scale,
    /// higher values exaggerate vertical relief so soft hills read better on screen.
    /// Changing this rebuilds the mesh from the current raster.
    /// </summary>
    [ObservableProperty]
    private double verticalExaggeration = 2.0;

    /// <summary>
    /// Time of day in hours, [0,24). Drives the <see cref="Atmosphere"/> sun / sky / fog model
    /// the 3D renderer samples each frame. 14.0 = early afternoon (default), 18.0 = sunset,
    /// 6.0 = sunrise, 0.0 = midnight. Persisted in <see cref="settingsStore"/>.
    /// </summary>
    [ObservableProperty]
    private double timeOfDayHours = 14.0;

    /// <summary>
    /// Base cloud coverage, [0,1]: 0 = clear sky, ~0.35 = scattered (default), 1 = heavy/overcast.
    /// Sets how much cirrus + sea-of-clouds the 3D atmosphere draws; the renderer modulates this
    /// with its own slow weather drift so the sky still evolves. Persisted in <see cref="settingsStore"/>.
    /// </summary>
    [ObservableProperty]
    private double cloudiness = 0.35;

    /// <summary>
    /// Wind strength, [0,1]: 0 = calm (slow, bright clouds), 1 = gale (fast-drifting, dark storm
    /// clouds). Drives the cloud drift speed and storm-darkening in the renderer. Persisted.
    /// </summary>
    [ObservableProperty]
    private double wind = 0.3;

    /// <summary>
    /// Snow-cover amount, [0,1]: 0 = no snow (default), 1 = full snow (the snowline drops to the
    /// valley floor). Drives the terrain shader's snow blend in the 3D renderer. Persisted.
    /// </summary>
    [ObservableProperty]
    private double snow;

    /// <summary>
    /// Forest density, [0,1]: 0 = no trees, 1 = densest. Drives how many trees the 3D renderer scatters
    /// over the terrain below the treeline (bound into <c>Terrain3DView.ForestDensity</c>). Persisted.
    /// </summary>
    [ObservableProperty]
    private double forestDensity = 0.6;

    /// <summary>
    /// Live atmospheric model derived from <see cref="TimeOfDayHours"/>, <see cref="Cloudiness"/>,
    /// <see cref="Wind"/> and <see cref="Snow"/>. Recomputed whenever any change and bound straight
    /// into <c>Terrain3DView.Atmosphere</c>. Cheap to build so deriving per change is fine.
    /// </summary>
    public Atmosphere Atmosphere => new((float)TimeOfDayHours, (float)Cloudiness, (float)Wind, (float)Snow);

    partial void OnTimeOfDayHoursChanged(double value)
    {
        settingsStore.TimeOfDayHours = value;
        // Atmosphere is a computed property; notify so the View re-binds the new instance.
        OnPropertyChanged(nameof(Atmosphere));
    }

    partial void OnCloudinessChanged(double value)
    {
        settingsStore.Cloudiness = value;
        OnPropertyChanged(nameof(Atmosphere));
    }

    partial void OnWindChanged(double value)
    {
        settingsStore.Wind = value;
        OnPropertyChanged(nameof(Atmosphere));
    }

    partial void OnSnowChanged(double value)
    {
        settingsStore.Snow = value;
        OnPropertyChanged(nameof(Atmosphere));
    }

    partial void OnForestDensityChanged(double value)
    {
        // Forest density is NOT part of the Atmosphere — the view binds ForestDensity directly and
        // rebuilds the tree placement when it changes. Just persist here.
        settingsStore.Forest = value;
        OnPropertyChanged(nameof(EffectiveForestDensity));
    }

    /// <summary>
    /// Render-quality profile: 0 = Wydajność, 1 = Zbalansowana, 2 = Wysoka. Scales the real cost levers
    /// (anti-aliasing + forest density) so the user trades fidelity for framerate. Default = Zbalansowana.
    /// </summary>
    [ObservableProperty]
    private int renderQuality = 1;

    partial void OnRenderQualityChanged(int value)
    {
        OnPropertyChanged(nameof(EffectiveForestDensity));
        OnPropertyChanged(nameof(AntiAliasingOn));
    }

    /// <summary>Anti-aliasing (MSAA) is on for Zbalansowana/Wysoka, off for Wydajność. Bound to the view.</summary>
    public bool AntiAliasingOn => RenderQuality > 0;

    /// <summary>
    /// The forest density actually rendered: the user's "Las" slider value scaled by the quality profile
    /// (Wydajność thins the forest for headroom; Wysoka renders it in full). Bound to the 3D view.
    /// </summary>
    public double EffectiveForestDensity => ForestDensity * RenderQuality switch
    {
        0 => 0.4,
        2 => 1.0,
        _ => 0.75,
    };

    /// <summary>Sets the render-quality profile from the Ustawienia segmented control (index as string).</summary>
    [RelayCommand]
    private void SelectQuality(string? index)
    {
        if (int.TryParse(index, out int q))
        {
            RenderQuality = Math.Clamp(q, 0, 2);
        }
    }

    /// <summary>Human-readable summary of cached record counts, shown in the Ustawienia "Cache" block.</summary>
    [ObservableProperty]
    private string cacheSummary = "—";

    // Refresh the cache counts whenever the Ustawienia panel (section 5) opens, so the figure is live.
    partial void OnActiveSectionChanged(int value)
    {
        if (value == 5)
        {
            _ = RefreshCacheSummaryAsync();
        }
    }

    private async Task RefreshCacheSummaryAsync()
    {
        try
        {
            int trails = await trailRepository.CountAsync().ConfigureAwait(true);
            int pois = await poiRepository.CountAsync().ConfigureAwait(true);
            int climbing = await climbingRepository.CountAsync().ConfigureAwait(true);
            CacheSummary = $"Szlaki: {trails} · POI: {pois} · Wspinaczka: {climbing}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read cache counts");
            CacheSummary = "—";
        }
    }

    /// <summary>Deletes all downloaded data (trails, POIs, climbing areas) from the local cache.</summary>
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        try
        {
            IsBusy = true;
            await trailRepository.ClearAsync().ConfigureAwait(true);
            await poiRepository.ClearAsync().ConfigureAwait(true);
            await climbingRepository.ClearAsync().ConfigureAwait(true);
            StatusMessage = "Wyczyszczono pobrane dane.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear cache");
            StatusMessage = "Nie udało się wyczyścić cache.";
        }
        finally
        {
            IsBusy = false;
            await RefreshCacheSummaryAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Whether the live FPS/scene-stats debug HUD is shown over the 3D view.</summary>
    [ObservableProperty]
    private bool showDebugOverlay;

    /// <summary>Whether verbose (Serilog Verbose) logging is enabled for in-field diagnostics.</summary>
    [ObservableProperty]
    private bool verboseLogging;

    partial void OnVerboseLoggingChanged(bool value)
    {
        MauiProgram.LogLevelSwitch.MinimumLevel = value
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information;
        logger.LogInformation("Verbose logging {State}", value ? "ON" : "OFF");
    }

    /// <summary>
    /// Serialized 3D camera state, two-way bound to <c>Terrain3DView.CameraState</c>. The view
    /// writes its current camera here (debounced) and reads it back to restore the framing when
    /// the matching DEM reloads. Persisted verbatim to <see cref="settingsStore"/>.
    /// </summary>
    [ObservableProperty]
    private string? cameraState;

    partial void OnCameraStateChanged(string? value)
    {
        settingsStore.CameraState = value;
    }

    private readonly MeshRebuildCoalescer meshRebuildCoalescer = new();

    /// <summary>
    /// Mirror every GPS fix onto the 2D Mapsui layer. The 3D view binds <c>UserLocation</c>
    /// directly so it picks the change up through its own bindable handler.
    /// </summary>
    partial void OnUserLocationChanged(UserLocation? value)
    {
        userLocationRenderer?.RenderUserLocation(Map, value);
    }

    /// <summary>
    /// Toggles GPS tracking. First press requests permission and starts the poll loop; second
    /// press stops the loop and clears the marker. Idempotent — safe to spam.
    /// </summary>
    [RelayCommand]
    public async Task ToggleLocationTrackingAsync()
    {
        if (userLocationService is null)
        {
            return;
        }
        if (userLocationService.IsTracking)
        {
            userLocationService.Stop();
            IsLocationTracking = false;
            UserLocation = null;
            StatusMessage = Localization.AppStrings.StatusLocationTrackingStopped;
            return;
        }

        bool started = await userLocationService.StartAsync().ConfigureAwait(true);
        if (started)
        {
            IsLocationTracking = true;
            StatusMessage = Localization.AppStrings.StatusLocationTrackingStarted;
        }
    }

    partial void OnVerticalExaggerationChanged(double value)
    {
        // Persist every change so a relaunch lands on the same setting.
        settingsStore.VerticalExaggeration = value;

        if (TerrainRaster is null)
        {
            return;
        }

        // Coalesce rapid slider changes into one in-flight rebuild, but always honour the
        // LAST value the user settled on — RequestRebuild returns null while a build is in
        // flight and stashes the trailing value for StartMeshRebuild's completion to replay.
        if (meshRebuildCoalescer.RequestRebuild(value) is { } toBuild)
        {
            StartMeshRebuild(toBuild);
        }
    }

    private void StartMeshRebuild(double value)
    {
        if (TerrainRaster is not { } raster)
        {
            return;
        }

        // Fire-and-forget rebuild — the slider drives many small changes; a single rebuild that
        // lands one frame later is plenty smooth at 360x180 meshes. On completion, replay the
        // trailing value if the user moved the slider again while this build was running.
        //
        // CRITICAL: pass the SAME ortho grid (orthoGridCols/Rows) the initial load used. Omitting them
        // defaulted the rebuild to a 1×1 grid, so every mesh tile sampled ortho cell 0 (the NW quadrant)
        // with UVs spanning the whole raster — the lowland NW image smeared across the peaks ("villages
        // on the summits"). Capturing the fields into locals keeps the background Task off the instance.
        int gridCols = orthoGridCols;
        int gridRows = orthoGridRows;
        _ = Task.Run(() =>
        {
            var options = new MapaTur.Application.Terrain.TerrainMeshOptions
            {
                VerticalExaggeration = (float)Math.Clamp(value, 1.0, 5.0),
            };
            var rebuilt = TerrainMesh3D.BuildTiles(raster, options, orthoGridCols: gridCols, orthoGridRows: gridRows);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                TerrainTiles = rebuilt;
                OnPropertyChanged(nameof(TerrainFrame));
                if (meshRebuildCoalescer.CompleteRebuild() is { } trailing)
                {
                    StartMeshRebuild(trailing);
                }
            });
        });
    }

    [ObservableProperty]
    private IReadOnlyList<Trail>? trails3DOverlay;

    /// <summary>Roads overlay for the 3D view (unmarked Trail polylines), or null when roads are hidden.</summary>
    [ObservableProperty]
    private IReadOnlyList<Trail>? roads3DOverlay;

    // Last-downloaded roads (full + simplified-for-3D), kept so the show/hide toggle re-applies without a refetch.
    private IReadOnlyList<Trail>? rawRoads;
    private IReadOnlyList<Trail>? rawRoads3D;

    /// <summary>Master show/hide for roads on the 2D map and 3D view.</summary>
    [ObservableProperty]
    private bool showRoads = true;

    partial void OnShowRoadsChanged(bool value) => ApplyRoads();

    /// <summary>Re-applies road visibility to the 2D map and 3D overlay from the last download.</summary>
    private void ApplyRoads()
    {
        if (rawRoads is null)
        {
            return;
        }
        if (ShowRoads)
        {
            roadRenderer.RenderRoads(Map, rawRoads);
            Roads3DOverlay = rawRoads3D;
        }
        else
        {
            roadRenderer.Clear(Map);
            Roads3DOverlay = null;
        }
    }

    /// <summary>Master show/hide for all trails; when off, no trail renders regardless of the colour/region filter.</summary>
    /// <summary>Whether the orthophoto drape is shown on the 3D terrain (else hypsometric shading).</summary>
    [ObservableProperty] private bool showOrtho = true;

    /// <summary>Whether summit glyphs + elevation labels are drawn over the 3D terrain.</summary>
    [ObservableProperty] private bool showPeakNames = true;

    /// <summary>Whether the avalanche slope-steepness ("Mapa nachylenia") shading is active.</summary>
    [ObservableProperty] private bool slopeMapMode;

    [ObservableProperty] private bool showTrails = true;

    partial void OnShowTrailsChanged(bool value) => OnTrailFilterChanged();

    // PTTK colour toggles for the trail filter. All true by default — the
    // partial OnXxxChanged hooks below rebuild Trails3DOverlay + 2D layer.
    [ObservableProperty] private bool trailColourRedEnabled = true;
    [ObservableProperty] private bool trailColourBlueEnabled = true;
    [ObservableProperty] private bool trailColourGreenEnabled = true;
    [ObservableProperty] private bool trailColourYellowEnabled = true;
    [ObservableProperty] private bool trailColourBlackEnabled = true;

    // Karpat sub-region toggles. None enabled = no region constraint (everything
    // that the colour filter accepts is shown).
    [ObservableProperty] private bool regionTatryEnabled;
    [ObservableProperty] private bool regionBeskidyEnabled;
    [ObservableProperty] private bool regionPieninyEnabled;
    [ObservableProperty] private bool regionBieszczadyEnabled;

    // Last raw download from Overpass — kept so that filter toggles can rebuild
    // the visible subset without re-hitting the network.
    private IReadOnlyList<Trail>? rawTrails;

    // Same trails simplified once for the 3D overlay (see SimplifyForOverlay3D). Filter toggles just
    // re-filter this cached set instead of re-simplifying every trail on the UI thread each click.
    private IReadOnlyList<Trail>? rawTrails3D;

    partial void OnTrailColourRedEnabledChanged(bool value) => OnTrailFilterChanged();
    partial void OnTrailColourBlueEnabledChanged(bool value) => OnTrailFilterChanged();
    partial void OnTrailColourGreenEnabledChanged(bool value) => OnTrailFilterChanged();
    partial void OnTrailColourYellowEnabledChanged(bool value) => OnTrailFilterChanged();
    partial void OnTrailColourBlackEnabledChanged(bool value) => OnTrailFilterChanged();
    partial void OnRegionTatryEnabledChanged(bool value) => OnTrailFilterChanged();
    partial void OnRegionBeskidyEnabledChanged(bool value) => OnTrailFilterChanged();
    partial void OnRegionPieninyEnabledChanged(bool value) => OnTrailFilterChanged();
    partial void OnRegionBieszczadyEnabledChanged(bool value) => OnTrailFilterChanged();

    private void OnTrailFilterChanged()
    {
        // Always refresh the viewport-aware 2D layer first: it re-queries the repository and applies the
        // live filter (including the ShowTrails master switch), so toggling works even when no trails were
        // loaded via ApplyTrailsAsync this session — e.g. they came straight from the SQLite cache at
        // startup, leaving rawTrails null. (This early-returned before, so unchecking "Szlaki" did nothing.)
        viewportTrailController?.RequestRefresh();

        // The direct 2D render + 3D overlay re-filter only apply when the raw set is held in memory.
        if (rawTrails is null)
        {
            return;
        }

        var filter = BuildTrailFilter();
        // The master ShowTrails switch wins: when off, nothing is shown on either layer.
        var filtered = ShowTrails ? rawTrails.Where(filter.IsVisible).ToList() : new List<Trail>();
        // Filter the pre-simplified set for the 3D overlay — cheap, no re-simplification per toggle.
        Trails3DOverlay = ShowTrails ? rawTrails3D?.Where(filter.IsVisible).ToList() : null;
        trailRenderer.RenderTrails(Map, filtered);
    }

    // Trails feed the 3D overlay at full Overpass resolution (hundreds of polylines × hundreds of
    // points), and every vertex is lifted to the DEM and projected each frame. At the kilometres-wide
    // 3D scale that detail is invisible, so simplify to a coarse epsilon for the overlay only — the 2D
    // map keeps its own (zoom-aware) geometry. This is a one-off per download/filter change, not per frame.
    private const double Trail3DSimplifyEpsilonMeters = 20.0;

    private static IReadOnlyList<Trail> SimplifyForOverlay3D(IReadOnlyList<Trail> trails)
    {
        var result = new List<Trail>(trails.Count);
        foreach (Trail trail in trails)
        {
            IReadOnlyList<GeoPoint> simplified = TrailGeometrySimplifier.Simplify(trail.Geometry, Trail3DSimplifyEpsilonMeters);
            result.Add(new Trail(trail.Id, trail.Name, trail.Markings, simplified));
        }
        return result;
    }

    /// <summary>
    /// Adopts a freshly obtained trail set (from a live Overpass download or a pre-bundled file):
    /// persists it, caches the raw + simplified-for-3D copies, and renders the filtered subset on
    /// both the 2D map and the 3D overlay. Shared by the viewport download and startup auto-load.
    /// </summary>
    private async Task ApplyTrailsAsync(IReadOnlyList<Trail> trails)
    {
        await trailRepository.UpsertAsync(trails).ConfigureAwait(true);
        rawTrails = trails;
        // Simplify once now (off the per-toggle path) so filter changes are cheap re-filters.
        rawTrails3D = SimplifyForOverlay3D(trails);
        var filter = BuildTrailFilter();
        var filteredTrails = trails.Where(filter.IsVisible).ToList();

        // The viewport controller re-queries the repo with current-zoom epsilon and
        // renders the simplified subset; falling back to a direct render keeps the
        // old behaviour if the controller hasn't been activated yet (e.g. tests).
        if (viewportTrailController is not null)
        {
            viewportTrailController.RequestRefresh();
        }
        else
        {
            trailRenderer.RenderTrails(Map, filteredTrails);
        }
        Trails3DOverlay = rawTrails3D.Where(filter.IsVisible).ToList();
    }

    /// <summary>Builds the current <see cref="TrailFilter"/> snapshot from the toggle state.</summary>
    public TrailFilter BuildTrailFilter()
    {
        var f = new TrailFilter();
        if (TrailColourRedEnabled) f.EnabledColours.Add(PttkColor.Red);
        if (TrailColourBlueEnabled) f.EnabledColours.Add(PttkColor.Blue);
        if (TrailColourGreenEnabled) f.EnabledColours.Add(PttkColor.Green);
        if (TrailColourYellowEnabled) f.EnabledColours.Add(PttkColor.Yellow);
        if (TrailColourBlackEnabled) f.EnabledColours.Add(PttkColor.Black);
        if (RegionTatryEnabled) f.EnabledRegions.Add(KarpatRegions.Tatry);
        if (RegionBeskidyEnabled) f.EnabledRegions.Add(KarpatRegions.Beskidy);
        if (RegionPieninyEnabled) f.EnabledRegions.Add(KarpatRegions.Pieniny);
        if (RegionBieszczadyEnabled) f.EnabledRegions.Add(KarpatRegions.Bieszczady);
        return f;
    }

    [ObservableProperty]
    private Domain.Routing.Route? route3DOverlay;

    [ObservableProperty]
    private IReadOnlyList<MapaTur.Domain.Climbing.ClimbingArea>? climbing3DOverlay;

    [ObservableProperty]
    private IReadOnlyList<MapaTur.Domain.Pois.MountainPoi>? pois3DOverlay;

    // Last-downloaded POIs, kept so the per-type filter can re-apply without re-querying Overpass.
    private IReadOnlyList<MapaTur.Domain.Pois.MountainPoi>? rawPois;

    // Per-kind POI visibility toggles (default all on). Unchecking all hides POIs entirely.
    [ObservableProperty]
    private bool showHuts = true;
    [ObservableProperty]
    private bool showWildernessHuts = true;
    [ObservableProperty]
    private bool showChalets = true;
    [ObservableProperty]
    private bool showShelters = true;
    [ObservableProperty]
    private bool showViewpoints = true;

    partial void OnShowHutsChanged(bool value) => ApplyPoiFilter();
    partial void OnShowWildernessHutsChanged(bool value) => ApplyPoiFilter();
    partial void OnShowChaletsChanged(bool value) => ApplyPoiFilter();
    partial void OnShowSheltersChanged(bool value) => ApplyPoiFilter();
    partial void OnShowViewpointsChanged(bool value) => ApplyPoiFilter();

    /// <summary>Returns true when a POI of the given kind is currently enabled in the type filter.</summary>
    private bool IsPoiKindVisible(MapaTur.Domain.Pois.PoiKind kind) => kind switch
    {
        MapaTur.Domain.Pois.PoiKind.Hut => ShowHuts,
        MapaTur.Domain.Pois.PoiKind.WildernessHut => ShowWildernessHuts,
        MapaTur.Domain.Pois.PoiKind.Chalet => ShowChalets,
        MapaTur.Domain.Pois.PoiKind.Shelter => ShowShelters,
        MapaTur.Domain.Pois.PoiKind.Viewpoint => ShowViewpoints,
        _ => true,
    };

    /// <summary>Re-applies the per-kind filter to the last-downloaded POIs across the 2D map and 3D view.</summary>
    private void ApplyPoiFilter()
    {
        if (rawPois is null)
        {
            return;
        }
        var filtered = rawPois.Where(poi => IsPoiKindVisible(poi.Kind)).ToList();
        poiRenderer.RenderPois(Map, filtered);
        Pois3DOverlay = filtered;
    }

    /// <summary>
    /// Finds a loaded POI by its (OSM) id so a tapped 2D marker — which only carries the id — can be
    /// resolved back to the full domain object for the details popup. Returns false when not loaded.
    /// </summary>
    public bool TryFindPoiById(long id, out MapaTur.Domain.Pois.MountainPoi poi)
    {
        if (rawPois is not null)
        {
            foreach (var candidate in rawPois)
            {
                if (candidate.Id == id)
                {
                    poi = candidate;
                    return true;
                }
            }
        }

        poi = null!;
        return false;
    }

    /// <summary>
    /// Finds a loaded climbing area by its (OSM) id so a tapped 2D marker can be resolved back to the
    /// full domain object for the details popup. Returns false when not loaded.
    /// </summary>
    public bool TryFindClimbingById(long id, out MapaTur.Domain.Climbing.ClimbingArea area)
    {
        if (Climbing3DOverlay is { } areas)
        {
            foreach (var candidate in areas)
            {
                if (candidate.Id == id)
                {
                    area = candidate;
                    return true;
                }
            }
        }

        area = null!;
        return false;
    }

    /// <summary>
    /// Loads POIs cached in the local SQLite repository within <paramref name="bounds"/> and applies
    /// them to the 2D map + 3D overlay. Best-effort and silent when the cache is empty — used at
    /// auto-load so once-downloaded refuges survive a restart without another Overpass call.
    /// </summary>
    private async Task LoadCachedPoisAsync(MapBounds bounds)
    {
        try
        {
            IReadOnlyList<MapaTur.Domain.Pois.MountainPoi> cached =
                await poiRepository.FindIntersectingAsync(bounds).ConfigureAwait(true);
            if (cached.Count == 0)
            {
                return;
            }
            rawPois = cached;
            ApplyPoiFilter();
            logger.LogInformation("Loaded {Count} cached POIs for the loaded DEM footprint", cached.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load cached POIs");
        }
    }

    /// <summary>Path to an ortho-photo image draped over the 3D terrain (GPU path), or null for the hypsometric tint.</summary>
    [ObservableProperty]
    private string? orthoTexturePath;

    /// <summary>Ortho tiles (row-major) draped over the 3D terrain, one per mesh cell; null/empty for none.</summary>
    [ObservableProperty]
    private IReadOnlyList<string>? orthoTexturePaths;

    /// <summary>
    /// Pre-decoded ortho cells composited from a basemap MBTiles archive (row-major). When set,
    /// the 3D view uploads these RGBA8 buffers directly and ignores <see cref="OrthoTexturePath"/>
    /// / <see cref="OrthoTexturePaths"/>. Null when no MBTiles draping is in effect.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<OrthoTextureCell>? orthoTextureCells;

    // Path of the basemap MBTiles to drape on the 3D mesh once a DEM is available. Set during
    // auto-load; consumed by EnsureOrthoTextureCellsAsync after LoadDemFromPathAsync finishes
    // (the DEM provides the bounds the compositor needs to project tiles into mesh UV space).
    private string? draping3DBasemapPath;

    // Ortho grid the terrain mesh is tiled to match (1×1 = a single full-extent texture).
    private int orthoGridCols = 1;
    private int orthoGridRows = 1;

    /// <summary>
    /// DEM-derived summits drawn as labelled markers in the 3D view so it isn't bare terrain +
    /// trails. Computed offline from the loaded raster (no network) and refreshed each DEM load.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<TerrainPeak>? peaks3DOverlay;

    // Union of every loaded basemap's extent — used to clip Overpass downloads to
    // the area we actually have map coverage for, even when multiple regional
    // archives are stacked.
    private MapBounds? basemapBounds;
    private bool autoLoadAttempted;

    private void ExtendBasemapBounds(MapBounds? loaded)
    {
        if (loaded is not { } extent)
        {
            return;
        }
        basemapBounds = basemapBounds is { } existing ? existing.Union(extent) : extent;
        UpdateTrailCoverage();
    }

    // Małopolska voivodeship bounding box (generous). Now that the online ortho base covers the whole
    // region, trails/roads clip to this instead of the small Tatry basemap — so they show across all of
    // Małopolska on the imagery, not just over the bundled Tatra rectangle.
    private static readonly MapBounds MalopolskaRegion = new(
        new GeoPoint(48.95, 19.0),
        new GeoPoint(50.6, 21.6));

    // The 2D trail/road coverage. With the online ortho base present, that's the whole Małopolska region
    // (unioned with any larger loaded basemap). Called whenever a basemap or DEM loads.
    private void UpdateTrailCoverage()
    {
        MapBounds coverage = MalopolskaRegion;
        if (basemapBounds is { } basemap)
        {
            coverage = coverage.Union(basemap);
        }

        if (viewportTrailController is not null)
        {
            viewportTrailController.CoverageBounds = coverage;
            viewportTrailController.RequestRefresh();
        }

        // Roads use the same coverage clip; re-render the last-downloaded set with it applied.
        roadRenderer.CoverageBounds = coverage;
        ApplyRoads();
    }

    /// <summary>
    /// Initializes a new instance of the view model.
    /// </summary>
    /// <param name="filePicker">File picker service used to obtain MBTiles/TCX paths.</param>
    /// <param name="fileSaver">File saver service for export destinations.</param>
    /// <param name="mapLoader">Tile archive loader.</param>
    /// <param name="autoLoader">Discovers pre-bundled / installed map data on disk for one-shot auto-load on first appearance.</param>
    /// <param name="tileSourceFactory">Opens MBTiles archives to read their metadata (zoom range, bounds) when prioritizing basemaps.</param>
    /// <param name="settingsStore">Persistent backing store for 3D-mode user settings (vertical exaggeration, etc.).</param>
    /// <param name="trackRenderer">Track polyline renderer.</param>
    /// <param name="trailRenderer">Trail polyline renderer.</param>
    /// <param name="routeRenderer">Planned-route polyline renderer.</param>
    /// <param name="climbingRenderer">Climbing-area marker renderer.</param>
    /// <param name="importTcxFileUseCase">TCX import use case.</param>
    /// <param name="overpassClient">Overpass HTTP client (trails).</param>
    /// <param name="trailRepository">Trail persistence repository.</param>
    /// <param name="climbingOverpassClient">Overpass HTTP client (climbing).</param>
    /// <param name="climbingRepository">Climbing-area persistence repository.</param>
    /// <param name="poiOverpassClient">Overpass HTTP client (mountain POIs).</param>
    /// <param name="poiRenderer">Mountain-POI marker renderer.</param>
    /// <param name="poiRepository">Local SQLite cache for mountain POIs (offline re-load).</param>
    /// <param name="roadOverpassClient">Overpass HTTP client (roads).</param>
    /// <param name="roadRenderer">Road polyline renderer.</param>
    /// <param name="planRouteUseCase">Route planning use case.</param>
    /// <param name="exportRouteToGpxUseCase">GPX export use case.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="orthoCompositor">Optional compositor that drapes basemap MBTiles tiles onto the 3D terrain mesh; null disables MBTiles draping.</param>
    /// <param name="userLocationService">Optional OS GPS feed; null disables the live location dot (e.g. in tests / on a headless host).</param>
    /// <param name="userLocationRenderer">Optional 2D-map renderer for the live GPS dot; null skips 2D rendering of the location.</param>
    /// <param name="regionDemLoader">Optional online-region DEM loader (GUGiK 1 m + Terrarium); null disables the "load Tatra region" button.</param>
    /// <param name="offlineDownloader">Optional bulk tile prefetcher (GUGiK 1 m); null disables the "download Tatras offline" button.</param>
    public MapPageViewModel(
        IFilePickerService filePicker,
        IFileSaverService fileSaver,
        IOfflineMapLoader mapLoader,
        IMapAutoLoader autoLoader,
        ITileSourceFactory tileSourceFactory,
        I3DSettingsStore settingsStore,
        ITrackLayerRenderer trackRenderer,
        ITrailLayerRenderer trailRenderer,
        IRouteLayerRenderer routeRenderer,
        IClimbingLayerRenderer climbingRenderer,
        ImportTcxFileUseCase importTcxFileUseCase,
        IOverpassClient overpassClient,
        ITrailRepository trailRepository,
        IClimbingOverpassClient climbingOverpassClient,
        IClimbingRepository climbingRepository,
        IPoiOverpassClient poiOverpassClient,
        IPoiLayerRenderer poiRenderer,
        IPoiRepository poiRepository,
        IRoadOverpassClient roadOverpassClient,
        IRoadLayerRenderer roadRenderer,
        PlanRouteUseCase planRouteUseCase,
        ExportRouteToGpxUseCase exportRouteToGpxUseCase,
        ILogger<MapPageViewModel> logger,
        MBTilesOrthoCompositor? orthoCompositor = null,
        IUserLocationService? userLocationService = null,
        IUserLocationLayerRenderer? userLocationRenderer = null,
        OnlineRegionDemLoader? regionDemLoader = null,
        OfflineRegionDownloader? offlineDownloader = null)
    {
        ArgumentNullException.ThrowIfNull(filePicker);
        ArgumentNullException.ThrowIfNull(fileSaver);
        ArgumentNullException.ThrowIfNull(mapLoader);
        ArgumentNullException.ThrowIfNull(autoLoader);
        ArgumentNullException.ThrowIfNull(tileSourceFactory);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(trackRenderer);
        ArgumentNullException.ThrowIfNull(trailRenderer);
        ArgumentNullException.ThrowIfNull(routeRenderer);
        ArgumentNullException.ThrowIfNull(climbingRenderer);
        ArgumentNullException.ThrowIfNull(importTcxFileUseCase);
        ArgumentNullException.ThrowIfNull(overpassClient);
        ArgumentNullException.ThrowIfNull(trailRepository);
        ArgumentNullException.ThrowIfNull(climbingOverpassClient);
        ArgumentNullException.ThrowIfNull(climbingRepository);
        ArgumentNullException.ThrowIfNull(poiOverpassClient);
        ArgumentNullException.ThrowIfNull(poiRenderer);
        ArgumentNullException.ThrowIfNull(poiRepository);
        ArgumentNullException.ThrowIfNull(roadOverpassClient);
        ArgumentNullException.ThrowIfNull(roadRenderer);
        ArgumentNullException.ThrowIfNull(planRouteUseCase);
        ArgumentNullException.ThrowIfNull(exportRouteToGpxUseCase);
        ArgumentNullException.ThrowIfNull(logger);

        this.filePicker = filePicker;
        this.fileSaver = fileSaver;
        this.mapLoader = mapLoader;
        this.autoLoader = autoLoader;
        this.tileSourceFactory = tileSourceFactory;
        this.orthoCompositor = orthoCompositor;
        this.userLocationService = userLocationService;
        this.userLocationRenderer = userLocationRenderer;
        this.settingsStore = settingsStore;
        this.regionDemLoader = regionDemLoader;
        this.offlineDownloader = offlineDownloader;

        // Subscribe to the location feed once at construction. The service stays silent until the
        // user opts in via ToggleLocationTracking; we just need to be listening so the first fix
        // lands on the right thread. The handler hops to the dispatcher because the OS may deliver
        // updates from a non-UI thread.
        if (userLocationService is not null)
        {
            userLocationService.LocationChanged += (_, fix) =>
            {
                if (MainThread.IsMainThread)
                {
                    UserLocation = fix;
                }
                else
                {
                    MainThread.BeginInvokeOnMainThread(() => UserLocation = fix);
                }
            };
            userLocationService.PermissionDenied += (_, _) =>
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsLocationTracking = false;
                    StatusMessage = Localization.AppStrings.StatusLocationPermissionDenied;
                });
            };
        }

        // Restore the saved vertical exaggeration before the partial OnXxxChanged hook
        // can fire on the default value. Clamp to [1, 5] to defend against tampered
        // preference values.
        if (settingsStore.VerticalExaggeration is { } saved)
        {
            verticalExaggeration = Math.Clamp(saved, 1.0, 5.0);
        }
        if (settingsStore.TimeOfDayHours is { } savedTime)
        {
            // Clamp to [0,24); the Atmosphere also wraps, but a tampered value (e.g. negative)
            // could land the default-day visual on a midnight initial frame which is jarring.
            timeOfDayHours = Math.Clamp(savedTime, 0.0, 24.0);
        }
        if (settingsStore.Cloudiness is { } savedCloudiness)
        {
            cloudiness = Math.Clamp(savedCloudiness, 0.0, 1.0);
        }
        if (settingsStore.Wind is { } savedWind)
        {
            wind = Math.Clamp(savedWind, 0.0, 1.0);
        }
        if (settingsStore.Snow is { } savedSnow)
        {
            snow = Math.Clamp(savedSnow, 0.0, 1.0);
        }
        if (settingsStore.Forest is { } savedForest)
        {
            forestDensity = Math.Clamp(savedForest, 0.0, 1.0);
        }
        cameraState = settingsStore.CameraState;
        this.trackRenderer = trackRenderer;
        this.trailRenderer = trailRenderer;
        this.routeRenderer = routeRenderer;
        this.climbingRenderer = climbingRenderer;
        this.importTcxFileUseCase = importTcxFileUseCase;
        this.overpassClient = overpassClient;
        this.trailRepository = trailRepository;
        this.climbingOverpassClient = climbingOverpassClient;
        this.climbingRepository = climbingRepository;
        this.poiOverpassClient = poiOverpassClient;
        this.poiRenderer = poiRenderer;
        this.poiRepository = poiRepository;
        this.roadOverpassClient = roadOverpassClient;
        this.roadRenderer = roadRenderer;
        this.planRouteUseCase = planRouteUseCase;
        this.exportRouteToGpxUseCase = exportRouteToGpxUseCase;
        this.logger = logger;
        Map = new Map();
        StatusMessage = Localization.AppStrings.StatusInitial;
    }

    /// <summary>The route most recently planned, or null when no route has been computed yet.</summary>
    public Domain.Routing.Route? LastPlannedRoute { get; private set; }

    /// <summary>
    /// Centers the map on the default starting region. Call from the page's first appearance,
    /// after the MapControl has been laid out, so the navigator has non-zero viewport dimensions.
    /// </summary>
    public void CenterOnDefaultRegion()
    {
        var (centerX, centerY) = SphericalMercator.FromLonLat(DefaultCenterLongitude, DefaultCenterLatitude);
        Map.Navigator.CenterOnAndZoomTo(new MPoint(centerX, centerY), DefaultResolution);
    }

    /// <summary>
    /// Reads the current 2D map focus so the 3D camera can be pointed at the same place when
    /// switching into 3D. Returns false until the viewport has been laid out (dimensions &gt; 0).
    /// </summary>
    /// <param name="center">Geographic centre of the current viewport.</param>
    /// <param name="resolution">Current map resolution (mercator metres per pixel).</param>
    /// <param name="viewportHeightPixels">Viewport height in pixels (for the distance↔resolution map).</param>
    public bool TryGetMapFocus(out GeoPoint center, out double resolution, out double viewportHeightPixels)
    {
        var viewport = Map.Navigator.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0 || viewport.Resolution <= 0)
        {
            center = default;
            resolution = 0;
            viewportHeightPixels = 0;
            return false;
        }

        var (longitude, latitude) = SphericalMercator.ToLonLat(viewport.CenterX, viewport.CenterY);
        center = new GeoPoint(latitude, longitude);
        resolution = viewport.Resolution;
        viewportHeightPixels = viewport.Height;
        return true;
    }

    /// <summary>
    /// Centres the 2D map on a geographic point at the given resolution. Used to make the flat map
    /// frame the same spot the 3D camera was looking at — "chcę tę górę widzieć na mapie".
    /// </summary>
    /// <param name="center">Geographic point to centre on.</param>
    /// <param name="resolution">Target resolution; ignored (centre only) when not positive/finite.</param>
    public void CenterMapOn(GeoPoint center, double resolution)
    {
        var (x, y) = SphericalMercator.FromLonLat(center.Longitude, center.Latitude);
        if (double.IsFinite(resolution) && resolution > 0)
        {
            Map.Navigator.CenterOnAndZoomTo(new MPoint(x, y), resolution);
        }
        else
        {
            Map.Navigator.CenterOn(new MPoint(x, y));
        }
    }

    /// <summary>Mapsui map model bound to the MapControl.</summary>
    public Map Map { get; }

    /// <summary>
    /// Prompts the user for an MBTiles file and loads it into the map.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    public async Task OpenMBTilesAsync()
    {
        try
        {
            string? path = await filePicker.PickFileAsync(Localization.AppStrings.FilePickerMBTiles);
            if (path is null)
            {
                return;
            }

            ExtendBasemapBounds(mapLoader.LoadMBTilesArchive(Map, path));
            StatusMessage = $"Loaded: {Path.GetFileName(path)}";
            logger.LogInformation("Loaded MBTiles archive {Path}", path);
        }
        catch (FileNotFoundException ex)
        {
            StatusMessage = Localization.AppStrings.StatusFileNotFound;
            logger.LogWarning(ex, "MBTiles file not found");
        }
        catch (Exception ex)
        {
            // Includes COMException from the Windows file picker, SQLite errors from
            // BruTile, IO errors, etc. Surface type + HRESULT (if any) + message.
            int? hresult = ex.HResult != 0 ? ex.HResult : null;
            string hresultText = hresult is not null ? $" (0x{hresult:X8})" : string.Empty;
            string detail = string.IsNullOrEmpty(ex.Message) ? "(no message)" : ex.Message;
            StatusMessage = $"Could not load archive: {ex.GetType().Name}{hresultText}: {detail}";
            logger.LogError(ex, "Failed to open MBTiles archive");
        }
    }

    /// <summary>
    /// Prompts the user for a hillshade MBTiles file and loads it as the bottom layer.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    public async Task OpenHillshadeAsync()
    {
        try
        {
            string? path = await filePicker.PickFileAsync(Localization.AppStrings.FilePickerHillshade);
            if (path is null)
            {
                return;
            }

            // Hillshade sits beneath the basemap purely as a visual under-layer; its extent never
            // constrains Overpass downloads, so the returned bounds are intentionally discarded.
            mapLoader.LoadMBTilesArchive(Map, path, MBTilesLayerKind.Hillshade);
            StatusMessage = $"{Localization.AppStrings.StatusHillshadeLoaded}: {Path.GetFileName(path)}";
            logger.LogInformation("Loaded hillshade MBTiles archive {Path}", path);
        }
        catch (FileNotFoundException ex)
        {
            StatusMessage = Localization.AppStrings.StatusFileNotFound;
            logger.LogWarning(ex, "Hillshade file not found");
        }
        catch (Exception ex)
        {
            int? hresult = ex.HResult != 0 ? ex.HResult : null;
            string hresultText = hresult is not null ? $" (0x{hresult:X8})" : string.Empty;
            string detail = string.IsNullOrEmpty(ex.Message) ? "(no message)" : ex.Message;
            StatusMessage = $"Could not load hillshade: {ex.GetType().Name}{hresultText}: {detail}";
            logger.LogError(ex, "Failed to open hillshade archive");
        }
    }

    /// <summary>
    /// Prompts the user for a TCX file and renders its first track on the map.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    public async Task OpenTcxAsync()
    {
        try
        {
            string? path = await filePicker.PickFileAsync(Localization.AppStrings.FilePickerTcx);
            if (path is null)
            {
                return;
            }

            var tracks = await importTcxFileUseCase.HandleAsync(path);
            if (tracks.Count == 0)
            {
                StatusMessage = Localization.AppStrings.StatusTcxNoTracks;
                return;
            }

            var track = tracks[0];
            trackRenderer.RenderTrack(Map, track);

            double distanceKilometers = track.ComputeDistanceMeters() / 1000.0;
            var profile = track.ComputeElevationProfile();
            StatusMessage = $"Loaded {track.Name}: {distanceKilometers:F2} km, +{profile.TotalAscentMeters:F0} m / -{profile.TotalDescentMeters:F0} m.";
            logger.LogInformation("Imported TCX {Path} with {PointCount} points", path, track.Points.Count);
        }
        catch (FileNotFoundException ex)
        {
            StatusMessage = Localization.AppStrings.StatusFileNotFound;
            logger.LogWarning(ex, "TCX file not found");
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = $"Could not parse TCX: {ex.Message}";
            logger.LogError(ex, "Failed to parse TCX file");
        }
    }

    /// <summary>
    /// Downloads OSM hiking trails for the currently visible map viewport, persists them
    /// to the local trail database, and renders them as colored polylines.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    public async Task DownloadTrailsForViewportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var bounds = ComputeDownloadBounds();
        if (bounds is null)
        {
            StatusMessage = Localization.AppStrings.StatusViewportNotReady;
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = Localization.AppStrings.StatusDownloadingTrails;

            var trails = await overpassClient.FetchHikingTrailsAsync(bounds.Value).ConfigureAwait(true);
            await ApplyTrailsAsync(trails).ConfigureAwait(true);

            StatusMessage = trails.Count == 0
                ? Localization.AppStrings.StatusNoTrailsFound
                : string.Format(System.Globalization.CultureInfo.CurrentUICulture, Localization.AppStrings.StatusTrailsLoadedFormat, trails.Count);
            logger.LogInformation("Downloaded {Count} trails for bounds {Bounds}", trails.Count, bounds);
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Overpass request failed: {ex.Message}";
            logger.LogError(ex, "Overpass HTTP request failed");
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = $"Could not parse Overpass response: {ex.Message}";
            logger.LogError(ex, "Overpass response parse failure");
        }
        catch (TaskCanceledException ex)
        {
            StatusMessage = Localization.AppStrings.StatusOverpassTimeout;
            logger.LogWarning(ex, "Overpass request timed out");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Downloads OSM climbing-tagged features for the currently visible viewport,
    /// persists them locally, and renders markers on the map.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    public async Task DownloadClimbingForViewportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var bounds = ComputeDownloadBounds();
        if (bounds is null)
        {
            StatusMessage = Localization.AppStrings.StatusViewportNotReady;
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = Localization.AppStrings.StatusDownloadingClimbing;

            var areas = await climbingOverpassClient.FetchClimbingAreasAsync(bounds.Value).ConfigureAwait(true);
            await climbingRepository.UpsertAsync(areas).ConfigureAwait(true);
            climbingRenderer.RenderClimbingAreas(Map, areas);
            Climbing3DOverlay = areas;

            StatusMessage = areas.Count == 0
                ? Localization.AppStrings.StatusNoClimbingFound
                : string.Format(System.Globalization.CultureInfo.CurrentUICulture, Localization.AppStrings.StatusClimbingLoadedFormat, areas.Count);
            logger.LogInformation("Downloaded {Count} climbing areas for bounds {Bounds}", areas.Count, bounds);
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Overpass request failed: {ex.Message}";
            logger.LogError(ex, "Overpass climbing request failed");
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = $"Could not parse Overpass response: {ex.Message}";
            logger.LogError(ex, "Overpass climbing parse failure");
        }
        catch (TaskCanceledException ex)
        {
            StatusMessage = Localization.AppStrings.StatusOverpassTimeout;
            logger.LogWarning(ex, "Overpass climbing request timed out");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Downloads OSM mountain POIs (huts, shelters, chalets, viewpoints) for the currently
    /// visible viewport, renders them as colour-coded markers, and feeds the 3D overlay.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    public async Task DownloadPoisForViewportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var bounds = ComputeDownloadBounds();
        if (bounds is null)
        {
            StatusMessage = Localization.AppStrings.StatusViewportNotReady;
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = Localization.AppStrings.StatusDownloadingPois;

            var pois = await poiOverpassClient.FetchPoisAsync(bounds.Value).ConfigureAwait(true);
            rawPois = pois;
            ApplyPoiFilter();

            // Cache to SQLite so the POIs (and their 3D night lights) survive a restart and
            // re-load from disk at startup without another Overpass round-trip. Best-effort:
            // a persistence failure must not break the in-memory result the user just got.
            if (pois.Count > 0)
            {
                try
                {
                    await poiRepository.UpsertAsync(pois).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to cache {Count} downloaded POIs", pois.Count);
                }
            }

            StatusMessage = pois.Count == 0
                ? Localization.AppStrings.StatusNoPoisFound
                : string.Format(System.Globalization.CultureInfo.CurrentUICulture, Localization.AppStrings.StatusPoisLoadedFormat, pois.Count);
            logger.LogInformation("Downloaded {Count} POIs for bounds {Bounds}", pois.Count, bounds);
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Overpass request failed: {ex.Message}";
            logger.LogError(ex, "Overpass POI request failed");
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = $"Could not parse Overpass response: {ex.Message}";
            logger.LogError(ex, "Overpass POI parse failure");
        }
        catch (TaskCanceledException ex)
        {
            StatusMessage = Localization.AppStrings.StatusOverpassTimeout;
            logger.LogWarning(ex, "Overpass POI request timed out");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Downloads OSM roads (highway ways) for the currently visible viewport, renders them as grey
    /// polylines on the 2D map, and feeds the 3D overlay.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    public async Task DownloadRoadsForViewportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var bounds = ComputeDownloadBounds();
        if (bounds is null)
        {
            StatusMessage = Localization.AppStrings.StatusViewportNotReady;
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = Localization.AppStrings.StatusDownloadingRoads;

            var roads = await roadOverpassClient.FetchRoadsAsync(bounds.Value).ConfigureAwait(true);
            rawRoads = roads;
            // Simplify once for the 3D overlay (same coarse epsilon as trails) so toggling is a cheap re-apply.
            rawRoads3D = SimplifyForOverlay3D(roads);
            ApplyRoads();

            StatusMessage = roads.Count == 0
                ? Localization.AppStrings.StatusNoRoadsFound
                : string.Format(System.Globalization.CultureInfo.CurrentUICulture, Localization.AppStrings.StatusRoadsLoadedFormat, roads.Count);
            logger.LogInformation("Downloaded {Count} roads for bounds {Bounds}", roads.Count, bounds);
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Overpass request failed: {ex.Message}";
            logger.LogError(ex, "Overpass road request failed");
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = $"Could not parse Overpass response: {ex.Message}";
            logger.LogError(ex, "Overpass road parse failure");
        }
        catch (TaskCanceledException ex)
        {
            StatusMessage = Localization.AppStrings.StatusOverpassTimeout;
            logger.LogWarning(ex, "Overpass road request timed out");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Called by the page when the user taps a point on the map. The first tap sets the
    /// origin waypoint, the second triggers route planning and renders the result.
    /// </summary>
    /// <param name="point">Tapped point in WGS-84 coordinates.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task HandleMapTapAsync(GeoPoint point)
    {
        if (IsBusy)
        {
            return;
        }

        // Route planning is an explicit mode now: a plain map tap only drops waypoints when the user has
        // turned planning on (from the ☰ menu). Otherwise the tap does nothing — so browsing / tapping
        // markers no longer accidentally starts a route.
        if (!IsRoutePlanningMode)
        {
            return;
        }

        if (waypoints.Count >= 2)
        {
            // Third tap restarts the workflow.
            waypoints.Clear();
            LastPlannedRoute = null;
            Route3DOverlay = null;
            routeRenderer.Clear(Map);
        }

        waypoints.Add(point);
        routeRenderer.RenderWaypoints(Map, waypoints);

        if (waypoints.Count == 1)
        {
            StatusMessage = Localization.AppStrings.StatusOriginSet;
            return;
        }

        await PlanRouteForWaypointsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Prompts the user for a .dem file and loads it as the active 3D terrain mesh.
    /// </summary>
    [RelayCommand]
    public async Task OpenDemAsync()
    {
        try
        {
            string? path = await filePicker.PickFileAsync(Localization.AppStrings.FilePickerDem);
            if (path is null)
            {
                return;
            }

            await LoadDemFromPathAsync(path).ConfigureAwait(true);
        }
        catch (FileNotFoundException ex)
        {
            StatusMessage = Localization.AppStrings.StatusFileNotFound;
            logger.LogWarning(ex, "DEM file not found");
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = $"Could not parse DEM: {ex.Message}";
            logger.LogError(ex, "DEM parse failure");
        }
        catch (Exception ex)
        {
            int? hresult = ex.HResult != 0 ? ex.HResult : null;
            string hresultText = hresult is not null ? $" (0x{hresult:X8})" : string.Empty;
            string detail = string.IsNullOrEmpty(ex.Message) ? "(no message)" : ex.Message;
            StatusMessage = $"Could not load DEM: {ex.GetType().Name}{hresultText}: {detail}";
            logger.LogError(ex, "Failed to load DEM");
        }
    }

    /// <summary>
    /// Toggles between flat 2D and 3D terrain mode. If 3D is enabled and no mesh has
    /// been loaded yet, the user is prompted to pick a .dem file.
    /// </summary>
    [RelayCommand]
    public async Task Toggle3DAsync()
    {
        if (Is3DMode)
        {
            Is3DMode = false;
            StatusMessage = Localization.AppStrings.Status2DMode;
            return;
        }

        if (TerrainTiles is null)
        {
            await OpenDemAsync().ConfigureAwait(true);
            if (TerrainTiles is null)
            {
                return;
            }
        }

        Is3DMode = true;
        StatusMessage = Localization.AppStrings.Status3DMode;
    }

    // Vertex budget per platform — sized to the renderer that draws the mesh AND the build
    // pipeline that produces it. The GL renderer can chew through tens of millions of verts but
    // BuildTiles allocates a couple of float arrays per vertex on the CPU first, and pushing
    // 9.5 M-vert mesh through that on a phone hung the auto-load for 30 s+ on a Samsung S22.
    //   - Windows: hardware GL + plenty of RAM, full mesh.
    //   - Android: 5 M cap balances GPU detail (LiDAR ~30 m output) with a build that finishes
    //     in a couple of seconds on mobile CPU.
    //   - iOS / Mac Catalyst still on CPU Skia path; the 2 M cap keeps them interactive.
#if WINDOWS
    private const int MaxMeshVerticesForPlatform = int.MaxValue;
#elif ANDROID
    private const int MaxMeshVerticesForPlatform = 5_000_000;
#else
    private const int MaxMeshVerticesForPlatform = 2_000_000;
#endif

    /// <summary>
    /// Picks the smallest subsample stride that brings the raster's vertex count under the
    /// platform budget, then returns the (possibly identical) decimated raster. The Bounds and
    /// no-data sentinel are preserved so every downstream lookup (overlay projection, autoload
    /// status, peak detection) stays meaningful.
    /// </summary>
    private DemRaster SubsampleRasterForRenderer(DemRaster source)
    {
        long verts = (long)source.Columns * source.Rows;
        if (verts <= MaxMeshVerticesForPlatform)
        {
            logger.LogInformation(
                "DEM within renderer budget ({Verts} <= {Cap}); no subsample (full detail)",
                verts, MaxMeshVerticesForPlatform);
            return source;
        }

        // Find the smallest step where (cols/step) × (rows/step) ≤ budget. Squared because both
        // dimensions decimate, so the ratio drops quadratically.
        double ratio = Math.Sqrt((double)verts / MaxMeshVerticesForPlatform);
        int step = Math.Max(2, (int)Math.Ceiling(ratio));
        DemRaster decimated = source.Subsample(step);
        logger.LogInformation(
            "DEM subsampled {Step}× for renderer budget: {SrcCols}×{SrcRows} → {DstCols}×{DstRows} verts",
            step, source.Columns, source.Rows, decimated.Columns, decimated.Rows);
        return decimated;
    }

    private async Task LoadDemFromPathAsync(string path)
    {
        var raster = await Task.Run(() => DemRasterReader.Read(path)).ConfigureAwait(true);
        await BuildSceneFromRasterAsync(raster, Path.GetFileName(path)).ConfigureAwait(true);
    }

    /// <summary>
    /// Shared mesh-build path for any <see cref="DemRaster"/> source — a local .dem file or an online
    /// region mosaic. Subsamples to the platform vertex budget, builds the tiled 3D mesh, detects and
    /// names peaks, and posts a status line. <c>orthoGridCols/Rows</c> carry whatever the last local
    /// map discovery set (1×1 = no ortho texturing → hypsometric colouring, fine for an online region).
    /// </summary>
    private async Task BuildSceneFromRasterAsync(DemRaster raster, string label)
    {
        // CPU-Skia 3D path (mobile + non-Windows desktop) can't keep an interactive frame rate on
        // ~9 M-vertex LiDAR meshes — orbit/pinch stutter — so subsample the loaded DEM down to a
        // vertex budget the CPU rasteriser handles cleanly. Step is the smallest stride that
        // brings cols × rows under the budget; 1 leaves the raster untouched.
        raster = SubsampleRasterForRenderer(raster);

        TerrainRaster = raster;
        UpdateTrailCoverage();
        var initialOptions = new MapaTur.Application.Terrain.TerrainMeshOptions
        {
            VerticalExaggeration = (float)Math.Clamp(VerticalExaggeration, 1.0, 5.0),
        };
        int gridCols = orthoGridCols;
        int gridRows = orthoGridRows;
        TerrainTiles = await Task.Run(() => TerrainMesh3D.BuildTiles(raster, initialOptions, orthoGridCols: gridCols, orthoGridRows: gridRows)).ConfigureAwait(true);
        OnPropertyChanged(nameof(TerrainFrame));
        // Detect summits off the UI thread so the 3D view shows labelled peaks, not just terrain.
        // Match each against the curated Tatra gazetteer so prominent peaks get a name above the
        // elevation; unmatched maxima keep their elevation-only label.
        // Dominance radius in METRES (not cells) so summit spacing is constant on the ground whatever
        // the DEM resolution — without this the high-res DEM clustered all the peaks onto the top massif
        // and left most of the map bare. MergeWithGazetteer then guarantees every known named summit
        // shows (seated on the terrain), with detected maxima filling the gaps.
        //
        // Detect on a COARSE copy: the dominance scan is O(cells × window²) and the window is a fixed
        // GROUND distance, so on a z16 1 m raster (~4.7 M cells, 550 m ≈ 366-cell window) it balloons to
        // ~10¹² ops and effectively hangs. A ~20 k-cell copy (≈20 m/px) is ample for summit spotting and
        // runs instantly; the full raster is still passed to MergeWithGazetteer so named summits seat on
        // the real terrain elevation.
        var peakOptions = new PeakDetectionOptions { DominanceRadiusMeters = 550.0, MaxPeaks = 48 };
        DemRaster peakRaster = DemRasterDownsampler.SubsampleToMaxCells(raster, maxCells: 20_000);
        Peaks3DOverlay = await Task.Run(() =>
            PeakNamer.MergeWithGazetteer(PeakDetector.Detect(peakRaster, peakOptions), TatraSummits.All, raster)).ConfigureAwait(true);
        logger.LogInformation("Loaded DEM {Label} ({Cols}x{Rows})", label, raster.Columns, raster.Rows);
        StatusMessage = $"{Localization.AppStrings.StatusDemLoaded}: {label}";
    }

    /// <summary>
    /// Streams the high-resolution GUGiK NMT 1 m terrain for a Tatra region (Morskie Oko / Rysy) via the
    /// online DEM source (GUGiK 1 m with Terrarium fallback) and builds the 3D scene — the proof path
    /// for the 1 m LiDAR source. No-op with a status note when no region source was injected.
    /// </summary>
    [RelayCommand]
    private async Task LoadTatraRegionAsync()
    {
        if (regionDemLoader is null)
        {
            StatusMessage = "Źródło terenu online niedostępne";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Pobieranie terenu 1 m (Tatry, GUGiK)…";

            // High Tatra core around Morskie Oko (see TatraDemRegion): the budget is sized so the planner
            // picks z16 (≈1.5 m/px, near GUGiK's native 1 m) over the largest area whose native-resolution
            // mosaic still fits under the Android mesh vertex cap — maximum detail without decimation.
            MapBounds bounds = TatraDemRegion.Bounds;
            int zoom = DemTilePlanner.ChooseZoomForBudget(
                bounds, TatraDemRegion.MaxTiles, TatraDemRegion.MinZoom, TatraDemRegion.MaxZoom);
            long plannedTiles = DemTilePlanner.TileCount(bounds, zoom);
            logger.LogInformation("GUGiK region load start: z{Zoom}, {Tiles} tiles planned", zoom, plannedTiles);

            var progress = new Progress<RegionLoadProgress>(p =>
            {
                int percent = p.Total > 0 ? (int)(100L * p.Completed / p.Total) : 0;
                StatusMessage = $"Pobieranie terenu 1 m… {percent}% ({p.Completed}/{p.Total} kafli)";
                logger.LogInformation("GUGiK region tiles: {Completed}/{Total} ({Percent}%)", p.Completed, p.Total, percent);
            });

            DemRaster? raster = await regionDemLoader.LoadRegionAsync(bounds, zoom, progress).ConfigureAwait(true);
            if (raster is null)
            {
                logger.LogWarning("GUGiK region load returned no raster (no network / coverage)");
                StatusMessage = "Nie udało się pobrać terenu (brak sieci/pokrycia)";
                return;
            }

            logger.LogInformation(
                "GUGiK region stitched: {Cols}x{Rows} = {Samples} samples",
                raster.Columns, raster.Rows, (long)raster.Columns * raster.Rows);

            // Drop the local terrain's ortho drape: its texture covers a DIFFERENT geographic area, so
            // stretching it over the GUGiK region mesh smears it across the wrong ground. Clearing all
            // ortho state makes the 1 m region render in clean hypsometric colour (1×1 grid = no drape).
            OrthoTexturePath = null;
            OrthoTexturePaths = null;
            OrthoTextureCells = null;
            orthoGridCols = 1;
            orthoGridRows = 1;

            await BuildSceneFromRasterAsync(raster, $"Tatry 1 m (z{zoom})").ConfigureAwait(true);

            // Land in 3D with the camera-movement pads — the 3D view (and its only movement controls)
            // is hidden in 2D mode. Loading a region must take the user INTO the headline 3D view, not
            // leave them on the flat map with no way to fly the freshly-loaded terrain.
            Is3DMode = true;

            // The region's bounds differ from the saved camera's DEM, so reframe onto the new mesh —
            // otherwise it appears tiny / off-map until the user manually taps "Reset kamery".
            TerrainReframeRequested?.Invoke(this, EventArgs.Empty);
            logger.LogInformation("GUGiK region scene built: {Label}", $"Tatry 1 m (z{zoom})");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Online region DEM load failed");
            StatusMessage = "Błąd pobierania terenu regionu";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Pre-fetches the whole Tatra range's GUGiK 1 m tiles into the on-disk cache so the app works
    /// offline in the field (no signal on the ridge). Resumable — a dropped connection just re-runs and
    /// skips what is already cached. The WiFi-or-warn gate lives in the view (it owns connectivity + the
    /// dialog); this command is invoked once the user has agreed to the download. No-op when no
    /// downloader was injected.
    /// </summary>
    [RelayCommand]
    private async Task DownloadTatraOfflineAsync()
    {
        if (offlineDownloader is null)
        {
            StatusMessage = "Pobieranie offline niedostępne";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Pobieranie Tatr offline…";
            logger.LogInformation("Offline Tatra download start: z{Zoom}", TatraOfflineRegion.DownloadZoom);

            var progress = new Progress<OfflineDownloadProgress>(p =>
            {
                int percent = p.Total > 0 ? (int)(100L * p.Completed / p.Total) : 0;
                string failed = p.Failed > 0 ? $", {p.Failed} pominięto" : string.Empty;
                StatusMessage = $"Pobieranie Tatr offline… {percent}% ({p.Completed}/{p.Total}{failed})";
            });

            OfflineDownloadResult result = await offlineDownloader.DownloadAsync(
                TatraOfflineRegion.Bounds, TatraOfflineRegion.DownloadZoom, progress).ConfigureAwait(true);

            logger.LogInformation(
                "Offline Tatra download done: {Downloaded} new, {Cached} cached, {Failed} skipped of {Total}",
                result.Downloaded, result.AlreadyCached, result.Failed, result.Total);
            StatusMessage = result.Failed == 0
                ? $"Tatry offline gotowe: {result.Total} kafli 1 m na dysku"
                : $"Tatry offline: {result.Total - result.Failed}/{result.Total} kafli (sieć/pokrycie — ponów, by dobrać resztę)";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Offline Tatra download failed");
            StatusMessage = "Błąd pobierania Tatr offline";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// LOD Etap 2 proof: a STATIC coarse base (z12 ≈ 37 m) under a single 1 m detail patch (z16) overlaid
    /// in the SAME scene-local frame (one fixed origin = base centre). The base stays static so the camera
    /// frames+roams it like region mode (works); the 1 m patch just sits on top showing local detail. No
    /// camera streaming — this validates the persistent-base + overlay architecture before adding tile
    /// streaming. Both layers from the offline cache where available. No-op without a region loader.
    /// </summary>
    [RelayCommand]
    private async Task LoadLodDemoAsync()
    {
        if (regionDemLoader is null)
        {
            StatusMessage = "LOD demo niedostępne";
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "LOD demo: baza 30 m…";

            var center = new GeoPoint(
                (TatraDemRegion.Bounds.NorthEast.Latitude + TatraDemRegion.Bounds.SouthWest.Latitude) / 2.0,
                (TatraDemRegion.Bounds.NorthEast.Longitude + TatraDemRegion.Bounds.SouthWest.Longitude) / 2.0);

            // Base: coarse, wide, STATIC. z12 ≈ 37 m/px over ~6 km.
            DemRaster? baseRaster = await regionDemLoader.LoadRegionAsync(LodTerrainWindow.Around(center, 3000), 12).ConfigureAwait(true);
            if (baseRaster is null)
            {
                StatusMessage = "LOD demo: brak bazy (sieć?)";
                return;
            }

            // Real coarse base (no artificial blockiness now that the overlay is proven). The 1 m detail
            // near the camera blends into it seamlessly; the base carries the distance.
            baseRaster = SubsampleRasterForRenderer(baseRaster);
            var baseCentre = new GeoPoint(
                (baseRaster.North + baseRaster.South) / 2.0, (baseRaster.East + baseRaster.West) / 2.0);
            logger.LogInformation(
                "LOD base: {Cols}x{Rows}, centre {Lat:F4},{Lon:F4}",
                baseRaster.Columns, baseRaster.Rows, baseCentre.Latitude, baseCentre.Longitude);

            StatusMessage = "LOD demo: kafel 1 m…";
            var options = new MapaTur.Application.Terrain.TerrainMeshOptions
            {
                VerticalExaggeration = (float)Math.Clamp(VerticalExaggeration, 1.0, 5.0),
            };

            // Clean hypsometric (no ortho drape) so the 1 m patch's detail is obvious against the coarse base.
            OrthoTexturePath = null;
            OrthoTexturePaths = null;
            OrthoTextureCells = null;
            orthoGridCols = 1;
            orthoGridRows = 1;

            var baseTiles = await Task.Run(() => TerrainMesh3D.BuildTiles(baseRaster, options)).ConfigureAwait(true);
            var combined = new List<TerrainMesh3D>(baseTiles);

            // Initial detail ring centred on the base centre, anchored to the same scene origin.
            IReadOnlyList<TerrainMesh3D>? detailTiles = await BuildDetailTilesAsync(baseCentre, baseCentre).ConfigureAwait(true);
            if (detailTiles is not null)
            {
                combined.AddRange(detailTiles);
            }

            TerrainRaster = baseRaster;
            Peaks3DOverlay = null; // skip peak detection for the demo
            lodBaseTiles = baseTiles;
            lodAnchor = baseCentre;
            lodDetailCentre = baseCentre;
            TerrainTiles = combined;
            OnPropertyChanged(nameof(TerrainFrame));
            Is3DMode = true;
            TerrainReframeRequested?.Invoke(this, EventArgs.Empty);

            // Base is framed + static; turn on detail streaming so the 1 m ring follows the camera focus
            // (Etap 3) — the view stops reframing on detail swaps so the camera roams the base freely.
            IsLodStreaming = true;
            logger.LogInformation("LOD built: {BaseTiles} base + {Total} total tiles; detail streaming ON", baseTiles.Count, combined.Count);
            StatusMessage = "LOD: baza + 1 m podąża za kamerą (Etap 3)";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LOD demo failed");
            StatusMessage = "Błąd LOD demo";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>True while LOD Etap 3 detail streaming is active (1 m ring follows the camera over a static base).</summary>
    [ObservableProperty]
    private bool isLodStreaming;

    private IReadOnlyList<TerrainMesh3D>? lodBaseTiles;
    private GeoPoint lodAnchor;
    private GeoPoint lodDetailCentre;
    private bool lodDetailLoading;
    private DateTime lastLodDetailReloadUtc = DateTime.MinValue;
    private const double LodDetailReloadThresholdMeters = 700;            // re-centre after ~700 m drift (the 2 km patch has headroom)
    private static readonly TimeSpan LodDetailReloadCooldown = TimeSpan.FromMilliseconds(1200);

    // Builds the tinted 1 m detail tiles for a patch centred on `focus`, anchored to the fixed scene origin
    // `anchor` (= base centre) so it lands correctly over the static base. Cyan tint is a diagnostic.
    private async Task<IReadOnlyList<TerrainMesh3D>?> BuildDetailTilesAsync(GeoPoint focus, GeoPoint anchor)
    {
        if (regionDemLoader is null)
        {
            return null;
        }

        // Detail covers the near-field (~4 km around the focus) — fills the visible foreground.
        DemRaster? detail = await regionDemLoader.LoadRegionAsync(LodTerrainWindow.Around(focus, 2000), 16).ConfigureAwait(true);
        if (detail is null)
        {
            logger.LogWarning("LOD detail: no 1 m raster at {Lat:F4},{Lon:F4}", focus.Latitude, focus.Longitude);
            return null;
        }

        (double dMin, double dMax) = detail.GetElevationRange();
        logger.LogInformation(
            "LOD detail @ {Lat:F4},{Lon:F4}: {Cols}x{Rows}, elev {Min:F0}-{Max:F0} m",
            focus.Latitude, focus.Longitude, detail.Columns, detail.Rows, dMin, dMax);

        // NoData fallback (rule #12): past the Polish border GUGiK returns empty/zero tiles — don't overlay
        // a flat-zero plateau, keep the coarse base showing there instead.
        if (!DemRasterCoverage.HasTerrain(detail, minTopMeters: 100))
        {
            logger.LogInformation("LOD detail @ {Lat:F4},{Lon:F4}: no 1 m coverage — keeping base", focus.Latitude, focus.Longitude);
            return null;
        }

        // Cap the detail so each reload stays smooth while flying (a 4 km z16 patch is ~7 M verts; ~1.5 M
        // keeps it clearly finer than the base yet quick to rebuild + upload).
        detail = DemRasterDownsampler.SubsampleToMaxCells(detail, maxCells: 1_500_000);

        // Cyan tint (diagnostic) so the extent of the 1 m improvement is visible — turn off for the final
        // seamless look (Etap 5).
        var detailOptions = new MapaTur.Application.Terrain.TerrainMeshOptions
        {
            VerticalExaggeration = (float)Math.Clamp(VerticalExaggeration, 1.0, 5.0),
            OverlayTintArgb = 0xFF00E5FFu,
            OverlayTintStrength = 0.45f,
        };
        return await Task.Run(() => TerrainMesh3D.BuildTiles(detail, detailOptions, projectionAnchor: anchor)).ConfigureAwait(true);
    }

    /// <summary>
    /// LOD Etap 3: the camera moved over the static base — re-centre the 1 m detail ring on the new focus
    /// (from cache, fast) and swap ONLY the detail layer. The base (and camera framing) stays put, so this
    /// never moves the camera. Debounced + cooldown so a fast pan doesn't thrash rebuilds.
    /// </summary>
    public async Task OnDetailFocusAsync(GeoPoint focus)
    {
        if (!IsLodStreaming || lodDetailLoading || lodBaseTiles is null || regionDemLoader is null)
        {
            return;
        }

        if (!LodTerrainWindow.ShouldReload(lodDetailCentre, focus, LodDetailReloadThresholdMeters))
        {
            return;
        }

        if (DateTime.UtcNow - lastLodDetailReloadUtc < LodDetailReloadCooldown)
        {
            return;
        }

        lodDetailLoading = true;
        lastLodDetailReloadUtc = DateTime.UtcNow;
        try
        {
            IReadOnlyList<TerrainMesh3D>? detailTiles = await BuildDetailTilesAsync(focus, lodAnchor).ConfigureAwait(true);
            if (detailTiles is null)
            {
                // Off coverage (rule #12): show the base ALONE — drop the stale detail patch rather than
                // leaving it hanging where the camera no longer is.
                TerrainTiles = new List<TerrainMesh3D>(lodBaseTiles);
                OnPropertyChanged(nameof(TerrainFrame));
                lodDetailCentre = focus;
                return;
            }

            var combined = new List<TerrainMesh3D>(lodBaseTiles);
            combined.AddRange(detailTiles);
            TerrainTiles = combined; // OnTilesChanged: DetailStreamingEnabled ⇒ repaint only, no reframe
            OnPropertyChanged(nameof(TerrainFrame));
            lodDetailCentre = focus;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LOD detail reload failed");
        }
        finally
        {
            lodDetailLoading = false;
        }
    }

    /// <summary>Clears any planned route and waypoints.</summary>
    [RelayCommand]
    public void ClearRoute()
    {
        waypoints.Clear();
        LastPlannedRoute = null;
        Route3DOverlay = null;
        routeRenderer.Clear(Map);
        StatusMessage = Localization.AppStrings.StatusRouteCleared;
    }

    /// <summary>
    /// Exports the last planned route to a GPX file in the application's exports folder.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [RelayCommand]
    public async Task ExportRouteAsync()
    {
        if (LastPlannedRoute is null)
        {
            StatusMessage = Localization.AppStrings.StatusExportPlanFirst;
            return;
        }

        try
        {
            string fileName = $"mapatur-route-{DateTime.Now:yyyyMMdd-HHmmss}.gpx";
            string? destinationPath = await fileSaver.PromptSavePathAsync(fileName).ConfigureAwait(true);
            if (destinationPath is null)
            {
                return;
            }

            await exportRouteToGpxUseCase.HandleAsync(LastPlannedRoute, destinationPath, fileName).ConfigureAwait(true);
            StatusMessage = $"Exported GPX to {destinationPath}";
            logger.LogInformation("Exported route to {Path}", destinationPath);
        }
        catch (IOException ex)
        {
            StatusMessage = $"Could not write GPX file: {ex.Message}";
            logger.LogError(ex, "GPX export failed");
        }
    }

    private async Task PlanRouteForWaypointsAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = Localization.AppStrings.StatusPlanningRoute;

            var request = new RouteRequest(waypoints[0], waypoints[1], RouteProfile.FastestTime);
            // Push graph build + A* off the UI thread. The use case is CPU-bound past
            // its first await; without Task.Run the window freezes on big trail sets.
            var route = await Task.Run(() => planRouteUseCase.HandleAsync(request)).ConfigureAwait(true);

            if (route is null)
            {
                StatusMessage = Localization.AppStrings.StatusNoRouteFound;
                return;
            }

            LastPlannedRoute = route;
            Route3DOverlay = route;
            routeRenderer.RenderRoute(Map, route);

            double distanceKilometers = route.TotalDistanceMeters / 1000.0;
            TimeSpan duration = TimeSpan.FromSeconds(route.TotalDurationSeconds);
            StatusMessage = $"Route: {distanceKilometers:F2} km, +{route.TotalAscentMeters:F0} m / -{route.TotalDescentMeters:F0} m, ~{duration:hh\\:mm}.";
            logger.LogInformation(
                "Planned route with {SegmentCount} segments, {Km:F2} km",
                route.Segments.Count,
                distanceKilometers);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            StatusMessage = $"Could not plan route: {ex.Message}";
            logger.LogError(ex, "Route planning failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Probes the configured map data directories and, on the first call, opens
    /// whatever it finds: basemap MBTiles (or hillshade as fallback), and DEM.
    /// Subsequent calls are no-ops so the user's manual choices aren't overwritten.
    /// </summary>
    /// <summary>
    /// Attaches a viewport-aware controller to the map so trail rendering tracks
    /// pan/zoom: the trail layer is rebuilt from the repo every time the viewport
    /// settles, pulling only intersecting trails at the appropriate Douglas–Peucker
    /// epsilon for the current zoom. Idempotent.
    /// </summary>
    public void ActivateViewportAwareTrailLayer(ILogger<ViewportAwareTrailLayerController> controllerLogger)
    {
        ArgumentNullException.ThrowIfNull(controllerLogger);
        if (viewportTrailController is not null)
        {
            return;
        }
        viewportTrailController = new ViewportAwareTrailLayerController(Map, trailRepository, trailRenderer, controllerLogger)
        {
            Filter = trail => ShowTrails && BuildTrailFilter().IsVisible(trail),
        };
        UpdateTrailCoverage(); // apply coverage if a basemap / DEM already loaded before activation
    }

    /// <summary>
    /// Composites the basemap's XYZ tiles into a single ortho texture sized to the loaded DEM and
    /// hands it to the 3D view via <see cref="OrthoTextureCells"/>. Best-effort: a failure leaves
    /// the terrain showing its hypsometric tint instead.
    /// </summary>
    private async Task TryComposite3DOrthoFromBasemapAsync(string basemapPath)
    {
        if (orthoCompositor is null || TerrainRaster is null)
        {
            return;
        }
        try
        {
            // Mesh is 1×1 ortho cells (the only grid we build when no ortho PNGs were discovered),
            // so a single composited texture spanning the DEM is exactly what the renderer expects.
            // 4096×4096 over the Tatra bbox is ~16 m/px output. 8192 was tried but on Android the
            // composite (allocating a ~256 MB intermediate RGBA8 buffer + bilinear-sampling 67 M
            // output pixels through MBTiles z15 tiles) stalled / silently failed for the user. Until
            // we have a tiled composite path that streams cell-by-cell, 4096 is the proven cap.
            // Costs ~85 MB GPU after mipmaps.
            const int cellSize = 4096;
            logger.LogInformation("Starting MBTiles 3D ortho composite {Size}x{Size} from {Path}", cellSize, cellSize, basemapPath);
            MapBounds bounds = TerrainRaster.Bounds;
            IReadOnlyList<OrthoTextureCell> cells = await Task.Run(async () =>
            {
                using ITileSource source = tileSourceFactory.OpenFromFile(basemapPath);
                return await orthoCompositor.CompositeAsync(
                    source, bounds, gridCols: 1, gridRows: 1, cellSize, cellSize).ConfigureAwait(false);
            }).ConfigureAwait(true);

            OrthoTextureCells = cells;
            logger.LogInformation(
                "Composited 3D ortho ({Count} cell(s), {Px}px) from basemap {Path}",
                cells.Count, cellSize, basemapPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MBTiles 3D ortho compositing failed for {Path}", basemapPath);
        }
    }

    public async Task AutoLoadOnStartupAsync()
    {
        if (autoLoadAttempted)
        {
            return;
        }
        autoLoadAttempted = true;

        try
        {
            // Global online orthophoto base (Esri) at the very bottom — gives the whole voivodeship
            // satellite imagery even where there's no offline basemap; tiles cache locally on view.
            // The detailed Tatry MBTiles (loaded below) stacks on top where it exists.
            OnlineOrthoBaseLayer.EnsureAdded(Map, Microsoft.Maui.Storage.FileSystem.Current.CacheDirectory);

            var discovery = autoLoader.Discover();
            logger.LogInformation(
                "Auto-load discovery: basemaps=[{Basemaps}], hillshade={Hillshade}, dem={Dem}, trails={Trails}, ortho={Ortho}",
                string.Join(", ", discovery.BasemapMBTilesPaths),
                discovery.HillshadeMBTilesPath ?? "(none)",
                discovery.DemPath ?? "(none)",
                discovery.TrailsDataPath ?? "(none)",
                discovery.OrthoTexturePath ?? "(none)");
            var loaded = new List<string>(capacity: 3);

            if (discovery.BasemapMBTilesPaths.Count > 0)
            {
                // Prioritize the basemaps by detail so a high-resolution local archive
                // (e.g. the purchased Tatra raster) is drawn on top of, and framed in
                // preference to, a coarse broad-area archive whose bounds contain it.
                // Plain enumeration order would otherwise let a coarse archive that merely
                // sorts first hijack the launch viewport — "the Polish map never shows".
                var descriptors = ReadBasemapDescriptors(discovery.BasemapMBTilesPaths);
                var plan = BasemapLoadPlanner.Plan(descriptors);

                foreach (string basemapPath in plan.LoadOrder)
                {
                    ExtendBasemapBounds(mapLoader.LoadMBTilesArchive(Map, basemapPath, MBTilesLayerKind.Basemap));
                    loaded.Add(Path.GetFileName(basemapPath));
                    logger.LogInformation("Auto-loaded basemap {Path}", basemapPath);
                }

                // The loader zooms to whichever basemap loaded first; override that to
                // frame the primary (most detailed / most local) archive instead.
                ZoomToPrimaryBasemap(descriptors, plan.PrimaryPath);

                // Remember the most-detailed archive so we can composite its tiles into the
                // 3D ortho texture once the DEM (and therefore the cell bounds) is available.
                draping3DBasemapPath = plan.PrimaryPath ?? plan.LoadOrder.LastOrDefault();
            }
            else if (discovery.HillshadeMBTilesPath is { } hillshadePath)
            {
                // Hillshade is a fallback: only auto-load it when no basemap was found. Its extent is
                // never used to clip downloads, so the returned bounds are intentionally discarded.
                mapLoader.LoadMBTilesArchive(Map, hillshadePath, MBTilesLayerKind.Hillshade);
                loaded.Add(Path.GetFileName(hillshadePath));
                logger.LogInformation("Auto-loaded hillshade (basemap fallback) {Path}", hillshadePath);
            }

            // Capture the ortho grid BEFORE building the DEM mesh, so the mesh is tiled to match the ortho
            // cells (each mesh tile samples its own texture). A single/no ortho leaves the default 1×1.
            if (discovery.OrthoTilePaths is { Count: > 0 } tilePaths)
            {
                OrthoTexturePaths = tilePaths;
                orthoGridCols = discovery.OrthoGridCols;
                orthoGridRows = discovery.OrthoGridRows;
            }

            if (discovery.DemPath is { } demPath)
            {
                await LoadDemFromPathAsync(demPath).ConfigureAwait(true);
                loaded.Add(Path.GetFileName(demPath));
                logger.LogInformation("Auto-loaded DEM {Path}", demPath);

                // Only drape the basemap on 3D when no explicit ortho PNG was discovered — a
                // checked-in ortho image is always higher fidelity than re-sampled XYZ tiles.
                if (discovery.OrthoTexturePath is null
                    && (discovery.OrthoTilePaths is null || discovery.OrthoTilePaths.Count == 0)
                    && draping3DBasemapPath is { } basemapForDraping)
                {
                    await TryComposite3DOrthoFromBasemapAsync(basemapForDraping).ConfigureAwait(true);
                }

                // Re-hydrate any POIs cached from a previous session within the DEM footprint, so
                // refuges (and their 3D night lights) reappear on launch without a fresh download.
                if (TerrainRaster is { } demRaster)
                {
                    await LoadCachedPoisAsync(demRaster.Bounds).ConfigureAwait(true);
                }
            }

            if (discovery.TrailsDataPath is { } trailsPath)
            {
                // A pre-bundled Overpass response: load the whole regional trail set from disk so the
                // app shows trails on first launch without a live download. Parse failures are caught
                // by the outer best-effort handler — the manual download button stays available.
                byte[] payload = await File.ReadAllBytesAsync(trailsPath).ConfigureAwait(true);
                IReadOnlyList<Trail> trails = OverpassResponseParser.Parse(payload);
                await ApplyTrailsAsync(trails).ConfigureAwait(true);
                loaded.Add(Path.GetFileName(trailsPath));
                logger.LogInformation("Auto-loaded {Count} pre-bundled trails from {Path}", trails.Count, trailsPath);
            }

            if (discovery.OrthoTexturePath is { } orthoPath)
            {
                // The 3D view decodes + uploads this to the GPU; nothing to parse here, just surface the path.
                OrthoTexturePath = orthoPath;
                loaded.Add(discovery.OrthoGridCols * discovery.OrthoGridRows > 1
                    ? $"ortho {discovery.OrthoGridCols}×{discovery.OrthoGridRows} tiles"
                    : Path.GetFileName(orthoPath));
                logger.LogInformation("Auto-loaded ortho ({Cols}x{Rows}) from {Path}",
                    discovery.OrthoGridCols, discovery.OrthoGridRows, orthoPath);
            }

            if (loaded.Count > 0)
            {
                StatusMessage = $"Auto-loaded: {string.Join(", ", loaded)}";
            }

            // Start in 3D when a terrain mesh is available — the app's headline view. Falls back to
            // the flat map when no DEM was found (3D would otherwise be an empty scene).
            // On non-Windows (mobile + Mac) ALWAYS start in 3D when DEM is present, and force-fall
            // even without DEM is avoided to spare an empty sky view. On Windows we also default
            // to 3D for the same reason.
            if (TerrainTiles is not null)
            {
                Is3DMode = true;
            }
            else
            {
                // No DEM found — 3D would be an empty sky, so fall back to the flat map.
                Is3DMode = false;
            }
#if ANDROID || IOS
            // Mobile-specific: explicitly prefer 3D on phones because the 2D Mapsui view is hard
            // to fit/use on small portrait screens, while 3D shows the mountains immediately —
            // the whole reason someone installs the app. The if-block above already covered this,
            // but the duplicate guard makes the intent obvious for the next reader.
            if (TerrainTiles is not null)
            {
                Is3DMode = true;
            }
#endif
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-load failed");
            // Auto-load is best-effort; manual pickers remain available.
        }
    }

    /// <summary>
    /// Reads each basemap archive's metadata (max zoom + bounds) so the planner can rank
    /// them by detail. A file whose metadata can't be read is described conservatively
    /// (coarsest, no bounds) so it still loads but can never hijack the viewport.
    /// </summary>
    private IReadOnlyList<BasemapDescriptor> ReadBasemapDescriptors(IReadOnlyList<string> paths)
    {
        var descriptors = new List<BasemapDescriptor>(paths.Count);
        foreach (string path in paths)
        {
            try
            {
                using var source = tileSourceFactory.OpenFromFile(path);
                var meta = source.GetMetadata();
                descriptors.Add(new BasemapDescriptor(path, meta.MaxZoomLevel, meta.Bounds));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
            {
                logger.LogWarning(ex, "Could not read MBTiles metadata for {Path}; treating as coarsest", path);
                descriptors.Add(new BasemapDescriptor(path, MaxZoomLevel: 0, Bounds: null));
            }
        }
        return descriptors;
    }

    /// <summary>
    /// Frames the primary basemap's bounds in the viewport. No-op when the primary has
    /// no declared bounds (then the loader's default zoom-to-first stands).
    /// </summary>
    private void ZoomToPrimaryBasemap(IReadOnlyList<BasemapDescriptor> descriptors, string? primaryPath)
    {
        if (primaryPath is null)
        {
            return;
        }

        MapBounds? bounds = descriptors.FirstOrDefault(d => d.Path == primaryPath)?.Bounds;
        if (bounds is not { } extent)
        {
            return;
        }

        var (minX, minY) = SphericalMercator.FromLonLat(extent.SouthWest.Longitude, extent.SouthWest.Latitude);
        var (maxX, maxY) = SphericalMercator.FromLonLat(extent.NorthEast.Longitude, extent.NorthEast.Latitude);
        Map.Navigator.ZoomToBox(new MRect(minX, minY, maxX, maxY));
    }

    /// <summary>
    /// Returns the bbox to use for an Overpass download: the visible viewport intersected with the
    /// Małopolska region (the area now covered by the online orthophoto base). Returns null if the
    /// viewport isn't ready or it's entirely outside the region. Zooming out to the whole region and
    /// downloading therefore fetches trails / roads / POIs across all of Małopolska, not just the Tatry
    /// basemap footprint.
    /// </summary>
    private MapBounds? ComputeDownloadBounds()
    {
        var viewport = ViewportBounds.FromMercatorExtent(GetCurrentExtent());
        if (viewport is null)
        {
            return null;
        }

        // Coverage = Małopolska (unioned with any larger loaded basemap), matching the render/trail clip.
        MapBounds coverage = MalopolskaRegion;
        if (basemapBounds is { } basemap)
        {
            coverage = coverage.Union(basemap);
        }

        return viewport.Value.Intersect(coverage);
    }

    private MRect? GetCurrentExtent()
    {
        var viewport = Map.Navigator.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0 || viewport.Resolution <= 0)
        {
            return null;
        }

        double halfWidth = viewport.Width * viewport.Resolution / 2.0;
        double halfHeight = viewport.Height * viewport.Resolution / 2.0;
        return new MRect(
            viewport.CenterX - halfWidth,
            viewport.CenterY - halfHeight,
            viewport.CenterX + halfWidth,
            viewport.CenterY + halfHeight);
    }
}