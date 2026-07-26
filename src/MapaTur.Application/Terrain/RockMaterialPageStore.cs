using System.Text;

namespace MapaTur.Application.Terrain;

/// <summary>Reads and writes the RTX1 GPU-ready BC1 material-page format.</summary>
public static class RockMaterialPageStore
{
    public const string FileExtension = ".rtex";
    private const ushort FormatVersion = 1;
    private const ushort FormatBc1 = 1;
    private const int HeaderBytes = 32;
    private static readonly byte[] Magic = "RTX1"u8.ToArray();

    public static void Write(Stream stream, RockMaterialPage page)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(page);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(FormatBc1);
        writer.Write(page.PageId);
        writer.Write(page.MipCount);
        writer.Write(page.Width);
        writer.Write(page.Height);
        writer.Write((uint)page.Bc1Data.Length);
        writer.Write(0L);
        writer.Write(page.Bc1Data);
    }

    public static RockMaterialPage Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("Stream is not an RTX1 rock material page.");
        }

        ushort version = reader.ReadUInt16();
        ushort format = reader.ReadUInt16();
        ushort pageId = reader.ReadUInt16();
        ushort mipCount = reader.ReadUInt16();
        int width = reader.ReadInt32();
        int height = reader.ReadInt32();
        uint payloadBytes = reader.ReadUInt32();
        _ = reader.ReadInt64();
        if (version != FormatVersion
            || format != FormatBc1
            || payloadBytes != RockMaterialPage.CalculatePayloadBytes(width, height))
        {
            throw new InvalidDataException("RTX1 header is invalid.");
        }

        byte[] payload = reader.ReadBytes(checked((int)payloadBytes));
        if (payload.Length != payloadBytes || stream.Position < HeaderBytes)
        {
            throw new InvalidDataException("RTX1 payload is truncated.");
        }

        try
        {
            return new RockMaterialPage(pageId, width, height, mipCount, payload);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("RTX1 page content is invalid.", ex);
        }
    }
}
