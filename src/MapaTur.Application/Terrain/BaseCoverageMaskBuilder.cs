using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// A world-space coverage bitmap: <see cref="Coverage"/> is row-major <see cref="Width"/>×<see cref="Height"/>
/// bytes (255 = full-detail ground, 0 = base-owned), row 0 at the window's min-Y edge, mapping linearly from
/// world XY over the window — the same sampling scheme as the trail mask, so the shader lookup is
/// <c>uv = (worldXY − min) / size</c>. Built by <see cref="BaseCoverageMaskBuilder"/>.
/// </summary>
public sealed record BaseCoverageMask(
    byte[] Coverage,
    int Width,
    int Height,
    float WorldMinX,
    float WorldMinY,
    float WorldSizeX,
    float WorldSizeY)
{
    /// <summary>True when the mask marks the given world point as fully-detailed ground (base may be
    /// discarded there). Points outside the window are never covered.</summary>
    public bool CoveredAt(float worldX, float worldY)
    {
        int i = (int)MathF.Floor((worldX - WorldMinX) / WorldSizeX * Width);
        int j = (int)MathF.Floor((worldY - WorldMinY) / WorldSizeY * Height);
        if (i < 0 || i >= Width || j < 0 || j >= Height)
        {
            return false;
        }

        return Coverage[(j * Width) + i] != 0;
    }
}

/// <summary>
/// The SURFACE-OWNERSHIP builder (invariant: "a ground cell whose full 1 m detail is resident is rendered by
/// that detail, not by the base skin"). The box-averaged base sits 0.5–4 m ABOVE the true z16 surface on
/// convex slopes, so the always-drawn base depth-buried the streamed detail there — whole slopes rendered as
/// the smooth base dome ("lotnisko obok ostrej grani") while the z16 meshes underneath were shaded for
/// nothing. Per-BASE-TILE culling (skip a base tile only when FULLY covered) proved too coarse to ever
/// trigger in practice; this builder instead produces a small world-space bitmap of the hole-free z16 tiles'
/// union, ERODED by one texel, and the terrain shader discards base-skin fragments inside it — per-pixel
/// ownership, independent of base tile sizes and of how the resident set is distributed.
///
/// Conservative by construction: permissive rasterisation (texel-centre inside any rect) followed by a
/// 4-neighbour erosion means a covered texel is surrounded by detail on all sides — a discarded base fragment
/// can never expose a hole, while interior borders between adjacent tiles stay covered (their neighbours are
/// filled). Pure CPU, no GL; runs at streaming cadence (a few hundred rects into ≤512² texels).
/// </summary>
public static class BaseCoverageMaskBuilder
{
    /// <summary>Texel edge length in world metres. 100 m resolves z16 tiles (~400 m) into 4×4 texels —
    /// enough that erosion costs only the outer ring — while a full 448-tile resident set stays ≤ ~200²
    /// texels (40 KB).</summary>
    public const float TexelSizeMeters = 100f;

    /// <summary>Hard cap on either mask dimension; a window larger than cap×texel is clipped (the clipped-off
    /// far detail simply keeps its base underlay — safe, just not optimal).</summary>
    public const int MaxTexels = 512;

    /// <summary>
    /// Builds the coverage mask for the given full-detail tile footprints (world-XY AABBs). Returns null when
    /// there is nothing to cover (no rects, or nothing survives erosion) — the caller then disables the
    /// shader-side discard entirely.
    /// </summary>
    /// <param name="fullDetailRects">World-space AABBs of the hole-free finest-zoom resident tiles.</param>
    /// <returns>The mask, or null when no texel is covered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fullDetailRects"/> is null.</exception>
    public static BaseCoverageMask? Build(IReadOnlyList<(Vector2 Min, Vector2 Max)> fullDetailRects)
    {
        ArgumentNullException.ThrowIfNull(fullDetailRects);
        if (fullDetailRects.Count == 0)
        {
            return null;
        }

        // Window = union bbox padded by one texel so the erosion has an empty ring to eat outside the union
        // (otherwise a rect flush with the window edge would keep an un-eroded outer texel).
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        foreach ((Vector2 min, Vector2 max) in fullDetailRects)
        {
            minX = MathF.Min(minX, min.X);
            minY = MathF.Min(minY, min.Y);
            maxX = MathF.Max(maxX, max.X);
            maxY = MathF.Max(maxY, max.Y);
        }

        minX -= TexelSizeMeters;
        minY -= TexelSizeMeters;
        maxX += TexelSizeMeters;
        maxY += TexelSizeMeters;

        int width = Math.Clamp((int)MathF.Ceiling((maxX - minX) / TexelSizeMeters), 1, MaxTexels);
        int height = Math.Clamp((int)MathF.Ceiling((maxY - minY) / TexelSizeMeters), 1, MaxTexels);
        float sizeX = width * TexelSizeMeters;
        float sizeY = height * TexelSizeMeters;

        // Permissive rasterisation: mark every texel whose CENTRE lies inside any rect.
        var filled = new byte[width * height];
        foreach ((Vector2 min, Vector2 max) in fullDetailRects)
        {
            int i0 = Math.Clamp((int)MathF.Floor((min.X - minX) / TexelSizeMeters), 0, width - 1);
            int i1 = Math.Clamp((int)MathF.Ceiling((max.X - minX) / TexelSizeMeters), 0, width - 1);
            int j0 = Math.Clamp((int)MathF.Floor((min.Y - minY) / TexelSizeMeters), 0, height - 1);
            int j1 = Math.Clamp((int)MathF.Ceiling((max.Y - minY) / TexelSizeMeters), 0, height - 1);
            for (int j = j0; j <= j1; j++)
            {
                float cy = minY + ((j + 0.5f) * TexelSizeMeters);
                if (cy < min.Y || cy > max.Y)
                {
                    continue;
                }

                for (int i = i0; i <= i1; i++)
                {
                    float cx = minX + ((i + 0.5f) * TexelSizeMeters);
                    if (cx < min.X || cx > max.X)
                    {
                        continue;
                    }

                    filled[(j * width) + i] = 255;
                }
            }
        }

        // 4-neighbour erosion: a texel survives only when it AND all four neighbours are filled — so every
        // covered texel is strictly interior to the union, and the base is only discarded where the detail
        // fully surrounds the ground. Window-edge texels never survive (their outside neighbour is empty).
        var eroded = new byte[width * height];
        bool any = false;
        for (int j = 1; j < height - 1; j++)
        {
            for (int i = 1; i < width - 1; i++)
            {
                int idx = (j * width) + i;
                bool covered = filled[idx] != 0
                    && filled[idx - 1] != 0
                    && filled[idx + 1] != 0
                    && filled[idx - width] != 0
                    && filled[idx + width] != 0;
                if (covered)
                {
                    eroded[idx] = 255;
                    any = true;
                }
            }
        }

        return any ? new BaseCoverageMask(eroded, width, height, minX, minY, sizeX, sizeY) : null;
    }
}