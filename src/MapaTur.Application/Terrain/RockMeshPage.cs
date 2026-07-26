using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// One offline-baked, GPU-ready page of steep-rock geometry. The vertex block already uses the packed
/// 16-byte runtime layout; loading a page requires only an asynchronous read and buffer upload.
/// </summary>
public sealed class RockMeshPage
{
    /// <summary>Size of one packed vertex in the RMP1 format.</summary>
    public const int VertexStrideBytes = 16;

    /// <summary>Largest vertex count addressable by the page's ushort index buffer.</summary>
    public const int MaxVertices = ushort.MaxValue;

    /// <summary>Creates and validates a rock-mesh page.</summary>
    public RockMeshPage(
        byte lod,
        int pageX,
        int pageY,
        Vector3 worldMin,
        Vector3 worldExtent,
        float geometricError,
        byte[] vertexData,
        ushort[] indices)
    {
        ArgumentNullException.ThrowIfNull(vertexData);
        ArgumentNullException.ThrowIfNull(indices);
        if (lod > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(lod), "Rock mesh LOD must be in the 0..2 range.");
        }

        if (vertexData.Length % VertexStrideBytes != 0)
        {
            throw new ArgumentException(
                $"Vertex data length must be a multiple of {VertexStrideBytes}.",
                nameof(vertexData));
        }

        int vertexCount = vertexData.Length / VertexStrideBytes;
        if (vertexCount > MaxVertices)
        {
            throw new ArgumentException(
                $"A rock page can contain at most {MaxVertices} vertices.",
                nameof(vertexData));
        }

        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException("The index block must contain complete triangles.", nameof(indices));
        }

        if (indices.Any(index => index >= vertexCount))
        {
            throw new ArgumentException("An index points outside the vertex block.", nameof(indices));
        }

        if (!IsFinitePositive(worldExtent.X)
            || !IsFinitePositive(worldExtent.Y)
            || !IsFinitePositive(worldExtent.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(worldExtent), "Page extents must be finite and positive.");
        }

        if (!float.IsFinite(geometricError) || geometricError < 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(geometricError));
        }

        Lod = lod;
        PageX = pageX;
        PageY = pageY;
        WorldMin = worldMin;
        WorldExtent = worldExtent;
        GeometricError = geometricError;
        VertexData = vertexData;
        Indices = indices;
    }

    public byte Lod { get; }
    public int PageX { get; }
    public int PageY { get; }
    public Vector3 WorldMin { get; }
    public Vector3 WorldExtent { get; }
    public float GeometricError { get; }
    public byte[] VertexData { get; }
    public ushort[] Indices { get; }
    public int VertexCount => VertexData.Length / VertexStrideBytes;
    public int IndexCount => Indices.Length;

    private static bool IsFinitePositive(float value) => float.IsFinite(value) && value > 0f;
}
