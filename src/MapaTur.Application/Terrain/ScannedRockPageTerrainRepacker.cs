using System.Buffers.Binary;
using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>
/// Vertex arrays of one RMP2 page repacked into the TERRAIN tile layout (the five per-attribute VBOs
/// <c>Terrain3DGlRenderer</c> uploads for every <see cref="TerrainMesh3D"/> tile), so the page can be drawn by
/// the terrain program itself — and therefore gets the full ortho colour chain (base cell + det25/det05 arrays,
/// de-blue, tone law), the sun/shadow/fog lighting and the CSM caster pass for free. Colour is white with the
/// page's own AO in alpha (the tile convention: <see cref="TerrainCurvatureAo"/> lives in the alpha byte);
/// <see cref="Detail"/> is zero because the page carries real geometry, not a synthesised micro-relief.
/// </summary>
public sealed record TerrainVertexPack(
    Vector3[] Positions,
    uint[] Colors,
    Vector3[] Normals,
    float[] TexCoords,
    float[] Detail,
    uint[] Indices);

/// <summary>
/// Pilot „kolor z orto" (2026-09-05): mirror of the bake-side encoders (<c>ScannedRockPageBaker</c>: position =
/// round(t·65535) over the page AABB, normal = octahedral round(clamp·32767)) and of the GLSL <c>octDecode</c>
/// in <c>PhotogrammetricRockGlLayer</c>, producing the terrain tile layout. Base-ortho UV follows the tile
/// convention exactly: u grows east across the ortho CELL, v = 0 on the cell's NORTH edge (raster row 0).
/// </summary>
public static class ScannedRockPageTerrainRepacker
{
    /// <summary>Repacks one page into the terrain tile layout; throws when the page centre is outside the cell.</summary>
    /// <param name="page">Page in the on-disk quantised format.</param>
    /// <param name="cellMin">World-frame (scene anchor) minimum corner of the base-ortho cell the page lies in.</param>
    /// <param name="cellMax">World-frame maximum corner of that cell.</param>
    public static TerrainVertexPack Repack(ScannedRockMeshPage page, Vector3 cellMin, Vector3 cellMax)
    {
        ArgumentNullException.ThrowIfNull(page);
        Vector3 centre = page.WorldMin + (page.WorldExtent * 0.5f);
        if (centre.X < cellMin.X || centre.X > cellMax.X || centre.Y < cellMin.Y || centre.Y > cellMax.Y)
        {
            throw new ArgumentException(
                $"page centre ({centre.X:F1},{centre.Y:F1}) lies outside the ortho cell [{cellMin.X:F0}..{cellMax.X:F0}]×[{cellMin.Y:F0}..{cellMax.Y:F0}]",
                nameof(cellMin));
        }

        float cellW = MathF.Max(cellMax.X - cellMin.X, 1e-3f);
        float cellH = MathF.Max(cellMax.Y - cellMin.Y, 1e-3f);
        int count = page.VertexCount;
        var positions = new Vector3[count];
        var colors = new uint[count];
        var normals = new Vector3[count];
        var tex = new float[count * 2];
        var detail = new float[count];
        ReadOnlySpan<byte> data = page.VertexData;
        for (int i = 0; i < count; i++)
        {
            int o = i * ScannedRockMeshPage.VertexStrideBytes;
            float qx = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(o + 0, 2)) / (float)ushort.MaxValue;
            float qy = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(o + 2, 2)) / (float)ushort.MaxValue;
            float qz = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(o + 4, 2)) / (float)ushort.MaxValue;
            Vector3 p = page.WorldMin + (new Vector3(qx, qy, qz) * page.WorldExtent);
            positions[i] = p;

            // GL normalizes a signed short attribute as max(s/32767, -1) — same here.
            float ex = MathF.Max(BinaryPrimitives.ReadInt16LittleEndian(data.Slice(o + 6, 2)) / (float)short.MaxValue, -1f);
            float ey = MathF.Max(BinaryPrimitives.ReadInt16LittleEndian(data.Slice(o + 8, 2)) / (float)short.MaxValue, -1f);
            normals[i] = OctDecode(ex, ey);

            byte ao = data[o + 14];
            colors[i] = 0x00FFFFFFu | ((uint)ao << 24);

            tex[i * 2] = Math.Clamp((p.X - cellMin.X) / cellW, 0f, 1f);
            tex[(i * 2) + 1] = Math.Clamp((cellMax.Y - p.Y) / cellH, 0f, 1f);
            detail[i] = 0f;
        }

        var indices = new uint[page.IndexCount];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = page.Indices[i];
        }

        return new TerrainVertexPack(positions, colors, normals, tex, detail, indices);
    }

    /// <summary>Exact port of the GLSL <c>octDecode</c> used by the RMP2 vertex shader.</summary>
    public static Vector3 OctDecode(float ex, float ey)
    {
        var v = new Vector3(ex, ey, 1f - MathF.Abs(ex) - MathF.Abs(ey));
        if (v.Z < 0f)
        {
            float sx = ex >= 0f ? 1f : -1f;
            float sy = ey >= 0f ? 1f : -1f;
            v = new Vector3((1f - MathF.Abs(v.Y)) * sx, (1f - MathF.Abs(v.X)) * sy, v.Z);
        }

        return Vector3.Normalize(v);
    }
}
