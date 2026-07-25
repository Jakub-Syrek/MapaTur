namespace MapaTur.Application.Terrain;

/// <summary>
/// Nodata w kaflach orto detalu (2026-07-24, czarne trójkąty przy granicy PL/SK): GUGiK WMS przycina
/// ortofotomapę na granicy Polski, a obszar poza nią wypełnia KRYJĄCĄ czernią — WebP bez kanału alfa,
/// RGB dokładnie (0,0,0) (audyt: 38 kafli granicznych det25, do 96,7% kafla). Pokrycie detalu jest
/// kodowane alfą (DXT1a punch-through, alfa-ważone mipy), więc taki piksel MUSI dostać alfa=0 na wejściu
/// pipeline'u — bez tego przechodzi każdą bramkę `dcs.a` w shaderze i maluje czarne trójkąty (kwant do
/// kafla, hipotenusa = skos granicy). Dokładne zero nie występuje w realnym zdjęciu lotniczym po stratnej
/// kompresji (cień ma wartości ~2-15), a pojedynczy trafiony piksel ginie w filtrowaniu i mipach.
/// Stosować we WSZYSTKICH ścieżkach dekodu kafli detalu (bake CLI i runtime compose) — nigdy w bazie,
/// której pokrycie wyznacza AABB, nie alfa.
/// </summary>
public static class OrthoNodata
{
    /// <summary>Ustawia alfa=0 na każdym pikselu o dokładnym RGB=(0,0,0). Bufor RGBA8, in place;
    /// kanały koloru pozostają nietknięte (piksel święty — to korekta POKRYCIA, nie koloru).</summary>
    public static void ZeroAlphaOnBlack(byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        for (int o = 0; o + 3 < rgba.Length; o += 4)
        {
            if (rgba[o] == 0 && rgba[o + 1] == 0 && rgba[o + 2] == 0)
            {
                rgba[o + 3] = 0;
            }
        }
    }

    /// <summary>Domyślny próg jasności rąbka: zmierzone mediany w kaflach granicznych det25 to 1,0 / 1,3 / 8,0
    /// w odległości 1 / 2 / 3 px od kryjącej czerni, a prawdziwy teren startuje od ~90 (kontrola: 0,0% pikseli
    /// terenu poniżej 16 na próbie 982 659).</summary>
    public const int DefaultRimLuma = 16;

    /// <summary>
    /// <see cref="ZeroAlphaOnBlack"/> + RĄBEK: gasi także piksele near-black, które SĄSIADUJĄ (8-spójnie,
    /// przechodnio) z kryjącą czernią nodata. Stratny WebP zostawia wokół nodata pierścień ~3 px o lumie
    /// 1-8, który NIE jest dokładnym zerem — przechodzi więc każdą bramkę alfy jako „kryjący czarny teren"
    /// i maluje czarną kropkowaną linię wzdłuż granicy PL (zmierzone 2026-07-25 na pozie Szpiglasowego).
    ///
    /// Kryterium jest SPÓJNOŚĆ, nie sam próg jasności: głęboki cień skalny w środku zdjęcia nie dotyka
    /// nodata, więc zostaje nietknięty. Kanały koloru bez zmian (korekta POKRYCIA, nie koloru).
    /// Stosować we WSZYSTKICH ścieżkach dekodu kafli detalu — tak jak <see cref="ZeroAlphaOnBlack"/>.
    /// </summary>
    /// <param name="rgba">Bufor RGBA8 o rozmiarze width×height×4, modyfikowany w miejscu.</param>
    /// <param name="width">Szerokość kafla w pikselach.</param>
    /// <param name="height">Wysokość kafla w pikselach.</param>
    /// <param name="maxRimLuma">Górny próg jasności pikseli rąbka (domyślnie <see cref="DefaultRimLuma"/>).</param>
    public static void ZeroAlphaOnNodataRim(byte[] rgba, int width, int height, int maxRimLuma = DefaultRimLuma)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException("Bufor krótszy niż width×height×4.", nameof(rgba));
        }

        // Zalew od kryjącej czerni: kolejka zamiast rekurencji (kafel to 512-8192 px na bok).
        var queue = new Queue<int>();
        for (int i = 0; i < width * height; i++)
        {
            int o = i * 4;
            if (rgba[o] == 0 && rgba[o + 1] == 0 && rgba[o + 2] == 0)
            {
                rgba[o + 3] = 0;
                queue.Enqueue(i);
            }
        }

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % width, y = i / width;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= width || ny >= height)
                    {
                        continue;
                    }

                    int no = (((ny * width) + nx) * 4);
                    if (rgba[no + 3] == 0)
                    {
                        continue; // już zgaszony (czerń albo wcześniejszy rąbek)
                    }

                    int luma = ((rgba[no] * 299) + (rgba[no + 1] * 587) + (rgba[no + 2] * 114)) / 1000;
                    if (luma > maxRimLuma)
                    {
                        continue; // prawdziwy teren zatrzymuje propagację
                    }

                    rgba[no + 3] = 0;
                    queue.Enqueue((ny * width) + nx);
                }
            }
        }
    }
}