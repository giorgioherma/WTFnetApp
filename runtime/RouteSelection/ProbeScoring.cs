using System;
using System.Collections.Generic;
using System.Linq;

namespace WTFnetApp
{
    public enum ProbeCandidateKind
    {
        Raw = 0,
        Speedify = 1
    }

    public enum ProbeWarningLevel
    {
        None = 0,
        Borderline = 1,
        Risky = 2
    }

    public enum ProbeBadgeKind
    {
        None = 0,
        Smoothest = 1
    }

    public enum ProbeWinnerConfidenceKind
    {
        NoClearWinner = 0,
        Tentative = 1,
        HighConfidence = 2
    }

    public enum ProbeReadinessKind
    {
        NotReady = 0,
        GoodStable = 1,
        BadStable = 2,
        TimeoutFallback = 3
    }

    public sealed class ProbeCandidateDefinition
    {
        public string RouteKey { get; set; }
        public string DisplayName { get; set; }
        public string ConnectTarget { get; set; }
        public ProbeCandidateKind Kind { get; set; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(DisplayName)
                ? (RouteKey ?? "unknown")
                : DisplayName;
        }
    }

    public sealed class ProbeReadinessResult
    {
        public ProbeReadinessKind Kind { get; set; }
        public string Reason { get; set; }

        public bool IsReady
        {
            get
            {
                return Kind == ProbeReadinessKind.GoodStable ||
                       Kind == ProbeReadinessKind.BadStable ||
                       Kind == ProbeReadinessKind.TimeoutFallback;
            }
        }
    }

    public sealed class ProbeCandidateRuntimeData
    {
        public ProbeCandidateDefinition Candidate { get; set; }

        public string TimeBucketKey { get; set; }

        public double AvgPingMs { get; set; } = -1;
        public double MaxPingMs { get; set; } = -1;
        public double P95PingMs { get; set; } = -1;

        public double AvgJitterMs { get; set; } = -1;
        public double MinJitterMs { get; set; } = -1;
        public double MaxJitterMs { get; set; } = -1;
        public double P95JitterMs { get; set; } = -1;

        public double AvgLossRate { get; set; } = -1;
        public double StartupAdjustedLossRate { get; set; } = -1;
        public double AvgBadQualityRate { get; set; } = -1;
        public double AvgConfidence { get; set; } = -1;

        public double SpikeDensityPerMinute { get; set; } = -1;
        public double StaleRate { get; set; } = -1;

        public bool TunnelConnected { get; set; }
        public bool AggregateCongested { get; set; }
        public int ActiveConnectionCount { get; set; }
        public int BackupConnectionCount { get; set; }
        public double SpeedifyAggregateLatencyMs { get; set; } = -1;
        public double SpeedifyAggregateJitterMs { get; set; } = -1;
        public double SpeedifyReportedLatencyMs { get; set; } = -1;
        public int SpeedifyFreshSampleCount { get; set; }
        public double SpeedifyExpectedSampleCount { get; set; } = -1;
        public double SpeedifyAggregateLatencyStdDevMs { get; set; } = -1;
        public DateTime FirstFreshPostSwitchStatsUtc { get; set; } = DateTime.MinValue;
        public double FirstFreshPostSwitchAggLatencyMs { get; set; } = -1;
        public double FirstFreshPostSwitchAggJitterMs { get; set; } = -1;
        public double FirstFreshPostSwitchReportedLatencyMs { get; set; } = -1;
        public int FirstFreshPostSwitchActiveConnectionCount { get; set; } = -1;
        public bool FirstFreshPostSwitchAggregateCongested { get; set; }

        public bool IsValid { get; set; } = true;
        public string Notes { get; set; }

        public bool StabilizationReached { get; set; }
        public bool StabilizationTimedOut { get; set; }
        public string StabilizationReason { get; set; }

        public ProbeReadinessKind ReadinessKind { get; set; } = ProbeReadinessKind.NotReady;
        public string ReadinessReason { get; set; }

        public bool ReachedUsableState { get; set; }
        public bool MeasurementStarted { get; set; }
        public bool MeasurementCompleted { get; set; }
        public bool MeasurementStoppedEarly { get; set; }
        public string MeasurementStopReason { get; set; }
        public string DiscardReason { get; set; }
        public bool StartupSpeedifyTrusted { get; set; } = false;
    }

    public sealed class ProbeScoreResult
    {
        public ProbeCandidateDefinition Candidate { get; set; }
        public ProbeCandidateRuntimeData Runtime { get; set; }

        public double OverallStabilityScore { get; set; } = double.MaxValue;
        public double JitterStabilityScore { get; set; } = double.MaxValue;
        public double StartupSelectionScore { get; set; } = double.MaxValue;

        public ProbeWarningLevel WarningLevel { get; set; } = ProbeWarningLevel.None;
        public ProbeBadgeKind Badge { get; set; } = ProbeBadgeKind.None;

        public bool IsDiscardedEarly { get; set; }
        public bool IsShortlisted { get; set; }
        public bool IsWinner { get; set; }

        public string SummaryText { get; set; }
        public string WarningText { get; set; }
        public string BadgeText { get; set; }

        public double JitterRangeMs { get; set; } = -1;

        public double LatencyContribution { get; set; }
        public double JitterContribution { get; set; }
        public double SpikeContribution { get; set; }
        public double LossStaleBadContribution { get; set; }
        public double SpeedifyGlobalAdjustment { get; set; }
        public double SpeedifyPositiveInfluenceCapApplied { get; set; }
        public double SpeedifyLatencyModifier { get; set; } = 1.0;
        public double SpeedifyJitterModifier { get; set; } = 1.0;
        public double SpeedifySpikeModifier { get; set; } = 1.0;
        public double SpeedifyLossStaleModifier { get; set; } = 1.0;
        public string SpeedifyHealthLabel { get; set; } = "n/a";
        public double ConfidenceModifier { get; set; } = 1.0;
    }

    public sealed class ProbeWinnerConfidenceResult
    {
        public ProbeWinnerConfidenceKind Kind { get; set; } = ProbeWinnerConfidenceKind.NoClearWinner;
        public double ConfidenceScore { get; set; }
        public string Reason { get; set; }
        public double GapToRunnerUpScore { get; set; }
    }

    public sealed class ProbeDiscardDecision
    {
        public bool ShouldDiscard { get; set; }
        public string ReasonCategory { get; set; }
    }

    public sealed class ProbeDecisionDiagnostics
    {
        public bool IsLowConfidence { get; set; }
        public bool IsCloseCall { get; set; }
        public bool HasHighVariance { get; set; }
        public bool HasCrossSignalMismatch { get; set; }
        public bool HasLowSampleCoverage { get; set; }
        public string Summary { get; set; }
    }

    public sealed class ProbeDecisionStabilityState
    {
        public string LastWinnerRouteKey { get; set; }
        public double LastWinnerScore { get; set; }
        public DateTime LastSwitchUtc { get; set; }
        public int ConsecutiveSameWinnerCount { get; set; }
        public double LastConfidenceModifier { get; set; }
    }

    public sealed class ProbeDecisionPolicyResult
    {
        public bool ShouldDeferDecision { get; set; }
        public bool ShouldReprobe { get; set; }
        public bool ShouldDelaySwitch { get; set; }
        public int DelayMs { get; set; }
        public bool ShouldStickToPrevious { get; set; }
        public bool IsStabilityHold { get; set; }
        public string PolicyReason { get; set; }
        public string DecisionType { get; set; }
        public bool HoldFallbackToCurrent { get; set; }
    }

    public static class ProbeScoring
    {
        private enum SpeedifyHealthBucket
        {
            NotApplicable = 0,
            NoTelemetry = 1,
            Unhealthy = 2,
            HealthySingleLink = 3,
            HealthyBonded = 4
        }

        private const double BorderlineAvgJitterMs = 12.0;
        private const double RiskyAvgJitterMs = 18.0;

        private const double BorderlineJitterRangeMs = 10.0;
        private const double RiskyJitterRangeMs = 18.0;

        private const double BorderlineP95PingMs = 85.0;
        private const double RiskyP95PingMs = 120.0;

        private const double BorderlineSpikeDensity = 4.0;
        private const double RiskySpikeDensity = 8.0;

        private const double BorderlineLossRate = 0.08;
        private const double RiskyLossRate = 0.18;

        private const double BorderlineStaleRate = 0.05;
        private const double RiskyStaleRate = 0.15;

        private const double BorderlineBadQualityRate = 0.12;
        private const double RiskyBadQualityRate = 0.25;

        private const double SpeedifyBorderlineAvgJitterMs = 14.5;
        private const double SpeedifyRiskyAvgJitterMs = 22.0;
        private const double SpeedifyBorderlineJitterRangeMs = 13.0;
        private const double SpeedifyRiskyJitterRangeMs = 22.0;
        private const double SpeedifyBorderlineP95PingMs = 105.0;
        private const double SpeedifyRiskyP95PingMs = 150.0;
        private const double SpeedifyBorderlineSpikeDensity = 5.0;
        private const double SpeedifyRiskySpikeDensity = 9.5;
        private const double SpeedifyBorderlineLossRate = 0.08;
        private const double SpeedifyRiskyLossRate = 0.12;
        private const double SpeedifyBorderlineStaleRate = 0.07;
        private const double SpeedifyRiskyStaleRate = 0.18;
        private const double SpeedifyBorderlineBadRate = 0.16;
        private const double SpeedifyRiskyBadRate = 0.30;

        private const double MaxHealthySinglePositiveInfluence = 0.06;
        private const double MaxHealthyBondedPositiveInfluence = 0.10;

        public static string GetCurrentTimeBucketKey(DateTime localTime)
        {
            int hour = localTime.Hour;

            if (hour >= 0 && hour < 6)
                return "night";

            if (hour >= 6 && hour < 12)
                return "morning";

            if (hour >= 12 && hour < 18)
                return "day";

            return "evening";
        }

        public static ProbeScoreResult ScoreCandidate(ProbeCandidateRuntimeData runtime)
        {
            ProbeScoreResult result = new ProbeScoreResult
            {
                Candidate = runtime?.Candidate,
                Runtime = runtime
            };

            if (!TryValidateMeasuredRuntimeForScoring(runtime, out string invalidReason))
            {
                result.WarningLevel = ProbeWarningLevel.Risky;
                result.WarningText = "unusable";
                result.SummaryText = invalidReason;
                return result;
            }

            double avgJitter = Safe(runtime.AvgJitterMs);
            double jitterRange = Safe(ComputeJitterRange(runtime));
            double p95Ping = Safe(runtime.P95PingMs);
            double spikeDensity = Safe(runtime.SpikeDensityPerMinute);
            double lossRate = EffectiveLossRate(GetLossRateForScoring(runtime));
            double staleRate = Safe(runtime.StaleRate);
            double badRate = Safe(runtime.AvgBadQualityRate);
            double avgPing = Safe(runtime.AvgPingMs);
            SpeedifyHealthBucket healthBucket = ClassifySpeedifyHealth(runtime);

            double latencyModifier = GetLatencyPenaltyModifier(runtime, healthBucket);
            double jitterModifier = GetJitterPenaltyModifier(runtime, healthBucket);
            double spikeModifier = GetSpikePenaltyModifier(runtime, healthBucket);
            double lossStaleModifier = GetLossStalePenaltyModifier(runtime, healthBucket);
            double speedifyAdjustment = SpeedifyPenalty(runtime, healthBucket);

            bool speedifyValid =
                runtime.Candidate != null &&
                runtime.Candidate.Kind == ProbeCandidateKind.Speedify &&
                runtime.StartupSpeedifyTrusted &&
                runtime.TunnelConnected &&
                runtime.ActiveConnectionCount > 0 &&
                ChoosePositive(runtime.SpeedifyAggregateLatencyMs, runtime.SpeedifyReportedLatencyMs) > 0;

            if (!speedifyValid)
            {
                healthBucket = SpeedifyHealthBucket.NoTelemetry;
                latencyModifier = 1.0;
                jitterModifier = 1.0;
                spikeModifier = 1.0;
                lossStaleModifier = 1.0;
                speedifyAdjustment = 0.0;
            }

            result.JitterRangeMs = jitterRange;
            result.SpeedifyLatencyModifier = latencyModifier;
            result.SpeedifyJitterModifier = jitterModifier;
            result.SpeedifySpikeModifier = spikeModifier;
            result.SpeedifyLossStaleModifier = lossStaleModifier;
            result.SpeedifyHealthLabel = GetSpeedifyHealthLabel(healthBucket);

            result.JitterStabilityScore =
                avgJitter +
                (jitterRange * 0.65) +
                (spikeDensity * 0.45) +
                (badRate * 12.0);

            double latencyContribution = ((p95Ping * 0.10) + (avgPing * 0.05)) * latencyModifier;
            double jitterContribution = ((avgJitter * 1.75) + (jitterRange * 1.05)) * jitterModifier;
            double spikeContribution = (spikeDensity * 2.95) * spikeModifier;
            double lossStaleBadContribution =
                ((badRate * 112.0) + (lossRate * 52.0) + (staleRate * 108.0)) * lossStaleModifier;

            if (lossRate >= 0.05)
            {
                lossStaleBadContribution += 4.0 + ((lossRate - 0.05) * 60.0);
            }

            double speedifyAdjustedScore =
                latencyContribution +
                jitterContribution +
                spikeContribution +
                lossStaleBadContribution +
                speedifyAdjustment;

            double fastPingDominantScore =
                ((p95Ping * 0.10) + (avgPing * 0.05)) +
                ((avgJitter * 1.75) + (jitterRange * 1.05)) +
                (spikeDensity * 2.95) +
                ((badRate * 112.0) + (lossRate * 52.0) + (staleRate * 108.0));

            double cappedScore = ApplySpeedifyPositiveInfluenceCap(
                speedifyAdjustedScore,
                fastPingDominantScore,
                healthBucket,
                out double capApplied);

            result.LatencyContribution = latencyContribution;
            result.JitterContribution = jitterContribution;
            result.SpikeContribution = spikeContribution;
            result.LossStaleBadContribution = lossStaleBadContribution;
            result.SpeedifyGlobalAdjustment = speedifyAdjustment;
            result.SpeedifyPositiveInfluenceCapApplied = capApplied;
            result.OverallStabilityScore = cappedScore;

            Logger.Log("score",
                "compute | " + (runtime.Candidate?.ToString() ?? "unknown") +
                " | lat=" + F(result.LatencyContribution) +
                " | jit=" + F(result.JitterContribution) +
                " | spk=" + F(result.SpikeContribution) +
                " | lsb=" + F(result.LossStaleBadContribution) +
                " | adj=" + F(result.SpeedifyGlobalAdjustment) +
                " | cap=" + F(result.SpeedifyPositiveInfluenceCapApplied));

            if (runtime.MeasurementStoppedEarly)
                result.OverallStabilityScore += ComputeEarlyStopPenalty(runtime.MeasurementStopReason);

            double confidenceModifier = ComputeConfidenceModifier(runtime);
            result.ConfidenceModifier = confidenceModifier;

            Logger.Log("score-final",
                "finalize | " + (runtime.Candidate?.ToString() ?? "unknown") +
                " | baseQuality=" + F(cappedScore) +
                " | confM=" + confidenceModifier.ToString("0.00") +
                " | finalQuality=" + F(result.OverallStabilityScore));

            if (confidenceModifier < 0.70)
            {
                Logger.Log("decision-warning",
                    "low-confidence | " + (runtime.Candidate?.ToString() ?? "unknown") +
                    " | confM=" + confidenceModifier.ToString("0.00"));
            }

            result.StartupSelectionScore = result.OverallStabilityScore;
            result.WarningLevel = ClassifyWarning(runtime);

            if (result.WarningLevel == ProbeWarningLevel.Borderline)
                result.WarningText = "Borderline";

            if (result.WarningLevel == ProbeWarningLevel.Risky)
                result.WarningText = "Risky";

            result.SummaryText = BuildSummary(runtime, result);

            return result;
        }

        public static double ComputeStartupSelectionScore(
            ProbeScoreResult candidate,
            LearnedProbeStateProfile learned)
        {
            if (candidate == null || candidate.Runtime == null)
                return double.MaxValue;

            ProbeCandidateRuntimeData runtime = candidate.Runtime;

            double stabilityCore = candidate.OverallStabilityScore * 0.78;
            double spikePenalty = Safe(runtime.SpikeDensityPerMinute) * 1.20;

            double persistenceFactor = ComputeStartupPersistenceFactor(runtime);
            double effectiveLoss = Math.Min(1.0, EffectiveLossRate(GetLossRateForScoring(runtime)) * persistenceFactor);
            double effectiveBadQuality = Math.Min(1.0, Safe(runtime.AvgBadQualityRate) * persistenceFactor);

            double lossPenalty = effectiveLoss * 100.0 * 0.18;
            double badQualityPenalty = effectiveBadQuality * 100.0 * 0.10;

            double avgJitter = Safe(runtime.AvgJitterMs);
            double jitterRange = Safe(ComputeJitterRange(runtime));
            double jitterStabilityPenalty = (avgJitter * 0.85) + (jitterRange * 0.70);

            double p95Ping = Safe(runtime.P95PingMs);
            double avgPing = Safe(runtime.AvgPingMs);
            double tailPenalty = (p95Ping * 0.05) + (Math.Max(0.0, p95Ping - avgPing) * 0.04);
            double smallLatencyPenalty = avgPing * 0.012;

            double warningAdjustment =
                candidate.WarningLevel == ProbeWarningLevel.Risky ? 14.0 :
                candidate.WarningLevel == ProbeWarningLevel.Borderline ? 5.0 :
                0.0;

            double learningAdjustment = ComputeLearningAdjustment(runtime, learned);

            return stabilityCore +
                   jitterStabilityPenalty +
                   tailPenalty +
                   spikePenalty +
                   lossPenalty +
                   badQualityPenalty +
                   smallLatencyPenalty +
                   warningAdjustment +
                   learningAdjustment;
        }

        public static ProbeDecisionDiagnostics AnalyzeDecision(
            ProbeScoreResult winner,
            ProbeScoreResult runnerUp)
        {
            ProbeDecisionDiagnostics diagnostics = new ProbeDecisionDiagnostics();

            if (winner == null || winner.Runtime == null)
            {
                diagnostics.Summary = "clean";
                return diagnostics;
            }

            ProbeCandidateRuntimeData runtime = winner.Runtime;

            diagnostics.IsLowConfidence = winner.ConfidenceModifier < 0.70;

            if (runnerUp != null)
            {
                double gap = Math.Abs(winner.StartupSelectionScore - runnerUp.StartupSelectionScore);
                diagnostics.IsCloseCall = gap <= 5.0;
            }

            if (runtime.SpeedifyAggregateLatencyStdDevMs > 0 &&
                runtime.SpeedifyAggregateLatencyMs > 0)
            {
                double relStd = runtime.SpeedifyAggregateLatencyStdDevMs / runtime.SpeedifyAggregateLatencyMs;
                diagnostics.HasHighVariance = relStd > 0.35;
            }

            if (runtime.SpeedifyAggregateLatencyMs > 0 && runtime.AvgPingMs > 0)
            {
                double diff = Math.Abs(runtime.SpeedifyAggregateLatencyMs - runtime.AvgPingMs) / runtime.AvgPingMs;
                diagnostics.HasCrossSignalMismatch = diff > 0.40;
            }

            if (runtime.SpeedifyExpectedSampleCount > 0)
            {
                double coverage = runtime.SpeedifyFreshSampleCount / runtime.SpeedifyExpectedSampleCount;
                diagnostics.HasLowSampleCoverage = coverage < 0.6;
            }

            List<string> flags = new List<string>();
            if (diagnostics.IsLowConfidence) flags.Add("low-confidence");
            if (diagnostics.IsCloseCall) flags.Add("close-call");
            if (diagnostics.HasHighVariance) flags.Add("high-variance");
            if (diagnostics.HasCrossSignalMismatch) flags.Add("latency-mismatch");
            if (diagnostics.HasLowSampleCoverage) flags.Add("low-samples");

            diagnostics.Summary = flags.Count > 0
                ? string.Join(", ", flags)
                : "clean";

            return diagnostics;
        }

        public static ProbeDecisionPolicyResult ApplyDecisionPolicy(
            ProbeScoreResult winner,
            ProbeScoreResult runnerUp,
            ProbeDecisionDiagnostics diagnostics)
        {
            ProbeDecisionPolicyResult policy = new ProbeDecisionPolicyResult();
            if (winner == null)
            {
                policy.ShouldDeferDecision = true;
                policy.ShouldReprobe = true;
                policy.PolicyReason = "winner-missing";
                return policy;
            }

            double confidence = winner.ConfidenceModifier;
            bool lowConfidence = diagnostics?.IsLowConfidence == true || confidence < 0.70;
            bool closeCall = diagnostics?.IsCloseCall == true;

            if (lowConfidence && closeCall)
            {
                policy.ShouldDelaySwitch = true;
                policy.ShouldReprobe = true;
                policy.DelayMs = 1200;
                policy.PolicyReason = AppendReason(policy.PolicyReason, "low-confidence-close-call");
                return policy;
            }

            if (lowConfidence)
            {
                policy.ShouldReprobe = true;
                policy.PolicyReason = AppendReason(policy.PolicyReason, "low-confidence");
                return policy;
            }

            if (closeCall)
            {
                policy.ShouldDelaySwitch = true;
                policy.DelayMs = 900;
                policy.PolicyReason = AppendReason(policy.PolicyReason, "close-call");
                return policy;
            }

            policy.PolicyReason = AppendReason(policy.PolicyReason, "clean-decision");
            return policy;
        }

        public static void ApplyStabilityLayer(
            ProbeScoreResult winner,
            ProbeScoreResult runnerUp,
            ProbeDecisionDiagnostics diagnostics,
            ProbeDecisionStabilityState state,
            ProbeDecisionPolicyResult policy)
        {
            if (winner == null || state == null || policy == null)
                return;

            string currentKey = winner.Candidate?.RouteKey;
            string lastKey = state.LastWinnerRouteKey;

            if (string.IsNullOrWhiteSpace(lastKey))
                return;

            bool isSameRoute = string.Equals(currentKey, lastKey, StringComparison.OrdinalIgnoreCase);

            if (isSameRoute)
            {
                policy.PolicyReason = AppendReason(policy.PolicyReason, "stable-current-route");
                policy.DecisionType = "no-switch-same-route";
                return;
            }

            double currentScore = winner.StartupSelectionScore;
            double lastScore = state.LastWinnerScore;
            double improvement = lastScore - currentScore;
            double confidence = winner.ConfidenceModifier;

            bool closeCall = diagnostics?.IsCloseCall == true;

            if (improvement >= 10.0)
            {
                policy.PolicyReason = AppendReason(policy.PolicyReason, "strong-switch-emergency-override");
                policy.DecisionType = "emergency-override-switch";
                return;
            }

            double secondsSinceLastSwitch = state.LastSwitchUtc == DateTime.MinValue
                ? double.MaxValue
                : (DateTime.UtcNow - state.LastSwitchUtc).TotalSeconds;

            if (secondsSinceLastSwitch < 10.0)
            {
                ApplyHold(
                    policy,
                    "cooldown-hold",
                    extendWindow: false,
                    decisionType: "cooldown-hold");
                return;
            }

            if (improvement >= 8.0)
            {
                policy.PolicyReason = AppendReason(policy.PolicyReason, "strong-switch");
                policy.DecisionType = "strong-switch";
                return;
            }

            if (confidence < 0.70)
            {
                ApplyHold(
                    policy,
                    "low-confidence-hold",
                    extendWindow: true,
                    decisionType: "low-confidence-hold");
                return;
            }

            if (closeCall && confidence < 0.80)
            {
                ApplyHold(
                    policy,
                    "close-call-hold",
                    extendWindow: true,
                    decisionType: "close-call-hold");
                return;
            }

            if (improvement >= 4.0 && confidence >= 0.70)
            {
                policy.PolicyReason = AppendReason(policy.PolicyReason, "safe-switch");
                policy.DecisionType = "safe-switch";
                return;
            }

            if (improvement < 4.0)
            {
                ApplyHold(
                    policy,
                    "weak-win-hold",
                    extendWindow: false,
                    decisionType: "weak-win-hold");
                return;
            }
        }

        private static double ComputeStartupPersistenceFactor(ProbeCandidateRuntimeData runtime)
        {
            if (runtime == null)
                return 1.0;

            string notes = runtime.Notes ?? string.Empty;

            bool measuredExtension = NotesContain(notes, "measured-extension");
            bool confidenceExtension = NotesContain(notes, "startup-confidence-extension");

            double factor = 1.0;

            if (measuredExtension || confidenceExtension)
                factor = 1.03;

            return factor;
        }

        private static bool NotesContain(string notes, string token)
        {
            if (string.IsNullOrEmpty(notes) || string.IsNullOrEmpty(token))
                return false;

            return notes.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void AssignSmoothestBadge(IList<ProbeScoreResult> results)
        {
            if (results == null || results.Count == 0)
                return;

            ProbeScoreResult best = null;

            foreach (ProbeScoreResult result in results)
            {
                if (result == null)
                    continue;

                if (result.WarningLevel == ProbeWarningLevel.Risky)
                    continue;

                if (result.JitterStabilityScore < 0 || double.IsNaN(result.JitterStabilityScore))
                    continue;

                if (best == null || result.JitterStabilityScore < best.JitterStabilityScore)
                    best = result;
            }

            if (best == null)
                return;

            best.Badge = ProbeBadgeKind.Smoothest;
            best.BadgeText = "Smoothest";
        }

        public static ProbeDiscardDecision EvaluateDiscardDecision(
            ProbeScoreResult candidate,
            ProbeScoreResult baseline,
            ProbeScoreResult currentLeader)
        {
            ProbeDiscardDecision decision = new ProbeDiscardDecision
            {
                ShouldDiscard = false,
                ReasonCategory = "kept"
            };

            if (candidate == null)
            {
                decision.ShouldDiscard = true;
                decision.ReasonCategory = "unusable probe";
                return decision;
            }

            if (TryGetFailFastDiscardReason(candidate, out string failFastReason))
            {
                decision.ShouldDiscard = true;
                decision.ReasonCategory = failFastReason;
                return decision;
            }

            if (baseline != null &&
                baseline.WarningLevel != ProbeWarningLevel.Risky &&
                candidate.StartupSelectionScore > baseline.StartupSelectionScore + 28.0)
            {
                decision.ShouldDiscard = true;
                decision.ReasonCategory = "worse than raw baseline";
                return decision;
            }

            if (currentLeader != null &&
                candidate.StartupSelectionScore > currentLeader.StartupSelectionScore + 30.0)
            {
                decision.ShouldDiscard = true;
                decision.ReasonCategory = "worse than current leader";
                return decision;
            }

            return decision;
        }

        private static bool TryGetFailFastDiscardReason(ProbeScoreResult candidate, out string reason)
        {
            reason = null;

            if (candidate == null)
            {
                reason = "unusable probe";
                return true;
            }

            if (!TryValidateMeasuredRuntimeForScoring(candidate.Runtime, out string invalidReason))
            {
                reason = invalidReason ?? "unusable probe";
                return true;
            }

            return TryGetMeasuredCatastrophicStopReason(candidate.Runtime, out reason);
        }

        public static bool TryGetMeasuredCatastrophicStopReason(ProbeCandidateRuntimeData runtime, out string reason)
        {
            reason = null;

            if (runtime == null)
            {
                reason = "unusable probe";
                return true;
            }

            double spikeDensity = Safe(runtime.SpikeDensityPerMinute);
            double effectiveLoss = EffectiveLossRate(runtime.AvgLossRate);
            double staleRate = Safe(runtime.StaleRate);
            double badRate = Safe(runtime.AvgBadQualityRate);
            double confidence = Safe(runtime.AvgConfidence);
            double avgPing = Safe(runtime.AvgPingMs);
            double p95Ping = Safe(runtime.P95PingMs);

            bool catastrophicStaleTimeoutPattern =
                (staleRate >= 0.80 && effectiveLoss >= 0.65) ||
                (staleRate >= 0.65 && badRate >= 0.70) ||
                (effectiveLoss >= 0.80 && runtime.AvgConfidence >= 0 && confidence < 0.20);

            if (catastrophicStaleTimeoutPattern)
            {
                reason = "early-stop catastrophic stale/timeout";
                return true;
            }

            if (avgPing >= 1800.0 || p95Ping >= 2500.0)
            {
                reason = "early-stop catastrophic latency";
                return true;
            }

            if (effectiveLoss >= 0.55 && staleRate >= 0.45)
            {
                reason = "early-stop catastrophic loss";
                return true;
            }

            bool tunnelCollapsed =
                runtime.Candidate != null &&
                runtime.Candidate.Kind == ProbeCandidateKind.Speedify &&
                (!runtime.TunnelConnected || runtime.ActiveConnectionCount <= 0) &&
                (runtime.AggregateCongested || spikeDensity >= 10.0 || effectiveLoss >= 0.35);

            if (tunnelCollapsed)
            {
                reason = "early-stop tunnel collapse";
                return true;
            }

            return false;
        }

        public static string BuildShortText(ProbeScoreResult result)
        {
            if (result == null)
                return "unknown";

            List<string> parts = new List<string>();

            if (result.Badge == ProbeBadgeKind.Smoothest)
                parts.Add("Smoothest");

            if (result.WarningLevel == ProbeWarningLevel.Borderline)
                parts.Add("Borderline");

            if (result.WarningLevel == ProbeWarningLevel.Risky)
                parts.Add("Risky");

            if (parts.Count == 0)
                parts.Add("Clean");

            return string.Join(" / ", parts);
        }

        public static string GetWarningLabel(ProbeScoreResult result)
        {
            if (result == null)
                return "unknown";

            if (result.WarningLevel == ProbeWarningLevel.Risky)
                return "risky";

            if (result.WarningLevel == ProbeWarningLevel.Borderline)
                return "borderline";

            return "clean";
        }

        public static string GetBadgeLabel(ProbeScoreResult result)
        {
            if (result == null || result.Badge == ProbeBadgeKind.None)
                return "none";

            return result.Badge == ProbeBadgeKind.Smoothest ? "smoothest" : "none";
        }

        public static string BuildRouteExplanation(ProbeScoreResult result)
        {
            if (result == null || result.Runtime == null)
                return "no probe data";

            string warning = GetWarningLabel(result);
            string primary = GetPrimaryInstabilityReason(result);
            string badge = result.Badge == ProbeBadgeKind.Smoothest ? ", smoothest jitter" : string.Empty;

            if (warning == "clean")
                return "clean stability profile" + badge;

            return warning + ", " + primary + badge;
        }

        public static string BuildWinnerReason(
            ProbeScoreResult winner,
            ProbeScoreResult runnerUp,
            ProbeWinnerConfidenceResult confidence,
            Func<ProbeScoreResult, double> scoreSelector = null)
        {
            if (winner == null)
                return "no winner";

            Func<ProbeScoreResult, double> selector =
                scoreSelector ?? (x => x != null ? x.OverallStabilityScore : double.MaxValue);

            string gapText = "no runner-up";
            if (runnerUp != null)
            {
                bool hasConfidenceGap = confidence != null &&
                    !double.IsNaN(confidence.GapToRunnerUpScore) &&
                    !double.IsInfinity(confidence.GapToRunnerUpScore) &&
                    confidence.GapToRunnerUpScore >= 0.0;

                double gap = hasConfidenceGap
                    ? confidence.GapToRunnerUpScore
                    : Math.Max(0.0, selector(runnerUp) - selector(winner));

                gapText = gap >= 8.0 ? "clear score gap" :
                          gap >= 3.0 ? "moderate score gap" :
                          "narrow score gap";
            }

            string confidenceText = confidence != null
                ? confidence.Kind.ToString()
                : "NoClearWinner";

            return gapText + " + " + confidenceText + " confidence";
        }

        public static ProbeWinnerConfidenceResult EvaluateWinnerConfidence(
            ProbeScoreResult winner,
            ProbeScoreResult runnerUp,
            LearnedProbeStateProfile learned,
            Func<ProbeScoreResult, double> scoreSelector = null)
        {
            ProbeWinnerConfidenceResult result = new ProbeWinnerConfidenceResult();

            if (winner == null || winner.Runtime == null)
            {
                result.Reason = "winner metrics unavailable";
                return result;
            }

            Func<ProbeScoreResult, double> selector =
                scoreSelector ?? (x => x != null ? x.OverallStabilityScore : double.MaxValue);

            bool missingRunnerUp = runnerUp == null;
            double gap = runnerUp != null
                ? Math.Max(0.0, selector(runnerUp) - selector(winner))
                : 0.0;

            result.GapToRunnerUpScore = gap;

            double gapPoints =
                gap >= 22.0 ? 45.0 :
                gap >= 14.0 ? 34.0 :
                gap >= 8.0 ? 22.0 :
                gap >= 4.0 ? 12.0 :
                2.0;

            double comparisonPenalty = missingRunnerUp ? 14.0 : 0.0;

            double confidence = Safe(winner.Runtime.AvgConfidence);
            double samplePoints =
                confidence >= 0.90 ? 28.0 :
                confidence >= 0.80 ? 24.0 :
                confidence >= 0.70 ? 18.0 :
                confidence >= 0.60 ? 12.0 :
                confidence >= 0.50 ? 6.0 :
                2.0;

            double staleRate = Safe(winner.Runtime.StaleRate);
            double stalePenalty =
                staleRate >= 0.18 ? 16.0 :
                staleRate >= 0.12 ? 10.0 :
                staleRate >= 0.07 ? 5.0 :
                0.0;

            double learningPoints = 0.0;
            string learningText = "seed/default-backed";

            if (learned != null && learned.IsTrustedLearning)
            {
                int totalSamples = learned.SampleCountGood + learned.SampleCountBad;

                if (totalSamples >= 10) learningPoints = 14.0;
                else if (totalSamples >= 5) learningPoints = 10.0;
                else learningPoints = 6.0;

                ServerProbeLearningStore.LearningAgeInfo age =
                    ServerProbeLearningStore.GetLearningAgeInfo(learned.LastUpdatedUtc);

                learningPoints *= age.InfluenceFactor;
                learningText = "learning-backed (" + age.AgeLabel + ")";
            }

            double warningPenalty =
                winner.WarningLevel == ProbeWarningLevel.Risky ? 24.0 :
                winner.WarningLevel == ProbeWarningLevel.Borderline ? 12.0 :
                0.0;

            double finalScore = gapPoints + samplePoints + learningPoints - stalePenalty - warningPenalty - comparisonPenalty;

            if (missingRunnerUp)
                finalScore = Math.Min(finalScore, 60.0);

            finalScore = Math.Max(0.0, Math.Min(100.0, finalScore));
            result.ConfidenceScore = finalScore;

            if (!missingRunnerUp &&
                finalScore >= 74.0 &&
                gap >= 8.0 &&
                winner.WarningLevel == ProbeWarningLevel.None)
            {
                result.Kind = ProbeWinnerConfidenceKind.HighConfidence;
            }
            else if (!missingRunnerUp && finalScore >= 38.0 && gap >= 2.0 && winner.WarningLevel != ProbeWarningLevel.Risky)
            {
                result.Kind = ProbeWinnerConfidenceKind.Tentative;
            }
            else
            {
                result.Kind = ProbeWinnerConfidenceKind.NoClearWinner;
            }

            string gapText = missingRunnerUp
                ? "runner-up unavailable"
                : gap >= 8.0 ? "clear lead" :
                  gap >= 3.0 ? "narrow lead" :
                  "near tie";

            string sampleText = confidence >= 0.70 ? "good sample quality" : "limited sample quality";
            string staleText = stalePenalty > 0 ? "stale penalty applied" : "fresh samples";

            result.Reason = gapText + ", " + sampleText + ", " + learningText + ", " + staleText;

            return result;
        }

        private static ProbeWarningLevel ClassifyWarning(ProbeCandidateRuntimeData runtime)
        {
            SpeedifyHealthBucket healthBucket = ClassifySpeedifyHealth(runtime);
            bool isSpeedify = runtime?.Candidate != null && runtime.Candidate.Kind == ProbeCandidateKind.Speedify;

            double avgJitterBorderline = isSpeedify ? SpeedifyBorderlineAvgJitterMs : BorderlineAvgJitterMs;
            double avgJitterRisky = isSpeedify ? SpeedifyRiskyAvgJitterMs : RiskyAvgJitterMs;
            double jitterRangeBorderline = isSpeedify ? SpeedifyBorderlineJitterRangeMs : BorderlineJitterRangeMs;
            double jitterRangeRisky = isSpeedify ? SpeedifyRiskyJitterRangeMs : RiskyJitterRangeMs;
            double p95Borderline = isSpeedify ? SpeedifyBorderlineP95PingMs : BorderlineP95PingMs;
            double p95Risky = isSpeedify ? SpeedifyRiskyP95PingMs : RiskyP95PingMs;
            double spikeBorderline = isSpeedify ? SpeedifyBorderlineSpikeDensity : BorderlineSpikeDensity;
            double spikeRisky = isSpeedify ? SpeedifyRiskySpikeDensity : RiskySpikeDensity;
            double lossBorderline = isSpeedify ? SpeedifyBorderlineLossRate : BorderlineLossRate;
            double lossRisky = isSpeedify ? SpeedifyRiskyLossRate : RiskyLossRate;
            double staleBorderline = isSpeedify ? SpeedifyBorderlineStaleRate : BorderlineStaleRate;
            double staleRisky = isSpeedify ? SpeedifyRiskyStaleRate : RiskyStaleRate;
            double badBorderline = isSpeedify ? SpeedifyBorderlineBadRate : BorderlineBadQualityRate;
            double badRisky = isSpeedify ? SpeedifyRiskyBadRate : RiskyBadQualityRate;

            if (healthBucket == SpeedifyHealthBucket.HealthyBonded)
            {
                spikeBorderline += 0.6;
                spikeRisky += 0.8;
                jitterRangeBorderline += 0.8;
            }
            else if (healthBucket == SpeedifyHealthBucket.Unhealthy)
            {
                spikeBorderline -= 0.3;
                spikeRisky -= 0.4;
            }

            double avgJitter = Safe(runtime.AvgJitterMs);
            double jitterRange = Safe(ComputeJitterRange(runtime));
            double p95Ping = Safe(runtime.P95PingMs);
            double spikeDensity = Safe(runtime.SpikeDensityPerMinute);
            double lossRate = EffectiveLossRate(GetLossRateForScoring(runtime));
            double staleRate = Safe(runtime.StaleRate);
            double badRate = Safe(runtime.AvgBadQualityRate);

            bool risky =
                avgJitter >= avgJitterRisky ||
                jitterRange >= jitterRangeRisky ||
                p95Ping >= p95Risky ||
                spikeDensity >= spikeRisky ||
                lossRate >= lossRisky ||
                staleRate >= staleRisky ||
                badRate >= badRisky;

            if (risky)
                return ProbeWarningLevel.Risky;

            bool borderline =
                avgJitter >= avgJitterBorderline ||
                jitterRange >= jitterRangeBorderline ||
                p95Ping >= p95Borderline ||
                spikeDensity >= spikeBorderline ||
                lossRate >= lossBorderline ||
                staleRate >= staleBorderline ||
                badRate >= badBorderline;

            if (borderline)
                return ProbeWarningLevel.Borderline;

            return ProbeWarningLevel.None;
        }

        private static double SpeedifyPenalty(ProbeCandidateRuntimeData runtime, SpeedifyHealthBucket healthBucket)
        {
            if (runtime == null || runtime.Candidate == null)
                return 0.0;

            if (runtime.Candidate.Kind == ProbeCandidateKind.Raw)
                return 0.0;

            return healthBucket switch
            {
                SpeedifyHealthBucket.NoTelemetry => 3.5,
                SpeedifyHealthBucket.Unhealthy => 12.0,
                SpeedifyHealthBucket.HealthySingleLink => 0.0,
                SpeedifyHealthBucket.HealthyBonded => -0.6,
                _ => 0.0
            };
        }

        private static SpeedifyHealthBucket ClassifySpeedifyHealth(ProbeCandidateRuntimeData runtime)
        {
            if (runtime?.Candidate == null || runtime.Candidate.Kind != ProbeCandidateKind.Speedify)
                return SpeedifyHealthBucket.NotApplicable;

            bool hasTelemetry =
                runtime.SpeedifyAggregateLatencyMs > 0 ||
                runtime.SpeedifyAggregateJitterMs > 0 ||
                runtime.SpeedifyReportedLatencyMs > 0 ||
                runtime.ActiveConnectionCount > 0;

            if (!hasTelemetry)
                return SpeedifyHealthBucket.NoTelemetry;

            if (!runtime.TunnelConnected || runtime.ActiveConnectionCount <= 0 || runtime.AggregateCongested)
                return SpeedifyHealthBucket.Unhealthy;

            return runtime.ActiveConnectionCount >= 2
                ? SpeedifyHealthBucket.HealthyBonded
                : SpeedifyHealthBucket.HealthySingleLink;
        }

        private static double GetLatencyPenaltyModifier(ProbeCandidateRuntimeData runtime, SpeedifyHealthBucket healthBucket)
        {
            if (runtime?.Candidate == null || runtime.Candidate.Kind != ProbeCandidateKind.Speedify)
                return 1.0;

            double fastLatency = Safe(runtime.P95PingMs) > 0 ? Safe(runtime.P95PingMs) : Safe(runtime.AvgPingMs);
            double speedifyLatency = ChoosePositive(runtime.SpeedifyAggregateLatencyMs, runtime.SpeedifyReportedLatencyMs);

            double correlation = ComputeCorrelationModifier(
                fastLatency,
                speedifyLatency,
                expectedOverheadRatio: 0.30,
                healthyModifier: healthBucket == SpeedifyHealthBucket.HealthyBonded ? 0.86 : 0.90,
                unhealthyModifier: 1.10);

            double baseTolerance = healthBucket switch
            {
                SpeedifyHealthBucket.HealthyBonded => 0.92,
                SpeedifyHealthBucket.HealthySingleLink => 0.96,
                SpeedifyHealthBucket.Unhealthy => 1.08,
                SpeedifyHealthBucket.NoTelemetry => 0.96,
                _ => 1.0
            };

            return Clamp(baseTolerance * correlation, 0.68, 1.22);
        }

        private static double GetJitterPenaltyModifier(ProbeCandidateRuntimeData runtime, SpeedifyHealthBucket healthBucket)
        {
            if (runtime?.Candidate == null || runtime.Candidate.Kind != ProbeCandidateKind.Speedify)
                return 1.0;

            double fastJitter = Safe(runtime.P95JitterMs) > 0 ? Safe(runtime.P95JitterMs) : Safe(runtime.AvgJitterMs);
            double speedifyJitter = Safe(runtime.SpeedifyAggregateJitterMs);

            double correlation = ComputeCorrelationModifier(
                fastJitter,
                speedifyJitter,
                expectedOverheadRatio: 0.55,
                healthyModifier: healthBucket == SpeedifyHealthBucket.HealthyBonded ? 0.84 : 0.90,
                unhealthyModifier: 1.12);

            double baseTolerance = healthBucket switch
            {
                SpeedifyHealthBucket.HealthyBonded => 0.93,
                SpeedifyHealthBucket.HealthySingleLink => 0.97,
                SpeedifyHealthBucket.Unhealthy => 1.08,
                SpeedifyHealthBucket.NoTelemetry => 0.98,
                _ => 1.0
            };

            return Clamp(baseTolerance * correlation, 0.70, 1.24);
        }

        private static double GetSpikePenaltyModifier(ProbeCandidateRuntimeData runtime, SpeedifyHealthBucket healthBucket)
        {
            if (runtime?.Candidate == null || runtime.Candidate.Kind != ProbeCandidateKind.Speedify)
                return 1.0;

            bool lowSpike = Safe(runtime.SpikeDensityPerMinute) <= 4.0;
            bool latencyHealthy = Safe(runtime.SpeedifyAggregateLatencyMs) > 0 && Safe(runtime.SpeedifyAggregateLatencyMs) <= 95.0;
            bool jitterHealthy = Safe(runtime.SpeedifyAggregateJitterMs) > 0 && Safe(runtime.SpeedifyAggregateJitterMs) <= 20.0;

            double modifier = healthBucket switch
            {
                SpeedifyHealthBucket.HealthyBonded => 0.76,
                SpeedifyHealthBucket.HealthySingleLink => 0.86,
                SpeedifyHealthBucket.Unhealthy => 1.18,
                SpeedifyHealthBucket.NoTelemetry => 1.0,
                _ => 1.0
            };

            if (healthBucket == SpeedifyHealthBucket.HealthyBonded ||
                healthBucket == SpeedifyHealthBucket.HealthySingleLink)
            {
                if (latencyHealthy && jitterHealthy)
                    modifier -= 0.05;

                if (!lowSpike && runtime.AggregateCongested)
                    modifier += 0.08;
            }

            return Clamp(modifier, 0.68, 1.28);
        }

        private static double GetLossStalePenaltyModifier(ProbeCandidateRuntimeData runtime, SpeedifyHealthBucket healthBucket)
        {
            if (runtime?.Candidate == null || runtime.Candidate.Kind != ProbeCandidateKind.Speedify)
                return 1.0;

            double ugliness =
                Safe(runtime.StaleRate) +
                EffectiveLossRate(runtime.AvgLossRate) +
                Safe(runtime.AvgBadQualityRate);

            double modifier = healthBucket switch
            {
                SpeedifyHealthBucket.HealthyBonded => ugliness < 0.45 ? 0.82 : 0.90,
                SpeedifyHealthBucket.HealthySingleLink => ugliness < 0.45 ? 0.90 : 0.97,
                SpeedifyHealthBucket.Unhealthy => ugliness > 0.35 ? 1.16 : 1.08,
                SpeedifyHealthBucket.NoTelemetry => 0.98,
                _ => 1.0
            };

            return Clamp(modifier, 0.76, 1.26);
        }

        private static double ComputeCorrelationModifier(
            double fastMetric,
            double speedifyMetric,
            double expectedOverheadRatio,
            double healthyModifier,
            double unhealthyModifier)
        {
            if (fastMetric <= 0 || speedifyMetric <= 0)
                return 1.0;

            double expectedMax = speedifyMetric * (1.0 + Math.Max(0.0, expectedOverheadRatio));

            if (fastMetric <= expectedMax)
                return healthyModifier;

            if (fastMetric >= expectedMax * 1.25)
                return unhealthyModifier;

            return 1.0;
        }

        private static double ChoosePositive(double first, double second)
        {
            if (first > 0)
                return first;
            if (second > 0)
                return second;
            return -1;
        }

        private static double Clamp(double value, double min, double max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        private static double ApplySpeedifyPositiveInfluenceCap(
            double adjustedScore,
            double fastPingDominantScore,
            SpeedifyHealthBucket healthBucket,
            out double capApplied)
        {
            capApplied = 0.0;

            double maxPositiveInfluence = healthBucket switch
            {
                SpeedifyHealthBucket.HealthyBonded => MaxHealthyBondedPositiveInfluence,
                SpeedifyHealthBucket.HealthySingleLink => MaxHealthySinglePositiveInfluence,
                _ => 0.0
            };

            if (maxPositiveInfluence <= 0.0)
                return adjustedScore;

            double floor = fastPingDominantScore * (1.0 - maxPositiveInfluence);

            if (adjustedScore >= floor)
                return adjustedScore;

            capApplied = floor - adjustedScore;
            return floor;
        }

        private static double ComputeLearningAdjustment(
            ProbeCandidateRuntimeData runtime,
            LearnedProbeStateProfile learned)
        {
            if (runtime == null || learned == null || !learned.IsTrustedLearning)
                return 0.0;

            double goodBias = 0.0;
            double badBias = 0.0;

            if (learned.AvgJitterMs.IsInGoodBand(Safe(runtime.AvgJitterMs))) goodBias += 0.8;
            if (learned.JitterRangeMs.IsInGoodBand(Safe(ComputeJitterRange(runtime)))) goodBias += 0.8;
            if (learned.P95PingMs.IsInGoodBand(Safe(runtime.P95PingMs))) goodBias += 0.7;
            if (learned.SpikeDensityPerMinute.IsInGoodBand(Safe(runtime.SpikeDensityPerMinute))) goodBias += 0.7;

            if (learned.AvgJitterMs.IsInBadBand(Safe(runtime.AvgJitterMs))) badBias += 1.2;
            if (learned.JitterRangeMs.IsInBadBand(Safe(ComputeJitterRange(runtime)))) badBias += 1.1;
            if (learned.P95PingMs.IsInBadBand(Safe(runtime.P95PingMs))) badBias += 1.0;
            if (learned.SpikeDensityPerMinute.IsInBadBand(Safe(runtime.SpikeDensityPerMinute))) badBias += 0.9;

            ServerProbeLearningStore.LearningAgeInfo age =
                ServerProbeLearningStore.GetLearningAgeInfo(learned.LastUpdatedUtc);

            double trustedBias = (badBias - goodBias) * age.InfluenceFactor;
            return Math.Max(-4.0, Math.Min(6.0, trustedBias));
        }

        private static double ComputeJitterRange(ProbeCandidateRuntimeData runtime)
        {
            if (runtime == null)
                return -1;

            if (runtime.MinJitterMs >= 0 && runtime.MaxJitterMs >= 0)
                return Math.Max(0, runtime.MaxJitterMs - runtime.MinJitterMs);

            if (runtime.AvgJitterMs >= 0 && runtime.P95JitterMs >= 0)
                return Math.Max(0, runtime.P95JitterMs - runtime.AvgJitterMs);

            return -1;
        }

        private static double EffectiveLossRate(double lossRate)
        {
            double safeLoss = Safe(lossRate);

            if (safeLoss <= 0.00) return 0.0;
            if (safeLoss <= 0.03) return 0.0;
            if (safeLoss <= 0.06) return 0.02;
            if (safeLoss <= 0.10) return 0.05;
            if (safeLoss <= 0.15) return 0.10;

            return safeLoss;
        }

        private static double GetLossRateForScoring(ProbeCandidateRuntimeData runtime)
        {
            if (runtime == null)
                return -1;

            if (runtime.StartupAdjustedLossRate >= 0)
                return runtime.StartupAdjustedLossRate;

            return runtime.AvgLossRate;
        }

        private static string BuildSummary(ProbeCandidateRuntimeData runtime, ProbeScoreResult result)
        {
            string warning =
                result.WarningLevel == ProbeWarningLevel.Risky ? "Risky" :
                result.WarningLevel == ProbeWarningLevel.Borderline ? "Borderline" :
                "Clean";

            string measurementStatus = GetMeasurementStatusLabel(runtime);
            string weakness = GetPrimaryInstabilityReason(result);
            string speedifyState = runtime?.Candidate?.Kind == ProbeCandidateKind.Speedify
                ? (result?.SpeedifyHealthLabel ?? "no telemetry")
                : "raw baseline";

            return
                measurementStatus + " | " +
                warning +
                " | weak=" + weakness +
                " | spd=" + speedifyState +
                " | j=" + F(runtime.AvgJitterMs) +
                " jr=" + F(result.JitterRangeMs) +
                " p95=" + F(runtime.P95PingMs) +
                " spkD=" + F(runtime.SpikeDensityPerMinute) +
                " loss=" + F(EffectiveLossRate(GetLossRateForScoring(runtime)) * 100.0);
        }

        public static string BuildScoreBreakdown(ProbeScoreResult result)
        {
            if (result == null)
                return "breakdown=unavailable";

            return "breakdown{lat=" + F(result.LatencyContribution) +
                   ",jit=" + F(result.JitterContribution) +
                   ",spk=" + F(result.SpikeContribution) +
                   ",lsb=" + F(result.LossStaleBadContribution) +
                   ",mLat=" + result.SpeedifyLatencyModifier.ToString("0.00") +
                   ",mJit=" + result.SpeedifyJitterModifier.ToString("0.00") +
                   ",mSpk=" + result.SpeedifySpikeModifier.ToString("0.00") +
                   ",mLsb=" + result.SpeedifyLossStaleModifier.ToString("0.00") +
                   ",adj=" + F(result.SpeedifyGlobalAdjustment) +
                   ",cap=" + F(result.SpeedifyPositiveInfluenceCapApplied) +
                   ",confM=" + result.ConfidenceModifier.ToString("0.00") +
                   ",final=" + F(result.OverallStabilityScore) + "}";
        }

        public static double ComputeConfidenceModifier(ProbeCandidateRuntimeData runtime)
        {
            if (runtime?.Candidate == null || runtime.Candidate.Kind != ProbeCandidateKind.Speedify)
                return 1.0;

            double expectedSamples = runtime.SpeedifyExpectedSampleCount > 0
                ? runtime.SpeedifyExpectedSampleCount
                : 4.0;
            double freshSamples = Math.Max(0.0, runtime.SpeedifyFreshSampleCount);
            double sampleCoverage = Clamp(freshSamples / Math.Max(1.0, expectedSamples), 0.0, 1.2);
            double sampleConfidence = Clamp(0.40 + (sampleCoverage * 0.65), 0.35, 1.08);

            double speedifyLatency = ChoosePositive(runtime.SpeedifyAggregateLatencyMs, runtime.SpeedifyReportedLatencyMs);
            double stdDev = runtime.SpeedifyAggregateLatencyStdDevMs;
            double relativeStdDev = speedifyLatency > 0 ? stdDev / Math.Max(1.0, speedifyLatency) : -1;
            double stabilityConfidence = relativeStdDev >= 0
                ? Clamp(1.03 - (relativeStdDev * 0.85), 0.45, 1.05)
                : 0.92;

            string notes = runtime.Notes ?? string.Empty;
            double noteConfidence = 1.0;
            if (notes.IndexOf("low-confidence", StringComparison.OrdinalIgnoreCase) >= 0)
                noteConfidence *= 0.90;
            if (notes.IndexOf("unstable", StringComparison.OrdinalIgnoreCase) >= 0)
                noteConfidence *= 0.88;

            double fastPingLatency = Safe(runtime.AvgPingMs);
            double crossSignalConfidence = 1.0;
            if (speedifyLatency > 0 && fastPingLatency > 0)
            {
                double relativeDifference = Math.Abs(speedifyLatency - fastPingLatency) / Math.Max(1.0, fastPingLatency);
                crossSignalConfidence = Clamp(1.10 - (relativeDifference * 0.75), 0.45, 1.12);
            }

            double modifier = sampleConfidence * stabilityConfidence * noteConfidence * crossSignalConfidence;
            modifier = Clamp(modifier, 0.3, 1.15);

            Logger.Log("conf",
                "compute | " + (runtime.Candidate?.ToString() ?? "unknown") +
                " | samples=" + freshSamples.ToString("0") + "/" + expectedSamples.ToString("0") +
                " | sampleC=" + sampleConfidence.ToString("0.00") +
                " | std=" + (stdDev >= 0 ? stdDev.ToString("0.0") : "--") +
                " | stabilityC=" + stabilityConfidence.ToString("0.00") +
                " | noteC=" + noteConfidence.ToString("0.00") +
                " | crossC=" + crossSignalConfidence.ToString("0.00") +
                " | final=" + modifier.ToString("0.00"));

            return modifier;
        }

        private static string GetMeasurementStatusLabel(ProbeCandidateRuntimeData runtime)
        {
            if (runtime == null || !runtime.ReachedUsableState || !runtime.MeasurementStarted)
                return "unusable";

            if (runtime.MeasurementStoppedEarly)
                return "measured-early-stop";

            if (runtime.MeasurementCompleted)
                return "measured-full";

            return "measured-partial";
        }

        private static string GetSpeedifyHealthLabel(SpeedifyHealthBucket healthBucket)
        {
            return healthBucket switch
            {
                SpeedifyHealthBucket.NoTelemetry => "no telemetry",
                SpeedifyHealthBucket.Unhealthy => "unhealthy",
                SpeedifyHealthBucket.HealthySingleLink => "healthy single-link",
                SpeedifyHealthBucket.HealthyBonded => "healthy bonded",
                _ => "n/a"
            };
        }

        private static string GetPrimaryInstabilityReason(ProbeScoreResult result)
        {
            if (result == null || result.Runtime == null)
                return "limited metrics";

            ProbeCandidateRuntimeData runtime = result.Runtime;

            if (Safe(runtime.SpikeDensityPerMinute) >= BorderlineSpikeDensity)
                return "higher spike density";

            if (Safe(result.JitterRangeMs) >= BorderlineJitterRangeMs)
                return "high jitter range";

            if (Safe(runtime.AvgJitterMs) >= BorderlineAvgJitterMs)
                return "high average jitter";

            if (Safe(runtime.P95PingMs) >= BorderlineP95PingMs)
                return "high p95 latency";

            if (EffectiveLossRate(GetLossRateForScoring(runtime)) >= BorderlineLossRate)
                return "loss pressure";

            if (Safe(runtime.StaleRate) >= BorderlineStaleRate)
                return "stale sample pressure";

            return "mixed instability signals";
        }

        private static double Safe(double value)
        {
            return value < 0 || double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;
        }

        private static bool HasFundamentallyBrokenSamples(ProbeCandidateRuntimeData runtime)
        {
            if (runtime == null)
                return true;

            bool hasAnyCoreMetric =
                runtime.AvgPingMs >= 0 ||
                runtime.P95PingMs >= 0 ||
                runtime.AvgJitterMs >= 0 ||
                runtime.SpikeDensityPerMinute >= 0 ||
                runtime.AvgLossRate >= 0 ||
                runtime.StaleRate >= 0;

            if (!hasAnyCoreMetric)
                return true;

            if (runtime.AvgConfidence >= 0 && runtime.AvgConfidence < 0.05 && runtime.StaleRate >= 0.95)
                return true;

            return false;
        }

        private static bool TryValidateMeasuredRuntimeForScoring(ProbeCandidateRuntimeData runtime, out string reason)
        {
            reason = null;

            if (runtime == null || runtime.Candidate == null)
            {
                reason = "unusable probe";
                return false;
            }

            if (!runtime.ReachedUsableState || !runtime.MeasurementStarted)
            {
                reason = runtime.DiscardReason ?? "unusable-before-measurement";
                return false;
            }

            if (HasFundamentallyBrokenSamples(runtime))
            {
                reason = "unusable-measurement-samples";
                return false;
            }

            return true;
        }

        private static double ComputeEarlyStopPenalty(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return 35.0;

            if (reason.IndexOf("catastrophic latency", StringComparison.OrdinalIgnoreCase) >= 0)
                return 60.0;

            if (reason.IndexOf("catastrophic stale", StringComparison.OrdinalIgnoreCase) >= 0)
                return 55.0;

            if (reason.IndexOf("tunnel collapse", StringComparison.OrdinalIgnoreCase) >= 0)
                return 50.0;

            return 40.0;
        }

        private static string F(double value)
        {
            return value < 0 ? "--" : value.ToString("0.0");
        }

        private static string AppendReason(string current, string addition)
        {
            if (string.IsNullOrWhiteSpace(addition))
                return current;

            if (string.IsNullOrWhiteSpace(current))
                return addition;

            return current + "," + addition;
        }

        private static void ApplyHold(
            ProbeDecisionPolicyResult policy,
            string reason,
            bool extendWindow,
            string decisionType)
        {
            if (policy == null)
                return;

            policy.ShouldStickToPrevious = true;
            policy.IsStabilityHold = true;
            policy.PolicyReason = AppendReason(policy.PolicyReason, reason);
            policy.DecisionType = decisionType;

            if (extendWindow)
            {
                policy.ShouldDelaySwitch = true;
                policy.ShouldReprobe = true;
                if (policy.DelayMs <= 0)
                    policy.DelayMs = 1200;
            }
        }
    }
}
