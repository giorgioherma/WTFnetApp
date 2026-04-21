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
                getWarmupTelemetry,
                isCandidateUsableForMeasurement,
                appendNote,
                log);

            measurement = new StartupSpeedifyMeasurement(
                getFastPingState,
                getShortWindowSnapshot,
                getWarmupTelemetry,
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
        private readonly Func<StartupDecisionSnapshot> getWarmupTelemetry;
        private readonly CandidateUsableEvaluator isCandidateUsableForMeasurement;
        private readonly Func<string, string, string> appendNote;
        private readonly Action<string> log;

        public StartupSpeedifyWarmup(
            StartupProbeTimingConfig timing,
            Func<StartupDecisionSnapshot> getWarmupTelemetry,
            CandidateUsableEvaluator isCandidateUsableForMeasurement,
            Func<string, string, string> appendNote,
            Action<string> log)
        {
            this.timing = timing;
            this.getWarmupTelemetry = getWarmupTelemetry;
            this.isCandidateUsableForMeasurement = isCandidateUsableForMeasurement;
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
                    double activationElapsedMs = Math.Max(0.0, (DateTime.UtcNow - activationRequestedUtc).TotalMilliseconds);
                    double remainingMs = Math.Max(0.0, (fixedSettleEndUtc - DateTime.UtcNow).TotalMilliseconds);

                    log?.Invoke("startup-probe | candidate fixed-settle waiting | " + candidate +
                        " | reason=" + reasonNow +
                        " | remainingMs=" + remainingMs.ToString("0"));

                    log?.Invoke("startup-probe | warmup-observe | " + candidate +
                        " | phase=fixed-settle" +
                        " | elapsedMs=" + activationElapsedMs.ToString("0") +
                        " | freshSamples=" + buffer.FreshSampleCount +
                        " | minRequired=" + minimumFreshSamples +
                        " | spreadMs=" + buffer.SampleDurationMs.ToString("0") +
                        " | minSpreadMs=" + minimumSpreadMs.ToString("0") +
                        " | usableSamples=" + buffer.UsableSampleCount +
                        " | avgAggLat=" + FormatMetric(buffer.AverageAggregateLatencyMs) +
                        " | avgAggJit=" + FormatMetric(buffer.AverageAggregateJitterMs) +
                        " | avgQueue=" + FormatMetric(buffer.AverageQueueDelayMs) +
                        " | congested=" + (buffer.AnyCongestion ? "yes" : "no") +
                        " | stableEnough=" + (buffer.StableEnough ? "yes" : "no") +
                        " | usable=" + (usableNow ? "yes" : "no") +
                        " | reason=" + reasonNow);

                    nextWarmupLogUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
                await Task.Delay(pollInterval, cancellationToken);
            }

            bool check1Done = false;
            bool check2Done = false;

            while (DateTime.UtcNow < gateForceUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                StartupDecisionSnapshot telemetry = getWarmupTelemetry?.Invoke();

                bool usableNow = isCandidateUsableForMeasurement(candidate, out string reasonNow);
                buffer.AddIfFresh(telemetry, usableNow, out _);

                DateTime nowUtc = DateTime.UtcNow;
                bool stableNow =
                    usableNow &&
                    buffer.MeetsMinimum &&
                    buffer.HasGoodTimeSpread &&
                    buffer.StableEnough;

                if (!check1Done && nowUtc >= gateCheck1Utc)
                {
                    check1Done = true;

                    log?.Invoke("startup-probe | stable-start check | " + candidate +
                        " | checkpoint=" + check1.TotalSeconds.ToString("0.#") + "s" +
                        " | stable=" + (stableNow ? "yes" : "no") +
                        " | usable=" + (usableNow ? "yes" : "no") +
                        " | freshSamples=" + buffer.FreshSampleCount +
                        " | spreadMs=" + buffer.SampleDurationMs.ToString("0") +
                        " | reason=" + reasonNow);

                    if (stableNow)
                    {
                        string warmupTag =
                            "fixed-settle-" + fixedSettle.TotalSeconds.ToString("0.#") + "s | stable-start-" +
                            check1.TotalSeconds.ToString("0.#") + "s";

                        log?.Invoke("startup-probe | warmup telemetry trust established | " + candidate +
                            " | checkpoint=" + check1.TotalSeconds.ToString("0.#") + "s");

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

                    log?.Invoke("startup-probe | stable-start check | " + candidate +
                        " | checkpoint=" + check2.TotalSeconds.ToString("0.#") + "s" +
                        " | stable=" + (stableNow ? "yes" : "no") +
                        " | usable=" + (usableNow ? "yes" : "no") +
                        " | freshSamples=" + buffer.FreshSampleCount +
                        " | spreadMs=" + buffer.SampleDurationMs.ToString("0") +
                        " | reason=" + reasonNow);

                    if (stableNow)
                    {
                        string warmupTag =
                            "fixed-settle-" + fixedSettle.TotalSeconds.ToString("0.#") + "s | stable-start-" +
                            check2.TotalSeconds.ToString("0.#") + "s";

                        log?.Invoke("startup-probe | warmup telemetry trust established | " + candidate +
                            " | checkpoint=" + check2.TotalSeconds.ToString("0.#") + "s");

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
                    double activationElapsedMs = Math.Max(0.0, (DateTime.UtcNow - activationRequestedUtc).TotalMilliseconds);
                    double remainingMs = Math.Max(0.0, (gateForceUtc - DateTime.UtcNow).TotalMilliseconds);

                    log?.Invoke("startup-probe | candidate stable-gate waiting | " + candidate +
                        " | reason=" + reasonNow +
                        " | remainingMs=" + remainingMs.ToString("0"));

                    log?.Invoke("startup-probe | warmup-observe | " + candidate +
                        " | phase=stable-gate" +
                        " | elapsedMs=" + activationElapsedMs.ToString("0") +
                        " | freshSamples=" + buffer.FreshSampleCount +
                        " | minRequired=" + minimumFreshSamples +
                        " | spreadMs=" + buffer.SampleDurationMs.ToString("0") +
                        " | minSpreadMs=" + minimumSpreadMs.ToString("0") +
                        " | usableSamples=" + buffer.UsableSampleCount +
                        " | avgAggLat=" + FormatMetric(buffer.AverageAggregateLatencyMs) +
                        " | avgAggJit=" + FormatMetric(buffer.AverageAggregateJitterMs) +
                        " | avgQueue=" + FormatMetric(buffer.AverageQueueDelayMs) +
                        " | congested=" + (buffer.AnyCongestion ? "yes" : "no") +
                        " | stableEnough=" + (buffer.StableEnough ? "yes" : "no") +
                        " | usable=" + (usableNow ? "yes" : "no") +
                        " | reason=" + reasonNow);

                    nextWarmupLogUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }
                await Task.Delay(pollInterval, cancellationToken);
            }

            bool finalUsable = isCandidateUsableForMeasurement(candidate, out string finalReason);
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

            log?.Invoke("startup-probe | stable-start check | " + candidate +
                " | checkpoint=" + forceStart.TotalSeconds.ToString("0.#") + "s" +
                " | stable=" + (warmupTelemetryTrusted ? "yes" : "no") +
                " | usable=" + (finalUsable ? "yes" : "no") +
                " | freshSamples=" + buffer.FreshSampleCount +
                " | spreadMs=" + buffer.SampleDurationMs.ToString("0") +
                " | reason=" + finalReason);

            if (warmupTelemetryTrusted)
            {
                log?.Invoke("startup-probe | warmup telemetry trust established | " + candidate +
                    " | checkpoint=" + forceStart.TotalSeconds.ToString("0.#") + "s-auto");
            }
            else
            {
                log?.Invoke("startup-probe | warmup telemetry trust not established | " + candidate +
                    " | checkpoint=" + forceStart.TotalSeconds.ToString("0.#") + "s-auto");
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
    }

    internal sealed class StartupSpeedifyMeasurement
    {
        private readonly Func<FastPingState> getFastPingState;
        private readonly Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot;
        private readonly Func<StartupDecisionSnapshot> getWarmupTelemetry;
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

        public StartupSpeedifyMeasurement(
            Func<FastPingState> getFastPingState,
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
            Func<StartupDecisionSnapshot> getWarmupTelemetry,
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
            DateTime measurementStartUtc = DateTime.UtcNow;
            DateTime? stabilizationReachedUtc = null;
            DateTime? postStableMeasurementStartUtc = null;
            DateTime? postStableMeasurementEndUtc = null;
            List<StartupLossObservation> lossHistory = new List<StartupLossObservation>();

            log?.Invoke("startup-probe | stabilization phase start | " + candidate +
                " | maxWaitMs=" + stabilizationObserveDuration.TotalMilliseconds.ToString("0"));

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
                RecordLossObservation(lossHistory, sliceRuntime.AvgLossRate, "stabilization");

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

                    log?.Invoke("startup-probe | candidate measured-early-stop | " + candidate + " | reason=" + stabilizationStopReason);
                    startupLogger.LogCandidateLossCurveSummary(candidate, measurementStartUtc, stabilizationReachedUtc, stabilizationTimedOut,
                        stabilizationReason, postStableMeasurementStartUtc, postStableMeasurementEndUtc, lossHistory, false, 0, 0.0, false);
                    return stabilizationRuntime;
                }

                if (isSpeedifyCandidate)
                {
                    StartupDecisionSnapshot telemetry = getWarmupTelemetry != null ? getWarmupTelemetry() : null;
                    bool usableNow = isCandidateUsableForMeasurement(candidate, out _);
                    bool accepted = telemetryWindow.AddIfFresh(telemetry, usableNow, out string ignoreReason);
                    startupLogger.LogTelemetrySnapshotDecision(candidate, telemetry, accepted, ignoreReason);

                    log?.Invoke("startup-probe | stabilization observe | " + candidate +
                        " | speedifyFreshSnapshots=" + telemetryWindow.FreshSampleCount +
                        " | minFresh=" + minimumFreshSpeedifySnapshots);
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

                string deltaText = double.IsNaN(deltaPercentagePoints) ? "--" : deltaPercentagePoints.ToString("0.0");
                string lossText = sliceRuntime.AvgLossRate >= 0 ? (sliceRuntime.AvgLossRate * 100.0).ToString("0.0") : "--";
                string fastPingLossText = currentFastPingLossRate >= 0 ? (currentFastPingLossRate * 100.0).ToString("0.0") : "--";
                string bestFastPingLossText = bestFastPingLossRate >= 0 ? (bestFastPingLossRate * 100.0).ToString("0.0") : "--";
                string fastPingGapText = !double.IsNaN(fastPingGapPercentagePoints) ? fastPingGapPercentagePoints.ToString("0.0") : "--";
                string fastPingConfidenceText = fastPingConfidence >= 0 ? fastPingConfidence.ToString("0.00") : "--";
                string fastPingSpikeText = fastPingSpikeDensity >= 0 ? fastPingSpikeDensity.ToString("0.0") : "--";
                string fastPingJitterText = fastPingAvgJitterMs >= 0 ? fastPingAvgJitterMs.ToString("0.0") : "--";

                log?.Invoke("startup-probe | stabilization observe | " + candidate +
                    " | loss=" + lossText + "%" +
                    " | delta=" + deltaText + "pp" +
                    " | flattening=" + (stableThisSlice ? "yes" : "no") +
                    " | fastPingLoss=" + fastPingLossText + "%" +
                    " | bestFastPingLoss=" + bestFastPingLossText + "%" +
                    " | fastPingConfidence=" + fastPingConfidenceText +
                    " | fastPingSpikeDensity=" + fastPingSpikeText +
                    " | fastPingAvgJitter=" + fastPingJitterText +
                    " | fastPingReferenceEligible=" + (fastPingReferenceEligible ? "yes" : "no") +
                    " | gap=" + fastPingGapText + "pp" +
                    " | closeToFastPing=" + (closeToFastPingStable ? "yes" : "no"));

                if (stableThisSlice || closeToFastPingStable)
                {
                    stabilizationReached = true;
                    stabilizationReachedUtc = DateTime.UtcNow;
                    stabilizationReason = closeToFastPingStable ? "loss-close-to-fastping" : "loss-flattened";

                    if (closeToFastPingStable)
                    {
                        log?.Invoke("startup-probe | stabilization reached | " + candidate +
                            " | reason=loss-close-to-fastping" +
                            " | candidateLoss=" + (candidateLossRate * 100.0).ToString("0.0") + "%" +
                            " | fastPingLoss=" + (currentFastPingLossRate * 100.0).ToString("0.0") + "%" +
                            " | bestFastPingLoss=" + (bestFastPingLossRate * 100.0).ToString("0.0") + "%" +
                            " | gap=" + fastPingGapPercentagePoints.ToString("0.0") + "pp");
                        log?.Invoke("startup-probe | startup loss stabilization reached | " + candidate +
                            " | reason=loss-close-to-fastping");
                    }
                    else
                    {
                        log?.Invoke("startup-probe | stabilization reached | " + candidate + " | reason=loss-flattened");
                        log?.Invoke("startup-probe | startup loss stabilization reached | " + candidate +
                            " | reason=loss-flattened");
                    }

                    break;
                }
            }

            if (!stabilizationReached)
            {
                stabilizationTimedOut = true;
                stabilizationReason = "timeout-forced-stabilization-start";
                log?.Invoke("startup-probe | stabilization timeout | " + candidate +
                    " | waitedMs=" + stabilizationObserved.TotalMilliseconds.ToString("0"));
                log?.Invoke("startup-probe | startup loss stabilization timeout | " + candidate);
            }

            log?.Invoke("startup-probe | post-stable measurement start | " + candidate +
                " | durationMs=" + postStableMeasuredDuration.TotalMilliseconds.ToString("0"));
            postStableMeasurementStartUtc = DateTime.UtcNow;

            List<TimeSpan> measuredSlices = BuildMainWindowSlices(postStableMeasuredDuration);
            ProbeCandidateRuntimeData aggregateRuntime = null;
            TimeSpan accumulatedMeasured = TimeSpan.Zero;
            double adjustedLossWeightedSum = 0.0;
            double adjustedLossDurationSum = 0.0;
            StartupLossGateTracker startupLossGate = new StartupLossGateTracker(candidate, log);
            SpeedifyWarmupWindowBuffer postStableTelemetryWindow = new SpeedifyWarmupWindowBuffer(minimumFreshSpeedifySnapshots, 0.0);
            bool postStableTelemetryExtensionHappened = false;
            bool postStableTelemetryTunnelActiveDuringWait = false;
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
                RecordLossObservation(lossHistory, sliceRuntime.AvgLossRate, "post-stable");

                if (isSpeedifyCandidate)
                {
                    StartupDecisionSnapshot telemetry = getWarmupTelemetry != null ? getWarmupTelemetry() : null;
                    bool usableNow = isCandidateUsableForMeasurement(candidate, out _);
                    bool accepted = postStableTelemetryWindow.AddIfFresh(telemetry, usableNow, out string ignoreReason);
                    startupLogger.LogTelemetrySnapshotDecision(candidate, telemetry, accepted, ignoreReason);
                    startupLogger.LogPostStableTelemetryProgress(candidate, postStableTelemetryWindow.FreshSampleCount,
                        minimumFreshSpeedifySnapshots, sliceRuntime.TunnelConnected, false);
                }

                startupLossGate.Observe(sliceRuntime.AvgLossRate, measuredBeforeSlice, false);
                AccumulateStartupAdjustedLoss(sliceRuntime, slice, startupLossGate.IsValidated, ref adjustedLossWeightedSum, ref adjustedLossDurationSum);

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
                    aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, startupLossGate.IsValidated ? "startup-loss-validated" : "startup-loss-ignored");
                }

                if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(aggregateRuntime, out string mainStopReason))
                {
                    aggregateRuntime.MeasurementStoppedEarly = true;
                    aggregateRuntime.MeasurementCompleted = false;
                    aggregateRuntime.MeasurementStopReason = mainStopReason;
                    aggregateRuntime.DiscardReason = mainStopReason;
                    aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, mainStopReason);

                    log?.Invoke("startup-probe | candidate measured-early-stop | " + candidate + " | reason=" + mainStopReason);
                    postStableMeasurementEndUtc = DateTime.UtcNow;
                    startupLogger.LogCandidateLossCurveSummary(candidate, measurementStartUtc, stabilizationReachedUtc, stabilizationTimedOut,
                        stabilizationReason, postStableMeasurementStartUtc, postStableMeasurementEndUtc, lossHistory,
                        postStableTelemetryExtensionHappened, postStableTelemetryWindow.FreshSampleCount,
                        postStableTelemetryExtraWaitMs, postStableTelemetryTunnelActiveDuringWait);
                    return aggregateRuntime;
                }
            }

            if (aggregateRuntime != null && isSpeedifyCandidate)
            {
                while (aggregateRuntime.TunnelConnected && postStableTelemetryWindow.FreshSampleCount < minimumFreshSpeedifySnapshots)
                {
                    postStableTelemetryExtensionHappened = true;
                    postStableTelemetryTunnelActiveDuringWait = true;

                    log?.Invoke("startup-probe | post-stable measurement extend | " + candidate +
                        " | reason=waiting-for-speedify-telemetry | tunnelActive=yes");

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
                    RecordLossObservation(lossHistory, waitRuntime.AvgLossRate, "post-stable-telemetry-wait");

                    startupLossGate.Observe(waitRuntime.AvgLossRate, accumulatedMeasured, true);
                    AccumulateStartupAdjustedLoss(waitRuntime, waitSlice, startupLossGate.IsValidated, ref adjustedLossWeightedSum, ref adjustedLossDurationSum);

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

                        log?.Invoke("startup-probe | candidate measured-early-stop | " + candidate + " | reason=" + waitStopReason);
                        postStableMeasurementEndUtc = DateTime.UtcNow;
                        postStableTelemetryExtraWaitMs += Math.Max(0.0, (DateTime.UtcNow - waitSliceStartUtc).TotalMilliseconds);
                        startupLogger.LogCandidateLossCurveSummary(candidate, measurementStartUtc, stabilizationReachedUtc, stabilizationTimedOut,
                            stabilizationReason, postStableMeasurementStartUtc, postStableMeasurementEndUtc, lossHistory,
                            postStableTelemetryExtensionHappened, postStableTelemetryWindow.FreshSampleCount,
                            postStableTelemetryExtraWaitMs, postStableTelemetryTunnelActiveDuringWait);
                        return aggregateRuntime;
                    }

                    StartupDecisionSnapshot telemetry = getWarmupTelemetry != null ? getWarmupTelemetry() : null;
                    bool usableNow = isCandidateUsableForMeasurement(candidate, out _);
                    bool accepted = postStableTelemetryWindow.AddIfFresh(telemetry, usableNow, out string ignoreReason);
                    startupLogger.LogTelemetrySnapshotDecision(candidate, telemetry, accepted, ignoreReason);
                    postStableTelemetryExtraWaitMs += Math.Max(0.0, (DateTime.UtcNow - waitSliceStartUtc).TotalMilliseconds);

                    startupLogger.LogPostStableTelemetryProgress(candidate, postStableTelemetryWindow.FreshSampleCount,
                        minimumFreshSpeedifySnapshots, aggregateRuntime.TunnelConnected, true);
                }

                if (aggregateRuntime.TunnelConnected && postStableTelemetryWindow.FreshSampleCount >= minimumFreshSpeedifySnapshots)
                {
                    log?.Invoke("startup-probe | post-stable telemetry sufficient | " + candidate +
                        " | freshSnapshots=" + postStableTelemetryWindow.FreshSampleCount);
                }
                else if (aggregateRuntime.TunnelConnected && postStableTelemetryWindow.FreshSampleCount < minimumFreshSpeedifySnapshots)
                {
                    startupLogger.LogPostStableTelemetryProgress(candidate, postStableTelemetryWindow.FreshSampleCount,
                        minimumFreshSpeedifySnapshots, true, true);
                }
            }

            bool promising = IsPromisingForExtension(aggregateRuntime, rawScore, leaderScore);
            if (promising && timing.PromisingCandidateExtensionDuration > TimeSpan.Zero)
            {
                TimeSpan extension = timing.PromisingCandidateExtensionDuration;
                log?.Invoke("startup-probe | measured-evaluation extend | " + candidate +
                    " | extensionMs=" + extension.TotalMilliseconds.ToString("0") +
                    " | reason=promising-candidate");

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

                    if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(extensionRuntime, out string extensionStopReason))
                    {
                        extensionRuntime.MeasurementStoppedEarly = true;
                        extensionRuntime.MeasurementCompleted = false;
                        extensionRuntime.MeasurementStopReason = extensionStopReason;
                        extensionRuntime.DiscardReason = extensionStopReason;
                        extensionRuntime.Notes = appendNote(extensionRuntime.Notes, extensionStopReason);

                        log?.Invoke("startup-probe | candidate measured-early-stop | " + candidate + " | reason=" + extensionStopReason);

                        ProbeCandidateRuntimeData catastrophicMerged = mergeMeasuredRuntime(aggregateRuntime, extensionRuntime, postStableMeasuredDuration, extension);
                        ApplyStartupAdjustedLossRate(catastrophicMerged, adjustedLossWeightedSum, adjustedLossDurationSum);
                        return catastrophicMerged;
                    }

                    aggregateRuntime = mergeMeasuredRuntime(aggregateRuntime, extensionRuntime, postStableMeasuredDuration, extension);
                    aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, "startup-confidence-extension");
                    aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes,
                        startupLossGate.IsValidated ? "startup-loss-validated" : "startup-loss-ignored");

                    ApplyStartupAdjustedLossRate(aggregateRuntime, adjustedLossWeightedSum, adjustedLossDurationSum);
                }
            }

            if (aggregateRuntime == null)
                return null;

            postStableMeasurementEndUtc = DateTime.UtcNow;
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

            log?.Invoke("startup-probe | candidate measured-full | " + candidate);
            startupLogger.LogCandidateLossCurveSummary(candidate, measurementStartUtc, stabilizationReachedUtc, stabilizationTimedOut,
                stabilizationReason, postStableMeasurementStartUtc, postStableMeasurementEndUtc, lossHistory,
                postStableTelemetryExtensionHappened, postStableTelemetryWindow.FreshSampleCount,
                postStableTelemetryExtraWaitMs, postStableTelemetryTunnelActiveDuringWait);

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

        private static void RecordLossObservation(List<StartupLossObservation> history, double lossRate, string phase)
        {
            if (history == null || lossRate < 0)
                return;

            history.Add(new StartupLossObservation(DateTime.UtcNow, lossRate, phase));
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
        private readonly Action<string> log;

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
            string snapshotUtcText = telemetry != null && telemetry.TimeUtc != DateTime.MinValue
                ? telemetry.TimeUtc.ToString("o")
                : "none";

            if (accepted)
            {
                log?.Invoke("startup-probe | telemetry snapshot accepted | candidate=" + candidate +
                    " | snapshotUtc=" + snapshotUtcText);
                return;
            }

            log?.Invoke("startup-probe | telemetry snapshot ignored | reason=" + (ignoreReason ?? "unknown") +
                " | candidate=" + candidate +
                " | snapshotUtc=" + snapshotUtcText);
        }

        public void LogPostStableTelemetryProgress(
            ProbeCandidateDefinition candidate,
            int observedSnapshotCount,
            int requiredSnapshotCount,
            bool tunnelActive,
            bool baseWindowEnded)
        {
            log?.Invoke("startup-probe | post-stable telemetry progress | candidate=" + candidate +
                " | newSnapshots=" + observedSnapshotCount + "/" + requiredSnapshotCount +
                " | tunnelActive=" + (tunnelActive ? "yes" : "no") +
                (baseWindowEnded ? " | baseWindowEnded=yes | continueWait=indefinite" : string.Empty));
        }

        public void LogCandidateLossCurveSummary(
            ProbeCandidateDefinition candidate,
            DateTime measurementStartUtc,
            DateTime? stabilizationReachedUtc,
            bool stabilizationTimedOut,
            string stabilizationReason,
            DateTime? postStableMeasurementStartUtc,
            DateTime? postStableMeasurementEndUtc,
            List<StartupLossObservation> history,
            bool postStableTelemetryExtensionHappened,
            int postStableFreshSnapshotCount,
            double postStableTelemetryExtraWaitMs,
            bool postStableTelemetryTunnelActiveDuringWait)
        {
            if (history == null || history.Count == 0)
                return;

            List<string> entries = new List<string>(history.Count);
            for (int i = 0; i < history.Count; i++)
            {
                StartupLossObservation point = history[i];
                entries.Add(
                    point.ObservedUtc.ToLocalTime().ToString("HH:mm:ss") +
                    " -> " + (point.LossRate * 100.0).ToString("0.0") + "%" +
                    " (" + point.Phase + ")");
            }

            DateTime? flatteningUtc = null;
            for (int i = 1; i < history.Count; i++)
            {
                double deltaPercentagePoints = (history[i - 1].LossRate - history[i].LossRate) * 100.0;
                if (deltaPercentagePoints <= 3.0)
                {
                    flatteningUtc = history[i].ObservedUtc;
                    break;
                }
            }

            double timeToStableMs =
                stabilizationReachedUtc.HasValue
                    ? Math.Max(0.0, (stabilizationReachedUtc.Value - measurementStartUtc).TotalMilliseconds)
                    : -1;

            string timingSummary = "measurementStart=" + measurementStartUtc.ToLocalTime().ToString("HH:mm:ss") +
                " | flattening=" + (flatteningUtc.HasValue ? flatteningUtc.Value.ToLocalTime().ToString("HH:mm:ss") : "n/a") +
                " | stableAt=" + (stabilizationReachedUtc.HasValue ? stabilizationReachedUtc.Value.ToLocalTime().ToString("HH:mm:ss") : "n/a") +
                " | timeToStableMs=" + (timeToStableMs >= 0 ? timeToStableMs.ToString("0") : "n/a");

            string timeoutSummary = stabilizationTimedOut
                ? "stabilization=timeout | reason=" + (string.IsNullOrWhiteSpace(stabilizationReason) ? "timeout-forced-stabilization-start" : stabilizationReason)
                : "stabilization=reached | reason=" + (string.IsNullOrWhiteSpace(stabilizationReason) ? "loss-trend-stable" : stabilizationReason);

            string postStableSummary =
                postStableMeasurementStartUtc.HasValue
                    ? ("postStableWindow=" + postStableMeasurementStartUtc.Value.ToLocalTime().ToString("HH:mm:ss") +
                       " -> " +
                       (postStableMeasurementEndUtc.HasValue
                           ? postStableMeasurementEndUtc.Value.ToLocalTime().ToString("HH:mm:ss")
                           : "running"))
                    : "postStableWindow=not-started";

            string telemetryWaitSummary =
                "postStableTelemetryExtension=" + (postStableTelemetryExtensionHappened ? "yes" : "no") +
                " | postStableFreshSnapshots=" + postStableFreshSnapshotCount +
                " | postStableTelemetryExtraWaitMs=" + postStableTelemetryExtraWaitMs.ToString("0") +
                " | postStableTunnelActiveDuringWait=" + (postStableTelemetryTunnelActiveDuringWait ? "yes" : "no");

            log?.Invoke("startup-probe | loss-curve summary | " + candidate +
                " | points=[" + string.Join(" ; ", entries) + "]" +
                " | " + timingSummary +
                " | " + timeoutSummary +
                " | " + postStableSummary +
                " | " + telemetryWaitSummary +
                (stabilizationTimedOut ? " | timeout-forced-post-stable=yes" : " | timeout-forced-post-stable=no"));
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
