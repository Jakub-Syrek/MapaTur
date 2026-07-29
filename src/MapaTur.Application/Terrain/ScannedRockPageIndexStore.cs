using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>Compact prebaked spatial index for an RMP2 page set.</summary>
public static class ScannedRockPageIndexStore
{
    public const string FileName = "_pages.ridx";
    private static readonly byte[] Magic = "RIX1"u8.ToArray();
    private const int MaximumPageCount = 10_000_000;

    public static void Write(string root, IEnumerable<ScannedRockPageDescriptor> pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(pages);
        Directory.CreateDirectory(root);
        ScannedRockPageDescriptor[] ordered = pages
            .OrderBy(page => page.Key.PageX)
            .ThenBy(page => page.Key.PageY)
            .ThenBy(page => page.Key.Lod)
            .ToArray();

        using FileStream stream = File.Create(Path.Combine(root, FileName));
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(ordered.Length);
        foreach (ScannedRockPageDescriptor page in ordered)
        {
            writer.Write(page.Key.Lod);
            writer.Write(page.Key.PageX);
            writer.Write(page.Key.PageY);
            Write(writer, page.WorldMin);
            Write(writer, page.WorldExtent);
            writer.Write(page.GeometricError);
            writer.Write(page.MaterialPageId);
            writer.Write(page.VertexCount);
            writer.Write(page.IndexCount);
        }
    }

    public static IReadOnlyList<ScannedRockPageDescriptor> Read(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        using FileStream stream = File.OpenRead(Path.Combine(root, FileName));
        using var reader = new BinaryReader(stream);
        byte[] magic = reader.ReadBytes(Magic.Length);
        if (!magic.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Invalid scanned-rock page index magic.");
        }

        int count = reader.ReadInt32();
        if (count is < 0 or > MaximumPageCount)
        {
            throw new InvalidDataException($"Invalid scanned-rock page count {count}.");
        }

        var result = new ScannedRockPageDescriptor[count];
        for (int index = 0; index < count; index++)
        {
            byte lod = reader.ReadByte();
            int pageX = reader.ReadInt32();
            int pageY = reader.ReadInt32();
            Vector3 worldMin = ReadVector3(reader);
            Vector3 worldExtent = ReadVector3(reader);
            float geometricError = reader.ReadSingle();
            ushort materialPageId = reader.ReadUInt16();
            int vertexCount = reader.ReadInt32();
            int indexCount = reader.ReadInt32();
            string path = Path.Combine(
                root,
                ScannedRockMeshPageStore.RelativePathFor(lod, pageX, pageY));
            result[index] = new ScannedRockPageDescriptor(
                new ScannedRockPageKey(pageX, pageY, lod),
                worldMin,
                worldExtent,
                geometricError,
                materialPageId,
                vertexCount,
                indexCount,
                path);
        }

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Scanned-rock page index has trailing bytes.");
        }

        return result;
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
