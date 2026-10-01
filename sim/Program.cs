using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using XrNetSync;

namespace Sim
{
    /// <summary>Venue Wi-Fi phase. All numbers are assumptions, printed in the report and the video.</summary>
    sealed record Phase(string Name, double Start, double End, double BaseLatMs, double JitterMeanMs,
                        double Loss, double HostUplinkMbps, bool Bursts);

    sealed class Wifi
    {
        public static readonly Phase[] Phases =
        {
            new("Normal Wi-Fi",                       0, 20, 3, 2,  0.005, 40, false),
            new("Busy venue Wi-Fi",                  20, 38, 4, 10, 0.03,  15, false),
            new("Busy + interference bursts",        38, 50, 4, 10, 0.03,  15, true),
            new("Recovered",                         50, 65, 3, 2,  0.005, 40, false),
        };
        readonly List<(double s, double e)> bursts = new();
        readonly Random rng;

        public Wifi(Random rng)
        {
            this.rng = rng;
            var p = Phases.First(x => x.Bursts);
            for (double t = p.Start + 0.4; t < p.End;)
            {
                double len = 0.15 + rng.NextDouble() * 0.25;
                bursts.Add((t * 1000, (t + len) * 1000));
                t += len + 1.0 + rng.NextDouble() * 1.4;
            }
        }

        public static Phase At(double ms) => Phases.FirstOrDefault(p => ms < p.End * 1000) ?? Phases[^1];

        public bool InBurst(double ms) => bursts.Any(b => ms >= b.s && ms < b.e);

        /// <summary>One air hop: latency sample, or null if the packet is lost.</summary>
        public double? Hop(double ms)
        {
            var p = At(ms);
            double loss = InBurst(ms) ? 0.85 : p.Loss;
            if (rng.NextDouble() < loss) return null;
            double jitter = -p.JitterMeanMs * Math.Log(1 - rng.NextDouble());
            if (p.JitterMeanMs > 5 && rng.NextDouble() < 0.01) jitter += 30 + rng.NextDouble() * 50; // retry storms
            return p.BaseLatMs + jitter;
        }
    }

    /// <summary>One way of putting avatars on the wire, relayed by the host (player 0, like NGO DAHost).</summary>
    sealed class Pipeline
    {
        public readonly string Name;
        public readonly bool Packed;
        public readonly Wifi Wifi;
        public readonly AvatarSnapshot[] HostLatest;
        public readonly bool[] HostHas;
        public readonly IRemoteAvatar[][,] Receivers; // [receiverKind][viewer, subject]
        public readonly RelevancyScheduler Scheduler;
        public double QueueFreeAt;
        // per-second counters
        public readonly Dictionary<int, Counters> PerSecond = new();

        public sealed class Counters
        {
            public double HostBytes, UplinkBytes, QueueDelaySum, AvatarRecords;
            public int HostPackets, QueueDrops, AirLost, Delivered;
        }

        public Pipeline(string name, bool packed, int n, Random rng)
        {
            Name = name; Packed = packed; Wifi = new Wifi(rng);
            HostLatest = new AvatarSnapshot[n]; HostHas = new bool[n];
            Receivers = new IRemoteAvatar[2][,];
            Receivers[0] = new IRemoteAvatar[n, n];
            Receivers[1] = new IRemoteAvatar[n, n];
            for (int v = 0; v < n; v++)
                for (int s = 0; s < n; s++)
                {
                    Receivers[0][v, s] = new InterpolatedAvatar(2 * 1000.0 / 30);
                    Receivers[1][v, s] = new PredictedAvatar(Tuning.Prediction());
                }
            if (packed) Scheduler = new RelevancyScheduler(n);
        }

        public Counters At(double ms)
        {
            int k = (int)(ms / 1000);
            if (!PerSecond.TryGetValue(k, out var c)) PerSecond[k] = c = new Counters();
            return c;
        }
    }

    /// <summary>Dev-only overrides (XRNS_*) for tuning sweeps; defaults are the library defaults.</summary>
    static class Tuning
    {
        static float F(string k, float d) => float.TryParse(Environment.GetEnvironmentVariable(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
        public static double DurationMs => F("XRNS_DURATION_MS", 65000);
        public static PredictionSettings Prediction()
        {
            var s = new PredictionSettings();
            s.HeadExtrapTauMs = F("XRNS_HEAD_TAU", s.HeadExtrapTauMs);
            s.HandExtrapTauMs = F("XRNS_HAND_TAU", s.HandExtrapTauMs);
            s.HandRotExtrapTauMs = F("XRNS_HANDROT_TAU", s.HandRotExtrapTauMs);
            s.VelocityBlend = F("XRNS_VEL_BLEND", s.VelocityBlend);
            s.VelocityWindowMs = F("XRNS_VEL_WINDOW", (float)s.VelocityWindowMs);
            s.CorrectionTauMs = F("XRNS_CORR_TAU", s.CorrectionTauMs);
            return s;
        }
    }

    abstract record Ev(double Time);
    sealed record UplinkEv(double Time, Pipeline P, int Player, byte[] Bytes, AvatarSnapshot Snap) : Ev(Time);
    sealed record DownlinkEv(double Time, Pipeline P, int Viewer, List<byte[]> Bytes, List<AvatarSnapshot> Snaps) : Ev(Time);

    static class Program
    {
        const int N = 32;
        const double TickMs = 1000.0 / 30;
        const double RenderMs = 1000.0 / 72;
        const double FrameMs = 1000.0 / 30;
        static readonly double DurationMs = Tuning.DurationMs;
        const double TraceStartMs = 5000;
        const int UdpIpHeader = 28;
        const int MaxPayload = 1200;
        const double MaxQueueMs = 250;
        const int FilmedViewer = 1;
        static readonly string[] ReceiverNames = { "interp", "predict" };

        static int Main(string[] args)
        {
            string outDir = args.Length > 0 ? args[0] : "out";
            string tracePath = args.Length > 1 ? args[1] : null;
            Directory.CreateDirectory(outDir);
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            var rng = new Random(7);
            var players = new PlayerMotion[N];
            for (int i = 0; i < N; i++) players[i] = new PlayerMotion(i, new Random(100 + i), i == FilmedViewer);
            var pipes = new[]
            {
                new Pipeline("naive", false, N, new Random(11)),
                new Pipeline("optimized", true, N, new Random(11)),
            };
            var tickPhase = Enumerable.Range(0, N).Select(_ => rng.NextDouble() * TickMs).ToArray();
            double hostPhase = rng.NextDouble() * TickMs;
            var nextTick = tickPhase.ToArray();
            double nextHost = hostPhase, nextRender = 0, nextFrame = TraceStartMs;
            var queue = new PriorityQueue<Ev, (double, long)>();
            long seq = 0;
            var stats = new ErrorStats();
            var trace = tracePath != null ? new TraceWriter(tracePath) : null;
            var rendered = new AvatarState[2, N];    // filmed viewer's latest render (naive/interp, optimized/predict)
            var renderedOk = new bool[2, N];
            var selection = new List<int>();
            var order = Enumerable.Range(1, N - 1).ToArray();
            var relayRng = new Random(3);
            // shared network clocks are never perfect: each headset is off by up to +-5 ms
            var clockErr = Enumerable.Range(0, N).Select(_ => (rng.NextDouble() * 2 - 1) * 5).ToArray();

            for (double t = 0; t <= DurationMs; t += 1)
            {
                // ground truth: tracked poses
                for (int i = 0; i < N; i++) players[i].Step(0.001f, (float)(t / 1000), players);

                // clients send their own avatar (host's own state is local)
                for (int i = 0; i < N; i++)
                {
                    if (t < nextTick[i]) continue;
                    nextTick[i] += TickMs;
                    var st = players[i].State;
                    foreach (var p in pipes)
                    {
                        var snap = new AvatarSnapshot { PlayerId = i, TimeMs = t, State = st };
                        byte[] bytes = p.Packed ? AvatarCodec.Encode(i, t, st) : null;
                        int size = p.Packed ? bytes.Length : NaiveTransformCost.AvatarBytes;
                        if (i == 0) { HostStore(p, p.Packed ? AvatarCodec.Decode(bytes, t) : snap); continue; }
                        p.At(t).UplinkBytes += size + UdpIpHeader;
                        var lat = p.Wifi.Hop(t);
                        if (lat == null) { p.At(t).AirLost++; continue; }
                        queue.Enqueue(new UplinkEv(t + lat.Value, p, i, bytes, snap), (t + lat.Value, seq++));
                    }
                }

                // host relays to every other player
                if (t >= nextHost)
                {
                    nextHost += TickMs;
                    var phase = Wifi.At(t);
                    double bytesPerMs = phase.HostUplinkMbps * 1e6 / 8 / 1000;
                    foreach (var p in pipes)
                    {
                        var c = p.At(t);
                        // the OS doesn't serve viewers in a fixed order, so neither do we
                        for (int k = order.Length - 1; k > 0; k--) { int j = relayRng.Next(k + 1); (order[k], order[j]) = (order[j], order[k]); }
                        foreach (int v in order)
                        {
                            if (p.Packed)
                            {
                                var latestStates = p.HostLatest.Select(s => s.State).ToArray();
                                p.Scheduler.Select(v, latestStates, p.HostHas, selection);
                            }
                            else
                            {
                                selection.Clear();
                                for (int s = 0; s < N; s++) if (s != v && p.HostHas[s]) selection.Add(s);
                            }
                            // split into MTU-sized packets
                            var pktBytes = new List<byte[]>(); var pktSnaps = new List<AvatarSnapshot>(); int pktSize = 4;
                            void Flush()
                            {
                                if (pktSnaps.Count == 0) return;
                                SendDown(p, v, t, pktSize, pktBytes, pktSnaps, bytesPerMs, c, queue, ref seq);
                                pktBytes = new List<byte[]>(); pktSnaps = new List<AvatarSnapshot>(); pktSize = 4;
                            }
                            foreach (var s in selection)
                            {
                                var snap = p.HostLatest[s];
                                byte[] b = p.Packed ? AvatarCodec.Encode(s, snap.TimeMs, snap.State) : null;
                                int size = p.Packed ? b.Length : NaiveTransformCost.AvatarBytes;
                                if (pktSize + size > MaxPayload) Flush();
                                pktBytes.Add(b); pktSnaps.Add(snap); pktSize += size;
                                c.AvatarRecords++;
                            }
                            Flush();
                        }
                    }
                }

                // deliveries
                while (queue.Count > 0 && queue.Peek().Time <= t)
                {
                    switch (queue.Dequeue())
                    {
                        case UplinkEv u:
                            HostStore(u.P, u.P.Packed ? AvatarCodec.Decode(u.Bytes, u.Time) : u.Snap);
                            break;
                        case DownlinkEv d:
                            d.P.At(d.Time).Delivered++;
                            for (int k = 0; k < d.Snaps.Count; k++)
                            {
                                var snap = d.P.Packed ? AvatarCodec.Decode(d.Bytes[k], d.Time + clockErr[d.Viewer]) : d.Snaps[k];
                                for (int r = 0; r < 2; r++) d.P.Receivers[r][d.Viewer, snap.PlayerId].OnSnapshot(snap, d.Time + clockErr[d.Viewer]);
                            }
                            break;
                    }
                }

                // headsets render at 72 Hz; measure what each viewer sees against the real person
                if (t >= nextRender)
                {
                    nextRender += RenderMs;
                    var phase = Wifi.At(t);
                    for (int pi = 0; pi < pipes.Length; pi++)
                        for (int r = 0; r < 2; r++)
                            for (int v = 1; v < N; v++)
                            {
                                var viewerHead = players[v].State.Head;
                                for (int s = 0; s < N; s++)
                                {
                                    if (s == v) continue;
                                    if (!pipes[pi].Receivers[r][v, s].Sample(t + clockErr[v], out var shown)) continue;
                                    if (v == FilmedViewer && ((pi == 0 && r == 0) || (pi == 1 && r == 1)))
                                    { rendered[pi, s] = shown; renderedOk[pi, s] = true; }
                                    if (t < 2000 || !Visible(viewerHead, players[s].State.Head)) continue;
                                    stats.Add(pi * 2 + r, phase.Name, (int)(t / 1000), players[s].State, shown);
                                }
                            }
                }

                if (trace != null && t >= nextFrame && t < DurationMs)
                {
                    nextFrame += FrameMs;
                    trace.Frame(t, players, rendered, renderedOk);
                }
            }

            var report = BuildReport(pipes, stats);
            File.WriteAllText(Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(outDir, "summary.md"), report.Markdown);
            Console.WriteLine(report.Markdown);
            trace?.Finish(report);
            return 0;
        }

        static void HostStore(Pipeline p, AvatarSnapshot snap)
        {
            if (p.HostHas[snap.PlayerId] && p.HostLatest[snap.PlayerId].TimeMs >= snap.TimeMs) return;
            p.HostLatest[snap.PlayerId] = snap;
            p.HostHas[snap.PlayerId] = true;
        }

        static void SendDown(Pipeline p, int viewer, double t, int payload, List<byte[]> bytes, List<AvatarSnapshot> snaps,
                             double bytesPerMs, Pipeline.Counters c, PriorityQueue<Ev, (double, long)> queue, ref long seq)
        {
            int wire = payload + UdpIpHeader;
            c.HostBytes += wire; c.HostPackets++;
            // the host headset's Wi-Fi uplink is a FIFO with limited airtime; drop-tail past 250 ms
            double start = Math.Max(t, p.QueueFreeAt);
            if (start - t > MaxQueueMs) { c.QueueDrops++; return; }
            p.QueueFreeAt = start + wire / bytesPerMs;
            c.QueueDelaySum += p.QueueFreeAt - t;
            var lat = p.Wifi.Hop(p.QueueFreeAt);
            if (lat == null) { c.AirLost++; return; }
            double arrive = p.QueueFreeAt + lat.Value;
            queue.Enqueue(new DownlinkEv(arrive, p, viewer, bytes, snaps), (arrive, seq++));
        }

        /// <summary>Is the subject's head inside the viewer's Quest 3 field of view (~55° half-angle)?</summary>
        static bool Visible(in Pose viewer, in Pose subject)
        {
            var to = subject.Position - viewer.Position;
            float d = to.Length;
            if (d < 0.2f) return false;
            return Vec3.Dot(viewer.Forward, to * (1f / d)) > MathF.Cos(55f * MathF.PI / 180f);
        }

        static Report BuildReport(Pipeline[] pipes, ErrorStats stats)
        {
            var rep = new Report();
            var combos = new[] { (0, 0), (0, 1), (1, 0), (1, 1) };
            var md = new StringBuilder();
            md.AppendLine("## Results (32 players, all 31 non-host headsets as viewers, errors vs the real tracked pose at render time)\n");
            md.AppendLine("| Pipeline | Receiver | Phase | Head p50 / p95 (cm) | Hands p50 / p95 (cm) | Gun aim p95 (deg) |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var (pi, r) in combos)
                foreach (var ph in Wifi.Phases)
                {
                    var e = stats.Phase(pi * 2 + r, ph.Name);
                    if (double.IsNaN(e.HeadP50)) continue;
                    rep.Errors.Add(new ErrorRow(pipes[pi].Name, ReceiverNames[r], ph.Name, e.HeadP50, e.HeadP95, e.HandP50, e.HandP95, e.AimP95));
                    md.AppendLine($"| {pipes[pi].Name} | {ReceiverNames[r]} | {ph.Name} | {e.HeadP50:0.0} / {e.HeadP95:0.0} | {e.HandP50:0.0} / {e.HandP95:0.0} | {e.AimP95:0.0} |");
                }

            md.AppendLine("\n## Host (relay headset) Wi-Fi uplink\n");
            md.AppendLine("| Pipeline | Phase | Offered (Mbps) | Avg queue delay (ms) | Packets dropped in queue | Avatar records / s |");
            md.AppendLine("|---|---|---|---|---|---|");
            foreach (var p in pipes)
                foreach (var ph in Wifi.Phases)
                {
                    var secs = p.PerSecond.Where(kv => kv.Key >= ph.Start && kv.Key < ph.End).Select(kv => kv.Value).ToList();
                    if (secs.Count == 0) continue;
                    double mbps = secs.Average(c => c.HostBytes) * 8 / 1e6;
                    double qd = secs.Sum(c => c.QueueDelaySum) / Math.Max(1, secs.Sum(c => c.HostPackets - c.QueueDrops));
                    double drop = secs.Sum(c => (double)c.QueueDrops) / Math.Max(1, secs.Sum(c => c.HostPackets));
                    double recs = secs.Average(c => c.AvatarRecords);
                    rep.Network.Add(new NetRow(p.Name, ph.Name, mbps, qd, drop, recs, ph.HostUplinkMbps));
                    md.AppendLine($"| {p.Name} | {ph.Name} ({ph.HostUplinkMbps} Mbps assumed) | {mbps:0.0} | {qd:0} | {drop:P0} | {recs:0} |");
                }

            int packedBytes = (AvatarCodec.FullSizeBits + 7) / 8;
            md.AppendLine($"\nWire size per avatar: naive {NaiveTransformCost.AvatarBytes} B (3 transforms x (7 floats + {NaiveTransformCost.PerObjectHeaderBytes} B header)), packed {packedBytes} B ({AvatarCodec.FullSizeBits} bits).");
            rep.NaiveAvatarBytes = NaiveTransformCost.AvatarBytes;
            rep.PackedAvatarBytes = packedBytes;

            foreach (var p in pipes)
                foreach (var kv in p.PerSecond.OrderBy(k => k.Key))
                    rep.PerSecond.Add(new SecondRow(p.Name, kv.Key, kv.Value.HostBytes * 8 / 1e6,
                        kv.Value.QueueDelaySum / Math.Max(1, kv.Value.HostPackets - kv.Value.QueueDrops),
                        kv.Value.QueueDrops / (double)Math.Max(1, kv.Value.HostPackets)));
            foreach (var (pi, r) in combos)
                foreach (var (sec, e) in stats.Seconds(pi * 2 + r))
                    rep.ErrorPerSecond.Add(new ErrorSecond(pipes[pi].Name, ReceiverNames[r], sec, e.HeadP95, e.HandP95, e.AimP95, e.HandP50));
            rep.Markdown = md.ToString();
            return rep;
        }
    }

    sealed class ErrorStats
    {
        sealed class Bucket { public List<float> Head = new(), Hand = new(), Aim = new(); }
        readonly Dictionary<(int, string), Bucket> phases = new();
        readonly Dictionary<(int, int), Bucket> seconds = new();

        public void Add(int combo, string phase, int sec, in AvatarState truth, in AvatarState shown)
        {
            float head = Vec3.Distance(truth.Head.Position, shown.Head.Position) * 100;
            float l = Vec3.Distance(truth.LeftHand.Position, shown.LeftHand.Position) * 100;
            float r = Vec3.Distance(truth.RightHand.Position, shown.RightHand.Position) * 100;
            float aim = MathF.Acos(Math.Clamp(Vec3.Dot(truth.RightHand.Forward, shown.RightHand.Forward), -1f, 1f)) * 180 / MathF.PI;
            foreach (var b in new[] { Get(phases, (combo, phase)), Get(seconds, (combo, sec)) })
            { b.Head.Add(head); b.Hand.Add(l); b.Hand.Add(r); b.Aim.Add(aim); }
        }

        static Bucket Get<K>(Dictionary<K, Bucket> d, K k) where K : notnull
        {
            if (!d.TryGetValue(k, out var b)) d[k] = b = new Bucket();
            return b;
        }

        public readonly record struct Summary(double HeadP50, double HeadP95, double HandP50, double HandP95, double AimP95);

        static Summary Sum(Bucket b) => new(P(b.Head, 50), P(b.Head, 95), P(b.Hand, 50), P(b.Hand, 95), P(b.Aim, 95));

        public Summary Phase(int combo, string phase) =>
            phases.TryGetValue((combo, phase), out var b) ? Sum(b) : new Summary(double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);

        public IEnumerable<(int, Summary)> Seconds(int combo) =>
            seconds.Where(kv => kv.Key.Item1 == combo).OrderBy(kv => kv.Key.Item2).Select(kv => (kv.Key.Item2, Sum(kv.Value)));

        static double P(List<float> xs, double pct)
        {
            if (xs.Count == 0) return double.NaN;
            var a = xs.ToArray(); Array.Sort(a);
            return a[Math.Min(a.Length - 1, (int)(pct / 100 * a.Length))];
        }
    }

    sealed record ErrorRow(string Pipeline, string Receiver, string Phase, double HeadP50, double HeadP95, double HandP50, double HandP95, double AimP95);
    sealed record NetRow(string Pipeline, string Phase, double HostMbps, double QueueDelayMs, double QueueDropRate, double AvatarRecordsPerSec, double AssumedCapacityMbps);
    sealed record SecondRow(string Pipeline, int Second, double HostMbps, double QueueDelayMs, double DropRate);
    sealed record ErrorSecond(string Pipeline, string Receiver, int Second, double HeadP95, double HandP95, double AimP95, double HandP50);

    sealed class Report
    {
        public List<ErrorRow> Errors { get; } = new();
        public List<NetRow> Network { get; } = new();
        public List<SecondRow> PerSecond { get; } = new();
        public List<ErrorSecond> ErrorPerSecond { get; } = new();
        public int NaiveAvatarBytes { get; set; }
        public int PackedAvatarBytes { get; set; }
        public object Phases => Wifi.Phases;
        [System.Text.Json.Serialization.JsonIgnore] public string Markdown { get; set; }
    }

    /// <summary>Per-frame poses for the filmed viewer, streamed as JSON for the three.js viewer.</summary>
    sealed class TraceWriter
    {
        readonly StreamWriter w;
        bool first = true;

        public TraceWriter(string path)
        {
            w = new StreamWriter(path);
            w.Write("{\"frames\":[");
        }

        public void Frame(double t, PlayerMotion[] players, AvatarState[,] rendered, bool[,] ok)
        {
            var sb = new StringBuilder();
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"t\":").Append(t.ToString("0", CultureInfo.InvariantCulture)).Append(",\"T\":[");
            for (int i = 0; i < players.Length; i++) { if (i > 0) sb.Append(','); Pose(sb, players[i].State); }
            for (int k = 0; k < 2; k++)
            {
                sb.Append(k == 0 ? "],\"A\":[" : "],\"B\":[");
                for (int i = 0; i < players.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (ok[k, i]) Pose(sb, rendered[k, i]); else sb.Append("null");
                }
            }
            sb.Append("]}");
            w.Write(sb.ToString());
        }

        static void Pose(StringBuilder sb, in AvatarState s)
        {
            sb.Append('[');
            V(sb, s.Head.Position); sb.Append(','); V(sb, s.Head.Forward); sb.Append(',');
            V(sb, s.LeftHand.Position); sb.Append(','); V(sb, s.RightHand.Position); sb.Append(','); V(sb, s.RightHand.Forward);
            sb.Append(']');
        }

        static void V(StringBuilder sb, Vec3 v) =>
            sb.Append(v.X.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(v.Y.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
              .Append(v.Z.ToString("0.###", CultureInfo.InvariantCulture));

        public void Finish(Report report)
        {
            w.Write("],\"report\":");
            w.Write(JsonSerializer.Serialize(report));
            w.Write('}');
            w.Dispose();
        }
    }
}
