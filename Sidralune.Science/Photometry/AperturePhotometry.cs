using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;

namespace Sidralune.Science.Photometry
{
    public class ApertureSettings
    {
        /// <summary>Aperture radius in pixels; if FwhmMultiple &gt; 0 the radius is FWHM × multiple.</summary>
        public double Radius = 6, InnerAnnulus = 10, OuterAnnulus = 15;
        public double FwhmMultiple = 1.75;
        public double GainEPerAdu = 1.0;
        public double ReadNoiseE = 5.0;
        public double DarkCurrentE = 0.0;
        /// <summary>Scale normalised pixel values back to ADU for noise calculations.</summary>
        public double AduScale = 65535;
        public double SaturationLevel = 0.95;
        public bool Recentre = true;

        /// <summary>A copy for one run, so the settings can be edited while it works.</summary>
        public ApertureSettings Clone() => (ApertureSettings)MemberwiseClone();
    }

    public class PhotometryMeasurement
    {
        public double X, Y;
        public double Flux, FluxError;       // ADU
        public double Sky, SkySigma;         // ADU per pixel
        public double InstMag, InstMagError;
        public double Snr;
        public double Fwhm;
        public bool Saturated;
        public int ApertureArea;
        public double PeakAdu;
    }

    /// <summary>Circular aperture photometry with sigma-clipped annulus sky and CCD noise equation (Merline &amp; Howell 1995).</summary>
    public static class AperturePhotometry
    {
        public static PhotometryMeasurement Measure(float[] d, int w, int h, double x, double y, ApertureSettings s, double fwhm = double.NaN)
        {
            if (s.Recentre)
            {
                for (int it = 0; it < 3; it++)
                {
                    double sum = 0, sx = 0, sy = 0;
                    int r = (int)Math.Max(3, (double.IsNaN(fwhm) ? s.Radius : fwhm * 1.5));
                    // local background for centroiding
                    double bgc = LocalMedian(d, w, h, x, y, r + 3, r + 6);
                    for (int yy = (int)y - r; yy <= (int)y + r; yy++)
                        for (int xx = (int)x - r; xx <= (int)x + r; xx++)
                        {
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            double dd = (xx - x) * (xx - x) + (yy - y) * (yy - y);
                            if (dd > r * r) continue;
                            double v = d[yy * w + xx] - bgc;
                            if (v <= 0) continue;
                            sum += v; sx += v * xx; sy += v * yy;
                        }
                    if (sum <= 0) break;
                    double nx = sx / sum, ny = sy / sum;
                    if (Math.Abs(nx - x) > 5 || Math.Abs(ny - y) > 5) break;
                    x = nx; y = ny;
                }
            }
            double rad = !double.IsNaN(fwhm) && s.FwhmMultiple > 0 ? fwhm * s.FwhmMultiple : s.Radius;
            double rin = !double.IsNaN(fwhm) && s.FwhmMultiple > 0 ? Math.Max(rad + 3, s.InnerAnnulus * rad / s.Radius) : s.InnerAnnulus;
            double rout = !double.IsNaN(fwhm) && s.FwhmMultiple > 0 ? Math.Max(rin + 4, s.OuterAnnulus * rad / s.Radius) : s.OuterAnnulus;
            var m = new PhotometryMeasurement { X = x, Y = y, Fwhm = fwhm };
            var sky = new List<double>();
            double flux = 0, area = 0, peak = 0;
            int R = (int)Math.Ceiling(rout) + 1;
            for (int yy = (int)y - R; yy <= (int)y + R; yy++)
                for (int xx = (int)x - R; xx <= (int)x + R; xx++)
                {
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    double dist = Math.Sqrt((xx - x) * (xx - x) + (yy - y) * (yy - y));
                    double v = d[yy * w + xx];
                    if (dist >= rin && dist <= rout) sky.Add(v);
                    // partial-pixel weighting for the aperture edge
                    double frac = AstroMath.Clamp(rad - dist + 0.5, 0, 1);
                    if (frac > 0)
                    {
                        flux += v * frac; area += frac;
                        if (v > peak) peak = v;
                        if (v >= s.SaturationLevel) m.Saturated = true;
                    }
                }
            if (sky.Count < 10 || area < 1) { m.InstMag = double.NaN; return m; }
            double mean, sig; int used;
            Stats.SigmaClip(sky, 3, 5, out mean, out sig, out used);
            // mode estimate 3·median − 2·mean (robust against crowding)
            double med = Stats.Median(sky);
            double skyLevel = mean < med ? mean : 3 * med - 2 * mean;
            if (double.IsNaN(skyLevel)) skyLevel = med;
            double sc = s.AduScale;
            m.Sky = skyLevel * sc; m.SkySigma = sig * sc;
            m.Flux = (flux - skyLevel * area) * sc;
            m.ApertureArea = (int)Math.Round(area);
            m.PeakAdu = peak * sc;
            double g = s.GainEPerAdu;
            double signalE = Math.Max(0, m.Flux * g);
            double skyE = Math.Max(0, m.Sky * g);
            double var = signalE + area * (1 + area / used) * (skyE + s.DarkCurrentE + s.ReadNoiseE * s.ReadNoiseE + g * g * 0.083);
            double errE = Math.Sqrt(var);
            m.FluxError = errE / g;
            m.Snr = errE > 0 ? signalE / errE : 0;
            m.InstMag = m.Flux > 0 ? -2.5 * Math.Log10(m.Flux) + 25 : double.NaN;
            m.InstMagError = m.Snr > 0 ? 1.0857 / m.Snr : double.NaN;
            return m;
        }

        private static double LocalMedian(float[] d, int w, int h, double x, double y, double r1, double r2)
        {
            var v = new List<double>();
            for (int yy = (int)(y - r2); yy <= (int)(y + r2); yy++)
                for (int xx = (int)(x - r2); xx <= (int)(x + r2); xx++)
                {
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    double dd = Math.Sqrt((xx - x) * (xx - x) + (yy - y) * (yy - y));
                    if (dd >= r1 && dd <= r2) v.Add(d[yy * w + xx]);
                }
            return v.Count > 0 ? Stats.Median(v) : 0;
        }

        /// <summary>Curve of growth: instrumental magnitude vs aperture radius (to choose an optimal aperture).</summary>
        public static List<double[]> CurveOfGrowth(float[] d, int w, int h, double x, double y, ApertureSettings s, double maxRadius = 20)
        {
            var list = new List<double[]>();
            for (double r = 1; r <= maxRadius; r += 0.5)
            {
                var ss = new ApertureSettings { Radius = r, InnerAnnulus = maxRadius + 3, OuterAnnulus = maxRadius + 8, FwhmMultiple = 0, GainEPerAdu = s.GainEPerAdu, ReadNoiseE = s.ReadNoiseE, AduScale = s.AduScale, Recentre = false };
                var m = Measure(d, w, h, x, y, ss);
                list.Add(new[] { r, m.InstMag, m.Snr });
            }
            return list;
        }
    }

    public class PhotometryTarget
    {
        public string Name;
        public double Ra, Dec;       // deg
        public double CatalogMag = double.NaN; // for comparison stars
        public double CatalogMagError = 0.02;
        public double CatalogColor = double.NaN; // B-V
        public bool IsComparison, IsCheck;
    }

    public class LightCurvePoint
    {
        public double Jd;           // mid-exposure (JD UTC or BJD_TDB)
        public double Mag, MagError;
        public double Airmass = double.NaN;
        public double Flux, FluxError; // relative flux
        public string Filter = "V";
        public string FrameFile;
        public double CheckMag = double.NaN;
        public double Sky;
        public bool Rejected;
    }

    /// <summary>Ensemble differential photometry across frames using a plate-solved WCS to locate stars.</summary>
    public class DifferentialPhotometry
    {
        public ApertureSettings Aperture = new ApertureSettings();
        public List<PhotometryTarget> Targets = new List<PhotometryTarget>();
        public Dictionary<string, List<LightCurvePoint>> Curves = new Dictionary<string, List<LightCurvePoint>>();
        /// <summary>Transformation coefficient (Tv_bv style) and extinction for standardisation.</summary>
        public double TransformationCoefficient = 0;
        public double ExtinctionK = 0;

        /// <summary>Measure one frame; positions from a WCS (or pixel coords supplied directly via the provider function).</summary>
        public void AddFrame(AstroImage img, Astrometry.Wcs wcs, double jdMid, double airmass = double.NaN, string filter = "V")
        {
            var lum = img.Luminance();
            var stars = StarDetector.Detect(lum, img.Width, img.Height, new StarDetectionOptions { MaxStars = 300 });
            double fwhm, hfr, ecc;
            StarDetector.Summary(stars, out fwhm, out hfr, out ecc);
            var meas = new Dictionary<PhotometryTarget, PhotometryMeasurement>();
            foreach (var t in Targets)
            {
                double x, y;
                if (!wcs.WorldToPixel(t.Ra, t.Dec, out x, out y)) continue;
                if (x < 10 || y < 10 || x > img.Width - 10 || y > img.Height - 10) continue;
                meas[t] = AperturePhotometry.Measure(lum, img.Width, img.Height, x, y, Aperture, fwhm);
            }
            var comps = Targets.Where(t => t.IsComparison && meas.ContainsKey(t) && !double.IsNaN(meas[t].InstMag) && !meas[t].Saturated).ToList();
            if (comps.Count == 0) return;
            foreach (var t in Targets.Where(t => !t.IsComparison && meas.ContainsKey(t)))
            {
                var m = meas[t];
                if (double.IsNaN(m.InstMag)) continue;
                // ensemble: weighted mean of zero points from each comparison star
                var zps = new List<double>(); var ws = new List<double>();
                double fluxSum = 0, fluxErr2 = 0;
                foreach (var c in comps)
                {
                    var cm = meas[c];
                    double zp = (double.IsNaN(c.CatalogMag) ? 0 : c.CatalogMag) - cm.InstMag;
                    if (!double.IsNaN(t.CatalogColor) && !double.IsNaN(c.CatalogColor)) zp += TransformationCoefficient * (t.CatalogColor - c.CatalogColor);
                    double e = Math.Sqrt(cm.InstMagError * cm.InstMagError + c.CatalogMagError * c.CatalogMagError);
                    zps.Add(zp); ws.Add(1 / (e * e));
                    fluxSum += cm.Flux; fluxErr2 += cm.FluxError * cm.FluxError;
                }
                double zpMean, zpErr;
                zpMean = Stats.WeightedMean(zps, ws.Select(v => 1 / Math.Sqrt(v)).ToList(), out zpErr);
                var p = new LightCurvePoint
                {
                    Jd = jdMid, Airmass = airmass, Filter = filter, FrameFile = img.SourcePath, Sky = m.Sky,
                    Mag = m.InstMag + zpMean,
                    MagError = Math.Sqrt(m.InstMagError * m.InstMagError + (comps.Count > 1 ? zpErr * zpErr : 0)),
                    Flux = m.Flux / fluxSum,
                    FluxError = m.Flux / fluxSum * Math.Sqrt(Math.Pow(m.FluxError / m.Flux, 2) + fluxErr2 / (fluxSum * fluxSum))
                };
                var check = Targets.FirstOrDefault(c => c.IsCheck && meas.ContainsKey(c));
                if (check != null && !double.IsNaN(meas[check].InstMag)) p.CheckMag = meas[check].InstMag + zpMean;
                List<LightCurvePoint> list;
                if (!Curves.TryGetValue(t.Name, out list)) Curves[t.Name] = list = new List<LightCurvePoint>();
                list.Add(p);
            }
        }

        /// <summary>
        /// AAVSO Extended File Format report (one target). obsCode = AAVSO observer code; chart = AAVSO chart id.
        /// </summary>
        public static string AavsoExtended(string targetName, IEnumerable<LightCurvePoint> pts, string obsCode, string compName, string checkName, string chart, string software = "Sidralune 0.2.0", bool transformed = false)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#TYPE=EXTENDED");
            sb.AppendLine("#OBSCODE=" + obsCode);
            sb.AppendLine("#SOFTWARE=" + software);
            sb.AppendLine("#DELIM=,");
            sb.AppendLine("#DATE=JD");
            sb.AppendLine("#OBSTYPE=CCD");
            sb.AppendLine("#NAME,DATE,MAG,MERR,FILT,TRANS,MTYPE,CNAME,CMAG,KNAME,KMAG,AMASS,GROUP,CHART,NOTES");
            foreach (var p in pts.Where(q => !q.Rejected))
                sb.AppendLine(string.Join(",", new[]
                {
                    targetName, p.Jd.ToString("0.00000", CultureInfo.InvariantCulture), p.Mag.ToString("0.000", CultureInfo.InvariantCulture),
                    p.MagError.ToString("0.000", CultureInfo.InvariantCulture), p.Filter, transformed ? "YES" : "NO", "STD",
                    compName, "na", checkName ?? "na", double.IsNaN(p.CheckMag) ? "na" : p.CheckMag.ToString("0.000", CultureInfo.InvariantCulture),
                    double.IsNaN(p.Airmass) ? "na" : p.Airmass.ToString("0.000", CultureInfo.InvariantCulture), "na", chart ?? "na", "na"
                }));
            return sb.ToString();
        }

        public static void ExportCsv(string path, IEnumerable<LightCurvePoint> pts)
        {
            Core.Util.CsvWriter.Write(path, new[] { "JD", "Mag", "MagErr", "RelFlux", "RelFluxErr", "Airmass", "Filter", "CheckMag", "Sky", "File" },
                pts.Select(p => (IList<object>)new object[] { p.Jd, p.Mag, p.MagError, p.Flux, p.FluxError, p.Airmass, p.Filter, p.CheckMag, p.Sky, p.FrameFile }));
        }
    }

    /// <summary>Time systems for timing work: heliocentric and barycentric Julian dates.</summary>
    public static class TimeCorrections
    {
        /// <summary>BJD_TDB from JD_UTC for a target (deg), using the Earth's barycentric position (Rømer delay).</summary>
        public static double BjdTdb(double jdUtc, double raDeg, double decDeg)
        {
            var t = new Core.Time.AstroTime(jdUtc);
            double jdTdb = t.JdTDB;
            Vec3 p, v;
            Core.Ephemeris.EphemerisManager.State(Core.Ephemeris.Body.Earth, jdTdb, out p, out v);
            // when only the analytic (heliocentric) theory is loaded this yields HJD-like precision (~±4 s)
            var u = Vec3.FromSpherical(raDeg * AstroMath.Deg2Rad, decDeg * AstroMath.Deg2Rad);
            double delay = Vec3.Dot(p, u) * AstroMath.LightTimeAU_days;
            return jdTdb + delay;
        }

        public static double HjdUtc(double jdUtc, double raDeg, double decDeg)
        {
            Vec3 e, v;
            Core.Ephemeris.EphemerisManager.EarthHeliocentric(new Core.Time.AstroTime(jdUtc).JdTT, out e, out v);
            var u = Vec3.FromSpherical(raDeg * AstroMath.Deg2Rad, decDeg * AstroMath.Deg2Rad);
            return jdUtc + Vec3.Dot(e, u) * AstroMath.LightTimeAU_days;
        }
    }
}
