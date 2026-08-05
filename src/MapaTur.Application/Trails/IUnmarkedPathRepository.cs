namespace MapaTur.Application.Trails;

/// <summary>
/// Magazyn NIEZNAKOWANYCH ścieżek OSM (perci — ways bez koloru i bez relacji szlaku), trzymany OSOBNO od
/// szlaków znakowanych, żeby żadna perć nigdy nie weszła do domyślnego grafu planowania. Planer sięga tu
/// wyłącznie pod flagą pozaszlaków (<c>RouteRequest.IncludeOffTrailTracks</c>), z karą kosztu — patrz
/// <c>TrailRoutePlanner</c>. Interfejs-marker: kontrakt identyczny z <see cref="ITrailRepository"/>,
/// osobny typ istnieje tylko po to, by DI dało się wstrzyknąć dwa różne magazyny naraz.
/// </summary>
public interface IUnmarkedPathRepository : ITrailRepository;
