using System;

namespace XrNetSync
{
    /// <summary>One player's tracked head and controllers, in colocation-anchor space.</summary>
    public struct AvatarState
    {
        public Pose Head, LeftHand, RightHand;
        public bool LeftTracked, RightTracked;
    }

    /// <summary>
    /// A decoded avatar snapshot. <see cref="TimeMs"/> is the sender's sample time on the
    /// shared network clock, so receivers can interpolate/extrapolate against real time.
    /// </summary>
    public struct AvatarSnapshot
    {
        public int PlayerId;
        public double TimeMs;
        public AvatarState State;
    }

    /// <summary>
    /// Packs head + both hands into one bit-packed record (~30 bytes instead of 3 transforms
    /// of 7 floats each plus per-object headers).
    ///
    /// - Head position: anchor space, 1 mm steps, +-16.384 m per axis (15 bits).
    /// - Hand positions: relative to the head, 1 mm steps, +-2.048 m (12 bits).
    ///   Hands never get far from the head, so they need far fewer bits than world positions.
    /// - Rotations: "smallest three" (drop the largest component, send its index + the
    ///   other three, which lie in +-1/sqrt(2)). 11 bits for the head, 10 for hands.
    /// - An untracked hand costs one bit.
    /// - Time: low 16 bits of the shared clock in ms, unwrapped against the receiver's clock.
    /// </summary>
    public static class AvatarCodec
    {
        public const int PlayerIdBits = 6;      // up to 64 players
        public const int TimeBits = 16;
        public const float PosStep = 0.001f;
        public const int HeadPosBits = 15;
        public const int HandPosBits = 12;
        public const int HeadRotBits = 11;
        public const int HandRotBits = 10;

        const float SmallestThreeMax = 0.70710678f;

        public static byte[] Encode(int playerId, double timeMs, in AvatarState s)
        {
            var w = new BitWriter();
            Write(w, playerId, timeMs, s);
            return w.ToArray();
        }

        public static void Write(BitWriter w, int playerId, double timeMs, in AvatarState s)
        {
            w.Write((uint)playerId, PlayerIdBits);
            w.Write((uint)((long)Math.Round(timeMs) & 0xFFFF), TimeBits);
            WriteVec(w, s.Head.Position, HeadPosBits);
            WriteRot(w, s.Head.Rotation, HeadRotBits);
            WriteHand(w, s.LeftTracked, s.LeftHand, s.Head.Position);
            WriteHand(w, s.RightTracked, s.RightHand, s.Head.Position);
        }

        /// <param name="receiverNowMs">Receiver's shared-clock time, used to unwrap the 16-bit stamp.</param>
        public static AvatarSnapshot Decode(byte[] data, double receiverNowMs)
        {
            var r = new BitReader(data);
            return Read(r, receiverNowMs);
        }

        public static AvatarSnapshot Read(BitReader r, double receiverNowMs)
        {
            var snap = new AvatarSnapshot { PlayerId = (int)r.Read(PlayerIdBits) };
            snap.TimeMs = UnwrapTime(r.Read(TimeBits), receiverNowMs);
            var head = new Pose(ReadVec(r, HeadPosBits), ReadRot(r, HeadRotBits));
            var st = new AvatarState { Head = head };
            st.LeftTracked = ReadHand(r, head.Position, out st.LeftHand);
            st.RightTracked = ReadHand(r, head.Position, out st.RightHand);
            snap.State = st;
            return snap;
        }

        /// <summary>Size in bits of an encoded snapshot (both hands tracked).</summary>
        public static int FullSizeBits =>
            PlayerIdBits + TimeBits + 3 * HeadPosBits + 2 + 3 * HeadRotBits
            + 2 * (1 + 3 * HandPosBits + 2 + 3 * HandRotBits);

        public static double UnwrapTime(uint stamp16, double nowMs)
        {
            long now = (long)Math.Round(nowMs);
            long candidate = (now & ~0xFFFFL) | stamp16;
            // pick the candidate closest to now (stamps are always slightly in the past)
            if (candidate - now > 0x8000) candidate -= 0x10000;
            else if (now - candidate > 0x8000) candidate += 0x10000;
            return candidate;
        }

        static void WriteHand(BitWriter w, bool tracked, in Pose hand, Vec3 headPos)
        {
            w.WriteBool(tracked);
            if (!tracked) return;
            WriteVec(w, hand.Position - headPos, HandPosBits);
            WriteRot(w, hand.Rotation, HandRotBits);
        }

        static bool ReadHand(BitReader r, Vec3 headPos, out Pose hand)
        {
            if (!r.ReadBool()) { hand = default; return false; }
            var rel = ReadVec(r, HandPosBits);
            hand = new Pose(headPos + rel, ReadRot(r, HandRotBits));
            return true;
        }

        static void WriteVec(BitWriter w, Vec3 v, int bits)
        {
            w.Write(QuantizePos(v.X, bits), bits);
            w.Write(QuantizePos(v.Y, bits), bits);
            w.Write(QuantizePos(v.Z, bits), bits);
        }

        static Vec3 ReadVec(BitReader r, int bits) =>
            new Vec3(DequantizePos(r.Read(bits), bits), DequantizePos(r.Read(bits), bits), DequantizePos(r.Read(bits), bits));

        static uint QuantizePos(float v, int bits)
        {
            long half = 1L << (bits - 1);
            long q = (long)Math.Round(v / PosStep) + half;
            return (uint)Math.Clamp(q, 0, (1L << bits) - 1);
        }

        static float DequantizePos(uint q, int bits) => ((long)q - (1L << (bits - 1))) * PosStep;

        static void WriteRot(BitWriter w, Quat q, int bits)
        {
            q = q.Normalized;
            Span<float> c = stackalloc float[] { q.X, q.Y, q.Z, q.W };
            int largest = 0;
            for (int i = 1; i < 4; i++)
                if (MathF.Abs(c[i]) > MathF.Abs(c[largest])) largest = i;
            float sign = c[largest] < 0 ? -1f : 1f; // q and -q are the same rotation
            w.Write((uint)largest, 2);
            for (int i = 0; i < 4; i++)
                if (i != largest) w.Write(QuantizeUnit(c[i] * sign, bits), bits);
        }

        static Quat ReadRot(BitReader r, int bits)
        {
            int largest = (int)r.Read(2);
            Span<float> c = stackalloc float[4];
            float sum = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i == largest) continue;
                c[i] = DequantizeUnit(r.Read(bits), bits);
                sum += c[i] * c[i];
            }
            c[largest] = MathF.Sqrt(MathF.Max(0f, 1f - sum));
            return new Quat(c[0], c[1], c[2], c[3]).Normalized;
        }

        static uint QuantizeUnit(float v, int bits)
        {
            uint max = (1u << bits) - 1;
            float t = (Math.Clamp(v, -SmallestThreeMax, SmallestThreeMax) + SmallestThreeMax) / (2 * SmallestThreeMax);
            return (uint)MathF.Round(t * max);
        }

        static float DequantizeUnit(uint q, int bits)
        {
            uint max = (1u << bits) - 1;
            return q / (float)max * (2 * SmallestThreeMax) - SmallestThreeMax;
        }
    }

    /// <summary>
    /// Wire cost of the naive approach: three independent full-precision transforms
    /// (position 3 floats + rotation 4 floats) each with its own per-object header.
    /// The header size is an assumption (object id, behaviour index, message type, tick).
    /// </summary>
    public static class NaiveTransformCost
    {
        public const int PerObjectHeaderBytes = 8;
        public const int TransformBytes = 3 * 4 + 4 * 4;
        public const int TransformsPerAvatar = 3;
        public const int AvatarBytes = TransformsPerAvatar * (PerObjectHeaderBytes + TransformBytes);
    }
}
