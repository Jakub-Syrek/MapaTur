using MapaTur.Domain.Tracks;

using Map = Mapsui.Map;

namespace MapaTur.App.Services;

/// <summary>
/// Renders imported <see cref="Track"/> aggregates as polyline layers on a Mapsui map.
/// </summary>
public interface ITrackLayerRenderer
{
    /// <summary>
    /// Draws the given track on the map, replacing any previously drawn track layer with
    /// the same identifier. Pans the viewport so the track is fully visible.
    /// </summary>
    /// <param name="map">The map to mutate.</param>
    /// <param name="track">The track to render.</param>
    void RenderTrack(Map map, Track track);
}