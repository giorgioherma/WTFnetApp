using System;

namespace WTFnetApp
{
    public sealed class NetworkSignalResolver
    {
        private const int SpeedifyFreshnessMs = 1500;

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

            if (speedifyUsable)
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

            state.JitterPressure = ComputeJitterPressure(state.JitterMs);
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

        private static UnifiedNetworkHealthState ClassifyFastPingOnlyHealth(UnifiedNetworkState state)
        {
            if (state.IsStale)
                return UnifiedNetworkHealthState.Stale;

            if (state.LatencyMs <= 0)
                return UnifiedNetworkHealthState.Unknown;

            if (state.LossRate > 0.25)
                return UnifiedNetworkHealthState.Critical;

            if (state.LatencyMs > 180 || state.JitterMs > 50)
                return UnifiedNetworkHealthState.Critical;

            if (state.LatencyMs > 110 || state.JitterMs > 30 || state.BadQualityRate > 0.35)
                return UnifiedNetworkHealthState.Warning;

            if (state.BaselineLatencyMs > 0 && state.LatencyMs > state.BaselineLatencyMs + 25)
                return UnifiedNetworkHealthState.OffBaseline;

            return UnifiedNetworkHealthState.Healthy;
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

            double resolvedPing = ChoosePositive(
                fastPing.DisplayedPingMs,
                fastPing.StablePingMs,
                fastPing.InstantPingMs);

            state.LatencyMs = resolvedPing;
            state.JitterMs = fastPing.JitterMs;
            state.BaselineLatencyMs = fastPing.BaselinePingMs;
            state.FloorLatencyMs = fastPing.FloorPingMs;
            state.DisplayedPingMs = resolvedPing;

            state.Confidence = fastPing.Confidence;
            state.LossRate = fastPing.LossRate;
            state.BadQualityRate = fastPing.BadQualityRate;

            state.SpikeDetected = fastPing.SpikeDetected;
            state.IsStale = fastPing.IsStale && resolvedPing <= 0;

            state.TunnelConnected = false;
            state.ActiveConnectionCount = 0;
            state.BackupConnectionCount = 0;

            state.PrimaryLatencySource = "fastping";
            state.PrimaryJitterSource = "fastping";
            state.PrimaryConfidenceSource = "fastping";

            state.HealthState = ClassifyFastPingOnlyHealth(state);
            state.HealthReason = "Resolved from FastPing only";
            state.Notes = "Speedify telemetry unavailable or stale; using probe-derived metrics only.";
        }

        private static void ResolveSpeedifyOnly(UnifiedNetworkState state, MetricsSnapshot snapshot)
        {
            state.SourceKind = UnifiedNetworkSourceKind.Speedify;

            state.LatencyMs = ChoosePositive(
                snapshot.AggregateLatencyMs,
                snapshot.LatencyMs,
                snapshot.Adapter1Latency);

            state.JitterMs = ChoosePositive(
                snapshot.AggregateJitterMs,
                snapshot.Jitter,
                snapshot.MicroJitter);

            state.PrimaryJitterSource =
                snapshot.AggregateJitterMs > 0 ? "speedify.aggregate" :
                snapshot.Jitter > 0 ? "speedify.legacy" :
                snapshot.MicroJitter > 0 ? "speedify.micro" :
                "none";

            state.BaselineLatencyMs = ChoosePositive(
                snapshot.BaseLatencyMs,
                snapshot.DynamicBase,
                0);

            state.FloorLatencyMs = ChoosePositive(
                snapshot.DynamicBase,
                snapshot.BaseLatencyMs,
                0);

            state.DisplayedPingMs = state.LatencyMs;

            state.Confidence = ComputeSpeedifyOnlyConfidence(snapshot);
            state.LossRate = snapshot.PacketLoss > 0 ? snapshot.PacketLoss : 0;
            state.BadQualityRate = 0;

            state.SpikeDetected = false;
            state.IsStale = false;

            state.PrimaryLatencySource = "speedify";
            state.PrimaryConfidenceSource = "speedify";

            state.HealthState = ClassifySpeedifyOnlyHealth(state, snapshot);
            state.HealthReason = "Resolved from Speedify only";
            state.Notes = "FastPing ignored while fresh Speedify telemetry is available.";
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

            double jitter = ChoosePositive(snapshot.AggregateJitterMs, snapshot.Jitter, snapshot.MicroJitter);

            if (jitter > 15)
                confidence -= 0.10;

            if (jitter > 25)
                confidence -= 0.10;

            if (confidence < 0)
                confidence = 0;
            if (confidence > 1)
                confidence = 1;

            return confidence;
        }

        private static UnifiedNetworkHealthState ClassifySpeedifyOnlyHealth(UnifiedNetworkState state, MetricsSnapshot snapshot)
        {
            if (!snapshot.TunnelConnected)
                return UnifiedNetworkHealthState.Critical;

            if (state.Confidence < 0.20)
                return UnifiedNetworkHealthState.Stale;

            if (snapshot.PacketLoss > 0.20)
                return UnifiedNetworkHealthState.Critical;

            if (state.JitterMs > 22 || snapshot.AggregateCongested)
                return UnifiedNetworkHealthState.Warning;

            if (state.LatencyMs > 0 &&
                state.BaselineLatencyMs > 0 &&
                state.LatencyMs > state.BaselineLatencyMs + 18)
                return UnifiedNetworkHealthState.OffBaseline;

            return UnifiedNetworkHealthState.Healthy;
        }

        private static double ComputeJitterPressure(double jitterMs)
        {
            if (jitterMs <= 0)
                return 0.0;

            if (jitterMs <= 4)
                return 0.0;

            if (jitterMs <= 8)
                return 0.15;

            if (jitterMs <= 12)
                return 0.35;

            if (jitterMs <= 18)
                return 0.60;

            if (jitterMs <= 28)
                return 0.82;

            return 1.0;
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