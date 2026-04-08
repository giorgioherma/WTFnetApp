using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WTFnetApp
{
    public sealed class StartupProbeCoordinator
    {
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

            ProbeCandidateRuntimeData rawRuntime =
                await captureProbeAsync(
                    rawCandidate,
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(12));

            rawRuntime.TimeBucketKey = timeBucket;

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
                    Log("startup-probe | activation failed | " + candidate);
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

                ProbeCandidateRuntimeData runtime =
                    await captureProbeAsync(
                        candidate,
                        TimeSpan.Zero,
                        TimeSpan.FromSeconds(10));

                runtime.TimeBucketKey = timeBucket;

                ProbeScoreResult score = ProbeScoring.ScoreCandidate(runtime);

                bool discard = ProbeScoring.ShouldDiscardEarly(score, rawScore, leader);

                if (discard)
                {
                    score.IsDiscardedEarly = true;
                    output.AllResults.Add(score);

                    Log("startup-probe | discard | " + candidate +
                        " | " + ProbeScoring.BuildShortText(score) +
                        " | overall=" + score.OverallStabilityScore.ToString("0.0") +
                        " | jitterScore=" + score.JitterStabilityScore.ToString("0.0"));

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
                    " | jitterScore=" + score.JitterStabilityScore.ToString("0.0"));
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
                    learningStore.GetProfile(routeKey, timeBucketKey);

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
                ? result.Winner.Candidate + " (" + ProbeScoring.BuildShortText(result.Winner) + ")"
                : "none";

            string shortlist = result.Shortlist != null && result.Shortlist.Count > 0
                ? string.Join(" | ",
                    result.Shortlist.Select(x =>
                        x.Candidate + " => " +
                        ProbeScoring.BuildShortText(x) +
                        " | overall=" + x.OverallStabilityScore.ToString("0.0") +
                        " | jitterScore=" + x.JitterStabilityScore.ToString("0.0")))
                : "none";

            return "startup-probe | winner=" + winner + " | shortlist=" + shortlist;
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

        public string SummaryText { get; set; }
    }
}