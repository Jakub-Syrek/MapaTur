namespace MapaTur.Application.Terrain;

/// <summary>Wynik decyzji <see cref="GlBufferPoolPolicy{T}.Acquire"/>: <see cref="Reused"/> = jednostka z puli
/// (wypełnij BufferSubData), null = utwórz nową o pojemnościach <see cref="VertexCap"/>/<see cref="IndexCap"/>
/// (BufferData z pełną pojemnością RAZ, potem już tylko SubData).</summary>
public readonly struct GlPoolAcquire<T>
    where T : class
{
    public T? Reused { get; init; }

    public int VertexCap { get; init; }

    public int IndexCap { get; init; }
}

/// <summary>
/// Polityka poolingu jednostek buforów GL (P0 2026-08-06). Zmierzone tło (08-02, dev/p0-morning):
/// strumień Gen/Delete + BufferData tysięcy NIEPOWTARZALNYCH rozmiarów podczas lotu zostawia osad w pulach
/// ANGLE/D3D11/sterownika — commit D3D 8,4→16,9 GB przy stałych licznikach GlTrack, +1 GB ws na lot F9.
/// Lek (wzorzec z fixu masek, Terrain3DGlRenderer.EnsureImmutableMaskStorage): pojemności z DRABINKI KLAS
/// (kroki ≤×1,25 — rozmiary powtarzalne), alokacja jednostki raz, aktualizacje wyłącznie SubData, a zwolniona
/// jednostka wraca tu zamiast do DeleteBuffer. Klasa podejmuje wyłącznie decyzje (zero wywołań GL) — właściciel
/// (renderer, wątek GL) wykonuje faktyczne create/fill/delete. Jednowątkowa jak cały stan renderera.
/// </summary>
public sealed class GlBufferPoolPolicy<T>
    where T : class
{
    private const int MinCap = 64;

    private readonly int bytesPerVertex;
    private readonly int bytesPerIndex;
    private readonly long maxFreeBytes;

    // Koszyki po DOKŁADNEJ parze pojemności z drabinki (LIFO — najcieplejsza jednostka pierwsza) +
    // globalna lista wieku do eksmisji LRU ponad budżet bajtowy.
    private readonly Dictionary<(int VertexCap, int IndexCap), Stack<T>> free = new();
    private readonly LinkedList<(T Unit, int VertexCap, int IndexCap)> age = new();
    private readonly Dictionary<T, LinkedListNode<(T Unit, int VertexCap, int IndexCap)>> ageNodes = new();

    public GlBufferPoolPolicy(int bytesPerVertex, int bytesPerIndex, long maxFreeBytes)
    {
        this.bytesPerVertex = bytesPerVertex;
        this.bytesPerIndex = bytesPerIndex;
        this.maxFreeBytes = maxFreeBytes;
    }

    /// <summary>Bajty jednostek czekających w puli (pojemności, nie payloady).</summary>
    public long FreeBytes { get; private set; }

    /// <summary>Liczba jednostek czekających w puli.</summary>
    public int FreeCount => ageNodes.Count;

    /// <summary>Acquire trafił w wolną jednostkę (reuse bez żadnej alokacji sterownika).</summary>
    public long Hits { get; private set; }

    /// <summary>Acquire wymagał utworzenia nowej jednostki.</summary>
    public long Misses { get; private set; }

    /// <summary>Jednostki wskazane do skasowania przez budżet bajtowy.</summary>
    public long Evictions { get; private set; }

    /// <summary>Najbliższa pojemność z drabinki klas ≥ <paramref name="count"/>. Kroki ≤×1,25 od 64 —
    /// pojemności POWTARZALNE między kaflami, więc sterownik widzi kilkadziesiąt rozmiarów zamiast tysięcy.</summary>
    public static int RoundUpCap(int count)
    {
        int cap = MinCap;
        while (cap < count)
        {
            cap += cap / 4;
        }

        return cap;
    }

    /// <summary>Decyzja dla żądania (vertexCount, indexCount): reuse jednostki z koszyka dokładnie tej klasy
    /// albo polecenie utworzenia nowej. Wydana jednostka znika z puli (nigdy dwóch właścicieli).</summary>
    public GlPoolAcquire<T> Acquire(int vertexCount, int indexCount)
    {
        int vertexCap = RoundUpCap(vertexCount);
        int indexCap = RoundUpCap(indexCount);

        if (free.TryGetValue((vertexCap, indexCap), out Stack<T>? bucket) && bucket.Count > 0)
        {
            T unit = bucket.Pop();
            LinkedListNode<(T Unit, int VertexCap, int IndexCap)> node = ageNodes[unit];
            age.Remove(node);
            ageNodes.Remove(unit);
            FreeBytes -= UnitBytes(vertexCap, indexCap);
            Hits++;
            return new GlPoolAcquire<T> { Reused = unit, VertexCap = vertexCap, IndexCap = indexCap };
        }

        Misses++;
        return new GlPoolAcquire<T> { Reused = null, VertexCap = vertexCap, IndexCap = indexCap };
    }

    /// <summary>Oddaje jednostkę do puli. Zwraca jednostki do FAKTYCZNEGO skasowania (DeleteBuffer) —
    /// najstarsze ponad budżet bajtowy; zwykle pusta lista. Pojemności muszą być tymi z Acquire.</summary>
    public IReadOnlyList<T> Release(T unit, int vertexCap, int indexCap)
    {
        if (!free.TryGetValue((vertexCap, indexCap), out Stack<T>? bucket))
        {
            bucket = new Stack<T>();
            free[(vertexCap, indexCap)] = bucket;
        }

        bucket.Push(unit);
        ageNodes[unit] = age.AddLast((unit, vertexCap, indexCap));
        FreeBytes += UnitBytes(vertexCap, indexCap);

        if (FreeBytes <= maxFreeBytes)
        {
            return Array.Empty<T>();
        }

        var evicted = new List<T>();
        while (FreeBytes > maxFreeBytes && age.First is not null)
        {
            (T oldUnit, int vc, int ic) = age.First.Value;
            age.RemoveFirst();
            ageNodes.Remove(oldUnit);
            RemoveFromBucket(oldUnit, vc, ic);
            FreeBytes -= UnitBytes(vc, ic);
            Evictions++;
            evicted.Add(oldUnit);
        }

        return evicted;
    }

    /// <summary>Teardown na ŻYWYM kontekście: przenosi wszystkie wolne jednostki do <paramref name="into"/>
    /// (właściciel je kasuje) i zeruje pulę. Odpowiednik Reset(), ale ze spisem do DeleteBuffer.</summary>
    public void DrainTo(ICollection<T> into)
    {
        foreach ((T unit, _, _) in age)
        {
            into.Add(unit);
        }

        Reset();
    }

    /// <summary>Utrata kontekstu GL: uchwyty umarły razem z kontekstem — zapomnij wszystko BEZ zwracania
    /// jednostek do kasowania (DeleteBuffer na martwym kontekście to błąd; wzorzec jak zerowanie ID w rendererze).</summary>
    public void Reset()
    {
        free.Clear();
        age.Clear();
        ageNodes.Clear();
        FreeBytes = 0;
    }

    private long UnitBytes(int vertexCap, int indexCap)
        => ((long)bytesPerVertex * vertexCap) + ((long)bytesPerIndex * indexCap);

    private void RemoveFromBucket(T unit, int vertexCap, int indexCap)
    {
        if (!free.TryGetValue((vertexCap, indexCap), out Stack<T>? bucket))
        {
            return;
        }

        // Eksmisja LRU trafia w SPÓD stosu LIFO — przepakowanie przez bufor tymczasowy zachowuje kolejność reszty.
        var keep = new Stack<T>(bucket.Count);
        while (bucket.Count > 0)
        {
            T candidate = bucket.Pop();
            if (!ReferenceEquals(candidate, unit))
            {
                keep.Push(candidate);
            }
        }

        while (keep.Count > 0)
        {
            bucket.Push(keep.Pop());
        }
    }
}