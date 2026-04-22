using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WTFnetApp
{
    internal sealed class StartupSpeedifyTunnelProbe
    {
        internal readonly struct SpeedifyProbeLifecycleResult
        {
            public SpeedifyProbeLifecycleResult(
                bool isUsable,
                string reason,
                double waitedMs,
                bool warmupTelemetryTrusted,
                string warmupTag,
                ProbeCandidateRuntimeData runtime)
            {
                IsUsable = isUsable;
                Reason = reason;
                WaitedMs = waitedMs;
                WarmupTelemetryTrusted = warmupTelemetryTrusted;
                WarmupTag = warmupTag;
                Runtime = runtime;
            }

            public bool IsUsable { get; }
            public string Reason { get; }
            public double WaitedMs { get; }
            public bool WarmupTelemetryTrusted { get; }
            public string WarmupTag { get; }
            public ProbeCandidateRuntimeData Runtime { get; }
        }

        private readonly StartupSpeedifyWarmup warmup;
        private readonly StartupSpeedifyMeasurement measurement;


        public StartupSpeedifyTunnelProbe(
            Func<FastPingState> getFastPingState,
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
            Func<StartupDecisionSnapshot> getWarmupTelemetry,
            Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap,
            CandidateUsableEvaluator isCandidateUsableForMeasurement,
            Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, Task<ProbeCandidateRuntimeData>> captureProbeAsync,
            StartupProbeTimingConfig timing,
            StartupLogger startupLogger,
            Func<ProbeCandidateRuntimeData, ProbeCandidateRuntimeData, TimeSpan, TimeSpan, ProbeCandidateRuntimeData> mergeMeasuredRuntime,
            Func<string, string, string> appendNote,
            Action<string> log)
        {
            warmup = new StartupSpeedifyWarmup(
                timing,
                getFastPingState,
                getShortWindowSnapshot,
                getWarmupTelemetry,
                getMiniSpeedifyMiniSnap,
                isCandidateUsableForMeasurement,
                startupLogger,
                appendNote,
                log);

            measurement = new StartupSpeedifyMeasurement(
                getFastPingState,
                getShortWindowSnapshot,
                getWarmupTelemetry,
                getMiniSpeedifyMiniSnap,
                isCandidateUsableForMeasurement,
                captureProbeAsync,
                timing,
                startupLogger,
                mergeMeasuredRuntime,
                appendNote,
                log);
        }

        public async Task<SpeedifyProbeLifecycleResult> RunLifecycleAsync(
            ProbeCandidateDefinition candidate,
            DateTime activationRequestedUtc,
            ProbeScoreResult rawScore,
            ProbeScoreResult leaderScore,
            string timeBucketKey,
            CancellationToken cancellationToken)
        {
            StartupSpeedifyWarmupResult warmupResult = await warmup.RunAsync(
                candidate,
                activationRequestedUtc,
                cancellationToken);

            if (!warmupResult.IsUsable)
            {
                return new SpeedifyProbeLifecycleResult(
                    false,
                    warmupResult.Reason,
                    warmupResult.WaitedMs,
                    warmupResult.WarmupTelemetryTrusted,
                    warmupResult.WarmupTag,
                    null);
            }

            ProbeCandidateRuntimeData runtime = await measurement.RunAsync(
                candidate,
                rawScore,
                leaderScore,
                timeBucketKey,
                cancellationToken);

            return new SpeedifyProbeLifecycleResult(
                true,
                warmupResult.Reason,
                warmupResult.WaitedMs,
                warmupResult.WarmupTelemetryTrusted,
                warmupResult.WarmupTag,
                runtime);
        }
    }

    internal readonly struct StartupSpeedifyWarmupResult
    {
        public StartupSpeedifyWarmupResult(
            bool isUsable,
            string reason,
            double waitedMs,
            bool warmupTelemetryTrusted,
            string warmupTag)
        {
            IsUsable = isUsable;
            Reason = reason;
            WaitedMs = waitedMs;
            WarmupTelemetryTrusted = warmupTelemetryTrusted;
            WarmupTag = warmupTag;
        }

        public bool IsUsable { get; }
        public string Reason { get; }
        public double WaitedMs { get; }
        public bool WarmupTelemetryTrusted { get; }
        public string WarmupTag { get; }
    }

    internal sealed class SpeedifyWarmupWindowBuffer
    {
        private readonly List<StartupDecisionSnapshot> samples = new List<StartupDecisionSnapshot>();
        private readonly int minimumSamples;
        private readonly double minimumSpreadMs;
        private DateTime firstAcceptedUtc = DateTime.MinValue;
        private DateTime lastAcceptedUtc = DateTime.MinValue;
        private int usableSampleCount;

        public SpeedifyWarmupWindowBuffer(int minimumSamples, double minimumSpreadMs)
        {
            this.minimumSamples = Math.Max(1, minimumSamples);
            this.minimumSpreadMs = Math.Max(0.0, minimumSpreadMs);
        }

        public int FreshSampleCount => samples.Count;
        public bool MeetsMinimum => samples.Count >= minimumSamples;
        public int UsableSampleCount => usableSampleCount;
        public double SampleDurationMs =>
            firstAcceptedUtc != DateTime.MinValue &&
            lastAcceptedUtc != DateTime.MinValue &&
            lastAcceptedUtc > firstAcceptedUtc
                ? (lastAcceptedUtc - firstAcceptedUtc).TotalMilliseconds
                : 0.0;
        public bool HasGoodTimeSpread => SampleDurationMs >= minimumSpreadMs;
        public bool StableEnough => samples.Count > 0 && usableSampleCount >= (samples.Count * 0.60);
        public double AverageAggregateLatencyMs => Average(s => s.AggregateLatencyMs);
        public double AverageAggregateJitterMs => Average(s => s.AggregateJitterMs);
        public double AverageQueueDelayMs => Average(s => s.QueueDelayMs);
        public bool AnyCongestion => samples.Any(s => s.AggregateCongested || s.DownloadCongested || s.UploadCongested);

        public bool AddIfFresh(StartupDecisionSnapshot snapshot, bool isUsable, out string ignoredReason)
        {
            ignoredReason = null;

            if (snapshot == null || !snapshot.HasFreshTelemetry)
            {
                ignoredReason = "missing-or-not-fresh";
                return false;
            }

            if (snapshot.TimeUtc == DateTime.MinValue)
            {
                ignoredReason = "missing-snapshot-time";
                return false;
            }

            if (lastAcceptedUtc != DateTime.MinValue && snapshot.TimeUtc == lastAcceptedUtc)
            {
                ignoredReason = "sticky-same-timestamp";
                return false;
            }

            if (lastAcceptedUtc != DateTime.MinValue && snapshot.TimeUtc < lastAcceptedUtc)
            {
                ignoredReason = "older-timestamp";
                return false;
            }

            if (firstAcceptedUtc == DateTime.MinValue)
                firstAcceptedUtc = snapshot.TimeUtc;

            lastAcceptedUtc = snapshot.TimeUtc;
            samples.Add(snapshot);

            if (isUsable)
                usableSampleCount++;

            return true;
        }

        private double Average(Func<StartupDecisionSnapshot, double> selector)
        {
            if (samples.Count == 0)
                return -1;

            double sum = 0;
            int count = 0;
            for (int i = 0; i < samples.Count; i++)
            {
                double value = selector(samples[i]);
                if (value >= 0)
                {
                    sum += value;
                    count++;
                }
            }

            return count > 0 ? sum / count : -1;
        }
    }

    internal sealed class StartupSpeedifyWarmup
    {
        private static readonly HashSet<string> SpeedifyWarmupSoftReasons =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "stale route state",
                "missing measurement telemetry",
                "no fresh measurement telemetry",
                "confidence too low"
            };

        private readonly StartupProbeTimingConfig timing;
        private readonly Func<FastPingState> getFastPingState;
        private readonly Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot;
        private readonly Func<StartupDecisionSnapshot> getWarmupTelemetry;
        private readonly Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap;
        private readonly CandidateUsableEvaluator isCandidateUsableForMeasurement;
        private readonly StartupLogger startupLogger;
        private readonly Func<string, string, string> appendNote;
        private readonly Action<string> log;
        private const int StartupRequiredConsecutiveUsableReadings = 3;
        private static readonly TimeSpan StartupTunnelHardBuffer = TimeSpan.FromSeconds(10);

        public StartupSpeedifyWarmup(
            StartupProbeTimingConfig timing,
            Func<FastPingState> getFastPingState,
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
            Func<StartupDecisionSnapshot> getWarmupTelemetry,
            Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap,
            CandidateUsableEvaluator isCandidateUsableForMeasurement,
            StartupLogger startupLogger,
            Func<string, string, string> appendNote,
            Action<string> log)
        {
            this.timing = timing;
            this.getFastPingState = getFastPingState;
            this.getShortWindowSnapshot = getShortWindowSnapshot;
            this.getWarmupTelemetry = getWarmupTelemetry;
            this.getMiniSpeedifyMiniSnap = getMiniSpeedifyMiniSnap;
            this.isCandidateUsableForMeasurement = isCandidateUsableForMeasurement;
            this.startupLogger = startupLogger;
            this.appendNote = appendNote;
            this.log = log;
        }

        public async Task<StartupSpeedifyWarmupResult> RunAsync(
            ProbeCandidateDefinition candidate,
            DateTime activationRequestedUtc,
            CancellationToken cancellationToken)
        {
            bool isSpeedifyCandidate =
                candidate != null &&
                candidate.Kind == ProbeCandidateKind.Speedify;

            if (!isSpeedifyCandidate)
            {
                bool immediateUsable = isCandidateUsableForMeasurement(candidate, out string immediateReason);
                return new StartupSpeedifyWarmupResult(immediateUsable, immediateReason, 0.0, false, "warmup-skipped");
            }

            TimeSpan fixedSettle = timing.RoutedFixedSettleDuration;
            if (fixedSettle < TimeSpan.Zero)
                fixedSettle = TimeSpan.Zero;

            TimeSpan check1 = timing.RoutedStableGateCheck1;
            TimeSpan check2 = timing.RoutedStableGateCheck2;
            TimeSpan forceStart = timing.RoutedStableGateForceStart;

            if (check1 < TimeSpan.Zero)
                check1 = TimeSpan.Zero;
            if (check2 < check1)
                check2 = check1;
            if (forceStart < check2)
                forceStart = check2;

            const int minimumFreshSamples = 4;
            const double minimumSpreadMs = 1500.0;

            DateTime warmupStartUtc = DateTime.UtcNow;
            DateTime fixedSettleEndUtc = warmupStartUtc + fixedSettle;
            DateTime gateStartUtc = fixedSettleEndUtc;
            DateTime gateCheck1Utc = gateStartUtc + check1;
            DateTime gateCheck2Utc = gateStartUtc + check2;
            TimeSpan hardBuffer = StartupTunnelHardBuffer;
            if (forceStart < hardBuffer)
                forceStart = hardBuffer;

            DateTime gateForceUtc = gateStartUtc + forceStart;
            DateTime nextWarmupLogUtc = warmupStartUtc;

            TimeSpan pollInterval = timing.SpeedifyTelemetryCheckInterval > TimeSpan.Zero
                ? timing.SpeedifyTelemetryCheckInterval
                : TimeSpan.FromMilliseconds(250);

            SpeedifyWarmupWindowBuffer buffer = new SpeedifyWarmupWindowBuffer(minimumFreshSamples, minimumSpreadMs);

            while (DateTime.UtcNow < fixedSettleEndUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                StartupDecisionSnapshot telemetry = getWarmupTelemetry != null
                    ? getWarmupTelemetry()
                    : null;

                bool usableNow = isCandidateUsableForMeasurement(candidate, out string reasonNow);
                buffer.AddIfFresh(telemetry, usableNow, out _);

                if (DateTime.UtcNow >= nextWarmupLogUtc)
                {
                    ProbeCandidateRuntimeData warmupSnapshot = BuildWarmupFastPingSnapshot();
                    MiniSpeedifyMiniSnap miniSnap = getMiniSpeedifyMiniSnap != null
                        ? getMiniSpeedifyMiniSnap()
                        : null;

                    startupLogger?.LogProbeRow(
                        candidate,
                        "warmup",
                        warmupSnapshot,
                        buffer,
                        minimumFreshSamples,
                        reasonNow == "usable" ? "-" : reasonNow,
                        miniSnap);

                    nextWarmupLogUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }

                await Task.Delay(pollInterval, cancellationToken);
            }

            bool check1Done = false;
            bool check2Done = false;
            int consecutiveUsableReadings = 0;
            int bestConsecutiveUsableReadings = 0;

            while (DateTime.UtcNow < gateForceUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                StartupDecisionSnapshot telemetry = getWarmupTelemetry?.Invoke();

                bool usableNow = isCandidateUsableForMeasurement(candidate, out string reasonNow);
                buffer.AddIfFresh(telemetry, usableNow, out _);
                consecutiveUsableReadings = usableNow ? consecutiveUsableReadings + 1 : 0;

                if (consecutiveUsableReadings > bestConsecutiveUsableReadings)
                    bestConsecutiveUsableReadings = consecutiveUsableReadings;

                DateTime nowUtc = DateTime.UtcNow;
                bool hardBufferComplete = (nowUtc - warmupStartUtc) >= StartupTunnelHardBuffer;
                bool stableNow =
                    hardBufferComplete &&
                    usableNow &&
                    consecutiveUsableReadings >= StartupRequiredConsecutiveUsableReadings &&
                    buffer.MeetsMinimum &&
                    buffer.HasGoodTimeSpread &&
                    buffer.StableEnough;

                if (!check1Done && nowUtc >= gateCheck1Utc)
                {
                    check1Done = true;

                    if (stableNow)
                    {
                        string warmupTag =
                            "fixed-settle-" + fixedSettle.TotalSeconds.ToString("0.#") + "s | stable-start-" +
                            Math.Max(StartupTunnelHardBuffer.TotalSeconds, check1.TotalSeconds).ToString("0.#") + "s";

                        startupLogger?.LogEvent(candidate, "WARMUP_TRUST_ESTABLISHED");

                        return new StartupSpeedifyWarmupResult(
                            true,
                            reasonNow,
                            Math.Max(0.0, (DateTime.UtcNow - warmupStartUtc).TotalMilliseconds),
                            true,
                            warmupTag);
                    }
                }

                if (!check2Done && nowUtc >= gateCheck2Utc)
                {
                    check2Done = true;

                    if (stableNow)
                    {
                        string warmupTag =
                            "fixed-settle-" + fixedSettle.TotalSeconds.ToString("0.#") + "s | stable-start-" +
                            Math.Max(StartupTunnelHardBuffer.TotalSeconds, check2.TotalSeconds).ToString("0.#") + "s";

                        startupLogger?.LogEvent(candidate, "WARMUP_TRUST_ESTABLISHED");

                        return new StartupSpeedifyWarmupResult(
                            true,
                            reasonNow,
                            Math.Max(0.0, (DateTime.UtcNow - warmupStartUtc).TotalMilliseconds),
                            true,
                            warmupTag);
                    }
                }

                if (DateTime.UtcNow >= nextWarmupLogUtc)
                {
                    ProbeCandidateRuntimeData warmupSnapshot = BuildWarmupFastPingSnapshot();
                    MiniSpeedifyMiniSnap miniSnap = getMiniSpeedifyMiniSnap != null
                        ? getMiniSpeedifyMiniSnap()
                        : null;

                    string warmupReason = reasonNow == "usable"
                        ? (consecutiveUsableReadings >= StartupRequiredConsecutiveUsableReadings
                            ? "-"
                            : "usable-streak-" + consecutiveUsableReadings + "/" + StartupRequiredConsecutiveUsableReadings)
                        : reasonNow;

                    startupLogger?.LogProbeRow(
                        candidate,
                        "warmup",
                        warmupSnapshot,
                        buffer,
                        minimumFreshSamples,
                        warmupReason,
                        miniSnap);

                    nextWarmupLogUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }

                await Task.Delay(pollInterval, cancellationToken);
            }

            bool finalUsable = isCandidateUsableForMeasurement(candidate, out string finalReason);

            if (finalUsable && bestConsecutiveUsableReadings < StartupRequiredConsecutiveUsableReadings)
            {
                finalUsable = false;
                finalReason = "insufficient-consecutive-usable-" +
                              bestConsecutiveUsableReadings + "/" +
                              StartupRequiredConsecutiveUsableReadings;
            }

            bool warmupTelemetryTrusted = buffer.MeetsMinimum && buffer.HasGoodTimeSpread && buffer.StableEnough;

            string finalTag =
                "fixed-settle-" + fixedSettle.TotalSeconds.ToString("0.#") + "s | stable-start-auto-" +
                forceStart.TotalSeconds.ToString("0.#") + "s";

            if (!buffer.MeetsMinimum)
                finalTag = appendNote(finalTag, "speedify-low-confidence-insufficient-samples");
            if (!buffer.HasGoodTimeSpread)
                finalTag = appendNote(finalTag, "speedify-low-confidence-poor-time-spread");
            if (!buffer.StableEnough)
                finalTag = appendNote(finalTag, "speedify-unstable-window");
            if (buffer.AnyCongestion)
                finalTag = appendNote(finalTag, "speedify-congestion-observed");

            if (!string.Equals(finalReason, "usable", StringComparison.OrdinalIgnoreCase) &&
                !SpeedifyWarmupSoftReasons.Contains(finalReason))
            {
                finalTag = appendNote(
                    finalTag,
                    "warmup-hard-failure-" + finalReason.Replace(' ', '-'));
            }

            if (warmupTelemetryTrusted)
            {
                startupLogger?.LogEvent(candidate, "STABILIZATION_REACHED (warmup-stable-gate)");
                startupLogger?.LogEvent(candidate, "WARMUP_TRUST_ESTABLISHED");
            }
            else
            {
                startupLogger?.LogEvent(candidate, "STABILIZATION_TIMEOUT");
            }

            return new StartupSpeedifyWarmupResult(
                finalUsable,
                finalReason,
                Math.Max(0.0, (DateTime.UtcNow - warmupStartUtc).TotalMilliseconds),
                warmupTelemetryTrusted,
                finalTag);
        }

        private static string FormatMetric(double value)
        {
            return value > 0 ? value.ToString("0.0") : "--";
        }

        private ProbeCandidateRuntimeData BuildWarmupFastPingSnapshot()
        {
            FastPingSignalAnalyzer.FastPingAggregateSnapshot snapshot = getShortWindowSnapshot != null
                ? getShortWindowSnapshot()
                : null;
            FastPingState state = getFastPingState != null
                ? getFastPingState()
                : null;

            ProbeCandidateRuntimeData runtime = new ProbeCandidateRuntimeData();
            if (snapshot != null)
            {
                runtime.AvgLossRate = snapshot.AvgLossRate;
                runtime.AvgPingMs = snapshot.AvgPingMs;
                runtime.P95PingMs = snapshot.P95PingMs;
                runtime.AvgJitterMs = snapshot.AvgJitterMs;
                runtime.P95JitterMs = snapshot.P95JitterMs;
                runtime.SpikeDensityPerMinute = snapshot.SpikeDensityPerMinute;
            }

            runtime.AvgConfidence = state?.Confidence ?? -1;
            return runtime;
        }
    }

    internal sealed class StartupSpeedifyMeasurement
    {
        private readonly Func<FastPingState> getFastPingState;
        private readonly Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot;
        private readonly Func<StartupDecisionSnapshot> getWarmupTelemetry;
        private readonly Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap;
        private readonly CandidateUsableEvaluator isCandidateUsableForMeasurement;
        private readonly Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, Task<ProbeCandidateRuntimeData>> captureProbeAsync;
        private readonly StartupProbeTimingConfig timing;
        private readonly StartupLogger startupLogger;
        private readonly Func<ProbeCandidateRuntimeData, ProbeCandidateRuntimeData, TimeSpan, TimeSpan, ProbeCandidateRuntimeData> mergeMeasuredRuntime;
        private readonly Func<string, string, string> appendNote;
        private readonly Action<string> log;

        private const int MainWindowSliceCount = 10;
        private const double SpeedifyFastPingStableGapPercentagePoints = 4.0;
        private const double FastPingReferenceMinimumConfidence = 0.50;
        private const double FastPingReferenceMaximumLossRate = 0.10;
        private static readonly TimeSpan StabilizationObserveSliceDuration = TimeSpan.FromMilliseconds(1500);

        // =========================
        // CONSTRUCTOR
        // =========================

        public StartupSpeedifyMeasurement(
            Func<FastPingState> getFastPingState,
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
            Func<StartupDecisionSnapshot> getWarmupTelemetry,
            Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap,
            CandidateUsableEvaluator isCandidateUsableForMeasurement,
            Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, Task<ProbeCandidateRuntimeData>> captureProbeAsync,
            StartupProbeTimingConfig timing,
            StartupLogger startupLogger,
            Func<ProbeCandidateRuntimeData, ProbeCandidateRuntimeData, TimeSpan, TimeSpan, ProbeCandidateRuntimeData> mergeMeasuredRuntime,
            Func<string, string, string> appendNote,
            Action<string> log)
        {
            this.getFastPingState = getFastPingState;
            this.getShortWindowSnapshot = getShortWindowSnapshot;
            this.getWarmupTelemetry = getWarmupTelemetry;
            this.getMiniSpeedifyMiniSnap = getMiniSpeedifyMiniSnap;
            this.isCandidateUsableForMeasurement = isCandidateUsableForMeasurement;
            this.captureProbeAsync = captureProbeAsync;
            this.timing = timing;
            this.startupLogger = startupLogger;
            this.mergeMeasuredRuntime = mergeMeasuredRuntime;
            this.appendNote = appendNote;
            this.log = log;
        }

        public async Task<ProbeCandidateRuntimeData> RunAsync(
                ProbeCandidateDefinition candidate,
                ProbeScoreResult rawScore,
                ProbeScoreResult leaderScore,
                string timeBucketKey,
                CancellationToken cancellationToken)
        {
            TimeSpan stabilizationObserveDuration = GetMaxStabilizationObserveDuration();
            TimeSpan postStableMeasuredDuration = GetPostStableMeasuredDuration();
            DateTime? postStableMeasurementStartUtc = null;
            DateTime? postStableMeasurementEndUtc = null;

            List<TimeSpan> stabilizationSlices = BuildStabilizationObserveSlices(stabilizationObserveDuration);
            ProbeCandidateRuntimeData stabilizationRuntime = null;
            TimeSpan stabilizationObserved = TimeSpan.Zero;
            StartupLossStabilizationTracker stabilizationTracker = new StartupLossStabilizationTracker();
            bool stabilizationReached = false;
            bool stabilizationTimedOut = false;
            string stabilizationReason = string.Empty;
            const int minimumFreshSpeedifySnapshots = 4;

            SpeedifyWarmupWindowBuffer telemetryWindow = new SpeedifyWarmupWindowBuffer(minimumFreshSpeedifySnapshots, 0.0);
            bool isSpeedifyCandidate = candidate != null && candidate.Kind == ProbeCandidateKind.Speedify;
            double bestFastPingLossObservedRate = -1;

            for (int i = 0; i < stabilizationSlices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan slice = stabilizationSlices[i];

                ProbeCandidateRuntimeData sliceRuntime = await captureProbeAsync(candidate, TimeSpan.Zero, slice);
                if (sliceRuntime == null)
                    return stabilizationRuntime;

                sliceRuntime.Candidate = sliceRuntime.Candidate ?? candidate;
                sliceRuntime.TimeBucketKey = timeBucketKey;
                sliceRuntime.ReachedUsableState = true;
                sliceRuntime.MeasurementStarted = true;
                sliceRuntime.MeasurementCompleted = false;
                sliceRuntime.MeasurementStoppedEarly = false;
                sliceRuntime.Notes = appendNote(sliceRuntime.Notes, "stabilization-observe");

                stabilizationRuntime = stabilizationRuntime == null
                    ? sliceRuntime
                    : mergeMeasuredRuntime(stabilizationRuntime, sliceRuntime, stabilizationObserved, slice);
                stabilizationObserved += slice;

                if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(stabilizationRuntime, out string stabilizationStopReason))
                {
                    stabilizationRuntime.MeasurementStoppedEarly = true;
                    stabilizationRuntime.MeasurementCompleted = false;
                    stabilizationRuntime.MeasurementStopReason = stabilizationStopReason;
                    stabilizationRuntime.DiscardReason = stabilizationStopReason;
                    stabilizationRuntime.Notes = appendNote(stabilizationRuntime.Notes, stabilizationStopReason);

                    startupLogger?.LogEvent(candidate, "DISCARDED (" + stabilizationStopReason + ")");
                    startupLogger.LogCandidateSummaryFromMeasurement(
                        candidate,
                        stabilizationTimedOut,
                        stabilizationReason,
                        postStableMeasurementStartUtc,
                        postStableMeasurementEndUtc,
                        false,
                        0.0,
                        -1,
                        stabilizationRuntime,
                        null,
                        "discarded-early");
                    return stabilizationRuntime;
                }

                if (isSpeedifyCandidate)
                {
                    StartupDecisionSnapshot telemetry = getWarmupTelemetry != null ? getWarmupTelemetry() : null;
                    bool usableNow = isCandidateUsableForMeasurement(candidate, out _);
                    bool accepted = telemetryWindow.AddIfFresh(telemetry, usableNow, out string ignoreReason);
                    startupLogger.LogTelemetrySnapshotDecision(candidate, telemetry, accepted, ignoreReason);
                }

                bool stableThisSlice = stabilizationTracker.Observe(sliceRuntime.AvgLossRate, out double deltaPercentagePoints);
                bool closeToFastPingStable = TryIsSpeedifyLossCloseToFastPing(
                    isSpeedifyCandidate,
                    sliceRuntime,
                    telemetryWindow,
                    ref bestFastPingLossObservedRate,
                    out double candidateLossRate,
                    out double currentFastPingLossRate,
                    out double bestFastPingLossRate,
                    out double fastPingGapPercentagePoints,
                    out double fastPingConfidence,
                    out double fastPingSpikeDensity,
                    out double fastPingAvgJitterMs,
                    out bool fastPingReferenceEligible);

                string stabilizeNote = double.IsNaN(deltaPercentagePoints)
                    ? "-"
                    : "delta=" + deltaPercentagePoints.ToString("0.0") + "pp";

                MiniSpeedifyMiniSnap stabilizeMiniSnap = getMiniSpeedifyMiniSnap != null
                    ? getMiniSpeedifyMiniSnap()
                    : null;

                startupLogger?.LogProbeRow(
                    candidate,
                    "stabilize",
                    sliceRuntime,
                    telemetryWindow,
                    minimumFreshSpeedifySnapshots,
                    stabilizeNote,
                    stabilizeMiniSnap);

                if (stableThisSlice || closeToFastPingStable)
                {
                    stabilizationReached = true;
                    stabilizationReason = closeToFastPingStable ? "loss-close-to-fastping" : "loss-flattened";

                    if (!closeToFastPingStable)
                        startupLogger?.LogEvent(candidate, "LOSS_CURVE_FLATTENED");
                    startupLogger?.LogEvent(candidate, "STABILIZATION_REACHED (" + stabilizationReason + ")");

                    break;
                }
            }

            if (!stabilizationReached)
            {
                stabilizationTimedOut = true;
                stabilizationReason = "timeout-forced-stabilization-start";
                startupLogger?.LogEvent(candidate, "STABILIZATION_TIMEOUT");
            }

            startupLogger?.LogEvent(candidate, "MAIN_SCORING_WINDOW_STARTED");
            postStableMeasurementStartUtc = DateTime.UtcNow;

            List<TimeSpan> measuredSlices = BuildMainWindowSlices(postStableMeasuredDuration);
            ProbeCandidateRuntimeData aggregateRuntime = null;
            TimeSpan accumulatedMeasured = TimeSpan.Zero;
            double adjustedLossWeightedSum = 0.0;
            double adjustedLossDurationSum = 0.0;
            StartupLossGateTracker startupLossGate = new StartupLossGateTracker(candidate, log);
            SpeedifyWarmupWindowBuffer postStableTelemetryWindow = new SpeedifyWarmupWindowBuffer(minimumFreshSpeedifySnapshots, 0.0);
            bool postStableTelemetryExtensionHappened = false;
            double postStableTelemetryExtraWaitMs = 0.0;

            for (int i = 0; i < measuredSlices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan slice = measuredSlices[i];
                TimeSpan measuredBeforeSlice = accumulatedMeasured;

                ProbeCandidateRuntimeData sliceRuntime = await captureProbeAsync(candidate, TimeSpan.Zero, slice);
                if (sliceRuntime == null)
                    return aggregateRuntime;

                sliceRuntime.Candidate = sliceRuntime.Candidate ?? candidate;
                sliceRuntime.TimeBucketKey = timeBucketKey;
                sliceRuntime.ReachedUsableState = true;
                sliceRuntime.MeasurementStarted = true;
                sliceRuntime.MeasurementCompleted = false;
                sliceRuntime.MeasurementStoppedEarly = false;
                sliceRuntime.Notes = appendNote(sliceRuntime.Notes, "measured-post-stable");

                if (isSpeedifyCandidate)
                {
                    StartupDecisionSnapshot telemetry = getWarmupTelemetry != null ? getWarmupTelemetry() : null;
                    bool usableNow = isCandidateUsableForMeasurement(candidate, out _);
                    bool accepted = postStableTelemetryWindow.AddIfFresh(telemetry, usableNow, out string ignoreReason);
                    startupLogger.LogTelemetrySnapshotDecision(candidate, telemetry, accepted, ignoreReason);
                    startupLogger.LogPostStableTelemetryProgress(
                        candidate,
                        postStableTelemetryWindow.FreshSampleCount,
                        minimumFreshSpeedifySnapshots,
                        sliceRuntime.TunnelConnected,
                        false);
                }

                MiniSpeedifyMiniSnap postStableMiniSnap = getMiniSpeedifyMiniSnap != null
                    ? getMiniSpeedifyMiniSnap()
                    : null;

                startupLogger?.LogProbeRow(
                    candidate,
                    "post-stable",
                    sliceRuntime,
                    postStableTelemetryWindow,
                    minimumFreshSpeedifySnapshots,
                    "-",
                    postStableMiniSnap);

                startupLossGate.Observe(sliceRuntime.AvgLossRate, measuredBeforeSlice, false);
                AccumulateStartupAdjustedLoss(
                    sliceRuntime,
                    slice,
                    startupLossGate.IsValidated,
                    ref adjustedLossWeightedSum,
                    ref adjustedLossDurationSum);

                aggregateRuntime = aggregateRuntime == null
                    ? sliceRuntime
                    : mergeMeasuredRuntime(aggregateRuntime, sliceRuntime, accumulatedMeasured, slice);
                accumulatedMeasured += slice;

                if (aggregateRuntime != null)
                {
                    aggregateRuntime.Candidate = aggregateRuntime.Candidate ?? candidate;
                    aggregateRuntime.TimeBucketKey = timeBucketKey;
                    aggregateRuntime.ReachedUsableState = true;
                    aggregateRuntime.MeasurementStarted = true;
                    aggregateRuntime.MeasurementCompleted = false;
                    aggregateRuntime.MeasurementStoppedEarly = false;
                    aggregateRuntime.StabilizationReached = stabilizationReached;
                    aggregateRuntime.StabilizationTimedOut = stabilizationTimedOut;
                    aggregateRuntime.StabilizationReason = stabilizationReason;
                    aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, "stabilization-phase-excluded-from-score");
                    aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, stabilizationReason);
                    aggregateRuntime.Notes = AppendLossStabilizationNote(aggregateRuntime.Notes, stabilizationReason);

                    ApplyStartupAdjustedLossRate(aggregateRuntime, adjustedLossWeightedSum, adjustedLossDurationSum);
                    aggregateRuntime.Notes = appendNote(
                        aggregateRuntime.Notes,
                        startupLossGate.IsValidated ? "startup-loss-validated" : "startup-loss-ignored");
                }

                if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(aggregateRuntime, out string mainStopReason))
                {
                    aggregateRuntime.MeasurementStoppedEarly = true;
                    aggregateRuntime.MeasurementCompleted = false;
                    aggregateRuntime.MeasurementStopReason = mainStopReason;
                    aggregateRuntime.DiscardReason = mainStopReason;
                    aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, mainStopReason);

                    startupLogger?.LogEvent(candidate, "DISCARDED (" + mainStopReason + ")");
                    postStableMeasurementEndUtc = DateTime.UtcNow;
                    double postStableMs = postStableMeasurementStartUtc.HasValue
                        ? Math.Max(0.0, (postStableMeasurementEndUtc.Value - postStableMeasurementStartUtc.Value).TotalMilliseconds + postStableTelemetryExtraWaitMs)
                        : -1;
                    startupLogger?.RegisterCandidateEvaluation(
                        candidate,
                        -1,
                        postStableMs,
                        postStableTelemetryExtensionHappened,
                        aggregateRuntime,
                        null);
                    startupLogger.LogCandidateSummaryFromMeasurement(
                        candidate,
                        stabilizationTimedOut,
                        stabilizationReason,
                        postStableMeasurementStartUtc,
                        postStableMeasurementEndUtc,
                        postStableTelemetryExtensionHappened,
                        postStableTelemetryExtraWaitMs,
                        -1,
                        aggregateRuntime,
                        null,
                        "discarded-after-measurement");
                    return aggregateRuntime;
                }
            }

            if (aggregateRuntime != null && isSpeedifyCandidate)
            {
                while (aggregateRuntime.TunnelConnected && postStableTelemetryWindow.FreshSampleCount < minimumFreshSpeedifySnapshots)
                {
                    postStableTelemetryExtensionHappened = true;

                    startupLogger?.LogEvent(candidate, "EXTENSION STARTED (TELEMETRY WAIT)");

                    TimeSpan waitSlice = StabilizationObserveSliceDuration;
                    DateTime waitSliceStartUtc = DateTime.UtcNow;

                    ProbeCandidateRuntimeData waitRuntime = await captureProbeAsync(candidate, TimeSpan.Zero, waitSlice);
                    if (waitRuntime == null)
                        break;

                    waitRuntime.Candidate = waitRuntime.Candidate ?? candidate;
                    waitRuntime.TimeBucketKey = timeBucketKey;
                    waitRuntime.ReachedUsableState = true;
                    waitRuntime.MeasurementStarted = true;
                    waitRuntime.MeasurementCompleted = false;
                    waitRuntime.MeasurementStoppedEarly = false;
                    waitRuntime.Notes = appendNote(waitRuntime.Notes, "measured-post-stable-telemetry-wait");

                    startupLossGate.Observe(waitRuntime.AvgLossRate, accumulatedMeasured, true);
                    AccumulateStartupAdjustedLoss(
                        waitRuntime,
                        waitSlice,
                        startupLossGate.IsValidated,
                        ref adjustedLossWeightedSum,
                        ref adjustedLossDurationSum);

                    aggregateRuntime = mergeMeasuredRuntime(aggregateRuntime, waitRuntime, accumulatedMeasured, waitSlice);
                    accumulatedMeasured += waitSlice;

                    if (aggregateRuntime != null)
                    {
                        aggregateRuntime.Candidate = aggregateRuntime.Candidate ?? candidate;
                        aggregateRuntime.TimeBucketKey = timeBucketKey;
                        aggregateRuntime.ReachedUsableState = true;
                        aggregateRuntime.MeasurementStarted = true;
                        aggregateRuntime.MeasurementCompleted = false;
                        aggregateRuntime.MeasurementStoppedEarly = false;
                        aggregateRuntime.StabilizationReached = stabilizationReached;
                        aggregateRuntime.StabilizationTimedOut = stabilizationTimedOut;
                        aggregateRuntime.StabilizationReason = stabilizationReason;
                        aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, "stabilization-phase-excluded-from-score");
                        aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, stabilizationReason);
                        aggregateRuntime.Notes = AppendLossStabilizationNote(aggregateRuntime.Notes, stabilizationReason);
                        ApplyStartupAdjustedLossRate(aggregateRuntime, adjustedLossWeightedSum, adjustedLossDurationSum);
                    }

                    if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(aggregateRuntime, out string waitStopReason))
                    {
                        aggregateRuntime.MeasurementStoppedEarly = true;
                        aggregateRuntime.MeasurementCompleted = false;
                        aggregateRuntime.MeasurementStopReason = waitStopReason;
                        aggregateRuntime.DiscardReason = waitStopReason;
                        aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, waitStopReason);

                        startupLogger?.LogEvent(candidate, "DISCARDED (" + waitStopReason + ")");
                        postStableMeasurementEndUtc = DateTime.UtcNow;
                        postStableTelemetryExtraWaitMs += Math.Max(0.0, (DateTime.UtcNow - waitSliceStartUtc).TotalMilliseconds);

                        double postStableMs = postStableMeasurementStartUtc.HasValue
                            ? Math.Max(0.0, (postStableMeasurementEndUtc.Value - postStableMeasurementStartUtc.Value).TotalMilliseconds + postStableTelemetryExtraWaitMs)
                            : -1;

                        startupLogger?.RegisterCandidateEvaluation(
                            candidate,
                            -1,
                            postStableMs,
                            postStableTelemetryExtensionHappened,
                            aggregateRuntime,
                            null);

                        startupLogger.LogCandidateSummaryFromMeasurement(
                            candidate,
                            stabilizationTimedOut,
                            stabilizationReason,
                            postStableMeasurementStartUtc,
                            postStableMeasurementEndUtc,
                            postStableTelemetryExtensionHappened,
                            postStableTelemetryExtraWaitMs,
                            -1,
                            aggregateRuntime,
                            null,
                            "discarded-after-measurement");
                        return aggregateRuntime;
                    }

                    StartupDecisionSnapshot telemetry = getWarmupTelemetry != null ? getWarmupTelemetry() : null;
                    bool usableNow = isCandidateUsableForMeasurement(candidate, out _);
                    bool accepted = postStableTelemetryWindow.AddIfFresh(telemetry, usableNow, out string ignoreReason);
                    startupLogger.LogTelemetrySnapshotDecision(candidate, telemetry, accepted, ignoreReason);
                    postStableTelemetryExtraWaitMs += Math.Max(0.0, (DateTime.UtcNow - waitSliceStartUtc).TotalMilliseconds);

                    startupLogger.LogPostStableTelemetryProgress(
                        candidate,
                        postStableTelemetryWindow.FreshSampleCount,
                        minimumFreshSpeedifySnapshots,
                        aggregateRuntime.TunnelConnected,
                        true);

                    MiniSpeedifyMiniSnap telemetryWaitMiniSnap = getMiniSpeedifyMiniSnap != null
                        ? getMiniSpeedifyMiniSnap()
                        : null;

                    startupLogger?.LogProbeRow(
                        candidate,
                        "post-stable",
                        waitRuntime,
                        postStableTelemetryWindow,
                        minimumFreshSpeedifySnapshots,
                        "telemetry-wait",
                        telemetryWaitMiniSnap);
                }

                if (aggregateRuntime.TunnelConnected && postStableTelemetryWindow.FreshSampleCount >= minimumFreshSpeedifySnapshots)
                {
                    startupLogger?.LogEvent(candidate, "TELEMETRY_SUFFICIENT");
                }
                else if (aggregateRuntime.TunnelConnected && postStableTelemetryWindow.FreshSampleCount < minimumFreshSpeedifySnapshots)
                {
                    startupLogger.LogPostStableTelemetryProgress(
                        candidate,
                        postStableTelemetryWindow.FreshSampleCount,
                        minimumFreshSpeedifySnapshots,
                        true,
                        true);
                }
            }

            bool promising = IsPromisingForExtension(aggregateRuntime, rawScore, leaderScore);
            if (promising && timing.PromisingCandidateExtensionDuration > TimeSpan.Zero)
            {
                TimeSpan extension = timing.PromisingCandidateExtensionDuration;
                startupLogger?.LogEvent(candidate, "EXTENSION STARTED (PROMISING CANDIDATE)");

                ProbeCandidateRuntimeData extensionRuntime = await captureProbeAsync(candidate, TimeSpan.Zero, extension);
                if (extensionRuntime != null)
                {
                    extensionRuntime.Candidate = extensionRuntime.Candidate ?? candidate;
                    extensionRuntime.TimeBucketKey = timeBucketKey;
                    extensionRuntime.ReachedUsableState = true;
                    extensionRuntime.MeasurementStarted = true;
                    extensionRuntime.Notes = appendNote(extensionRuntime.Notes, "measured-extension");

                    if (extensionRuntime.AvgLossRate >= 0)
                    {
                        startupLossGate.Observe(extensionRuntime.AvgLossRate, accumulatedMeasured, true);
                        double extensionSeconds = Math.Max(0.0, extension.TotalSeconds);
                        if (extensionSeconds > 0.0 && startupLossGate.IsValidated)
                        {
                            adjustedLossWeightedSum += extensionRuntime.AvgLossRate * extensionSeconds;
                            adjustedLossDurationSum += extensionSeconds;
                        }
                    }

                    ApplyStartupAdjustedLossRate(extensionRuntime, adjustedLossWeightedSum, adjustedLossDurationSum);

                    MiniSpeedifyMiniSnap extensionMiniSnap = getMiniSpeedifyMiniSnap != null
                        ? getMiniSpeedifyMiniSnap()
                        : null;

                    startupLogger?.LogProbeRow(
                        candidate,
                        "extension",
                        extensionRuntime,
                        postStableTelemetryWindow,
                        minimumFreshSpeedifySnapshots,
                        "promising-candidate",
                        extensionMiniSnap);

                    if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(extensionRuntime, out string extensionStopReason))
                    {
                        extensionRuntime.MeasurementStoppedEarly = true;
                        extensionRuntime.MeasurementCompleted = false;
                        extensionRuntime.MeasurementStopReason = extensionStopReason;
                        extensionRuntime.DiscardReason = extensionStopReason;
                        extensionRuntime.Notes = appendNote(extensionRuntime.Notes, extensionStopReason);

                        startupLogger?.LogEvent(candidate, "DISCARDED (" + extensionStopReason + ")");

                        ProbeCandidateRuntimeData catastrophicMerged = mergeMeasuredRuntime(
                            aggregateRuntime,
                            extensionRuntime,
                            postStableMeasuredDuration,
                            extension);

                        ApplyStartupAdjustedLossRate(catastrophicMerged, adjustedLossWeightedSum, adjustedLossDurationSum);
                        return catastrophicMerged;
                    }

                    aggregateRuntime = mergeMeasuredRuntime(
                        aggregateRuntime,
                        extensionRuntime,
                        postStableMeasuredDuration,
                        extension);

                    aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, "startup-confidence-extension");
                    aggregateRuntime.Notes = appendNote(
                        aggregateRuntime.Notes,
                        startupLossGate.IsValidated ? "startup-loss-validated" : "startup-loss-ignored");

                    ApplyStartupAdjustedLossRate(aggregateRuntime, adjustedLossWeightedSum, adjustedLossDurationSum);
                }
            }

            if (aggregateRuntime == null)
                return null;

            postStableMeasurementEndUtc = DateTime.UtcNow;
            double measuredPostStableMs = postStableMeasurementStartUtc.HasValue
                ? Math.Max(0.0, (postStableMeasurementEndUtc.Value - postStableMeasurementStartUtc.Value).TotalMilliseconds + postStableTelemetryExtraWaitMs)
                : -1;

            startupLogger?.RegisterCandidateEvaluation(
                candidate,
                -1,
                measuredPostStableMs,
                postStableTelemetryExtensionHappened,
                aggregateRuntime,
                null);

            aggregateRuntime.ReachedUsableState = true;
            aggregateRuntime.MeasurementStarted = true;
            aggregateRuntime.MeasurementCompleted = true;
            aggregateRuntime.MeasurementStoppedEarly = false;
            aggregateRuntime.StabilizationReached = stabilizationReached;
            aggregateRuntime.StabilizationTimedOut = stabilizationTimedOut;
            aggregateRuntime.StabilizationReason = stabilizationReason;
            aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, "stabilization-phase-excluded-from-score");
            aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, stabilizationReason);
            aggregateRuntime.Notes = AppendLossStabilizationNote(aggregateRuntime.Notes, stabilizationReason);
            ApplyStartupAdjustedLossRate(aggregateRuntime, adjustedLossWeightedSum, adjustedLossDurationSum);

            startupLogger?.LogEvent(candidate, "MEASUREMENT_FINISHED");

            return aggregateRuntime;
        }

        private static List<TimeSpan> BuildMainWindowSlices(TimeSpan totalDuration)
        {
            List<TimeSpan> slices = new List<TimeSpan>();
            if (totalDuration <= TimeSpan.Zero)
            {
                slices.Add(TimeSpan.Zero);
                return slices;
            }

            long totalTicks = totalDuration.Ticks;
            long consumedTicks = 0;
            for (int i = 0; i < MainWindowSliceCount; i++)
            {
                long remainingTicks = totalTicks - consumedTicks;
                int remainingSlices = MainWindowSliceCount - i;
                long sliceTicks = remainingSlices <= 1 ? remainingTicks : totalTicks / MainWindowSliceCount;
                if (sliceTicks <= 0)
                    sliceTicks = remainingTicks;

                slices.Add(TimeSpan.FromTicks(sliceTicks));
                consumedTicks += sliceTicks;
            }

            return slices.Where(x => x >= TimeSpan.Zero).ToList();
        }

        private static List<TimeSpan> BuildStabilizationObserveSlices(TimeSpan totalDuration)
        {
            List<TimeSpan> slices = new List<TimeSpan>();
            if (totalDuration <= TimeSpan.Zero)
            {
                slices.Add(TimeSpan.Zero);
                return slices;
            }

            TimeSpan remaining = totalDuration;
            TimeSpan sliceDuration = StabilizationObserveSliceDuration > TimeSpan.Zero
                ? StabilizationObserveSliceDuration
                : TimeSpan.FromMilliseconds(1500);

            while (remaining > TimeSpan.Zero)
            {
                TimeSpan current = remaining > sliceDuration ? sliceDuration : remaining;
                slices.Add(current);
                remaining -= current;
            }

            return slices;
        }

        private static void AccumulateStartupAdjustedLoss(
            ProbeCandidateRuntimeData sliceRuntime,
            TimeSpan sliceDuration,
            bool lossValidated,
            ref double adjustedLossWeightedSum,
            ref double adjustedLossDurationSum)
        {
            if (sliceRuntime == null || sliceRuntime.AvgLossRate < 0)
                return;

            double sliceSeconds = Math.Max(0.0, sliceDuration.TotalSeconds);
            if (sliceSeconds <= 0.0 || !lossValidated)
                return;

            adjustedLossWeightedSum += sliceRuntime.AvgLossRate * sliceSeconds;
            adjustedLossDurationSum += sliceSeconds;
        }

        private static void ApplyStartupAdjustedLossRate(
            ProbeCandidateRuntimeData runtime,
            double adjustedLossWeightedSum,
            double adjustedLossDurationSum)
        {
            if (runtime == null)
                return;

            runtime.StartupAdjustedLossRate =
                adjustedLossDurationSum > 0.0
                    ? adjustedLossWeightedSum / adjustedLossDurationSum
                    : -1;
        }

        private bool TryIsSpeedifyLossCloseToFastPing(
            bool isSpeedifyCandidate,
            ProbeCandidateRuntimeData runtime,
            SpeedifyWarmupWindowBuffer telemetryWindow,
            ref double bestFastPingLossObservedRate,
            out double candidateLossRate,
            out double currentFastPingLossRate,
            out double bestFastPingLossRate,
            out double fastPingGapPercentagePoints,
            out double fastPingConfidence,
            out double fastPingSpikeDensity,
            out double fastPingAvgJitterMs,
            out bool fastPingReferenceEligible)
        {
            candidateLossRate = runtime?.AvgLossRate ?? -1;
            currentFastPingLossRate = -1;
            bestFastPingLossRate = bestFastPingLossObservedRate;
            fastPingGapPercentagePoints = double.NaN;
            fastPingConfidence = -1;
            fastPingSpikeDensity = -1;
            fastPingAvgJitterMs = -1;
            fastPingReferenceEligible = false;

            if (!isSpeedifyCandidate || runtime == null)
                return false;
            if (!runtime.TunnelConnected || runtime.ActiveConnectionCount <= 0)
                return false;
            if (candidateLossRate < 0)
                return false;
            if (telemetryWindow == null || telemetryWindow.FreshSampleCount <= 0)
                return false;

            FastPingSignalAnalyzer.FastPingAggregateSnapshot fastPingWindow = getShortWindowSnapshot();
            FastPingState fastPingState = getFastPingState();

            if (fastPingWindow == null || !fastPingWindow.IsValid)
                return false;
            if (fastPingState == null || fastPingState.IsStale)
                return false;

            fastPingConfidence = fastPingState.Confidence;
            currentFastPingLossRate = fastPingWindow.AvgLossRate;
            fastPingSpikeDensity = fastPingWindow.SpikeDensityPerMinute;
            fastPingAvgJitterMs = fastPingWindow.AvgJitterMs;

            if (currentFastPingLossRate < 0)
                return false;

            if (bestFastPingLossObservedRate < 0 || currentFastPingLossRate < bestFastPingLossObservedRate)
                bestFastPingLossObservedRate = currentFastPingLossRate;

            bestFastPingLossRate = bestFastPingLossObservedRate;

            fastPingReferenceEligible =
                fastPingConfidence >= FastPingReferenceMinimumConfidence &&
                bestFastPingLossRate >= 0 &&
                bestFastPingLossRate <= FastPingReferenceMaximumLossRate;

            if (!fastPingReferenceEligible)
                return false;

            fastPingGapPercentagePoints = Math.Abs(candidateLossRate - bestFastPingLossRate) * 100.0;
            return fastPingGapPercentagePoints <= SpeedifyFastPingStableGapPercentagePoints;
        }

        private TimeSpan GetMaxStabilizationObserveDuration()
        {
            if (timing.MaxStabilizationObserveDuration > TimeSpan.Zero)
                return timing.MaxStabilizationObserveDuration;

            return TimeSpan.FromSeconds(30.0);
        }

        private TimeSpan GetPostStableMeasuredDuration()
        {
            if (timing.PostStableMeasuredDuration > TimeSpan.Zero)
                return timing.PostStableMeasuredDuration;

            return TimeSpan.FromSeconds(12.0);
        }

        private static bool IsPromisingForExtension(
            ProbeCandidateRuntimeData runtime,
            ProbeScoreResult rawScore,
            ProbeScoreResult leaderScore)
        {
            if (runtime == null || runtime.MeasurementStoppedEarly)
                return false;

            ProbeScoreResult snapshot = ProbeScoring.ScoreCandidate(runtime);

            double baseline =
                leaderScore?.StartupSelectionScore ??
                rawScore?.StartupSelectionScore ??
                double.MaxValue;

            double gap = snapshot.StartupSelectionScore - baseline;
            bool competitive = gap <= 5.0;
            if (!competitive)
                return false;

            return gap <= 3.0 ||
                   snapshot.WarningLevel != ProbeWarningLevel.None ||
                   runtime.AvgConfidence < 0.90;
        }

        private string AppendLossStabilizationNote(string notes, string stabilizationReason)
        {
            if (string.Equals(stabilizationReason, "timeout-forced-stabilization-start", StringComparison.OrdinalIgnoreCase))
                return appendNote(notes, "startup-loss-timeout");

            if (string.Equals(stabilizationReason, "loss-close-to-fastping", StringComparison.OrdinalIgnoreCase))
                return appendNote(notes, "startup-loss-close-to-raw");

            if (string.Equals(stabilizationReason, "loss-flattened", StringComparison.OrdinalIgnoreCase))
                return appendNote(notes, "startup-loss-stabilized");

            return notes;
        }
    }
}



namespace WTFnetApp
{
    internal sealed class StartupLogger
    {
        private sealed class CandidateSummarySnapshot
        {
            public double WarmupMs { get; set; } = -1;
            public double PostStableMs { get; set; } = -1;
            public bool Extended { get; set; }
            public ProbeCandidateRuntimeData Runtime { get; set; }
            public ProbeScoreResult Score { get; set; }
        }

        private readonly Action<string> log;

        private readonly HashSet<string> printedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> summarizedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CandidateSummarySnapshot> candidateSnapshots = new Dictionary<string, CandidateSummarySnapshot>(StringComparer.OrdinalIgnoreCase);
        private const string TableHeader = "time         phase          loss%   avgPing  p95Ping  avgJit  p95Jit  spikeD  conf   note";
        private const string TableDivider = "---------------------------------------------------------------------------------------";

        public StartupLogger(Action<string> log)
        {
            this.log = log;
        }

        public void LogTelemetrySnapshotDecision(
            ProbeCandidateDefinition candidate,
            StartupDecisionSnapshot telemetry,
            bool accepted,
            string ignoreReason)
        {
            // Suppressed in compact table mode.
        }

        public void LogPostStableTelemetryProgress(
            ProbeCandidateDefinition candidate,
            int observedSnapshotCount,
            int requiredSnapshotCount,
            bool tunnelActive,
            bool baseWindowEnded)
        {
            // Suppressed in compact table mode.
        }

        public void LogProbeRow(
            ProbeCandidateDefinition candidate,
            string phase,
            ProbeCandidateRuntimeData runtime,
            SpeedifyWarmupWindowBuffer telemetryWindow,
            int requiredSnapshots,
            string note,
            MiniSpeedifyMiniSnap miniSnap)
        {
            string key = candidate?.RouteKey ?? candidate?.DisplayName ?? "unknown";
            bool firstForCandidate = !printedCandidates.Contains(key);

            string snap = requiredSnapshots > 0 && telemetryWindow != null
                ? telemetryWindow.FreshSampleCount + "/" + requiredSnapshots
                : "--";

            string serverIdentity = !string.IsNullOrWhiteSpace(candidate?.DisplayName)
                ? candidate.DisplayName
                : !string.IsNullOrWhiteSpace(candidate?.RouteKey)
                    ? candidate.RouteKey
                    : "--";

            string speedifyLine =
                "speedify | fresh=" + snap +
                " | target=" + serverIdentity +
                " | lat=" + FormatNumber(miniSnap?.ReportedLatencyMs ?? -1) +
                " | aggLat=" + FormatNumber(miniSnap?.AggregateLatencyMs ?? -1) +
                " | aggJit=" + FormatNumber(miniSnap?.AggregateJitterMs ?? -1) +
                " | base=" + FormatNumber(miniSnap?.BaseLatencyMs ?? -1) +
                " | dynBase=" + FormatNumber(miniSnap?.DynamicBaseMs ?? -1) +
                " | links=" + FormatCount(miniSnap?.ActiveConnectionCount ?? -1) +
                " | backup=" + FormatCount(miniSnap?.BackupConnectionCount ?? -1) +
                " | congested=" + ((miniSnap?.AggregateCongested ?? false) ? "yes" : "no") +
                " | freshSnap=" + ((miniSnap?.IsFresh ?? false) ? "yes" : "no") +
                " | warnings=" + (miniSnap != null ? miniSnap.ParseWarningCount.ToString() : "--");

            if (firstForCandidate)
            {
                printedCandidates.Add(key);
                log?.Invoke((candidate?.DisplayName ?? "Unknown").ToUpperInvariant());
                log?.Invoke(speedifyLine);
                log?.Invoke(TableHeader);
                log?.Invoke(TableDivider);
            }
            else
            {
                log?.Invoke(speedifyLine);
            }

            string line = string.Format(
                "{0,-12} {1,-14} {2,-7} {3,-8} {4,-8} {5,-7} {6,-7} {7,-7} {8,-6} {9}",
                DateTime.Now.ToString("HH:mm:ss.fff"),
                phase ?? "--",
                FormatPercent((double)(runtime?.AvgLossRate ?? -1)),
                FormatNumber((double)(runtime?.AvgPingMs ?? -1)),
                FormatNumber((double)(runtime?.P95PingMs ?? -1)),
                FormatNumber((double)(runtime?.AvgJitterMs ?? -1)),
                FormatNumber((double)(runtime?.P95JitterMs ?? -1)),
                FormatNumber((double)(runtime?.SpikeDensityPerMinute ?? -1)),
                FormatConf((double)(runtime?.AvgConfidence ?? -1)),
                string.IsNullOrWhiteSpace(note) ? "-" : note);

            log?.Invoke(line);
        }

        public void LogEvent(ProbeCandidateDefinition candidate, string eventText)
        {
            EnsureCandidateTable(candidate);
            string line = string.Format(
                "{0,-12} {1,-14} {2,-7} {3,-8} {4,-8} {5,-7} {6,-7} {7,-7} {8,-6} {9}",
                DateTime.Now.ToString("HH:mm:ss.fff"),
                "EVENT",
                "-----",
                "-----",
                "-----",
                "-----",
                "-----",
                "-----",
                "-----",
                (eventText ?? "-").ToUpperInvariant());
            log?.Invoke(line);
        }

        private void EnsureCandidateTable(ProbeCandidateDefinition candidate)
        {
            string key = candidate?.RouteKey ?? candidate?.DisplayName ?? "unknown";
            if (printedCandidates.Contains(key))
                return;

            printedCandidates.Add(key);
            log?.Invoke((candidate?.DisplayName ?? "Unknown").ToUpperInvariant());
            log?.Invoke(TableHeader);
            log?.Invoke(TableDivider);
        }

        public void LogCandidateSummaryFromMeasurement(
            ProbeCandidateDefinition candidate,
            bool stabilizationTimedOut,
            string stabilizationReason,
            DateTime? postStableMeasurementStartUtc,
            DateTime? postStableMeasurementEndUtc,
            bool postStableTelemetryExtensionHappened,
            double postStableTelemetryExtraWaitMs,
            double warmupMs,
            ProbeCandidateRuntimeData runtime,
            ProbeScoreResult score,
            string outcome)
        {
            ProbeScoreResult effectiveScore = score ?? (runtime != null ? ProbeScoring.ScoreCandidate(runtime) : null);
            double postStableMs =
                postStableMeasurementStartUtc.HasValue && postStableMeasurementEndUtc.HasValue
                    ? Math.Max(0.0, (postStableMeasurementEndUtc.Value - postStableMeasurementStartUtc.Value).TotalMilliseconds + postStableTelemetryExtraWaitMs)
                    : 0.0;

            string stabilized = (!stabilizationTimedOut && runtime?.StabilizationReached == true) ? "yes" : "no";
            string stabilizationText = string.IsNullOrWhiteSpace(stabilizationReason) ? "--" : stabilizationReason;

            LogStartupProbeSummary(
                candidate,
                warmupMs,
                stabilized,
                stabilizationText,
                postStableMs,
                postStableTelemetryExtensionHappened,
                runtime,
                effectiveScore,
                outcome);
        }

        public void LogStartupProbeSummary(
            ProbeCandidateDefinition candidate,
            double warmupMs,
            string stabilized,
            string stabilizationReason,
            double postStableMs,
            bool extended,
            ProbeCandidateRuntimeData runtime,
            ProbeScoreResult score,
            string outcome)
        {
            string key = candidate?.RouteKey ?? candidate?.DisplayName ?? "unknown";
            if (summarizedCandidates.Contains(key))
                return;
            summarizedCandidates.Add(key);

            if (candidateSnapshots.TryGetValue(key, out CandidateSummarySnapshot snapshot))
            {
                if (warmupMs < 0)
                    warmupMs = snapshot.WarmupMs;
                if (postStableMs < 0)
                    postStableMs = snapshot.PostStableMs;
                if (runtime == null)
                    runtime = snapshot.Runtime;
                if (score == null)
                    score = snapshot.Score;
                if (!extended)
                    extended = snapshot.Extended;
            }

            log?.Invoke("[startup-probe-summary] " + (candidate?.DisplayName ?? "Unknown") +
                " | warmupMs=" + (warmupMs >= 0 ? warmupMs.ToString("0") : "--") +
                " | stabilized=" + (string.IsNullOrWhiteSpace(stabilized) ? "--" : stabilized) +
                " | stabilizationReason=" + (string.IsNullOrWhiteSpace(stabilizationReason) ? "--" : stabilizationReason) +
                " | postStableMs=" + (postStableMs >= 0 ? postStableMs.ToString("0") : "--") +
                " | extended=" + (extended ? "yes" : "no") +
                " | finalLoss=" + FormatPercent(runtime?.AvgLossRate ?? -1) +
                " | finalAvg=" + FormatNumber(runtime?.AvgPingMs ?? -1) +
                " | finalJit=" + FormatNumber(runtime?.AvgJitterMs ?? -1) +
                " | finalSpk=" + FormatNumber(runtime?.SpikeDensityPerMinute ?? -1) +
                " | composite=" + FormatNumber(score?.StartupSelectionScore ?? -1) +
                " | overall=" + FormatNumber(score?.OverallStabilityScore ?? -1) +
                " | warning=" + (score != null ? ProbeScoring.GetWarningLabel(score) : "--") +
                " | outcome=" + (string.IsNullOrWhiteSpace(outcome) ? "kept-not-winner" : outcome));
        }

        public void RegisterCandidateEvaluation(
            ProbeCandidateDefinition candidate,
            double warmupMs,
            double postStableMs,
            bool extended,
            ProbeCandidateRuntimeData runtime,
            ProbeScoreResult score)
        {
            string key = candidate?.RouteKey ?? candidate?.DisplayName ?? "unknown";
            candidateSnapshots.TryGetValue(key, out CandidateSummarySnapshot existing);
            CandidateSummarySnapshot snapshot = existing ?? new CandidateSummarySnapshot();

            if (warmupMs >= 0)
                snapshot.WarmupMs = warmupMs;
            if (postStableMs >= 0)
                snapshot.PostStableMs = postStableMs;
            if (extended)
                snapshot.Extended = true;
            if (runtime != null)
                snapshot.Runtime = runtime;
            if (score != null)
                snapshot.Score = score;

            candidateSnapshots[key] = snapshot;
        }

        public void LogFinalDecisionBlock(
            ProbeScoreResult winner,
            ProbeScoreResult runnerUp,
            ProbeScoreResult third,
            ProbeWinnerConfidenceResult confidence,
            string winnerReason,
            string decisionPolicy,
            bool restoreSucceeded,
            List<ProbeScoreResult> shortlist)
        {
            log?.Invoke("FINAL STARTUP DECISION");
            log?.Invoke("winner   : " + (winner?.Candidate?.DisplayName ?? "none"));
            log?.Invoke("runner-up: " + (runnerUp?.Candidate?.DisplayName ?? "none"));
            log?.Invoke("3rd      : " + (third?.Candidate?.DisplayName ?? "none"));
            log?.Invoke("winnerReason    : " + (winnerReason ?? "none"));
            log?.Invoke("runnerUpReason  : " + (runnerUp != null ? ProbeScoring.BuildRouteExplanation(runnerUp) : "none"));
            log?.Invoke("decisionPolicy  : " + (decisionPolicy ?? "none"));
            log?.Invoke("winnerConfidence: " +
                (confidence != null
                    ? confidence.Kind + " (" + confidence.ConfidenceScore.ToString("0.0") + ")"
                    : "none"));
            log?.Invoke("restore         : " + (restoreSucceeded ? "succeeded" : "failed"));
            log?.Invoke("shortlist:");

            if (shortlist == null || shortlist.Count == 0)
                return;

            for (int i = 0; i < shortlist.Count && i < 3; i++)
            {
                ProbeScoreResult item = shortlist[i];
                log?.Invoke("  " + (i + 1).ToString() + ". " + (item?.Candidate?.DisplayName ?? "unknown") +
                    " | composite=" + FormatNumber(item?.StartupSelectionScore ?? -1) +
                    " | overall=" + FormatNumber(item?.OverallStabilityScore ?? -1) +
                    " | warning=" + (item != null ? ProbeScoring.GetWarningLabel(item) : "--") +
                    " | badge=" + (item != null ? ProbeScoring.GetBadgeLabel(item) : "--") +
                    " | explain=" + (item != null ? ProbeScoring.BuildRouteExplanation(item) : "--"));
            }
        }

        

        private static string FormatNumber(double value)
        {
            return value >= 0 ? value.ToString("0.0") : "--";
        }

        private static string FormatPercent(double value)
        {
            return value >= 0 ? (value * 100.0).ToString("0.0") + "%" : "--";
        }

        private static string FormatConf(double value)
        {
            return value >= 0 ? value.ToString("0.00") : "--";
        }

        private static string FormatCount(int value)
        {
            return value >= 0 ? value.ToString() : "--";
        }

    }
}



namespace WTFnetApp
{
    internal enum StartupLossGateState
    {
        IgnoringLoss,
        LossRecoveryCandidate,
        LossValidated
    }

    internal readonly struct StartupLossObservation
    {
        public StartupLossObservation(DateTime observedUtc, double lossRate, string phase)
        {
            ObservedUtc = observedUtc;
            LossRate = lossRate;
            Phase = phase;
        }

        public DateTime ObservedUtc { get; }
        public double LossRate { get; }
        public string Phase { get; }
    }

    internal sealed class StartupLossGateTracker
    {
        private const int MinimumIgnoredSamplesBeforeTrigger = 2;
        private const int RequiredValidationSamples = 3;
        private const double MinimumBaselineLossForTrigger = 0.08;
        private const double MinimumMeaningfulDrop = 0.05;
        private const double TriggerDropRatio = 0.65;
        private const double TriggerUpperBandAbsolute = 0.08;
        private const double CandidateHeadroom = 0.02;
        private const double CandidateBaselineRatio = 0.75;
        private const double RelapseHeadroom = 0.06;
        private const double RelapseBaselineRatio = 0.90;
        private const double RelapseFloorAbsolute = 0.18;

        private readonly Action<string> logger;
        private readonly ProbeCandidateDefinition candidate;
        private double ignoredLossSum;
        private int ignoredLossCount;
        private double candidateBaselineLoss = -1;
        private double triggerLoss = -1;
        private int candidateAcceptableCount;

        public StartupLossGateTracker(ProbeCandidateDefinition candidate, Action<string> logger)
        {
            this.candidate = candidate;
            this.logger = logger;
        }

        public StartupLossGateState State { get; private set; } = StartupLossGateState.IgnoringLoss;
        public bool IsValidated => State == StartupLossGateState.LossValidated;

        public void Observe(double observedLoss, TimeSpan measuredBeforeSlice, bool isExtensionSlice)
        {
            if (observedLoss < 0 || State == StartupLossGateState.LossValidated)
                return;

            switch (State)
            {
                case StartupLossGateState.IgnoringLoss:
                    ObserveIgnoring(observedLoss, measuredBeforeSlice, isExtensionSlice);
                    break;
                case StartupLossGateState.LossRecoveryCandidate:
                    ObserveCandidate(observedLoss, measuredBeforeSlice, isExtensionSlice);
                    break;
            }
        }

        private void ObserveIgnoring(double observedLoss, TimeSpan measuredBeforeSlice, bool isExtensionSlice)
        {
            ignoredLossSum += observedLoss;
            ignoredLossCount++;

            if (ignoredLossCount < MinimumIgnoredSamplesBeforeTrigger)
                return;

            double baselineLoss = ignoredLossSum / Math.Max(1, ignoredLossCount);
            if (baselineLoss < MinimumBaselineLossForTrigger)
                return;

            double triggerDropThreshold = Math.Min(TriggerUpperBandAbsolute, baselineLoss * TriggerDropRatio);
            bool meaningfulDrop = (baselineLoss - observedLoss) >= MinimumMeaningfulDrop;
            bool enteredLowerBand = observedLoss <= triggerDropThreshold;

            if (!meaningfulDrop || !enteredLowerBand)
                return;

            State = StartupLossGateState.LossRecoveryCandidate;
            candidateBaselineLoss = baselineLoss;
            triggerLoss = observedLoss;
            candidateAcceptableCount = 1;

            logger?.Invoke("startup-probe | startup-loss trigger | " +
                (candidate?.ToString() ?? "unknown") +
                " | atMs=" + measuredBeforeSlice.TotalMilliseconds.ToString("0") +
                " | phase=" + (isExtensionSlice ? "extension" : "main") +
                " | baseline=" + (baselineLoss * 100.0).ToString("0.0") + "%" +
                " | triggerLoss=" + (observedLoss * 100.0).ToString("0.0") + "%");
        }

        private void ObserveCandidate(double observedLoss, TimeSpan measuredBeforeSlice, bool isExtensionSlice)
        {
            double baselineLoss = candidateBaselineLoss > 0 ? candidateBaselineLoss : observedLoss;
            double acceptableUpperBound = Math.Min(triggerLoss + CandidateHeadroom, baselineLoss * CandidateBaselineRatio);
            double relapseThreshold = Math.Max(
                RelapseFloorAbsolute,
                Math.Max(triggerLoss + RelapseHeadroom, baselineLoss * RelapseBaselineRatio));

            if (observedLoss >= relapseThreshold)
            {
                State = StartupLossGateState.IgnoringLoss;
                candidateBaselineLoss = -1;
                triggerLoss = -1;
                candidateAcceptableCount = 0;
                ResetIgnoredBaseline(observedLoss);

                logger?.Invoke("startup-probe | startup-loss relapse -> ignore | " +
                    (candidate?.ToString() ?? "unknown") +
                    " | atMs=" + measuredBeforeSlice.TotalMilliseconds.ToString("0") +
                    " | phase=" + (isExtensionSlice ? "extension" : "main") +
                    " | loss=" + (observedLoss * 100.0).ToString("0.0") + "%" +
                    " | relapseThreshold=" + (relapseThreshold * 100.0).ToString("0.0") + "%");
                return;
            }

            if (observedLoss <= acceptableUpperBound)
                candidateAcceptableCount++;
            else
                candidateAcceptableCount = Math.Max(0, candidateAcceptableCount - 1);

            if (candidateAcceptableCount < RequiredValidationSamples)
                return;

            State = StartupLossGateState.LossValidated;
            logger?.Invoke("startup-probe | startup-loss validated | " +
                (candidate?.ToString() ?? "unknown") +
                " | atMs=" + measuredBeforeSlice.TotalMilliseconds.ToString("0") +
                " | phase=" + (isExtensionSlice ? "extension" : "main") +
                " | validatedSamples=" + candidateAcceptableCount);
        }

        private void ResetIgnoredBaseline(double seedLoss)
        {
            ignoredLossSum = 0;
            ignoredLossCount = 0;

            if (seedLoss < 0)
                return;

            ignoredLossSum = seedLoss;
            ignoredLossCount = 1;
        }
    }

    internal sealed class StartupLossStabilizationTracker
    {
        private const double StabilizationDeltaThresholdPercentagePoints = 3.0;

        private double? previousLossRate;
        private int consecutiveSmallDrops;
        private bool previousTransitionWasPositiveSmallDrop;

        public bool Observe(double lossRate, out double deltaPercentagePoints)
        {
            deltaPercentagePoints = double.NaN;

            if (lossRate < 0)
                return false;

            if (!previousLossRate.HasValue)
            {
                previousLossRate = lossRate;
                return false;
            }

            deltaPercentagePoints = (previousLossRate.Value - lossRate) * 100.0;
            previousLossRate = lossRate;

            bool smallDropOrLess = deltaPercentagePoints <= StabilizationDeltaThresholdPercentagePoints;
            bool positiveSmallDrop = deltaPercentagePoints > 0.0 && deltaPercentagePoints <= StabilizationDeltaThresholdPercentagePoints;
            bool flatOrRising = deltaPercentagePoints <= 0.0;

            if (smallDropOrLess)
                consecutiveSmallDrops++;
            else
                consecutiveSmallDrops = 0;

            if (previousTransitionWasPositiveSmallDrop && flatOrRising)
                return true;

            if (consecutiveSmallDrops >= 2)
                return true;

            previousTransitionWasPositiveSmallDrop = positiveSmallDrop;
            return false;
        }
    }
}
