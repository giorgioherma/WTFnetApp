using System;
using System.Collections.Generic;

namespace WTFnetApp
{
    internal sealed class StartupLogger
    {
        private sealed class CandidateSummarySnapshot
        {
            public double WarmupMs { get; set; } = -1;
            public double PostStableMs { get; set; } = -1;
            public bool Extended { get; set; }
            public ProbeCandidateRuntimeData Runtime { get; set; }
            public ProbeScoreResult Score { get; set; }
        }

        private readonly Action<string> log;

        private readonly HashSet<string> printedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> summarizedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CandidateSummarySnapshot> candidateSnapshots = new Dictionary<string, CandidateSummarySnapshot>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> miniSnapshotLastLogUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private const string TableHeader = "time         phase          loss%   avgPing  p95Ping  avgJit  p95Jit  spikeD  conf   note";
        private const string TableDivider = "---------------------------------------------------------------------------------------";

        public StartupLogger(Action<string> log)
        {
            this.log = log;
        }

        public void LogPhaseRow(
            ProbeCandidateDefinition candidate,
            string phase,
            ProbeCandidateRuntimeData runtime,
            string note)
        {
            EnsureCandidateTable(candidate);
            log?.Invoke(BuildTableRow(
                phase,
                FormatPercent((double)(runtime?.AvgLossRate ?? -1)),
                FormatNumber((double)(runtime?.AvgPingMs ?? -1)),
                FormatNumber((double)(runtime?.P95PingMs ?? -1)),
                FormatNumber((double)(runtime?.AvgJitterMs ?? -1)),
                FormatNumber((double)(runtime?.P95JitterMs ?? -1)),
                FormatNumber((double)(runtime?.SpikeDensityPerMinute ?? -1)),
                FormatConf((double)(runtime?.AvgConfidence ?? -1)),
                string.IsNullOrWhiteSpace(note) ? "-" : note));
        }

        public void LogStartupPhase(
            ProbeCandidateDefinition candidate,
            string phase,
            ProbeCandidateRuntimeData runtime,
            string note,
            MiniSpeedifyMiniSnap miniSnap,
            bool logMini = true)
        {
            LogPhaseRow(candidate, phase, runtime, note);

            if (logMini)
                LogMiniSnapshot(candidate, phase, miniSnap);
        }

        public void LogEvent(ProbeCandidateDefinition candidate, string eventText)
        {
            EnsureCandidateTable(candidate);
            log?.Invoke(BuildTableRow(
                "EVENT",
                "-----",
                "-----",
                "-----",
                "-----",
                "-----",
                "-----",
                "-----",
                (eventText ?? "-").ToUpperInvariant()));
        }

        private void EnsureCandidateTable(ProbeCandidateDefinition candidate)
        {
            string key = candidate?.RouteKey ?? candidate?.DisplayName ?? "unknown";
            if (printedCandidates.Contains(key))
                return;

            printedCandidates.Add(key);
            log?.Invoke((candidate?.DisplayName ?? "Unknown").ToUpperInvariant());
            log?.Invoke(TableHeader);
            log?.Invoke(TableDivider);
        }

        public void LogCandidateSummaryFromMeasurement(
            ProbeCandidateDefinition candidate,
            bool stabilizationTimedOut,
            string stabilizationReason,
            DateTime? postStableMeasurementStartUtc,
            DateTime? postStableMeasurementEndUtc,
            bool postStableMiniExtensionHappened,
            double postStableMiniExtraWaitMs,
            double warmupMs,
            ProbeCandidateRuntimeData runtime,
            ProbeScoreResult score,
            string outcome)
        {
            ProbeScoreResult effectiveScore = score ??
                (runtime != null
                    ? ProbeScoring.ScoreCandidate(StartupScoreRuntimeSanitizer.BuildScoreRuntimeWithoutSpeedify(runtime))
                    : null);
            double postStableMs =
                postStableMeasurementStartUtc.HasValue && postStableMeasurementEndUtc.HasValue
                    ? Math.Max(0.0, (postStableMeasurementEndUtc.Value - postStableMeasurementStartUtc.Value).TotalMilliseconds + postStableMiniExtraWaitMs)
                    : 0.0;

            string stabilized = (!stabilizationTimedOut && runtime?.StabilizationReached == true) ? "yes" : "no";
            string stabilizationText = string.IsNullOrWhiteSpace(stabilizationReason) ? "--" : stabilizationReason;

            LogStartupProbeSummary(
                candidate,
                warmupMs,
                stabilized,
                stabilizationText,
                postStableMs,
                postStableMiniExtensionHappened,
                runtime,
                effectiveScore,
                outcome);
        }

        public void LogStartupProbeSummary(
            ProbeCandidateDefinition candidate,
            double warmupMs,
            string stabilized,
            string stabilizationReason,
            double postStableMs,
            bool extended,
            ProbeCandidateRuntimeData runtime,
            ProbeScoreResult score,
            string outcome)
        {
            string key = candidate?.RouteKey ?? candidate?.DisplayName ?? "unknown";
            if (summarizedCandidates.Contains(key))
                return;
            summarizedCandidates.Add(key);

            if (candidateSnapshots.TryGetValue(key, out CandidateSummarySnapshot snapshot))
            {
                if (warmupMs < 0)
                    warmupMs = snapshot.WarmupMs;
                if (postStableMs < 0)
                    postStableMs = snapshot.PostStableMs;
                if (runtime == null)
                    runtime = snapshot.Runtime;
                if (score == null)
                    score = snapshot.Score;
                if (!extended)
                    extended = snapshot.Extended;
            }

            log?.Invoke("[startup-probe-summary] " + (candidate?.DisplayName ?? "Unknown") +
                " | warmupMs=" + (warmupMs >= 0 ? warmupMs.ToString("0") : "--") +
                " | stabilized=" + (string.IsNullOrWhiteSpace(stabilized) ? "--" : stabilized) +
                " | stabilizationReason=" + (string.IsNullOrWhiteSpace(stabilizationReason) ? "--" : stabilizationReason) +
                " | postStableMs=" + (postStableMs >= 0 ? postStableMs.ToString("0") : "--") +
                " | extended=" + (extended ? "yes" : "no") +
                " | finalLoss=" + FormatPercent(runtime?.AvgLossRate ?? -1) +
                " | finalAvg=" + FormatNumber(runtime?.AvgPingMs ?? -1) +
                " | finalJit=" + FormatNumber(runtime?.AvgJitterMs ?? -1) +
                " | finalSpk=" + FormatNumber(runtime?.SpikeDensityPerMinute ?? -1) +
                " | composite=" + FormatNumber(score?.StartupSelectionScore ?? -1) +
                " | overall=" + FormatNumber(score?.OverallStabilityScore ?? -1) +
                " | warning=" + (score != null ? ProbeScoring.GetWarningLabel(score) : "--") +
                " | measuredFriendlyName=" + (runtime?.MeasuredServerFriendlyName ?? "-") +
                " | measuredTag=" + (runtime?.MeasuredServerTag ?? "-") +
                " | measuredPublicIp=" + (runtime?.MeasuredServerPublicIp ?? "-") +
                " | measuredRouteTarget=" + (runtime?.MeasuredRouteTarget ?? "-") +
                " | restoredTarget=" + (runtime?.RestoredServerTarget ?? "-") +
                " | outcome=" + (string.IsNullOrWhiteSpace(outcome) ? "kept-not-winner" : outcome));
        }

        public void RegisterCandidateEvaluation(
            ProbeCandidateDefinition candidate,
            double warmupMs,
            double postStableMs,
            bool extended,
            ProbeCandidateRuntimeData runtime,
            ProbeScoreResult score)
        {
            string key = candidate?.RouteKey ?? candidate?.DisplayName ?? "unknown";
            candidateSnapshots.TryGetValue(key, out CandidateSummarySnapshot existing);
            CandidateSummarySnapshot snapshot = existing ?? new CandidateSummarySnapshot();

            if (warmupMs >= 0)
                snapshot.WarmupMs = warmupMs;
            if (postStableMs >= 0)
                snapshot.PostStableMs = postStableMs;
            if (extended)
                snapshot.Extended = true;
            if (runtime != null)
            {
                snapshot.Runtime = snapshot.Runtime == null
                    ? runtime
                    : StartupProbeRuntimeMerge.MergeMeasuredRuntime(
                        snapshot.Runtime,
                        runtime,
                        TimeSpan.Zero,
                        TimeSpan.Zero,
                        AppendNote);
            }
            if (score != null)
                snapshot.Score = score;

            candidateSnapshots[key] = snapshot;
        }

        private static string AppendNote(string first, string second)
        {
            if (string.IsNullOrWhiteSpace(first))
                return second;
            if (string.IsNullOrWhiteSpace(second))
                return first;
            return first + " | " + second;
        }

        public void LogFinalDecisionBlock(
            ProbeScoreResult winner,
            ProbeScoreResult runnerUp,
            ProbeScoreResult third,
            ProbeWinnerConfidenceResult confidence,
            string winnerReason,
            string decisionPolicy,
            bool restoreSucceeded,
            List<ProbeScoreResult> shortlist)
        {
            log?.Invoke("FINAL STARTUP DECISION");
            log?.Invoke("winner   : " + (winner?.Candidate?.DisplayName ?? "none"));
            log?.Invoke("runner-up: " + (runnerUp?.Candidate?.DisplayName ?? "none"));
            log?.Invoke("3rd      : " + (third?.Candidate?.DisplayName ?? "none"));
            log?.Invoke("winnerReason    : " + (winnerReason ?? "none"));
            log?.Invoke("runnerUpReason  : " + (runnerUp != null ? ProbeScoring.BuildRouteExplanation(runnerUp) : "none"));
            log?.Invoke("decisionPolicy  : " + (decisionPolicy ?? "none"));
            log?.Invoke("winnerConfidence: " +
                (confidence != null
                    ? confidence.Kind + " (" + confidence.ConfidenceScore.ToString("0.0") + ")"
                    : "none"));
            log?.Invoke("restore         : " + (restoreSucceeded ? "succeeded" : "failed"));
            log?.Invoke("shortlist:");

            if (shortlist == null || shortlist.Count == 0)
                return;

            for (int i = 0; i < shortlist.Count && i < 3; i++)
            {
                ProbeScoreResult item = shortlist[i];
                log?.Invoke("  " + (i + 1).ToString() + ". " + (item?.Candidate?.DisplayName ?? "unknown") +
                    " | composite=" + FormatNumber(item?.StartupSelectionScore ?? -1) +
                    " | overall=" + FormatNumber(item?.OverallStabilityScore ?? -1) +
                    " | warning=" + (item != null ? ProbeScoring.GetWarningLabel(item) : "--") +
                    " | badge=" + (item != null ? ProbeScoring.GetBadgeLabel(item) : "--") +
                    " | explain=" + (item != null ? ProbeScoring.BuildRouteExplanation(item) : "--"));
            }
        }

        

        private static string FormatNumber(double value)
        {
            return value >= 0 ? value.ToString("0.0") : "--";
        }

        private static string FormatPercent(double value)
        {
            return value >= 0 ? (value * 100.0).ToString("0.0") + "%" : "--";
        }

        private static string FormatConf(double value)
        {
            return value >= 0 ? value.ToString("0.00") : "--";
        }

        private void LogMiniSnapshot(
            ProbeCandidateDefinition candidate,
            string phase,
            MiniSpeedifyMiniSnap miniSnap)
        {
            if (!IsMeaningfulMiniSnapshot(miniSnap))
                return;

            string key = candidate?.RouteKey ?? candidate?.DisplayName ?? "unknown";
            if (miniSnapshotLastLogUtc.TryGetValue(key, out DateTime lastUtc) &&
                miniSnap.TimeUtc != DateTime.MinValue &&
                miniSnap.TimeUtc <= lastUtc)
            {
                return;
            }

            if (miniSnap.TimeUtc != DateTime.MinValue)
                miniSnapshotLastLogUtc[key] = miniSnap.TimeUtc;

            bool tunnelConnected = miniSnap.ActiveConnectionCount > 0 ||
                                   miniSnap.ReportedLatencyMs > 0 ||
                                   miniSnap.AggregateLatencyMs > 0 ||
                                   miniSnap.TunnelAggregatePresent;

            log?.Invoke(
                "startup-mini | phase=" + (phase ?? "--") +
                " | target=" + (candidate?.DisplayName ?? candidate?.RouteKey ?? "unknown") +
                " | ts=" + miniSnap.TimeUtc.ToString("HH:mm:ss.fff") +
                " | connected=" + (tunnelConnected ? "yes" : "no") +
                " | active=" + Math.Max(0, miniSnap.ActiveConnectionCount) +
                " | backup=" + Math.Max(0, miniSnap.BackupConnectionCount) +
                " | tunnelLat=" + FormatNumber(miniSnap.ReportedLatencyMs) +
                " | aggLat=" + FormatNumber(miniSnap.AggregateLatencyMs) +
                " | aggJit=" + FormatNumber(miniSnap.AggregateJitterMs) +
                " | congested=" + (miniSnap.AggregateCongested ? "yes" : "no"));
        }

        private static bool IsMeaningfulMiniSnapshot(MiniSpeedifyMiniSnap miniSnap)
        {
            if (miniSnap == null || !miniSnap.IsFresh)
                return false;

            if (miniSnap.TimeUtc == DateTime.MinValue)
                return false;

            return miniSnap.ActiveConnectionCount > 0 ||
                   miniSnap.ReportedLatencyMs > 0 ||
                   miniSnap.AggregateLatencyMs > 0 ||
                   miniSnap.TunnelAggregatePresent;
        }

        private static string BuildTableRow(
            string phase,
            string loss,
            string avgPing,
            string p95Ping,
            string avgJitter,
            string p95Jitter,
            string spikeDensity,
            string confidence,
            string note)
        {
            return string.Format(
                "{0,-12} {1,-14} {2,-7} {3,-8} {4,-8} {5,-7} {6,-7} {7,-7} {8,-6} {9}",
                DateTime.Now.ToString("HH:mm:ss.fff"),
                phase ?? "--",
                loss ?? "--",
                avgPing ?? "--",
                p95Ping ?? "--",
                avgJitter ?? "--",
                p95Jitter ?? "--",
                spikeDensity ?? "--",
                confidence ?? "--",
                note ?? "-");
        }

    }
}
