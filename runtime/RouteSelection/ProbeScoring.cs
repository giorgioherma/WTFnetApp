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

        // Ping
        public double AvgPingMs { get; set; } = -1;
        public double MaxPingMs { get; set; } = -1;
        public double P95PingMs { get; set; } = -1;

        // Jitter
        public double AvgJitterMs { get; set; } = -1;
        public double MinJitterMs { get; set; } = -1;
        public double MaxJitterMs { get; set; } = -1;
        public double P95JitterMs { get; set; } = -1;

        public double AvgLossRate { get; set; } = -1;
        public double AvgBadQualityRate { get; set; } = -1;
        public double AvgConfidence { get; set; } = -1;

        public double SpikeDensityPerMinute { get; set; } = -1;
        public double StaleRate { get; set; } = -1;

        // Speedify side if candidate uses tunnel
        public bool TunnelConnected { get; set; }
        public bool AggregateCongested { get; set; }
        public int ActiveConnectionCount { get; set; }
        public int BackupConnectionCount { get; set; }
        public double SpeedifyAggregateLatencyMs { get; set; } = -1;
        public double SpeedifyAggregateJitterMs { get; set; } = -1;
        public double SpeedifyReportedLatencyMs { get; set; } = -1;

        public bool IsValid { get; set; } = true;
        public string Notes { get; set; }

        public bool StabilizationReached { get; set; }
        public bool StabilizationTimedOut { get; set; }

        public ProbeReadinessKind ReadinessKind { get; set; } = ProbeReadinessKind.NotReady;
        public string ReadinessReason { get; set; }

        public bool ReachedUsableState { get; set; }
        public bool MeasurementStarted { get; set; }
        public bool MeasurementCompleted { get; set; }
        public bool MeasurementStoppedEarly { get; set; }
        public string MeasurementStopReason { get; set; }
        public string DiscardReason { get; set; }
    }

    public sealed class ProbeScoreResult
    {
        public ProbeCandidateDefinition Candidate { get; set; }
        public ProbeCandidateRuntimeData Runtime { get; set; }

        // lower is better
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

        // Raw thresholds
        private const double BorderlineAvgJitterMs = 12.0;
        private const double RiskyAvgJitterMs = 18.0;

        private const double BorderlineJitterRangeMs = 10.0;
        private const double RiskyJitterRangeMs = 18.0;

        private const double BorderlineP95PingMs = 85.0;
        private const double RiskyP95PingMs = 120.0;

        private const double BorderlineSpikeDensity = 4.0;
        private const double RiskySpikeDensity = 8.0;

        // loss thresholds relaxed because LTE + probe timing creates fake mini-loss
        private const double BorderlineLossRate = 0.08;
        private const double RiskyLossRate = 0.18;

        private const double BorderlineStaleRate = 0.05;
        private const double RiskyStaleRate = 0.15;

        private const double BorderlineBadQualityRate = 0.12;
        private const double RiskyBadQualityRate = 0.25;

        // Speedify warning thresholds
        private const double SpeedifyBorderlineAvgJitterMs = 14.5;
        private const double SpeedifyRiskyAvgJitterMs = 22.0;
        private const double SpeedifyBorderlineJitterRangeMs = 13.0;
        private const double SpeedifyRiskyJitterRangeMs = 22.0;
        private const double SpeedifyBorderlineP95PingMs = 105.0;
        private const double SpeedifyRiskyP95PingMs = 150.0;
        private const double SpeedifyBorderlineSpikeDensity = 5.0;
        private const double SpeedifyRiskySpikeDensity = 9.5;
        private const double SpeedifyBorderlineLossRate = 0.11;
        private const double SpeedifyRiskyLossRate = 0.23;
        private const double SpeedifyBorderlineStaleRate = 0.07;
        private const double SpeedifyRiskyStaleRate = 0.18;
        private const double SpeedifyBorderlineBadRate = 0.16;
        private const double SpeedifyRiskyBadRate = 0.30;

        // Bounded Speedify influence
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
            double lossRate = EffectiveLossRate(runtime.AvgLossRate);
            double staleRate = Safe(runtime.StaleRate);
            double badRate = Safe(runtime.AvgBadQualityRate);
            double avgPing = Safe(runtime.AvgPingMs);
            SpeedifyHealthBucket healthBucket = ClassifySpeedifyHealth(runtime);

            double latencyModifier = GetLatencyPenaltyModifier(runtime, healthBucket);
            double jitterModifier = GetJitterPenaltyModifier(runtime, healthBucket);
            double spikeModifier = GetSpikePenaltyModifier(runtime, healthBucket);
            double lossStaleModifier = GetLossStalePenaltyModifier(runtime, healthBucket);
            double speedifyAdjustment = SpeedifyPenalty(runtime, healthBucket);
            double confidencePenalty = ConfidencePenalty(runtime.AvgConfidence);

            result.JitterRangeMs = jitterRange;
            result.SpeedifyLatencyModifier = latencyModifier;
            result.SpeedifyJitterModifier = jitterModifier;
            result.SpeedifySpikeModifier = spikeModifier;
            result.SpeedifyLossStaleModifier = lossStaleModifier;
            result.SpeedifyHealthLabel = GetSpeedifyHealthLabel(healthBucket);

            // Smoothness metric only. Lower is better.
            result.JitterStabilityScore =
                avgJitter +
                (jitterRange * 0.65) +
                (spikeDensity * 0.45) +
                (badRate * 12.0);

            // Full comparison metric. FastPing base remains dominant, Speedify only corrects interpretation.
            double latencyContribution = ((p95Ping * 0.10) + (avgPing * 0.05)) * latencyModifier;
            double jitterContribution = ((avgJitter * 1.75) + (jitterRange * 1.05)) * jitterModifier;
            double spikeContribution = (spikeDensity * 2.95) * spikeModifier;
            double lossStaleBadContribution =
                ((badRate * 112.0) + (lossRate * 42.0) + (staleRate * 102.0)) * lossStaleModifier;

            double speedifyAdjustedScore =
                latencyContribution +
                jitterContribution +
                spikeContribution +
                lossStaleBadContribution +
                confidencePenalty +
                speedifyAdjustment;

            double fastPingDominantScore =
                ((p95Ping * 0.10) + (avgPing * 0.05)) +
                ((avgJitter * 1.75) + (jitterRange * 1.05)) +
                (spikeDensity * 2.95) +
                ((badRate * 112.0) + (lossRate * 42.0) + (staleRate * 102.0)) +
                confidencePenalty;

            double cappedScore = ApplySpeedifyPositiveInfluenceCap(speedifyAdjustedScore, fastPingDominantScore, healthBucket, out double capApplied);

            result.LatencyContribution = latencyContribution;
            result.JitterContribution = jitterContribution;
            result.SpikeContribution = spikeContribution;
            result.LossStaleBadContribution = lossStaleBadContribution;
            result.SpeedifyGlobalAdjustment = speedifyAdjustment;
            result.SpeedifyPositiveInfluenceCapApplied = capApplied;
            result.OverallStabilityScore = cappedScore;

            if (runtime.MeasurementStoppedEarly)
            {
                result.OverallStabilityScore += ComputeEarlyStopPenalty(runtime.MeasurementStopReason);
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

            double stabilityCore = candidate.OverallStabilityScore * 0.62;
            double spikePenalty = Safe(runtime.SpikeDensityPerMinute) * 1.20;
            double lossPenalty = EffectiveLossRate(runtime.AvgLossRate) * 100.0 * 0.16;
            double badQualityPenalty = Safe(runtime.AvgBadQualityRate) * 100.0 * 0.12;
            double confidencePenalty = Math.Max(0.0, 1.0 - Safe(runtime.AvgConfidence)) * 14.0;

            double warningAdjustment =
                candidate.WarningLevel == ProbeWarningLevel.Risky ? 18.0 :
                candidate.WarningLevel == ProbeWarningLevel.Borderline ? 6.0 :
                0.0;

            double learningAdjustment = ComputeLearningAdjustment(runtime, learned);

            return stabilityCore +
                   spikePenalty +
                   lossPenalty +
                   badQualityPenalty +
                   confidencePenalty +
                   warningAdjustment +
                   learningAdjustment;
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

            string failFastReason;
            if (TryGetFailFastDiscardReason(candidate, out failFastReason))
            {
                // Hard reject only for clearly unhealthy probes.
                decision.ShouldDiscard = true;
                decision.ReasonCategory = failFastReason;
                return decision;
            }

            if (baseline != null &&
                baseline.WarningLevel != ProbeWarningLevel.Risky &&
                candidate.StartupSelectionScore > baseline.StartupSelectionScore + 28.0)
            {
                // Conservative guardrail against obviously worse-than-raw probes.
                decision.ShouldDiscard = true;
                decision.ReasonCategory = "worse than raw baseline";
                return decision;
            }

            if (currentLeader != null &&
                candidate.StartupSelectionScore > currentLeader.StartupSelectionScore + 30.0)
            {
                // Conservative guardrail against obviously worse-than-held-leader probes.
                decision.ShouldDiscard = true;
                decision.ReasonCategory = "worse than current leader";
                return decision;
            }

            return decision;
        }

        private static bool TryGetFailFastDiscardReason(ProbeScoreResult candidate, out string reason)
        {
            reason = null;

            if (candidate == null || !TryValidateMeasuredRuntimeForScoring(candidate.Runtime, out string invalidReason))
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
                gapText = gap >= 8.0 ? "clear score gap" : gap >= 3.0 ? "moderate score gap" : "narrow score gap";
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

            Func<ProbeScoreResult, double> selector = scoreSelector ?? (x => x != null ? x.OverallStabilityScore : double.MaxValue);

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
                finalScore >= 72.0 &&
                gap >= 8.0 &&
                winner.WarningLevel == ProbeWarningLevel.None)
                result.Kind = ProbeWinnerConfidenceKind.HighConfidence;
            else if (!missingRunnerUp && finalScore >= 45.0 && gap >= 3.0)
                result.Kind = ProbeWinnerConfidenceKind.Tentative;
            else
                result.Kind = ProbeWinnerConfidenceKind.NoClearWinner;

            string gapText = missingRunnerUp
                ? "runner-up unavailable"
                : gap >= 8.0 ? "clear lead" : gap >= 3.0 ? "narrow lead" : "near tie";
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
            double lossRate = EffectiveLossRate(runtime.AvgLossRate);
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

            bool hasTelemetry = runtime.SpeedifyAggregateLatencyMs > 0 ||
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

            if (healthBucket is SpeedifyHealthBucket.HealthyBonded or SpeedifyHealthBucket.HealthySingleLink)
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

            double ugliness = Safe(runtime.StaleRate) + EffectiveLossRate(runtime.AvgLossRate) + Safe(runtime.AvgBadQualityRate);

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
            if (safeLoss <= 0.03) return 0.0;     // ignore tiny probe noise
            if (safeLoss <= 0.06) return 0.02;    // very light penalty
            if (safeLoss <= 0.10) return 0.05;    // moderate
            if (safeLoss <= 0.15) return 0.10;    // more serious

            return safeLoss;                      // real sustained loss
        }

        private static double ConfidencePenalty(double confidence)
        {
            if (confidence < 0)
                return 15.0;

            if (confidence >= 0.90) return 0.0;
            if (confidence >= 0.75) return 3.0;
            if (confidence >= 0.60) return 7.0;
            if (confidence >= 0.45) return 12.0;
            return 18.0;
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
                " loss=" + F(EffectiveLossRate(runtime.AvgLossRate) * 100.0);
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
                   ",final=" + F(result.OverallStabilityScore) + "}";
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

            if (EffectiveLossRate(runtime.AvgLossRate) >= BorderlineLossRate)
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
    }
}
