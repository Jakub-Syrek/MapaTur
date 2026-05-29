using System.ComponentModel;
using System.Numerics;

using MapaTur.App.Services;
using MapaTur.App.ViewModels;
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
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // Keeps the 3D camera and the 2D map framed on the same place + zoom as the user
    // toggles between them ("przechodzenie pomiędzy 3d a 2d ... po wybraniu kąta i powiększenia").
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MapPageViewModel.Is3DMode))
        {
            return;
        }

        if (viewModel.Is3DMode)
        {
            // Defer so this runs after any mesh-change FrameMesh() the binding may dispatch,
            // otherwise the auto-frame would clobber the focus we just synced from the 2D map.
            Dispatcher.Dispatch(SyncCameraToMap);
        }
        else
        {
            SyncMapToCamera();
        }
    }

    // 2D → 3D: point the camera at whatever the flat map is centred on, matching its zoom.
    // Orbit angle (azimuth/pitch) is left untouched so the user's chosen viewing angle persists.
    private void SyncCameraToMap()
    {
        if (viewModel.TerrainMesh is not { } mesh)
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

        double distance = CameraFocusSync.ResolutionToDistance(
            resolution, TerrainView.Camera.FieldOfViewYRadians, viewportHeight, center.Latitude);
        TerrainView.FocusOnWorld(target, (float)distance);
    }

    // 3D → 2D: centre the flat map on the camera's focal point, matching its zoom. This is the
    // core fix for "patrzę na górę w 3d, daję 2d i mapa jest gdzieś obok".
    private void SyncMapToCamera()
    {
        if (viewModel.TerrainMesh is not { } mesh)
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
    }

    private async void OnMapTapped(object? sender, MapEventArgs eventArgs)
    {
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