namespace MapaTur.Application.Terrain;

/// <summary>
/// Apparent equatorial position of the Moon and its illuminated fraction (phase). Uses Schlyter's compact
/// lunar theory with the main periodic perturbations (evection, variation, annual equation, …), accurate to
/// ~1–2 arcmin — ample for a labelled night sky. The illuminated fraction comes from the Moon–Sun ecliptic
/// elongation, so combine with <see cref="SolarPosition"/> for a phase-correct Moon disc and moonlight.
/// </summary>
public static class LunarPosition
{
    private const double DegToRad = Math.PI / 180.0;
    private const double RadToDeg = 180.0 / Math.PI;

    private static double SinD(double deg) => Math.Sin(deg * DegToRad);
    private static double CosD(double deg) => Math.Cos(deg * DegToRad);
    private static double Norm360(double d)
    {
        d %= 360.0;
        return d < 0.0 ? d + 360.0 : d;
    }

    /// <summary>Apparent right ascension (hours, [0,24)) and declination (degrees) of the Moon.</summary>
    public static (double RaHours, double DecDegrees) Equatorial(double julianDate)
    {
        (double lon, double lat) = EclipticLonLat(julianDate);
        double d = julianDate - 2451543.5;
        double ecl = 23.4393 - (3.563e-7 * d);

        double xe = CosD(lon) * CosD(lat);
        double ye = SinD(lon) * CosD(lat);
        double ze = SinD(lat);

        double xeq = xe;
        double yeq = (ye * CosD(ecl)) - (ze * SinD(ecl));
        double zeq = (ye * SinD(ecl)) + (ze * CosD(ecl));

        double ra = Norm360(Math.Atan2(yeq, xeq) * RadToDeg);
        double dec = Math.Asin(zeq) * RadToDeg;
        return (ra / 15.0, dec);
    }

    /// <summary>Illuminated fraction of the Moon's disc in [0,1]: 0 = new, 1 = full.</summary>
    public static double IlluminatedFraction(double julianDate)
    {
        double lonMoon = EclipticLonLat(julianDate).LonDegrees;
        double lonSun = SolarPosition.ApparentLongitudeDegrees(julianDate);
        return (1.0 - CosD(lonMoon - lonSun)) / 2.0;
    }

    /// <summary>Geocentric ecliptic longitude + latitude (degrees) with the main perturbations.</summary>
    public static (double LonDegrees, double LatDegrees) EclipticLonLat(double julianDate)
    {
        double d = julianDate - 2451543.5;

        // Sun elements (for the perturbation arguments).
        double ws = 282.9404 + (4.70935e-5 * d);
        double ms = Norm360(356.0470 + (0.9856002585 * d));

        // Moon orbital elements.
        double n = 125.1228 - (0.0529538083 * d);
        double inc = 5.1454;
        double w = 318.0634 + (0.1643573223 * d);
        const double a = 60.2666;
        const double e = 0.054900;
        double m = Norm360(115.3654 + (13.0649929509 * d));

        // Eccentric anomaly (Kepler, iterate).
        double ea = m + (RadToDeg * e * SinD(m) * (1.0 + (e * CosD(m))));
        for (int it = 0; it < 6; it++)
        {
            double delta = (ea - (RadToDeg * e * SinD(ea)) - m) / (1.0 - (e * CosD(ea)));
            ea -= delta;
            if (Math.Abs(delta) < 1e-6)
            {
                break;
            }
        }

        // Position in the orbital plane → true anomaly + distance (Earth radii).
        double xv = a * (CosD(ea) - e);
        double yv = a * (Math.Sqrt(1.0 - (e * e)) * SinD(ea));
        double v = Norm360(Math.Atan2(yv, xv) * RadToDeg);
        double r = Math.Sqrt((xv * xv) + (yv * yv));

        // Geocentric ecliptic rectangular.
        double xh = r * ((CosD(n) * CosD(v + w)) - (SinD(n) * SinD(v + w) * CosD(inc)));
        double yh = r * ((SinD(n) * CosD(v + w)) + (CosD(n) * SinD(v + w) * CosD(inc)));
        double zh = r * (SinD(v + w) * SinD(inc));

        double lon = Norm360(Math.Atan2(yh, xh) * RadToDeg);
        double lat = Math.Atan2(zh, Math.Sqrt((xh * xh) + (yh * yh))) * RadToDeg;

        // Perturbation arguments.
        double lm = Norm360(n + w + m);  // Moon mean longitude
        double ls = Norm360(ws + ms);    // Sun mean longitude
        double dd = Norm360(lm - ls);    // mean elongation
        double f = Norm360(lm - n);      // argument of latitude

        lon += (-1.274 * SinD(m - (2 * dd)))
            + (0.658 * SinD(2 * dd))
            - (0.186 * SinD(ms))
            - (0.059 * SinD((2 * m) - (2 * dd)))
            - (0.057 * SinD(m - (2 * dd) + ms))
            + (0.053 * SinD(m + (2 * dd)))
            + (0.046 * SinD((2 * dd) - ms))
            + (0.041 * SinD(m - ms))
            - (0.035 * SinD(dd))
            - (0.031 * SinD(m + ms))
            - (0.015 * SinD((2 * f) - (2 * dd)))
            + (0.011 * SinD(m - (4 * dd)));

        lat += (-0.173 * SinD(f - (2 * dd)))
            - (0.055 * SinD(m - f - (2 * dd)))
            - (0.046 * SinD(m + f - (2 * dd)))
            + (0.033 * SinD(f + (2 * dd)))
            + (0.017 * SinD((2 * m) + f));

        return (Norm360(lon), lat);
    }
}