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
}
