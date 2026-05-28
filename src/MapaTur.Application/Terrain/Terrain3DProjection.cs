using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Projects a <see cref="TerrainMesh3D"/> through a <see cref="Camera3D"/> to screen space.
/// Backfaces are culled in screen space; triangles are sorted back-to-front so a single
/// painter's-algorithm draw call renders correct occlusion without a depth buffer.
/// </summary>
public static class Terrain3DProjection
{
    private static readonly Vector3 InvalidVertex = new(float.NaN, float.NaN, float.NaN);

    /// <summary>
    /// Projects the mesh and returns visible-and-sorted triangle indices. Allocates fresh
    /// buffers — for the hot rendering path, use the
    /// <see cref="Project(TerrainMesh3D, Camera3D, float, float, Terrain3DFrameScratch)"/>
    /// overload instead.
    /// </summary>
    public static ProjectedTerrainFrame Project(
        TerrainMesh3D mesh,
        Camera3D camera,
        float screenWidth,
        float screenHeight)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(camera);

        var scratch = new Terrain3DFrameScratch();
        ProjectedTerrainFrame frame = Project(mesh, camera, screenWidth, screenHeight, scratch);

        // Allocating overload returns arrays sized exactly to the valid prefix so callers
        // can rely on ScreenVertices.Length / VisibleIndices.Length.
        var screen = new Vector3[frame.VertexCount];
        Array.Copy(scratch.Screen, screen, frame.VertexCount);

        var visible = new ushort[frame.VisibleIndexCount];
        Array.Copy(scratch.VisibleIndices, visible, frame.VisibleIndexCount);

        return new ProjectedTerrainFrame(screen, visible);
    }

    /// <summary>
    /// Projects the mesh into the supplied <paramref name="scratch"/> buffers, reusing
    /// allocations across frames. The returned frame points at the scratch's arrays;
    /// the caller must read only the prefix indicated by <see cref="ProjectedTerrainFrame.VertexCount"/>
    /// and <see cref="ProjectedTerrainFrame.VisibleIndexCount"/>.
    /// </summary>
    public static ProjectedTerrainFrame Project(
        TerrainMesh3D mesh,
        Camera3D camera,
        float screenWidth,
        float screenHeight,
        Terrain3DFrameScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(scratch);

        Vector3[] vertices = mesh.Vertices;
        int vertexCount = vertices.Length;
        scratch.EnsureVertexCapacity(vertexCount);
        Vector3[] screen = scratch.Screen;

        if (screenWidth <= 0f || screenHeight <= 0f)
        {
            for (int i = 0; i < vertexCount; i++)
            {
                screen[i] = InvalidVertex;
            }

            return new ProjectedTerrainFrame(screen, scratch.VisibleIndices, vertexCount, 0);
        }

        // Build view * projection once per frame; projecting per-vertex with the
        // matrix-accepting overload avoids rebuilding the view/projection matrices
        // 22 000+ times for a typical mesh.
        Matrix4x4 viewProjection = camera.BuildViewProjection(screenWidth / screenHeight);

        for (int i = 0; i < vertexCount; i++)
        {
            Vector4 clip = Vector4.Transform(new Vector4(vertices[i], 1f), viewProjection);
            if (clip.W <= 0f)
            {
                screen[i] = InvalidVertex;
                continue;
            }

            float invW = 1f / clip.W;
            float ndcX = clip.X * invW;
            float ndcY = clip.Y * invW;
            float ndcZ = clip.Z * invW;

            if (ndcZ < 0f || ndcZ > 1f)
            {
                screen[i] = InvalidVertex;
                continue;
            }

            float sx = (ndcX + 1f) * 0.5f * screenWidth;
            float sy = (1f - ndcY) * 0.5f * screenHeight;
            screen[i] = new Vector3(sx, sy, ndcZ);
        }

        ushort[] sourceIndices = mesh.Indices;
        int triangleCount = sourceIndices.Length / 3;
        scratch.EnsureTriangleCapacity(triangleCount);
        float[] depths = scratch.Depths;
        int[] triangleIndices = scratch.TriangleIndices;

        int visibleCount = 0;
        for (int t = 0; t < triangleCount; t++)
        {
            int baseIdx = t * 3;
            ushort i0 = sourceIndices[baseIdx];
            ushort i1 = sourceIndices[baseIdx + 1];
            ushort i2 = sourceIndices[baseIdx + 2];

            Vector3 v0 = screen[i0];
            Vector3 v1 = screen[i1];
            Vector3 v2 = screen[i2];

            if (float.IsNaN(v0.X) || float.IsNaN(v1.X) || float.IsNaN(v2.X))
            {
                continue;
            }

            // Screen-space signed area. Terrain triangles are wound NW→NE→SW (CW in
            // world XY for terrain viewed from above). Screen Y grows downward, which
            // inverts the orientation: front-facing triangles end up CW in screen
            // coordinates → positive signed area. Cull when the area is non-positive.
            float crossZ = ((v1.X - v0.X) * (v2.Y - v0.Y))
                         - ((v1.Y - v0.Y) * (v2.X - v0.X));
            if (crossZ <= 0f)
            {
                continue;
            }

            // Store positive averaged NDC depth so we can both bucket-sort and re-bucket
            // in the emission pass without extra memory.
            depths[visibleCount] = (v0.Z + v1.Z + v2.Z) * (1f / 3f);
            triangleIndices[visibleCount] = t;
            visibleCount++;
        }

        // Bucket-sort by NDC depth instead of Array.Sort: O(n) two passes versus the
        // ~17 comparisons per element introsort would do on ~128k triangles. The bucket
        // width is ~1/BucketCount in NDC depth (≈1e-4 with 4096 buckets) which is much
        // tighter than any screen-space depth artefact we'd notice.
        const int BucketCount = 4096;
        Span<int> bucketHeads = stackalloc int[BucketCount];
        bucketHeads.Clear();

        for (int k = 0; k < visibleCount; k++)
        {
            int b = QuantizeDepth(depths[k], BucketCount);
            bucketHeads[b]++;
        }

        // Convert per-bucket counts into write offsets, scanning back-to-front so the
        // furthest triangles (highest depth) land at the start of the emitted index
        // stream — same painter's-algorithm output order as the old Array.Sort path.
        int running = 0;
        for (int b = BucketCount - 1; b >= 0; b--)
        {
            int count = bucketHeads[b];
            bucketHeads[b] = running;
            running += count;
        }

        ushort[] visibleIndices = scratch.VisibleIndices;
        for (int k = 0; k < visibleCount; k++)
        {
            int b = QuantizeDepth(depths[k], BucketCount);
            int slot = bucketHeads[b]++;
            int t = triangleIndices[k];
            int baseSrc = t * 3;
            int baseDst = slot * 3;
            visibleIndices[baseDst] = sourceIndices[baseSrc];
            visibleIndices[baseDst + 1] = sourceIndices[baseSrc + 1];
            visibleIndices[baseDst + 2] = sourceIndices[baseSrc + 2];
        }

        return new ProjectedTerrainFrame(screen, visibleIndices, vertexCount, visibleCount * 3);
    }

    private static int QuantizeDepth(float depth, int bucketCount)
    {
        // depth is in [0,1] for in-frustum triangles, but float jitter can land just outside.
        if (depth <= 0f) return 0;
        if (depth >= 1f) return bucketCount - 1;
        int b = (int)(depth * bucketCount);
        return b >= bucketCount ? bucketCount - 1 : b;
    }
}