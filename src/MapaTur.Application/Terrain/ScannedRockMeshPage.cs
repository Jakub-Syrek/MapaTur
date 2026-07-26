using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// One GPU-ready RMP2 page of real photogrammetric geometry. The 20-byte vertex adds UV and a material
/// binding to the RMP1 position/normal layout, so the scan's structure is never replaced by projected ortho.
/// </summary>
public sealed class ScannedRockMeshPage
{
    public const int VertexStrideBytes = 20;
    public const int MaxVertices = ushort.MaxValue;

    public ScannedRockMeshPage(
        byte lod,
        int pageX,
        int pageY,
        Vector3 worldMin,
        Vector3 worldExtent,
        float geometricError,
        ushort materialPageId,
        byte[] vertexData,
        ushort[] indices)
    {
        ArgumentNullException.ThrowIfNull(vertexData);
        ArgumentNullException.ThrowIfNull(indices);
        if (lod > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(lod));
        }

        if (vertexData.Length == 0 || vertexData.Length % VertexStrideBytes != 0)
        {
            throw new ArgumentException(
                $"RMP2 vertex data must be a non-empty multiple of {VertexStrideBytes}.",
                nameof(vertexData));
        }

        int vertexCount = vertexData.Length / VertexStrideBytes;
        if (vertexCount > MaxVertices)
        {
            throw new ArgumentException($"An RMP2 page can contain at most {MaxVertices} vertices.", nameof(vertexData));
        }

        if (indices.Length == 0 || indices.Length % 3 != 0 || indices.Any(index => index >= vertexCount))
        {
            throw new ArgumentException("RMP2 indices must contain valid complete triangles.", nameof(indices));
        }

        if (!IsFinite(worldMin)
            || !IsFinitePositive(worldExtent.X)
            || !IsFinitePositive(worldExtent.Y)
            || !IsFinitePositive(worldExtent.Z))
        {
            throw new ArgumentOutOfRangeException(nameof(worldExtent));
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
        MaterialPageId = materialPageId;
        VertexData = vertexData;
        Indices = indices;
    }

    public byte Lod { get; }
    public int PageX { get; }
    public int PageY { get; }
    public Vector3 WorldMin { get; }
    public Vector3 WorldExtent { get; }
    public float GeometricError { get; }
    public ushort MaterialPageId { get; }
    public byte[] VertexData { get; }
    public ushort[] Indices { get; }
    public int VertexCount => VertexData.Length / VertexStrideBytes;
    public int IndexCount => Indices.Length;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinitePositive(float value) => float.IsFinite(value) && value > 0f;
}
