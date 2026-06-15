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
                result.Add(ReadPackage(element));
            }

            return new PackageManifest(result);
        }
    }

    private static RegionPackage ReadPackage(JsonElement e) =>
        new(
            Id: ReadString(e, "id"),
            Name: ReadString(e, "name"),
            Layer: ReadEnum<PackageLayer>(e, "layer"),
            Format: ReadEnum<PackageFormat>(e, "format"),
            Version: ReadInt(e, "version"),
            SizeBytes: ReadLong(e, "sizeBytes"),
            Sha256: ReadString(e, "sha256"),
            Url: ReadString(e, "url"));

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

    private static TEnum ReadEnum<TEnum>(JsonElement e, string name)
        where TEnum : struct, Enum
    {
        string raw = ReadString(e, name);
        if (!Enum.TryParse(raw, ignoreCase: true, out TEnum value) || !Enum.IsDefined(value))
        {
            throw new InvalidDataException($"Package entry has an unknown {typeof(TEnum).Name} '{raw}'.");
        }

        return value;
    }
}