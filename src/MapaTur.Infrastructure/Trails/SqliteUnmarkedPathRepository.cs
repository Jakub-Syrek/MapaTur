using MapaTur.Application.Trails;
using MapaTur.Domain.Geography;
using MapaTur.Domain.Trails;

namespace MapaTur.Infrastructure.Trails;

/// <summary>
/// Magazyn nieznakowanych ścieżek OSM (perci) — osobny plik SQLite (<c>mapatur-paths.db</c>) o dokładnie
/// tym samym schemacie i zachowaniu co <see cref="SqliteTrailRepository"/> (delegacja 1:1). Osobny plik,
/// nie osobna tabela: szlaki znakowane i perci mają różne cykle życia (perci wolno wyczyścić/odświeżyć
/// bez dotykania szlaków), a rozdzielenie plików wyklucza pomyłkę, w której perć trafia do domyślnego
/// grafu planowania. Epsilon 0 jak w szlakach — geometria pełna, junctiony zachowane (lekcja żlebu).
/// </summary>
public sealed class SqliteUnmarkedPathRepository : IUnmarkedPathRepository, IDisposable
{
    private readonly SqliteTrailRepository inner;

    /// <summary>Otwiera (lub tworzy) magazyn perci pod wskazaną ścieżką pliku.</summary>
    public SqliteUnmarkedPathRepository(string databasePath)
        => inner = new SqliteTrailRepository(databasePath, simplificationEpsilonMeters: 0.0);

    /// <inheritdoc />
    public Task UpsertAsync(IEnumerable<Trail> trails, CancellationToken cancellationToken = default)
        => inner.UpsertAsync(trails, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Trail>> FindIntersectingAsync(MapBounds bounds, CancellationToken cancellationToken = default)
        => inner.FindIntersectingAsync(bounds, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Trail>> FindIntersectingAsync(MapBounds bounds, double simplificationEpsilonMeters, CancellationToken cancellationToken = default)
        => inner.FindIntersectingAsync(bounds, simplificationEpsilonMeters, cancellationToken);

    /// <inheritdoc />
    public Task<int> CountAsync(CancellationToken cancellationToken = default)
        => inner.CountAsync(cancellationToken);

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
        => inner.ClearAsync(cancellationToken);

    /// <inheritdoc />
    public void Dispose() => inner.Dispose();
}
