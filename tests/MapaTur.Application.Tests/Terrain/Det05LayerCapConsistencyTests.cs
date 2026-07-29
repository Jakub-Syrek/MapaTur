using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Cap warstw det05 musi być SPÓJNY w trzech miejscach: stałej <c>Det05HardCapCells</c>, rozmiarze
/// tablicy uniformów w shaderze (<c>uniform vec4 uDet05Aabb[N]</c>) oraz w bramce wpuszczającej cele
/// do uploadu AABB. Rozejście się ich jest CICHE: cele lądują w VRAM, licznik rezydencji rośnie,
/// log pokazuje <c>resident 192 / desired 192 / queue 0</c> — a shader ich nie widzi, bo slot bez AABB
/// czyta się jako pusty (min &gt; max).
///
/// TEN BŁĄD WYSTĄPIŁ DWA RAZY:
///  * 2026-07-25 — bufory i licznik uploadu na sztywno 48 przy capie podnoszonym do 96; „podniesienie
///    capa" było pozorne. Naprawiono ROZMIAR buforów.
///  * 2026-07-30 — ta sama linia, ale BRAMKA <c>cell.Layer &gt;= 48</c> została z literałem przy capie 192.
///    Objaw zgłoszony przez użytkownika przy Gierlachu: JEDEN kafel 5 cm, nieprzesuwający się za kamerą,
///    reszta rozmyta. Wyglądało to jak awaria streamingu i wysłało diagnozę w stronę zasięgu pierścienia
///    oraz brakującej warstwy 25 cm — a przyczyną był literał w rendererze. Bliźniacza ścieżka det25
///    była poprawna, bo używa NAZWANEJ stałej <c>Det25ArrLayers</c>.
///
/// Dlatego ten test czyta ŹRÓDŁO: pilnuje, żeby w ścieżce det05 nie było ANI JEDNEGO literału
/// porównywanego z <c>cell.Layer</c>, i żeby tablica w shaderze miała dokładnie rozmiar capa.
/// </summary>
public sealed class Det05LayerCapConsistencyTests
{
    private static string RendererSource()
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

    [Fact]
    public void Bramki_na_cell_Layer_nie_moga_uzywac_literalow()
    {
        string src = RendererSource();

        // dopuszczalne są tylko porównania z 0 (walidacja "przydzielony") i z NAZWANYMI stałymi
        var offenders = Regex.Matches(src, @"cell\.Layer\s*(>=|>|<=|<)\s*(\d+)")
            .Where(m => m.Groups[2].Value != "0")
            .Select(m => m.Value)
            .ToList();

        offenders.Should().BeEmpty(
            "cap warstw musi wynikać z nazwanej stałej (Det05HardCapCells / Det25ArrLayers); literał "
            + "rozjeżdża się z capem CICHO — cele siedzą w VRAM i są niewidoczne (bug 07-25 i 07-30)");
    }

    [Fact]
    public void Tablica_uniformow_w_shaderze_ma_rozmiar_capa_det05()
    {
        string src = RendererSource();

        Match cap = Regex.Match(src, @"Det05HardCapCells\s*=\s*OperatingSystem\.IsWindows\(\)\s*\?\s*(\d+)");
        cap.Success.Should().BeTrue("stała Det05HardCapCells musi być czytelna ze źródła");
        int windowsCap = int.Parse(cap.Groups[1].Value, CultureInfo.InvariantCulture);

        Match arr = Regex.Match(src, @"uniform vec4 uDet05Aabb\[(\d+)\]");
        arr.Success.Should().BeTrue("shader musi deklarować tablicę uDet05Aabb");
        int shaderSlots = int.Parse(arr.Groups[1].Value, CultureInfo.InvariantCulture);

        shaderSlots.Should().Be(windowsCap,
            "tablica uniformów mniejsza od capa = cele bez AABB (niewidoczne); większa = marnowane "
            + "uniformy i pętla po pustych slotach w każdym fragmencie");

        Match loop = Regex.Match(src, @"for \(int i = 0; i < (\d+); i\+\+\) \{\\n"" \+\s*\n\s*""\s*vec2 mn = uDet05Aabb");
        if (loop.Success)
        {
            int loopBound = int.Parse(loop.Groups[1].Value, CultureInfo.InvariantCulture);
            loopBound.Should().Be(windowsCap, "pętla wyboru celi w shaderze musi przejść CAŁĄ tablicę");
        }
    }
}
