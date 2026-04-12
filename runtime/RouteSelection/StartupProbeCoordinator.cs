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
        private readonly StartupProbeTimingConfig timing;
        private readonly Action<string> log;
        private DateTime transitionGraceUntilUtc = DateTime.MinValue;

        public StartupProbeCoordinator(
            Func<Task<bool>> disableSpeedifyAsync,
            Func<string, Task<bool>> activateSpeedifyAsync,
            Func<FastPingState> getFastPingState,
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
            Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, Task<ProbeCandidateRuntimeData>> captureProbeAsync,
            ServerProbeLearningStore learningStore,
            StartupProbeTimingConfig timing = null,
            Action<string> log = null)
        {
            this.disableSpeedifyAsync = disableSpeedifyAsync ?? throw new ArgumentNullException(nameof(disableSpeedifyAsync));
            this.activateSpeedifyAsync = activateSpeedifyAsync ?? throw new ArgumentNullException(nameof(activateSpeedifyAsync));
            this.getFastPingState = getFastPingState ?? throw new ArgumentNullException(nameof(getFastPingState));
            this.getShortWindowSnapshot = getShortWindowSnapshot ?? throw new ArgumentNullException(nameof(getShortWindowSnapshot));
            this.captureProbeAsync = captureProbeAsync ?? throw new ArgumentNullException(nameof(captureProbeAsync));
            this.learningStore = learningStore ?? throw new ArgumentNullException(nameof(learningStore));
            this.timing = timing ?? StartupProbeTimingConfig.Default;
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

            await BeginTransitionGraceAsync("raw-disable", cancellationToken);

            bool rawSettled =
                await WaitForPlausibleSettleZoneAsync(
                    rawCandidate,
                    cancellationToken);

            if (!rawSettled)
                Log("startup-probe | raw settle zone timeout | proceeding cautiously");

            ProbeReadinessResult readiness =
                await WaitForReadinessAsync(
                    routeKey: rawCandidate.RouteKey,
                    timeBucketKey: timeBucket,
                    minWait: timing.ReadinessMinWait,
                    maxWait: timing.ReadinessMaxWait,
                    cancellationToken: cancellationToken);

            output.RawReadiness = readiness;
            learningStore.UpdateFromReadinessSnapshot(
                rawCandidate.RouteKey,
                timeBucket,
                getShortWindowSnapshot(),
                readiness.Kind);

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
                    timing.MeasurementStartGuard,
                    timing.ObserveDuration);

            rawRuntime.TimeBucketKey = timeBucket;
            rawRuntime.StabilizationReached = rawStabilized;
            rawRuntime.StabilizationTimedOut = !rawStabilized;
            rawRuntime.ReadinessKind = readiness.Kind;
            rawRuntime.ReadinessReason = readiness.Reason;
            rawRuntime.Notes = rawStabilized
                ? rawRuntime.Notes
                : AppendNote(rawRuntime.Notes, "raw-stabilization-timeout");

            ProbeScoreResult rawScore = ProbeScoring.ScoreCandidate(rawRuntime);
            LearnedProbeStateProfile rawLearned = learningStore.GetProfile(rawCandidate.RouteKey, timeBucket);
            rawScore.StartupSelectionScore = ProbeScoring.ComputeStartupSelectionScore(rawScore, rawLearned);
            rawScore.IsShortlisted = true;

            output.Baseline = rawScore;
            output.AllResults.Add(rawScore);
            output.Shortlist.Add(rawScore);

            ProbeScoreResult leader = rawScore;
            Log("startup-probe | leader initialized -> " + FormatLeaderLabel(leader));

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
                    Log("startup-probe | candidate lost -> restoring leader " + FormatLeaderLabel(leader));
                    bool restored = await RestoreLeaderRouteAsync(leader, cancellationToken);
                    if (!restored)
                        return BuildUnreliableResult(output, leader, rawScore, "leader-restore-failed");
                    continue;
                }

                await BeginTransitionGraceAsync("candidate-activate:" + candidate.RouteKey, cancellationToken);

                bool candidateSettled =
                    await WaitForPlausibleSettleZoneAsync(
                        candidate,
                        cancellationToken);

                if (!candidateSettled)
                {
                    Log("startup-probe | discard | " + candidate + " | discardReason=settle-zone-timeout");
                    Log("startup-probe | candidate lost -> restoring leader " + FormatLeaderLabel(leader));
                    bool restoredBySettleTimeout = await RestoreLeaderRouteAsync(leader, cancellationToken);
                    if (!restoredBySettleTimeout)
                        return BuildUnreliableResult(output, leader, rawScore, "leader-restore-failed");
                    continue;
                }

                ProbeReadinessResult candidateReadiness =
                    await WaitForReadinessAsync(
                        routeKey: candidate.RouteKey,
                        timeBucketKey: timeBucket,
                        minWait: timing.ReadinessMinWait,
                        maxWait: timing.ReadinessMaxWait,
                        cancellationToken: cancellationToken);

                learningStore.UpdateFromReadinessSnapshot(
                    candidate.RouteKey,
                    timeBucket,
                    getShortWindowSnapshot(),
                    candidateReadiness.Kind);

                Log("startup-probe | candidate readiness | " + candidate +
                    " | " + candidateReadiness.Kind + " | " + candidateReadiness.Reason);

                bool stabilized =
                    await WaitForRouteStabilizationAsync(
                        routeLabel: candidate.ToString(),
                        cancellationToken);

                if (stabilized)
                    Log("candidate measure | " + candidate + " | started after stabilization");
                else
                {
                    Log("candidate measure | " + candidate + " | skipped (stabilization timeout)");
                }

                TimeSpan observeDuration = stabilized
                    ? timing.ObserveDuration
                    : timing.UnstableObserveDuration;

                ProbeCandidateRuntimeData runtime = await captureProbeAsync(
                    candidate,
                    timing.MeasurementStartGuard,
                    observeDuration);

                runtime.TimeBucketKey = timeBucket;
                runtime.StabilizationReached = stabilized;
                runtime.StabilizationTimedOut = !stabilized;
                runtime.ReadinessKind = candidateReadiness.Kind;
                runtime.ReadinessReason = candidateReadiness.Reason;
                runtime.Notes = stabilized
                    ? runtime.Notes
                    : AppendNote(runtime.Notes, "stabilization-timeout");

                if (!stabilized)
                {
                    runtime.Notes = AppendNote(runtime.Notes, "unstable-for-run");
                    runtime.IsValid = false;
                }

                ProbeScoreResult score = ProbeScoring.ScoreCandidate(runtime);
                LearnedProbeStateProfile candidateLearned = learningStore.GetProfile(candidate.RouteKey, timeBucket);
                score.StartupSelectionScore = ProbeScoring.ComputeStartupSelectionScore(score, candidateLearned);

                Log("startup-probe | compare | " + candidate +
                    " | composite=" + score.StartupSelectionScore.ToString("0.0") +
                    " | stability=" + score.OverallStabilityScore.ToString("0.0") +
                    " | warning=" + ProbeScoring.GetWarningLabel(score));

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

                    Log("startup-probe | candidate lost -> restoring leader " + FormatLeaderLabel(leader));
                    bool restored = await RestoreLeaderRouteAsync(leader, cancellationToken);
                    if (!restored)
                        return BuildUnreliableResult(output, leader, rawScore, "leader-restore-failed");
                    continue;
                }

                output.AllResults.Add(score);
                score.IsShortlisted = true;
                output.Shortlist.Add(score);

                // Display shortlist: top performers by startup composite score.
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
                    Log("startup-probe | candidate lost -> restoring leader " + FormatLeaderLabel(leader));
                    bool restored = await RestoreLeaderRouteAsync(leader, cancellationToken);
                    if (!restored)
                        return BuildUnreliableResult(output, leader, rawScore, "leader-restore-failed");
                }

                Log("startup-probe | keep | " + candidate +
                    " | composite=" + score.StartupSelectionScore.ToString("0.0") +
                    " | " + ProbeScoring.BuildShortText(score) +
                    " | overall=" + score.OverallStabilityScore.ToString("0.0") +
                    " | jitterScore=" + score.JitterStabilityScore.ToString("0.0") +
                    " | explain=" + ProbeScoring.BuildRouteExplanation(score));
            }

            output.Shortlist = output.Shortlist
                .OrderBy(x => x.StartupSelectionScore)
                .ToList();

            ProbeScoring.AssignSmoothestBadge(output.Shortlist);

            output.Winner = leader ?? rawScore;

            if (output.Winner != null)
                output.Winner.IsWinner = true;

            ProbeScoreResult runnerUp = output.Shortlist
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

                if (IsWithinTransitionGrace(nowUtc))
                {
                    await Task.Delay(200, cancellationToken);
                    continue;
                }

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

                if (consecutiveStableMatches >= timing.ReadinessConsecutiveChecks)
                    return readiness;

                await Task.Delay(350, cancellationToken);
            }
        }

        private async Task<bool> WaitForRouteStabilizationAsync(
            string routeLabel,
            CancellationToken cancellationToken)
        {
            DateTime startedUtc = DateTime.UtcNow;
            DateTime earliestCheckUtc = startedUtc + timing.StabilizeMinWait;
            DateTime hardStopUtc = startedUtc + timing.StabilizeMaxWait;

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

                if (IsWithinTransitionGrace(nowUtc))
                {
                    await Task.Delay(220, cancellationToken);
                    continue;
                }

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
                    Log(prefix + "stableCheck=" + stableChecks + "/" + timing.StabilizeConsecutiveChecks);

                    if (stableChecks >= timing.StabilizeConsecutiveChecks)
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

            if (fastPingState.Confidence < 0.68)
                return false;

            if (!fastPingState.PrimaryFresh || !fastPingState.SecondaryFresh)
                return false;

            if (shortWindow.StaleRate > 0.08)
                return false;

            if (shortWindow.SpikeDensityPerMinute > 5.0)
                return false;

            if (shortWindow.AvgJitterMs > 16.0)
                return false;

            if (shortWindow.AvgLossRate >= 0.16)
                return false;

            if (shortWindow.P95PingMs >= 220.0)
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

        private async Task BeginTransitionGraceAsync(string reason, CancellationToken cancellationToken)
        {
            DateTime untilUtc = DateTime.UtcNow + timing.TransitionGrace;
            transitionGraceUntilUtc = untilUtc;

            Log("startup-probe | transition grace | " + reason +
                " | " + timing.TransitionGrace.TotalMilliseconds.ToString("0") + "ms");

            await Task.Delay(timing.TransitionGrace, cancellationToken);
        }

        private bool IsWithinTransitionGrace(DateTime nowUtc)
        {
            return nowUtc < transitionGraceUntilUtc;
        }

        private async Task<bool> WaitForPlausibleSettleZoneAsync(
            ProbeCandidateDefinition candidate,
            CancellationToken cancellationToken)
        {
            bool speedifyCandidate = candidate != null && candidate.Kind == ProbeCandidateKind.Speedify;

            DateTime startedUtc = DateTime.UtcNow;
            DateTime hardStopUtc = startedUtc + timing.SettleZoneMaxWait;
            int stableChecks = 0;

            string label = candidate != null ? candidate.ToString() : "unknown";
            Log("startup-probe | settle zone | " + label + " | started");

            while (DateTime.UtcNow < hardStopUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FastPingState state = getFastPingState();
                FastPingSignalAnalyzer.FastPingAggregateSnapshot window = getShortWindowSnapshot();

                bool settled = IsInPlausibleSettleZone(window, state, speedifyCandidate);
                if (settled)
                {
                    stableChecks++;
                    if (stableChecks >= timing.SettleZoneConsecutiveChecks)
                    {
                        double elapsed = (DateTime.UtcNow - startedUtc).TotalSeconds;
                        Log("startup-probe | settle zone | " + label + " | reached after=" + elapsed.ToString("0.0") + "s");
                        return true;
                    }
                }
                else
                {
                    stableChecks = 0;
                }

                await Task.Delay(300, cancellationToken);
            }

            Log("startup-probe | settle zone | " + label + " | timeout");
            return false;
        }

        private static bool IsInPlausibleSettleZone(
            FastPingSignalAnalyzer.FastPingAggregateSnapshot window,
            FastPingState state,
            bool speedifyCandidate)
        {
            if (state == null || window == null || !window.IsValid)
                return false;

            if (state.IsStale || !state.PrimaryFresh || !state.SecondaryFresh)
                return false;

            if (state.Confidence < 0.45)
                return false;

            if (speedifyCandidate)
            {
                if (window.StaleRate > 0.30)
                    return false;

                if (window.AvgLossRate > 0.45)
                    return false;

                if (window.SpikeDensityPerMinute > 12.0)
                    return false;

                if (window.AvgJitterMs > 30.0)
                    return false;
            }

            return true;
        }

        private StartupProbeResult BuildUnreliableResult(
            StartupProbeResult output,
            ProbeScoreResult leader,
            ProbeScoreResult rawScore,
            string reason)
        {
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

        private async Task<bool> RestoreLeaderRouteAsync(ProbeScoreResult leader, CancellationToken cancellationToken)
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
            {
                shortlist.RemoveAt(shortlist.Count - 1);
            }
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
                        " | " +
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

    public sealed class StartupProbeTimingConfig
    {
        public TimeSpan ReadinessMinWait { get; set; } = TimeSpan.FromSeconds(4.0);
        public TimeSpan ReadinessMaxWait { get; set; } = TimeSpan.FromSeconds(10.0);
        public int ReadinessConsecutiveChecks { get; set; } = 3;
        public TimeSpan StabilizeMinWait { get; set; } = TimeSpan.FromSeconds(2.5);
        public TimeSpan StabilizeMaxWait { get; set; } = TimeSpan.FromSeconds(16.0);
        public int StabilizeConsecutiveChecks { get; set; } = 3;
        public TimeSpan TransitionGrace { get; set; } = TimeSpan.FromSeconds(1.6);
        public TimeSpan SettleZoneMaxWait { get; set; } = TimeSpan.FromSeconds(8.0);
        public int SettleZoneConsecutiveChecks { get; set; } = 3;
        public TimeSpan MeasurementStartGuard { get; set; } = TimeSpan.FromSeconds(0.8);
        public TimeSpan UnstableObserveDuration { get; set; } = TimeSpan.FromSeconds(2.0);
        public TimeSpan ObserveDuration { get; set; } = TimeSpan.FromSeconds(10.0);

        public static StartupProbeTimingConfig Default { get; } = new StartupProbeTimingConfig();
    }
}
