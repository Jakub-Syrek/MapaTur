using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Tracks;

/// <summary>
/// A single point along a recorded track: position in space and time, plus optional
/// physiological sensors.
/// </summary>
/// <param name="Position">Geographic position (latitude, longitude, optional elevation).</param>
/// <param name="Timestamp">UTC instant at which this point was recorded.</param>
/// <param name="HeartRateBpm">Optional heart rate in beats per minute.</param>
/// <param name="CadenceRpm">Optional cadence in revolutions per minute.</param>
public sealed record TrackPoint(
    GeoPoint Position,
    DateTimeOffset Timestamp,
    int? HeartRateBpm = null,
    int? CadenceRpm = null);