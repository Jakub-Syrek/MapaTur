using System.Buffers.Binary;
using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

public sealed class ScannedRockPageLodBuilderTests
{
    [Fact]
    public void should_preserve_lod0_payload_byte_for_byte()
    {
        // Arrange
        ScannedRockMeshPage source = CreatePage();

        // Act
        ScannedRockMeshPage copy = ScannedRockPageLodBuilder.CreateFinestCopy(source);

        // Assert
        copy.Should().BeEquivalentTo(
            source,
            options => options.Excluding(page => page.GeometricError));
    }

    [Fact]
    public void should_replace_legacy_lod0_relief_bound_with_quantization_error()
    {
        // Arrange
        ScannedRockMeshPage source = CreatePage();
        float expected = 0.5f * MathF.Sqrt(
            MathF.Pow(source.WorldExtent.X / ushort.MaxValue, 2f)
            + MathF.Pow(source.WorldExtent.Y / ushort.MaxValue, 2f)
            + MathF.Pow(source.WorldExtent.Z / ushort.MaxValue, 2f));

        // Act
        ScannedRockMeshPage copy = ScannedRockPageLodBuilder.CreateFinestCopy(source);

        // Assert
        copy.GeometricError.Should().BeApproximately(expected, 1e-8f);
    }

    [Fact]
    public void should_request_locked_border_simplification_with_absolute_error_budget()
    {
        // Arrange
        ScannedRockMeshPage source = CreatePage();
        var simplifier = new RecordingSimplifier([0, 1, 2], geometricErrorMeters: 0.2f);

        // Act
        _ = ScannedRockPageLodBuilder.Build(
            source,
            lod: 1,
            targetTriangleFraction: 0.5f,
            maximumGeometricErrorMeters: 0.35f,
            simplifier);

        // Assert
        simplifier.Request.Should().Be(new ScannedRockSimplificationRequest(
            TargetIndexCount: 3,
            MaximumGeometricErrorMeters: 0.35f,
            LockBorder: true));
    }

    [Fact]
    public void should_keep_packed_attributes_of_retained_vertices_unchanged()
    {
        // Arrange
        ScannedRockMeshPage source = CreatePage();
        var simplifier = new RecordingSimplifier([2, 3, 0], geometricErrorMeters: 0.2f);

        // Act
        ScannedRockMeshPage lod = ScannedRockPageLodBuilder.Build(
            source,
            lod: 1,
            targetTriangleFraction: 0.5f,
            maximumGeometricErrorMeters: 0.35f,
            simplifier);

        // Assert
        lod.VertexData.Should().Equal(
            VertexRecord(source, 2)
                .Concat(VertexRecord(source, 3))
                .Concat(VertexRecord(source, 0)));
    }

    [Fact]
    public void should_reject_simplifier_result_above_geometric_error_budget()
    {
        // Arrange
        ScannedRockMeshPage source = CreatePage();
        var simplifier = new RecordingSimplifier([0, 1, 2], geometricErrorMeters: 0.36f);

        // Act
        Action act = () => ScannedRockPageLodBuilder.Build(
            source,
            lod: 1,
            targetTriangleFraction: 0.5f,
            maximumGeometricErrorMeters: 0.35f,
            simplifier);

        // Assert
        act.Should().Throw<InvalidDataException>();
    }

    private static ScannedRockMeshPage CreatePage()
    {
        Vector3[] positions =
        [
            new(10f, 20f, 30f),
            new(14f, 20f, 31f),
            new(14f, 26f, 32f),
            new(10f, 26f, 33f),
        ];
        Vector3 minimum = new(10f, 20f, 30f);
        Vector3 extent = new(4f, 6f, 3f);
        var vertexData = new byte[positions.Length * ScannedRockMeshPage.VertexStrideBytes];
        for (int index = 0; index < positions.Length; index++)
        {
            Span<byte> vertex = vertexData.AsSpan(
                index * ScannedRockMeshPage.VertexStrideBytes,
                ScannedRockMeshPage.VertexStrideBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(
                vertex,
                Quantize(positions[index].X, minimum.X, extent.X));
            BinaryPrimitives.WriteUInt16LittleEndian(
                vertex[2..],
                Quantize(positions[index].Y, minimum.Y, extent.Y));
            BinaryPrimitives.WriteUInt16LittleEndian(
                vertex[4..],
                Quantize(positions[index].Z, minimum.Z, extent.Z));
            vertex[6] = (byte)(40 + index);
            vertex[10] = (byte)(80 + index);
            vertex[14] = (byte)(120 + index);
            vertex[15] = (byte)(160 + index);
            vertex[16] = (byte)(200 + index);
        }

        return new ScannedRockMeshPage(
            lod: 0,
            pageX: -12,
            pageY: 34,
            minimum,
            extent,
            geometricError: 4f,
            materialPageId: 20,
            vertexData,
            indices: [0, 1, 2, 0, 2, 3]);
    }

    private static byte[] VertexRecord(ScannedRockMeshPage page, int vertex) =>
        page.VertexData
            .AsSpan(
                vertex * ScannedRockMeshPage.VertexStrideBytes,
                ScannedRockMeshPage.VertexStrideBytes)
            .ToArray();

    private static ushort Quantize(float value, float minimum, float extent) =>
        (ushort)Math.Clamp(
            (int)MathF.Round(((value - minimum) / extent) * ushort.MaxValue),
            0,
            ushort.MaxValue);

    private sealed class RecordingSimplifier(
        uint[] resultIndices,
        float geometricErrorMeters) : IScannedRockIndexSimplifier
    {
        public ScannedRockSimplificationRequest? Request { get; private set; }

        public ScannedRockIndexSimplification Simplify(
            ReadOnlySpan<uint> indices,
            ReadOnlySpan<float> positions,
            int vertexCount,
            ScannedRockSimplificationRequest request)
        {
            Request = request;
            return new ScannedRockIndexSimplification(resultIndices, geometricErrorMeters);
        }
    }
}
