using MapaTur.Application.Diagnostics;

using Xunit;

namespace MapaTur.Application.Tests.Diagnostics;

public class UiScriptParserTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void should_return_empty_when_script_missing(string? script)
    {
        Assert.Empty(UiScriptParser.Parse(script));
    }

    [Fact]
    public void should_parse_single_step()
    {
        var steps = UiScriptParser.Parse("20:6");

        UiScriptStep step = Assert.Single(steps);
        Assert.Equal(20.0, step.AtSeconds);
        Assert.Equal(6, step.Section);
    }

    [Fact]
    public void should_sort_steps_by_time()
    {
        var steps = UiScriptParser.Parse("30:0, 20:6, 45:1");

        Assert.Equal(3, steps.Count);
        Assert.Equal(new UiScriptStep(20.0, 6), steps[0]);
        Assert.Equal(new UiScriptStep(30.0, 0), steps[1]);
        Assert.Equal(new UiScriptStep(45.0, 1), steps[2]);
    }

    [Fact]
    public void should_parse_fractional_seconds_with_invariant_dot()
    {
        var steps = UiScriptParser.Parse("12.5:3");

        UiScriptStep step = Assert.Single(steps);
        Assert.Equal(12.5, step.AtSeconds);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("20")]
    [InlineData("20:x")]
    [InlineData("-5:2")]
    [InlineData("10:7")]
    [InlineData("10:-1")]
    public void should_skip_invalid_entries(string entry)
    {
        Assert.Empty(UiScriptParser.Parse(entry));
    }

    [Fact]
    public void should_expand_stress_spec_into_alternating_steps()
    {
        var steps = UiScriptParser.ParseStress("60:4:300");

        Assert.Equal(4, steps.Count);
        Assert.Equal(60.0, steps[0].AtSeconds);
        Assert.Equal(60.3, steps[1].AtSeconds, 3);
        Assert.Equal(60.6, steps[2].AtSeconds, 3);
        Assert.Equal(60.9, steps[3].AtSeconds, 3);
    }

    [Fact]
    public void should_alternate_open_and_close_in_stress_spec()
    {
        var steps = UiScriptParser.ParseStress("10:6:250");

        Assert.Equal("1,0,2,0,3,0", string.Join(',', steps.Select(s => s.Section)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("60:4")]
    [InlineData("60:0:300")]
    [InlineData("60:4:0")]
    [InlineData("x:4:300")]
    public void should_return_empty_for_invalid_stress_spec(string? spec)
    {
        Assert.Empty(UiScriptParser.ParseStress(spec));
    }

    [Fact]
    public void should_keep_valid_entries_when_mixed_with_invalid()
    {
        var steps = UiScriptParser.Parse("garbage,20:6,99:9");

        UiScriptStep step = Assert.Single(steps);
        Assert.Equal(20.0, step.AtSeconds);
        Assert.Equal(6, step.Section);
    }
}