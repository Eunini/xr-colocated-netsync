# xr-colocated-netsync

Head + controller avatar sync for **co-located mixed reality with 32 players** (Quest 3 / Pico, one room, one Wi-Fi network).

In co-located MR you can see the real person through passthrough, so any network delay shows up as the avatar (gun, hands, hitbox) trailing the body. And 32 headsets on one access point means the relay's Wi-Fi uplink is the bottleneck. This repo is a plain C# library for both problems, plus a simulator that measures them.

- `src/XrNetSync/`: the library. netstandard2.1 / C# 9, no engine dependency, ships an `.asmdef` (`noEngineReferences`) so it can be dropped into a Unity project. **It has not been compiled or run inside Unity yet.**
- `sim/`: 32 synthetic laser-tag players, a host headset relaying (like Netcode for GameObjects' DAHost), and modelled venue Wi-Fi. Runs a naive pipeline and the optimized pipeline over the same traffic.
- `tests/`: xUnit tests for the codec, prediction and scheduler.
- `viewer/`: three.js renderer and Playwright recorder for the demo video.

## What it does

| Problem | Approach | Where |
|---|---|---|
| Bandwidth | One bit-packed avatar record: head position at 1 mm in anchor space (15 bits/axis), hands relative to the head (12 bits/axis), smallest-three quaternions (11/10 bits), 16-bit timestamp, 1 bit per untracked hand. **30 B vs 108 B** for three full-float transforms with per-object headers. | `AvatarCodec.cs` |
| Relevancy | Every player in the room is relevant, so nothing is culled. Each viewer gets 30 Hz for players in front of them and close, 20 Hz in front but far (or right behind), and 10 Hz behind. A priority accumulator per (viewer, player) spreads the reduced-rate sends evenly. | `RelevancyScheduler.cs` |
| Interpolation / prediction | Remote heads and hands are **predicted to the present** using the shared network clock. Hands are predicted relative to the predicted head (in its yaw frame), so a running player carries their hands along and only the arm's own motion uses the short horizon. Extrapolation decays and is capped at 250 ms. Corrections are blended over ~45 ms instead of popping. Late or out-of-order packets never move an avatar backwards. | `RemoteAvatar.cs` (`PredictedAvatar`) |
| Baseline for comparison | Classic buffered interpolation at a fixed 2-tick (67 ms) delay, holding the last pose when the buffer runs dry. | `RemoteAvatar.cs` (`InterpolatedAvatar`) |

## Results (from `sim`, 65 s, seed fixed)

Errors are measured at 72 Hz for every one of the 31 non-host viewers against every player inside their field of view, compared with the player's **real tracked pose at render time**.

| Pipeline + receiver | Host upload | Hands p50 / p95 normal | Hands p50 / p95 busy Wi-Fi | Hands p95 interference |
|---|---|---|---|---|
| Naive + interpolation | 25.6 Mbps | 5.3 / 20.2 cm | 25.6 / 81.6 cm | 92.4 cm |
| Naive + prediction | 25.6 Mbps | 0.8 / 8.9 cm | 13.7 / 50.9 cm | 70.3 cm |
| Optimized + interpolation | 4.3 Mbps | 5.4 / 20.5 cm | 6.4 / 25.6 cm | 35.0 cm |
| **Optimized + prediction** | **4.3 Mbps** | **0.8 / 9.0 cm** | **1.3 / 13.1 cm** | **19.6 cm** |

The two halves do different jobs:
- **Packing and relevancy** keep the relay inside its link. At an assumed 15 Mbps, the naive stream queues for 231 ms and drops 41% of packets.
- **Prediction** removes the lag you can see against the real person (head p50 drops from 4.2 cm to 0.7 cm on normal Wi-Fi).

The full tables are in `out/summary.md` after a run.

## Assumptions (read before quoting numbers)

- **Motion is synthetic**, produced by damped springs with sprints, strafing, saccades, recoil kicks and arm swing. Real Quest/Pico tracking is noisier and less predictable. The prediction settings (`PredictionSettings`) were tuned on this motion, so real data may want more velocity smoothing (`VelocityBlend` 0.6–0.8). The gains need re-measuring on device.
- **Wi-Fi model:** one air hop per direction, with base latency, exponential jitter, random loss, interference bursts (85% loss for 150–400 ms), and rare 30–80 ms retry stalls. The relay headset's uplink is a FIFO with an *assumed* effective capacity: 40 Mbps normal, 15 Mbps busy. The bandwidth reduction (−83%) doesn't depend on these; the queueing collapse of the naive pipeline does.
- **Naive wire size:** 28 B per transform + an assumed 8 B per-object header. NGO's `NetworkTransform` has half-float and quaternion-compression options that close part of this gap.
- **Clocks:** each headset's shared clock is off by up to ±5 ms.
- No body/arm IK, colocation-anchor drift, host migration, or gameplay events (hits, scores) are modelled.

## Run it

```
dotnet test tests                                          # 9 tests
dotnet run --project sim -c Release -- out viewer/trace.json  # ~1 min, writes out/summary.md
cd viewer && node record.mjs all && node record.mjs cards     # frames (needs playwright)
```

Then use ffmpeg to join `frames/intro.png`, `frames/f%05d.jpg` and `frames/outro.png` into the demo video.

## Using it in Unity (outline, not yet tested in-engine)

1. Owner: each network tick, read head and controller poses in colocation-anchor space and call `AvatarCodec.Write` into a `FastBufferWriter`-backed custom message (or an unreliable RPC with a byte payload) to the host.
2. Host: keep the newest snapshot per player and, every tick, ask `RelevancyScheduler.Select(viewer, ...)` which avatars to batch for each client. Send them as one unreliable message.
3. Clients: `AvatarCodec.Read` → `PredictedAvatar.OnSnapshot`, then in `Application.onBeforeRender` call `Sample(NetworkTime)` and write the head and hand transforms.

This replaces three `NetworkTransform`s per player.
