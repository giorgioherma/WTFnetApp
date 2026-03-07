using System;
using System.Collections.Generic;
using System.Linq;

namespace WTFnetApp
{
    public class LteResult
    {
        public double Quality;
        public double Confidence;
        public int Spikes;
        public string Label;
        public string Trend;
        public double Best;
    }

    public class LteProTool
    {
        private double lastQuality = 0;
        private double smoothQuality = 0;
        private double bestQuality = 0;

        public LteResult Evaluate(
            List<int> pings,
            int avg,
            int jitter,
            double lossRate,
            double pressure)
        {
            var lastSamples = pings.Skip(Math.Max(0, pings.Count - 10)).ToList();

            int median = lastSamples.OrderBy(x => x).ElementAt(lastSamples.Count / 2);

            int spikeCount = lastSamples.Count(x => x > median + 25);

            double variance = lastSamples.Select(x => Math.Pow(x - avg, 2)).Average();

            double jitterPenalty = jitter * 2.2;
            double spikePenalty = spikeCount * 6.0;
            double lossPenalty = lossRate * 180;
            double pressurePenalty = pressure * 4.5;
            double variancePenalty = variance / 40.0;

            double rawScore = 100
                - jitterPenalty
                - spikePenalty
                - lossPenalty
                - pressurePenalty
                - variancePenalty;

            double quality = Math.Max(0, Math.Min(100, rawScore));

            if (smoothQuality == 0)
                smoothQuality = quality;
            else
            {
                double alpha = 0.15;
                smoothQuality = (alpha * quality) + ((1 - alpha) * smoothQuality);
            }

            if (spikeCount == 0 && jitter < 15 && lossRate < 0.02)
                smoothQuality += 2;

            smoothQuality = Math.Min(100, Math.Max(0, smoothQuality));

            // slight decay so old "best" doesn't lie forever
            bestQuality = Math.Max(bestQuality * 0.997, smoothQuality);

            double confidence =
                1.0
                - (jitter / 45.0)
                - (lossRate * 2.2)
                - (spikeCount * 0.06);

            confidence = Math.Max(0, Math.Min(1, confidence));

            double delta = smoothQuality - lastQuality;
            lastQuality = smoothQuality;

            string trend =
                delta > 1 ? "↑" :
                delta < -1 ? "↓" :
                "→";

            string label =
                smoothQuality > 90 ? "LOCKED" :
                smoothQuality > 75 ? "STABLE" :
                smoothQuality > 60 ? "PLAYABLE" :
                smoothQuality > 45 ? "WEAK" :
                "BAD";

            return new LteResult
            {
                Quality = smoothQuality,
                Confidence = confidence,
                Spikes = spikeCount,
                Label = label,
                Trend = trend,
                Best = bestQuality
            };
        }
    }
}