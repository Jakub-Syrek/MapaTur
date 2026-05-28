using MapaTur.Application.Trails;

using Mapsui;

using Microsoft.Extensions.Logging;

using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Subscribes to Mapsui's <c>Navigator.FetchRequested</c> so that whenever the
/// viewport settles after a pan or zoom, only trails intersecting the visible
/// area are pulled from the repository — with Douglas–Peucker simplification
/// tuned to the current zoom level — and handed to the layer renderer.
/// </summary>
/// <remarks>
/// This is the rendering counterpart to the bbox-indexed
/// <see cref="ITrailRepository.FindIntersectingAsync(MapaTur.Domain.Geography.MapBounds, double, System.Threading.CancellationToken)"/>
/// overload: instead of stuffing the full downloaded list into a MemoryLayer once and
/// re-projecting all of it every frame, the layer is rebuilt per viewport with
/// already-simplified geometry. Combined with write-time simplification and the
/// bbox index, this collapses the per-frame work from "all 760 trails fully
/// detailed" to "~20 trails simplified for the current zoom".
/// </remarks>
public sealed class ViewportAwareTrailLayerController : IDisposable
{
    private readonly Map map;
    private readonly ITrailRepository repository;
    private readonly ITrailLayerRenderer renderer;
    private readonly ILogger<ViewportAwareTrailLayerController> logger;
    private CancellationTokenSource? activeQuery;
    private bool disposed;

    /// <summary>
    /// Optional client-side filter applied after the repository query. Toggling
    /// trail-colour / region checkboxes in the view-model rotates this in.
    /// When null, every queried trail is rendered.
    /// </summary>
    public Func<MapaTur.Domain.Trails.Trail, bool>? Filter { get; set; }

    public ViewportAwareTrailLayerController(
        Map map,
        ITrailRepository repository,
        ITrailLayerRenderer renderer,
        ILogger<ViewportAwareTrailLayerController> logger)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(logger);

        this.map = map;
        this.repository = repository;
        this.renderer = renderer;
        this.logger = logger;

        map.Navigator.FetchRequested += OnFetchRequested;
    }

    /// <summary>
    /// Forces an immediate refresh from the repository — call this right after upserting
    /// new trails (e.g. after a successful Overpass download) so the user doesn't have to
    /// pan to see them.
    /// </summary>
    public void RequestRefresh()
    {
        _ = QueryAndRenderAsync();
    }

    private void OnFetchRequested(object? sender, EventArgs e)
    {
        _ = QueryAndRenderAsync();
    }

    private async Task QueryAndRenderAsync()
    {
        if (disposed)
        {
            return;
        }

        // Cancel any in-flight query — only the latest viewport matters.
        activeQuery?.Cancel();
        activeQuery = new CancellationTokenSource();
        var token = activeQuery.Token;

        try
        {
            var viewport = map.Navigator.Viewport;
            if (viewport.Width <= 0 || viewport.Height <= 0 || viewport.Resolution <= 0)
            {
                return;
            }

            double halfWidth = viewport.Width * viewport.Resolution / 2.0;
            double halfHeight = viewport.Height * viewport.Resolution / 2.0;
            var extent = new MRect(
                viewport.CenterX - halfWidth,
                viewport.CenterY - halfHeight,
                viewport.CenterX + halfWidth,
                viewport.CenterY + halfHeight);
            var bounds = ViewportBounds.FromMercatorExtent(extent);
            if (bounds is null)
            {
                return;
            }

            double epsilon = ZoomEpsilonCalculator.EpsilonMetersForResolution(viewport.Resolution);
            var trails = await repository.FindIntersectingAsync(bounds.Value, epsilon, token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                return;
            }

            // Apply the user-driven colour / region filter, if any.
            var filter = Filter;
            if (filter is not null)
            {
                trails = trails.Where(filter).ToList();
            }

            // Mapsui mutations must run on the UI thread.
            await MainThread.InvokeOnMainThreadAsync(() =>
            {
                if (!token.IsCancellationRequested && !disposed)
                {
                    renderer.RenderTrails(map, trails);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer fetch.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ViewportAwareTrailLayerController query failed");
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        map.Navigator.FetchRequested -= OnFetchRequested;
        activeQuery?.Cancel();
        activeQuery?.Dispose();
    }
}