using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using MapaTur.App.Services;
using MapaTur.Application.Climbing;
using MapaTur.Application.Localization;
using MapaTur.Application.Location;
using MapaTur.Application.Maps;
using MapaTur.Application.Markers;
using MapaTur.Application.Packaging;
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

    // Validates + de-jitters raw GPS fixes (offline-robust: rejects teleports, duplicates, out-of-order and
    // coarse readings; never blanks the marker) before they reach the map. See LocationResolver + its TDD spec.
    private readonly LocationResolver locationResolver = new();
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
    private readonly MultiStopRoutePlanner multiStopPlanner;
    private readonly ExportRouteToGpxUseCase exportRouteToGpxUseCase;
    private readonly ILogger<MapPageViewModel> logger;
    private readonly OnlineRegionDemLoader? regionDemLoader;
    private readonly OfflineRegionDownloader? offlineDownloader;
    private readonly OfflinePackageService? packageService;
    private readonly DemElevationSource? elevationSource;

    // Stage 2c: the scanned baked-pyramid availability index (null when none was wired / none on disk). Backs the
    // baked-tile streaming path (UseBakedTileStreaming) — its IsBaked/Roots drive the quadtree selector and its
    // Load reads .bdt tiles off-thread for the streaming manager built in BuildLodSceneAsync.
    private readonly MapaTur.Application.Terrain.BakedTileAvailabilityIndex? bakedTileIndex;

    /// <summary>Bound to Terrain3DView.BakedElevationIndex so trail/route/road line seating can sample the same
    /// real baked-tile elevation the renderer actually draws. Set once at construction, never reassigned.</summary>
    public MapaTur.Application.Terrain.BakedTileAvailabilityIndex? BakedElevationIndex => bakedTileIndex;

    // Cache-presence gate for the LOD render loop (Krok 4b): only already-cached 1 m tiles are loaded while
    // flying, so detail streaming never triggers a WCS download. Null when no GUGiK source is wired.
    private readonly Func<DemTileKey, bool>? detailTileCached;

    // Detail tile gate for the LOD streamer — platform-split so the DESKTOP stays exactly as it works today.
    private Func<DemTileKey, bool>? DetailTileGate
    {
        get
        {
#if WINDOWS
            // Desktop is left UNTOUCHED ("nie zjeb desktopowej wersji"): its z16 cache is already populated, so
            // the existing cache-only render shows 1 m everywhere. Same gate as before — zero behaviour change.
            return detailTileCached;
#else
            // Phone: live WCS fetch on every camera move (when online) hammered z16 downloads + huge raster/mesh
            // rebuilds → memory pressure / ANR ("dławienie", system even LMK-killed other apps). DISABLED pending
            // diagnosis: cache-only, same as desktop — deterministic, no per-move network/alloc storm. This is also
            // the clean baseline for reading the LOD badge (a cache miss now shows as "z16 ON 0/N", not a stall).
            return detailTileCached;
#endif
        }
    }


    [ObservableProperty]
    private string statusMessage = string.Empty;

    // Text of the always-on LOD badge. Default = the quiet label; when the "LOD diagnostics" debug toggle is on,
    // OnDetailFocusAsync overwrites it with the live LOD detail decision (LodDetailDiagnostics) — the badge is the
    // only LOD element that is permanently visible (IsLodStreaming), so it is where the on-device ground truth must
    // go (the status pill auto-hides). This also stops the badge from lying that 1 m is on when detail is null.
    [ObservableProperty]
    private string lodBadgeText = QuietLodBadgeText;

    // The always-on find-me / teleport (search + location) bar can be collapsed to free screen space.
    [ObservableProperty]
    private bool isLocateBarExpanded = true;

    [RelayCommand]
    private void ToggleLocateBar() => IsLocateBarExpanded = !IsLocateBarExpanded;

    [ObservableProperty]
    private bool isBusy;

    /// <summary>
    /// Drives the on-map status pill. NOT just <see cref="IsBusy"/>: the pill pops up on EVERY status
    /// change and lingers a few seconds after the work ends, so the FINAL message ("LOD: baza + 1 m…",
    /// "Błąd LOD demo", guard rejections set without busy) is actually readable. Binding the pill straight
    /// to IsBusy hid the outcome the instant it appeared — success and failure were indistinguishable
    /// on-device ("przycisk nie działa").
    /// </summary>
    [ObservableProperty]
    private bool isStatusPillVisible;

    private const int StatusPillLingerMilliseconds = 5000;
    private CancellationTokenSource? statusPillHideCts;

    partial void OnIsBusyChanged(bool value)
    {
        if (value)
        {
            CancelStatusPillHide();
            IsStatusPillVisible = true;
            return;
        }

        ScheduleStatusPillHide();
    }

    // A status set while idle (toasts, guard rejections) shows the pill too, then auto-hides.
    partial void OnStatusMessageChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        IsStatusPillVisible = true;
        if (!IsBusy)
        {
            ScheduleStatusPillHide();
        }
    }

    private void CancelStatusPillHide()
    {
        statusPillHideCts?.Cancel();
        statusPillHideCts = null;
    }

    private void ScheduleStatusPillHide()
    {
        CancelStatusPillHide();
        var cts = new CancellationTokenSource();
        statusPillHideCts = cts;
        _ = HideStatusPillAfterLingerAsync(cts.Token);
    }

    private async Task HideStatusPillAfterLingerAsync(CancellationToken token)
    {
        try
        {
            // ConfigureAwait(true): resume on the UI thread — the property change re-renders the pill.
            await Task.Delay(StatusPillLingerMilliseconds, token).ConfigureAwait(true);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        IsStatusPillVisible = false;
    }

    // Default TRUE — 3D is the headline view and must be what the user sees first, with no flash of
    // the 2D map while the DEM auto-loads. Terrain3DView paints a sky-blue placeholder until the tiles
    // arrive (not the old "biała mapa"). AutoLoadOnStartupAsync falls back to 2D only when no DEM exists.
    [ObservableProperty]
    private bool is3DMode = true;

    /// <summary>True while the 3D view is running a scripted fly-through (two-way bound from the
    /// view). Used to hide the toolbar / slider chrome for a clean cinematic shot.</summary>
    [ObservableProperty]
    private bool is3DFlying;

    /// <summary>True from launch until the initial terrain scene is ready — drives the full-screen loading overlay.</summary>
    [ObservableProperty]
    private bool isInitialLoading = true;

    /// <summary>Initial-load progress in [0,1] for the loading overlay's progress bar; advances across the load stages.</summary>
    [ObservableProperty]
    private double loadProgress;

    /// <summary>
    /// Stage label for the STARTUP overlay. Deliberately separate from <see cref="StatusMessage"/>: the idle
    /// status is a call-to-action ("wczytaj MBTiles…") which is absurd behind a blocking overlay the user
    /// cannot act through — this only ever shows load-stage text.
    /// </summary>
    [ObservableProperty]
    private string initialLoadStage = Localization.AppStrings.StatusStartingUp;

    /// <summary>True while the baked-tile stream is still filling the desired set — drives the small
    /// "Doczytywanie terenu… N/M" pill with a spinner, so the user can tell mid-fill smoothness from the
    /// finished terrain ("dopóki się doczytują detale powinna być jakaś animacja i status").</summary>
    [ObservableProperty]
    private bool isStreamingDetail;

    /// <summary>Live fill progress text for the streaming pill ("Doczytywanie terenu… 296/448").</summary>
    [ObservableProperty]
    private string streamingDetailText = string.Empty;

    /// <summary>Sampler of the TRUE rendered surface (baked 1 m tiles) for the 3D view's camera
    /// anti-tunnelling floor: (lon, lat) → elevation metres, or null off-coverage. Null until a baked
    /// scene is streaming (the view then floors on the coarse raster alone).</summary>
    [ObservableProperty]
    private Func<double, double, double?>? fineElevationSampler;

    /// <summary>Whether to show the 3D on-screen chrome (sliders): only in 3D mode and not mid-flight.</summary>
    public bool Show3DChrome => Is3DMode && !Is3DFlying;

    /// <summary>
    /// Immersive mode: hides the floating UI (top menu bar + on-screen camera pads) so a phone screenshot
    /// captures just the scene. Driven by device orientation — the host page turns it ON in landscape and
    /// OFF in portrait ("przechylenie telefonu bokiem wyłącza menu, pionowo włącza").
    /// </summary>
    [ObservableProperty]
    private bool immersiveMode;

    /// <summary>Screenshot mode ("daj mi przycisk do chowania UI"): hides EVERY floating UI element — top
    /// bar, panels, pills, badges, camera pads — leaving only the small toggle button, so a clean screenshot
    /// is one tap away. Toggled by the 📷 button, which stays visible to bring the UI back.</summary>
    [ObservableProperty]
    private bool uiHidden;

    /// <summary>Whether the floating UI chrome (top menu bar + camera pads) is shown: hidden mid-flight,
    /// in immersive landscape mode, or in screenshot mode.</summary>
    public bool ChromeVisible => !Is3DFlying && !ImmersiveMode && !UiHidden;

    /// <summary>Status pill visibility gated by screenshot mode.</summary>
    public bool ShowStatusPill => IsStatusPillVisible && !UiHidden;

    /// <summary>LOD badge visibility gated by screenshot mode.</summary>
    public bool ShowLodBadge => IsLodStreaming && !UiHidden;

    /// <summary>Detail-fill pill visibility gated by screenshot mode.</summary>
    public bool ShowStreamingPill => IsStreamingDetail && !UiHidden;

    [RelayCommand]
    private void ToggleUi() => UiHidden = !UiHidden;

    partial void OnUiHiddenChanged(bool value)
    {
        OnPropertyChanged(nameof(ChromeVisible));
        OnPropertyChanged(nameof(ShowStatusPill));
        OnPropertyChanged(nameof(ShowLodBadge));
        OnPropertyChanged(nameof(ShowStreamingPill));
    }

    partial void OnIsStatusPillVisibleChanged(bool value) => OnPropertyChanged(nameof(ShowStatusPill));

    partial void OnIsLodStreamingChanged(bool value) => OnPropertyChanged(nameof(ShowLodBadge));

    partial void OnIsStreamingDetailChanged(bool value) => OnPropertyChanged(nameof(ShowStreamingPill));

    partial void OnIs3DModeChanged(bool value) => OnPropertyChanged(nameof(Show3DChrome));

    partial void OnIs3DFlyingChanged(bool value)
    {
        OnPropertyChanged(nameof(Show3DChrome));
        OnPropertyChanged(nameof(ChromeVisible));
    }

    partial void OnImmersiveModeChanged(bool value) => OnPropertyChanged(nameof(ChromeVisible));

    /// <summary>
    /// Raised after an explicit terrain load (e.g. the GUGiK region) so the host page reframes the 3D
    /// camera onto the new mesh. A freshly loaded region has different bounds than the saved camera, so
    /// without this it stays framed on the old terrain (appearing tiny / off-map until a manual reset).
    /// </summary>
    public event EventHandler? TerrainReframeRequested;

    /// <summary>Current GPS fix or null. Bound by both the 2D map renderer and the 3D view.</summary>
    [ObservableProperty]
    private UserLocation? userLocation;

    /// <summary>
    /// How current the shown fix is, re-evaluated on every fix AND on every poll heartbeat so it ages toward
    /// Stale/Lost when GPS goes silent (a gorge, no signal). Drives the "GPS nieaktualny / sygnał utracony"
    /// badge and the faded marker. <see cref="LocationResolver"/> computes it.
    /// </summary>
    [ObservableProperty]
    private LocationFreshness locationFreshness = LocationFreshness.None;

    /// <summary>True when the shown fix is no longer Live (Stale or Lost) — surfaces the staleness badge.</summary>
    public bool IsLocationStale => LocationFreshness is LocationFreshness.Stale or LocationFreshness.Lost;

    /// <summary>Badge text for a non-Live fix; empty when Live/None.</summary>
    public string LocationStatusText => LocationFreshness switch
    {
        LocationFreshness.Stale => Localization.AppStrings.LocationStale,
        LocationFreshness.Lost => Localization.AppStrings.LocationLost,
        _ => string.Empty,
    };

    partial void OnLocationFreshnessChanged(LocationFreshness value)
    {
        OnPropertyChanged(nameof(IsLocationStale));
        OnPropertyChanged(nameof(LocationStatusText));
    }

    private void RefreshLocationFreshness()
        => LocationFreshness = locationResolver.ResolveAt(DateTimeOffset.UtcNow).Freshness;

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

    /// <summary>Raised with a camera framing the 3D view should fly to — the route start when planning is
    /// turned off, or the whole route when the user taps "Pokaż trasę" — so the host view can frame it.</summary>
    public event EventHandler<Application.Routing.RouteFraming>? RouteFocusRequested;

    // Distance the camera sits back when flying to a single stop (planning turned off); whole-route framing
    // sizes its own distance from the route extent (RouteCameraFraming).
    private const double SingleStopFocusDistanceMeters = 4000.0;

    // Enabling planning closes the menu so the map is free to tap; the status line tells the user.
    // Turning it OFF with stops already picked centres the camera on the FIRST stop (the route start).
    partial void OnIsRoutePlanningModeChanged(bool value)
    {
        IsMenuOpen = false;
        StatusMessage = value
            ? Localization.AppStrings.StatusRoutePlanningOn
            : Localization.AppStrings.StatusRoutePlanningOff;

        if (!value && RouteStops.Count > 0)
        {
            RouteFocusRequested?.Invoke(
                this, new Application.Routing.RouteFraming(RouteStops[0].Location, SingleStopFocusDistanceMeters));
        }
    }

    /// <summary>True once a route has been planned and drawn — gates the summary card + "Pokaż trasę" button.</summary>
    [ObservableProperty]
    private bool hasPlannedRoute;

    /// <summary>One-line route summary (distance · ascent · time) shown under the stop list.</summary>
    [ObservableProperty]
    private string routeSummary = string.Empty;

    /// <summary>Collapses the data panel and frames the 3D camera on the whole planned route ("pokaż na mapie").</summary>
    [RelayCommand]
    private void ShowRoute()
    {
        if (LastPlannedRoute is not { } route)
        {
            return;
        }

        ActiveSection = 0; // collapse the Dane panel so the route is unobstructed
        RouteFocusRequested?.Invoke(this, Application.Routing.RouteCameraFraming.Fit(route.ToPolyline()));
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
            case "PoiParking": ShowParking = !ShowParking; break;
            case "PoiPasses": ShowPasses = !ShowPasses; break;
            case "Trails": ShowTrails = !ShowTrails; break;
            case "ExposedRoutes": ShowExposedRoutes = !ShowExposedRoutes; break;
            case "PeakNames": ShowPeakNames = !ShowPeakNames; break;
            case "NightSky": ShowNightSky = !ShowNightSky; break;
            case "CableCar": ShowCableCar = !ShowCableCar; break;
            case "Contours": ShowContours = !ShowContours; break;
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

    // Whenever the base terrain raster changes (any load path), hand it to the routing elevation source so the
    // planner can lift trail geometry onto the DEM — that's what makes "fastest time" see real mountain slopes.
    partial void OnTerrainRasterChanged(DemRaster? value) => elevationSource?.SetRaster(value);

    /// <summary>
    /// The retained 1 m LOD detail field (raster + window). Bound to the 3D view so trail / road / route
    /// vertices inside the detail window seat on the carved-deeper detail surface instead of floating over
    /// it on the coarse base. Set when a detail patch is built, cleared when the area has no 1 m coverage.
    /// </summary>
    [ObservableProperty]
    private DetailElevationField? detailElevation;

    /// <summary>
    /// Multiplier applied to elevation when building the 3D mesh. 1.0 = TRUE scale (default — the Tatras are
    /// dramatic enough at real proportions; 2× made slopes ~2× too steep / needle-like vs photos). Higher
    /// values exaggerate vertical relief; the user can still raise it on the "Pion" slider. Changing this
    /// rebuilds the mesh from the current raster.
    /// </summary>
    [ObservableProperty]
    private double verticalExaggeration = 1.0;

    /// <summary>
    /// Time of day in hours, [0,24). Drives the <see cref="Atmosphere"/> sun / sky / fog model
    /// the 3D renderer samples each frame. 16.0 = mid-afternoon (default — a lower sun rakes the
    /// slopes and models the relief far better than a high noon sun), 18.0 = sunset, 6.0 = sunrise,
    /// 0.0 = midnight. Persisted in <see cref="settingsStore"/>.
    /// </summary>
    [ObservableProperty]
    private double timeOfDayHours = 16.0;

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
    /// Storm intensity, [0,1]: 0 = no storm (default), 1 = heavy storm. The further right, the darker the
    /// clouds turn and the more often lightning strikes (the renderer flashes the clouds + ground). Feeds
    /// <see cref="Atmosphere"/>. Persisted.
    /// </summary>
    [ObservableProperty]
    private double storm;

    /// <summary>
    /// Forest density, [0,1]: 0 = no trees, 1 = densest. Drives how many trees the 3D renderer scatters
    /// over the terrain below the treeline (bound into <c>Terrain3DView.ForestDensity</c>). Persisted.
    /// </summary>
    [ObservableProperty]
    private double forestDensity = 0.6;

    /// <summary>
    /// Peak-name label radius in metres: summit name labels only show for peaks within this camera
    /// distance, so the user can trim distant label clutter. The "Nazwy szczytów" toggle gates the whole
    /// summit overlay; this slider sets how far it reaches. Bound into
    /// <c>Terrain3DView.PeakLabelRadiusMeters</c>. Default 15 km. Persisted.
    /// </summary>
    [ObservableProperty]
    private double peakLabelRadiusMeters = 15000;

    /// <summary>Human-readable peak-label radius for the slider value chip, e.g. "15.0 km".</summary>
    public string PeakLabelRadiusText => $"{PeakLabelRadiusMeters / 1000.0:0.0} km";

    /// <summary>
    /// Live atmospheric model derived from <see cref="TimeOfDayHours"/>, <see cref="Cloudiness"/>,
    /// <see cref="Wind"/> and <see cref="Snow"/>. Recomputed whenever any change and bound straight
    /// into <c>Terrain3DView.Atmosphere</c>. Cheap to build so deriving per change is fine.
    /// </summary>
    public Atmosphere Atmosphere => new((float)TimeOfDayHours, (float)Cloudiness, (float)Wind, (float)Snow, (float)Storm);

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

    partial void OnStormChanged(double value)
    {
        settingsStore.Storm = value;
        OnPropertyChanged(nameof(Atmosphere));
    }

    partial void OnForestDensityChanged(double value)
    {
        // Forest density is NOT part of the Atmosphere — the view binds ForestDensity directly and
        // rebuilds the tree placement when it changes. Just persist here.
        settingsStore.Forest = value;
        OnPropertyChanged(nameof(EffectiveForestDensity));
    }

    partial void OnPeakLabelRadiusMetersChanged(double value)
    {
        // Not part of the Atmosphere — the view binds PeakLabelRadiusMeters straight into the peak
        // projector. Persist + refresh the value chip.
        settingsStore.PeakLabelRadiusMeters = value;
        OnPropertyChanged(nameof(PeakLabelRadiusText));
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

    /// <summary>
    /// Selected UI language: 0 = Polski, 1 = English. Mirrors the Ustawienia segmented control (highlighted
    /// via IntEquals). The initial value is restored from <see cref="settingsStore"/> in the constructor by
    /// setting the backing field directly, so launch does not trigger a restart.
    /// </summary>
    [ObservableProperty]
    private int languageIndex;

    partial void OnLanguageIndexChanged(int value)
    {
        string code = value == 1 ? AppLanguage.English : AppLanguage.Polish;
        if (string.Equals(settingsStore.Language, code, StringComparison.Ordinal))
        {
            return;
        }

        // Persist the choice, then soft-restart the UI so every compile-time {x:Static} string
        // re-resolves under the new culture (a live re-bind would need a markup extension).
        settingsStore.Language = code;
        App.SwitchLanguage(code);
    }

    /// <summary>Sets the UI language from the Ustawienia segmented control (index as string).</summary>
    [RelayCommand]
    private void SelectLanguage(string? index)
    {
        if (int.TryParse(index, out int i))
        {
            LanguageIndex = Math.Clamp(i, 0, 1);
        }
    }

    /// <summary>
    /// Formats a localized status string under the current UI culture (so numbers use the user's
    /// language conventions). Shorthand for the status messages assigned throughout the view-model.
    /// </summary>
    private static string Fmt(string format, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentUICulture, format, args);

    /// <summary>Human-readable summary of cached record counts, shown in the Ustawienia "Cache" block.</summary>
    [ObservableProperty]
    private string cacheSummary = "—";

    // Refresh the cache counts whenever the Ustawienia panel (section 6) opens, so the figure is live.
    partial void OnActiveSectionChanged(int value)
    {
        if (value == 6)
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
            CacheSummary = string.Format(
                System.Globalization.CultureInfo.CurrentUICulture,
                Localization.AppStrings.CacheSummaryFormat, trails, pois, climbing);
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
            StatusMessage = Localization.AppStrings.StatusCacheCleared;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to clear cache");
            StatusMessage = Localization.AppStrings.StatusCacheClearFailed;
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
    /// Whether the LOD badge shows the full detail diagnostic (zoom tier, ON/OFF, cache ratio, mesh step,
    /// distance) instead of the quiet "LOD 1 m" label. Toggled from Ustawienia → DEBUG; works on both
    /// desktop and mobile. The next camera move repaints the badge; turning it off restores the quiet label.
    /// </summary>
    [ObservableProperty]
    private bool showLodDiagnostics;

    partial void OnShowLodDiagnosticsChanged(bool value)
    {
        if (!value)
        {
            LodBadgeText = QuietLodBadgeText;
        }
    }

    /// <summary>The quiet LOD badge label shown when the detail diagnostic is off.</summary>
    private const string QuietLodBadgeText = "LOD 1 m";

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
    // "Centre on me" sets this so the NEXT fix recentres the 3D camera, even though the fix arrives a moment
    // after the button is tapped (when tracking had to be started first).
    private bool centerOnNextFix;

    partial void OnUserLocationChanged(UserLocation? value)
    {
        userLocationRenderer?.RenderUserLocation(Map, value);

        if (centerOnNextFix && value is { } fix)
        {
            centerOnNextFix = false;
            RaiseCenterOnLocation(fix);
        }

        // Follow-camera tracking option: chase the user from behind on every fix, oriented along the
        // detected direction of travel (held when standing still). Only while tracking is active.
        if (FollowCamera && IsLocationTracking && value is { } followFix)
        {
            FollowRequested?.Invoke(this, new FollowCameraRequest(followFix.Position, ComputeHeadingDegrees(followFix)));
        }
    }

    /// <summary>
    /// "Follow camera" tracking option: while GPS tracking is on, the 3D camera chases the user from behind,
    /// oriented along the detected direction of travel. Persisted; takes effect only while tracking is on.
    /// </summary>
    [ObservableProperty]
    private bool followCamera;

    // Last fix used as the heading baseline, and the most recent confident travel bearing (degrees).
    private UserLocation? lastHeadingFix;
    private double? lastHeadingDegrees;

    // Below this movement (m) between fixes the heading is HELD — GPS jitter while standing still must not
    // spin the camera. ~4 m clears typical consumer-GPS noise; real walking advances past it each fix.
    private const double FollowHeadingMinMoveMeters = 4.0;

    partial void OnFollowCameraChanged(bool value)
    {
        settingsStore.FollowCamera = value;
        if (!value)
        {
            ResetFollowHeading();
            return;
        }

        // Snap straight into the follow view if we are already tracking and have a fix.
        if (IsLocationTracking && UserLocation is { } fix)
        {
            FollowRequested?.Invoke(this, new FollowCameraRequest(fix.Position, ComputeHeadingDegrees(fix)));
        }
    }

    private void ResetFollowHeading()
    {
        lastHeadingFix = null;
        lastHeadingDegrees = null;
    }

    // Updates + returns the travel bearing from consecutive fixes. Returns the held bearing (possibly null
    // on the first fixes) until the user has moved past the jitter threshold, then the fresh bearing.
    private double? ComputeHeadingDegrees(UserLocation fix)
    {
        if (lastHeadingFix is { } prev)
        {
            if (prev.Position.HaversineDistanceMetersTo(fix.Position) >= FollowHeadingMinMoveMeters)
            {
                lastHeadingDegrees = prev.Position.InitialBearingDegreesTo(fix.Position);
                lastHeadingFix = fix;
            }
        }
        else
        {
            lastHeadingFix = fix;
        }

        return lastHeadingDegrees;
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
            ResetFollowHeading();
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

    /// <summary>
    /// "Centre on me": brings the 3D camera to the live GPS position — the field-test gap "nie ma opcji
    /// wycentruj na mnie". If a fix is already in hand it centres at once; otherwise it starts tracking and
    /// centres on the first fix that arrives. Reuses the place-teleport path (TeleportRequested -> TeleportTo).
    /// </summary>
    [RelayCommand]
    public async Task CenterOnMeAsync()
    {
        if (UserLocation is { } fix)
        {
            RaiseCenterOnLocation(fix);
            return;
        }

        // No fix yet — make sure the GPS loop is running, then centre on the first one that comes in.
        centerOnNextFix = true;
        if (userLocationService is { IsTracking: false })
        {
            bool started = await userLocationService.StartAsync().ConfigureAwait(true);
            IsLocationTracking = started;
            if (started)
            {
                StatusMessage = Localization.AppStrings.StatusLocationTrackingStarted;
            }
        }
    }

    private void RaiseCenterOnLocation(UserLocation fix)
    {
        // Name is unused by the camera move (TeleportTo reads only Location + elevation); elevation is left
        // null so TeleportTo samples the DEM under the fix, exactly like a place teleport.
        var here = new Domain.Routing.RouteWaypoint(
            "GPS", fix.Position, Domain.Routing.WaypointKind.TrailPoint, null);
        TeleportRequested?.Invoke(this, here);
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
        // flight and stashes the trailing value for the completion handler to replay.
        if (meshRebuildCoalescer.RequestRebuild(value) is { } toBuild)
        {
            // The LOD scene (ring base + baked/runtime detail streaming) MUST rebuild through its own
            // pipeline: the legacy StartMeshRebuild replaces the scene with a whole-raster BuildTiles mesh
            // that the very next stream publish overwrites with the OLD lodBaseTiles + resident baked tiles
            // (all built at the exaggeration captured once at scene setup) — i.e. the slider visibly did
            // nothing ("pion w widoku nie działa"). Exaggeration is baked into vertex Z at build time, so
            // the base AND the streaming manager both have to be rebuilt with the new value.
            if (IsLodStreaming)
            {
                StartLodExaggerationRebuild(toBuild);
            }
            else
            {
                StartMeshRebuild(toBuild);
            }
        }
    }

    // Whether the current LOD scene's base was built as a ring-LOD adaptive plan (local whole-Tatra DEM)
    // vs the legacy uniform subsample (online fallback window) — needed to rebuild the SAME kind of base
    // when the vertical exaggeration changes.
    private bool lodRingBase;

    /// <summary>
    /// Rebuilds the ACTIVE LOD scene at a new vertical exaggeration: the ring/uniform base via the same
    /// builder the scene setup used, then a fresh baked-streaming manager (its mesh options — including
    /// exaggeration — are captured at construction and baked into every resident tile's vertex Z), then a
    /// stream kick so the near-field detail refills WITHOUT waiting for the next camera move.
    /// </summary>
    private void StartLodExaggerationRebuild(double value)
    {
        if (TerrainRaster is not { } raster)
        {
            return;
        }

        GeoPoint focus = lodAnchor; // scene anchor — IsLodStreaming guarantees the scene set it

        var options = new MapaTur.Application.Terrain.TerrainMeshOptions
        {
            VerticalExaggeration = (float)Math.Clamp(value, 1.0, 5.0),
        };
        MapaTur.Application.Terrain.OrthoCoverage? coverage = lodOrthoCoverage;
        bool ringBase = lodRingBase;

        _ = Task.Run(() => BuildLodBaseTiles(raster, focus, options, coverage, ringBase))
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    logger.LogError(t.Exception, "LOD exaggeration rebuild failed");
                    meshRebuildCoalescer.CompleteRebuild();
                    return;
                }

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    lodBaseTiles = MarkBaseSkin(t.Result);
                    lodBaseTileFootprints = ComputeBaseTileFootprints(t.Result);
                    detailMeshCache.Clear(); // exaggeration is part of every cached block's identity
                    // Fresh manager = fresh residents, all rebuilt at the new exaggeration on the next update.
                    SetUpBakedStreaming(options.VerticalExaggeration);
                    // Publish the rescaled base immediately (the user is mid-drag — the terrain must move NOW);
                    // the stream refills the near-field detail on top a beat later.
                    TerrainTiles = lodBaseTiles;
                    OnPropertyChanged(nameof(TerrainFrame));
                    KickBakedStream();
                    if (meshRebuildCoalescer.CompleteRebuild() is { } trailing)
                    {
                        StartLodExaggerationRebuild(trailing);
                    }
                });
            }, TaskScheduler.Default);
    }

    // Last camera/viewport handed to StreamBakedDetailAsync — lets a NON-camera event (the exaggeration
    // rebuild) re-run the stream immediately from the current pose instead of waiting for the next move.
    private MapaTur.Application.Terrain.Camera3D? lastBakedStreamCamera;
    private int lastBakedStreamViewportH;
    private int lastBakedStreamViewportW;

    private void KickBakedStream()
    {
        if (lastBakedStreamCamera is { } cam)
        {
            lastBakedStreamUtc = DateTime.MinValue; // bypass the debounce — this is an explicit refresh
            _ = StreamBakedDetailAsync(cam, lastBakedStreamViewportH, lastBakedStreamViewportW);
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

    // Watercourses (waterway=river|stream ways + waterfall nodes) — painted INTO the terrain as a shiny
    // water decal, not drawn as floating lines. Null client = feature unavailable (tests/legacy wiring).
    private readonly MapaTur.Application.Waterways.IWaterwayOverpassClient? waterwayClient;

    /// <summary>Watercourse polylines for the 3D water decal, or null when hidden/not downloaded.</summary>
    [ObservableProperty]
    private IReadOnlyList<Trail>? waterways3DOverlay;

    /// <summary>Waterfall points rendered as bright foam accents on their streams.</summary>
    [ObservableProperty]
    private IReadOnlyList<MapaTur.Application.Waterways.Waterfall>? waterfalls3DOverlay;

    // Last-downloaded watercourses (simplified for 3D), kept so the toggle re-applies without a refetch.
    private IReadOnlyList<Trail>? rawWaterways3D;
    private IReadOnlyList<MapaTur.Application.Waterways.Waterfall>? rawWaterfalls;

    /// <summary>
    /// Master show/hide for the RUNTIME watercourse decal (streams/waterfalls painted via the trail mask).
    /// Default OFF since 2026-07-02: watercourses are baked INTO the ortho cells instead
    /// (testdata/maps/bake-waterways-into-ortho.py, TILE-PRODUCTION.md 3.9) — zero runtime cost, natural look.
    /// The decal path stays functional behind this switch for A/B comparison and for areas without ortho.
    /// </summary>
    [ObservableProperty]
    private bool showWaterways;

    partial void OnShowWaterwaysChanged(bool value) => ApplyWaterways();

    private void ApplyWaterways()
    {
        if (ShowWaterways)
        {
            Waterways3DOverlay = rawWaterways3D;
            Waterfalls3DOverlay = rawWaterfalls;
        }
        else
        {
            Waterways3DOverlay = null;
            Waterfalls3DOverlay = null;
        }
    }

    /// <summary>
    /// Downloads OSM watercourses (stream/river ways + waterfall nodes) for the visible viewport and feeds
    /// the 3D water decal.
    /// </summary>
    [RelayCommand]
    public async Task DownloadWaterwaysForViewportAsync()
    {
        if (IsBusy || waterwayClient is null)
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
            StatusMessage = Localization.AppStrings.StatusDownloadingWaterways;

            var result = await waterwayClient.FetchWaterwaysAsync(bounds.Value).ConfigureAwait(true);
            rawWaterways3D = SimplifyForOverlay3D(result.Streams);
            rawWaterfalls = result.Waterfalls;
            ApplyWaterways();

            StatusMessage = result.Streams.Count == 0
                ? Localization.AppStrings.StatusNoWaterwaysFound
                : string.Format(System.Globalization.CultureInfo.CurrentUICulture, Localization.AppStrings.StatusWaterwaysLoadedFormat, result.Streams.Count, result.Waterfalls.Count);
            logger.LogInformation(
                "Downloaded {Streams} watercourses + {Falls} waterfalls for bounds {Bounds}",
                result.Streams.Count, result.Waterfalls.Count, bounds);
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = Fmt(Localization.AppStrings.StatusOverpassRequestFailedFormat, ex.Message);
            logger.LogError(ex, "Overpass waterway request failed");
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = Fmt(Localization.AppStrings.StatusOverpassParseFailedFormat, ex.Message);
            logger.LogError(ex, "Overpass waterway parse failure");
        }
        catch (TaskCanceledException ex)
        {
            StatusMessage = Localization.AppStrings.StatusOverpassTimeout;
            logger.LogWarning(ex, "Overpass waterway request timed out");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Exposed/guide-route overlay for the 3D view (sac_scale / via_ferrata ways), drawn as dots; null when hidden.</summary>
    [ObservableProperty]
    private IReadOnlyList<Trail>? exposedRoutes3DOverlay;

    // Last-downloaded exposed routes (simplified for the 3D overlay), kept so the toggle re-applies without a refetch.
    private IReadOnlyList<Trail>? rawExposedRoutes3D;

    /// <summary>Master show/hide for the exposed/guide routes (dotted) overlay.</summary>
    [ObservableProperty]
    private bool showExposedRoutes = true;

    partial void OnShowExposedRoutesChanged(bool value) => ApplyExposedRoutes();

    /// <summary>Re-applies exposed-route visibility to the 3D overlay from the last download.</summary>
    private void ApplyExposedRoutes()
    {
        ExposedRoutes3DOverlay = (ShowExposedRoutes && rawExposedRoutes3D is not null) ? rawExposedRoutes3D : null;
    }

    /// <summary>Master show/hide for all trails; when off, no trail renders regardless of the colour/region filter.</summary>
    /// <summary>Whether the orthophoto drape is shown on the 3D terrain (else hypsometric shading).</summary>
    [ObservableProperty] private bool showOrtho = true;

    /// <summary>Whether summit glyphs + elevation labels are drawn over the 3D terrain.</summary>
    [ObservableProperty] private bool showPeakNames = true;

    /// <summary>Easter egg: a dark tower topped by the glowing Eye of Sauron on Świnica. Off by default. Persisted.</summary>
    [ObservableProperty] private bool showSauronTower;

    /// <summary>Easter egg: eagles soaring on thermals over the Orla Perć ridge. Off by default. Persisted.</summary>
    [ObservableProperty] private bool showEagles;

    /// <summary>
    /// Animated atmosphere effects (drifting clouds, lightning, eagles, bloom/god-rays + the continuous repaint
    /// that powers them). On by default. Turning it OFF stops the per-frame redraw when the camera is still, so
    /// the GPU idles instead of rendering ~15 heavy fps forever — the main battery lever on mobile. Persisted.
    /// </summary>
    [ObservableProperty] private bool atmosphereEffectsEnabled = true;

    /// <summary>Whether the night-sky pass (stars + name labels + constellation lines) is drawn after dusk.</summary>
    [ObservableProperty] private bool showNightSky = true;

    /// <summary>Whether the Kasprowy Wierch cable-car overlay (cables + station masts) is drawn in 3D.</summary>
    [ObservableProperty] private bool showCableCar; // off by default — keeps the peak clean (toggle via the "🚠 Kolejka" chip)

    /// <summary>Whether the contour-line (warstwice) overlay is draped on the 3D relief.</summary>
    [ObservableProperty] private bool showContours = true;

    /// <summary>Whether the avalanche slope-steepness ("Mapa nachylenia") shading is active.</summary>
    [ObservableProperty] private bool slopeMapMode;

    /// <summary>Premium menu "Skały": blend a rock material onto steep faces where the top-down ortho smears.</summary>
    [ObservableProperty] private bool rockMaterialOn = true;

    /// <summary>Premium menu "Biomy": paint the base albedo by elevation-zone biomes (hala/piargi/skała/śnieg/lód).</summary>
    [ObservableProperty] private bool biomeMaterialOn;

    // Default ON — trails are core to a hiking map, so they show by default (field complaint: "szlaki się nie
    // ładują... miała być na defaulcie dociąganie"). AutoLoad seats them OFFLINE-FIRST from the SQLite cache
    // (a prior session's download) and only fetches from Overpass when nothing is cached and there's signal
    // (LoadOrFetchTrailsOnStartupAsync). POI categories stay opt-in from the Dane panel.
    [ObservableProperty] private bool showTrails = true;

    partial void OnShowTrailsChanged(bool value)
    {
        OnTrailFilterChanged();
        if (value && rawTrails is null)
        {
            // Toggled on with nothing loaded this session → pull them in (cache-first, then Overpass) instead
            // of showing nothing. Same path AutoLoad uses, so an offline cache shows immediately.
            _ = LoadOrFetchTrailsOnStartupAsync();
        }
    }

    /// <summary>
    /// Seats the trail set on startup / first "Szlaki" use: OFFLINE-FIRST from the SQLite cache (a prior
    /// session's download), falling back to a live Overpass fetch only when nothing is cached. An offline
    /// fetch fails quietly. Best-effort — never blocks startup.
    /// </summary>
    private async Task LoadOrFetchTrailsOnStartupAsync()
    {
        if (!ShowTrails || rawTrails is not null || TerrainRaster is not { } raster)
        {
            return;
        }

        try
        {
            IReadOnlyList<Trail> cached = await trailRepository.FindIntersectingAsync(raster.Bounds).ConfigureAwait(true);
            if (cached.Count > 0)
            {
                await ApplyTrailsAsync(cached).ConfigureAwait(true);
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Startup cached-trail load failed");
        }

        await DownloadTrailsForViewportAsync().ConfigureAwait(true);
    }

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

        // A restored route (stops loaded but no line yet, because trails weren't available) can now be planned
        // over the freshly loaded trail graph.
        if (RouteStops.Count >= 2 && !HasPlannedRoute)
        {
            await ReplanRouteAsync().ConfigureAwait(true);
        }
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

    // ── Tourist-map route chain ──────────────────────────────────────────────────────────────────
    // Route planning is now a chain of named stops (peaks / huts / lakes / tapped trail points), picked
    // from the searchable list OR by tapping near a place on the map. Each consecutive pair is routed
    // over the trail graph and concatenated (MultiStopRoutePlanner). The gazetteer is rebuilt whenever
    // the peak / lake / POI sets change.
    private PlaceGazetteer placeGazetteer = new();

    /// <summary>Ordered stops the route passes through (start → … → end).</summary>
    public System.Collections.ObjectModel.ObservableCollection<Domain.Routing.RouteWaypoint> RouteStops { get; } = new();

    /// <summary>Search results for the current <see cref="PlaceQuery"/> (the place picker list).</summary>
    public System.Collections.ObjectModel.ObservableCollection<Domain.Routing.RouteWaypoint> PlaceResults { get; } = new();

    /// <summary>Search box text driving the place picker.</summary>
    [ObservableProperty]
    private string placeQuery = string.Empty;

    // A map tap within this radius of a named place adds THAT place (so you can "tap Rysy"); farther
    // taps add a plain trail point at the tapped spot.
    private const double StopSnapRadiusMeters = 250.0;

    partial void OnPlaceQueryChanged(string value) => RefreshPlaceResults();

    private void RefreshPlaceResults()
    {
        PlaceResults.Clear();
        foreach (Domain.Routing.RouteWaypoint w in placeGazetteer.Search(PlaceQuery, 30))
        {
            PlaceResults.Add(w);
        }

        logger.LogInformation(
            "Place search '{Query}': gazetteer={Total}, results={Results}",
            PlaceQuery, placeGazetteer.All.Count, PlaceResults.Count);
    }

    /// <summary>Raised when the user picks a place to fly the 3D camera over — the page moves the camera there.</summary>
    public event EventHandler<Domain.Routing.RouteWaypoint>? TeleportRequested;

    /// <summary>A follow-camera update for the 3D view: the user's position and, when known, their travel bearing.</summary>
    public sealed record FollowCameraRequest(GeoPoint Position, double? BearingDegrees);

    /// <summary>Raised on each GPS fix while the follow-camera option is on, so the view can chase the user.</summary>
    public event EventHandler<FollowCameraRequest>? FollowRequested;

    [RelayCommand]
    private void TeleportToPlace(Domain.Routing.RouteWaypoint? place)
    {
        if (place is null)
        {
            return;
        }

        ActiveSection = 0; // close the panel so the flown-to place is unobstructed
        TeleportRequested?.Invoke(this, place);
    }

    // Rebuilds the searchable place picker from the current named-place sets. Cheap; called after the
    // peak / lake / POI data changes. Uses EffectivePois() so the bundled offline gazetteers (huts +
    // Tatra-core cols/passes, incl. Honoratka) are searchable even with no POI download; ApplyPoiFilter
    // then makes those same bundled features visible as markers without one.
    private void RebuildPlaceGazetteer()
    {
        placeGazetteer = new PlaceGazetteer(Peaks3DOverlay, MountainLakeData.All, EffectivePois());
        RefreshPlaceResults();
        ApplyPoiFilter();
    }

    [ObservableProperty]
    private IReadOnlyList<MapaTur.Domain.Climbing.ClimbingArea>? climbing3DOverlay;

    [ObservableProperty]
    private IReadOnlyList<MapaTur.Domain.Pois.MountainPoi>? pois3DOverlay;

    // Last-downloaded POIs, kept so the per-type filter can re-apply without re-querying Overpass.
    private IReadOnlyList<MapaTur.Domain.Pois.MountainPoi>? rawPois;

    // Per-kind POI visibility toggles. Default all OFF after a fresh install — the user opts in to
    // each category from the Dane panel (only peak names start on). Checking any reveals that kind.
    [ObservableProperty]
    private bool showHuts;
    [ObservableProperty]
    private bool showWildernessHuts;
    [ObservableProperty]
    private bool showChalets;
    [ObservableProperty]
    private bool showShelters;
    [ObservableProperty]
    private bool showViewpoints;
    [ObservableProperty]
    private bool showParking;
    [ObservableProperty]
    private bool showPasses;

    partial void OnShowHutsChanged(bool value) => ApplyPoiFilter();
    partial void OnShowWildernessHutsChanged(bool value) => ApplyPoiFilter();
    partial void OnShowChaletsChanged(bool value) => ApplyPoiFilter();
    partial void OnShowSheltersChanged(bool value) => ApplyPoiFilter();
    partial void OnShowViewpointsChanged(bool value) => ApplyPoiFilter();
    partial void OnShowParkingChanged(bool value) => ApplyPoiFilter();
    partial void OnShowPassesChanged(bool value) => ApplyPoiFilter();

    /// <summary>Returns true when a POI of the given kind is currently enabled in the type filter.</summary>
    private bool IsPoiKindVisible(MapaTur.Domain.Pois.PoiKind kind) => kind switch
    {
        MapaTur.Domain.Pois.PoiKind.Hut => ShowHuts,
        MapaTur.Domain.Pois.PoiKind.WildernessHut => ShowWildernessHuts,
        MapaTur.Domain.Pois.PoiKind.Chalet => ShowChalets,
        MapaTur.Domain.Pois.PoiKind.Shelter => ShowShelters,
        MapaTur.Domain.Pois.PoiKind.Viewpoint => ShowViewpoints,
        MapaTur.Domain.Pois.PoiKind.Parking => ShowParking,
        MapaTur.Domain.Pois.PoiKind.Pass => ShowPasses,
        _ => true,
    };

    /// <summary>Re-applies the per-kind filter to the effective POI set across the 2D map and 3D view.</summary>
    private void ApplyPoiFilter()
    {
        var filtered = EffectivePois().Where(poi => IsPoiKindVisible(poi.Kind)).ToList();
        poiRenderer.RenderPois(Map, filtered);
        Pois3DOverlay = filtered;
    }

    /// <summary>
    /// The POI set actually shown + searched: downloaded/cached POIs unioned with the bundled offline
    /// gazetteers (huts + Tatra-core cols/passes), so well-known huts and przełęcze (incl. Honoratka) are
    /// visible AND searchable without a POI download. Downloaded OSM features win — a bundled entry whose
    /// name already arrived from OSM is skipped (dedup by name).
    /// </summary>
    private IReadOnlyList<MapaTur.Domain.Pois.MountainPoi> EffectivePois()
    {
        // PoiMerger: downloaded wins; bundled fills gaps; duplicates collapse by NAME or by PROXIMITY within
        // one kind (the "Schronisko nad Morskim Okiem" vs "Schronisko PTTK Morskie Oko" double label);
        // suppressed downloaded names drop entirely (the Honoratka node). Logged deltas for observability.
        IReadOnlyList<MapaTur.Domain.Pois.MountainPoi> bundled =
            TatraHuts.All.Concat(TatraPasses.All).ToList();
        IReadOnlyList<MapaTur.Domain.Pois.MountainPoi> merged =
            PoiMerger.Merge(rawPois, bundled, TatraPasses.SuppressedOsmNames);
        int dropped = ((rawPois?.Count ?? 0) + bundled.Count) - merged.Count;
        if (dropped > 0)
        {
            logger.LogInformation("EffectivePois: {Dropped} duplicate/suppressed POI(s) collapsed in the merge", dropped);
        }

        return merged;
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
            RebuildPlaceGazetteer();
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

    // Geographic extent the bundled ortho covers (= the auto-loaded DEM bounds the ortho was generated for),
    // captured at auto-load. Used to geo-reference ortho UV when draping it on the streaming-LOD sub-region
    // tiles (whose extent differs from the ortho). Null until an ortho + DEM have auto-loaded.
    private MapBounds? bundledOrthoCoverageBounds;

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
    /// <param name="multiStopPlanner">Plans the chained tourist-map route through all stops.</param>
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
    /// <param name="gugikDemSource">Optional GUGiK 1 m tile source; supplies the cache-only availability check used by LOD detail streaming; null disables it.</param>
    /// <param name="packageService">Optional region-package service (download DEM/ortho packages from the server); null disables the "download data packages" button.</param>
    /// <param name="elevationSource">Optional routing elevation source; the base DEM is handed to it on load so "fastest time" routing sees real slopes; null disables elevation-aware routing.</param>
    /// <param name="bakedTileIndex">Optional scanned baked-DEM-pyramid index (Stage 2c); backs the baked-tile streaming path. Null/empty falls back to the runtime-build detail path.</param>
    /// <param name="waterwayClient">Optional Overpass client for watercourses (streams/rivers + waterfalls); null disables the water layer download.</param>
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
        MultiStopRoutePlanner multiStopPlanner,
        ExportRouteToGpxUseCase exportRouteToGpxUseCase,
        ILogger<MapPageViewModel> logger,
        MBTilesOrthoCompositor? orthoCompositor = null,
        IUserLocationService? userLocationService = null,
        IUserLocationLayerRenderer? userLocationRenderer = null,
        OnlineRegionDemLoader? regionDemLoader = null,
        OfflineRegionDownloader? offlineDownloader = null,
        GugikNmtDemTileSource? gugikDemSource = null,
        OfflinePackageService? packageService = null,
        DemElevationSource? elevationSource = null,
        MapaTur.Application.Terrain.BakedTileAvailabilityIndex? bakedTileIndex = null,
        MapaTur.Application.Waterways.IWaterwayOverpassClient? waterwayClient = null)
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
        this.packageService = packageService;
        this.elevationSource = elevationSource;
        this.detailTileCached = gugikDemSource is null ? null : gugikDemSource.IsCached;
        this.bakedTileIndex = bakedTileIndex;
        this.waterwayClient = waterwayClient;

        // Subscribe to the location feed once at construction. The service stays silent until the
        // user opts in via ToggleLocationTracking; we just need to be listening so the first fix
        // lands on the right thread. The handler hops to the dispatcher because the OS may deliver
        // updates from a non-UI thread.
        if (userLocationService is not null)
        {
            userLocationService.LocationChanged += (_, fix) =>
            {
                // Run the raw fix through the resolver: only an ACCEPTED fix advances the displayed position, so
                // a teleport / duplicate / out-of-order / coarse reading can't make the dot jump or the heading
                // spin when GPS gets noisy in the mountains. A rejected fix leaves the marker where it was.
                void Apply()
                {
                    if (locationResolver.Offer(fix).Accepted)
                    {
                        UserLocation = locationResolver.LastAccepted;
                    }

                    RefreshLocationFreshness();
                }

                if (MainThread.IsMainThread)
                {
                    Apply();
                }
                else
                {
                    MainThread.BeginInvokeOnMainThread(Apply);
                }
            };
            userLocationService.Polled += (_, _) => MainThread.BeginInvokeOnMainThread(RefreshLocationFreshness);
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
        if (settingsStore.Storm is { } savedStorm)
        {
            storm = Math.Clamp(savedStorm, 0.0, 1.0);
        }
        if (settingsStore.Forest is { } savedForest)
        {
            forestDensity = Math.Clamp(savedForest, 0.0, 1.0);
        }
        if (settingsStore.PeakLabelRadiusMeters is { } savedPeakRadius)
        {
            peakLabelRadiusMeters = Math.Clamp(savedPeakRadius, 1000.0, 30_000.0);
        }
        cameraState = settingsStore.CameraState;
        // Restore the chosen UI language for the Ustawienia selector. Set the backing field directly so the
        // OnLanguageIndexChanged hook does NOT fire a restart on launch (App already applied the culture).
        languageIndex = AppLanguage.Normalize(settingsStore.Language) == AppLanguage.English ? 1 : 0;
        // Restore the follow-camera tracking option (backing field, so OnFollowCameraChanged doesn't fire before the view exists).
        followCamera = settingsStore.FollowCamera;
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
        this.multiStopPlanner = multiStopPlanner;
        this.exportRouteToGpxUseCase = exportRouteToGpxUseCase;
        this.logger = logger;
        Map = new Map();
        StatusMessage = Localization.AppStrings.StatusInitial;

        // Restore the persisted Tryby + Ustawienia toggles (per-platform) so the user's last layer/material/POI
        // /quality/debug choices survive a restart. Must run AFTER the store + Map exist (POI toggles re-filter).
        LoadPersistedModeSettings();
    }

    // ── Persisted Tryby + Ustawienia toggles (per-platform — see I3DSettingsStore.GetFlag) ───────────────
    // One table maps each persisted bool property to its getter/setter; LoadPersistedModeSettings() restores
    // them from the store and the OnPropertyChanged override below writes any user change straight back.
    // RenderQuality (an int choice) is handled alongside. Keys are platform-scoped in the store, so desktop
    // and mobile remember their toggle sets independently.
    private bool isLoadingPersistedSettings;
    private Dictionary<string, (Func<bool> Get, Action<bool> Set)>? persistedFlags;

    private Dictionary<string, (Func<bool> Get, Action<bool> Set)> PersistedFlags => persistedFlags ??=
        new Dictionary<string, (Func<bool> Get, Action<bool> Set)>(StringComparer.Ordinal)
        {
            [nameof(ShowOrtho)] = (() => ShowOrtho, v => ShowOrtho = v),
            [nameof(RockMaterialOn)] = (() => RockMaterialOn, v => RockMaterialOn = v),
            [nameof(BiomeMaterialOn)] = (() => BiomeMaterialOn, v => BiomeMaterialOn = v),
            [nameof(SlopeMapMode)] = (() => SlopeMapMode, v => SlopeMapMode = v),
            [nameof(ShowTrails)] = (() => ShowTrails, v => ShowTrails = v),
            [nameof(ShowRoads)] = (() => ShowRoads, v => ShowRoads = v),
            [nameof(ShowExposedRoutes)] = (() => ShowExposedRoutes, v => ShowExposedRoutes = v),
            [nameof(TrailColourRedEnabled)] = (() => TrailColourRedEnabled, v => TrailColourRedEnabled = v),
            [nameof(TrailColourBlueEnabled)] = (() => TrailColourBlueEnabled, v => TrailColourBlueEnabled = v),
            [nameof(TrailColourGreenEnabled)] = (() => TrailColourGreenEnabled, v => TrailColourGreenEnabled = v),
            [nameof(TrailColourYellowEnabled)] = (() => TrailColourYellowEnabled, v => TrailColourYellowEnabled = v),
            [nameof(TrailColourBlackEnabled)] = (() => TrailColourBlackEnabled, v => TrailColourBlackEnabled = v),
            [nameof(ShowPeakNames)] = (() => ShowPeakNames, v => ShowPeakNames = v),
            [nameof(ShowSauronTower)] = (() => ShowSauronTower, v => ShowSauronTower = v),
            [nameof(ShowEagles)] = (() => ShowEagles, v => ShowEagles = v),
            [nameof(AtmosphereEffectsEnabled)] = (() => AtmosphereEffectsEnabled, v => AtmosphereEffectsEnabled = v),
            [nameof(ShowNightSky)] = (() => ShowNightSky, v => ShowNightSky = v),
            [nameof(ShowContours)] = (() => ShowContours, v => ShowContours = v),
            [nameof(ShowHuts)] = (() => ShowHuts, v => ShowHuts = v),
            [nameof(ShowWildernessHuts)] = (() => ShowWildernessHuts, v => ShowWildernessHuts = v),
            [nameof(ShowChalets)] = (() => ShowChalets, v => ShowChalets = v),
            [nameof(ShowShelters)] = (() => ShowShelters, v => ShowShelters = v),
            [nameof(ShowViewpoints)] = (() => ShowViewpoints, v => ShowViewpoints = v),
            [nameof(ShowParking)] = (() => ShowParking, v => ShowParking = v),
            [nameof(ShowPasses)] = (() => ShowPasses, v => ShowPasses = v),
            [nameof(ShowDebugOverlay)] = (() => ShowDebugOverlay, v => ShowDebugOverlay = v),
            [nameof(VerboseLogging)] = (() => VerboseLogging, v => VerboseLogging = v),
            [nameof(ShowLodDiagnostics)] = (() => ShowLodDiagnostics, v => ShowLodDiagnostics = v),
        };

    private void LoadPersistedModeSettings()
    {
        isLoadingPersistedSettings = true;
        try
        {
            RenderQuality = settingsStore.GetChoice(nameof(RenderQuality), RenderQuality);
            foreach (var (name, fns) in PersistedFlags)
            {
                fns.Set(settingsStore.GetFlag(name, fns.Get()));
            }
        }
        finally
        {
            isLoadingPersistedSettings = false;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (isLoadingPersistedSettings || e.PropertyName is not { } name)
        {
            return;
        }

        if (name == nameof(RenderQuality))
        {
            settingsStore.SetChoice(name, RenderQuality);
        }
        else if (PersistedFlags.TryGetValue(name, out (Func<bool> Get, Action<bool> Set) fns))
        {
            settingsStore.SetFlag(name, fns.Get());
        }
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
            StatusMessage = Fmt(Localization.AppStrings.StatusMbtilesLoadedFormat, Path.GetFileName(path));
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
            StatusMessage = Fmt(Localization.AppStrings.StatusArchiveLoadFailedFormat, $"{ex.GetType().Name}{hresultText}: {detail}");
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
            StatusMessage = Fmt(Localization.AppStrings.StatusHillshadeLoadFailedFormat, $"{ex.GetType().Name}{hresultText}: {detail}");
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
            StatusMessage = Fmt(Localization.AppStrings.StatusTcxLoadedFormat, track.Name, distanceKilometers, profile.TotalAscentMeters, profile.TotalDescentMeters);
            logger.LogInformation("Imported TCX {Path} with {PointCount} points", path, track.Points.Count);
        }
        catch (FileNotFoundException ex)
        {
            StatusMessage = Localization.AppStrings.StatusFileNotFound;
            logger.LogWarning(ex, "TCX file not found");
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = Fmt(Localization.AppStrings.StatusTcxParseFailedFormat, ex.Message);
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

            // Best-effort: also pull the exposed/guide routes (sac_scale / via_ferrata) for the same box and
            // build the dotted overlay. A failure here must not sink the trails result, so swallow + log.
            try
            {
                var exposed = await overpassClient.FetchExposedRoutesAsync(bounds.Value).ConfigureAwait(true);
                rawExposedRoutes3D = SimplifyForOverlay3D(exposed);
                ApplyExposedRoutes();
                logger.LogInformation("Downloaded {Count} exposed routes for bounds {Bounds}", exposed.Count, bounds);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Exposed-route download failed (non-fatal)");
            }

            StatusMessage = trails.Count == 0
                ? Localization.AppStrings.StatusNoTrailsFound
                : string.Format(System.Globalization.CultureInfo.CurrentUICulture, Localization.AppStrings.StatusTrailsLoadedFormat, trails.Count);
            logger.LogInformation("Downloaded {Count} trails for bounds {Bounds}", trails.Count, bounds);
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = Fmt(Localization.AppStrings.StatusOverpassRequestFailedFormat, ex.Message);
            logger.LogError(ex, "Overpass HTTP request failed");
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = Fmt(Localization.AppStrings.StatusOverpassParseFailedFormat, ex.Message);
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
            StatusMessage = Fmt(Localization.AppStrings.StatusOverpassRequestFailedFormat, ex.Message);
            logger.LogError(ex, "Overpass climbing request failed");
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = Fmt(Localization.AppStrings.StatusOverpassParseFailedFormat, ex.Message);
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
            RebuildPlaceGazetteer();

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
            StatusMessage = Fmt(Localization.AppStrings.StatusOverpassRequestFailedFormat, ex.Message);
            logger.LogError(ex, "Overpass POI request failed");
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = Fmt(Localization.AppStrings.StatusOverpassParseFailedFormat, ex.Message);
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
            StatusMessage = Fmt(Localization.AppStrings.StatusOverpassRequestFailedFormat, ex.Message);
            logger.LogError(ex, "Overpass road request failed");
        }
        catch (InvalidDataException ex)
        {
            StatusMessage = Fmt(Localization.AppStrings.StatusOverpassParseFailedFormat, ex.Message);
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
    /// Called by the page when the user taps the map / terrain. In route-planning mode the tap ADDS a
    /// stop to the chain: it snaps to a named place (peak / hut / lake) within
    /// <see cref="StopSnapRadiusMeters"/>, else drops a plain trail point at the tapped spot.
    /// </summary>
    /// <param name="point">Tapped point in WGS-84 coordinates.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task HandleMapTapAsync(GeoPoint point)
    {
        if (IsBusy || !IsRoutePlanningMode)
        {
            return;
        }

        await AddStopAsync(SnapTapToStop(point)).ConfigureAwait(true);
    }

    // How much farther a named place may be than the nearest trail vertex and still win the snap. Lets a
    // summit/pass that sits ON the ridge keep its name, while a tap on the żleb (peak ~150 m away, trail a few
    // metres away) snaps to the trail — not to a peak.
    private const double NamedPlaceTieMeters = 40.0;

    // A route-planning tap should land on a TRAIL (the route runs on trails), not jump to a peak 200 m away.
    // So snap to the nearest trail vertex; a named place wins only when it essentially coincides with that
    // trail point, or when no trail is within range. This is what lets you drop a point on the black żleb.
    private Domain.Routing.RouteWaypoint SnapTapToStop(GeoPoint point)
    {
        bool hasTrail = TryNearestTrailVertex(point, out GeoPoint trailVertex, out double trailMeters);
        Domain.Routing.RouteWaypoint? place = NearestPlace(point, out double placeMeters);

        bool trailInRange = hasTrail && trailMeters <= StopSnapRadiusMeters;
        bool placeInRange = place is not null && placeMeters <= StopSnapRadiusMeters;

        if (placeInRange && (!trailInRange || placeMeters <= trailMeters + NamedPlaceTieMeters))
        {
            return place!;
        }

        if (trailInRange)
        {
            return new Domain.Routing.RouteWaypoint("Punkt na szlaku", trailVertex, Domain.Routing.WaypointKind.TrailPoint);
        }

        return place ?? new Domain.Routing.RouteWaypoint("Punkt na szlaku", point, Domain.Routing.WaypointKind.TrailPoint);
    }

    // Nearest vertex on any loaded trail (full geometry if held, else the 3D-simplified set). False when no
    // trails are in memory (e.g. they came straight from the viewport cache).
    private bool TryNearestTrailVertex(GeoPoint point, out GeoPoint nearest, out double meters)
    {
        nearest = default;
        meters = double.MaxValue;
        IReadOnlyList<Trail>? trails = rawTrails ?? rawTrails3D;
        if (trails is null)
        {
            return false;
        }

        bool found = false;
        foreach (Trail trail in trails)
        {
            foreach (GeoPoint vertex in trail.Geometry)
            {
                double d = point.HaversineDistanceMetersTo(vertex);
                if (d < meters)
                {
                    meters = d;
                    nearest = vertex;
                    found = true;
                }
            }
        }

        return found;
    }

    // Nearest named place (summit/pass/lake/POI), regardless of radius; caller applies the radius/tie rules.
    private Domain.Routing.RouteWaypoint? NearestPlace(GeoPoint point, out double meters)
    {
        meters = double.MaxValue;
        Domain.Routing.RouteWaypoint? best = null;
        foreach (Domain.Routing.RouteWaypoint candidate in placeGazetteer.All)
        {
            double d = point.HaversineDistanceMetersTo(candidate.Location);
            if (d < meters)
            {
                meters = d;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>Appends a stop to the route chain and re-plans (from the picker list or a map tap).</summary>
    [RelayCommand]
    private async Task AddStopAsync(Domain.Routing.RouteWaypoint waypoint)
    {
        if (waypoint is null)
        {
            return;
        }

        RouteStops.Add(waypoint);
        await ReplanRouteAsync().ConfigureAwait(true);
    }

    /// <summary>Raised when the user asks to film the planned route — the page starts the route fly-through.</summary>
    public event EventHandler? RouteFilmRequested;

    /// <summary>Films the planned tourist route: a cinematic fly-through ALONG it, recorded to MP4. Detail
    /// streaming STAYS ON during the film so the 1 m detail LIVE-STREAMS along the route as the camera advances
    /// steadily (a short start gate waits for the first patch so the opening isn't over bare base; after that the
    /// flight just keeps moving). The film honours the normal ~35 s detail-reload cooldown — re-centring churns
    /// tiles + triggers GC, so the wide window covers the gaze between rare cooldown-paced re-centres.</summary>
    [RelayCommand]
    private void MakeRouteFilm()
    {
        if (!HasPlannedRoute || LastPlannedRoute is null)
        {
            return; // the flight reads the route polyline itself from the bound Route overlay
        }

        ActiveSection = 0; // clear the panel for a clean cinematic shot

        // Detail MUST stream during the film so it follows the route; do NOT pre-build/suppress. Mark the film
        // active so EndRouteFilm knows this teardown is a real route film (vs the built-in demo) and resets the
        // detail state. Detail re-centres still honour the normal ~35 s cooldown. Cleared in EndRouteFilm.
        routeFilmActive = true;
        routeFilmSuppress = false; // belt-and-braces: detail streams normally during the film

        RouteFilmRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Called when the route film ends: clear the film-active flag and resume normal streaming. The film
    /// no longer pre-caches/suppresses detail — it streams live and gates the camera on readiness — so there is no
    /// retained pre-cache to drop, only the active flag to reset.</summary>
    public void EndRouteFilm()
    {
        if (!routeFilmActive && !routeFilmSuppress && !routeFilmAccumulate)
        {
            return; // not a route film (e.g. the built-in demo) — nothing to undo
        }

        routeFilmActive = false;
        routeFilmSuppress = false;
        routeFilmAccumulate = false;
        retainedFilmDetail.Clear();
        if (lodBaseTiles is not null)
        {
            DetailElevation = null;
            TerrainTiles = new List<TerrainMesh3D>(lodBaseTiles); // next camera move re-streams normally
            OnPropertyChanged(nameof(TerrainFrame));
            lodDetailCentre = new GeoPoint(0, 0); // force the next move to rebuild
        }
    }

    /// <summary>Removes a stop from the chain and re-plans the shorter route.</summary>
    [RelayCommand]
    private async Task RemoveStopAsync(Domain.Routing.RouteWaypoint waypoint)
    {
        if (waypoint is null || !RouteStops.Remove(waypoint))
        {
            return;
        }

        await ReplanRouteAsync().ConfigureAwait(true);
    }

    // Route persistence: the planned stops are saved as JSON in the settings store after every change and
    // restored at startup, so the tourist route survives an app restart (the chain "Biela voda" etc. comes back).
    private void PersistRouteStops() =>
        settingsStore.RouteStopsJson = RouteStops.Count == 0
            ? null
            : Application.Routing.RouteStopsSerializer.Serialize(RouteStops);

    /// <summary>
    /// Re-loads the saved route stops (if any) and renders the chain. Best-effort: the route LINE only draws
    /// once the trail graph is available, but the stops + markers come back immediately. No-op if already populated.
    /// </summary>
    public async Task RestoreRouteStopsAsync()
    {
        if (RouteStops.Count > 0)
        {
            return;
        }

        IReadOnlyList<Domain.Routing.RouteWaypoint> saved =
            Application.Routing.RouteStopsSerializer.Deserialize(settingsStore.RouteStopsJson);
        if (saved.Count == 0)
        {
            return;
        }

        foreach (Domain.Routing.RouteWaypoint w in saved)
        {
            RouteStops.Add(w);
        }

        await ReplanRouteAsync().ConfigureAwait(true);
    }

    // Drag-reorder finished in the route-stops list (CollectionView CanReorderItems): the ObservableCollection
    // is already in the new order, so just re-plan the chained route to match the new stop sequence.
    public Task ReplanAfterReorderAsync() => ReplanRouteAsync();

    // Renders the current stop markers, then (with ≥2 stops) plans the chained route over the trail
    // graph and renders it. A leg with no path names the gap so the user knows where the chain broke.
    private async Task ReplanRouteAsync()
    {
        PersistRouteStops();
        routeRenderer.RenderWaypoints(Map, RouteStops.Select(s => s.Location).ToList());

        if (RouteStops.Count < 2)
        {
            LastPlannedRoute = null;
            Route3DOverlay = null;
            HasPlannedRoute = false;
            RouteSummary = string.Empty;
            StatusMessage = RouteStops.Count == 1
                ? Fmt(Localization.AppStrings.StatusFirstStopFormat, RouteStops[0].Name)
                : Localization.AppStrings.StatusRoutePlanningOn;
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = Localization.AppStrings.StatusPlanningRoute;

            List<GeoPoint> stops = RouteStops.Select(s => s.Location).ToList();
            // Shortest distance, NOT fastest time: in the mountains the marked descent you want (e.g. the Żleb
            // Kulczyńskiego) is the short, STEEP one, and a Tobler "fastest time" cost penalises steepness so
            // hard it detours AROUND the żleb (2 km instead of 0.9 m) — turning a stop on it into a spur. The
            // direct on-trail route is what a walker following the markings actually wants.
            MultiStopRouteResult result = await Task.Run(
                () => multiStopPlanner.PlanAsync(stops, RouteProfile.ShortestDistance)).ConfigureAwait(true);

            if (result.Route is null)
            {
                int leg = result.FailedLegIndex ?? 0;
                StatusMessage = Fmt(Localization.AppStrings.StatusNoTrailBetweenFormat, RouteStops[leg].Name, RouteStops[leg + 1].Name);
                LastPlannedRoute = null;
                Route3DOverlay = null;
                HasPlannedRoute = false;
                RouteSummary = string.Empty;
                routeRenderer.Clear(Map);
                routeRenderer.RenderWaypoints(Map, stops);
                return;
            }

            LastPlannedRoute = result.Route;
            Route3DOverlay = result.Route;
            routeRenderer.RenderRoute(Map, result.Route);

            RouteSummary = Application.Routing.RouteSummaryFormatter.Format(
                result.Route.TotalDistanceMeters, result.Route.TotalAscentMeters, result.Route.TotalDurationSeconds);
            HasPlannedRoute = true;

            double km = result.Route.TotalDistanceMeters / 1000.0;
            TimeSpan eta = TimeSpan.FromSeconds(result.Route.TotalDurationSeconds);
            StatusMessage = Fmt(
                Localization.AppStrings.StatusRouteSummaryFormat,
                RouteStops.Count, km, result.Route.TotalAscentMeters,
                eta.ToString(@"hh\:mm", System.Globalization.CultureInfo.InvariantCulture));
            logger.LogInformation(
                "Multi-stop route: {Stops} stops, {Segments} segments, {Km:F2} km",
                RouteStops.Count, result.Route.Segments.Count, km);

            // DIAGNOSTIC ("trasa nie w pobliżu punktów"): for each stop, how far the planned route actually
            // passes from it. A large gap means that stop snapped to a far trail node — FindNearestNode has
            // no distance cap, so an off-trail / out-of-coverage stop jumps to whatever node is nearest and
            // the route then connects the wrong places.
            IReadOnlyList<GeoPoint> routePolyline = result.Route.ToPolyline();
            for (int i = 0; i < RouteStops.Count; i++)
            {
                Domain.Routing.RouteWaypoint s = RouteStops[i];
                double nearestMeters = routePolyline.Min(p => p.HaversineDistanceMetersTo(s.Location));
                logger.LogInformation(
                    "  stop {Idx} '{Name}' ({Kind}) @ {Lat:F5},{Lon:F5} — route passes {Dist:F0} m away",
                    i, s.Name, s.Kind, s.Location.Latitude, s.Location.Longitude, nearestMeters);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            StatusMessage = Fmt(Localization.AppStrings.StatusRoutePlanFailedFormat, ex.Message);
            logger.LogError(ex, "Multi-stop route planning failed");
        }
        finally
        {
            IsBusy = false;
        }
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
            StatusMessage = Fmt(Localization.AppStrings.StatusDemParseFailedFormat, ex.Message);
            logger.LogError(ex, "DEM parse failure");
        }
        catch (Exception ex)
        {
            int? hresult = ex.HResult != 0 ? ex.HResult : null;
            string hresultText = hresult is not null ? $" (0x{hresult:X8})" : string.Empty;
            string detail = string.IsNullOrEmpty(ex.Message) ? "(no message)" : ex.Message;
            StatusMessage = Fmt(Localization.AppStrings.StatusDemLoadFailedFormat, $"{ex.GetType().Name}{hresultText}: {detail}");
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
    // 5 M keeps the base mesh cheap so the phone holds FPS. Raising it to 12 M to render the full 15 m base
    // natively TANKED FPS to ~3 (too many verts) and did NOT fix quality — sharpness comes from the 1 m DETAIL,
    // not from rendering more of the coarse base (which is blocky up close at 15 m anyway). Keep the base cheap;
    // the fix is getting the 1 m detail to actually stream, not inflating the base.
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
        // Despike FIRST (off the UI thread): tatry.dem carries one-cell pits hundreds of metres deep along
        // watercourses (bake artefacts) that render as dark-walled trench "dashes". The LOD demo pipeline
        // already despikes; the auto-loaded MAIN map must too, or the same DEM shows holes here.
        DemRaster loadedRaster = raster;
        raster = await Task.Run(() => DemRasterRepair.FillPits(
            DemRasterRepair.FillDropoutStrips(
                DemRasterRepair.FillNarrowZeroStrips(loadedRaster, maxWidthCells: 24),
                depthThresholdMeters: 50.0, minRunCells: 3),
            depthThresholdMeters: 20.0)).ConfigureAwait(true);

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
        IReadOnlyList<NamedSummit> gazetteer = await GetTatraGazetteerAsync().ConfigureAwait(true);
        Peaks3DOverlay = await Task.Run(() =>
            RefinePeaksOnBakedTiles(
                PeakNamer.MergeWithGazetteer(PeakDetector.Detect(peakRaster, peakOptions), gazetteer, raster),
                gazetteer,
                bakedTileIndex)).ConfigureAwait(true);
        RebuildPlaceGazetteer();
        logger.LogInformation("Loaded DEM {Label} ({Cols}x{Rows})", label, raster.Columns, raster.Rows);
        StatusMessage = $"{Localization.AppStrings.StatusDemLoaded}: {label}";
        if (IsInitialLoading)
        {
            LoadProgress = 0.15; // startup overlay: DEM read done, scene build (0.3+) comes next
            InitialLoadStage = $"{Localization.AppStrings.StatusDemLoaded}: {label}";
        }
    }

    /// <summary>
    /// The named-summit gazetteer for the Tatra peak overlay: OSM <c>natural=peak</c> (named, ≥1500 m) from
    /// the bundled <c>tatra-osm-peaks.json</c>, merged with the curated <see cref="TatraSummits.All"/>
    /// fallback so nothing OSM omits is lost. Loaded and cached once; falls back to the curated list alone
    /// if the bundle can't be read or parsed — peak labels must never silently vanish.
    /// </summary>
    private async Task<IReadOnlyList<NamedSummit>> GetTatraGazetteerAsync()
    {
        if (tatraGazetteer is not null)
        {
            return tatraGazetteer;
        }

        try
        {
            await using Stream stream = await Microsoft.Maui.Storage.FileSystem
                .OpenAppPackageFileAsync("tatra-osm-peaks.json").ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer).ConfigureAwait(false);

            IReadOnlyList<OsmPeak> peaks = OverpassPeakResponseParser.Parse(buffer.ToArray());
            // Collapse OSM's multi-summit massifs (Rysy/Wysoka as separate nodes) before merging, so the
            // overlay doesn't stack two or three labels on one apex.
            // Cut-off lowered 1500→1300 m: now that the "zasięg" slider culls distant labels, a fuller peak
            // set no longer clutters the view, so surface the notable lower summits too (Gęsia Szyja 1489,
            // Opalone Wierchy, Furkaska, …). Range handles density; raise/lower this to taste.
            const double peakMinElevationMeters = 1300.0;
            IReadOnlyList<NamedSummit> osmSummits = SummitSources.Deduplicate(OsmPeakSummitMapper.ToSummits(peaks, peakMinElevationMeters));
            tatraGazetteer = SummitSources.Combine(osmSummits, TatraSummits.All);
            logger.LogInformation(
                "Tatra gazetteer: {Osm} OSM peaks (named, ≥{Min:F0} m) + {Fallback} curated → {Total} summits",
                osmSummits.Count, peakMinElevationMeters, TatraSummits.All.Count, tatraGazetteer.Count);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Bundled OSM peaks unavailable; using the curated TatraSummits gazetteer");
            tatraGazetteer = TatraSummits.All;
        }

        return tatraGazetteer;
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
            StatusMessage = Localization.AppStrings.StatusOnlineTerrainUnavailable;
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = Localization.AppStrings.StatusDownloadingTerrain1m;

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
                StatusMessage = Fmt(Localization.AppStrings.StatusDownloadingTerrain1mProgressFormat, percent, p.Completed, p.Total);
                logger.LogInformation("GUGiK region tiles: {Completed}/{Total} ({Percent}%)", p.Completed, p.Total, percent);
            });

            DemRaster? raster = await regionDemLoader.LoadRegionAsync(bounds, zoom, progress).ConfigureAwait(true);
            if (raster is null)
            {
                logger.LogWarning("GUGiK region load returned no raster (no network / coverage)");
                StatusMessage = Localization.AppStrings.StatusTerrainDownloadFailed;
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

            await BuildSceneFromRasterAsync(raster, Fmt(Localization.AppStrings.SceneLabelTatra1mFormat, zoom)).ConfigureAwait(true);

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
            StatusMessage = Localization.AppStrings.StatusRegionTerrainError;
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
            StatusMessage = Localization.AppStrings.StatusOfflineUnavailable;
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = Localization.AppStrings.StatusDownloadingTatraOffline;
            logger.LogInformation("Offline Tatra download start: base z{Base}+z14+detail z{Detail}", LodBaseZoom, TatraOfflineRegion.DownloadZoom);

            var progress = new Progress<OfflineDownloadProgress>(p =>
            {
                int percent = p.Total > 0 ? (int)(100L * p.Completed / p.Total) : 0;
                string failed = p.Failed > 0 ? Fmt(Localization.AppStrings.StatusOfflineSkippedSuffixFormat, p.Failed) : string.Empty;
                StatusMessage = Fmt(Localization.AppStrings.StatusDownloadingTatraOfflineProgressFormat, percent, p.Completed, p.Total, failed);
            });

            // Download the SMOOTH base zooms (z13/z14) over the whole region AND the 1 m detail (z16). The wide
            // z13 is what lets the LOD base load cache-only (offline, smooth, wide — no online fetch, no loading
            // stripes); z16 is the near-field 1 m. Coarse→fine so the base is usable first.
            int[] zooms = { LodBaseZoom, 14, TatraOfflineRegion.DownloadZoom };
            int totDownloaded = 0, totCached = 0, totFailed = 0, totTotal = 0;
            foreach (int z in zooms)
            {
                StatusMessage = Fmt(Localization.AppStrings.StatusDownloadingTatraOfflineZoomFormat, z);
                OfflineDownloadResult r = await offlineDownloader.DownloadAsync(
                    TatraOfflineRegion.Bounds, z, progress).ConfigureAwait(true);
                totDownloaded += r.Downloaded; totCached += r.AlreadyCached; totFailed += r.Failed; totTotal += r.Total;
                logger.LogInformation(
                    "Offline z{Zoom}: {Downloaded} new, {Cached} cached, {Failed} skipped of {Total}",
                    z, r.Downloaded, r.AlreadyCached, r.Failed, r.Total);
            }

            StatusMessage = totFailed == 0
                ? Fmt(Localization.AppStrings.StatusTatraOfflineDoneFormat, totTotal)
                : Fmt(Localization.AppStrings.StatusTatraOfflinePartialFormat, totTotal - totFailed, totTotal);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Offline Tatra download failed");
            StatusMessage = Localization.AppStrings.StatusTatraOfflineError;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Downloads the pre-baked region packages (DEM 1 m + ortho) from the package server and unpacks them into
    /// the renderer's data dirs, so a fresh user gets full offline data with no manual side-loading. Only
    /// missing or out-of-date packages are fetched, and each download resumes if a prior attempt was cut off.
    /// The WiFi-or-warn gate lives in the view; this runs once the user has agreed. No-op without a service.
    /// </summary>
    [RelayCommand]
    private async Task DownloadDataPackagesAsync()
    {
        if (packageService is null)
        {
            StatusMessage = Localization.AppStrings.StatusPackagesUnavailable;
            return;
        }

        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = Localization.AppStrings.StatusCheckingPackages;
            IReadOnlyList<PackageStatus> catalog = await packageService.GetCatalogAsync().ConfigureAwait(true);
            var todo = catalog.Where(p => p.State != PackageState.Installed).Select(p => p.Package).ToList();
            if (todo.Count == 0)
            {
                StatusMessage = Localization.AppStrings.StatusPackagesUpToDate;
                return;
            }

            for (int i = 0; i < todo.Count; i++)
            {
                RegionPackage package = todo[i];
                int index = i + 1;
                var progress = new Progress<PackageDownloadProgress>(p =>
                {
                    int percent = p.TotalBytes > 0 ? (int)(100L * p.BytesReceived / p.TotalBytes) : 0;
                    StatusMessage = Fmt(Localization.AppStrings.StatusDownloadingPackageFormat, package.Name, index, todo.Count, percent);
                });
                logger.LogInformation("Package download start: {Id} v{Version}", package.Id, package.Version);
                await packageService.InstallAsync(package, progress).ConfigureAwait(true);
                logger.LogInformation("Package installed: {Id} v{Version}", package.Id, package.Version);
            }

            StatusMessage = Fmt(Localization.AppStrings.StatusPackagesReadyFormat, todo.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Data package download failed");
            StatusMessage = Localization.AppStrings.StatusPackagesError;
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
    private Task LoadLodDemoAsync() => BuildLodSceneAsync(reframeCamera: true);

    /// <summary>
    /// Builds THE Tatra scene: ring-LOD native base (FillPits + hole repair) + 1 m detail streaming +
    /// landmarks + lakes. Started automatically at the end of auto-load (the LOD pipeline IS the main
    /// experience — see <see cref="AutoStartLodPipeline"/>); the historical "LOD demo" entry point is
    /// the same method with a camera reframe.
    /// </summary>
    /// <param name="reframeCamera">True = reframe onto the new scene (the old demo-button behaviour);
    /// false = keep the current/restored camera (startup path — the per-DEM saved pose must survive).</param>
    private async Task BuildLodSceneAsync(bool reframeCamera)
    {
        if (regionDemLoader is null)
        {
            StatusMessage = Localization.AppStrings.StatusLodUnavailable;
            return;
        }

        if (IsBusy)
        {
            // Another scene load (e.g. the startup auto-load) is running — say so instead of silently doing
            // nothing (a silent return made the button look DEAD: "przycisk nie działa").
            StatusMessage = Localization.AppStrings.StatusMapLoading;
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = Localization.AppStrings.StatusLodBase;
            InitialLoadStage = Localization.AppStrings.StatusLodBase;
            LoadProgress = 0.3;

            var center = new GeoPoint(
                (TatraDemRegion.Bounds.NorthEast.Latitude + TatraDemRegion.Bounds.SouthWest.Latitude) / 2.0,
                (TatraDemRegion.Bounds.NorthEast.Longitude + TatraDemRegion.Bounds.SouthWest.Longitude) / 2.0);

            // Base: wide, STATIC. z13 (~6 m/px over ~6 km, ~1 M verts < the 5 M cap so it's never decimated) —
            // bumped from z12 (~12 m) because z12 SHAVED the sharp summit apexes, so distant peaks read blunter
            // than the real Tatras. The 1 m detail still sharpens whatever you look at; this keeps the SKYLINE
            // peaks faithful too. fillNoData: false so a missing tile holes to the sky, not a flat green plate.
            // Base = the LOCAL whole-Tatra DEM (tatry.dem, ~30 m): covers ALL the Tatras offline + instantly,
            // with no base streaming, no missing tiles, and no GUGiK z13 supersampler ring-grid (the local DEM
            // doesn't go through GUGiK at all). The 1 m detail still streams near the look-at. Falls back to the
            // online z13 window only when the local DEM isn't installed.
            string? localDemPath = autoLoader.Discover().DemPath;
            DemRaster? baseRaster = localDemPath is not null
                ? await Task.Run(() => DemRasterReader.Read(localDemPath)).ConfigureAwait(true)
                : await regionDemLoader.LoadRegionAsync(LodTerrainWindow.Around(center, LodBaseHalfWidthMeters), LodBaseZoom, fillNoData: false).ConfigureAwait(true);
            if (baseRaster is null)
            {
                StatusMessage = Localization.AppStrings.StatusLodNoBase;
                return;
            }

            // Real coarse base (no artificial blockiness now that the overlay is proven). The 1 m detail
            // near the camera blends into it seamlessly; the base carries the distance.
            // Whole-Tatra base prep is heavy (subsample ~9.5 M cells, hole, flood-fill) — run it OFF the UI thread
            // so entering the demo doesn't FREEZE. The local DEM is the whole range, far bigger than the old
            // online window, so on the UI thread this stalled the LOD entry ("nie wchodzi demo").
            // Ring-LOD base (local DEM only): keep the raster at NATIVE resolution and let RingBasePlanner +
            // BuildAdaptiveTiles render it at per-tile steps (native near the focus, 2/4 further out). The old
            // uniform SubsampleRasterForRenderer base is exactly the blunted/shifted ridge that pokes out past
            // the detail-window edge as a "duplicated ridge" — near the focus the base must match the source.
            bool ringBase = localDemPath is not null;
            DemRaster loadedBase = baseRaster;
            baseRaster = await Task.Run(() =>
            {
                // Despike FIRST, on the FULL raster: tatry.dem carries one-cell pits hundreds of metres deep at
                // regular processing-grid positions (water/void bake artefacts) — on the rendered base each is a
                // cell-wide dark-walled shaft (the black "dashes" along valleys). Before the stride subsample,
                // or the stride could sample a pit cell whose true neighbours are then ~50 m away.
                // Checklist §A: bridge narrow flat-0 strips from neighbours FIRST, then fully fill corrupt single
                // ROW/COLUMN dropouts (mosaic/stitch artefacts hundreds of m below their neighbours — FillPits only
                // shaves them to ~20 m, leaving a residual narrow trench in the base), then despike one-cell pits.
                DemRaster bridgedBase = DemRasterRepair.FillNarrowZeroStrips(loadedBase, maxWidthCells: 24);
                DemRaster destriped = DemRasterRepair.FillDropoutStrips(bridgedBase, depthThresholdMeters: 50.0, minRunCells: 3);
                DemRaster r = DemRasterRepair.FillPits(destriped, depthThresholdMeters: 20.0);
                if (!ringBase)
                {
                    // Legacy uniform base for the online fallback window only.
                    r = SubsampleRasterForRenderer(r);
                }

                // GUGiK flat-0 out-of-coverage → NoData, then fill interior gaps but keep edge-connected gaps as
                // holes (→ sky). No flat green plate, no see-through windows in the bottom layer.
                r = DemRasterRepair.HoleBelow(r, DetailCoverageFloorMeters);
                return DemRasterRepair.FillInteriorKeepEdgeGaps(r);
            }).ConfigureAwait(true);
            var baseCentre = new GeoPoint(
                (baseRaster.North + baseRaster.South) / 2.0, (baseRaster.East + baseRaster.West) / 2.0);
            (double bMin, double bMax) = baseRaster.GetElevationRange();
            logger.LogInformation(
                "LOD base: {Cols}x{Rows}, centre {Lat:F4},{Lon:F4}, elev {Min:F0}-{Max:F0} m",
                baseRaster.Columns, baseRaster.Rows, baseCentre.Latitude, baseCentre.Longitude, bMin, bMax);

            StatusMessage = Localization.AppStrings.StatusLodDetail;
            InitialLoadStage = Localization.AppStrings.StatusLodDetail;
            LoadProgress = 0.7;
            var options = new MapaTur.Application.Terrain.TerrainMeshOptions
            {
                VerticalExaggeration = (float)Math.Clamp(VerticalExaggeration, 1.0, 5.0),
            };

            // Ortho-on-LOD: if a bundled ortho auto-loaded, KEEP it (textures stay uploaded + enabled) and drape
            // it on the LOD tiles via geo-referenced UV (the ortho covers a larger extent than these tiles).
            // Otherwise clean hypsometric so the 1 m detail is obvious against the coarse base.
            MapaTur.Application.Terrain.OrthoCoverage? orthoCoverage = bundledOrthoCoverageBounds is { } cov
                ? new MapaTur.Application.Terrain.OrthoCoverage(cov, orthoGridCols, orthoGridRows)
                : null;
            if (orthoCoverage is null)
            {
                OrthoTexturePath = null;
                OrthoTexturePaths = null;
                OrthoTextureCells = null;
                orthoGridCols = 1;
                orthoGridRows = 1;
            }

            lodOrthoCoverage = orthoCoverage; // shared with the per-tile detail build
            // Tell the renderer where the ortho actually covers, so the wider base fades to hypsometric beyond it
            // (instead of stretching clamped edge texels = the "strata" seam bands). Null when no bundled ortho.
            LodOrthoCoverageBounds = orthoCoverage?.Bounds;

            DemRaster preparedBase = baseRaster;
            GeoPoint focus = center;
            IReadOnlyList<TerrainMesh3D> baseTiles = await Task.Run(
                () => BuildLodBaseTiles(preparedBase, focus, options, orthoCoverage, ringBase)).ConfigureAwait(true);
            var combined = new List<TerrainMesh3D>(baseTiles);

            // Set the LOD base BEFORE building the detail: the detail backfills its NoData voids (GUGiK has
            // none on watercourses/the Slovak side) AND edge-matches from TerrainRaster — both need the base.
            TerrainRaster = baseRaster;

            // Stage 2c: when baked streaming will run this scene, SKIP the one-shot runtime detail build — the
            // baked stream fills the near field on the first camera tick, so building (and uploading) a redundant
            // mosaic/repair detail here would only re-introduce the very multi-second startup stall the bake
            // removes. The base alone is shown until the first stream update. The runtime build still runs for the
            // fallback path (streaming off / no baked pyramid).
            bool willStreamBaked = UseBakedTileStreaming && bakedTileIndex is { Count: > 0 };
            if (!willStreamBaked)
            {
                // Initial detail ring centred on the base centre, anchored to the same scene origin (finest z16).
                IReadOnlyList<TerrainMesh3D>? detailTiles = await BuildDetailTilesAsync(baseCentre, baseCentre, NearDetailZoom).ConfigureAwait(true);
                if (detailTiles is not null)
                {
                    combined.AddRange(detailTiles);
                }
            }
            // Landmarks: name + seat the known Tatra summits on the LOD base so peaks (Rysy, Mięguszowiecki,
            // Mnich, Kozi Wierch, …) are labelled in the demo too. Detect on a coarse copy (the dominance scan
            // is O(cells×window²)); the gazetteer guarantees every named summit in view shows, seated on the
            // base terrain. Same world frame as the tiles (anchor = base centre), so labels line up.
            var lodPeakOptions = new PeakDetectionOptions { DominanceRadiusMeters = 550.0, MaxPeaks = 48 };
            DemRaster lodPeakRaster = DemRasterDownsampler.SubsampleToMaxCells(baseRaster, maxCells: 20_000);
            IReadOnlyList<NamedSummit> lodGazetteer = await GetTatraGazetteerAsync().ConfigureAwait(true);
            // PeakNamer now snaps each name to the NEAREST local maximum (its own apex), not the highest cell
            // in the radius — so a low summit (e.g. Mnich) no longer borrows a taller neighbour's ridge.
            Peaks3DOverlay = await Task.Run(() =>
                RefinePeaksOnBakedTiles(
                    PeakNamer.MergeWithGazetteer(PeakDetector.Detect(lodPeakRaster, lodPeakOptions), lodGazetteer, baseRaster),
                    lodGazetteer,
                    bakedTileIndex)).ConfigureAwait(true);
            RebuildPlaceGazetteer();
            lodBaseTiles = MarkBaseSkin(baseTiles);
            lodBaseTileFootprints = ComputeBaseTileFootprints(baseTiles);
            lodAnchor = baseCentre;
            lodRingBase = ringBase;
            lodDetailCentre = baseCentre;
            detailMeshCache.Clear(); // new scene: anchor + ortho coverage changed ⇒ cached block keys are no longer valid
            detailRepairCache.Clear(); // new scene ⇒ raw tile inputs differ ⇒ cached repairs invalid
            detailRoughnessCache.Clear(); // new scene ⇒ terrain differs ⇒ cached roughness invalid
            lastValidLookAtWorld = null; // new scene frame — drop any look-at from a previous LOD session

            // Stage 2c: wire (or reset) the baked-tile streaming manager for THIS scene's anchor + ortho coverage.
            // No-op when streaming is off / no baked pyramid is on disk → OnDetailFocusAsync uses the runtime path.
            SetUpBakedStreaming((float)Math.Clamp(VerticalExaggeration, 1.0, 5.0));
            TerrainTiles = combined;
            OnPropertyChanged(nameof(TerrainFrame));
            Is3DMode = true;
            if (reframeCamera)
            {
                TerrainReframeRequested?.Invoke(this, EventArgs.Empty);
            }

            // Base is framed + static; turn on detail streaming so the 1 m ring follows the camera focus
            // (Etap 3) — the view stops reframing on detail swaps so the camera roams the base freely.
            IsLodStreaming = true;
            LoadProgress = 1.0;
            logger.LogInformation("LOD built: {BaseTiles} base + {Total} total tiles; detail streaming ON", baseTiles.Count, combined.Count);
            StatusMessage = Localization.AppStrings.StatusLodReady;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LOD demo failed");
            StatusMessage = Localization.AppStrings.StatusLodError;
        }
        finally
        {
            IsBusy = false;
            // The startup overlay ends with the scene build — success OR failure (a failed build must never
            // trap the user behind a spinner; the status pill carries the error).
            IsInitialLoading = false;
        }
    }

    // Stage 2c: (re)build the baked-tile streaming manager for the current LOD scene, or leave streaming inactive
    // when it's switched off or no baked pyramid is on disk (then OnDetailFocusAsync stays on the runtime-build
    // path). The manager streams the pre-baked z13–z16 pyramid against the scene's anchor + ortho coverage, so
    // baked tiles land in the same world frame and drape the same ortho as the base/detail the rest of the scene
    // already uses. exaggeration must match the live terrain so a baked tile shades + seats identically.
    private void SetUpBakedStreaming(float exaggeration)
    {
        bakedStreamManager = null;
        bakedStreamActive = false;

        if (!UseBakedTileStreaming || bakedTileIndex is null || bakedTileIndex.Count == 0)
        {
            FineElevationSampler = null; // coarse-only camera floor (pre-bake scenes)
            logger.LogInformation(
                "[BakedStream] inactive (enabled={Enabled}, bakedTiles={Count}) — using runtime-build detail",
                UseBakedTileStreaming, bakedTileIndex?.Count ?? 0);
            return;
        }

        var meshOptions = new MapaTur.Application.Terrain.TerrainMeshOptions
        {
            VerticalExaggeration = exaggeration,
        };

        MapaTur.Application.Terrain.BakedTileAvailabilityIndex index = bakedTileIndex;
        bakedStreamManager = new MapaTur.Application.Terrain.BakedTileStreamingManager(
            index.Roots,
            index.IsBaked,
            index.Load,
            lodAnchor,
            index.MinZoom,
            Math.Max(index.MinZoom, BakedStreamMaxZoom),
            BakedStreamMaxResidentTiles,
            BakedStreamMaxConcurrentLoads,
            BakedStreamMaxErrorPixels,
            BakedTileSkirtDepthMeters,
            meshOptions,
            lodOrthoCoverage,
            orthoTileIndexOffset: 0, // baked tiles share the base scene's ortho coverage grid → same cell indices
            maxResidentBytes: BakedStreamMaxResidentBytes);
        bakedStreamActive = true;

        // Camera anti-tunnelling floor: give the 3D view a sampler of the TRUE rendered surface (the baked
        // 1 m z16), because the coarse base raster it samples otherwise understates ridges by metres and the
        // eye clipped into the drawn terrain ("wjazd w powierzchnię mapy zdarza się często").
        var floorSampler = new MapaTur.Application.Terrain.BakedFineElevationSampler(
            index.IsBaked, index.Load, Math.Max(index.MinZoom, BakedStreamMaxZoom));
        FineElevationSampler = floorSampler.Sample;

        logger.LogInformation(
            "[BakedStream] active: {Count} baked tiles, roots={Roots} z{MinZoom}-{MaxZoom}, cap={Cap}, ortho={Ortho}",
            index.Count, index.Roots.Count, index.MinZoom, Math.Max(index.MinZoom, BakedStreamMaxZoom),
            BakedStreamMaxResidentTiles, lodOrthoCoverage is not null ? "textured" : "hypsometric");
    }

    // Per-tile downward skirt depth: a coarser tile (lower zoom) spans more ground and meets finer neighbours
    // with a bigger vertical step, so it hangs a proportionally deeper apron to hide the LOD-seam crack. ~6 m at
    // the finest z16, doubling per coarser level — the visible top surface is untouched (the skirt only appends).
    private static float BakedTileSkirtDepthMeters(DemTileKey key)
    {
        int stepsCoarserThanFinest = Math.Max(0, BakedStreamMaxZoom - key.Zoom);
        return 6f * (1 << Math.Min(stepsCoarserThanFinest, 4)); // cap the multiplier so a far root skirt stays sane
    }

    // Re-entrancy + light debounce for the baked stream. The manager update (select + diff + capped off-thread
    // load) is far cheaper than the old mosaic/repair rebuild, so this is a small guard against piling updates,
    // not the heavy ~35 s cooldown the runtime path needs.
    private bool bakedStreamUpdating;
    private DateTime lastBakedStreamUtc = DateTime.MinValue;
    private static readonly TimeSpan BakedStreamMinInterval = TimeSpan.FromMilliseconds(120);

    // Stage 2c: advance the baked-tile stream for the current camera and swap the drawable set to
    // base + resident-baked. The selector/residency/load all live in BakedTileStreamingManager; here we just
    // feed it the camera + viewport, publish the result, and log the per-move delta on the always-visible badge.
    private async Task StreamBakedDetailAsync(
        MapaTur.Application.Terrain.Camera3D camera, int viewportHeightPixels, int viewportWidthPixels)
    {
        // Remember the pose inputs so a non-camera event (exaggeration rebuild) can KickBakedStream() from
        // the current view instead of waiting for the next camera move. Camera3D is the view's live object,
        // so a later kick reads its CURRENT pose.
        lastBakedStreamCamera = camera;
        lastBakedStreamViewportH = viewportHeightPixels;
        lastBakedStreamViewportW = viewportWidthPixels;

        if (bakedStreamManager is null || lodBaseTiles is null)
        {
            return;
        }

        // Coalesce: one update in flight at a time, and skip micro-moves within the debounce window. A move that
        // arrives mid-update is simply dropped — the next move re-selects from the latest pose, so nothing is lost.
        if (bakedStreamUpdating || DateTime.UtcNow - lastBakedStreamUtc < BakedStreamMinInterval)
        {
            return;
        }

        bakedStreamUpdating = true;
        lastBakedStreamUtc = DateTime.UtcNow;
        bool continueFill = false;
        try
        {
            double viewportHeight = viewportHeightPixels > 0 ? viewportHeightPixels : 1000.0;
            float aspect = viewportWidthPixels > 0 && viewportHeightPixels > 0
                ? (float)viewportWidthPixels / viewportHeightPixels
                : 16f / 9f; // first frame before a real backbuffer size is known — a sane default for the cull

            MapaTur.Application.Terrain.BakedStreamingUpdate update =
                await bakedStreamManager.UpdateAsync(camera, aspect, viewportHeight).ConfigureAwait(true);

            // The resident/occlusion set only changes when a tile loads or evicts, so only then do we rebuild the
            // drawable list (otherwise the renderer diffs by identity and rebuilding the List allocates + re-runs
            // OnTilesChanged needlessly). Skip the base tiles fully hidden behind resident, hole-free baked detail
            // — that base is otherwise shaded then depth-rejected under every baked tile (the terrain shader's
            // reflection-clip discard disables early-Z), pure overdraw. A streaming hole leaves its base drawn.
            if (update.Loaded > 0 || update.Evicted > 0 || TerrainTiles is null)
            {
                // Base-under-baked overdraw culling (B) is OFF: the per-pass GPU profiler showed the frame is
                // GPU-cheap (~8–16 ms), so the overdraw win is negligible — while culling the base out from under
                // COARSE (z13/z14) baked tiles removed the smooth underlay that hid LOD seams, exposing
                // mis-stitched "rectangular tile" walls / LOD mismatches. So the base is ALWAYS drawn under the
                // baked detail (known-good). The flag keeps the machinery for a future z16-only variant.
                bool[] occludedBase = Array.Empty<bool>();
                int occluderCount = 0;
                if (BakedStreamCullOccludedBase && lodBaseTileFootprints is { Count: > 0 })
                {
                    // Z16-ONLY: coarser resident tiles must NOT cull the base (their box-averaged surface needs
                    // the base's smooth underlay to hide LOD seams — the documented regression that kept this
                    // feature off). Only complete 1 m tiles may claim the ground.
                    IReadOnlyList<MapaTur.Application.Terrain.DemTileKey> occluders = bakedStreamManager.OccludingKeys
                        .Where(k => k.Zoom == BakedStreamMaxZoom)
                        .ToList();
                    occluderCount = occluders.Count;
                    if (occluderCount > 0)
                    {
                        occludedBase = MapaTur.Application.Terrain.BaseTileOcclusionPlanner.OccludedBaseTiles(
                            lodBaseTileFootprints, occluders, BakedStreamMaxZoom);
                    }
                }

                bool cull = occludedBase.Length == lodBaseTiles.Count;

                var combined = new List<TerrainMesh3D>(lodBaseTiles.Count + update.ResidentTiles.Count);
                int culled = 0;
                for (int i = 0; i < lodBaseTiles.Count; i++)
                {
                    if (cull && occludedBase[i])
                    {
                        culled++;
                        continue;
                    }

                    combined.Add(lodBaseTiles[i]);
                }

                combined.AddRange(update.ResidentTiles);
                TerrainTiles = combined; // OnTilesChanged: DetailStreamingEnabled ⇒ repaint only, no reframe
                OnPropertyChanged(nameof(TerrainFrame));

                // Surface ownership: refresh the coverage mask the shader uses to discard the base skin over
                // ground whose full 1 m detail is resident (see BaseCoverageMaskBuilder — the "airport" fix).
                BaseCoverageMask = MapaTur.Application.Terrain.BaseCoverageMaskBuilder.Build(
                    bakedStreamManager.HoleFreeFinestWorldRects());

                if ((update.Loaded > 0 || update.Evicted > 0) && BakedStreamCullOccludedBase)
                {
                    logger.LogInformation(
                        "[BakedStream] base occlusion: culled {Culled}/{Total} base tiles behind {Occluders} hole-free baked",
                        culled, lodBaseTiles.Count, occluderCount);
                }
            }

            // The baked stream owns the whole detail surface now, so overlays fall back to the coarse base (the
            // trail decal is re-integrated in Stage 3). Clearing the fine detail field keeps trails/route from
            // seating against a window the streamer no longer maintains.
            DetailElevation = null;

            if (update.Loaded > 0 || update.Evicted > 0)
            {
                // Keys + pose make churn diagnosable from the log alone: WHICH tiles flap (a ring edge shows
                // adjacent x/y, a clamp cascade shows a parent/children mix) and whether the camera actually
                // moved between ticks (a "still" camera that drifts is the churn's suspected driver).
                logger.LogInformation(
                    "[BakedStream] resident={Resident} loaded={Loaded} evicted={Evicted} desired={Desired} clamped={Clamped} "
                    + "eye=({EyeX:F1},{EyeY:F1},{EyeZ:F1}) target=({TgtX:F1},{TgtY:F1},{TgtZ:F1}) "
                    + "in=[{LoadedKeys}] out=[{EvictedKeys}]",
                    bakedStreamManager.ResidentCount, update.Loaded, update.Evicted, update.Desired, update.WasClampedByBudget,
                    camera.Position.X, camera.Position.Y, camera.Position.Z,
                    camera.Target.X, camera.Target.Y, camera.Target.Z,
                    FormatTileKeys(update.LoadedKeys), FormatTileKeys(update.EvictedKeys));
            }

            if (ShowLodDiagnostics)
            {
                string badge = $"BAKED z{bakedTileIndex?.MinZoom ?? 0}-{BakedStreamMaxZoom} "
                    + $"res {bakedStreamManager.ResidentCount}/{update.Desired}"
                    + (update.WasClampedByBudget ? " (cap)" : string.Empty);
                MainThread.BeginInvokeOnMainThread(() => LodBadgeText = badge);
            }

            // Live fill indicator: while the desired set is still loading (or stales are draining), show the
            // "Doczytywanie terenu… N/M" pill so mid-fill smoothness is visibly "still loading", not "done".
            bool filling = bakedStreamManager.ResidentCount < update.Desired
                || update.Loaded > 0
                || update.StalePending > 0;
            if (filling)
            {
                StreamingDetailText = Fmt(
                    Localization.AppStrings.StatusStreamingDetailFormat,
                    Math.Min(bakedStreamManager.ResidentCount, update.Desired), update.Desired);
            }

            IsStreamingDetail = filling;

            // SELF-KICK until converged (2026-07-03): updates fire on camera moves, so a STILL camera froze the
            // fill mid-way ("mogę tam stać tydzień i nic się nie doczyta" — the log showed resident stuck at
            // 296/448 the moment the user stopped moving). Keep ticking while: this update made progress toward
            // an incomplete desired set (Loaded), OR stale ex-desired residents are still draining through their
            // eviction grace window (Evicted / StalePending — without this they'd squat until the next camera
            // move: "stara grań 5 km dalej nie znika"). A no-progress round with nothing pending stops the loop
            // (unreadable tiles retry on the next real camera move instead of spinning on disk errors).
            continueFill = (update.Loaded > 0 && bakedStreamManager.ResidentCount < update.Desired)
                || update.Evicted > 0
                || update.StalePending > 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[BakedStream] update failed");
        }
        finally
        {
            bakedStreamUpdating = false;
        }

        if (continueFill)
        {
            // Past the debounce window (120 ms), then re-enter from the CURRENT pose (KickBakedStream reads the
            // live camera object). Yields the UI thread between rounds, so input stays responsive during the fill.
            await Task.Delay(BakedStreamSelfKickDelayMs).ConfigureAwait(true);
            KickBakedStream();
        }
    }

    // Delay between self-kicked fill rounds — just past BakedStreamMinInterval so the debounce never eats the kick.
    private const int BakedStreamSelfKickDelayMs = 150;

    // Compact "z16/35205/22289" list for the [BakedStream] churn log; capped so a big legitimate stream
    // (first fill loads hundreds) doesn't flood the line — the interesting case (churn) flips only a few.
    private static string FormatTileKeys(IReadOnlyList<MapaTur.Application.Terrain.DemTileKey> keys)
    {
        const int maxShown = 10;
        IEnumerable<string> shown = keys.Take(maxShown).Select(k => $"z{k.Zoom}/{k.X}/{k.Y}");
        string suffix = keys.Count > maxShown ? $" +{keys.Count - maxShown}" : string.Empty;
        return string.Join(" ", shown) + suffix;
    }

    // Named summit labels re-snapped on the baked z16 tiles, ANCHORED at each summit's ORIGINAL gazetteer
    // coordinate: needle summits (Mnich, Zadni Mnich) are blurred into a neighbour's slope on the coarse
    // base raster ("Mnich ma opis obok siebie"), and in dense clusters (Mięguszowieckie) the coarse snap
    // collapses several names onto one blurred blob — so refining from the coarse position lands on the
    // wrong sub-summit ("klejenie do najbliższego szczytu średnio działa jak jest ich sporo"). The OSM
    // coordinate is the trustworthy anchor; the 1 m tile resolves each tower as its own local maximum.
    // Null/empty index = unchanged (no bake). Logs refined/kept counts for observability.
    private IReadOnlyList<MapaTur.Application.Terrain.TerrainPeak> RefinePeaksOnBakedTiles(
        IReadOnlyList<MapaTur.Application.Terrain.TerrainPeak> peaks,
        IReadOnlyList<NamedSummit> summits,
        MapaTur.Application.Terrain.BakedTileAvailabilityIndex? bakedIndex)
    {
        if (bakedIndex is null || bakedIndex.Count == 0)
        {
            return peaks;
        }

        var anchorByName = new Dictionary<string, GeoPoint>(StringComparer.Ordinal);
        foreach (NamedSummit summit in summits)
        {
            anchorByName.TryAdd(summit.Name, summit.Location);
        }

        var cache = new Dictionary<MapaTur.Application.Terrain.DemTileKey, MapaTur.Domain.Terrain.DemRaster?>();
        var refined = new List<MapaTur.Application.Terrain.TerrainPeak>(peaks.Count);
        int moved = 0, kept = 0;
        foreach (MapaTur.Application.Terrain.TerrainPeak peak in peaks)
        {
            if (string.IsNullOrEmpty(peak.Name))
            {
                refined.Add(peak); // unnamed detected maxima don't need apex precision
                continue;
            }

            GeoPoint anchor = anchorByName.TryGetValue(peak.Name, out GeoPoint gaz) ? gaz : peak.Location;
            (int x, int y) = MapaTur.Application.Terrain.SlippyTileMath.LonLatToTile(
                anchor.Longitude, anchor.Latitude, NearDetailZoom);
            var key = new MapaTur.Application.Terrain.DemTileKey(NearDetailZoom, x, y);
            if (!cache.TryGetValue(key, out MapaTur.Domain.Terrain.DemRaster? fine))
            {
                MapaTur.Application.Terrain.BakedDemTile? tile = bakedIndex.IsBaked(key) ? bakedIndex.Load(key) : null;
                fine = tile is not null ? MapaTur.Application.Terrain.BakedTileMeshBuilder.AsRaster(tile) : null;
                cache[key] = fine;
            }

            if (fine is null)
            {
                refined.Add(peak);
                kept++;
                continue;
            }

            MapaTur.Application.Terrain.TerrainPeak result = PeakNamer.RefineOnRaster(peak, anchor, fine);
            refined.Add(result);
            if (result.Location.Equals(peak.Location))
            {
                kept++;
            }
            else
            {
                moved++;
            }
        }

        logger.LogInformation("[Peaks] fine-apex refine: {Moved} moved to z16 apex, {Kept} kept coarse", moved, kept);
        return refined;
    }

    // Tags every base tile as the BASE SKIN so the renderer can discard its fragments inside the coverage
    // mask (surface ownership: resident hole-free z16 owns its ground — see BaseCoverageMaskBuilder).
    private static IReadOnlyList<TerrainMesh3D> MarkBaseSkin(IReadOnlyList<TerrainMesh3D> tiles)
    {
        foreach (TerrainMesh3D tile in tiles)
        {
            tile.IsBaseSkin = true;
        }

        return tiles;
    }

    /// <summary>World-space coverage mask of the resident full-detail (hole-free z16) ground — the renderer
    /// discards BASE-SKIN fragments inside it, so the smooth base can never depth-bury the streamed 1 m
    /// detail on convex slopes ("lotnisko obok ostrej grani"). Null = no discard.</summary>
    [ObservableProperty]
    private MapaTur.Application.Terrain.BaseCoverageMask? baseCoverageMask;

    // Geographic footprint of each base tile from its world AABB (X east, Y north): the SW-most ground corner maps
    // from (WorldMin.X, WorldMin.Y) and the NE-most from (WorldMax.X, WorldMax.Y), via the tile's own anchor. Built
    // once per scene; consumed by BaseTileOcclusionPlanner to skip base tiles hidden under resident baked detail.
    private static IReadOnlyList<MapBounds> ComputeBaseTileFootprints(IReadOnlyList<TerrainMesh3D> baseTiles)
    {
        var footprints = new List<MapBounds>(baseTiles.Count);
        foreach (TerrainMesh3D tile in baseTiles)
        {
            GeoPoint sw = tile.WorldToGeo(new System.Numerics.Vector3(tile.WorldMin.X, tile.WorldMin.Y, 0f));
            GeoPoint ne = tile.WorldToGeo(new System.Numerics.Vector3(tile.WorldMax.X, tile.WorldMax.Y, 0f));
            footprints.Add(new MapBounds(sw, ne));
        }

        return footprints;
    }

    /// <summary>
    /// Ortho CELL boundaries as base-raster cell columns (the same lon→col mapping BuildTiles' BuildTileCuts
    /// uses) — handed to <see cref="MapaTur.Application.Terrain.RingBasePlanner"/> as forced cuts so no ring
    /// tile straddles an ortho cell (a straddling tile clamps its far-side UV → "strata" stripes).
    /// </summary>
    private static int[] OrthoCellCutColumns(DemRaster raster, MapaTur.Application.Terrain.OrthoCoverage? coverage)
    {
        if (coverage is null)
        {
            return Array.Empty<int>();
        }

        double covW = coverage.Bounds.SouthWest.Longitude;
        double covE = coverage.Bounds.NorthEast.Longitude;
        var cuts = new int[coverage.GridCols - 1];
        for (int i = 1; i < coverage.GridCols; i++)
        {
            double lon = covW + (i * (covE - covW) / coverage.GridCols);
            cuts[i - 1] = (int)Math.Round((lon - raster.West) / (raster.East - raster.West) * (raster.Columns - 1));
        }

        return cuts;
    }

    /// <summary>
    /// Builds the LOD scene's BASE tile set — ring-LOD adaptive plan around <paramref name="focus"/> for the
    /// local whole-Tatra DEM, or the legacy uniform tiling for the online fallback window. Extracted from the
    /// scene build so a vertical-exaggeration change can rebuild the SAME kind of base with new options
    /// (exaggeration is baked into vertex Z — there is no per-frame scale uniform). Pure compute; call off
    /// the UI thread.
    /// </summary>
    private IReadOnlyList<TerrainMesh3D> BuildLodBaseTiles(
        DemRaster preparedBase,
        GeoPoint focus,
        MapaTur.Application.Terrain.TerrainMeshOptions options,
        MapaTur.Application.Terrain.OrthoCoverage? orthoCoverage,
        bool ringBase)
    {
        if (!ringBase)
        {
            return TerrainMesh3D.BuildTiles(preparedBase, options, orthoCoverage: orthoCoverage);
        }

        // Ring-LOD base: native step around the demo focus (where the 1 m detail window lives —
        // its boundary must meet the finest base grid the source has, or the base's blunted ridge
        // pokes out past the window edge as a "duplicated ridge"), coarser rings farther out.
        // Forced cuts keep every plan tile inside ONE ortho cell (the "strata" stripes fix —
        // BuildTiles does the same via BuildTileCuts).
        int focusCol = (int)Math.Round((focus.Longitude - preparedBase.West) / (preparedBase.East - preparedBase.West) * (preparedBase.Columns - 1));
        int focusRow = (int)Math.Round((preparedBase.North - focus.Latitude) / (preparedBase.North - preparedBase.South) * (preparedBase.Rows - 1));
        double midLat = (preparedBase.North + preparedBase.South) / 2.0;
        System.Numerics.Vector3 westWorld = MapaTur.Application.Terrain.LocalTangentProjection.GeoToWorld(
            new GeoPoint(midLat, preparedBase.West), 0f, focus, 1f);
        System.Numerics.Vector3 eastWorld = MapaTur.Application.Terrain.LocalTangentProjection.GeoToWorld(
            new GeoPoint(midLat, preparedBase.East), 0f, focus, 1f);
        double cellMeters = Math.Abs(eastWorld.X - westWorld.X) / (preparedBase.Columns - 1);

        IReadOnlyList<MapaTur.Application.Terrain.PerTileLodDecision> plan = MapaTur.Application.Terrain.RingBasePlanner.Plan(
            preparedBase.Columns, preparedBase.Rows, focusCol, focusRow, cellMeters,
            nearRadiusMeters: LodRingNearRadiusMeters, midRadiusMeters: LodRingMidRadiusMeters,
            forcedColumnCuts: OrthoCellCutColumns(preparedBase, orthoCoverage),
            forcedRowCuts: OrthoCellCutRows(preparedBase, orthoCoverage));
        logger.LogInformation(
            "LOD ring base: {Tiles} plan tiles (step1={S1} step2={S2} step4={S4}), native {Cols}x{Rows} @ {Cell:F1} m/cell",
            plan.Count, plan.Count(t => t.SubsampleStep == 1), plan.Count(t => t.SubsampleStep == 2),
            plan.Count(t => t.SubsampleStep == 4), preparedBase.Columns, preparedBase.Rows, cellMeters);
        return TerrainMesh3D.BuildAdaptiveTiles(preparedBase, plan, options, orthoCoverage: orthoCoverage);
    }

    /// <summary>As <see cref="OrthoCellCutColumns"/>, for ortho cell rows (lat→row).</summary>
    private static int[] OrthoCellCutRows(DemRaster raster, MapaTur.Application.Terrain.OrthoCoverage? coverage)
    {
        if (coverage is null)
        {
            return Array.Empty<int>();
        }

        double covS = coverage.Bounds.SouthWest.Latitude;
        double covN = coverage.Bounds.NorthEast.Latitude;
        var cuts = new int[coverage.GridRows - 1];
        for (int i = 1; i < coverage.GridRows; i++)
        {
            double lat = covN - (i * (covN - covS) / coverage.GridRows);
            cuts[i - 1] = (int)Math.Round((raster.North - lat) / (raster.North - raster.South) * (raster.Rows - 1));
        }

        return cuts;
    }

    /// <summary>True while LOD Etap 3 detail streaming is active (1 m ring follows the camera over a static base).</summary>
    [ObservableProperty]
    private bool isLodStreaming;

    private IReadOnlyList<TerrainMesh3D>? lodBaseTiles;

    // Geographic footprint of each base tile (parallel to lodBaseTiles), computed once per scene from each tile's
    // world AABB. Used by BaseTileOcclusionPlanner to skip drawing base tiles fully hidden behind resident baked
    // detail (overdraw kill — the base is otherwise shaded then depth-rejected under every baked tile).
    private IReadOnlyList<MapBounds>? lodBaseTileFootprints;
    private MapaTur.Application.Terrain.OrthoCoverage? lodOrthoCoverage; // ortho coverage for the current LOD scene (null = hypsometric)

    // ── Stage 2c: baked-tile streaming ──────────────────────────────────────────────────────────────────────
    // The detail path: instead of mosaicking + repairing + per-window re-meshing the z16 raster on every camera
    // move (the runtime-build path, kept as the fallback below), STREAM the pre-baked, pre-repaired tile pyramid.
    // A quadtree screen-space-error selector picks the desired tiles (finest near the eye), a residency planner
    // diffs them against what's resident, and each new tile is read + meshed OFF-thread — no multi-second freeze.
    // Default ON for this dev build; toggle OFF (UseBakedTileStreaming=false) to fall back to the proven runtime
    // build. Inactive when no baked pyramid is on disk (bakedTileIndex null/empty) — the old path then runs.
    private const bool UseBakedTileStreaming = true;
    // 2026-07-02: platform-split and raised on DESKTOP 256 → 448. At cam ~0.5 km over a big face the selector
    // wants >256 finest tiles ("res 256/256 (cap)"); the clamp keeps coarse z13/14 in frame right next to z16,
    // and under grazing golden-hour light the coarse tile's box-averaged macro-normal points into shadow →
    // razor-straight lit/black seams along tile borders ("obcięcie nożem", Buczynowe). The phone keeps the old
    // caps (256 / 1280 MB) — its RAM cannot take a 448×~4 MB resident set.
    // 2026-07-05: desktop 448 → 896. At 448 the selector rode the cap (desired=439-448, clamped often true)
    // — one camera step back from a big face (Gerlach) and the clamp coarsened tiles right where the user
    // looked, on a 64 GB RAM / 16 GB VRAM machine where 896 × ~4 MB ≈ 3.6 GB of geometry is comfortable.
    // Watch [PassTimes] sumGpu after this: more resident tiles = more draw calls (the known FPS ceiling).
    private static readonly int BakedStreamMaxResidentTiles =
        DeviceInfo.Platform == DevicePlatform.WinUI ? 896 : 256;
    // Cull base tiles fully hidden behind resident hole-free baked tiles. SUPERSEDED (2026-07-03) by the
    // per-pixel surface-ownership mask (BaseCoverageMaskBuilder → shader discard of base-skin fragments):
    // whole-base-tile culling required FULL coverage of a large adaptive base tile and almost never fired in
    // practice (culled 0–1/340), so the box-averaged base — 0.5–4 m ABOVE the true z16 surface on convex
    // slopes — kept depth-burying the streamed detail ("lotnisko obok ostrej grani"). The machinery stays
    // for a possible future overdraw optimisation; visual correctness now comes from the mask.
    private const bool BakedStreamCullOccludedBase = false;
    // Residency cap by total resident tile GEOMETRY bytes. The broad 1 m ring streamed many tiles whose geometry
    // alone (on top of the resident ortho) tipped the process into OOM at the route film's lowest point. Capping
    // resident geometry bounds memory while the RING SELECTION stays broad (this only bounds what's kept resident
    // — eviction is still farthest-first).
    // 2026-06-30: raised 512 MB → 1280 MB. At 512 MB the desired finest ring (res 255) clamped to ~127 resident
    // (badge "res 127/255 (cap)"), so HALF the already-baked, available 1 m tiles in view were evicted → the big
    // smooth "dead zones". A 256² tile + skirt + 32-bit indices is ~4 MB, so the full ~255-tile desired set needs
    // ~1 GB; 1280 MB holds it with headroom. GPU is cheap (~8 ms/frame measured) so coverage, not FPS, is the
    // limit. Trade-off: more resident geometry = more RAM/VRAM — watch [Mem] heap for OOM; base-occlusion culling
    // (BaseTileOcclusionPlanner) frees the covered base VBOs, reclaiming some of it.
    private static readonly long BakedStreamMaxResidentBytes =
        DeviceInfo.Platform == DevicePlatform.WinUI ? 6144L * 1024 * 1024 : 1280L * 1024 * 1024; // 896 tiles × ~4 MB ≈ 3.6 GB; 6 GB leaves headroom so the byte cap never silently undercuts the count cap
    // Desktop 8 → 24 (2026-07-05): BuildTiles now builds tiles in PARALLEL, so 24 per update costs close to
    // what 8 sequential did — a full ~450-tile refocus drops from ~40 s of visible fill to ~10 s. The phone
    // keeps 8 (fewer cores, and the old budget was tuned for its thermals).
    private static readonly int BakedStreamMaxConcurrentLoads =
        DeviceInfo.Platform == DevicePlatform.WinUI ? 24 : 8;
    private const double BakedStreamMaxErrorPixels = 2.5;  // screen-space pixel-error budget driving refinement
    private const int BakedStreamMaxZoom = NearDetailZoom; // finest baked zoom = the native 1 m level (z16)
    // The streaming manager for the current LOD scene (rebuilt per BuildLodSceneAsync; null when streaming is
    // inactive). Driven from OnDetailFocusAsync on every camera move.
    private MapaTur.Application.Terrain.BakedTileStreamingManager? bakedStreamManager;
    // True once BuildLodSceneAsync has wired a streaming manager for this scene — gates OnDetailFocusAsync onto
    // the baked path. Cleared with the manager when a new scene loads or streaming is unavailable.
    private bool bakedStreamActive;

    // Route-film state. The CURRENT approach keeps detail streaming ON during the film and gates the camera's
    // forward progress on detail readiness (OnFlightTick holds while IsLodStreaming), so the 1 m detail follows
    // the route with no mid-motion stutter. routeFilmActive is true while the film runs and is used by EndRouteFilm
    // to tell a real route-film teardown apart from the built-in demo. Detail re-centres honour the normal ~35 s
    // reload cooldown (re-centring churns tiles + GC). Set in MakeRouteFilm, cleared in EndRouteFilm.
    private bool routeFilmActive;
    // Legacy pre-cache flags (no longer driven — the pre-cache path was removed): routeFilmAccumulate had
    // OnDetailFocusAsync APPEND retained patches, routeFilmSuppress stopped per-move rebuilds. Kept so the
    // accumulate/suppress branches still compile and EndRouteFilm can defensively reset them.
    private bool routeFilmAccumulate;
    private bool routeFilmSuppress;
    private readonly List<TerrainMesh3D> retainedFilmDetail = new();
    private const int MaxRouteFilmPatches = 8; // desktop memory cap on retained 1 m patches

    // Persistent per-tile detail mesh cache. The window-based rebuild produced 340 FRESH block objects on every
    // camera move → the renderer (SyncTiles, keyed by object) re-uploaded all of them = the flythrough stutter.
    // With the cache + absolute-grid tiling, unchanged ground returns the SAME object across moves → only NEW
    // tiles upload, exactly like the base already does. Kill-switch: false ⇒ legacy (no origins passed, no cache).
    private const bool EnablePerTileDetailCache = true;
    // Plan-tile size in cells, snapped to the absolute grid. MUST equal TerrainMesh3D.maxTileSide (250): a larger
    // quantum makes BuildAdaptiveTiles sub-cut every plan tile into a full block + a tiny sliver (256→250+6), ~4×-ing
    // the block count → 4× the mesh upload (swap hitch) and meshing. 250 ⇒ one block per plan tile, no slivers.
    private const int PerTileDetailQuantum = 250;
    private const int PerTileRepairMargin = 40;   // neighbour cells around each tile when repairing (> the repairs' reach)
    private readonly MapaTur.Application.Terrain.PerTileMeshCache detailMeshCache = new();
    // Repaired-raster cache: FillNarrowZeroStrips + FillPits dominate each build (~23 s over the whole window per
    // the log). Cached per-tile, a sliding window only repairs new tiles ⇒ kills the per-build repair + its GC churn.
    private readonly MapaTur.Application.Terrain.PerTileRepairCache detailRepairCache = new();
    // Roughness cache: the per-tile roughness scan is camera-INDEPENDENT (a pure function of the repaired terrain)
    // yet re-ran over the whole window on every re-center. Cached per absolute tile, a sliding window only scans
    // the new tiles ⇒ the camera-dependent distance→step decision stays per-call (cheap) but the scan does not.
    private readonly MapaTur.Application.Terrain.PerTileRoughnessCache detailRoughnessCache = new();
    // The detail raster (~90 MB) is rented from the pool (PerTileRepair) and shared with the render thread (overlay
    // seating reads it async). Return it only after 2 newer ones exist — the render is then provably off it — so a
    // moving window recycles ~3 buffers instead of churning a fresh 90 MB each reload.
    private readonly Queue<float[]> detailRasterRecycle = new();

    /// <summary>Geographic extent the LOD ortho actually covers (null = none). The 3D view hands this to the
    /// renderer so a base wider than the ortho fades to hypsometric beyond it instead of stretching edge texels.</summary>
    [ObservableProperty]
    private MapaTur.Domain.Geography.MapBounds? lodOrthoCoverageBounds;

    /// <summary>Geographic bounds of the CURRENT streamed 1 m detail window (null = none). The renderer keeps
    /// the proven legacy lake-water seating inside it (the fine basin is real there) and seats/skips lakes
    /// against the coarse base elsewhere, so water planes can't poke through coarse-filled basins.</summary>
    [ObservableProperty]
    private MapaTur.Domain.Geography.MapBounds? lodDetailBounds;
    private IReadOnlyList<NamedSummit>? tatraGazetteer;                  // bundled OSM natural=peak merged with the curated fallback; loaded once
    private GeoPoint lodAnchor;
    private GeoPoint lodDetailCentre;
    private bool lodDetailLoading;
    private DateTime lastLodDetailReloadUtc = DateTime.MinValue;
    // Last per-tile z16 plan/cache counts + realised mesh coarseness, captured for the on-screen LOD diagnostic
    // readout (see LodDetailDiagnostics). avgStep/finestStep reveal whether the z16 mesh is actually fine (≈1) or
    // demoted coarse by the vertex budget (≥2) — the real "plasticine" signal even when z16 is selected & cached.
    private int lastPerTileRequested;
    private int lastPerTileCached;
    private double lastPerTileAvgStep;
    private int lastPerTileFinestStep;
    private string lastPerTileNote = string.Empty; // why the per-tile detail did/didn't render: "ok" | "no-raster" | "no-terrain"
    private const double LodDetailReloadThresholdMeters = 1000;           // re-centre after ~1000 m drift (was 700). Looser gate = fewer expensive per-tile rebuilds while panning → less stutter; the detail patch re-centres slightly later on a fast pan. Pure timing — no effect on glue/budget/geometry.
    // Min spacing between rebuilds, measured from the build START. Each build re-loads + re-processes the whole
    // ~90 MB window (mosaic + repairs + ~400 meshes) ⇒ ~360 MB of Large Object Heap garbage; back-to-back during a
    // flight that outran the background GC ⇒ a ~1.4 s blocking Gen2 stall. The per-tile cache RETAINS detail between
    // builds and the 3.5 km window keeps covering the gaze for ~50 s of flight, so re-centring more than ~every 35 s
    // is redundant work — spacing it there keeps the gaze covered while dropping the allocation rate below the rate
    // the concurrent GC sustains (no blocking collection). NOT masking — removing rebuilds the cache made needless.
    private static readonly TimeSpan LodDetailReloadCooldown = TimeSpan.FromMilliseconds(35000);

    // Wider-coverage P0 (shared world origin). STEP 1 = NO-OP: this threshold is deliberately enormous so
    // WorldOriginPolicy NEVER re-anchors and nothing moves — we only log the camera↔origin drift to confirm
    // the measurement on device before any geometry is touched. The real (few-km) value arrives in step 2.
    private const double OriginReanchorThresholdMeters = 1e9;

    // Wider-coverage P1 streaming PROBE (read-only): plan the base tiles the view would stream + the residency
    // load/keep/evict decision, and LOG them — no fetch, no swap. Validates the streaming pipeline on real
    // device bounds before any tile loading is wired (mirrors the no-op origin probe).
    private const int BaseStreamingMaxTiles = 64;
    private const int BaseStreamingMinZoom = 9;
    private const int BaseStreamingMaxZoom = 14;
    private readonly DemTileResidencyPlanner baseStreamingProbePlanner = new(96);

    // Krok 4 (screen-space-error LOD): the detail patch follows the look-at point (raycast through the
    // screen centre, Krok 1) and its zoom adapts to the on-screen error (Krok 2/3) instead of a fixed z16.
    private const int LodBaseZoom = 13;                                   // static base zoom (~6 m; z12 shaved summit apexes → distant peaks too blunt vs real)
    private const double LodBaseHalfWidthMeters = 6000.0;                  // FALLBACK ONLY: online z13 window radius used if the local whole-Tatra tatry.dem isn't installed. The normal LOD base is the local DEM (whole Tatras, ~30 m), so this rarely fires.

    // Ring-LOD base (the "duplicated ridge" fix): the static whole-Tatra base renders at per-tile steps —
    // NATIVE tatry.dem cells out to Near, step 2 to Mid, step 4 beyond — so the base silhouette near the
    // 1 m detail window matches the source instead of a uniformly blunted subsample (whose shifted ridge
    // poked out past the window edge as a second, paler ridge line). ~2 M verts total at tatry.dem scale,
    // less than the old uniform 2160×1100 base. Rings are static per demo entry (centred on the entry focus).
    // DESKTOP: no rings — the near radius swallows the whole range, so the base is NATIVE 15 m EVERYWHERE
    // (~9.5 M verts, ~450 MB of VBO — desktop-class GPU territory, far over any phone budget).
#if WINDOWS
    private const double LodRingNearRadiusMeters = 1_000_000.0;
    private const double LodRingMidRadiusMeters = 2_000_000.0;
#else
    // Reverted to 6 km / 14 km — widening the native ring (to 20-100 km) blew the vertex count and crashed FPS
    // on the phone for no quality win (the base is coarse regardless; the fix is the 1 m detail, not the base).
    private const double LodRingNearRadiusMeters = 6000.0;
    private const double LodRingMidRadiusMeters = 14000.0;
#endif
    private const int NearDetailZoom = 16;                                // finest detail zoom (GUGiK native 1 m)
    private static readonly int[] DetailZoomCandidates = { 16, 14, 12 };  // finest → coarsest, fed to ScreenSpaceLod
    private const double DetailMaxErrorPixels = 1.0;                      // per-tile screen-space error budget (tightened 2.0→1.0: with the 48 M desktop budget barely used, keep near/mid tiles at step 1 so the rendered mesh — and the trail seated on it — tracks the true 1 m rock, not a smoothed 2 m surface; far tiles still coarsen)
    private const int BaseDetailZoomFloor = 12;                          // chosen zoom at/below base (z12) ⇒ no detail patch
    private const double DetailCoverageFloorMeters = 100.0;              // below this ⇒ GUGiK out-of-coverage flat-0 → hole (Tatra-context guard)
    private const int DetailEdgeMatchRows = 8;                           // morph band: blend the patch perimeter into the base over N rows
    private const int PerTileEdgeMatchRows = 40;                         // per-tile window-perimeter morph band, in FULL-RES z16 cells (~2.4 m) ≈ 100 m — melts the patch edge into the ~45 m-cell whole-Tatra base (no step/"duplicated ridge" at the window boundary)
    // Look-at fallback: if the screen-centre ray hits sky (looking horizontally across a ridge), probe lower in
    // the frame so detail still streams to the terrain the camera is flying toward instead of falling back to
    // the off-screen target. Centre column, so aspect-independent.
    private static readonly float[] LookAtLowerFrameFallbacks = { 0.62f, 0.74f, 0.86f };
    private const bool ShowDiagnosticDetailTint = false;                 // Krok 5: tint OFF for the seamless look; true re-enables tint-by-zoom debug

    // Model 1 (per-tile roughness): split the loaded z16 window into a grid and give each tile its own
    // subsample step from screen-space-error × roughness (sharp ridges/walls keep 1 m from farther, smooth
    // valleys step down), capped by a hard vertex budget. Flag OFF ⇒ the proven single-patch path stays.
    private static readonly bool UsePerTileDetail = true;                // false ⇒ fall back to BuildDetailTilesAsync (single patch)
    private const int PerTileGridN = 8;                                  // N×N crops over the loaded window (8 = ~500 m tiles, finer budget control)
    private static readonly int[] PerTileSubsampleSteps = { 1, 2, 4, 8 };// finest → coarsest stride per tile
    private const float PerTileSkirtDepthMeters = 25f;                   // vertical curtain hides inter-tile (and window→base) seams


    private const int PerTileMaxTileSide = 250;                          // skirt + 16-bit index limit
#if WINDOWS
    private const long PerTileVertexBudget = 48_000_000;                // desktop GPU (sized to a discrete card, e.g. RTX-class 16 GB VRAM + ample RAM): the 3.5 km z16 window is ~49 M cells @1 m, so 48 M lets the budget essentially STOP demoting tiles — geometry is then as fine as the screen-space-error metric wants (near/mid = step 1, far coarsens by SSE, the correct reason, not budget starvation). Was 6 M (phone value left in by mistake) → ~⅔ of tiles demoted to an 8 m "plasticine" mesh under the 1 m photo. ~2 GB VRAM for verts; detail rebuild is slower (off-thread, no freeze). Lower this if running on a weaker desktop GPU.
#else
    private const long PerTileVertexBudget = 6_000_000;                  // raised 3 M→6 M: a 2200 m z16 window is ~7.95 M cells @1 m, so 3 M forced ConstrainToBudget to demote most tiles to step 2/4 (~3-6 m = the "plasticine" mesh even with z16 ON). 6 M holds near-step-1. FPS/mem to verify on device.
#endif
    private const int PerTileRoughnessStride = 4;                        // sample every 4th cell for roughness (cost ÷16, metric scale kept)
    private const int PerTileRoughnessNeighborDistance = 8;              // measure curvature over ±8 cells (~10 m) so ridge roughness registers (±1 reads ~0)
    private const int PerTileNormalSmoothingRadius = 3;                  // normal low-pass radius (1 = sharp; >1 softens 1 m facets, heights untouched) — A/B knob
#if WINDOWS
    private const double PerTileWindowRadiusMeters = 3500.0;            // desktop: ~2.3× the phone window — "1 m everywhere you look", budget scaled to match (2500 shrank GC garbage but the detail reached too close — user preferred 3500)
#else
    private const double PerTileWindowRadiusMeters = 2200.0;             // raised from 1500: the 1 m ring was too small, so the deforming 15 m base showed on the very peaks you look at
#endif
    private const double PerTileCameraBubbleRadiusMeters = 250.0;        // near-camera tiles forced fine regardless of look-at (no blocky foreground under a low camera)
    private const int PerTileCameraBubbleStep = 2;                       // coarsest step allowed inside the camera bubble

    // ───────────────────────────────────────────────────────────────────────────────────────────────────
    // STAGE 0 — MAXIMUM-FIDELITY QUALITY PROOF (reversible). Flip this ONE flag to false to fully revert.
    // Goal: render a close peak at full native 1 m with NO budget/SSE demotion, so the user can judge whether
    // baking full-resolution 1 m tiles is worth it. Performance is intentionally NOT a concern here (a one-time
    // close look may stutter). When ON, the per-tile detail planner is fed a single candidate step (1) and an
    // effectively-infinite vertex budget, so EVERY near-window tile renders at true 1 m (finestStep=1,
    // avgStep=1.0, hist all in bucket 1) — VertexBudget can never demote a tile that has no coarser level.
    // The detail window is also tightened so the near peak is genuinely all step-1 without an absurd vertex
    // count. Texture sampling (ortho mipmaps + max anisotropy) is already on in the GL renderer's ortho path;
    // Stage 0 only forces the GEOMETRY step/budget here. Toggle OFF ⇒ the proven adaptive {1,2,4,8}+budget path.
    // Stage 2c: the baked-tile STREAMING path (UseBakedTileStreaming) replaces the Stage-0 desktop proof — the
    // near field is full native 1 m from the baked z16 pyramid, the far field uses coarser baked LODs, all
    // pre-repaired so a camera move never triggers the per-window mosaic/repair/rebuild. Stage-0's "force every
    // tile to step 1 with no budget" is therefore off; the baked path is the proof now.
    private static readonly bool Stage0MaxFidelity = false;

    // Stage-0 effective knobs (used only when Stage0MaxFidelity). Single step ⇒ no demotion is possible;
    // long.MaxValue budget ⇒ ConstrainToBudget never steps anything down. SSE error budget irrelevant with one
    // candidate, but tightened anyway so the intent reads clearly. Window kept generous but bounded so the near
    // peak is full 1 m (a 2500 m z16 window ≈ 25 M cells @1 m — heavy but a one-time look, fine on a desktop GPU).
    private static readonly int[] Stage0SubsampleSteps = { 1 };          // ONLY step 1 ⇒ every tile is native 1 m, budget cannot demote
    private const long Stage0VertexBudget = long.MaxValue;              // no budget cap ⇒ no plasticine demotion (perf intentionally ignored)
    private const double Stage0WindowRadiusMeters = 2500.0;            // tightened from 3500 so the full step-1 window stays a sane vertex count for a one-time close look
    private const double Stage0MaxErrorPixels = 0.5;                   // (moot with one candidate) signals "ask for the finest" to SelectLod

    // Effective per-tile knobs: Stage-0 overrides when the proof flag is on, else the proven adaptive values.
    private static int[] EffectivePerTileSubsampleSteps => Stage0MaxFidelity ? Stage0SubsampleSteps : PerTileSubsampleSteps;
    private static long EffectivePerTileVertexBudget => Stage0MaxFidelity ? Stage0VertexBudget : PerTileVertexBudget;
    private static double EffectivePerTileWindowRadiusMeters => Stage0MaxFidelity ? Stage0WindowRadiusMeters : PerTileWindowRadiusMeters;
    private static double EffectiveDetailMaxErrorPixels => Stage0MaxFidelity ? Stage0MaxErrorPixels : DetailMaxErrorPixels;

    // Last look-at world point with a real terrain hit. On a transient raycast miss (sky / off-DEM) the
    // detail holds here instead of teleporting to the camera target — avoids micro-jumps of the patch.
    // Kept in the scene frame of the current lodAnchor, so it is reset whenever a new LOD session loads.
    private System.Numerics.Vector3? lastValidLookAtWorld;

    // Builds the tinted 1 m detail tiles for a patch centred on `focus`, anchored to the fixed scene origin
    // `anchor` (= base centre) so it lands correctly over the static base. Cyan tint is a diagnostic.
    private async Task<IReadOnlyList<TerrainMesh3D>?> BuildDetailTilesAsync(GeoPoint focus, GeoPoint anchor, int zoom)
    {
        if (regionDemLoader is null)
        {
            return null;
        }

        // Detail covers the near-field (~4 km around the focus) as ONE stitched raster → one crack-free mesh
        // (no overlapping multi-res layers, which caused vertical curtains). Zoom is the look-at's adaptive
        // SSE zoom; loaded CACHE-ONLY so flying never triggers a WCS download.
        MapBounds window = LodTerrainWindow.Around(focus, 2000);
        IReadOnlyList<DemTileKey> planned = DemTilePlanner.TilesForBounds(window, zoom);
        int cachedCount = detailTileCached is null ? planned.Count : planned.Count(detailTileCached);
        logger.LogInformation(
            "LOD cache-only z{Zoom}: requested={Requested}, cached={Cached}, skipped={Skipped}",
            zoom, planned.Count, cachedCount, planned.Count - cachedCount);

        // fillNoData: false — keep NoData so the NoData-aware mesh holes gaps/uncovered cells through to the
        // base (Krok 4c), instead of rendering flat geometry over them (the yellow blinds).
        DemRaster? detail = await regionDemLoader.LoadRegionAsync(window, zoom, tileAvailable: DetailTileGate, fillNoData: false).ConfigureAwait(true);
        if (detail is null)
        {
            logger.LogWarning("LOD detail: no cached z{Zoom} raster at {Lat:F4},{Lon:F4}", zoom, focus.Latitude, focus.Longitude);
            return null;
        }

        // Checklist §A (same chain as the per-tile path): bridge narrow flat-0 strips from the 1 m neighbours
        // + despike one-cell pits, THEN hole the flat-0 out-of-coverage plate.
        detail = DemRasterRepair.FillNarrowZeroStrips(detail, maxWidthCells: 24);
        detail = DemRasterRepair.FillPits(detail, depthThresholdMeters: 20.0);
        detail = DemRasterRepair.HoleBelow(detail, DetailCoverageFloorMeters);

        (double dMin, double dMax) = detail.GetElevationRange();
        logger.LogInformation(
            "LOD detail @ {Lat:F4},{Lon:F4} z{Zoom}: {Cols}x{Rows}, elev {Min:F0}-{Max:F0} m",
            focus.Latitude, focus.Longitude, zoom, detail.Columns, detail.Rows, dMin, dMax);

        // NoData fallback (rule #12): past the Polish border GUGiK returns empty/zero tiles — don't overlay
        // a flat-zero plateau, keep the coarse base showing there instead.
        if (!DemRasterCoverage.HasTerrain(detail, minTopMeters: 100))
        {
            logger.LogInformation("LOD detail @ {Lat:F4},{Lon:F4}: no 1 m coverage — keeping base", focus.Latitude, focus.Longitude);
            return null;
        }

        // Backfill the detail's NoData voids from the coarse base: GUGiK NMT has voids ALONG WATERCOURSES
        // (no LiDAR ground return on water) and past the border — dropped triangles there read as chains of
        // black see-through slits along streams. Base-height terrain in the voids keeps the base's visual.
        if (TerrainRaster is { } detailBase)
        {
            detail = DemRasterRepair.FillNoDataFrom(detail, detailBase);
        }

        // Cap the detail so each reload stays smooth while flying (a 4 km z16 patch is ~7 M verts; ~1.5 M
        // keeps it clearly finer than the base yet quick to rebuild + upload).
        detail = DemRasterDownsampler.SubsampleToMaxCells(detail, maxCells: 1_500_000);

        // Krok 5: the diagnostic tint is OFF — the detail now renders in plain hypsometric colour like the
        // base, so (heights edge-matched) it melts in seamlessly and the patch is invisible. The tint-by-zoom
        // (z16 cyan / z14 amber / coarser red) stays available behind the flag for debugging; the LOD is still
        // observable from the `cache-only z{N}` / `detail z{N}` log lines.
        uint? tint = ShowDiagnosticDetailTint
            ? (zoom >= 16 ? 0xFF00E5FFu : zoom >= 14 ? 0xFFFFC400u : 0xFFFF3B30u)
            : null;
        var detailOptions = new MapaTur.Application.Terrain.TerrainMeshOptions
        {
            VerticalExaggeration = (float)Math.Clamp(VerticalExaggeration, 1.0, 5.0),
            OverlayTintArgb = tint,
            OverlayTintStrength = 0.45f,
        };
        // Edge matching (Krok 4c): morph the patch's outer band into the coarse base over several rows so it
        // melts in instead of stepping down to it ("hard boundary").
        DemRaster? baseForEdges = TerrainRaster;
        LodDetailBounds = window; // fine detail covers this area → lake water keeps its proven legacy seating here
        DetailElevation = new DetailElevationField(detail); // seat overlays on this 1 m surface inside the window
        return await Task.Run(() =>
            TerrainMesh3D.BuildTiles(detail, detailOptions, projectionAnchor: anchor, edgeHeightSource: baseForEdges, edgeMatchRows: DetailEdgeMatchRows)).ConfigureAwait(true);
    }

    // Model 1 per-tile detail: loads the SAME z16 window as the single patch, but instead of one global
    // subsample it splits the window into a grid and gives each tile its own step from screen-space-error ×
    // roughness (sharp/near → 1 m, smooth/far → coarser), capped by a hard vertex budget. Per-tile seams are
    // covered by a skirt (a vertical curtain), so we deliberately DON'T edge-match every crop to the base —
    // that would morph the inner (neighbour-shared) edges too and waffle the surface. v1 is skirt-only; if the
    // outer window→base boundary steps visibly, an outer-only edge-match pre-pass is the next iteration.
    private async Task<IReadOnlyList<TerrainMesh3D>?> BuildPerTileDetailAsync(
        GeoPoint focus, GeoPoint anchor, System.Numerics.Vector3 cameraPosition, double fovY, double viewportHeight)
    {
        if (regionDemLoader is null)
        {
            return null;
        }

        MapBounds window = LodTerrainWindow.Around(focus, EffectivePerTileWindowRadiusMeters);
        IReadOnlyList<DemTileKey> planned = DemTilePlanner.TilesForBounds(window, NearDetailZoom);
        int cachedCount = detailTileCached is null ? planned.Count : planned.Count(detailTileCached);
        lastPerTileRequested = planned.Count;   // surfaced in the on-screen LOD diagnostic (LodDetailDiagnostics)
        lastPerTileCached = cachedCount;
        logger.LogInformation(
            "LOD per-tile cache-only z{Zoom}: requested={Requested}, cached={Cached}, skipped={Skipped}",
            NearDetailZoom, planned.Count, cachedCount, planned.Count - cachedCount);

        // Time the mosaic (fetch+decode+stitch). With the loader's decoded-tile cache, a re-center decodes only the
        // newly-entered z16 tiles and reuses the overlap, so mosaicMs should drop sharply once the window is warm —
        // surfaced in the timing log below alongside repair/roughness/mesh so the breakdown is visible on device.
        var mosaicTimer = System.Diagnostics.Stopwatch.StartNew();
        DemRaster? full = await regionDemLoader.LoadRegionAsync(window, NearDetailZoom, tileAvailable: DetailTileGate, fillNoData: false).ConfigureAwait(true);
        mosaicTimer.Stop();
        long mosaicMs = mosaicTimer.ElapsedMilliseconds;
        int mosaicTileHits = regionDemLoader.LastDecodedTileHits;
        int mosaicTileMisses = regionDemLoader.LastDecodedTileMisses;
        if (full is null)
        {
            lastPerTileNote = "no-raster";
            logger.LogWarning("LOD per-tile: no cached z{Zoom} raster at {Lat:F4},{Lon:F4}", NearDetailZoom, focus.Latitude, focus.Longitude);
            return null;
        }

        // All the heavy CPU — HoleBelow, the roughness/SSE/budget plan, and the per-tile meshing — runs OFF the
        // UI thread so flying never freezes (the roughness scan over the ~8 M-cell z16 window was the stall).
        DemRaster loaded = full;
        double loadedMax = full.GetElevationRange().Max; // raw z16 cache content max — distinguishes empty tiles vs repair zeroing
        float exaggeration = (float)Math.Clamp(VerticalExaggeration, 1.0, 5.0);
        RoughnessLodPreset preset = RoughnessLodPreset.Balanced;
        var detailOptions = new MapaTur.Application.Terrain.TerrainMeshOptions
        {
            VerticalExaggeration = exaggeration,
            SkirtDepthMeters = PerTileSkirtDepthMeters,
            NormalSmoothingRadius = PerTileNormalSmoothingRadius,
        };

        // Absolute z16-pixel origin of the window's NW corner. The mosaic is whole-tile aligned (OnlineRegionDemLoader),
        // so origin = minTile × tilePixels — a STABLE coordinate for the same ground across window moves, which is what
        // lets the per-tile mesh cache reuse blocks while flying. Disabled / non-uniform tiling ⇒ origin -1 ⇒ legacy.
        int detailAbsCol = -1, detailAbsRow = -1;
        int detailMinTileX = 0, detailMinTileY = 0, detailTilePxX = 0, detailTilePxY = 0;
        MapaTur.Application.Terrain.PerTileMeshCache? activeCache = null;
        MapaTur.Application.Terrain.PerTileRepairCache? activeRepairCache = null;
        MapaTur.Application.Terrain.PerTileRoughnessCache? activeRoughnessCache = null;
        if (EnablePerTileDetailCache && planned.Count > 0)
        {
            int minTileX = int.MaxValue, minTileY = int.MaxValue, maxTileX = int.MinValue, maxTileY = int.MinValue;
            foreach (MapaTur.Application.Terrain.DemTileKey k in planned)
            {
                minTileX = Math.Min(minTileX, k.X);
                minTileY = Math.Min(minTileY, k.Y);
                maxTileX = Math.Max(maxTileX, k.X);
                maxTileY = Math.Max(maxTileY, k.Y);
            }

            int tilesX = maxTileX - minTileX + 1;
            int tilesY = maxTileY - minTileY + 1;
            if (tilesX > 0 && tilesY > 0 && full.Columns % tilesX == 0 && full.Rows % tilesY == 0)
            {
                detailTilePxX = full.Columns / tilesX;
                detailTilePxY = full.Rows / tilesY;
                detailMinTileX = minTileX;
                detailMinTileY = minTileY;
                detailAbsCol = minTileX * detailTilePxX;
                detailAbsRow = minTileY * detailTilePxY;
                activeCache = detailMeshCache;
                activeRepairCache = detailRepairCache;
                activeRoughnessCache = detailRoughnessCache;
                detailMeshCache.BeginRound();
                detailRepairCache.BeginRound();
                detailRoughnessCache.BeginRound();
            }
        }

        DemRaster? perTileBase = TerrainRaster; // captured on the UI thread for the worker below
        // The finished detail raster (1 m + base-filled voids) is carried back out of the worker so trails /
        // roads / route can seat on the SAME surface the tiles render. The await below is the memory barrier.
        DemRaster? builtDetailRaster = null;
        IReadOnlyList<MapaTur.Application.Terrain.PerTileLodDecision>? builtPlan = null; // per-tile steps → overlays seat on the SAME subsampled surface the mesh draws
        IReadOnlyList<TerrainMesh3D>? perTileResult = await Task.Run(() =>
        {
            var totalTimer = System.Diagnostics.Stopwatch.StartNew();
            // Bridge narrow flat-0 strips from the 1 m neighbours — the GUGiK z16 tile-edge dropout that
            // renders as a thin, dead-straight vertical "fault" (0 m survives the NoData filter). ONLY narrow
            // (<=24-cell ~ 58 m) interior strips bracketed by valid data are interpolated; WIDE 0-voids (whole
            // GUGiK holes, e.g. over a tarn) are left for the base-backfill below, so we never fabricate a
            // smooth patch ("square") across a real coverage gap.
            // FillNarrowZeroStrips (bridge GUGiK z16 tile-edge "fault" dropouts) + FillPits (despike the 1 m
            // trench-dashes along watercourses) are the heavy, neighbour-aware passes the log proved dominate each
            // build (~23 s over the whole 8 M-cell window). They are CACHED PER TILE here — identical result
            // (a margin gives edge context; proven cell-for-cell by PerTileRepairTests), but a sliding window only
            // repairs the NEW tiles instead of re-scanning everything every move. Legacy chain when the cache is off.
            var repairTimer = System.Diagnostics.Stopwatch.StartNew();
            DemRaster repaired = activeRepairCache is not null
                ? MapaTur.Application.Terrain.PerTileRepair.RepairWindowCached(
                    loaded, planned, detailMinTileX, detailMinTileY, detailTilePxX, detailTilePxY,
                    activeRepairCache, PerTileRepairMargin, pitDepthMeters: 20.0, zeroStripMaxCells: 24)
                : DemRasterRepair.FillPits(DemRasterRepair.FillNarrowZeroStrips(loaded, maxWidthCells: 24), depthThresholdMeters: 20.0);

            // The stitched window mosaic is fully consumed now (`repaired` is a SEPARATE buffer) — recycle its
            // ~90 MB array to the pool so the next reload reuses it instead of churning the LOH. `loaded` is dead
            // past this point (only `repaired`/`holed` are used below), so nothing reads the returned buffer.
            MeshBufferPool.Shared.Return(loaded.Samples);
            DemRasterRepair.HoleBelowInPlace(repaired, DetailCoverageFloorMeters); // mutate (worker owns it) — no 90 MB clone
            DemRaster holed = repaired;
            repairTimer.Stop();
            if (!DemRasterCoverage.HasTerrain(holed, minTopMeters: 100))
            {
                double holedMax = holed.GetElevationRange().Max;
                // raw = max of the loaded z16 cache; holed = after FillNarrowZeroStrips→FillPits→HoleBelow.
                // raw≈0 ⇒ cache tiles are empty/NoData (data problem); raw≈2000 but holed<100 ⇒ the repair chain
                // zeroed real terrain (code bug, e.g. HoleBelow over-holing).
                lastPerTileNote = $"no-terrain raw{loadedMax:F0} holed{holedMax:F0}";
                logger.LogInformation(
                    "LOD per-tile @ {Lat:F4},{Lon:F4}: no 1 m coverage — keeping base (rawMax={Raw:F0} holedMax={Holed:F0})",
                    focus.Latitude, focus.Longitude, loadedMax, holedMax);
                return null;
            }

            // Backfill NoData voids from the coarse base (GUGiK voids on watercourses / past the border):
            // dropped triangles there read as chains of black see-through slits along streams. Base-height
            // terrain in the voids keeps the base's visual; a fully-empty patch already returned null above.
            if (perTileBase is { } baseRaster)
            {
                DemRasterRepair.FillNoDataFromInPlace(holed, baseRaster); // mutate in place — no 90 MB clone
            }

            builtDetailRaster = holed; // surface the seated-surface raster for overlay elevation sampling

            PerTilePlanResult planResult = PerTileDetailPlanner.PlanDetailed(
                holed, cameraPosition, anchor, exaggeration, PerTileGridN, EffectivePerTileSubsampleSteps,
                fovY, viewportHeight, EffectiveDetailMaxErrorPixels, preset, EffectivePerTileVertexBudget,
                PerTileRoughnessStride, PerTileRoughnessNeighborDistance,
                PerTileCameraBubbleRadiusMeters, PerTileCameraBubbleStep,
                absColOrigin: detailAbsCol, absRowOrigin: detailAbsRow, tileQuantum: PerTileDetailQuantum,
                roughnessCache: activeRoughnessCache);
            IReadOnlyList<PerTileLodDecision> plan = planResult.Tiles;
            builtPlan = plan; // carried out so DetailElevation reconstructs the rendered (subsampled) surface, not raw 1 m

            long totalVertices = 0;
            foreach (PerTileLodDecision d in plan)
            {
                int planCols = (d.Columns + d.SubsampleStep - 1) / d.SubsampleStep;
                int planRows = (d.Rows + d.SubsampleStep - 1) / d.SubsampleStep;
                totalVertices += (long)planCols * planRows;
            }

            int finestStep = plan.Count == 0 ? 0 : plan.Min(d => d.SubsampleStep);
            double avgStep = plan.Count == 0 ? 0 : plan.Average(d => d.SubsampleStep);
            lastPerTileAvgStep = avgStep;       // surfaced on the LOD badge (LodDetailDiagnostics) to see real mesh coarseness
            lastPerTileFinestStep = finestStep; // (worker thread write; the await in OnDetailFocusAsync is the memory barrier)
            int s1 = plan.Count(d => d.SubsampleStep == 1);
            int s2 = plan.Count(d => d.SubsampleStep == 2);
            int s4 = plan.Count(d => d.SubsampleStep == 4);
            int s8 = plan.Count(d => d.SubsampleStep >= 8);
            int boostedTiles = planResult.TileInfos.Count(t => t.RoughnessFactor > 1.01);
            int demotedTiles = planResult.TileInfos.Count(t => t.FinalStep > t.DesiredStep);
            double maxRoughness = planResult.TileInfos.Count == 0 ? 0 : planResult.TileInfos.Max(t => t.RoughnessMeters);
            double maxFactor = planResult.TileInfos.Count == 0 ? 0 : planResult.TileInfos.Max(t => t.RoughnessFactor);
            // How close/far the finest (step-1) tiles sit from the camera — reveals whether the foreground right
            // under the eye is sharp (nearStep1 small) or detail only starts hundreds of metres out.
            var step1Distances = planResult.TileInfos.Where(t => t.FinalStep == 1).Select(t => t.DistanceMeters).ToList();
            double nearStep1 = step1Distances.Count == 0 ? -1 : step1Distances.Min();
            double farStep1 = step1Distances.Count == 0 ? -1 : step1Distances.Max();

            var meshTimer = System.Diagnostics.Stopwatch.StartNew();
            // Crack-free: build every tile straight from the FULL window raster at its own step on the shared
            // absolute grid (NOT independent crops + per-crop subsample, which made different-step tiles' edges
            // land at different world positions → see-through cracks). Edges weld to coarser neighbours.
            // NOTE: deliberately NO edgeHeightSource here. Morphing the window perimeter toward the coarse base
            // looked right on paper, but where the boundary crosses a RIDGE the base's crest is displaced, so
            // the morph dragged the detail edge down the base's flank — an artificial NOTCH (black gap) at the
            // boundary, worse than the un-morphed step (verified on device). The boundary mismatch is instead
            // minimized by a finer base (vertex budget) — silhouettes then nearly coincide.
            var meshes = new List<TerrainMesh3D>(TerrainMesh3D.BuildAdaptiveTiles(
                holed, plan, detailOptions, projectionAnchor: anchor, orthoCoverage: lodOrthoCoverage,
                cache: activeCache, absColOrigin: detailAbsCol < 0 ? 0 : detailAbsCol, absRowOrigin: detailAbsRow < 0 ? 0 : detailAbsRow));

            meshTimer.Stop();
            totalTimer.Stop();

            // Diagnostics (Krok 4): the FULL per-tile distribution so the screenshot and the log can't diverge —
            // step histogram + avgStep (is the viewed ridge actually fine?), boosted/demoted counts (is it the
            // SSE metric or the budget keeping it coarse?), max roughness/factor, plus the split timings.
            logger.LogInformation(
                "LOD per-tile [{Preset}]: tiles={Tiles} finestStep={Finest} avgStep={Avg:F1} hist(1/2/4/8)={S1}/{S2}/{S4}/{S8} " +
                "boosted={Boosted} demoted={Demoted} maxRough={MaxR:F1}m maxFactor={MaxF:F2} vertices={Verts}/{Budget}; " +
                "step1Dist={NearS1:F0}-{FarS1:F0}m; " +
                "meshHit={MeshHit} meshMiss={MeshMiss}(new={MissNew} churn={MissChurn}) roughHit={RoughHit} roughMiss={RoughMiss} " +
                "tileHit={TileHit} tileMiss={TileMiss} win={WinCols}x{WinRows} tilePx={TilePx} mosaicMs={MosaicMs} repairMs={RepairMs} roughnessMs={RoughnessMs:F0} planningMs={PlanningMs:F0} meshBuildMs={MeshMs} totalDetailMs={TotalMs} (stride {Stride})",
                preset.Name, plan.Count, finestStep, avgStep, s1, s2, s4, s8,
                boostedTiles, demotedTiles, maxRoughness, maxFactor, totalVertices, EffectivePerTileVertexBudget,
                nearStep1, farStep1,
                activeCache?.Hits ?? 0, activeCache?.Misses ?? 0, activeCache?.MissNewGround ?? 0, activeCache?.MissSameGround ?? 0,
                activeRoughnessCache?.Hits ?? 0, activeRoughnessCache?.Misses ?? 0,
                mosaicTileHits, mosaicTileMisses,
                holed.Columns, holed.Rows, detailTilePxX,
                mosaicMs, repairTimer.ElapsedMilliseconds, planResult.RoughnessMs, planResult.PlanningMs, meshTimer.ElapsedMilliseconds, totalTimer.ElapsedMilliseconds, PerTileRoughnessStride);

            lastPerTileNote = "ok";
            return (IReadOnlyList<TerrainMesh3D>?)meshes;
        }).ConfigureAwait(true);

        // Drop cached blocks no longer in the live window (bounds memory). Safe here: one build runs at a time
        // (lodDetailLoading gate) and the await above is the memory barrier after the worker's GetOrBuild calls.
        if (activeCache is not null)
        {
            detailMeshCache.EvictUnused();
        }

        if (activeRepairCache is not null)
        {
            detailRepairCache.EvictUnused();
        }

        if (activeRoughnessCache is not null)
        {
            detailRoughnessCache.EvictUnused();
        }

        if (perTileResult is not null)
        {
            LodDetailBounds = window; // fine detail covers this area → lake water keeps its legacy seating here
            DetailElevation = builtDetailRaster is not null ? new DetailElevationField(builtDetailRaster, builtPlan) : null;

            // Recycle the detail raster buffer: hold the last 3, return the oldest. By the time a buffer is returned,
            // two newer detail rasters have replaced it on screen ⇒ no render frame still reads it ⇒ safe to reuse.
            if (builtDetailRaster is not null)
            {
                detailRasterRecycle.Enqueue(builtDetailRaster.Samples);
                while (detailRasterRecycle.Count > 3)
                {
                    MeshBufferPool.Shared.Return(detailRasterRecycle.Dequeue());
                }
            }
        }

        return perTileResult;
    }

    /// <summary>
    /// LOD Krok 4: the camera moved over the static base — re-centre the 1 m detail patch on the LOOK-AT
    /// point (raycast through the screen centre onto the terrain, Krok 1) instead of the camera target, and
    /// pick its zoom from the on-screen error (Krok 2/3) so detail follows the gaze and adapts to distance.
    /// Swaps ONLY the detail layer; the base (and camera framing) stays put. Debounced + cooldown so a fast
    /// pan doesn't thrash rebuilds.
    /// </summary>
    public async Task OnDetailFocusAsync(
        MapaTur.Application.Terrain.Camera3D camera, int viewportHeightPixels = 0, int viewportWidthPixels = 0)
    {
        ArgumentNullException.ThrowIfNull(camera);
        // During a route film the detail is PRE-CACHED and retained; a per-move rebuild here would only swap in a
        // lagging single patch (the very bug we're fixing), so suppress it and keep the pre-built 1 m on screen.
        if (routeFilmSuppress)
        {
            return;
        }

        // NOTE: lodDetailLoading is checked LOWER DOWN (before the rebuild), not here — so the diagnostic badge
        // still refreshes the tier/distance on every camera move even while a detail build (15–26 s) is in flight.
        if (!IsLodStreaming || lodBaseTiles is null)
        {
            return;
        }

        // Stage 2c: when baked-tile streaming is active for this scene, stream the pre-baked pyramid instead of
        // the runtime mosaic/repair/rebuild below. Returns straight after — the baked path is self-contained (its
        // own selector + residency + off-thread load), so none of the runtime-build gating applies.
        if (bakedStreamActive && bakedStreamManager is not null)
        {
            await StreamBakedDetailAsync(camera, viewportHeightPixels, viewportWidthPixels).ConfigureAwait(true);
            return;
        }

        // The runtime-build detail path (the fallback when streaming is off / unavailable) needs the region loader.
        if (regionDemLoader is null)
        {
            return;
        }

        // DEBUG (roughness-LOD tuning): full camera orbit so a good viewpoint can be pinned and reproduced across
        // redeploys (paste these 6 numbers into Terrain3DView.DebugPinnedCamera). Order matches SerializeCamera.
        logger.LogInformation(
            "LOD camera: {TX:R};{TY:R};{TZ:R};{Dist:R};{Az:R};{Pitch:R} (fov={Fov:F4})",
            camera.Target.X, camera.Target.Y, camera.Target.Z, camera.Distance,
            camera.AzimuthRadians, camera.PitchRadians, camera.FieldOfViewYRadians);

        // Look-at point: where the screen centre ray meets the terrain (Krok 1), raycast against the coarse
        // base raster. Fallback chain: a fresh hit → the last valid look-at → the camera target's ground
        // point. Holding the last valid look-at on a transient miss (sky / off-DEM) keeps the detail patch
        // from teleporting to the camera target and back — no micro-jumps when the gaze grazes the horizon.
        float verticalExaggeration = (float)Math.Clamp(VerticalExaggeration, 1.0, 5.0);
        GeoPoint targetGeo = LocalTangentProjection.WorldToGeo(camera.Target, lodAnchor);
        System.Numerics.Vector3? freshLookAt = null;
        if (TerrainRaster is { } baseRaster)
        {
            // Centre ray first (aspect-independent); on a sky miss, probe lower in the frame so a horizontal
            // "looking across the ridge" view still streams detail to the terrain below the gaze.
            freshLookAt = LookAtPoint.Resolve(
                camera, 1f, 1f, baseRaster, lodAnchor, verticalExaggeration, LookAtLowerFrameFallbacks);
        }

        if (freshLookAt is { } hitNow)
        {
            lastValidLookAtWorld = hitNow;
        }

        System.Numerics.Vector3? effectiveLookAt = freshLookAt ?? lastValidLookAtWorld;
        GeoPoint focus = effectiveLookAt is { } w ? LocalTangentProjection.WorldToGeo(w, lodAnchor) : targetGeo;
        string focusSource = freshLookAt is not null ? "look-at" : effectiveLookAt is not null ? "last-valid" : "target";

        // Wider-coverage P0 step 1 — NO-OP DIAGNOSTIC. Measure how far the camera's look-at has drifted from the
        // scene origin (lodAnchor) and what the re-anchor policy would decide. The threshold is enormous so the
        // decision is ALWAYS "don't re-anchor": the result is read ONLY for this log line — no origin moves, no
        // ExistingShift is applied, no geometry changes. This just proves the measurement on device (step 2 acts).
        WorldOriginDecision originProbe = WorldOriginPolicy.Evaluate(lodAnchor, focus, OriginReanchorThresholdMeters);
        double originDriftMeters = LocalTangentProjection.GeoToWorld(focus, 0f, lodAnchor, 1f).Length();
        logger.LogInformation(
            "LOD origin-probe (NO-OP): origin {OLat:F4},{OLon:F4} → focus {FLat:F4},{FLon:F4}; drift {Drift:F0} m; wouldReanchor={Re}",
            lodAnchor.Latitude, lodAnchor.Longitude, focus.Latitude, focus.Longitude, originDriftMeters, originProbe.ShouldReanchor);

        // Adaptive detail zoom (Krok 2/3): screen-space error from the camera→look-at distance picks the zoom.
        // Computed BEFORE the reload gates so the on-screen LOD diagnostic tracks the CURRENT tier even on a pure
        // zoom-in/out — that changes the distance (and tier) but NOT the look-at, so the detail patch isn't
        // rebuilt and the badge used to freeze on the last reload's tier ("z14 nie zmienia się przy przybliżaniu").
        double cameraToLookAt = effectiveLookAt is { } w2
            ? System.Numerics.Vector3.Distance(camera.Position, w2)
            : camera.Distance;
        double viewportHeight = 1000.0;
        // Mobile: use the real 3D backbuffer height. TryGetMapFocus reads the 2D Mapsui viewport, which is never
        // laid out when Is3DMode=true, so it returns false → vh stays 1000 → screen-space error under-estimated
        // (~2-3×) → the per-tile planner asks for coarser detail than the screen needs. Desktop is left on its
        // existing path (unchanged) so its SSE/visual does not move.
        if (!OperatingSystem.IsWindows() && viewportHeightPixels > 0)
        {
            viewportHeight = viewportHeightPixels;
        }
        else if (TryGetMapFocus(out _, out _, out double vh) && vh > 0)
        {
            viewportHeight = vh;
        }

        int detailZoom = ScreenSpaceLod.ZoomForCameraDistance(
            DetailZoomCandidates, cameraToLookAt, focus.Latitude, camera.FieldOfViewYRadians, viewportHeight, DetailMaxErrorPixels);

        logger.LogInformation(
            "LOD focus [{Source}]: target {TLat:F4},{TLon:F4} → {FLat:F4},{FLon:F4}; cam→look-at {Dist:F0} m → detail z{Zoom}",
            focusSource, targetGeo.Latitude, targetGeo.Longitude, focus.Latitude, focus.Longitude, cameraToLookAt, detailZoom);

        // Refresh the diagnostic badge on EVERY camera move with the current tier/distance, reusing the last
        // build's cache/step figures (those change only on an actual reload below). Without this the badge stayed
        // frozen until the look-at drifted past the reload gate — so a pure zoom never updated the z## tier.
        if (ShowLodDiagnostics)
        {
            string liveBadge = LodDetailDiagnostics.Format(
                focusSource, cameraToLookAt, viewportHeight, detailZoom, BaseDetailZoomFloor,
                lastPerTileRequested, lastPerTileCached, lastPerTileAvgStep, lastPerTileFinestStep, lastPerTileNote,
                building: lodDetailLoading); // a rebuild is mid-flight ⇒ show ⌛bld (base/old detail is what's drawn now)
            MainThread.BeginInvokeOnMainThread(() => LodBadgeText = liveBadge);
        }

        // A detail build is already running (15–26 s) — the badge above is up to date, but don't queue another
        // rebuild on top of it.
        if (lodDetailLoading)
        {
            return;
        }

        if (!LodTerrainWindow.ShouldReload(lodDetailCentre, focus, LodDetailReloadThresholdMeters))
        {
            return;
        }

        // The ~35 s reload cooldown ALWAYS applies — including during the route film. Re-centring re-meshes every
        // tile whose LOD step flips near the new window boundary (churn + Gen2 GC); doing that on every small drift
        // during the film produced many small lag spikes. The 3.5 km window keeps the gaze covered for ~50 s of
        // flight, so the rare large-jump re-centre on the cooldown cadence is enough. The drift threshold above
        // still applies, so a stationary gaze still loads exactly one patch.
        if (DateTime.UtcNow - lastLodDetailReloadUtc < LodDetailReloadCooldown)
        {
            return;
        }

        // Wider-coverage P1 streaming PROBE (NO-OP): plan the base tiles this view would stream + the residency
        // load/keep/evict decision and LOG them. No fetch, no swap — just validates the streaming planner on real
        // device bounds before tile loading is wired.
        double streamMpp = viewportHeight > 0
            ? (2.0 * cameraToLookAt * Math.Tan(camera.FieldOfViewYRadians * 0.5)) / viewportHeight
            : 30.0;
        double streamHalfWidth = Math.Clamp(cameraToLookAt, 1000.0, 30000.0);
        StreamingTilePlan streamPlan = StreamingTilePlanner.Plan(
            LodTerrainWindow.Around(focus, streamHalfWidth), streamMpp, focus.Latitude,
            BaseStreamingMaxTiles, BaseStreamingMinZoom, BaseStreamingMaxZoom);
        DemTileResidencyPlan streamResidency = baseStreamingProbePlanner.Plan(streamPlan.Tiles);
        logger.LogInformation(
            "LOD stream-probe (NO-OP): z{Zoom} desired={Desired} load={Load} evict={Evict} resident={Res} (mpp={Mpp:F1}, half={Half:F0}m)",
            streamPlan.Zoom, streamPlan.Tiles.Count, streamResidency.ToLoad.Count, streamResidency.ToEvict.Count,
            baseStreamingProbePlanner.Resident.Count, streamMpp, streamHalfWidth);

        lodDetailLoading = true;
        lastLodDetailReloadUtc = DateTime.UtcNow;

        // Flag the badge as building NOW for this focus (the ~15 s rebuild starts below; base/old detail is on
        // screen until it finishes) — so the readout reflects the CURRENT surface, not the not-yet-drawn target.
        if (ShowLodDiagnostics)
        {
            string buildingBadge = LodDetailDiagnostics.Format(
                focusSource, cameraToLookAt, viewportHeight, detailZoom, BaseDetailZoomFloor,
                lastPerTileRequested, lastPerTileCached, null, null, null, building: true);
            MainThread.BeginInvokeOnMainThread(() => LodBadgeText = buildingBadge);
        }
        try
        {
            // ONE stitched detail patch at the look-at's adaptive zoom — a single crack-free surface.
            // (Overlapping multi-res layers caused vertical curtains; proper multi-res stitching is 4c.)
            // At/below the base zoom the patch adds nothing — keep the base alone.
            IReadOnlyList<TerrainMesh3D>? detailTiles;
            int? diagRequested = null;
            int? diagCached = null;
            double? diagAvgStep = null;
            int? diagFinestStep = null;
            string? diagNote = null;
            if (detailZoom <= BaseDetailZoomFloor)
            {
                detailTiles = null; // at/below the base zoom the patch adds nothing — keep the base alone.
            }
            else if (UsePerTileDetail)
            {
                // Model 1: per-tile roughness LOD over the look-at window (1 m on sharp, coarser on smooth).
                detailTiles = await BuildPerTileDetailAsync(
                    focus, lodAnchor, camera.Position, camera.FieldOfViewYRadians, viewportHeight).ConfigureAwait(true);
                diagRequested = lastPerTileRequested;
                diagCached = lastPerTileCached;
                diagNote = lastPerTileNote; // "ok" | "no-raster" | "no-terrain" — why detail did/didn't render
                // Only meaningful if a real mesh came back; if detailTiles is null we are on base, leave step unset.
                if (detailTiles is not null)
                {
                    diagAvgStep = lastPerTileAvgStep;
                    diagFinestStep = lastPerTileFinestStep;
                }
            }
            else
            {
                // Fallback: the proven single stitched patch at the look-at's adaptive zoom.
                detailTiles = await BuildDetailTilesAsync(focus, lodAnchor, detailZoom).ConfigureAwait(true);
            }
            // On-screen LOD ground truth — logcat/Serilog comes back empty on the phone, so surface the detail
            // decision on the always-visible LOD badge (the status pill auto-hides): was the 1 m patch nulled by the
            // zoom floor (the "plasticine" cause), and how much z16 is actually cached. Gated behind the
            // "LOD diagnostics" debug toggle (Ustawienia → DEBUG); off = the quiet "LOD 1 m" label, both platforms.
            if (ShowLodDiagnostics)
            {
                // OnDetailFocusAsync is raised from the renderer's camera-moved event (a GL/render thread, NOT the
                // UI thread). LodBadgeText is bound to a Label, and touching a view off the UI thread crashes Android
                // ("Only the original thread that created a view hierarchy can touch its views"). Marshal the set —
                // same pattern the VM uses for other view-bound updates (e.g. UserLocation).
                string badge = LodDetailDiagnostics.Format(
                    focusSource, cameraToLookAt, viewportHeight, detailZoom, BaseDetailZoomFloor,
                    diagRequested, diagCached, diagAvgStep, diagFinestStep, diagNote);
                MainThread.BeginInvokeOnMainThread(() => LodBadgeText = badge);
            }
            if (detailTiles is null)
            {
                // Pre-cache: a patch with no detail (off coverage) just contributes nothing — keep what we have.
                if (routeFilmAccumulate)
                {
                    lodDetailCentre = focus;
                    return;
                }

                // Off coverage (rule #12): show the base ALONE — drop the stale detail patch rather than
                // leaving it hanging where the camera no longer is. Clear the detail field too so trails /
                // roads / route fall back to the base everywhere instead of seating on the gone window.
                DetailElevation = null;
                TerrainTiles = new List<TerrainMesh3D>(lodBaseTiles);
                OnPropertyChanged(nameof(TerrainFrame));
                lodDetailCentre = focus;
                return;
            }

            // Route-film pre-cache: ACCUMULATE patches (base + every patch built along the route) so the whole
            // corridor stays at 1 m as the camera flies; cap retained patches for memory. Normal use replaces.
            if (routeFilmAccumulate)
            {
                retainedFilmDetail.AddRange(detailTiles);
                while (retainedFilmDetail.Count > MaxRouteFilmPatches)
                {
                    retainedFilmDetail.RemoveAt(0); // evict oldest
                }

                var film = new List<TerrainMesh3D>(lodBaseTiles);
                film.AddRange(retainedFilmDetail);
                TerrainTiles = film;
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

    /// <summary>Clears the route chain — all stops, the drawn line and the planned result.</summary>
    [RelayCommand]
    public void ClearRoute()
    {
        RouteStops.Clear();
        LastPlannedRoute = null;
        Route3DOverlay = null;
        HasPlannedRoute = false;
        RouteSummary = string.Empty;
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

            await exportRouteToGpxUseCase.HandleAsync(LastPlannedRoute, destinationPath, fileName, RouteStops.ToList()).ConfigureAwait(true);
            StatusMessage = Fmt(Localization.AppStrings.StatusGpxExportedFormat, destinationPath);
            logger.LogInformation("Exported route to {Path}", destinationPath);
        }
        catch (IOException ex)
        {
            StatusMessage = Fmt(Localization.AppStrings.StatusGpxExportFailedFormat, ex.Message);
            logger.LogError(ex, "GPX export failed");
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

                    // The bundled ortho was generated for THIS DEM's extent, so its coverage = the DEM bounds.
                    // Capture it now (before any LOD demo overwrites TerrainRaster) to geo-reference ortho UV
                    // when draping the bundled ortho on the streaming-LOD sub-region tiles.
                    if (OrthoTexturePaths is { Count: > 0 } || orthoGridCols * orthoGridRows > 1)
                    {
                        bundledOrthoCoverageBounds = demRaster.Bounds;
                    }
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

            // Default-ON trails: if nothing was bundled, seat them offline-first from the SQLite cache (or
            // fetch when online + uncached) so the map shows trails without a manual toggle.
            await LoadOrFetchTrailsOnStartupAsync().ConfigureAwait(true);

            // Restore the user's saved tourist route (stops persist across restarts). Best-effort: the stop
            // markers come back now; the route LINE draws once trails are available (bundled above, or after a
            // "Szlaki" download — ApplyTrailsAsync re-plans then).
            await RestoreRouteStopsAsync().ConfigureAwait(true);

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
                StatusMessage = Fmt(Localization.AppStrings.StatusAutoLoadedFormat, string.Join(", ", loaded));
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

            // The LOD pipeline IS the main experience now: ring-LOD native base + FillPits + 1 m detail
            // streaming (GUGiK on the PL side, DMR 5.0 on the SK side) + lakes. Upgrade the scene IN PLACE
            // right after the fast legacy mesh is up, WITHOUT reframing — the per-DEM restored camera must
            // survive a normal launch. On ANY failure the legacy scene simply stays (the pre-unification
            // behaviour), so this is strictly additive.
            if (AutoStartLodPipeline && discovery.DemPath is not null && TerrainTiles is not null)
            {
                await BuildLodSceneAsync(reframeCamera: false).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-load failed");
            // Auto-load is best-effort; manual pickers remain available.
        }
        finally
        {
            // The startup overlay must NEVER outlive auto-load: when no DEM/scene was found the LOD build
            // (whose finally normally clears this) never runs — without this line the user would be trapped
            // behind a blocking spinner telling them to load data they cannot reach.
            IsInitialLoading = false;
        }
    }

    // Kill-switch for the unified startup: false = the legacy uniform 2× base stays the launch scene
    // and the full LOD pipeline never auto-starts (it remains reachable only programmatically).
    private static readonly bool AutoStartLodPipeline = true;

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
        // Coverage = Małopolska (unioned with any larger loaded basemap), matching the render/trail clip.
        MapBounds coverage = MalopolskaRegion;
        if (basemapBounds is { } basemap)
        {
            coverage = coverage.Union(basemap);
        }

        var viewport = ViewportBounds.FromMercatorExtent(GetCurrentExtent());
        if (viewport is { } v)
        {
            return v.Intersect(coverage);
        }

        // 3D mode: the 2D Mapsui viewport is never sized (the map control is hidden). Falling back to the
        // WHOLE DEM rectangle (65×33 km) flooded the map with thousands of POIs + a dense foothill road
        // net. Use the TATRA CORE instead — the high massif + its trailhead parkings on both sides,
        // without the Podhale foothills / towns.
        if (TerrainRaster is not null)
        {
            return TatraCoreRegion.Intersect(coverage);
        }

        return null;
    }

    // The high Tatra massif + its trailhead parkings (Brzeziny, Kuźnice, Kiry, Palenica, Łysa Polana,
    // Štrbské, Tatranská Lomnica) on both sides of the border — the meaningful "around the Tatras" area
    // for a hiker, minus the foothill towns that turn a viewport download into thousands of features.
    private static readonly MapBounds TatraCoreRegion = new(
        new GeoPoint(49.08, 19.78), new GeoPoint(49.32, 20.35));

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