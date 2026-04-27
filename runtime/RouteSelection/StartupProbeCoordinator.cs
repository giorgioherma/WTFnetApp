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
        private readonly Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, DateTime, Task<ProbeCandidateRuntimeData>> captureProbeAsync;
        private readonly ServerProbeLearningStore learningStore;
        private readonly StartupProbeTimingConfig timing;
        private readonly Action<string> log;
        private readonly StartupRawProbe rawProbe;
        private readonly StartupSpeedifyTunnelProbe routedProbe;
        private readonly StartupProbeCandidateOrdering candidateOrdering;
        private readonly StartupProbeFinalization finalization;
        private readonly StartupLogger startupLogger;
        private readonly StartupProbeResultBuilder resultBuilder = new StartupProbeResultBuilder();
        private readonly ProbeDecisionStabilityState stabilityState = new ProbeDecisionStabilityState();

        public StartupProbeCoordinator(
            Func<Task<bool>> disableSpeedifyAsync,
            Func<string, Task<bool>> activateSpeedifyAsync,
            Func<FastPingState> getFastPingState,
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
            Func<MiniSpeedifyMiniSnap> getMiniSpeedifyMiniSnap,
            Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, DateTime, Task<ProbeCandidateRuntimeData>> captureProbeAsync,
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
            this.timing = timing ?? throw new ArgumentNullException(nameof(timing));
            this.log = log;
            startupLogger = new StartupLogger(Log);
            candidateOrdering = new StartupProbeCandidateOrdering();
            finalization = new StartupProbeFinalization(
                this.disableSpeedifyAsync,
                this.activateSpeedifyAsync,
                this.learningStore,
                this.timing,
                stabilityState,
                startupLogger,
                AppendNote,
                Log);

            rawProbe = new StartupRawProbe(
                this.timing,
                this.getShortWindowSnapshot,
                IsCandidateUsableForMeasurement,
                this.captureProbeAsync,
                (first, second, firstDuration, secondDuration) =>
                    StartupProbeRuntimeMerge.MergeMeasuredRuntime(first, second, firstDuration, secondDuration, AppendNote),
                AppendNote,
                Log);

            routedProbe = new StartupSpeedifyTunnelProbe(
                getFastPingState: this.getFastPingState,
                getShortWindowSnapshot: this.getShortWindowSnapshot,
                getMiniSpeedifyMiniSnap: getMiniSpeedifyMiniSnap,
                isCandidateUsableForMeasurement: IsCandidateUsableForMeasurement,
                captureProbeAsync: this.captureProbeAsync,
                timing: this.timing,
                startupLogger: startupLogger,
                mergeMeasuredRuntime: (first, second, firstDuration, secondDuration) =>
                    StartupProbeRuntimeMerge.MergeMeasuredRuntime(first, second, firstDuration, secondDuration, AppendNote),
                appendNote: AppendNote);
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

            List<ProbeCandidateDefinition> ordered = candidateOrdering.ReorderRoutedCandidatesForStartup(
                originalRoutedOrder,
                timeBucket,
                learningStore,
                Log);

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
                return resultBuilder.BuildUnreliableResult(output, null, null, "raw-unusable-before-measurement", Log);
            }

            Log("startup-probe | raw usable -> protected startup + mandatory measurement start");

            ProbeCandidateRuntimeData rawRuntime = await rawProbe.RunAsync(
                rawCandidate,
                timeBucket,
                cancellationToken);

            if (rawRuntime == null)
                return resultBuilder.BuildUnreliableResult(output, null, null, "raw-measurement-failed", Log);

            rawRuntime.TimeBucketKey = timeBucket;
            rawRuntime.ReachedUsableState = true;
            rawRuntime.MeasurementStarted = true;
            rawRuntime.MeasurementCompleted = !rawRuntime.MeasurementStoppedEarly;
            rawRuntime.ReadinessKind = readiness.Kind;
            rawRuntime.ReadinessReason = readiness.Reason;
            NeutralizeRawStartupSpeedifyFields(rawRuntime);

            ProbeScoreResult rawScore = ProbeScoring.ScoreCandidate(StartupScoreRuntimeSanitizer.BuildScoreRuntimeWithoutSpeedify(rawRuntime));
            LogLossRiskIfNeeded(rawCandidate, rawRuntime, rawScore);

            LearnedProbeStateProfile rawLearned = learningStore.GetProfile(rawCandidate.RouteKey, timeBucket);
            rawScore.StartupSelectionScore = ProbeScoring.ComputeStartupSelectionScore(rawScore, rawLearned);
            rawScore.IsShortlisted = true;

            output.Baseline = rawScore;
            output.AllResults.Add(rawScore);
            output.Shortlist.Add(rawScore);

            ProbeScoreResult leader = rawScore;
            Log("startup-probe | leader initialized -> " + StartupProbeResultBuilder.FormatLeaderLabel(leader) +
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
                    startupLogger.LogEvent(candidate, "DISCARDED (activation failed)");
                    startupLogger.LogStartupProbeSummary(candidate, -1, "no", "activation-failed", 0, false, null, null, "activation-failed");
                    continue;
                }

                await BeginTransitionGraceAsync("candidate-activate:" + candidate.RouteKey, cancellationToken);
                StartupSpeedifyTunnelProbe.SpeedifyProbeLifecycleResult lifecycle = await routedProbe.RunLifecycleAsync(
                    candidate,
                    activationRequestedUtc,
                    timeBucket,
                    cancellationToken);

                if (!lifecycle.IsUsable)
                {
                    Log("startup-probe | discard | " + candidate +
                        " | discardReason=unusable-before-measurement | reason=" + lifecycle.Reason);
                    startupLogger.LogEvent(candidate, "DISCARDED (unusable-before-measurement: " + lifecycle.Reason + ")");
                    startupLogger.LogStartupProbeSummary(candidate, lifecycle.WaitedMs, "no", lifecycle.Reason, 0, false, null, null, "unusable-before-measurement");
                    continue;
                }

                Log("startup-probe | candidate usable -> measurement start | " + candidate +
                    " | warmupElapsedMs=" + lifecycle.WaitedMs.ToString("0"));

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

                ProbeCandidateRuntimeData runtime = lifecycle.Runtime;

                if (runtime == null)
                {
                    Log("startup-probe | discard | " + candidate + " | discardReason=measurement-capture-failed");
                    startupLogger.LogEvent(candidate, "DISCARDED (measurement-capture-failed)");
                    startupLogger.LogStartupProbeSummary(candidate, lifecycle.WaitedMs, "no", "measurement-capture-failed", 0, false, null, null, "discarded-early");
                    continue;
                }

                runtime.TimeBucketKey = timeBucket;
                runtime.ReadinessKind = candidateReadiness.Kind;
                runtime.ReadinessReason = candidateReadiness.Reason;
                runtime.Notes = AppendNote(runtime.Notes, lifecycle.WarmupTag);
                runtime.Notes = AppendNote(
                    runtime.Notes,
                    lifecycle.WarmupMiniTrusted ? "startup-mini-trusted" : "startup-mini-untrusted");

                ProbeScoreResult score = ProbeScoring.ScoreCandidate(StartupScoreRuntimeSanitizer.BuildScoreRuntimeWithoutSpeedify(runtime));
                LogLossRiskIfNeeded(candidate, runtime, score);

                LearnedProbeStateProfile candidateLearned = learningStore.GetProfile(candidate.RouteKey, timeBucket);
                score.StartupSelectionScore = ProbeScoring.ComputeStartupSelectionScore(score, candidateLearned);
                if (StartupLogger.EnableIdentityDiagnostics)
                {
                    Log("startup-probe | identity after score | candidate=" + candidate +
                        " | friendly=" + (score?.Runtime?.MeasuredServerFriendlyName ?? "-") +
                        " | tag=" + (score?.Runtime?.MeasuredServerTag ?? "-") +
                        " | publicIp=" + (score?.Runtime?.MeasuredServerPublicIp ?? "-") +
                        " | routeTarget=" + (score?.Runtime?.MeasuredRouteTarget ?? "-") +
                        " | auth=" + ((score?.Runtime?.HasAuthoritativeIdentity ?? false) ? "true" : "false"));
                }
                startupLogger.RegisterCandidateEvaluation(
                    candidate,
                    lifecycle.WaitedMs,
                    -1,
                    runtime.Notes != null && runtime.Notes.IndexOf("startup-confidence-extension", StringComparison.OrdinalIgnoreCase) >= 0,
                    runtime,
                    score);

                leader = finalization.HandleScoredCandidate(
                    candidate,
                    runtime,
                    score,
                    rawScore,
                    leader,
                    output);
            }

            return await finalization.FinalizeAsync(
                output,
                rawScore,
                leader,
                timeBucket,
                cancellationToken);
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

            bool churn =
    state.Confidence < 0.25 &&
    (window.StaleRate >= 0.35 || window.AvgLossRate >= 0.35);

            if (churn)
            {
                reason = "reconnect/disconnect churn";
                return false;
            }

            if (state.Confidence < 0.30)
            {
                reason = "confidence too low";
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

        private static void NeutralizeRawStartupSpeedifyFields(ProbeCandidateRuntimeData runtime)
        {
            if (runtime == null)
                return;

            runtime.StartupSpeedifyTrusted = false;
            runtime.TunnelConnected = false;
            runtime.AggregateCongested = false;
            runtime.ActiveConnectionCount = 0;
            runtime.BackupConnectionCount = 0;
            runtime.SpeedifyAggregateLatencyMs = -1;
            runtime.SpeedifyAggregateJitterMs = -1;
            runtime.SpeedifyReportedLatencyMs = -1;
            runtime.SpeedifyFreshSampleCount = 0;

            runtime.SpeedifyExpectedSampleCount = -1;
            runtime.SpeedifyAggregateLatencyStdDevMs = -1;
            runtime.FirstFreshPostSwitchStatsUtc = DateTime.MinValue;
            runtime.FirstFreshPostSwitchAggLatencyMs = -1;
            runtime.FirstFreshPostSwitchAggJitterMs = -1;
            runtime.FirstFreshPostSwitchReportedLatencyMs = -1;
            runtime.FirstFreshPostSwitchActiveConnectionCount = -1;
            runtime.FirstFreshPostSwitchAggregateCongested = false;
            runtime.StartupSpeedifyConfidenceScore = -1;
            runtime.StartupSpeedifyConfidenceLabel = null;
            runtime.StartupSpeedifyFastPingAgreementDeltaMs = -1;
            runtime.StartupSpeedifyBondedEvidence = false;
            runtime.StartupSpeedifyFastPingAgreementGood = false;
            runtime.StartupSpeedifyCongestedEvidence = false;
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

    internal delegate bool CandidateUsableEvaluator(ProbeCandidateDefinition candidate, out string reason);

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

    internal sealed class StartupProbeCandidateOrdering
    {
        private sealed class RoutedCandidateOrderingEntry
        {
            public ProbeCandidateDefinition Candidate { get; set; }
            public ServerProbeLearningStore.HistoricalRouteOrderingScore Historical { get; set; }
            public int OriginalIndex { get; set; }
            public int FreshnessRank { get; set; }
        }

        private const double CloseScoreOrderingMargin = 6.0;

        public List<ProbeCandidateDefinition> ReorderRoutedCandidatesForStartup(
            List<ProbeCandidateDefinition> originalOrder,
            string timeBucket,
            ServerProbeLearningStore learningStore,
            Action<string> log)
        {
            List<ProbeCandidateDefinition> safeOriginal = originalOrder ?? new List<ProbeCandidateDefinition>();

            log?.Invoke("startup-probe | routed order | original=" +
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
                log?.Invoke("startup-probe | trusted learning unavailable | using score ordering with close-score tie-breakers only");
            }

            entries.Sort(CompareRoutedCandidateOrderingEntries);

            log?.Invoke("startup-probe | routed order | reordered=" +
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
    }
}
