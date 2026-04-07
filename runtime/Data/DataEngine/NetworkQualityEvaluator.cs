using System;

namespace WTFnetApp
{
    public static class NetworkQualityEvaluator
    {
        private const double CriticalLoss = 0.25;
        private const double CriticalLatency = 180;
        private const double WarningLatency = 110;
        private const double CriticalJitter = 50;
        private const double WarningJitter = 30;
        private const double OffBaselineDelta = 25;

        public static void ClassifyFastPing(UnifiedNetworkState state)
        {
            if (state.IsStale)
            {
                state.HealthState = UnifiedNetworkHealthState.Stale;
                return;
            }

            if (state.LatencyMs <= 0)
            {
                state.HealthState = UnifiedNetworkHealthState.Unknown;
                return;
            }

            if (state.LossRate > CriticalLoss)
            {
                state.HealthState = UnifiedNetworkHealthState.Critical;
                return;
            }

            if (state.LatencyMs > CriticalLatency || state.JitterMs > CriticalJitter)
            {
                state.HealthState = UnifiedNetworkHealthState.Critical;
                return;
            }

            if (state.LatencyMs > WarningLatency || state.JitterMs > WarningJitter || state.BadQualityRate > 0.35)
            {
                state.HealthState = UnifiedNetworkHealthState.Warning;
                return;
            }

            if (state.BaselineLatencyMs > 0 && state.LatencyMs > state.BaselineLatencyMs + OffBaselineDelta)
            {
                state.HealthState = UnifiedNetworkHealthState.OffBaseline;
                return;
            }

            state.HealthState = UnifiedNetworkHealthState.Healthy;
        }

        public static void ClassifySpeedify(UnifiedNetworkState state, MetricsSnapshot snapshot)
        {
            state.Confidence = ComputeConfidence(snapshot);

            if (!snapshot.TunnelConnected)
            {
                state.HealthState = UnifiedNetworkHealthState.Critical;
                return;
            }

            if (state.Confidence < 0.20)
            {
                state.HealthState = UnifiedNetworkHealthState.Stale;
                return;
            }

            if (snapshot.PacketLoss > 0.20)
            {
                state.HealthState = UnifiedNetworkHealthState.Critical;
                return;
            }

            if (state.JitterMs > 22 || snapshot.AggregateCongested)
            {
                state.HealthState = UnifiedNetworkHealthState.Warning;
                return;
            }

            if (state.LatencyMs > 0 &&
                state.BaselineLatencyMs > 0 &&
                state.LatencyMs > state.BaselineLatencyMs + 18)
            {
                state.HealthState = UnifiedNetworkHealthState.OffBaseline;
                return;
            }

            state.HealthState = UnifiedNetworkHealthState.Healthy;
        }

        private static double ComputeConfidence(MetricsSnapshot snapshot)
        {
            double confidence = 0.75;

            if (!snapshot.TunnelConnected)
                confidence -= 0.30;

            if (snapshot.AggregateCongested)
                confidence -= 0.10;

            if (snapshot.PacketLoss > 0)
                confidence -= Math.Min(0.25, snapshot.PacketLoss);

            double jitter = snapshot.AggregateJitterMs > 0
                ? snapshot.AggregateJitterMs
                : snapshot.Jitter > 0
                    ? snapshot.Jitter
                    : snapshot.MicroJitter;

            if (jitter > 15) confidence -= 0.10;
            if (jitter > 25) confidence -= 0.10;

            return Math.Max(0, Math.Min(1, confidence));
        }
    }
}