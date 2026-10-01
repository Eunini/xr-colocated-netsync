using System;

namespace XrNetSync
{
    // Minimal vector/quaternion types so the library has no UnityEngine dependency.
    // Conventions match Unity: Y up, quaternions are (x, y, z, w).
    public readonly struct Vec3
    {
        public readonly float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 Forward = new Vec3(0, 0, 1);
        public static readonly Vec3 Up = new Vec3(0, 1, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, float s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(float s, Vec3 a) => a * s;

        public float Length => MathF.Sqrt(X * X + Y * Y + Z * Z);
        public static float Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static Vec3 Lerp(Vec3 a, Vec3 b, float t) => a + (b - a) * t;
        public static float Distance(Vec3 a, Vec3 b) => (a - b).Length;

        public Vec3 Normalized
        {
            get { float l = Length; return l > 1e-9f ? this * (1f / l) : Zero; }
        }

        public Vec3 ClampLength(float max)
        {
            float l = Length;
            return l > max ? this * (max / l) : this;
        }

        public override string ToString() => $"({X:0.000}, {Y:0.000}, {Z:0.000})";
    }

    public readonly struct Quat
    {
        public readonly float X, Y, Z, W;
        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        public static readonly Quat Identity = new Quat(0, 0, 0, 1);

        public static Quat operator *(Quat a, Quat b) => new Quat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        public static Vec3 operator *(Quat q, Vec3 v)
        {
            // v' = v + 2w(u x v) + 2u x (u x v)
            var u = new Vec3(q.X, q.Y, q.Z);
            var t = Cross(u, v) * 2f;
            return v + t * q.W + Cross(u, t);
        }

        static Vec3 Cross(Vec3 a, Vec3 b) =>
            new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

        public Quat Conjugate => new Quat(-X, -Y, -Z, W);

        public Quat Normalized
        {
            get
            {
                float l = MathF.Sqrt(X * X + Y * Y + Z * Z + W * W);
                return l > 1e-9f ? new Quat(X / l, Y / l, Z / l, W / l) : Identity;
            }
        }

        public static float Dot(Quat a, Quat b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

        public static Quat AxisAngle(Vec3 axis, float radians)
        {
            var n = axis.Normalized;
            float s = MathF.Sin(radians * 0.5f);
            return new Quat(n.X * s, n.Y * s, n.Z * s, MathF.Cos(radians * 0.5f));
        }

        public static Quat Euler(float yaw, float pitch, float roll) =>
            AxisAngle(Vec3.Up, yaw) * AxisAngle(new Vec3(1, 0, 0), pitch) * AxisAngle(Vec3.Forward, roll);

        /// <summary>Angle in radians between two orientations.</summary>
        public static float Angle(Quat a, Quat b)
        {
            float d = MathF.Min(1f, MathF.Abs(Dot(a.Normalized, b.Normalized)));
            return 2f * MathF.Acos(d);
        }

        public static Quat Slerp(Quat a, Quat b, float t)
        {
            float d = Dot(a, b);
            if (d < 0) { b = new Quat(-b.X, -b.Y, -b.Z, -b.W); d = -d; }
            if (d > 0.9995f)
                return new Quat(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t,
                                a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t).Normalized;
            float th = MathF.Acos(d), s = MathF.Sin(th);
            float wa = MathF.Sin((1 - t) * th) / s, wb = MathF.Sin(t * th) / s;
            return new Quat(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb, a.W * wa + b.W * wb);
        }

        /// <summary>Rotation vector (axis * angle, radians) taking a to b, shortest way.</summary>
        public static Vec3 Delta(Quat a, Quat b)
        {
            var q = b * a.Conjugate;
            if (q.W < 0) q = new Quat(-q.X, -q.Y, -q.Z, -q.W);
            var v = new Vec3(q.X, q.Y, q.Z);
            float s = v.Length;
            if (s < 1e-7f) return v * 2f;
            float angle = 2f * MathF.Atan2(s, q.W);
            return v * (angle / s);
        }

        /// <summary>Applies a rotation vector (axis * angle) on top of q.</summary>
        public static Quat Integrate(Quat q, Vec3 rotationVector)
        {
            float angle = rotationVector.Length;
            if (angle < 1e-7f) return q;
            return (AxisAngle(rotationVector, angle) * q).Normalized;
        }
    }

    public readonly struct Pose
    {
        public readonly Vec3 Position;
        public readonly Quat Rotation;
        public Pose(Vec3 position, Quat rotation) { Position = position; Rotation = rotation; }
        public Vec3 Forward => Rotation * Vec3.Forward;
    }
}
