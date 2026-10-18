using System;
using System.Collections.Generic;
using Sidralune.Core.Mathematics;

namespace Sidralune.Core.Catalogs
{
    public enum ObjectType
    {
        Unknown, Star, DoubleStar, VariableStar, Galaxy, GalaxyPair, GalaxyGroup, GlobularCluster, OpenCluster, ClusterNebula,
        EmissionNebula, ReflectionNebula, PlanetaryNebula, SupernovaRemnant, DarkNebula, HIIRegion, Asterism, StarCloud, Quasar,
        Planet, Moon, Sun, Asteroid, Comet, Satellite, RadioSource, Exoplanet, Nova, Supernova, Other
    }

    /// <summary>Common base for anything with a fixed J2000 position.</summary>
    public abstract class SkyObject
    {
        /// <summary>ICRS/J2000 right ascension, radians.</summary>
        public double Ra;
        /// <summary>ICRS/J2000 declination, radians.</summary>
        public double Dec;
        public double Mag = double.NaN;
        public string Name;
        public ObjectType Type;
        public string Constellation;

        public double RaHours => Ra * AstroMath.Rad2Hour;
        public double DecDeg => Dec * AstroMath.Rad2Deg;
        public abstract string DisplayName { get; }
        public override string ToString() => DisplayName;
    }

    public class Star : SkyObject
    {
        public int Hip;
        public int Hd;
        public int Hr;
        public string Bayer;      // e.g. "Alp Ori" or "58 Ori"
        public string Gaia;
        /// <summary>Proper motion in RA*cos(dec) and Dec, mas/yr.</summary>
        public float PmRa, PmDec;
        public float Parallax = float.NaN;
        public float BV = float.NaN;
        public string SpectralType;
        public float RadialVelocity = float.NaN;
        /// <summary>Variable star info (VSX type/range) when known.</summary>
        public string VariableType;
        public float MagMin = float.NaN;
        public double PeriodDays = double.NaN;
        /// <summary>Double star: separation (arcsec), PA (deg), companion mag.</summary>
        public float Separation = float.NaN, PositionAngle = float.NaN, CompanionMag = float.NaN;

        public Star() { Type = ObjectType.Star; }

        public override string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Name)) return Name;
                if (!string.IsNullOrEmpty(Bayer)) return Bayer;
                if (Hip > 0) return "HIP " + Hip;
                if (Hd > 0) return "HD " + Hd;
                return "Star";
            }
        }

        /// <summary>Approximate effective temperature from B-V (Ballesteros 2012).</summary>
        public double TemperatureK => float.IsNaN(BV) ? 5800 : 4600 * (1 / (0.92 * BV + 1.7) + 1 / (0.92 * BV + 0.62));
    }

    public class DeepSkyObject : SkyObject
    {
        /// <summary>Primary catalog designation, e.g. "NGC 224" or "M 31".</summary>
        public string Id;
        public string Messier;
        public string Caldwell;
        public string Ngc;
        public string Ic;
        public List<string> AltNames = new List<string>();
        /// <summary>Major/minor axis, arcmin.</summary>
        public double MajorAxis = double.NaN, MinorAxis = double.NaN;
        /// <summary>Position angle of the major axis, degrees (N through E).</summary>
        public double PositionAngleDeg = double.NaN;
        /// <summary>Surface brightness, mag/arcsec².</summary>
        public double SurfaceBrightness = double.NaN;
        public string Morphology;
        public double Redshift = double.NaN;
        public string Notes;

        public override string DisplayName
        {
            get
            {
                string id = !string.IsNullOrEmpty(Messier) ? Messier : (Id ?? "");
                if (!string.IsNullOrEmpty(Name)) return id.Length > 0 ? id + " " + Name : Name;
                return id;
            }
        }

        /// <summary>Mean surface brightness estimate from magnitude and size if not supplied.</summary>
        public double EffectiveSurfaceBrightness
        {
            get
            {
                if (!double.IsNaN(SurfaceBrightness)) return SurfaceBrightness;
                if (double.IsNaN(Mag) || double.IsNaN(MajorAxis)) return double.NaN;
                double minor = double.IsNaN(MinorAxis) ? MajorAxis : MinorAxis;
                double areaArcsec2 = Math.PI / 4 * MajorAxis * minor * 3600;
                return Mag + 2.5 * Math.Log10(Math.Max(areaArcsec2, 1));
            }
        }

        public static string TypeLabel(ObjectType t)
        {
            switch (t)
            {
                case ObjectType.Galaxy: return "Galaxy";
                case ObjectType.GalaxyPair: return "Galaxy pair";
                case ObjectType.GalaxyGroup: return "Galaxy group";
                case ObjectType.GlobularCluster: return "Globular cluster";
                case ObjectType.OpenCluster: return "Open cluster";
                case ObjectType.ClusterNebula: return "Cluster + nebula";
                case ObjectType.EmissionNebula: return "Emission nebula";
                case ObjectType.ReflectionNebula: return "Reflection nebula";
                case ObjectType.PlanetaryNebula: return "Planetary nebula";
                case ObjectType.SupernovaRemnant: return "Supernova remnant";
                case ObjectType.DarkNebula: return "Dark nebula";
                case ObjectType.HIIRegion: return "HII region";
                case ObjectType.Asterism: return "Asterism";
                case ObjectType.StarCloud: return "Star cloud";
                case ObjectType.DoubleStar: return "Double star";
                case ObjectType.Quasar: return "Quasar";
            }
            return t.ToString();
        }
    }

    /// <summary>A constellation with label position, stick-figure lines and boundary.</summary>
    public class Constellation
    {
        public string Abbr;
        public string Name;
        public string Genitive;
        public double LabelRa, LabelDec; // radians J2000
        /// <summary>Pairs of HIP numbers forming the stick figure.</summary>
        public List<int[]> LineHip = new List<int[]>();
        /// <summary>Resolved line segments (ra1, dec1, ra2, dec2), radians.</summary>
        public List<double[]> Lines = new List<double[]>();
        /// <summary>Boundary vertices (ra, dec) radians, J2000.</summary>
        public List<double[]> Boundary = new List<double[]>();
        public override string ToString() => Name;
    }
}
