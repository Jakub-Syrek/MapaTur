using System.Numerics;

namespace MapaTur.Climbing;

/// <summary>
/// A local climbable triangle mesh in climb space (real metres), with its attached holds.
/// Unlike the terrain heightfield it can represent true overhangs. Triangles are wound
/// counter-clockwise as seen from the climber's side, so the geometric normal points outward.
/// Hold identity lives here, never in the visual terrain LOD: a tile swap may re-tessellate
/// the rendered rock, but the patch (and its hold ids) stays the same.
/// </summary>
public sealed class ClimbSurfacePatch
{
    private readonly Vector3[] vertices;
    private readonly int[] triangleIndices;
    private readonly Vector3[] vertexNormals;
    private readonly ClimbHold[] holds;

    public ClimbSurfacePatch(
        string id,
        IReadOnlyList<Vector3> vertices,
        IReadOnlyList<int> triangleIndices,
        IReadOnlyList<ClimbHold> holds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(triangleIndices);
        ArgumentNullException.ThrowIfNull(holds);
        if (triangleIndices.Count == 0 || triangleIndices.Count % 3 != 0)
        {
            throw new ArgumentException("Triangle indices must come in non-empty multiples of three.", nameof(triangleIndices));
        }

        this.vertices = [.. vertices];
        this.triangleIndices = [.. triangleIndices];
        this.holds = [.. holds];
        foreach (int index in this.triangleIndices)
        {
            if (index < 0 || index >= this.vertices.Length)
            {
                throw new ArgumentException($"Triangle index {index} is outside the vertex list.", nameof(triangleIndices));
            }
        }

        Id = id;
        vertexNormals = ComputeVertexNormals(this.vertices, this.triangleIndices);
    }

    public string Id { get; }

    public IReadOnlyList<Vector3> Vertices => vertices;

    public IReadOnlyList<int> TriangleIndices => triangleIndices;

    public IReadOnlyList<ClimbHold> Holds => holds;

    public int TriangleCount => triangleIndices.Length / 3;

    public void GetTriangle(int triangleIndex, out Vector3 a, out Vector3 b, out Vector3 c)
    {
        int offset = triangleIndex * 3;
        a = vertices[triangleIndices[offset]];
        b = vertices[triangleIndices[offset + 1]];
        c = vertices[triangleIndices[offset + 2]];
    }

    /// <summary>Smooth surface normal at barycentric (u, v, w) of a triangle; falls back to the face normal.</summary>
    public Vector3 InterpolateNormal(int triangleIndex, float u, float v, float w)
    {
        int offset = triangleIndex * 3;
        Vector3 blended = (vertexNormals[triangleIndices[offset]] * u)
            + (vertexNormals[triangleIndices[offset + 1]] * v)
            + (vertexNormals[triangleIndices[offset + 2]] * w);
        if (blended.LengthSquared() < 1e-10f)
        {
            GetTriangle(triangleIndex, out Vector3 a, out Vector3 b, out Vector3 c);
            blended = Vector3.Cross(b - a, c - a);
        }

        return Vector3.Normalize(blended);
    }

    private static Vector3[] ComputeVertexNormals(Vector3[] vertices, int[] indices)
    {
        var normals = new Vector3[vertices.Length];
        for (int offset = 0; offset < indices.Length; offset += 3)
        {
            Vector3 a = vertices[indices[offset]];
            Vector3 b = vertices[indices[offset + 1]];
            Vector3 c = vertices[indices[offset + 2]];
            Vector3 areaWeighted = Vector3.Cross(b - a, c - a);
            normals[indices[offset]] += areaWeighted;
            normals[indices[offset + 1]] += areaWeighted;
            normals[indices[offset + 2]] += areaWeighted;
        }

        for (int i = 0; i < normals.Length; i++)
        {
            normals[i] = normals[i].LengthSquared() > 1e-10f ? Vector3.Normalize(normals[i]) : Vector3.Zero;
        }

        return normals;
    }
}