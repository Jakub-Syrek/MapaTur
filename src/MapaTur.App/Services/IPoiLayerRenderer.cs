using MapaTur.Domain.Pois;

using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Renders a collection of mountain POIs as marker overlays on a Mapsui map.
/// </summary>
public interface IPoiLayerRenderer
{
    /// <summary>
    /// Draws POI markers on the map, replacing any previously drawn POI layer.
    /// Markers are colour-coded by <see cref="PoiKind"/>.
    /// </summary>
    /// <param name="map">The map to mutate.</param>
    /// <param name="pois">POIs to draw.</param>
    void RenderPois(Map map, IReadOnlyList<MountainPoi> pois);

    /// <summary>Removes the POI marker layer, if present.</summary>
    /// <param name="map">The map to mutate.</param>
    void Clear(Map map);
}
