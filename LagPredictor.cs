using System;

namespace WTFnetApp
{
    public struct LagPrediction
    {
        public double Probability;
        public string Confidence;
        public string DominantSignal;
    }

    public class LagPredictor
    {
        private readonly SignalWeights weights;

        public LagPredictor(SignalWeights weights)
        {
            this.weights = weights;
        }

        public LagPrediction Predict(MetricsSnapshot s)
        {
            double qa = Math.Max(0, s.QueueAcceleration) * weights.QueueAccelerationWeight;
            double rg = Math.Max(0, s.RetransmissionGrowth) * weights.RetransGrowthWeight * 20.0;
            double drift = Math.Max(0, s.LatencyDrift) * weights.DriftWeight;
            double qd = Math.Max(0, s.QueueDelay) * weights.QueueDelayWeight * 0.5;

            double score = qa + rg + drift + qd;
            double probability = Math.Max(0, Math.Min(1, score / 20.0));

            string confidence = probability < 0.3 ? "LOW" :
                probability <= 0.6 ? "MEDIUM" : "HIGH";

            string dominant = "QueueDelay";
            double best = qd;
            if (qa > best) { best = qa; dominant = "QueueAcceleration"; }
            if (rg > best) { best = rg; dominant = "RetransmissionGrowth"; }
            if (drift > best) { dominant = "LatencyDrift"; }

            return new LagPrediction
            {
                Probability = probability,
                Confidence = confidence,
                DominantSignal = dominant
            };
        }
    }
}
