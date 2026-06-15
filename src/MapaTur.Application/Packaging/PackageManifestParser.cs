using System.Text.Json;

namespace MapaTur.Application.Packaging;

/// <summary>
/// Parses the server's package catalogue (<c>manifest.json</c>) into a <see cref="PackageManifest"/>.
/// Mirrors the project's hand-rolled <see cref="JsonDocument"/> style: navigate with
/// <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> and fail loud with
/// <see cref="InvalidDataException"/> so a malformed manifest surfaces a clear message rather than a default.
/// </summary>
public static class PackageManifestParser
{
    /// <summary>Parses a UTF-8 manifest document.</summary>
    /// <param name="utf8Json">Raw UTF-8 bytes of the manifest.</param>
    /// <returns>The advertised packages, in document order.</returns>
    /// <exception cref="InvalidDataException">The JSON is malformed, the <c>packages</c> array is absent, or
    /// an entry is missing a required field / carries an unknown enum value.</exception>
    public static PackageManifest Parse(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Package manifest is not valid JSON.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("packages", out JsonElement packages)
                || packages.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Package manifest is missing the 'packages' array.");
            }

            var result = new List<RegionPackage>(packages.GetArrayLength());
            foreach (JsonElement element in packages.EnumerateArray())
            {
                RegionPackage? package = ReadPackage(element);
                if (package is not null)
                {
                    result.Add(package);
                }
            }

            return new PackageManifest(result);
        }
    }

    private static RegionPackage? ReadPackage(JsonElement e)
    {
        // Forward-compatible: a package whose layer/format THIS build doesn't recognise is SKIPPED, not fatal —
        // a newer manifest (e.g. a layer added later) must not break an older app. A missing layer/format field
        // is still structural and throws (via ReadString inside TryReadEnum), as do other missing fields.
        if (!TryReadEnum(e, "layer", out PackageLayer layer) || !TryReadEnum(e, "format", out PackageFormat format))
        {
            return null;
        }

        return new(
            Id: ReadString(e, "id"),
            Name: ReadString(e, "name"),
            Layer: layer,
            Format: format,
            Version: ReadInt(e, "version"),
            SizeBytes: ReadLong(e, "sizeBytes"),
            Sha256: ReadString(e, "sha256"),
            Url: ReadString(e, "url"));
    }

    private static string ReadString(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Package entry is missing the string field '{name}'.");
        }

        string? s = v.GetString();
        if (string.IsNullOrEmpty(s))
        {
            throw new InvalidDataException($"Package entry has an empty '{name}'.");
        }

        return s;
    }

    private static int ReadInt(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number
            || !v.TryGetInt32(out int i))
        {
            throw new InvalidDataException($"Package entry is missing the integer field '{name}'.");
        }

        return i;
    }

    private static long ReadLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Number
            || !v.TryGetInt64(out long l))
        {
            throw new InvalidDataException($"Package entry is missing the integer field '{name}'.");
        }

        return l;
    }

    private static bool TryReadEnum<TEnum>(JsonElement e, string name, out TEnum value)
        where TEnum : struct, Enum
    {
        // A missing/empty field is structural → ReadString throws. An unrecognised VALUE returns false so the
        // caller skips the package (forward compatibility), rather than failing the whole manifest.
        string raw = ReadString(e, name);
        return Enum.TryParse(raw, ignoreCase: true, out value) && Enum.IsDefined(value);
    }
}