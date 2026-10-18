using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core;
using Sidralune.Imaging.IO;

namespace Sidralune.Imaging.Planetary
{
    public enum PippObjectMode { Planetary, FullDisc, Surface, None }
    public enum PippOutputFormat { Ser, Avi, Fits, Tiff, Png, Bmp, Jpeg }
    public enum PippOutputOrder { Original, Quality }

    /// <summary>Options mirroring PIPP (Planetary Imaging PreProcessor) by Chris Garry.</summary>
    public class PippOptions
    {
        // source
        public int FirstFrame = 0, LastFrame = -1, FrameStep = 1;
        public bool Debayer = true;
        public DebayerMethod DebayerMethod = DebayerMethod.Bilinear;
        public BayerPattern? ForceBayer;
        public bool ConvertToMono = false;
        // object detection
        public PippObjectMode ObjectMode = PippObjectMode.Planetary;
        public double DetectionThreshold = -1; // -1 = automatic (Otsu)
        public bool CentreObject = true;
        public bool RejectEdgeFrames = true;
        public double MinObjectSizeFraction = 0.3;   // relative to median size
        public double MaxObjectSizeFraction = 1.6;
        public bool RejectBrightnessOutliers = true;
        public double BrightnessTolerance = 0.25;
        // geometry
        public int CropWidth = 0, CropHeight = 0;    // 0 = auto (object size + margin)
        public double AutoCropMargin = 1.3;
        public double RotateDeg = 0;
        public bool FlipHorizontal, FlipVertical;
        // field derotation for alt-az / non polar aligned mounts
        public bool FieldDerotation = false;
        public bool FieldDerotationMeasure = false;  // estimate rotation from the images themselves
        public Observer Site;
        public double TargetRaRad = double.NaN, TargetDecRad = double.NaN;
        public bool AltAzMount = true;
        // colour
        public bool AlignRgbChannels = false;        // atmospheric dispersion correction
        public double Gain = 1.0, Gamma = 1.0;
        // quality
        public bool EstimateQuality = true;
        public QualityMetric Metric = QualityMetric.Gradient;
        public double KeepBestPercent = 100;
        public int KeepBestCount = 0;
        public PippOutputOrder Order = PippOutputOrder.Original;
        // output
        public PippOutputFormat Format = PippOutputFormat.Ser;
        public int OutputBitDepth = 16;
        public string OutputFolder;
        public string OutputName;
        public int MaxFramesPerFile = 0;            // split output
        public bool WinJuposNaming = true;
        public string PlanetCode = "";
    }

    public class PippFrameInfo
    {
        public int Index;
        public DateTime? Time;
        public ObjectInfo Object;
        public double Quality;
        public bool Rejected;
        public string Reason;
        public double Rotation;
    }

    public class PippResult
    {
        public List<PippFrameInfo> Frames = new List<PippFrameInfo>();
        public List<string> OutputFiles = new List<string>();
        public int Written;
        public string Summary;
    }

    /// <summary>Frame pre-processing pipeline for planetary/lunar/solar videos.</summary>
    public static class Preprocessor
    {
        public static AstroImage PrepareFrame(IFrameSource src, int index, PippOptions o)
        {
            var f = src.ReadFrame(index);
            if (o.ForceBayer.HasValue && f.Channels == 1) f.Bayer = o.ForceBayer.Value;
            if (o.Debayer && f.Channels == 1 && f.Bayer != BayerPattern.None) f = Imaging.Debayer.Apply(f, o.DebayerMethod);
            if (o.ConvertToMono && f.Channels > 1) f = f.ToMono();
            if (o.Gain != 1.0 || o.Gamma != 1.0)
            {
                double g = o.Gain, ig = 1 / o.Gamma;
                f.Apply(v => (float)Math.Pow(Math.Max(0, v * g), ig));
            }
            return f;
        }

        public static PippResult Run(IFrameSource src, PippOptions o, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            var res = new PippResult();
            int last = o.LastFrame < 0 ? src.FrameCount - 1 : Math.Min(o.LastFrame, src.FrameCount - 1);
            var indices = new List<int>();
            for (int i = Math.Max(0, o.FirstFrame); i <= last; i += Math.Max(1, o.FrameStep)) indices.Add(i);
            // pass 1: analyse
            var infos = new PippFrameInfo[indices.Count];
            int done = 0;
            Parallel.For(0, indices.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, k =>
            {
                var f = PrepareFrame(src, indices[k], o);
                var lum = f.Luminance();
                var info = new PippFrameInfo { Index = indices[k], Time = src.Timestamp(indices[k]) ?? f.DateObs };
                if (o.ObjectMode != PippObjectMode.None && o.ObjectMode != PippObjectMode.Surface)
                {
                    info.Object = ObjectDetection.Detect(lum, f.Width, f.Height, o.DetectionThreshold);
                    if (!info.Object.Found) { info.Rejected = true; info.Reason = "No object"; }
                    else if (o.RejectEdgeFrames && info.Object.TouchesEdge) { info.Rejected = true; info.Reason = "Object touches edge"; }
                }
                if (o.EstimateQuality && !info.Rejected)
                {
                    if (info.Object != null && info.Object.Found)
                    {
                        var ob = info.Object;
                        info.Quality = FrameQuality.Measure(lum, f.Width, f.Height, o.Metric, ob.X0, ob.Y0, ob.Width, ob.Height);
                    }
                    else info.Quality = FrameQuality.Measure(lum, f.Width, f.Height, o.Metric, f.Width / 4, f.Height / 4, f.Width / 2, f.Height / 2);
                }
                infos[k] = info;
                int d = Interlocked.Increment(ref done);
                if (progress != null && (d & 15) == 0) progress.Report(0.5 * d / indices.Count);
            });
            res.Frames = infos.ToList();
            var good = res.Frames.Where(i => !i.Rejected).ToList();
            // size and brightness outliers relative to the median
            if (good.Count > 5 && good[0].Object != null)
            {
                double medR = Core.Mathematics.Stats.Median(good.Select(g => g.Object.Radius).ToList());
                double medB = Core.Mathematics.Stats.Median(good.Select(g => g.Object.Brightness).ToList());
                foreach (var g in good)
                {
                    if (g.Object.Radius < medR * o.MinObjectSizeFraction || g.Object.Radius > medR * o.MaxObjectSizeFraction) { g.Rejected = true; g.Reason = "Object size"; }
                    else if (o.RejectBrightnessOutliers && Math.Abs(g.Object.Brightness - medB) > o.BrightnessTolerance * medB) { g.Rejected = true; g.Reason = "Brightness"; }
                }
                good = good.Where(g => !g.Rejected).ToList();
            }
            // quality selection
            if (o.EstimateQuality)
            {
                var sorted = good.OrderByDescending(g => g.Quality).ToList();
                int keep = sorted.Count;
                if (o.KeepBestPercent < 100) keep = Math.Max(1, (int)Math.Round(sorted.Count * o.KeepBestPercent / 100));
                if (o.KeepBestCount > 0) keep = Math.Min(keep, o.KeepBestCount);
                foreach (var g in sorted.Skip(keep)) { g.Rejected = true; g.Reason = "Quality"; }
                good = o.Order == PippOutputOrder.Quality ? sorted.Take(keep).ToList() : good.Where(g => !g.Rejected).ToList();
            }
            if (good.Count == 0) { res.Summary = "All frames rejected"; return res; }
            // crop size
            int cw = o.CropWidth, ch = o.CropHeight;
            if ((cw <= 0 || ch <= 0) && good[0].Object != null)
            {
                double maxDim = good.Max(g => Math.Max(g.Object.Width, g.Object.Height));
                cw = ch = Even((int)(maxDim * o.AutoCropMargin) + 8);
            }
            // rotation per frame
            double[] fieldAngles = null;
            if (o.FieldDerotation && !o.FieldDerotationMeasure && o.Site != null && !double.IsNaN(o.TargetRaRad))
            {
                var times = good.Select(g => g.Time ?? DateTime.UtcNow).ToList();
                fieldAngles = FieldRotation.Angles(o.Site, times, o.TargetRaRad, o.TargetDecRad, 0, o.AltAzMount);
            }
            // pass 2: write
            Directory.CreateDirectory(o.OutputFolder ?? ".");
            string baseName = o.OutputName ?? Path.GetFileNameWithoutExtension(src.Path) + "_pipp";
            IDisposable writer = null;
            int fileNo = 0, inFile = 0;
            AstroImage refForMeasure = null;
            Func<AstroImage, IDisposable> openWriter = img =>
            {
                fileNo++;
                string suffix = o.MaxFramesPerFile > 0 ? "_" + fileNo.ToString("000") : "";
                string p = Path.Combine(o.OutputFolder ?? ".", baseName + suffix + (o.Format == PippOutputFormat.Ser ? ".ser" : ".avi"));
                res.OutputFiles.Add(p);
                if (o.Format == PippOutputFormat.Ser) return new SerWriter(p, img.Width, img.Height, o.OutputBitDepth, img.Channels);
                return new AviWriter(p, img.Width, img.Height, img.Channels);
            };
            try
            {
                for (int k = 0; k < good.Count; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    var info = good[k];
                    var f = PrepareFrame(src, info.Index, o);
                    if (o.CentreObject && info.Object != null && info.Object.Found && cw > 0)
                    {
                        // sub-pixel centring: shift then crop
                        double fx = info.Object.Cx - Math.Floor(info.Object.Cx), fy = info.Object.Cy - Math.Floor(info.Object.Cy);
                        int x0 = (int)Math.Floor(info.Object.Cx) - cw / 2, y0 = (int)Math.Floor(info.Object.Cy) - ch / 2;
                        f = CropPadded(f, x0, y0, cw, ch);
                        if (fx != 0 || fy != 0) f = Geometry.Shift(f, -fx, -fy, Interpolation.Bicubic);
                    }
                    else if (cw > 0 && ch > 0) f = CropPadded(f, (f.Width - cw) / 2, (f.Height - ch) / 2, cw, ch);
                    double rot = o.RotateDeg;
                    if (fieldAngles != null) rot += fieldAngles[k];
                    if (o.FieldDerotation && o.FieldDerotationMeasure)
                    {
                        if (refForMeasure == null) refForMeasure = f;
                        else
                        {
                            double r = Math.Min(f.Width, f.Height) / 2.0;
                            rot += -RotationEstimator.Estimate(refForMeasure.Luminance(), f.Luminance(), f.Width, f.Height, f.Width / 2.0, f.Height / 2.0, r * 0.15, r * 0.9, 20);
                        }
                    }
                    info.Rotation = rot;
                    if (Math.Abs(rot) > 1e-4) f = FieldRotation.RotateAbout(f, rot, f.Width / 2.0, f.Height / 2.0);
                    if (o.FlipHorizontal) f = Geometry.FlipHorizontal(f);
                    if (o.FlipVertical) f = Geometry.FlipVertical(f);
                    if (o.AlignRgbChannels && f.Channels == 3) ColorOps.AlignChannels(f);
                    f.Clip(0, 1);
                    if (info.Time.HasValue) f.DateObs = info.Time;
                    if (o.Format == PippOutputFormat.Ser || o.Format == PippOutputFormat.Avi)
                    {
                        if (writer == null || (o.MaxFramesPerFile > 0 && inFile >= o.MaxFramesPerFile)) { writer?.Dispose(); writer = openWriter(f); inFile = 0; }
                        if (writer is SerWriter) ((SerWriter)writer).Write(f, info.Time); else ((AviWriter)writer).Write(f);
                        inFile++;
                    }
                    else
                    {
                        string name = baseName + "_" + k.ToString("00000");
                        if (o.WinJuposNaming && info.Time.HasValue) name = WinJuposName(info.Time.Value, o.PlanetCode, k);
                        string ext = o.Format == PippOutputFormat.Fits ? ".fits" : o.Format == PippOutputFormat.Tiff ? ".tif" : o.Format == PippOutputFormat.Png ? ".png" : o.Format == PippOutputFormat.Bmp ? ".bmp" : ".jpg";
                        string p = Path.Combine(o.OutputFolder ?? ".", name + ext);
                        var fmt = o.Format == PippOutputFormat.Fits ? (o.OutputBitDepth > 16 ? ImageSaveFormat.Fits32Float : ImageSaveFormat.Fits16)
                            : o.Format == PippOutputFormat.Tiff ? ImageSaveFormat.Tiff16 : ImageFile.FormatFromExtension(p);
                        ImageFile.Save(p, f, fmt);
                        res.OutputFiles.Add(p);
                    }
                    res.Written++;
                    progress?.Report(0.5 + 0.5 * (k + 1) / good.Count);
                }
            }
            finally { writer?.Dispose(); }
            res.Summary = string.Format("{0} of {1} frames written ({2} rejected), crop {3}×{4}", res.Written, indices.Count, indices.Count - good.Count, cw, ch);
            return res;
        }

        private static int Even(int v) => (v + 1) & ~1;

        /// <summary>Crop with zero padding where the window leaves the frame.</summary>
        public static AstroImage CropPadded(AstroImage src, int x0, int y0, int w, int h)
        {
            var o = new AstroImage(w, h, src.Channels) { Header = src.Header.Clone(), OriginalBitDepth = src.OriginalBitDepth, Bayer = Imaging.Debayer.Shift(src.Bayer, x0, y0) };
            for (int c = 0; c < src.Channels; c++)
                for (int y = 0; y < h; y++)
                {
                    int sy = y0 + y;
                    if (sy < 0 || sy >= src.Height) continue;
                    int sx0 = Math.Max(0, x0), sx1 = Math.Min(src.Width, x0 + w);
                    if (sx1 > sx0) Array.Copy(src.Data[c], sy * src.Width + sx0, o.Data[c], y * w + (sx0 - x0), sx1 - sx0);
                }
            return o;
        }

        /// <summary>WinJUPOS file name: YYYY-MM-DD-HHMM_T-Planet (T = tenth of minute).</summary>
        public static string WinJuposName(DateTime utc, string planet, int k)
        {
            int tenth = utc.Second / 6;
            return string.Format("{0:yyyy-MM-dd-HHmm}_{1}{2}{3}", utc, tenth, string.IsNullOrEmpty(planet) ? "" : "-" + planet, "_" + k.ToString("0000"));
        }
    }
}
