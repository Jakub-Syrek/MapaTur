using System.Buffers.Binary;
using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Pilot „kolor z orto" (2026-09-05, prośba usera): strony RMP2 mają być rysowane PROGRAMEM TERENU jako
/// dodatkowe kafle, więc ich wierzchołki trzeba przepakować z formatu strony (pos u16 znormalizowane do AABB,
/// normalna oktahedralna i16, ao u8) do layoutu kafla (pos float3 w ramce sceny, color RGBA z AO w alfie,
/// normalna float3, UV komórki orto bazowej, detail=0). Dekoder MUSI być lustrem enkodera bake'u
/// (ScannedRockPageBaker: pos = round(t·65535), oct = round(clamp·32767)) i GLSL-owego octDecode.
/// </summary>
public sealed class ScannedRockPageTerrainRepackerTests
{
    private static ScannedRockMeshPage MakePage(
        (float x, float y, float z, Vector3 n, byte ao)[] verts,
        Vector3 worldMin,
        Vector3 worldExtent,
        ushort[] indices)
    {
        byte[] data = new byte[verts.Length * ScannedRockMeshPage.VertexStrideBytes];
        for (int i = 0; i < verts.Length; i++)
        {
            int o = i * ScannedRockMeshPage.VertexStrideBytes;
            var (x, y, z, n, ao) = verts[i];
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(o + 0), Q(x, worldMin.X, worldExtent.X));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(o + 2), Q(y, worldMin.Y, worldExtent.Y));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(o + 4), Q(z, worldMin.Z, worldExtent.Z));
            Vector2 e = OctEncode(Vector3.Normalize(n));
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(o + 6), (short)MathF.Round(e.X * short.MaxValue));
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(o + 8), (short)MathF.Round(e.Y * short.MaxValue));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(o + 10), 0); // uv (nieużywane w trybie terenu)
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(o + 12), 0);
            data[o + 14] = ao;
            data[o + 15] = 0; // seam
        }

        return new ScannedRockMeshPage(
            lod: 0, pageX: 0, pageY: 0, worldMin: worldMin, worldExtent: worldExtent,
            geometricError: 0.1f, materialPageId: 20, vertexData: data, indices: indices);
    }

    private static ushort Q(float v, float min, float extent) =>
        (ushort)MathF.Round(Math.Clamp((v - min) / extent, 0f, 1f) * ushort.MaxValue);

    // Ten sam enkoder co ScannedRockPageBaker.EncodeOctahedral (lustro GLSL octDecode).
    private static Vector2 OctEncode(Vector3 n)
    {
        float l1 = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
        Vector2 e = new(n.X / l1, n.Y / l1);
        if (n.Z < 0f)
        {
            e = new Vector2((1f - MathF.Abs(e.Y)) * (e.X >= 0f ? 1f : -1f), (1f - MathF.Abs(e.X)) * (e.Y >= 0f ? 1f : -1f));
        }

        return e;
    }

    private static readonly Vector3 CellMin = new(1000f, -2000f, 0f);
    private static readonly Vector3 CellMax = new(6000f, 3000f, 0f);

    [Fact]
    public void positions_decode_to_world_metres_within_quantisation_step()
    {
        var page = MakePage(
            [(1200f, -1500f, 1800.5f, Vector3.UnitZ, 255), (1232f, -1500f, 1801f, Vector3.UnitZ, 255), (1200f, -1468f, 1803f, Vector3.UnitZ, 255)],
            worldMin: new Vector3(1200f, -1500f, 1800f), worldExtent: new Vector3(32f, 32f, 4f),
            indices: [0, 1, 2]);

        TerrainVertexPack pack = ScannedRockPageTerrainRepacker.Repack(page, CellMin, CellMax);

        pack.Positions.Should().HaveCount(3);
        pack.Positions[0].Should().BeEquivalentTo(new Vector3(1200f, -1500f, 1800.5f), o => o.WithStrictOrdering().Using<float>(c => c.Subject.Should().BeApproximately(c.Expectation, 32f / ushort.MaxValue)).WhenTypeIs<float>());
        pack.Positions[2].Z.Should().BeApproximately(1803f, 4f / ushort.MaxValue);
        pack.Indices.Should().Equal(0u, 1u, 2u);
    }

    [Fact]
    public void normals_round_trip_through_octahedral_encoding_including_lower_hemisphere()
    {
        Vector3 up = Vector3.UnitZ;
        Vector3 slanted = Vector3.Normalize(new Vector3(0.6f, -0.3f, 0.74f));
        Vector3 overhang = Vector3.Normalize(new Vector3(0.2f, 0.9f, -0.39f)); // z<0 = druga gałąź octDecode
        var page = MakePage(
            [(2000f, 0f, 1500f, up, 255), (2001f, 0f, 1500f, slanted, 255), (2000f, 1f, 1500f, overhang, 255)],
            worldMin: new Vector3(2000f, 0f, 1500f), worldExtent: new Vector3(1f, 1f, 1f), indices: [0, 1, 2]);

        TerrainVertexPack pack = ScannedRockPageTerrainRepacker.Repack(page, CellMin, CellMax);

        Vector3.Dot(pack.Normals[0], up).Should().BeGreaterThan(0.9999f);
        Vector3.Dot(pack.Normals[1], slanted).Should().BeGreaterThan(0.999f);
        Vector3.Dot(pack.Normals[2], overhang).Should().BeGreaterThan(0.999f);
    }

    [Fact]
    public void ortho_uv_maps_cell_corners_with_v_zero_at_north()
    {
        // Konwencja kafla terenu: u rośnie na wschód, v=0 na PÓŁNOCNEJ krawędzi komórki (row 0 = północ).
        var page = MakePage(
            [(1000f, 3000f, 0f, Vector3.UnitZ, 255), (6000f, -2000f, 0f, Vector3.UnitZ, 255), (3500f, 500f, 0f, Vector3.UnitZ, 255)],
            worldMin: new Vector3(1000f, -2000f, 0f), worldExtent: new Vector3(5000f, 5000f, 1f), indices: [0, 1, 2]);

        TerrainVertexPack pack = ScannedRockPageTerrainRepacker.Repack(page, CellMin, CellMax);

        pack.TexCoords[0].Should().BeApproximately(0f, 1e-3f); // NW: u=0
        pack.TexCoords[1].Should().BeApproximately(0f, 1e-3f); //     v=0
        pack.TexCoords[2].Should().BeApproximately(1f, 1e-3f); // SE: u=1
        pack.TexCoords[3].Should().BeApproximately(1f, 1e-3f); //     v=1
        pack.TexCoords[4].Should().BeApproximately(0.5f, 1e-3f);
        pack.TexCoords[5].Should().BeApproximately(0.5f, 1e-3f);
    }

    [Fact]
    public void colour_is_white_with_page_ao_in_alpha_and_detail_is_zero()
    {
        var page = MakePage(
            [(2000f, 0f, 1500f, Vector3.UnitZ, 255), (2001f, 0f, 1500f, Vector3.UnitZ, 128), (2000f, 1f, 1500f, Vector3.UnitZ, 0)],
            worldMin: new Vector3(2000f, 0f, 1500f), worldExtent: new Vector3(1f, 1f, 1f), indices: [0, 1, 2]);

        TerrainVertexPack pack = ScannedRockPageTerrainRepacker.Repack(page, CellMin, CellMax);

        // Alfa = AO z kafla (TerrainCurvatureAo mnoży sumę światła przez mix(1, a, uAoStrength)) — strona wnosi
        // własne AO ze skanu; rgb = biel, bo kolor daje orto (useOrtho=1) i nie wolno go zabarwić.
        ((byte)(pack.Colors[0] >> 24)).Should().Be((byte)255);
        ((byte)(pack.Colors[1] >> 24)).Should().Be((byte)128);
        ((byte)(pack.Colors[2] >> 24)).Should().Be((byte)0);
        (pack.Colors[0] & 0x00FFFFFFu).Should().Be(0x00FFFFFFu);
        pack.Detail.Should().OnlyContain(d => d == 0f);
    }

    [Fact]
    public void rejects_page_outside_cell_bounds()
    {
        var page = MakePage(
            [(0f, 0f, 0f, Vector3.UnitZ, 255), (1f, 0f, 0f, Vector3.UnitZ, 255), (0f, 1f, 0f, Vector3.UnitZ, 255)],
            worldMin: new Vector3(9000f, 9000f, 0f), worldExtent: new Vector3(1f, 1f, 1f), indices: [0, 1, 2]);

        Action act = () => ScannedRockPageTerrainRepacker.Repack(page, CellMin, CellMax);

        act.Should().Throw<ArgumentException>();
    }
}
