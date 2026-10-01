using System;
using System.Collections.Generic;

namespace XrNetSync
{
    public sealed class RelevancySettings
    {
        public float TickHz = 30;
        /// <summary>Half-angle of the "can see it" cone around the viewer's head yaw (Quest 3 FOV ~110° + turning margin).</summary>
        public float ViewHalfAngleDeg = 70;
        public float NearRadius = 4f;
        public float InViewNearHz = 30;
        public float InViewFarHz = 20;
        public float BehindNearHz = 20;   // someone right behind you can still bump into you
        public float BehindNearRadius = 1.5f;
        public float BehindHz = 10;
        /// <summary>Per-viewer cap on avatar records per tick; the most overdue go first.</summary>
        public int MaxAvatarsPerTick = 64;
    }

    /// <summary>
    /// Per-viewer send-rate scheduling for the host/relay. In a colocated room every player is
    /// "relevant", so instead of culling, each viewer gets full rate for players in front of
    /// them and nearby, and a lower rate for players behind them. A priority accumulator per
    /// (viewer, player) pair spreads the reduced-rate sends evenly across ticks.
    /// </summary>
    public sealed class RelevancyScheduler
    {
        readonly RelevancySettings cfg;
        readonly float[,] acc;
        readonly int players;
        readonly List<(int id, float acc)> due = new List<(int, float)>(64);

        public RelevancyScheduler(int players, RelevancySettings settings = null)
        {
            this.players = players;
            cfg = settings ?? new RelevancySettings();
            acc = new float[players, players];
        }

        public float RateHz(in Pose viewerHead, in Pose subjectHead)
        {
            var to = subjectHead.Position - viewerHead.Position;
            var flatTo = new Vec3(to.X, 0, to.Z);
            float dist = flatTo.Length;
            var fwd = viewerHead.Forward;
            var flatFwd = new Vec3(fwd.X, 0, fwd.Z).Normalized;
            bool inView = dist < 0.3f ||
                Vec3.Dot(flatFwd, flatTo.Normalized) > MathF.Cos(cfg.ViewHalfAngleDeg * MathF.PI / 180f);
            if (inView) return dist < cfg.NearRadius ? cfg.InViewNearHz : cfg.InViewFarHz;
            return dist < cfg.BehindNearRadius ? cfg.BehindNearHz : cfg.BehindHz;
        }

        /// <summary>Players to send to <paramref name="viewer"/> this tick.</summary>
        public void Select(int viewer, AvatarState[] latest, bool[] available, List<int> result)
        {
            result.Clear();
            due.Clear();
            var viewerHead = latest[viewer].Head;
            for (int p = 0; p < players; p++)
            {
                if (p == viewer || !available[p]) continue;
                float a = MathF.Min(acc[viewer, p] + RateHz(viewerHead, latest[p].Head) / cfg.TickHz, 3f);
                acc[viewer, p] = a;
                if (a >= 1f) due.Add((p, a));
            }
            due.Sort((x, y) => y.acc.CompareTo(x.acc));
            for (int i = 0; i < due.Count && result.Count < cfg.MaxAvatarsPerTick; i++)
            {
                result.Add(due[i].id);
                acc[viewer, due[i].id] -= 1f;
            }
        }
    }
}
