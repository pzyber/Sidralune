using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Sidralune.Core;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.MinorBodies;
using Sidralune.Core.Time;
using Sidralune.Science.Astrometry;

namespace Sidralune.Science.SolarSystem
{
    /// <summary>One astrometric observation of a moving object.</summary>
    public class AstrometricObservation
    {
        public string PackedNumber = "";      // permanent designation (packed), cols 1-5
        public string ProvisionalOrTemp = ""; // provisional or temporary designation, cols 6-12
        public bool Discovery;
        public DateTime TimeUtc;              // mid-exposure
        public double RaDeg, DecDeg;
        public double RaErrArcsec = double.NaN, DecErrArcsec = double.NaN;
        public double Mag = double.NaN;
        public string Band = "G";
        public string AstrometricCatalog = "Gaia3";
        public string Station = "500";
        public string Note = "";
    }

    /// <summary>Astrometric reporting to the Minor Planet Center (obs80 and ADES PSV).</summary>
    public static class MpcReporting
    {
        /// <summary>MPC 80-column format line ("C" = CCD).</summary>
        public static string Obs80(AstrometricObservation o)
        {
            var sb = new StringBuilder();
            sb.Append(Fix(o.PackedNumber, 5));
            sb.Append(Fix(o.ProvisionalOrTemp, 7));
            sb.Append(o.Discovery ? '*' : ' ');
            sb.Append(' ');
            sb.Append('C');
            double day = o.TimeUtc.Day + o.TimeUtc.TimeOfDay.TotalDays;
            sb.Append(o.TimeUtc.ToString("yyyy MM ", CultureInfo.InvariantCulture));
            sb.Append(day.ToString("00.000000", CultureInfo.InvariantCulture).Substring(0, 8));
            sb.Append(' ');
            string ra = AstroMath.FormatHms(o.RaDeg / 15, 3, true).Replace(':', ' ');
            sb.Append(Fix(ra, 12));
            string dec = AstroMath.FormatDms(o.DecDeg, 2, true, true).Replace(':', ' ');
            sb.Append(Fix(dec, 12));
            sb.Append(new string(' ', 9));
            sb.Append(double.IsNaN(o.Mag) ? new string(' ', 5) : o.Mag.ToString("00.0", CultureInfo.InvariantCulture).PadLeft(4) + " ");
            sb.Append(o.Band.Length > 0 ? o.Band[0] : ' ');
            sb.Append(new string(' ', 6));
            sb.Append(Fix(o.Station, 3));
            string line = sb.ToString();
            return line.Length > 80 ? line.Substring(0, 80) : line.PadRight(80);
        }

        private static string Fix(string s, int n) { s = s ?? ""; return s.Length >= n ? s.Substring(0, n) : s.PadRight(n); }

        /// <summary>ADES pipe-separated values with the mandatory header blocks.</summary>
        public static string AdesPsv(IEnumerable<AstrometricObservation> obs, string mpcCode, string observerName, string measurer, string telescope, double apertureM, string design, string detector)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# version=2017");
            sb.AppendLine("# observatory");
            sb.AppendLine("! mpcCode " + mpcCode);
            sb.AppendLine("# submitter");
            sb.AppendLine("! name " + observerName);
            sb.AppendLine("# observers");
            sb.AppendLine("! name " + observerName);
            sb.AppendLine("# measurers");
            sb.AppendLine("! name " + measurer);
            sb.AppendLine("# telescope");
            sb.AppendLine("! aperture " + apertureM.ToString("0.000", CultureInfo.InvariantCulture));
            sb.AppendLine("! design " + design);
            sb.AppendLine("! detector " + detector);
            sb.AppendLine("# software");
            sb.AppendLine("! astrometry Sidralune");
            sb.AppendLine("permID |provID     |trkSub  |mode|stn |obsTime                 |ra         |dec        |rmsRA|rmsDec|astCat|mag  |rmsMag|band|photCat|notes|remarks");
            foreach (var o in obs)
            {
                string perm = string.IsNullOrEmpty(o.PackedNumber) ? "" : MpcFormats.UnpackDesignation(o.PackedNumber);
                sb.AppendLine(string.Join("|", new[]
                {
                    perm.PadRight(7), (o.ProvisionalOrTemp ?? "").PadRight(11), "".PadRight(8), "CCD ", o.Station.PadRight(4),
                    o.TimeUtc.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z  ",
                    o.RaDeg.ToString("0.000000", CultureInfo.InvariantCulture).PadLeft(11), o.DecDeg.ToString("+0.000000;-0.000000", CultureInfo.InvariantCulture).PadLeft(11),
                    double.IsNaN(o.RaErrArcsec) ? "     " : o.RaErrArcsec.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(5),
                    double.IsNaN(o.DecErrArcsec) ? "      " : o.DecErrArcsec.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(6),
                    o.AstrometricCatalog.PadRight(6),
                    double.IsNaN(o.Mag) ? "     " : o.Mag.ToString("0.00", CultureInfo.InvariantCulture).PadLeft(5), "      ", o.Band.PadRight(4), o.AstrometricCatalog.PadRight(7), o.Note.PadRight(5), ""
                }));
            }
            return sb.ToString();
        }

        /// <summary>Measure an object's position at pixel (x, y) on a plate-solved image.</summary>
        public static AstrometricObservation Measure(Wcs wcs, double x, double y, DateTime midUtc, string station)
        {
            double ra, dec;
            wcs.PixelToWorld(x, y, out ra, out dec);
            return new AstrometricObservation { RaDeg = ra, DecDeg = dec, TimeUtc = midUtc, Station = station, RaErrArcsec = wcs.RmsArcsec, DecErrArcsec = wcs.RmsArcsec };
        }

        /// <summary>Diameter (km) from absolute magnitude and geometric albedo: D = 1329/√p × 10^(-H/5).</summary>
        public static double DiameterKm(double H, double albedo = 0.15) => 1329.0 / Math.Sqrt(albedo) * Math.Pow(10, -H / 5);
    }

    public class OccultationEvent
    {
        public MinorBody Body;
        public RefStar Star;
        public AstroTime Time;          // geocentric closest approach
        public double MinSeparationArcsec;
        public double ShadowWidthKm;
        public double DurationSec;      // maximum central duration
        public double MagDrop;
        public double StarMag;
        /// <summary>Ground track: [lat, lon] degrees at regular times, for the shadow centre line.</summary>
        public List<double[]> Path = new List<double[]>();
        /// <summary>Distance (km) of the observer from the centre line, signed; NaN if not computed.</summary>
        public double ObserverOffsetKm = double.NaN;
        public override string ToString() => string.Format("{0} occults {1} (mag {2:0.0}) at {3}, Δmag {4:0.0}, dur {5:0.0}s", Body.DisplayName, Star.Id ?? "star", StarMag, Time, MagDrop, DurationSec);
    }

    /// <summary>Asteroid occultation prediction with Besselian-style fundamental-plane geometry.</summary>
    public static class OccultationPredictor
    {
        /// <summary>Search occultations of catalog stars by a minor body between two dates.</summary>
        public static List<OccultationEvent> Search(MinorBody body, AstroTime start, AstroTime end, IStarCatalogProvider catalog, double magLimit = 13, double albedo = 0.15)
        {
            var events = new List<OccultationEvent>();
            double diam = MpcReporting.DiameterKm(body.H, albedo);
            double step = 1.0 / 24; // 1 hour
            var earth = new Func<double, Vec3>(jd => EphemerisManager.Position(Body.Earth, jd));
            Func<double, Vec3> geo = jd =>
            {
                Vec3 d = Vec3.Zero; double tau = 0;
                for (int i = 0; i < 3; i++) { d = body.PositionForEphemeris(jd - tau) - earth(jd); tau = d.Length * AstroMath.LightTimeAU_days; }
                return d;
            };
            for (double jd = start.JdTT; jd < end.JdTT; jd += step * 6)
            {
                // query stars along the next 6 hours of track
                var a = geo(jd); var b = geo(jd + step * 6);
                double ra1, de1, ra2, de2;
                a.ToSpherical(out ra1, out de1); b.ToSpherical(out ra2, out de2);
                var mid = (a.Normalized() + b.Normalized()).Normalized();
                double mra, mde;
                mid.ToSpherical(out mra, out mde);
                double half = Vec3.AngleBetween(a, b) * AstroMath.Rad2Deg / 2 + 0.02;
                List<RefStar> stars;
                try { stars = catalog.Cone(mra * AstroMath.Rad2Deg, mde * AstroMath.Rad2Deg, half, magLimit, 2000); }
                catch { continue; }
                foreach (var s in stars)
                {
                    var su = Vec3.FromSpherical(s.Ra * AstroMath.Deg2Rad, s.Dec * AstroMath.Deg2Rad);
                    Func<double, double> sep = t => Vec3.AngleBetween(geo(t), su);
                    double tmin = Roots.GoldenMin(sep, jd - step, jd + step * 7, 1e-7);
                    double smin = sep(tmin);
                    var g = geo(tmin);
                    double distKm = g.Length * AstroMath.AU_km;
                    // the shadow touches the Earth if the perpendicular distance of the shadow axis from geocentre < Earth radius
                    double perpKm = Math.Sin(smin) * distKm;
                    if (perpKm > AstroMath.EarthRadius_km + diam / 2) continue;
                    if (events.Any(e => e.Star == s)) continue;
                    var ev = new OccultationEvent
                    {
                        Body = body, Star = s, Time = AstroTime.FromTT(tmin), MinSeparationArcsec = smin * AstroMath.Rad2ArcSec,
                        ShadowWidthKm = diam, StarMag = s.Mag
                    };
                    // shadow velocity on the fundamental plane (km/s)
                    var g2 = geo(tmin + 1.0 / 1440);
                    var perp1 = g - su * Vec3.Dot(g, su); var perp2 = g2 - su * Vec3.Dot(g2, su);
                    double vKms = (perp2 - perp1).Length * AstroMath.AU_km / 60.0;
                    ev.DurationSec = vKms > 0 ? diam / vKms : double.NaN;
                    double ast = body.Magnitude(body.HeliocentricEquatorial(tmin).Length, g.Length, 0.3);
                    ev.MagDrop = 2.5 * Math.Log10((Math.Pow(10, -0.4 * s.Mag) + Math.Pow(10, -0.4 * ast)) / Math.Pow(10, -0.4 * ast));
                    ev.Path = GroundTrack(geo, su, tmin, vKms);
                    events.Add(ev);
                }
            }
            return events.OrderBy(e => e.Time.JdUtc).ToList();
        }

        /// <summary>Centre line: intersect lines parallel to the star direction through the asteroid with the Earth's surface.</summary>
        public static List<double[]> GroundTrack(Func<double, Vec3> geo, Vec3 starDir, double tmid, double vKms)
        {
            var pts = new List<double[]>();
            double halfSpan = vKms > 0 ? (2 * AstroMath.EarthRadius_km / vKms) / 86400.0 : 0.01;
            for (double t = tmid - halfSpan; t <= tmid + halfSpan; t += halfSpan / 60)
            {
                var g = geo(t) * AstroMath.AU_km; // km, geocentric ICRF
                // line: P(s) = g - s * starDir ; |P| = Re (sphere approx), choose the near-side root
                double b = -2 * Vec3.Dot(g, starDir), c = g.LengthSquared - AstroMath.EarthRadius_km * AstroMath.EarthRadius_km;
                double disc = b * b - 4 * c;
                if (disc < 0) continue;
                double s = (-b - Math.Sqrt(disc)) / 2; // first intersection travelling from the asteroid towards Earth
                var p = g - starDir * s;
                // Earth-fixed: rotate by GMST (and precession ignored for the map)
                var at = AstroTime.FromTT(t);
                var pf = Mat3.RotZ(at.Gmst) * (Precession.BiasPrecessionMatrix(at.T) * p);
                double lon, lat;
                pf.ToSpherical(out lon, out lat);
                pts.Add(new[] { lat * AstroMath.Rad2Deg, AstroMath.NormalizeDeg180(lon * AstroMath.Rad2Deg), t });
            }
            return pts;
        }
    }

    public class LunarOccultation
    {
        public string Star;
        public double StarMag;
        public AstroTime Disappearance, Reappearance;
        public double DisappearancePA, ReappearancePA;
        public double MoonAltitude, SunAltitude;
        public double MoonIlluminated;
        public bool Graze;
        public override string ToString() => string.Format("{0} (mag {1:0.0}): D {2}  R {3}", Star, StarMag, Disappearance, Reappearance);
    }

    public static class LunarOccultations
    {
        /// <summary>Topocentric lunar occultations of catalog stars (and planets) for an observer.</summary>
        public static List<LunarOccultation> Predict(Observer obs, AstroTime start, double days, IStarCatalogProvider catalog, double magLimit = 7.5)
        {
            var res = new List<LunarOccultation>();
            var open = new Dictionary<string, LunarOccultation>(); // star → event whose reappearance is still to come
            double step = 1.0 / 24;
            for (double jd = start.JdUtc; jd < start.JdUtc + days; jd += step)
            {
                var ctx = new SkyContext(obs, new AstroTime(jd));
                var moon = ctx.BodyPlace(Body.Moon);
                var stars = catalog.Cone(moon.Astrometric.RaHours * 15, moon.Astrometric.DecDeg, 1.0, magLimit, 500);
                foreach (var s in stars)
                {
                    string id = s.Id ?? s.Ra.ToString("0.0000", CultureInfo.InvariantCulture);
                    LunarOccultation pending;
                    open.TryGetValue(id, out pending);
                    if (pending == null && res.Any(r => r.Star == id)) continue;
                    double sra = s.Ra * AstroMath.Deg2Rad, sdec = s.Dec * AstroMath.Deg2Rad;
                    Func<double, double> f = t =>
                    {
                        var c = new SkyContext(obs, new AstroTime(t));
                        var m = c.BodyPlace(Body.Moon);
                        double radius = Math.Asin(1737.4 / (m.Distance * AstroMath.AU_km));
                        return AstroMath.Separation(m.Astrometric.Ra, m.Astrometric.Dec, sra, sdec) - radius;
                    };
                    var roots = Roots.FindAll(f, jd, jd + step, 2.0 / 1440, 1e-8);
                    if (roots.Count == 0) continue;
                    var lo = pending ?? new LunarOccultation { Star = id, StarMag = s.Mag };
                    foreach (var r in roots)
                    {
                        bool entering = f(r + 1e-4) < 0;
                        var c = new SkyContext(obs, new AstroTime(r));
                        var m = c.BodyPlace(Body.Moon);
                        double pa = AstroMath.PositionAngle(m.Astrometric.Ra, m.Astrometric.Dec, sra, sdec) * AstroMath.Rad2Deg;
                        if (entering) { lo.Disappearance = new AstroTime(r); lo.DisappearancePA = pa; }
                        else { lo.Reappearance = new AstroTime(r); lo.ReappearancePA = pa; }
                        lo.MoonAltitude = m.Horizontal.AltDeg;
                        lo.MoonIlluminated = m.IlluminatedFraction;
                        lo.SunAltitude = c.BodyPlace(Body.Sun).Horizontal.AltDeg;
                    }
                    bool ended = lo.Reappearance.JdUtc > 0 || roots.All(r => f(r + 1e-4) > 0);
                    if (!ended) open[id] = lo; else open.Remove(id);
                    if (pending == null && lo.MoonAltitude > 0) res.Add(lo);
                }
            }
            return res.OrderBy(r => (r.Disappearance.JdUtc > 0 ? r.Disappearance : r.Reappearance).JdUtc).ToList();
        }
    }

    public class GalileanMoon
    {
        public string Name;
        public double X, Y;     // Jupiter equatorial radii; X positive to the west, Y to the north
        public bool InFront;    // between Jupiter and the observer
        public bool Transit, Occulted;
    }

    /// <summary>Positions of Io, Europa, Ganymede and Callisto (Meeus, Astronomical Algorithms ch. 44, low accuracy).</summary>
    public static class GalileanMoons
    {
        public static readonly string[] Names = { "Io", "Europa", "Ganymede", "Callisto" };

        public static GalileanMoon[] Positions(double jdTT)
        {
            double d = jdTT - 2451545.0;
            double V = 172.74 + 0.00111588 * d;
            double M = 357.529 + 0.9856003 * d;
            double N = 20.020 + 0.0830853 * d + 0.329 * AstroMath.SinD(V);
            double J = 66.115 + 0.9025179 * d - 0.329 * AstroMath.SinD(V);
            double A = 1.915 * AstroMath.SinD(M) + 0.020 * AstroMath.SinD(2 * M);
            double B = 5.555 * AstroMath.SinD(N) + 0.168 * AstroMath.SinD(2 * N);
            double K = J + A - B;
            double R = 1.00014 - 0.01671 * AstroMath.CosD(M) - 0.00014 * AstroMath.CosD(2 * M);
            double r = 5.20872 - 0.25208 * AstroMath.CosD(N) - 0.00611 * AstroMath.CosD(2 * N);
            double delta = Math.Sqrt(r * r + R * R - 2 * r * R * AstroMath.CosD(K));
            double psi = AstroMath.AsinD(R / delta * AstroMath.SinD(K));
            double lambda = 34.35 + 0.083091 * d + 0.329 * AstroMath.SinD(V) + B;
            double Ds = 3.12 * AstroMath.SinD(lambda + 42.8);
            double De = Ds - 2.22 * AstroMath.SinD(psi) * AstroMath.CosD(lambda + 22) - 1.30 * (r - delta) / delta * AstroMath.SinD(lambda - 100.5);
            double dd = d - delta / 173;
            double u1 = 163.8069 + 203.4058646 * dd + psi - B;
            double u2 = 358.4140 + 101.2916335 * dd + psi - B;
            double u3 = 5.7176 + 50.2345180 * dd + psi - B;
            double u4 = 224.8092 + 21.4879800 * dd + psi - B;
            double G = 331.18 + 50.310482 * dd, H = 87.45 + 21.569231 * dd;
            u1 += 0.473 * AstroMath.SinD(2 * (u1 - u2));
            u2 += 1.065 * AstroMath.SinD(2 * (u2 - u3));
            u3 += 0.165 * AstroMath.SinD(G);
            u4 += 0.843 * AstroMath.SinD(H);
            double r1 = 5.9057 - 0.0244 * AstroMath.CosD(2 * (u1 - u2));
            double r2 = 9.3966 - 0.0882 * AstroMath.CosD(2 * (u2 - u3));
            double r3 = 14.9883 - 0.0216 * AstroMath.CosD(G);
            double r4 = 26.3627 - 0.1939 * AstroMath.CosD(H);
            var u = new[] { u1, u2, u3, u4 }; var rr = new[] { r1, r2, r3, r4 };
            var res = new GalileanMoon[4];
            for (int i = 0; i < 4; i++)
            {
                double X = rr[i] * AstroMath.SinD(u[i]), Y = -rr[i] * AstroMath.CosD(u[i]) * AstroMath.SinD(De);
                bool front = AstroMath.CosD(u[i]) > 0;
                bool overlap = X * X + Y * Y / (0.935 * 0.935) < 1;
                res[i] = new GalileanMoon { Name = Names[i], X = X, Y = Y, InFront = front, Transit = overlap && front, Occulted = overlap && !front };
            }
            return res;
        }

        /// <summary>Transit/occultation start and end times over a span (minute resolution).</summary>
        public static List<AstroEvent> Phenomena(double jd1, double jd2)
        {
            var list = new List<AstroEvent>();
            var prev = Positions(jd1);
            for (double jd = jd1 + 1.0 / 1440; jd <= jd2; jd += 1.0 / 1440)
            {
                var cur = Positions(jd);
                for (int i = 0; i < 4; i++)
                {
                    if (cur[i].Transit != prev[i].Transit) list.Add(new AstroEvent { Time = AstroTime.FromTT(jd), Kind = EventKind.Other, Description = Names[i] + (cur[i].Transit ? " transit begins" : " transit ends") });
                    if (cur[i].Occulted != prev[i].Occulted) list.Add(new AstroEvent { Time = AstroTime.FromTT(jd), Kind = EventKind.Occultation, Description = Names[i] + (cur[i].Occulted ? " occultation begins" : " occultation ends") });
                }
                prev = cur;
            }
            return list;
        }
    }

    public class MeteorShower
    {
        public string Code, Name;
        public double PeakSolarLongitude, RadiantRa, RadiantDec, VelocityKms, Zhr;
        public int ActiveFromDay, ActiveToDay; // day of year (approx.)
        public double PopulationIndex = 2.5;

        /// <summary>Peak date in a given year from the solar longitude.</summary>
        public AstroTime PeakIn(int year)
        {
            double guess = AstroTime.CalendarToJd(year, 3, 20) + AstroMath.NormalizeDeg(PeakSolarLongitude) / 360.0 * 365.2422;
            // solar longitudes past ~280° fall in the next January from the March equinox: the year's own peak is a year earlier
            if (guess >= AstroTime.CalendarToJd(year + 1, 1, 1)) guess -= 365.2422;
            // IMO solar longitudes are referred to the equinox of J2000.0, not of date (the difference grows by
            // 50″ a year, about 9 hours of time by 2025)
            Func<double, double> f = jd => AstroMath.NormalizeDeg180(SolarLongitudeJ2000(jd) - PeakSolarLongitude);
            double root = Roots.Brent(f, guess - 15, guess + 15, 1e-5);
            return AstroTime.FromTT(double.IsNaN(root) ? guess : root);
        }

        /// <summary>The Sun's geocentric ecliptic longitude (deg) on the J2000.0 ecliptic and equinox, with aberration.</summary>
        public static double SolarLongitudeJ2000(double jdTT)
        {
            Vec3 e, v;
            EphemerisManager.EarthHeliocentric(jdTT, out e, out v);
            var sun = Core.Coordinates.Transforms.EquatorialToEcliptic(-e, Core.Coordinates.Precession.ObliquityJ2000);
            double lon, lat;
            sun.ToSpherical(out lon, out lat);
            return AstroMath.NormalizeDeg(lon * AstroMath.Rad2Deg - 20.4898 / 3600.0 / e.Length);
        }

        /// <summary>Expected hourly rate for an observer: ZHR × sin(h_radiant)^1.0 / r^((6.5 - LM)) (IMO formula).</summary>
        public double HourlyRate(double radiantAltDeg, double limitingMag)
        {
            if (radiantAltDeg <= 0) return 0;
            return Zhr * Math.Sin(radiantAltDeg * AstroMath.Deg2Rad) / Math.Pow(PopulationIndex, 6.5 - limitingMag);
        }
    }

    public static class MeteorShowers
    {
        public static readonly List<MeteorShower> Major = new List<MeteorShower>
        {
            new MeteorShower { Code = "QUA", Name = "Quadrantids", PeakSolarLongitude = 283.15, RadiantRa = 230.1, RadiantDec = 48.5, VelocityKms = 41, Zhr = 110, ActiveFromDay = 362, ActiveToDay = 12, PopulationIndex = 2.1 },
            new MeteorShower { Code = "LYR", Name = "Lyrids", PeakSolarLongitude = 32.32, RadiantRa = 271.4, RadiantDec = 33.6, VelocityKms = 49, Zhr = 18, ActiveFromDay = 104, ActiveToDay = 120, PopulationIndex = 2.1 },
            new MeteorShower { Code = "ETA", Name = "η-Aquariids", PeakSolarLongitude = 45.5, RadiantRa = 338.0, RadiantDec = -0.8, VelocityKms = 66, Zhr = 50, ActiveFromDay = 109, ActiveToDay = 148, PopulationIndex = 2.4 },
            new MeteorShower { Code = "SDA", Name = "Southern δ-Aquariids", PeakSolarLongitude = 127.0, RadiantRa = 340.0, RadiantDec = -16.1, VelocityKms = 41, Zhr = 25, ActiveFromDay = 193, ActiveToDay = 231, PopulationIndex = 2.5 },
            new MeteorShower { Code = "CAP", Name = "α-Capricornids", PeakSolarLongitude = 127.0, RadiantRa = 306.6, RadiantDec = -8.2, VelocityKms = 23, Zhr = 5, ActiveFromDay = 186, ActiveToDay = 227, PopulationIndex = 2.5 },
            new MeteorShower { Code = "PER", Name = "Perseids", PeakSolarLongitude = 140.0, RadiantRa = 48.3, RadiantDec = 58.1, VelocityKms = 59, Zhr = 100, ActiveFromDay = 198, ActiveToDay = 236, PopulationIndex = 2.2 },
            new MeteorShower { Code = "DRA", Name = "Draconids", PeakSolarLongitude = 195.4, RadiantRa = 263.1, RadiantDec = 55.8, VelocityKms = 20, Zhr = 10, ActiveFromDay = 279, ActiveToDay = 283, PopulationIndex = 2.6 },
            new MeteorShower { Code = "ORI", Name = "Orionids", PeakSolarLongitude = 208.0, RadiantRa = 95.3, RadiantDec = 15.9, VelocityKms = 66, Zhr = 20, ActiveFromDay = 275, ActiveToDay = 311, PopulationIndex = 2.5 },
            new MeteorShower { Code = "STA", Name = "Southern Taurids", PeakSolarLongitude = 197.0, RadiantRa = 32.0, RadiantDec = 9.0, VelocityKms = 27, Zhr = 5, ActiveFromDay = 253, ActiveToDay = 334, PopulationIndex = 2.3 },
            new MeteorShower { Code = "NTA", Name = "Northern Taurids", PeakSolarLongitude = 230.0, RadiantRa = 58.0, RadiantDec = 22.0, VelocityKms = 29, Zhr = 5, ActiveFromDay = 294, ActiveToDay = 344, PopulationIndex = 2.3 },
            new MeteorShower { Code = "LEO", Name = "Leonids", PeakSolarLongitude = 235.27, RadiantRa = 152.3, RadiantDec = 22.2, VelocityKms = 71, Zhr = 15, ActiveFromDay = 310, ActiveToDay = 334, PopulationIndex = 2.5 },
            new MeteorShower { Code = "GEM", Name = "Geminids", PeakSolarLongitude = 262.2, RadiantRa = 112.3, RadiantDec = 32.5, VelocityKms = 35, Zhr = 150, ActiveFromDay = 338, ActiveToDay = 354, PopulationIndex = 2.6 },
            new MeteorShower { Code = "URS", Name = "Ursids", PeakSolarLongitude = 270.7, RadiantRa = 217.1, RadiantDec = 75.3, VelocityKms = 33, Zhr = 10, ActiveFromDay = 351, ActiveToDay = 361, PopulationIndex = 3.0 },
        };
    }
}
