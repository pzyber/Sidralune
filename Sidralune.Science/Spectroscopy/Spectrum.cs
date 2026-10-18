using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Sidralune.Core.Mathematics;
using Sidralune.Imaging;
using Sidralune.Imaging.IO;

namespace Sidralune.Science.Spectroscopy
{
    /// <summary>One-dimensional spectrum. Wavelengths in Ångström (vacuum or air as given by the source).</summary>
    public class Spectrum
    {
        public double[] Wavelength;
        public double[] Flux;
        public double[] Error;
        public FitsHeader Header = new FitsHeader();
        public string Name;
        public string FluxUnit = "ADU";

        public int Length => Flux.Length;

        public Spectrum(int n) { Wavelength = new double[n]; Flux = new double[n]; }
        public Spectrum(double[] wl, double[] f) { Wavelength = wl; Flux = f; }

        public Spectrum Clone()
        {
            return new Spectrum((double[])Wavelength.Clone(), (double[])Flux.Clone())
            { Error = Error != null ? (double[])Error.Clone() : null, Header = Header.Clone(), Name = Name, FluxUnit = FluxUnit };
        }

        /// <summary>Linear interpolation at wavelength.</summary>
        public double At(double wl)
        {
            int n = Length;
            if (n == 0) return double.NaN;
            if (wl <= Wavelength[0]) return Flux[0];
            if (wl >= Wavelength[n - 1]) return Flux[n - 1];
            int lo = 0, hi = n - 1;
            while (hi - lo > 1) { int m = (lo + hi) >> 1; if (Wavelength[m] > wl) hi = m; else lo = m; }
            double t = (wl - Wavelength[lo]) / (Wavelength[hi] - Wavelength[lo]);
            return Flux[lo] + t * (Flux[hi] - Flux[lo]);
        }

        public Spectrum Resample(double start, double end, double step)
        {
            int n = (int)Math.Floor((end - start) / step) + 1;
            var s = new Spectrum(n) { Header = Header.Clone(), Name = Name, FluxUnit = FluxUnit };
            for (int i = 0; i < n; i++) { s.Wavelength[i] = start + i * step; s.Flux[i] = At(s.Wavelength[i]); }
            return s;
        }

        public double Dispersion => Length > 1 ? (Wavelength[Length - 1] - Wavelength[0]) / (Length - 1) : double.NaN;

        // ---------------------------------------------------------------- I/O

        public static Spectrum Load(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".fit" || ext == ".fits" || ext == ".fts") return LoadFits(path);
            return LoadText(path);
        }

        /// <summary>1D FITS (NAXIS=1, or 2D with one row) with linear or log-linear wavelength solution.</summary>
        public static Spectrum LoadFits(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                var h = FitsIO.ReadHeader(fs);
                int naxis = h.GetInt("NAXIS"), n = h.GetInt("NAXIS1"), bitpix = h.GetInt("BITPIX");
                if (naxis >= 2 && h.GetInt("NAXIS2") > 1)
                {
                    fs.Position = 0;
                    var img = FitsIO.Read(fs);
                    return FromImageRow(img, img.Height / 2);
                }
                int bpp = Math.Abs(bitpix) / 8;
                var buf = new byte[n * bpp];
                fs.Read(buf, 0, buf.Length);
                double bzero = h.GetDouble("BZERO", 0), bscale = h.GetDouble("BSCALE", 1);
                var s = new Spectrum(n) { Header = h, Name = h.GetString("OBJECT") ?? h.GetString("OBJNAME") ?? Path.GetFileNameWithoutExtension(path) };
                for (int i = 0; i < n; i++)
                {
                    int o = i * bpp;
                    double v;
                    switch (bitpix)
                    {
                        case 16: v = (short)((buf[o] << 8) | buf[o + 1]); break;
                        case 32: v = (buf[o] << 24) | (buf[o + 1] << 16) | (buf[o + 2] << 8) | buf[o + 3]; break;
                        case -32: v = BitConverter.ToSingle(new[] { buf[o + 3], buf[o + 2], buf[o + 1], buf[o] }, 0); break;
                        case -64: { var t = new byte[8]; for (int k = 0; k < 8; k++) t[k] = buf[o + 7 - k]; v = BitConverter.ToDouble(t, 0); break; }
                        default: v = buf[o]; break;
                    }
                    s.Flux[i] = v * bscale + bzero;
                }
                double crval = h.GetDouble("CRVAL1", 0), cdelt = h.GetDouble(new[] { "CDELT1", "CD1_1" }, 1), crpix = h.GetDouble("CRPIX1", 1);
                bool log = (h.GetString("CTYPE1") ?? "").ToUpperInvariant().Contains("LOG") || h.GetInt("DC-FLAG", 0) == 1;
                string unit = (h.GetString("CUNIT1") ?? "").ToLowerInvariant();
                double unitScale = unit.StartsWith("nm") ? 10 : unit.StartsWith("um") || unit.StartsWith("micron") ? 1e4 : unit == "m" ? 1e10 : 1;
                for (int i = 0; i < n; i++)
                {
                    double x = crval + (i + 1 - crpix) * cdelt;
                    s.Wavelength[i] = (log ? Math.Pow(10, x) : x) * unitScale;
                }
                return s;
            }
        }

        /// <summary>Two- or three-column text (wavelength, flux[, error]); separators space/tab/comma/semicolon.</summary>
        public static Spectrum LoadText(string path)
        {
            var wl = new List<double>(); var fl = new List<double>(); var er = new List<double>();
            foreach (var line in File.ReadLines(path))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("#") || t.StartsWith("%")) continue;
                var f = t.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 2) continue;
                double a = AstroMath.ParseInvariant(f[0]), b = AstroMath.ParseInvariant(f[1]);
                if (double.IsNaN(a) || double.IsNaN(b)) continue;
                wl.Add(a); fl.Add(b);
                if (f.Length > 2) er.Add(AstroMath.ParseInvariant(f[2]));
            }
            if (wl.Count > 0 && wl.Max() < 50) for (int i = 0; i < wl.Count; i++) wl[i] *= 1e4;      // microns
            else if (wl.Count > 0 && wl.Max() < 2500 && wl.Min() > 200) for (int i = 0; i < wl.Count; i++) wl[i] *= 10; // nm
            var s = new Spectrum(wl.ToArray(), fl.ToArray()) { Name = Path.GetFileNameWithoutExtension(path) };
            if (er.Count == wl.Count) s.Error = er.ToArray();
            return s;
        }

        public void SaveText(string path)
        {
            using (var w = new StreamWriter(path))
            {
                w.WriteLine("# Wavelength(A)\tFlux" + (Error != null ? "\tError" : ""));
                for (int i = 0; i < Length; i++)
                    w.WriteLine(Wavelength[i].ToString("0.0000", CultureInfo.InvariantCulture) + "\t" + Flux[i].ToString("G8", CultureInfo.InvariantCulture) + (Error != null ? "\t" + Error[i].ToString("G6", CultureInfo.InvariantCulture) : ""));
            }
        }

        /// <summary>
        /// Save as linear 1D FITS following the BeSS (Be Star Spectra) database conventions: resampled to constant step,
        /// CRVAL1/CDELT1 in Å, OBJNAME, DATE-OBS, EXPTIME, BSS_VHEL, BSS_INST, BSS_SITE.
        /// </summary>
        public void SaveBessFits(string path, string objName, string observer, string instrument, string site, double vhel = 0)
        {
            double step = Dispersion;
            var lin = Resample(Wavelength[0], Wavelength[Length - 1], step);
            var img = new AstroImage(lin.Length, 1, 1);
            for (int i = 0; i < lin.Length; i++) img.Data[0][i] = (float)lin.Flux[i];
            img.Header = Header.Clone();
            img.Header.Set("CRVAL1", lin.Wavelength[0]); img.Header.Set("CDELT1", step); img.Header.Set("CRPIX1", 1.0);
            img.Header.Set("CTYPE1", "Wavelength"); img.Header.Set("CUNIT1", "Angstrom");
            img.Header.Set("OBJNAME", objName); img.Header.Set("OBSERVER", observer);
            img.Header.Set("BSS_INST", instrument); img.Header.Set("BSS_SITE", site); img.Header.Set("BSS_VHEL", vhel);
            img.Header.Set("BSS_TELL", "None"); img.Header.Set("BSS_COSM", "None");
            // 1D FITS: write NAXIS=1 by writing a 1-row image then patching header is not needed; most tools accept NAXIS2=1
            FitsIO.Write(path, img, -32);
        }

        public static Spectrum FromImageRow(AstroImage img, int row)
        {
            var s = new Spectrum(img.Width) { Header = img.Header.Clone(), Name = img.ObjectName };
            for (int x = 0; x < img.Width; x++) { s.Wavelength[x] = x; s.Flux[x] = img.Data[0][row * img.Width + x]; }
            return s;
        }
    }

    public class SpectralLine
    {
        public string Name;
        public double Wavelength; // Å, air
        public string Kind;       // Balmer, metal, nebular, telluric, lamp
        public SpectralLine(string n, double w, string k) { Name = n; Wavelength = w; Kind = k; }
        public override string ToString() => Name + " " + Wavelength.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>Line lists (air wavelengths, Å).</summary>
    public static class LineLists
    {
        public static readonly List<SpectralLine> Stellar = new List<SpectralLine>
        {
            new SpectralLine("Hα", 6562.81, "Balmer"), new SpectralLine("Hβ", 4861.33, "Balmer"), new SpectralLine("Hγ", 4340.47, "Balmer"),
            new SpectralLine("Hδ", 4101.74, "Balmer"), new SpectralLine("Hε", 3970.07, "Balmer"), new SpectralLine("H8", 3889.05, "Balmer"),
            new SpectralLine("H9", 3835.38, "Balmer"), new SpectralLine("Paα", 18751.0, "Paschen"), new SpectralLine("Paβ", 12818.1, "Paschen"),
            new SpectralLine("Ca II K", 3933.66, "metal"), new SpectralLine("Ca II H", 3968.47, "metal"), new SpectralLine("Ca I", 4226.73, "metal"),
            new SpectralLine("G band (CH)", 4307.9, "molecular"), new SpectralLine("Fe I", 4383.55, "metal"), new SpectralLine("Mg I b1", 5183.60, "metal"),
            new SpectralLine("Mg I b2", 5172.68, "metal"), new SpectralLine("Mg I b4", 5167.32, "metal"), new SpectralLine("Fe I E", 5269.54, "metal"),
            new SpectralLine("Na I D1", 5895.92, "metal"), new SpectralLine("Na I D2", 5889.95, "metal"),
            new SpectralLine("He I", 4471.48, "helium"), new SpectralLine("He I", 5875.62, "helium"), new SpectralLine("He I", 6678.15, "helium"),
            new SpectralLine("He I", 4026.19, "helium"), new SpectralLine("He II", 4685.7, "helium"), new SpectralLine("He II", 5411.52, "helium"),
            new SpectralLine("Ca II", 8498.02, "metal"), new SpectralLine("Ca II", 8542.09, "metal"), new SpectralLine("Ca II", 8662.14, "metal"),
            new SpectralLine("TiO band", 7054.0, "molecular"), new SpectralLine("TiO band", 6158.0, "molecular"), new SpectralLine("Si II", 6347.11, "metal"),
            new SpectralLine("Fe II", 5169.03, "metal"), new SpectralLine("Fe II", 4923.92, "metal"), new SpectralLine("O I", 7774.2, "metal"),
        };

        public static readonly List<SpectralLine> Nebular = new List<SpectralLine>
        {
            new SpectralLine("[O II]", 3727.3, "nebular"), new SpectralLine("[Ne III]", 3868.76, "nebular"), new SpectralLine("[O III]", 4363.21, "nebular"),
            new SpectralLine("[O III]", 4958.91, "nebular"), new SpectralLine("[O III]", 5006.84, "nebular"), new SpectralLine("[N II]", 6548.05, "nebular"),
            new SpectralLine("[N II]", 6583.45, "nebular"), new SpectralLine("[S II]", 6716.44, "nebular"), new SpectralLine("[S II]", 6730.82, "nebular"),
            new SpectralLine("[O I]", 6300.30, "nebular"), new SpectralLine("[Ar III]", 7135.8, "nebular"), new SpectralLine("He I", 5875.62, "nebular"),
            new SpectralLine("Hα", 6562.81, "nebular"), new SpectralLine("Hβ", 4861.33, "nebular"), new SpectralLine("Hγ", 4340.47, "nebular"),
            new SpectralLine("Mg II (QSO)", 2798.0, "nebular"), new SpectralLine("C IV (QSO)", 1549.0, "nebular"), new SpectralLine("Lyα (QSO)", 1215.67, "nebular"),
            new SpectralLine("C III] (QSO)", 1908.7, "nebular"),
        };

        public static readonly List<SpectralLine> Telluric = new List<SpectralLine>
        {
            new SpectralLine("O₂ A band", 7594.0, "telluric"), new SpectralLine("O₂ B band", 6867.2, "telluric"), new SpectralLine("H₂O", 7186.0, "telluric"),
            new SpectralLine("O₂ γ", 6277.0, "telluric"), new SpectralLine("H₂O", 8227.0, "telluric"),
        };

        /// <summary>Calibration lamp lines (air, Å): Neon (red), Argon, Mercury/compact fluorescent, plus hydrogen.</summary>
        public static readonly Dictionary<string, double[]> Lamps = new Dictionary<string, double[]>
        {
            { "Neon", new[] { 5852.49, 5881.90, 5944.83, 5975.53, 6030.00, 6074.34, 6096.16, 6143.06, 6163.59, 6217.28, 6266.50, 6304.79, 6334.43, 6382.99, 6402.25, 6506.53, 6532.88, 6598.95, 6678.28, 6717.04, 6929.47, 7032.41, 7173.94, 7245.17, 7438.90, 7488.87, 7535.77, 8082.46, 8136.41, 8377.61 } },
            { "Argon", new[] { 4158.59, 4198.32, 4259.36, 4300.10, 4333.56, 4510.73, 6965.43, 7067.22, 7272.94, 7383.98, 7503.87, 7635.11, 7723.76, 7948.18, 8006.16, 8014.79, 8115.31, 8264.52, 8408.21, 8424.65, 8521.44, 9122.97 } },
            { "Mercury", new[] { 4046.56, 4077.83, 4358.33, 4916.07, 5460.74, 5769.60, 5790.66 } },
            { "Hydrogen", new[] { 6562.81, 4861.33, 4340.47, 4101.74, 3970.07 } },
            { "Neon+Argon (Alpy)", new[] { 5852.49, 5881.90, 5944.83, 6029.99, 6074.34, 6143.06, 6266.50, 6334.43, 6402.25, 6506.53, 6598.95, 6929.47, 7032.41, 7245.17, 4158.59, 4259.36, 4300.10, 4510.73, 6965.43, 7067.22, 7383.98, 7503.87, 7635.11, 8115.31 } },
        };
    }

    public class LineMeasurement
    {
        public double Centre, Fwhm, Depth, EquivalentWidth, Flux, Continuum;
        public double RadialVelocity; // km/s relative to rest wavelength
        public double RestWavelength;
        public string Name;
        public bool Emission;
    }

    /// <summary>Spectral reduction and analysis.</summary>
    public static class SpectroTools
    {
        public const double C = 299792.458;

        // ---------------------------------------------------------------- extraction from 2D

        /// <summary>Find the spectrum trace: per column-block centroid of the bright row profile, fit with a polynomial.</summary>
        public static double[] FindTrace(AstroImage img, int degree = 2, int blocks = 40)
        {
            int w = img.Width, h = img.Height;
            var d = img.Luminance();
            var xs = new List<double>(); var ys = new List<double>();
            int bw = Math.Max(4, w / blocks);
            for (int b = 0; b < blocks; b++)
            {
                int x0 = b * bw, x1 = Math.Min(w, x0 + bw);
                var prof = new double[h];
                for (int y = 0; y < h; y++) { double s = 0; for (int x = x0; x < x1; x++) s += d[y * w + x]; prof[y] = s / (x1 - x0); }
                double med = Stats.Median(prof);
                int pk = Array.IndexOf(prof, prof.Max());
                if (prof[pk] - med < 5 * Stats.Mad(prof, med) * Stats.MadToSigma) continue;
                double s1 = 0, s2 = 0;
                for (int y = Math.Max(0, pk - 6); y <= Math.Min(h - 1, pk + 6); y++) { double v = prof[y] - med; if (v > 0) { s1 += v; s2 += v * y; } }
                if (s1 > 0) { xs.Add((x0 + x1) / 2.0); ys.Add(s2 / s1); }
            }
            if (xs.Count < degree + 1) return new[] { h / 2.0 };
            // reject outliers
            var c = LinAlg.PolyFit(xs, ys, degree);
            var res = xs.Select((x, i) => ys[i] - LinAlg.PolyEval(c, x)).ToList();
            double mad = Stats.Mad(res, Stats.Median(res)) * Stats.MadToSigma;
            var kx = new List<double>(); var ky = new List<double>();
            for (int i = 0; i < xs.Count; i++) if (Math.Abs(res[i]) < 3 * Math.Max(mad, 0.3)) { kx.Add(xs[i]); ky.Add(ys[i]); }
            return LinAlg.PolyFit(kx, ky, Math.Min(degree, kx.Count - 1)) ?? c;
        }

        /// <summary>
        /// Extract a 1D spectrum along the trace with sky subtraction from side bands. Optimal extraction (Horne 1986)
        /// weights pixels by the normalised spatial profile and inverse variance.
        /// </summary>
        public static Spectrum Extract(AstroImage img, double[] trace, double halfWidth = 5, double skyGap = 4, double skyWidth = 8, bool optimal = true, double gain = 1, double readNoise = 5)
        {
            int w = img.Width, h = img.Height;
            var d = img.Luminance();
            var s = new Spectrum(w) { Header = img.Header.Clone(), Name = img.ObjectName };
            s.Error = new double[w];
            for (int x = 0; x < w; x++)
            {
                double yc = LinAlg.PolyEval(trace, x);
                var sky = new List<double>();
                for (double y = yc - halfWidth - skyGap - skyWidth; y < yc - halfWidth - skyGap; y++) { int yi = (int)Math.Round(y); if (yi >= 0 && yi < h) sky.Add(d[yi * w + x]); }
                for (double y = yc + halfWidth + skyGap; y < yc + halfWidth + skyGap + skyWidth; y++) { int yi = (int)Math.Round(y); if (yi >= 0 && yi < h) sky.Add(d[yi * w + x]); }
                double skyV = sky.Count > 2 ? Stats.Median(sky) : 0;
                int y0 = (int)Math.Floor(yc - halfWidth), y1 = (int)Math.Ceiling(yc + halfWidth);
                double box = 0, n = 0;
                for (int y = y0; y <= y1; y++) if (y >= 0 && y < h) { box += d[y * w + x] - skyV; n++; }
                if (!optimal || box <= 0)
                {
                    s.Wavelength[x] = x; s.Flux[x] = box;
                    s.Error[x] = Math.Sqrt(Math.Max(0, box) / gain + n * (skyV / gain + readNoise * readNoise / (gain * gain) / (65535.0 * 65535.0)));
                    continue;
                }
                // spatial profile from this column (smoothed estimate would be better; use local normalised data)
                double num = 0, den = 0;
                for (int y = y0; y <= y1; y++)
                {
                    if (y < 0 || y >= h) continue;
                    double v = d[y * w + x] - skyV;
                    double P = Math.Max(0, v) / box;
                    double var = Math.Max(1e-12, Math.Max(0, v) * box / Math.Max(1, box) / gain + skyV / gain + 1e-9);
                    num += P * v / var; den += P * P / var;
                }
                s.Wavelength[x] = x;
                s.Flux[x] = den > 0 ? num / den : box;
                s.Error[x] = den > 0 ? Math.Sqrt(1 / den) : 0;
            }
            return s;
        }

        // ---------------------------------------------------------------- calibration

        /// <summary>Peak positions (pixel, sub-pixel by Gaussian-ish parabola) above k sigma of a smoothed baseline.</summary>
        public static List<double> FindPeaks(double[] y, double sigma = 5, int minSeparation = 4)
        {
            int n = y.Length;
            var med = new double[n];
            for (int i = 0; i < n; i++)
            {
                var win = new List<double>();
                for (int k = Math.Max(0, i - 25); k < Math.Min(n, i + 25); k++) win.Add(y[k]);
                med[i] = Stats.Median(win);
            }
            var res = y.Select((v, i) => v - med[i]).ToArray();
            double noise = Stats.Mad(res, Stats.Median(res)) * Stats.MadToSigma;
            var peaks = new List<double>();
            for (int i = 2; i < n - 2; i++)
            {
                if (res[i] < sigma * noise || res[i] < res[i - 1] || res[i] < res[i + 1]) continue;
                double l = res[i - 1], c = res[i], r = res[i + 1];
                double den = l - 2 * c + r;
                double p = i + (Math.Abs(den) > 1e-12 ? 0.5 * (l - r) / den : 0);
                if (peaks.Count > 0 && p - peaks[peaks.Count - 1] < minSeparation) { if (res[(int)Math.Round(peaks[peaks.Count - 1])] < c) peaks[peaks.Count - 1] = p; continue; }
                peaks.Add(p);
            }
            return peaks;
        }

        /// <summary>Fit a dispersion polynomial λ(pixel) from identified (pixel, λ) pairs. Returns coefficients and RMS (Å).</summary>
        public static double[] FitDispersion(IList<double> pixels, IList<double> wavelengths, int degree, out double rms)
        {
            var c = LinAlg.PolyFit(pixels, wavelengths, Math.Min(degree, pixels.Count - 1));
            rms = c == null ? double.NaN : Math.Sqrt(pixels.Select((p, i) => Math.Pow(LinAlg.PolyEval(c, p) - wavelengths[i], 2)).Average());
            return c;
        }

        /// <summary>
        /// Automatic line identification: given an approximate linear dispersion (Å/px) and λ at pixel 0, match lamp peaks
        /// to the line list by searching offsets/scales, then refine with a polynomial.
        /// </summary>
        public static double[] AutoCalibrate(double[] lampFlux, double[] lineList, double approxStart, double approxDispersion, int degree, out double rms, out List<double[]> identified)
        {
            var peaks = FindPeaks(lampFlux, 6);
            identified = new List<double[]>();
            rms = double.NaN;
            double bestScore = -1, bs = approxStart, bd = approxDispersion;
            for (double ds = 0.9; ds <= 1.1; ds += 0.0025)
                for (double off = -150; off <= 150; off += 1)
                {
                    double d = approxDispersion * ds, s0 = approxStart + off;
                    int score = 0;
                    foreach (var p in peaks)
                    {
                        double wl = s0 + d * p;
                        if (lineList.Any(l => Math.Abs(l - wl) < 2 * Math.Abs(d))) score++;
                    }
                    if (score > bestScore) { bestScore = score; bs = s0; bd = d; }
                }
            foreach (var p in peaks)
            {
                double wl = bs + bd * p;
                double best = lineList.OrderBy(l => Math.Abs(l - wl)).First();
                if (Math.Abs(best - wl) < 2 * Math.Abs(bd)) identified.Add(new[] { p, best });
            }
            if (identified.Count < degree + 2) return new[] { bs, bd };
            var c = FitDispersion(identified.Select(i => i[0]).ToList(), identified.Select(i => i[1]).ToList(), degree, out rms);
            // reject >3σ residuals and refit
            double r0 = rms;
            var keep = identified.Where(i => Math.Abs(LinAlg.PolyEval(c, i[0]) - i[1]) < 3 * Math.Max(r0, 0.05)).ToList();
            if (keep.Count >= degree + 2) { identified = keep; c = FitDispersion(keep.Select(i => i[0]).ToList(), keep.Select(i => i[1]).ToList(), degree, out rms); }
            return c;
        }

        public static void ApplyDispersion(Spectrum s, double[] coef)
        {
            for (int i = 0; i < s.Length; i++) s.Wavelength[i] = LinAlg.PolyEval(coef, i);
            s.Header.Set("WAVECAL", "poly" + (coef.Length - 1));
        }

        /// <summary>Instrument response = observed / reference (both continuum-smoothed), avoiding telluric and Balmer regions.</summary>
        public static Spectrum InstrumentResponse(Spectrum observed, Spectrum reference, double smoothAngstrom = 50)
        {
            var r = observed.Clone();
            for (int i = 0; i < r.Length; i++)
            {
                double refv = reference.At(r.Wavelength[i]);
                r.Flux[i] = refv > 0 ? observed.Flux[i] / refv : double.NaN;
            }
            var mask = r.Wavelength.Select(wl => LineLists.Telluric.Any(t => Math.Abs(t.Wavelength - wl) < 25) || LineLists.Stellar.Where(l => l.Kind == "Balmer").Any(l => Math.Abs(l.Wavelength - wl) < 30)).ToArray();
            var xs = new List<double>(); var ys = new List<double>();
            for (int i = 0; i < r.Length; i++) if (!mask[i] && !double.IsNaN(r.Flux[i])) { xs.Add(r.Wavelength[i]); ys.Add(r.Flux[i]); }
            // smooth with a running median then spline
            var sm = new List<double>(); var sx = new List<double>();
            double step = smoothAngstrom;
            for (double wl = r.Wavelength[0]; wl <= r.Wavelength[r.Length - 1]; wl += step)
            {
                var v = ys.Where((y, i) => Math.Abs(xs[i] - wl) < step / 2).ToList();
                if (v.Count > 3) { sx.Add(wl); sm.Add(Stats.Median(v)); }
            }
            if (sx.Count < 4) return r;
            var sp = new CubicSpline(sx, sm);
            for (int i = 0; i < r.Length; i++) r.Flux[i] = sp.Evaluate(r.Wavelength[i]);
            r.Name = "Instrument response";
            return r;
        }

        public static Spectrum Divide(Spectrum a, Spectrum b)
        {
            var r = a.Clone();
            for (int i = 0; i < r.Length; i++) { double d = b.At(a.Wavelength[i]); r.Flux[i] = d != 0 ? a.Flux[i] / d : 0; }
            return r;
        }

        /// <summary>Continuum normalisation by iterative asymmetric sigma clipping of a smoothing spline/polynomial.</summary>
        public static Spectrum Normalize(Spectrum s, int degree = 5, double lowSigma = 1.5, double highSigma = 3, int iterations = 8)
        {
            var keep = Enumerable.Repeat(true, s.Length).ToArray();
            double wmid = s.Wavelength.Average();
            double[] c = null;
            for (int it = 0; it < iterations; it++)
            {
                var xs = new List<double>(); var ys = new List<double>();
                for (int i = 0; i < s.Length; i++) if (keep[i]) { xs.Add(s.Wavelength[i] - wmid); ys.Add(s.Flux[i]); }
                c = LinAlg.PolyFit(xs, ys, degree);
                if (c == null) break;
                var res = new List<double>();
                for (int i = 0; i < s.Length; i++) if (keep[i]) res.Add(s.Flux[i] - LinAlg.PolyEval(c, s.Wavelength[i] - wmid));
                double sig = Stats.StdDev(res);
                bool changed = false;
                for (int i = 0; i < s.Length; i++)
                {
                    double r = s.Flux[i] - LinAlg.PolyEval(c, s.Wavelength[i] - wmid);
                    bool k = r > -lowSigma * sig && r < highSigma * sig;
                    if (k != keep[i]) { keep[i] = k; changed = true; }
                }
                if (!changed) break;
            }
            var o = s.Clone();
            if (c != null) for (int i = 0; i < o.Length; i++) { double cont = LinAlg.PolyEval(c, s.Wavelength[i] - wmid); o.Flux[i] = cont != 0 ? s.Flux[i] / cont : 0; }
            o.FluxUnit = "normalised";
            return o;
        }

        // ---------------------------------------------------------------- measurement

        /// <summary>Measure a line in [wl1, wl2] with linear continuum from the window edges and a Gaussian fit.</summary>
        public static LineMeasurement MeasureLine(Spectrum s, double wl1, double wl2, double restWavelength = double.NaN, string name = null)
        {
            var idx = Enumerable.Range(0, s.Length).Where(i => s.Wavelength[i] >= wl1 && s.Wavelength[i] <= wl2).ToList();
            if (idx.Count < 5) return null;
            int ne = Math.Max(2, idx.Count / 8);
            double cl = idx.Take(ne).Average(i => s.Flux[i]), cr = idx.Skip(idx.Count - ne).Average(i => s.Flux[i]);
            double wl = s.Wavelength[idx[0]], wr = s.Wavelength[idx[idx.Count - 1]];
            Func<double, double> cont = x => cl + (cr - cl) * (x - wl) / (wr - wl);
            double ew = 0, flux = 0;
            for (int k = 1; k < idx.Count; k++)
            {
                int i = idx[k];
                double dw = s.Wavelength[i] - s.Wavelength[idx[k - 1]];
                double c = cont(s.Wavelength[i]);
                ew += (1 - s.Flux[i] / c) * dw;
                flux += (s.Flux[i] - c) * dw;
            }
            bool emission = flux > 0;
            int pk = emission ? idx.OrderByDescending(i => s.Flux[i] - cont(s.Wavelength[i])).First() : idx.OrderBy(i => s.Flux[i] - cont(s.Wavelength[i])).First();
            double amp0 = s.Flux[pk] - cont(s.Wavelength[pk]);
            var xs = idx.Select(i => s.Wavelength[i]).ToArray();
            var ys = idx.Select(i => s.Flux[i] - cont(s.Wavelength[i])).ToArray();
            var fit = LevenbergMarquardt.Fit((p, i) => p[0] * Math.Exp(-0.5 * Math.Pow((xs[i] - p[1]) / p[2], 2)), xs.Length, ys, null,
                new[] { amp0, s.Wavelength[pk], Math.Max(0.3, (wr - wl) / 8) });
            var m = new LineMeasurement
            {
                Name = name, RestWavelength = restWavelength, Emission = emission,
                Centre = fit.Parameters[1], Fwhm = Math.Abs(fit.Parameters[2]) * 2.3548, Depth = fit.Parameters[0] / cont(fit.Parameters[1]),
                EquivalentWidth = ew, Flux = flux, Continuum = cont(fit.Parameters[1])
            };
            if (!double.IsNaN(restWavelength)) m.RadialVelocity = (m.Centre - restWavelength) / restWavelength * C;
            return m;
        }

        /// <summary>Radial velocity by cross-correlation in log-λ space against a template (km/s).</summary>
        public static double CrossCorrelationRv(Spectrum obs, Spectrum template, double maxKms = 1000, double stepKms = 1)
        {
            double w0 = Math.Max(obs.Wavelength[0], template.Wavelength[0]) * (1 + maxKms / C);
            double w1 = Math.Min(obs.Wavelength[obs.Length - 1], template.Wavelength[template.Length - 1]) * (1 - maxKms / C);
            if (w1 <= w0) return double.NaN;
            double dlog = stepKms / C / Math.Log(10);
            int n = (int)((Math.Log10(w1) - Math.Log10(w0)) / dlog);
            var a = new double[n]; var b = new double[n];
            for (int i = 0; i < n; i++) { double wl = Math.Pow(10, Math.Log10(w0) + i * dlog); a[i] = obs.At(wl); b[i] = template.At(wl); }
            double ma = a.Average(), mb = b.Average();
            int maxLag = (int)(maxKms / stepKms);
            double best = double.MinValue; int bl = 0;
            var cc = new double[2 * maxLag + 1];
            for (int lag = -maxLag; lag <= maxLag; lag++)
            {
                double s = 0;
                for (int i = Math.Max(0, lag); i < Math.Min(n, n + lag); i++) s += (a[i] - ma) * (b[i - lag] - mb);
                cc[lag + maxLag] = s;
                if (s > best) { best = s; bl = lag; }
            }
            double frac = 0;
            if (bl > -maxLag && bl < maxLag)
            {
                double l = cc[bl + maxLag - 1], c = cc[bl + maxLag], r = cc[bl + maxLag + 1], den = l - 2 * c + r;
                if (Math.Abs(den) > 1e-20) frac = 0.5 * (l - r) / den;
            }
            return (bl + frac) * stepKms;
        }

        /// <summary>Planck B_λ(T) (arbitrary units) for λ in Å.</summary>
        public static double Planck(double wlA, double T)
        {
            double l = wlA * 1e-10;
            return 1.0 / (Math.Pow(l, 5) * (Math.Exp(0.0143877735 / (l * T)) - 1));
        }

        /// <summary>Fit a blackbody (temperature, scale) to a flux-calibrated spectrum; returns temperature in K.</summary>
        public static double BlackbodyTemperature(Spectrum s, out double scale)
        {
            var xs = s.Wavelength; var ys = s.Flux;
            double tBest = 5800, cBest = 0, chiBest = double.MaxValue;
            for (double T = 2000; T <= 40000; T *= 1.02)
            {
                double num = 0, den = 0;
                for (int i = 0; i < xs.Length; i++) { double b = Planck(xs[i], T); num += ys[i] * b; den += b * b; }
                double c = den > 0 ? num / den : 0, chi = 0;
                for (int i = 0; i < xs.Length; i++) { double r = ys[i] - c * Planck(xs[i], T); chi += r * r; }
                if (chi < chiBest) { chiBest = chi; tBest = T; cBest = c; }
            }
            scale = cBest;
            return tBest;
        }

        /// <summary>Wien peak temperature from the wavelength of maximum flux.</summary>
        public static double WienTemperature(double peakAngstrom) => 2.897771955e7 / peakAngstrom;

        /// <summary>Redshift from an observed/rest wavelength pair; relativistic velocity.</summary>
        public static double Redshift(double observed, double rest) => observed / rest - 1;
        public static double RelativisticVelocity(double z) => C * ((1 + z) * (1 + z) - 1) / ((1 + z) * (1 + z) + 1);

        /// <summary>Barycentric velocity correction (km/s) for a target (deg) at JD: projection of Earth's velocity.</summary>
        public static double BarycentricCorrection(double jdUtc, double raDeg, double decDeg)
        {
            var t = new Core.Time.AstroTime(jdUtc);
            Vec3 p, v;
            Core.Ephemeris.EphemerisManager.State(Core.Ephemeris.Body.Earth, t.JdTDB, out p, out v);
            var u = Vec3.FromSpherical(raDeg * AstroMath.Deg2Rad, decDeg * AstroMath.Deg2Rad);
            return Vec3.Dot(v, u) * AstroMath.AU_km / 86400.0;
        }

        /// <summary>Very rough spectral class from Balmer and metal line strengths in a normalised spectrum (educational).</summary>
        public static string EstimateSpectralClass(Spectrum normalized)
        {
            Func<double, double> ewAt = wl => { var m = MeasureLine(normalized, wl - 15, wl + 15, wl); return m != null ? m.EquivalentWidth : 0; };
            double hb = ewAt(4861.33), hg = ewAt(4340.47), heI = ewAt(4471.48), heII = ewAt(4685.7), caK = ewAt(3933.66), mgb = ewAt(5172.7), na = ewAt(5892), tio = 1 - normalized.At(7054) / Math.Max(1e-6, normalized.At(7040));
            if (heII > 0.3) return "O";
            if (heI > 0.4 && hb > 2) return "B";
            if (hb > 6) return "A";
            if (hb > 2.5 && caK < 6) return "F";
            if (tio > 0.15) return "M";
            if (mgb > 2 && na > 2 && hb < 1.5) return "K";
            return "G";
        }
    }
}
