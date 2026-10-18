using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Sidralune.Core;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;

namespace Sidralune.Science.Radio
{
    public static class RadioMath
    {
        public const double Boltzmann = 1.380649e-23;
        public const double Jansky = 1e-26;               // W m^-2 Hz^-1
        public const double HydrogenLineMHz = 1420.405751768;
        public const double OhLineMHz = 1667.359;
        public const double C = 299792458.0;

        public static double WavelengthM(double freqHz) => C / freqHz;
        /// <summary>Half-power beam width (deg) for a parabolic dish with typical illumination taper (k = 70).</summary>
        public static double BeamWidthDeg(double freqHz, double diameterM, double k = 70) => k * WavelengthM(freqHz) / diameterM;
        /// <summary>Antenna gain in dBi for aperture efficiency η.</summary>
        public static double DishGainDbi(double freqHz, double diameterM, double efficiency = 0.55) => 10 * Math.Log10(efficiency * Math.Pow(Math.PI * diameterM / WavelengthM(freqHz), 2));
        public static double EffectiveArea(double diameterM, double efficiency = 0.55) => efficiency * Math.PI * diameterM * diameterM / 4;
        /// <summary>Antenna temperature (K) produced by a source of flux S (Jy) for one polarisation.</summary>
        public static double AntennaTemperature(double fluxJy, double effectiveAreaM2) => fluxJy * Jansky * effectiveAreaM2 / (2 * Boltzmann);
        /// <summary>Radiometer equation: minimum detectable ΔT (K) = Tsys / sqrt(B τ).</summary>
        public static double RadiometerSensitivity(double tsysK, double bandwidthHz, double integrationSec) => tsysK / Math.Sqrt(bandwidthHz * integrationSec);
        /// <summary>System equivalent flux density (Jy).</summary>
        public static double Sefd(double tsysK, double effectiveAreaM2) => 2 * Boltzmann * tsysK / effectiveAreaM2 / Jansky;
        public static double DbToRatio(double db) => Math.Pow(10, db / 10);
        public static double RatioToDb(double r) => 10 * Math.Log10(r);
        /// <summary>Y-factor method: Tsys = (Thot - Y Tcold)/(Y - 1).</summary>
        public static double TsysFromYFactor(double y, double tHot = 290, double tCold = 10) => (tHot - y * tCold) / (y - 1);
        /// <summary>Radio velocity convention (km/s) from observed/rest frequency.</summary>
        public static double RadioVelocity(double fObs, double fRest) => C / 1000.0 * (fRest - fObs) / fRest;
        public static double FrequencyForVelocity(double vKms, double fRest) => fRest * (1 - vKms * 1000 / C);
    }

    /// <summary>Velocity frames for spectral-line work (hydrogen line).</summary>
    public static class VelocityFrames
    {
        /// <summary>Solar motion relative to the LSR, Schönrich, Binney &amp; Dehnen (2010): (U,V,W) = (11.1, 12.24, 7.25) km/s.</summary>
        public static readonly double[] SolarUvw = { 11.1, 12.24, 7.25 };
        /// <summary>Galactocentric distance of the Sun (kpc; GRAVITY 2019) and circular speed (km/s; Eilers et al. 2019 ~229).</summary>
        public const double R0Kpc = 8.18, V0Kms = 229.0;

        /// <summary>Correction (km/s) to add to a topocentric velocity to obtain V_LSR for a target (ICRS deg) at a time.</summary>
        public static double LsrCorrection(Observer obs, AstroTime t, double raDeg, double decDeg)
        {
            var u = Vec3.FromSpherical(raDeg * AstroMath.Deg2Rad, decDeg * AstroMath.Deg2Rad);
            var ctx = new SkyContext(obs, t);
            // observer barycentric velocity (km/s) incl. diurnal: ctx.Beta * c
            var vObs = ctx.Beta * AstroMath.C_kms;
            double vBary = Vec3.Dot(vObs, u);
            // solar motion: convert UVW (galactic, U towards GC, V rotation, W NGP) to ICRS vector
            double ra, dec;
            Transforms.GalacticToEquatorial(0, 0, out ra, out dec); var ex = Vec3.FromSpherical(ra, dec);
            Transforms.GalacticToEquatorial(AstroMath.HalfPi, 0, out ra, out dec); var ey = Vec3.FromSpherical(ra, dec);
            Transforms.GalacticToEquatorial(0, AstroMath.HalfPi, out ra, out dec); var ez = Vec3.FromSpherical(ra, dec);
            var vSun = ex * SolarUvw[0] + ey * SolarUvw[1] + ez * SolarUvw[2];
            return vBary + Vec3.Dot(vSun, u);
        }

        /// <summary>Flat-rotation-curve LSR velocity expected at galactic (l, b) and distance d (kpc).</summary>
        public static double ModelVlsr(double lDeg, double bDeg, double dKpc, double v0 = V0Kms, double r0 = R0Kpc)
        {
            double l = lDeg * AstroMath.Deg2Rad, b = bDeg * AstroMath.Deg2Rad;
            double dproj = dKpc * Math.Cos(b);
            double R = Math.Sqrt(r0 * r0 + dproj * dproj - 2 * r0 * dproj * Math.Cos(l));
            return (v0 * r0 / R - v0) * Math.Sin(l) * Math.Cos(b);
        }

        /// <summary>Tangent-point method: from the maximum velocity at longitude l (inner Galaxy) get R and V(R).</summary>
        public static void TangentPoint(double lDeg, double vMaxKms, out double radiusKpc, out double rotationKms, double r0 = R0Kpc, double v0 = V0Kms)
        {
            double sl = Math.Sin(lDeg * AstroMath.Deg2Rad);
            radiusKpc = r0 * Math.Abs(sl);
            rotationKms = vMaxKms * Math.Sign(sl) + v0 * Math.Abs(sl);
        }

        /// <summary>Kinematic distance(s) (kpc) for V_LSR at longitude l (flat rotation); returns near and far solutions.</summary>
        public static double[] KinematicDistances(double lDeg, double vlsr, double r0 = R0Kpc, double v0 = V0Kms)
        {
            double l = lDeg * AstroMath.Deg2Rad;
            double sl = Math.Sin(l), cl = Math.Cos(l);
            double denom = vlsr / sl + v0;
            if (Math.Abs(sl) < 1e-6 || denom <= 0) return new double[0];
            double R = v0 * r0 / denom;
            double disc = R * R - r0 * r0 * sl * sl;
            if (disc < 0) return new double[0];
            double s = Math.Sqrt(disc);
            var d = new List<double> { r0 * cl - s, r0 * cl + s };
            return d.Where(x => x > 0).ToArray();
        }
    }

    public enum IqFormat { RtlSdrUint8, HackRfInt8, Int16, Float32, WavStereo16 }

    public class RadioSpectrum
    {
        public double[] FrequencyHz;
        public double[] Power;      // linear (arbitrary)
        public double CentreHz, SampleRate;
        public int Averages;
        public double[] PowerDb => Power.Select(p => 10 * Math.Log10(Math.Max(1e-30, p))).ToArray();
        public double[] VelocityKms(double restHz, double correction = 0) => FrequencyHz.Select(f => RadioMath.RadioVelocity(f, restHz) + correction).ToArray();
    }

    /// <summary>Spectral analysis of complex baseband recordings (Welch averaging).</summary>
    public static class IqProcessing
    {
        public static IEnumerable<double[]> ReadIq(string path, IqFormat fmt, int samplesPerBlock)
        {
            using (var fs = File.OpenRead(path))
            {
                if (fmt == IqFormat.WavStereo16) fs.Position = 44;
                int bytesPer = fmt == IqFormat.RtlSdrUint8 || fmt == IqFormat.HackRfInt8 ? 2 : fmt == IqFormat.Float32 ? 8 : 4;
                var buf = new byte[samplesPerBlock * bytesPer];
                while (true)
                {
                    int r = fs.Read(buf, 0, buf.Length);
                    if (r < buf.Length) yield break;
                    var iq = new double[samplesPerBlock * 2];
                    for (int i = 0; i < samplesPerBlock; i++)
                    {
                        switch (fmt)
                        {
                            case IqFormat.RtlSdrUint8: iq[2 * i] = (buf[2 * i] - 127.5) / 127.5; iq[2 * i + 1] = (buf[2 * i + 1] - 127.5) / 127.5; break;
                            case IqFormat.HackRfInt8: iq[2 * i] = (sbyte)buf[2 * i] / 128.0; iq[2 * i + 1] = (sbyte)buf[2 * i + 1] / 128.0; break;
                            case IqFormat.Float32: iq[2 * i] = BitConverter.ToSingle(buf, 8 * i); iq[2 * i + 1] = BitConverter.ToSingle(buf, 8 * i + 4); break;
                            default: iq[2 * i] = BitConverter.ToInt16(buf, 4 * i) / 32768.0; iq[2 * i + 1] = BitConverter.ToInt16(buf, 4 * i + 2) / 32768.0; break;
                        }
                    }
                    yield return iq;
                }
            }
        }

        /// <summary>Averaged power spectrum (fftshifted, DC removed) from interleaved IQ blocks.</summary>
        public static RadioSpectrum Spectrum(IEnumerable<double[]> blocks, int fftSize, double centreHz, double sampleRate, int maxBlocks = int.MaxValue, Action<double[]> waterfallRow = null)
        {
            var acc = new double[fftSize];
            var win = Fft.BlackmanHarrisWindow(fftSize);
            int count = 0;
            var re = new double[fftSize]; var im = new double[fftSize];
            foreach (var b in blocks)
            {
                int nseg = b.Length / 2 / fftSize;
                for (int s = 0; s < nseg; s++)
                {
                    for (int i = 0; i < fftSize; i++) { re[i] = b[2 * (s * fftSize + i)] * win[i]; im[i] = b[2 * (s * fftSize + i) + 1] * win[i]; }
                    Fft.Transform(re, im, false);
                    var row = waterfallRow != null ? new double[fftSize] : null;
                    for (int i = 0; i < fftSize; i++)
                    {
                        int k = (i + fftSize / 2) % fftSize; // fftshift
                        double p = re[k] * re[k] + im[k] * im[k];
                        acc[i] += p;
                        if (row != null) row[i] = p;
                    }
                    if (row != null) waterfallRow(row);
                    count++;
                }
                if (count >= maxBlocks) break;
            }
            for (int i = 0; i < fftSize; i++) acc[i] /= Math.Max(1, count);
            // suppress the DC spike typical of zero-IF receivers
            int c = fftSize / 2;
            if (fftSize > 8) acc[c] = 0.5 * (acc[c - 2] + acc[c + 2]);
            var spec = new RadioSpectrum { Power = acc, CentreHz = centreHz, SampleRate = sampleRate, Averages = count, FrequencyHz = new double[fftSize] };
            for (int i = 0; i < fftSize; i++) spec.FrequencyHz[i] = centreHz + (i - fftSize / 2) * sampleRate / fftSize;
            return spec;
        }

        /// <summary>On/off (or frequency-switched) calibration: (on - off)/off × Tsys.</summary>
        public static double[] OnOff(RadioSpectrum on, RadioSpectrum off, double tsys)
        {
            var r = new double[on.Power.Length];
            for (int i = 0; i < r.Length; i++) r[i] = off.Power[i] > 0 ? (on.Power[i] - off.Power[i]) / off.Power[i] * tsys : 0;
            return r;
        }

        /// <summary>Polynomial baseline removal excluding a velocity window around the line.</summary>
        public static double[] Baseline(double[] x, double[] y, int degree, double excludeLo, double excludeHi)
        {
            var xs = new List<double>(); var ys = new List<double>();
            for (int i = 0; i < x.Length; i++) if (x[i] < excludeLo || x[i] > excludeHi) { xs.Add(x[i]); ys.Add(y[i]); }
            var c = LinAlg.PolyFit(xs, ys, degree);
            return y.Select((v, i) => c != null ? v - LinAlg.PolyEval(c, x[i]) : v).ToArray();
        }
    }

    /// <summary>Client for rtl_tcp servers (RTL-SDR dongles), streaming 8-bit IQ.</summary>
    public class RtlTcpClient : IDisposable
    {
        private TcpClient _tcp;
        private NetworkStream _ns;
        public string TunerType { get; private set; }
        public int GainCount { get; private set; }

        public void Connect(string host = "127.0.0.1", int port = 1234)
        {
            _tcp = new TcpClient(host, port);
            _ns = _tcp.GetStream();
            var hdr = new byte[12];
            int r = 0;
            while (r < 12) { int k = _ns.Read(hdr, r, 12 - r); if (k <= 0) throw new IOException("rtl_tcp closed"); r += k; }
            if (hdr[0] != 'R' || hdr[1] != 'T' || hdr[2] != 'L' || hdr[3] != '0') throw new IOException("Not an rtl_tcp server");
            int tuner = (hdr[4] << 24) | (hdr[5] << 16) | (hdr[6] << 8) | hdr[7];
            string[] names = { "Unknown", "E4000", "FC0012", "FC0013", "FC2580", "R820T", "R828D" };
            TunerType = tuner >= 0 && tuner < names.Length ? names[tuner] : tuner.ToString();
            GainCount = (hdr[8] << 24) | (hdr[9] << 16) | (hdr[10] << 8) | hdr[11];
        }

        private void Cmd(byte cmd, uint value)
        {
            var b = new byte[] { cmd, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value };
            _ns.Write(b, 0, 5);
        }

        public void SetFrequency(double hz) => Cmd(0x01, (uint)hz);
        public void SetSampleRate(double hz) => Cmd(0x02, (uint)hz);
        public void SetManualGain(bool manual) => Cmd(0x03, manual ? 1u : 0u);
        /// <summary>Gain in tenths of dB.</summary>
        public void SetGain(int tenthsDb) => Cmd(0x04, (uint)tenthsDb);
        public void SetPpm(int ppm) => Cmd(0x05, (uint)ppm);
        public void SetAgc(bool on) => Cmd(0x08, on ? 1u : 0u);
        public void SetBiasTee(bool on) => Cmd(0x0e, on ? 1u : 0u);

        /// <summary>Read n complex samples as interleaved doubles.</summary>
        public double[] Read(int samples, CancellationToken ct = default(CancellationToken))
        {
            var buf = new byte[samples * 2];
            int r = 0;
            while (r < buf.Length)
            {
                ct.ThrowIfCancellationRequested();
                int k = _ns.Read(buf, r, buf.Length - r);
                if (k <= 0) throw new IOException("rtl_tcp stream ended");
                r += k;
            }
            var iq = new double[buf.Length];
            for (int i = 0; i < buf.Length; i++) iq[i] = (buf[i] - 127.5) / 127.5;
            return iq;
        }

        public void Dispose() { _ns?.Dispose(); _tcp?.Close(); }
    }

    public class RadioSource
    {
        public string Name, Type;
        public double RaDeg, DecDeg;
        public double Flux1400Jy, SpectralIndex;
        public double FluxAt(double freqHz) => Flux1400Jy * Math.Pow(freqHz / 1.4e9, SpectralIndex);
    }

    public static class RadioSources
    {
        /// <summary>Bright calibrator/landmark sources (J2000, flux density at 1.4 GHz, spectral index α with S ∝ ν^α).</summary>
        public static readonly List<RadioSource> Bright = new List<RadioSource>
        {
            new RadioSource { Name = "Cassiopeia A", Type = "SNR", RaDeg = 350.85, DecDeg = 58.815, Flux1400Jy = 1700, SpectralIndex = -0.77 },
            new RadioSource { Name = "Cygnus A", Type = "Radio galaxy", RaDeg = 299.868, DecDeg = 40.734, Flux1400Jy = 1590, SpectralIndex = -1.0 },
            new RadioSource { Name = "Taurus A (Crab)", Type = "SNR/pulsar", RaDeg = 83.633, DecDeg = 22.014, Flux1400Jy = 875, SpectralIndex = -0.30 },
            new RadioSource { Name = "Virgo A (M87)", Type = "Radio galaxy", RaDeg = 187.706, DecDeg = 12.391, Flux1400Jy = 210, SpectralIndex = -0.82 },
            new RadioSource { Name = "Centaurus A", Type = "Radio galaxy", RaDeg = 201.365, DecDeg = -43.019, Flux1400Jy = 1330, SpectralIndex = -0.65 },
            new RadioSource { Name = "Sagittarius A", Type = "Galactic centre", RaDeg = 266.417, DecDeg = -29.008, Flux1400Jy = 2000, SpectralIndex = -0.5 },
            new RadioSource { Name = "Orion A (M42)", Type = "HII region", RaDeg = 83.82, DecDeg = -5.39, Flux1400Jy = 360, SpectralIndex = 0.0 },
            new RadioSource { Name = "3C 273", Type = "Quasar", RaDeg = 187.278, DecDeg = 2.052, Flux1400Jy = 43, SpectralIndex = -0.35 },
            new RadioSource { Name = "Fornax A", Type = "Radio galaxy", RaDeg = 50.674, DecDeg = -37.208, Flux1400Jy = 125, SpectralIndex = -0.8 },
            new RadioSource { Name = "Hydra A", Type = "Radio galaxy", RaDeg = 139.524, DecDeg = -12.096, Flux1400Jy = 43, SpectralIndex = -0.9 },
            new RadioSource { Name = "Hercules A", Type = "Radio galaxy", RaDeg = 252.784, DecDeg = 4.993, Flux1400Jy = 45, SpectralIndex = -1.0 },
            new RadioSource { Name = "Pictor A", Type = "Radio galaxy", RaDeg = 79.957, DecDeg = -45.779, Flux1400Jy = 66, SpectralIndex = -0.8 },
            new RadioSource { Name = "3C 48", Type = "Quasar", RaDeg = 24.422, DecDeg = 33.160, Flux1400Jy = 16, SpectralIndex = -0.8 },
            new RadioSource { Name = "PKS 1934-638", Type = "Calibrator", RaDeg = 294.854, DecDeg = -63.713, Flux1400Jy = 14.9, SpectralIndex = -0.6 },
            new RadioSource { Name = "W51", Type = "HII complex", RaDeg = 290.92, DecDeg = 14.10, Flux1400Jy = 700, SpectralIndex = -0.1 },
            new RadioSource { Name = "Cygnus X", Type = "Star-forming complex", RaDeg = 307.5, DecDeg = 41.0, Flux1400Jy = 1000, SpectralIndex = -0.1 },
        };
    }

    public static class JupiterRadio
    {
        /// <summary>Io phase (deg) measured from superior geocentric conjunction, and System III CML (deg).</summary>
        public static void IoPhaseAndCml(double jdTT, out double ioPhase, out double cml)
        {
            var moons = SolarSystem.GalileanMoons.Positions(jdTT);
            // Io phase from X and front/back: phase = atan2(X, -Z) style; recover from Meeus u: X = r sin u, front when cos u > 0
            var io = moons[0];
            double r = 5.9057;
            double su = AstroMath.Clamp(io.X / r, -1, 1);
            double u = Math.Asin(su) * AstroMath.Rad2Deg;
            if (!io.InFront) u = 180 - u;
            // u = 0 at inferior conjunction; Io phase is 0 at superior conjunction
            ioPhase = AstroMath.NormalizeDeg(u + 180);
            var obs = new Observer { LatitudeDeg = 0, LongitudeDeg = 0 };
            var ctx = new SkyContext(obs, AstroTime.FromTT(jdTT));
            var jp = ctx.BodyPlace(Body.Jupiter);
            double s1, s2, s3;
            PlanetPhysics.JupiterCentralMeridians(ctx.JdTDB, jp.Astrometric.ToVector(), jp.LightTimeDays, out s1, out s2, out s3);
            cml = s3;
        }

        /// <summary>Source regions (Carr et al. 1983 style boxes, approximate) active for a given CML and Io phase.</summary>
        public static string ActiveSources(double cml, double ioPhase)
        {
            var list = new List<string>();
            Func<double, double, double, bool> inR = (v, a, b) => a <= b ? v >= a && v <= b : v >= a || v <= b;
            if (inR(cml, 200, 290) && inR(ioPhase, 195, 265)) list.Add("Io-A");
            if (inR(cml, 90, 200) && inR(ioPhase, 75, 110)) list.Add("Io-B");
            if (inR(cml, 290, 20) && inR(ioPhase, 225, 255)) list.Add("Io-C");
            if (inR(cml, 0, 200) && inR(ioPhase, 95, 130)) list.Add("Io-D");
            if (list.Count == 0 && inR(cml, 200, 290)) list.Add("non-Io-A");
            return list.Count > 0 ? string.Join(", ", list) : "none";
        }

        /// <summary>List windows in the next 'days' when an Io-related source is predicted and Jupiter is above the horizon.</summary>
        public static List<string> Predict(Observer obs, AstroTime start, double days)
        {
            var res = new List<string>();
            string prev = "none"; double windowStart = 0;
            for (double jd = start.JdUtc; jd < start.JdUtc + days; jd += 10.0 / 1440)
            {
                var t = new AstroTime(jd);
                double ph, cml;
                IoPhaseAndCml(t.JdTT, out ph, out cml);
                string s = ActiveSources(cml, ph);
                var ctx = new SkyContext(obs, t);
                bool up = ctx.BodyPlace(Body.Jupiter).Horizontal.AltDeg > 5 && ctx.BodyPlace(Body.Sun).Horizontal.AltDeg < 0;
                if (!up) s = "none";
                if (s != prev)
                {
                    if (prev != "none") res.Add(string.Format("{0} → {1}: {2}", new AstroTime(windowStart), t, prev));
                    windowStart = jd; prev = s;
                }
            }
            return res;
        }
    }

    public static class TimeDomainRadio
    {
        /// <summary>Fit a Gaussian to a drift-scan (time, power) to derive transit time, FWHM (s) and peak.</summary>
        public static double[] FitDriftScan(IList<double> t, IList<double> p)
        {
            int imax = 0;
            for (int i = 1; i < p.Count; i++) if (p[i] > p[imax]) imax = i;
            double baseline = Stats.Percentile(p, 10);
            var tt = t.ToArray();
            var fit = LevenbergMarquardt.Fit((q, i) => q[0] * Math.Exp(-0.5 * Math.Pow((tt[i] - q[1]) / q[2], 2)) + q[3] + q[4] * (tt[i] - tt[0]),
                t.Count, p.ToArray(), null, new[] { p[imax] - baseline, t[imax], (t[t.Count - 1] - t[0]) / 10, baseline, 0 });
            return new[] { fit.Parameters[1], Math.Abs(fit.Parameters[2]) * 2.3548, fit.Parameters[0] };
        }

        /// <summary>Beam width (deg) from drift-scan FWHM (s) at declination: HPBW = 15.041″/s · FWHM · cos δ (sidereal rate).</summary>
        public static double BeamFromDrift(double fwhmSec, double decDeg) => fwhmSec * 15.041 / 3600.0 * Math.Cos(decDeg * AstroMath.Deg2Rad);

        /// <summary>Epoch folding of a time series at a trial period: returns the mean profile in nbins phase bins.</summary>
        public static double[] Fold(IList<double> t, IList<double> y, double period, int nbins, double epoch = 0)
        {
            var s = new double[nbins]; var c = new int[nbins];
            for (int i = 0; i < t.Count; i++)
            {
                double ph = ((t[i] - epoch) / period) % 1; if (ph < 0) ph += 1;
                int b = Math.Min(nbins - 1, (int)(ph * nbins));
                s[b] += y[i]; c[b]++;
            }
            for (int b = 0; b < nbins; b++) s[b] = c[b] > 0 ? s[b] / c[b] : 0;
            return s;
        }

        /// <summary>Dispersion delay (s) between two frequencies (MHz) for a dispersion measure (pc cm^-3).</summary>
        public static double DispersionDelay(double dm, double fLowMHz, double fHighMHz) => 4.148808e3 * dm * (1 / (fLowMHz * fLowMHz) - 1 / (fHighMHz * fHighMHz));

        /// <summary>Incoherent dedispersion of a filterbank (channels × time) by shifting each channel.</summary>
        public static double[] Dedisperse(double[][] filterbank, double[] channelMHz, double dm, double sampleTimeSec)
        {
            int nt = filterbank[0].Length;
            var o = new double[nt];
            double fmax = channelMHz.Max();
            for (int c = 0; c < filterbank.Length; c++)
            {
                int shift = (int)Math.Round(DispersionDelay(dm, channelMHz[c], fmax) / sampleTimeSec);
                for (int i = 0; i < nt; i++) { int j = i + shift; if (j < nt) o[i] += filterbank[c][j]; }
            }
            return o;
        }

        /// <summary>Sudden ionospheric disturbance detection in a VLF amplitude log: flags rapid rises above a running baseline.</summary>
        public static List<int> DetectSid(IList<double> amplitude, int window = 60, double kSigma = 4)
        {
            var events = new List<int>();
            for (int i = window; i < amplitude.Count; i++)
            {
                var seg = amplitude.Skip(i - window).Take(window).ToList();
                double med = Stats.Median(seg), mad = Stats.Mad(seg, med) * Stats.MadToSigma;
                if (amplitude[i] - med > kSigma * Math.Max(mad, 1e-12) && (events.Count == 0 || i - events[events.Count - 1] > window)) events.Add(i);
            }
            return events;
        }
    }
}
