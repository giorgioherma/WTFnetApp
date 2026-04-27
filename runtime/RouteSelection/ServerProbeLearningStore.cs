using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WTFnetApp
{
    public sealed class LearnedMetricBand
    {
        public double GoodMin { get; set; } = -1;
        public double GoodMax { get; set; } = -1;
        public double BadMin { get; set; } = -1;
        public double BadMax { get; set; } = -1;

        public bool HasGoodBand
        {
            get { return GoodMin >= 0 && GoodMax >= 0; }
        }

        public bool HasBadBand
        {
            get { return BadMin >= 0 && BadMax >= 0; }
        }

        public bool IsInGoodBand(double value)
        {
            if (!HasGoodBand || value < 0)
                return false;

            return value >= GoodMin && value <= GoodMax;
        }

        public bool IsInBadBand(double value)
        {
            if (!HasBadBand || value < 0)
                return false;

            return value >= BadMin && value <= BadMax;
        }

        public void SeedGood(double min, double max)
        {
            GoodMin = min;
            GoodMax = max;
        }

        public void SeedBad(double min, double max)
        {
            BadMin = min;
            BadMax = max;
        }

        public void UpdateGood(double value, double expandFactor = 0.20)
        {
            if (value < 0)
                return;

            if (!HasGoodBand)
            {
                GoodMin = value;
                GoodMax = value;
                return;
            }

            if (value < GoodMin)
            {
                double delta = GoodMin - value;
                GoodMin = value;
                GoodMax += delta * expandFactor;
            }
            else if (value > GoodMax)
            {
                double delta = value - GoodMax;
                GoodMax = value;
                GoodMin = Math.Max(0, GoodMin - (delta * expandFactor));
            }
            else
            {
                GoodMin = Math.Max(0, (GoodMin * 0.97) + (value * 0.03));
                GoodMax = (GoodMax * 0.97) + (value * 0.03);
            }
        }

        public void UpdateBad(double value, double expandFactor = 0.20)
        {
            if (value < 0)
                return;

            if (!HasBadBand)
            {
                BadMin = value;
                BadMax = value;
                return;
            }

            if (value < BadMin)
            {
                double delta = BadMin - value;
                BadMin = value;
                BadMax += delta * expandFactor;
            }
            else if (value > BadMax)
            {
                double delta = value - BadMax;
                BadMax = value;
                BadMin = Math.Max(0, BadMin - (delta * expandFactor));
            }
            else
            {
                BadMin = Math.Max(0, (BadMin * 0.97) + (value * 0.03));
                BadMax = (BadMax * 0.97) + (value * 0.03);
            }
        }
    }

    public sealed class LearnedProbeStateProfile
    {
        public string ProfileKey { get; set; }
        public string RouteKey { get; set; }
        public string TimeBucketKey { get; set; }

        public LearnedMetricBand AvgJitterMs { get; set; } = new LearnedMetricBand();
        public LearnedMetricBand JitterRangeMs { get; set; } = new LearnedMetricBand();
        public LearnedMetricBand P95PingMs { get; set; } = new LearnedMetricBand();
        public LearnedMetricBand SpikeDensityPerMinute { get; set; } = new LearnedMetricBand();
        public LearnedMetricBand AvgLossRate { get; set; } = new LearnedMetricBand();
        public LearnedMetricBand WorstSegmentLoss { get; set; } = new LearnedMetricBand();
        public LearnedMetricBand LossBurstDensity { get; set; } = new LearnedMetricBand();
        public LearnedMetricBand StaleRate { get; set; } = new LearnedMetricBand();
        public LearnedMetricBand BadTimeRatio { get; set; } = new LearnedMetricBand();
        public LearnedMetricBand AvgConfidence { get; set; } = new LearnedMetricBand();

        public int SampleCountGood { get; set; }
        public int SampleCountBad { get; set; }

        public DateTime LastUpdatedUtc { get; set; } = DateTime.MinValue;

        public bool HasUsefulLearning
        {
            get
            {
                return AvgJitterMs.HasGoodBand ||
                       AvgJitterMs.HasBadBand ||
                       JitterRangeMs.HasGoodBand ||
                       JitterRangeMs.HasBadBand ||
                       P95PingMs.HasGoodBand ||
                       P95PingMs.HasBadBand ||
                       SpikeDensityPerMinute.HasGoodBand ||
                       SpikeDensityPerMinute.HasBadBand ||
                       AvgLossRate.HasGoodBand ||
                       AvgLossRate.HasBadBand ||
                       WorstSegmentLoss.HasGoodBand ||
                       WorstSegmentLoss.HasBadBand ||
                       LossBurstDensity.HasGoodBand ||
                       LossBurstDensity.HasBadBand ||
                       StaleRate.HasGoodBand ||
                       StaleRate.HasBadBand ||
                       BadTimeRatio.HasGoodBand ||
                       BadTimeRatio.HasBadBand ||
                       AvgConfidence.HasGoodBand ||
                       AvgConfidence.HasBadBand;
            }
        }

        public bool IsTrustedLearning
        {
            get
            {
                return HasUsefulLearning &&
                       (SampleCountGood >= ServerProbeLearningStore.MinimumGoodSamples ||
                        SampleCountBad >= ServerProbeLearningStore.MinimumBadSamples);
            }
        }
    }

    public sealed class ServerProbeLearningStore
    {
        private const int PersistenceVersion = 2;

        public const int MinimumGoodSamples = 5;
        public const int MinimumBadSamples = 5;

        private readonly Dictionary<string, LearnedProbeStateProfile> profiles =
            new Dictionary<string, LearnedProbeStateProfile>(StringComparer.OrdinalIgnoreCase);

        private bool defaultsSeeded;

        public sealed class HistoricalRouteOrderingScore
        {
            public double Score { get; set; } = double.MaxValue;
            public bool IsTrusted { get; set; }
            public bool HasGoodBias { get; set; }
            public bool HasBadBias { get; set; }
            public string Reason { get; set; }

            // Direct freshness signal for startup ordering.
            // 0=fresh, 1=aging, 2=stale, 3=very-stale
            public int FreshnessRank { get; set; } = 3;
            public string FreshnessLabel { get; set; } = "very-stale";
        }

        public sealed class LearningAgeInfo
        {
            public double InfluenceFactor { get; set; } = 1.0;
            public string AgeLabel { get; set; } = "fresh";
            public int FreshnessRank { get; set; } = 0;
        }

        public enum LearningStoreLoadStatus
        {
            Loaded = 0,
            Missing = 1,
            Invalid = 2,
            Error = 3
        }

        public sealed class LearningStoreLoadResult
        {
            public LearningStoreLoadStatus Status { get; set; }
            public int ProfilesApplied { get; set; }
        }

        public sealed class LearnedLossCredibility
        {
            public double CredibilityMultiplier { get; set; } = 1.0;
            public double ProbeNoiseSuspicion { get; set; }
            public string Label { get; set; } = "neutral";
            public string Reason { get; set; } = "insufficient-evidence";
        }

        public void SeedDefaultsIfEmpty()
        {
            if (defaultsSeeded)
                return;

            SeedProfileDefaults("raw", "night",
                avgJitterGoodMax: 10, avgJitterBadMin: 15,
                jitterRangeGoodMax: 10, jitterRangeBadMin: 18,
                p95GoodMax: 75, p95BadMin: 115,
                spikeGoodMax: 3.0, spikeBadMin: 7.0);

            SeedProfileDefaults("raw", "morning",
                avgJitterGoodMax: 10, avgJitterBadMin: 15,
                jitterRangeGoodMax: 10, jitterRangeBadMin: 18,
                p95GoodMax: 75, p95BadMin: 115,
                spikeGoodMax: 3.0, spikeBadMin: 7.0);

            SeedProfileDefaults("raw", "day",
                avgJitterGoodMax: 11, avgJitterBadMin: 16,
                jitterRangeGoodMax: 11, jitterRangeBadMin: 19,
                p95GoodMax: 80, p95BadMin: 120,
                spikeGoodMax: 3.5, spikeBadMin: 7.5);

            SeedProfileDefaults("raw", "evening",
                avgJitterGoodMax: 11, avgJitterBadMin: 16,
                jitterRangeGoodMax: 11, jitterRangeBadMin: 19,
                p95GoodMax: 80, p95BadMin: 120,
                spikeGoodMax: 3.5, spikeBadMin: 7.5);

            SeedProfileDefaults("*", "night",
                avgJitterGoodMax: 9, avgJitterBadMin: 14,
                jitterRangeGoodMax: 9, jitterRangeBadMin: 17,
                p95GoodMax: 70, p95BadMin: 110,
                spikeGoodMax: 2.5, spikeBadMin: 6.5);

            SeedProfileDefaults("*", "morning",
                avgJitterGoodMax: 9, avgJitterBadMin: 14,
                jitterRangeGoodMax: 9, jitterRangeBadMin: 17,
                p95GoodMax: 70, p95BadMin: 110,
                spikeGoodMax: 2.5, spikeBadMin: 6.5);

            SeedProfileDefaults("*", "day",
                avgJitterGoodMax: 10, avgJitterBadMin: 15,
                jitterRangeGoodMax: 10, jitterRangeBadMin: 18,
                p95GoodMax: 75, p95BadMin: 115,
                spikeGoodMax: 3.0, spikeBadMin: 7.0);

            SeedProfileDefaults("*", "evening",
                avgJitterGoodMax: 10, avgJitterBadMin: 15,
                jitterRangeGoodMax: 10, jitterRangeBadMin: 18,
                p95GoodMax: 75, p95BadMin: 115,
                spikeGoodMax: 3.0, spikeBadMin: 7.0);

            defaultsSeeded = true;
        }

        public LearnedProbeStateProfile GetProfile(string routeKey, string timeBucketKey)
        {
            string normalizedRouteKey = NormalizeRouteKey(routeKey);
            string normalizedBucket = NormalizeTimeBucket(timeBucketKey);

            string exactKey = BuildProfileKey(normalizedRouteKey, normalizedBucket);

            LearnedProbeStateProfile profile;
            if (profiles.TryGetValue(exactKey, out profile))
                return profile;

            string wildcardKey = BuildProfileKey("*", normalizedBucket);
            if (profiles.TryGetValue(wildcardKey, out profile))
                return profile;

            profile = CreateEmptyProfile(normalizedRouteKey, normalizedBucket);
            profiles[exactKey] = profile;
            return profile;
        }

        public LearnedProbeStateProfile GetProfileForReadiness(string routeKey, string timeBucketKey)
        {
            LearnedProbeStateProfile profile = GetProfile(routeKey, timeBucketKey);
            if (profile != null && profile.IsTrustedLearning)
                return profile;

            return GetProfile("*", timeBucketKey);
        }

        public HistoricalRouteOrderingScore EvaluateHistoricalOrderingScore(
            string routeKey,
            string timeBucketKey)
        {
            LearnedProbeStateProfile exact = GetExactProfileIfExists(routeKey, timeBucketKey);
            LearnedProbeStateProfile wildcard = GetExactProfileIfExists("*", timeBucketKey);

            LearnedProbeStateProfile primary = exact ?? wildcard;
            if (primary == null)
            {
                return new HistoricalRouteOrderingScore
                {
                    Score = 1000.0,
                    IsTrusted = false,
                    HasGoodBias = false,
                    HasBadBias = false,
                    Reason = "no profile | neutral default",
                    FreshnessRank = 3,
                    FreshnessLabel = "very-stale"
                };
            }

            LearningAgeInfo exactAge = exact != null
                ? GetLearningAgeInfo(exact.LastUpdatedUtc)
                : null;

            LearningAgeInfo wildcardAge = wildcard != null
                ? GetLearningAgeInfo(wildcard.LastUpdatedUtc)
                : null;

            bool exactTrusted = exact != null && exact.IsTrustedLearning;
            bool wildcardTrusted = wildcard != null && wildcard.IsTrustedLearning;

            // Prefer exact trusted learning.
            // If exact exists but is weak/untrusted, blend lightly with wildcard if available.
            double exactWeight = exact != null ? 1.0 : 0.0;
            double wildcardWeight = wildcard != null ? 1.0 : 0.0;

            if (exact != null && wildcard != null)
            {
                if (exactTrusted && !wildcardTrusted)
                {
                    exactWeight = 0.82;
                    wildcardWeight = 0.18;
                }
                else if (!exactTrusted && wildcardTrusted)
                {
                    exactWeight = 0.40;
                    wildcardWeight = 0.60;
                }
                else
                {
                    exactWeight = 0.68;
                    wildcardWeight = 0.32;
                }
            }

            double exactScore = exact != null
                ? ComputeProfileOrderingScore(exact)
                : 1000.0;

            double wildcardScore = wildcard != null
                ? ComputeProfileOrderingScore(wildcard)
                : 1000.0;

            double blendedRawScore;
            if (exact != null && wildcard != null)
            {
                blendedRawScore =
                    (exactScore * exactWeight) +
                    (wildcardScore * wildcardWeight);
            }
            else
            {
                blendedRawScore = exact != null ? exactScore : wildcardScore;
            }

            int goodSamples = 0;
            int badSamples = 0;

            if (exact != null)
            {
                goodSamples += exact.SampleCountGood;
                badSamples += exact.SampleCountBad;
            }

            if (wildcard != null && !ReferenceEquals(wildcard, exact))
            {
                goodSamples += (int)Math.Round(wildcard.SampleCountGood * 0.35);
                badSamples += (int)Math.Round(wildcard.SampleCountBad * 0.35);
            }

            bool goodBias = goodSamples >= badSamples + 2;
            bool badBias = badSamples >= goodSamples + 2;

            double sampleBalanceAdjustment = 0.0;
            if (goodBias)
                sampleBalanceAdjustment -= Math.Min(8.0, (goodSamples - badSamples) * 0.85);
            else if (badBias)
                sampleBalanceAdjustment += Math.Min(10.0, (badSamples - goodSamples) * 0.95);

            bool isTrusted = exactTrusted || wildcardTrusted;

            LearningAgeInfo effectiveAge =
                exactTrusted ? exactAge :
                wildcardTrusted ? wildcardAge :
                exactAge ?? wildcardAge ?? new LearningAgeInfo
                {
                    InfluenceFactor = 0.34,
                    AgeLabel = "very-stale",
                    FreshnessRank = 3
                };

            double ageNeutralScore = 56.0;
            double scoreWithBias = blendedRawScore + sampleBalanceAdjustment;

            double finalScore =
                (scoreWithBias * effectiveAge.InfluenceFactor) +
                (ageNeutralScore * (1.0 - effectiveAge.InfluenceFactor));

            finalScore = Math.Max(0.0, finalScore);

            string sourceLabel =
                exact != null && wildcard != null ? "exact+bucket-blend" :
                exact != null ? "exact" :
                "bucket-default";

            string trustLabel =
                isTrusted ? "trusted" : "untrusted";

            string biasLabel =
                badBias ? "bad-bias" :
                goodBias ? "good-bias" :
                "mixed-bias";

            return new HistoricalRouteOrderingScore
            {
                Score = finalScore,
                IsTrusted = isTrusted,
                HasGoodBias = goodBias,
                HasBadBias = badBias,
                Reason =
                    sourceLabel +
                    " | " + trustLabel +
                    " | " + biasLabel +
                    " | " + effectiveAge.AgeLabel,
                FreshnessRank = effectiveAge.FreshnessRank,
                FreshnessLabel = effectiveAge.AgeLabel
            };
        }

        private static double ComputeProfileOrderingScore(LearnedProbeStateProfile profile)
        {
            if (profile == null)
                return 1000.0;

            // Build a robust central estimate from learned bands.
            // This is NOT an all-time average.
            // It approximates expected startup quality from the good/bad envelopes.

            double avgJitterScore = ComputeMetricOrderingScore(
                profile.AvgJitterMs,
                fallbackGoodCenter: 13.0,
                fallbackBadCenter: 22.0,
                goodWeight: 1.85,
                badWeight: 1.10);

            double jitterRangeScore = ComputeMetricOrderingScore(
                profile.JitterRangeMs,
                fallbackGoodCenter: 14.0,
                fallbackBadCenter: 24.0,
                goodWeight: 1.35,
                badWeight: 0.95);

            double p95PingScore = ComputeMetricOrderingScore(
                profile.P95PingMs,
                fallbackGoodCenter: 95.0,
                fallbackBadCenter: 145.0,
                goodWeight: 0.16,
                badWeight: 0.10);

            double spikeScore = ComputeMetricOrderingScore(
                profile.SpikeDensityPerMinute,
                fallbackGoodCenter: 5.0,
                fallbackBadCenter: 10.0,
                goodWeight: 4.20,
                badWeight: 2.40);

            double avgLossScore = ComputeMetricOrderingScore(
                profile.AvgLossRate,
                fallbackGoodCenter: 0.02,
                fallbackBadCenter: 0.14,
                goodWeight: 26.0,
                badWeight: 15.0);

            double lossBurstScore = ComputeMetricOrderingScore(
                profile.LossBurstDensity,
                fallbackGoodCenter: 0.30,
                fallbackBadCenter: 1.50,
                goodWeight: 1.85,
                badWeight: 1.10);

            double staleRateScore = ComputeMetricOrderingScore(
                profile.StaleRate,
                fallbackGoodCenter: 0.04,
                fallbackBadCenter: 0.20,
                goodWeight: 9.0,
                badWeight: 5.5);

            double badTimeScore = ComputeMetricOrderingScore(
                profile.BadTimeRatio,
                fallbackGoodCenter: 0.06,
                fallbackBadCenter: 0.25,
                goodWeight: 11.0,
                badWeight: 8.0);

            double sampleConfidenceAdjustment = ComputeSampleConfidenceAdjustment(profile);
            double bandWidthAdjustment = ComputeBandWidthAdjustment(profile);

            double total =
                avgJitterScore +
                jitterRangeScore +
                p95PingScore +
                spikeScore +
                avgLossScore +
                lossBurstScore +
                staleRateScore +
                badTimeScore +
                sampleConfidenceAdjustment +
                bandWidthAdjustment;

            return Math.Max(0.0, total);
        }

        private static double ComputeMetricOrderingScore(
            LearnedMetricBand band,
            double fallbackGoodCenter,
            double fallbackBadCenter,
            double goodWeight,
            double badWeight)
        {
            if (band == null)
                return (fallbackGoodCenter * goodWeight);

            double goodCenter = GetGoodCenter(band, fallbackGoodCenter);
            double badCenter = GetBadCenter(band, fallbackBadCenter);

            bool hasGood = band.HasGoodBand;
            bool hasBad = band.HasBadBand;

            if (hasGood && hasBad)
            {
                // Robust central estimate:
                // mostly anchored on good center,
                // but pulled toward bad pressure if bad band is close/strong.
                return (goodCenter * goodWeight) + (badCenter * badWeight * 0.40);
            }

            if (hasGood)
            {
                return goodCenter * goodWeight;
            }

            if (hasBad)
            {
                // If only bad learning exists, be conservative.
                return (fallbackGoodCenter * goodWeight * 0.75) + (badCenter * badWeight * 0.85);
            }

            return fallbackGoodCenter * goodWeight;
        }

        private static double ComputeSampleConfidenceAdjustment(LearnedProbeStateProfile profile)
        {
            if (profile == null)
                return 0.0;

            int totalSamples = profile.SampleCountGood + profile.SampleCountBad;

            if (totalSamples >= 16)
                return -4.5;
            if (totalSamples >= 10)
                return -2.5;
            if (totalSamples >= 6)
                return -1.0;
            if (totalSamples >= 3)
                return 1.0;

            return 3.0;
        }

        private static double ComputeBandWidthAdjustment(LearnedProbeStateProfile profile)
        {
            if (profile == null)
                return 0.0;

            double penalty = 0.0;

            penalty += ComputeSingleBandWidthPenalty(profile.AvgJitterMs, 0.18);
            penalty += ComputeSingleBandWidthPenalty(profile.JitterRangeMs, 0.14);
            penalty += ComputeSingleBandWidthPenalty(profile.P95PingMs, 0.03);
            penalty += ComputeSingleBandWidthPenalty(profile.SpikeDensityPerMinute, 0.50);
            penalty += ComputeSingleBandWidthPenalty(profile.AvgLossRate, 3.50);
            penalty += ComputeSingleBandWidthPenalty(profile.LossBurstDensity, 0.70);
            penalty += ComputeSingleBandWidthPenalty(profile.StaleRate, 2.20);
            penalty += ComputeSingleBandWidthPenalty(profile.BadTimeRatio, 2.50);

            return penalty;
        }

        private static double ComputeSingleBandWidthPenalty(LearnedMetricBand band, double weight)
        {
            if (band == null || !band.HasGoodBand)
                return 0.0;

            double width = Math.Max(0.0, band.GoodMax - band.GoodMin);
            return width * weight;
        }

        private static double GetGoodCenter(LearnedMetricBand band, double fallback)
        {
            if (band == null || !band.HasGoodBand)
                return fallback;

            return MedianLikeCenter(band.GoodMin, band.GoodMax);
        }

        private static double GetBadCenter(LearnedMetricBand band, double fallback)
        {
            if (band == null || !band.HasBadBand)
                return fallback;

            return MedianLikeCenter(band.BadMin, band.BadMax);
        }

        private static double MedianLikeCenter(double min, double max)
        {
            if (min < 0 && max < 0)
                return -1;
            if (min < 0)
                return max;
            if (max < 0)
                return min;

            // Robust center, not a global average.
            return (min + max) * 0.5;
        }

        public static LearningAgeInfo GetLearningAgeInfo(DateTime lastUpdatedUtc)
        {
            if (lastUpdatedUtc <= DateTime.MinValue || lastUpdatedUtc > DateTime.UtcNow.AddMinutes(2))
            {
                return new LearningAgeInfo
                {
                    InfluenceFactor = 1.0,
                    AgeLabel = "fresh",
                    FreshnessRank = 0
                };
            }

            double ageDays = (DateTime.UtcNow - lastUpdatedUtc).TotalDays;

            if (ageDays <= 3.0)
            {
                return new LearningAgeInfo
                {
                    InfluenceFactor = 1.0,
                    AgeLabel = "fresh",
                    FreshnessRank = 0
                };
            }

            if (ageDays <= 14.0)
            {
                return new LearningAgeInfo
                {
                    InfluenceFactor = 0.78,
                    AgeLabel = "aging",
                    FreshnessRank = 1
                };
            }

            if (ageDays <= 45.0)
            {
                return new LearningAgeInfo
                {
                    InfluenceFactor = 0.56,
                    AgeLabel = "stale",
                    FreshnessRank = 2
                };
            }

            return new LearningAgeInfo
            {
                InfluenceFactor = 0.34,
                AgeLabel = "very-stale",
                FreshnessRank = 3
            };
        }

        public LearningStoreLoadResult LoadFromJsonFile(string filePath)
        {
            LearningStoreLoadResult result = new LearningStoreLoadResult
            {
                Status = LearningStoreLoadStatus.Missing
            };

            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    return result;

                string json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    result.Status = LearningStoreLoadStatus.Invalid;
                    return result;
                }

                LearningStorePersistenceModel model =
                    JsonSerializer.Deserialize<LearningStorePersistenceModel>(
                        json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (model == null || model.Profiles == null)
                {
                    result.Status = LearningStoreLoadStatus.Invalid;
                    return result;
                }

                if (model.Version != 0 && model.Version != 1 && model.Version != PersistenceVersion)
                {
                    result.Status = LearningStoreLoadStatus.Invalid;
                    return result;
                }

                int applied = 0;

                foreach (LearningProfilePersistenceModel persisted in model.Profiles)
                {
                    if (persisted == null)
                        continue;

                    if (string.IsNullOrWhiteSpace(persisted.RouteKey) ||
                        string.IsNullOrWhiteSpace(persisted.TimeBucketKey))
                        continue;

                    LearnedProbeStateProfile profile =
                        GetOrCreateOwnedProfile(
                            NormalizeRouteKey(persisted.RouteKey),
                            NormalizeTimeBucket(persisted.TimeBucketKey));

                    profile.ProfileKey = BuildProfileKey(profile.RouteKey, profile.TimeBucketKey);
                    profile.SampleCountGood = Math.Max(0, persisted.SampleCountGood);
                    profile.SampleCountBad = Math.Max(0, persisted.SampleCountBad);
                    profile.LastUpdatedUtc = persisted.LastUpdatedUtc;

                    ApplyBand(profile.AvgJitterMs, persisted.AvgJitterMs);
                    ApplyBand(profile.JitterRangeMs, persisted.JitterRangeMs);
                    ApplyBand(profile.P95PingMs, persisted.P95PingMs);
                    ApplyBand(profile.SpikeDensityPerMinute, persisted.SpikeDensityPerMinute);
                    ApplyBand(profile.AvgLossRate, persisted.AvgLossRate);
                    ApplyBand(profile.WorstSegmentLoss, persisted.WorstSegmentLoss);
                    ApplyBand(profile.LossBurstDensity, persisted.LossBurstDensity);
                    ApplyBand(profile.StaleRate, persisted.StaleRate);
                    ApplyBand(profile.BadTimeRatio, persisted.BadTimeRatio);
                    ApplyBand(profile.AvgConfidence, persisted.AvgConfidence);

                    applied++;
                }

                result.Status = LearningStoreLoadStatus.Loaded;
                result.ProfilesApplied = applied;
                return result;
            }
            catch (JsonException)
            {
                result.Status = LearningStoreLoadStatus.Invalid;
                return result;
            }
            catch
            {
                result.Status = LearningStoreLoadStatus.Error;
                return result;
            }
        }

        public bool SaveToJsonFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            try
            {
                string directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                LearningStorePersistenceModel model = new LearningStorePersistenceModel
                {
                    Version = PersistenceVersion,
                    Profiles = profiles.Values
                        .OrderBy(x => x.ProfileKey, StringComparer.OrdinalIgnoreCase)
                        .Select(ToPersistenceProfile)
                        .ToList()
                };

                string json =
                    JsonSerializer.Serialize(
                        model,
                        new JsonSerializerOptions { WriteIndented = true });

                File.WriteAllText(filePath, json);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public int GetProfileCount()
        {
            return profiles.Count;
        }

        public void UpdateFromResult(ProbeScoreResult result)
        {
            if (result == null || result.Runtime == null || result.Candidate == null)
                return;

            string routeKey = NormalizeRouteKey(result.Candidate.RouteKey);
            string timeBucketKey = NormalizeTimeBucket(result.Runtime.TimeBucketKey);

            LearnedProbeStateProfile profile = GetOrCreateOwnedProfile(routeKey, timeBucketKey);

            bool isGood = IsStrongGoodResult(result);
            bool isBad = IsStrongBadResult(result);

            if (!isGood && !isBad)
            {
                profile.LastUpdatedUtc = DateTime.UtcNow;
                return;
            }

            double avgJitter = result.Runtime.AvgJitterMs;
            double jitterRange = result.JitterRangeMs;
            double p95Ping = result.Runtime.P95PingMs;
            double spikeDensity = result.Runtime.SpikeDensityPerMinute;
            double avgLossRate = result.Runtime.AvgLossRate;
            double worstSegmentLoss = result.Runtime.WorstSegmentLoss;
            double lossBurstDensity = result.Runtime.LossBurstDensity;
            double staleRate = result.Runtime.StaleRate;
            double badTimeRatio = result.Runtime.BadTimeRatio;
            double avgConfidence = result.Runtime.AvgConfidence;

            if (isGood)
            {
                profile.AvgJitterMs.UpdateGood(avgJitter);
                profile.JitterRangeMs.UpdateGood(jitterRange);
                profile.P95PingMs.UpdateGood(p95Ping);
                profile.SpikeDensityPerMinute.UpdateGood(spikeDensity);
                profile.AvgLossRate.UpdateGood(avgLossRate);
                profile.WorstSegmentLoss.UpdateGood(worstSegmentLoss);
                profile.LossBurstDensity.UpdateGood(lossBurstDensity);
                profile.StaleRate.UpdateGood(staleRate);
                profile.BadTimeRatio.UpdateGood(badTimeRatio);
                profile.AvgConfidence.UpdateGood(avgConfidence);
                profile.SampleCountGood++;
            }

            if (isBad)
            {
                profile.AvgJitterMs.UpdateBad(avgJitter);
                profile.JitterRangeMs.UpdateBad(jitterRange);
                profile.P95PingMs.UpdateBad(p95Ping);
                profile.SpikeDensityPerMinute.UpdateBad(spikeDensity);
                profile.AvgLossRate.UpdateBad(avgLossRate);
                profile.WorstSegmentLoss.UpdateBad(worstSegmentLoss);
                profile.LossBurstDensity.UpdateBad(lossBurstDensity);
                profile.StaleRate.UpdateBad(staleRate);
                profile.BadTimeRatio.UpdateBad(badTimeRatio);
                profile.AvgConfidence.UpdateBad(avgConfidence);
                profile.SampleCountBad++;
            }

            profile.LastUpdatedUtc = DateTime.UtcNow;
        }

        private static bool IsStrongGoodResult(ProbeScoreResult result)
        {
            if (result == null || result.Runtime == null)
                return false;

            if (result.IsDiscardedEarly)
                return false;

            if (result.WarningLevel != ProbeWarningLevel.None)
                return false;

            if (!result.Runtime.StabilizationReached || result.Runtime.StabilizationTimedOut)
                return false;

            double confidence = result.Runtime.AvgConfidence;
            if (confidence >= 0 && confidence < 0.70)
                return false;

            return true;
        }

        private static bool IsStrongBadResult(ProbeScoreResult result)
        {
            if (result == null || result.Runtime == null)
                return false;

            if (result.IsDiscardedEarly || result.WarningLevel == ProbeWarningLevel.Risky)
                return true;

            if (result.Runtime.StabilizationTimedOut && result.WarningLevel != ProbeWarningLevel.None)
                return true;

            return false;
        }

        public void UpdateFromReadinessSnapshot(
            string routeKey,
            string timeBucketKey,
            FastPingSignalAnalyzer.FastPingAggregateSnapshot snapshot,
            ProbeReadinessKind readinessKind)
        {
            if (snapshot == null)
                return;

            LearnedProbeStateProfile profile =
                GetOrCreateOwnedProfile(NormalizeRouteKey(routeKey), NormalizeTimeBucket(timeBucketKey));

            double avgJitter = snapshot.AvgJitterMs;
            double jitterRange = ComputeJitterRange(snapshot);
            double p95Ping = snapshot.P95PingMs;
            double spikeDensity = snapshot.SpikeDensityPerMinute;
            double avgLossRate = snapshot.AvgLossRate;
            double worstSegmentLoss = snapshot.WorstSegmentLoss;
            double lossBurstDensity = snapshot.LossBurstDensity;
            double staleRate = snapshot.StaleRate;
            double badTimeRatio = snapshot.BadTimeRatio;
            double avgConfidence = snapshot.AvgConfidence;

            if (readinessKind == ProbeReadinessKind.GoodStable)
            {
                profile.AvgJitterMs.UpdateGood(avgJitter);
                profile.JitterRangeMs.UpdateGood(jitterRange);
                profile.P95PingMs.UpdateGood(p95Ping);
                profile.SpikeDensityPerMinute.UpdateGood(spikeDensity);
                profile.AvgLossRate.UpdateGood(avgLossRate);
                profile.WorstSegmentLoss.UpdateGood(worstSegmentLoss);
                profile.LossBurstDensity.UpdateGood(lossBurstDensity);
                profile.StaleRate.UpdateGood(staleRate);
                profile.BadTimeRatio.UpdateGood(badTimeRatio);
                profile.AvgConfidence.UpdateGood(avgConfidence);
                profile.SampleCountGood++;
            }
            else if (readinessKind == ProbeReadinessKind.BadStable)
            {
                profile.AvgJitterMs.UpdateBad(avgJitter);
                profile.JitterRangeMs.UpdateBad(jitterRange);
                profile.P95PingMs.UpdateBad(p95Ping);
                profile.SpikeDensityPerMinute.UpdateBad(spikeDensity);
                profile.AvgLossRate.UpdateBad(avgLossRate);
                profile.WorstSegmentLoss.UpdateBad(worstSegmentLoss);
                profile.LossBurstDensity.UpdateBad(lossBurstDensity);
                profile.StaleRate.UpdateBad(staleRate);
                profile.BadTimeRatio.UpdateBad(badTimeRatio);
                profile.AvgConfidence.UpdateBad(avgConfidence);
                profile.SampleCountBad++;
            }

            profile.LastUpdatedUtc = DateTime.UtcNow;
        }

        private void SeedProfileDefaults(
            string routeKey,
            string timeBucketKey,
            double avgJitterGoodMax,
            double avgJitterBadMin,
            double jitterRangeGoodMax,
            double jitterRangeBadMin,
            double p95GoodMax,
            double p95BadMin,
            double spikeGoodMax,
            double spikeBadMin)
        {
            LearnedProbeStateProfile profile = GetOrCreateOwnedProfile(routeKey, timeBucketKey);

            if (!profile.AvgJitterMs.HasGoodBand)
                profile.AvgJitterMs.SeedGood(0, avgJitterGoodMax);

            if (!profile.AvgJitterMs.HasBadBand)
                profile.AvgJitterMs.SeedBad(avgJitterBadMin, 999);

            if (!profile.JitterRangeMs.HasGoodBand)
                profile.JitterRangeMs.SeedGood(0, jitterRangeGoodMax);

            if (!profile.JitterRangeMs.HasBadBand)
                profile.JitterRangeMs.SeedBad(jitterRangeBadMin, 999);

            if (!profile.P95PingMs.HasGoodBand)
                profile.P95PingMs.SeedGood(0, p95GoodMax);

            if (!profile.P95PingMs.HasBadBand)
                profile.P95PingMs.SeedBad(p95BadMin, 999);

            if (!profile.SpikeDensityPerMinute.HasGoodBand)
                profile.SpikeDensityPerMinute.SeedGood(0, spikeGoodMax);

            if (!profile.SpikeDensityPerMinute.HasBadBand)
                profile.SpikeDensityPerMinute.SeedBad(spikeBadMin, 999);

            if (!profile.AvgLossRate.HasGoodBand)
                profile.AvgLossRate.SeedGood(0.0, 0.025);

            if (!profile.AvgLossRate.HasBadBand)
                profile.AvgLossRate.SeedBad(0.12, 1.0);

            if (!profile.WorstSegmentLoss.HasGoodBand)
                profile.WorstSegmentLoss.SeedGood(0.0, 0.09);

            if (!profile.WorstSegmentLoss.HasBadBand)
                profile.WorstSegmentLoss.SeedBad(0.22, 1.0);

            if (!profile.LossBurstDensity.HasGoodBand)
                profile.LossBurstDensity.SeedGood(0.0, 0.70);

            if (!profile.LossBurstDensity.HasBadBand)
                profile.LossBurstDensity.SeedBad(1.45, 10.0);

            if (!profile.StaleRate.HasGoodBand)
                profile.StaleRate.SeedGood(0.0, 0.05);

            if (!profile.StaleRate.HasBadBand)
                profile.StaleRate.SeedBad(0.18, 1.0);

            if (!profile.BadTimeRatio.HasGoodBand)
                profile.BadTimeRatio.SeedGood(0.0, 0.08);

            if (!profile.BadTimeRatio.HasBadBand)
                profile.BadTimeRatio.SeedBad(0.20, 1.0);

            if (!profile.AvgConfidence.HasGoodBand)
                profile.AvgConfidence.SeedGood(0.78, 1.0);

            if (!profile.AvgConfidence.HasBadBand)
                profile.AvgConfidence.SeedBad(0.0, 0.45);
        }

        public LearnedLossCredibility EvaluateLossCredibility(
            string routeKey,
            string timeBucketKey,
            ProbeCandidateRuntimeData runtime)
        {
            LearnedLossCredibility output = new LearnedLossCredibility();
            if (runtime == null)
            {
                output.Label = "neutral";
                output.Reason = "runtime-missing";
                return output;
            }

            LearnedProbeStateProfile profile = GetProfileForReadiness(routeKey, timeBucketKey);
            List<string> reasons = new List<string>();

            double credibility = 1.0;
            double noiseSuspicion = 0.0;

            double runtimeLoss = runtime.AvgLossRate;
            bool lossHigh =
                runtimeLoss >= 0.10 ||
                runtime.WorstSegmentLoss >= 0.18 ||
                runtime.LossBurstDensity >= 1.15 ||
                runtime.BadTimeRatio >= 0.16;

            int otherGoodSignals = 0;
            int otherBadSignals = 0;

            CountSignal(runtime.AvgJitterMs, 12.0, 16.0, ref otherGoodSignals, ref otherBadSignals);
            CountSignal(runtime.P95PingMs, 95.0, 130.0, ref otherGoodSignals, ref otherBadSignals);
            CountSignal(runtime.SpikeDensityPerMinute, 4.0, 8.0, ref otherGoodSignals, ref otherBadSignals);
            CountSignal(runtime.StaleRate, 0.06, 0.15, ref otherGoodSignals, ref otherBadSignals);
            CountSignal(runtime.BadTimeRatio, 0.08, 0.18, ref otherGoodSignals, ref otherBadSignals);

            if (lossHigh && otherGoodSignals >= 4 && otherBadSignals == 0)
            {
                credibility *= 0.58;
                noiseSuspicion += 0.30;
                reasons.Add("high-loss while jitter/p95/spikes/stale/bad-time remain good");
            }
            else if (lossHigh && otherBadSignals >= 3)
            {
                credibility *= 1.20;
                reasons.Add("high-loss aligned with degraded jitter/p95/spikes/stale/bad-time");
            }

            if (lossHigh && profile != null && profile.HasUsefulLearning)
            {
                bool lossInGoodBand = profile.AvgLossRate.IsInGoodBand(runtimeLoss);
                bool lossInBadBand = profile.AvgLossRate.IsInBadBand(runtimeLoss);
                bool lossOutsideBadBand =
                    profile.AvgLossRate.HasBadBand &&
                    runtimeLoss >= 0 &&
                    runtimeLoss > (profile.AvgLossRate.BadMax + 0.02);

                bool learnedGoodNonLoss =
                    profile.StaleRate.IsInGoodBand(runtime.StaleRate) &&
                    profile.BadTimeRatio.IsInGoodBand(runtime.BadTimeRatio);

                if (lossInGoodBand && learnedGoodNonLoss)
                {
                    credibility *= 0.78;
                    noiseSuspicion += 0.34;
                    reasons.Add("learned profile often tolerates similar loss with stable stale/bad-time");
                }

                if (lossInBadBand)
                {
                    credibility *= 1.10;
                    reasons.Add("runtime loss sits in learned bad band");
                }

                if (lossOutsideBadBand && otherBadSignals >= 2)
                {
                    credibility *= 1.15;
                    reasons.Add("runtime loss exceeds learned bad range while other instability is elevated");
                }
            }

            credibility = Clamp(credibility, 0.40, 1.35);
            noiseSuspicion = Clamp(noiseSuspicion, 0.0, 1.0);

            string label =
                credibility <= 0.75 ? "likely-probe-noise" :
                credibility >= 1.12 ? "likely-real-loss" :
                "mixed";

            output.CredibilityMultiplier = credibility;
            output.ProbeNoiseSuspicion = noiseSuspicion;
            output.Label = label;
            output.Reason = reasons.Count > 0
                ? string.Join(" | ", reasons)
                : "loss credibility remains neutral";

            return output;
        }

        private LearnedProbeStateProfile GetOrCreateOwnedProfile(string routeKey, string timeBucketKey)
        {
            string profileKey = BuildProfileKey(routeKey, timeBucketKey);

            LearnedProbeStateProfile profile;
            if (profiles.TryGetValue(profileKey, out profile))
                return profile;

            profile = CreateEmptyProfile(routeKey, timeBucketKey);
            profiles[profileKey] = profile;
            return profile;
        }

        private LearnedProbeStateProfile GetExactProfileIfExists(string routeKey, string timeBucketKey)
        {
            string profileKey = BuildProfileKey(routeKey, timeBucketKey);

            LearnedProbeStateProfile profile;
            if (profiles.TryGetValue(profileKey, out profile))
                return profile;

            return null;
        }

        private static LearnedProbeStateProfile CreateEmptyProfile(string routeKey, string timeBucketKey)
        {
            return new LearnedProbeStateProfile
            {
                ProfileKey = BuildProfileKey(routeKey, timeBucketKey),
                RouteKey = routeKey,
                TimeBucketKey = timeBucketKey
            };
        }

        private static string BuildProfileKey(string routeKey, string timeBucketKey)
        {
            return NormalizeRouteKey(routeKey) + "|" + NormalizeTimeBucket(timeBucketKey);
        }

        private static string NormalizeRouteKey(string routeKey)
        {
            if (string.IsNullOrWhiteSpace(routeKey))
                return "raw";

            return routeKey.Trim().ToLowerInvariant();
        }

        private static string NormalizeTimeBucket(string timeBucketKey)
        {
            if (string.IsNullOrWhiteSpace(timeBucketKey))
                return "day";

            return timeBucketKey.Trim().ToLowerInvariant();
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

        private static void ApplyBand(LearnedMetricBand target, LearnedMetricBandPersistenceModel source)
        {
            if (target == null || source == null)
                return;

            target.GoodMin = source.GoodMin;
            target.GoodMax = source.GoodMax;
            target.BadMin = source.BadMin;
            target.BadMax = source.BadMax;
        }

        private static LearnedMetricBandPersistenceModel ToPersistenceBand(LearnedMetricBand band)
        {
            if (band == null)
                return new LearnedMetricBandPersistenceModel();

            return new LearnedMetricBandPersistenceModel
            {
                GoodMin = band.GoodMin,
                GoodMax = band.GoodMax,
                BadMin = band.BadMin,
                BadMax = band.BadMax
            };
        }

        private static LearningProfilePersistenceModel ToPersistenceProfile(LearnedProbeStateProfile profile)
        {
            if (profile == null)
                return null;

            return new LearningProfilePersistenceModel
            {
                ProfileKey = profile.ProfileKey,
                RouteKey = profile.RouteKey,
                TimeBucketKey = profile.TimeBucketKey,
                SampleCountGood = profile.SampleCountGood,
                SampleCountBad = profile.SampleCountBad,
                LastUpdatedUtc = profile.LastUpdatedUtc,
                AvgJitterMs = ToPersistenceBand(profile.AvgJitterMs),
                JitterRangeMs = ToPersistenceBand(profile.JitterRangeMs),
                P95PingMs = ToPersistenceBand(profile.P95PingMs),
                SpikeDensityPerMinute = ToPersistenceBand(profile.SpikeDensityPerMinute),
                AvgLossRate = ToPersistenceBand(profile.AvgLossRate),
                WorstSegmentLoss = ToPersistenceBand(profile.WorstSegmentLoss),
                LossBurstDensity = ToPersistenceBand(profile.LossBurstDensity),
                StaleRate = ToPersistenceBand(profile.StaleRate),
                BadTimeRatio = ToPersistenceBand(profile.BadTimeRatio),
                AvgConfidence = ToPersistenceBand(profile.AvgConfidence)
            };
        }

        private static void CountSignal(
            double value,
            double goodMax,
            double badMin,
            ref int goodCount,
            ref int badCount)
        {
            if (value < 0)
                return;

            if (value <= goodMax)
                goodCount++;
            else if (value >= badMin)
                badCount++;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private sealed class LearningStorePersistenceModel
        {
            public int Version { get; set; }
            public List<LearningProfilePersistenceModel> Profiles { get; set; } = new List<LearningProfilePersistenceModel>();
        }

        private sealed class LearningProfilePersistenceModel
        {
            public string ProfileKey { get; set; }
            public string RouteKey { get; set; }
            public string TimeBucketKey { get; set; }
            public int SampleCountGood { get; set; }
            public int SampleCountBad { get; set; }
            public DateTime LastUpdatedUtc { get; set; }
            public LearnedMetricBandPersistenceModel AvgJitterMs { get; set; } = new LearnedMetricBandPersistenceModel();
            public LearnedMetricBandPersistenceModel JitterRangeMs { get; set; } = new LearnedMetricBandPersistenceModel();
            public LearnedMetricBandPersistenceModel P95PingMs { get; set; } = new LearnedMetricBandPersistenceModel();
            public LearnedMetricBandPersistenceModel SpikeDensityPerMinute { get; set; } = new LearnedMetricBandPersistenceModel();
            public LearnedMetricBandPersistenceModel AvgLossRate { get; set; } = new LearnedMetricBandPersistenceModel();
            public LearnedMetricBandPersistenceModel WorstSegmentLoss { get; set; } = new LearnedMetricBandPersistenceModel();
            public LearnedMetricBandPersistenceModel LossBurstDensity { get; set; } = new LearnedMetricBandPersistenceModel();
            public LearnedMetricBandPersistenceModel StaleRate { get; set; } = new LearnedMetricBandPersistenceModel();
            public LearnedMetricBandPersistenceModel BadTimeRatio { get; set; } = new LearnedMetricBandPersistenceModel();
            public LearnedMetricBandPersistenceModel AvgConfidence { get; set; } = new LearnedMetricBandPersistenceModel();
        }

        private sealed class LearnedMetricBandPersistenceModel
        {
            public double GoodMin { get; set; } = -1;
            public double GoodMax { get; set; } = -1;
            public double BadMin { get; set; } = -1;
            public double BadMax { get; set; } = -1;
        }
    }
}
