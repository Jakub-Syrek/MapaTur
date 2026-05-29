using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using MapaTur.App.Services;
using MapaTur.Application.Climbing;
using MapaTur.Application.Maps;
using MapaTur.Application.Routing;
using MapaTur.Application.Terrain;
using MapaTur.Application.Tracks;
using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Terrain;
using MapaTur.Domain.Trails;
using MapaTur.Infrastructure.Terrain;

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
    private readonly I3DSettingsStore settingsStore;
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
    private readonly PlanRouteUseCase planRouteUseCase;
    private readonly ExportRouteToGpxUseCase exportRouteToGpxUseCase;
    private readonly ILogger<MapPageViewModel> logger;

    private readonly List<GeoPoint> waypoints = new(capacity: 2);

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool is3DMode;

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

    private readonly MeshRebuildCoalescer meshRebuildCoalescer = new();

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
        _ = Task.Run(() =>
        {
            var options = new MapaTur.Application.Terrain.TerrainMeshOptions
            {
                VerticalExaggeration = (float)Math.Clamp(value, 1.0, 5.0),
            };
            var rebuilt = TerrainMesh3D.BuildTiles(raster, options);
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
        if (rawTrails is null)
        {
            return;
        }
        var filter = BuildTrailFilter();
        var filtered = rawTrails.Where(filter.IsVisible).ToList();
        Trails3DOverlay = filtered;
        trailRenderer.RenderTrails(Map, filtered);
        viewportTrailController?.RequestRefresh();
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
    /// <param name="planRouteUseCase">Route planning use case.</param>
    /// <param name="exportRouteToGpxUseCase">GPX export use case.</param>
    /// <param name="logger">Logger.</param>
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
        PlanRouteUseCase planRouteUseCase,
        ExportRouteToGpxUseCase exportRouteToGpxUseCase,
        ILogger<MapPageViewModel> logger)
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
        ArgumentNullException.ThrowIfNull(planRouteUseCase);
        ArgumentNullException.ThrowIfNull(exportRouteToGpxUseCase);
        ArgumentNullException.ThrowIfNull(logger);

        this.filePicker = filePicker;
        this.fileSaver = fileSaver;
        this.mapLoader = mapLoader;
        this.autoLoader = autoLoader;
        this.tileSourceFactory = tileSourceFactory;
        this.settingsStore = settingsStore;

        // Restore the saved vertical exaggeration before the partial OnXxxChanged hook
        // can fire on the default value. Clamp to [1, 5] to defend against tampered
        // preference values.
        if (settingsStore.VerticalExaggeration is { } saved)
        {
            verticalExaggeration = Math.Clamp(saved, 1.0, 5.0);
        }
        this.trackRenderer = trackRenderer;
        this.trailRenderer = trailRenderer;
        this.routeRenderer = routeRenderer;
        this.climbingRenderer = climbingRenderer;
        this.importTcxFileUseCase = importTcxFileUseCase;
        this.overpassClient = overpassClient;
        this.trailRepository = trailRepository;
        this.climbingOverpassClient = climbingOverpassClient;
        this.climbingRepository = climbingRepository;
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
            await trailRepository.UpsertAsync(trails).ConfigureAwait(true);
            rawTrails = trails;
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
            Trails3DOverlay = filteredTrails;

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

    private async Task LoadDemFromPathAsync(string path)
    {
        var raster = await Task.Run(() => DemRasterReader.Read(path)).ConfigureAwait(true);
        TerrainRaster = raster;
        var initialOptions = new MapaTur.Application.Terrain.TerrainMeshOptions
        {
            VerticalExaggeration = (float)Math.Clamp(VerticalExaggeration, 1.0, 5.0),
        };
        TerrainTiles = await Task.Run(() => TerrainMesh3D.BuildTiles(raster, initialOptions)).ConfigureAwait(true);
        OnPropertyChanged(nameof(TerrainFrame));
        // Detect summits off the UI thread so the 3D view shows labelled peaks, not just terrain.
        // Match each against the curated Tatra gazetteer so prominent peaks get a name above the
        // elevation; unmatched maxima keep their elevation-only label.
        Peaks3DOverlay = await Task.Run(() =>
            PeakNamer.AssignNames(PeakDetector.Detect(raster), TatraSummits.All)).ConfigureAwait(true);
        logger.LogInformation("Loaded DEM {Path} ({Cols}x{Rows})", path, raster.Columns, raster.Rows);
        StatusMessage = $"{Localization.AppStrings.StatusDemLoaded}: {Path.GetFileName(path)}";
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
            Filter = trail => BuildTrailFilter().IsVisible(trail),
        };
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
            var discovery = autoLoader.Discover();
            logger.LogInformation(
                "Auto-load discovery: basemaps=[{Basemaps}], hillshade={Hillshade}, dem={Dem}",
                string.Join(", ", discovery.BasemapMBTilesPaths),
                discovery.HillshadeMBTilesPath ?? "(none)",
                discovery.DemPath ?? "(none)");
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
            }
            else if (discovery.HillshadeMBTilesPath is { } hillshadePath)
            {
                // Hillshade is a fallback: only auto-load it when no basemap was found. Its extent is
                // never used to clip downloads, so the returned bounds are intentionally discarded.
                mapLoader.LoadMBTilesArchive(Map, hillshadePath, MBTilesLayerKind.Hillshade);
                loaded.Add(Path.GetFileName(hillshadePath));
                logger.LogInformation("Auto-loaded hillshade (basemap fallback) {Path}", hillshadePath);
            }

            if (discovery.DemPath is { } demPath)
            {
                await LoadDemFromPathAsync(demPath).ConfigureAwait(true);
                loaded.Add(Path.GetFileName(demPath));
                logger.LogInformation("Auto-loaded DEM {Path}", demPath);
            }

            if (loaded.Count > 0)
            {
                StatusMessage = $"Auto-loaded: {string.Join(", ", loaded)}";
            }
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
    /// Returns the bbox to use for an Overpass download: the visible viewport
    /// intersected with any loaded basemap and DEM bounds. Returns null if the
    /// viewport isn't ready or the intersection is empty (the user is looking
    /// at an area entirely outside the loaded map data).
    /// </summary>
    private MapBounds? ComputeDownloadBounds()
    {
        var viewport = ViewportBounds.FromMercatorExtent(GetCurrentExtent());
        if (viewport is null)
        {
            return null;
        }

        MapBounds? clipped = viewport;
        if (basemapBounds is { } basemap)
        {
            clipped = clipped?.Intersect(basemap);
        }
        if (TerrainRaster?.Bounds is { } demBounds)
        {
            clipped = clipped?.Intersect(demBounds);
        }
        return clipped;
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