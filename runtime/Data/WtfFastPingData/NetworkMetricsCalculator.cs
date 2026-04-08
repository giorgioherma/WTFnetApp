using System;

namespace WTFnetApp
{
    public static class NetworkMetricsCalculator
    {
        public static void ResolveFromFastPing(UnifiedNetworkState state, FastPingState fastPing)
        {
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
            state.IsStale = fastPing.IsStale;

            state.TunnelConnected = false;
            state.ActiveConnectionCount = 0;
            state.BackupConnectionCount = 0;

            state.PrimaryLatencySource = "fastping";
            state.PrimaryJitterSource = "fastping";
            state.PrimaryConfidenceSource = "fastping";
        }

        public static void ResolveFromSpeedify(UnifiedNetworkState state, MetricsSnapshot snapshot)
        {
            state.LatencyMs = ChoosePositive(
                snapshot.AggregateLatencyMs,
                snapshot.LatencyMs,
                snapshot.Adapter1Latency);

            state.JitterMs = ChoosePositive(
                snapshot.AggregateJitterMs,
                snapshot.Jitter,
                snapshot.MicroJitter);

            state.BaselineLatencyMs = ChoosePositive(
                snapshot.BaseLatencyMs,
                snapshot.DynamicBase,
                0);

            state.FloorLatencyMs = ChoosePositive(
                snapshot.DynamicBase,
                snapshot.BaseLatencyMs,
                0);

            state.DisplayedPingMs = state.LatencyMs;

            state.LossRate = snapshot.PacketLoss > 0 ? snapshot.PacketLoss : 0;
            state.BadQualityRate = 0;

            state.SpikeDetected =
                (state.LatencyMs > 0 &&
                 state.BaselineLatencyMs > 0 &&
                 state.LatencyMs > state.BaselineLatencyMs + 25)
                ||
                state.JitterMs > 22
                ||
                snapshot.AggregateCongested;

            state.IsStale = false;

            state.PrimaryLatencySource = "speedify";
            state.PrimaryConfidenceSource = "speedify";

            state.PrimaryJitterSource =
                snapshot.AggregateJitterMs > 0 ? "speedify.aggregate" :
                snapshot.Jitter > 0 ? "speedify.legacy" :
                snapshot.MicroJitter > 0 ? "speedify.micro" :
                "none";
        }

        private static double ChoosePositive(double first, double second, double third)
        {
            if (first > 0) return first;
            if (second > 0) return second;
            if (third > 0) return third;
            return -1;
        }
    }
}