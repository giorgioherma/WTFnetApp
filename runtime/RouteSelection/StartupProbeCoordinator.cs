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

            public void AddIfFresh(StartupDecisionSnapshot snapshot, bool isUsable)
            {
                if (snapshot == null || !snapshot.HasFreshTelemetry)
                    return;

                if (snapshot.TimeUtc == DateTime.MinValue)
                    return;

                if (lastAcceptedUtc != DateTime.MinValue &&
                    snapshot.TimeUtc <= lastAcceptedUtc)
                {
                    return;
                }

                if (firstAcceptedUtc == DateTime.MinValue)
                    firstAcceptedUtc = snapshot.TimeUtc;

                lastAcceptedUtc = snapshot.TimeUtc;
                samples.Add(snapshot);

                if (isUsable)
                    usableSampleCount++;
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
        private const int ProtectedFullResultCountBeforeContentionDrop = 3;
        private const int ContenderBoundarySize = 4;
        private const double EarlyDropStartProgress = 0.50;
        private const double EarlyDropEndProgress = 0.80;
        private const double EarlyDropSafetyMargin = 5.0;
        private const int MainWindowSliceCount = 10;

        private static readonly HashSet<string> SpeedifyWarmupSoftReasons =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "stale route state",
                "missing probe telemetry",
                "no fresh probes",
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
                priorMeasuredResults: output.AllResults,
                timeBucketKey: timeBucket,
                cancellationToken: cancellationToken);

            if (rawRuntime == null)
                return BuildUnreliableResult(output, null, null, "raw-measurement-failed");

            rawRuntime.TimeBucketKey = timeBucket;
            rawRuntime.StabilizationReached = true;
            rawRuntime.StabilizationTimedOut = false;
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
                    output.AllResults,
                    timeBucket,
                    cancellationToken);

                if (runtime == null)
                {
                    Log("startup-probe | discard | " + candidate + " | discardReason=measurement-capture-failed");
                    continue;
                }

                runtime.TimeBucketKey = timeBucket;
                runtime.StabilizationReached = true;
                runtime.StabilizationTimedOut = false;
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
                reason = "missing probe telemetry";
                return false;
            }

            if (state.IsStale)
            {
                reason = "stale route state";
                return false;
            }

            if (!state.PrimaryFresh && !state.SecondaryFresh)
            {
                reason = "no fresh probes";
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

            if (!isSpeedifyCandidate || timing.SpeedifyWarmupTolerance <= TimeSpan.Zero)
            {
                bool immediateUsable = IsCandidateUsableForMeasurement(candidate, out string immediateReason);
                return new CandidateWarmupResult(immediateUsable, immediateReason, 0.0, false, "warmup-skipped");
            }

            TimeSpan fixedWindow = timing.SpeedifyWarmupTolerance;
            if (fixedWindow < TimeSpan.FromSeconds(2))
                fixedWindow = TimeSpan.FromSeconds(2);
            if (fixedWindow > TimeSpan.FromSeconds(4))
                fixedWindow = TimeSpan.FromSeconds(4);

            const int minimumFreshSamples = 4;
            const double minimumSpreadMs = 1500.0;
            DateTime warmupStartUtc = DateTime.UtcNow;
            DateTime warmupDeadlineUtc = warmupStartUtc + fixedWindow;
            DateTime nextWarmupLogUtc = warmupStartUtc;

            SpeedifyWarmupWindowBuffer buffer = new SpeedifyWarmupWindowBuffer(minimumFreshSamples, minimumSpreadMs);

            while (DateTime.UtcNow < warmupDeadlineUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                StartupDecisionSnapshot telemetry = getWarmupTelemetry != null
                    ? getWarmupTelemetry()
                    : null;

                bool usableNow = IsCandidateUsableForMeasurement(candidate, out string reasonNow);
                buffer.AddIfFresh(telemetry, usableNow);

                if (DateTime.UtcNow >= nextWarmupLogUtc)
                {
                    double activationElapsedMs = Math.Max(0.0, (DateTime.UtcNow - activationRequestedUtc).TotalMilliseconds);
                    double remainingMs = Math.Max(0.0, (warmupDeadlineUtc - DateTime.UtcNow).TotalMilliseconds);

                    Log("startup-probe | candidate warm-up waiting | " + candidate +
                        " | reason=" + reasonNow +
                        " | remainingMs=" + remainingMs.ToString("0"));

                    Log("startup-probe | warmup-observe | " + candidate +
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

                await Task.Delay(timing.SpeedifyWarmupPollInterval, cancellationToken);
            }

            bool finalUsable = IsCandidateUsableForMeasurement(candidate, out string finalReason);
            bool startupTrustEstablished = buffer.MeetsMinimum && buffer.HasGoodTimeSpread;
            finalUsable = finalUsable && buffer.StableEnough;
            string warmupTag = "window-sampled";

            if (!buffer.MeetsMinimum)
                warmupTag = AppendNote(warmupTag, "speedify-low-confidence-insufficient-samples");
            if (!buffer.HasGoodTimeSpread)
                warmupTag = AppendNote(warmupTag, "speedify-low-confidence-poor-time-spread");
            if (!buffer.StableEnough)
                warmupTag = AppendNote(warmupTag, "speedify-unstable-window");

            if (buffer.AnyCongestion)
                warmupTag = AppendNote(warmupTag, "speedify-congestion-observed");

            if (!string.Equals(finalReason, "usable", StringComparison.OrdinalIgnoreCase) &&
                !SpeedifyWarmupSoftReasons.Contains(finalReason))
            {
                warmupTag = AppendNote(
                    warmupTag,
                    finalReason == "stale route state"
                        ? "stale-route-state-after-warmup-window"
                        : "warmup-hard-failure-" + finalReason.Replace(' ', '-'));
            }

            return new CandidateWarmupResult(
                finalUsable,
                finalReason,
                Math.Max(0.0, (DateTime.UtcNow - warmupStartUtc).TotalMilliseconds),
                startupTrustEstablished,
                warmupTag);
        }

        private static string FormatMetric(double value)
        {
            return value > 0 ? value.ToString("0.0") : "--";
        }

        private bool EvaluateStartupTrustGate(
            StartupDecisionSnapshot telemetry,
            out string reason)
        {
            reason = "missing speedify telemetry";

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

            if (string.Equals(reason, "missing probe telemetry", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(reason, "no fresh probes", StringComparison.OrdinalIgnoreCase))
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
            IList<ProbeScoreResult> priorMeasuredResults,
            string timeBucketKey,
            CancellationToken cancellationToken)
        {
            TimeSpan protectedStartupBuffer = timing.CommonProtectedStartupBufferDuration;
            TimeSpan ignoreNegativeWindow = timing.CommonIgnoreNegativeEventsDuration;
            TimeSpan main = GetEffectiveMeasuredDuration(
                candidate,
                protectedStartupBuffer,
                ignoreNegativeWindow);

            if (protectedStartupBuffer > TimeSpan.Zero)
            {
                Log("startup-probe | protected-startup-buffer start | " + candidate +
                    " | durationMs=" + protectedStartupBuffer.TotalMilliseconds.ToString("0"));

                (bool catastrophic, string stopReason) =
                    await WaitWithCatastrophicChecksAsync(candidate, protectedStartupBuffer, cancellationToken);

                if (catastrophic)
                {
                    return await BuildEarlyStopRuntimeAsync(
                        candidate,
                        stopReason ?? "early-stop catastrophic during protected-startup-buffer");
                }

                Log("startup-probe | protected-startup-buffer end | " + candidate);
            }

            TimeSpan remainingIgnore = ignoreNegativeWindow;

            if (remainingIgnore > TimeSpan.Zero)
            {
                Log("startup-probe | startup-noise-ignore active | " + candidate +
                    " | durationMs=" + remainingIgnore.TotalMilliseconds.ToString("0") +
                    " | ignoring spikes/ordinary negatives");

                (bool catastrophic, string stopReason) =
                    await WaitWithCatastrophicChecksAsync(candidate, remainingIgnore, cancellationToken);

                if (catastrophic)
                {
                    return await BuildEarlyStopRuntimeAsync(
                        candidate,
                        stopReason ?? "early-stop catastrophic during startup-noise-ignore");
                }
            }

            Log("startup-probe | measured-evaluation start | " + candidate +
                " | durationMs=" + main.TotalMilliseconds.ToString("0"));

            List<TimeSpan> slices = BuildMainWindowSlices(main);
            ProbeCandidateRuntimeData aggregateRuntime = null;
            TimeSpan accumulatedMeasured = TimeSpan.Zero;
            LearnedProbeStateProfile learned = learningStore.GetProfile(candidate?.RouteKey, timeBucketKey);

            for (int i = 0; i < slices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan slice = slices[i];
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
                sliceRuntime.Notes = AppendNote(sliceRuntime.Notes, "measured-main");

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

                    return aggregateRuntime;
                }

                double progress = main.TotalMilliseconds > 0
                    ? accumulatedMeasured.TotalMilliseconds / main.TotalMilliseconds
                    : 1.0;

                if (CanEvaluateContenderDrop(candidate, progress, priorMeasuredResults) &&
                    TryGetContenderBoundaryScore(priorMeasuredResults, out double boundaryScore))
                {
                    ProbeScoreResult partialScore = ProbeScoring.ScoreCandidate(aggregateRuntime);
                    partialScore.StartupSelectionScore = ProbeScoring.ComputeStartupSelectionScore(partialScore, learned);

                    double optimisticBestFinal = EstimateOptimisticBestFinalScore(
                        partialScore.StartupSelectionScore,
                        progress);

                    bool outsideContention = optimisticBestFinal > boundaryScore + EarlyDropSafetyMargin;

                    Log("startup-probe | contender-check | " + candidate +
                        " | progress=" + (progress * 100.0).ToString("0") + "%" +
                        " | partial=" + partialScore.StartupSelectionScore.ToString("0.0") +
                        " | optimisticBest=" + optimisticBestFinal.ToString("0.0") +
                        " | boundary4=" + boundaryScore.ToString("0.0") +
                        " | keepMargin=" + EarlyDropSafetyMargin.ToString("0.0") +
                        " | decision=" + (outsideContention ? "drop" : "keep"));

                    if (outsideContention)
                    {
                        const string stopReason = "early-drop outside top4 contention";

                        aggregateRuntime.MeasurementStoppedEarly = true;
                        aggregateRuntime.MeasurementCompleted = false;
                        aggregateRuntime.MeasurementStopReason = stopReason;
                        aggregateRuntime.DiscardReason = stopReason;
                        aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, "top4-out-of-contention");
                        aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, stopReason);

                        Log("startup-probe | candidate measured-early-stop | " + candidate +
                            " | reason=" + stopReason +
                            " | progress=" + (progress * 100.0).ToString("0") + "%" +
                            " | optimisticBest=" + optimisticBestFinal.ToString("0.0") +
                            " | boundary4=" + boundaryScore.ToString("0.0"));

                        return aggregateRuntime;
                    }
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

                    if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(extensionRuntime, out string extensionStopReason))
                    {
                        extensionRuntime.MeasurementStoppedEarly = true;
                        extensionRuntime.MeasurementCompleted = false;
                        extensionRuntime.MeasurementStopReason = extensionStopReason;
                        extensionRuntime.DiscardReason = extensionStopReason;
                        extensionRuntime.Notes = AppendNote(extensionRuntime.Notes, extensionStopReason);

                        Log("startup-probe | candidate measured-early-stop | " + candidate +
                            " | reason=" + extensionStopReason);

                        return MergeMeasuredRuntime(aggregateRuntime, extensionRuntime, main, extension);
                    }

                    aggregateRuntime = MergeMeasuredRuntime(aggregateRuntime, extensionRuntime, main, extension);
                    aggregateRuntime.Notes = AppendNote(aggregateRuntime.Notes, "startup-confidence-extension");
                }
            }

            aggregateRuntime.ReachedUsableState = true;
            aggregateRuntime.MeasurementStarted = true;
            aggregateRuntime.MeasurementCompleted = true;
            aggregateRuntime.MeasurementStoppedEarly = false;

            Log("startup-probe | candidate measured-full | " + candidate);

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

        private static bool CanEvaluateContenderDrop(
            ProbeCandidateDefinition candidate,
            double progress,
            IList<ProbeScoreResult> priorMeasuredResults)
        {
            if (candidate == null || candidate.Kind == ProbeCandidateKind.Raw)
                return false;

            if (progress < EarlyDropStartProgress)
                return false;

            if (progress >= EarlyDropEndProgress)
                return false;

            int fullyMeasuredCount = CountFullyMeasuredResults(priorMeasuredResults);
            if (fullyMeasuredCount < ProtectedFullResultCountBeforeContentionDrop)
                return false;

            if (fullyMeasuredCount < ContenderBoundarySize)
                return false;

            return true;
        }

        private static int CountFullyMeasuredResults(IList<ProbeScoreResult> scores)
        {
            if (scores == null)
                return 0;

            int count = 0;
            for (int i = 0; i < scores.Count; i++)
            {
                if (IsFullyMeasuredResult(scores[i]))
                    count++;
            }

            return count;
        }

        private static bool IsFullyMeasuredResult(ProbeScoreResult score)
        {
            return score != null &&
                   score.Runtime != null &&
                   score.Runtime.MeasurementStarted &&
                   score.Runtime.MeasurementCompleted &&
                   !score.Runtime.MeasurementStoppedEarly;
        }

        private static bool TryGetContenderBoundaryScore(
            IList<ProbeScoreResult> priorMeasuredResults,
            out double boundaryScore)
        {
            boundaryScore = double.MaxValue;

            if (priorMeasuredResults == null)
                return false;

            List<ProbeScoreResult> fullyMeasured = priorMeasuredResults
                .Where(IsFullyMeasuredResult)
                .OrderBy(x => x.StartupSelectionScore)
                .ToList();

            if (fullyMeasured.Count < ContenderBoundarySize)
                return false;

            boundaryScore = fullyMeasured
                .Take(ContenderBoundarySize)
                .Last()
                .StartupSelectionScore;

            return true;
        }

        private static double EstimateOptimisticBestFinalScore(double partialScore, double progress)
        {
            double safeProgress = Math.Max(EarlyDropStartProgress, Math.Min(EarlyDropEndProgress, progress));
            double remainingFraction = Math.Max(0.0, 1.0 - safeProgress);

            double improvementAllowance = 24.0 * remainingFraction;
            return partialScore - improvementAllowance;
        }

        private TimeSpan GetEffectiveMeasuredDuration(
            ProbeCandidateDefinition candidate,
            TimeSpan protectedStartupDuration,
            TimeSpan ignoreNegativeDuration)
        {
            bool isRawCandidate =
                candidate == null ||
                candidate.Kind == ProbeCandidateKind.Raw;

            if (isRawCandidate)
                return timing.RawMeasuredDuration;

            TimeSpan shieldDuration = protectedStartupDuration + ignoreNegativeDuration;
            TimeSpan minimumRequired = shieldDuration + timing.MinimumPostBufferMeasuredDuration;

            return timing.BaseMeasuredDuration >= minimumRequired
                ? timing.BaseMeasuredDuration
                : minimumRequired;
        }

        private async Task<(bool IsCatastrophic, string Reason)> WaitWithCatastrophicChecksAsync(
            ProbeCandidateDefinition candidate,
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            DateTime endUtc = DateTime.UtcNow + duration;

            TimeSpan pollInterval =
                timing.SpeedifyWarmupPollInterval > TimeSpan.Zero
                    ? timing.SpeedifyWarmupPollInterval
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

            if (runtime.AvgLossRate < 0.05)
                return;

            Log("startup-probe | risky loss trigger | " + (candidate?.ToString() ?? "unknown") +
                " | lossPct=" + (runtime.AvgLossRate * 100.0).ToString("0.0") +
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

        public TimeSpan CommonProtectedStartupBufferDuration { get; set; }
        public TimeSpan CommonIgnoreNegativeEventsDuration { get; set; }

        public TimeSpan BaseMeasuredDuration { get; set; }
        public TimeSpan RawMeasuredDuration { get; set; }
        public TimeSpan MinimumPostBufferMeasuredDuration { get; set; }

        public TimeSpan PromisingCandidateExtensionDuration { get; set; }

        public TimeSpan SpeedifyWarmupTolerance { get; set; }
        public TimeSpan SpeedifyWarmupPollInterval { get; set; }

        public TimeSpan ObserveDuration { get; set; }
    }
}