using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;
using Sidralune.Core.Util;
using Sidralune.Imaging;
using Sidralune.Imaging.Analysis;
using Sidralune.Imaging.IO;
using Sidralune.Science.Astrometry;

namespace Sidralune.Devices.Automation
{
    public class ExposureRequest
    {
        public double Seconds = 1;
        public bool Light = true;
        public int Bin = 1;
        public int Gain = -1, Offset = -1;
        public string Filter;
        public string FrameType = "LIGHT";
        public string Target = "";
        public bool Save = true;
        public int[] SubFrame; // x, y, w, h

        /// <summary>Frame types of saved frames, in the Imaging tab and the sequencer (Imaging also offers SNAPSHOT).</summary>
        public static readonly string[] FrameTypes = { "LIGHT", "DARK", "FLAT", "BIAS" };
    }

    /// <summary>Planetary video recording: short exposures back to back, written straight to a SER file.</summary>
    public class VideoRequest
    {
        public double ExposureMs = 10;
        public int Bin = 1;
        public int Gain = -1, Offset = -1;
        /// <summary>Region of interest in binned pixels, centred on the sensor; 0 means the full sensor.</summary>
        public int RoiWidth, RoiHeight;
        public int MaxFrames = 1000;
        public double MaxSeconds = 60;
        public string Filter;
        public string Target = "";

        /// <summary>The regions of interest offered for video, in the Imaging tab and the sequencer.</summary>
        public static readonly string[] Regions = { "Full sensor", "1920 × 1080", "1280 × 960", "800 × 600", "640 × 480", "320 × 240" };

        /// <summary>Width and height of a region such as "640 × 480"; 0 × 0 for "Full sensor" or anything unrecognised.</summary>
        public static void ParseRegion(string region, out int width, out int height)
        {
            width = height = 0;
            var m = System.Text.RegularExpressions.Regex.Match(region ?? "", @"(\d+)\s*[×x]\s*(\d+)");
            if (m.Success) { width = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); height = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture); }
        }
    }

    public class VideoResult
    {
        public string Path;
        public int Frames, Width, Height;
        public TimeSpan Elapsed;
        public bool Stopped;
        public double Fps => Elapsed.TotalSeconds > 0 ? Frames / Elapsed.TotalSeconds : 0;
    }

    public class CaptureResult
    {
        public AstroImage Image;
        public string Path;
        public double Hfr = double.NaN, Fwhm = double.NaN;
        public int Stars;
        public double Median;
    }

    /// <summary>Camera exposure workflow: configure, expose, download, annotate FITS header, analyse, save.</summary>
    public class CaptureService
    {
        public DeviceManager Devices = DeviceManager.Instance;
        public string ImageFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Sidralune");
        /// <summary>File pattern tokens: $TARGET $FILTER $TYPE $EXPOSURE $BIN $GAIN $TEMP $DATE $TIME $DATETIME $FRAMENR $NIGHT $HFR</summary>
        public string FilePattern = @"$NIGHT\$TARGET\$TYPE\$TARGET_$FILTER_$EXPOSUREs_$DATETIME_$FRAMENR";
        public ImageSaveFormat SaveFormat = ImageSaveFormat.Fits16;
        public int FrameCounter;
        public event Action<CaptureResult> ImageCaptured;
        /// <summary>A frame of a video recording, at most twice a second, with the recording so far (manual or sequence).</summary>
        public event Action<AstroImage, VideoResult> VideoFrame;
        /// <summary>A video recording ended (Path is null when nothing was saved).</summary>
        public event Action<VideoResult> VideoRecorded;
        public event Action<string> Status;

        public async Task<CaptureResult> Expose(ExposureRequest r, CancellationToken ct)
        {
            var cam = Devices.Camera;
            if (cam == null) throw new InvalidOperationException("No camera connected");
            if (!string.IsNullOrEmpty(r.Filter) && Devices.FilterWheel != null) await SetFilter(r.Filter, ct);
            if (cam.MaxBin >= r.Bin) { cam.BinX = r.Bin; cam.BinY = r.Bin; }
            if (r.SubFrame != null) { cam.StartX = r.SubFrame[0] / r.Bin; cam.StartY = r.SubFrame[1] / r.Bin; cam.NumX = r.SubFrame[2] / r.Bin; cam.NumY = r.SubFrame[3] / r.Bin; }
            else { cam.StartX = 0; cam.StartY = 0; cam.NumX = cam.SensorWidth / r.Bin; cam.NumY = cam.SensorHeight / r.Bin; }
            if (r.Gain >= 0 && cam.HasGain) cam.Gain = r.Gain;
            if (r.Offset >= 0 && cam.HasOffset) cam.Offset = r.Offset;
            Status?.Invoke(string.Format("Exposing {0:0.###}s {1} {2}", r.Seconds, r.FrameType, r.Filter));
            var start = DateTime.UtcNow;
            cam.StartExposure(r.Seconds, r.Light);
            using (ct.Register(() => { try { cam.AbortExposure(); } catch { } }))
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, r.Seconds - 0.1)), ct);
                var until = DateTime.UtcNow.AddSeconds(120 + r.Seconds * 0.2);
                while (!cam.ImageReady)
                {
                    ct.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow > until) throw new TimeoutException("Camera did not deliver an image");
                    if (cam.State == CameraState.Error) throw new InvalidOperationException("Camera error");
                    await Task.Delay(100, ct);
                }
            }
            Status?.Invoke("Downloading");
            var img = await Task.Run(() => cam.GetImage(), ct);
            if (img == null) throw new InvalidOperationException("Camera returned no image");
            Annotate(img, r, start);
            var res = new CaptureResult { Image = img };
            await Task.Run(() =>
            {
                var stars = StarDetector.Detect(img.Luminance(), img.Width, img.Height, new StarDetectionOptions { MaxStars = 500 });
                double f, h, e;
                StarDetector.Summary(stars, out f, out h, out e);
                res.Hfr = h; res.Fwhm = f; res.Stars = stars.Count;
                res.Median = ImageStatistics.Compute(img.Data[0], img.Width, 0).Median;
                if (!double.IsNaN(h)) img.Header.Set("HFR", h, "Median half flux radius [px]");
            }, ct);
            if (r.Save)
            {
                FrameCounter++;
                res.Path = BuildPath(img, r, res.Hfr);
                Directory.CreateDirectory(Path.GetDirectoryName(res.Path));
                ImageFile.Save(res.Path, img, SaveFormat);
                img.SourcePath = res.Path;
            }
            ImageCaptured?.Invoke(res);
            return res;
        }

        public async Task SetFilter(string name, CancellationToken ct)
        {
            var fw = Devices.FilterWheel;
            if (fw == null) throw new InvalidOperationException("No filter wheel connected");
            int idx = Array.FindIndex(fw.Names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new ArgumentException("Unknown filter " + name);
            if (fw.Position == idx) return;
            int prevOffset = fw.Position >= 0 && fw.Position < fw.FocusOffsets.Length ? fw.FocusOffsets[fw.Position] : 0;
            Status?.Invoke("Changing filter to " + name);
            fw.Position = idx;
            var until = DateTime.UtcNow.AddSeconds(60);
            while (fw.Position != idx) { ct.ThrowIfCancellationRequested(); if (DateTime.UtcNow > until) throw new TimeoutException("Filter wheel timeout"); await Task.Delay(200, ct); }
            // apply relative focus offset
            if (Devices.Focuser != null && idx < fw.FocusOffsets.Length)
            {
                int delta = fw.FocusOffsets[idx] - prevOffset;
                if (delta != 0) { Devices.Focuser.Move(Devices.Focuser.Position + delta); while (Devices.Focuser.IsMoving) await Task.Delay(100, ct); }
            }
        }

        private void Annotate(AstroImage img, ExposureRequest r, DateTime start)
        {
            var h = img.Header;
            img.ExposureSeconds = r.Seconds;
            img.DateObs = start;
            h.Set("IMAGETYP", r.FrameType == "LIGHT" ? "Light Frame" : r.FrameType == "DARK" ? "Dark Frame" : r.FrameType == "FLAT" ? "Flat Frame" : r.FrameType == "BIAS" ? "Bias Frame" : r.FrameType);
            if (!string.IsNullOrEmpty(r.Target)) h.Set("OBJECT", r.Target);
            if (!string.IsNullOrEmpty(r.Filter)) h.Set("FILTER", r.Filter);
            var site = Devices.Site;
            h.Set("SITELAT", site.LatitudeDeg); h.Set("SITELONG", site.LongitudeDeg); h.Set("SITEELEV", site.ElevationM);
            h.Set("FOCALLEN", Devices.FocalLengthMm); h.Set("APTDIA", Devices.ApertureMm);
            var tel = Devices.Telescope;
            if (tel != null)
            {
                try
                {
                    double ra = tel.RightAscension, dec = tel.Declination;
                    var ctx = new SkyContext(site, AstroTime.FromDateTime(start));
                    var j2000 = ctx.ApparentToIcrs(ra * AstroMath.Hour2Rad, dec * AstroMath.Deg2Rad);
                    h.Set("RA", j2000.RaHours * 15, "J2000 RA of pointing [deg]");
                    h.Set("DEC", j2000.DecDeg, "J2000 Dec of pointing [deg]");
                    h.Set("OBJCTRA", AstroMath.FormatHms(j2000.RaHours, 1, true).Replace(':', ' '));
                    h.Set("OBJCTDEC", AstroMath.FormatDms(j2000.DecDeg, 0, true, true).Replace(':', ' '));
                    h.Set("CENTALT", tel.Altitude); h.Set("CENTAZ", tel.Azimuth);
                    h.Set("AIRMASS", Transforms.Airmass(tel.Altitude * AstroMath.Deg2Rad));
                    h.Set("PIERSIDE", tel.SideOfPier.ToString().ToUpperInvariant());
                }
                catch { }
            }
            if (Devices.Focuser != null) { h.Set("FOCPOS", Devices.Focuser.Position); if (!double.IsNaN(Devices.Focuser.Temperature)) h.Set("FOCTEMP", Devices.Focuser.Temperature); }
            if (Devices.Weather != null) { try { h.Set("AMBTEMP", Devices.Weather.Temperature); h.Set("HUMIDITY", Devices.Weather.Humidity); } catch { } }
            h.Set("SWCREATE", "Sidralune");
        }

        /// <summary>
        /// Record a SER video through the camera driver: short exposures back to back, no per-frame analysis or files,
        /// so the frame rate is what the driver can deliver (ASCOM transfers whole frames, so expect a few to a few tens of
        /// frames per second; a small region of interest helps most). Each frame is time-stamped at mid-exposure. Stopping
        /// (cancellation) ends the recording and still finalises the file. The file is named after the recording's
        /// mid-time in the WinJUPOS style, YYYY-MM-DD-HHMM_T-target.ser, under ImageFolder\Planetary\target.
        /// </summary>
        public async Task<VideoResult> RecordVideo(VideoRequest r, Action<AstroImage, VideoResult> preview, CancellationToken ct)
        {
            var cam = Devices.Camera;
            if (cam == null) throw new InvalidOperationException("No camera connected");
            if (!string.IsNullOrEmpty(r.Filter) && Devices.FilterWheel != null) await SetFilter(r.Filter, ct);
            int bin = cam.MaxBin >= r.Bin ? r.Bin : 1;
            cam.BinX = bin; cam.BinY = bin;
            int fullW = cam.SensorWidth / bin, fullH = cam.SensorHeight / bin;
            // even sizes and offsets keep the Bayer pattern of colour sensors in phase
            int w = r.RoiWidth > 0 ? Math.Min(r.RoiWidth, fullW) & ~1 : fullW, h = r.RoiHeight > 0 ? Math.Min(r.RoiHeight, fullH) & ~1 : fullH;
            cam.StartX = ((fullW - w) / 2) & ~1; cam.StartY = ((fullH - h) / 2) & ~1; cam.NumX = w; cam.NumY = h;
            if (r.Gain >= 0 && cam.HasGain) cam.Gain = r.Gain;
            if (r.Offset >= 0 && cam.HasOffset) cam.Offset = r.Offset;

            string target = string.IsNullOrWhiteSpace(r.Target) ? "Untitled" : r.Target.Trim();
            string safe = new string(target.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            string folder = Path.Combine(ImageFolder, "Planetary", safe);
            Directory.CreateDirectory(folder);
            string temp = Path.Combine(folder, "recording-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".ser.part");
            var res = new VideoResult { Width = w, Height = h };
            double seconds = Math.Max(0, r.ExposureMs / 1000.0);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            DateTime first = DateTime.MinValue, last = DateTime.MinValue;
            var lastPreview = DateTime.MinValue;
            SerWriter writer = null;
            Status?.Invoke(string.Format("Recording {0}×{1} at {2:0.###} ms", w, h, r.ExposureMs));
            try
            {
                while (res.Frames < r.MaxFrames && sw.Elapsed.TotalSeconds < r.MaxSeconds)
                {
                    ct.ThrowIfCancellationRequested();
                    var start = DateTime.UtcNow;
                    cam.StartExposure(seconds, true);
                    using (ct.Register(() => { try { cam.AbortExposure(); } catch { } }))
                    {
                        if (seconds > 0.03) await Task.Delay(TimeSpan.FromSeconds(seconds - 0.02), ct);
                        var until = DateTime.UtcNow.AddSeconds(30 + seconds * 2);
                        while (!cam.ImageReady)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (DateTime.UtcNow > until) throw new TimeoutException("Camera did not deliver a frame");
                            if (cam.State == CameraState.Error) throw new InvalidOperationException("Camera error");
                            Thread.Sleep(1);
                        }
                    }
                    var img = cam.GetImage();
                    if (img == null) throw new InvalidOperationException("Camera returned no image");
                    var mid = start.AddSeconds(seconds / 2);
                    if (writer == null)
                    {
                        res.Width = img.Width; res.Height = img.Height;
                        writer = new SerWriter(temp, img.Width, img.Height, cam.BitDepth > 8 ? 16 : 8, img.Channels, img.Bayer, "", cam.Info != null ? cam.Info.Name : "", "");
                        first = mid;
                    }
                    writer.Write(img, mid);
                    last = mid;
                    res.Frames++;
                    res.Elapsed = sw.Elapsed;
                    if ((DateTime.UtcNow - lastPreview).TotalSeconds >= 0.5)
                    {
                        lastPreview = DateTime.UtcNow;
                        preview?.Invoke(img, res);
                        VideoFrame?.Invoke(img, res);
                    }
                }
            }
            catch (OperationCanceledException) { res.Stopped = true; }
            finally { if (writer != null) writer.Dispose(); }
            res.Elapsed = sw.Elapsed;
            if (res.Frames == 0) { try { File.Delete(temp); } catch { } VideoRecorded?.Invoke(res); return res; }
            // name the file after the mid-time of the recording, the convention WinJUPOS and the Derotation tab read
            var midTime = first.AddTicks((last - first).Ticks / 2);
            string name = string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd-HHmm}_{1}-{2}", midTime, midTime.Second / 6, safe);
            string path = Path.Combine(folder, name + ".ser");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, name + " (" + i + ").ser");
            File.Move(temp, path);
            res.Path = path;
            VideoRecorded?.Invoke(res);
            return res;
        }

        public string BuildPath(AstroImage img, ExposureRequest r, double hfr)
        {
            var now = img.DateObs ?? DateTime.UtcNow;
            var local = Devices.Site.ToLocal(now);
            var night = (local.Hour < 12 ? local.AddDays(-1) : local).ToString("yyyy-MM-dd");
            var cam = Devices.Camera;
            string p = FilePattern
                .Replace("$TARGET", Safe(string.IsNullOrEmpty(r.Target) ? "Untitled" : r.Target))
                .Replace("$FILTER", Safe(r.Filter ?? "NoFilter"))
                .Replace("$TYPE", r.FrameType)
                .Replace("$EXPOSURE", r.Seconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Replace("$BIN", r.Bin + "x" + r.Bin)
                .Replace("$GAIN", cam != null && cam.HasGain ? cam.Gain.ToString() : "")
                .Replace("$TEMP", cam != null && !double.IsNaN(cam.CcdTemperature) ? cam.CcdTemperature.ToString("0") : "")
                .Replace("$DATETIME", now.ToString("yyyyMMdd-HHmmss"))
                .Replace("$DATE", now.ToString("yyyy-MM-dd"))
                .Replace("$TIME", now.ToString("HHmmss"))
                .Replace("$NIGHT", night)
                .Replace("$HFR", double.IsNaN(hfr) ? "" : hfr.ToString("0.00", CultureInfo.InvariantCulture))
                .Replace("$FRAMENR", FrameCounter.ToString("0000"));
            string ext = SaveFormat == ImageSaveFormat.Xisf16 || SaveFormat == ImageSaveFormat.Xisf32Float ? ".xisf" : SaveFormat == ImageSaveFormat.Tiff16 || SaveFormat == ImageSaveFormat.Tiff32Float ? ".tif" : ".fits";
            return Path.Combine(ImageFolder, p + ext);
        }

        private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Replace(' ', '_');
    }

    public class AutofocusPoint { public int Position; public double Hfr; public int Stars; }

    public class AutofocusResult
    {
        public List<AutofocusPoint> Points = new List<AutofocusPoint>();
        public int BestPosition;
        public double BestHfr;
        public double A, B, C; // hyperbola HFR = A·sqrt(1 + ((x - C)/B)²)
        public bool Success;
        public string Message;
        public double Temperature;
    }

    /// <summary>
    /// V-curve autofocus: steps through positions around the current focus, measures HFR, fits a hyperbola
    /// (the theoretical shape of defocused star size) by Levenberg-Marquardt and moves to the minimum.
    /// </summary>
    public class Autofocus
    {
        public CaptureService Capture;
        public int StepSize = 100;
        public int Steps = 9;
        public double Exposure = 3;
        public int Bin = 2;
        public string Filter;
        public int BacklashOvershoot = 200;
        public event Action<AutofocusPoint> PointMeasured;
        public event Action<string> Status;

        public async Task<AutofocusResult> Run(CancellationToken ct)
        {
            var foc = Capture.Devices.Focuser;
            if (foc == null) throw new InvalidOperationException("No focuser connected");
            var res = new AutofocusResult { Temperature = foc.Temperature };
            int centre = foc.Position, original = centre;
            var good = new List<AutofocusPoint>();
            for (int pass = 0; pass < 3; pass++)
            {
                good.Clear();
                int start = centre - StepSize * (Steps / 2);
                // approach from below to take up backlash consistently
                await MoveTo(foc, start - BacklashOvershoot, ct);
                for (int i = 0; i < Steps; i++)
                {
                    int pos = start + i * StepSize;
                    await MoveTo(foc, pos, ct);
                    var cap = await Capture.Expose(new ExposureRequest { Seconds = Exposure, Bin = Bin, Filter = Filter, Save = false, FrameType = "FOCUS" }, ct);
                    var p = new AutofocusPoint { Position = pos, Hfr = cap.Hfr, Stars = cap.Stars };
                    res.Points.Add(p);
                    if (!double.IsNaN(p.Hfr) && p.Stars >= 3) good.Add(p);
                    Status?.Invoke(string.Format("Focus {0}: HFR {1:0.00} ({2} stars)", pos, cap.Hfr, cap.Stars));
                    PointMeasured?.Invoke(p);
                }
                if (good.Count < 5) { res.Message = "Not enough stars for autofocus"; await MoveTo(foc, original, ct); return res; }
                var minP = good.OrderBy(p => p.Hfr).First();
                bool atEdge = minP.Position == good.Min(p => p.Position) || minP.Position == good.Max(p => p.Position);
                if (!atEdge) break;
                // the minimum is not bracketed: re-centre the whole sweep on the best sample and measure again
                centre = minP.Position + (minP.Position == good.Min(p => p.Position) ? -1 : 1) * StepSize * (Steps / 2 - 1);
                Status?.Invoke("Minimum not bracketed, moving the focus sweep to " + centre);
            }
            good = good.OrderBy(p => p.Position).ToList();
            double[] fit = FitHyperbola(good.Select(p => (double)p.Position).ToArray(), good.Select(p => p.Hfr).ToArray());
            res.A = fit[0]; res.B = fit[1]; res.C = fit[2];
            res.BestPosition = (int)Math.Round(fit[2]);
            res.BestHfr = fit[0];
            if (res.BestPosition < good.First().Position - StepSize || res.BestPosition > good.Last().Position + StepSize)
            {
                res.Message = "Fit minimum outside the measured range; using the best sample";
                res.BestPosition = good.OrderBy(p => p.Hfr).First().Position;
            }
            await MoveTo(foc, res.BestPosition - BacklashOvershoot, ct);
            await MoveTo(foc, res.BestPosition, ct);
            res.Success = true;
            res.Message = res.Message ?? string.Format("Best focus {0} (HFR {1:0.00})", res.BestPosition, res.BestHfr);
            Status?.Invoke(res.Message);
            return res;
        }

        private static async Task MoveTo(IFocuser f, int pos, CancellationToken ct)
        {
            f.Move(Math.Max(0, Math.Min(f.MaxStep, pos)));
            await Task.Delay(100, ct);
            var until = DateTime.UtcNow.AddMinutes(2);
            while (f.IsMoving) { ct.ThrowIfCancellationRequested(); if (DateTime.UtcNow > until) throw new TimeoutException("Focuser move timeout"); await Task.Delay(100, ct); }
        }

        /// <summary>Fit HFR(x) = a·sqrt(1 + ((x - c)/b)²); returns [a, b, c].</summary>
        public static double[] FitHyperbola(double[] x, double[] y)
        {
            int imin = Array.IndexOf(y, y.Min());
            double a0 = y[imin], c0 = x[imin];
            double b0 = Math.Max(1, (x.Max() - x.Min()) / 4);
            var r = LevenbergMarquardt.Fit((p, i) => p[0] * Math.Sqrt(1 + Math.Pow((x[i] - p[2]) / p[1], 2)), x.Length, y, null, new[] { a0, b0, c0 },
                new[] { 0.05, 1.0, x.Min() - (x.Max() - x.Min()) }, new[] { y.Max() * 2, (x.Max() - x.Min()) * 10, x.Max() + (x.Max() - x.Min()) });
            return r.Parameters;
        }
    }

    /// <summary>Plate-solve-and-centre loop (slew, expose, solve, sync, re-slew until within tolerance).</summary>
    public class Centering
    {
        public CaptureService Capture;
        public SolveOptions Solve = new SolveOptions();
        public double ToleranceArcmin = 1.0;
        public int MaxAttempts = 5;
        public double Exposure = 5;
        public int Bin = 2;
        public bool UseSync = true;
        public event Action<string> Status;

        public async Task<Wcs> Center(double raJ2000Deg, double decJ2000Deg, CancellationToken ct)
        {
            var tel = Capture.Devices.Telescope;
            if (tel == null) throw new InvalidOperationException("No telescope connected");
            var site = Capture.Devices.Site;
            Func<double, double, double[]> toJNow = (ra, dec) =>
            {
                var ctx = new SkyContext(site, AstroTime.Now);
                var app = ctx.IcrsToApparent(ra * AstroMath.Deg2Rad, dec * AstroMath.Deg2Rad);
                return new[] { app.RaHours, app.DecDeg };
            };
            var target = toJNow(raJ2000Deg, decJ2000Deg);
            await Slew(tel, target[0], target[1], ct);
            Wcs last = null;
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                Status?.Invoke("Centering attempt " + attempt);
                var cap = await Capture.Expose(new ExposureRequest { Seconds = Exposure, Bin = Bin, Save = false, FrameType = "SNAPSHOT" }, ct);
                var opt = Solve;
                opt.HintRa = raJ2000Deg; opt.HintDec = decJ2000Deg;
                var cam = Capture.Devices.Camera;
                opt.ScaleHint = 206.265 * cam.PixelSizeX * Bin / Capture.Devices.FocalLengthMm;
                var sr = await Task.Run(() => PlateSolver.Solve(cap.Image, opt, null, ct), ct);
                if (!sr.Success) { Status?.Invoke("Plate solve failed: " + sr.Message); continue; }
                last = sr.Wcs;
                double cra, cdec;
                sr.Wcs.Centre(out cra, out cdec);
                double err = AstroMath.Separation(cra * AstroMath.Deg2Rad, cdec * AstroMath.Deg2Rad, raJ2000Deg * AstroMath.Deg2Rad, decJ2000Deg * AstroMath.Deg2Rad) * AstroMath.Rad2Deg * 60;
                Status?.Invoke(string.Format("Pointing error {0:0.00}'", err));
                if (err <= ToleranceArcmin) return last;
                var solvedNow = toJNow(cra, cdec);
                if (UseSync && tel.CanSync) { tel.SyncToCoordinates(solvedNow[0], solvedNow[1]); await Slew(tel, target[0], target[1], ct); }
                else
                {
                    // offset the slew by the measured error
                    double dra = AstroMath.NormalizeDeg180((target[0] - solvedNow[0]) * 15) / 15, ddec = target[1] - solvedNow[1];
                    await Slew(tel, AstroMath.NormalizeHours(tel.RightAscension + dra), tel.Declination + ddec, ct);
                }
            }
            if (last == null) throw new InvalidOperationException("Centering failed: no plate solution");
            return last;
        }

        public static async Task Slew(ITelescope tel, double raH, double decD, CancellationToken ct)
        {
            if (tel.AtPark) tel.Unpark();
            tel.SlewToCoordinatesAsync(raH, decD);
            await Task.Delay(300, ct);
            var until = DateTime.UtcNow.AddMinutes(5);
            while (tel.Slewing) { ct.ThrowIfCancellationRequested(); if (DateTime.UtcNow > until) throw new TimeoutException("Slew timeout"); await Task.Delay(200, ct); }
            await Task.Delay(1000, ct); // settle
        }
    }

    public class PolarAlignmentResult
    {
        public double AzErrorArcmin, AltErrorArcmin, TotalErrorArcmin;
        public double AxisRa, AxisDec; // deg, rotation axis direction (JNow)
        public string Advice;
    }

    /// <summary>
    /// Three-point polar alignment (as in NINA TPPA / SharpCap-like): plate solve at three RA positions, find the
    /// mount's RA rotation axis as the pole of the circle through the three pointings, compare with the refracted pole.
    /// </summary>
    public class PolarAlignment
    {
        public CaptureService Capture;
        public SolveOptions Solve = new SolveOptions();
        public double StepDeg = 25;
        public double Exposure = 4;
        public int Bin = 2;
        public event Action<string> Status;

        public async Task<PolarAlignmentResult> Run(CancellationToken ct)
        {
            var tel = Capture.Devices.Telescope;
            if (tel == null) throw new InvalidOperationException("No telescope connected");
            var site = Capture.Devices.Site;
            var pts = new List<Vec3>();
            for (int k = 0; k < 3; k++)
            {
                if (k > 0)
                {
                    // rotate in RA only, keeping the declination axis fixed
                    double ra = AstroMath.NormalizeHours(tel.RightAscension + StepDeg / 15);
                    await Centering.Slew(tel, ra, tel.Declination, ct);
                }
                var cap = await Capture.Expose(new ExposureRequest { Seconds = Exposure, Bin = Bin, Save = false, FrameType = "SNAPSHOT" }, ct);
                var cam = Capture.Devices.Camera;
                var opt = Solve;
                var ctx = new SkyContext(site, AstroTime.Now);
                var guess = ctx.ApparentToIcrs(tel.RightAscension * AstroMath.Hour2Rad, tel.Declination * AstroMath.Deg2Rad);
                opt.HintRa = guess.RaHours * 15; opt.HintDec = guess.DecDeg; opt.SearchRadius = 10;
                opt.ScaleHint = 206.265 * cam.PixelSizeX * Bin / Capture.Devices.FocalLengthMm;
                var sr = await Task.Run(() => PlateSolver.Solve(cap.Image, opt, null, ct), ct);
                if (!sr.Success) throw new InvalidOperationException("Plate solve failed at point " + (k + 1));
                double cra, cdec;
                sr.Wcs.Centre(out cra, out cdec);
                // to apparent topocentric frame of date, but expressed in a frame fixed to the Earth (HA, Dec) so sky rotation between frames is removed
                var ctx2 = new SkyContext(site, AstroTime.FromDateTime(cap.Image.DateObs ?? DateTime.UtcNow));
                var app = ctx2.IcrsToApparent(cra * AstroMath.Deg2Rad, cdec * AstroMath.Deg2Rad);
                double ha = ctx2.Last - app.Ra;
                pts.Add(Vec3.FromSpherical(-ha, app.Dec)); // longitude = -HA (Earth-fixed)
                Status?.Invoke(string.Format("Point {0}: {1}", k + 1, sr.Wcs));
            }
            // normal of the plane through the three points = mount rotation axis
            var n = Vec3.Cross(pts[1] - pts[0], pts[2] - pts[0]).Normalized();
            if (n.Z * Math.Sign(site.LatitudeDeg == 0 ? 1 : site.LatitudeDeg) < 0) n = -n;
            double axLon, axLat;
            n.ToSpherical(out axLon, out axLat);
            double axHa = -axLon;
            var hz = Transforms.HaDecToAltAz(axHa, axLat, site.LatRad);
            // refracted celestial pole position
            double poleAlt = Math.Abs(site.LatRad);
            poleAlt += Transforms.RefractionFromTrue(poleAlt, site.PressureHpa, site.TemperatureC);
            double poleAz = site.LatitudeDeg >= 0 ? 0 : Math.PI;
            var res = new PolarAlignmentResult
            {
                AltErrorArcmin = (hz.Alt - poleAlt) * AstroMath.Rad2Deg * 60,
                AzErrorArcmin = AstroMath.NormalizeRadPi(hz.Az - poleAz) * AstroMath.Rad2Deg * 60 * Math.Cos(poleAlt),
                AxisRa = AstroMath.NormalizeDeg(-axHa * AstroMath.Rad2Deg), AxisDec = axLat * AstroMath.Rad2Deg
            };
            res.TotalErrorArcmin = Math.Sqrt(res.AltErrorArcmin * res.AltErrorArcmin + res.AzErrorArcmin * res.AzErrorArcmin);
            res.Advice = string.Format("Move the polar axis {0} by {1:0.0}' and {2} by {3:0.0}'",
                res.AltErrorArcmin > 0 ? "down" : "up", Math.Abs(res.AltErrorArcmin),
                res.AzErrorArcmin > 0 ? (site.LatitudeDeg >= 0 ? "west" : "east") : (site.LatitudeDeg >= 0 ? "east" : "west"), Math.Abs(res.AzErrorArcmin));
            return res;
        }
    }

    /// <summary>Meridian flip helper for German equatorial mounts.</summary>
    public static class MeridianFlip
    {
        /// <summary>Minutes past the meridian (positive = west of meridian) for a JNow RA.</summary>
        public static double MinutesPastMeridian(ITelescope tel, double raHours) => AstroMath.NormalizeDeg180((tel.SiderealTime - raHours) * 15) * 4;

        public static bool IsDue(ITelescope tel, double minutesAfter = 5) => tel.SideOfPier == PierSide.East && MinutesPastMeridian(tel, tel.RightAscension) >= minutesAfter;

        /// <summary>Flip by re-slewing to the current target; the mount chooses the other pier side when past the meridian.</summary>
        public static async Task Perform(ITelescope tel, double raHours, double decDeg, IGuider guider, CancellationToken ct)
        {
            bool wasGuiding = guider != null && guider.IsGuiding;
            if (wasGuiding) guider.StopGuiding();
            await Centering.Slew(tel, raHours, decDeg, ct);
            if (wasGuiding) await guider.StartGuiding(false, ct);
        }
    }
}
