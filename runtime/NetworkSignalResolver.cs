using System;

namespace WTFnetApp
{
    public sealed class NetworkSignalResolver
    {
        private const int SpeedifyFreshnessMs = 4000;

        public UnifiedNetworkState Resolve(MetricsSnapshot snapshot, FastPingState fastPing)
        {
            UnifiedNetworkState state = new UnifiedNetworkState();
            state.TimeUtc = DateTime.UtcNow;

            bool hasFastPing = fastPing != null;
            bool hasSpeedify = snapshot != null;

            state.HasFastPing = hasFastPing;
            state.HasSpeedify = hasSpeedify;

            bool speedifyFresh = IsSpeedifyFresh(snapshot);
            state.SpeedifyTelemetryFresh = speedifyFresh;

            bool speedifyUsable = hasSpeedify && speedifyFresh;
            state.SpeedifyAvailable = speedifyUsable;

            if (hasSpeedify)
                ApplySpeedifyFields(state, snapshot);

            if (hasFastPing)
                ApplyFastPingFields(state, fastPing);

            if (speedifyUsable && hasFastPing)
            {
                ResolveHybrid(state, snapshot, fastPing);
            }
            else if (speedifyUsable)
            {
                ResolveSpeedifyOnly(state, snapshot);
            }
            else if (hasFastPing)
            {
                ResolveFastPingOnly(state, fastPing);
            }
            else
            {
                ResolveEmpty(state);
            }

            return state;
        }

        private static bool IsSpeedifyFresh(MetricsSnapshot snapshot)
        {
            if (snapshot == null)
                return false;

            if (snapshot.TimeUtc == DateTime.MinValue)
                return false;

            double ageMs = (DateTime.UtcNow - snapshot.TimeUtc).TotalMilliseconds;
            return ageMs >= 0 && ageMs <= SpeedifyFreshnessMs;
        }

        private static void ApplySpeedifyFields(UnifiedNetworkState state, MetricsSnapshot snapshot)
        {
            state.TunnelConnected = snapshot.TunnelConnected;
            state.AggregateCongested = snapshot.AggregateCongested;
            state.ActiveConnectionCount = snapshot.ActiveConnectionCount;
            state.BackupConnectionCount = snapshot.BackupConnectionCount;

            state.AggregateLatencyMs = snapshot.AggregateLatencyMs;
            state.AggregateJitterMs = snapshot.AggregateJitterMs;
            state.AggregateRxBps = snapshot.AggregateRxBps;
            state.AggregateTxBps = snapshot.AggregateTxBps;
            state.AggregateTotalBps = snapshot.AggregateTotalBps;

            state.ConnectedServerName = snapshot.ConnectedServerName;
            state.ConnectedServerPublicIp = snapshot.ConnectedServerPublicIp;
            state.PrimaryRemoteIp = snapshot.PrimaryRemoteIp;
            state.SpeedifyRemoteIp = snapshot.SpeedifyRemoteIp;

            state.Adapter1Latency = snapshot.Adapter1Latency;
            state.Adapter2Latency = snapshot.Adapter2Latency;

            state.RecentLatencyMs = snapshot.Recent;
            state.AverageLatencyMs = snapshot.Average;
            state.MaxLatencyMs = snapshot.Max;
            state.MicroJitterMs = snapshot.MicroJitter;
            state.DynamicBaseMs = snapshot.DynamicBase;
            state.QueueDelayMs = snapshot.QueueDelayMs;
            state.PacketLoss = snapshot.PacketLoss;
        }

        private static void ApplyFastPingFields(UnifiedNetworkState state, FastPingState fastPing)
        {
            state.FastPingPrimaryTarget = fastPing.PrimaryTarget;
            state.FastPingSecondaryTarget = fastPing.SecondaryTarget;

            state.FastPingInstantMs = fastPing.InstantPingMs;
            state.FastPingStableMs = fastPing.StablePingMs;
            state.FastPingBaselineMs = fastPing.BaselinePingMs;
            state.FastPingFloorMs = fastPing.FloorPingMs;
            state.FastPingJitterMs = fastPing.JitterMs;
            state.FastPingConfidence = fastPing.Confidence;
            state.FastPingLossRate = fastPing.LossRate;
            state.FastPingBadQualityRate = fastPing.BadQualityRate;
            state.FastPingPrimarySecondaryDivergenceMs = fastPing.PrimarySecondaryDivergenceMs;

            state.FastPingSpikeDetected = fastPing.SpikeDetected;
            state.FastPingIsStale = fastPing.IsStale;
            state.FastPingHealthState = fastPing.HealthState.ToString();
        }

        private static void ResolveEmpty(UnifiedNetworkState state)
        {
            state.SourceKind = UnifiedNetworkSourceKind.None;
            state.HealthState = UnifiedNetworkHealthState.Unknown;
            state.HealthReason = "No telemetry available";
            state.PrimaryLatencySource = "none";
            state.PrimaryJitterSource = "none";
            state.PrimaryConfidenceSource = "none";
            state.Notes = "No Speedify snapshot and no FastPing state were available.";
        }

        private static void ResolveFastPingOnly(UnifiedNetworkState state, FastPingState fastPing)
        {
            state.SourceKind = UnifiedNetworkSourceKind.FastPing;

            state.LatencyMs = ChoosePositive(fastPing.DisplayedPingMs, fastPing.StablePingMs, fastPing.InstantPingMs);
            state.JitterMs = fastPing.JitterMs;
            state.BaselineLatencyMs = fastPing.BaselinePingMs;
            state.FloorLatencyMs = fastPing.FloorPingMs;
            state.DisplayedPingMs = fastPing.DisplayedPingMs;

            state.Confidence = fastPing.Confidence;
            state.LossRate = fastPing.LossRate;
            state.BadQualityRate = fastPing.BadQualityRate;

            state.SpikeDetected = fastPing.SpikeDetected;
            state.IsStale = fastPing.IsStale;

            state.TunnelConnected = false;
            state.ActiveConnectionCount = 0;
            state.BackupConnectionCount = 0;

            state.PrimaryLatencySource = "fastping";
            state.PrimaryJitterSource = "fastping";
            state.PrimaryConfidenceSource = "fastping";

            state.HealthState = ConvertFastPingHealth(fastPing.HealthState);
            state.HealthReason = "Resolved from FastPing only";
            state.Notes = "Speedify telemetry not available; using probe-derived metrics.";
        }

        private static void ResolveSpeedifyOnly(UnifiedNetworkState state, MetricsSnapshot snapshot)
        {
            state.SourceKind = UnifiedNetworkSourceKind.Speedify;

            state.LatencyMs = ChoosePositive(snapshot.AggregateLatencyMs, snapshot.LatencyMs, snapshot.Adapter1Latency);
            state.JitterMs = ChoosePositive(snapshot.AggregateJitterMs, snapshot.Jitter, 0);
            state.BaselineLatencyMs = ChoosePositive(snapshot.BaseLatencyMs, snapshot.DynamicBase, 0);
            state.FloorLatencyMs = ChoosePositive(snapshot.DynamicBase, snapshot.BaseLatencyMs, 0);
            state.DisplayedPingMs = state.LatencyMs;

            state.Confidence = ComputeSpeedifyOnlyConfidence(snapshot);
            state.LossRate = snapshot.PacketLoss > 0 ? snapshot.PacketLoss : 0;
            state.BadQualityRate = 0;

            state.SpikeDetected = false;
            state.IsStale = false;

            state.PrimaryLatencySource = "speedify";
            state.PrimaryJitterSource = "speedify";
            state.PrimaryConfidenceSource = "speedify";

            state.HealthState = ClassifySpeedifyOnlyHealth(state, snapshot);
            state.HealthReason = "Resolved from Speedify only";
            state.Notes = "FastPing not available; using tunnel/link telemetry only.";
        }

        private static void ResolveHybrid(UnifiedNetworkState state, MetricsSnapshot snapshot, FastPingState fastPing)
        {
            state.SourceKind = UnifiedNetworkSourceKind.Hybrid;

            bool speedifyLatencyUsable = snapshot.AggregateLatencyMs > 0 || snapshot.LatencyMs > 0;
            bool fastPingUsable = fastPing.DisplayedPingMs > 0 && !fastPing.IsStale;

            if (speedifyLatencyUsable)
            {
                state.LatencyMs = ChoosePositive(snapshot.AggregateLatencyMs, snapshot.LatencyMs, fastPing.DisplayedPingMs);
                state.PrimaryLatencySource = snapshot.AggregateLatencyMs > 0 ? "speedify.aggregate" : "speedify.legacy";
            }
            else if (fastPingUsable)
            {
                state.LatencyMs = fastPing.DisplayedPingMs;
                state.PrimaryLatencySource = "fastping";
            }
            else
            {
                state.LatencyMs = ChoosePositive(snapshot.Adapter1Latency, fastPing.StablePingMs, -1);
                state.PrimaryLatencySource = "fallback";
            }

            if (snapshot.AggregateJitterMs > 0)
            {
                state.JitterMs = snapshot.AggregateJitterMs;
                state.PrimaryJitterSource = "speedify.aggregate";
            }
            else if (snapshot.Jitter > 0)
            {
                state.JitterMs = snapshot.Jitter;
                state.PrimaryJitterSource = "speedify.legacy";
            }
            else
            {
                state.JitterMs = fastPing.JitterMs;
                state.PrimaryJitterSource = "fastping";
            }

            state.BaselineLatencyMs = ChoosePositive(
                fastPing.BaselinePingMs,
                snapshot.BaseLatencyMs,
                snapshot.DynamicBase);

            state.FloorLatencyMs = ChoosePositive(
                fastPing.FloorPingMs,
                snapshot.DynamicBase,
                snapshot.BaseLatencyMs);

            state.DisplayedPingMs = fastPing.DisplayedPingMs > 0
                ? fastPing.DisplayedPingMs
                : state.LatencyMs;

            state.Confidence = ResolveHybridConfidence(snapshot, fastPing);
            state.LossRate = Math.Max(fastPing.LossRate, snapshot.PacketLoss > 0 ? snapshot.PacketLoss : 0);
            state.BadQualityRate = fastPing.BadQualityRate;

            state.SpikeDetected = fastPing.SpikeDetected;
            state.IsStale = fastPing.IsStale && !state.SpeedifyTelemetryFresh;

            state.PrimaryConfidenceSource = "hybrid";
            state.HealthState = ResolveHybridHealth(state, snapshot, fastPing);
            state.HealthReason = BuildHybridHealthReason(snapshot, fastPing, state);
            state.Notes = BuildHybridNotes(snapshot, fastPing, state);
        }

        private static double ComputeSpeedifyOnlyConfidence(MetricsSnapshot snapshot)
        {
            double confidence = 0.75;

            if (!snapshot.TunnelConnected)
                confidence -= 0.30;

            if (snapshot.AggregateCongested)
                confidence -= 0.10;

            if (snapshot.PacketLoss > 0)
                confidence -= Math.Min(0.25, snapshot.PacketLoss);

            if (snapshot.AggregateJitterMs > 15)
                confidence -= 0.10;

            if (snapshot.AggregateJitterMs > 25)
                confidence -= 0.10;

            if (confidence < 0)
                confidence = 0;
            if (confidence > 1)
                confidence = 1;

            return confidence;
        }

        private static double ResolveHybridConfidence(MetricsSnapshot snapshot, FastPingState fastPing)
        {
            double fastPingWeight = 0.60;
            double speedifyWeight = 0.40;

            double speedifyConfidence = ComputeSpeedifyOnlyConfidence(snapshot);
            double result = (fastPing.Confidence * fastPingWeight) + (speedifyConfidence * speedifyWeight);

            if (!snapshot.TunnelConnected)
                result -= 0.15;

            if (fastPing.IsStale)
                result -= 0.10;

            if (fastPing.SpikeDetected)
                result -= 0.08;

            if (result < 0)
                result = 0;
            if (result > 1)
                result = 1;

            return result;
        }

        private static UnifiedNetworkHealthState ResolveHybridHealth(
            UnifiedNetworkState state,
            MetricsSnapshot snapshot,
            FastPingState fastPing)
        {
            UnifiedNetworkHealthState fastPingHealth = ConvertFastPingHealth(fastPing.HealthState);

            if (fastPingHealth == UnifiedNetworkHealthState.Stale && !state.SpeedifyTelemetryFresh)
                return UnifiedNetworkHealthState.Stale;

            if (!snapshot.TunnelConnected && fastPingHealth >= UnifiedNetworkHealthState.Warning)
                return UnifiedNetworkHealthState.Critical;

            if (fastPingHealth == UnifiedNetworkHealthState.Critical)
                return UnifiedNetworkHealthState.Critical;

            if (fastPingHealth == UnifiedNetworkHealthState.Warning)
                return UnifiedNetworkHealthState.Warning;

            if (snapshot.AggregateCongested && fastPingHealth >= UnifiedNetworkHealthState.OffBaseline)
                return UnifiedNetworkHealthState.Warning;

            if (snapshot.AggregateJitterMs > 22 || snapshot.PacketLoss > 0.15)
                return UnifiedNetworkHealthState.Warning;

            if (fastPingHealth == UnifiedNetworkHealthState.OffBaseline)
                return UnifiedNetworkHealthState.OffBaseline;

            return UnifiedNetworkHealthState.Healthy;
        }

        private static UnifiedNetworkHealthState ClassifySpeedifyOnlyHealth(UnifiedNetworkState state, MetricsSnapshot snapshot)
        {
            if (!snapshot.TunnelConnected)
                return UnifiedNetworkHealthState.Critical;

            if (state.Confidence < 0.20)
                return UnifiedNetworkHealthState.Stale;

            if (snapshot.PacketLoss > 0.20)
                return UnifiedNetworkHealthState.Critical;

            if (snapshot.AggregateJitterMs > 22 || snapshot.AggregateCongested)
                return UnifiedNetworkHealthState.Warning;

            if (state.LatencyMs > 0 && state.BaselineLatencyMs > 0 && state.LatencyMs > state.BaselineLatencyMs + 18)
                return UnifiedNetworkHealthState.OffBaseline;

            return UnifiedNetworkHealthState.Healthy;
        }

        private static string BuildHybridHealthReason(
            MetricsSnapshot snapshot,
            FastPingState fastPing,
            UnifiedNetworkState state)
        {
            if (!snapshot.TunnelConnected)
                return "Speedify tunnel not connected";

            if (fastPing.IsStale && !state.SpeedifyTelemetryFresh)
                return "FastPing stale and Speedify telemetry not fresh";

            if (fastPing.HealthState == FastPingHealthState.Critical)
                return "FastPing classified the path as critical";

            if (fastPing.HealthState == FastPingHealthState.Warning)
                return "FastPing classified the path as warning";

            if (snapshot.AggregateCongested)
                return "Speedify reports aggregate congestion";

            if (snapshot.AggregateJitterMs > 22)
                return "Speedify aggregate jitter is elevated";

            if (fastPing.HealthState == FastPingHealthState.OffBaseline)
                return "FastPing is off baseline";

            return "Hybrid resolution normal";
        }

        private static string BuildHybridNotes(
            MetricsSnapshot snapshot,
            FastPingState fastPing,
            UnifiedNetworkState state)
        {
            string note = "Topology from Speedify; quality resolved with hybrid logic.";

            if (!snapshot.TunnelConnected)
                note += " Tunnel is down.";

            if (fastPing.PrimarySecondaryDivergenceMs > 20)
                note += " FastPing targets diverge strongly.";

            if (snapshot.ActiveConnectionCount >= 2)
                note += " Bonded multi-link active.";

            return note;
        }

        private static UnifiedNetworkHealthState ConvertFastPingHealth(FastPingHealthState value)
        {
            switch (value)
            {
                case FastPingHealthState.Healthy:
                    return UnifiedNetworkHealthState.Healthy;

                case FastPingHealthState.OffBaseline:
                    return UnifiedNetworkHealthState.OffBaseline;

                case FastPingHealthState.Warning:
                    return UnifiedNetworkHealthState.Warning;

                case FastPingHealthState.Critical:
                    return UnifiedNetworkHealthState.Critical;

                case FastPingHealthState.Stale:
                    return UnifiedNetworkHealthState.Stale;

                default:
                    return UnifiedNetworkHealthState.Unknown;
            }
        }

        private static double ChoosePositive(double first, double second, double third)
        {
            if (first > 0)
                return first;

            if (second > 0)
                return second;

            if (third > 0)
                return third;

            return -1;
        }
    }
}