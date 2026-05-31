using MapaTur.Application.Location;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Location;

using Microsoft.Extensions.Logging;
using Microsoft.Maui.Devices.Sensors;

using MauiGeoLocation = Microsoft.Maui.Devices.Sensors.Location;

namespace MapaTur.App.Services;

/// <summary>
/// MAUI implementation of <see cref="IUserLocationService"/> backed by
/// <see cref="Geolocation.Default"/>. Polls every <see cref="PollIntervalMs"/> ms at Best
/// accuracy while tracking; pushes each fix to subscribers via <see cref="LocationChanged"/>.
/// Polling (vs LocationChanged event-based subscription) keeps the implementation portable
/// across MAUI's platform back-ends — the listener API isn't supported everywhere, while
/// <c>GetLocationAsync</c> is universal.
/// </summary>
public sealed class MauiUserLocationService : IUserLocationService, IAsyncDisposable
{
    // Hiking-grade cadence: 2 s gives noticeable responsiveness without flattening the battery.
    // OS may rate-limit even below this anyway; raise to ~5 s on platforms reporting high cost.
    private const int PollIntervalMs = 2000;

    private readonly IGeolocation geolocation;
    private readonly ILogger<MauiUserLocationService> logger;
    private CancellationTokenSource? loopCts;
    private Task? loopTask;

    public MauiUserLocationService(ILogger<MauiUserLocationService> logger)
        : this(Geolocation.Default, logger)
    {
    }

    public MauiUserLocationService(IGeolocation geolocation, ILogger<MauiUserLocationService> logger)
    {
        ArgumentNullException.ThrowIfNull(geolocation);
        ArgumentNullException.ThrowIfNull(logger);
        this.geolocation = geolocation;
        this.logger = logger;
    }

    /// <inheritdoc />
    public UserLocation? LastKnown { get; private set; }

    /// <inheritdoc />
    public bool IsTracking => loopTask is not null && !loopTask.IsCompleted;

    /// <inheritdoc />
    public event EventHandler<UserLocation>? LocationChanged;

    /// <inheritdoc />
    public event EventHandler? PermissionDenied;

    /// <inheritdoc />
    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsTracking)
        {
            return true;
        }

        // Permissions live in Microsoft.Maui.ApplicationModel; request on the UI thread so the
        // platform can show the system dialog. Granted → start the poll loop; denied → notify
        // subscribers so the UI can degrade gracefully (button stays toggleable, marker stays empty).
        PermissionStatus status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>().ConfigureAwait(true);
        if (status != PermissionStatus.Granted)
        {
            status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>().ConfigureAwait(true);
        }
        if (status != PermissionStatus.Granted)
        {
            logger.LogInformation("Location permission denied (status {Status})", status);
            PermissionDenied?.Invoke(this, EventArgs.Empty);
            return false;
        }

        loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        loopTask = Task.Run(() => PollLoopAsync(loopCts.Token), loopCts.Token);
        return true;
    }

    /// <inheritdoc />
    public void Stop()
    {
        loopCts?.Cancel();
        loopCts?.Dispose();
        loopCts = null;
        loopTask = null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var request = new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(10));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                MauiGeoLocation? fix = await geolocation.GetLocationAsync(request, ct).ConfigureAwait(false);
                if (fix is not null)
                {
                    var location = new UserLocation(
                        new GeoPoint(fix.Latitude, fix.Longitude, fix.Altitude),
                        // MAUI reports accuracy as nullable double in metres; 0 (unknown) is acceptable.
                        fix.Accuracy.GetValueOrDefault(0.0),
                        fix.Timestamp);
                    LastKnown = location;
                    LocationChanged?.Invoke(this, location);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // GPS can be transiently unavailable (indoors, mid-tunnel). Log + back off instead of
                // killing the loop — the next iteration will retry once we're back outdoors.
                logger.LogDebug(ex, "Location poll failed; retrying after the next interval");
            }

            try
            {
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}