using FluentAssertions;

using MapaTur.Application.Trails;

namespace MapaTur.Application.Tests.Trails;

/// <summary>
/// Polityka auto-syncu szlaków (2026-08-05, żądanie usera: „wszystkie szlaki powinny się pobierać same").
/// Dotąd automat pobierał szlaki TYLKO przy pustej bazie — baza z 26 czerwca nigdy nie dostała Rohaczy
/// (brak Tatranskiej magistrali, żółtych 8572/8617, zielonego 5568; planer „prowadził dookoła" — ta sama
/// klasa co żleb Kulczyńskiego). Polityka: sync całych Tatr przy starcie, gdy ostatni jest starszy niż
/// tydzień albo nie było go wcale; znacznik czasu wędruje przez ustawienia jako string ISO (round-trip).
/// </summary>
public sealed class TrailAutoSyncPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShouldSync_NeverSyncedBefore_IsTrue()
    {
        TrailAutoSyncPolicy.ShouldSync(lastSyncUtc: null, Now).Should().BeTrue();
    }

    [Fact]
    public void ShouldSync_FreshSync_IsFalse()
    {
        TrailAutoSyncPolicy.ShouldSync(Now.AddDays(-1), Now).Should().BeFalse();
    }

    [Fact]
    public void ShouldSync_OlderThanAWeek_IsTrue()
    {
        TrailAutoSyncPolicy.ShouldSync(Now.AddDays(-8), Now).Should().BeTrue();
    }

    [Fact]
    public void ShouldSync_ClockSkewFromTheFuture_IsTrue()
    {
        // Znacznik „z przyszłości" (przestawiony zegar) nie może zamrozić syncu na zawsze.
        TrailAutoSyncPolicy.ShouldSync(Now.AddDays(2), Now).Should().BeTrue();
    }

    [Fact]
    public void StampAndParse_RoundTripThroughTheSettingsString()
    {
        string stamp = TrailAutoSyncPolicy.Stamp(Now);

        TrailAutoSyncPolicy.Parse(stamp).Should().Be(Now);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nie-data")]
    public void Parse_GarbageOrMissing_IsNull(string? raw)
    {
        TrailAutoSyncPolicy.Parse(raw).Should().BeNull();
    }

    [Fact]
    public void TatraBounds_CoverTheWholeRegionC()
    {
        // Region C = 19.50,49.10,20.40,49.40 — zasięg naszej mapy (ten sam co cel „całe Tatry 5 cm").
        // Rohacze (Zverovka 49.239,19.714) i wschód (Łomnica ~49.19,20.21) MUSZĄ być w środku.
        TrailAutoSyncPolicy.TatraBounds.SouthWest.Latitude.Should().Be(49.10);
        TrailAutoSyncPolicy.TatraBounds.SouthWest.Longitude.Should().Be(19.50);
        TrailAutoSyncPolicy.TatraBounds.NorthEast.Latitude.Should().Be(49.40);
        TrailAutoSyncPolicy.TatraBounds.NorthEast.Longitude.Should().Be(20.40);
    }
}
