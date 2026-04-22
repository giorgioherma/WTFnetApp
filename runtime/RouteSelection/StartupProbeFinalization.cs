using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WTFnetApp
{
    internal sealed class StartupProbeFinalization
    {
        private readonly Func<Task<bool>> disableSpeedifyAsync;
        private readonly Func<string, Task<bool>> activateSpeedifyAsync;
        private readonly ServerProbeLearningStore learningStore;
        private readonly StartupProbeTimingConfig timing;
        private readonly StartupProbeResultBuilder resultBuilder = new StartupProbeResultBuilder();
        private readonly StartupProbeComparisonEngine comparisonEngine = new StartupProbeComparisonEngine();
        private readonly ProbeDecisionStabilityState stabilityState;
        private readonly StartupLogger startupLogger;
        private readonly Func<string, string, string> appendNote;
        private readonly Action<string> log;

        public StartupProbeFinalization(
            Func<Task<bool>> disableSpeedifyAsync,
            Func<string, Task<bool>> activateSpeedifyAsync,
            ServerProbeLearningStore learningStore,
            StartupProbeTimingConfig timing,
            ProbeDecisionStabilityState stabilityState,
            StartupLogger startupLogger,
            Func<string, string, string> appendNote,
            Action<string> log)
        {
            this.disableSpeedifyAsync = disableSpeedifyAsync;
            this.activateSpeedifyAsync = activateSpeedifyAsync;
            this.learningStore = learningStore;
            this.timing = timing;
            this.stabilityState = stabilityState;
            this.startupLogger = startupLogger;
            this.appendNote = appendNote;
            this.log = log;
        }

        public ProbeScoreResult HandleScoredCandidate(
            ProbeCandidateDefinition candidate,
            ProbeCandidateRuntimeData runtime,
            ProbeScoreResult score,
            ProbeScoreResult rawScore,
            ProbeScoreResult leader,
            StartupProbeResult output)
        {
            return comparisonEngine.HandleScoredCandidate(
                candidate,
                runtime,
                score,
                rawScore,
                leader,
                output,
                log);
        }

        public async Task<StartupProbeResult> FinalizeAsync(
            StartupProbeResult output,
            ProbeScoreResult rawScore,
            ProbeScoreResult leader,
            string timeBucket,
            CancellationToken cancellationToken)
        {
            output.Shortlist = output.Shortlist
                .OrderBy(x => x.StartupSelectionScore)
                .ToList();

            ProbeScoring.AssignSmoothestBadge(output.Shortlist);

            output.Winner = leader
                ?? output.Winner
                ?? output.Shortlist?
                    .Where(x => x != null && !x.IsDiscardedEarly)
                    .OrderBy(x => x.StartupSelectionScore)
                    .FirstOrDefault()
                ?? rawScore;

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
                    policy.PolicyReason = appendNote(policy.PolicyReason, "hold-requested-previous-missing");
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
                return resultBuilder.BuildUnreliableResult(output, output.Winner, rawScore, "winner-restore-failed", log);

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

            List<ProbeScoreResult> rankedShortlist = output.Shortlist != null
                ? output.Shortlist
                    .Where(x => x != null)
                    .OrderBy(x => x.StartupSelectionScore)
                    .ToList()
                : new List<ProbeScoreResult>();

            for (int i = 0; i < rankedShortlist.Count; i++)
            {
                ProbeScoreResult candidateScore = rankedShortlist[i];
                bool isWinner = ReferenceEquals(candidateScore, output.Winner);
                startupLogger?.LogEvent(candidateScore?.Candidate, isWinner ? "WINNER_SELECTED" : "KEPT_NOT_WINNER");
                startupLogger?.LogStartupProbeSummary(
                    candidateScore?.Candidate,
                    -1,
                    candidateScore?.Runtime?.StabilizationReached == true && candidateScore?.Runtime?.StabilizationTimedOut != true ? "yes" : "no",
                    candidateScore?.Runtime?.StabilizationReason,
                    -1,
                    candidateScore?.Runtime?.Notes != null &&
                        candidateScore.Runtime.Notes.IndexOf("startup-confidence-extension", StringComparison.OrdinalIgnoreCase) >= 0,
                    candidateScore?.Runtime,
                    candidateScore,
                    isWinner ? "winner" : "kept-not-winner");
            }

            ProbeScoreResult third = rankedShortlist.Count > 2 ? rankedShortlist[2] : null;
            string winnerReason = ProbeScoring.BuildWinnerReason(
                output.Winner,
                runnerUp,
                output.WinnerConfidence,
                x => x.StartupSelectionScore);
            startupLogger?.LogFinalDecisionBlock(
                output.Winner,
                runnerUp,
                third,
                output.WinnerConfidence,
                winnerReason,
                policy?.DecisionType,
                output.FinalRestoreSucceeded,
                rankedShortlist);

            if (output.Winner != null)
            {
                log?.Invoke("decision | winner | " + output.Winner.Candidate +
                    " | score=" + output.Winner.OverallStabilityScore.ToString("0.0") +
                    " | confM=" + output.Winner.ConfidenceModifier.ToString("0.00") +
                    " | reason=best-score");
            }

            if (output.Winner != null && runnerUp != null)
            {
                double gap = Math.Abs(output.Winner.StartupSelectionScore - runnerUp.StartupSelectionScore);
                if (gap <= 5.0)
                {
                    log?.Invoke("decision | close-call | " +
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

            output.SummaryText = resultBuilder.BuildSummary(output);
            if (policy != null && policy.IsStabilityHold && !policy.HoldFallbackToCurrent && holdSucceeded)
                output.SummaryText += " | stability hold";

            log?.Invoke(output.SummaryText);

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
                    log?.Invoke("startup-probe | leader restored -> " + StartupProbeResultBuilder.FormatLeaderLabel(leader));
                else
                    log?.Invoke("startup-probe | leader restore failed -> " + StartupProbeResultBuilder.FormatLeaderLabel(leader));

                return disabled;
            }

            bool activated = await activateSpeedifyAsync(leader.Candidate.ConnectTarget);
            if (activated)
                await BeginTransitionGraceAsync("restore-speedify-leader:" + leader.Candidate.RouteKey, cancellationToken);

            if (activated)
                log?.Invoke("startup-probe | leader restored -> " + StartupProbeResultBuilder.FormatLeaderLabel(leader));
            else
                log?.Invoke("startup-probe | leader restore failed -> " + StartupProbeResultBuilder.FormatLeaderLabel(leader));

            return activated;
        }

        private async Task BeginTransitionGraceAsync(string reason, CancellationToken cancellationToken)
        {
            log?.Invoke("startup-probe | transition grace | " + reason +
                " | " + timing.TransitionGrace.TotalMilliseconds.ToString("0") + "ms");

            await Task.Delay(timing.TransitionGrace, cancellationToken);
        }

        private static string BuildAdvisoryFlags(ProbeDecisionPolicyResult policy)
        {
            if (policy == null)
                return "none";

            System.Collections.Generic.List<string> flags = new System.Collections.Generic.List<string>();
            if (policy.ShouldDelaySwitch)
                flags.Add("delay-switch");
            if (policy.ShouldReprobe)
                flags.Add("reprobe");

            return flags.Count == 0 ? "none" : string.Join(",", flags);
        }
    }
}



namespace WTFnetApp
{
    internal sealed class StartupProbeComparisonEngine
    {
        public ProbeScoreResult HandleScoredCandidate(
            ProbeCandidateDefinition candidate,
            ProbeCandidateRuntimeData runtime,
            ProbeScoreResult score,
            ProbeScoreResult rawScore,
            ProbeScoreResult leader,
            StartupProbeResult output,
            Action<string> log)
        {
            if (!runtime.MeasurementStarted)
            {
                log?.Invoke("startup-probe | compare | " + candidate +
                    " | unusable-before-measurement | discardReason=" + (runtime.DiscardReason ?? "unknown"));
            }
            else
            {
                log?.Invoke("startup-probe | compare | " + candidate +
                    " | composite=" + score.StartupSelectionScore.ToString("0.0") +
                    " | stability=" + score.OverallStabilityScore.ToString("0.0") +
                    " | warning=" + ProbeScoring.GetWarningLabel(score) +
                    " | " + ProbeScoring.BuildScoreBreakdown(score));
            }

            if (runtime.MeasurementStoppedEarly)
            {
                score.IsDiscardedEarly = true;
                output.AllResults.Add(score);

                log?.Invoke("startup-probe | discarded-early | " + candidate +
                    " | discardReason=" + (runtime.MeasurementStopReason ?? runtime.DiscardReason ?? "unknown") +
                    " | measured=" + (runtime.MeasurementStarted ? "yes" : "no") +
                    " | " + ProbeScoring.BuildShortText(score) +
                    " | overall=" + score.OverallStabilityScore.ToString("0.0") +
                    " | jitterScore=" + score.JitterStabilityScore.ToString("0.0") +
                    " | " + ProbeScoring.BuildScoreBreakdown(score) +
                    " | explain=" + ProbeScoring.BuildRouteExplanation(score));
                return leader;
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

                log?.Invoke("startup-probe | discarded-after-measurement | " + candidate +
                    " | discardReason=" + (discardDecision.ReasonCategory ?? "unknown") +
                    " | measured=" + (runtime.MeasurementStarted ? "yes" : "no") +
                    " | " + ProbeScoring.BuildShortText(score) +
                    " | overall=" + score.OverallStabilityScore.ToString("0.0") +
                    " | jitterScore=" + score.JitterStabilityScore.ToString("0.0") +
                    " | " + ProbeScoring.BuildScoreBreakdown(score) +
                    " | explain=" + ProbeScoring.BuildRouteExplanation(score));
                return leader;
            }

            output.AllResults.Add(score);
            score.IsShortlisted = true;
            output.Shortlist.Add(score);

            TrimShortlistByStartupScore(output.Shortlist, 3);

            bool candidateBeatsLeader = score.StartupSelectionScore < leader.StartupSelectionScore;
            if (candidateBeatsLeader)
            {
                leader = score;
                log?.Invoke("startup-probe | leader promoted -> " + StartupProbeResultBuilder.FormatLeaderLabel(leader) +
                    " | composite=" + leader.StartupSelectionScore.ToString("0.0"));
            }
            else
            {
                log?.Invoke("startup-probe | candidate lost | leader unchanged " + StartupProbeResultBuilder.FormatLeaderLabel(leader));
            }

            log?.Invoke("startup-probe | keep | " + candidate +
                " | composite=" + score.StartupSelectionScore.ToString("0.0") +
                " | " + ProbeScoring.BuildShortText(score) +
                " | overall=" + score.OverallStabilityScore.ToString("0.0") +
                " | jitterScore=" + score.JitterStabilityScore.ToString("0.0") +
                " | " + ProbeScoring.BuildScoreBreakdown(score) +
                " | explain=" + ProbeScoring.BuildRouteExplanation(score));

            return leader;
        }

        private static void TrimShortlistByStartupScore(List<ProbeScoreResult> shortlist, int maxKeep)
        {
            if (shortlist == null)
                return;

            shortlist.Sort((a, b) => a.StartupSelectionScore.CompareTo(b.StartupSelectionScore));

            while (shortlist.Count > maxKeep)
                shortlist.RemoveAt(shortlist.Count - 1);
        }
    }
}



namespace WTFnetApp
{
    internal sealed class StartupProbeResultBuilder
    {
        public StartupProbeResult BuildUnreliableResult(
            StartupProbeResult output,
            ProbeScoreResult leader,
            ProbeScoreResult rawScore,
            string reason,
            Action<string> log)
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

            log?.Invoke(output.SummaryText);
            return output;
        }

        public string BuildSummary(StartupProbeResult result)
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

        public static string FormatLeaderLabel(ProbeScoreResult leader)
        {
            if (leader?.Candidate == null)
                return "unknown";

            if (leader.Candidate.Kind == ProbeCandidateKind.Raw)
                return "raw";

            return string.IsNullOrWhiteSpace(leader.Candidate.DisplayName)
                ? leader.Candidate.RouteKey ?? "speedify"
                : leader.Candidate.DisplayName;
        }
    }
}



namespace WTFnetApp
{
    internal static class StartupProbeRuntimeMerge
    {
        public static ProbeCandidateRuntimeData MergeMeasuredRuntime(
            ProbeCandidateRuntimeData first,
            ProbeCandidateRuntimeData second,
            TimeSpan firstDuration,
            TimeSpan secondDuration,
            Func<string, string, string> appendNote)
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

                AvgPingMs = WeightedAverageKnown(first.AvgPingMs, second.AvgPingMs, w1, w2),
                MaxPingMs = MaxKnown(first.MaxPingMs, second.MaxPingMs),
                P95PingMs = WeightedAverageKnown(first.P95PingMs, second.P95PingMs, w1, w2),

                AvgJitterMs = WeightedAverageKnown(first.AvgJitterMs, second.AvgJitterMs, w1, w2),
                MinJitterMs = MinKnown(first.MinJitterMs, second.MinJitterMs),
                MaxJitterMs = MaxKnown(first.MaxJitterMs, second.MaxJitterMs),
                P95JitterMs = WeightedAverageKnown(first.P95JitterMs, second.P95JitterMs, w1, w2),

                AvgLossRate = WeightedAverageKnown(first.AvgLossRate, second.AvgLossRate, w1, w2),
                StartupAdjustedLossRate = WeightedAverageKnown(first.StartupAdjustedLossRate, second.StartupAdjustedLossRate, w1, w2),
                AvgBadQualityRate = WeightedAverageKnown(first.AvgBadQualityRate, second.AvgBadQualityRate, w1, w2),
                AvgConfidence = WeightedAverageKnown(first.AvgConfidence, second.AvgConfidence, w1, w2),
                SpikeDensityPerMinute = WeightedAverageKnown(first.SpikeDensityPerMinute, second.SpikeDensityPerMinute, w1, w2),
                StaleRate = WeightedAverageKnown(first.StaleRate, second.StaleRate, w1, w2),

                TunnelConnected = first.TunnelConnected || second.TunnelConnected,
                AggregateCongested = first.AggregateCongested || second.AggregateCongested,
                ActiveConnectionCount = Math.Max(first.ActiveConnectionCount, second.ActiveConnectionCount),
                BackupConnectionCount = Math.Max(first.BackupConnectionCount, second.BackupConnectionCount),
                SpeedifyAggregateLatencyMs = WeightedAverageKnown(first.SpeedifyAggregateLatencyMs, second.SpeedifyAggregateLatencyMs, w1, w2),
                SpeedifyAggregateJitterMs = WeightedAverageKnown(first.SpeedifyAggregateJitterMs, second.SpeedifyAggregateJitterMs, w1, w2),
                SpeedifyReportedLatencyMs = WeightedAverageKnown(first.SpeedifyReportedLatencyMs, second.SpeedifyReportedLatencyMs, w1, w2),
                SpeedifyFreshSampleCount = Math.Max(0, first.SpeedifyFreshSampleCount) + Math.Max(0, second.SpeedifyFreshSampleCount),
                SpeedifyExpectedSampleCount = WeightedAverageKnown(first.SpeedifyExpectedSampleCount, second.SpeedifyExpectedSampleCount, w1, w2),
                SpeedifyAggregateLatencyStdDevMs = WeightedAverageKnown(first.SpeedifyAggregateLatencyStdDevMs, second.SpeedifyAggregateLatencyStdDevMs, w1, w2),
                FirstFreshPostSwitchStatsUtc = ChooseEarliestKnown(first.FirstFreshPostSwitchStatsUtc, second.FirstFreshPostSwitchStatsUtc),
                FirstFreshPostSwitchAggLatencyMs = WeightedAverageKnown(first.FirstFreshPostSwitchAggLatencyMs, second.FirstFreshPostSwitchAggLatencyMs, w1, w2),
                FirstFreshPostSwitchAggJitterMs = WeightedAverageKnown(first.FirstFreshPostSwitchAggJitterMs, second.FirstFreshPostSwitchAggJitterMs, w1, w2),
                FirstFreshPostSwitchReportedLatencyMs = WeightedAverageKnown(first.FirstFreshPostSwitchReportedLatencyMs, second.FirstFreshPostSwitchReportedLatencyMs, w1, w2),
                FirstFreshPostSwitchActiveConnectionCount = Math.Max(first.FirstFreshPostSwitchActiveConnectionCount, second.FirstFreshPostSwitchActiveConnectionCount),
                FirstFreshPostSwitchAggregateCongested = first.FirstFreshPostSwitchAggregateCongested || second.FirstFreshPostSwitchAggregateCongested,

                IsValid = first.IsValid || second.IsValid,
                Notes = appendNote(first.Notes, second.Notes),

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

        public static double WeightedAverageKnown(double a, double b, double wa, double wb)
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

        public static double MaxKnown(double a, double b)
        {
            if (a < 0 && b < 0) return -1;
            if (a < 0) return b;
            if (b < 0) return a;
            return Math.Max(a, b);
        }

        public static double MinKnown(double a, double b)
        {
            if (a < 0 && b < 0) return -1;
            if (a < 0) return b;
            if (b < 0) return a;
            return Math.Min(a, b);
        }

        private static DateTime ChooseEarliestKnown(DateTime a, DateTime b)
        {
            bool hasA = a != DateTime.MinValue;
            bool hasB = b != DateTime.MinValue;
            if (!hasA && !hasB)
                return DateTime.MinValue;
            if (!hasA)
                return b;
            if (!hasB)
                return a;
            return a <= b ? a : b;
        }
    }
}
