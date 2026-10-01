using System;
using System.Collections.Generic;
using XrNetSync;
using Xunit;

public class CodecTests
{
    static AvatarState RandomState(Random r)
    {
        Vec3 V(float range) => new Vec3((float)(r.NextDouble() * 2 - 1) * range, (float)(r.NextDouble() * 2 - 1) * range, (float)(r.NextDouble() * 2 - 1) * range);
        Quat Q() => new Quat((float)r.NextDouble() - 0.5f, (float)r.NextDouble() - 0.5f, (float)r.NextDouble() - 0.5f, (float)r.NextDouble() - 0.5f).Normalized;
        var head = V(8f);
        return new AvatarState
        {
            Head = new Pose(head, Q()),
            LeftHand = new Pose(head + V(0.9f), Q()),
            RightHand = new Pose(head + V(0.9f), Q()),
            LeftTracked = true,
            RightTracked = true,
        };
    }

    [Fact]
    public void RoundTripStaysWithinQuantisationError()
    {
        var r = new Random(1);
        for (int i = 0; i < 5000; i++)
        {
            var s = RandomState(r);
            var d = AvatarCodec.Decode(AvatarCodec.Encode(17, 123456, s), 123480).State;
            Assert.True(Vec3.Distance(s.Head.Position, d.Head.Position) < 0.001f);
            Assert.True(Vec3.Distance(s.LeftHand.Position, d.LeftHand.Position) < 0.0015f);
            Assert.True(Vec3.Distance(s.RightHand.Position, d.RightHand.Position) < 0.0015f);
            Assert.True(Quat.Angle(s.Head.Rotation, d.Head.Rotation) * 180 / MathF.PI < 0.15f);
            Assert.True(Quat.Angle(s.RightHand.Rotation, d.RightHand.Rotation) * 180 / MathF.PI < 0.3f);
        }
    }

    [Fact]
    public void PackedAvatarIs30Bytes()
    {
        var s = RandomState(new Random(2));
        Assert.Equal(30, AvatarCodec.Encode(5, 0, s).Length);
        Assert.Equal(108, NaiveTransformCost.AvatarBytes);
    }

    [Fact]
    public void UntrackedHandCostsOneBit()
    {
        var s = RandomState(new Random(3));
        s.LeftTracked = false;
        var bytes = AvatarCodec.Encode(5, 0, s);
        Assert.Equal(22, bytes.Length);   // 68 fewer bits
        var d = AvatarCodec.Decode(bytes, 0).State;
        Assert.False(d.LeftTracked);
        Assert.True(d.RightTracked);
    }

    [Fact]
    public void PlayerIdAndTimeSurvive16BitWrap()
    {
        var s = RandomState(new Random(4));
        double sent = 65530;            // stamp wraps between send and receive
        var d = AvatarCodec.Decode(AvatarCodec.Encode(31, sent, s), sent + 40);
        Assert.Equal(31, d.PlayerId);
        Assert.Equal(sent, d.TimeMs);
    }
}

public class PredictionTests
{
    static AvatarSnapshot Snap(double t, Vec3 head, Vec3 rightRel)
    {
        var st = new AvatarState
        {
            Head = new Pose(head, Quat.Identity),
            LeftHand = new Pose(head + new Vec3(-0.2f, -0.5f, 0.1f), Quat.Identity),
            RightHand = new Pose(head + rightRel, Quat.Identity),
            LeftTracked = true,
            RightTracked = true,
        };
        return new AvatarSnapshot { PlayerId = 1, TimeMs = t, State = st };
    }

    [Fact]
    public void PredictsRunningPlayerToThePresent()
    {
        // 2 m/s along +Z, snapshots at 30 Hz arriving 60 ms late
        var v = new Vec3(0, 0, 0.002f);
        var pred = new PredictedAvatar();
        var interp = new InterpolatedAvatar(66.7);
        double now = 0;
        for (int i = 0; i < 30; i++)
        {
            double t = i * 33.3;
            var s = Snap(t, new Vec3(0, 1.6f, 0) + v * (float)t, new Vec3(0.2f, -0.2f, 0.4f));
            now = t + 60;
            pred.OnSnapshot(s, now);
            interp.OnSnapshot(s, now);
            pred.Sample(now, out _);
        }
        var truth = new Vec3(0, 1.6f, 0) + v * (float)now;
        pred.Sample(now, out var p);
        interp.Sample(now, out var q);
        float predErr = Vec3.Distance(truth, p.Head.Position);
        float interpErr = Vec3.Distance(truth, q.Head.Position);
        Assert.True(predErr < 0.03f, $"predicted error {predErr}");
        Assert.True(interpErr > 0.12f, $"interpolated error {interpErr}");  // 66.7 ms buffer = 13 cm at 2 m/s
        // hand rides along with the predicted head
        Assert.True(Vec3.Distance(truth + new Vec3(0.2f, -0.2f, 0.4f), p.RightHand.Position) < 0.04f);
    }

    [Fact]
    public void LateSnapshotNeverMovesAvatarBackwards()
    {
        var pred = new PredictedAvatar();
        var v = new Vec3(0.001f, 0, 0);
        for (int i = 0; i < 10; i++) pred.OnSnapshot(Snap(i * 33.3, v * (float)(i * 33.3), Vec3.Zero), i * 33.3 + 10);
        pred.Sample(340, out var before);
        pred.OnSnapshot(Snap(5 * 33.3, Vec3.Zero, Vec3.Zero), 341);   // stale duplicate-ish packet
        pred.Sample(341, out var after);
        Assert.True(after.Head.Position.X >= before.Head.Position.X);
    }

    [Fact]
    public void ExtrapolationIsBoundedDuringOutage()
    {
        var pred = new PredictedAvatar();
        var v = new Vec3(0.002f, 0, 0);   // 2 m/s
        for (int i = 0; i < 10; i++) pred.OnSnapshot(Snap(i * 33.3, v * (float)(i * 33.3), Vec3.Zero), i * 33.3);
        var last = v * (float)(9 * 33.3);
        for (double t = 300; t < 3000; t += 14) pred.Sample(t, out _);
        pred.Sample(3000, out var s);
        // max 250 ms of decayed extrapolation, never 2.7 s worth (5.4 m)
        Assert.True(Vec3.Distance(s.Head.Position, last) < 0.55f);
    }

    [Fact]
    public void CorrectionsAreBlendedNotPopped()
    {
        var pred = new PredictedAvatar();
        for (int i = 0; i < 5; i++) pred.OnSnapshot(Snap(i * 33.3, Vec3.Zero, Vec3.Zero), i * 33.3);
        pred.Sample(140, out var a);
        pred.OnSnapshot(Snap(166.5, new Vec3(0.2f, 0, 0), Vec3.Zero), 150);   // sudden 20 cm move
        pred.Sample(151, out var b);
        Assert.True(Vec3.Distance(a.Head.Position, b.Head.Position) < 0.05f);  // no pop on screen
        // player stays at the new spot
        for (int i = 6; i < 15; i++) pred.OnSnapshot(Snap(i * 33.3, new Vec3(0.2f, 0, 0), Vec3.Zero), i * 33.3);
        for (double t = 152; t < 600; t += 14) pred.Sample(t, out b);
        Assert.True(MathF.Abs(b.Head.Position.X - 0.2f) < 0.01f);           // converged
    }
}

public class RelevancyTests
{
    [Fact]
    public void PlayersInFrontGetFullRateBehindGetReduced()
    {
        const int n = 4;
        var sched = new RelevancyScheduler(n);
        var states = new AvatarState[n];
        Pose At(float x, float z) => new Pose(new Vec3(x, 1.6f, z), Quat.Identity);
        states[0] = new AvatarState { Head = At(0, 0) };      // viewer looking +Z
        states[1] = new AvatarState { Head = At(0, 2) };      // in front, near: 30 Hz
        states[2] = new AvatarState { Head = At(0, -5) };     // behind, far: 10 Hz
        states[3] = new AvatarState { Head = At(0, 8) };      // in front, far: 20 Hz
        var has = new[] { true, true, true, true };
        var counts = new int[n];
        var sel = new List<int>();
        for (int tick = 0; tick < 300; tick++)
        {
            sched.Select(0, states, has, sel);
            foreach (var p in sel) counts[p]++;
        }
        Assert.Equal(300, counts[1]);
        Assert.InRange(counts[2], 99, 101);
        Assert.InRange(counts[3], 199, 201);
        Assert.Equal(0, counts[0]);
    }
}
