using System.Globalization;
using System.Text.RegularExpressions;

using FluentAssertions;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Cap warstw det05 musi być spójny z tablicą <c>cell→slot</c>. Tablica ma load factor ≤50%, a shader
/// wykonuje ograniczony lookup zamiast pętli po wszystkich rezydentach.
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
/// porównywanego z <c>cell.Layer</c>, hash mieścił cały cap oraz żeby nie wrócił liniowy skan AABB.
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

    private static string ViewSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "MapaTur.App", "Views", "Terrain3DView.xaml.cs");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Nie znaleziono Terrain3DView.xaml.cs idąc w górę od " + AppContext.BaseDirectory);
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
    public void Hash_w_shaderze_miesci_cap_i_nie_ma_liniowego_skanu_rezydentow()
    {
        string src = RendererSource();

        Match cap = Regex.Match(src, @"Det05HardCapCells\s*=\s*OperatingSystem\.IsWindows\(\)\s*\?\s*(\d+)");
        cap.Success.Should().BeTrue("stała Det05HardCapCells musi być czytelna ze źródła");
        int windowsCap = int.Parse(cap.Groups[1].Value, CultureInfo.InvariantCulture);

        Match tableConst = Regex.Match(src, @"Det05CellHashSize\s*=\s*(\d+)");
        tableConst.Success.Should().BeTrue();
        int tableSize = int.Parse(tableConst.Groups[1].Value, CultureInfo.InvariantCulture);
        Match shaderTable = Regex.Match(src, @"uniform ivec4 uDet05CellHash\[(\d+)\]");
        shaderTable.Success.Should().BeTrue("shader musi deklarować hash komórka→slot");
        int shaderSize = int.Parse(shaderTable.Groups[1].Value, CultureInfo.InvariantCulture);

        shaderSize.Should().Be(tableSize);
        tableSize.Should().BeGreaterThanOrEqualTo(windowsCap * 2,
            "load factor ≤50% utrzymuje krótki, stały probe przy pełnym capie");
        src.Should().NotContain("uDet05Aabb");
        src.Should().NotContain("for (int i = 0; i < 192",
            "koszt fragmentu nie może rosnąć razem z capem rezydencji");

        Match probeConst = Regex.Match(src, @"DetailCellHashMaxProbe\s*=\s*(\d+)");
        probeConst.Success.Should().BeTrue();
        string probe = probeConst.Groups[1].Value;
        Regex.Matches(src, @"for \(int p = 0; p < (\d+); p\+\+\)")
            .Select(m => m.Groups[1].Value)
            .Should().OnlyContain(v => v == probe,
                "CPU builder i oba lookupy shaderowe muszą mieć ten sam twardy limit probe");
    }

    [Fact]
    public void Tail_first_musi_ograniczac_sampling_do_gotowego_mipu()
    {
        string src = RendererSource();

        src.Should().Contain("TryAssembleTailWindow(",
            "det05 ma najpierw czytać fizycznie pierwszy tail .opk zamiast czekać na wszystkie strony 5 cm");
        src.Should().Contain("TryAssembleFineWindow(",
            "drugi etap ma czytać tylko strony 5/10 cm, bez ponownego I/O i uploadu gotowego tail-a");
        src.Should().Contain("cell.MinimumLod",
            "każda rezydentna cela musi przekazać shaderowi najdrobniejszy faktycznie wgrany mip");
        src.Should().Contain("bestHit.y & 255",
            "alpha promocji musi być odpakowana z dolnego bajtu metadanych hasha");
        src.Should().Contain("bestHit.y >> 8",
            "shader musi odpakować minimum LOD i nigdy nie próbować niegotowych mipów 0-1");
        src.Should().Contain("max(float(minimumLod), implicitLod)",
            "tail-ready sampling musi clampować żądany LOD do gotowego poziomu");
    }

    [Fact]
    public void Izolowany_test_kompaktowego_taila_musi_miec_override_katalogu_opk()
    {
        string view = ViewSource();

        view.Should().Contain("MAPATUR_DET05_OPK_DIR",
            "test nowego layoutu danych nie może podmieniać wspólnego AppData ani produkcyjnego det05");
        view.Should().Contain("System.IO.Path.GetFullPath(det05Override)",
            "override ma wskazywać jawny izolowany katalog .opk");
    }

    [Fact]
    public void Odrzucony_tail_first_runtime_musi_byc_domyslnie_wylaczony()
    {
        string src = RendererSource();

        src.Should().Contain("private static readonly bool Det05TailFirstRuntimeEnabled = false",
            "zmierzony kandydat c8102e9 ładował panoramę 35,2 s + 22,0 s i nie może pozostać aktywną ścieżką");
        src.Should().Contain("if (!Det05TailFirstRuntimeEnabled)",
            "domyślny runtime ma wrócić do jednego pełnego odczytu .opk na celę");
    }
}