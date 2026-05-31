using MapaTur.Domain.Location;

using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Renders the current GPS fix as a single point + accuracy halo on a Mapsui map.
/// </summary>
public interface IUserLocationLayerRenderer
{
    /// <summary>
    /// Draws (or replaces) the user-location marker. A null fix clears the marker.
    /// </summary>
    /// <param name="map">The map to mutate.</param>
    /// <param name="location">Current fix, or null to clear.</param>
    void RenderUserLocation(Map map, UserLocation? location);

    /// <summary>Removes the user-location marker layer, if present.</summary>
    /// <param name="map">The map to mutate.</param>
    void Clear(Map map);
}