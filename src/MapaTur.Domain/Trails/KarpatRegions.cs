using MapaTur.Domain.Geography;

namespace MapaTur.Domain.Trails;

/// <summary>
/// Canonical bounding boxes for the major Polish + Slovak Carpathian sub-regions
/// the trail filter exposes as toggles. Coarse and intentionally so — they're
/// only used to decide whether a trail's lon/lat bbox intersects the area the
/// user wants to see, not for any geographic-truth claim.
/// </summary>
public static class KarpatRegions
{
    /// <summary>Polish + Slovak Tatry (Western + High).</summary>
    public static MapBounds Tatry { get; } = new(
        new GeoPoint(49.05, 19.55),
        new GeoPoint(49.40, 20.30));

    /// <summary>Beskidy (Sląski + Żywiecki + Sądecki + Niski).</summary>
    public static MapBounds Beskidy { get; } = new(
        new GeoPoint(49.30, 18.50),
        new GeoPoint(49.80, 21.50));

    /// <summary>Pieniny.</summary>
    public static MapBounds Pieniny { get; } = new(
        new GeoPoint(49.35, 20.20),
        new GeoPoint(49.55, 20.55));

    /// <summary>Bieszczady (Polish + Ukrainian fringe).</summary>
    public static MapBounds Bieszczady { get; } = new(
        new GeoPoint(49.00, 22.00),
        new GeoPoint(49.40, 22.90));
}