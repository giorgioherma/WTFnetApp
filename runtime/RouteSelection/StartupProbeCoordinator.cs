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

            ProbeReadinessResult readiness = new ProbeReadinessResult
            {
                Kind = ProbeReadinessKind.TimeoutFallback,
                Reason = "fixed-settle measurement"
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

            Log("startup-probe | raw usable -> measurement start");

            ProbeCandidateRuntimeData rawRuntime = await RunSegmentedMeasurementAsync(
                rawCandidate,
                cancellationToken);

            if (rawRuntime == null)
                return BuildUnreliableResult(output, null, null, "raw-measurement-failed");

            rawRuntime.TimeBucketKey = timeBucket;
            rawRuntime.StabilizationReached = true;
            rawRuntime.StabilizationTimedOut = false;
            rawRuntime.ReachedUsableState = true;
            rawRuntime.MeasurementStarted = true;
            rawRuntime.MeasurementCompleted = true;
            rawRuntime.ReadinessKind = readiness.Kind;
            rawRuntime.ReadinessReason = readiness.Reason;

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
                    continue;
                }

                await BeginTransitionGraceAsync("candidate-activate:" + candidate.RouteKey, cancellationToken);
                bool candidateUsable = IsCandidateUsableForMeasurement(candidate, out string candidateUsableReason);
                if (!candidateUsable)
                {
                    Log("startup-probe | discard | " + candidate + " | discardReason=unusable-before-measurement | reason=" + candidateUsableReason);
                    continue;
                }

                Log("startup-probe | candidate usable -> measurement start | " + candidate);

                ProbeReadinessResult candidateReadiness = new ProbeReadinessResult
                {
                    Kind = ProbeReadinessKind.TimeoutFallback,
                    Reason = "fixed-settle measurement"
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

                ProbeScoreResult score = ProbeScoring.ScoreCandidate(runtime);
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
                        " | warning=" + ProbeScoring.GetWarningLabel(score));
                }

                ProbeDiscardDecision discardDecision =
                    ProbeScoring.EvaluateDiscardDecision(score, rawScore, leader);

                bool discard = discardDecision.ShouldDiscard;

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
                        " | explain=" + ProbeScoring.BuildRouteExplanation(score));
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
                    Log("startup-probe | candidate lost | leader unchanged " + FormatLeaderLabel(leader));
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

            bool finalRestore = await RestoreLeaderRouteAsync(output.Winner, cancellationToken);
            if (!finalRestore)
                return BuildUnreliableResult(output, output.Winner, rawScore, "winner-restore-failed");

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

            ProbeCandidateRuntimeData merged = new ProbeCandidateRuntimeData
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
                ReadinessReason = second.ReadinessReason ?? first.ReadinessReason
            };

            return merged;
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
            CancellationToken cancellationToken)
        {
            TimeSpan hardIgnore = timing.HardIgnoreDuration;
            TimeSpan provisional = timing.ProvisionalDuration;
            TimeSpan main = timing.MainMeasureDuration > TimeSpan.Zero
                ? timing.MainMeasureDuration
                : timing.ObserveDuration;

            if (hardIgnore > TimeSpan.Zero)
            {
                Log("startup-probe | hard-ignore start | " + candidate +
                    " | durationMs=" + hardIgnore.TotalMilliseconds.ToString("0"));
                (bool hardCatastrophic, string hardStopReason) =
                    await WaitWithCatastrophicChecksAsync(candidate, hardIgnore, cancellationToken);
                if (hardCatastrophic)
                    return await BuildEarlyStopRuntimeAsync(candidate, hardStopReason ?? "early-stop catastrophic during hard-ignore");
            }

            ProbeCandidateRuntimeData provisionalRuntime = null;
            if (provisional > TimeSpan.Zero)
            {
                provisionalRuntime = await captureProbeAsync(
                    candidate,
                    timing.MeasurementStartGuard,
                    provisional);

                provisionalRuntime.ReachedUsableState = true;
                provisionalRuntime.MeasurementStarted = true;
                provisionalRuntime.Notes = AppendNote(provisionalRuntime.Notes, "measured-provisional");

                if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(provisionalRuntime, out string provisionalStopReason))
                {
                    provisionalRuntime.MeasurementStoppedEarly = true;
                    provisionalRuntime.MeasurementCompleted = false;
                    provisionalRuntime.MeasurementStopReason = provisionalStopReason;
                    provisionalRuntime.Notes = AppendNote(provisionalRuntime.Notes, provisionalStopReason);
                    Log("startup-probe | candidate measured-early-stop | " + candidate + " | reason=" + provisionalStopReason);
                    return provisionalRuntime;
                }
            }

            ProbeCandidateRuntimeData mainRuntime = await captureProbeAsync(
                candidate,
                TimeSpan.Zero,
                main);

            mainRuntime.ReachedUsableState = true;
            mainRuntime.MeasurementStarted = true;
            mainRuntime.Notes = AppendNote(mainRuntime.Notes, "measured-main");

            if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(mainRuntime, out string mainStopReason))
            {
                mainRuntime.MeasurementStoppedEarly = true;
                mainRuntime.MeasurementCompleted = false;
                mainRuntime.MeasurementStopReason = mainStopReason;
                mainRuntime.Notes = AppendNote(mainRuntime.Notes, mainStopReason);
                Log("startup-probe | candidate measured-early-stop | " + candidate + " | reason=" + mainStopReason);
                return mainRuntime;
            }

            ProbeCandidateRuntimeData merged = provisionalRuntime != null
                ? MergePersistenceAwareRuntime(
                    provisionalRuntime,
                    mainRuntime,
                    provisional,
                    main)
                : mainRuntime;

            merged.ReachedUsableState = true;
            merged.MeasurementStarted = true;
            merged.MeasurementCompleted = true;
            merged.MeasurementStoppedEarly = false;
            Log("startup-probe | candidate measured-full | " + candidate);

            return merged;
        }

        private async Task<(bool IsCatastrophic, string Reason)> WaitWithCatastrophicChecksAsync(
            ProbeCandidateDefinition candidate,
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            DateTime endUtc = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < endUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (TryGetCatastrophicReasonFromCurrentWindow(out string reason))
                {
                    Log("startup-probe | catastrophic detected during hard-ignore | " + candidate);
                    return (true, reason);
                }

                await Task.Delay(250, cancellationToken);
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

            if (state != null && state.Confidence >= 0 && state.Confidence < 0.08 && window.StaleRate >= 0.80)
            {
                reason = "early-stop tunnel collapse";
                return true;
            }

            return false;
        }

        private ProbeCandidateRuntimeData MergePersistenceAwareRuntime(
            ProbeCandidateRuntimeData provisional,
            ProbeCandidateRuntimeData main,
            TimeSpan provisionalDuration,
            TimeSpan mainDuration)
        {
            ProbeCandidateRuntimeData merged = MergeMeasuredRuntime(provisional, main, provisionalDuration, mainDuration);

            merged.SpikeDensityPerMinute = PersistenceAwareMetric(
                provisional.SpikeDensityPerMinute,
                main.SpikeDensityPerMinute,
                x => x >= 8.0,
                provisionalDuration,
                mainDuration);
            merged.StaleRate = PersistenceAwareMetric(
                provisional.StaleRate,
                main.StaleRate,
                x => x >= 0.15,
                provisionalDuration,
                mainDuration);
            merged.AvgLossRate = PersistenceAwareMetric(
                provisional.AvgLossRate,
                main.AvgLossRate,
                x => x >= 0.18,
                provisionalDuration,
                mainDuration);
            merged.AvgJitterMs = PersistenceAwareMetric(
                provisional.AvgJitterMs,
                main.AvgJitterMs,
                x => x >= 18.0,
                provisionalDuration,
                mainDuration);
            merged.P95PingMs = PersistenceAwareMetric(
                provisional.P95PingMs,
                main.P95PingMs,
                x => x >= 120.0,
                provisionalDuration,
                mainDuration);
            merged.AvgBadQualityRate = PersistenceAwareMetric(
                provisional.AvgBadQualityRate,
                main.AvgBadQualityRate,
                x => x >= 0.25,
                provisionalDuration,
                mainDuration);

            bool provisionalOnlyBad =
                IsBad(provisional.SpikeDensityPerMinute, x => x >= 8.0) && !IsBad(main.SpikeDensityPerMinute, x => x >= 8.0) ||
                IsBad(provisional.StaleRate, x => x >= 0.15) && !IsBad(main.StaleRate, x => x >= 0.15) ||
                IsBad(provisional.AvgLossRate, x => x >= 0.18) && !IsBad(main.AvgLossRate, x => x >= 0.18) ||
                IsBad(provisional.AvgJitterMs, x => x >= 18.0) && !IsBad(main.AvgJitterMs, x => x >= 18.0) ||
                IsBad(provisional.P95PingMs, x => x >= 120.0) && !IsBad(main.P95PingMs, x => x >= 120.0);

            merged.Notes = AppendNote(merged.Notes, provisionalOnlyBad ? "provisional-softened" : "provisional-persistent-or-clean");

            return merged;
        }

        private static double PersistenceAwareMetric(
            double provisionalValue,
            double mainValue,
            Func<double, bool> isBad,
            TimeSpan provisionalDuration,
            TimeSpan mainDuration)
        {
            if (provisionalValue < 0)
                return mainValue;
            if (mainValue < 0)
                return provisionalValue;

            bool provisionalBad = IsBad(provisionalValue, isBad);
            bool mainBad = IsBad(mainValue, isBad);

            if (provisionalBad && !mainBad)
                return (mainValue * 0.85) + (provisionalValue * 0.15);

            double wp = Math.Max(0.1, provisionalDuration.TotalSeconds);
            double wm = Math.Max(0.1, mainDuration.TotalSeconds);
            return ((provisionalValue * wp) + (mainValue * wm)) / (wp + wm);
        }

        private static bool IsBad(double value, Func<double, bool> isBad)
        {
            return value >= 0 && isBad != null && isBad(value);
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
        public TimeSpan TransitionGrace { get; set; } = TimeSpan.FromSeconds(1.6);
        public TimeSpan HardIgnoreDuration { get; set; } = TimeSpan.FromSeconds(2.5);
        public TimeSpan ProvisionalDuration { get; set; } = TimeSpan.FromSeconds(2.5);
        public TimeSpan MainMeasureDuration { get; set; } = TimeSpan.FromSeconds(10.0);
        public TimeSpan MeasurementStartGuard { get; set; } = TimeSpan.FromSeconds(0.8);
        public TimeSpan ObserveDuration { get; set; } = TimeSpan.FromSeconds(10.0);

        public static StartupProbeTimingConfig Default { get; } = new StartupProbeTimingConfig();
    }
}
