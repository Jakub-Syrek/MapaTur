using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Location;

/// <summary>
/// A single GPS fix for the device's current position. Carries the reported accuracy radius and the
/// fix timestamp so a stale or low-precision reading can be shown differently (faded marker, expanded
/// halo) without changing the rendering pipeline.
/// </summary>
/// <param name="Position">Latitude/longitude (and optional GNSS-reported altitude) of the fix.</param>
/// <param name="AccuracyMeters">Horizontal accuracy radius in metres at 1σ. ≥ 0; 0 means unknown.</param>
/// <param name="Timestamp">UTC instant the fix was produced by the OS location service.</param>
public sealed record UserLocation(GeoPoint Position, double AccuracyMeters, DateTimeOffset Timestamp)
{
    /// <summary>
    /// Creates a <see cref="UserLocation"/>, guarding against negative or non-finite accuracy.
    /// </summary>
    public static UserLocation Create(GeoPoint position, double accuracyMeters, DateTimeOffset timestamp)
    {
        if (!double.IsFinite(accuracyMeters) || accuracyMeters < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(accuracyMeters), accuracyMeters, "Accuracy must be a finite, non-negative number of metres.");
        }
        return new UserLocation(position, accuracyMeters, timestamp);
    }
}