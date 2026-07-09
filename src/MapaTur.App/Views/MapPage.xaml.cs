using System.ComponentModel;
using System.Globalization;
using System.Numerics;

using MapaTur.App.Localization;
using MapaTur.App.Services;
using MapaTur.App.ViewModels;
using MapaTur.Application.Markers;
using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

using Mapsui;
using Mapsui.Projections;

using Microsoft.Extensions.Logging;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Networking;

namespace MapaTur.App.Views;

/// <summary>
/// Primary screen of the application showing the offline map and a toolbar
/// for loading MBTiles archives, importing tracks, and downloading trails.
/// </summary>
public partial class MapPage : ContentPage
{
    private readonly MapPageViewModel viewModel;
    private readonly ILogger<ViewportAwareTrailLayerController> trailControllerLogger;
    private bool initialCenterApplied;

    /// <summary>
    /// Initializes the page with its view model.
    /// </summary>
    /// <param name="viewModel">View model injected by the DI container.</param>
    /// <param name="trailControllerLogger">Logger for the viewport-aware trail controller.</param>
    public MapPage(MapPageViewModel viewModel, ILogger<ViewportAwareTrailLayerController> trailControllerLogger)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(trailControllerLogger);

        InitializeComponent();
        this.viewModel = viewModel;
        this.trailControllerLogger = trailControllerLogger;
        BindingContext = viewModel;
        MapControl.Map.Tapped += OnMapTapped;
        TerrainView.MarkerTapped += OnMarkerTapped;
        TerrainView.TerrainTapped += OnTerrainTapped;
        viewModel.RouteFocusRequested += OnRouteFocusRequested;
        TerrainView.RecordingSaved += OnRecordingSaved;
        TerrainView.FlightEnded += (_, _) => viewModel.EndRouteFilm();
        TerrainView.CameraFocusMoved += OnCameraFocusMoved;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.TerrainReframeRequested += OnTerrainReframeRequested;
        viewModel.TeleportRequested += OnTeleportRequested;
        viewModel.FollowRequested += OnFollowRequested;
        viewModel.RouteFilmRequested += OnRouteFilmRequested;
    }

    // Keeps the 3D camera and the 2D map framed on the same place + zoom as the user
    // toggles between them ("przechodzenie pomiędzy 3d a 2d ... po wybraniu kąta i powiększenia").
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapPageViewModel.ActiveSection))
        {
            AnimateActiveSection(viewModel.ActiveSection);
            return;
        }

        if (e.PropertyName == nameof(MapPageViewModel.IsLodStreaming))
        {
            // LOD Etap 3: let the 3D view report the camera focus only while streaming, so the 1 m detail
            // ring follows it. The base stays static, so this never moves the camera.
            TerrainView.DetailStreamingEnabled = viewModel.IsLodStreaming;
            return;
        }

        if (e.PropertyName != nameof(MapPageViewModel.Is3DMode))
        {
            return;
        }

        if (viewModel.Is3DMode)
        {
            // The 2D map covers the whole voivodeship while the 3D DEM is just one region, so syncing the
            // 3D camera FROM the 2D centre kept parking it off the terrain (grey, mis-framed). Decoupled:
            // entering 3D keeps the 3D view's own framing (FrameMesh default / the last 3D position). Just
            // grab keyboard focus so the arrow keys work immediately.
            Dispatcher.Dispatch(TerrainView.FocusForKeyboard);
        }
        // 3D → 2D no longer drags the flat map either; the two views are independent.
    }

    // A pointer-only overlay button (e.g. the 📷 screenshot toggle): drop it from the keyboard tab order so the
    // Space/Enter key can't "click" it. Without this, pressing Space to flap the dragon ALSO invoked the focused
    // button (hiding the panels) — the window-root key handler flaps AND the focused button fires. Pointer/tap
    // still works.
    private void OnPointerOnlyButtonHandlerChanged(object? sender, EventArgs e)
    {
#if WINDOWS
        if (sender is Button { Handler.PlatformView: Microsoft.UI.Xaml.Controls.Control control })
        {
            control.IsTabStop = false;                  // out of the keyboard tab order
            control.AllowFocusOnInteraction = false;    // a tap invokes the Command but does NOT steal focus
            control.UseSystemFocusVisuals = false;
            Serilog.Log.Information("[UI] pointer-only button focus disabled ({Type})", control.GetType().Name);
        }
#endif
    }

    // 2D → 3D: point the camera at whatever the flat map is centred on, matching its zoom AND its
    // rotation (bearing) so the heading carries over. Pitch is left untouched.
    private void SyncCameraToMap()
    {
        if (viewModel.TerrainFrame is not { } mesh)
        {
            return;
        }

        if (!viewModel.TryGetMapFocus(out GeoPoint center, out double resolution, out double viewportHeight))
        {
            return;
        }

        Vector3 world = mesh.GeoToWorld(center, 0f);
        float extent = mesh.HorizontalExtent;
        var target = new Vector3(
            Math.Clamp(world.X, -extent, extent),
            Math.Clamp(world.Y, -extent, extent),
            TerrainView.Camera.Target.Z);

        // Carry the 2D map's bearing into the orbit azimuth so the same direction faces "up" in 3D.
        double bearing = BearingFromMapRotation(MapControl.Map.Navigator.Viewport.Rotation);
        TerrainView.Camera.AzimuthRadians = (float)CameraFocusSync.AzimuthRadiansFromBearing(bearing);

        double distance = CameraFocusSync.ResolutionToDistance(
            resolution, TerrainView.Camera.FieldOfViewYRadians, viewportHeight, center.Latitude);
        TerrainView.FocusOnWorld(target, (float)distance);
    }

    // 3D → 2D: centre the flat map on the camera's focal point, matching its zoom. This is the
    // core fix for "patrzę na górę w 3d, daję 2d i mapa jest gdzieś obok".
    private void SyncMapToCamera()
    {
        if (viewModel.TerrainFrame is not { } mesh)
        {
            return;
        }

        if (!viewModel.TryGetMapFocus(out _, out _, out double viewportHeight))
        {
            return;
        }

        Camera3D camera = TerrainView.Camera;
        GeoPoint focus = mesh.WorldToGeo(camera.Target);
        double resolution = CameraFocusSync.DistanceToResolution(
            camera.Distance, camera.FieldOfViewYRadians, viewportHeight, focus.Latitude);
        viewModel.CenterMapOn(focus, resolution);

        // Carry the 3D heading into the 2D map rotation so switching back keeps the same bearing.
        double bearing = CameraFocusSync.BearingRadiansFromAzimuth(camera.AzimuthRadians);
        MapControl.Map.Navigator.RotateTo(MapRotationFromBearing(bearing));
    }

    // Mapsui Viewport.Rotation (degrees) ↔ compass bearing (radians, east-of-north). The sign is set so
    // the heading shown "up" matches between the rotated 2D map and the 3D camera; verified on device.
    private static double BearingFromMapRotation(double mapRotationDegrees)
        => -mapRotationDegrees * Math.PI / 180.0;

    private static double MapRotationFromBearing(double bearingRadians)
        => -bearingRadians * 180.0 / Math.PI;

    private async void OnRecordingSaved(object? sender, string path)
    {
        await DisplayAlertAsync(
            AppStrings.RecordingSavedTitle,
            string.Format(CultureInfo.CurrentCulture, AppStrings.RecordingSavedFormat, path),
            AppStrings.RecordingDismiss).ConfigureAwait(true);
    }

    private async void OnMapTapped(object? sender, MapEventArgs eventArgs)
    {
        // A tap landing on a POI / climbing marker shows its details popup and is consumed there, so it
        // doesn't also drop a route waypoint.
        if (ResolveTappedMarker(eventArgs) is { } popup)
        {
            eventArgs.Handled = true;
            viewModel.ShowMarkerPopup(popup);
            return;
        }

        var worldPosition = eventArgs.WorldPosition;
        var (longitude, latitude) = SphericalMercator.ToLonLat(worldPosition.X, worldPosition.Y);

        try
        {
            await viewModel.HandleMapTapAsync(new GeoPoint(latitude, longitude)).ConfigureAwait(true);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Tap fell outside the valid Mercator latitude band; ignore.
        }
        catch (Exception ex)
        {
            // async-void handler: an escaping exception is a process-killing stowed exception (see OnTerrainTapped).
            Serilog.Log.Error(ex, "Map tap (route planning) failed at {Lat},{Lon}", latitude, longitude);
        }
    }

    // Resolves a 2D map tap to popup content when it hits a POI / climbing marker. The Mapsui feature
    // only carries the id, so we look the full domain object back up from the view model.
    private MarkerPopupContent? ResolveTappedMarker(MapEventArgs eventArgs)
    {
        if (eventArgs.GetMapInfo is null)
        {
            return null;
        }

        var markerLayers = MapControl.Map.Layers
            .Where(layer => layer.Name is string name &&
                (name.StartsWith(MapsuiPoiLayerRenderer.PoiLayerPrefix, StringComparison.Ordinal) ||
                 name.StartsWith(MapsuiClimbingLayerRenderer.ClimbingLayerPrefix, StringComparison.Ordinal)))
            .ToList();
        if (markerLayers.Count == 0)
        {
            return null;
        }

        var info = eventArgs.GetMapInfo(markerLayers);
        if (info?.Feature is not { } feature || info.Layer?.Name is not { } layerName)
        {
            return null;
        }

        if (feature["id"] is not { } idValue)
        {
            return null;
        }

        long id = Convert.ToInt64(idValue, CultureInfo.InvariantCulture);

        if (layerName.StartsWith(MapsuiClimbingLayerRenderer.ClimbingLayerPrefix, StringComparison.Ordinal) &&
            viewModel.TryFindClimbingById(id, out var area))
        {
            return MarkerPopupFormatter.ForClimbing(area, MarkerPopupLabels.Instance);
        }

        if (layerName.StartsWith(MapsuiPoiLayerRenderer.PoiLayerPrefix, StringComparison.Ordinal) &&
            viewModel.TryFindPoiById(id, out var poi))
        {
            return MarkerPopupFormatter.ForPoi(poi, MarkerPopupLabels.Instance);
        }

        return null;
    }

    private void OnMarkerTapped(object? sender, MarkerPopupContent content)
    {
        viewModel.ShowMarkerPopup(content);
    }

    // 3D tap-to-plan: a bare-terrain tap resolved to WGS-84 routes into the SAME waypoint flow as a
    // 2D map tap, so route planning works in the primary (3D) view too.
    //
    // try/catch: this is an async-void UI handler — an exception escaping it is a WinUI stowed exception
    // (0xc000027b) that KILLS the process with nothing in the log (two APPCRASHes while planning a route,
    // 2026-07-03). Route planning must never take the app down: log it and surface the status instead.
    private async void OnTerrainTapped(object? sender, GeoPoint point)
    {
        try
        {
            await viewModel.HandleMapTapAsync(point);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Terrain tap (route planning) failed at {Lat},{Lon}", point.Latitude, point.Longitude);
        }
    }

    // Flies the 3D camera to a route framing: the first stop when planning is turned off, or the whole
    // route (centre + fit distance) when the user taps "Pokaż trasę".
    private void OnRouteFocusRequested(object? sender, MapaTur.Application.Routing.RouteFraming framing)
    {
        Dispatcher.Dispatch(() => TerrainView.FocusOnGeo(framing.Center, (float)framing.DistanceMeters));
    }

    // Premium-menu microinteraction: the frosted section panel slides down + fades in as it opens, and the
    // dim scrim cross-fades. The panel's IsVisible is binding-driven (show/hide); this just polishes the
    // entrance so sections feel like floating glass, not a hard cut. Exit is an instant hide (acceptable).
    private void AnimateActiveSection(int section)
    {
        _ = Scrim.FadeToAsync(section > 0 ? 1 : 0, 160, Easing.CubicOut);

        Border? panel = section switch
        {
            1 => PanelTrasa,
            2 => PanelTryby,
            3 => PanelWidok,
            4 => PanelMapa,
            5 => PanelPogoda,
            6 => PanelUstawienia,
            _ => null,
        };
        if (panel is null)
        {
            return;
        }

        panel.Opacity = 0;
        panel.TranslationY = -14;
        _ = panel.FadeToAsync(1, 200, Easing.CubicOut);
        _ = panel.TranslateToAsync(0, 0, 220, Easing.CubicOut);
    }

    // Premium-menu "Widok" actions. Camera framing + the cinematic fly-through live in the 3D view, so the
    // page forwards these button taps straight to it (the on-screen pad + altitude buttons are unchanged).
    private void OnResetCameraClicked(object? sender, EventArgs e)
    {
        viewModel.ActiveSection = 0; // close the panel so the framed view is unobstructed
        TerrainView.FrameMesh();
    }

    // A freshly loaded region (e.g. GUGiK 1 m) has different bounds than the saved camera, so the VM asks
    // the page to reframe — same action as "Reset kamery". Dispatched so the mesh binding has propagated to
    // the 3D view (FrameMesh reads its WorldFrame) before we frame it.
    private void OnTerrainReframeRequested(object? sender, EventArgs e)
    {
        viewModel.ActiveSection = 0;
        Dispatcher.Dispatch(() => TerrainView.FrameMesh());
    }

    // Name-search "teleport": fly the 3D camera over the picked place. Dispatched so any pending binding
    // (the panel closing) has propagated before the camera jumps.
    private void OnTeleportRequested(object? sender, Domain.Routing.RouteWaypoint place)
    {
        Dispatcher.Dispatch(() => TerrainView.TeleportTo(place));
    }

    // Follow-camera tracking: each GPS fix (while the option is on) chases the user from behind, oriented
    // along their travel direction. Dispatched onto the UI thread like the other camera moves.
    private void OnFollowRequested(object? sender, MapPageViewModel.FollowCameraRequest req)
    {
        Dispatcher.Dispatch(() => TerrainView.FollowTo(req.Position, req.BearingDegrees));
    }

    // "Film z trasy": start the cinematic fly-through along the planned route (records to MP4), pausing 3 s at
    // each stop the user entered.
    private void OnRouteFilmRequested(object? sender, EventArgs e)
    {
        var stops = viewModel.RouteStops.Select(s => s.Location).ToList();
        // Detail streams LIVE during the film (it follows the route) and the flight gates its forward progress on
        // detail readiness — so run the normal flow: keep the start-build gate (wait for the first patch before the
        // first move) and let OnFlightTick hold the camera while a build is in progress.
        Dispatcher.Dispatch(() => TerrainView.StartRouteFlight(stops, detailPrebuilt: false));
    }

    // LOD Krok 4: the 3D view reports a snapshot of the camera pose as it roams the static base; forward it
    // so the VM raycasts the look-at point and streams the 1 m detail patch to the gaze (it gates the reload
    // on drift + cooldown). The base and camera framing don't change, so this never yanks the camera.
    private async void OnCameraFocusMoved(object? sender, Camera3D camera)
    {
        try
        {
            await viewModel.OnDetailFocusAsync(camera, TerrainView.SurfacePixelHeight, TerrainView.SurfacePixelWidth);
        }
        catch (Exception ex)
        {
            // async-void handler: an escaping exception is a process-killing stowed exception (see OnTerrainTapped).
            Serilog.Log.Error(ex, "Detail-focus update failed");
        }
    }

    // The user dragged a route stop to a new position (CollectionView CanReorderItems) — the stops collection
    // is already reordered, so re-plan the chained route to follow the new sequence.
    private async void OnRouteStopsReorderCompleted(object? sender, EventArgs e)
    {
        try
        {
            await viewModel.ReplanAfterReorderAsync();
        }
        catch (Exception ex)
        {
            // async-void handler: an escaping exception is a process-killing stowed exception (see OnTerrainTapped).
            Serilog.Log.Error(ex, "Route re-plan after stop reorder failed");
        }
    }

    // "Download whole Tatras offline": a big one-time pull meant for WiFi (no signal in the field). The
    // connectivity check + warning live here in the view (the VM stays platform-agnostic and testable);
    // off WiFi we warn with the size and let the user proceed on mobile data if they insist.
    private async void OnDownloadTatraOfflineTapped(object? sender, TappedEventArgs e)
    {
        viewModel.ActiveSection = 0; // close the panel so the status line is visible

        long tiles = DemTilePlanner.TileCount(TatraOfflineRegion.Bounds, TatraOfflineRegion.DownloadZoom);
        int megabytes = (int)(TatraOfflineRegion.EstimatedBytes((int)tiles) / (1024 * 1024));

        bool onWifi = Connectivity.Current.ConnectionProfiles.Contains(ConnectionProfile.WiFi);
        if (!onWifi)
        {
            bool proceed = await DisplayAlertAsync(
                AppStrings.DialogNoWifiTitle,
                string.Format(CultureInfo.CurrentUICulture, AppStrings.DialogDownloadTatraBodyFormat, megabytes, tiles),
                AppStrings.DialogDownloadAnyway,
                AppStrings.DialogCancel);
            if (!proceed)
            {
                return;
            }
        }

        await viewModel.DownloadTatraOfflineCommand.ExecuteAsync(null);
    }

    // "Download data packages": pulls the pre-baked DEM/ortho packages from the server. Sizes come from the
    // manifest (hundreds of MB to a few GB for ortho), so we WiFi-gate this just like the offline tile pull.
    private async void OnDownloadDataPackagesTapped(object? sender, TappedEventArgs e)
    {
        viewModel.ActiveSection = 0; // close the panel so the status line is visible

        bool onWifi = Connectivity.Current.ConnectionProfiles.Contains(ConnectionProfile.WiFi);
        if (!onWifi)
        {
            bool proceed = await DisplayAlertAsync(
                AppStrings.DialogNoWifiTitle,
                AppStrings.DialogDownloadPackagesBody,
                AppStrings.DialogDownloadAnyway,
                AppStrings.DialogCancel);
            if (!proceed)
            {
                return;
            }
        }

        await viewModel.DownloadDataPackagesCommand.ExecuteAsync(null);
    }

    private void OnFlyThroughClicked(object? sender, EventArgs e)
    {
        viewModel.ActiveSection = 0;
        TerrainView.StartOrlaPercFlight();
    }

    /// <inheritdoc />
    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Orientation-driven immersive mode: landscape hides the floating UI so a phone screenshot of the
        // scene is chrome-free; portrait restores it ("przechylenie telefonu bokiem wyłącza menu").
        DeviceDisplay.Current.MainDisplayInfoChanged += OnMainDisplayInfoChanged;
        ApplyOrientationChrome(DeviceDisplay.Current.MainDisplayInfo.Orientation);

        if (initialCenterApplied)
        {
            return;
        }

        // Defer one frame so the MapControl has measured its size; otherwise the
        // navigator's viewport width/height are zero and the center call is a no-op.
        Dispatcher.Dispatch(async () =>
        {
            viewModel.CenterOnDefaultRegion();
            viewModel.ActivateViewportAwareTrailLayer(trailControllerLogger);
            initialCenterApplied = true;
            await viewModel.AutoLoadOnStartupAsync().ConfigureAwait(true);
        });
    }

    /// <inheritdoc />
    protected override void OnDisappearing()
    {
        DeviceDisplay.Current.MainDisplayInfoChanged -= OnMainDisplayInfoChanged;
        base.OnDisappearing();
    }

    private void OnMainDisplayInfoChanged(object? sender, DisplayInfoChangedEventArgs e)
        => ApplyOrientationChrome(e.DisplayInfo.Orientation);

    // Landscape → immersive (floating UI hidden for a clean screenshot); portrait → chrome restored.
    // PHONE ONLY: the gesture is "turn the device sideways for a clean shot". A desktop monitor is
    // ALWAYS landscape, so applying this there would permanently hide the whole menu + camera pads
    // (the "nie ma strzałek ani menu" bug). On desktop the chrome is always shown.
    private void ApplyOrientationChrome(DisplayOrientation orientation)
    {
        if (DeviceInfo.Current.Idiom == DeviceIdiom.Desktop)
        {
            viewModel.ImmersiveMode = false;
            return;
        }

        viewModel.ImmersiveMode = orientation == DisplayOrientation.Landscape;
    }
}