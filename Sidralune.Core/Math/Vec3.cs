using System;

namespace Sidralune.Core.Mathematics
{
    /// <summary>Immutable 3-vector used for positions, velocities and direction cosines.</summary>
    public struct Vec3
    {
        public readonly double X, Y, Z;

        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 UnitX = new Vec3(1, 0, 0);
        public static readonly Vec3 UnitY = new Vec3(0, 1, 0);
        public static readonly Vec3 UnitZ = new Vec3(0, 0, 1);

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double LengthSquared => X * X + Y * Y + Z * Z;

        public Vec3 Normalized()
        {
            double l = Length;
            return l > 0 ? new Vec3(X / l, Y / l, Z / l) : this;
        }

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator /(Vec3 a, double s) => new Vec3(a.X / s, a.Y / s, a.Z / s);

        public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vec3 Cross(Vec3 a, Vec3 b) =>
            new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        /// <summary>Angle between two vectors in radians (numerically stable atan2 form).</summary>
        public static double AngleBetween(Vec3 a, Vec3 b) => Math.Atan2(Cross(a, b).Length, Dot(a, b));

        /// <summary>Builds a unit vector from spherical longitude/latitude in radians.</summary>
        public static Vec3 FromSpherical(double lon, double lat, double r = 1.0)
        {
            double cl = Math.Cos(lat);
            return new Vec3(r * cl * Math.Cos(lon), r * cl * Math.Sin(lon), r * Math.Sin(lat));
        }

        /// <summary>Returns longitude in [0, 2pi) and latitude in [-pi/2, pi/2], radians.</summary>
        public void ToSpherical(out double lon, out double lat, out double r)
        {
            r = Length;
            lon = Math.Atan2(Y, X);
            if (lon < 0) lon += 2 * Math.PI;
            double rho = Math.Sqrt(X * X + Y * Y);
            lat = Math.Atan2(Z, rho);
        }

        public void ToSpherical(out double lon, out double lat)
        {
            double r;
            ToSpherical(out lon, out lat, out r);
        }

        public override string ToString() => string.Format("({0:G9}, {1:G9}, {2:G9})", X, Y, Z);
    }

    /// <summary>Row-major 3x3 matrix for rotations (precession, nutation, frame changes).</summary>
    public struct Mat3
    {
        public readonly double M11, M12, M13, M21, M22, M23, M31, M32, M33;

        public Mat3(double m11, double m12, double m13, double m21, double m22, double m23, double m31, double m32, double m33)
        {
            M11 = m11; M12 = m12; M13 = m13;
            M21 = m21; M22 = m22; M23 = m23;
            M31 = m31; M32 = m32; M33 = m33;
        }

        public static readonly Mat3 Identity = new Mat3(1, 0, 0, 0, 1, 0, 0, 0, 1);

        /// <summary>Passive rotation about X (frame rotation, IAU SOFA convention R1).</summary>
        public static Mat3 RotX(double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Mat3(1, 0, 0, 0, c, s, 0, -s, c);
        }

        public static Mat3 RotY(double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Mat3(c, 0, -s, 0, 1, 0, s, 0, c);
        }

        public static Mat3 RotZ(double a)
        {
            double c = Math.Cos(a), s = Math.Sin(a);
            return new Mat3(c, s, 0, -s, c, 0, 0, 0, 1);
        }

        public static Mat3 operator *(Mat3 a, Mat3 b) => new Mat3(
            a.M11 * b.M11 + a.M12 * b.M21 + a.M13 * b.M31, a.M11 * b.M12 + a.M12 * b.M22 + a.M13 * b.M32, a.M11 * b.M13 + a.M12 * b.M23 + a.M13 * b.M33,
            a.M21 * b.M11 + a.M22 * b.M21 + a.M23 * b.M31, a.M21 * b.M12 + a.M22 * b.M22 + a.M23 * b.M32, a.M21 * b.M13 + a.M22 * b.M23 + a.M23 * b.M33,
            a.M31 * b.M11 + a.M32 * b.M21 + a.M33 * b.M31, a.M31 * b.M12 + a.M32 * b.M22 + a.M33 * b.M32, a.M31 * b.M13 + a.M32 * b.M23 + a.M33 * b.M33);

        public static Vec3 operator *(Mat3 m, Vec3 v) => new Vec3(
            m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z,
            m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z,
            m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z);

        public Mat3 Transpose() => new Mat3(M11, M21, M31, M12, M22, M32, M13, M23, M33);
    }
}
