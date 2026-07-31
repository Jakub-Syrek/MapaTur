namespace MapaTur.Application.Terrain;

/// <summary>
/// Migracja pakietu `.opk` ze starego tail-a L1↓ do kompaktowego L2↓ bez ponownego dekodowania
/// ortofotomapy i kodowania BC1. Zwykłe strony są już gotowym BC1, więc pełna migracja jedynie
/// przepisuje ich zweryfikowane payloady; tryb tail-only służy do małego,
/// izolowanego testu pierwszego etapu streamingu.
/// </summary>
public static class OrthoCompactTailMigrator
{
    public static bool TryMigrate(
        string sourcePath,
        string destinationPath,
        int expectedCellPx,
        bool tailOnly,
        int zstdLevel = 0)
    {
        using OrthoPagePack? source = OrthoPagePack.Open(sourcePath, expectedCellPx);
        if (source is null)
        {
            return false;
        }

        OrthoPagePack.Entry? tailEntry = source.Entries
            .FirstOrDefault(e => e.PageId == OrthoPagePack.TailPageId);
        if (tailEntry is null
            || tailEntry.Value.PageId != OrthoPagePack.TailPageId
            || tailEntry.Value.Level is < 1 or > 2
            || !source.TryReadPage(OrthoPagePack.TailPageId, out byte[] sourceTail))
        {
            return false;
        }

        int skipBytes = tailEntry.Value.Level == 1
            ? Bc1Encoder.EncodedSize(expectedCellPx / 2, expectedCellPx / 2)
            : 0;
        if (sourceTail.Length <= skipBytes)
        {
            return false;
        }

        byte[] compactTail = sourceTail.AsSpan(skipBytes).ToArray();
        var pages = new List<OrthoPagePack.PageData>(tailOnly ? 1 : source.PageCount)
        {
            new(
                OrthoPagePack.TailPageId,
                Level: 2,
                compactTail,
                tailEntry.Value.SrcHash),
        };

        if (!tailOnly)
        {
            foreach (OrthoPagePack.Entry entry in source.Entries)
            {
                if (entry.PageId == OrthoPagePack.TailPageId)
                {
                    continue;
                }

                if (!source.TryReadPage(entry.PageId, out byte[] payload))
                {
                    return false;
                }

                pages.Add(new OrthoPagePack.PageData(entry.PageId, entry.Level, payload, entry.SrcHash));
            }
        }

        OrthoPagePack.Write(destinationPath, expectedCellPx, pages, zstdLevel);
        return true;
    }
}