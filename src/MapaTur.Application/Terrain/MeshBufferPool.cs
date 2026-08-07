using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Reuses the large per-tile vertex buffers (positions, normals, colours, UVs) so that streaming detail does NOT
/// churn the Large Object Heap. Each rebuilt tile allocates ~2.5 MB of arrays; during a flight ~170 tiles rebuild
/// per reload (the same ground re-meshed under a shifted LOD key), so the heap ballooned to ~15 GB and the GC
/// stalled ~1.5 s. These arrays are write-once by the build and read ONCE by the renderer's tile upload — nothing
/// reads them afterwards — so a tile returns its buffers to this pool right after the GL upload, and the next
/// rebuild rents them back instead of allocating. Buffers are pooled by EXACT length so a rented array's
/// <c>Length</c> stays the true vertex count. Thread-safe: the build worker rents while the render thread returns.
/// </summary>
public sealed class MeshBufferPool
{
    /// <summary>Process-wide shared pool (one terrain renderer; buffers are interchangeable across tiles).</summary>
    public static readonly MeshBufferPool Shared = new();

    /// <summary>Kill-switch. When false, <c>Rent*</c> always allocates and <c>Return</c> is a no-op (legacy behaviour).</summary>
    public static bool Enabled { get; set; } = true;

    private const int MaxPerBucket = 4096; // hard cap so a pathological size can't grow the pool without bound

    private readonly Dictionary<int, Stack<Vector3[]>> vec3 = new();
    private readonly Dictionary<int, Stack<uint[]>> u32 = new();
    private readonly Dictionary<int, Stack<float[]>> f32 = new();
    private readonly Dictionary<int, Stack<byte[]>> u8 = new();
    private readonly object gate = new();

    /// <summary>Rents a <see cref="Vector3"/> array of EXACTLY <paramref name="length"/> (reused or freshly allocated).</summary>
    public Vector3[] RentVector3(int length)
    {
        if (Enabled)
        {
            lock (gate)
            {
                if (vec3.TryGetValue(length, out Stack<Vector3[]>? s) && s.Count > 0)
                {
                    return s.Pop();
                }
            }
        }

        return new Vector3[length];
    }

    /// <summary>Rents a <see cref="uint"/> array of EXACTLY <paramref name="length"/>.</summary>
    public uint[] RentUInt32(int length)
    {
        if (Enabled)
        {
            lock (gate)
            {
                if (u32.TryGetValue(length, out Stack<uint[]>? s) && s.Count > 0)
                {
                    return s.Pop();
                }
            }
        }

        return new uint[length];
    }

    /// <summary>Rents a <see cref="float"/> array of EXACTLY <paramref name="length"/>.</summary>
    public float[] RentSingle(int length)
    {
        if (Enabled)
        {
            lock (gate)
            {
                if (f32.TryGetValue(length, out Stack<float[]>? s) && s.Count > 0)
                {
                    return s.Pop();
                }
            }
        }

        return new float[length];
    }

    /// <summary>Rents a <see cref="byte"/> array of EXACTLY <paramref name="length"/>. Used for the huge ortho
    /// detail cell buffers (64 MiB det25 / 256 MiB det05) whose per-compose allocation was gigabytes of LOH
    /// churn per traverse; only a handful circulate (composes in flight + pending uploads), so the retained
    /// set stays tiny. The buffer arrives DIRTY — the compose path clears it (its hole semantics require it).</summary>
    public byte[] RentBytes(int length)
    {
        if (Enabled)
        {
            lock (gate)
            {
                if (u8.TryGetValue(length, out Stack<byte[]>? s) && s.Count > 0)
                {
                    byte[] taken = s.Pop();
                    pooledByteBytes -= taken.Length;
                    return taken;
                }
            }
        }

        return new byte[length];
    }

    /// <summary>Górny pułap BAJTÓW trzymanych w puli <c>byte[]</c> (2026-07-25). Pula miała limit LICZBY
    /// buforów (4096/koszyk), ale nie ich rozmiaru — a łańcuch BC1 celi det05 to ~43 MB. Po podniesieniu
    /// zasięgu do 192 cel oznaczało to ~8,2 GB kopii CPU trzymanych na stałe (zmierzone: sterta zarządzana
    /// 11-15 GB w locie i rosnący licznik gen2). Kopia po stronie CPU jest potrzebna WYŁĄCZNIE na czas
    /// uploadu — po nim dane żyją w teksturze. Pooling zostaje dla buforów, które faktycznie krążą
    /// (strony, scratch zstd, siatki); nadmiar oddajemy GC zamiast trzymać w nieskończoność.</summary>
    private const long MaxPooledByteBytes = 512L << 20;

    private long pooledByteBytes;

    /// <summary>Bajty aktualnie trzymane w puli <c>byte[]</c> — do logu pamięci.</summary>
    public long PooledByteBytes
    {
        get { lock (this.gate) { return this.pooledByteBytes; } }
    }

    /// <summary>Returns a buffer for reuse. See <see cref="Return(Vector3[])"/>.</summary>
    public void Return(byte[]? buffer)
    {
        if (!Enabled || buffer is null || buffer.Length == 0)
        {
            return;
        }

        lock (gate)
        {
            if (pooledByteBytes + buffer.Length > MaxPooledByteBytes)
            {
                return; // ponad budżet — oddajemy GC zamiast trzymać (patrz MaxPooledByteBytes)
            }

            if (Push(u8, buffer.Length, buffer))
            {
                pooledByteBytes += buffer.Length;
            }
        }
    }

    /// <summary>Returns a buffer for reuse. Null / empty is ignored. Caller must not touch the array afterwards.</summary>
    public void Return(Vector3[]? buffer)
    {
        if (!Enabled || buffer is null || buffer.Length == 0)
        {
            return;
        }

        lock (gate)
        {
            Push(vec3, buffer.Length, buffer);
        }
    }

    /// <summary>Returns a buffer for reuse. See <see cref="Return(Vector3[])"/>.</summary>
    public void Return(uint[]? buffer)
    {
        if (!Enabled || buffer is null || buffer.Length == 0)
        {
            return;
        }

        lock (gate)
        {
            Push(u32, buffer.Length, buffer);
        }
    }

    /// <summary>Returns a buffer for reuse. See <see cref="Return(Vector3[])"/>.</summary>
    public void Return(float[]? buffer)
    {
        if (!Enabled || buffer is null || buffer.Length == 0)
        {
            return;
        }

        lock (gate)
        {
            Push(f32, buffer.Length, buffer);
        }
    }

    private static bool Push<T>(Dictionary<int, Stack<T[]>> buckets, int length, T[] buffer)
    {
        if (!buckets.TryGetValue(length, out Stack<T[]>? s))
        {
            s = new Stack<T[]>();
            buckets[length] = s;
        }

        // Guard duplikatów (2026-08-07, regresja „płaskie cieniowanie"): podwójny Return tej samej
        // tablicy (np. drugi UploadTile tej samej referencji kafla po utracie kontekstu) kładł ją w
        // koszyku DWA razy — dwóch najemców pisało po jednej pamięci i nadpisywało sobie normalne/UV.
        // Contains po referencji; koszyki są krótkie (typowo dziesiątki), wołane poza gorącą pętlą klatki.
        if (s.Count >= MaxPerBucket || s.Contains(buffer))
        {
            return false;
        }

        s.Push(buffer);
        return true;
    }
}