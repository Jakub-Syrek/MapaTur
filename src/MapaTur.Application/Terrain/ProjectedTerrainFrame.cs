using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Output of <see cref="Terrain3DProjection.Project(TerrainMesh3D, Camera3D, float, float)"/>:
/// per-vertex screen positions and a triangle index buffer with backfaces culled and
/// triangles sorted back-to-front.
/// </summary>
/// <remarks>
/// When produced by the allocating overload, <see cref="VertexCount"/> equals
/// <c>ScreenVertices.Length</c> and <see cref="VisibleIndexCount"/> equals
/// <c>VisibleIndices.Length</c>. When produced by a scratch-buffer overload, the arrays
/// may be larger than the valid prefix; iterate up to the counts only.
/// </remarks>
public readonly struct ProjectedTerrainFrame
{
    /// <summary>
    /// Buffer holding per-vertex screen positions for indices <c>[0, VertexCount)</c>.
    /// (X, Y) are pixel coordinates; Z is NDC depth in [0,1]. Off-frustum vertices are
    /// marked with <see cref="float.NaN"/>.
    /// </summary>
    public Vector3[] ScreenVertices { get; }

    /// <summary>Buffer holding the visible triangle index stream for indices <c>[0, VisibleIndexCount)</c>.</summary>
    public ushort[] VisibleIndices { get; }

    /// <summary>Valid prefix of <see cref="ScreenVertices"/>.</summary>
    public int VertexCount { get; }

    /// <summary>Valid prefix of <see cref="VisibleIndices"/>. Always a multiple of 3.</summary>
    public int VisibleIndexCount { get; }

    /// <summary>
    /// Constructs a frame whose count fields equal the array lengths (i.e. the arrays
    /// are sized exactly to the valid data — used by the allocating projector overload).
    /// </summary>
    public ProjectedTerrainFrame(Vector3[] screenVertices, ushort[] visibleIndices)
        : this(screenVertices, visibleIndices, screenVertices.Length, visibleIndices.Length)
    {
    }

    /// <summary>
    /// Constructs a frame that points at potentially larger scratch buffers; callers
    /// iterate <see cref="ScreenVertices"/>/<see cref="VisibleIndices"/> up to the
    /// provided counts only.
    /// </summary>
    public ProjectedTerrainFrame(Vector3[] screenVertices, ushort[] visibleIndices, int vertexCount, int visibleIndexCount)
    {
        ScreenVertices = screenVertices;
        VisibleIndices = visibleIndices;
        VertexCount = vertexCount;
        VisibleIndexCount = visibleIndexCount;
    }
}