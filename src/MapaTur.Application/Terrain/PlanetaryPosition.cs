namespace MapaTur.Application.Terrain;

/// <summary>The naked-eye planets the night sky labels.</summary>
public enum Planet
{
    Mercury,
    Venus,
    Mars,
    Jupiter,
    Saturn,
}

/// <summary>
/// Geocentric apparent equatorial position of the naked-eye planets — Schlyter's heliocentric orbital
/// elements (linear in the day number) solved per planet, offset by the Sun's geocentric vector to get the
/// geocentric direction, then rotated to the equator. Accurate to a few arcmin (no planet–planet
/// perturbations), ample for a labelled sky. Combine with <see cref="CelestialCoordinates.EquatorialToWorld"/>.
/// </summary>
public static class PlanetaryPosition
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

    /// <summary>Apparent geocentric right ascension (hours, [0,24)) and declination (degrees).</summary>
    public static (double RaHours, double DecDegrees) Equatorial(Planet planet, double julianDate)
    {
        double d = julianDate - 2451543.5;
        (double n, double i, double w, double a, double e, double m) = Elements(planet, d);

        (double xh, double yh, double zh) = Heliocentric(n, i, w, a, e, m);
        (double xs, double ys) = SunRectangular(d);

        double xg = xh + xs;
        double yg = yh + ys;
        double zg = zh;

        double ecl = 23.4393 - (3.563e-7 * d);
        double xe = xg;
        double ye = (yg * CosD(ecl)) - (zg * SinD(ecl));
        double ze = (yg * SinD(ecl)) + (zg * CosD(ecl));

        double ra = Norm360(Math.Atan2(ye, xe) * RadToDeg);
        double dec = Math.Atan2(ze, Math.Sqrt((xe * xe) + (ye * ye))) * RadToDeg;
        return (ra / 15.0, dec);
    }

    private static (double Xh, double Yh, double Zh) Heliocentric(double n, double i, double w, double a, double e, double m)
    {
        double ea = SolveKepler(m, e);
        double xv = a * (CosD(ea) - e);
        double yv = a * Math.Sqrt(1.0 - (e * e)) * SinD(ea);
        double v = Norm360(Math.Atan2(yv, xv) * RadToDeg);
        double r = Math.Sqrt((xv * xv) + (yv * yv));

        double xh = r * ((CosD(n) * CosD(v + w)) - (SinD(n) * SinD(v + w) * CosD(i)));
        double yh = r * ((SinD(n) * CosD(v + w)) + (CosD(n) * SinD(v + w) * CosD(i)));
        double zh = r * (SinD(v + w) * SinD(i));
        return (xh, yh, zh);
    }

    private static (double Xs, double Ys) SunRectangular(double d)
    {
        double w = 282.9404 + (4.70935e-5 * d);
        double e = 0.016709 - (1.151e-9 * d);
        double m = Norm360(356.0470 + (0.9856002585 * d));
        double ea = SolveKepler(m, e);
        double xv = CosD(ea) - e;
        double yv = Math.Sqrt(1.0 - (e * e)) * SinD(ea);
        double v = Norm360(Math.Atan2(yv, xv) * RadToDeg);
        double r = Math.Sqrt((xv * xv) + (yv * yv));
        double lon = v + w;
        return (r * CosD(lon), r * SinD(lon));
    }

    private static double SolveKepler(double mDegrees, double e)
    {
        double ea = mDegrees + (RadToDeg * e * SinD(mDegrees) * (1.0 + (e * CosD(mDegrees))));
        for (int it = 0; it < 8; it++)
        {
            double delta = (ea - (RadToDeg * e * SinD(ea)) - mDegrees) / (1.0 - (e * CosD(ea)));
            ea -= delta;
            if (Math.Abs(delta) < 1e-7)
            {
                break;
            }
        }
        return ea;
    }

    private static (double N, double I, double W, double A, double E, double M) Elements(Planet planet, double d) => planet switch
    {
        Planet.Mercury => (Norm360(48.3313 + (3.24587e-5 * d)), 7.0047 + (5.00e-8 * d), Norm360(29.1241 + (1.01444e-5 * d)), 0.387098, 0.205635 + (5.59e-10 * d), Norm360(168.6562 + (4.0923344368 * d))),
        Planet.Venus => (Norm360(76.6799 + (2.46590e-5 * d)), 3.3946 + (2.75e-8 * d), Norm360(54.8910 + (1.38374e-5 * d)), 0.723330, 0.006773 - (1.302e-9 * d), Norm360(48.0052 + (1.6021302244 * d))),
        Planet.Mars => (Norm360(49.5574 + (2.11081e-5 * d)), 1.8497 - (1.78e-8 * d), Norm360(286.5016 + (2.92961e-5 * d)), 1.523688, 0.093405 + (2.516e-9 * d), Norm360(18.6021 + (0.5240207766 * d))),
        Planet.Jupiter => (Norm360(100.4542 + (2.76854e-5 * d)), 1.3030 - (1.557e-7 * d), Norm360(273.8777 + (1.64505e-5 * d)), 5.20256, 0.048498 + (4.469e-9 * d), Norm360(19.8950 + (0.0830853001 * d))),
        Planet.Saturn => (Norm360(113.6634 + (2.38980e-5 * d)), 2.4886 - (1.081e-7 * d), Norm360(339.3939 + (2.97661e-5 * d)), 9.55475, 0.055546 - (9.499e-9 * d), Norm360(316.9670 + (0.0334442282 * d))),
        _ => throw new ArgumentOutOfRangeException(nameof(planet)),
    };
}