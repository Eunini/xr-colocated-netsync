using System;
using System.Collections.Generic;

namespace XrNetSync
{
    public interface IRemoteAvatar
    {
        void OnSnapshot(in AvatarSnapshot snap, double arrivalMs);
        /// <summary>Pose to render at <paramref name="nowMs"/> (shared clock). False until the first snapshot.</summary>
        bool Sample(double nowMs, out AvatarState state);
    }

    /// <summary>
    /// Baseline: classic buffered interpolation. Renders a fixed delay in the past and lerps
    /// between the two snapshots around that time; holds the newest pose if the buffer runs dry.
    /// Good for VR where nobody can see the real player, but in colocated MR the avatar
    /// visibly trails the real person you see through passthrough.
    /// </summary>
    public sealed class InterpolatedAvatar : IRemoteAvatar
    {
        readonly List<AvatarSnapshot> buffer = new List<AvatarSnapshot>(16);
        readonly double delayMs;

        public InterpolatedAvatar(double delayMs) { this.delayMs = delayMs; }

        public void OnSnapshot(in AvatarSnapshot snap, double arrivalMs)
        {
            int i = buffer.Count;
            while (i > 0 && buffer[i - 1].TimeMs > snap.TimeMs) i--;
            if (i > 0 && buffer[i - 1].TimeMs == snap.TimeMs) return;
            buffer.Insert(i, snap);
            if (buffer.Count > 32) buffer.RemoveAt(0);
        }

        public bool Sample(double nowMs, out AvatarState state)
        {
            state = default;
            if (buffer.Count == 0) return false;
            double t = nowMs - delayMs;
            if (t >= buffer[buffer.Count - 1].TimeMs) { state = buffer[buffer.Count - 1].State; return true; }
            if (t <= buffer[0].TimeMs) { state = buffer[0].State; return true; }
            int b = 1;
            while (buffer[b].TimeMs < t) b++;
            var A = buffer[b - 1]; var B = buffer[b];
            float u = (float)((t - A.TimeMs) / (B.TimeMs - A.TimeMs));
            state = AvatarMath.Lerp(A.State, B.State, u);
            return true;
        }
    }

    /// <summary>Tuning for <see cref="PredictedAvatar"/>.</summary>
    public sealed class PredictionSettings
    {
        /// <summary>Velocity is measured over at least this window to keep quantisation noise down.</summary>
        public double VelocityWindowMs = 30;
        /// <summary>
        /// Weight of the newest velocity sample (0..1). Lower = smoother, laggier. 1.0 was best on
        /// the simulated motion; noisy real tracking data may want 0.6-0.8.
        /// </summary>
        public float VelocityBlend = 1f;
        /// <summary>
        /// Extrapolation fades out with this time constant. The head (and the body it carries)
        /// moves smoothly; the arm motion relative to the head doesn't, so it gets a shorter one.
        /// </summary>
        public float HeadExtrapTauMs = 700;
        public float HandExtrapTauMs = 60;
        /// <summary>Controller rotation (gun aim) is steadier than controller position.</summary>
        public float HandRotExtrapTauMs = 150;
        /// <summary>No extrapolation past this age; the avatar holds instead of flying off.</summary>
        public double MaxExtrapolationMs = 250;
        /// <summary>Corrections are blended away with this time constant instead of popping.</summary>
        public float CorrectionTauMs = 45;
        /// <summary>Corrections bigger than this snap (teleport, re-anchor).</summary>
        public float SnapDistance = 0.75f;
    }

    /// <summary>
    /// Colocated-MR receiver: predicts every remote head/hand to the present so the avatar
    /// sits on the real person seen through passthrough, instead of trailing ~100 ms behind.
    ///
    /// 1. Velocity and angular velocity are estimated per part from the newest snapshots;
    ///    hands are tracked relative to the head (in its yaw frame).
    /// 2. The newest pose is extrapolated by (now - sample time), with a decaying gain so an
    ///    old or noisy velocity can't throw a hand across the room: the head (and the body it
    ///    carries) gets a long horizon, the arm's own motion a short one. Out-of-order/late
    ///    snapshots never move it backwards.
    /// 3. When new data changes the prediction, the visual jump is kept as an offset and
    ///    decayed over ~45 ms, so corrections are smooth.
    /// </summary>
    public sealed class PredictedAvatar : IRemoteAvatar
    {
        readonly PredictionSettings cfg;
        readonly List<AvatarSnapshot> history = new List<AvatarSnapshot>(8);
        readonly Vec3[] vel = new Vec3[3];      // m/ms; head in world space, hands head-relative
        readonly Vec3[] angVel = new Vec3[3];   // rad/ms as rotation vector
        readonly Vec3[] posOffset = new Vec3[3];
        readonly Vec3[] rotOffset = new Vec3[3];
        double lastSampleMs;
        bool hasRendered;

        public PredictedAvatar(PredictionSettings settings = null) { cfg = settings ?? new PredictionSettings(); }

        public double NewestSampleMs => history.Count > 0 ? history[history.Count - 1].TimeMs : double.NaN;

        public void OnSnapshot(in AvatarSnapshot snap, double arrivalMs)
        {
            if (history.Count > 0 && snap.TimeMs <= history[history.Count - 1].TimeMs)
                return; // late or duplicate: newer data already drives the prediction

            // what we would have shown at arrival with the old data
            bool blend = hasRendered && history.Count > 0;
            AvatarState before = blend ? Predict(arrivalMs) : default;

            // velocity from the newest snapshot that is at least VelocityWindowMs older
            for (int i = history.Count - 1; i >= 0; i--)
            {
                double dt = snap.TimeMs - history[i].TimeMs;
                if (dt < cfg.VelocityWindowMs && i > 0) continue;
                if (dt <= 0) break;
                for (int k = 0; k < 3; k++) UpdateVelocity(k, history[i].State, snap.State, dt);
                // a gap that long means the old velocity no longer describes the motion
                if (dt > 200) for (int k = 0; k < 3; k++) { vel[k] = Vec3.Zero; angVel[k] = Vec3.Zero; }
                break;
            }

            history.Add(snap);
            if (history.Count > 8) history.RemoveAt(0);

            if (blend)
            {
                // keep what is on screen and blend the difference away instead of popping
                var after = Predict(arrivalMs);
                Capture(0, before.Head, after.Head);
                Capture(1, before.LeftHand, after.LeftHand);
                Capture(2, before.RightHand, after.RightHand);
            }
        }

        void UpdateVelocity(int k, in AvatarState a, in AvatarState b, double dtMs)
        {
            // head: world space. hands: relative to the head in the head's yaw frame, so a
            // running or turning player carries their hands along with the (smooth) head
            // prediction and only the arm's own motion is extrapolated with the short gain.
            Pose pa = Part(a, k), pb = Part(b, k);
            Vec3 xa = k == 0 ? pa.Position : HandLocal(a.Head, pa.Position);
            Vec3 xb = k == 0 ? pb.Position : HandLocal(b.Head, pb.Position);
            var v = (xb - xa) * (float)(1.0 / dtMs);
            var w = Quat.Delta(pa.Rotation, pb.Rotation) * (float)(1.0 / dtMs);
            vel[k] = Vec3.Lerp(vel[k], v, cfg.VelocityBlend);
            angVel[k] = Vec3.Lerp(angVel[k], w, cfg.VelocityBlend);
        }

        static Pose Part(in AvatarState s, int k) => k == 0 ? s.Head : k == 1 ? s.LeftHand : s.RightHand;

        static Quat Yaw(Quat headRotation)
        {
            var f = headRotation * Vec3.Forward;
            return Quat.AxisAngle(Vec3.Up, MathF.Atan2(f.X, f.Z));
        }

        static Vec3 HandLocal(in Pose head, Vec3 handPos) => Yaw(head.Rotation).Conjugate * (handPos - head.Position);

        public bool Sample(double nowMs, out AvatarState state)
        {
            state = default;
            if (history.Count == 0) return false;
            if (hasRendered)
            {
                float decay = MathF.Exp(-(float)Math.Max(0, nowMs - lastSampleMs) / cfg.CorrectionTauMs);
                for (int k = 0; k < 3; k++) { posOffset[k] *= decay; rotOffset[k] *= decay; }
            }
            var target = Predict(nowMs);
            state = target;
            state.Head = Apply(0, target.Head);
            state.LeftHand = Apply(1, target.LeftHand);
            state.RightHand = Apply(2, target.RightHand);
            lastSampleMs = nowMs;
            hasRendered = true;
            return true;
        }

        /// <summary>Newest snapshot extrapolated to <paramref name="nowMs"/>, without correction smoothing.</summary>
        AvatarState Predict(double nowMs)
        {
            var newest = history[history.Count - 1];
            double age = Math.Clamp(nowMs - newest.TimeMs, 0, cfg.MaxExtrapolationMs);
            var target = newest.State;
            target.Head = Extrapolate(0, newest.State.Head, age, cfg.HeadExtrapTauMs);
            target.LeftHand = ExtrapolateHand(1, newest.State, target.Head, age);
            target.RightHand = ExtrapolateHand(2, newest.State, target.Head, age);
            return target;
        }

        Pose Extrapolate(int k, in Pose p, double ageMs, float tauMs)
        {
            // integral of a velocity decaying with tau: tau * (1 - e^(-t/tau))
            float t = tauMs * (1f - MathF.Exp(-(float)ageMs / tauMs));
            return new Pose(p.Position + vel[k] * t, Quat.Integrate(p.Rotation, angVel[k] * t));
        }

        Pose ExtrapolateHand(int k, in AvatarState newest, in Pose predictedHead, double ageMs)
        {
            var hand = Part(newest, k);
            float t = cfg.HandExtrapTauMs * (1f - MathF.Exp(-(float)ageMs / cfg.HandExtrapTauMs));
            var local = (HandLocal(newest.Head, hand.Position) + vel[k] * t).ClampLength(1.5f);
            var pos = predictedHead.Position + Yaw(predictedHead.Rotation) * local;
            float tr = cfg.HandRotExtrapTauMs * (1f - MathF.Exp(-(float)ageMs / cfg.HandRotExtrapTauMs));
            return new Pose(pos, Quat.Integrate(hand.Rotation, angVel[k] * tr));
        }

        void Capture(int k, in Pose before, in Pose after)
        {
            var dp = before.Position - after.Position;
            if (dp.Length > cfg.SnapDistance) { posOffset[k] = Vec3.Zero; rotOffset[k] = Vec3.Zero; return; }
            posOffset[k] += dp;
            rotOffset[k] += Quat.Delta(after.Rotation, before.Rotation);
        }

        Pose Apply(int k, in Pose target) =>
            new Pose(target.Position + posOffset[k], Quat.Integrate(target.Rotation, rotOffset[k]));
    }

    public static class AvatarMath
    {
        public static Pose Lerp(in Pose a, in Pose b, float t) =>
            new Pose(Vec3.Lerp(a.Position, b.Position, t), Quat.Slerp(a.Rotation, b.Rotation, t));

        public static AvatarState Lerp(in AvatarState a, in AvatarState b, float t) => new AvatarState
        {
            Head = Lerp(a.Head, b.Head, t),
            LeftHand = Lerp(a.LeftHand, b.LeftHand, t),
            RightHand = Lerp(a.RightHand, b.RightHand, t),
            LeftTracked = t < 0.5f ? a.LeftTracked : b.LeftTracked,
            RightTracked = t < 0.5f ? a.RightTracked : b.RightTracked,
        };
    }
}
