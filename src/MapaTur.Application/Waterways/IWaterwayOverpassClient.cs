using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Waterways;

/// <summary>A named waterfall point (<c>waterway=waterfall</c> node) — rendered as a foam accent on its stream.</summary>
/// <param name="Id">OSM node id.</param>
/// <param name="Name">OSM <c>name</c>, or empty when untagged.</param>
/// <param name="Position">Node position.</param>
public readonly record struct Waterfall(long Id, string Name, GeoPoint Position);

/// <summary>
/// One watercourse fetch: stream/river polylines (reusing the unmarked <see cref="Trail"/> shape, exactly like
/// the roads layer) plus the waterfall points found in the same box.
/// </summary>
/// <param name="Streams">One polyline per <c>waterway=river|stream</c> way.</param>
/// <param name="Waterfalls">All <c>waterway=waterfall</c> nodes.</param>
public sealed record WaterwayFetchResult(IReadOnlyList<Trail> Streams, IReadOnlyList<Waterfall> Waterfalls);

/// <summary>Fetches surface watercourses (streams/rivers + waterfalls) from Overpass for a bounding box.</summary>
public interface IWaterwayOverpassClient
{
    /// <summary>Fetches all watercourses within <paramref name="bounds"/>.</summary>
    Task<WaterwayFetchResult> FetchWaterwaysAsync(MapBounds bounds, CancellationToken cancellationToken = default);
}