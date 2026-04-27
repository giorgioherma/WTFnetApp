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
            Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, DateTime, Task<ProbeCandidateRuntimeData>> captureProbeAsync,
            StartupProbeTimingConfig timing,
            StartupLogger startupLogger,
            Func<ProbeCandidateRuntimeData, ProbeCandidateRuntimeData, TimeSpan, TimeSpan, ProbeCandidateRuntimeData> mergeMeasuredRuntime,
            Func<string, string, string> appendNote)
        {
            warmup = new StartupSpeedifyWarmup(
                timing,
                getFastPingState,
                getShortWindowSnapshot,
                getMiniSpeedifyMiniSnap,
                isCandidateUsableForMeasurement,
                startupLogger);

            measurement = new StartupSpeedifyMeasurement(
                getMiniSpeedifyMiniSnap,
                captureProbeAsync,
                timing,
                startupLogger,
                mergeMeasuredRuntime,
                appendNote);
        }

        public async Task<SpeedifyProbeLifecycleResult> RunLifecycleAsync(
            ProbeCandidateDefinition candidate,
            DateTime activationRequestedUtc,
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
        private bool sawAssistCapableEvidence;
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
        public bool HasDualLinkEvidence => sawAssistCapableEvidence;
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
            int availableConnectionCount = ResolveAvailableConnectionCount(miniSnap);
            sawAssistCapableEvidence = sawAssistCapableEvidence || availableConnectionCount >= 2;
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
        private static int ResolveAvailableConnectionCount(MiniSpeedifyMiniSnap miniSnap)
        {
            if (miniSnap == null)
                return 0;

            if (miniSnap.AvailableConnectionCount >= 0)
                return miniSnap.AvailableConnectionCount;

            return Math.Max(0, miniSnap.ActiveConnectionCount);
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
                runtime.LossBurstDensity = snapshot.LossBurstDensity;
                runtime.BadTimeRatio = snapshot.BadTimeRatio;
                runtime.WorstSegmentLoss = snapshot.WorstSegmentLoss;
            }

            runtime.AvgConfidence = state?.Confidence ?? -1;
            return runtime;
        }
    }

    internal sealed class StartupSpeedifyMeasurement
    {
        private readonly Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap;
        private readonly Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, DateTime, Task<ProbeCandidateRuntimeData>> captureProbeAsync;
        private readonly StartupProbeTimingConfig timing;
        private readonly StartupLogger startupLogger;
        private readonly Func<ProbeCandidateRuntimeData, ProbeCandidateRuntimeData, TimeSpan, TimeSpan, ProbeCandidateRuntimeData> mergeMeasuredRuntime;
        private readonly Func<string, string, string> appendNote;

        private const int MainWindowSliceCount = 10;
        private const int PhaseMiniMinimumFreshSamples = 3;
        private const double PhaseMiniMinimumSpreadMs = 900.0;

        // =========================
        // CONSTRUCTOR
        // =========================

        public StartupSpeedifyMeasurement(
            Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap,
            Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, DateTime, Task<ProbeCandidateRuntimeData>> captureProbeAsync,
            StartupProbeTimingConfig timing,
            StartupLogger startupLogger,
            Func<ProbeCandidateRuntimeData, ProbeCandidateRuntimeData, TimeSpan, TimeSpan, ProbeCandidateRuntimeData> mergeMeasuredRuntime,
            Func<string, string, string> appendNote)
        {
            this.getMiniSpeedifyMiniSnap = getMiniSpeedifyMiniSnap;
            this.captureProbeAsync = captureProbeAsync;
            this.timing = timing;
            this.startupLogger = startupLogger;
            this.mergeMeasuredRuntime = mergeMeasuredRuntime;
            this.appendNote = appendNote;
        }

        public async Task<ProbeCandidateRuntimeData> RunAsync(
                ProbeCandidateDefinition candidate,
                DateTime activationRequestedUtc,
                string timeBucketKey,
                CancellationToken cancellationToken)
        {
            TimeSpan postStableMeasuredDuration = GetPostStableMeasuredDuration();
            DateTime? postStableMeasurementStartUtc = null;
            DateTime? postStableMeasurementEndUtc = null;
            StartupMiniSnapBuffer phaseMiniBuffer = new StartupMiniSnapBuffer(
                PhaseMiniMinimumFreshSamples,
                PhaseMiniMinimumSpreadMs,
                activationRequestedUtc);

            startupLogger?.LogEvent(candidate, "MAIN_SCORING_WINDOW_STARTED");
            postStableMeasurementStartUtc = DateTime.UtcNow;

            List<TimeSpan> measuredSlices = BuildMainWindowSlices(postStableMeasuredDuration);
            ProbeCandidateRuntimeData aggregateRuntime = null;
            TimeSpan accumulatedMeasured = TimeSpan.Zero;
            double adjustedLossWeightedSum = 0.0;
            double adjustedLossDurationSum = 0.0;

            for (int i = 0; i < measuredSlices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan slice = measuredSlices[i];
                DateTime sliceStartUtc = DateTime.UtcNow;

                ProbeCandidateRuntimeData sliceRuntime = await captureProbeAsync(candidate, TimeSpan.Zero, slice, sliceStartUtc);
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

                AccumulateStartupAdjustedLoss(
                    sliceRuntime,
                    slice,
                    ref adjustedLossWeightedSum,
                    ref adjustedLossDurationSum);

                aggregateRuntime = aggregateRuntime == null
                    ? sliceRuntime
                    : mergeMeasuredRuntime(aggregateRuntime, sliceRuntime, accumulatedMeasured, slice);
                accumulatedMeasured += slice;
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
            double effectiveSpeedifyLatencyMs = SelectEffectiveSpeedifyLatencyMs(
                phaseMiniBuffer.AverageAggLatencyMs,
                phaseMiniBuffer.AverageTunnelLatencyMs,
                aggregateRuntime.SpeedifyAggregateLatencyMs,
                aggregateRuntime.SpeedifyReportedLatencyMs);
            aggregateRuntime.StartupSpeedifyFastPingAgreementDeltaMs = ComputeStartupSpeedifyAgreementDeltaMs(
                effectiveSpeedifyLatencyMs,
                aggregateRuntime.AvgPingMs);
            aggregateRuntime.StartupSpeedifyFastPingAgreementGood =
                aggregateRuntime.StartupSpeedifyFastPingAgreementDeltaMs >= 0 &&
                aggregateRuntime.StartupSpeedifyFastPingAgreementDeltaMs <= 14.0;
            aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, miniConfidenceTag);
            startupLogger?.LogEvent(candidate, miniConfidenceTag);

            postStableMeasurementEndUtc = DateTime.UtcNow;
            double measuredPostStableMs = postStableMeasurementStartUtc.HasValue
                ? Math.Max(0.0, (postStableMeasurementEndUtc.Value - postStableMeasurementStartUtc.Value).TotalMilliseconds)
                : -1;

            startupLogger?.RegisterCandidateEvaluation(
                candidate,
                -1,
                measuredPostStableMs,
                false,
                aggregateRuntime,
                null);

            aggregateRuntime.ReachedUsableState = true;
            aggregateRuntime.MeasurementStarted = true;
            aggregateRuntime.MeasurementCompleted = true;
            aggregateRuntime.MeasurementStoppedEarly = false;
            aggregateRuntime.StabilizationReached = false;
            aggregateRuntime.StabilizationTimedOut = false;
            aggregateRuntime.StabilizationReason = "measurement-window-authoritative";
            aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, "measurement-window-authoritative");
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

        private static void AccumulateStartupAdjustedLoss(
            ProbeCandidateRuntimeData sliceRuntime,
            TimeSpan sliceDuration,
            ref double adjustedLossWeightedSum,
            ref double adjustedLossDurationSum)
        {
            if (sliceRuntime == null || sliceRuntime.AvgLossRate < 0)
                return;

            double sliceSeconds = Math.Max(0.0, sliceDuration.TotalSeconds);
            if (sliceSeconds <= 0.0)
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
            runtime.SpeedifyUsableLinkCount = -1;
            runtime.SpeedifyAvailableConnectionCount = -1;
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
            runtime.SpeedifyUsableLinkCount = Math.Max(0, miniSnap.UsableLinkCount);
            runtime.SpeedifyAvailableConnectionCount = ResolveAvailableConnectionCount(miniSnap);
            runtime.SpeedifyReportedLatencyMs = miniSnap.ReportedLatencyMs > 0 ? miniSnap.ReportedLatencyMs : -1;
            runtime.SpeedifyAggregateLatencyMs = miniSnap.AggregateLatencyMs > 0 ? miniSnap.AggregateLatencyMs : -1;
            runtime.SpeedifyAggregateJitterMs = miniSnap.AggregateJitterMs > 0 ? miniSnap.AggregateJitterMs : -1;
            runtime.AggregateCongested = miniSnap.AggregateCongested && runtime.TunnelConnected;
            runtime.SpeedifyFreshSampleCount = 1;
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

        private static int ResolveAvailableConnectionCount(MiniSpeedifyMiniSnap miniSnap)
        {
            if (miniSnap == null)
                return 0;

            if (miniSnap.AvailableConnectionCount >= 0)
                return miniSnap.AvailableConnectionCount;

            return Math.Max(0, miniSnap.ActiveConnectionCount);
        }

        private static double SelectEffectiveSpeedifyLatencyMs(
            double aggregateLatencyMs,
            double reportedLatencyMs,
            double runtimeAggregateLatencyMs,
            double runtimeReportedLatencyMs)
        {
            if (aggregateLatencyMs > 0)
                return aggregateLatencyMs;

            if (reportedLatencyMs > 0)
                return reportedLatencyMs;

            if (runtimeAggregateLatencyMs > 0)
                return runtimeAggregateLatencyMs;

            if (runtimeReportedLatencyMs > 0)
                return runtimeReportedLatencyMs;

            return -1;
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

        private TimeSpan GetPostStableMeasuredDuration()
        {
            if (timing.PostStableMeasuredDuration > TimeSpan.Zero)
                return timing.PostStableMeasuredDuration;

            return TimeSpan.FromSeconds(12.0);
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
                LossBurstDensity = source.LossBurstDensity,
                BadTimeRatio = source.BadTimeRatio,
                WorstSegmentLoss = source.WorstSegmentLoss,
                StaleRate = source.StaleRate,
                TunnelConnected = source.TunnelConnected,
                AggregateCongested = source.AggregateCongested,
                ActiveConnectionCount = source.ActiveConnectionCount,
                BackupConnectionCount = source.BackupConnectionCount,
                SpeedifyUsableLinkCount = source.SpeedifyUsableLinkCount,
                SpeedifyAvailableConnectionCount = source.SpeedifyAvailableConnectionCount,
                SpeedifyAggregateLatencyMs = source.SpeedifyAggregateLatencyMs,
                SpeedifyAggregateJitterMs = source.SpeedifyAggregateJitterMs,
                SpeedifyReportedLatencyMs = source.SpeedifyReportedLatencyMs,
                SpeedifyFreshSampleCount = source.SpeedifyFreshSampleCount,
                SpeedifyExpectedSampleCount = source.SpeedifyExpectedSampleCount,
                SpeedifyAggregateLatencyStdDevMs = source.SpeedifyAggregateLatencyStdDevMs,
                FirstFreshPostSwitchStatsUtc = source.FirstFreshPostSwitchStatsUtc,
                FirstFreshPostSwitchAggLatencyMs = source.FirstFreshPostSwitchAggLatencyMs,
                FirstFreshPostSwitchAggJitterMs = source.FirstFreshPostSwitchAggJitterMs,
                FirstFreshPostSwitchReportedLatencyMs = source.FirstFreshPostSwitchReportedLatencyMs,
                FirstFreshPostSwitchActiveConnectionCount = source.FirstFreshPostSwitchActiveConnectionCount,
                FirstFreshPostSwitchAggregateCongested = source.FirstFreshPostSwitchAggregateCongested,
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
                StartupSpeedifyCongestedEvidence = source.StartupSpeedifyCongestedEvidence,
                MeasuredServerFriendlyName = source.MeasuredServerFriendlyName,
                MeasuredServerTag = source.MeasuredServerTag,
                MeasuredRouteTarget = source.MeasuredRouteTarget,
                MeasuredServerCity = source.MeasuredServerCity,
                MeasuredServerPublicIp = source.MeasuredServerPublicIp,
                MeasuredServerIdentifier = source.MeasuredServerIdentifier,
                HasAuthoritativeIdentity = source.HasAuthoritativeIdentity,
                RestoredServerTarget = source.RestoredServerTarget
            };
        }
    }
}
