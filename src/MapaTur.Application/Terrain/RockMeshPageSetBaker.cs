using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>Partitions an offline steep-rock source mesh and emits every requested GPU-ready LOD page.</summary>
public static class RockMeshPageSetBaker
{
    public static IReadOnlyList<RockMeshPage> Bake(
        IReadOnlyList<RockMeshTriangle> source,
        float pageSizeMeters,
        Func<Vector3, Vector3, RockSurfaceSample> sampleSurface,
        IReadOnlyList<byte>? lods = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sampleSurface);
        if (!float.IsFinite(pageSizeMeters) || pageSizeMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSizeMeters));
        }

        byte[] requestedLods = (lods ?? new byte[] { 0, 1, 2 })
            .Distinct()
            .Order()
            .ToArray();
        if (requestedLods.Any(lod => lod > 2))
        {
            throw new ArgumentOutOfRangeException(nameof(lods), "Rock page LOD must be in the 0..2 range.");
        }

        var groups = source
            .Where(triangle => triangle.SlopeDegrees >= RockMeshPageBaker.MinimumRockSlopeDegrees)
            .GroupBy(triangle =>
            {
                Vector3 centroid = (triangle.A + triangle.B + triangle.C) / 3f;
                return new PageKey(
                    (int)MathF.Floor(centroid.X / pageSizeMeters),
                    (int)MathF.Floor(centroid.Y / pageSizeMeters));
            })
            .OrderBy(group => group.Key.X)
            .ThenBy(group => group.Key.Y);

        var pages = new List<RockMeshPage>();
        foreach (IGrouping<PageKey, RockMeshTriangle> group in groups)
        {
            RockMeshTriangle[] triangles = group.ToArray();
            foreach (byte lod in requestedLods)
            {
                pages.Add(RockMeshPageBaker.Bake(
                    lod,
                    group.Key.X,
                    group.Key.Y,
                    triangles,
                    sampleSurface));
            }
        }

        return pages;
    }

    private readonly record struct PageKey(int X, int Y);
}
