using System.Numerics;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Classic climbing routes on Mnich: the catalogue must be non-trivial (all the well-known classics),
/// every route path runs from the wall base UP to just below the summit, and the summit anchor snaps
/// to the local DEM maximum so approximate source coordinates still land the topo on the actual tower.
/// </summary>
public sealed class TatraClimbingRoutesTests
{
    [Fact]
    public void Mnich_should_catalogue_at_least_ten_named_graded_routes()
    {
        IReadOnlyList<ClimbingRouteDefinition> routes = TatraClimbingRoutes.Mnich;

        Assert.True(routes.Count >= 10, $"expected >= 10 routes, got {routes.Count}");
        Assert.Equal(routes.Count, routes.Select(route => route.Name).Distinct().Count());
        Assert.All(routes, route => Assert.False(string.IsNullOrWhiteSpace(route.Grade)));
        Assert.All(routes, route => Assert.True(route.OffsetsFromSummitMeters.Count >= 3));
    }

    [Fact]
    public void All_massif_routes_should_climb_toward_their_summit()
    {
        foreach (TatraClimbingRoutes.ClimbingMassif massif in TatraClimbingRoutes.Massifs)
        {
            foreach (ClimbingRouteDefinition route in massif.Routes)
            {
                float baseDistance = route.OffsetsFromSummitMeters[0].Length();
                float topDistance = route.OffsetsFromSummitMeters[^1].Length();
                Assert.True(topDistance < 10f, $"{massif.Name}/{route.Name}: top point {topDistance:F0} m from the summit");
                Assert.True(baseDistance > 25f, $"{massif.Name}/{route.Name}: base only {baseDistance:F0} m from the summit");
            }
        }
    }

    [Fact]
    public void Massifs_should_catalogue_active_towers_with_distinct_route_names()
    {
        // Mniszek is intentionally not active (it does not separate from Mnich in the DEM), but its
        // route catalogue is retained for when it can be anchored properly.
        Assert.Contains(TatraClimbingRoutes.Massifs, massif => massif.Name == "Mnich" && massif.Routes.Count >= 25);
        Assert.Contains(TatraClimbingRoutes.Massifs, massif => massif.Name == "Mnich Małołącki" && massif.Routes.Count >= 5);
        Assert.DoesNotContain(TatraClimbingRoutes.Massifs, massif => massif.Name == "Mniszek");
        Assert.True(TatraClimbingRoutes.Mniszek.Count >= 8, "Mniszek route data should be retained");
        foreach (TatraClimbingRoutes.ClimbingMassif massif in TatraClimbingRoutes.Massifs)
        {
            Assert.Equal(massif.Routes.Count, massif.Routes.Select(route => route.Name).Distinct().Count());
        }
    }

    [Fact]
    public void BuildWorldRoutes_should_translate_offsets_by_the_summit_position()
    {
        var summit = new Vector2(1000f, 2000f);

        IReadOnlyList<WorldClimbingRoute> world = TatraClimbingRoutes.BuildWorldRoutes(TatraClimbingRoutes.Mnich, summit);

        Assert.Equal(TatraClimbingRoutes.Mnich.Count, world.Count);
        for (int i = 0; i < world.Count; i++)
        {
            Vector2 expectedTop = summit + TatraClimbingRoutes.Mnich[i].OffsetsFromSummitMeters[^1];
            Assert.True(Vector2.Distance(world[i].PathXY[^1], expectedTop) < 1e-3f);
        }
    }

    [Fact]
    public void SnapToLocalMaximum_should_find_the_top_of_a_synthetic_tower()
    {
        var top = new Vector2(12f, -7f);
        float? Ground(Vector2 xy) => 2000f - (0.5f * Vector2.Distance(xy, top));

        Vector2 snapped = TatraClimbingRoutes.SnapToLocalMaximum(Ground, seedXY: Vector2.Zero, radiusMeters: 40f, stepMeters: 2f);

        Assert.True(Vector2.Distance(snapped, top) <= 2.5f, $"snapped {snapped} vs top {top}");
    }

    [Fact]
    public void SnapToLocalMaximum_should_stay_at_the_seed_on_flat_or_missing_ground()
    {
        Vector2 snapped = TatraClimbingRoutes.SnapToLocalMaximum(_ => null, seedXY: new Vector2(5f, 5f), radiusMeters: 40f, stepMeters: 2f);

        Assert.Equal(new Vector2(5f, 5f), snapped);
    }

    [Fact]
    public void SnapToLocalMaximum_should_not_jump_across_a_saddle_to_a_higher_neighbouring_slope()
    {
        // A tower (top 2068 m at the origin) standing next to a HIGHER massif slope across a saddle at
        // x=25 (the Mnich/Mniszek situation): the snap must climb the tower it was seeded on, never walk
        // through the col onto the neighbour that keeps rising.
        var towerTop = Vector2.Zero;
        float? Ground(Vector2 xy)
        {
            float tower = 2068f - (1.4f * Vector2.Distance(xy, towerTop));
            float massif = 2050f + (1.2f * (xy.X - 25f));
            return MathF.Max(tower, massif);
        }

        Vector2 snapped = TatraClimbingRoutes.SnapToLocalMaximum(Ground, seedXY: new Vector2(6f, 4f), radiusMeters: 40f, stepMeters: 2f);

        Assert.True(Vector2.Distance(snapped, towerTop) <= 2.5f, $"snapped {snapped}, expected the tower top {towerTop}");
    }

    [Fact]
    public void SnapToLocalMaximum_should_prefer_the_summit_matching_the_catalogued_elevation()
    {
        // The REAL Mnich failure: the seed samples the FOOT of the wall (~130 m below the top), and both
        // the tower top (2068) and the higher Mnichowa Kopa top (2090) are within the search radius.
        // With the catalogued elevation the snap must pick the 2068 tower, not the higher neighbour.
        var towerTop = new Vector2(-30f, 25f);
        var kopaTop = new Vector2(-40f, -70f);
        float? Ground(Vector2 xy)
        {
            float tower = 2068f - (2.0f * Vector2.Distance(xy, towerTop));
            float kopa = 2090f - (0.9f * Vector2.Distance(xy, kopaTop));
            return MathF.Max(tower, kopa);
        }

        Vector2 snapped = TatraClimbingRoutes.SnapToLocalMaximum(
            Ground, seedXY: Vector2.Zero, radiusMeters: 120f, stepMeters: 2f, targetElevationMeters: 2068f);

        Assert.True(Vector2.Distance(snapped, towerTop) <= 3f, $"snapped {snapped}, expected the tower top {towerTop}");
    }

    [Fact]
    public void SnapToLocalMaximum_should_widen_the_search_when_no_top_matches_inside_the_first_radius()
    {
        // The Mnich OSM node samples a featureless slope: NO prominent top within the first radius at
        // all — the tower top (2068 m) stands ~200 m away. With the catalogued elevation the snap must
        // fall back to a wider sweep and still land on the tower.
        var towerTop = new Vector2(150f, 140f);
        float? Ground(Vector2 xy)
        {
            float slope = 1900f + (0.3f * xy.X); // featureless rising slope around the seed
            float tower = 2068f - (1.8f * Vector2.Distance(xy, towerTop));
            return MathF.Max(slope, tower);
        }

        Vector2 snapped = TatraClimbingRoutes.SnapToLocalMaximum(
            Ground, seedXY: Vector2.Zero, radiusMeters: 120f, stepMeters: 2f, targetElevationMeters: 2068f);

        Assert.True(Vector2.Distance(snapped, towerTop) <= 5f, $"snapped {snapped}, expected the tower top {towerTop}");
    }
}