using MapaTur.Domain.Trails;

using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Renders road polylines (modelled as unmarked <see cref="Trail"/> geometry) as a Mapsui overlay,
/// kept separate from the hiking-trail layers.
/// </summary>
public interface IRoadLayerRenderer
{
    /// <summary>Draws road polylines on the map, replacing any previously drawn road layer.</summary>
    void RenderRoads(Map map, IReadOnlyList<Trail> roads);

    /// <summary>Removes the road layer, if present.</summary>
    void Clear(Map map);
}
