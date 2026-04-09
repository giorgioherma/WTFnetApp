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

        public bool IsValid { get; set; } = true;
        public string Notes { get; set; }

        public bool StabilizationReached { get; set; }
        public bool StabilizationTimedOut { get; set; }
    }

    public sealed class ProbeScoreResult
    {
        public ProbeCandidateDefinition Candidate { get; set; }
        public ProbeCandidateRuntimeData Runtime { get; set; }

        // lower is better
        public double OverallStabilityScore { get; set; } = double.MaxValue;
        public double JitterStabilityScore { get; set; } = double.MaxValue;

        public ProbeWarningLevel WarningLevel { get; set; } = ProbeWarningLevel.None;
        public ProbeBadgeKind Badge { get; set; } = ProbeBadgeKind.None;

        public bool IsDiscardedEarly { get; set; }
        public bool IsShortlisted { get; set; }
        public bool IsWinner { get; set; }

        public string SummaryText { get; set; }
        public string WarningText { get; set; }
        public string BadgeText { get; set; }

        public double JitterRangeMs { get; set; } = -1;
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

            if (runtime == null || runtime.Candidate == null || !runtime.IsValid)
            {
                result.WarningLevel = ProbeWarningLevel.Risky;
                result.WarningText = "invalid";
                result.SummaryText = "invalid probe";
                return result;
            }

            double avgJitter = Safe(runtime.AvgJitterMs);
            double jitterRange = Safe(ComputeJitterRange(runtime));
            double p95Ping = Safe(runtime.P95PingMs);
            double spikeDensity = Safe(runtime.SpikeDensityPerMinute);
            double lossRate = EffectiveLossRate(runtime.AvgLossRate);
            double staleRate = Safe(runtime.StaleRate);
            double badRate = Safe(runtime.AvgBadQualityRate);

            result.JitterRangeMs = jitterRange;

            // Smoothness metric only. Lower is better.
            result.JitterStabilityScore =
                avgJitter +
                (jitterRange * 0.65) +
                (spikeDensity * 0.45) +
                (badRate * 12.0);

            // Full comparison metric.
            // Jitter and spike behavior dominate. Loss is only a secondary penalty now.
            result.OverallStabilityScore =
                (avgJitter * 1.90) +
                (jitterRange * 1.15) +
                (p95Ping * 0.16) +
                (spikeDensity * 2.80) +
                (badRate * 120.0) +
                (lossRate * 45.0) +
                (staleRate * 110.0) +
                ConfidencePenalty(runtime.AvgConfidence) +
                SpeedifyPenalty(runtime);

            result.WarningLevel = ClassifyWarning(runtime);

            if (result.WarningLevel == ProbeWarningLevel.Borderline)
                result.WarningText = "Borderline";

            if (result.WarningLevel == ProbeWarningLevel.Risky)
                result.WarningText = "Risky";

            result.SummaryText = BuildSummary(runtime, result);

            return result;
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

        public static bool ShouldDiscardEarly(
            ProbeScoreResult candidate,
            ProbeScoreResult baseline,
            ProbeScoreResult currentLeader)
        {
            return EvaluateDiscardDecision(candidate, baseline, currentLeader).ShouldDiscard;
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

            if (candidate.WarningLevel == ProbeWarningLevel.Risky)
            {
                decision.ShouldDiscard = true;
                decision.ReasonCategory =
                    candidate.Runtime != null && !candidate.Runtime.IsValid
                        ? "unusable probe"
                        : "risky threshold";
                return decision;
            }

            if (baseline != null &&
                baseline.WarningLevel != ProbeWarningLevel.Risky &&
                candidate.OverallStabilityScore > baseline.OverallStabilityScore + 28.0)
            {
                decision.ShouldDiscard = true;
                decision.ReasonCategory = "worse than raw baseline";
                return decision;
            }

            if (currentLeader != null &&
                candidate.OverallStabilityScore > currentLeader.OverallStabilityScore + 30.0)
            {
                decision.ShouldDiscard = true;
                decision.ReasonCategory = "worse than current leader";
                return decision;
            }

            return decision;
        }

        public static ProbeReadinessResult EvaluateReadiness(
            FastPingSignalAnalyzer.FastPingAggregateSnapshot shortWindow,
            FastPingState fastPingState,
            LearnedProbeStateProfile learned,
            bool timeoutReached)
        {
            if (timeoutReached)
            {
                return new ProbeReadinessResult
                {
                    Kind = ProbeReadinessKind.TimeoutFallback,
                    Reason = "max wait reached"
                };
            }

            if (fastPingState == null || shortWindow == null || !shortWindow.IsValid)
            {
                return new ProbeReadinessResult
                {
                    Kind = ProbeReadinessKind.NotReady,
                    Reason = "not enough samples"
                };
            }

            if (fastPingState.IsStale)
            {
                return new ProbeReadinessResult
                {
                    Kind = ProbeReadinessKind.NotReady,
                    Reason = "stale"
                };
            }

            if (fastPingState.Confidence < 0.55)
            {
                return new ProbeReadinessResult
                {
                    Kind = ProbeReadinessKind.NotReady,
                    Reason = "low confidence"
                };
            }

            double avgJitter = shortWindow.AvgJitterMs;
            double p95Ping = shortWindow.P95PingMs;
            double spikeDensity = shortWindow.SpikeDensityPerMinute;
            double jitterRange = ComputeJitterRange(shortWindow);
            double effectiveLoss = EffectiveLossRate(shortWindow.AvgLossRate);

            if (learned != null && learned.HasUsefulLearning)
            {
                bool trustedLearning = learned.IsTrustedLearning;
                ServerProbeLearningStore.LearningAgeInfo age =
                    ServerProbeLearningStore.GetLearningAgeInfo(learned.LastUpdatedUtc);

                int goodMatches = 0;
                if (learned.AvgJitterMs.IsInGoodBand(avgJitter)) goodMatches++;
                if (learned.JitterRangeMs.IsInGoodBand(jitterRange)) goodMatches++;
                if (learned.P95PingMs.IsInGoodBand(p95Ping)) goodMatches++;
                if (learned.SpikeDensityPerMinute.IsInGoodBand(spikeDensity)) goodMatches++;

                int badMatches = 0;
                if (learned.AvgJitterMs.IsInBadBand(avgJitter)) badMatches++;
                if (learned.JitterRangeMs.IsInBadBand(jitterRange)) badMatches++;
                if (learned.P95PingMs.IsInBadBand(p95Ping)) badMatches++;
                if (learned.SpikeDensityPerMinute.IsInBadBand(spikeDensity)) badMatches++;

                int requiredGoodMatches =
                    trustedLearning && age.InfluenceFactor < 0.55 ? 4 :
                    3;

                int requiredBadMatches =
                    trustedLearning && age.InfluenceFactor < 0.75 ? 2 :
                    1;

                bool goodStable = goodMatches >= requiredGoodMatches;
                bool badStable = badMatches >= requiredBadMatches;

                if (goodStable)
                {
                    return new ProbeReadinessResult
                    {
                        Kind = ProbeReadinessKind.GoodStable,
                        Reason = trustedLearning
                            ? "learned good range | " + age.AgeLabel
                            : "seeded good range"
                    };
                }

                if (badStable)
                {
                    return new ProbeReadinessResult
                    {
                        Kind = ProbeReadinessKind.BadStable,
                        Reason = trustedLearning
                            ? "learned bad range | " + age.AgeLabel
                            : "seeded bad range"
                    };
                }
            }

            bool fixedGood =
                fastPingState.Confidence >= 0.65 &&
                !fastPingState.SpikeDetected &&
                fastPingState.JitterMs >= 0 &&
                fastPingState.JitterMs <= 12 &&
                shortWindow.SpikeDensityPerMinute <= 4.0 &&
                effectiveLoss <= 0.08;

            if (fixedGood)
            {
                return new ProbeReadinessResult
                {
                    Kind = ProbeReadinessKind.GoodStable,
                    Reason = "fixed good threshold"
                };
            }

            bool fixedBadStable =
                shortWindow.AvgJitterMs >= 16.0 ||
                shortWindow.P95PingMs >= 110.0 ||
                shortWindow.SpikeDensityPerMinute >= 7.0 ||
                effectiveLoss >= 0.18;

            if (fixedBadStable)
            {
                return new ProbeReadinessResult
                {
                    Kind = ProbeReadinessKind.BadStable,
                    Reason = "fixed bad threshold"
                };
            }

            return new ProbeReadinessResult
            {
                Kind = ProbeReadinessKind.NotReady,
                Reason = "still settling"
            };
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
            ProbeWinnerConfidenceResult confidence)
        {
            if (winner == null)
                return "no winner";

            string gapText = "no runner-up";
            if (runnerUp != null)
            {
                double gap = Math.Max(0.0, runnerUp.OverallStabilityScore - winner.OverallStabilityScore);
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
            LearnedProbeStateProfile learned)
        {
            ProbeWinnerConfidenceResult result = new ProbeWinnerConfidenceResult();

            if (winner == null || winner.Runtime == null)
            {
                result.Reason = "winner metrics unavailable";
                return result;
            }

            double gap = 30.0;
            if (runnerUp != null)
                gap = Math.Max(0.0, runnerUp.OverallStabilityScore - winner.OverallStabilityScore);

            result.GapToRunnerUpScore = gap;

            double gapPoints =
                gap >= 22.0 ? 45.0 :
                gap >= 14.0 ? 34.0 :
                gap >= 8.0 ? 22.0 :
                gap >= 4.0 ? 12.0 :
                4.0;

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

            double finalScore = gapPoints + samplePoints + learningPoints - stalePenalty - warningPenalty;
            finalScore = Math.Max(0.0, Math.Min(100.0, finalScore));

            result.ConfidenceScore = finalScore;

            if (finalScore >= 72.0 && gap >= 8.0 && winner.WarningLevel == ProbeWarningLevel.None)
                result.Kind = ProbeWinnerConfidenceKind.HighConfidence;
            else if (finalScore >= 45.0 && gap >= 3.0)
                result.Kind = ProbeWinnerConfidenceKind.Tentative;
            else
                result.Kind = ProbeWinnerConfidenceKind.NoClearWinner;

            string gapText = gap >= 8.0 ? "clear lead" : gap >= 3.0 ? "narrow lead" : "near tie";
            string sampleText = confidence >= 0.70 ? "good sample quality" : "limited sample quality";
            string staleText = stalePenalty > 0 ? "stale penalty applied" : "fresh samples";

            result.Reason = gapText + ", " + sampleText + ", " + learningText + ", " + staleText;

            return result;
        }

        private static ProbeWarningLevel ClassifyWarning(ProbeCandidateRuntimeData runtime)
        {
            double avgJitter = Safe(runtime.AvgJitterMs);
            double jitterRange = Safe(ComputeJitterRange(runtime));
            double p95Ping = Safe(runtime.P95PingMs);
            double spikeDensity = Safe(runtime.SpikeDensityPerMinute);
            double lossRate = EffectiveLossRate(runtime.AvgLossRate);
            double staleRate = Safe(runtime.StaleRate);
            double badRate = Safe(runtime.AvgBadQualityRate);

            bool risky =
                avgJitter >= RiskyAvgJitterMs ||
                jitterRange >= RiskyJitterRangeMs ||
                p95Ping >= RiskyP95PingMs ||
                spikeDensity >= RiskySpikeDensity ||
                lossRate >= RiskyLossRate ||
                staleRate >= RiskyStaleRate ||
                badRate >= RiskyBadQualityRate;

            if (risky)
                return ProbeWarningLevel.Risky;

            bool borderline =
                avgJitter >= BorderlineAvgJitterMs ||
                jitterRange >= BorderlineJitterRangeMs ||
                p95Ping >= BorderlineP95PingMs ||
                spikeDensity >= BorderlineSpikeDensity ||
                lossRate >= BorderlineLossRate ||
                staleRate >= BorderlineStaleRate ||
                badRate >= BorderlineBadQualityRate;

            if (borderline)
                return ProbeWarningLevel.Borderline;

            return ProbeWarningLevel.None;
        }

        private static double SpeedifyPenalty(ProbeCandidateRuntimeData runtime)
        {
            if (runtime == null || runtime.Candidate == null)
                return 0.0;

            if (runtime.Candidate.Kind == ProbeCandidateKind.Raw)
                return 0.0;

            double penalty = 0.0;

            if (!runtime.TunnelConnected)
                penalty += 25.0;

            if (runtime.AggregateCongested)
                penalty += 10.0;

            if (runtime.ActiveConnectionCount <= 0)
                penalty += 8.0;

            return penalty;
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

        private static double ComputeJitterRange(FastPingSignalAnalyzer.FastPingAggregateSnapshot snapshot)
        {
            if (snapshot == null)
                return -1;

            if (snapshot.MinJitterMs >= 0 && snapshot.MaxJitterMs >= 0)
                return Math.Max(0, snapshot.MaxJitterMs - snapshot.MinJitterMs);

            if (snapshot.AvgJitterMs >= 0 && snapshot.P95JitterMs >= 0)
                return Math.Max(0, snapshot.P95JitterMs - snapshot.AvgJitterMs);

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

            return
                warning +
                " | j=" + F(runtime.AvgJitterMs) +
                " jr=" + F(result.JitterRangeMs) +
                " p95=" + F(runtime.P95PingMs) +
                " spkD=" + F(runtime.SpikeDensityPerMinute) +
                " loss=" + F(EffectiveLossRate(runtime.AvgLossRate) * 100.0);
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

        private static string F(double value)
        {
            return value < 0 ? "--" : value.ToString("0.0");
        }
    }
}
