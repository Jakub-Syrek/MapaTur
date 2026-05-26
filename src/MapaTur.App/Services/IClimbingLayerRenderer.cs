using MapaTur.Domain.Climbing;
using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Renders a collection of climbing areas as marker overlays on a Mapsui map.
/// </summary>
public interface IClimbingLayerRenderer
{
    /// <summary>
    /// Draws climbing markers on the map, replacing any previously drawn climbing layer.
    /// Markers are colour-coded by <see cref="ClimbingType"/>.
    /// </summary>
    /// <param name="map">The map to mutate.</param>
    /// <param name="areas">Climbing areas to draw.</param>
    void RenderClimbingAreas(Map map, IReadOnlyList<ClimbingArea> areas);

    /// <summary>Removes the climbing marker layer, if present.</summary>
    /// <param name="map">The map to mutate.</param>
    void Clear(Map map);
}
