using System.Numerics;
using System.Text;

namespace MapaTur.Application.Terrain;

/// <summary>Fixed 64-byte RMP2 header followed by directly uploadable vertices and ushort indices.</summary>
public static class ScannedRockMeshPageStore
{
    public const string FileExtension = ".rmp2";
    private const ushort FormatVersion = 2;
    private static readonly byte[] Magic = "RMP2"u8.ToArray();

    public static string RelativePathFor(byte lod, int pageX, int pageY) => Path.Combine(
        $"lod{lod}",
        pageX.ToString(System.Globalization.CultureInfo.InvariantCulture),
        pageY.ToString(System.Globalization.CultureInfo.InvariantCulture) + FileExtension);

    public static void Write(Stream stream, ScannedRockMeshPage page)
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
        writer.Write(page.MaterialPageId);
        writer.Write((ushort)0);
        writer.Write((uint)page.VertexData.Length);
        writer.Write((uint)(page.Indices.Length * sizeof(ushort)));
        writer.Write(page.VertexData);
        foreach (ushort index in page.Indices)
        {
            writer.Write(index);
        }
    }

    public static ScannedRockMeshPage Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Stream is not an RMP2 scanned-rock page.");
        }

        ushort version = reader.ReadUInt16();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"Unsupported RMP2 version {version}.");
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
        ushort materialPageId = reader.ReadUInt16();
        _ = reader.ReadUInt16();
        uint vertexBytes = reader.ReadUInt32();
        uint indexBytes = reader.ReadUInt32();
        if (vertexCount > ScannedRockMeshPage.MaxVertices
            || vertexBytes != vertexCount * ScannedRockMeshPage.VertexStrideBytes
            || indexBytes != indexCount * sizeof(ushort)
            || indexCount % 3 != 0)
        {
            throw new InvalidDataException("RMP2 buffer lengths or counts are invalid.");
        }

        byte[] vertices = reader.ReadBytes(checked((int)vertexBytes));
        if (vertices.Length != vertexBytes)
        {
            throw new InvalidDataException("RMP2 vertex block is truncated.");
        }

        var indices = new ushort[indexCount];
        try
        {
            for (int i = 0; i < indices.Length; i++)
            {
                indices[i] = reader.ReadUInt16();
            }
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("RMP2 index block is truncated.", ex);
        }

        try
        {
            return new ScannedRockMeshPage(
                lod,
                pageX,
                pageY,
                worldMin,
                worldExtent,
                geometricError,
                materialPageId,
                vertices,
                indices);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("RMP2 page content is invalid.", ex);
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
}
