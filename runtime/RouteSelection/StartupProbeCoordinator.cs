using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WTFnetApp
{
    public sealed class StartupProbeCoordinator
    {
        private static readonly TimeSpan CandidateStabilizeMinWait = TimeSpan.FromSeconds(2.5);
        private static readonly TimeSpan CandidateStabilizeMaxWait = TimeSpan.FromSeconds(16.0);
        private const int CandidateStabilizeConsecutiveChecks = 3;

        private readonly Func<Task<bool>> disableSpeedifyAsync;
        private readonly Func<string, Task<bool>> activateSpeedifyAsync;
        private readonly Func<FastPingState> getFastPingState;
        private readonly Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot;
        private readonly Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, Task<ProbeCandidateRuntimeData>> captureProbeAsync;
        private readonly ServerProbeLearningStore learningStore;
        private readonly Action<string> log;

        public StartupProbeCoordinator(
            Func<Task<bool>> disableSpeedifyAsync,
            Func<string, Task<bool>> activateSpeedifyAsync,
            Func<FastPingState> getFastPingState,
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
            Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, Task<ProbeCandidateRuntimeData>> captureProbeAsync,
            ServerProbeLearningStore learningStore,
            Action<string> log = null)
        {
            this.disableSpeedifyAsync = disableSpeedifyAsync ?? throw new ArgumentNullException(nameof(disableSpeedifyAsync));
            this.activateSpeedifyAsync = activateSpeedifyAsync ?? throw new ArgumentNullException(nameof(activateSpeedifyAsync));
            this.getFastPingState = getFastPingState ?? throw new ArgumentNullException(nameof(getFastPingState));
            this.getShortWindowSnapshot = getShortWindowSnapshot ?? throw new ArgumentNullException(nameof(getShortWindowSnapshot));
            this.captureProbeAsync = captureProbeAsync ?? throw new ArgumentNullException(nameof(captureProbeAsync));
            this.learningStore = learningStore ?? throw new ArgumentNullException(nameof(learningStore));
            this.log = log;
        }

        public async Task<StartupProbeResult> RunAsync(
            IEnumerable<ProbeCandidateDefinition> orderedSpeedifyCandidates,
            CancellationToken cancellationToken = default)
        {
            if (orderedSpeedifyCandidates == null)
                throw new ArgumentNullException(nameof(orderedSpeedifyCandidates));

            learningStore.SeedDefaultsIfEmpty();

            List<ProbeCandidateDefinition> ordered = orderedSpeedifyCandidates.ToList();
            StartupProbeResult output = new StartupProbeResult();

            string timeBucket = ProbeScoring.GetCurrentTimeBucketKey(DateTime.Now);

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

            ProbeReadinessResult readiness =
                await WaitForReadinessAsync(
                    routeKey: rawCandidate.RouteKey,
                    timeBucketKey: timeBucket,
                    minWait: TimeSpan.FromSeconds(4),
                    maxWait: TimeSpan.FromSeconds(12),
                    cancellationToken: cancellationToken);

            output.RawReadiness = readiness;

            Log("startup-probe | raw readiness | " + readiness.Kind + " | " + readiness.Reason);

            bool rawStabilized =
                await WaitForRouteStabilizationAsync(
                    routeLabel: "raw",
                    cancellationToken: cancellationToken);

            if (rawStabilized)
                Log("raw measure | started after stabilization");
            else
                Log("raw measure | started with fallback (stabilization timeout)");

            ProbeCandidateRuntimeData rawRuntime =
                await captureProbeAsync(
                    rawCandidate,
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(12));

            rawRuntime.TimeBucketKey = timeBucket;
            rawRuntime.StabilizationReached = rawStabilized;
            rawRuntime.StabilizationTimedOut = !rawStabilized;
            rawRuntime.Notes = rawStabilized
                ? rawRuntime.Notes
                : AppendNote(rawRuntime.Notes, "raw-stabilization-timeout");

            ProbeScoreResult rawScore = ProbeScoring.ScoreCandidate(rawRuntime);
            rawScore.IsShortlisted = true;

            output.Baseline = rawScore;
            output.AllResults.Add(rawScore);
            output.Shortlist.Add(rawScore);

            ProbeScoreResult leader = rawScore;

            foreach (ProbeCandidateDefinition candidate in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (candidate == null)
                    continue;

                if (candidate.Kind != ProbeCandidateKind.Speedify)
                    continue;

                Log("startup-probe | candidate start | " + candidate);

                bool activated = await activateSpeedifyAsync(candidate.ConnectTarget);
                if (!activated)
                {
                    Log("startup-probe | discard | " + candidate + " | discardReason=activation failed");
                    continue;
                }

                ProbeReadinessResult candidateReadiness =
                    await WaitForReadinessAsync(
                        routeKey: candidate.RouteKey,
                        timeBucketKey: timeBucket,
                        minWait: TimeSpan.FromSeconds(4),
                        maxWait: TimeSpan.FromSeconds(10),
                        cancellationToken: cancellationToken);

                Log("startup-probe | candidate readiness | " + candidate +
                    " | " + candidateReadiness.Kind + " | " + candidateReadiness.Reason);

                bool stabilized =
                    await WaitForRouteStabilizationAsync(
                        routeLabel: candidate.ToString(),
                        cancellationToken);

                if (stabilized)
                    Log("candidate measure | " + candidate + " | started after stabilization");
                else
                    Log("candidate measure | " + candidate + " | started with fallback (stabilization timeout)");

                ProbeCandidateRuntimeData runtime =
                    await captureProbeAsync(
                        candidate,
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(10));

                runtime.TimeBucketKey = timeBucket;
                runtime.StabilizationReached = stabilized;
                runtime.StabilizationTimedOut = !stabilized;
                runtime.Notes = stabilized
                    ? runtime.Notes
                    : AppendNote(runtime.Notes, "stabilization-timeout");

                ProbeScoreResult score = ProbeScoring.ScoreCandidate(runtime);

                ProbeDiscardDecision discardDecision =
                    ProbeScoring.EvaluateDiscardDecision(score, rawScore, leader);

                bool discard = discardDecision.ShouldDiscard;

                if (discard)
                {
                    score.IsDiscardedEarly = true;
                    output.AllResults.Add(score);

                    Log("startup-probe | discard | " + candidate +
                        " | discardReason=" + (discardDecision.ReasonCategory ?? "unknown") +
                        " | " + ProbeScoring.BuildShortText(score) +
                        " | overall=" + score.OverallStabilityScore.ToString("0.0") +
                        " | jitterScore=" + score.JitterStabilityScore.ToString("0.0") +
                        " | explain=" + ProbeScoring.BuildRouteExplanation(score));

                    continue;
                }

                output.AllResults.Add(score);
                score.IsShortlisted = true;
                output.Shortlist.Add(score);

                TrimShortlist(output.Shortlist, 3);

                leader = output.Shortlist
                    .OrderBy(x => x.OverallStabilityScore)
                    .FirstOrDefault() ?? rawScore;

                Log("startup-probe | keep | " + candidate +
                    " | " + ProbeScoring.BuildShortText(score) +
                    " | overall=" + score.OverallStabilityScore.ToString("0.0") +
                    " | jitterScore=" + score.JitterStabilityScore.ToString("0.0") +
                    " | explain=" + ProbeScoring.BuildRouteExplanation(score));
            }

            output.Shortlist = output.Shortlist
                .OrderBy(x => x.OverallStabilityScore)
                .ToList();

            ProbeScoring.AssignSmoothestBadge(output.Shortlist);

            output.Winner = output.Shortlist
                .OrderBy(x => x.OverallStabilityScore)
                .FirstOrDefault() ?? rawScore;

            if (output.Winner != null)
                output.Winner.IsWinner = true;

            ProbeScoreResult runnerUp = output.Shortlist
                .Where(x => x != null && x != output.Winner)
                .OrderBy(x => x.OverallStabilityScore)
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
                winnerLearned);

            output.SummaryText = BuildSummary(output);

            Log(output.SummaryText);

            return output;
        }

        public async Task<ProbeReadinessResult> WaitForReadinessAsync(
            string routeKey,
            string timeBucketKey,
            TimeSpan minWait,
            TimeSpan maxWait,
            CancellationToken cancellationToken = default)
        {
            DateTime startedUtc = DateTime.UtcNow;
            DateTime earliestCheckUtc = startedUtc + minWait;
            DateTime hardStopUtc = startedUtc + maxWait;

            int consecutiveStableMatches = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DateTime nowUtc = DateTime.UtcNow;

                if (nowUtc < earliestCheckUtc)
                {
                    await Task.Delay(250, cancellationToken);
                    continue;
                }

                bool timeoutReached = nowUtc >= hardStopUtc;

                FastPingState fastPingState = getFastPingState();
                FastPingSignalAnalyzer.FastPingAggregateSnapshot shortWindow = getShortWindowSnapshot();

                LearnedProbeStateProfile learned =
                    learningStore.GetProfileForReadiness(routeKey, timeBucketKey);

                ProbeReadinessResult readiness =
                    ProbeScoring.EvaluateReadiness(
                        shortWindow,
                        fastPingState,
                        learned,
                        timeoutReached);

                if (readiness.Kind == ProbeReadinessKind.GoodStable ||
                    readiness.Kind == ProbeReadinessKind.BadStable)
                {
                    consecutiveStableMatches++;
                }
                else if (readiness.Kind == ProbeReadinessKind.TimeoutFallback)
                {
                    return readiness;
                }
                else
                {
                    consecutiveStableMatches = 0;
                }

                if (consecutiveStableMatches >= 3)
                    return readiness;

                await Task.Delay(350, cancellationToken);
            }
        }

        private async Task<bool> WaitForRouteStabilizationAsync(
            string routeLabel,
            CancellationToken cancellationToken)
        {
            DateTime startedUtc = DateTime.UtcNow;
            DateTime earliestCheckUtc = startedUtc + CandidateStabilizeMinWait;
            DateTime hardStopUtc = startedUtc + CandidateStabilizeMaxWait;

            int stableChecks = 0;

            string normalizedLabel = string.IsNullOrWhiteSpace(routeLabel) ? "unknown" : routeLabel;
            string prefix = string.Equals(normalizedLabel, "raw", StringComparison.OrdinalIgnoreCase)
                ? "raw stabilize | "
                : "candidate stabilize | " + normalizedLabel + " | ";

            Log(prefix + "started");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DateTime nowUtc = DateTime.UtcNow;

                if (nowUtc < earliestCheckUtc)
                {
                    await Task.Delay(250, cancellationToken);
                    continue;
                }

                bool timeoutReached = nowUtc >= hardStopUtc;
                if (timeoutReached)
                {
                    double elapsed = (nowUtc - startedUtc).TotalSeconds;
                    Log(prefix +
                        "timeout | after=" + elapsed.ToString("0.0") +
                        "s | using fallback measurement");
                    return false;
                }

                FastPingState fastPingState = getFastPingState();
                FastPingSignalAnalyzer.FastPingAggregateSnapshot shortWindow = getShortWindowSnapshot();

                bool stableNow = IsCandidateStabilized(shortWindow, fastPingState);

                if (stableNow)
                {
                    stableChecks++;
                    Log(prefix + "stableCheck=" + stableChecks + "/" + CandidateStabilizeConsecutiveChecks);

                    if (stableChecks >= CandidateStabilizeConsecutiveChecks)
                    {
                        double elapsed = (nowUtc - startedUtc).TotalSeconds;
                        Log(prefix + "reached | after=" + elapsed.ToString("0.0") + "s");
                        return true;
                    }
                }
                else
                {
                    stableChecks = 0;
                }

                await Task.Delay(400, cancellationToken);
            }
        }

        private static bool IsCandidateStabilized(
            FastPingSignalAnalyzer.FastPingAggregateSnapshot shortWindow,
            FastPingState fastPingState)
        {
            if (shortWindow == null || !shortWindow.IsValid || fastPingState == null)
                return false;

            if (fastPingState.IsStale)
                return false;

            if (fastPingState.Confidence < 0.62)
                return false;

            if (shortWindow.StaleRate > 0.10)
                return false;

            if (shortWindow.SpikeDensityPerMinute > 6.0)
                return false;

            if (shortWindow.AvgJitterMs > 18.0)
                return false;

            return true;
        }

        private static string AppendNote(string notes, string suffix)
        {
            if (string.IsNullOrWhiteSpace(suffix))
                return notes;

            if (string.IsNullOrWhiteSpace(notes))
                return suffix;

            return notes + " | " + suffix;
        }

        private static void TrimShortlist(List<ProbeScoreResult> shortlist, int maxKeep)
        {
            if (shortlist == null)
                return;

            shortlist.Sort((a, b) => a.OverallStabilityScore.CompareTo(b.OverallStabilityScore));

            while (shortlist.Count > maxKeep)
                shortlist.RemoveAt(shortlist.Count - 1);
        }

        private string BuildSummary(StartupProbeResult result)
        {
            if (result == null)
                return "startup-probe | no result";

            string winner = result.Winner != null
                ? result.Winner.Candidate +
                  " | overall=" + result.Winner.OverallStabilityScore.ToString("0.0") +
                  " | badge=" + ProbeScoring.GetBadgeLabel(result.Winner)
                : "none";

            ProbeScoreResult runnerUp = result.Shortlist != null
                ? result.Shortlist
                    .Where(x => x != null && x != result.Winner)
                    .OrderBy(x => x.OverallStabilityScore)
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
                result.WinnerConfidence);

            string shortlist = result.Shortlist != null && result.Shortlist.Count > 0
                ? string.Join(" | ",
                    result.Shortlist.Select(x =>
                        x.Candidate + " => " +
                        "overall=" + x.OverallStabilityScore.ToString("0.0") +
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
    }

    public sealed class StartupProbeResult
    {
        public ProbeReadinessResult RawReadiness { get; set; }
        public ProbeScoreResult Baseline { get; set; }
        public ProbeScoreResult Winner { get; set; }

        public List<ProbeScoreResult> AllResults { get; set; } = new List<ProbeScoreResult>();
        public List<ProbeScoreResult> Shortlist { get; set; } = new List<ProbeScoreResult>();
        public ProbeWinnerConfidenceResult WinnerConfidence { get; set; }

        public string SummaryText { get; set; }
    }
}
