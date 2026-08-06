namespace MapaTur.Application.Terrain;

/// <summary>
/// Polityka puli tekstur staging (P0 2026-08-06) — bliźniak <see cref="GlBufferPoolPolicy{T}"/> dla tekstur
/// o DOKŁADNYM (W,H): tekstura immutable (TexStorage2D) nie zmienia rozmiaru, więc reuse ma sens tylko przy
/// identycznych wymiarach. Motywacja zmierzona: tier bazy orto flipuje celę near↔far, a delete+create
/// mutable TexImage2D przy każdym flipie to ten sam wzorzec, który na maskach szlaków/wody zostawiał
/// ~1 GB/lot osadu ANGLE/D3D11 (fix 08-02: EnsureImmutableMaskStorage). Czysta logika decyzji — zero GL;
/// właściciel wykonuje Gen/TexStorage2D/Delete na wątku GL.
/// </summary>
public sealed class GlTexturePoolPolicy<T>
    where T : class
{
    private readonly int bytesPerPixel;
    private readonly long maxFreeBytes;

    private readonly Dictionary<(int W, int H), Stack<T>> free = new();
    private readonly LinkedList<(T Tex, int W, int H)> age = new();
    private readonly Dictionary<T, LinkedListNode<(T Tex, int W, int H)>> ageNodes = new();

    public GlTexturePoolPolicy(int bytesPerPixel, long maxFreeBytes)
    {
        this.bytesPerPixel = bytesPerPixel;
        this.maxFreeBytes = maxFreeBytes;
    }

    /// <summary>Bajty tekstur czekających w puli (z narzutem mipów ×4/3 — ta sama księgowość co [Mem]).</summary>
    public long FreeBytes { get; private set; }

    public int FreeCount => ageNodes.Count;

    public long Hits { get; private set; }

    public long Misses { get; private set; }

    public long Evictions { get; private set; }

    /// <summary>Szacunek bajtów tekstury W×H z pełnym łańcuchem mipów (×4/3).</summary>
    public static long EstimateBytes(int width, int height, int bytesPerPixel)
        => (long)width * height * bytesPerPixel * 4 / 3;

    /// <summary>Tekstura z puli o dokładnie tym (W,H) — najświeższa pierwsza — albo null (utwórz nową).</summary>
    public T? Acquire(int width, int height)
    {
        if (free.TryGetValue((width, height), out Stack<T>? bucket) && bucket.Count > 0)
        {
            T tex = bucket.Pop();
            age.Remove(ageNodes[tex]);
            ageNodes.Remove(tex);
            FreeBytes -= EstimateBytes(width, height, bytesPerPixel);
            Hits++;
            return tex;
        }

        Misses++;
        return null;
    }

    /// <summary>Oddaje teksturę do puli. Zwraca tekstury do FAKTYCZNEGO skasowania (najstarsze ponad budżet).</summary>
    public IReadOnlyList<T> Release(T tex, int width, int height)
    {
        if (!free.TryGetValue((width, height), out Stack<T>? bucket))
        {
            bucket = new Stack<T>();
            free[(width, height)] = bucket;
        }

        bucket.Push(tex);
        ageNodes[tex] = age.AddLast((tex, width, height));
        FreeBytes += EstimateBytes(width, height, bytesPerPixel);

        if (FreeBytes <= maxFreeBytes)
        {
            return Array.Empty<T>();
        }

        var evicted = new List<T>();
        while (FreeBytes > maxFreeBytes && age.First is not null)
        {
            (T oldTex, int w, int h) = age.First.Value;
            age.RemoveFirst();
            ageNodes.Remove(oldTex);
            RemoveFromBucket(oldTex, w, h);
            FreeBytes -= EstimateBytes(w, h, bytesPerPixel);
            Evictions++;
            evicted.Add(oldTex);
        }

        return evicted;
    }

    /// <summary>Teardown na ŻYWYM kontekście: przenosi wszystkie wolne tekstury do <paramref name="into"/>
    /// (właściciel je kasuje) i zeruje pulę.</summary>
    public void DrainTo(ICollection<T> into)
    {
        foreach ((T tex, _, _) in age)
        {
            into.Add(tex);
        }

        Reset();
    }

    /// <summary>Utrata kontekstu GL: zapomnij wszystko bez zwracania tekstur do kasowania.</summary>
    public void Reset()
    {
        free.Clear();
        age.Clear();
        ageNodes.Clear();
        FreeBytes = 0;
    }

    private void RemoveFromBucket(T tex, int width, int height)
    {
        if (!free.TryGetValue((width, height), out Stack<T>? bucket))
        {
            return;
        }

        var keep = new Stack<T>(bucket.Count);
        while (bucket.Count > 0)
        {
            T candidate = bucket.Pop();
            if (!ReferenceEquals(candidate, tex))
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