using FluentAssertions;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Granica pokrycia detalu orto (2026-07-25): texel przezroczysty DXT1a dekoduje się jako RGBA(0,0,0,0),
/// a mipy alfa-ważone zapisują RGB=0, gdy cała czwórka jest pusta (<c>BuildMipChain</c>/<c>Half</c>:
/// <c>if (sumA == 0)</c>). KAŻDE filtrowanie przy krawędzi pokrycia (bilinear, mip, bicubic) rozcieńcza
/// więc kolor CZERNIĄ proporcjonalnie do (1−α). Zmierzone skutki na pozie usera (Szpiglasowy Wierch):
/// ciemna nitka w wyświetlanym samplu ORAZ — po podaniu takiego sampla do prawa tonu — <c>delta &lt; 0</c>,
/// czyli <c>dc − delta</c> PODBIJAŁO jasność do jasnej piły (kolor linii 165,167,162 przy terenie 95,99,94).
/// Lekarstwo jest dokładne: rgb_f = Σ wᵢ·cᵢ (przezroczyste wnoszą 0), α_f = Σ_pokryte wᵢ ⇒ rgb_f / α_f
/// = średnia po POKRYTYCH texelach (<c>unpremulPunch</c>).
///
/// Ten test pilnuje INWARIANTU we WSZYSTKICH ścieżkach detalu naraz (det1m, det25Arr, det05Array,
/// applyOrthoDetail) — dokładnie tryb awarii z historii projektu: podmiana shadera, która CICHO nie weszła
/// na jednej ze ścieżek, i przedwcześnie ogłoszony fix.
/// </summary>
public sealed class TerrainShaderPunchThroughTests
{
    private const int DetailSamplingPaths = 4; // det1m, det25Arr, det05Array, applyOrthoDetail

    private static string ShaderSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "MapaTur.App", "Services", "Terrain3DGlRenderer.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Nie znaleziono Terrain3DGlRenderer.cs idąc w górę od " + AppContext.BaseDirectory);
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            n++;
            i += needle.Length;
        }

        return n;
    }

    [Fact]
    public void should_define_the_unpremultiply_helper_once()
    {
        ShaderSource().Should().Contain("vec3 unpremulPunch(vec4 s)");
    }

    [Fact]
    public void should_unpremultiply_the_displayed_sample_on_every_detail_path()
    {
        Count(ShaderSource(), "vec3 dc = unpremulPunch(dcs);").Should().Be(DetailSamplingPaths);
    }

    [Fact]
    public void should_never_take_the_displayed_sample_raw()
    {
        ShaderSource().Should().NotContain("vec3 dc = dcs.rgb;");
    }

    [Fact]
    public void should_unpremultiply_the_tone_reference_on_every_detail_path()
    {
        Count(ShaderSource(), "vec3 dRaw = unpremulPunch(tRaw);").Should().Be(DetailSamplingPaths);
    }

    [Fact]
    public void should_never_read_the_tone_reference_raw()
    {
        ShaderSource().Should().NotContain("toneLod).rgb");
    }

    [Fact]
    public void should_gate_tone_harmonisation_by_coverage_of_the_tone_footprint()
    {
        Count(ShaderSource(), "smoothstep(0.02, 0.12, toneA)").Should().Be(DetailSamplingPaths);
    }

    [Fact]
    public void should_carry_alpha_through_both_bicubic_helpers()
    {
        string src = ShaderSource();

        src.Should().Contain("vec4 texBicubic(sampler2D t");
        src.Should().Contain("vec4 texBicubicArr(mediump sampler2DArray t");
    }
}