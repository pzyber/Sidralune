using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;

namespace Sidralune.Core.Satellites
{
    /// <summary>NORAD two-line element set.</summary>
    public class Tle
    {
        public string Name, Line1, Line2;
        public int CatalogNumber;
        public double EpochJd;           // UTC
        public double Bstar, Inclination, Raan, Eccentricity, ArgPerigee, MeanAnomaly, MeanMotionRevDay;
        /// <summary>Standard magnitude at 1000 km range and 90° phase (if known).</summary>
        public double StdMag = 4.0;

        public static Tle Parse(string name, string l1, string l2)
        {
            var t = new Tle { Name = (name ?? "").Trim(), Line1 = l1, Line2 = l2 };
            t.CatalogNumber = int.Parse(l1.Substring(2, 5).Trim(), CultureInfo.InvariantCulture);
            int yy = int.Parse(l1.Substring(18, 2), CultureInfo.InvariantCulture);
            double day = double.Parse(l1.Substring(20, 12), CultureInfo.InvariantCulture);
            int year = yy < 57 ? 2000 + yy : 1900 + yy;
            t.EpochJd = AstroTime.CalendarToJd(year, 1, 0.0) + day;
            t.Bstar = ParseExp(l1.Substring(53, 8));
            t.Inclination = double.Parse(l2.Substring(8, 8), CultureInfo.InvariantCulture);
            t.Raan = double.Parse(l2.Substring(17, 8), CultureInfo.InvariantCulture);
            t.Eccentricity = double.Parse("0." + l2.Substring(26, 7).Trim(), CultureInfo.InvariantCulture);
            t.ArgPerigee = double.Parse(l2.Substring(34, 8), CultureInfo.InvariantCulture);
            t.MeanAnomaly = double.Parse(l2.Substring(43, 8), CultureInfo.InvariantCulture);
            t.MeanMotionRevDay = double.Parse(l2.Substring(52, 11), CultureInfo.InvariantCulture);
            return t;
        }

        private static double ParseExp(string s)
        {
            s = s.Trim();
            if (s.Length == 0) return 0;
            int sign = 1;
            if (s[0] == '-') { sign = -1; s = s.Substring(1); } else if (s[0] == '+') s = s.Substring(1);
            int e = s.LastIndexOfAny(new[] { '-', '+' });
            if (e <= 0) return sign * double.Parse("0." + s, CultureInfo.InvariantCulture);
            double mant = double.Parse("0." + s.Substring(0, e), CultureInfo.InvariantCulture);
            int exp = int.Parse(s.Substring(e), CultureInfo.InvariantCulture);
            return sign * mant * Math.Pow(10, exp);
        }

        public static List<Tle> LoadFile(string path)
        {
            var lines = File.ReadAllLines(path);
            var list = new List<Tle>();
            for (int i = 0; i < lines.Length; i++)
            {
                string a = lines[i].TrimEnd();
                if (a.StartsWith("1 ") && i + 1 < lines.Length && lines[i + 1].StartsWith("2 "))
                {
                    string name = i > 0 && !lines[i - 1].StartsWith("1 ") && !lines[i - 1].StartsWith("2 ") ? lines[i - 1].Trim() : "";
                    try { list.Add(Parse(name.TrimStart('0', ' '), a, lines[i + 1].TrimEnd())); } catch { }
                    i++;
                }
            }
            return list;
        }

        public override string ToString() => Name + " (" + CatalogNumber + ")";
    }

    /// <summary>
    /// SGP4 propagator (Hoots &amp; Roehrich 1980, as revised by Vallado et al. 2006), near-Earth branch with WGS-72 constants.
    /// Deep-space objects (period ≥ 225 min) are propagated with the same secular model, which is adequate for visual
    /// pass prediction of GEO/HEO objects over a few days but less accurate than full SDP4.
    /// </summary>
    public class Sgp4
    {
        private const double Re = 6378.135, Mu = 398600.8, J2 = 0.001082616, J3 = -0.00000253881, J4 = -0.00000165597;
        private static readonly double Xke = 60.0 / Math.Sqrt(Re * Re * Re / Mu);
        private const double X2o3 = 2.0 / 3.0;
        private const double TwoPi = 2 * Math.PI;

        public readonly Tle Tle;
        public bool IsDeepSpace { get; }
        private readonly double _ecco, _inclo, _nodeo, _argpo, _mo, _bstar, _no;
        private double _ao, _con41, _x1mth2, _x7thm1, _cc1, _cc4, _cc5, _d2, _d3, _d4, _delmo, _eta, _sinmao, _t2cof, _t3cof, _t4cof, _t5cof;
        private double _mdot, _argpdot, _nodedot, _nodecf, _omgcof, _xmcof, _xlcof, _aycof, _cosio, _sinio;
        private bool _isimp;
        public string Error { get; private set; }

        public Sgp4(Tle tle)
        {
            Tle = tle;
            _ecco = tle.Eccentricity;
            _inclo = tle.Inclination * AstroMath.Deg2Rad;
            _nodeo = tle.Raan * AstroMath.Deg2Rad;
            _argpo = tle.ArgPerigee * AstroMath.Deg2Rad;
            _mo = tle.MeanAnomaly * AstroMath.Deg2Rad;
            _bstar = tle.Bstar;
            double noKozai = tle.MeanMotionRevDay * TwoPi / 1440.0;

            _cosio = Math.Cos(_inclo); _sinio = Math.Sin(_inclo);
            double cosio2 = _cosio * _cosio;
            double eccsq = _ecco * _ecco, omeosq = 1 - eccsq, rteosq = Math.Sqrt(omeosq);
            double ak = Math.Pow(Xke / noKozai, X2o3);
            double d1 = 0.75 * J2 * (3 * cosio2 - 1) / (rteosq * omeosq);
            double del = d1 / (ak * ak);
            double adel = ak * (1 - del * del - del * (1.0 / 3 + 134 * del * del / 81));
            del = d1 / (adel * adel);
            _no = noKozai / (1 + del);
            _ao = Math.Pow(Xke / _no, X2o3);
            IsDeepSpace = TwoPi / _no >= 225.0;
            double po = _ao * omeosq;
            _con41 = 3 * cosio2 - 1;
            double con42 = 1 - 5 * cosio2;
            double rp = _ao * (1 - _ecco);
            double perige = (rp - 1) * Re;
            double sfour = 78.0 / Re + 1, qzms24 = Math.Pow((120 - 78) / Re, 4);
            if (perige < 156)
            {
                double s = perige - 78;
                if (perige < 98) s = 20;
                qzms24 = Math.Pow((120 - s) / Re, 4);
                sfour = s / Re + 1;
            }
            double pinvsq = 1 / (po * po);
            double tsi = 1 / (_ao - sfour);
            _eta = _ao * _ecco * tsi;
            double etasq = _eta * _eta, eeta = _ecco * _eta, psisq = Math.Abs(1 - etasq);
            double coef = qzms24 * Math.Pow(tsi, 4), coef1 = coef / Math.Pow(psisq, 3.5);
            double cc2 = coef1 * _no * (_ao * (1 + 1.5 * etasq + eeta * (4 + etasq)) + 0.375 * J2 * tsi / psisq * _con41 * (8 + 3 * etasq * (8 + etasq)));
            _cc1 = _bstar * cc2;
            double cc3 = _ecco > 1e-4 ? -2 * coef * tsi * (J3 / J2) * _no * _sinio / _ecco : 0;
            _x1mth2 = 1 - cosio2;
            _cc4 = 2 * _no * coef1 * _ao * omeosq * (_eta * (2 + 0.5 * etasq) + _ecco * (0.5 + 2 * etasq)
                - J2 * tsi / (_ao * psisq) * (-3 * _con41 * (1 - 2 * eeta + etasq * (1.5 - 0.5 * eeta)) + 0.75 * _x1mth2 * (2 * etasq - eeta * (1 + etasq)) * Math.Cos(2 * _argpo)));
            _cc5 = 2 * coef1 * _ao * omeosq * (1 + 2.75 * (etasq + eeta) + eeta * etasq);
            double cosio4 = cosio2 * cosio2;
            double temp1 = 1.5 * J2 * pinvsq * _no, temp2 = 0.5 * temp1 * J2 * pinvsq, temp3 = -0.46875 * J4 * pinvsq * pinvsq * _no;
            _mdot = _no + 0.5 * temp1 * rteosq * _con41 + 0.0625 * temp2 * rteosq * (13 - 78 * cosio2 + 137 * cosio4);
            _argpdot = -0.5 * temp1 * con42 + 0.0625 * temp2 * (7 - 114 * cosio2 + 395 * cosio4) + temp3 * (3 - 36 * cosio2 + 49 * cosio4);
            double xhdot1 = -temp1 * _cosio;
            _nodedot = xhdot1 + (0.5 * temp2 * (4 - 19 * cosio2) + 2 * temp3 * (3 - 7 * cosio2)) * _cosio;
            _omgcof = _bstar * cc3 * Math.Cos(_argpo);
            _xmcof = _ecco > 1e-4 ? -X2o3 * coef * _bstar / eeta : 0;
            _nodecf = 3.5 * omeosq * xhdot1 * _cc1;
            _t2cof = 1.5 * _cc1;
            _xlcof = Math.Abs(_cosio + 1) > 1.5e-12 ? -0.25 * (J3 / J2) * _sinio * (3 + 5 * _cosio) / (1 + _cosio) : -0.25 * (J3 / J2) * _sinio * (3 + 5 * _cosio) / 1.5e-12;
            _aycof = -0.5 * (J3 / J2) * _sinio;
            _delmo = Math.Pow(1 + _eta * Math.Cos(_mo), 3);
            _sinmao = Math.Sin(_mo);
            _x7thm1 = 7 * cosio2 - 1;
            _isimp = rp < (220.0 / Re + 1);
            if (!_isimp)
            {
                double cc1sq = _cc1 * _cc1;
                _d2 = 4 * _ao * tsi * cc1sq;
                double temp = _d2 * tsi * _cc1 / 3;
                _d3 = (17 * _ao + sfour) * temp;
                _d4 = 0.5 * temp * _ao * tsi * (221 * _ao + 31 * sfour) * _cc1;
                _t3cof = _d2 + 2 * cc1sq;
                _t4cof = 0.25 * (3 * _d3 + _cc1 * (12 * _d2 + 10 * cc1sq));
                _t5cof = 0.2 * (3 * _d4 + 12 * _cc1 * _d3 + 6 * _d2 * _d2 + 15 * cc1sq * (2 * _d2 + cc1sq));
            }
        }

        /// <summary>Position (km) and velocity (km/s) in the TEME frame, tsince minutes from TLE epoch. Returns false on decay/error.</summary>
        public bool Propagate(double tsince, out Vec3 pos, out Vec3 vel)
        {
            pos = vel = Vec3.Zero;
            double t = tsince;
            double xmdf = _mo + _mdot * t, argpdf = _argpo + _argpdot * t, nodedf = _nodeo + _nodedot * t;
            double argpm = argpdf, mm = xmdf, t2 = t * t;
            double nodem = nodedf + _nodecf * t2;
            double tempa = 1 - _cc1 * t, tempe = _bstar * _cc4 * t, templ = _t2cof * t2;
            if (!_isimp)
            {
                double delomg = _omgcof * t;
                double delm = _xmcof * (Math.Pow(1 + _eta * Math.Cos(xmdf), 3) - _delmo);
                double tdel = delomg + delm;
                mm = xmdf + tdel;
                argpm = argpdf - tdel;
                double t3 = t2 * t, t4 = t3 * t;
                tempa = tempa - _d2 * t2 - _d3 * t3 - _d4 * t4;
                tempe = tempe + _bstar * _cc5 * (Math.Sin(mm) - _sinmao);
                templ = templ + _t3cof * t3 + t4 * (_t4cof + t * _t5cof);
            }
            double nm = _no, em = _ecco, inclm = _inclo;
            if (nm <= 0) { Error = "mean motion <= 0"; return false; }
            double am = Math.Pow(Xke / nm, X2o3) * tempa * tempa;
            nm = Xke / Math.Pow(am, 1.5);
            em = em - tempe;
            if (em >= 1 || em < -0.001) { Error = "eccentricity out of range"; return false; }
            if (em < 1e-6) em = 1e-6;
            mm = mm + _no * templ;
            double xlm = mm + argpm + nodem;
            nodem = nodem % TwoPi; argpm = argpm % TwoPi; xlm = xlm % TwoPi;
            mm = (xlm - argpm - nodem) % TwoPi;
            double sinim = Math.Sin(inclm), cosim = Math.Cos(inclm);
            double ep = em, xincp = inclm, argpp = argpm, nodep = nodem, mp = mm;
            double axnl = ep * Math.Cos(argpp);
            double temp0 = 1 / (am * (1 - ep * ep));
            double aynl = ep * Math.Sin(argpp) + temp0 * _aycof;
            double xl = mp + argpp + nodep + temp0 * _xlcof * axnl;
            double u = (xl - nodep) % TwoPi;
            double eo1 = u, tem5 = 9999.9, sineo1 = 0, coseo1 = 0;
            for (int k = 0; Math.Abs(tem5) >= 1e-12 && k < 10; k++)
            {
                sineo1 = Math.Sin(eo1); coseo1 = Math.Cos(eo1);
                tem5 = 1 - coseo1 * axnl - sineo1 * aynl;
                tem5 = (u - aynl * coseo1 + axnl * sineo1 - eo1) / tem5;
                if (Math.Abs(tem5) >= 0.95) tem5 = tem5 > 0 ? 0.95 : -0.95;
                eo1 += tem5;
            }
            double ecose = axnl * coseo1 + aynl * sineo1, esine = axnl * sineo1 - aynl * coseo1;
            double el2 = axnl * axnl + aynl * aynl;
            double pl = am * (1 - el2);
            if (pl < 0) { Error = "semi-latus rectum < 0"; return false; }
            double rl = am * (1 - ecose);
            double rdotl = Math.Sqrt(am) * esine / rl;
            double rvdotl = Math.Sqrt(pl) / rl;
            double betal = Math.Sqrt(1 - el2);
            double temp = esine / (1 + betal);
            double sinu = am / rl * (sineo1 - aynl - axnl * temp);
            double cosu = am / rl * (coseo1 - axnl + aynl * temp);
            double su = Math.Atan2(sinu, cosu);
            double sin2u = (cosu + cosu) * sinu, cos2u = 1 - 2 * sinu * sinu;
            temp = 1 / pl;
            double temp1 = 0.5 * J2 * temp, temp2 = temp1 * temp;
            double mrt = rl * (1 - 1.5 * temp2 * betal * _con41) + 0.5 * temp1 * _x1mth2 * cos2u;
            su = su - 0.25 * temp2 * _x7thm1 * sin2u;
            double xnode = nodep + 1.5 * temp2 * cosim * sin2u;
            double xinc = xincp + 1.5 * temp2 * cosim * sinim * cos2u;
            double mvt = rdotl - nm * temp1 * _x1mth2 * sin2u / Xke;
            double rvdot = rvdotl + nm * temp1 * (_x1mth2 * cos2u + 1.5 * _con41) / Xke;
            double sinsu = Math.Sin(su), cossu = Math.Cos(su), snod = Math.Sin(xnode), cnod = Math.Cos(xnode), sini = Math.Sin(xinc), cosi = Math.Cos(xinc);
            double xmx = -snod * cosi, xmy = cnod * cosi;
            double ux = xmx * sinsu + cnod * cossu, uy = xmy * sinsu + snod * cossu, uz = sini * sinsu;
            double vx = xmx * cossu - cnod * sinsu, vy = xmy * cossu - snod * sinsu, vz = sini * cossu;
            double vkmps = Re * Xke / 60.0;
            pos = new Vec3(mrt * ux * Re, mrt * uy * Re, mrt * uz * Re);
            vel = new Vec3((mvt * ux + rvdot * vx) * vkmps, (mvt * uy + rvdot * vy) * vkmps, (mvt * uz + rvdot * vz) * vkmps);
            if (mrt < 1.0) { Error = "decayed"; return false; }
            return true;
        }

        public bool PropagateTo(AstroTime t, out Vec3 pos, out Vec3 vel) => Propagate((t.JdUtc - Tle.EpochJd) * 1440.0, out pos, out vel);
    }

    /// <summary>Topocentric look angles and visibility for a satellite.</summary>
    public class SatelliteLook
    {
        public double AzDeg, AltDeg, RangeKm, RangeRateKms;
        public double RaHours, DecDeg; // topocentric, of date
        public bool Sunlit;
        public double Magnitude = double.NaN;
        public double SubLatDeg, SubLonDeg, HeightKm;
    }

    public class SatellitePass
    {
        public AstroTime Rise, Culmination, Set;
        public double MaxAltDeg, RiseAzDeg, SetAzDeg, BrightestMag = double.NaN;
        public bool Visible;
        public override string ToString() => string.Format("{0}  max {1:0}°  {2}", Rise, MaxAltDeg, Visible ? "visible" : "");
    }

    public static class SatelliteTools
    {
        /// <summary>Look angles for an observer. Uses GMST rotation TEME->PEF (polar motion neglected).</summary>
        public static SatelliteLook Look(Sgp4 sat, Observer obs, AstroTime t)
        {
            Vec3 p, v;
            if (!sat.PropagateTo(t, out p, out v)) return null;
            double gmst = t.Gmst;
            var pef = Mat3.RotZ(gmst) * p;
            var vpef = Mat3.RotZ(gmst) * v - Vec3.Cross(new Vec3(0, 0, 7.292115e-5), pef);
            var site = obs.GeocentricItrs();
            var d = pef - site;
            double lat = obs.LatRad, lon = obs.LonRad;
            double sl = Math.Sin(lat), cl = Math.Cos(lat), so = Math.Sin(lon), co = Math.Cos(lon);
            double south = sl * co * d.X + sl * so * d.Y - cl * d.Z;
            double east = -so * d.X + co * d.Y;
            double up = cl * co * d.X + cl * so * d.Y + sl * d.Z;
            double range = d.Length;
            var look = new SatelliteLook
            {
                RangeKm = range,
                AltDeg = Math.Asin(up / range) * AstroMath.Rad2Deg,
                AzDeg = AstroMath.NormalizeDeg(Math.Atan2(east, -south) * AstroMath.Rad2Deg),
                RangeRateKms = Vec3.Dot(d, vpef) / range
            };
            // topocentric RA/Dec (TEME ~ of date)
            var siteTeme = Mat3.RotZ(-gmst) * site;
            var dt = p - siteTeme;
            double ra, dec;
            dt.ToSpherical(out ra, out dec);
            look.RaHours = ra * AstroMath.Rad2Hour;
            look.DecDeg = dec * AstroMath.Rad2Deg;
            // sub-satellite point (geocentric latitude approx -> geodetic)
            double lonS, latS, rS;
            pef.ToSpherical(out lonS, out latS, out rS);
            double f = AstroMath.EarthFlattening;
            look.SubLatDeg = Math.Atan(Math.Tan(latS) / ((1 - f) * (1 - f))) * AstroMath.Rad2Deg;
            look.SubLonDeg = AstroMath.NormalizeDeg180(lonS * AstroMath.Rad2Deg);
            look.HeightKm = rS - AstroMath.EarthRadius_km * (1 - f * Math.Sin(latS) * Math.Sin(latS));
            // illumination: cylindrical shadow approximation with sun direction
            double sra, sdec;
            Ephemeris.Events.SunRaDecFast(t.JdTT, out sra, out sdec);
            var sunDir = Vec3.FromSpherical(sra, sdec);
            double proj = Vec3.Dot(p, sunDir);
            var perp = p - sunDir * proj;
            look.Sunlit = proj > 0 || perp.Length > AstroMath.EarthRadius_km;
            if (look.Sunlit)
            {
                var toSun = sunDir * 1.496e8 - p;
                double phase = Vec3.AngleBetween(toSun, -dt);
                double fphase = ((Math.PI - phase) * Math.Cos(phase) + Math.Sin(phase)) / Math.PI;
                look.Magnitude = sat.Tle.StdMag - 15.75 + 2.5 * Math.Log10(range * range / Math.Max(fphase, 1e-6));
            }
            return look;
        }

        /// <summary>Predict passes above minAlt (deg) within [start, start+days].</summary>
        public static List<SatellitePass> Passes(Sgp4 sat, Observer obs, AstroTime start, double days, double minAltDeg = 10)
        {
            var list = new List<SatellitePass>();
            double step = 30.0 / 86400.0;
            Func<double, double> alt = jd => { var l = Look(sat, obs, new AstroTime(jd)); return l == null ? -90 : l.AltDeg; };
            double t = start.JdUtc, end = t + days;
            double prev = alt(t);
            SatellitePass cur = null;
            while (t < end)
            {
                double tn = t + step;
                double a = alt(tn);
                if (prev < 0 && a >= 0)
                {
                    double r = Roots.Brent(alt, t, tn, 1e-7);
                    cur = new SatellitePass { Rise = new AstroTime(r), MaxAltDeg = a };
                    var l = Look(sat, obs, cur.Rise);
                    if (l != null) cur.RiseAzDeg = l.AzDeg;
                }
                if (cur != null && a > cur.MaxAltDeg) { cur.MaxAltDeg = a; cur.Culmination = new AstroTime(tn); }
                if (cur != null)
                {
                    var l = Look(sat, obs, new AstroTime(tn));
                    if (l != null && l.Sunlit && a > 0 && Ephemeris.Events.SunAltitudeFast(obs, new AstroTime(tn)) < -6)
                    {
                        cur.Visible = true;
                        if (double.IsNaN(cur.BrightestMag) || l.Magnitude < cur.BrightestMag) cur.BrightestMag = l.Magnitude;
                    }
                }
                if (prev >= 0 && a < 0 && cur != null)
                {
                    double r = Roots.Brent(alt, t, tn, 1e-7);
                    cur.Set = new AstroTime(r);
                    var l = Look(sat, obs, cur.Set);
                    if (l != null) cur.SetAzDeg = l.AzDeg;
                    if (cur.MaxAltDeg >= minAltDeg) list.Add(cur);
                    cur = null;
                }
                prev = a; t = tn;
            }
            return list;
        }
    }
}
