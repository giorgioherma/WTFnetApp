using System;

namespace WTFnetApp
{
    public static class NetworkQualityEvaluator
    {
        private const double CriticalLoss = 0.40;
        private const double WarningLoss = 0.20;

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

            double effectiveLoss = EffectiveLoss(state.LossRate);

            // only truly awful loss should go straight critical
            if (effectiveLoss >= CriticalLoss)
            {
                state.HealthState = UnifiedNetworkHealthState.Critical;
                return;
            }

            if (state.LatencyMs >= CriticalLatency || state.JitterMs >= CriticalJitter)
            {
                state.HealthState = UnifiedNetworkHealthState.Critical;
                return;
            }

            // warning if multiple signs of trouble, not just tiny fake loss
            if (effectiveLoss >= WarningLoss)
            {
                state.HealthState = UnifiedNetworkHealthState.Warning;
                return;
            }

            if (state.LatencyMs >= WarningLatency || state.JitterMs >= WarningJitter || state.BadQualityRate >= 0.35)
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

            double effectivePacketLoss = EffectiveLoss(snapshot.PacketLoss);

            if (effectivePacketLoss >= 0.30)
            {
                state.HealthState = UnifiedNetworkHealthState.Critical;
                return;
            }

            if (state.JitterMs >= 30 || snapshot.AggregateCongested)
            {
                state.HealthState = UnifiedNetworkHealthState.Warning;
                return;
            }

            if (effectivePacketLoss >= 0.18)
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
            double confidence = 0.78;

            if (!snapshot.TunnelConnected)
                confidence -= 0.30;

            if (snapshot.AggregateCongested)
                confidence -= 0.10;

            double effectivePacketLoss = EffectiveLoss(snapshot.PacketLoss);
            if (effectivePacketLoss > 0)
                confidence -= Math.Min(0.18, effectivePacketLoss * 0.60);

            double jitter = snapshot.AggregateJitterMs > 0
                ? snapshot.AggregateJitterMs
                : snapshot.Jitter > 0
                    ? snapshot.Jitter
                    : snapshot.MicroJitter;

            if (jitter > 15) confidence -= 0.06;
            if (jitter > 25) confidence -= 0.08;
            if (jitter > 35) confidence -= 0.08;

            return Math.Max(0, Math.Min(1, confidence));
        }

        private static double EffectiveLoss(double loss)
        {
            if (loss <= 0.00) return 0.0;
            if (loss <= 0.03) return 0.0;
            if (loss <= 0.06) return 0.02;
            if (loss <= 0.10) return 0.05;
            if (loss <= 0.15) return 0.10;
            return loss;
        }
    }
}