using System.Numerics;

namespace MapaTur.Application.Terrain;

/// <summary>Ugotowany stan modelu HW dla jednej chwili: 9 współczynników + norma radiancji na kanał RGB.
/// <see cref="Radiance"/> ewaluuje rozszerzoną formułę Pereza — shader nieba LUSTRZE ją 1:1 (uniformy
/// z <see cref="ConfigVec3"/>/<see cref="RadianceVec3"/>).</summary>
public sealed class HosekSkyState
{
    /// <summary>configs[kanał][param 0..8] — kanały: 0=R, 1=G, 2=B.</summary>
    public double[][] Configs { get; }

    /// <summary>Norma radiancji na kanał (mnożnik wyniku formuły).</summary>
    public double[] RadianceNorm { get; }

    internal HosekSkyState(double[][] configs, double[] radianceNorm)
    {
        Configs = configs;
        RadianceNorm = radianceNorm;
    }

    /// <summary>Parametr <paramref name="i"/> (0..8) jako wektor (R,G,B) — do uniformów vec3.</summary>
    public Vector3 ConfigVec3(int i)
        => new((float)Configs[0][i], (float)Configs[1][i], (float)Configs[2][i]);

    /// <summary>Norma radiancji jako (R,G,B).</summary>
    public Vector3 RadianceVec3()
        => new((float)RadianceNorm[0], (float)RadianceNorm[1], (float)RadianceNorm[2]);

    /// <summary>
    /// Radiancja RGB dla kąta zenitalnego <paramref name="theta"/> i kąta do słońca <paramref name="gamma"/>
    /// (radiany). LUSTRO ArHosekSkyModel_GetRadianceInternal — UWAGA na indeksy referencji: człon zenitalny
    /// mnoży c[7], a anizotropia Mie w mianowniku to c[8].
    /// </summary>
    public (double R, double G, double B) Radiance(double theta, double gamma)
    {
        // Podłoga 0.05 (~3° nad horyzontem), nie 0: exp(k1/(cosθ+0.01)) eksploduje przy cosθ→0 i ostatni
        // stopień ściska się na ekranie do twardej krawędzi, a strefa pod horyzontem dziedziczy ekstremum
        // (zmierzone 08-08 o zachodzie). Shader LUSTRZE tę samą podłogę.
        double cosTheta = Math.Max(0.05, Math.Cos(theta));
        double cosGamma = Math.Cos(gamma);

        double[] result = new double[3];
        for (int c = 0; c < 3; c++)
        {
            double[] k = Configs[c];
            double expM = Math.Exp(k[4] * gamma);
            double rayM = cosGamma * cosGamma;
            double mieM = (1.0 + rayM) / Math.Pow(1.0 + (k[8] * k[8]) - (2.0 * k[8] * cosGamma), 1.5);
            double zenith = Math.Sqrt(cosTheta);

            result[c] = (1.0 + (k[0] * Math.Exp(k[1] / (cosTheta + 0.01))))
                * (k[2] + (k[3] * expM) + (k[5] * rayM) + (k[6] * mieM) + (k[7] * zenith))
                * RadianceNorm[c];
        }

        return (result[0], result[1], result[2]);
    }
}

/// <summary>
/// Task #5 (2026-08-08): analityczny model nieba Hoska–Wilkiego („An Analytic Model for Full Spectral
/// Sky-Dome Radiance", SIGGRAPH 2012) — wersja RGB. LUSTRO referencyjnego ArHosekSkyModel.cc
/// (ArHosekSkyModel_CookConfiguration / CookRadianceConfiguration): kwintyczny Bezier po
/// elevation^(1/3), bilinear po turbidity (całkowita część + reszta) i albedo. Dataset:
/// <see cref="HosekSkyDataset"/> (konwersja mechaniczna, licencja BSD w pliku). Model obowiązuje dla
/// słońca NAD horyzontem — wejścia są clampowane, zmierzch obsługuje shader blendem do toru legacy.
/// </summary>
public static class HosekSky
{
    /// <summary>Gotuje stan dla (turbidity [1,10], albedo [0,1], elewacja słońca w radianach [0, π/2]).</summary>
    public static HosekSkyState Create(double turbidity, double albedo, double solarElevationRadians)
    {
        turbidity = Math.Clamp(turbidity, 1.0, 10.0);
        albedo = Math.Clamp(albedo, 0.0, 1.0);
        double elevation = Math.Clamp(solarElevationRadians, 0.0, Math.PI / 2.0);

        double[][] sets = { HosekSkyDataset.DatasetRGB1, HosekSkyDataset.DatasetRGB2, HosekSkyDataset.DatasetRGB3 };
        double[][] rads = { HosekSkyDataset.DatasetRGBRad1, HosekSkyDataset.DatasetRGBRad2, HosekSkyDataset.DatasetRGBRad3 };

        var configs = new double[3][];
        var radiance = new double[3];
        for (int c = 0; c < 3; c++)
        {
            configs[c] = CookConfiguration(sets[c], turbidity, albedo, elevation);
            radiance[c] = CookRadiance(rads[c], turbidity, albedo, elevation);
        }

        return new HosekSkyState(configs, radiance);
    }

    /// <summary>
    /// Task #6: ambient hemisferyczny — irradiancja kopuły dla normalnej „w górę":
    /// E = ∫ L(θ,γ)·cosθ dω po górnej półkuli (reguła punktu środkowego 16×32; test pinuje zgodność
    /// z gęstą sumą 32×64 w 2%). Azymut słońca nie wpływa na wynik (symetria całki), więc wystarczy
    /// elewacja. Liczone TYLKO przy przegotowaniu stanu (co 0,1° elewacji) — nie per klatkę.
    /// </summary>
    public static (double R, double G, double B) HemisphereIrradiance(HosekSkyState state, double solarElevationRadians)
    {
        double elevation = Math.Clamp(solarElevationRadians, 0.0, Math.PI / 2.0);
        double sunX = Math.Cos(elevation);
        double sunZ = Math.Sin(elevation);

        const int ThetaSteps = 16;
        const int PhiSteps = 32;
        double r = 0, g = 0, b = 0;
        for (int it = 0; it < ThetaSteps; it++)
        {
            double theta = (it + 0.5) / ThetaSteps * (Math.PI / 2.0);
            for (int ip = 0; ip < PhiSteps; ip++)
            {
                double phi = (ip + 0.5) / PhiSteps * (2.0 * Math.PI);
                double cosGamma = (Math.Sin(theta) * Math.Cos(phi) * sunX) + (Math.Cos(theta) * sunZ);
                double gamma = Math.Acos(Math.Clamp(cosGamma, -1.0, 1.0));
                (double lr, double lg, double lb) = state.Radiance(theta, gamma);
                double w = Math.Cos(theta) * Math.Sin(theta) * (Math.PI / 2.0 / ThetaSteps) * (2.0 * Math.PI / PhiSteps);
                r += lr * w;
                g += lg * w;
                b += lb * w;
            }
        }

        return (r, g, b);
    }

    // Kwintyczny Bezier po t = (elewacja / (π/2))^(1/3) — wagi 1,5,10,10,5,1; stride 9 parametrów
    // na punkt kontrolny, 6 punktów na turbidity, 10 turbidities na albedo.
    private static double[] CookConfiguration(double[] dataset, double turbidity, double albedo, double elevation)
    {
        int intTurb = Math.Min((int)turbidity, 10);
        double turbRem = turbidity - intTurb;
        double t = Math.Pow(elevation / (Math.PI / 2.0), 1.0 / 3.0);

        var config = new double[9];
        AccumulateBezier(config, dataset, 9 * 6 * (intTurb - 1), (1.0 - albedo) * (1.0 - turbRem), t, stride: 9);
        AccumulateBezier(config, dataset, (9 * 6 * 10) + (9 * 6 * (intTurb - 1)), albedo * (1.0 - turbRem), t, stride: 9);
        if (intTurb < 10)
        {
            AccumulateBezier(config, dataset, 9 * 6 * intTurb, (1.0 - albedo) * turbRem, t, stride: 9);
            AccumulateBezier(config, dataset, (9 * 6 * 10) + (9 * 6 * intTurb), albedo * turbRem, t, stride: 9);
        }

        return config;
    }

    private static double CookRadiance(double[] dataset, double turbidity, double albedo, double elevation)
    {
        int intTurb = Math.Min((int)turbidity, 10);
        double turbRem = turbidity - intTurb;
        double t = Math.Pow(elevation / (Math.PI / 2.0), 1.0 / 3.0);

        var single = new double[1];
        AccumulateBezier(single, dataset, 6 * (intTurb - 1), (1.0 - albedo) * (1.0 - turbRem), t, stride: 1);
        AccumulateBezier(single, dataset, (6 * 10) + (6 * (intTurb - 1)), albedo * (1.0 - turbRem), t, stride: 1);
        if (intTurb < 10)
        {
            AccumulateBezier(single, dataset, 6 * intTurb, (1.0 - albedo) * turbRem, t, stride: 1);
            AccumulateBezier(single, dataset, (6 * 10) + (6 * intTurb), albedo * turbRem, t, stride: 1);
        }

        return single[0];
    }

    private static void AccumulateBezier(double[] into, double[] dataset, int offset, double weight, double t, int stride)
    {
        if (weight == 0.0)
        {
            return;
        }

        double it = 1.0 - t;
        double w0 = it * it * it * it * it;
        double w1 = 5.0 * it * it * it * it * t;
        double w2 = 10.0 * it * it * it * t * t;
        double w3 = 10.0 * it * it * t * t * t;
        double w4 = 5.0 * it * t * t * t * t;
        double w5 = t * t * t * t * t;

        for (int i = 0; i < into.Length; i++)
        {
            into[i] += weight * ((w0 * dataset[offset + i])
                + (w1 * dataset[offset + stride + i])
                + (w2 * dataset[offset + (2 * stride) + i])
                + (w3 * dataset[offset + (3 * stride) + i])
                + (w4 * dataset[offset + (4 * stride) + i])
                + (w5 * dataset[offset + (5 * stride) + i]));
        }
    }
}