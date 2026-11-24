using System;
using Sidralune.Core;
using Sidralune.Core.Mathematics;
using Sidralune.Core.MinorBodies;
using Sidralune.Core.Time;

namespace Sidralune.Devices.Guiding
{
    /// <summary>Where comet tracking's rates come from: typed in, a comet's orbit, or learned from moving the lock position.</summary>
    public enum CometRateSource { Entered, Comet, Trained }

    /// <summary>A comet's motion on the sky from its orbit.</summary>
    public static class CometRates
    {
        /// <summary>
        /// The comet's apparent motion at a time, seen from the site: RA as sky motion (arcsec/h, times cos dec, positive
        /// eastwards) and Dec (arcsec/h, positive northwards), from its places half an hour either side.
        /// </summary>
        public static void Compute(MinorBody comet, Observer site, DateTime utc, out double raArcsecPerHour, out double decArcsecPerHour)
        {
            var a = MinorBodyStore.Place(new SkyContext(site, AstroTime.FromDateTime(utc.AddMinutes(-30))), comet).Apparent;
            var b = MinorBodyStore.Place(new SkyContext(site, AstroTime.FromDateTime(utc.AddMinutes(30))), comet).Apparent;
            double dra = AstroMath.NormalizeRad(b.Ra - a.Ra + Math.PI) - Math.PI;
            raArcsecPerHour = dra * Math.Cos(0.5 * (a.Dec + b.Dec)) * AstroMath.Rad2ArcSec;
            decArcsecPerHour = (b.Dec - a.Dec) * AstroMath.Rad2ArcSec;
        }

        /// <summary>The comet of that name in Sidralune's comet list, or null.</summary>
        public static MinorBody Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (var c in MinorBodyStore.Instance.Comets)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(c.Designation, name, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }
    }
}
