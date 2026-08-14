using System.Text.RegularExpressions;

using FluentAssertions;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Task #8 (pełzanie commit GPU): macierz 08-08 wykazała wzrost gpuDed WYŁĄCZNIE przy ruchu kamery,
/// niezależnie od uploadów i alokacji — hipoteza wiodąca to renamy dynamicznych constant bufferów
/// D3D11 per draw (ANGLE: brudne uniformy → MAP_DISCARD). Dyskryminator wymaga LICZBY draw calls
/// w status.json (korelacja nachylenia gpuDed z draws/min) oraz działającej czapy FPS dla orbity
/// harnessu (LOWFPS 08-08 był nieważny: orbita self-invalidowała się do 296 fps mimo FRAME_MS=33).
///
/// Test czyta ŹRÓDŁO jak <see cref="Det05LayerCapConsistencyTests"/>, bo to ta sama topologia ryzyka
/// co cap 48: instrumentacja, która obejmuje 29 z 30 miejsc draw, kłamie CICHO — licznik wygląda
/// na żywy, a korelacja wychodzi fałszywa. Każde nowe miejsce draw MUSI się policzyć albo ten test
/// ma spaść.
/// </summary>
public sealed class DrawCallInstrumentationTests
{
    private static string SourceFile(params string[] relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine([dir.FullName, .. relative]);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            "Nie znaleziono " + Path.Combine(relative) + " idąc w górę od " + AppContext.BaseDirectory);
    }

    private static string RendererSource() =>
        SourceFile("src", "MapaTur.App", "Services", "Terrain3DGlRenderer.cs");

    [Fact]
    public void Kazde_miejsce_draw_w_rendererze_musi_byc_policzone()
    {
        string[] lines = RendererSource().Split('\n');
        var drawCall = new Regex(@"\.Draw(Elements|ArraysInstanced|Arrays)\s*\(");

        var uncounted = new List<string>();
        int drawSites = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            Match m = drawCall.Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }

            // pomiń trafienia w komentarzach (np. „glDrawArraysInstanced" w opisie instancingu)
            int comment = lines[i].IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0 && comment < m.Index)
            {
                continue;
            }

            drawSites++;
            string prev = i > 0 ? lines[i - 1].Trim() : string.Empty;
            if (prev != "GlTrack.CountDraw();")
            {
                uncounted.Add($"linia {i + 1}: {lines[i].Trim()}");
            }
        }

        drawSites.Should().BeGreaterThanOrEqualTo(30,
            "regex musi widzieć wszystkie znane miejsca draw — jeśli spadło, ktoś zmienił składnię wywołań");
        uncounted.Should().BeEmpty(
            "licznik draws w status.json jest dyskryminatorem taska #8 (gpuDed ∝ draw calls); "
            + "miejsce draw bez GlTrack.CountDraw() bezpośrednio nad wywołaniem fałszuje korelację CICHO");
    }

    [Fact]
    public void Status_json_musi_publikowac_licznik_draws()
    {
        string harness = SourceFile("src", "MapaTur.App", "Services", "HarnessDiag.cs");

        harness.Should().Contain("draws = GlTrack.Draws",
            "bench-mem-sampler koreluje nachylenie gpuDed z draws/min wprost z pola status.json");
    }

    [Fact]
    public void Orbita_harnessu_musi_respektowac_czape_FRAME_MS()
    {
        string view = SourceFile("src", "MapaTur.App", "Views", "Terrain3DView.xaml.cs");

        Regex.IsMatch(view, @"HarnessFrameMsCapped\s*=[\s\S]{0,200}MAPATUR_FRAME_MS")
            .Should().BeTrue(
                "czapa orbity musi wynikać z TEGO SAMEGO env co timer animacji — dwa źródła prawdy "
                + "rozjadą się jak literał capa 48");
        Regex.IsMatch(view, @"if \(HarnessFrameMsCapped\)\s*\{\s*return;")
            .Should().BeTrue(
                "LOWFPS 08-08 był nieważny: orbita self-invalidowała się do 296 fps mimo FRAME_MS=33 — "
                + "przy aktywnym override orbita NIE self-invaliduje, kadencję trzyma timer animacji "
                + "(już przeskalowany przez MAPATUR_FRAME_MS)");
    }
}