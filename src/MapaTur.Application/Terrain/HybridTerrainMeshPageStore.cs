using System.Numerics;
using System.Text;

namespace MapaTur.Application.Terrain;

/// <summary>Fixed 64-byte RMP3 header followed by directly uploadable 20-byte vertices and ushort indices.</summary>
public static class HybridTerrainMeshPageStore
{
    public const string FileExtension = ".rmp3";
    public const int HeaderBytes = 64;
    private const ushort FormatVersion = 1;
    private static readonly byte[] Magic = "RMP3"u8.ToArray();

    public static string RelativePathFor(byte lod, int pageX, int pageY) => Path.Combine(
        $"lod{lod}",
        pageX.ToString(System.Globalization.CultureInfo.InvariantCulture),
        pageY.ToString(System.Globalization.CultureInfo.InvariantCulture) + FileExtension);

    public static void Write(Stream stream, HybridTerrainMeshPage page)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(page);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(page.Lod);
        writer.Write((byte)0);
        writer.Write(page.PageX);
        writer.Write(page.PageY);
        writer.Write((uint)page.VertexCount);
        writer.Write((uint)page.IndexCount);
        Write(writer, page.WorldMin);
        Write(writer, page.WorldExtent);
        writer.Write(page.GeometricError);
        writer.Write((uint)page.VertexData.Length);
        writer.Write((uint)(page.Indices.Length * sizeof(ushort)));
        writer.Write((uint)0);
        writer.Write(page.VertexData);
        foreach (ushort index in page.Indices)
        {
            writer.Write(index);
        }
    }

    public static HybridTerrainMeshPage Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        HybridTerrainMeshPageHeader header = ReadHeader(stream);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        byte[] vertices = reader.ReadBytes(header.VertexBytes);
        if (vertices.Length != header.VertexBytes)
        {
            throw new InvalidDataException("RMP3 vertex block is truncated.");
        }

        var indices = new ushort[header.IndexCount];
        try
        {
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = reader.ReadUInt16();
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("RMP3 index block is truncated.", ex);
        }

        try
        {
            return new HybridTerrainMeshPage(
                header.Lod,
                header.PageX,
                header.PageY,
                header.WorldMin,
                header.WorldExtent,
                header.GeometricError,
                vertices,
                indices);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("RMP3 page content is invalid.", ex);
        }
    }

    public static HybridTerrainMeshPageHeader ReadHeader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            {
                throw new InvalidDataException("Stream is not an RMP3 hybrid-terrain page.");
            }

            ushort version = reader.ReadUInt16();
            if (version != FormatVersion)
            {
                throw new InvalidDataException($"Unsupported RMP3 version {version}.");
            }

            byte lod = reader.ReadByte();
            _ = reader.ReadByte();
            int pageX = reader.ReadInt32();
            int pageY = reader.ReadInt32();
            uint vertexCount = reader.ReadUInt32();
            uint indexCount = reader.ReadUInt32();
            Vector3 worldMin = ReadVector3(reader);
            Vector3 worldExtent = ReadVector3(reader);
            float geometricError = reader.ReadSingle();
            uint vertexBytes = reader.ReadUInt32();
            uint indexBytes = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            if (lod > 2
                || vertexCount == 0
                || vertexCount > HybridTerrainMeshPage.MaxVertices
                || indexCount == 0
                || indexCount > int.MaxValue
                || vertexBytes > int.MaxValue
                || indexBytes > int.MaxValue
                || vertexBytes != vertexCount * HybridTerrainMeshPage.VertexStrideBytes
                || indexBytes != indexCount * sizeof(ushort)
                || indexCount % 3 != 0
                || !IsFinite(worldMin)
                || !IsFinitePositive(worldExtent.X)
                || !IsFinitePositive(worldExtent.Y)
                || !IsFinitePositive(worldExtent.Z)
                || !float.IsFinite(geometricError)
                || geometricError < 0f)
            {
                throw new InvalidDataException("RMP3 header is invalid.");
            }

            return new HybridTerrainMeshPageHeader(
                lod,
                pageX,
                pageY,
                worldMin,
                worldExtent,
                geometricError,
                checked((int)vertexCount),
                checked((int)indexCount),
                checked((int)vertexBytes),
                checked((int)indexBytes));
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("RMP3 header is truncated.", ex);
        }
    }

    private static void Write(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector3(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinitePositive(float value) => float.IsFinite(value) && value > 0f;
}

public readonly record struct HybridTerrainMeshPageHeader(
    byte Lod,
    int PageX,
    int PageY,
    Vector3 WorldMin,
    Vector3 WorldExtent,
    float GeometricError,
    int VertexCount,
    int IndexCount,
    int VertexBytes,
    int IndexBytes);
