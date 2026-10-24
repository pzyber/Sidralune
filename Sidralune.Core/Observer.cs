using System;
using System.Collections.Generic;
using Sidralune.Core.Mathematics;

namespace Sidralune.Core
{
    /// <summary>An observing site.</summary>
    public class Observer
    {
        public string Name { get; set; } = "Greenwich";
        /// <summary>Geodetic latitude, degrees (north positive).</summary>
        public double LatitudeDeg { get; set; } = 51.4769;
        /// <summary>Longitude, degrees (east positive).</summary>
        public double LongitudeDeg { get; set; } = -0.0005;
        /// <summary>Height above the WGS84 ellipsoid, metres.</summary>
        public double ElevationM { get; set; } = 46;
        /// <summary>Windows time zone id; null means use the local machine time zone.</summary>
        public string TimeZoneId { get; set; }
        public double TemperatureC { get; set; } = 10;
        public double PressureHpa { get; set; } = 1010;
        public double RelativeHumidity { get; set; } = 0.5;
        /// <summary>Zenith sky brightness from artificial light (mag/arcsec²); 22.0 = pristine.</summary>
        public double SqmZenith { get; set; } = 21.3;
        /// <summary>Bortle scale (1-9), informational; derived from SQM if not set.</summary>
        public int Bortle { get; set; } = 4;
        /// <summary>Extinction coefficient in V (mag/airmass).</summary>
        public double ExtinctionV { get; set; } = 0.20;
        /// <summary>Typical seeing FWHM, arcsec.</summary>
        public double SeeingArcsec { get; set; } = 2.5;
        /// <summary>Local horizon profile: azimuth (deg) to altitude (deg) samples.</summary>
        public List<double[]> HorizonProfile { get; set; } = new List<double[]>();
        /// <summary>IAU/MPC observatory code if any.</summary>
        public string MpcCode { get; set; } = "500";

        public double LatRad => LatitudeDeg * AstroMath.Deg2Rad;
        public double LonRad => LongitudeDeg * AstroMath.Deg2Rad;

        public TimeZoneInfo TimeZone
        {
            get
            {
                if (!string.IsNullOrEmpty(TimeZoneId))
                {
                    try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); } catch { }
                }
                return TimeZoneInfo.Local;
            }
        }

        public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);

        public DateTime ToUtc(DateTime local) => TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZone);

        /// <summary>Altitude of the local horizon (degrees) at an azimuth, linear interpolation of the profile.</summary>
        public double HorizonAltitude(double azDeg)
        {
            if (HorizonProfile == null || HorizonProfile.Count == 0) return 0;
            azDeg = AstroMath.NormalizeDeg(azDeg);
            var p = HorizonProfile;
            if (p.Count == 1) return p[0][1];
            for (int i = 0; i < p.Count; i++)
            {
                var a = p[i]; var b = p[(i + 1) % p.Count];
                double a0 = a[0], a1 = b[0];
                if (a1 < a0) a1 += 360;
                double az = azDeg < a0 ? azDeg + 360 : azDeg;
                if (az >= a0 && az <= a1)
                {
                    double t = a1 > a0 ? (az - a0) / (a1 - a0) : 0;
                    return a[1] + (b[1] - a[1]) * t;
                }
            }
            return p[0][1];
        }

        /// <summary>Geocentric position of the observer in the terrestrial frame (km): x towards lon 0, z to pole.</summary>
        public Vec3 GeocentricItrs()
        {
            double f = AstroMath.EarthFlattening, a = AstroMath.EarthRadius_km;
            double lat = LatRad, lon = LonRad, h = ElevationM / 1000.0;
            double e2 = f * (2 - f);
            double sl = Math.Sin(lat), cl = Math.Cos(lat);
            double N = a / Math.Sqrt(1 - e2 * sl * sl);
            return new Vec3((N + h) * cl * Math.Cos(lon), (N + h) * cl * Math.Sin(lon), (N * (1 - e2) + h) * sl);
        }

        public static int SqmToBortle(double sqm)
        {
            if (sqm >= 21.99) return 1;
            if (sqm >= 21.89) return 2;
            if (sqm >= 21.69) return 3;
            if (sqm >= 20.49) return 4;
            if (sqm >= 19.50) return 5;
            if (sqm >= 18.94) return 6;
            if (sqm >= 18.38) return 7;
            if (sqm >= 17.80) return 8;
            return 9;
        }

        public static double BortleToSqm(int b)
        {
            // a typical zenith SQM inside each class's range in SqmToBortle (index 0 unused)
            double[] v = { 22.0, 22.0, 21.95, 21.8, 20.6, 19.8, 19.1, 18.6, 18.1, 17.5 };
            return v[AstroMath.Clamp(b, 1, 9)];
        }

        public Observer Clone() => (Observer)MemberwiseClone();

        public override string ToString() =>
            string.Format("{0} ({1}, {2})", Name, AstroMath.FormatDms(LatitudeDeg, 0), AstroMath.FormatDms3(LongitudeDeg, 0));
    }
}
