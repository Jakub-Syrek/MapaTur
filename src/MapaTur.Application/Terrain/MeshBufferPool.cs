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

    private static void Push<T>(Dictionary<int, Stack<T[]>> buckets, int length, T[] buffer)
    {
        if (!buckets.TryGetValue(length, out Stack<T[]>? s))
        {
            s = new Stack<T[]>();
            buckets[length] = s;
        }

        if (s.Count < MaxPerBucket)
        {
            s.Push(buffer);
        }
    }
}