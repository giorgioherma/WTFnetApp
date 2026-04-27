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
                string measurementStartReason,
                double startLossRate,
                double lossSlopePerSec,
                ProbeCandidateRuntimeData.LossRecoveryState recoveryState,
                int flatLossConfirmCount,
                int flatLossConfirmRequired,
                double expectedLossDeltaAtStart,
                ProbeCandidateRuntimeData runtime)
            {
                IsUsable = isUsable;
                Reason = reason;
                WaitedMs = waitedMs;
                WarmupMiniTrusted = warmupMiniTrusted;
                WarmupTag = warmupTag;
                MeasurementStartReason = measurementStartReason;
                StartLossRate = startLossRate;
                LossSlopePerSec = lossSlopePerSec;
                RecoveryState = recoveryState;
                FlatLossConfirmCount = flatLossConfirmCount;
                FlatLossConfirmRequired = flatLossConfirmRequired;
                ExpectedLossDeltaAtStart = expectedLossDeltaAtStart;
                Runtime = runtime;
            }

            public bool IsUsable { get; }
            public string Reason { get; }
            public double WaitedMs { get; }
            public bool WarmupMiniTrusted { get; }
            public string WarmupTag { get; }
            public string MeasurementStartReason { get; }
            public double StartLossRate { get; }
            public double LossSlopePerSec { get; }
            public ProbeCandidateRuntimeData.LossRecoveryState RecoveryState { get; }
            public int FlatLossConfirmCount { get; }
            public int FlatLossConfirmRequired { get; }
            public double ExpectedLossDeltaAtStart { get; }
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
                getShortWindowSnapshot,
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
            double learnedExpectedLossRate,
            string timeBucketKey,
            CancellationToken cancellationToken)
        {
            StartupSpeedifyWarmupResult warmupResult = await warmup.RunAsync(
                candidate,
                activationRequestedUtc,
                learnedExpectedLossRate,
                cancellationToken);

            if (!warmupResult.IsUsable)
            {
                return new SpeedifyProbeLifecycleResult(
                    false,
                    warmupResult.Reason,
                    warmupResult.WaitedMs,
                    warmupResult.WarmupMiniTrusted,
                    warmupResult.WarmupTag,
                    warmupResult.MeasurementStartReason,
                    warmupResult.StartLossRate,
                    warmupResult.LossSlopePerSec,
                    warmupResult.RecoveryState,
                    warmupResult.FlatLossConfirmCount,
                    warmupResult.FlatLossConfirmRequired,
                    warmupResult.ExpectedLossDeltaAtStart,
                    null);
            }

            ProbeCandidateRuntimeData runtime = await measurement.RunAsync(
                candidate,
                activationRequestedUtc,
                learnedExpectedLossRate,
                warmupResult,
                timeBucketKey,
                cancellationToken);

            return new SpeedifyProbeLifecycleResult(
                true,
                warmupResult.Reason,
                warmupResult.WaitedMs,
                warmupResult.WarmupMiniTrusted,
                warmupResult.WarmupTag,
                warmupResult.MeasurementStartReason,
                warmupResult.StartLossRate,
                warmupResult.LossSlopePerSec,
                warmupResult.RecoveryState,
                warmupResult.FlatLossConfirmCount,
                warmupResult.FlatLossConfirmRequired,
                warmupResult.ExpectedLossDeltaAtStart,
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
            string warmupTag,
            string measurementStartReason,
            double startLossRate,
            double lossSlopePerSec,
            ProbeCandidateRuntimeData.LossRecoveryState recoveryState,
            int flatLossConfirmCount,
            int flatLossConfirmRequired,
            double expectedLossDeltaAtStart)
        {
            IsUsable = isUsable;
            Reason = reason;
            WaitedMs = waitedMs;
            WarmupMiniTrusted = warmupMiniTrusted;
            WarmupTag = warmupTag;
            MeasurementStartReason = measurementStartReason;
            StartLossRate = startLossRate;
            LossSlopePerSec = lossSlopePerSec;
            RecoveryState = recoveryState;
            FlatLossConfirmCount = flatLossConfirmCount;
            FlatLossConfirmRequired = flatLossConfirmRequired;
            ExpectedLossDeltaAtStart = expectedLossDeltaAtStart;
        }

        public bool IsUsable { get; }
        public string Reason { get; }
        public double WaitedMs { get; }
        public bool WarmupMiniTrusted { get; }
        public string WarmupTag { get; }
        public string MeasurementStartReason { get; }
        public double StartLossRate { get; }
        public double LossSlopePerSec { get; }
        public ProbeCandidateRuntimeData.LossRecoveryState RecoveryState { get; }
        public int FlatLossConfirmCount { get; }
        public int FlatLossConfirmRequired { get; }
        public double ExpectedLossDeltaAtStart { get; }
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
        private static readonly TimeSpan StartupTunnelHardBuffer = TimeSpan.FromSeconds(4);
        private const int WarmupMiniMinimumFreshSamples = 3;
        private const double WarmupMiniMinimumSpreadMs = 1200.0;
        private const int FlatLossConfirmationsRequired = 3;
        private const double FallingFastSlopePerSec = -0.006;
        private const double FallingSlopePerSec = -0.002;
        private const double FlatSlopeAbsPerSec = 0.0015;
        private const double WorseningSlopePerSec = 0.003;
        private const double LowLossRateThreshold = 0.025;
        private const double HighLossRateThreshold = 0.05;
        private const double AboveExpectedMeaningfulDelta = 0.02;
        private const double LossTrendWindowSeconds = 4.0;
        private const double ProjectionLookaheadSeconds = 2.0;
        private const double ProjectedRecoveryMaxStartLossRate = 0.07;

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
            double learnedExpectedLossRate,
            CancellationToken cancellationToken)
        {
            bool isSpeedifyCandidate =
                candidate != null &&
                candidate.Kind == ProbeCandidateKind.Speedify;

            if (!isSpeedifyCandidate)
            {
                bool immediateUsable = isCandidateUsableForMeasurement(candidate, out string immediateReason);
                return new StartupSpeedifyWarmupResult(
                    immediateUsable,
                    immediateReason,
                    0.0,
                    false,
                    "warmup-skipped",
                    "raw-immediate",
                    -1,
                    double.NaN,
                    ProbeCandidateRuntimeData.LossRecoveryState.Unknown,
                    0,
                    FlatLossConfirmationsRequired,
                    double.NaN);
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
            LossTrendTracker lossTrendTracker = new LossTrendTracker();

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
                FastPingSignalAnalyzer.FastPingAggregateSnapshot shortWindowSnapshot = getShortWindowSnapshot?.Invoke();
                lossTrendTracker.Observe(shortWindowSnapshot);
                MiniSpeedifyMiniSnap miniSnap = getMiniSpeedifyMiniSnap?.Invoke();
                bool acceptedMini = miniBuffer.TryAdd(miniSnap);
                if (!acceptedMini)
                    miniSnap = null;

                if (DateTime.UtcNow >= nextWarmupLogUtc)
                {
                    ProbeCandidateRuntimeData warmupSnapshot = BuildWarmupFastPingSnapshot(shortWindowSnapshot);
                    string warmupReason = reasonNow == "usable" ? "-" : reasonNow;
                    warmupReason = AppendTag(warmupReason, lossTrendTracker.BuildLogNote(learnedExpectedLossRate));
                    startupLogger?.LogStartupPhase(candidate, "warmup", warmupSnapshot, warmupReason, miniSnap, logMini: true);

                    nextWarmupLogUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }

                await Task.Delay(pollInterval, cancellationToken);
            }

            bool check1Done = false;
            bool check2Done = false;

            while (DateTime.UtcNow < gateForceUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool usableNow = isCandidateUsableForMeasurement(candidate, out string reasonNow);
                FastPingSignalAnalyzer.FastPingAggregateSnapshot shortWindowSnapshot = getShortWindowSnapshot?.Invoke();
                lossTrendTracker.Observe(shortWindowSnapshot);
                MiniSpeedifyMiniSnap miniSnap = getMiniSpeedifyMiniSnap?.Invoke();
                bool acceptedMini = miniBuffer.TryAdd(miniSnap);
                if (!acceptedMini)
                    miniSnap = null;

                DateTime nowUtc = DateTime.UtcNow;
                bool hardBufferComplete = (nowUtc - warmupStartUtc) >= StartupTunnelHardBuffer;
                bool hasFastPingSignal = HasFastPingSignal(shortWindowSnapshot);
                LossReadinessDecision decision = EvaluateLossReadiness(shortWindowSnapshot, lossTrendTracker, learnedExpectedLossRate, gateForceUtc, nowUtc);
                bool readinessGateSatisfied =
                    hardBufferComplete &&
                    shortWindowSnapshot != null &&
                    (usableNow || hasFastPingSignal) &&
                    decision.AllowStart;
                bool stableNow = readinessGateSatisfied;
                string stableReason = BuildStableReason(shortWindowSnapshot);
                string stableTags = BuildStableTags(lossTrendTracker);
                string decisionTag = decision.DecisionTag;

                if (!check1Done && nowUtc >= gateCheck1Utc)
                {
                    check1Done = true;

                    if (stableNow)
                    {
                        string warmupTag =
                            "fixed-settle-" + fixedSettle.TotalSeconds.ToString("0.#") + "s | stable-start-" +
                            Math.Max(StartupTunnelHardBuffer.TotalSeconds, check1.TotalSeconds).ToString("0.#") + "s";
                        warmupTag = AppendTag(warmupTag, decisionTag);

                        return new StartupSpeedifyWarmupResult(
                            true,
                            stableReason,
                            Math.Max(0.0, (DateTime.UtcNow - warmupStartUtc).TotalMilliseconds),
                            miniBuffer.IsSufficientForStartupTrust,
                            AppendTag(warmupTag, stableTags),
                            decision.StartReason,
                            shortWindowSnapshot?.AvgLossRate ?? -1,
                            decision.LossSlopePerSec,
                            decision.RecoveryState,
                            lossTrendTracker.FlatConfirmCount,
                            FlatLossConfirmationsRequired,
                            decision.ExpectedDelta);
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
                        warmupTag = AppendTag(warmupTag, decisionTag);

                        return new StartupSpeedifyWarmupResult(
                            true,
                            stableReason,
                            Math.Max(0.0, (DateTime.UtcNow - warmupStartUtc).TotalMilliseconds),
                            miniBuffer.IsSufficientForStartupTrust,
                            AppendTag(warmupTag, stableTags),
                            decision.StartReason,
                            shortWindowSnapshot?.AvgLossRate ?? -1,
                            decision.LossSlopePerSec,
                            decision.RecoveryState,
                            lossTrendTracker.FlatConfirmCount,
                            FlatLossConfirmationsRequired,
                            decision.ExpectedDelta);
                    }
                }

                if (DateTime.UtcNow >= nextWarmupLogUtc)
                {
                    ProbeCandidateRuntimeData warmupSnapshot = BuildWarmupFastPingSnapshot(shortWindowSnapshot);
                    string warmupReason = reasonNow == "usable"
                        ? (decision.AllowStart
                            ? (BuildStableReason(shortWindowSnapshot) + " | ready")
                            : "hold-loss-trend")
                        : reasonNow;
                    warmupReason = AppendTag(warmupReason, decisionTag);
                    warmupReason = AppendTag(warmupReason, lossTrendTracker.BuildLogNote(learnedExpectedLossRate));

                    startupLogger?.LogStartupPhase(candidate, "warmup", warmupSnapshot, warmupReason, miniSnap, logMini: true);

                    nextWarmupLogUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }

                await Task.Delay(pollInterval, cancellationToken);
            }

            FastPingSignalAnalyzer.FastPingAggregateSnapshot finalSnapshot = getShortWindowSnapshot?.Invoke();
            bool finalUsable = isCandidateUsableForMeasurement(candidate, out string finalReason);
            bool hasFinalFastPingSignal = HasFastPingSignal(finalSnapshot);
            bool forceUsable = finalUsable || hasFinalFastPingSignal;
            LossReadinessDecision forceDecision = EvaluateLossReadiness(finalSnapshot, lossTrendTracker, learnedExpectedLossRate, gateForceUtc, DateTime.UtcNow);
            if (forceUsable)
            {
                finalReason = "force-start-timeout";
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
            if (forceUsable)
                finalTag = AppendTag(finalTag, finalReason);
            finalTag = AppendTag(finalTag, forceDecision.DecisionTag);

            return new StartupSpeedifyWarmupResult(
                forceUsable,
                finalReason,
                Math.Max(0.0, (DateTime.UtcNow - warmupStartUtc).TotalMilliseconds),
                warmupMiniTrusted,
                finalTag,
                forceDecision.StartReason,
                finalSnapshot?.AvgLossRate ?? -1,
                forceDecision.LossSlopePerSec,
                forceDecision.RecoveryState,
                lossTrendTracker.FlatConfirmCount,
                FlatLossConfirmationsRequired,
                forceDecision.ExpectedDelta);
        }

        private static bool HasFastPingSignal(FastPingSignalAnalyzer.FastPingAggregateSnapshot snapshot)
        {
            return snapshot != null &&
                   (snapshot.IsValid ||
                    snapshot.AvgPingMs >= 0 ||
                    snapshot.P95PingMs >= 0 ||
                    snapshot.AvgLossRate >= 0 ||
                    snapshot.AvgJitterMs >= 0 ||
                    snapshot.SpikeDensityPerMinute >= 0);
        }

        private static string BuildStableReason(FastPingSignalAnalyzer.FastPingAggregateSnapshot snapshot)
        {
            bool goodQuality =
                snapshot != null &&
                snapshot.AvgLossRate >= 0 &&
                snapshot.AvgLossRate <= 0.03 &&
                (snapshot.AvgJitterMs < 0 || snapshot.AvgJitterMs <= 15.0);

            return goodQuality
                ? "activated-stable-good-quality"
                : "activated-stable-bad-quality";
        }

        private static string BuildStableTags(LossTrendTracker tracker)
        {
            string tags = string.Empty;
            if (tracker.RecoveryState == ProbeCandidateRuntimeData.LossRecoveryState.Flat)
                tags = AppendTag(tags, "loss-flat");
            if (tracker.RecoveryState == ProbeCandidateRuntimeData.LossRecoveryState.FallingFast)
                tags = AppendTag(tags, "loss-falling-fast");
            return tags;
        }

        private LossReadinessDecision EvaluateLossReadiness(
            FastPingSignalAnalyzer.FastPingAggregateSnapshot snapshot,
            LossTrendTracker tracker,
            double learnedExpectedLossRate,
            DateTime gateForceUtc,
            DateTime nowUtc)
        {
            double currentLoss = snapshot?.AvgLossRate ?? -1;
            double slopeRaw = tracker.SlopePerSecond;
            double slopeSmoothed = tracker.SmoothedSlopePerSecond;
            ProbeCandidateRuntimeData.LossRecoveryState recoveryState = tracker.RecoveryState;
            LossTier lossTier = ClassifyLossTier(currentLoss);
            bool slopeRawFinite = IsFinite(slopeRaw);
            bool slopeSmoothedFinite = IsFinite(slopeSmoothed);
            double projectedLoss =
                currentLoss >= 0 && slopeSmoothedFinite
                    ? currentLoss + (slopeSmoothed * ProjectionLookaheadSeconds)
                    : double.NaN;
            LossTier? projectedLossTier = IsFinite(projectedLoss)
                ? ClassifyLossTier(projectedLoss)
                : (LossTier?)null;
            double expectedDelta = learnedExpectedLossRate >= 0 && currentLoss >= 0
                ? currentLoss - learnedExpectedLossRate
                : double.NaN;
            bool nearFlat =
                slopeSmoothedFinite &&
                Math.Abs(slopeSmoothed) < (FlatSlopeAbsPerSec * 1.5);
            bool rawFallingFast =
                slopeRawFinite &&
                slopeRaw <= FallingFastSlopePerSec;
            bool smoothFallingFast =
                slopeSmoothedFinite &&
                slopeSmoothed <= FallingFastSlopePerSec;
            bool recoveryMomentum =
                smoothFallingFast || (rawFallingFast && tracker.FlatConfirmCount >= 1);

            string decisionTag =
                "lossSlopeRaw=" + FormatSlope(slopeRaw) +
                " | lossSlopeSmooth=" + FormatSlope(slopeSmoothed) +
                " | recovery=" + recoveryState +
                " | flatConfirm=" + tracker.FlatConfirmCount + "/" + FlatLossConfirmationsRequired +
                " | nearFlat=" + (nearFlat ? "true" : "false") +
                " | recoveryMomentum=" + (recoveryMomentum ? "true" : "false") +
                " | lossTier=" + lossTier +
                " | projectedLoss=" + (IsFinite(projectedLoss) ? projectedLoss.ToString("0.000") : "--") +
                " | projectedLossTier=" + (projectedLossTier.HasValue ? projectedLossTier.Value.ToString() : "--");
            if (learnedExpectedLossRate >= 0)
            {
                decisionTag = AppendTag(decisionTag, "expectedLoss=" + learnedExpectedLossRate.ToString("0.000"));
                decisionTag = AppendTag(decisionTag, "expectedDelta=" + (double.IsNaN(expectedDelta) ? "--" : expectedDelta.ToString("0.000")));
            }

            bool timeoutReached = nowUtc >= gateForceUtc;
            if (timeoutReached)
            {
                string timeoutReason = BuildTimeoutForcedReason(recoveryState);
                return new LossReadinessDecision(true, timeoutReason, timeoutReason, decisionTag, slopeSmoothed, recoveryState, expectedDelta);
            }

            if (recoveryState == ProbeCandidateRuntimeData.LossRecoveryState.Worsening)
                return new LossReadinessDecision(false, null, "hold-loss-worsening", decisionTag, slopeSmoothed, recoveryState, expectedDelta);

            bool lowLoss = currentLoss >= 0 && currentLoss <= LowLossRateThreshold;
            bool lowLossSlopeSafe =
                slopeSmoothedFinite &&
                slopeSmoothed < (WorseningSlopePerSec * 0.5);
            if (lowLoss)
            {
                if (lowLossSlopeSafe)
                    return new LossReadinessDecision(true, "low-loss", "allow-low-loss", decisionTag, slopeSmoothed, recoveryState, expectedDelta);

                return new LossReadinessDecision(false, null, "hold-low-loss-slope-uncertain", decisionTag, slopeSmoothed, recoveryState, expectedDelta);
            }

            bool highLoss = currentLoss >= HighLossRateThreshold;
            bool projectedLowLoss =
                IsFinite(projectedLoss) &&
                projectedLoss <= LowLossRateThreshold;
            bool projectedRecoveryAllowed =
                projectedLowLoss &&
                (recoveryState == ProbeCandidateRuntimeData.LossRecoveryState.Falling ||
                 recoveryState == ProbeCandidateRuntimeData.LossRecoveryState.FallingFast) &&
                currentLoss <= ProjectedRecoveryMaxStartLossRate &&
                slopeSmoothedFinite &&
                slopeSmoothed < FallingSlopePerSec &&
                recoveryState != ProbeCandidateRuntimeData.LossRecoveryState.Worsening;
            if (projectedRecoveryAllowed)
            {
                return new LossReadinessDecision(true, "projected-recovery", "allow-projected-recovery", decisionTag, slopeSmoothed, recoveryState, expectedDelta);
            }

            if (recoveryState == ProbeCandidateRuntimeData.LossRecoveryState.FallingFast && highLoss)
                return new LossReadinessDecision(false, null, "hold-loss-falling-fast-high", decisionTag, slopeSmoothed, recoveryState, expectedDelta);

            if (recoveryState == ProbeCandidateRuntimeData.LossRecoveryState.FallingFast)
                return new LossReadinessDecision(false, null, "hold-loss-falling-fast", decisionTag, slopeSmoothed, recoveryState, expectedDelta);

            if (recoveryState == ProbeCandidateRuntimeData.LossRecoveryState.Falling)
            {
                string explainTag = !double.IsNaN(expectedDelta) && expectedDelta <= AboveExpectedMeaningfulDelta
                    ? "near-expected-context"
                    : "above-expected-context";
                return new LossReadinessDecision(false, null, AppendTag("hold-loss-falling", explainTag), decisionTag, slopeSmoothed, recoveryState, expectedDelta);
            }

            if (nearFlat &&
                tracker.FlatConfirmCount >= FlatLossConfirmationsRequired &&
                Math.Abs(slopeSmoothed) < (FlatSlopeAbsPerSec * 1.5))
            {
                return new LossReadinessDecision(true, "loss-flat-confirmed", "allow-flat-confirmed", decisionTag, slopeSmoothed, recoveryState, expectedDelta);
            }

            return new LossReadinessDecision(false, null, "hold-await-flat-confirm", decisionTag, slopeSmoothed, recoveryState, expectedDelta);
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

        private ProbeCandidateRuntimeData BuildWarmupFastPingSnapshot(FastPingSignalAnalyzer.FastPingAggregateSnapshot snapshot = null)
        {
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

        private sealed class LossTrendTracker
        {
            private readonly Queue<(DateTime Utc, double Loss)> lossSamples = new Queue<(DateTime Utc, double Loss)>();
            private int noSignalSampleStreak;
            public int FlatConfirmCount { get; private set; }
            public double SlopePerSecond { get; private set; } = double.NaN;
            public double SmoothedSlopePerSecond { get; private set; } = double.NaN;
            public ProbeCandidateRuntimeData.LossRecoveryState RecoveryState { get; private set; } = ProbeCandidateRuntimeData.LossRecoveryState.Unknown;

            public void Observe(FastPingSignalAnalyzer.FastPingAggregateSnapshot snapshot)
            {
                double currentLoss = snapshot?.AvgLossRate ?? -1;
                if (currentLoss < 0)
                {
                    noSignalSampleStreak++;
                    if (noSignalSampleStreak >= 4 && FlatConfirmCount > 0)
                        FlatConfirmCount = Math.Max(0, FlatConfirmCount - 1);
                    return;
                }
                noSignalSampleStreak = 0;

                DateTime sampleUtc = snapshot?.WindowEndUtc ?? DateTime.UtcNow;
                if (sampleUtc == DateTime.MinValue)
                    sampleUtc = DateTime.UtcNow;

                if (lossSamples.Count > 0 && sampleUtc <= lossSamples.Last().Utc)
                    sampleUtc = lossSamples.Last().Utc.AddMilliseconds(1);

                lossSamples.Enqueue((sampleUtc, currentLoss));
                DateTime minimumUtc = sampleUtc.AddSeconds(-LossTrendWindowSeconds);
                while (lossSamples.Count > 2 && lossSamples.Peek().Utc < minimumUtc)
                    lossSamples.Dequeue();

                SlopePerSecond = ComputeSlopePerSecond();
                if (double.IsNaN(SmoothedSlopePerSecond) || double.IsInfinity(SmoothedSlopePerSecond))
                    SmoothedSlopePerSecond = SlopePerSecond;
                else if (!double.IsNaN(SlopePerSecond) && !double.IsInfinity(SlopePerSecond))
                    SmoothedSlopePerSecond = (SmoothedSlopePerSecond * 0.65) + (SlopePerSecond * 0.35);

                RecoveryState = ResolveRecoveryState(SmoothedSlopePerSecond);
                if (RecoveryState == ProbeCandidateRuntimeData.LossRecoveryState.Flat)
                {
                    FlatConfirmCount++;
                }
                else if (RecoveryState == ProbeCandidateRuntimeData.LossRecoveryState.FallingFast ||
                         RecoveryState == ProbeCandidateRuntimeData.LossRecoveryState.Worsening)
                {
                    FlatConfirmCount = 0;
                }
                else if (RecoveryState == ProbeCandidateRuntimeData.LossRecoveryState.Falling)
                {
                    FlatConfirmCount = Math.Max(0, FlatConfirmCount - 1);
                }
                else if (RecoveryState == ProbeCandidateRuntimeData.LossRecoveryState.Unknown && FlatConfirmCount > 0)
                {
                    FlatConfirmCount = Math.Max(0, FlatConfirmCount - 1);
                }
            }

            public string BuildLogNote(double learnedExpectedLossRate)
            {
                double currentLoss = lossSamples.Count > 0 ? lossSamples.Last().Loss : -1;
                string note =
                    "lossSlopeRaw=" + FormatSlope(SlopePerSecond) +
                    " | lossSlopeSmoothed=" + FormatSlope(SmoothedSlopePerSecond) +
                    " | lossRecoveryState=" + RecoveryState +
                    " | flatLossConfirmCount=" + FlatConfirmCount + "/" + FlatLossConfirmationsRequired +
                    " | lossTier=" + ClassifyLossTier(currentLoss);
                if (learnedExpectedLossRate >= 0 && currentLoss >= 0)
                {
                    note = AppendTag(note, "expectedLoss=" + learnedExpectedLossRate.ToString("0.000"));
                    note = AppendTag(note, "distanceToExpected=" + (currentLoss - learnedExpectedLossRate).ToString("0.000"));
                }
                return note;
            }

            private double ComputeSlopePerSecond()
            {
                if (lossSamples.Count < 2)
                    return double.NaN;

                (DateTime Utc, double Loss) first = lossSamples.Peek();
                (DateTime Utc, double Loss) last = lossSamples.Last();
                double elapsedSec = (last.Utc - first.Utc).TotalSeconds;
                if (elapsedSec <= 0.01)
                    return double.NaN;
                return (last.Loss - first.Loss) / elapsedSec;
            }

            private static ProbeCandidateRuntimeData.LossRecoveryState ResolveRecoveryState(double slopePerSecond)
            {
                if (double.IsNaN(slopePerSecond) || double.IsInfinity(slopePerSecond))
                    return ProbeCandidateRuntimeData.LossRecoveryState.Unknown;
                if (slopePerSecond <= FallingFastSlopePerSec)
                    return ProbeCandidateRuntimeData.LossRecoveryState.FallingFast;
                if (slopePerSecond <= FallingSlopePerSec)
                    return ProbeCandidateRuntimeData.LossRecoveryState.Falling;
                if (Math.Abs(slopePerSecond) < FlatSlopeAbsPerSec)
                    return ProbeCandidateRuntimeData.LossRecoveryState.Flat;
                if (slopePerSecond >= WorseningSlopePerSec)
                    return ProbeCandidateRuntimeData.LossRecoveryState.Worsening;
                return ProbeCandidateRuntimeData.LossRecoveryState.Unknown;
            }
        }

        private readonly struct LossReadinessDecision
        {
            public LossReadinessDecision(
                bool allowStart,
                string startReason,
                string decisionReason,
                string decisionTag,
                double lossSlopePerSec,
                ProbeCandidateRuntimeData.LossRecoveryState recoveryState,
                double expectedDelta)
            {
                AllowStart = allowStart;
                StartReason = startReason;
                DecisionReason = decisionReason;
                DecisionTag = AppendTag(decisionReason, decisionTag);
                LossSlopePerSec = lossSlopePerSec;
                RecoveryState = recoveryState;
                ExpectedDelta = expectedDelta;
            }

            public bool AllowStart { get; }
            public string StartReason { get; }
            public string DecisionReason { get; }
            public string DecisionTag { get; }
            public double LossSlopePerSec { get; }
            public ProbeCandidateRuntimeData.LossRecoveryState RecoveryState { get; }
            public double ExpectedDelta { get; }
        }

        private static string FormatSlope(double slopePerSec)
        {
            if (double.IsNaN(slopePerSec) || double.IsInfinity(slopePerSec))
                return "--";
            return slopePerSec.ToString("0.000") + "/s";
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private enum LossTier
        {
            Unknown = 0,
            Good = 1,
            Acceptable = 2,
            Risky = 3,
            Bad = 4
        }

        private static LossTier ClassifyLossTier(double currentLoss)
        {
            if (currentLoss < 0)
                return LossTier.Unknown;
            if (currentLoss < 0.025)
                return LossTier.Good;
            if (currentLoss < 0.06)
                return LossTier.Acceptable;
            if (currentLoss < 0.12)
                return LossTier.Risky;
            return LossTier.Bad;
        }

        private static string BuildTimeoutForcedReason(ProbeCandidateRuntimeData.LossRecoveryState recoveryState)
        {
            switch (recoveryState)
            {
                case ProbeCandidateRuntimeData.LossRecoveryState.Flat:
                    return "timeout-forced-flat";
                case ProbeCandidateRuntimeData.LossRecoveryState.Falling:
                    return "timeout-forced-falling";
                case ProbeCandidateRuntimeData.LossRecoveryState.FallingFast:
                    return "timeout-forced-falling-fast";
                case ProbeCandidateRuntimeData.LossRecoveryState.Worsening:
                    return "timeout-forced-worsening";
                default:
                    return "timeout-forced-unknown";
            }
        }
    }

    internal sealed class StartupSpeedifyMeasurement
    {
        private readonly Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot;
        private readonly Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap;
        private readonly Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, DateTime, Task<ProbeCandidateRuntimeData>> captureProbeAsync;
        private readonly StartupProbeTimingConfig timing;
        private readonly StartupLogger startupLogger;
        private readonly Func<ProbeCandidateRuntimeData, ProbeCandidateRuntimeData, TimeSpan, TimeSpan, ProbeCandidateRuntimeData> mergeMeasuredRuntime;
        private readonly Func<string, string, string> appendNote;

        private const int MainWindowSliceCount = 10;
        private const int PhaseMiniMinimumFreshSamples = 3;
        private const double PhaseMiniMinimumSpreadMs = 900.0;
        private static readonly TimeSpan StartupTableRowLogInterval = TimeSpan.FromMilliseconds(500);

        // =========================
        // CONSTRUCTOR
        // =========================

        public StartupSpeedifyMeasurement(
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
            Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap,
            Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, DateTime, Task<ProbeCandidateRuntimeData>> captureProbeAsync,
            StartupProbeTimingConfig timing,
            StartupLogger startupLogger,
            Func<ProbeCandidateRuntimeData, ProbeCandidateRuntimeData, TimeSpan, TimeSpan, ProbeCandidateRuntimeData> mergeMeasuredRuntime,
            Func<string, string, string> appendNote)
        {
            this.getShortWindowSnapshot = getShortWindowSnapshot;
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
                double learnedExpectedLossRate,
                StartupSpeedifyWarmupResult warmupResult,
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

            string measurementStartReason = string.IsNullOrWhiteSpace(warmupResult.MeasurementStartReason)
                ? "loss-flat-confirmed"
                : warmupResult.MeasurementStartReason;
            startupLogger?.LogEvent(candidate, "MAIN_SCORING_WINDOW_STARTED | reason=" + measurementStartReason);
            postStableMeasurementStartUtc = DateTime.UtcNow;

            List<TimeSpan> measuredSlices = BuildMainWindowSlices(postStableMeasuredDuration);
            ProbeCandidateRuntimeData aggregateRuntime = null;
            TimeSpan accumulatedMeasured = TimeSpan.Zero;
            double adjustedLossWeightedSum = 0.0;
            double adjustedLossDurationSum = 0.0;
            DateTime lastLoggedSnapshotUtc = DateTime.MinValue;

            for (int i = 0; i < measuredSlices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TimeSpan slice = measuredSlices[i];
                DateTime sliceStartUtc = DateTime.UtcNow;

                Task<ProbeCandidateRuntimeData> sliceCaptureTask = captureProbeAsync(candidate, TimeSpan.Zero, slice, sliceStartUtc);
                SliceCaptureResult sliceResult = await LogMeasurementRowsWhileRunningAsync(
                    candidate,
                    "post-stable",
                    sliceCaptureTask,
                    cancellationToken,
                    lastLoggedSnapshotUtc);
                ProbeCandidateRuntimeData sliceRuntime = sliceResult.Runtime;
                lastLoggedSnapshotUtc = sliceResult.LastLoggedSnapshotUtc;
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
            aggregateRuntime.MeasurementStartReason = measurementStartReason;
            aggregateRuntime.MeasurementStartLossRate = warmupResult.StartLossRate;
            aggregateRuntime.MeasurementEndLossRate = aggregateRuntime.AvgLossRate;
            aggregateRuntime.LossSlopePerSec = warmupResult.LossSlopePerSec;
            aggregateRuntime.StartupLossRecoveryState = warmupResult.RecoveryState;
            aggregateRuntime.FlatLossConfirmCount = warmupResult.FlatLossConfirmCount;
            aggregateRuntime.FlatLossConfirmRequired = warmupResult.FlatLossConfirmRequired;
            aggregateRuntime.LearnedExpectedLossRate = learnedExpectedLossRate;
            aggregateRuntime.ExpectedLossDeltaAtStart = warmupResult.ExpectedLossDeltaAtStart;

            if (aggregateRuntime.MeasurementStartLossRate >= 0 &&
                aggregateRuntime.MeasurementEndLossRate >= 0)
            {
                double recoveredLoss = aggregateRuntime.MeasurementStartLossRate - aggregateRuntime.MeasurementEndLossRate;
                if (recoveredLoss >= 0.04 && aggregateRuntime.LossSlopePerSec <= -0.002)
                {
                    aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, "settling-window-loss-recovered");
                    startupLogger?.LogEvent(candidate, "SETTLING_WINDOW | lossRecovered=" + recoveredLoss.ToString("0.000"));
                }
            }

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

        private async Task<SliceCaptureResult> LogMeasurementRowsWhileRunningAsync(
            ProbeCandidateDefinition candidate,
            string phase,
            Task<ProbeCandidateRuntimeData> sliceCaptureTask,
            CancellationToken cancellationToken,
            DateTime lastLoggedSnapshotUtc)
        {
            if (sliceCaptureTask == null)
                return new SliceCaptureResult(null, lastLoggedSnapshotUtc);

            while (!sliceCaptureTask.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lastLoggedSnapshotUtc = TryLogMeasurementSnapshot(candidate, phase, lastLoggedSnapshotUtc);
                Task delayTask = Task.Delay(StartupTableRowLogInterval, cancellationToken);
                await Task.WhenAny(sliceCaptureTask, delayTask);
            }

            lastLoggedSnapshotUtc = TryLogMeasurementSnapshot(candidate, phase, lastLoggedSnapshotUtc);
            return new SliceCaptureResult(await sliceCaptureTask, lastLoggedSnapshotUtc);
        }

        private DateTime TryLogMeasurementSnapshot(
            ProbeCandidateDefinition candidate,
            string phase,
            DateTime lastLoggedSnapshotUtc)
        {
            FastPingSignalAnalyzer.FastPingAggregateSnapshot window = getShortWindowSnapshot?.Invoke();
            if (window == null || !window.IsValid)
                return lastLoggedSnapshotUtc;

            DateTime snapshotUtc = window.WindowEndUtc != DateTime.MinValue ? window.WindowEndUtc : DateTime.MinValue;
            if (snapshotUtc != DateTime.MinValue &&
                lastLoggedSnapshotUtc != DateTime.MinValue &&
                snapshotUtc <= lastLoggedSnapshotUtc)
            {
                return lastLoggedSnapshotUtc;
            }

            if (snapshotUtc != DateTime.MinValue)
                lastLoggedSnapshotUtc = snapshotUtc;

            ProbeCandidateRuntimeData rowRuntime = new ProbeCandidateRuntimeData
            {
                Candidate = candidate,
                AvgLossRate = window.AvgLossRate,
                AvgPingMs = window.AvgPingMs,
                P95PingMs = window.P95PingMs,
                AvgJitterMs = window.AvgJitterMs,
                P95JitterMs = window.P95JitterMs,
                SpikeDensityPerMinute = window.SpikeDensityPerMinute,
                AvgConfidence = window.AvgConfidence
            };

            startupLogger?.LogStartupPhase(candidate, phase, rowRuntime, "-", null, logMini: false);
            return lastLoggedSnapshotUtc;
        }

        private readonly struct SliceCaptureResult
        {
            public SliceCaptureResult(ProbeCandidateRuntimeData runtime, DateTime lastLoggedSnapshotUtc)
            {
                Runtime = runtime;
                LastLoggedSnapshotUtc = lastLoggedSnapshotUtc;
            }

            public ProbeCandidateRuntimeData Runtime { get; }
            public DateTime LastLoggedSnapshotUtc { get; }
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

            return TimeSpan.FromSeconds(17.0);
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
                RestoredServerTarget = source.RestoredServerTarget,
                LossSlopePerSec = source.LossSlopePerSec,
                StartupLossRecoveryState = source.StartupLossRecoveryState,
                FlatLossConfirmCount = source.FlatLossConfirmCount,
                FlatLossConfirmRequired = source.FlatLossConfirmRequired,
                LearnedExpectedLossRate = source.LearnedExpectedLossRate,
                ExpectedLossDeltaAtStart = source.ExpectedLossDeltaAtStart,
                MeasurementStartLossRate = source.MeasurementStartLossRate,
                MeasurementEndLossRate = source.MeasurementEndLossRate,
                MeasurementStartReason = source.MeasurementStartReason
            };
        }
    }
}
