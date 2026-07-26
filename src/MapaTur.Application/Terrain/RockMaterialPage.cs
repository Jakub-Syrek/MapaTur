namespace MapaTur.Application.Terrain;

/// <summary>One offline-prepared BC1 material page with a complete mip chain, ready for compressed upload.</summary>
public sealed class RockMaterialPage
{
    public RockMaterialPage(ushort pageId, int width, int height, ushort mipCount, byte[] bc1Data)
    {
        ArgumentNullException.ThrowIfNull(bc1Data);
        if (width <= 0 || height <= 0 || mipCount == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        int expectedMips = CalculateMipCount(width, height);
        int expectedBytes = CalculatePayloadBytes(width, height);
        if (mipCount != expectedMips || bc1Data.Length != expectedBytes)
        {
            throw new ArgumentException("Rock material mip count or BC1 payload length is invalid.", nameof(bc1Data));
        }

        PageId = pageId;
        Width = width;
        Height = height;
        MipCount = mipCount;
        Bc1Data = bc1Data;
    }

    public ushort PageId { get; }
    public int Width { get; }
    public int Height { get; }
    public ushort MipCount { get; }
    public byte[] Bc1Data { get; }

    public static int CalculateMipCount(int width, int height)
    {
        int count = 0;
        for (int w = width, h = height; ; w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
        {
            count++;
            if (w == 1 && h == 1)
            {
                return count;
            }
        }
    }

    public static int CalculatePayloadBytes(int width, int height)
    {
        int bytes = 0;
        for (int w = width, h = height; ; w = Math.Max(1, w / 2), h = Math.Max(1, h / 2))
        {
            bytes = checked(bytes + Bc1Encoder.EncodedSize(w, h));
            if (w == 1 && h == 1)
            {
                return bytes;
            }
        }
    }
}

/// <summary>Builds the complete BC1 chain offline; runtime never decodes an image or generates a mip.</summary>
public static class RockMaterialPageBaker
{
    public static RockMaterialPage Bake(ushort pageId, byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0 || height <= 0 || rgba.Length != checked(width * height * 4))
        {
            throw new ArgumentException("RGBA input dimensions do not match its payload.", nameof(rgba));
        }

        var payload = new byte[RockMaterialPage.CalculatePayloadBytes(width, height)];
        byte[] level = rgba;
        int levelWidth = width;
        int levelHeight = height;
        int destinationOffset = 0;
        while (true)
        {
            int levelBytes = Bc1Encoder.EncodedSize(levelWidth, levelHeight);
            Bc1Encoder.Encode(
                level,
                levelWidth,
                levelHeight,
                payload.AsSpan(destinationOffset, levelBytes));
            destinationOffset += levelBytes;
            if (levelWidth == 1 && levelHeight == 1)
            {
                break;
            }

            level = Downsample(level, levelWidth, levelHeight);
            levelWidth = Math.Max(1, levelWidth / 2);
            levelHeight = Math.Max(1, levelHeight / 2);
        }

        return new RockMaterialPage(
            pageId,
            width,
            height,
            checked((ushort)RockMaterialPage.CalculateMipCount(width, height)),
            payload);
    }

    private static byte[] Downsample(byte[] source, int width, int height)
    {
        int destinationWidth = Math.Max(1, width / 2);
        int destinationHeight = Math.Max(1, height / 2);
        var destination = new byte[checked(destinationWidth * destinationHeight * 4)];
        for (int y = 0; y < destinationHeight; y++)
        {
            for (int x = 0; x < destinationWidth; x++)
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < 2; dy++)
                    {
                        int sourceY = Math.Min((y * 2) + dy, height - 1);
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sourceX = Math.Min((x * 2) + dx, width - 1);
                            sum += source[((sourceY * width + sourceX) * 4) + channel];
                        }
                    }

                    destination[((y * destinationWidth + x) * 4) + channel] = (byte)((sum + 2) / 4);
                }
            }
        }

        return destination;
    }
}
