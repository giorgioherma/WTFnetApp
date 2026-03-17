using System;

namespace WTFnetApp
{
    public class PredictionEngine
    {
        private double previousScore;

        public double ComputeCongestionScore(MetricsSnapshot s, double congestionFlags)
        {
            double raw =
                0.35 * s.QueueAcceleration +
                0.25 * s.QueueGrowth +
                80.0 * s.RetransmissionGrowth +
                0.15 * s.LatencyDrift +
                congestionFlags;

            raw = Math.Max(0, Math.Min(15, raw));
            previousScore = previousScore * 0.7 + raw * 0.3;
            return Math.Max(0, Math.Min(15, previousScore));
        }

        public double ComputeLagScore(double currentPing, double baselinePing)
        {
            if (baselinePing <= 0)
                return 0;

            return (currentPing - baselinePing) / baselinePing;
        }
    }
}
