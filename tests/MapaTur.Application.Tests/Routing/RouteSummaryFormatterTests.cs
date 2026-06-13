using FluentAssertions;

using MapaTur.Application.Routing;

namespace MapaTur.Application.Tests.Routing;

public sealed class RouteSummaryFormatterTests
{
    [Fact]
    public void Format_KilometreRoute_ShowsKmAscentAndTime()
    {
        // 22.12 km, +1450 m, 6 h 30 m (23400 s).
        string summary = RouteSummaryFormatter.Format(22_120, 1450, 23_400);

        summary.Should().Be("22.1 km · +1450 m · 6:30 h");
    }

    [Fact]
    public void Format_ShortRoute_ShowsMetresAndPadsMinutes()
    {
        // 850 m, +30 m, 15 m (900 s) — sub-kilometre distance stays in metres, minutes zero-padded.
        string summary = RouteSummaryFormatter.Format(850, 30, 900);

        summary.Should().Be("850 m · +30 m · 0:15 h");
    }

    [Fact]
    public void Format_RoundsToWholeMinutes()
    {
        // 1 h 1 m 1 s (3661 s) truncates the trailing seconds to 1:01.
        string summary = RouteSummaryFormatter.Format(5000, 0, 3661);

        summary.Should().Be("5.0 km · +0 m · 1:01 h");
    }
}