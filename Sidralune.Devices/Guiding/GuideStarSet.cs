using System;
using System.Collections.Generic;
using System.Linq;

namespace Sidralune.Devices.Guiding
{
    /// <summary>A star guided on besides the guide star: where it was last measured and where it sits relative to the guide star.</summary>
    public class SecondaryStar
    {
        public GuideStarInfo Info;
        /// <summary>Its place relative to the guide star (pixels); fixed while guiding, so its reference is the lock position plus this.</summary>
        public double OffX, OffY;
        /// <summary>Frames in a row it was not found, and frames in a row it disagreed with the others.</summary>
        public int Missed, LeftOut;
        public bool FoundNow;
    }

    /// <summary>What one frame gave: the displacement to guide on, and what happened to the stars.</summary>
    public class StarSetFrame
    {
        /// <summary>There is a displacement to guide on (or, when not guiding, the guide star was found).</summary>
        public bool Usable;
        /// <summary>The displacement from the lock position (image pixels) of all the stars together.</summary>
        public double Dx, Dy;
        public int StarsUsed;
        public bool PrimaryFound, PrimaryRejected, PrimaryAlone;
        /// <summary>Another star became the guide star; the lock position moves to its reference.</summary>
        public bool Promoted;
        public double NewLockX = double.NaN, NewLockY = double.NaN;
        /// <summary>Where the new guide star was from the old one (image pixels), when one was promoted.</summary>
        public double PromotedOffX, PromotedOffY;
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// The guide star and the other stars guided on with it. Each frame every star is measured in its own search box; while
    /// guiding, their displacements from their references are averaged, weighted by SNR, leaving out stars that disagree
    /// with the rest, so the seeing and measuring noise of one star counts for less. A large jump of the guide star (a bump)
    /// is trusted on its own. When the guide star cannot be seen, the others carry on, and after a while the best of them
    /// becomes the guide star. The guide star's brightness is watched: a frame in which it changes a lot is not used.
    /// </summary>
    public class GuideStarSet
    {
        public int SearchRadius = 15;
        public bool MultiStar = true;
        public int MaxStars = 9;
        public bool MassCheck = true;
        /// <summary>The share by which the guide star's flux may change (0.5: 50 %).</summary>
        public double MassTolerance = 0.5;
        /// <summary>A guide star jump larger than this (pixels) is followed on the guide star alone.</summary>
        public double JumpPx = 2.5;
        /// <summary>Frames without the guide star before another star takes over.</summary>
        public int PromoteAfter = 10;

        /// <summary>The guide star's last measurement; when it is not seen, where it was last.</summary>
        public GuideStarInfo Primary { get; private set; }
        public List<SecondaryStar> Secondaries { get; } = new List<SecondaryStar>();
        public int PrimaryMissed { get; private set; }
        /// <summary>How many stars the last frame used.</summary>
        public int LastUsed { get; private set; }
        private readonly List<double> _flux = new List<double>();
        private int _rejectedRun;
        private DateTime _lastSearch = DateTime.MinValue;

        /// <summary>A new guide star, and (with multi-star) the other stars chosen around it in the same frame.</summary>
        /// <summary>Stars this passes (the catalogue's single, steady stars) are chosen as partners first; null for no preference.</summary>
        public Func<double, double, bool> Prefer;

        public void SetPrimary(GuideStarInfo star, float[] d, int w, int h, int edgeMargin)
        {
            Primary = star;
            PrimaryMissed = 0;
            _flux.Clear(); _rejectedRun = 0;
            Secondaries.Clear();
            LastUsed = star != null ? 1 : 0;
            if (star != null && MultiStar && d != null) AddSecondaries(d, w, h, edgeMargin);
        }

        public void Clear() { SetPrimary(null, null, 0, 0, 0); }

        private void AddSecondaries(float[] d, int w, int h, int edgeMargin)
        {
            _lastSearch = DateTime.UtcNow;
            int want = Math.Max(0, MaxStars - 1) - Secondaries.Count;
            if (want <= 0 || Primary == null) return;
            var taken = new List<double[]> { new[] { Primary.X, Primary.Y } };
            taken.AddRange(Secondaries.Select(s => new[] { s.Info.X, s.Info.Y }));
            foreach (var m in GuideStars.SelectMore(d, w, h, SearchRadius, edgeMargin, taken, want, 6, Prefer))
                Secondaries.Add(new SecondaryStar { Info = m, OffX = m.X - Primary.X, OffY = m.Y - Primary.Y, FoundNow = true });
        }

        /// <summary>
        /// Measures every star in a frame. While guiding, the result is the displacement from the lock position of all the
        /// stars together; otherwise the other stars' places relative to the guide star are brought up to date.
        /// </summary>
        public StarSetFrame Track(float[] d, int w, int h, double lockX, double lockY, bool guiding, int edgeMargin)
        {
            var r = new StarSetFrame();
            if (Primary == null) { LastUsed = 0; return r; }
            bool haveLock = !double.IsNaN(lockX) && !double.IsNaN(lockY);
            // the guide star in its box, or around the lock position (a large correction may have moved it beyond the box)
            var m = GuideStars.Measure(d, w, h, Primary.X, Primary.Y, SearchRadius);
            if (m == null && guiding && haveLock) m = GuideStars.Measure(d, w, h, lockX, lockY, SearchRadius);
            r.PrimaryFound = m != null;
            if (m != null)
            {
                PrimaryMissed = 0;
                if (guiding && MassCheck && _flux.Count >= 3)
                {
                    double reference = Median(_flux);
                    if (reference > 0 && Math.Abs(m.Flux / reference - 1) > MassTolerance)
                    {
                        // a passing cloud, a satellite, another star: this frame is not used, but after three in a row the
                        // new brightness is the star's
                        if (++_rejectedRun >= 3) { _flux.Clear(); _rejectedRun = 0; r.Notes.Add("Star mass reference reset"); }
                        else { r.PrimaryRejected = true; r.Notes.Add(string.Format("Star mass changed by {0:0} %", (m.Flux / reference - 1) * 100)); }
                    }
                    else _rejectedRun = 0;
                }
                if (!r.PrimaryRejected) { _flux.Add(m.Flux); if (_flux.Count > 10) _flux.RemoveAt(0); }
                Primary = m;
            }
            else PrimaryMissed++;

            foreach (var s in Secondaries)
            {
                var sm = GuideStars.Measure(d, w, h, s.Info.X, s.Info.Y, SearchRadius);
                if (sm == null && m != null) sm = GuideStars.Measure(d, w, h, m.X + s.OffX, m.Y + s.OffY, SearchRadius);
                if (sm == null && guiding && haveLock) sm = GuideStars.Measure(d, w, h, lockX + s.OffX, lockY + s.OffY, SearchRadius);
                s.FoundNow = sm != null;
                if (sm == null) { s.Missed++; continue; }
                s.Info = sm;
                s.Missed = 0;
                if (!guiding && m != null) { s.OffX = sm.X - m.X; s.OffY = sm.Y - m.Y; }
            }

            if (!guiding)
            {
                r.Usable = m != null;
                LastUsed = m != null ? 1 + Secondaries.Count(s => s.FoundNow) : 0;
                r.StarsUsed = LastUsed;
            }
            else Combine(r, m, lockX, lockY);

            int before = Secondaries.Count;
            Secondaries.RemoveAll(s => s.Missed >= 5 || s.LeftOut >= 3);
            if (Secondaries.Count < before) r.Notes.Add(string.Format("{0} star(s) dropped", before - Secondaries.Count));

            // the guide star has been gone a while: the best of the others takes over
            if (guiding && m == null && PrimaryMissed >= PromoteAfter && haveLock)
            {
                var best = Secondaries.Where(s => s.FoundNow).OrderByDescending(s => s.Info.Snr).FirstOrDefault();
                if (best != null)
                {
                    r.Promoted = true;
                    r.NewLockX = lockX + best.OffX; r.NewLockY = lockY + best.OffY;
                    r.PromotedOffX = best.OffX; r.PromotedOffY = best.OffY;
                    foreach (var s in Secondaries) { s.OffX -= best.OffX; s.OffY -= best.OffY; }
                    Secondaries.Remove(best);
                    Primary = best.Info;
                    PrimaryMissed = 0;
                    _flux.Clear(); _rejectedRun = 0;
                    r.Notes.Add("Guide star replaced by " + best.Info);
                }
            }

            // fewer than half the stars left: look for more, at most once a minute
            if (MultiStar && m != null && Secondaries.Count < Math.Max(0, MaxStars - 1) / 2 && (DateTime.UtcNow - _lastSearch).TotalSeconds >= 60)
            {
                int had = Secondaries.Count;
                AddSecondaries(d, w, h, edgeMargin);
                if (Secondaries.Count > had) r.Notes.Add(string.Format("{0} star(s) added", Secondaries.Count - had));
            }
            return r;
        }

        private void Combine(StarSetFrame r, GuideStarInfo m, double lockX, double lockY)
        {
            bool primaryOk = m != null && !r.PrimaryRejected;
            double pdx = primaryOk ? m.X - lockX : double.NaN, pdy = primaryOk ? m.Y - lockY : double.NaN;
            if (primaryOk && Math.Sqrt(pdx * pdx + pdy * pdy) > JumpPx)
            {
                r.Usable = true; r.Dx = pdx; r.Dy = pdy; r.StarsUsed = 1; r.PrimaryAlone = true;
                LastUsed = 1;
                return;
            }
            // every star's displacement from its reference, with its SNR as the weight
            var xs = new List<double>(); var ys = new List<double>(); var ws = new List<double>(); var who = new List<SecondaryStar>();
            if (primaryOk) { xs.Add(pdx); ys.Add(pdy); ws.Add(Math.Max(1, m.Snr)); who.Add(null); }
            foreach (var s in Secondaries.Where(s => s.FoundNow))
            {
                xs.Add(s.Info.X - (lockX + s.OffX)); ys.Add(s.Info.Y - (lockY + s.OffY)); ws.Add(Math.Max(1, s.Info.Snr)); who.Add(s);
            }
            int n = xs.Count;
            // without the guide star, two others are needed to trust a displacement
            if (n == 0 || (!primaryOk && n < 2)) { LastUsed = 0; return; }
            double mx = Median(xs), my = Median(ys);
            var dist = new List<double>();
            for (int i = 0; i < n; i++) dist.Add(Math.Sqrt((xs[i] - mx) * (xs[i] - mx) + (ys[i] - my) * (ys[i] - my)));
            double limit = Math.Max(2, 3 * 1.4826 * Median(dist));
            double sw = 0, sx = 0, sy = 0;
            int used = 0;
            for (int i = 0; i < n; i++)
            {
                var s = who[i];
                // the guide star always counts; the others only while they agree with the rest
                if (s != null && n >= 3 && dist[i] > limit) { s.LeftOut++; continue; }
                if (s != null) s.LeftOut = 0;
                sw += ws[i]; sx += ws[i] * xs[i]; sy += ws[i] * ys[i]; used++;
            }
            if (sw <= 0) { LastUsed = 0; return; }
            r.Usable = true; r.Dx = sx / sw; r.Dy = sy / sw; r.StarsUsed = used;
            LastUsed = used;
        }

        private static double Median(List<double> v)
        {
            var a = v.ToArray();
            Array.Sort(a);
            int n = a.Length;
            return n == 0 ? 0 : n % 2 == 1 ? a[n / 2] : 0.5 * (a[n / 2 - 1] + a[n / 2]);
        }
    }
}
