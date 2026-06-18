using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Behaviour pinning for <see cref="CentralEuropeanTime"/>: the DST-aware UTC offset that lets the night-sky
/// model turn the app's local wall-clock time slider into the correct Julian Date. Standard time CET is
/// UTC+1; summer time CEST is UTC+2 from the last Sunday of March to the last Sunday of October. The
/// transition dates are recomputed per year (2025 and 2026 fall on different days), so the table guards that
/// the last-Sunday calculation is real, not hardcoded.
/// </summary>
public sealed class CentralEuropeanTimeTests
{
    [Theory]
    // Clear seasons.
    [InlineData(2026, 1, 15, 1.0)]   // January — winter, CET
    [InlineData(2026, 2, 28, 1.0)]   // late February — still winter
    [InlineData(2026, 7, 15, 2.0)]   // July — summer, CEST
    [InlineData(2026, 4, 1, 2.0)]    // April — summer
    [InlineData(2026, 9, 30, 2.0)]   // late September — still summer
    [InlineData(2026, 11, 1, 1.0)]   // November — winter
    [InlineData(2026, 12, 25, 1.0)]  // December — winter
    // 2026 transitions: last Sunday of March = 29th, last Sunday of October = 25th.
    [InlineData(2026, 3, 28, 1.0)]   // Saturday before spring-forward — still CET
    [InlineData(2026, 3, 29, 2.0)]   // spring-forward Sunday — CEST
    [InlineData(2026, 10, 24, 2.0)]  // Saturday before fall-back — still CEST
    [InlineData(2026, 10, 25, 1.0)]  // fall-back Sunday — back to CET
    // 2025 transitions fall on different days (Mar 30, Oct 26) — guards the per-year computation.
    [InlineData(2025, 3, 29, 1.0)]   // day before 2025 spring-forward
    [InlineData(2025, 3, 30, 2.0)]   // 2025 spring-forward Sunday
    [InlineData(2025, 10, 25, 2.0)]  // day before 2025 fall-back
    [InlineData(2025, 10, 26, 1.0)]  // 2025 fall-back Sunday
    public void UtcOffsetHours_ResolvesCetVersusCest_ForDate(int year, int month, int day, double expected)
    {
        double offset = CentralEuropeanTime.UtcOffsetHours(year, month, day);

        offset.Should().Be(expected);
    }
}