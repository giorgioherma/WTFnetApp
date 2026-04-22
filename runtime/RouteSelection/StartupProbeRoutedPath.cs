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
                bool warmupMiniTrusted,
                string warmupTag,
                ProbeCandidateRuntimeData runtime)
            {
                IsUsable = isUsable;
                Reason = reason;
                WaitedMs = waitedMs;
                WarmupMiniTrusted = warmupMiniTrusted;
                WarmupTag = warmupTag;
                Runtime = runtime;
            }

            public bool IsUsable { get; }
            public string Reason { get; }
            public double WaitedMs { get; }
            public bool WarmupMiniTrusted { get; }
            public string WarmupTag { get; }
            public ProbeCandidateRuntimeData Runtime { get; }
        }

        private readonly StartupSpeedifyWarmup warmup;
        private readonly StartupSpeedifyMeasurement measurement;


        public StartupSpeedifyTunnelProbe(
            Func<FastPingState> getFastPingState,
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
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
                getMiniSpeedifyMiniSnap,
                isCandidateUsableForMeasurement,
                startupLogger);

            measurement = new StartupSpeedifyMeasurement(
                getShortWindowSnapshot,
                getMiniSpeedifyMiniSnap,
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
                    warmupResult.WarmupMiniTrusted,
                    warmupResult.WarmupTag,
                    null);
            }

            ProbeCandidateRuntimeData runtime = await measurement.RunAsync(
                candidate,
                activationRequestedUtc,
                rawScore,
                leaderScore,
                timeBucketKey,
                cancellationToken);

            return new SpeedifyProbeLifecycleResult(
                true,
                warmupResult.Reason,
                warmupResult.WaitedMs,
                warmupResult.WarmupMiniTrusted,
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
            bool warmupMiniTrusted,
            string warmupTag)
        {
            IsUsable = isUsable;
            Reason = reason;
            WaitedMs = waitedMs;
            WarmupMiniTrusted = warmupMiniTrusted;
            WarmupTag = warmupTag;
        }

        public bool IsUsable { get; }
        public string Reason { get; }
        public double WaitedMs { get; }
        public bool WarmupMiniTrusted { get; }
        public string WarmupTag { get; }
    }

    internal sealed class StartupMiniSnapBuffer
    {
        private readonly int minimumFreshSamples;
        private readonly double minimumSpreadMs;
        private readonly DateTime activationRequestedUtc;
        private readonly List<MiniSpeedifyMiniSnap> samples = new List<MiniSpeedifyMiniSnap>();
        private DateTime firstAcceptedUtc = DateTime.MinValue;
        private DateTime lastAcceptedUtc = DateTime.MinValue;
        private bool sawConnectedEvidence;
        private int connectedEvidenceCount;
        private bool sawDualLinkEvidence;
        private bool sawCongestion;
        private double tunnelLatencySum;
        private int tunnelLatencyCount;
        private double aggLatencySum;
        private int aggLatencyCount;
        private double aggJitterSum;
        private int aggJitterCount;

        public StartupMiniSnapBuffer(int minimumFreshSamples, double minimumSpreadMs, DateTime activationRequestedUtc)
        {
            this.minimumFreshSamples = Math.Max(1, minimumFreshSamples);
            this.minimumSpreadMs = Math.Max(0.0, minimumSpreadMs);
            this.activationRequestedUtc = activationRequestedUtc;
        }

        public int SampleCount => samples.Count;
        public DateTime FirstAcceptedUtc => firstAcceptedUtc;
        public DateTime LastAcceptedUtc => lastAcceptedUtc;
        public double SampleSpreadMs =>
            firstAcceptedUtc != DateTime.MinValue &&
            lastAcceptedUtc != DateTime.MinValue &&
            lastAcceptedUtc > firstAcceptedUtc
                ? (lastAcceptedUtc - firstAcceptedUtc).TotalMilliseconds
                : 0.0;
        public bool HasConnectedEvidence => sawConnectedEvidence;
        public bool HasDualLinkEvidence => sawDualLinkEvidence;
        public bool HasCongestionEvidence => sawCongestion;
        public double AverageTunnelLatencyMs => tunnelLatencyCount > 0 ? tunnelLatencySum / tunnelLatencyCount : -1;
        public double AverageAggLatencyMs => aggLatencyCount > 0 ? aggLatencySum / aggLatencyCount : -1;
        public double AverageAggJitterMs => aggJitterCount > 0 ? aggJitterSum / aggJitterCount : -1;
        public bool HasMeaningfulSignal => sawConnectedEvidence || AverageTunnelLatencyMs > 0 || AverageAggLatencyMs > 0;
        public bool HasMinimumSamples => SampleCount >= minimumFreshSamples;
        public bool HasMinimumSpread => SampleSpreadMs >= minimumSpreadMs;
        public bool IsSufficientForStartupTrust
        {
            get
            {
                bool strongLatencySignal =
                    AverageTunnelLatencyMs > 0 ||
                    AverageAggLatencyMs > 0;

                bool repeatedConnectedEvidence = connectedEvidenceCount >= 2;

                return HasMinimumSamples &&
                       HasMinimumSpread &&
                       HasMeaningfulSignal &&
                       (repeatedConnectedEvidence || strongLatencySignal);
            }
        }

        public bool TryAdd(MiniSpeedifyMiniSnap miniSnap)
        {
            if (miniSnap == null || !miniSnap.IsFresh || miniSnap.TimeUtc == DateTime.MinValue)
                return false;

            if (activationRequestedUtc != DateTime.MinValue && miniSnap.TimeUtc <= activationRequestedUtc)
                return false;

            if (lastAcceptedUtc != DateTime.MinValue && miniSnap.TimeUtc <= lastAcceptedUtc)
                return false;

            if (firstAcceptedUtc == DateTime.MinValue)
                firstAcceptedUtc = miniSnap.TimeUtc;
            lastAcceptedUtc = miniSnap.TimeUtc;
            samples.Add(miniSnap);

            bool connectedEvidence =
                miniSnap.ActiveConnectionCount > 0 ||
                miniSnap.ReportedLatencyMs > 0 ||
                miniSnap.AggregateLatencyMs > 0 ||
                miniSnap.TunnelAggregatePresent;

            sawConnectedEvidence = sawConnectedEvidence || connectedEvidence;
            if (connectedEvidence)
                connectedEvidenceCount++;
            sawDualLinkEvidence = sawDualLinkEvidence || miniSnap.ActiveConnectionCount >= 2;
            sawCongestion = sawCongestion || miniSnap.AggregateCongested;

            if (miniSnap.ReportedLatencyMs > 0)
            {
                tunnelLatencySum += miniSnap.ReportedLatencyMs;
                tunnelLatencyCount++;
            }

            if (miniSnap.AggregateLatencyMs > 0)
            {
                aggLatencySum += miniSnap.AggregateLatencyMs;
                aggLatencyCount++;
            }

            if (miniSnap.AggregateJitterMs > 0)
            {
                aggJitterSum += miniSnap.AggregateJitterMs;
                aggJitterCount++;
            }

            return true;
        }
    }

    internal sealed class StartupSpeedifyWarmup
    {
        private readonly StartupProbeTimingConfig timing;
        private readonly Func<FastPingState> getFastPingState;
        private readonly Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot;
        private readonly Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap;
        private readonly CandidateUsableEvaluator isCandidateUsableForMeasurement;
        private readonly StartupLogger startupLogger;
        private const int StartupRequiredConsecutiveUsableReadings = 3;
        private static readonly TimeSpan StartupTunnelHardBuffer = TimeSpan.FromSeconds(4);
        private const int WarmupMiniMinimumFreshSamples = 3;
        private const double WarmupMiniMinimumSpreadMs = 1200.0;

        public StartupSpeedifyWarmup(
            StartupProbeTimingConfig timing,
            Func<FastPingState> getFastPingState,
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
            Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap,
            CandidateUsableEvaluator isCandidateUsableForMeasurement,
            StartupLogger startupLogger)
        {
            this.timing = timing;
            this.getFastPingState = getFastPingState;
            this.getShortWindowSnapshot = getShortWindowSnapshot;
            this.getMiniSpeedifyMiniSnap = getMiniSpeedifyMiniSnap;
            this.isCandidateUsableForMeasurement = isCandidateUsableForMeasurement;
            this.startupLogger = startupLogger;
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

            TimeSpan fixedSettle = TimeSpan.FromSeconds(10);
            TimeSpan check1 = TimeSpan.FromSeconds(4);
            TimeSpan check2 = TimeSpan.FromSeconds(6);
            TimeSpan forceStart = TimeSpan.FromSeconds(8);

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
            StartupMiniSnapBuffer miniBuffer = new StartupMiniSnapBuffer(
                WarmupMiniMinimumFreshSamples,
                WarmupMiniMinimumSpreadMs,
                activationRequestedUtc);

            while (DateTime.UtcNow < fixedSettleEndUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool usableNow = isCandidateUsableForMeasurement(candidate, out string reasonNow);
                MiniSpeedifyMiniSnap miniSnap = getMiniSpeedifyMiniSnap?.Invoke();
                bool acceptedMini = miniBuffer.TryAdd(miniSnap);
                if (!acceptedMini)
                    miniSnap = null;

                if (DateTime.UtcNow >= nextWarmupLogUtc)
                {
                    ProbeCandidateRuntimeData warmupSnapshot = BuildWarmupFastPingSnapshot();
                    startupLogger?.LogStartupPhase(candidate, "warmup", warmupSnapshot, reasonNow == "usable" ? "-" : reasonNow, miniSnap, logMini: true);

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

                bool usableNow = isCandidateUsableForMeasurement(candidate, out string reasonNow);
                consecutiveUsableReadings = usableNow ? consecutiveUsableReadings + 1 : 0;
                MiniSpeedifyMiniSnap miniSnap = getMiniSpeedifyMiniSnap?.Invoke();
                bool acceptedMini = miniBuffer.TryAdd(miniSnap);
                if (!acceptedMini)
                    miniSnap = null;

                if (consecutiveUsableReadings > bestConsecutiveUsableReadings)
                    bestConsecutiveUsableReadings = consecutiveUsableReadings;

                DateTime nowUtc = DateTime.UtcNow;
                bool hardBufferComplete = (nowUtc - warmupStartUtc) >= StartupTunnelHardBuffer;
                bool stableNow =
                    hardBufferComplete &&
                    usableNow &&
                    consecutiveUsableReadings >= StartupRequiredConsecutiveUsableReadings;

                if (!check1Done && nowUtc >= gateCheck1Utc)
                {
                    check1Done = true;

                    if (stableNow)
                    {
                        string warmupTag =
                            "fixed-settle-" + fixedSettle.TotalSeconds.ToString("0.#") + "s | stable-start-" +
                            Math.Max(StartupTunnelHardBuffer.TotalSeconds, check1.TotalSeconds).ToString("0.#") + "s";

                        return new StartupSpeedifyWarmupResult(
                            true,
                            reasonNow,
                            Math.Max(0.0, (DateTime.UtcNow - warmupStartUtc).TotalMilliseconds),
                            miniBuffer.IsSufficientForStartupTrust,
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

                        return new StartupSpeedifyWarmupResult(
                            true,
                            reasonNow,
                            Math.Max(0.0, (DateTime.UtcNow - warmupStartUtc).TotalMilliseconds),
                            miniBuffer.IsSufficientForStartupTrust,
                            warmupTag);
                    }
                }

                if (DateTime.UtcNow >= nextWarmupLogUtc)
                {
                    ProbeCandidateRuntimeData warmupSnapshot = BuildWarmupFastPingSnapshot();
                    string warmupReason = reasonNow == "usable"
                        ? (consecutiveUsableReadings >= StartupRequiredConsecutiveUsableReadings
                            ? "-"
                            : "usable-streak-" + consecutiveUsableReadings + "/" + StartupRequiredConsecutiveUsableReadings)
                        : reasonNow;

                    startupLogger?.LogStartupPhase(candidate, "warmup", warmupSnapshot, warmupReason, miniSnap, logMini: true);

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

            string finalTag =
                "fixed-settle-" + fixedSettle.TotalSeconds.ToString("0.#") + "s | stable-start-auto-" +
                forceStart.TotalSeconds.ToString("0.#") + "s";
            bool warmupMiniTrusted = miniBuffer.IsSufficientForStartupTrust;

            if (!miniBuffer.HasMinimumSamples)
                finalTag = AppendTag(finalTag, "mini-insufficient-samples");
            if (!miniBuffer.HasMinimumSpread)
                finalTag = AppendTag(finalTag, "mini-poor-time-spread");
            if (!miniBuffer.HasMeaningfulSignal)
                finalTag = AppendTag(finalTag, "mini-no-meaningful-signal");
            if (miniBuffer.HasCongestionEvidence)
                finalTag = AppendTag(finalTag, "mini-congestion-observed");

            return new StartupSpeedifyWarmupResult(
                finalUsable,
                finalReason,
                Math.Max(0.0, (DateTime.UtcNow - warmupStartUtc).TotalMilliseconds),
                warmupMiniTrusted,
                finalTag);
        }

        private static string AppendTag(string existing, string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return existing;

            if (string.IsNullOrWhiteSpace(existing))
                return tag;

            if (existing.IndexOf(tag, StringComparison.OrdinalIgnoreCase) >= 0)
                return existing;

            return existing + " | " + tag;
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
        private readonly Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot;
        private readonly Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap;
        private readonly Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, Task<ProbeCandidateRuntimeData>> captureProbeAsync;
        private readonly StartupProbeTimingConfig timing;
        private readonly StartupLogger startupLogger;
        private readonly Func<ProbeCandidateRuntimeData, ProbeCandidateRuntimeData, TimeSpan, TimeSpan, ProbeCandidateRuntimeData> mergeMeasuredRuntime;
        private readonly Func<string, string, string> appendNote;
        private readonly Action<string> log;

        private const int MainWindowSliceCount = 10;
        private static readonly TimeSpan StabilizationObserveSliceDuration = TimeSpan.FromMilliseconds(1500);
        private const int PhaseMiniMinimumFreshSamples = 3;
        private const double PhaseMiniMinimumSpreadMs = 900.0;

        // =========================
        // CONSTRUCTOR
        // =========================

        public StartupSpeedifyMeasurement(
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
            Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap,
            Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, Task<ProbeCandidateRuntimeData>> captureProbeAsync,
            StartupProbeTimingConfig timing,
            StartupLogger startupLogger,
            Func<ProbeCandidateRuntimeData, ProbeCandidateRuntimeData, TimeSpan, TimeSpan, ProbeCandidateRuntimeData> mergeMeasuredRuntime,
            Func<string, string, string> appendNote,
            Action<string> log)
        {
            this.getShortWindowSnapshot = getShortWindowSnapshot;
            this.getMiniSpeedifyMiniSnap = getMiniSpeedifyMiniSnap;
            this.captureProbeAsync = captureProbeAsync;
            this.timing = timing;
            this.startupLogger = startupLogger;
            this.mergeMeasuredRuntime = mergeMeasuredRuntime;
            this.appendNote = appendNote;
            this.log = log;
        }

        public async Task<ProbeCandidateRuntimeData> RunAsync(
                ProbeCandidateDefinition candidate,
                DateTime activationRequestedUtc,
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
            StartupMiniSnapBuffer phaseMiniBuffer = new StartupMiniSnapBuffer(
                PhaseMiniMinimumFreshSamples,
                PhaseMiniMinimumSpreadMs,
                activationRequestedUtc);
            double previousLossRate = -1;
            double bestValidFastPingLossRate = -1;
            int flattenedLossConfirmations = 0;
            int nearFastPingFloorConfirmations = 0;
            int flatOrUpwardConfirmations = 0;

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

                stabilizationTracker.Observe(sliceRuntime.AvgLossRate, out double deltaPercentagePoints);
                bool fastPingStableQuality = MeetsStabilizationQualityGuardrails(sliceRuntime);
                bool catastrophicSlice = ProbeScoring.TryGetMeasuredCatastrophicStopReason(sliceRuntime, out _);

                if (sliceRuntime.AvgLossRate >= 0 && previousLossRate >= 0)
                {
                    double lossDropPercentagePoints = (previousLossRate - sliceRuntime.AvgLossRate) * 100.0;
                    bool flattenedLossQualified = lossDropPercentagePoints >= 0.0 && lossDropPercentagePoints < 3.0;
                    bool flatOrUpwardQualified = lossDropPercentagePoints <= 0.5;

                    flattenedLossConfirmations = (flattenedLossQualified && fastPingStableQuality && !catastrophicSlice)
                        ? flattenedLossConfirmations + 1
                        : 0;
                    flatOrUpwardConfirmations = (flatOrUpwardQualified && fastPingStableQuality && !catastrophicSlice)
                        ? flatOrUpwardConfirmations + 1
                        : 0;

                    if (bestValidFastPingLossRate >= 0 && bestValidFastPingLossRate <= 0.15)
                    {
                        // Both values here come from live startup-time observations:
                        // tunnel slice loss and a rolling live FastPing floor approximation.
                        bool nearFastPingFloorQualified =
                            Math.Abs(sliceRuntime.AvgLossRate - bestValidFastPingLossRate) <= 0.03;
                        nearFastPingFloorConfirmations = (nearFastPingFloorQualified && fastPingStableQuality && !catastrophicSlice)
                            ? nearFastPingFloorConfirmations + 1
                            : 0;
                    }
                    else
                    {
                        nearFastPingFloorConfirmations = 0;
                    }
                }

                previousLossRate = sliceRuntime.AvgLossRate >= 0 ? sliceRuntime.AvgLossRate : previousLossRate;
                UpdateBestValidFastPingLoss(ref bestValidFastPingLossRate);

                string stabilizeNote = double.IsNaN(deltaPercentagePoints)
                    ? "-"
                    : "delta=" + deltaPercentagePoints.ToString("0.0") + "pp";

                MiniSpeedifyMiniSnap stabilizeMiniSnap = getMiniSpeedifyMiniSnap?.Invoke();
                bool acceptedStabilizeMini = phaseMiniBuffer.TryAdd(stabilizeMiniSnap);
                if (!acceptedStabilizeMini)
                    stabilizeMiniSnap = null;
                ApplyStartupMiniSnapContext(sliceRuntime, stabilizeMiniSnap);
                startupLogger?.LogStartupPhase(candidate, "stabilize", sliceRuntime, stabilizeNote, stabilizeMiniSnap, logMini: true);

                if (!fastPingStableQuality || catastrophicSlice)
                    continue;

                bool flattenedPathSatisfied = flattenedLossConfirmations >= 3;
                bool nearFastPingFloorPathSatisfied = nearFastPingFloorConfirmations >= 3;
                bool flatOrUpwardPathSatisfied = flatOrUpwardConfirmations >= 3;

                if (flattenedPathSatisfied || nearFastPingFloorPathSatisfied || flatOrUpwardPathSatisfied)
                {
                    stabilizationReached = true;
                    stabilizationReason = flattenedPathSatisfied
                        ? "loss-flattened"
                        : nearFastPingFloorPathSatisfied
                            ? "loss-near-live-floor"
                            : "loss-flat-or-upward";

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
            bool postStableMiniExtensionHappened = false;
            double postStableMiniExtraWaitMs = 0.0;

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

                MiniSpeedifyMiniSnap postStableMiniSnap = getMiniSpeedifyMiniSnap?.Invoke();
                bool acceptedPostStableMini = phaseMiniBuffer.TryAdd(postStableMiniSnap);
                if (!acceptedPostStableMini)
                    postStableMiniSnap = null;
                ApplyStartupMiniSnapContext(sliceRuntime, postStableMiniSnap);
                startupLogger?.LogStartupPhase(candidate, "post-stable", sliceRuntime, "-", postStableMiniSnap, logMini: true);

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
                        ? Math.Max(0.0, (postStableMeasurementEndUtc.Value - postStableMeasurementStartUtc.Value).TotalMilliseconds + postStableMiniExtraWaitMs)
                        : -1;
                    startupLogger?.RegisterCandidateEvaluation(
                        candidate,
                        -1,
                        postStableMs,
                        postStableMiniExtensionHappened,
                        aggregateRuntime,
                        null);
                    startupLogger.LogCandidateSummaryFromMeasurement(
                        candidate,
                        stabilizationTimedOut,
                        stabilizationReason,
                        postStableMeasurementStartUtc,
                        postStableMeasurementEndUtc,
                        postStableMiniExtensionHappened,
                        postStableMiniExtraWaitMs,
                        -1,
                        aggregateRuntime,
                        null,
                        "discarded-after-measurement");
                    return aggregateRuntime;
                }
            }

            postStableMiniExtensionHappened = false;
            postStableMiniExtraWaitMs = 0.0;

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

                    MiniSpeedifyMiniSnap extensionMiniSnap = getMiniSpeedifyMiniSnap?.Invoke();
                    bool acceptedExtensionMini = phaseMiniBuffer.TryAdd(extensionMiniSnap);
                    if (!acceptedExtensionMini)
                        extensionMiniSnap = null;
                    ApplyStartupMiniSnapContext(extensionRuntime, extensionMiniSnap);
                    startupLogger?.LogStartupPhase(candidate, "extension", extensionRuntime, "promising-candidate", extensionMiniSnap, logMini: true);

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

            StartupMiniConfidenceResult miniConfidence = ComputeStartupMiniConfidence(phaseMiniBuffer);
            string miniConfidenceTag = "startup-mini-confidence=" + miniConfidence.Label;
            aggregateRuntime.StartupSpeedifyTrusted = miniConfidence.Score >= 0.65;
            aggregateRuntime.StartupSpeedifyConfidenceScore = miniConfidence.Score;
            aggregateRuntime.StartupSpeedifyConfidenceLabel = BuildStartupSpeedifyConfidenceLabel(miniConfidence.Score);
            aggregateRuntime.StartupSpeedifyBondedEvidence = phaseMiniBuffer.HasDualLinkEvidence;
            aggregateRuntime.StartupSpeedifyCongestedEvidence = phaseMiniBuffer.HasCongestionEvidence;
            aggregateRuntime.StartupSpeedifyFastPingAgreementDeltaMs = ComputeStartupSpeedifyAgreementDeltaMs(
                phaseMiniBuffer.AverageAggLatencyMs,
                aggregateRuntime.AvgPingMs);
            aggregateRuntime.StartupSpeedifyFastPingAgreementGood =
                aggregateRuntime.StartupSpeedifyFastPingAgreementDeltaMs >= 0 &&
                aggregateRuntime.StartupSpeedifyFastPingAgreementDeltaMs <= 14.0;
            aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, miniConfidenceTag);
            aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, "startup-mini-confidence-score=" + miniConfidence.Score.ToString("0.00"));
            startupLogger?.LogEvent(candidate, miniConfidenceTag);

            postStableMeasurementEndUtc = DateTime.UtcNow;
            double measuredPostStableMs = postStableMeasurementStartUtc.HasValue
                ? Math.Max(0.0, (postStableMeasurementEndUtc.Value - postStableMeasurementStartUtc.Value).TotalMilliseconds + postStableMiniExtraWaitMs)
                : -1;

            startupLogger?.RegisterCandidateEvaluation(
                candidate,
                -1,
                measuredPostStableMs,
                postStableMiniExtensionHappened,
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

        private static void ApplyStartupMiniSnapContext(
            ProbeCandidateRuntimeData runtime,
            MiniSpeedifyMiniSnap miniSnap)
        {
            if (runtime == null)
                return;

            runtime.TunnelConnected = false;
            runtime.ActiveConnectionCount = 0;
            runtime.BackupConnectionCount = 0;
            runtime.SpeedifyReportedLatencyMs = -1;
            runtime.SpeedifyAggregateLatencyMs = -1;
            runtime.SpeedifyAggregateJitterMs = -1;
            runtime.AggregateCongested = false;
            runtime.SpeedifyFreshSampleCount = 0;
            runtime.SpeedifyExpectedSampleCount = -1;
            runtime.SpeedifyAggregateLatencyStdDevMs = -1;

            if (miniSnap == null || !miniSnap.IsFresh || miniSnap.TimeUtc == DateTime.MinValue)
                return;

            bool hasSignal =
                miniSnap.ActiveConnectionCount > 0 ||
                miniSnap.ReportedLatencyMs > 0 ||
                miniSnap.AggregateLatencyMs > 0 ||
                miniSnap.TunnelAggregatePresent;

            if (!hasSignal)
                return;

            runtime.TunnelConnected =
                miniSnap.ActiveConnectionCount > 0 ||
                miniSnap.ReportedLatencyMs > 0 ||
                miniSnap.AggregateLatencyMs > 0 ||
                miniSnap.TunnelAggregatePresent;
            runtime.ActiveConnectionCount = Math.Max(0, miniSnap.ActiveConnectionCount);
            runtime.BackupConnectionCount = Math.Max(0, miniSnap.BackupConnectionCount);
            runtime.SpeedifyReportedLatencyMs = miniSnap.ReportedLatencyMs > 0 ? miniSnap.ReportedLatencyMs : -1;
            runtime.SpeedifyAggregateLatencyMs = miniSnap.AggregateLatencyMs > 0 ? miniSnap.AggregateLatencyMs : -1;
            runtime.SpeedifyAggregateJitterMs = miniSnap.AggregateJitterMs > 0 ? miniSnap.AggregateJitterMs : -1;
            runtime.AggregateCongested = miniSnap.AggregateCongested && runtime.TunnelConnected;
            runtime.SpeedifyFreshSampleCount = 1;
        }

        private void UpdateBestValidFastPingLoss(ref double bestValidFastPingLossRate)
        {
            // Live rolling approximation from the current startup FastPing window, not a
            // dedicated historical startup-floor capture.
            FastPingSignalAnalyzer.FastPingAggregateSnapshot snapshot = getShortWindowSnapshot?.Invoke();
            if (snapshot == null || !snapshot.IsValid || snapshot.AvgLossRate < 0)
                return;

            if (snapshot.AvgLossRate > 0.15)
                return;

            if (bestValidFastPingLossRate < 0 || snapshot.AvgLossRate < bestValidFastPingLossRate)
                bestValidFastPingLossRate = snapshot.AvgLossRate;
        }

        private static StartupMiniConfidenceResult ComputeStartupMiniConfidence(StartupMiniSnapBuffer miniBuffer)
        {
            if (miniBuffer == null)
                return new StartupMiniConfidenceResult(0.0, "low");

            double score = 0.0;

            if (miniBuffer.HasMinimumSamples)
                score += 0.30;
            if (miniBuffer.HasMinimumSpread)
                score += 0.20;
            if (miniBuffer.HasConnectedEvidence)
                score += 0.20;
            if (miniBuffer.HasDualLinkEvidence)
                score += 0.10;
            if (miniBuffer.AverageTunnelLatencyMs > 0)
                score += 0.10;
            if (miniBuffer.AverageAggLatencyMs > 0)
                score += 0.10;
            if (miniBuffer.HasCongestionEvidence)
                score -= 0.08;

            if (score < 0.0)
                score = 0.0;
            if (score > 1.0)
                score = 1.0;

            string label = score >= 0.75
                ? "high"
                : score >= 0.45
                    ? "medium"
                    : "low";

            return new StartupMiniConfidenceResult(score, label);
        }

        private static string BuildStartupSpeedifyConfidenceLabel(double score)
        {
            if (score < 0.30)
                return "weak";
            if (score < 0.50)
                return "low";
            if (score < 0.75)
                return "medium";
            return "high";
        }

        private static double ComputeStartupSpeedifyAgreementDeltaMs(double aggLatencyMs, double fastPingMs)
        {
            if (aggLatencyMs <= 0 || fastPingMs <= 0)
                return -1;

            return Math.Abs(aggLatencyMs - fastPingMs);
        }

        private readonly struct StartupMiniConfidenceResult
        {
            public StartupMiniConfidenceResult(double score, string label)
            {
                Score = score;
                Label = string.IsNullOrWhiteSpace(label) ? "low" : label;
            }

            public double Score { get; }
            public string Label { get; }
        }

        private static bool MeetsStabilizationQualityGuardrails(ProbeCandidateRuntimeData runtime)
        {
            if (runtime == null)
                return false;

            if (runtime.AvgLossRate >= 0.55)
                return false;

            if (runtime.P95PingMs >= 1700.0)
                return false;

            if (runtime.AvgJitterMs >= 380.0)
                return false;

            if (runtime.SpikeDensityPerMinute >= 120.0)
                return false;

            if (runtime.StaleRate >= 0.80)
                return false;

            return true;
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

            ProbeScoreResult snapshot = ProbeScoring.ScoreCandidate(StartupScoreRuntimeSanitizer.BuildScoreRuntimeWithoutSpeedify(runtime));

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

            if (string.Equals(stabilizationReason, "loss-flattened", StringComparison.OrdinalIgnoreCase))
                return appendNote(notes, "startup-loss-stabilized");

            if (string.Equals(stabilizationReason, "loss-near-live-floor", StringComparison.OrdinalIgnoreCase))
                return appendNote(notes, "startup-loss-near-live-floor");

            if (string.Equals(stabilizationReason, "loss-flat-or-upward", StringComparison.OrdinalIgnoreCase))
                return appendNote(notes, "startup-loss-flat-or-upward");

            return notes;
        }

    }

    internal static class StartupScoreRuntimeSanitizer
    {
        public static ProbeCandidateRuntimeData BuildScoreRuntimeWithoutSpeedify(ProbeCandidateRuntimeData source)
        {
            if (source == null)
                return null;

            return new ProbeCandidateRuntimeData
            {
                Candidate = source.Candidate,
                TimeBucketKey = source.TimeBucketKey,
                AvgPingMs = source.AvgPingMs,
                MaxPingMs = source.MaxPingMs,
                P95PingMs = source.P95PingMs,
                AvgJitterMs = source.AvgJitterMs,
                MinJitterMs = source.MinJitterMs,
                MaxJitterMs = source.MaxJitterMs,
                P95JitterMs = source.P95JitterMs,
                AvgLossRate = source.AvgLossRate,
                StartupAdjustedLossRate = source.StartupAdjustedLossRate,
                AvgBadQualityRate = source.AvgBadQualityRate,
                AvgConfidence = source.AvgConfidence,
                SpikeDensityPerMinute = source.SpikeDensityPerMinute,
                StaleRate = source.StaleRate,
                TunnelConnected = false,
                AggregateCongested = false,
                ActiveConnectionCount = 0,
                BackupConnectionCount = 0,
                SpeedifyAggregateLatencyMs = -1,
                SpeedifyAggregateJitterMs = -1,
                SpeedifyReportedLatencyMs = -1,
                SpeedifyFreshSampleCount = 0,
                SpeedifyExpectedSampleCount = -1,
                SpeedifyAggregateLatencyStdDevMs = -1,
                FirstFreshPostSwitchStatsUtc = DateTime.MinValue,
                FirstFreshPostSwitchAggLatencyMs = -1,
                FirstFreshPostSwitchAggJitterMs = -1,
                FirstFreshPostSwitchReportedLatencyMs = -1,
                FirstFreshPostSwitchActiveConnectionCount = -1,
                FirstFreshPostSwitchAggregateCongested = false,
                IsValid = source.IsValid,
                Notes = source.Notes,
                StabilizationReached = source.StabilizationReached,
                StabilizationTimedOut = source.StabilizationTimedOut,
                StabilizationReason = source.StabilizationReason,
                ReadinessKind = source.ReadinessKind,
                ReadinessReason = source.ReadinessReason,
                ReachedUsableState = source.ReachedUsableState,
                MeasurementStarted = source.MeasurementStarted,
                MeasurementCompleted = source.MeasurementCompleted,
                MeasurementStoppedEarly = source.MeasurementStoppedEarly,
                MeasurementStopReason = source.MeasurementStopReason,
                DiscardReason = source.DiscardReason,
                StartupSpeedifyTrusted = source.StartupSpeedifyTrusted,
                StartupSpeedifyConfidenceScore = source.StartupSpeedifyConfidenceScore,
                StartupSpeedifyConfidenceLabel = source.StartupSpeedifyConfidenceLabel,
                StartupSpeedifyFastPingAgreementDeltaMs = source.StartupSpeedifyFastPingAgreementDeltaMs,
                StartupSpeedifyBondedEvidence = source.StartupSpeedifyBondedEvidence,
                StartupSpeedifyFastPingAgreementGood = source.StartupSpeedifyFastPingAgreementGood,
                StartupSpeedifyCongestedEvidence = source.StartupSpeedifyCongestedEvidence
            };
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
