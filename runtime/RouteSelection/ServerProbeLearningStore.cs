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
                       SpikeDensityPerMinute.HasBadBand;
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
        private const int PersistenceVersion = 1;

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
        }

        public sealed class LearningAgeInfo
        {
            public double InfluenceFactor { get; set; } = 1.0;
            public string AgeLabel { get; set; } = "fresh";
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
            LearnedProbeStateProfile profile = GetExactProfileIfExists(routeKey, timeBucketKey);

            if (profile == null || !profile.IsTrustedLearning)
            {
                return new HistoricalRouteOrderingScore
                {
                    Score = 1000.0,
                    IsTrusted = false,
                    Reason = "default order | untrusted profile"
                };
            }

            LearningAgeInfo age = GetLearningAgeInfo(profile.LastUpdatedUtc);

            int goodSamples = profile.SampleCountGood;
            int badSamples = profile.SampleCountBad;

            bool goodBias = goodSamples >= badSamples + 2;
            bool badBias = badSamples >= goodSamples + 2;

            double avgJitterGoodMax = profile.AvgJitterMs.HasGoodBand ? profile.AvgJitterMs.GoodMax : 14.0;
            double jitterRangeGoodMax = profile.JitterRangeMs.HasGoodBand ? profile.JitterRangeMs.GoodMax : 16.0;
            double p95GoodMax = profile.P95PingMs.HasGoodBand ? profile.P95PingMs.GoodMax : 110.0;
            double spikeGoodMax = profile.SpikeDensityPerMinute.HasGoodBand ? profile.SpikeDensityPerMinute.GoodMax : 7.0;

            double score =
                (avgJitterGoodMax * 1.80) +
                (jitterRangeGoodMax * 1.20) +
                (p95GoodMax * 0.12) +
                (spikeGoodMax * 4.00);

            score += Math.Min(16.0, badSamples * 0.90);
            score -= Math.Min(10.0, goodSamples * 0.65);

            if (badBias)
                score += 10.0;
            else if (goodBias)
                score -= 5.0;

            // Conservative aging: move old learning toward neutral ordering pressure.
            const double neutralScore = 56.0;
            score = (score * age.InfluenceFactor) + (neutralScore * (1.0 - age.InfluenceFactor));

            score = Math.Max(0.0, score);

            string reason =
                badBias ? "trusted bad profile" :
                goodBias ? "trusted good profile" :
                "trusted mixed profile";

            if (age.AgeLabel != "fresh")
                reason += " | " + age.AgeLabel + " influence reduced";
            else
                reason += " | fresh";

            return new HistoricalRouteOrderingScore
            {
                Score = score,
                IsTrusted = true,
                HasGoodBias = goodBias,
                HasBadBias = badBias,
                Reason = reason
            };
        }

        public static LearningAgeInfo GetLearningAgeInfo(DateTime lastUpdatedUtc)
        {
            if (lastUpdatedUtc <= DateTime.MinValue || lastUpdatedUtc > DateTime.UtcNow.AddMinutes(2))
            {
                return new LearningAgeInfo
                {
                    InfluenceFactor = 1.0,
                    AgeLabel = "fresh"
                };
            }

            double ageDays = (DateTime.UtcNow - lastUpdatedUtc).TotalDays;

            if (ageDays <= 3.0)
            {
                return new LearningAgeInfo
                {
                    InfluenceFactor = 1.0,
                    AgeLabel = "fresh"
                };
            }

            if (ageDays <= 14.0)
            {
                return new LearningAgeInfo
                {
                    InfluenceFactor = 0.75,
                    AgeLabel = "aging"
                };
            }

            if (ageDays <= 45.0)
            {
                return new LearningAgeInfo
                {
                    InfluenceFactor = 0.55,
                    AgeLabel = "stale"
                };
            }

            return new LearningAgeInfo
            {
                InfluenceFactor = 0.35,
                AgeLabel = "very-stale"
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

                if (model.Version != 0 && model.Version != PersistenceVersion)
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

            if (isGood)
            {
                profile.AvgJitterMs.UpdateGood(avgJitter);
                profile.JitterRangeMs.UpdateGood(jitterRange);
                profile.P95PingMs.UpdateGood(p95Ping);
                profile.SpikeDensityPerMinute.UpdateGood(spikeDensity);
                profile.SampleCountGood++;
            }

            if (isBad)
            {
                profile.AvgJitterMs.UpdateBad(avgJitter);
                profile.JitterRangeMs.UpdateBad(jitterRange);
                profile.P95PingMs.UpdateBad(p95Ping);
                profile.SpikeDensityPerMinute.UpdateBad(spikeDensity);
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

            if (readinessKind == ProbeReadinessKind.GoodStable)
            {
                profile.AvgJitterMs.UpdateGood(avgJitter);
                profile.JitterRangeMs.UpdateGood(jitterRange);
                profile.P95PingMs.UpdateGood(p95Ping);
                profile.SpikeDensityPerMinute.UpdateGood(spikeDensity);
                profile.SampleCountGood++;
            }
            else if (readinessKind == ProbeReadinessKind.BadStable)
            {
                profile.AvgJitterMs.UpdateBad(avgJitter);
                profile.JitterRangeMs.UpdateBad(jitterRange);
                profile.P95PingMs.UpdateBad(p95Ping);
                profile.SpikeDensityPerMinute.UpdateBad(spikeDensity);
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
                SpikeDensityPerMinute = ToPersistenceBand(profile.SpikeDensityPerMinute)
            };
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
