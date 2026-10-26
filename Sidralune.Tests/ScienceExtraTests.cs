using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Sidralune.Core;
using Sidralune.Core.Catalogs;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.MinorBodies;
using Sidralune.Core.Time;
using Sidralune.Core.Util;
using Sidralune.Imaging;
using Sidralune.Science.Astrometry;
using Sidralune.Science.Databases;
using Sidralune.Science.Photometry;
using Sidralune.Science.Radio;
using Sidralune.Science.SolarSystem;
using Sidralune.Science.Spectroscopy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Sidralune.Tests.Checks;

namespace Sidralune.Tests
{
    /// <summary>Photometry, timing, light curves, radio, meteors, occultations, spectra, catalogue providers and data tables.</summary>
    [TestClass]
    public class ScienceExtraTests : TestBase
    {
        private static string Folder(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "SidraluneTests", name);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            return dir;
        }

        [TestMethod]
        public void TestAperturePhotometry()
        {
            int w = 80, h = 80;
            double flux = 0.8; // normalised units; × AduScale gives ADU
            var img = Synthetic.Render(w, h, new[] { new Synthetic.SynthStar { X = 40.3, Y = 39.6, Flux = flux } }, Transform2D.Identity, 3, 0.1, 0.0001, 1, 0, 1e9);
            var s = new ApertureSettings { Radius = 8, InnerAnnulus = 12, OuterAnnulus = 18, AduScale = 65535 };
            var m = AperturePhotometry.Measure(img.Data[0], w, h, 42, 38, s);
            Check("Recentred x", m.X, 40.3, 0.05);
            Check("Recentred y", m.Y, 39.6, 0.05);
            Check("Sky level (ADU)", m.Sky, 0.1 * 65535, 30);
            Check("Total flux within 1%", m.Flux / (flux * 65535), 1, 0.01);
            Check("Instrumental magnitude", m.InstMag, -2.5 * Math.Log10(m.Flux) + 25, 1e-9);
            CheckTrue("SNR and error consistent", m.Snr > 30 && Math.Abs(m.InstMagError - 1.0857 / m.Snr) < 1e-9);
            CheckTrue("Not saturated", !m.Saturated);
            var cog = AperturePhotometry.CurveOfGrowth(img.Data[0], w, h, 40.3, 39.6, s, 15);
            CheckTrue("Curve of growth levels off", cog.Count > 5 && Math.Abs(cog[cog.Count - 1][1] - cog[cog.Count - 3][1]) < 0.02 && cog[0][1] > cog[cog.Count - 1][1], string.Join(" ", cog.Select(p => p[0].ToString("0") + ":" + p[1].ToString("0.00"))));
            var bright = Synthetic.Render(w, h, new[] { new Synthetic.SynthStar { X = 40, Y = 40, Flux = 30 } }, Transform2D.Identity, 3, 0.1, 0.0005, 1);
            bright.Clip();
            CheckTrue("Saturated star flagged", AperturePhotometry.Measure(bright.Data[0], w, h, 40, 40, s).Saturated);
            var empty = new float[w * h];
            CheckTrue("Star at the edge gives no magnitude", double.IsNaN(AperturePhotometry.Measure(empty, w, h, 1, 1, new ApertureSettings { Recentre = false, InnerAnnulus = 70, OuterAnnulus = 75 }).InstMag));
            // a measurement works on a copy, so the settings shown can change while it works
            var run = s.Clone();
            s.Radius = 3; s.Recentre = false;
            CheckTrue("A measurement keeps the aperture settings it started with", run.Radius == 8 && run.Recentre && run != s);
        }

        [TestMethod]
        public void TestDifferentialPhotometry()
        {
            int w = 300, h = 200;
            var wcs = Wcs.Create(100, 20, 2.0, 0, w, h);
            Func<double, double, double[]> radec = (x, y) => { double ra, dec; wcs.PixelToWorld(x, y, out ra, out dec); return new[] { ra, dec }; };
            var pTarget = new[] { 80.0, 100.0 }; var pComp1 = new[] { 200.0, 60.0 }; var pComp2 = new[] { 220.0, 150.0 }; var pCheck = new[] { 150.0, 40.0 };
            var dp = new DifferentialPhotometry();
            Func<string, double[], bool, bool, double, PhotometryTarget> target = (name, p, comp, check, mag) =>
            {
                var rd = radec(p[0], p[1]);
                return new PhotometryTarget { Name = name, Ra = rd[0], Dec = rd[1], IsComparison = comp, IsCheck = check, CatalogMag = mag };
            };
            dp.Targets.Add(target("V1", pTarget, false, false, double.NaN));
            dp.Targets.Add(target("C1", pComp1, true, false, 10.0));
            dp.Targets.Add(target("C2", pComp2, true, false, 10.5));
            dp.Targets.Add(target("K", pCheck, false, true, double.NaN));
            Func<double, double> fluxOf = mag => 0.5 * Math.Pow(10, -0.4 * (mag - 10));
            var truth = new List<double>();
            for (int i = 0; i < 8; i++)
            {
                double vmag = 10.8 + 0.3 * Math.Sin(i * 0.8);
                truth.Add(vmag);
                var stars = new[]
                {
                    new Synthetic.SynthStar { X = pTarget[0], Y = pTarget[1], Flux = fluxOf(vmag) }, new Synthetic.SynthStar { X = pComp1[0], Y = pComp1[1], Flux = fluxOf(10.0) },
                    new Synthetic.SynthStar { X = pComp2[0], Y = pComp2[1], Flux = fluxOf(10.5) }, new Synthetic.SynthStar { X = pCheck[0], Y = pCheck[1], Flux = fluxOf(11.2) }
                };
                var img = Synthetic.Render(w, h, stars, Transform2D.Identity, 3.0, 0.05, 0.0002, 10 + i, 0, 1e8);
                img.SourcePath = "frame" + i + ".fits";
                dp.AddFrame(img, wcs, 2460000.5 + i * 0.01, 1.2, "V");
            }
            var curve = dp.Curves.ContainsKey("V1") ? dp.Curves["V1"] : new List<LightCurvePoint>();
            CheckTrue("A light-curve point per frame", curve.Count == 8, curve.Count + " points");
            if (curve.Count == 8)
            {
                double worst = Enumerable.Range(0, 8).Max(i => Math.Abs(curve[i].Mag - truth[i]));
                CheckTrue("Target magnitudes recovered to 0.03", worst < 0.03, worst.ToString("0.0000"));
                CheckTrue("Check star magnitude recovered", curve.All(p => Math.Abs(p.CheckMag - 11.2) < 0.04), curve[0].CheckMag.ToString("0.000"));
                // the error comes from the CCD equation with the configured gain (1 e-/ADU), not from the image's actual noise
                CheckTrue("Relative flux and errors", curve.All(p => p.Flux > 0 && p.MagError > 0.01 && p.MagError < 0.1 && p.Airmass == 1.2), string.Format("flux {0:0.000}, error {1:0.0000}, airmass {2}", curve[0].Flux, curve[0].MagError, curve[0].Airmass));
                string aavso = DifferentialPhotometry.AavsoExtended("V1 Tst", curve, "XYZ", "ENSEMBLE", "K", "X12345");
                var lines = aavso.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                CheckTrue("AAVSO extended format", lines[0] == "#TYPE=EXTENDED" && lines.Count(l => !l.StartsWith("#")) == 8 && lines.Last().Split(',').Length == 15 && lines.Last().StartsWith("V1 Tst,2460000.57000,"));
                string csv = Path.Combine(Folder("photometry"), "curve.csv");
                DifferentialPhotometry.ExportCsv(csv, curve);
                using (var r = CsvReader.Open(csv))
                {
                    int rows = 0; string[] row;
                    while ((row = r.ReadRow()) != null) { rows++; }
                    CheckTrue("Light curve CSV export", r.Header[0] == "JD" && rows == 8);
                }
            }
        }

        [TestMethod]
        public void TestTimingAndLightCurves()
        {
            // star at the anti-solar point: light reaches the Earth 8.3 min before the Sun
            double jd = AstroTime.CalendarToJd(2025, 6, 21);
            double l, b, r;
            PlanetPhysics.SunApparentEcliptic(jd, out l, out b, out r);
            double ra, dec;
            Sidralune.Core.Coordinates.Transforms.EclipticToEquatorial((l + 180) * AstroMath.Deg2Rad, 0, 23.44 * AstroMath.Deg2Rad, out ra, out dec);
            double anti = (TimeCorrections.HjdUtc(jd, ra * AstroMath.Rad2Deg, dec * AstroMath.Rad2Deg) - jd) * 1440;
            Check("HJD correction at the anti-solar point (min)", anti, r * 8.3167, 0.05);
            Sidralune.Core.Coordinates.Transforms.EclipticToEquatorial(l * AstroMath.Deg2Rad, 0, 23.44 * AstroMath.Deg2Rad, out ra, out dec);
            Check("HJD correction towards the Sun (min)", (TimeCorrections.HjdUtc(jd, ra * AstroMath.Rad2Deg, dec * AstroMath.Rad2Deg) - jd) * 1440, -r * 8.3167, 0.05);
            double bjd = TimeCorrections.BjdTdb(jd, 250, 36), hjd = TimeCorrections.HjdUtc(jd, 250, 36);
            Check("BJD_TDB - HJD_UTC ≈ TT - UTC (s)", (bjd - hjd) * 86400, 69.18, 5);

            var t = Enumerable.Range(0, 200).Select(i => i * 0.05).ToList();
            var y = t.Select(v => 1.0 + 0.01 * v).ToList();
            var flat = LightCurveTools.Detrend(t, y, 1);
            CheckTrue("Detrend removes a linear drift", flat.All(v => Math.Abs(v - 1) < 1e-9));
            List<double> bt, by, be;
            LightCurveTools.Bin(t, t.Select(v => v < 5 ? 1.0 : 3.0).ToList(), 1.0, out bt, out by, out be);
            CheckTrue("Binning", bt.Count == 10 && Math.Abs(by[0] - 1) < 1e-12 && Math.Abs(by[9] - 3) < 1e-12 && Math.Abs(bt[0] - 0.475) < 1e-9);
            var noisy = Enumerable.Range(0, 100).Select(i => 1 + 0.001 * Math.Sin(i * 1.7)).ToList();
            noisy[37] = 1.2;
            var clipped = LightCurveTools.SigmaClip(noisy);
            CheckTrue("Sigma clip flags the outlier only", clipped[37] && clipped.Count(c => c) == 1);
            var oc = LightCurveTools.OMinusC(new[] { 100.001, 101.5 - 0.002, 110.0 }, 100, 0.5);
            CheckTrue("O-C epochs and residuals", oc[0][0] == 0 && Math.Abs(oc[0][1] - 0.001) < 1e-9 && oc[1][0] == 3 && Math.Abs(oc[1][1] + 0.002) < 1e-9 && oc[2][0] == 20);
            Check("Scatter in ppt", LightCurveTools.ScatterPpt(Enumerable.Range(0, 2000).Select(i => 1 + 0.002 * Stats.NormalQuantile((i + 0.5) / 2000)).ToList()), 2, 0.05);
            var ph = PeriodSearch.Phase(new[] { 10.0, 10.25, 11.75 }, 0.5, 10);
            CheckTrue("Phase folding", Math.Abs(ph[0]) < 1e-12 && Math.Abs(ph[1] - 0.5) < 1e-12 && Math.Abs(ph[2] - 0.5) < 1e-12);
            var tt = Enumerable.Range(0, 400).Select(i => i * 0.0731).ToList();
            var yy = tt.Select(v => Math.Sin(2 * Math.PI * v / 1.37)).ToList();
            Check("Phase dispersion minimisation period", PeriodSearch.Pdm(tt, yy, 0.5, 5).BestPeriod, 1.37, 0.01);
            var grid = PeriodSearch.FrequencyGrid(tt, 0.5, 5);
            CheckTrue("Frequency grid spans the periods", Math.Abs(grid.Min() - 0.2) < 0.01 && Math.Abs(grid.Max() - 2) < 0.01 && grid.Length > 50);

            var tm = new TransitModel { T0 = 0, Period = 3, Rp = 0.1, A = 10, Inclination = 90, U1 = 0, U2 = 0 };
            Check("Transit depth without limb darkening = Rp²", tm.Depth, 0.01, 2e-4);
            Check("Central transit duration P/π · asin((1+p)/a)", tm.Duration, 3 / Math.PI * Math.Asin(1.1 / 10), 0.003);
            CheckTrue("Out of transit flux is the baseline", Math.Abs(tm.Flux(0.5) - 1) < 1e-12);
            double rpJup, aAu;
            tm.Physical(1.0, out rpJup, out aAu);
            Check("Planet radius for a solar-size star (R_Jup)", rpJup, 0.1 * 6.957e5 / 71492, 0.01);
        }

        [TestMethod]
        public void TestRadioTools()
        {
            Check("Wavelength of 1420 MHz (cm)", RadioMath.WavelengthM(1420.405751768e6) * 100, 21.106, 0.001);
            Check("dB round trip", RadioMath.RatioToDb(RadioMath.DbToRatio(3.7)), 3.7, 1e-12);
            Check("Y-factor Tsys", RadioMath.TsysFromYFactor(2.0, 290, 10), 270, 1e-9);
            double ae = RadioMath.EffectiveArea(3);
            Check("SEFD and antenna temperature agree", RadioMath.AntennaTemperature(RadioMath.Sefd(100, ae), ae), 100, 1e-6);
            Check("Radiometer equation", RadioMath.RadiometerSensitivity(100, 1e6, 100), 0.01, 1e-12);
            CheckTrue("Dish gain increases with frequency", RadioMath.DishGainDbi(2e9, 3) > RadioMath.DishGainDbi(1e9, 3));
            var casA = RadioSources.Bright.FirstOrDefault(s => s.Name.Contains("Cas"));
            CheckTrue("Cas A flux falls with frequency", casA != null && casA.FluxAt(1.4e9) == casA.Flux1400Jy && casA.FluxAt(5e9) < casA.Flux1400Jy);
            double lsr = VelocityFrames.LsrCorrection(new Observer(), AstroTime.FromCalendar(2025, 3, 1), 266.4, -28.9);
            CheckTrue("LSR correction within ±50 km/s", Math.Abs(lsr) < 50 && Math.Abs(lsr) > 0.1, lsr.ToString("0.00"));
            double rad, rot;
            VelocityFrames.TangentPoint(30, 100, out rad, out rot);
            Check("Tangent point radius R0 sin l", rad, VelocityFrames.R0Kpc * 0.5, 1e-9);

            // IQ recordings in every format: a tone 100 kHz above centre shows up there
            string dir = Folder("iq");
            int n = 65536; double fs = 1e6, tone = 1e5;
            foreach (IqFormat fmt in Enum.GetValues(typeof(IqFormat)))
            {
                string path = Path.Combine(dir, fmt + ".iq");
                using (var bw = new BinaryWriter(File.Create(path)))
                {
                    if (fmt == IqFormat.WavStereo16) bw.Write(new byte[44]);
                    for (int i = 0; i < n; i++)
                    {
                        double I = 0.5 * Math.Cos(2 * Math.PI * tone * i / fs), Q = 0.5 * Math.Sin(2 * Math.PI * tone * i / fs);
                        switch (fmt)
                        {
                            case IqFormat.RtlSdrUint8: bw.Write((byte)Math.Round(I * 127.5 + 127.5)); bw.Write((byte)Math.Round(Q * 127.5 + 127.5)); break;
                            case IqFormat.HackRfInt8: bw.Write((sbyte)Math.Round(I * 127)); bw.Write((sbyte)Math.Round(Q * 127)); break;
                            case IqFormat.Float32: bw.Write((float)I); bw.Write((float)Q); break;
                            default: bw.Write((short)Math.Round(I * 32767)); bw.Write((short)Math.Round(Q * 32767)); break;
                        }
                    }
                }
                var spec = IqProcessing.Spectrum(IqProcessing.ReadIq(path, fmt, 8192), 1024, 1420e6, fs);
                int peak = Array.IndexOf(spec.Power, spec.Power.Max());
                CheckTrue("IQ " + fmt + ": tone at +100 kHz", Math.Abs(spec.FrequencyHz[peak] - (1420e6 + tone)) <= fs / 1024 && spec.Averages == n / 1024, (spec.FrequencyHz[peak] - 1420e6).ToString("0"));
            }
            var on = new RadioSpectrum { Power = new[] { 2.0, 3.0 } }; var off = new RadioSpectrum { Power = new[] { 2.0, 2.0 } };
            var cal = IqProcessing.OnOff(on, off, 100);
            CheckTrue("On/off calibration", cal[0] == 0 && cal[1] == 50);
            var x = Enumerable.Range(0, 100).Select(i => (double)i).ToArray();
            var withLine = x.Select(v => 0.5 + 0.01 * v + (Math.Abs(v - 50) < 5 ? 1 : 0)).ToArray();
            var flat = IqProcessing.Baseline(x, withLine, 1, 40, 60);
            CheckTrue("Baseline removed, line kept", Math.Abs(flat[10]) < 1e-9 && Math.Abs(flat[50] - 1) < 1e-9);

            // drift scan, folding, dedispersion and SID detection
            var ts = Enumerable.Range(0, 300).Select(i => i * 2.0).ToList();
            var ps = ts.Select(v => 5 + 3 * Math.Exp(-0.5 * Math.Pow((v - 310) / 40, 2))).ToList();
            var drift = TimeDomainRadio.FitDriftScan(ts, ps);
            CheckTrue("Drift scan fit", Math.Abs(drift[0] - 310) < 0.5 && Math.Abs(drift[1] - 40 * 2.3548) < 1 && Math.Abs(drift[2] - 3) < 0.05, string.Join(", ", drift.Select(v => v.ToString("0.00"))));
            Check("Beam from drift at the equator (deg)", TimeDomainRadio.BeamFromDrift(240, 0), 1.0027, 0.001);
            var pulses = Enumerable.Range(0, 1000).Select(i => i * 0.01).ToList();
            var folded = TimeDomainRadio.Fold(pulses, pulses.Select(v => (v % 0.5) < 0.05 ? 1.0 : 0.0).ToList(), 0.5, 10);
            CheckTrue("Folding puts the pulse in the first bin", folded[0] > 0.9 && folded.Skip(1).All(v => v < 0.1));
            var chans = new[] { 400.0, 500.0, 600.0, 800.0 };
            double dt = 0.001, dm = 30;
            var fb = chans.Select(f => { var row = new double[2000]; int at = 200 + (int)Math.Round(TimeDomainRadio.DispersionDelay(dm, f, 800) / dt); if (at < 2000) row[at] = 1; return row; }).ToArray();
            var ded = TimeDomainRadio.Dedisperse(fb, chans, dm, dt);
            Check("Dedispersion lines the pulse up", ded[200], 4, 0);
            var amp = Enumerable.Range(0, 400).Select(i => 1 + 0.01 * Math.Sin(i * 0.9) + (i >= 250 && i < 260 ? 1.0 : 0)).ToList();
            var sid = TimeDomainRadio.DetectSid(amp);
            CheckTrue("SID detected at the rise", sid.Count == 1 && sid[0] == 250, string.Join(",", sid));

            CheckTrue("Jupiter Io-B region", JupiterRadio.ActiveSources(150, 90).Contains("Io-B") && JupiterRadio.ActiveSources(250, 230).Contains("Io-A") && JupiterRadio.ActiveSources(40, 30) == "none");
            double io, cml;
            JupiterRadio.IoPhaseAndCml(AstroTime.FromCalendar(2026, 1, 10).JdTT, out io, out cml);
            CheckTrue("Io phase and CML in range", io >= 0 && io < 360 && cml >= 0 && cml < 360);
            double io2, cml2;
            JupiterRadio.IoPhaseAndCml(AstroTime.FromCalendar(2026, 1, 10, 1).JdTT, out io2, out cml2);
            Check("System III CML advances ~36.6° per hour", AstroMath.NormalizeDeg(cml2 - cml), 36.6, 0.5);
            Check("Io phase advances ~8.5° per hour", AstroMath.NormalizeDeg(io2 - io), 8.47, 0.5);
        }

        [TestMethod]
        public void TestRtlTcpClient()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var received = new List<byte>();
            var server = new Thread(() =>
            {
                using (var c = listener.AcceptTcpClient())
                using (var ns = c.GetStream())
                {
                    ns.Write(new byte[] { (byte)'R', (byte)'T', (byte)'L', (byte)'0', 0, 0, 0, 5, 0, 0, 0, 29 }, 0, 12);
                    var cmd = new byte[15];
                    int got = 0;
                    while (got < 15) { int k = ns.Read(cmd, got, 15 - got); if (k <= 0) break; got += k; }
                    lock (received) received.AddRange(cmd.Take(got));
                    var samples = new byte[] { 255, 0, 128, 127 };
                    ns.Write(samples, 0, samples.Length);
                    Thread.Sleep(200);
                }
            }) { IsBackground = true };
            server.Start();
            using (var client = new RtlTcpClient())
            {
                client.Connect("127.0.0.1", port);
                CheckTrue("rtl_tcp header: tuner and gain count", client.TunerType == "R820T" && client.GainCount == 29);
                client.SetFrequency(1420405752);
                client.SetGain(496);
                client.SetPpm(-3);
                var iq = client.Read(2);
                CheckTrue("rtl_tcp samples scaled to ±1", Math.Abs(iq[0] - 1) < 1e-12 && Math.Abs(iq[1] + 1) < 1e-12 && Math.Abs(iq[2]) < 0.01);
            }
            server.Join(2000);
            listener.Stop();
            byte[] cmds; lock (received) cmds = received.ToArray();
            CheckTrue("rtl_tcp commands are 5 bytes, big-endian",
                cmds.Length == 15 && cmds[0] == 0x01 && BitConverter.ToUInt32(cmds.Skip(1).Take(4).Reverse().ToArray(), 0) == 1420405752
                && cmds[5] == 0x04 && cmds[9] == 0xF0 && cmds[10] == 0x05 && cmds[11] == 0xFF && cmds[14] == 0xFD, BitConverter.ToString(cmds));
        }

        [TestMethod]
        public void TestMeteorsAndOccultations()
        {
            var per = MeteorShowers.Major.Single(s => s.Code == "PER").PeakIn(2025).ToDateTimeUtc();
            CheckTrue("Perseids 2025 peak 12-13 August", per.Month == 8 && (per.Day == 12 || per.Day == 13), per.ToString("yyyy-MM-dd HH:mm"));
            var gem = MeteorShowers.Major.Single(s => s.Code == "GEM").PeakIn(2025).ToDateTimeUtc();
            CheckTrue("Geminids 2025 peak 14 December", gem.Month == 12 && gem.Day == 14, gem.ToString("yyyy-MM-dd HH:mm"));
            var qua = MeteorShowers.Major.Single(s => s.Code == "QUA").PeakIn(2026).ToDateTimeUtc();
            CheckTrue("Quadrantids 2026 peak in early January 2026", qua.Year == 2026 && qua.Month == 1 && qua.Day >= 2 && qua.Day <= 4, qua.ToString("yyyy-MM-dd HH:mm"));
            var urs = MeteorShowers.Major.Single(s => s.Code == "URS").PeakIn(2025).ToDateTimeUtc();
            CheckTrue("Ursids 2025 peak 22 December 2025", urs.Year == 2025 && urs.Month == 12 && urs.Day >= 21 && urs.Day <= 23, urs.ToString("yyyy-MM-dd"));
            var shower = new MeteorShower { Zhr = 100, PopulationIndex = 2.0 };
            Check("Hourly rate at the zenith, LM 6.5", shower.HourlyRate(90, 6.5), 100, 1e-9);
            Check("Hourly rate at 30°, LM 5.5", shower.HourlyRate(30, 5.5), 25, 1e-9);
            CheckTrue("No meteors with the radiant below the horizon", shower.HourlyRate(-5, 6.5) == 0);
            CheckTrue("Shower list complete", MeteorShowers.Major.Count >= 12 && MeteorShowers.Major.All(s => s.Zhr > 0 && s.VelocityKms > 10));

            // a lunar occultation of a star placed on the Moon's path
            var obs = new Observer { LatitudeDeg = 52, LongitudeDeg = 5 };
            var t0 = AstroTime.FromCalendar(2025, 10, 30, 18);
            var moonAt = new SkyContext(obs, t0).BodyPlace(Body.Moon);
            var cat = new ListCatalog(new RefStar { Ra = moonAt.Astrometric.RaHours * 15, Dec = moonAt.Astrometric.DecDeg, Mag = 5.0, Id = "Test star" });
            var occ = LunarOccultations.Predict(obs, t0.AddHours(-3), 6, cat);
            CheckTrue("Lunar occultation found", occ.Count == 1, occ.Count + " found");
            if (occ.Count == 1)
            {
                var o = occ[0];
                CheckTrue("Disappearance before and reappearance after the central time", o.Disappearance < t0 && o.Reappearance > t0, o.ToString());
                double minutes = (o.Reappearance - o.Disappearance) * 1440;
                CheckTrue("Central occultation lasts about an hour", minutes > 45 && minutes < 80, minutes.ToString("0.0") + " min");
                CheckTrue("Reappearance on the opposite limb", Math.Abs(AstroMath.NormalizeDeg180(o.ReappearancePA - o.DisappearancePA)) > 150, o.DisappearancePA.ToString("0") + "° / " + o.ReappearancePA.ToString("0") + "°");
            }

            // an asteroid occultation of a star placed on Vesta's geocentric path
            var vesta = MinorBodyStore.BuiltInAsteroids()[3];
            double tmid = AstroTime.CalendarToJd(2026, 10, 18);
            var earth = EphemerisManager.Position(Body.Earth, tmid);
            Vec3 d = Vec3.Zero; double tau = 0;
            for (int i = 0; i < 3; i++) { d = vesta.PositionForEphemeris(tmid - tau) - earth; tau = d.Length * AstroMath.LightTimeAU_days; }
            double sra, sdec;
            d.ToSpherical(out sra, out sdec);
            var star = new RefStar { Ra = AstroMath.NormalizeRad(sra) * AstroMath.Rad2Deg, Dec = sdec * AstroMath.Rad2Deg, Mag = 9.0, Id = "Occulted" };
            var events = OccultationPredictor.Search(vesta, AstroTime.FromTT(tmid - 0.5), AstroTime.FromTT(tmid + 0.5), new ListCatalog(star));
            CheckTrue("Asteroid occultation found", events.Count == 1, events.Count + " events");
            if (events.Count == 1)
            {
                var ev = events[0];
                Check("Occultation time (min)", (ev.Time.JdUtc - AstroTime.FromTT(tmid).JdUtc) * 1440, 0, 2);
                CheckTrue("Central: separation below 0.01″", ev.MinSeparationArcsec < 0.01, ev.MinSeparationArcsec.ToString("0.0000"));
                Check("Shadow as wide as Vesta (km)", ev.ShadowWidthKm, MpcReporting.DiameterKm(vesta.H, 0.15), 1e-9);
                CheckTrue("Duration and magnitude drop sensible", ev.DurationSec > 1 && ev.DurationSec < 600 && ev.MagDrop > 0 && ev.MagDrop < 5, ev.ToString());
                CheckTrue("Ground track computed", ev.Path.Count > 2 && ev.Path.All(p => Math.Abs(p[0]) <= 90 && Math.Abs(p[1]) <= 360));
            }

            var phen = GalileanMoons.Phenomena(AstroTime.CalendarToJd(2026, 1, 10), AstroTime.CalendarToJd(2026, 1, 12));
            CheckTrue("Galilean moon phenomena", phen.Count >= 4 && phen.Any(e => e.Description.Contains("transit begins")) && phen.Any(e => e.Description.Contains("occultation ends")), phen.Count + " events");
            foreach (var name in GalileanMoons.Names)
            {
                var mine = phen.Where(e => e.Description.StartsWith(name + " transit")).ToList();
                CheckTrue(name + " transits alternate begin/end", Enumerable.Range(1, Math.Max(0, mine.Count - 1)).All(i => mine[i].Description.EndsWith("begins") != mine[i - 1].Description.EndsWith("begins")));
            }

            var wcs = Wcs.Create(123.4, -12.3, 1.5, 10, 1000, 800);
            var ob = MpcReporting.Measure(wcs, 500, 400, new DateTime(2026, 10, 24, 22, 30, 0, DateTimeKind.Utc), "Z80");
            CheckTrue("Astrometric measurement at the image centre", Math.Abs(ob.RaDeg - 123.4) < 1e-3 && Math.Abs(ob.DecDeg + 12.3) < 1e-3 && ob.Station == "Z80");
            ob.PackedNumber = "00004"; ob.Mag = 6.5; ob.Band = "V";
            string ades = MpcReporting.AdesPsv(new[] { ob }, "Z80", "J. Observer", "J. Observer", "0.20-m f/5 reflector", 0.2, "Reflector", "CMO");
            CheckTrue("ADES PSV report", ades.Contains("# version=2017") && ades.Contains("Z80") && ades.Contains("|") && ades.Split('\n').Count(l => l.StartsWith("4      |")) == 1, ades.Split('\n').FirstOrDefault());
        }

        private class ListCatalog : IStarCatalogProvider
        {
            private readonly List<RefStar> _stars;
            public ListCatalog(params RefStar[] stars) { _stars = stars.ToList(); }
            public string Name => "list";
            public bool IsOnline => false;
            public List<RefStar> Cone(double ra, double dec, double radius, double mag, int max) =>
                _stars.Where(s => s.Mag <= mag && AstroMath.Separation(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad, s.Ra * AstroMath.Deg2Rad, s.Dec * AstroMath.Deg2Rad) * AstroMath.Rad2Deg <= radius).Take(max).ToList();
        }

        [TestMethod]
        public void TestSpectra()
        {
            string dir = Folder("spectra");
            var s = new Spectrum(500);
            for (int i = 0; i < 500; i++) { s.Wavelength[i] = 4000 + 2 * i; s.Flux[i] = 1 + 0.001 * i; }
            s.Error = s.Flux.Select(f => 0.01 * f).ToArray();
            string txt = Path.Combine(dir, "s.txt");
            s.SaveText(txt);
            var back = Spectrum.Load(txt);
            CheckTrue("Text spectrum round trip", back.Length == 500 && Math.Abs(back.Wavelength[123] - s.Wavelength[123]) < 1e-4 && Math.Abs(back.Flux[499] - s.Flux[499]) < 1e-6 && back.Error != null);
            File.WriteAllText(Path.Combine(dir, "nm.csv"), "# nm,flux\n400.0,1\n500.0,2\n600.0,3\n");
            var nm = Spectrum.Load(Path.Combine(dir, "nm.csv"));
            CheckTrue("Wavelengths in nanometres converted to Å", nm.Wavelength[0] == 4000 && nm.Wavelength[2] == 6000);
            File.WriteAllText(Path.Combine(dir, "um.dat"), "1.0 5\n1.5 6\n");
            CheckTrue("Wavelengths in microns converted to Å", Spectrum.Load(Path.Combine(dir, "um.dat")).Wavelength[1] == 15000);
            string fits = Path.Combine(dir, "bess.fits");
            s.SaveBessFits(fits, "Vega", "Me", "LISA", "Home", 12.5);
            var bf = Spectrum.Load(fits);
            CheckTrue("BeSS FITS round trip", Math.Abs(bf.Wavelength[0] - 4000) < 1e-3 && Math.Abs(bf.Dispersion - 2) < 1e-6 && Math.Abs(bf.At(4500) - s.At(4500)) < 1e-5 && bf.Header.GetString("OBJNAME") == "Vega");
            Check("Interpolation", s.At(4001), 1.0005, 1e-12);
            var rs = s.Resample(4100, 4200, 5);
            CheckTrue("Resample", rs.Length == 21 && Math.Abs(rs.Flux[0] - s.At(4100)) < 1e-12);

            // a tilted trace in a 2D frame: found, then extracted with the sky removed
            int w = 300, h = 60;
            var img = new AstroImage(w, h, 1);
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    double yc = 25 + 0.03 * x;
                    img.Data[0][y * w + x] = (float)(0.05 + 0.5 * (1 + 0.5 * Math.Sin(x / 30.0)) * Math.Exp(-0.5 * Math.Pow((y - yc) / 1.5, 2)));
                }
            var trace = SpectroTools.FindTrace(img, 1);
            Check("Trace position at x = 150", LinAlg.PolyEval(trace, 150), 25 + 4.5, 0.1);
            var ext = SpectroTools.Extract(img, trace, 6, 3, 6, false);
            Check("Extracted flux (box) at x = 150", ext.Flux[150], 0.5 * (1 + 0.5 * Math.Sin(5)) * 1.5 * Math.Sqrt(2 * Math.PI), 0.02);
            var opt = SpectroTools.Extract(img, trace, 6, 3, 6, true);
            Check("Optimal extraction agrees with the box", opt.Flux[150], ext.Flux[150], 0.03);

            var peaks = new double[400];
            foreach (var p in new[] { 50.3, 180.0, 320.7 }) for (int i = 0; i < 400; i++) peaks[i] += 100 * Math.Exp(-0.5 * Math.Pow((i - p) / 2, 2));
            for (int i = 0; i < 400; i++) peaks[i] += 5 + 0.5 * Math.Sin(i * 0.37);
            var found = SpectroTools.FindPeaks(peaks);
            CheckTrue("Peaks found with sub-pixel positions", found.Count == 3 && Math.Abs(found[0] - 50.3) < 0.2 && Math.Abs(found[2] - 320.7) < 0.2, string.Join(", ", found.Select(v => v.ToString("0.00"))));

            var abs = new Spectrum(2000);
            for (int i = 0; i < 2000; i++)
            {
                abs.Wavelength[i] = 4000 + i;
                double cont = 2 + 0.0005 * i;
                abs.Flux[i] = cont * (1 - 0.5 * Math.Exp(-0.5 * Math.Pow((abs.Wavelength[i] - 4861.3) / 4, 2)) - 0.5 * Math.Exp(-0.5 * Math.Pow((abs.Wavelength[i] - 4340.5) / 4, 2)));
            }
            var norm = SpectroTools.Normalize(abs, 2);
            Check("Continuum normalised to 1", norm.At(5500), 1, 0.01);
            Check("Line depth kept after normalisation", norm.At(4861.3), 0.5, 0.02);
            var shifted = new Spectrum(2000);
            for (int i = 0; i < 2000; i++) { shifted.Wavelength[i] = abs.Wavelength[i]; shifted.Flux[i] = norm.At(abs.Wavelength[i] / (1 + 45 / SpectroTools.C)); }
            Check("Cross-correlation radial velocity (km/s)", SpectroTools.CrossCorrelationRv(shifted, norm, 300, 0.5), 45, 2);
            var resp = SpectroTools.InstrumentResponse(abs, norm, 30);
            Check("Instrument response is the continuum", resp.At(5000), 2 + 0.0005 * 1000, 0.05);
            var div = SpectroTools.Divide(abs, resp);
            Check("Dividing by the response flattens the continuum", div.At(5900), 1, 0.03);
            Check("Wien temperature of a 5000 Å peak", SpectroTools.WienTemperature(5000), 5795.5, 0.5);
            Check("Redshift", SpectroTools.Redshift(6600, 6000), 0.1, 1e-12);
            Check("Relativistic velocity of z = 0.1 (km/s)", SpectroTools.RelativisticVelocity(0.1), 28487, 5);
            double bc = SpectroTools.BarycentricCorrection(2460600.5, 279.2, 38.8);
            CheckTrue("Barycentric correction within ±30 km/s", Math.Abs(bc) < 30.5 && Math.Abs(bc) > 0.1, bc.ToString("0.00"));
            CheckTrue("Spectral class estimate", !string.IsNullOrEmpty(SpectroTools.EstimateSpectralClass(norm)));
            var fromRow = Spectrum.FromImageRow(img, 30);
            CheckTrue("Spectrum from an image row", fromRow.Length == w && fromRow.Flux[0] == img.Data[0][30 * w]);
            CheckTrue("Line lists and lamps", LineLists.Stellar.Any(l => l.Name.Contains("α") || l.Name.Contains("H")) && LineLists.Lamps.ContainsKey("Neon") && LineLists.Lamps["Neon"].Length > 10);
        }

        [TestMethod]
        public void TestCatalogProviders()
        {
            CatalogManager.Instance.LoadBuiltIn();
            var local = new LocalCatalogProvider();
            var near = local.Cone(279.23, 38.78, 2, 6, 10);
            CheckTrue("Local cone around Vega", near.Count >= 1 && near[0].Mag < 0.5 && near.Zip(near.Skip(1), (a, b) => a.Mag <= b.Mag).All(x => x) && !local.IsOnline);

            string old = AppPaths.Root;
            try
            {
                AppPaths.Root = Folder("vizier-cache");
                var viz = new VizierProvider { Source = VizierProvider.Catalog.GaiaDR3 };
                string key = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}_{1:0.000}_{2:0.000}_{3:0.000}_{4:0.0}_{5}", viz.Source, 10.0, -5.0, 0.5, 15.0, 100);
                string cache = Path.Combine(AppPaths.Cache, "vizier_" + key.Replace('/', '_').Replace('-', 'm') + ".tsv");
                File.WriteAllText(cache, "#\n# VizieR Astronomical Server\n#\nRA_ICRS\tDE_ICRS\tGmag\tBPmag\tRPmag\tpmRA\tpmDE\tSource\ndeg\tdeg\tmag\tmag\tmag\tmas/yr\tmas/yr\t\n" +
                    "--------\t--------\t-----\t-----\t-----\t------\t------\t-------\n" +
                    "10.0123\t-4.9876\t12.345\t12.8\t11.7\t5.5\t-3.2\t123456789\n10.1\t-5.1\t14.0\t\t\t\t\t987\n\t-5\t13\t\t\t\t\t1\n");
                var stars = viz.Cone(10.0, -5.0, 0.5, 15.0, 100);
                CheckTrue("VizieR answer parsed from the cache (offline)", stars.Count == 2 && Math.Abs(stars[0].Ra - 10.0123) < 1e-9 && stars[0].Mag == 12.345, stars.Count + " stars");
                if (stars.Count == 2)
                {
                    CheckTrue("Gaia extras: proper motion, id, epoch", stars[0].PmRa == 5.5 && stars[0].PmDec == -3.2 && stars[0].Id == "Gaia DR3 123456789" && stars[0].Epoch == 2016.0);
                    CheckTrue("B-V from BP-RP", stars[0].BV > 0.6 && stars[0].BV < 1.0 && double.IsNaN(stars[1].BV), stars[0].BV.ToString("0.000"));
                }
            }
            finally { AppPaths.Root = old; }

            var table = DataTable.FromCsv("main_id,ra,dec\n\"M 31\",10.68,41.27\nVega,279.23,38.78\n\n");
            CheckTrue("Data table from CSV", table.Columns.Count == 3 && table.Rows.Count == 2 && table.Get(0, "MAIN_ID") == "M 31" && Math.Abs(table.GetDouble(1, "ra") - 279.23) < 1e-9 && table.Get(0, "nope") == null);
        }
    }
}
