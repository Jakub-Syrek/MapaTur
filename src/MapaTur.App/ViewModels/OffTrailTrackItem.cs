using System.Globalization;

namespace MapaTur.App.ViewModels;

/// <summary>
/// A single row in the off-trail ("pozaszlaki") management panel: identifies an imported GPX/TCX track and
/// carries the display summary (distance + point count). The <see cref="Id"/> is the delete key.
/// </summary>
/// <param name="Id">Stable identifier of the persisted track.</param>
/// <param name="Name">Human-readable name (from the file / track name).</param>
/// <param name="DistanceKm">Total horizontal length in kilometres.</param>
/// <param name="PointCount">Number of geometry points.</param>
public sealed record OffTrailTrackItem(Guid Id, string Name, double DistanceKm, int PointCount)
{
    /// <summary>One-line summary shown under the name, e.g. "3.2 km · 412 pkt".</summary>
    public string Summary => string.Format(CultureInfo.CurrentUICulture, "{0:0.0} km · {1} pkt", DistanceKm, PointCount);
}