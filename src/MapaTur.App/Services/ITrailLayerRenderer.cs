using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Renders a collection of hiking trails as colored polyline layers on a Mapsui map.
/// </summary>
public interface ITrailLayerRenderer
{
    /// <summary>
    /// When set, trail geometry is clipped to these bounds (the loaded map coverage) so trails don't
    /// trail off onto blank space past the basemap. Null draws full geometry.
    /// </summary>
    MapBounds? CoverageBounds { get; set; }

    /// <summary>
    /// Renders the given trails on the map, replacing any previously drawn trail layer.
    /// Each trail is coloured according to its primary PTTK marking.
    /// </summary>
    /// <param name="map">The map to mutate.</param>
    /// <param name="trails">Trails to draw.</param>
    void RenderTrails(Map map, IReadOnlyList<Trail> trails);
}