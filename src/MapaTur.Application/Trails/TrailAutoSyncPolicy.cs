using System.Globalization;

using MapaTur.Domain.Geography;

namespace MapaTur.Application.Trails;

/// <summary>
/// Polityka automatycznej synchronizacji szlaków (2026-08-05, żądanie usera: „wszystkie szlaki powinny
/// się pobierać same"). Dotąd automat pobierał szlaki TYLKO przy pustej bazie — baza z 26 czerwca nigdy
/// nie dostała Rohaczy (brak Tatranskiej magistrali, żółtych 8572/8617, zielonego 5568 → planer
/// „prowadził dookoła"; ta sama klasa danych co żleb Kulczyńskiego). Sync obejmuje CAŁE Tatry naraz
/// (jeden box, kompletne relacje, upsert — nic nie kasuje), raz na tydzień przy starcie; znacznik czasu
/// jest przechowywany w ustawieniach jako string ISO (round-trip przez <see cref="Stamp"/>/<see cref="Parse"/>).
/// </summary>
public static class TrailAutoSyncPolicy
{
    /// <summary>Maksymalny wiek ostatniego syncu, po którym pobieramy ponownie.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Region C = 19.50,49.10 → 20.40,49.40 — zasięg naszej mapy (ten sam box co cel „całe Tatry 5 cm").
    /// Jeden box zamiast kadru widoku: to kadry-wycinki gubiły łączniki (żleb, Rohacze).
    /// </summary>
    public static readonly MapBounds TatraBounds = new(new GeoPoint(49.10, 19.50), new GeoPoint(49.40, 20.40));

    /// <summary>Czy przy tym starcie należy dociągnąć szlaki (nigdy nie było syncu / za stary / zegar cofnięty).</summary>
    public static bool ShouldSync(DateTime? lastSyncUtc, DateTime nowUtc)
    {
        if (lastSyncUtc is not { } last)
        {
            return true;
        }

        // Znacznik „z przyszłości" (przestawiony zegar) nie może zamrozić syncu na zawsze.
        return last > nowUtc || nowUtc - last >= MaxAge;
    }

    /// <summary>Znacznik czasu syncu do zapisania w ustawieniach (ISO-8601, round-trip).</summary>
    public static string Stamp(DateTime nowUtc) => nowUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>Odczyt znacznika z ustawień; null przy braku/śmieciu (⇒ sync startuje).</summary>
    public static DateTime? Parse(string? raw)
        => DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed)
            ? parsed
            : null;
}