using System;
using XrNetSync;

namespace Sim
{
    /// <summary>
    /// Synthetic laser-tag player: walks/sprints between cover points, stops to fight
    /// (strafe, aim at an opponent, fire bursts with recoil), crouches, scans. Head and
    /// hands are driven by damped springs so velocities/accelerations look like tracked data:
    /// fast head saccades, hand flicks while aiming, arm swing while walking.
    /// </summary>
    sealed class PlayerMotion
    {
        enum Mode { Move, Fight, CrouchFight, Scan, Hold }

        public const float ArenaHalf = 6f;
        readonly Random rng;
        readonly int id;
        readonly bool holder; // the filmed viewpoint holds a position at the edge

        Mode mode;
        float modeTimer, speed;
        Vec3 bodyPos, bodyVel, waypoint;
        int target;
        float yaw, yawVel, pitch, pitchVel, height = 1.65f, heightVel, walkPhase, scanBase;
        Vec3 rPos, rVel, lPos, lVel;
        Quat rRot = Quat.Identity, lRot = Quat.Identity;
        float recoil, recoilVel, shotTimer, burstLeft, gestureTimer;

        public AvatarState State;

        public PlayerMotion(int id, Random rng, bool holder)
        {
            this.id = id; this.rng = rng; this.holder = holder;
            bodyPos = holder ? new Vec3(-5.2f, 0, -5.2f) : RandomPoint();
            yaw = (float)(rng.NextDouble() * Math.PI * 2);
            scanBase = MathF.PI / 4; // towards arena centre for the holder
            rPos = bodyPos + new Vec3(0.2f, 1.1f, 0); lPos = bodyPos + new Vec3(-0.2f, 1.1f, 0);
            mode = holder ? Mode.Hold : Mode.Move;
            waypoint = RandomPoint(); speed = 1.5f;
            modeTimer = (float)rng.NextDouble() * 2;
        }

        Vec3 RandomPoint() => new Vec3(F(-ArenaHalf + 0.5f, ArenaHalf - 0.5f), 0, F(-ArenaHalf + 0.5f, ArenaHalf - 0.5f));
        float F(float a, float b) => a + (float)rng.NextDouble() * (b - a);

        static float WrapAngle(float a)
        {
            while (a > MathF.PI) a -= 2 * MathF.PI;
            while (a < -MathF.PI) a += 2 * MathF.PI;
            return a;
        }

        static void Spring(ref float x, ref float v, float target, float omega, float zeta, float dt)
        {
            v += (omega * omega * (target - x) - 2 * zeta * omega * v) * dt;
            x += v * dt;
        }

        static void Spring(ref Vec3 x, ref Vec3 v, Vec3 target, float omega, float zeta, float dt)
        {
            v += ((target - x) * (omega * omega) - v * (2 * zeta * omega)) * dt;
            x += v * dt;
        }

        static Quat Look(Vec3 dir)
        {
            dir = dir.Normalized;
            float yaw = MathF.Atan2(dir.X, dir.Z);
            float pitch = -MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
            return Quat.Euler(yaw, pitch, 0);
        }

        void PickMode(PlayerMotion[] all)
        {
            if (holder)
            {
                mode = Mode.Hold; modeTimer = F(2, 4);
                waypoint = new Vec3(-5.2f + F(-0.4f, 0.4f), 0, -5.2f + F(-0.4f, 0.4f));
                target = PickTarget(all);
                return;
            }
            double r = rng.NextDouble();
            mode = r < 0.42 ? Mode.Move : r < 0.77 ? Mode.Fight : r < 0.9 ? Mode.CrouchFight : Mode.Scan;
            modeTimer = mode == Mode.Move ? F(1.5f, 3.5f) : F(1.2f, 3f);
            if (mode == Mode.Move) { waypoint = RandomPoint(); speed = F(1.1f, 2.6f); }
            else { waypoint = bodyPos + new Vec3(F(-0.7f, 0.7f), 0, F(-0.7f, 0.7f)); speed = 0.7f; }
            target = PickTarget(all);
            scanBase = yaw;
        }

        int PickTarget(PlayerMotion[] all)
        {
            int t;
            do t = rng.Next(all.Length); while (t == id);
            return t;
        }

        public void Step(float dt, float time, PlayerMotion[] all)
        {
            modeTimer -= dt;
            if (modeTimer <= 0) PickMode(all);

            // body
            var to = waypoint - bodyPos; to = new Vec3(to.X, 0, to.Z);
            float dist = to.Length;
            var desired = dist > 0.05f ? to.Normalized * MathF.Min(speed, dist * 2.5f) : Vec3.Zero;
            var dv = (desired - bodyVel).ClampLength(5.5f * dt);
            bodyVel += dv;
            bodyPos += bodyVel * dt;
            bodyPos = new Vec3(Math.Clamp(bodyPos.X, -ArenaHalf, ArenaHalf), 0, Math.Clamp(bodyPos.Z, -ArenaHalf, ArenaHalf));
            float groundSpeed = bodyVel.Length;

            // head
            var targetHead = all[target].State.Head.Position;
            var headPos0 = bodyPos + new Vec3(0, height, 0);
            var toTarget = targetHead - headPos0;
            bool fighting = mode == Mode.Fight || mode == Mode.CrouchFight || mode == Mode.Hold;
            float desiredYaw = yaw, desiredPitch = -0.08f;
            if (fighting)
            {
                desiredYaw = MathF.Atan2(toTarget.X, toTarget.Z);
                desiredPitch = -MathF.Atan2(toTarget.Y, new Vec3(toTarget.X, 0, toTarget.Z).Length);
                if (mode == Mode.Hold)
                {
                    // the filmed viewpoint watches the action: centre of the other players, slow sweep
                    var c = Vec3.Zero;
                    foreach (var o in all) if (o != this) c += o.State.Head.Position;
                    c = c * (1f / (all.Length - 1)) - headPos0;
                    desiredYaw = MathF.Atan2(c.X, c.Z) + 0.35f * MathF.Sin(time * 0.3f);
                }
            }
            else if (mode == Mode.Move && groundSpeed > 0.3f) desiredYaw = MathF.Atan2(bodyVel.X, bodyVel.Z) + 0.35f * MathF.Sin(time * 1.3f + id);
            else if (mode == Mode.Scan) desiredYaw = scanBase + 1.2f * MathF.Sin(time * 1.6f + id);
            float yawErr = WrapAngle(desiredYaw - yaw);
            float y2 = yaw + yawErr;
            Spring(ref yaw, ref yawVel, y2, mode == Mode.Hold ? 4f : 13f, 0.9f, dt);
            yaw = WrapAngle(yaw);
            Spring(ref pitch, ref pitchVel, Math.Clamp(desiredPitch, -0.6f, 0.6f), 10f, 0.9f, dt);

            float desiredHeight = mode == Mode.CrouchFight ? 1.12f : 1.65f;
            Spring(ref height, ref heightVel, desiredHeight, 6f, 1f, dt);
            walkPhase += dt * 2 * MathF.PI * 1.9f * MathF.Min(groundSpeed / 1.4f, 1.4f);
            float bobAmp = MathF.Min(groundSpeed / 2f, 1f);
            var headPos = bodyPos + new Vec3(0, height + 0.025f * bobAmp * MathF.Sin(walkPhase * 2), 0);
            var headRot = Quat.Euler(yaw, pitch, 0.04f * bobAmp * MathF.Sin(walkPhase));
            var yawRot = Quat.AxisAngle(Vec3.Up, yaw);
            var aimRot = Quat.Euler(yaw, pitch, 0);

            // right hand: gun
            bool aiming = fighting || (mode == Mode.Move && groundSpeed < 1.6f);
            Vec3 rTarget = aiming
                ? headPos + aimRot * new Vec3(0.17f, -0.21f, 0.42f)
                : headPos + yawRot * new Vec3(0.24f, -0.52f, 0.22f + 0.05f * MathF.Sin(walkPhase));
            Spring(ref rPos, ref rVel, rTarget, 17f, 0.72f, dt);

            // fire bursts with recoil while fighting
            shotTimer -= dt;
            if (fighting && shotTimer <= 0)
            {
                if (burstLeft <= 0) { burstLeft = rng.Next(2, 7); shotTimer = F(0.3f, 1.2f); }
                else { burstLeft--; shotTimer = 0.11f; recoilVel += 2.2f; rVel += yawRot * new Vec3(0, 0.25f, -0.45f); }
            }
            Spring(ref recoil, ref recoilVel, 0, 22f, 0.6f, dt);
            var aimPoint = fighting ? targetHead : rPos + yawRot * new Vec3(0, -0.6f, 1f);
            var rDesired = Look(aimPoint - rPos) * Quat.AxisAngle(new Vec3(1, 0, 0), -recoil * 0.12f)
                         * Quat.Euler(0.03f * MathF.Sin(time * 7.1f + id), 0.03f * MathF.Sin(time * 5.3f + id * 2), 0);
            rRot = Quat.Slerp(rRot, rDesired, 1 - MathF.Exp(-dt * 22f));

            // left hand: arm swing, relaxed, or a quick look at the wrist HUD
            gestureTimer -= dt;
            if (gestureTimer < -F(4f, 12f)) gestureTimer = F(0.6f, 1.2f);
            Vec3 lLocal = gestureTimer > 0
                ? new Vec3(-0.12f, -0.18f, 0.38f)
                : fighting ? new Vec3(-0.22f, -0.5f, 0.18f)
                : new Vec3(-0.24f, -0.6f, 0.02f + 0.16f * bobAmp * MathF.Sin(walkPhase));
            Spring(ref lPos, ref lVel, headPos + yawRot * lLocal, 12f, 0.8f, dt);
            var lDesired = yawRot * Quat.Euler(0, gestureTimer > 0 ? -0.9f : 0.6f, -0.4f);
            lRot = Quat.Slerp(lRot, lDesired, 1 - MathF.Exp(-dt * 10f));

            State = new AvatarState
            {
                Head = new Pose(headPos, headRot),
                LeftHand = new Pose(lPos, lRot),
                RightHand = new Pose(rPos, rRot),
                LeftTracked = true,
                RightTracked = true,
            };
        }
    }
}
