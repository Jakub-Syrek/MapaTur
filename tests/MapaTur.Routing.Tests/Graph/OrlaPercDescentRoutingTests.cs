using System.Text.Json;
using System.Text.Json.Serialization;

using FluentAssertions;

using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;
using MapaTur.Routing;
using MapaTur.Routing.Costs;
using MapaTur.Routing.Graph;

namespace MapaTur.Routing.Tests.Graph;

/// <summary>
/// Real-data routing check for the Żleb Kulczyńskiego descent. A user reported that planning a route which
/// crosses several differently-coloured trails (descend the BLACK żleb onto the GREEN valley trail, then onto
/// the YELLOW trail at Zmarzły Staw Gąsienicowy) seemed unsupported — the green trail visibly stops short of
/// the yellow in the 3D overlay. The overlay simplifies geometry (≈20 m) so it can show a misleading gap, but
/// the ROUTING graph is built from the FULL geometry. This pins that the graph actually connects these real
/// OSM junctions, so the engine CAN route across the red ridge → black żleb → green → yellow chain.
///
/// Geometry is a fixture captured once from the OSM relation member ways (Overpass <c>out geom</c>):
/// red 3349371/3349373 (Orla Perć), black 1593577 (Żleb Kulczyńskiego), green 1593517 (Za Zmarzłym Stawem –
/// Zadni Granat), yellow 3353422/1593579. If OSM changes the junctions this fixture must be refreshed.
/// </summary>
public sealed class OrlaPercDescentRoutingTests
{
    // Real points sampled from each coloured trail in the fixture.
    private static readonly GeoPoint RedRidge = new(49.226958, 20.033232);
    private static readonly GeoPoint BlackZleb = new(49.221238, 20.029865);
    private static readonly GeoPoint GreenTrail = new(49.223599, 20.029431);
    private static readonly GeoPoint YellowAtZmarzly = new(49.222449, 20.025096);

    private static TrailGraph BuildGraph()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "orla-perc-descent.json");
        string json = File.ReadAllText(path);
        FixtureWay[] ways = JsonSerializer.Deserialize<FixtureWay[]>(json)!;

        var trails = new List<Trail>(ways.Length);
        long id = 1;
        foreach (FixtureWay w in ways)
        {
            var geometry = new List<GeoPoint>(w.Points.Length);
            foreach (double[] p in w.Points)
            {
                geometry.Add(new GeoPoint(p[0], p[1]));
            }

            trails.Add(new Trail(id++, w.Colour, new[] { new TrailMarking(PttkColor.None) }, geometry));
        }

        return TrailGraph.Build(trails);
    }

    private static bool IsRoutable(TrailGraph graph, GeoPoint from, GeoPoint to)
    {
        var router = new AStarRouter(graph);
        return router.FindPath(graph.FindNearestNode(from), graph.FindNearestNode(to), new DistanceCostFunction()) is not null;
    }

    [Fact]
    public void GreenValleyTrail_ConnectsToYellow_AtZmarzlyStaw()
        => IsRoutable(BuildGraph(), GreenTrail, YellowAtZmarzly).Should().BeTrue("green and yellow share an OSM node — the 3D-overlay gap is cosmetic");

    [Fact]
    public void BlackZleb_ConnectsToGreenValleyTrail()
        => IsRoutable(BuildGraph(), BlackZleb, GreenTrail).Should().BeTrue();

    [Fact]
    public void RedRidge_DescendsViaZleb_OntoYellow_AcrossSeveralTrails()
        => IsRoutable(BuildGraph(), RedRidge, YellowAtZmarzly).Should().BeTrue("the engine must route a single path spanning red → black → green → yellow");

    private sealed record FixtureWay(
        [property: JsonPropertyName("colour")] string Colour,
        [property: JsonPropertyName("points")] double[][] Points);
}