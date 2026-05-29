using MapaTur.Domain.Trails;

using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Renders a collection of hiking trails as colored polyline layers on a Mapsui map.
/// </summary>
public interface ITrailLayerRenderer
{
    /// <summary>
    /// Renders the given trails on the map, replacing any previously drawn trail layer.
    /// Each trail is coloured according to its primary PTTK marking.
    /// </summary>
    /// <param name="map">The map to mutate.</param>
    /// <param name="trails">Trails to draw.</param>
    void RenderTrails(Map map, IReadOnlyList<Trail> trails);
}