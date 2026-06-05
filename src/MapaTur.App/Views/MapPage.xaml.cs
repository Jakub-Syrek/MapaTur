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
        TerrainView.RecordingSaved += OnRecordingSaved;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
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

    // Premium-menu microinteraction: the frosted section panel slides down + fades in as it opens, and the
    // dim scrim cross-fades. The panel's IsVisible is binding-driven (show/hide); this just polishes the
    // entrance so sections feel like floating glass, not a hard cut. Exit is an instant hide (acceptable).
    private void AnimateActiveSection(int section)
    {
        _ = Scrim.FadeToAsync(section > 0 ? 1 : 0, 160, Easing.CubicOut);

        Border? panel = section switch
        {
            1 => PanelMapa,
            2 => PanelPogoda,
            3 => PanelWidok,
            4 => PanelDane,
            5 => PanelUstawienia,
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

    private void OnFlyThroughClicked(object? sender, EventArgs e)
    {
        viewModel.ActiveSection = 0;
        TerrainView.StartOrlaPercFlight();
    }

    /// <inheritdoc />
    protected override void OnAppearing()
    {
        base.OnAppearing();

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
}