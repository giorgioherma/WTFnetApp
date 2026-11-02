# FastPing and independent control probe

## Responsibilities

- **FastPing** remains the rapid latency source: nominal 350 ms loop cadence,
  adaptive 700–1800 ms timeout, successful RTTs, stable ping, jitter, spikes,
  baseline, and latency staleness. Raw FastPing `TimedOut` and exception
  statuses stay in `wtfnet_fastping_raw.txt` but **do not contribute** to live
  ICMP loss, consecutive-miss counts or confidence penalties.
- **ControlProbe** supplies live observed ICMP timeout outcomes. It uses a
  completion-aware **600 ms start-to-start minimum**, alternates 1.1.1.1 and
  8.8.8.8, keeps its **1500 ms timeout**, and uses separate Ping instances.
  If a request takes longer than 600 ms, the next starts on completion.
  Requests are never queued or overlapped.
- **Health** consumes FastPing latency/jitter/staleness alongside ControlProbe
  successes and `TimedOut` statuses. The existing weighted loss, confidence,
  bad-quality and health rules remain in use; only the **source of the ICMP
  outcome history** changes. Other control statuses and exceptions are
  diagnostic only.
- **Display** retains the normal FastPing RTT and jitter fields. A completed
  control timeout triggers a synchronous update of the normal publish pipeline,
  plus a short (2.5-second) `ICMP TO` marker next to jitter. A single missed
  control ICMP reply is **not proof** of game UDP packet loss.

FastPing staleness is deliberately independent: if it stops returning
successful RTTs, its stale detection can still alert even with no control
timeout. Control timeouts do not trigger FastPing probe backoff. The control
event is published before the CSV write and is delivered at completion, not
at a fixed wall-clock tick. The overlay itself draws on its 250 ms timer.

## Diagnostic files

- `wtfnet_control_probe.csv`: UTC completion time, target, outcome,
  successful RTT, and elapsed time for every control request.
- `wtfnet_probe_comparison.csv`: ~5-second samples comparing a rolling
  10-second control window with FastPing. The `fast_weighted_miss` legacy
  column now reflects the **control-sourced loss state exported via FastPing**;
  do not interpret it as FastPing raw failure rate. The `both`, `fast-only`,
  `control-only`, `clear`, and `insufficient` labels are provisional
  comparisons, not validated game-loss classifications.
- `wtfnet_fastping_raw.txt` retains the original line prefix, including
  `FAIL(TimedOut)`, plus `start_utc`, `end_utc`, `elapsed_ms`,
  `timeout_ms` and `burst` for each attempted request.

`IPStatus.TimedOut` is an ICMP probe outcome, not a measurement of UDP game
packet loss. A failed request returning well before its configured timeout
must not be equated with a dropped game packet.

## Validation checklist

1. On a stable link, both streams produce normal RTT data and the overlay
   continues showing low-latency FastPing readings without an ICMP marker.
2. A premature FastPing `TimedOut` is present in raw diagnostics, but does
   not increment live loss rate or trigger a control timeout indicator.
3. One ControlProbe `TimedOut` is immediately logged and triggers the short
   `ICMP TO` display marker, while normal FastPing latency measurements
   continue. Subsequent control successes reduce weighted loss.
4. During a sustained outage, both FastPing staleness and control loss
   reflect unresponsiveness. No control probe requests overlap.
5. Verify startup route evaluation, CSV logging and clean shutdown.
   Control failures do not directly switch routes.

## Integration

`DerGrosseGeneralstab.InitializeFastPing` subscribes to
`WtfControlProbe.ProbeCompleted` and passes each completed status to
`WtfFastPing.ApplyControlProbeResult`. Each control success or timeout
recomputes the existing state and publishes it immediately via
`StateUpdated`. The control loop remains independent, and shutdown stops
control before disposing FastPing.

This implementation changes no `runtime/Logger.cs`, NAT keepalive,
Speedify CLI command, or FastPing polling and timeout constants.
