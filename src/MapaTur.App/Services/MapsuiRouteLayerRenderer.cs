using MapaTur.Domain.Geography;
using MapaTur.Domain.Routing;

using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;

using NetTopologySuite.Geometries;

using Color = Mapsui.Styles.Color;
using Map = Mapsui.Map;
using Pen = Mapsui.Styles.Pen;
using SymbolStyle = Mapsui.Styles.SymbolStyle;
using VectorStyle = Mapsui.Styles.VectorStyle;

namespace MapaTur.App.Services;

/// <summary>
/// Renders planned routes with a thick distinct stroke and waypoints as circular markers
/// on top of the trail layers.
/// </summary>
public sealed class MapsuiRouteLayerRenderer : IRouteLayerRenderer
{
    private const string RouteLayerName = "planned-route";
    private const string WaypointLayerName = "route-waypoints";
    private const float RouteStrokeWidthPixels = 6.0f;
    private const double WaypointMarkerRadius = 10.0;

    /// <inheritdoc />
    public void RenderRoute(Map map, Route route)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(route);

        RemoveLayer(map, RouteLayerName);

        var coordinates = route.ToPolyline()
            .Select(point => SphericalMercator.FromLonLat(point.Longitude, point.Latitude))
            .Select(projected => new Coordinate(projected.x, projected.y))
            .ToArray();

        if (coordinates.Length < 2)
        {
            return;
        }

        var lineString = new LineString(coordinates);
        var feature = new GeometryFeature(lineString);

        var layer = new MemoryLayer
        {
            Name = RouteLayerName,
            Features = [feature],
            Style = new VectorStyle
            {
                Line = new Pen(Color.FromString("#7C3AED"), RouteStrokeWidthPixels), // violet, distinct from PTTK palette
            },
        };

        map.Layers.Add(layer);
    }

    /// <inheritdoc />
    public void RenderWaypoints(Map map, IReadOnlyList<GeoPoint> waypoints)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(waypoints);

        RemoveLayer(map, WaypointLayerName);

        if (waypoints.Count == 0)
        {
            return;
        }

        var features = waypoints
            .Select(point => SphericalMercator.FromLonLat(point.Longitude, point.Latitude))
            .Select(projected => new PointFeature(projected.x, projected.y))
            .Cast<IFeature>()
            .ToList();

        var layer = new MemoryLayer
        {
            Name = WaypointLayerName,
            Features = features,
            Style = new SymbolStyle
            {
                Fill = new Mapsui.Styles.Brush(Color.FromString("#F59E0B")), // amber dot
                Outline = new Pen(Color.FromString("#1F2937"), 2.0f),
                SymbolScale = WaypointMarkerRadius / 16.0,
            },
        };

        map.Layers.Add(layer);
    }

    /// <inheritdoc />
    public void Clear(Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        RemoveLayer(map, RouteLayerName);
        RemoveLayer(map, WaypointLayerName);
    }

    private static void RemoveLayer(Map map, string name)
    {
        var existing = map.Layers.FirstOrDefault(layer => layer.Name == name);
        if (existing is not null)
        {
            map.Layers.Remove(existing);
        }
    }
}