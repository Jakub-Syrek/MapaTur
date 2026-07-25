using System.Numerics;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Application.Terrain;

/// <summary>One catalogued climbing route: metric waypoints relative to the summit (X east, Y north),
/// ordered BASE → TOP (the climbing direction). Paths are approximate — hand-traced from published topo
/// descriptions onto the DEM heightfield; grades use the Tatra (UIAA-style) scale.</summary>
public sealed record ClimbingRouteDefinition(
    string Name,
    string Grade,
    PttkColor Color,
    IReadOnlyList<Vector2> OffsetsFromSummitMeters);

/// <summary>A catalogued route resolved into world XY metres (offsets translated by the snapped summit).</summary>
public sealed record WorldClimbingRoute(
    string Name,
    string Grade,
    PttkColor Color,
    IReadOnlyList<Vector2> PathXY);

/// <summary>
/// Classic climbing routes of the Tatras, hand-anchored to summits. The catalogue is defined in
/// summit-relative metres so a single anchor fix (snap to the local DEM maximum) grounds the whole topo
/// even when the source summit coordinate is a few metres off the DEM's tower.
/// </summary>
public static class TatraClimbingRoutes
{
    /// <summary>Mnich (2070 m per the Master Topo) above Morskie Oko. This is the needle top FOUND BY
    /// THE FINE DEM ITSELF (re-snap after 1 m streaming landed at 2069 m, snap 16.5 m from here) —
    /// calibrated 2026-07-17 with the marker grid + user screenshots. ⚠ On the COARSE base DEM the
    /// needle is smoothed away (only Mniszek at ~2018 m is prominent), so startup still anchors
    /// provisionally and the re-snap confirms once 1 m terrain is in.</summary>
    public static GeoPoint MnichSummit { get; } = new(49.192532, 20.054851);

    /// <summary>The classics of Mnich per the purchased Master Topo (G. Głazek, "Mnich i Mniszek",
    /// ed. II): the ~260 m east face over the Mnichowy Żleb (left→right = south→north exactly as the
    /// topo photograph orders them, route numbers in comments), and the north / north-west walls with
    /// the arêtes. BASE → TOP waypoints; grades as printed.</summary>
    public static IReadOnlyList<ClimbingRouteDefinition> Mnich { get; } =
    [
        // ── East face — traced from the Master-Topo photo onto the DEM face surface (2026-07-17):
        // topo horizontal → south/north (dy), topo height → position up the triangular fan (base→summit),
        // east offset (dx) taken from the real wall so each line SITS on the face. base → top. ──
        new("Droga Stanisławskiego", "VI", PttkColor.Green,                                     // 1
            [new(41f, -76f), new(31f, -58f), new(20f, -37f), new(10f, -20f), new(4f, -7f), new(0f, 0f)]),
        new("Komin Stanisławskiego wprost", "VI+, 1xA0", PttkColor.Blue,                       // 2
            [new(43f, -66f), new(31f, -58f), new(20f, -37f), new(10f, -20f), new(4f, -7f), new(0f, 0f)]),
        new("„2+1” (Amerykańskie Ryski)", "VIII", PttkColor.Yellow,                  // 5
            [new(46f, -42f), new(33f, -37f), new(20f, -32f), new(10f, -20f), new(4f, -7f), new(0f, 0f)]),
        new("Sprężyna", "VII- (okapik) / VI (płyta)", PttkColor.Red,                           // 6
            [new(47f, -33f), new(34f, -25f), new(22f, -20f), new(10f, -19f), new(4f, -7f), new(0f, 0f)]),
        new("Droga Sadusia", "IX (oryg. A1)", PttkColor.Black,                                 // 7
            [new(49f, -15f), new(36f, -9f), new(23f, -4f), new(12f, -1f), new(5f, -1f), new(0f, 0f)]),
        new("Wariant R", "VIII+/IX-", PttkColor.Blue,                                          // 8
            [new(50f, -5f), new(37f, -1f), new(24f, 0f), new(12f, 1f), new(4f, 1f), new(0f, 0f)]),
        new("Wachowicz", "VII+/VIII-", PttkColor.Green,                                        // 9
            [new(49f, 4f), new(36f, 6f), new(22f, 7f), new(11f, 6f), new(4f, 3f), new(0f, 0f)]),
        new("Misterium Nieprawości", "IX/IX+", PttkColor.Yellow,                               // 20
            [new(47f, 13f), new(35f, 14f), new(24f, 13f), new(14f, 13f), new(5f, 8f), new(0f, 0f)]),
        new("Metallica", "IX-/IX", PttkColor.Red,                                              // 21
            [new(45f, 18f), new(32f, 19f), new(18f, 20f), new(9f, 14f), new(4f, 6f), new(0f, 0f)]),
        new("Fereński", "VII/VII+", PttkColor.Black,                                           // 10
            [new(43f, 25f), new(30f, 28f), new(19f, 29f), new(14f, 22f), new(7f, 11f), new(2f, 4f), new(0f, 0f)]),
        new("Rysa Hobrzańskiego", "VI+/VII-", PttkColor.Blue,                                  // 11
            [new(41f, 30f), new(28f, 32f), new(19f, 29f), new(14f, 22f), new(7f, 11f), new(2f, 4f), new(0f, 0f)]),
        new("Droga Łapińskiego", "VII lub VII+", PttkColor.Green,                              // 12
            [new(40f, 34f), new(27f, 36f), new(19f, 29f), new(14f, 22f), new(7f, 11f), new(2f, 4f), new(0f, 0f)]),
        new("Międzymiastowa", "VI+", PttkColor.Yellow,                                         // 13
            [new(39f, 36f), new(27f, 38f), new(19f, 29f), new(14f, 22f), new(7f, 11f), new(2f, 4f), new(0f, 0f)]),
        new("Zacięcie Kosińskiego", "VII", PttkColor.Red,                                      // 14
            [new(38f, 42f), new(25f, 39f), new(18f, 28f), new(14f, 21f), new(7f, 10f), new(2f, 4f), new(0f, 0f)]),
        new("Wariant Baryły i Stonawskiego", "VIII", PttkColor.Black,                          // 19
            [new(36f, 49f), new(25f, 39f), new(18f, 28f), new(12f, 19f), new(6f, 9f), new(2f, 4f), new(0f, 0f)]),
        new("Superata Młodości", "VIII+/IX-", PttkColor.Blue,                                  // 22
            [new(35f, 53f), new(24f, 36f), new(17f, 26f), new(10f, 15f), new(5f, 8f), new(2f, 4f), new(0f, 0f)]),

        // ── North / north-west walls (Głazek "Mnich, ściany północna i pn.-zach."; S→N) ──
        new("Droga zejściowa „Przez Płytę”", "II-III", PttkColor.Green,              // 13
            [new(-26f, -52f), new(-24f, -36f), new(-16f, -20f), new(-8f, -9f), new(-3f, -3f)]),
        new("„Przez przewieszkę”", "II m. IV+", PttkColor.Yellow,                    // 14
            [new(-32f, -44f), new(-28f, -30f), new(-18f, -16f), new(-8f, -7f), new(-3f, -2f)]),
        new("Droga Robakiewicza", "III", PttkColor.Blue,                                       // 12
            [new(-40f, -36f), new(-34f, -25f), new(-22f, -13f), new(-10f, -5f), new(-3f, -2f)]),
        new("Droga Mogilnickiego", "VI-", PttkColor.Red,                                       // 11
            [new(-48f, -28f), new(-40f, -19f), new(-26f, -10f), new(-12f, -4f), new(-3f, -1f)]),
        new("Droga Klasyczna", "IV+", PttkColor.Green,                                         // 5
            [new(-58f, -18f), new(-46f, -12f), new(-32f, -7f), new(-16f, -3f), new(-4f, -1f)]),
        new("Droga Orłowskiego", "V-", PttkColor.Yellow,                                       // 6
            [new(-62f, -10f), new(-48f, -6f), new(-32f, -3f), new(-16f, -1f), new(-4f, 0f)]),
        new("Zemsta Wacława", "VI+/VII-", PttkColor.Blue,                                      // 9
            [new(-64f, -2f), new(-50f, 0f), new(-34f, 1f), new(-16f, 1f), new(-4f, 0f)]),
        new("„Ny-ny-ny”", "VII+/VIII-", PttkColor.Red,                               // 10
            [new(-60f, 6f), new(-46f, 5f), new(-32f, 4f), new(-16f, 2f), new(-4f, 1f)]),
        new("Fuczok", "VII OS", PttkColor.Black,                                               // 15
            [new(-56f, 14f), new(-44f, 12f), new(-30f, 9f), new(-14f, 5f), new(-4f, 2f)]),
        new("Kant Klasyczny", "V+", PttkColor.Green,                                           // 3
            [new(-40f, 28f), new(-32f, 21f), new(-22f, 14f), new(-10f, 7f), new(-3f, 2f)]),
        new("Międzykancie", "VII-", PttkColor.Yellow,                                          // 2
            [new(-34f, 40f), new(-28f, 30f), new(-20f, 20f), new(-10f, 10f), new(-3f, 3f)]),
        new("Kant Hakowy", "VII", PttkColor.Red,                                               // 1
            [new(-26f, 52f), new(-22f, 38f), new(-16f, 25f), new(-8f, 12f), new(-3f, 4f)]),
    ];

    /// <summary>Mniszek (2045 m) — the tower south of Mnich across the Mnichowa Przełączka Wyżnia.
    /// No OSM peak node exists. Seeded on the SEPARATE tower ~156 m south of the Mnich needle (the
    /// coarse DEM's 2018 m prominent top — the calibration-grid origin): snapping from the ridge north
    /// of it glued Mniszek onto the needle's south shoulder 52 m from Mnich, which is wrong.</summary>
    public static GeoPoint MniszekSummit { get; } = new(49.191131, 20.054466);

    /// <summary>East-wall classics of Mniszek per the purchased Master Topo (S→N).</summary>
    public static IReadOnlyList<ClimbingRouteDefinition> Mniszek { get; } =
    [
        new("Cud niepamięci", "VII", PttkColor.Yellow,                                         // 0
            [new(56f, -25f), new(46f, -19f), new(34f, -13f), new(22f, -8f), new(10f, -3f), new(3f, -1f)]),
        new("Lewym filarem", "VI", PttkColor.Green,                                            // 1
            [new(60f, -18f), new(50f, -14f), new(38f, -10f), new(24f, -6f), new(10f, -2f), new(3f, -1f)]),
        new("Droga Łapińskiego (Mniszek)", "V+", PttkColor.Blue,                               // 2
            [new(64f, -12f), new(52f, -9f), new(40f, -7f), new(26f, -4f), new(12f, -2f), new(3f, 0f)]),
        new("Superdirettissima", "VII+", PttkColor.Red,                                        // 4
            [new(66f, -6f), new(54f, -5f), new(40f, -3f), new(26f, -2f), new(12f, -1f), new(3f, 0f)]),
        new("Hiperdirettissima", "VII+", PttkColor.Black,                                      // K1
            [new(68f, -2f), new(56f, -1f), new(42f, -1f), new(26f, 0f), new(12f, 0f), new(3f, 0f)]),
        new("Skośna Rysa", "VI+", PttkColor.Yellow,                                            // 5
            [new(66f, 2f), new(54f, 2f), new(42f, 2f), new(26f, 1f), new(12f, 1f), new(3f, 0f)]),
        new("Direttissima", "VI", PttkColor.Green,                                             // 6
            [new(64f, 7f), new(52f, 6f), new(40f, 5f), new(26f, 3f), new(12f, 2f), new(3f, 1f)]),
        new("Śmigło", "VII- lub A1", PttkColor.Blue,                                           // 7
            [new(60f, 12f), new(50f, 10f), new(38f, 8f), new(24f, 5f), new(10f, 2f), new(3f, 1f)]),
        new("Kant Gierycha", "VI+", PttkColor.Red,                                             // 8
            [new(56f, 18f), new(46f, 15f), new(36f, 11f), new(22f, 7f), new(10f, 3f), new(3f, 1f)]),
    ];

    /// <summary>Mnich Małołącki ("Babka", ~1600 m, Dolina Małej Łąki) — the OSM peak node.</summary>
    public static GeoPoint MnichMalolackiSummit { get; } = new(49.2461666, 19.9282043);

    /// <summary>West-wall classics of Mnich Małołącki per the Master Topo photograph
    /// (facing the wall: left→right = north→south, the Mnichowy Przechód gully on the right).</summary>
    public static IReadOnlyList<ClimbingRouteDefinition> MnichMalolacki { get; } =
    [
        new("Droga Mroza", "A3", PttkColor.Yellow,
            [new(-44f, 6f), new(-36f, 5f), new(-26f, 3f), new(-16f, 2f), new(-6f, 1f), new(-2f, 0f)]),
        new("Wariant Malczyka", "VI A2", PttkColor.Green,
            [new(-48f, 1f), new(-38f, 0f), new(-28f, 0f), new(-18f, 0f), new(-8f, 0f), new(-2f, -1f)]),
        new("Wynalazek", "VII A2", PttkColor.Red,
            [new(-46f, -6f), new(-38f, -5f), new(-28f, -4f), new(-18f, -3f), new(-8f, -2f), new(-2f, -1f)]),
        new("Wolf-Jargiło", "V A1", PttkColor.Blue,
            [new(-42f, -13f), new(-34f, -11f), new(-26f, -8f), new(-16f, -5f), new(-8f, -3f), new(-2f, -2f)]),
        new("Czyż-Zgłobicka", "V A0", PttkColor.Black,
            [new(-36f, -20f), new(-30f, -16f), new(-24f, -12f), new(-16f, -8f), new(-8f, -4f), new(-3f, -2f)]),
    ];

    /// <summary>One summit + its catalogued routes; the view anchors each massif independently
    /// (snap to its own tower top) as terrain data around it becomes available. The published summit
    /// elevation (when known) picks the RIGHT prominent top near the seed — see
    /// <see cref="SnapToLocalMaximum"/>.</summary>
    public sealed record ClimbingMassif(
        string Name, GeoPoint Summit, float? SummitElevationMeters, IReadOnlyList<ClimbingRouteDefinition> Routes);

    /// <summary>All catalogued massifs. NOTE: Mniszek is intentionally NOT active yet — in the DEM it does
    /// not separate cleanly from Mnich (only ~50 m away across the low MPW col, the coarse base merges
    /// them), so its anchor lands on Mnich's own lower face and its routes render in the wrong place.
    /// Re-enable once its tower has a distinct verified position. <see cref="Mniszek"/> data is kept.</summary>
    public static IReadOnlyList<ClimbingMassif> Massifs { get; } =
    [
        new("Mnich", MnichSummit, 2070f, Mnich),
        new("Mnich Małołącki", MnichMalolackiSummit, null, MnichMalolacki),
    ];

    /// <summary>Translates catalogue offsets to world XY around the (snapped) summit position.</summary>
    public static IReadOnlyList<WorldClimbingRoute> BuildWorldRoutes(
        IReadOnlyList<ClimbingRouteDefinition> definitions, Vector2 summitWorldXY)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var routes = new List<WorldClimbingRoute>(definitions.Count);
        foreach (ClimbingRouteDefinition definition in definitions)
        {
            var path = new Vector2[definition.OffsetsFromSummitMeters.Count];
            for (int i = 0; i < path.Length; i++)
            {
                path[i] = summitWorldXY + definition.OffsetsFromSummitMeters[i];
            }

            routes.Add(new WorldClimbingRoute(definition.Name, definition.Grade, definition.Color, path));
        }

        return routes;
    }

    /// <summary>
    /// Refines a summit seed to the top of ITS OWN tower by greedy hill-climbing (8-neighbour ascent at
    /// <paramref name="stepMeters"/>, wandering at most <paramref name="radiusMeters"/> from the seed).
    /// Published coordinates are often a few metres off the rendered tower; walking strictly uphill finds
    /// the DEM's own peak WITHOUT crossing a saddle onto a higher neighbouring slope — a plain grid
    /// maximum around Mnich lands on the massif rising toward Cubryna instead of the tower. Falls back to
    /// the seed when the ground sampler has no data.
    /// </summary>
    public static Vector2 SnapToLocalMaximum(
        Func<Vector2, float?> ground, Vector2 seedXY, float radiusMeters, float stepMeters, float? targetElevationMeters = null)
    {
        ArgumentNullException.ThrowIfNull(ground);
        (Vector2 top, float score, bool found) =
            BestProminentTop(ground, seedXY, radiusMeters, stepMeters, targetElevationMeters);
        if (targetElevationMeters is not null && (!found || score > 25f))
        {
            // The seed can sit on a FEATURELESS flank with the real tower outside the first radius (the
            // Mnich OSM node samples ~130 m below the top with no prominent top nearby at all). Sweep
            // wider at a coarser step; keep whichever candidate matches the catalogued height better.
            (Vector2 wideTop, float wideScore, bool wideFound) =
                BestProminentTop(ground, seedXY, radiusMeters * 3.5f, stepMeters * 2f, targetElevationMeters);
            if (wideFound && (!found || wideScore < score))
            {
                return wideTop;
            }
        }

        return found ? top : seedXY;
    }

    /// <summary>Diagnostic: the prominent local tops around <paramref name="seedXY"/> (highest within a
    /// 12 m window), ordered by descending elevation. Lets the anchor log show WHERE the DEM's summits
    /// actually are when the catalogued coordinate doesn't match the terrain.</summary>
    public static List<(Vector2 Position, float Elevation)> ListProminentTops(
        Func<Vector2, float?> ground, Vector2 seedXY, float radiusMeters, float stepMeters, int maxCount)
    {
        ArgumentNullException.ThrowIfNull(ground);
        var tops = new List<(Vector2 Position, float Elevation)>();
        CollectProminentTops(ground, seedXY, radiusMeters, stepMeters, tops);
        tops.Sort((a, b) => b.Elevation.CompareTo(a.Elevation));
        if (tops.Count > maxCount)
        {
            tops.RemoveRange(maxCount, tops.Count - maxCount);
        }

        return tops;
    }

    // Collects every PROMINENT local top in the radius (highest within a 12 m window — boulders and
    // slopes don't qualify) and picks the one whose elevation best matches the catalogued summit height;
    // without a target, the highest prominent top wins. The prominence window must lie fully inside the
    // sampled grid, so a rising slope cut off at the edge can't fake a top.
    private static (Vector2 Top, float Score, bool Found) BestProminentTop(
        Func<Vector2, float?> ground, Vector2 seedXY, float radiusMeters, float stepMeters, float? targetElevationMeters)
    {
        var tops = new List<(Vector2 Position, float Elevation)>();
        CollectProminentTops(ground, seedXY, radiusMeters, stepMeters, tops);
        Vector2 best = seedXY;
        float bestScore = float.PositiveInfinity;
        foreach ((Vector2 position, float e) in tops)
        {
            float score = targetElevationMeters is { } target ? MathF.Abs(e - target) : -e;
            if (score < bestScore)
            {
                bestScore = score;
                best = position;
            }
        }

        return (best, bestScore, !float.IsPositiveInfinity(bestScore));
    }

    private static void CollectProminentTops(
        Func<Vector2, float?> ground, Vector2 seedXY, float radiusMeters, float stepMeters,
        List<(Vector2 Position, float Elevation)> tops)
    {
        const float ProminenceWindowMeters = 12f;
        int margin = Math.Max(1, (int)MathF.Ceiling(ProminenceWindowMeters / stepMeters));
        int half = (int)MathF.Ceiling(radiusMeters / stepMeters) + margin;
        int size = (2 * half) + 1;
        var elevation = new float[size, size];
        var sampled = new bool[size, size];
        for (int j = 0; j < size; j++)
        {
            for (int i = 0; i < size; i++)
            {
                var xy = new Vector2(seedXY.X + ((i - half) * stepMeters), seedXY.Y + ((j - half) * stepMeters));
                if (ground(xy) is { } e)
                {
                    elevation[j, i] = e;
                    sampled[j, i] = true;
                }
            }
        }

        for (int j = margin; j < size - margin; j++)
        {
            for (int i = margin; i < size - margin; i++)
            {
                if (!sampled[j, i])
                {
                    continue;
                }

                float dx = (i - half) * stepMeters;
                float dy = (j - half) * stepMeters;
                if ((dx * dx) + (dy * dy) > radiusMeters * radiusMeters)
                {
                    continue;
                }

                float e = elevation[j, i];
                bool isTop = true;
                for (int wj = -margin; wj <= margin && isTop; wj++)
                {
                    for (int wi = -margin; wi <= margin; wi++)
                    {
                        if (sampled[j + wj, i + wi] && elevation[j + wj, i + wi] > e + 0.01f)
                        {
                            isTop = false;
                            break;
                        }
                    }
                }

                if (isTop)
                {
                    tops.Add((new Vector2(seedXY.X + dx, seedXY.Y + dy), e));
                }
            }
        }
    }
}