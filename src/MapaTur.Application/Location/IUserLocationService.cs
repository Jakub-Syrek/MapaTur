using MapaTur.Domain.Location;

namespace MapaTur.Application.Location;

/// <summary>
/// Port for the OS-level GPS / geolocation feed. Implementations live in the App layer where
/// the MAUI <c>Geolocation</c> API and the platform permission flows are available.
/// Implementations push the latest <see cref="UserLocation"/> via <see cref="LocationChanged"/>;
/// subscribers (ViewModel, renderers) react and re-draw.
/// </summary>
/// <remarks>
/// Lifecycle: call <see cref="StartAsync"/> after the user opts in (a button, a permission grant).
/// The service is responsible for asking for foreground location permission on Android/iOS and
/// for handling the user denying it (raise <see cref="PermissionDenied"/>, never throw on the
/// hot path). Call <see cref="Stop"/> to release the OS subscription when the app suspends or
/// the user disables tracking.
/// </remarks>
public interface IUserLocationService
{
    /// <summary>Last fix the service has received, or null when tracking is off / never started.</summary>
    UserLocation? LastKnown { get; }

    /// <summary>True while the service is subscribed to OS location updates.</summary>
    bool IsTracking { get; }

    /// <summary>Fires on every new GPS fix, on a thread chosen by the platform layer.</summary>
    event EventHandler<UserLocation>? LocationChanged;

    /// <summary>Fires when the OS or user revokes/declines location permission.</summary>
    event EventHandler? PermissionDenied;

    /// <summary>
    /// Asks for permission if needed, then subscribes to OS location updates. Idempotent: a
    /// second call while already tracking is a no-op.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True on a successful subscription; false when permission was declined.</returns>
    Task<bool> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Releases the OS subscription. Idempotent.</summary>
    void Stop();
}