using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>Compact prebaked spatial and hierarchy index for an RMP3 page set.</summary>
public static class HybridTerrainPageIndexStore
{
    public const string FileName = "_pages.hidx";
    private const int MaximumPageCount = 10_000_000;
    private static readonly byte[] Magic = "HIX1"u8.ToArray();

    public static void Write(string root, IEnumerable<HybridTerrainPageDescriptor> pages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(pages);
        Directory.CreateDirectory(root);
        HybridTerrainPageDescriptor[] ordered = HybridTerrainPageCatalog.Order(pages);
        HybridTerrainPageCatalog.EnsureUniqueKeys(ordered);

        string finalPath = Path.Combine(root, FileName);
        string temporaryPath = finalPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            using (FileStream stream = File.Create(temporaryPath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(ordered.Length);
                foreach (HybridTerrainPageDescriptor page in ordered)
                {
                    writer.Write(page.Key.Lod);
                    writer.Write(page.Key.PageX);
                    writer.Write(page.Key.PageY);
                    Write(writer, page.WorldMin);
                    Write(writer, page.WorldExtent);
                    writer.Write(page.GeometricError);
                    writer.Write(page.VertexCount);
                    writer.Write(page.IndexCount);
                }

                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, finalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static IReadOnlyList<HybridTerrainPageDescriptor> Read(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        try
        {
            using FileStream stream = File.OpenRead(Path.Combine(root, FileName));
            using var reader = new BinaryReader(stream);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            {
                throw new InvalidDataException("Invalid hybrid-terrain page index magic.");
            }

            int count = reader.ReadInt32();
            if (count is < 0 or > MaximumPageCount)
            {
                throw new InvalidDataException($"Invalid hybrid-terrain page count {count}.");
            }

            var result = new HybridTerrainPageDescriptor[count];
            for (int index = 0; index < count; index++)
            {
                byte lod = reader.ReadByte();
                int pageX = reader.ReadInt32();
                int pageY = reader.ReadInt32();
                Vector3 worldMin = ReadVector3(reader);
                Vector3 worldExtent = ReadVector3(reader);
                float geometricError = reader.ReadSingle();
                int vertexCount = reader.ReadInt32();
                int indexCount = reader.ReadInt32();
                string path = Path.Combine(
                    root,
                    HybridTerrainMeshPageStore.RelativePathFor(lod, pageX, pageY));
                result[index] = new HybridTerrainPageDescriptor(
                    new HybridTerrainPageKey(pageX, pageY, lod),
                    worldMin,
                    worldExtent,
                    geometricError,
                    vertexCount,
                    indexCount,
                    path);
            }

            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Hybrid-terrain page index has trailing bytes.");
            }

            HybridTerrainPageCatalog.EnsureUniqueKeys(result);
            return result;
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Hybrid-terrain page index is truncated.", ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("Hybrid-terrain page index contains an invalid descriptor.", ex);
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
