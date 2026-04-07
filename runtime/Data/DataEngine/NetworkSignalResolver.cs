using System;

namespace WTFnetApp
{
    public sealed class NetworkSignalResolver
    {
        private const int SpeedifyFreshnessMs = 1500;

        public UnifiedNetworkState Resolve(MetricsSnapshot snapshot, FastPingState fastPing, NatState natState)
        {
            UnifiedNetworkState state = new UnifiedNetworkState();
            state.TimeUtc = DateTime.UtcNow;

            bool hasFastPing = fastPing != null;
            bool hasSpeedify = snapshot != null;

            state.HasFastPing = hasFastPing;
            state.HasSpeedify = hasSpeedify;

            bool speedifyFresh = IsSpeedifyFresh(snapshot);
            state.SpeedifyTelemetryFresh = speedifyFresh;

            bool speedifyUsable =
                hasSpeedify &&
                speedifyFresh &&
                snapshot.TunnelConnected &&
                ChoosePositive(snapshot.AggregateLatencyMs, snapshot.LatencyMs, snapshot.Adapter1Latency) > 0;
            state.SpeedifyAvailable = speedifyUsable;

            if (hasSpeedify)
                ApplySpeedifyFields(state, snapshot);

            if (hasFastPing)
                ApplyFastPingFields(state, fastPing);

            if (speedifyUsable)
            {
                state.SourceKind = UnifiedNetworkSourceKind.Speedify;

                NetworkMetricsCalculator.ResolveFromSpeedify(state, snapshot);
                NetworkQualityEvaluator.ClassifySpeedify(state, snapshot);

                state.HealthReason = "Resolved from Speedify only";
                state.Notes = "FastPing ignored while fresh Speedify telemetry is available.";
            }
            else if (hasFastPing)
            {
                state.SourceKind = UnifiedNetworkSourceKind.FastPing;

                NetworkMetricsCalculator.ResolveFromFastPing(state, fastPing);
                NetworkQualityEvaluator.ClassifyFastPing(state);

                state.HealthReason = "Resolved from FastPing only";
                state.Notes = "Speedify telemetry unavailable or stale; using probe-derived metrics only.";
            }
            else
            {
                ResolveEmpty(state);
            }

            state.JitterPressure = ComputeJitterPressure(state.JitterMs);
            state.RefreshDisplayState(natState);

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
