using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Reusable per-frame buffers for <see cref="Terrain3DProjection.Project(TerrainMesh3D, Camera3D, float, float, Terrain3DFrameScratch)"/>.
/// Hold one instance per viewport (long-lived) and pass it back every frame to
/// avoid the ~1 MB of GC churn an allocate-per-frame projector causes on a
/// 22k-vertex mesh.
/// </summary>
public sealed class Terrain3DFrameScratch
{
    internal Vector3[] Screen = Array.Empty<Vector3>();
    internal float[] Depths = Array.Empty<float>();
    internal int[] TriangleIndices = Array.Empty<int>();
    internal ushort[] VisibleIndices = Array.Empty<ushort>();

    internal void EnsureVertexCapacity(int vertexCount)
    {
        if (Screen.Length < vertexCount)
        {
            Screen = new Vector3[vertexCount];
        }
    }

    internal void EnsureTriangleCapacity(int triangleCount)
    {
        if (Depths.Length < triangleCount)
        {
            Depths = new float[triangleCount];
            TriangleIndices = new int[triangleCount];
        }

        int requiredIndices = triangleCount * 3;
        if (VisibleIndices.Length < requiredIndices)
        {
            VisibleIndices = new ushort[requiredIndices];
        }
    }
}
