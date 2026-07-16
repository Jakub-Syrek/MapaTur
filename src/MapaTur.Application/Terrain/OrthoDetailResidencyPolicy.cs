using MapaTur.Domain.Geography;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Chooses which detail CELLS should be resident this frame (docs/PLAN-ortho-massif-streaming.md, R2). Pure
/// decision logic — the GL compose/upload/evict is the streaming manager's job. Rules:
/// <list type="bullet">
/// <item>Above <c>fastMotionSpeedMps</c> (dragon flight) return NOTHING — the ring would only churn behind a
/// camera that outruns compose latency; the base ortho carries the frame (the z18/z19 fast-motion lesson).</item>
/// <item>Otherwise take the ring of cells within <c>ringRadiusMeters</c>, ranked nearest-first to a focus led
/// FORWARD along the velocity by <c>prefetchLeadMeters</c> (so a walked boundary crossing finds the next cell
/// already composed), and cap at <c>nearCap</c> (from the shared VRAM budget).</item>
/// <item>The cell UNDER the camera is always first, so there is never a hole beneath the viewer.</item>
/// </list>
/// </summary>
public sealed class OrthoDetailResidencyPolicy
{
    private const double MetersPerLatDeg = 111320.0;

    private readonly OrthoDetailGrid grid;
    private readonly double ringRadiusMeters;
    private readonly double fastMotionSpeedMps;
    private readonly double prefetchLeadMeters;

    /// <summary>Creates a policy for one detail level.</summary>
    /// <param name="grid">The detail lattice.</param>
    /// <param name="ringRadiusMeters">How far around the focus cells are wanted.</param>
    /// <param name="fastMotionSpeedMps">Above this camera speed the detail ring is suppressed entirely.</param>
    /// <param name="prefetchLeadMeters">Distance to lead the focus forward along velocity when ranking.</param>
    public OrthoDetailResidencyPolicy(
        OrthoDetailGrid grid, double ringRadiusMeters, double fastMotionSpeedMps, double prefetchLeadMeters)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (ringRadiusMeters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ringRadiusMeters));
        }

        this.grid = grid;
        this.ringRadiusMeters = ringRadiusMeters;
        this.fastMotionSpeedMps = fastMotionSpeedMps;
        this.prefetchLeadMeters = Math.Max(0, prefetchLeadMeters);
    }

    /// <summary>
    /// The cell keys to keep resident this frame, focus-cell first then nearest-to-the-led-focus up to
    /// <paramref name="nearCap"/>. Empty when suppressed (fast motion) or <paramref name="nearCap"/> ≤ 0.
    /// </summary>
    public IReadOnlyList<int> DesiredCells(GeoPoint focus, double velEastMps, double velNorthMps, int nearCap)
    {
        if (nearCap <= 0)
        {
            return Array.Empty<int>();
        }

        double speed = Math.Sqrt((velEastMps * velEastMps) + (velNorthMps * velNorthMps));
        if (speed > fastMotionSpeedMps)
        {
            return Array.Empty<int>();
        }

        double mPerLon = MetersPerLatDeg * Math.Cos(focus.Latitude * Math.PI / 180.0);
        GeoPoint led = focus;
        if (speed > 1e-6 && prefetchLeadMeters > 0)
        {
            led = new GeoPoint(
                focus.Latitude + (velNorthMps / speed * prefetchLeadMeters / MetersPerLatDeg),
                focus.Longitude + (velEastMps / speed * prefetchLeadMeters / mPerLon));
        }

        var ranked = grid.CellsInRadius(focus, ringRadiusMeters)
            .OrderBy(c => DistMeters(CellCentre(c.Ci, c.Cj), led))
            .ToList();

        var (fci, fcj) = grid.CellForPoint(focus);
        var result = new List<int> { grid.CellKey(fci, fcj) };
        foreach ((int Ci, int Cj) c in ranked)
        {
            if (result.Count >= nearCap)
            {
                break;
            }

            int key = grid.CellKey(c.Ci, c.Cj);
            if (!result.Contains(key))
            {
                result.Add(key);
            }
        }

        return result;
    }

    private GeoPoint CellCentre(int ci, int cj)
    {
        MapBounds b = grid.CellBounds(ci, cj);
        return new GeoPoint(
            (b.SouthWest.Latitude + b.NorthEast.Latitude) / 2.0,
            (b.SouthWest.Longitude + b.NorthEast.Longitude) / 2.0);
    }

    private static double DistMeters(GeoPoint a, GeoPoint b)
    {
        double dLat = (a.Latitude - b.Latitude) * MetersPerLatDeg;
        double dLon = (a.Longitude - b.Longitude) * MetersPerLatDeg
            * Math.Cos((a.Latitude + b.Latitude) * 0.5 * Math.PI / 180.0);
        return Math.Sqrt((dLat * dLat) + (dLon * dLon));
    }
}