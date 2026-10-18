using System;
using Sidralune.Core.Coordinates;
using Sidralune.Core.Ephemeris;
using Sidralune.Core.Mathematics;
using Sidralune.Core.Time;

namespace Sidralune.Core.Observing
{
    /// <summary>Optical system used for visual predictions.</summary>
    public class VisualInstrument
    {
        public string Name = "8\" Dobsonian";
        public double ApertureMm = 203;
        public double FocalLengthMm = 1200;
        public double CentralObstruction = 0.22; // fraction of diameter
        public double Transmission = 0.85;
        public double TrueFieldDeg = 1.0;        // for the chosen eyepiece
        public double EyepieceFocalMm = 25;
        public double EyepieceAfovDeg = 52;

        public double Magnification => FocalLengthMm / EyepieceFocalMm;
        public double ExitPupilMm => ApertureMm / Magnification;
        public double EffectiveTransmission => Transmission * (1 - CentralObstruction * CentralObstruction);
        /// <summary>Dawes limit in arcsec.</summary>
        public double DawesLimit => 116.0 / ApertureMm;
    }

    /// <summary>Observer physiology.</summary>
    public class ObserverEye
    {
        public int Age = 40;
        /// <summary>0 = beginner, 1 = average, 2 = experienced, 3 = expert.</summary>
        public int Experience = 1;
        public bool AvertedVision = true;
        /// <summary>Dark adapted pupil, mm (Watson &amp; Yellott 2012 unified formula, simplified for darkness).</summary>
        public double PupilMm => AstroMath.Clamp(7.8 - 0.045 * (Age - 20), 4.5, 8.0);
        /// <summary>Magnitude gain from experience and averted vision.</summary>
        public double SkillBonusMag => (Experience - 1) * 0.35 + (AvertedVision ? 0.6 : 0);
    }

    public enum Detectability { NotVisible, Difficult, Detectable, Easy, Obvious }

    public class VisibilityResult
    {
        public double SkyBrightness;     // V mag/arcsec² at the object's position
        public double ExtinctionMag;     // atmospheric extinction applied
        public double Margin;            // detection margin in magnitudes (>0 visible)
        public double BestMagnification;
        public double BestMargin;
        public Detectability Rating;
        public double LimitingMagnitude; // stellar limit at chosen magnification
        public string Summary =>
            Rating == Detectability.NotVisible ? "Not visible" :
            Rating == Detectability.Difficult ? "Difficult (averted vision)" :
            Rating == Detectability.Detectable ? "Detectable" :
            Rating == Detectability.Easy ? "Easy" : "Obvious";
    }

    /// <summary>
    /// Visual detectability model. Sky brightness: natural/artificial zenith brightness from the site SQM, airmass
    /// brightening, Krisciunas &amp; Schaefer (1991) moonlight and an empirical twilight term. Detection: Schaefer (1990)
    /// threshold illuminance I_th = c1 (1 + sqrt(c2 B))² with Ricco/Piper spatial summation for extended objects.
    /// </summary>
    public static class VisibilityModel
    {
        public static double MagArcsec2ToNanoLambert(double v) => 34.08 * Math.Exp(20.7233 - 0.92104 * v);
        public static double NanoLambertToMagArcsec2(double b) => (20.7233 - Math.Log(Math.Max(b, 1e-9) / 34.08)) / 0.92104;

        private static double Xks(double zenithRad)
        {
            double s = Math.Sin(Math.Min(zenithRad, AstroMath.Deg2Rad * 89.5));
            return 1.0 / Math.Sqrt(1 - 0.96 * s * s);
        }

        /// <summary>Moonlight scattered sky brightness (nL), Krisciunas &amp; Schaefer 1991.</summary>
        public static double MoonSkyNl(double moonPhaseAngleDeg, double moonZenithRad, double objZenithRad, double separationRad, double k)
        {
            if (moonZenithRad > AstroMath.HalfPi) return 0;
            double a = Math.Abs(moonPhaseAngleDeg);
            double istar = Math.Pow(10, -0.4 * (3.84 + 0.026 * a + 4e-9 * Math.Pow(a, 4)));
            double rhoDeg = Math.Max(separationRad * AstroMath.Rad2Deg, 0.5);
            double cr = Math.Cos(rhoDeg * AstroMath.Deg2Rad);
            double f = Math.Pow(10, 5.36) * (1.06 + cr * cr) + Math.Pow(10, 6.15 - rhoDeg / 40.0);
            return f * istar * Math.Pow(10, -0.4 * k * Xks(moonZenithRad)) * (1 - Math.Pow(10, -0.4 * k * Xks(objZenithRad)));
        }

        private static readonly double[,] TwilightKnots = { { 0, 7.5 }, { -3, 11.5 }, { -6, 14.6 }, { -9, 17.3 }, { -12, 19.4 }, { -15, 21.0 }, { -18, 22.0 } };

        /// <summary>Twilight sky brightness (nL) at the zenith for a sun altitude in degrees.</summary>
        public static double TwilightNl(double sunAltDeg)
        {
            if (sunAltDeg <= -18) return 0;
            if (sunAltDeg >= 0) return MagArcsec2ToNanoLambert(7.5 - sunAltDeg * 0.3);
            for (int i = 0; i < TwilightKnots.GetLength(0) - 1; i++)
            {
                double h0 = TwilightKnots[i, 0], h1 = TwilightKnots[i + 1, 0];
                if (sunAltDeg <= h0 && sunAltDeg >= h1)
                {
                    double t = (sunAltDeg - h0) / (h1 - h0);
                    double v = TwilightKnots[i, 1] + t * (TwilightKnots[i + 1, 1] - TwilightKnots[i, 1]);
                    // subtract the dark-sky part that is modelled separately
                    return Math.Max(0, MagArcsec2ToNanoLambert(v) - MagArcsec2ToNanoLambert(22.0));
                }
            }
            return 0;
        }

        /// <summary>
        /// Total sky brightness (V mag/arcsec²) at an object position. Altitudes in radians.
        /// </summary>
        public static double SkyBrightness(Observer obs, double objAlt, double sunAltDeg, double moonAlt, double moonPhaseDeg, double moonSeparation)
        {
            double z = AstroMath.HalfPi - Math.Max(objAlt, 0.5 * AstroMath.Deg2Rad);
            double k = obs.ExtinctionV;
            double X = Xks(z);
            double bZenith = MagArcsec2ToNanoLambert(obs.SqmZenith);
            // airglow / light pollution brighten towards the horizon (van Rhijn-like), dimmed by extinction
            double bSky = bZenith * X * Math.Pow(10, -0.4 * k * (X - 1));
            double bMoon = moonAlt > -0.02 ? MoonSkyNl(moonPhaseDeg, AstroMath.HalfPi - moonAlt, z, moonSeparation, k) : 0;
            double bTw = TwilightNl(sunAltDeg) * Math.Pow(X, 0.5);
            return NanoLambertToMagArcsec2(bSky + bMoon + bTw);
        }

        /// <summary>Schaefer (1990) night-vision threshold illuminance (ft-candles) for a point source on background B (nL).</summary>
        public static double PointThreshold(double bNl)
        {
            double c1 = Math.Pow(10, -9.8), c2 = Math.Pow(10, -1.9);
            if (bNl > 1500) { c1 = Math.Pow(10, -8.350); c2 = Math.Pow(10, -5.90); }
            double s = 1 + Math.Sqrt(c2 * bNl);
            return c1 * s * s;
        }

        /// <summary>Illuminance (ft-candles) of a source of V magnitude m.</summary>
        public static double MagToFootCandles(double m) => 2.36e-7 * Math.Pow(10, -0.4 * m);

        /// <summary>Naked-eye limiting magnitude for a sky brightness (mag/arcsec²).</summary>
        public static double NakedEyeLimit(double skyMag, ObserverEye eye = null)
        {
            double th = PointThreshold(MagArcsec2ToNanoLambert(skyMag));
            double lm = -2.5 * Math.Log10(th / 2.36e-7);
            return lm + (eye != null ? eye.SkillBonusMag - 0.6 : 0);
        }

        /// <summary>
        /// Detection margin (mag) of an object seen through the instrument at magnification M.
        /// objMag: total V magnitude after extinction; sizeArcmin: mean apparent diameter (0 for a star).
        /// </summary>
        public static double Margin(double objMag, double sizeArcmin, double skyMag, VisualInstrument ins, ObserverEye eye, double magnification, double seeingArcsec = 2.5)
        {
            double De = eye.PupilMm;
            double exitPupil = ins.ApertureMm / magnification;
            double effAperture = Math.Min(ins.ApertureMm, magnification * De);
            double T = ins.EffectiveTransmission;
            // background surface brightness at the eye: dimmed when exit pupil < eye pupil
            double bgFactor = T * Math.Pow(Math.Min(exitPupil, De) / De, 2);
            double bNl = MagArcsec2ToNanoLambert(skyMag) * bgFactor;
            // object flux at the eye
            double gain = T * Math.Pow(effAperture / De, 2);
            double flux = MagToFootCandles(objMag) * gain;
            // apparent size, including seeing/diffraction blur for small objects
            double blurArcmin = Math.Sqrt(Math.Pow(seeingArcsec / 60.0, 2) + Math.Pow(2.44 * 550e-6 / ins.ApertureMm * AstroMath.Rad2Deg * 60, 2));
            double apparentArcmin = Math.Sqrt(sizeArcmin * sizeArcmin + blurArcmin * blurArcmin) * magnification;
            // spatial summation: Ricco critical angle shrinks with background brightness
            double thetaC = AstroMath.Clamp(6.0 * Math.Pow(Math.Max(bNl, 1e-3) / 1000.0, -0.25), 2, 60);
            double th = PointThreshold(bNl);
            if (apparentArcmin > thetaC) th *= apparentArcmin / thetaC;
            double margin = 2.5 * Math.Log10(flux / th) + eye.SkillBonusMag - 0.6;
            return margin;
        }

        /// <summary>Full prediction for an object, scanning magnifications for the best view.</summary>
        public static VisibilityResult Predict(double objMag, double sizeArcmin, double altRad, double skyMag, Observer obs, VisualInstrument ins, ObserverEye eye)
        {
            var r = new VisibilityResult();
            double X = Transforms.Airmass(altRad);
            r.ExtinctionMag = altRad > 0 ? obs.ExtinctionV * X : 99;
            r.SkyBrightness = skyMag;
            double m = objMag + r.ExtinctionMag;
            double minMag = ins.ApertureMm / eye.PupilMm;
            double maxMag = Math.Min(ins.ApertureMm * 2.0, 600);
            double best = double.MinValue, bestM = minMag;
            for (double M = minMag; M <= maxMag; M *= 1.08)
            {
                double mg = Margin(m, sizeArcmin, skyMag, ins, eye, M, obs.SeeingArcsec);
                if (mg > best) { best = mg; bestM = M; }
            }
            r.BestMagnification = bestM;
            r.BestMargin = best;
            r.Margin = Margin(m, sizeArcmin, skyMag, ins, eye, ins.Magnification, obs.SeeingArcsec);
            // stellar limiting magnitude at the chosen magnification: margin = 0 for a point source
            r.LimitingMagnitude = m - Margin(m, 0, skyMag, ins, eye, ins.Magnification, obs.SeeingArcsec) + r.ExtinctionMag - obs.ExtinctionV * X;
            double v = r.BestMargin;
            r.Rating = altRad <= 0 || v < -0.5 ? Detectability.NotVisible : v < 0 ? Detectability.Difficult : v < 0.7 ? Detectability.Detectable : v < 1.8 ? Detectability.Easy : Detectability.Obvious;
            return r;
        }

        /// <summary>Convenience: sky brightness at a J2000 position for a given time, computing Sun and Moon.</summary>
        public static double SkyBrightnessAt(SkyContext ctx, double ra, double dec, out double altRad)
        {
            var h = ctx.IcrsToHorizontal(ra, dec);
            altRad = h.Alt;
            var sun = ctx.BodyPlace(Body.Sun);
            var moon = ctx.BodyPlace(Body.Moon);
            double sep = AstroMath.Separation(ra, dec, moon.Astrometric.Ra, moon.Astrometric.Dec);
            return SkyBrightness(ctx.Observer, h.Alt, sun.Horizontal.AltDeg, moon.Horizontal.Alt, moon.PhaseAngle * AstroMath.Rad2Deg, sep);
        }
    }

    /// <summary>Imaging camera/telescope for exposure planning.</summary>
    public class ImagingSystem
    {
        public string Name = "Imaging rig";
        public double ApertureMm = 80, FocalLengthMm = 480, CentralObstruction = 0;
        public double PixelSizeUm = 3.76, QuantumEfficiency = 0.8, ReadNoiseE = 1.5, DarkCurrentEPerSec = 0.002, GainEPerAdu = 1.0, FullWellE = 50000;
        public int WidthPx = 6248, HeightPx = 4176, BitDepth = 16;
        public double FilterBandwidthNm = 88; // V-like broadband default; ~3-7 for narrowband
        public double Transmission = 0.9;
        public bool IsColor = false;

        public double PixelScaleArcsec => 206.265 * PixelSizeUm / FocalLengthMm;
        public double FieldWidthDeg => WidthPx * PixelScaleArcsec / 3600.0;
        public double FieldHeightDeg => HeightPx * PixelScaleArcsec / 3600.0;
        public double FocalRatio => FocalLengthMm / ApertureMm;
    }

    /// <summary>CCD/CMOS signal-to-noise and exposure calculator.</summary>
    public static class ExposureCalculator
    {
        /// <summary>Photon rate (photons/s/m²/nm) for a V=0 source ≈ 1000 ph/s/cm²/Å at 550 nm.</summary>
        public const double V0PhotonFlux = 1.0e8;

        /// <summary>Electrons/s/pixel from a surface brightness (mag/arcsec²).</summary>
        public static double SurfaceRate(ImagingSystem sys, double magArcsec2)
        {
            double area = Math.PI / 4 * Math.Pow(sys.ApertureMm / 1000.0, 2) * (1 - sys.CentralObstruction * sys.CentralObstruction);
            double photons = V0PhotonFlux * Math.Pow(10, -0.4 * magArcsec2) * area * sys.FilterBandwidthNm;
            double pixArea = sys.PixelScaleArcsec * sys.PixelScaleArcsec;
            double cfa = sys.IsColor ? 0.33 : 1.0;
            return photons * pixArea * sys.QuantumEfficiency * sys.Transmission * cfa;
        }

        /// <summary>Electrons/s total for a point source of magnitude m.</summary>
        public static double PointRate(ImagingSystem sys, double mag)
        {
            double area = Math.PI / 4 * Math.Pow(sys.ApertureMm / 1000.0, 2) * (1 - sys.CentralObstruction * sys.CentralObstruction);
            return V0PhotonFlux * Math.Pow(10, -0.4 * mag) * area * sys.FilterBandwidthNm * sys.QuantumEfficiency * sys.Transmission * (sys.IsColor ? 0.33 : 1.0);
        }

        /// <summary>SNR per pixel of an extended target after N subs of t seconds.</summary>
        public static double ExtendedSnr(ImagingSystem sys, double targetSb, double skySb, double subSec, int subs)
        {
            double s = SurfaceRate(sys, targetSb) * subSec;
            double b = SurfaceRate(sys, skySb) * subSec;
            double d = sys.DarkCurrentEPerSec * subSec;
            double noise = Math.Sqrt(s + b + d + sys.ReadNoiseE * sys.ReadNoiseE);
            return s / noise * Math.Sqrt(subs);
        }

        /// <summary>SNR for a star of magnitude m in an aperture of radius r (pixels) for one exposure.</summary>
        public static double StarSnr(ImagingSystem sys, double mag, double skySb, double seeingArcsec, double expSec)
        {
            double fwhmPx = Math.Max(1.0, seeingArcsec / sys.PixelScaleArcsec);
            double rPx = 1.5 * fwhmPx;
            double npix = Math.PI * rPx * rPx;
            double s = PointRate(sys, mag) * expSec * 0.94;
            double b = SurfaceRate(sys, skySb) * expSec * npix;
            double d = sys.DarkCurrentEPerSec * expSec * npix;
            return s / Math.Sqrt(s + b + d + npix * sys.ReadNoiseE * sys.ReadNoiseE);
        }

        /// <summary>Optimal sub-exposure so read noise adds ≤ 5% to sky noise (Glover/Smith criterion: sky e- ≥ ~10 RN²).</summary>
        public static double OptimalSubExposure(ImagingSystem sys, double skySb, double readNoiseContribution = 0.05)
        {
            double skyRate = SurfaceRate(sys, skySb);
            double k = 1.0 / (Math.Pow(1 + readNoiseContribution, 2) - 1);
            return k * sys.ReadNoiseE * sys.ReadNoiseE / Math.Max(skyRate, 1e-9);
        }

        /// <summary>Total integration (s) to reach a target SNR on an extended object.</summary>
        public static double TimeForSnr(ImagingSystem sys, double targetSb, double skySb, double subSec, double snr)
        {
            double one = ExtendedSnr(sys, targetSb, skySb, subSec, 1);
            if (one <= 0) return double.PositiveInfinity;
            return Math.Ceiling(Math.Pow(snr / one, 2)) * subSec;
        }

        /// <summary>Critical focus zone (µm) for a given focal ratio at 550 nm: CFZ = 4.88 λ F².</summary>
        public static double CriticalFocusZoneUm(double fRatio, double lambdaNm = 550) => 4.88 * lambdaNm / 1000.0 * fRatio * fRatio;

        /// <summary>Nyquist-optimal focal length for a pixel size at the given seeing (FWHM/2.5 per pixel rule).</summary>
        public static double OptimalFocalLength(double pixelUm, double seeingArcsec) => 206.265 * pixelUm / (seeingArcsec / 2.5);

        /// <summary>
        /// Planetary imaging f-ratio for Nyquist sampling of the diffraction cutoff: F = 2p/λ (≈3.6×p µm at 550 nm).
        /// Many imagers use ~5×p to give headroom for good seeing; pass oversample=1.4 for that.
        /// </summary>
        public static double OptimalPlanetaryFRatio(double pixelUm, double lambdaNm = 550, double oversample = 1.0) => 2.0 * pixelUm / (lambdaNm / 1000.0) * oversample;

        /// <summary>Maximum exposure (s) before rotational smearing exceeds blurArcsec for a planet (rotation period in hours).</summary>
        public static double PlanetRotationLimitSec(double diameterArcsec, double periodHours, double blurArcsec)
        {
            double eqSpeed = Math.PI * diameterArcsec / (periodHours * 3600.0); // arcsec per second at the CM
            return blurArcsec / eqSpeed;
        }
    }
}
