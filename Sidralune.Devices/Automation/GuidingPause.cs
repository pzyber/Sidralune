using System;
using System.Threading;
using System.Threading.Tasks;
using Sidralune.Core.Util;
using Sidralune.Devices.Guiding;

namespace Sidralune.Devices.Automation
{
    /// <summary>
    /// Sidralune's own work that would upset guiding takes care of it by itself. Autofocus pauses guiding (the star changes
    /// shape as the focus moves) and lets it settle afterwards. Centring and a meridian flip stop it for their slews and
    /// start it again afterwards: when the mount came back to the field and pier side it guided on, on the star nearest
    /// the old lock position, held there, so the frames line up with those before. PHD2 is handled the same way through
    /// IGuider, as far as it allows.
    /// </summary>
    public static class GuidingPause
    {
        /// <summary>Guiding, or waiting for its star to come back (a cloud).</summary>
        public static bool Guiding(IGuider g)
        {
            if (g == null) return false;
            try { if (!g.Connected) return false; } catch { return false; }
            var sg = g as SidraluneGuider;
            if (sg != null) return sg.InGuidingPhase;
            return g.IsGuiding || g.State == SidraluneGuider.LostLock;
        }

        /// <summary>
        /// Runs work with guiding paused: corrections stop while the star is still followed, and guiding settles again
        /// before the work's result is handed on. Guiding paused already (by hand) is left as it is.
        /// </summary>
        public static async Task<T> During<T>(IGuider g, Func<Task<T>> work, CancellationToken ct)
        {
            if (!Guiding(g) || g.State == SidraluneGuider.Paused) return await work();
            g.Pause(true);
            Log.Info("Guider", "Guiding paused");
            T result;
            try { result = await work(); }
            catch { g.Pause(false); throw; }
            g.Pause(false);
            Log.Info("Guider", "Guiding resumed");
            var sg = g as SidraluneGuider;
            if (sg != null && sg.InGuidingPhase)
            {
                // corrections were held back: the star comes back to the lock position first
                try { await sg.Settle(sg.SettlePixels, sg.SettleSeconds, sg.SettleTimeoutSeconds, ct); }
                catch (InvalidOperationException ex) { Log.Warn("Guider", ex.Message); }
            }
            return result;
        }

        /// <summary>
        /// Runs a slew of Sidralune's own with guiding stopped, and starts guiding again afterwards when it was on. With
        /// sameField, and the mount on the same pier side afterwards, the star nearest the old lock position is guided on and
        /// held there. Failing work leaves guiding stopped (a sequence starts it again before its next frame).
        /// </summary>
        public static async Task<T> AroundSlew<T>(IGuider g, ITelescope tel, Func<Task<T>> work, bool sameField, CancellationToken ct)
        {
            if (!Guiding(g)) return await work();
            var sg = g as SidraluneGuider;
            double lx = double.NaN, ly = double.NaN;
            var pier = PierOf(tel);
            if (sg != null && sameField) { lx = sg.LockX; ly = sg.LockY; }
            g.StopGuiding();
            Log.Info("Guider", "Guiding stopped for a slew; it starts again afterwards");
            var result = await work();
            if (sg != null && !double.IsNaN(lx) && !double.IsNaN(ly) && PierOf(tel) == pier) sg.ResumeAt(lx, ly);
            await g.StartGuiding(false, ct);
            return result;
        }

        private static PierSide PierOf(ITelescope tel)
        {
            try { return tel != null && tel.Connected ? tel.SideOfPier : PierSide.Unknown; } catch { return PierSide.Unknown; }
        }
    }
}
