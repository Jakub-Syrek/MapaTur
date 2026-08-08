namespace MapaTur.Application.Terrain;

/// <summary>
/// Mgła wysokościowa (task #4, 2026-08-08): gęstość ρ(z) = ρ0·exp(−(z−ref)/H), z KLAPĄ ×3 poniżej
/// referencji (bez niej kotliny Podhala przy exp(+3) ≈ ×20 toną w zupie). Głębia optyczna wzdłuż
/// odcinka kamera→fragment liczona analitycznie, kawałkami przez strefę klapy — shader terenu
/// (Terrain3DGlRenderer, funkcja fogOpticalDepth) LUSTRZE tę matematykę 1:1; zmiany TYLKO tutaj,
/// z testami, potem przepisanie lustra. Jednostki: dowolne, byle spójne (renderer podaje world-Z
/// = metry n.p.m. × przewyższenie i H×przewyższenie — wzorzec jak PerennialFirn.LineMeters).
/// Domyślne stałe: ref 900 m (dna dolin tatrzańskich), H 450 m — grań 2200 m ma ~5,6% gęstości dna.
/// </summary>
public static class HeightFog
{
    /// <summary>Wysokość odniesienia (m n.p.m.) — tu gęstość równa się bazowej ρ0 z Atmosphere.FogDensity.</summary>
    public const double ReferenceAltitudeMeters = 900.0;

    /// <summary>Skala wysokościowa H (m) — co H w górę gęstość maleje e-krotnie.</summary>
    public const double ScaleHeightMeters = 450.0;

    /// <summary>Klapa gęstości poniżej referencji (×ρ0) — patrz opis klasy.</summary>
    public const double MaxBoostBelowReference = 3.0;

    /// <summary>Gęstość mgły na wysokości <paramref name="z"/> (z klapą).</summary>
    public static double Density(double z, double rho0, double refZ, double scaleH)
        => rho0 * Math.Min(MaxBoostBelowReference, Math.Exp(-(z - refZ) / scaleH));

    /// <summary>
    /// Głębia optyczna wzdłuż odcinka o długości <paramref name="dist"/> od wysokości
    /// <paramref name="camZ"/> do <paramref name="fragZ"/>. Analitycznie: nad strefą klapy całka
    /// z eksponenty ma formę zamkniętą; pod klapą gęstość jest stała; odcinek przecinający granicę
    /// klapy (zCap = ref − H·ln(klapa)) jest dzielony w punkcie przecięcia.
    /// </summary>
    public static double OpticalDepth(double camZ, double fragZ, double dist, double rho0, double refZ, double scaleH)
    {
        double zCap = refZ - (scaleH * Math.Log(MaxBoostBelowReference));
        double zLo = Math.Min(camZ, fragZ);
        double zHi = Math.Max(camZ, fragZ);
        double dz = zHi - zLo;

        if (dz < 1e-9)
        {
            return Density(camZ, rho0, refZ, scaleH) * dist;
        }

        // Udział długości odcinka przypadający na przedział wysokości [a,b] ⊆ [zLo,zHi]:
        // promień jest prosty, więc długość skaluje się liniowo z pokonanym Δz.
        double lenPerDz = dist / dz;

        double depth = 0.0;

        // Część POD klapą: gęstość stała ρ0·klapa.
        if (zLo < zCap)
        {
            double below = Math.Min(zHi, zCap) - zLo;
            depth += rho0 * MaxBoostBelowReference * below * lenPerDz;
        }

        // Część NAD klapą: ∫ exp(−(z−ref)/H) dz = H·[exp(−(a−ref)/H) − exp(−(b−ref)/H)].
        if (zHi > zCap)
        {
            double a = Math.Max(zLo, zCap);
            double integral = scaleH * (Math.Exp(-(a - refZ) / scaleH) - Math.Exp(-(zHi - refZ) / scaleH));
            depth += rho0 * integral * lenPerDz;
        }

        return depth;
    }

    /// <summary>Współczynnik mgły [0,1]: 1 − exp(−głębia optyczna).</summary>
    public static double FogFactor(double camZ, double fragZ, double dist, double rho0, double refZ, double scaleH)
        => 1.0 - Math.Exp(-OpticalDepth(camZ, fragZ, dist, rho0, refZ, scaleH));
}