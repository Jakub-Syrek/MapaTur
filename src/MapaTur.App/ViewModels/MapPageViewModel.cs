using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mapsui;
using Mapsui.Projections;
using MapaTur.App.Services;
using MapaTur.Application.Routing;
using MapaTur.Application.Tracks;
using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;
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
    private readonly ITrackLayerRenderer trackRenderer;
    private readonly ITrailLayerRenderer trailRenderer;
    private readonly IRouteLayerRenderer routeRenderer;
    private readonly ImportTcxFileUseCase importTcxFileUseCase;
    private readonly IOverpassClient overpassClient;
    private readonly ITrailRepository trailRepository;
    private readonly PlanRouteUseCase planRouteUseCase;
    private readonly ExportRouteToGpxUseCase exportRouteToGpxUseCase;
    private readonly ILogger<MapPageViewModel> logger;

    private readonly List<GeoPoint> waypoints = new(capacity: 2);

    [ObservableProperty]
    private string statusMessage = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    /// <summary>
    /// Initializes a new instance of the view model.
    /// </summary>
    /// <param name="filePicker">File picker service used to obtain MBTiles/TCX paths.</param>
    /// <param name="fileSaver">File saver service for export destinations.</param>
    /// <param name="mapLoader">Tile archive loader.</param>
    /// <param name="trackRenderer">Track polyline renderer.</param>
    /// <param name="trailRenderer">Trail polyline renderer.</param>
    /// <param name="routeRenderer">Planned-route polyline renderer.</param>
    /// <param name="importTcxFileUseCase">TCX import use case.</param>
    /// <param name="overpassClient">Overpass HTTP client.</param>
    /// <param name="trailRepository">Trail persistence repository.</param>
    /// <param name="planRouteUseCase">Route planning use case.</param>
    /// <param name="exportRouteToGpxUseCase">GPX export use case.</param>
    /// <param name="logger">Logger.</param>
    public MapPageViewModel(
        IFilePickerService filePicker,
        IFileSaverService fileSaver,
        IOfflineMapLoader mapLoader,
        ITrackLayerRenderer trackRenderer,
        ITrailLayerRenderer trailRenderer,
        IRouteLayerRenderer routeRenderer,
        ImportTcxFileUseCase importTcxFileUseCase,
        IOverpassClient overpassClient,
        ITrailRepository trailRepository,
        PlanRouteUseCase planRouteUseCase,
        ExportRouteToGpxUseCase exportRouteToGpxUseCase,
        ILogger<MapPageViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(filePicker);
        ArgumentNullException.ThrowIfNull(fileSaver);
        ArgumentNullException.ThrowIfNull(mapLoader);
        ArgumentNullException.ThrowIfNull(trackRenderer);
        ArgumentNullException.ThrowIfNull(trailRenderer);
        ArgumentNullException.ThrowIfNull(routeRenderer);
        ArgumentNullException.ThrowIfNull(importTcxFileUseCase);
        ArgumentNullException.ThrowIfNull(overpassClient);
        ArgumentNullException.ThrowIfNull(trailRepository);
        ArgumentNullException.ThrowIfNull(planRouteUseCase);
        ArgumentNullException.ThrowIfNull(exportRouteToGpxUseCase);
        ArgumentNullException.ThrowIfNull(logger);

        this.filePicker = filePicker;
        this.fileSaver = fileSaver;
        this.mapLoader = mapLoader;
        this.trackRenderer = trackRenderer;
        this.trailRenderer = trailRenderer;
        this.routeRenderer = routeRenderer;
        this.importTcxFileUseCase = importTcxFileUseCase;
        this.overpassClient = overpassClient;
        this.trailRepository = trailRepository;
        this.planRouteUseCase = planRouteUseCase;
        this.exportRouteToGpxUseCase = exportRouteToGpxUseCase;
        this.logger = logger;
        Map = new Map();
        StatusMessage = "No data loaded. Open MBTiles, import a TCX track, or download trails for this area.";
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
            string? path = await filePicker.PickFileAsync("Select MBTiles archive");
            if (path is null)
            {
                return;
            }

            mapLoader.LoadMBTilesArchive(Map, path);
            StatusMessage = $"Loaded: {Path.GetFileName(path)}";
            logger.LogInformation("Loaded MBTiles archive {Path}", path);
        }
        catch (FileNotFoundException ex)
        {
            StatusMessage = "Selected file does not exist.";
            logger.LogWarning(ex, "MBTiles file not found");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            StatusMessage = $"Could not load archive: {ex.Message}";
            logger.LogError(ex, "Failed to open MBTiles archive");
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
            string? path = await filePicker.PickFileAsync("Select TCX track");
            if (path is null)
            {
                return;
            }

            var tracks = await importTcxFileUseCase.HandleAsync(path);
            if (tracks.Count == 0)
            {
                StatusMessage = "TCX file contained no tracks with positions.";
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
            StatusMessage = "Selected file does not exist.";
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

        var bounds = ViewportBounds.FromMercatorExtent(GetCurrentExtent());
        if (bounds is null)
        {
            StatusMessage = "Map viewport is not ready yet. Try again after the map has rendered.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Downloading trails from OSM Overpass…";

            var trails = await overpassClient.FetchHikingTrailsAsync(bounds.Value).ConfigureAwait(true);
            await trailRepository.UpsertAsync(trails).ConfigureAwait(true);
            trailRenderer.RenderTrails(Map, trails);

            StatusMessage = trails.Count == 0
                ? "No hiking trails found in this area."
                : $"Loaded {trails.Count} hiking trail(s) in the current viewport.";
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
            StatusMessage = "Overpass request timed out. Try a smaller area.";
            logger.LogWarning(ex, "Overpass request timed out");
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
            routeRenderer.Clear(Map);
        }

        waypoints.Add(point);
        routeRenderer.RenderWaypoints(Map, waypoints);

        if (waypoints.Count == 1)
        {
            StatusMessage = "Origin set. Tap the map again to set the destination and compute a route.";
            return;
        }

        await PlanRouteForWaypointsAsync().ConfigureAwait(true);
    }

    /// <summary>Clears any planned route and waypoints.</summary>
    [RelayCommand]
    public void ClearRoute()
    {
        waypoints.Clear();
        LastPlannedRoute = null;
        routeRenderer.Clear(Map);
        StatusMessage = "Route cleared.";
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
            StatusMessage = "Plan a route first by tapping two points on the map.";
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
            StatusMessage = "Planning route…";

            var request = new RouteRequest(waypoints[0], waypoints[1], RouteProfile.FastestTime);
            var route = await planRouteUseCase.HandleAsync(request).ConfigureAwait(true);

            if (route is null)
            {
                StatusMessage = "No route found. Make sure trails are downloaded for both endpoints (use 'Download Trails').";
                return;
            }

            LastPlannedRoute = route;
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
