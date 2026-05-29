using MapaTur.Domain.Tracks;

using Mapsui;
using Mapsui.Layers;
using Mapsui.Nts;
using Mapsui.Projections;

using NetTopologySuite.Geometries;

using Color = Mapsui.Styles.Color;
using Map = Mapsui.Map;
using Pen = Mapsui.Styles.Pen;
using VectorStyle = Mapsui.Styles.VectorStyle;

namespace MapaTur.App.Services;

/// <summary>
/// Renders tracks using Mapsui memory layers and NTS geometries projected from WGS-84
/// to Spherical Mercator (Mapsui's native coordinate system for raster tiles).
/// </summary>
public sealed class MapsuiTrackLayerRenderer : ITrackLayerRenderer
{
    private const string TrackLayerName = "imported-track";
    private const float StrokeWidthPixels = 4.0f;

    /// <inheritdoc />
    public void RenderTrack(Map map, Track track)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(track);

        RemoveExistingTrackLayer(map);

        var projectedCoordinates = track.Points
            .Select(point => SphericalMercator.FromLonLat(point.Position.Longitude, point.Position.Latitude))
            .Select(projected => new Coordinate(projected.x, projected.y))
            .ToArray();

        if (projectedCoordinates.Length < 2)
        {
            return;
        }

        var lineString = new LineString(projectedCoordinates);
        var feature = new GeometryFeature(lineString);

        var layer = new MemoryLayer
        {
            Name = TrackLayerName,
            Features = [feature],
            Style = new VectorStyle
            {
                Line = new Pen(Color.FromString("#E11D48"), StrokeWidthPixels),
            },
        };

        map.Layers.Add(layer);

        if (lineString.EnvelopeInternal is { IsNull: false } envelope)
        {
            map.Navigator.ZoomToBox(new MRect(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY));
        }
    }

    private static void RemoveExistingTrackLayer(Map map)
    {
        var existing = map.Layers.FirstOrDefault(layer => layer.Name == TrackLayerName);
        if (existing is not null)
        {
            map.Layers.Remove(existing);
        }
    }
}