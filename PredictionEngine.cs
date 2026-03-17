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

        private void AddBandwidthSample(double bandwidthMbps)
        {
            if (bandwidthMbps < 0)
                bandwidthMbps = 0;

            bandwidthWindow[bandwidthIndex] = bandwidthMbps;
            bandwidthIndex = (bandwidthIndex + 1) % BandwidthWindowSize;
            if (bandwidthCount < BandwidthWindowSize)
                bandwidthCount++;
        }

        private double ComputeBandwidthStdDev()
        {
            if (bandwidthCount <= 1)
                return 0;

            double sum = 0;
            for (int i = 0; i < bandwidthCount; i++)
                sum += bandwidthWindow[i];

            double mean = sum / bandwidthCount;
            double var = 0;
            for (int i = 0; i < bandwidthCount; i++)
            {
                double d = bandwidthWindow[i] - mean;
                var += d * d;
            }

            return Math.Sqrt(var / bandwidthCount);
        }

        private double Clamp01(double x)
        {
            if (x < 0) return 0;
            if (x > 1) return 1;
            return x;
        }
    }
}
