# Control-probe RTT display + FastPing rapid warning

## Intent: preserve the proven display

**The overlay's RTT number comes from ControlProbe; its responsive red warning
comes from FastPing.** ControlProbe also owns ICMP-loss diagnostics and gray/stale.
Do not substitute control misses for FastPing's existing hitch/debounce calculation.

### FastPing (unchanged original red-warning behavior)

- Normal latency probing continues with its 350 ms loop delay and adaptive
  700–1800 ms timeout. Successful replies update the *FastPing internal*
  latency, jitter, baseline, spike and frame-to-frame measurements as before.
  These readings classify connection health but no longer supply the overlay
  numeric RTT once ControlProbe has a successful reply.
- FastPing raw `TimedOut` results and exceptions again feed its **original**
  weighted failure history, confidence, consecutive-miss counts and backoff.
  This is an intentionally sensitive **hitch/unresponsiveness warning**, **not
  verified IP or Rocket League UDP packet loss**.
- The original `NetworkQualityEvaluator` thresholds and `UIOverlay`
  layout/color rules are unchanged. FastPing's raw failure rate is used to
  classify the UI's red-warning state independently of ControlProbe's loss.
- FastPing raw failures remain in `wtfnet_fastping_raw.txt`, with their
  recorded elapsed time, requested timeout and burst marker.

### ControlProbe (observed ICMP outcomes + gray)

- Keeps a completion-aware 600 ms minimum start-to-start interval, alternates
  two fixed targets, uses its own `Ping` instances, 8-byte payload and
  1500 ms timeout. There are no concurrent requests or queued missed ticks.
- Its latest successful RTT supplies the overlay's numeric ping value. A timeout
  retains the last valid control RTT; existing control freshness eventually
  makes the overlay gray. Until the first valid control reply, the overlay
  temporarily uses FastPing's RTT rather than displaying an empty value.
- Each completion is logged in `wtfnet_control_probe.csv`. Only `Success`
  and `TimedOut` contribute to a **separate rolling 16-control-sample weighted
  observed ICMP timeout rate**. Exceptions and other statuses are diagnostic,
  not counted as loss.
- The unified `LossRate` field for logging and network interpretation is
  **control-sourced**, never the aggressive FastPing raw-miss percentage.
  Until any valid control outcome, this rate is `-1` (unknown).
- **Gray/stale** uses the time since the last successful control response
  once a control result has been received. It uses the existing stale
  threshold (normal 3200 ms, route evaluation 6500 ms). At startup, before
  any control result, the previous FastPing stale flag is used as fallback.
- Control success or timeout updates only separate diagnostics, latest valid
  control RTT and freshness under the FastPing state lock. It **never**
  increments or resets FastPing's miss streak, modifies its weighted outcomes,
  or forces additional UI ticks.
  The normal FastPing 250 ms publisher reflects control freshness promptly.
- The `ICMP TO` text previously appended to the jitter line was removed to
  restore the original overlay layout. Individual control timeouts remain in
  the application log and control CSV.

### Never conflate these measurements

A FastPing early `TimedOut` can be a useful symptom of responsiveness
trouble without proving any packets were lost. A ControlProbe timeout is an
observed failure of an ICMP exchange, **not** a Rocket League UDP loss sample.
A control ping may succeed while the game is suffering a short hitch.
Neither ping stream measures the game's real UDP packet-loss percentage.

## Diagnostic files and naming

- `wtfnet_fastping_raw.txt`: FastPing's raw successes, early terminal
  statuses, exceptions and request timing.
- `wtfnet_control_probe.csv`: completed ControlProbe results including
  `TimedOut` and successful RTTs.
- `wtfnet_probe_comparison.csv`: rolling 10 s comparison every ~5 s. Its
  legacy `fast_weighted_miss` column once again means **FastPing's
  raw-weighted failure/hitch signal**, not verified packet loss. Interpret
  `control_timeouts` separately. Labels such as `both`, `fast-only`,
  `control-only`, `clear` remain provisional diagnostics.
- `wtfnet_unified_metrics.csv` and unified state `LossRate`: control-based
  observed ICMP timeouts. FastPing raw failure evidence is still available
  via its own logs and analyzer, never as a game-loss claim.

## Regression validation

1. On a stable link, ControlProbe's last successful RTT supplies the numeric
   overlay ping; FastPing determines the green warning state and jitter.
2. Inject/reproduce several consecutive fast failures while control remains
   successful: the original **red hitch warning must still trigger** while
   the number continues to use ControlProbe's RTT; unified control
   `LossRate` should remain approximately zero.
3. Single isolated fast premature `TimedOut` must not be declared game loss
   or automatically trigger an outage; the old weighted UI debounce remains.
4. Force control failures while FastPing still returns success: the RTT
   number holds its last valid control reply, the control ICMP rate rises and
   the UI turns **gray** once the last control success exceeds the stale threshold.
5. On control recovery, the gray flag clears; FastPing continues to own red
   warnings. Check the 250 ms display refresh and that no `ICMP TO`
   string appears in the jitter text.
6. Check Windows CI, startup evaluation, raw/control CSV, and clean shutdown.
   Control results never directly trigger a Speedify route switch.

## Integration

`DerGrosseGeneralstab` subscribes to `WtfControlProbe.ProbeCompleted` and
passes results to `WtfFastPing.ApplyControlProbeResult`. That stores
independent control observations plus the latest successful RTT; normal
FastPing `StateUpdated` publishes both raw hitch state and control summary.
The metrics calculator uses ControlProbe RTT for the *displayed* ping after
first success and exports control loss/staleness separately, while FastPing
telemetry continues to drive the original red-warning health calculation.

No modifications to `runtime/Logger.cs`, NAT keepalive, Speedify CLI, or
FastPing constants are required.
