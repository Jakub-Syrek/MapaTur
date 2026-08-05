using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;

namespace MapaTur.Application.Routing;

/// <summary>
/// Outcome of a multi-stop plan: the concatenated <see cref="Route"/> when every leg connected, or a
/// null route plus the zero-based index of the first leg that had no path (leg <c>i</c> joins waypoint
/// <c>i</c> to waypoint <c>i+1</c>) so the UI can name the gap.
/// </summary>
/// <param name="Route">The full route across all stops, or null when a leg failed.</param>
/// <param name="FailedLegIndex">Index of the first leg with no path, or null on success.</param>
public readonly record struct MultiStopRouteResult(Route? Route, int? FailedLegIndex);

/// <summary>
/// Plans a tourist-map chain of stops: each consecutive pair of waypoints is routed over the trail
/// graph by the underlying <see cref="IRoutePlanner"/> and the per-leg segments are concatenated into a
/// single <see cref="Route"/> (so the aggregated distance/ascent/duration cover the whole walk). The
/// first leg that has no path aborts the plan and is reported by index — the UI turns that into
/// "no trail between stop 2 and stop 3" rather than a blank failure.
/// </summary>
public sealed class MultiStopRoutePlanner
{
    /// <summary>Odcinek krótszy niż to (metry) uznajemy za zdegenerowany (podwójnie dodany przystanek).</summary>
    private const double DegenerateLegMeters = 1.0;

    private readonly IRoutePlanner planner;

    /// <summary>Initializes the planner over an <see cref="IRoutePlanner"/> (the A* trail planner).</summary>
    public MultiStopRoutePlanner(IRoutePlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        this.planner = planner;
    }

    /// <summary>
    /// Plans a route visiting <paramref name="waypoints"/> in order.
    /// </summary>
    /// <param name="waypoints">Ordered stops; at least two (a start and an end).</param>
    /// <param name="profile">Cost profile applied to every leg.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="includeOffTrailTracks">When true, every leg may route over the user's imported off-trail tracks.</param>
    /// <exception cref="ArgumentException">Fewer than two waypoints.</exception>
    public async Task<MultiStopRouteResult> PlanAsync(
        IReadOnlyList<GeoPoint> waypoints,
        RouteProfile profile,
        CancellationToken cancellationToken = default,
        bool includeOffTrailTracks = false)
    {
        ArgumentNullException.ThrowIfNull(waypoints);
        if (waypoints.Count < 2)
        {
            throw new ArgumentException("A route needs at least two waypoints (a start and an end).", nameof(waypoints));
        }

        var segments = new List<RouteSegment>();
        for (int leg = 0; leg < waypoints.Count - 1; leg++)
        {
            // Podwójnie dodany przystanek (repro 2026-08-05: „Parking Zverovka – Spálená" 2× pod rząd)
            // daje odcinek start==cel: oba końce przycinają się do TEGO SAMEGO węzła grafu, a A* zwraca
            // „brak ścieżki" (start == goal → null) — padał cały plan i z panelu znikały podsumowanie
            // trasy oraz opcje filmu, bez śladu w logu. Zerowy odcinek to nie błąd, tylko brak ruchu —
            // pomijamy go, zamiast pytać router o „ścieżkę z X do X".
            if (waypoints[leg].HaversineDistanceMetersTo(waypoints[leg + 1]) < DegenerateLegMeters)
            {
                continue;
            }

            var request = new RouteRequest(waypoints[leg], waypoints[leg + 1], profile, includeOffTrailTracks);
            Route? legRoute = await planner.PlanRouteAsync(request, cancellationToken).ConfigureAwait(false);
            if (legRoute is null)
            {
                return new MultiStopRouteResult(null, leg);
            }

            segments.AddRange(legRoute.Segments);
        }

        // Wszystkie odcinki zdegenerowane (np. dwa identyczne przystanki i nic więcej) ⇒ nie ma czego
        // iść; Route nie może być pusta (inwariant domeny), więc zwracamy uczciwą porażkę pierwszego
        // odcinka zamiast wyjątku z konstruktora.
        if (segments.Count == 0)
        {
            return new MultiStopRouteResult(null, 0);
        }

        return new MultiStopRouteResult(new Route(segments), null);
    }
}