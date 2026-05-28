using Mapsui;
using Mapsui.Projections;
using MapaTur.App.Services;
using MapaTur.App.ViewModels;
using MapaTur.Domain.Geography;
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
