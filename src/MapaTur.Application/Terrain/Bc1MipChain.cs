namespace MapaTur.Application.Terrain;

/// <summary>
/// Layout pełnego, kwadratowego łańcucha mipów BC1 używanego przez pakiety <c>.opk</c>
/// i texture-array renderera. Nie jest cache'em ani formatem pliku — wyłącznie wspólną
/// arytmetyką bajtów gotowych do bezpośredniego uploadu na GPU.
/// </summary>
public static class Bc1MipChain
{
    /// <summary>Liczba bajtów wszystkich poziomów od <paramref name="pixels"/> do 1×1.</summary>
    public static int ByteSize(int pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pixels, 0);

        int total = 0;
        for (int p = pixels; p >= 1; p >>= 1)
        {
            total += Bc1Encoder.EncodedSize(p, p);
        }

        return total;
    }
}
