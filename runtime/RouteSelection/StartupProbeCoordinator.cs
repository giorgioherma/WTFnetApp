using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WTFnetApp
{
    public sealed class StartupProbeCoordinator
    {
        private readonly struct CandidateWarmupResult
        {
            public CandidateWarmupResult(
                bool isUsable,
                string reason,
                double waitedMs,
                bool startupTrustEstablished,
                string warmupTag)
            {
                IsUsable = isUsable;
                Reason = reason;
                WaitedMs = waitedMs;
                StartupTrustEstablished = startupTrustEstablished;
                WarmupTag = warmupTag;
            }

            public bool IsUsable { get; }
            public string Reason { get; }
            public double WaitedMs { get; }
            public bool StartupTrustEstablished { get; }
            public string WarmupTag { get; }
        }

        private sealed class SpeedifyWarmupWindowBuffer
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
            public DateTime LastAcceptedTelemetryUtc => lastAcceptedUtc;
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

                if (lastAcceptedUtc != DateTime.MinValue &&
                    snapshot.TimeUtc == lastAcceptedUtc)
                {
                    ignoredReason = "sticky-same-timestamp";
                    return false;
                }

                if (lastAcceptedUtc != DateTime.MinValue &&
                    snapshot.TimeUtc < lastAcceptedUtc)
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
                    if (value > 0)
                    {
                        sum += value;
                        count++;
                    }
                }

                return count > 0 ? sum / count : -1;
            }
        }

        private sealed class RoutedCandidateOrderingEntry
        {
            public ProbeCandidateDefinition Candidate { get; set; }
            public ServerProbeLearningStore.HistoricalRouteOrderingScore Historical { get; set; }
            public int OriginalIndex { get; set; }
            public int FreshnessRank { get; set; }
        }

        private readonly struct StartupLossObservation
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

        private struct RawPreMeasureSignal
        {
            public int Count;
            public double LossSum;
            public double JitterSum;
            public double PingSum;
            public double SpikeSum;

            public void Observe(FastPingSignalAnalyzer.FastPingAggregateSnapshot window)
            {
                if (window == null || !window.IsValid)
                    return;

                Count++;
                if (window.AvgLossRate >= 0)
                    LossSum += window.AvgLossRate;
                if (window.AvgJitterMs >= 0)
                    JitterSum += window.AvgJitterMs;
                if (window.AvgPingMs >= 0)
                    PingSum += window.AvgPingMs;
                if (window.SpikeDensityPerMinute >= 0)
                    SpikeSum += window.SpikeDensityPerMinute;
            }
        }

        private enum StartupLossGateState
        {
            IgnoringLoss,
            LossRecoveryCandidate,
            LossValidated
        }

        private sealed class StartupLossGateTracker
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
                if (observedLoss < 0)
                    return;

                if (State == StartupLossGateState.LossValidated)
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

        private sealed class StartupLossStabilizationTracker
        {
            private const double StabilizationDeltaThresholdPercentagePoints = 3.0;

            private double? previousLossRate;
            private int consecutiveSmallDrops;
            private bool previousTransitionWasPositiveSmallDrop;

            public bool IsStable { get; private set; }
            public string StableReason { get; private set; }

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
                bool positiveSmallDrop =
                    deltaPercentagePoints > 0.0 &&
                    deltaPercentagePoints <= StabilizationDeltaThresholdPercentagePoints;
                bool flatOrRising = deltaPercentagePoints <= 0.0;

                if (smallDropOrLess)
                    consecutiveSmallDrops++;
                else
                    consecutiveSmallDrops = 0;

                if (previousTransitionWasPositiveSmallDrop && flatOrRising)
                {
                    IsStable = true;
                    StableReason = "small-drop-then-rise";
                    return true;
                }

                if (consecutiveSmallDrops >= 2)
                {
                    IsStable = true;
                    StableReason = "two-small-drops";
                    return true;
                }

                previousTransitionWasPositiveSmallDrop = positiveSmallDrop;
                return false;
            }
        }

        private readonly Func<Task<bool>> disableSpeedifyAsync;
        private readonly Func<string, Task<bool>> activateSpeedifyAsync;
        private readonly Func<FastPingState> getFastPingState;
        private readonly Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot;
        private readonly Func<StartupDecisionSnapshot> getWarmupTelemetry;
        private readonly Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, Task<ProbeCandidateRuntimeData>> captureProbeAsync;
        private readonly ServerProbeLearningStore learningStore;
        private readonly StartupProbeTimingConfig timing;
        private readonly Action<string> log;
        private readonly ProbeDecisionStabilityState stabilityState = new ProbeDecisionStabilityState();

        private const double CloseScoreOrderingMargin = 6.0;
        private const int MainWindowSliceCount = 10;
        private const double SpeedifyFastPingStableGapPercentagePoints = 4.0;
        private const double FastPingReferenceMinimumConfidence = 0.50;
        private const double FastPingReferenceMaximumLossRate = 0.10;
        private static readonly TimeSpan StabilizationObserveSliceDuration = TimeSpan.FromMilliseconds(1500);

        private static readonly HashSet<string> SpeedifyWarmupSoftReasons =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "stale route state",
                "missing measurement telemetry",
                "no fresh measurement telemetry",
                "confidence too low"
            };

        public StartupProbeCoordinator(
            Func<Task<bool>> disableSpeedifyAsync,
            Func<string, Task<bool>> activateSpeedifyAsync,
            Func<FastPingState> getFastPingState,
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
            Func<StartupDecisionSnapshot> getWarmupTelemetry,
            Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, Task<ProbeCandidateRuntimeData>> captureProbeAsync,
            ServerProbeLearningStore learningStore,
            StartupProbeTimingConfig timing = null,
            Action<string> log = null)
        {
            this.disableSpeedifyAsync = disableSpeedifyAsync ?? throw new ArgumentNullException(nameof(disableSpeedifyAsync));
            this.activateSpeedifyAsync = activateSpeedifyAsync ?? throw new ArgumentNullException(nameof(activateSpeedifyAsync));
            this.getFastPingState = getFastPingState ?? throw new ArgumentNullException(nameof(getFastPingState));
            this.getShortWindowSnapshot = getShortWindowSnapshot ?? throw new ArgumentNullException(nameof(getShortWindowSnapshot));
            this.getWarmupTelemetry = getWarmupTelemetry;
            this.captureProbeAsync = captureProbeAsync ?? throw new ArgumentNullException(nameof(captureProbeAsync));
            this.learningStore = learningStore ?? throw new ArgumentNullException(nameof(learningStore));
            this.timing = timing ?? throw new ArgumentNullException(nameof(timing));
            this.log = log;
        }

        public async Task<StartupProbeResult> RunAsync(
            IEnumerable<ProbeCandidateDefinition> orderedSpeedifyCandidates,
            CancellationToken cancellationToken = default)
        {
            if (orderedSpeedifyCandidates == null)
                throw new ArgumentNullException(nameof(orderedSpeedifyCandidates));

            learningStore.SeedDefaultsIfEmpty();

            List<ProbeCandidateDefinition> originalRoutedOrder = orderedSpeedifyCandidates
                .Where(x => x != null)
                .ToList();

            StartupProbeResult output = new StartupProbeResult();

            string timeBucket = ProbeScoring.GetCurrentTimeBucketKey(DateTime.Now);

            List<ProbeCandidateDefinition> ordered = ReorderRoutedCandidatesForStartup(
                originalRoutedOrder,
                timeBucket);

            ProbeCandidateDefinition rawCandidate = new ProbeCandidateDefinition
            {
                RouteKey = "raw",
                DisplayName = "No Speedify",
                ConnectTarget = string.Empty,
                Kind = ProbeCandidateKind.Raw
            };

            Log("startup-probe | raw phase | disable speedify");

            cancellationToken.ThrowIfCancellationRequested();

            bool rawDisabled = await disableSpeedifyAsync();
            if (!rawDisabled)
            {
                StartupProbeResult failed = new StartupProbeResult
                {
                    SummaryText = "startup-probe | raw baseline failed | speedify did not disable cleanly"
                };

                Log(failed.SummaryText);
                return failed;
            }

            await BeginTransitionGraceAsync("raw-disable", cancellationToken);

            ProbeReadinessResult readiness = new ProbeReadinessResult
            {
                Kind = ProbeReadinessKind.TimeoutFallback,
                Reason = "equal protected startup + equal measured window"
            };

            output.RawReadiness = readiness;
            learningStore.UpdateFromReadinessSnapshot(
                rawCandidate.RouteKey,
                timeBucket,
                getShortWindowSnapshot(),
                readiness.Kind);

            Log("startup-probe | raw readiness | " + readiness.Kind + " | " + readiness.Reason);

            bool rawUsable = IsCandidateUsableForMeasurement(rawCandidate, out string rawUsableReason);
            if (!rawUsable)
            {
                Log("startup-probe | raw unusable before measurement | reason=" + rawUsableReason);
                return BuildUnreliableResult(output, null, null, "raw-unusable-before-measurement");
            }

            Log("startup-probe | raw usable -> protected startup + mandatory measurement start");

            ProbeCandidateRuntimeData rawRuntime = await RunSegmentedMeasurementAsync(
                rawCandidate,
                rawScore: null,
                leaderScore: null,
                timeBucketKey: timeBucket,
                cancellationToken: cancellationToken);

            if (rawRuntime == null)
                return BuildUnreliableResult(output, null, null, "raw-measurement-failed");

            rawRuntime.TimeBucketKey = timeBucket;
            rawRuntime.ReachedUsableState = true;
            rawRuntime.MeasurementStarted = true;
            rawRuntime.MeasurementCompleted = !rawRuntime.MeasurementStoppedEarly;
            rawRuntime.ReadinessKind = readiness.Kind;
            rawRuntime.ReadinessReason = readiness.Reason;

            ProbeScoreResult rawScore = ProbeScoring.ScoreCandidate(rawRuntime);
            LogLossRiskIfNeeded(rawCandidate, rawRuntime, rawScore);

            LearnedProbeStateProfile rawLearned = learningStore.GetProfile(rawCandidate.RouteKey, timeBucket);
            rawScore.StartupSelectionScore = ProbeScoring.ComputeStartupSelectionScore(rawScore, rawLearned);
            rawScore.IsShortlisted = true;

            output.Baseline = rawScore;
            output.AllResults.Add(rawScore);
            output.Shortlist.Add(rawScore);

            ProbeScoreResult leader = rawScore;
            Log("startup-probe | leader initialized -> " + FormatLeaderLabel(leader) +
                " | " + ProbeScoring.BuildScoreBreakdown(rawScore));

            foreach (ProbeCandidateDefinition candidate in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (candidate == null)
                    continue;

                if (candidate.Kind != ProbeCandidateKind.Speedify)
                    continue;

                Log("startup-probe | candidate start | " + candidate);

                DateTime activationRequestedUtc = DateTime.UtcNow;
                Log("startup-probe | warmup-milestone | " + candidate + " | activationRequestedMs=0");

                bool activated = await activateSpeedifyAsync(candidate.ConnectTarget);
                if (!activated)
                {
                    Log("startup-probe | discard | " + candidate + " | discardReason=activation failed");
                    continue;
                }

                await BeginTransitionGraceAsync("candidate-activate:" + candidate.RouteKey, cancellationToken);

                CandidateWarmupResult warmupResult = await WaitForCandidateUsableAfterActivationAsync(
                    candidate,
                    activationRequestedUtc,
                    cancellationToken);

                if (!warmupResult.IsUsable)
                {
                    Log("startup-probe | discard | " + candidate +
                        " | discardReason=unusable-before-measurement | reason=" + warmupResult.Reason);
                    continue;
                }

                Log("startup-probe | candidate usable -> measurement start | " + candidate +
                    " | warmupElapsedMs=" + warmupResult.WaitedMs.ToString("0"));

                ProbeReadinessResult candidateReadiness = new ProbeReadinessResult
                {
                    Kind = ProbeReadinessKind.TimeoutFallback,
                    Reason = "equal protected startup + equal measured window"
                };

                learningStore.UpdateFromReadinessSnapshot(
                    candidate.RouteKey,
                    timeBucket,
                    getShortWindowSnapshot(),
                    candidateReadiness.Kind);

                Log("startup-probe | candidate readiness | " + candidate +
                    " | " + candidateReadiness.Kind + " | " + candidateReadiness.Reason);

                ProbeCandidateRuntimeData runtime = await RunSegmentedMeasurementAsync(
                    candidate,
                    rawScore,
                    leader,
                    timeBucket,
                    cancellationToken);

                if (runtime == null)
                {
                    Log("startup-probe | discard | " + candidate + " | discardReason=measurement-capture-failed");
                    continue;
                }

                runtime.TimeBucketKey = timeBucket;
                runtime.ReadinessKind = candidateReadiness.Kind;
                runtime.ReadinessReason = candidateReadiness.Reason;
                runtime.StartupSpeedifyTrusted = warmupResult.StartupTrustEstablished;
                runtime.Notes = AppendNote(runtime.Notes, warmupResult.WarmupTag);
                runtime.Notes = AppendNote(
                    runtime.Notes,
                    warmupResult.StartupTrustEstablished
                        ? "startup-speedify-trusted"
                        : "startup-speedify-untrusted-fastping-only");

                ProbeScoreResult score = ProbeScoring.ScoreCandidate(runtime);
                LogLossRiskIfNeeded(candidate, runtime, score);

                LearnedProbeStateProfile candidateLearned = learningStore.GetProfile(candidate.RouteKey, timeBucket);
                score.StartupSelectionScore = ProbeScoring.ComputeStartupSelectionScore(score, candidateLearned);

                if (!runtime.MeasurementStarted)
                {
                    Log("startup-probe | compare | " + candidate +
                        " | unusable-before-measurement | discardReason=" + (runtime.DiscardReason ?? "unknown"));
                }
                else
                {
                    Log("startup-probe | compare | " + candidate +
                        " | composite=" + score.StartupSelectionScore.ToString("0.0") +
                        " | stability=" + score.OverallStabilityScore.ToString("0.0") +
                        " | warning=" + ProbeScoring.GetWarningLabel(score) +
                        " | " + ProbeScoring.BuildScoreBreakdown(score));
                }

                if (runtime.MeasurementStoppedEarly)
                {
                    score.IsDiscardedEarly = true;
                    output.AllResults.Add(score);

                    Log("startup-probe | discarded-early | " + candidate +
                        " | discardReason=" + (runtime.MeasurementStopReason ?? runtime.DiscardReason ?? "unknown") +
                        " | measured=" + (runtime.MeasurementStarted ? "yes" : "no") +
                        " | " + ProbeScoring.BuildShortText(score) +
                        " | overall=" + score.OverallStabilityScore.ToString("0.0") +
                        " | jitterScore=" + score.JitterStabilityScore.ToString("0.0") +
                        " | " + ProbeScoring.BuildScoreBreakdown(score) +
                        " | explain=" + ProbeScoring.BuildRouteExplanation(score));
                    continue;
                }

                ProbeDiscardDecision discardDecision = ProbeScoring.EvaluateDiscardDecision(score, rawScore, leader);

                bool minimumMet =
                    runtime.MeasurementStarted &&
                    !runtime.MeasurementStoppedEarly &&
                    runtime.MeasurementCompleted;

                bool discard = minimumMet && discardDecision.ShouldDiscard;

                if (discard)
                {
                    score.IsDiscardedEarly = true;
                    output.AllResults.Add(score);

                    Log("startup-probe | discarded-after-measurement | " + candidate +
                        " | discardReason=" + (discardDecision.ReasonCategory ?? "unknown") +
                        " | measured=" + (runtime.MeasurementStarted ? "yes" : "no") +
                        " | " + ProbeScoring.BuildShortText(score) +
                        " | overall=" + score.OverallStabilityScore.ToString("0.0") +
                        " | jitterScore=" + score.JitterStabilityScore.ToString("0.0") +
                        " | " + ProbeScoring.BuildScoreBreakdown(score) +
                        " | explain=" + ProbeScoring.BuildRouteExplanation(score));
                    continue;
                }

                output.AllResults.Add(score);
                score.IsShortlisted = true;
                output.Shortlist.Add(score);

                TrimShortlistByStartupScore(output.Shortlist, 3);

                bool candidateBeatsLeader = score.StartupSelectionScore < leader.StartupSelectionScore;
                if (candidateBeatsLeader)
                {
                    leader = score;
                    Log("startup-probe | leader promoted -> " + FormatLeaderLabel(leader) +
                        " | composite=" + leader.StartupSelectionScore.ToString("0.0"));
                }
                else
                {
                    Log("startup-probe | candidate lost | leader unchanged " + FormatLeaderLabel(leader));
                }

                Log("startup-probe | keep | " + candidate +
                    " | composite=" + score.StartupSelectionScore.ToString("0.0") +
                    " | " + ProbeScoring.BuildShortText(score) +
                    " | overall=" + score.OverallStabilityScore.ToString("0.0") +
                    " | jitterScore=" + score.JitterStabilityScore.ToString("0.0") +
                    " | " + ProbeScoring.BuildScoreBreakdown(score) +
                    " | explain=" + ProbeScoring.BuildRouteExplanation(score));
            }

            output.Shortlist = output.Shortlist
                .OrderBy(x => x.StartupSelectionScore)
                .ToList();

            ProbeScoring.AssignSmoothestBadge(output.Shortlist);

            output.Winner = leader ?? rawScore;

            ProbeScoreResult runnerUp = output.Shortlist
                .Where(x => x != null && !ReferenceEquals(x, output.Winner))
                .OrderBy(x => x.StartupSelectionScore)
                .FirstOrDefault();

            ProbeDecisionDiagnostics diagnostics = ProbeScoring.AnalyzeDecision(output.Winner, runnerUp);
            ProbeDecisionPolicyResult policy = ProbeScoring.ApplyDecisionPolicy(output.Winner, runnerUp, diagnostics);

            ProbeScoring.ApplyStabilityLayer(
                output.Winner,
                runnerUp,
                diagnostics,
                stabilityState,
                policy);

            string previousRouteKey = stabilityState?.LastWinnerRouteKey;
            double previousScore = stabilityState?.LastWinnerScore ?? -1;
            double currentScore = output.Winner?.StartupSelectionScore ?? -1;
            double improvement = (previousScore >= 0 && currentScore >= 0)
                ? previousScore - currentScore
                : 0.0;
            double confidence = output.Winner?.ConfidenceModifier ?? 0.0;
            bool holdRequested = policy.ShouldStickToPrevious;
            bool holdSucceeded = false;

            if (policy.ShouldStickToPrevious && !string.IsNullOrWhiteSpace(stabilityState?.LastWinnerRouteKey))
            {
                Logger.Log("decision-stability",
                    "hold | keeping=" + stabilityState.LastWinnerRouteKey +
                    " | new=" + output.Winner?.Candidate +
                    " | reason=" + (policy.PolicyReason ?? "stability-hold"));

                ProbeScoreResult heldWinner = output.Shortlist?
                    .FirstOrDefault(x => x?.Candidate?.RouteKey == stabilityState.LastWinnerRouteKey);

                if (heldWinner != null)
                {
                    output.Winner = heldWinner;
                    holdSucceeded = true;
                }
                else
                {
                    policy.HoldFallbackToCurrent = true;
                    policy.PolicyReason = AppendNote(policy.PolicyReason, "hold-requested-previous-missing");
                }
            }

            confidence = output.Winner?.ConfidenceModifier ?? confidence;

            string finalRouteKey = output.Winner?.Candidate?.RouteKey;
            bool hasPreviousRoute = !string.IsNullOrWhiteSpace(previousRouteKey);
            bool routeChanged = hasPreviousRoute &&
                !string.Equals(previousRouteKey, finalRouteKey, StringComparison.OrdinalIgnoreCase);

            string decisionType;
            if (policy.HoldFallbackToCurrent)
            {
                decisionType = "fallback-switch-previous-missing";
            }
            else if (holdRequested && holdSucceeded)
            {
                decisionType = !string.IsNullOrWhiteSpace(policy.DecisionType)
                    ? policy.DecisionType
                    : "stability-hold";
            }
            else if (hasPreviousRoute && !routeChanged)
            {
                decisionType = "no-switch-same-route";
            }
            else
            {
                decisionType = !string.IsNullOrWhiteSpace(policy.DecisionType)
                    ? policy.DecisionType
                    : (hasPreviousRoute ? "safe-switch" : "initial-selection");
            }

            policy.DecisionType = decisionType;

            string advisory = BuildAdvisoryFlags(policy);

            Logger.Log("decision",
                "policy | winner=" + (output.Winner?.Candidate?.RouteKey ?? "unknown") +
                " | previous=" + (previousRouteKey ?? "none") +
                " | improvement=" + improvement.ToString("0.0") +
                " | confidence=" + confidence.ToString("0.00") +
                " | type=" + decisionType +
                " | reason=" + (policy?.PolicyReason ?? "none") +
                " | advisory=" + advisory);

            bool finalRestore = await RestoreLeaderRouteAsync(output.Winner, cancellationToken);
            if (!finalRestore)
                return BuildUnreliableResult(output, output.Winner, rawScore, "winner-restore-failed");

            output.FinalRestoreSucceeded = true;

            if (output.Winner != null)
                output.Winner.IsWinner = true;

            runnerUp = output.Shortlist
                .Where(x => x != null && !ReferenceEquals(x, output.Winner))
                .OrderBy(x => x.StartupSelectionScore)
                .FirstOrDefault();

            LearnedProbeStateProfile winnerLearned = null;
            if (output.Winner?.Candidate != null)
            {
                winnerLearned = learningStore.GetProfile(
                    output.Winner.Candidate.RouteKey,
                    timeBucket);
            }

            output.WinnerConfidence = ProbeScoring.EvaluateWinnerConfidence(
                output.Winner,
                runnerUp,
                winnerLearned,
                x => x.StartupSelectionScore);

            if (output.Winner != null)
            {
                Log("decision | winner | " + output.Winner.Candidate +
                    " | score=" + output.Winner.OverallStabilityScore.ToString("0.0") +
                    " | confM=" + output.Winner.ConfidenceModifier.ToString("0.00") +
                    " | reason=best-score");
            }

            if (output.Winner != null && runnerUp != null)
            {
                double gap = Math.Abs(output.Winner.StartupSelectionScore - runnerUp.StartupSelectionScore);
                if (gap <= 5.0)
                {
                    Log("decision | close-call | " +
                        "winner=" + output.Winner.Candidate +
                        " | runnerUp=" + runnerUp.Candidate +
                        " | gap=" + gap.ToString("0.0"));
                }
            }

            diagnostics = ProbeScoring.AnalyzeDecision(output.Winner, runnerUp);
            if (output.Winner != null)
            {
                Logger.Log("decision-diagnostics",
                    "analyze | " + output.Winner.Candidate +
                    " | winner=" + output.Winner.Candidate +
                    " | flags=" + (diagnostics?.Summary ?? "clean") +
                    " | confM=" + output.Winner.ConfidenceModifier.ToString("0.00"));
            }

            if (output.Winner != null &&
                diagnostics != null &&
                !string.Equals(diagnostics.Summary, "clean", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Log("decision-alert",
                    "attention | " + output.Winner.Candidate +
                    " | winner=" + output.Winner.Candidate +
                    " | issues=" + diagnostics.Summary);
            }

            if (output.Winner != null &&
                diagnostics != null &&
                diagnostics.IsLowConfidence &&
                diagnostics.IsCloseCall)
            {
                Logger.Log("decision-critical",
                    "unstable-decision | " + output.Winner.Candidate +
                    " | winner=" + output.Winner.Candidate +
                    " | recommend=reprobe-or-delay-switch");
            }

            if (output.Winner != null)
            {
                string newKey = output.Winner.Candidate?.RouteKey;
                if (string.Equals(stabilityState.LastWinnerRouteKey, newKey, StringComparison.OrdinalIgnoreCase))
                {
                    stabilityState.ConsecutiveSameWinnerCount++;
                }
                else
                {
                    stabilityState.ConsecutiveSameWinnerCount = 1;
                    stabilityState.LastSwitchUtc = DateTime.UtcNow;
                }

                stabilityState.LastWinnerRouteKey = newKey;
                stabilityState.LastWinnerScore = output.Winner.StartupSelectionScore;
                stabilityState.LastConfidenceModifier = output.Winner.ConfidenceModifier;

                Logger.Log("decision-stability",
                    "state | last=" + (stabilityState.LastWinnerRouteKey ?? "none") +
                    " | score=" + stabilityState.LastWinnerScore.ToString("0.0") +
                    " | streak=" + stabilityState.ConsecutiveSameWinnerCount);
            }

            output.SummaryText = BuildSummary(output);
            if (policy != null && policy.IsStabilityHold && !policy.HoldFallbackToCurrent && holdSucceeded)
                output.SummaryText += " | stability hold";

            Log(output.SummaryText);

            return output;
        }

        private List<ProbeCandidateDefinition> ReorderRoutedCandidatesForStartup(
            List<ProbeCandidateDefinition> originalOrder,
            string timeBucket)
        {
            List<ProbeCandidateDefinition> safeOriginal = originalOrder ?? new List<ProbeCandidateDefinition>();

            Log("startup-probe | routed order | original=" +
                (safeOriginal.Count > 0
                    ? string.Join(" > ", safeOriginal.Select(x => x?.ToString() ?? "null"))
                    : "none"));

            List<RoutedCandidateOrderingEntry> entries = safeOriginal
                .Select((candidate, index) =>
                {
                    ServerProbeLearningStore.HistoricalRouteOrderingScore historical =
                        learningStore.EvaluateHistoricalOrderingScore(candidate?.RouteKey, timeBucket);

                    return new RoutedCandidateOrderingEntry
                    {
                        Candidate = candidate,
                        Historical = historical,
                        OriginalIndex = index,
                        FreshnessRank = historical?.FreshnessRank ?? 3
                    };
                })
                .ToList();

            bool allUntrusted = entries.Count > 0 && entries.All(x => x.Historical == null || !x.Historical.IsTrusted);
            if (allUntrusted)
            {
                Log("startup-probe | trusted learning unavailable | using score ordering with close-score tie-breakers only");
            }

            entries.Sort(CompareRoutedCandidateOrderingEntries);

            Log("startup-probe | routed order | reordered=" +
                (entries.Count > 0
                    ? string.Join(" > ", entries.Select(FormatRoutedOrderEntry))
                    : "none"));

            return entries.Select(x => x.Candidate).ToList();
        }

        private static int CompareRoutedCandidateOrderingEntries(
            RoutedCandidateOrderingEntry a,
            RoutedCandidateOrderingEntry b)
        {
            if (ReferenceEquals(a, b))
                return 0;
            if (a == null)
                return 1;
            if (b == null)
                return -1;

            double scoreA = a.Historical?.Score ?? double.MaxValue;
            double scoreB = b.Historical?.Score ?? double.MaxValue;
            double delta = scoreA - scoreB;

            if (Math.Abs(delta) > CloseScoreOrderingMargin)
                return scoreA.CompareTo(scoreB);

            bool trustedA = a.Historical != null && a.Historical.IsTrusted;
            bool trustedB = b.Historical != null && b.Historical.IsTrusted;
            if (trustedA != trustedB)
                return trustedA ? -1 : 1;

            if (a.FreshnessRank != b.FreshnessRank)
                return a.FreshnessRank.CompareTo(b.FreshnessRank);

            int biasA = GetBiasRank(a.Historical);
            int biasB = GetBiasRank(b.Historical);
            if (biasA != biasB)
                return biasA.CompareTo(biasB);

            return a.OriginalIndex.CompareTo(b.OriginalIndex);
        }

        private static int GetBiasRank(ServerProbeLearningStore.HistoricalRouteOrderingScore score)
        {
            if (score == null)
                return 1;

            if (score.HasGoodBias && !score.HasBadBias)
                return 0;

            if (!score.HasGoodBias && score.HasBadBias)
                return 2;

            return 1;
        }

        private static string FormatRoutedOrderEntry(RoutedCandidateOrderingEntry entry)
        {
            if (entry?.Candidate == null)
                return "unknown";

            string trusted = entry.Historical != null && entry.Historical.IsTrusted ? "trusted" : "untrusted";
            double score = entry.Historical?.Score ?? double.MaxValue;
            string freshness =
                entry.FreshnessRank == 0 ? "fresh" :
                entry.FreshnessRank == 1 ? "aging" :
                entry.FreshnessRank == 2 ? "stale" :
                "very-stale";

            string bias =
                entry.Historical == null ? "neutral" :
                entry.Historical.HasGoodBias && !entry.Historical.HasBadBias ? "good-bias" :
                !entry.Historical.HasGoodBias && entry.Historical.HasBadBias ? "bad-bias" :
                "mixed";

            return entry.Candidate +
                   "(score=" + score.ToString("0.0") +
                   ",trust=" + trusted +
                   ",fresh=" + freshness +
                   ",bias=" + bias + ")";
        }

        private static string AppendNote(string notes, string suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix))
                return notes;

            if (string.IsNullOrWhiteSpace(notes))
                return suffix;

            return notes + " | " + suffix;
        }

        private static string BuildAdvisoryFlags(ProbeDecisionPolicyResult policy)
        {
            if (policy == null)
                return "none";

            List<string> flags = new List<string>();
            if (policy.ShouldDelaySwitch)
                flags.Add("delay-switch");
            if (policy.ShouldReprobe)
                flags.Add("reprobe");

            return flags.Count == 0 ? "none" : string.Join(",", flags);
        }

        private async Task BeginTransitionGraceAsync(string reason, CancellationToken cancellationToken)
        {
            Log("startup-probe | transition grace | " + reason +
                " | " + timing.TransitionGrace.TotalMilliseconds.ToString("0") + "ms");

            await Task.Delay(timing.TransitionGrace, cancellationToken);
        }

        private bool IsCandidateUsableForMeasurement(
            ProbeCandidateDefinition candidate,
            out string reason)
        {
            reason = "usable";

            FastPingSignalAnalyzer.FastPingAggregateSnapshot window = getShortWindowSnapshot();
            FastPingState state = getFastPingState();

            if (state == null || window == null || !window.IsValid)
            {
                reason = "missing measurement telemetry";
                return false;
            }

            if (state.IsStale)
            {
                reason = "stale route state";
                return false;
            }

            if (!state.PrimaryFresh && !state.SecondaryFresh)
            {
                reason = "no fresh measurement telemetry";
                return false;
            }

            if (state.Confidence < 0.30)
            {
                reason = "confidence too low";
                return false;
            }

            bool churn =
                state.Confidence < 0.25 &&
                (window.StaleRate >= 0.35 || window.AvgLossRate >= 0.35);

            if (churn)
            {
                reason = "reconnect/disconnect churn";
                return false;
            }

            if (window.StaleRate >= 0.80)
            {
                reason = "catastrophic stale";
                return false;
            }

            if (window.AvgLossRate >= 0.80)
            {
                reason = "catastrophic loss";
                return false;
            }

            if (window.P95PingMs >= 2200.0 || window.AvgPingMs >= 1600.0)
            {
                reason = "catastrophic latency";
                return false;
            }

            return true;
        }

        private async Task<CandidateWarmupResult> WaitForCandidateUsableAfterActivationAsync(
            ProbeCandidateDefinition candidate,
            DateTime activationRequestedUtc,
            CancellationToken cancellationToken)
        {
            bool isSpeedifyCandidate =
                candidate != null &&
                candidate.Kind == ProbeCandidateKind.Speedify;

            if (!isSpeedifyCandidate)
            {
                bool immediateUsable = IsCandidateUsableForMeasurement(candidate, out string immediateReason);
                return new CandidateWarmupResult(immediateUsable, immediateReason, 0.0, false, "warmup-skipped");
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

            SpeedifyWarmupWindowBuffer buffer = new SpeedifyWarmupWindowBuffer(minimumFreshSamples, minimumSpreadMs);

            while (DateTime.UtcNow < fixedSettleEndUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                StartupDecisionSnapshot telemetry = getWarmupTelemetry != null
                    ? getWarmupTelemetry()
                    : null;

                bool usableNow = IsCandidateUsableForMeasurement(candidate, out string reasonNow);
                buffer.AddIfFresh(telemetry, usableNow, out _);

                if (DateTime.UtcNow >= nextWarmupLogUtc)
                {
                    double activationElapsedMs = Math.Max(0.0, (DateTime.UtcNow - activationRequestedUtc).TotalMilliseconds);
                    double remainingMs = Math.Max(0.0, (fixedSettleEndUtc - DateTime.UtcNow).TotalMilliseconds);

                    Log("startup-probe | candidate fixed-settle waiting | " + candidate +
                        " | reason=" + reasonNow +
                        " | remainingMs=" + remainingMs.ToString("0"));

                    Log("startup-probe | warmup-observe | " + candidate +
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

                await Task.Delay(timing.SpeedifyTelemetryCheckInterval, cancellationToken);
            }

            bool check1Done = false;
            bool check2Done = false;

            while (DateTime.UtcNow < gateForceUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                StartupDecisionSnapshot telemetry = getWarmupTelemetry != null
                    ? getWarmupTelemetry()
                    : null;

                bool usableNow = IsCandidateUsableForMeasurement(candidate, out string reasonNow);
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

                    Log("startup-probe | stable-start check | " + candidate +
                        " | checkpoint=4s" +
                        " | stable=" + (stableNow ? "yes" : "no") +
                        " | usable=" + (usableNow ? "yes" : "no") +
                        " | freshSamples=" + buffer.FreshSampleCount +
                        " | spreadMs=" + buffer.SampleDurationMs.ToString("0") +
                        " | reason=" + reasonNow);

                    if (stableNow)
                    {
                        string warmupTag = "fixed-settle-6s | stable-start-4s";
                        return new CandidateWarmupResult(
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

                    Log("startup-probe | stable-start check | " + candidate +
                        " | checkpoint=6s" +
                        " | stable=" + (stableNow ? "yes" : "no") +
                        " | usable=" + (usableNow ? "yes" : "no") +
                        " | freshSamples=" + buffer.FreshSampleCount +
                        " | spreadMs=" + buffer.SampleDurationMs.ToString("0") +
                        " | reason=" + reasonNow);

                    if (stableNow)
                    {
                        string warmupTag = "fixed-settle-6s | stable-start-6s";
                        return new CandidateWarmupResult(
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

                    Log("startup-probe | candidate stable-gate waiting | " + candidate +
                        " | reason=" + reasonNow +
                        " | remainingMs=" + remainingMs.ToString("0"));

                    Log("startup-probe | warmup-observe | " + candidate +
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

                await Task.Delay(timing.SpeedifyTelemetryCheckInterval, cancellationToken);
            }

            bool finalUsable = IsCandidateUsableForMeasurement(candidate, out string finalReason);
            bool startupTrustEstablished = buffer.MeetsMinimum && buffer.HasGoodTimeSpread && buffer.StableEnough;

            string finalTag = "fixed-settle-6s | stable-start-auto-8s";
            if (!buffer.MeetsMinimum)
                finalTag = AppendNote(finalTag, "speedify-low-confidence-insufficient-samples");
            if (!buffer.HasGoodTimeSpread)
                finalTag = AppendNote(finalTag, "speedify-low-confidence-poor-time-spread");
            if (!buffer.StableEnough)
                finalTag = AppendNote(finalTag, "speedify-unstable-window");
            if (buffer.AnyCongestion)
                finalTag = AppendNote(finalTag, "speedify-congestion-observed");

            if (!string.Equals(finalReason, "usable", StringComparison.OrdinalIgnoreCase) &&
                !SpeedifyWarmupSoftReasons.Contains(finalReason))
            {
                finalTag = AppendNote(
                    finalTag,
                    finalReason == "stale route state"
                        ? "stale-route-state-after-stable-gate"
                        : "warmup-hard-failure-" + finalReason.Replace(' ', '-'));
            }

            Log("startup-probe | stable-start check | " + candidate +
                " | checkpoint=8s-auto" +
                " | stable=" + (startupTrustEstablished ? "yes" : "no") +
                " | usable=" + (finalUsable ? "yes" : "no") +
                " | freshSamples=" + buffer.FreshSampleCount +
                " | spreadMs=" + buffer.SampleDurationMs.ToString("0") +
                " | reason=" + finalReason);

            return new CandidateWarmupResult(
                finalUsable,
                finalReason,
                Math.Max(0.0, (DateTime.UtcNow - warmupStartUtc).TotalMilliseconds),
                startupTrustEstablished,
                finalTag);
        }

        private static string FormatMetric(double value)
        {
            return value > 0 ? value.ToString("0.0") : "--";
        }

        private bool EvaluateStartupTrustGate(
            StartupDecisionSnapshot telemetry,
            out string reason)
        {
            reason = "missing measurement telemetry";

            if (telemetry == null ||
                !telemetry.HasFreshTelemetry ||
                !telemetry.TunnelConnected ||
                telemetry.ActiveConnectionCount <= 0)
            {
                return false;
            }

            double speedifyLatency = telemetry.AggregateLatencyMs > 0
                ? telemetry.AggregateLatencyMs
                : telemetry.ReportedLatencyMs;

            double speedifyJitter = telemetry.AggregateJitterMs;

            if (speedifyLatency <= 0)
            {
                reason = "latency unavailable";
                return false;
            }

            FastPingSignalAnalyzer.FastPingAggregateSnapshot window = getShortWindowSnapshot();
            if (window == null || !window.IsValid || window.AvgPingMs <= 0)
            {
                reason = "fastping not stable yet";
                return false;
            }

            bool latencyMatch =
                Math.Abs(speedifyLatency - window.AvgPingMs) <= Math.Max(8.0, window.AvgPingMs * 0.30);

            bool jitterMatch =
                speedifyJitter > 0 &&
                window.AvgJitterMs > 0 &&
                Math.Abs(speedifyJitter - window.AvgJitterMs) <= Math.Max(1.5, window.AvgJitterMs * 0.30);

            if (latencyMatch)
            {
                reason = "latency-match";
                return true;
            }

            if (jitterMatch)
            {
                reason = "jitter-match";
                return true;
            }

            reason = "large mismatch before trust gate";
            return false;
        }

        private static string BuildWarmupTimeoutTag(string reason)
        {
            if (string.Equals(reason, "stale route state", StringComparison.OrdinalIgnoreCase))
                return "stale-route-state-after-warmup-timeout";

            if (string.Equals(reason, "missing measurement telemetry", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "no fresh measurement telemetry", StringComparison.OrdinalIgnoreCase))
            {
                return "no-fresh-telemetry-after-warmup-timeout";
            }

            string safeReason = string.IsNullOrWhiteSpace(reason)
                ? "unknown"
                : reason.Replace(' ', '-');

            return "warmup-timeout-" + safeReason;
        }

        private static ProbeCandidateRuntimeData MergeMeasuredRuntime(
            ProbeCandidateRuntimeData first,
            ProbeCandidateRuntimeData second,
            TimeSpan firstDuration,
            TimeSpan secondDuration)
        {
            if (first == null)
                return second;

            if (second == null)
                return first;

            double w1 = Math.Max(0.0, firstDuration.TotalSeconds);
            double w2 = Math.Max(0.0, secondDuration.TotalSeconds);

            if (w1 <= 0.0 && w2 <= 0.0)
            {
                w1 = 1.0;
                w2 = 1.0;
            }

            return new ProbeCandidateRuntimeData
            {
                Candidate = second.Candidate ?? first.Candidate,
                TimeBucketKey = second.TimeBucketKey ?? first.TimeBucketKey,

                AvgPingMs = Weighted(first.AvgPingMs, second.AvgPingMs, w1, w2),
                MaxPingMs = MaxKnown(first.MaxPingMs, second.MaxPingMs),
                P95PingMs = Weighted(first.P95PingMs, second.P95PingMs, w1, w2),

                AvgJitterMs = Weighted(first.AvgJitterMs, second.AvgJitterMs, w1, w2),
                MinJitterMs = MinKnown(first.MinJitterMs, second.MinJitterMs),
                MaxJitterMs = MaxKnown(first.MaxJitterMs, second.MaxJitterMs),
                P95JitterMs = Weighted(first.P95JitterMs, second.P95JitterMs, w1, w2),

                AvgLossRate = Weighted(first.AvgLossRate, second.AvgLossRate, w1, w2),
                StartupAdjustedLossRate = Weighted(first.StartupAdjustedLossRate, second.StartupAdjustedLossRate, w1, w2),
                AvgBadQualityRate = Weighted(first.AvgBadQualityRate, second.AvgBadQualityRate, w1, w2),
                AvgConfidence = Weighted(first.AvgConfidence, second.AvgConfidence, w1, w2),
                SpikeDensityPerMinute = Weighted(first.SpikeDensityPerMinute, second.SpikeDensityPerMinute, w1, w2),
                StaleRate = Weighted(first.StaleRate, second.StaleRate, w1, w2),

                TunnelConnected = first.TunnelConnected || second.TunnelConnected,
                AggregateCongested = first.AggregateCongested || second.AggregateCongested,
                ActiveConnectionCount = Math.Max(first.ActiveConnectionCount, second.ActiveConnectionCount),
                BackupConnectionCount = Math.Max(first.BackupConnectionCount, second.BackupConnectionCount),
                SpeedifyAggregateLatencyMs = Weighted(first.SpeedifyAggregateLatencyMs, second.SpeedifyAggregateLatencyMs, w1, w2),
                SpeedifyAggregateJitterMs = Weighted(first.SpeedifyAggregateJitterMs, second.SpeedifyAggregateJitterMs, w1, w2),
                SpeedifyReportedLatencyMs = Weighted(first.SpeedifyReportedLatencyMs, second.SpeedifyReportedLatencyMs, w1, w2),
                SpeedifyFreshSampleCount = Math.Max(0, first.SpeedifyFreshSampleCount) + Math.Max(0, second.SpeedifyFreshSampleCount),
                SpeedifyExpectedSampleCount = Weighted(first.SpeedifyExpectedSampleCount, second.SpeedifyExpectedSampleCount, w1, w2),
                SpeedifyAggregateLatencyStdDevMs = Weighted(first.SpeedifyAggregateLatencyStdDevMs, second.SpeedifyAggregateLatencyStdDevMs, w1, w2),

                IsValid = first.IsValid || second.IsValid,
                Notes = AppendNote(first.Notes, second.Notes),

                ReachedUsableState = first.ReachedUsableState || second.ReachedUsableState,
                MeasurementStarted = first.MeasurementStarted || second.MeasurementStarted,
                MeasurementCompleted = first.MeasurementCompleted || second.MeasurementCompleted,
                MeasurementStoppedEarly = first.MeasurementStoppedEarly || second.MeasurementStoppedEarly,
                MeasurementStopReason = second.MeasurementStopReason ?? first.MeasurementStopReason,
                DiscardReason = second.DiscardReason ?? first.DiscardReason,

                StabilizationReached = first.StabilizationReached || second.StabilizationReached,
                StabilizationTimedOut = first.StabilizationTimedOut || second.StabilizationTimedOut,
                StabilizationReason = second.StabilizationReason ?? first.StabilizationReason,
                ReadinessKind = second.ReadinessKind != ProbeReadinessKind.NotReady ? second.ReadinessKind : first.ReadinessKind,
                ReadinessReason = second.ReadinessReason ?? first.ReadinessReason,
                StartupSpeedifyTrusted = first.StartupSpeedifyTrusted || second.StartupSpeedifyTrusted
            };
        }

        private static double Weighted(double a, double b, double wa, double wb)
        {
            bool aKnown = a >= 0;
            bool bKnown = b >= 0;

            if (!aKnown && !bKnown)
                return -1;
            if (!aKnown)
                return b;
            if (!bKnown)
                return a;

            return ((a * wa) + (b * wb)) / Math.Max(0.0001, wa + wb);
        }

        private static double MaxKnown(double a, double b)
        {
            if (a < 0 && b < 0) return -1;
            if (a < 0) return b;
            if (b < 0) return a;
            return Math.Max(a, b);
        }

        private static double MinKnown(double a, double b)
        {
            if (a < 0 && b < 0) return -1;
            if (a < 0) return b;
            if (b < 0) return a;
            return Math.Min(a, b);
        }

        private async Task<ProbeCandidateRuntimeData> RunSegmentedMeasurementAsync(
            ProbeCandidateDefinition candidate,
            ProbeScoreResult rawScore,
            ProbeScoreResult leaderScore,
            string timeBucketKey,
            CancellationToken cancellationToken)
        {
            bool isRawCandidate =
                candidate == null ||
                candidate.Kind == ProbeCandidateKind.Raw;

            if (isRawCandidate)
            {
                Log("startup-probe | raw measurement path | legacy-buffer-gate-measure");
                return await RunRawLegacyMeasurementAsync(candidate, timeBucketKey, cancellationToken);
            }

            Log("startup-probe | speedify measurement path | stabilization-then-post-stable");

            TimeSpan stabilizationObserveDuration = GetMaxStabilizationObserveDuration();
            TimeSpan postStableMeasuredDuration = GetPostStableMeasuredDuration();
            DateTime measurementStartUtc = DateTime.UtcNow;
            DateTime? stabilizationReachedUtc = null;
            DateTime? postStableMeasurementStartUtc = null;
            DateTime? postStableMeasurementEndUtc = null;
            List<StartupLossObservation> lossHistory = new List<StartupLossObservation>();

            Log("startup-probe | stabilization phase start | " + candidate +
                " | maxWaitMs=" + stabilizationObserveDuration.TotalMilliseconds.ToString("0"));

            List<TimeSpan> stabilizationSlices = BuildStabilizationObserveSlices(stabilizationObserveDuration);
            ProbeCandidateRuntimeData stabilizationRuntime = null;
            TimeSpan stabilizationObserved = TimeSpan.Zero;
            StartupLossStabilizationTracker stabilizationTracker = new StartupLossStabilizationTracker();
            bool stabilizationReached = false;
            bool stabilizationTimedOut = false;
            string stabilizationReason = string.Empty;
            const int minimumFreshSpeedifySnapshots = 4;

            SpeedifyWarmupWindowBuffer telemetryWindow = new SpeedifyWarmupWindowBuffer(
                minimumFreshSpeedifySnapshots,
                minimumSpreadMs: 0.0);
            bool isSpeedifyCandidate = candidate != null && candidate.Kind == ProbeCandidateKind.Speedify;
            double bestFastPingLossObservedRate = -1;

            for (int i = 0; i < stabilizationSlices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan slice = stabilizationSlices[i];

                ProbeCandidateRuntimeData sliceRuntime = await captureProbeAsync(
                    candidate,
                    TimeSpan.Zero,
                    slice);

                if (sliceRuntime == null)
                    return stabilizationRuntime;

                sliceRuntime.Candidate = sliceRuntime.Candidate ?? candidate;
                sliceRuntime.TimeBucketKey = timeBucketKey;
                sliceRuntime.ReachedUsableState = true;
                sliceRuntime.MeasurementStarted = true;
                sliceRuntime.MeasurementCompleted = false;
                sliceRuntime.MeasurementStoppedEarly = false;
                sliceRuntime.Notes = AppendNote(sliceRuntime.Notes, "stabilization-observe");
                RecordLossObservation(lossHistory, sliceRuntime.AvgLossRate, "stabilization");

                stabilizationRuntime = stabilizationRuntime == null
                    ? sliceRuntime
                    : MergeMeasuredRuntime(stabilizationRuntime, sliceRuntime, stabilizationObserved, slice);

                stabilizationObserved += slice;

                if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(stabilizationRuntime, out string stabilizationStopReason))
                {
                    stabilizationRuntime.MeasurementStoppedEarly = true;
                    stabilizationRuntime.MeasurementCompleted = false;
                    stabilizationRuntime.MeasurementStopReason = stabilizationStopReason;
                    stabilizationRuntime.DiscardReason = stabilizationStopReason;
                    stabilizationRuntime.Notes = AppendNote(stabilizationRuntime.Notes, stabilizationStopReason);

                    Log("startup-probe | candidate measured-early-stop | " + candidate +
                        " | reason=" + stabilizationStopReason);
                    LogCandidateLossCurveSummary(
                        candidate,
                        measurementStartUtc,
                        stabilizationReachedUtc,
                        stabilizationTimedOut,
                        stabilizationReason,
                        postStableMeasurementStartUtc,
                        postStableMeasurementEndUtc,
                        lossHistory,
                        postStableTelemetryExtensionHappened: false,
                        postStableFreshSnapshotCount: 0,
                        postStableTelemetryExtraWaitMs: 0.0,
                        postStableTelemetryTunnelActiveDuringWait: false);

                    return stabilizationRuntime;
                }

                if (isSpeedifyCandidate)
                {
                    StartupDecisionSnapshot telemetry = getWarmupTelemetry != null
                        ? getWarmupTelemetry()
                        : null;

                    bool usableNow = IsCandidateUsableForMeasurement(candidate, out _);
                    bool accepted = telemetryWindow.AddIfFresh(telemetry, usableNow, out string ignoreReason);
                    LogTelemetrySnapshotDecision(candidate, telemetry, accepted, ignoreReason);

                    Log("startup-probe | stabilization observe | " + candidate +
                        " | speedifyFreshSnapshots=" + telemetryWindow.FreshSampleCount +
                        " | minFresh=" + minimumFreshSpeedifySnapshots);
                }

                bool stableThisSlice = stabilizationTracker.Observe(
                    sliceRuntime.AvgLossRate,
                    out double deltaPercentagePoints);

                bool closeToFastPingStable =
                    TryIsSpeedifyLossCloseToFastPing(
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

                string deltaText = double.IsNaN(deltaPercentagePoints)
                    ? "--"
                    : deltaPercentagePoints.ToString("0.0");
                string lossText = sliceRuntime.AvgLossRate >= 0
                    ? (sliceRuntime.AvgLossRate * 100.0).ToString("0.0")
                    : "--";
                string fastPingLossText = currentFastPingLossRate >= 0
                    ? (currentFastPingLossRate * 100.0).ToString("0.0")
                    : "--";
                string bestFastPingLossText = bestFastPingLossRate >= 0
                    ? (bestFastPingLossRate * 100.0).ToString("0.0")
                    : "--";
                string fastPingGapText = !double.IsNaN(fastPingGapPercentagePoints)
                    ? fastPingGapPercentagePoints.ToString("0.0")
                    : "--";
                string fastPingConfidenceText = fastPingConfidence >= 0
                    ? fastPingConfidence.ToString("0.00")
                    : "--";
                string fastPingSpikeText = fastPingSpikeDensity >= 0
                    ? fastPingSpikeDensity.ToString("0.0")
                    : "--";
                string fastPingJitterText = fastPingAvgJitterMs >= 0
                    ? fastPingAvgJitterMs.ToString("0.0")
                    : "--";

                Log("startup-probe | stabilization observe | " + candidate +
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
                    stabilizationReason = closeToFastPingStable
                        ? "loss-close-to-fastping"
                        : "loss-flattened";

                    if (closeToFastPingStable)
                    {
                        Log("startup-probe | stabilization reached | " + candidate +
                            " | reason=loss-close-to-fastping" +
                            " | candidateLoss=" + (candidateLossRate * 100.0).ToString("0.0") + "%" +
                            " | fastPingLoss=" + (currentFastPingLossRate * 100.0).ToString("0.0") + "%" +
                            " | bestFastPingLoss=" + (bestFastPingLossRate * 100.0).ToString("0.0") + "%" +
                            " | gap=" + fastPingGapPercentagePoints.ToString("0.0") + "pp");
                    }
                    else
                    {
                        Log("startup-probe | stabilization reached | " + candidate +
                            " | reason=loss-flattened");
                    }

                    break;
                }
            }

            if (!stabilizationReached)
            {
                stabilizationTimedOut = true;
                stabilizationReason = "timeout-forced-stabilization-start";
                Log("startup-probe | stabilization timeout | " + candidate +
                    " | waitedMs=" + stabilizationObserved.TotalMilliseconds.ToString("0"));
            }

            Log("startup-probe | post-stable measurement start | " + candidate +
                " | durationMs=" + postStableMeasuredDuration.TotalMilliseconds.ToString("0"));
            postStableMeasurementStartUtc = DateTime.UtcNow;

            List<TimeSpan> measuredSlices = BuildMainWindowSlices(postStableMeasuredDuration);
            ProbeCandidateRuntimeData aggregateRuntime = null;
            TimeSpan accumulatedMeasured = TimeSpan.Zero;
            double adjustedLossWeightedSum = 0.0;
            double adjustedLossDurationSum = 0.0;
            StartupLossGateTracker startupLossGate = new StartupLossGateTracker(candidate, Log);
            SpeedifyWarmupWindowBuffer postStableTelemetryWindow = new SpeedifyWarmupWindowBuffer(
                minimumFreshSpeedifySnapshots,
                minimumSpreadMs: 0.0);
            bool postStableTelemetryExtensionHappened = false;
            bool postStableTelemetryTunnelActiveDuringWait = false;
            double postStableTelemetryExtraWaitMs = 0.0;

            for (int i = 0; i < measuredSlices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan slice = measuredSlices[i];
                TimeSpan measuredBeforeSlice = accumulatedMeasured;

                ProbeCandidateRuntimeData sliceRuntime = await captureProbeAsync(
                    candidate,
                    TimeSpan.Zero,
                    slice);

                if (sliceRuntime == null)
                    return aggregateRuntime;

                sliceRuntime.Candidate = sliceRuntime.Candidate ?? candidate;
                sliceRuntime.TimeBucketKey = timeBucketKey;
                sliceRuntime.ReachedUsableState = true;
                sliceRuntime.MeasurementStarted = true;
                sliceRuntime.MeasurementCompleted = false;
                sliceRuntime.MeasurementStoppedEarly = false;
                sliceRuntime.Notes = AppendNote(sliceRuntime.Notes, "measured-post-stable");
                RecordLossObservation(lossHistory, sliceRuntime.AvgLossRate, "post-stable");

                if (isSpeedifyCandidate)
                {
                    StartupDecisionSnapshot telemetry = getWarmupTelemetry != null
                        ? getWarmupTelemetry()
                        : null;
                    bool usableNow = IsCandidateUsableForMeasurement(candidate, out _);
                    bool accepted = postStableTelemetryWindow.AddIfFresh(telemetry, usableNow, out string ignoreReason);
                    LogTelemetrySnapshotDecision(candidate, telemetry, accepted, ignoreReason);
                    LogPostStableTelemetryProgress(
                        candidate,
                        postStableTelemetryWindow.FreshSampleCount,
                        minimumFreshSpeedifySnapshots,
                        sliceRuntime.TunnelConnected,
                        baseWindowEnded: false);
                }

                startupLossGate.Observe(sliceRuntime.AvgLossRate, measuredBeforeSlice, isExtensionSlice: false);
                AccumulateStartupAdjustedLoss(
                    sliceRuntime,
                    slice,
                    startupLossGate.IsValidated,
                    ref adjustedLossWeightedSum,
                    ref adjustedLossDurationSum);

                aggregateRuntime = aggregateRuntime == null
                    ? sliceRuntime
                    : MergeMeasuredRuntime(aggregateRuntime, sliceRuntime, accumulatedMeasured, slice);

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
                    aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, "stabilization-phase-excluded-from-score");
                    aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, stabilizationReason);

                    ApplyStartupAdjustedLossRate(
                        aggregateRuntime,
                        adjustedLossWeightedSum,
                        adjustedLossDurationSum);

                    if (!startupLossGate.IsValidated)
                    {
                        aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, "startup-loss-ignored");
                    }
                    else
                    {
                        aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, "startup-loss-validated");
                    }
                }

                if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(aggregateRuntime, out string mainStopReason))
                {
                    aggregateRuntime.MeasurementStoppedEarly = true;
                    aggregateRuntime.MeasurementCompleted = false;
                    aggregateRuntime.MeasurementStopReason = mainStopReason;
                    aggregateRuntime.DiscardReason = mainStopReason;
                    aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, mainStopReason);

                    Log("startup-probe | candidate measured-early-stop | " + candidate +
                        " | reason=" + mainStopReason);
                    postStableMeasurementEndUtc = DateTime.UtcNow;
                    LogCandidateLossCurveSummary(
                        candidate,
                        measurementStartUtc,
                        stabilizationReachedUtc,
                        stabilizationTimedOut,
                        stabilizationReason,
                        postStableMeasurementStartUtc,
                        postStableMeasurementEndUtc,
                        lossHistory,
                        postStableTelemetryExtensionHappened,
                        postStableTelemetryWindow.FreshSampleCount,
                        postStableTelemetryExtraWaitMs,
                        postStableTelemetryTunnelActiveDuringWait);

                    return aggregateRuntime;
                }
            }

            if (aggregateRuntime != null && isSpeedifyCandidate)
            {
                while (aggregateRuntime.TunnelConnected &&
                       postStableTelemetryWindow.FreshSampleCount < minimumFreshSpeedifySnapshots)
                {
                    postStableTelemetryExtensionHappened = true;
                    postStableTelemetryTunnelActiveDuringWait = true;

                    Log("startup-probe | post-stable measurement extend | " + candidate +
                        " | reason=waiting-for-speedify-telemetry | tunnelActive=yes");

                    TimeSpan waitSlice = StabilizationObserveSliceDuration;
                    DateTime waitSliceStartUtc = DateTime.UtcNow;

                    ProbeCandidateRuntimeData waitRuntime = await captureProbeAsync(
                        candidate,
                        TimeSpan.Zero,
                        waitSlice);

                    if (waitRuntime == null)
                        break;

                    waitRuntime.Candidate = waitRuntime.Candidate ?? candidate;
                    waitRuntime.TimeBucketKey = timeBucketKey;
                    waitRuntime.ReachedUsableState = true;
                    waitRuntime.MeasurementStarted = true;
                    waitRuntime.MeasurementCompleted = false;
                    waitRuntime.MeasurementStoppedEarly = false;
                    waitRuntime.Notes = AppendNote(waitRuntime.Notes, "measured-post-stable-telemetry-wait");
                    RecordLossObservation(lossHistory, waitRuntime.AvgLossRate, "post-stable-telemetry-wait");

                    startupLossGate.Observe(waitRuntime.AvgLossRate, accumulatedMeasured, isExtensionSlice: true);
                    AccumulateStartupAdjustedLoss(
                        waitRuntime,
                        waitSlice,
                        startupLossGate.IsValidated,
                        ref adjustedLossWeightedSum,
                        ref adjustedLossDurationSum);

                    aggregateRuntime = MergeMeasuredRuntime(aggregateRuntime, waitRuntime, accumulatedMeasured, waitSlice);
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
                        aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, "stabilization-phase-excluded-from-score");
                        aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, stabilizationReason);

                        ApplyStartupAdjustedLossRate(
                            aggregateRuntime,
                            adjustedLossWeightedSum,
                            adjustedLossDurationSum);
                    }

                    if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(aggregateRuntime, out string waitStopReason))
                    {
                        aggregateRuntime.MeasurementStoppedEarly = true;
                        aggregateRuntime.MeasurementCompleted = false;
                        aggregateRuntime.MeasurementStopReason = waitStopReason;
                        aggregateRuntime.DiscardReason = waitStopReason;
                        aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, waitStopReason);

                        Log("startup-probe | candidate measured-early-stop | " + candidate +
                            " | reason=" + waitStopReason);
                        postStableMeasurementEndUtc = DateTime.UtcNow;
                        postStableTelemetryExtraWaitMs += Math.Max(0.0, (DateTime.UtcNow - waitSliceStartUtc).TotalMilliseconds);
                        LogCandidateLossCurveSummary(
                            candidate,
                            measurementStartUtc,
                            stabilizationReachedUtc,
                            stabilizationTimedOut,
                            stabilizationReason,
                            postStableMeasurementStartUtc,
                            postStableMeasurementEndUtc,
                            lossHistory,
                            postStableTelemetryExtensionHappened,
                            postStableTelemetryWindow.FreshSampleCount,
                            postStableTelemetryExtraWaitMs,
                            postStableTelemetryTunnelActiveDuringWait);

                        return aggregateRuntime;
                    }

                    StartupDecisionSnapshot telemetry = getWarmupTelemetry != null
                        ? getWarmupTelemetry()
                        : null;
                    bool usableNow = IsCandidateUsableForMeasurement(candidate, out _);
                    bool accepted = postStableTelemetryWindow.AddIfFresh(telemetry, usableNow, out string ignoreReason);
                    LogTelemetrySnapshotDecision(candidate, telemetry, accepted, ignoreReason);
                    postStableTelemetryExtraWaitMs += Math.Max(0.0, (DateTime.UtcNow - waitSliceStartUtc).TotalMilliseconds);

                    LogPostStableTelemetryProgress(
                        candidate,
                        postStableTelemetryWindow.FreshSampleCount,
                        minimumFreshSpeedifySnapshots,
                        aggregateRuntime.TunnelConnected,
                        baseWindowEnded: true);
                }

                if (aggregateRuntime.TunnelConnected &&
                    postStableTelemetryWindow.FreshSampleCount >= minimumFreshSpeedifySnapshots)
                {
                    Log("startup-probe | post-stable telemetry sufficient | " + candidate +
                        " | freshSnapshots=" + postStableTelemetryWindow.FreshSampleCount);
                }
                else if (aggregateRuntime.TunnelConnected &&
                         postStableTelemetryWindow.FreshSampleCount < minimumFreshSpeedifySnapshots)
                {
                    LogPostStableTelemetryProgress(
                        candidate,
                        postStableTelemetryWindow.FreshSampleCount,
                        minimumFreshSpeedifySnapshots,
                        tunnelActive: true,
                        baseWindowEnded: true);
                }
            }

            bool promising = IsPromisingForExtension(aggregateRuntime, rawScore, leaderScore);
            if (promising && timing.PromisingCandidateExtensionDuration > TimeSpan.Zero)
            {
                TimeSpan extension = timing.PromisingCandidateExtensionDuration;

                Log("startup-probe | measured-evaluation extend | " + candidate +
                    " | extensionMs=" + extension.TotalMilliseconds.ToString("0") +
                    " | reason=promising-candidate");

                ProbeCandidateRuntimeData extensionRuntime = await captureProbeAsync(
                    candidate,
                    TimeSpan.Zero,
                    extension);

                if (extensionRuntime != null)
                {
                    extensionRuntime.Candidate = extensionRuntime.Candidate ?? candidate;
                    extensionRuntime.TimeBucketKey = timeBucketKey;
                    extensionRuntime.ReachedUsableState = true;
                    extensionRuntime.MeasurementStarted = true;
                    extensionRuntime.Notes = AppendNote(extensionRuntime.Notes, "measured-extension");

                    if (extensionRuntime.AvgLossRate >= 0)
                    {
                        startupLossGate.Observe(extensionRuntime.AvgLossRate, postStableMeasuredDuration, isExtensionSlice: true);
                        double extensionSeconds = Math.Max(0.0, extension.TotalSeconds);
                        if (extensionSeconds > 0.0 && startupLossGate.IsValidated)
                        {
                            adjustedLossWeightedSum += extensionRuntime.AvgLossRate * extensionSeconds;
                            adjustedLossDurationSum += extensionSeconds;
                        }
                    }

                    ApplyStartupAdjustedLossRate(
                        extensionRuntime,
                        adjustedLossWeightedSum,
                        adjustedLossDurationSum);

                    if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(extensionRuntime, out string extensionStopReason))
                    {
                        extensionRuntime.MeasurementStoppedEarly = true;
                        extensionRuntime.MeasurementCompleted = false;
                        extensionRuntime.MeasurementStopReason = extensionStopReason;
                        extensionRuntime.DiscardReason = extensionStopReason;
                        extensionRuntime.Notes = AppendNote(extensionRuntime.Notes, extensionStopReason);

                        Log("startup-probe | candidate measured-early-stop | " + candidate +
                            " | reason=" + extensionStopReason);

                        ProbeCandidateRuntimeData catastrophicMerged =
                            MergeMeasuredRuntime(aggregateRuntime, extensionRuntime, postStableMeasuredDuration, extension);

                        ApplyStartupAdjustedLossRate(
                            catastrophicMerged,
                            adjustedLossWeightedSum,
                            adjustedLossDurationSum);

                        return catastrophicMerged;
                    }

                    aggregateRuntime = MergeMeasuredRuntime(aggregateRuntime, extensionRuntime, postStableMeasuredDuration, extension);
                    aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, "startup-confidence-extension");
                    aggregateRuntime.Notes = AppendNote(
                        aggregateRuntime.Notes,
                        startupLossGate.IsValidated ? "startup-loss-validated" : "startup-loss-ignored");

                    ApplyStartupAdjustedLossRate(
                        aggregateRuntime,
                        adjustedLossWeightedSum,
                        adjustedLossDurationSum);
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
            aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, "stabilization-phase-excluded-from-score");
            aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, stabilizationReason);

            ApplyStartupAdjustedLossRate(
                aggregateRuntime,
                adjustedLossWeightedSum,
                adjustedLossDurationSum);

            Log("startup-probe | candidate measured-full | " + candidate);
            LogCandidateLossCurveSummary(
                candidate,
                measurementStartUtc,
                stabilizationReachedUtc,
                stabilizationTimedOut,
                stabilizationReason,
                postStableMeasurementStartUtc,
                postStableMeasurementEndUtc,
                lossHistory,
                postStableTelemetryExtensionHappened,
                postStableTelemetryWindow.FreshSampleCount,
                postStableTelemetryExtraWaitMs,
                postStableTelemetryTunnelActiveDuringWait);

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

                long sliceTicks = remainingSlices <= 1
                    ? remainingTicks
                    : totalTicks / MainWindowSliceCount;

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
            TimeSpan sliceDuration =
                StabilizationObserveSliceDuration > TimeSpan.Zero
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
            if (sliceRuntime == null)
                return;

            if (sliceRuntime.AvgLossRate < 0)
                return;

            double sliceSeconds = Math.Max(0.0, sliceDuration.TotalSeconds);
            if (sliceSeconds <= 0.0)
                return;

            if (!lossValidated)
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

        private async Task<ProbeCandidateRuntimeData> RunRawLegacyMeasurementAsync(
            ProbeCandidateDefinition candidate,
            string timeBucketKey,
            CancellationToken cancellationToken)
        {
            TimeSpan fixedSettle = timing.RoutedFixedSettleDuration > TimeSpan.Zero
                ? timing.RoutedFixedSettleDuration
                : TimeSpan.FromSeconds(6.0);
            TimeSpan check1 = timing.RoutedStableGateCheck1 > TimeSpan.Zero
                ? timing.RoutedStableGateCheck1
                : TimeSpan.FromSeconds(4.0);
            TimeSpan check2 = timing.RoutedStableGateCheck2 > check1
                ? timing.RoutedStableGateCheck2
                : TimeSpan.FromSeconds(6.0);
            TimeSpan forceStart = timing.RoutedStableGateForceStart > check2
                ? timing.RoutedStableGateForceStart
                : TimeSpan.FromSeconds(8.0);

            TimeSpan rawDuration = timing.RawMeasuredDuration > TimeSpan.Zero
                ? timing.RawMeasuredDuration
                : TimeSpan.FromSeconds(18.0);

            DateTime waitStartUtc = DateTime.UtcNow;
            DateTime fixedSettleEndUtc = waitStartUtc + fixedSettle;
            DateTime check1Utc = fixedSettleEndUtc + check1;
            DateTime check2Utc = fixedSettleEndUtc + check2;
            DateTime forceUtc = fixedSettleEndUtc + forceStart;
            DateTime nextWaitLogUtc = waitStartUtc;
            TimeSpan rawPollInterval = timing.SpeedifyTelemetryCheckInterval > TimeSpan.Zero
                ? timing.SpeedifyTelemetryCheckInterval
                : TimeSpan.FromMilliseconds(250);

            RawPreMeasureSignal preMeasureSignal = new RawPreMeasureSignal();

            while (DateTime.UtcNow < fixedSettleEndUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FastPingSignalAnalyzer.FastPingAggregateSnapshot window = getShortWindowSnapshot();
                preMeasureSignal.Observe(window);

                bool usableNow = IsCandidateUsableForMeasurement(candidate, out string reasonNow);
                if (DateTime.UtcNow >= nextWaitLogUtc)
                {
                    Log("startup-probe | raw fixed-settle waiting | reason=" + reasonNow +
                        " | usable=" + (usableNow ? "yes" : "no") +
                        " | remainingMs=" + Math.Max(0.0, (fixedSettleEndUtc - DateTime.UtcNow).TotalMilliseconds).ToString("0"));
                    nextWaitLogUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }

                await Task.Delay(rawPollInterval, cancellationToken);
            }

            bool check1Done = false;
            bool check2Done = false;

            while (DateTime.UtcNow < forceUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FastPingSignalAnalyzer.FastPingAggregateSnapshot window = getShortWindowSnapshot();
                preMeasureSignal.Observe(window);

                bool usableNow = IsCandidateUsableForMeasurement(candidate, out string reasonNow);
                DateTime nowUtc = DateTime.UtcNow;

                if (!check1Done && nowUtc >= check1Utc)
                {
                    check1Done = true;
                    Log("startup-probe | raw stable-start check | checkpoint=4s | usable=" + (usableNow ? "yes" : "no") +
                        " | reason=" + reasonNow);
                    if (usableNow)
                        break;
                }

                if (!check2Done && nowUtc >= check2Utc)
                {
                    check2Done = true;
                    Log("startup-probe | raw stable-start check | checkpoint=6s | usable=" + (usableNow ? "yes" : "no") +
                        " | reason=" + reasonNow);
                    if (usableNow)
                        break;
                }

                await Task.Delay(rawPollInterval, cancellationToken);
            }

            bool forceUsable = IsCandidateUsableForMeasurement(candidate, out string forceReason);
            Log("startup-probe | raw stable-start check | checkpoint=8s | usable=" + (forceUsable ? "yes" : "no") +
                " | reason=" + forceReason);
            Log("startup-probe | raw measurement start | durationMs=" + rawDuration.TotalMilliseconds.ToString("0"));

            List<TimeSpan> measuredSlices = BuildMainWindowSlices(rawDuration);
            ProbeCandidateRuntimeData aggregateRuntime = null;
            TimeSpan accumulatedMeasured = TimeSpan.Zero;

            for (int i = 0; i < measuredSlices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan slice = measuredSlices[i];
                ProbeCandidateRuntimeData sliceRuntime = await captureProbeAsync(
                    candidate,
                    TimeSpan.Zero,
                    slice);

                if (sliceRuntime == null)
                    return aggregateRuntime;

                sliceRuntime.Candidate = sliceRuntime.Candidate ?? candidate;
                sliceRuntime.TimeBucketKey = timeBucketKey;
                sliceRuntime.ReachedUsableState = true;
                sliceRuntime.MeasurementStarted = true;
                sliceRuntime.MeasurementCompleted = false;
                sliceRuntime.MeasurementStoppedEarly = false;
                sliceRuntime.Notes = AppendNote(sliceRuntime.Notes, "measured-raw-direct");

                aggregateRuntime = aggregateRuntime == null
                    ? sliceRuntime
                    : MergeMeasuredRuntime(aggregateRuntime, sliceRuntime, accumulatedMeasured, slice);

                accumulatedMeasured += slice;

                if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(aggregateRuntime, out string stopReason))
                {
                    aggregateRuntime.MeasurementStoppedEarly = true;
                    aggregateRuntime.MeasurementCompleted = false;
                    aggregateRuntime.MeasurementStopReason = stopReason;
                    aggregateRuntime.DiscardReason = stopReason;
                    aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, stopReason);
                    return aggregateRuntime;
                }
            }

            if (aggregateRuntime == null)
                return null;

            aggregateRuntime.ReachedUsableState = true;
            aggregateRuntime.MeasurementStarted = true;
            aggregateRuntime.MeasurementCompleted = true;
            aggregateRuntime.MeasurementStoppedEarly = false;
            aggregateRuntime.StartupAdjustedLossRate = -1;
            aggregateRuntime.StabilizationReached = false;
            aggregateRuntime.StabilizationTimedOut = false;
            aggregateRuntime.StabilizationReason = "raw-legacy-gated-measure";

            ApplyRawPreMeasureInfluence(aggregateRuntime, preMeasureSignal);

            return aggregateRuntime;
        }

        private static void RecordLossObservation(
            List<StartupLossObservation> history,
            double lossRate,
            string phase)
        {
            if (history == null || lossRate < 0)
                return;

            history.Add(new StartupLossObservation(DateTime.UtcNow, lossRate, phase));
        }

        private static void ApplyRawPreMeasureInfluence(
            ProbeCandidateRuntimeData runtime,
            RawPreMeasureSignal preMeasureSignal)
        {
            if (runtime == null || preMeasureSignal.Count <= 0)
                return;

            double preLoss = preMeasureSignal.LossSum / preMeasureSignal.Count;
            double preJitter = preMeasureSignal.JitterSum / preMeasureSignal.Count;
            double prePing = preMeasureSignal.PingSum / preMeasureSignal.Count;

            if (runtime.AvgPingMs >= 0 && prePing > 0)
                runtime.AvgPingMs = (runtime.AvgPingMs * 0.95) + (prePing * 0.05);

            if (runtime.AvgJitterMs >= 0 && preJitter > 0)
                runtime.AvgJitterMs = (runtime.AvgJitterMs * 0.95) + (preJitter * 0.05);

            if (runtime.AvgLossRate >= 0 && preLoss >= 0)
            {
                double lossAdjustment = 0.0;
                bool earlyUgly =
                    preLoss > runtime.AvgLossRate + 0.08 ||
                    (runtime.AvgJitterMs > 0 && preJitter > runtime.AvgJitterMs * 1.6);
                bool earlyExcellent =
                    runtime.AvgLossRate > preLoss + 0.08 &&
                    (runtime.AvgJitterMs > 0 && preJitter < runtime.AvgJitterMs * 0.75);

                if (earlyUgly)
                    lossAdjustment = 0.006;
                else if (earlyExcellent)
                    lossAdjustment = -0.003;

                runtime.AvgLossRate = Math.Max(0.0, runtime.AvgLossRate + lossAdjustment);
                runtime.StartupAdjustedLossRate = runtime.AvgLossRate;

                if (earlyUgly)
                    runtime.Notes = AppendNote(runtime.Notes, "raw-premeasure-small-penalty");
                else if (earlyExcellent)
                    runtime.Notes = AppendNote(runtime.Notes, "raw-premeasure-tiny-boost");
            }
        }

        private void LogTelemetrySnapshotDecision(
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
                Log("startup-probe | telemetry snapshot accepted | candidate=" + candidate +
                    " | snapshotUtc=" + snapshotUtcText);
                return;
            }

            Log("startup-probe | telemetry snapshot ignored | reason=" + (ignoreReason ?? "unknown") +
                " | candidate=" + candidate +
                " | snapshotUtc=" + snapshotUtcText);
        }

        private void LogPostStableTelemetryProgress(
            ProbeCandidateDefinition candidate,
            int observedSnapshotCount,
            int requiredSnapshotCount,
            bool tunnelActive,
            bool baseWindowEnded)
        {
            Log("startup-probe | post-stable telemetry progress | candidate=" + candidate +
                " | newSnapshots=" + observedSnapshotCount + "/" + requiredSnapshotCount +
                " | tunnelActive=" + (tunnelActive ? "yes" : "no") +
                (baseWindowEnded ? " | baseWindowEnded=yes | continueWait=indefinite" : string.Empty));
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

        private void LogCandidateLossCurveSummary(
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

            Log("startup-probe | loss-curve summary | " + candidate +
                " | points=[" + string.Join(" ; ", entries) + "]" +
                " | " + timingSummary +
                " | " + timeoutSummary +
                " | " + postStableSummary +
                " | " + telemetryWaitSummary +
                (stabilizationTimedOut ? " | timeout-forced-post-stable=yes" : " | timeout-forced-post-stable=no"));
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

        private async Task<(bool IsCatastrophic, string Reason)> WaitWithCatastrophicChecksAsync(
            ProbeCandidateDefinition candidate,
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            DateTime endUtc = DateTime.UtcNow + duration;

            TimeSpan pollInterval =
                timing.SpeedifyTelemetryCheckInterval > TimeSpan.Zero
                    ? timing.SpeedifyTelemetryCheckInterval
                    : TimeSpan.FromMilliseconds(250);

            while (DateTime.UtcNow < endUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TryGetCatastrophicReasonFromCurrentWindow(out string reason))
                {
                    Log("startup-probe | catastrophic detected during protected phase | " + candidate);
                    return (true, reason);
                }

                await Task.Delay(pollInterval, cancellationToken);
            }

            return (false, null);
        }

        private async Task<ProbeCandidateRuntimeData> BuildEarlyStopRuntimeAsync(
            ProbeCandidateDefinition candidate,
            string reason)
        {
            ProbeCandidateRuntimeData runtime = await captureProbeAsync(candidate, TimeSpan.Zero, TimeSpan.Zero);
            if (runtime == null)
                return null;

            runtime.ReachedUsableState = true;
            runtime.MeasurementStarted = true;
            runtime.MeasurementStoppedEarly = true;
            runtime.MeasurementCompleted = false;
            runtime.MeasurementStopReason = reason;
            runtime.DiscardReason = reason;
            runtime.Notes = AppendNote(runtime.Notes, reason);

            Log("startup-probe | candidate measured-early-stop | " + candidate + " | reason=" + reason);
            return runtime;
        }

        private bool TryGetCatastrophicReasonFromCurrentWindow(out string reason)
        {
            reason = null;

            FastPingSignalAnalyzer.FastPingAggregateSnapshot window = getShortWindowSnapshot();
            FastPingState state = getFastPingState();

            if (window == null || !window.IsValid)
                return false;

            if (window.P95PingMs >= 2500.0 || window.AvgPingMs >= 1800.0)
            {
                reason = "early-stop catastrophic latency";
                return true;
            }

            if (window.StaleRate >= 0.80 && window.AvgLossRate >= 0.65)
            {
                reason = "early-stop catastrophic stale/timeout";
                return true;
            }

            if (window.AvgLossRate >= 0.70 && window.StaleRate >= 0.45)
            {
                reason = "early-stop catastrophic loss";
                return true;
            }

            if (state != null &&
                state.Confidence >= 0 &&
                state.Confidence < 0.08 &&
                window.StaleRate >= 0.80)
            {
                reason = "early-stop tunnel collapse";
                return true;
            }

            return false;
        }

        private bool IsPromisingForExtension(
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

        private StartupProbeResult BuildUnreliableResult(
            StartupProbeResult output,
            ProbeScoreResult leader,
            ProbeScoreResult rawScore,
            string reason)
        {
            output.FinalRestoreSucceeded = false;
            output.Winner = leader ?? rawScore;

            if (output.Winner != null)
                output.Winner.IsWinner = true;

            output.SummaryText =
                "startup-probe | unreliable | reason=" + (reason ?? "leader-restore-failed") +
                " | heldLeader=" + FormatLeaderLabel(leader);

            output.WinnerConfidence = new ProbeWinnerConfidenceResult
            {
                Kind = ProbeWinnerConfidenceKind.NoClearWinner,
                ConfidenceScore = 0.0,
                Reason = "startup probe marked unreliable"
            };

            Log(output.SummaryText);
            return output;
        }

        private async Task<bool> RestoreLeaderRouteAsync(
            ProbeScoreResult leader,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (leader?.Candidate == null || leader.Candidate.Kind == ProbeCandidateKind.Raw)
            {
                bool disabled = await disableSpeedifyAsync();
                if (disabled)
                    await BeginTransitionGraceAsync("restore-raw-leader", cancellationToken);

                if (disabled)
                    Log("startup-probe | leader restored -> " + FormatLeaderLabel(leader));
                else
                    Log("startup-probe | leader restore failed -> " + FormatLeaderLabel(leader));

                return disabled;
            }

            bool activated = await activateSpeedifyAsync(leader.Candidate.ConnectTarget);
            if (activated)
                await BeginTransitionGraceAsync("restore-speedify-leader:" + leader.Candidate.RouteKey, cancellationToken);

            if (activated)
                Log("startup-probe | leader restored -> " + FormatLeaderLabel(leader));
            else
                Log("startup-probe | leader restore failed -> " + FormatLeaderLabel(leader));

            return activated;
        }

        private static string FormatLeaderLabel(ProbeScoreResult leader)
        {
            if (leader?.Candidate == null)
                return "unknown";

            if (leader.Candidate.Kind == ProbeCandidateKind.Raw)
                return "raw";

            return string.IsNullOrWhiteSpace(leader.Candidate.DisplayName)
                ? leader.Candidate.RouteKey ?? "speedify"
                : leader.Candidate.DisplayName;
        }

        private static void TrimShortlistByStartupScore(List<ProbeScoreResult> shortlist, int maxKeep)
        {
            if (shortlist == null)
                return;

            shortlist.Sort((a, b) => a.StartupSelectionScore.CompareTo(b.StartupSelectionScore));

            while (shortlist.Count > maxKeep)
                shortlist.RemoveAt(shortlist.Count - 1);
        }

        private string BuildSummary(StartupProbeResult result)
        {
            if (result == null)
                return "startup-probe | no result";

            string winner = result.Winner != null
                ? result.Winner.Candidate +
                  " | composite=" + result.Winner.StartupSelectionScore.ToString("0.0") +
                  " | overall=" + result.Winner.OverallStabilityScore.ToString("0.0") +
                  " | badge=" + ProbeScoring.GetBadgeLabel(result.Winner)
                : "none";

            ProbeScoreResult runnerUp = result.Shortlist != null
                ? result.Shortlist
                    .Where(x => x != null && !ReferenceEquals(x, result.Winner))
                    .OrderBy(x => x.StartupSelectionScore)
                    .FirstOrDefault()
                : null;

            string winnerConfidence = result.WinnerConfidence != null
                ? result.WinnerConfidence.Kind +
                  " (" + result.WinnerConfidence.ConfidenceScore.ToString("0.0") + ")" +
                  " | " + result.WinnerConfidence.Reason
                : "not-evaluated";

            string winnerReason = ProbeScoring.BuildWinnerReason(
                result.Winner,
                runnerUp,
                result.WinnerConfidence,
                x => x.StartupSelectionScore);

            string shortlist = result.Shortlist != null && result.Shortlist.Count > 0
                ? string.Join(" | ",
                    result.Shortlist
                        .OrderBy(x => x.StartupSelectionScore)
                        .Select(x =>
                            x.Candidate + " => " +
                            "composite=" + x.StartupSelectionScore.ToString("0.0") +
                            " | overall=" + x.OverallStabilityScore.ToString("0.0") +
                            " | jitterScore=" + x.JitterStabilityScore.ToString("0.0") +
                            " | warning=" + ProbeScoring.GetWarningLabel(x) +
                            " | badge=" + ProbeScoring.GetBadgeLabel(x) +
                            " | explain=" + ProbeScoring.BuildRouteExplanation(x)))
                : "none";

            return "startup-probe | winner=" + winner +
                   " | winnerConfidence=" + winnerConfidence +
                   " | winnerReason=" + winnerReason +
                   " | shortlist=" + shortlist;
        }

        private void Log(string message)
        {
            log?.Invoke(message);
        }

        private void LogLossRiskIfNeeded(
            ProbeCandidateDefinition candidate,
            ProbeCandidateRuntimeData runtime,
            ProbeScoreResult score)
        {
            if (runtime == null)
                return;

            double effectiveLoss = runtime.StartupAdjustedLossRate >= 0
                ? runtime.StartupAdjustedLossRate
                : runtime.AvgLossRate;

            if (effectiveLoss < 0.05)
                return;

            Log("startup-probe | risky loss trigger | " + (candidate?.ToString() ?? "unknown") +
                " | lossPct=" + (effectiveLoss * 100.0).ToString("0.0") +
                " | warning=" + ProbeScoring.GetWarningLabel(score));
        }
    }

    public sealed class StartupProbeResult
    {
        public ProbeReadinessResult RawReadiness { get; set; }
        public ProbeScoreResult Baseline { get; set; }
        public ProbeScoreResult Winner { get; set; }
        public bool FinalRestoreSucceeded { get; set; }
        public List<ProbeScoreResult> AllResults { get; set; } = new List<ProbeScoreResult>();
        public List<ProbeScoreResult> Shortlist { get; set; } = new List<ProbeScoreResult>();
        public ProbeWinnerConfidenceResult WinnerConfidence { get; set; }
        public string SummaryText { get; set; }
    }

    public sealed class StartupProbeTimingConfig
    {
        public TimeSpan TransitionGrace { get; set; }
        public TimeSpan RoutedFixedSettleDuration { get; set; }
        public TimeSpan RoutedStableGateCheck1 { get; set; }
        public TimeSpan RoutedStableGateCheck2 { get; set; }
        public TimeSpan RoutedStableGateForceStart { get; set; }
        public TimeSpan BaseMeasuredDuration { get; set; }
        public TimeSpan RawMeasuredDuration { get; set; }
        public TimeSpan MaxStabilizationObserveDuration { get; set; }
        public TimeSpan PostStableMeasuredDuration { get; set; }
        public TimeSpan PromisingCandidateExtensionDuration { get; set; }
        public TimeSpan SpeedifyTelemetryCheckInterval { get; set; }
    }
}
