using FluentAssertions;

using MapaTur.Application.Diagnostics;

namespace MapaTur.Application.Tests.Diagnostics;

/// <summary>
/// Task #7 (2026-08-08): tryb nagrywania — okno przycięte do proporcji Insta (4:5 / 9:16) albo
/// jawnego WxH, żeby (a) GameBar nagrywał dokładnie kadr publikacji, (b) mniej pikseli = płynniejszy
/// ruch (teren jest fragment-bound: ~40 ms przy 4,7 Mpx fullscreen). Planner jest czystą funkcją:
/// spec + obszar roboczy monitora → rozmiar KLIENTA okna. Wymiary zawsze PARZYSTE (enkodery H.264
/// odrzucają nieparzyste), preset = największy prostokąt o tej proporcji mieszczący się w obszarze.
/// </summary>
public sealed class RecordFramePlannerTests
{
    [Fact]
    public void preset_4_5_fills_work_area_height_on_ultrawide()
    {
        (int w, int h)? plan = RecordFramePlanner.Plan("4:5", workW: 3440, workH: 1360);

        plan.Should().NotBeNull();
        plan!.Value.h.Should().Be(1360);
        plan.Value.w.Should().Be(1088); // 1360*4/5 = 1088 — parzyste z natury
    }

    [Fact]
    public void preset_9_16_fits_height_and_rounds_width_down_to_even()
    {
        (int w, int h)? plan = RecordFramePlanner.Plan("9:16", 3440, 1360);

        plan.Should().NotBeNull();
        plan!.Value.h.Should().Be(1360);
        plan.Value.w.Should().Be(764); // 1360*9/16 = 765 → w dół do parzystego
    }

    [Fact]
    public void preset_wider_than_work_area_fits_width_instead()
    {
        // Obszar niemal kwadratowy: 4:5 ogranicza już szerokość, nie wysokość.
        (int w, int h)? plan = RecordFramePlanner.Plan("4:5", 1000, 1400);

        plan.Should().NotBeNull();
        plan!.Value.w.Should().Be(1000);
        plan.Value.h.Should().Be(1250); // 1000*5/4
    }

    [Fact]
    public void explicit_size_is_used_verbatim_when_it_fits()
    {
        (int w, int h)? plan = RecordFramePlanner.Plan("1080x1350", 3440, 1400);

        plan.Should().Be((1080, 1350));
    }

    [Fact]
    public void explicit_size_is_scaled_down_to_fit_preserving_aspect()
    {
        // 1080x1920 nie mieści się na monitorze 1440p — skala w dół z zachowaniem 9:16, parzyście.
        (int w, int h)? plan = RecordFramePlanner.Plan("1080x1920", 2560, 1400);

        plan.Should().NotBeNull();
        plan!.Value.h.Should().Be(1400);
        plan.Value.w.Should().Be(786); // 1400*1080/1920 = 787.5 → w dół do parzystego
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("blah")]
    [InlineData("4:0")]
    [InlineData("-1080x1350")]
    [InlineData("1080x")]
    public void invalid_or_empty_spec_returns_null(string? spec)
    {
        RecordFramePlanner.Plan(spec, 3440, 1360).Should().BeNull();
    }

    [Fact]
    public void degenerate_work_area_returns_null()
    {
        RecordFramePlanner.Plan("4:5", 0, 1360).Should().BeNull();
        RecordFramePlanner.Plan("4:5", 3440, -5).Should().BeNull();
    }

    [Fact]
    public void result_dimensions_are_always_even()
    {
        foreach ((string spec, int ww, int wh) in new[]
                 { ("4:5", 1367, 1361), ("9:16", 1367, 1361), ("333x777", 1367, 1361), ("9:16", 2559, 1399) })
        {
            (int w, int h)? plan = RecordFramePlanner.Plan(spec, ww, wh);
            plan.Should().NotBeNull(spec);
            (plan!.Value.w % 2).Should().Be(0, spec);
            (plan.Value.h % 2).Should().Be(0, spec);
        }
    }
}