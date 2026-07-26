using System.Buffers.Binary;
using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class RockMeshPageBakerTests
{
    [Fact]
    public void should_bake_only_triangles_steeper_than_the_rock_threshold()
    {
        // Arrange
        var source = new[]
        {
            new RockMeshTriangle(
                Vector3.Zero,
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f)),
            new RockMeshTriangle(
                new Vector3(10f, 0f, 0f),
                new Vector3(10f, 1f, 0f),
                new Vector3(10f, 0f, 1f)),
        };

        // Act
        RockMeshPage page = RockMeshPageBaker.Bake(
            lod: 2,
            pageX: 0,
            pageY: 0,
            source,
            static (_, _) => RockSurfaceSample.Unchanged);

        // Assert
        page.IndexCount.Should().Be(3);
    }

    [Fact]
    public void should_move_vertices_along_the_wall_normal()
    {
        // Arrange
        var source = new[]
        {
            new RockMeshTriangle(
                Vector3.Zero,
                new Vector3(0f, 1f, 0f),
                new Vector3(0f, 0f, 1f)),
        };

        // Act
        RockMeshPage page = RockMeshPageBaker.Bake(
            lod: 2,
            pageX: 0,
            pageY: 0,
            source,
            static (_, _) => new RockSurfaceSample(0.2f, 255, 0));

        // Assert
        DecodePosition(page, vertexIndex: 0).X.Should().BeApproximately(0.2f, 0.001f);
    }

    [Fact]
    public void should_recalculate_normals_after_geometric_displacement()
    {
        // Arrange
        var source = new[]
        {
            new RockMeshTriangle(
                Vector3.Zero,
                new Vector3(0f, 1f, 0f),
                new Vector3(0f, 0f, 1f)),
        };

        // Act
        RockMeshPage page = RockMeshPageBaker.Bake(
            lod: 2,
            pageX: 0,
            pageY: 0,
            source,
            static (position, _) => new RockSurfaceSample(position.Y * 0.25f, 255, 0));

        // Assert
        DecodeNormal(page, vertexIndex: 0).Y.Should().BeLessThan(-0.2f);
    }

    [Fact]
    public void should_generate_more_geometry_for_near_lod()
    {
        // Arrange
        var source = new[]
        {
            new RockMeshTriangle(
                Vector3.Zero,
                new Vector3(0f, 4f, 0f),
                new Vector3(0f, 0f, 4f)),
        };

        // Act
        RockMeshPage near = RockMeshPageBaker.Bake(
            lod: 0,
            pageX: 0,
            pageY: 0,
            source,
            static (_, _) => RockSurfaceSample.Unchanged);
        RockMeshPage far = RockMeshPageBaker.Bake(
            lod: 2,
            pageX: 0,
            pageY: 0,
            source,
            static (_, _) => RockSurfaceSample.Unchanged);

        // Assert
        near.VertexCount.Should().BeGreaterThan(far.VertexCount);
    }

    [Fact]
    public void should_fit_a_full_thirty_two_metre_lod0_wall_in_one_ushort_page()
    {
        // Arrange
        var source = new List<RockMeshTriangle>();
        for (int y = 0; y < 32; y++)
        {
            for (int z = 0; z < 32; z++)
            {
                var a = new Vector3(0f, y, z);
                var b = new Vector3(0f, y + 1, z);
                var c = new Vector3(0f, y, z + 1);
                var d = new Vector3(0f, y + 1, z + 1);
                source.Add(new RockMeshTriangle(a, b, c));
                source.Add(new RockMeshTriangle(b, d, c));
            }
        }

        // Act
        RockMeshPage page = RockMeshPageBaker.Bake(
            lod: 0,
            pageX: 0,
            pageY: 0,
            source,
            static (_, _) => RockSurfaceSample.Unchanged);

        // Assert
        page.VertexCount.Should().BeLessThanOrEqualTo(RockMeshPage.MaxVertices);
    }

    private static Vector3 DecodePosition(RockMeshPage page, int vertexIndex)
    {
        ReadOnlySpan<byte> vertex = page.VertexData.AsSpan(
            vertexIndex * RockMeshPage.VertexStrideBytes,
            RockMeshPage.VertexStrideBytes);
        return new Vector3(
            DecodeCoordinate(BinaryPrimitives.ReadUInt16LittleEndian(vertex), page.WorldMin.X, page.WorldExtent.X),
            DecodeCoordinate(BinaryPrimitives.ReadUInt16LittleEndian(vertex[2..]), page.WorldMin.Y, page.WorldExtent.Y),
            DecodeCoordinate(BinaryPrimitives.ReadUInt16LittleEndian(vertex[4..]), page.WorldMin.Z, page.WorldExtent.Z));
    }

    private static Vector3 DecodeNormal(RockMeshPage page, int vertexIndex)
    {
        ReadOnlySpan<byte> vertex = page.VertexData.AsSpan(
            vertexIndex * RockMeshPage.VertexStrideBytes,
            RockMeshPage.VertexStrideBytes);
        float x = BinaryPrimitives.ReadInt16LittleEndian(vertex[6..]) / 32767f;
        float y = BinaryPrimitives.ReadInt16LittleEndian(vertex[8..]) / 32767f;
        var normal = new Vector3(x, y, 1f - MathF.Abs(x) - MathF.Abs(y));
        if (normal.Z < 0f)
        {
            float oldX = normal.X;
            normal.X = (1f - MathF.Abs(normal.Y)) * MathF.CopySign(1f, oldX);
            normal.Y = (1f - MathF.Abs(oldX)) * MathF.CopySign(1f, normal.Y);
        }

        return Vector3.Normalize(normal);
    }

    private static float DecodeCoordinate(ushort packed, float minimum, float extent) =>
        minimum + ((packed / 65535f) * extent);
}
