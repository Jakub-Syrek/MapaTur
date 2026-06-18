namespace MapaTur.Application.Terrain;

/// <summary>
/// The bundled bright-star catalog, baked into C# (no runtime file I/O — same robustness as the baked lake
/// gazetteer; avoids asset-path differences between desktop and device). Mirrors <c>data/stars.csv</c>: the
/// brightest + named stars incl. the Big Dipper (Ursa Major) and Polaris, for a recognizable, labelled sky.
/// The dense faint field can later replace this via scripts/generate-star-catalog.py.
/// </summary>
public static class StarCatalogData
{
    /// <summary>Curated bright/named stars (J2000), brightest first.</summary>
    public static IReadOnlyList<Star> Bundled { get; } = new[]
    {
        new Star(6.7525, -16.7161, -1.46, "Sirius", "Canis Major"),
        new Star(6.3992, -52.6957, -0.74, "Canopus", "Carina"),
        new Star(14.2610, 19.1825, -0.05, "Arcturus", "Bootes"),
        new Star(18.6156, 38.7837, 0.03, "Vega", "Lyra"),
        new Star(5.2782, 45.9980, 0.08, "Capella", "Auriga"),
        new Star(5.2423, -8.2016, 0.13, "Rigel", "Orion"),
        new Star(7.6550, 5.2250, 0.34, "Procyon", "Canis Minor"),
        new Star(5.9195, 7.4070, 0.50, "Betelgeuse", "Orion"),
        new Star(1.6286, -57.2367, 0.46, "Achernar", "Eridanus"),
        new Star(14.0637, -60.3730, 0.61, "Hadar", "Centaurus"),
        new Star(19.8464, 8.8683, 0.77, "Altair", "Aquila"),
        new Star(4.5987, 16.5093, 0.85, "Aldebaran", "Taurus"),
        new Star(16.4901, -26.4320, 1.09, "Antares", "Scorpius"),
        new Star(13.4199, -11.1613, 1.04, "Spica", "Virgo"),
        new Star(7.7553, 28.0262, 1.14, "Pollux", "Gemini"),
        new Star(22.9608, -29.6222, 1.16, "Fomalhaut", "Piscis Austrinus"),
        new Star(20.6905, 45.2803, 1.25, "Deneb", "Cygnus"),
        new Star(10.1395, 11.9672, 1.35, "Regulus", "Leo"),
        new Star(7.5767, 31.8883, 1.58, "Castor", "Gemini"),
        new Star(5.4189, 6.3497, 1.64, "Bellatrix", "Orion"),
        new Star(5.4382, 28.6075, 1.65, "Elnath", "Taurus"),
        new Star(2.5302, 89.2641, 1.98, "Polaris", "Ursa Minor"),
        new Star(11.0621, 61.7510, 1.79, "Dubhe", "Ursa Major"),
        new Star(11.0307, 56.3825, 2.37, "Merak", "Ursa Major"),
        new Star(11.8972, 53.6948, 2.44, "Phecda", "Ursa Major"),
        new Star(12.2570, 57.0326, 3.31, "Megrez", "Ursa Major"),
        new Star(12.9005, 55.9598, 1.77, "Alioth", "Ursa Major"),
        new Star(13.3988, 54.9254, 2.04, "Mizar", "Ursa Major"),
        new Star(13.7923, 49.3133, 1.86, "Alkaid", "Ursa Major"),
    };
}