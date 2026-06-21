namespace MapaTur.Application.Localization;

/// <summary>
/// The set of UI languages MapaTur ships and the rules for turning a stored / OS language
/// code into a bare language ("pl" / "en") or a concrete culture name ("pl-PL" / "en-US").
/// Pure and culture-free so it can be unit-tested without a running MAUI host; the App layer
/// applies the result to <see cref="System.Globalization.CultureInfo"/>.
/// </summary>
public static class AppLanguage
{
    /// <summary>Bare code for Polish — the application default.</summary>
    public const string Polish = "pl";

    /// <summary>Bare code for English.</summary>
    public const string English = "en";

    /// <summary>Supported UI languages, in display order. Polish first (the default).</summary>
    public static IReadOnlyList<string> Supported { get; } = new[] { Polish, English };

    /// <summary>
    /// Reduces an arbitrary language / culture code to one of the <see cref="Supported"/> bare codes.
    /// Case-insensitive; strips a region suffix ("en-GB" → "en"). Null, empty or unsupported input
    /// falls back to <see cref="Polish"/> so the app is always readable.
    /// </summary>
    public static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Polish;
        }

        string bare = code.Trim().Split('-')[0].ToLowerInvariant();
        return Supported.Contains(bare) ? bare : Polish;
    }

    /// <summary>
    /// Maps any language / culture code to the concrete culture name the UI culture should be set to,
    /// applying the same <see cref="Normalize"/> fallback. Use to build a
    /// <see cref="System.Globalization.CultureInfo"/>.
    /// </summary>
    public static string ToCultureName(string? code) => Normalize(code) switch
    {
        English => "en-US",
        _ => "pl-PL",
    };
}