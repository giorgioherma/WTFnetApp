# Independent control probe (initial observational pass)

## Why

The existing FastPing engine runs a deliberately aggressive schedule (nominal
350 ms loop cadence and roughly 700 ms per target). Isolated ICMP timeouts
at that rate are not proof of game packet loss, and the existing UI already
weights/filters them. This control stream adds a separate, slower measurement
to help distinguish fast-probe artifacts from problems observable at an
ordinary cadence.

## Implementation

- New `WtfControlProbe`, separate from `WtfFastPing`: one ICMP probe per
  second *overall*, alternating 1.1.1.1 and 8.8.8.8.
- Independent `Ping` objects, 1500 ms timeout and 500 ms startup offset.
  A timeout can extend the actual interval beyond 1 second; the log records
  the measured elapsed time. There is never more than one outstanding
  control request.
- `wtfnet_control_probe.csv`: UTC, target, status, RTT and elapsed time for
  each control attempt. `TimedOut` is a probe outcome, **not** a claim about
  Rocket League's UDP loss.
- `wtfnet_probe_comparison.csv`: every ~5 seconds, compare a rolling
  10-second control window with the existing 10-second fast snapshot.
  Comparison values are `both`, `fast-only`, `control-only`, `clear`,
  and `insufficient`. This is a **provisional diagnostic classifier**, not
  validated evidence of true game-server loss.
- The fast engine, overlay colors, NAT keepalive and route-selection scoring
  are **unchanged**. No automatic route switch occurs from the new control.

## Data interpretation and testing

1. Check that stable connections produce `clear` after startup warmup
   and that isolated fast misses do not lead to persistent `both`.
2. Reproduce an actual network latency spike and verify overlapping fast
   and control elevated RTTs in the CSV files.
3. Compare physical raw routing and Speedify routing in separate windows;
   allow at least 10 seconds after changing routes before interpreting
   overlapping windows.
4. Verify clean shutdown, no overlapping control sends and acceptable CPU/
   network overhead.
5. Check that a few isolated ICMP timeouts are **not** reported as proven
   Rocket League packet loss. The control stream is corroborating evidence,
   not a ground-truth UDP measurement.

No control measurements are fed into existing UI thresholds until gameplay
A/B validation justifies it. The per-window thresholds in
`WtfControlProbe.WriteComparison` are starting heuristics and should be
tuned against recordings rather than treated as final truth.

## Integration

`DerGrosseGeneralstab.InitializeFastPing` starts the control independently
after the fast engine. `StopAsync` stops the control before the fast engine.
The only project registration is a new `Compile Include` in
`WTFnetApp.csproj`. No edits to `runtime/Logger.cs` or to deployment
exports are required.
