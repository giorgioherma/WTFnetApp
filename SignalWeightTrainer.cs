using System;

namespace WTFnetApp
{
    public class SignalWeights
    {
        public double QueueAccelerationWeight = 1.0;
        public double RetransGrowthWeight = 1.0;
        public double DriftWeight = 1.0;
        public double QueueDelayWeight = 1.0;
    }

    public class SignalWeightTrainer
    {
        private const double LearningRate = 0.02;
        private readonly SignalWeights weights;

        public SignalWeightTrainer(SignalWeights weights)
        {
            this.weights = weights;
        }

        public void LearnFromLagEvent(LagEventFeatures f)
        {
            weights.QueueAccelerationWeight += LearningRate * Math.Max(0, f.MaxQueueAcceleration) * 0.1;
            weights.RetransGrowthWeight += LearningRate * Math.Max(0, f.RetransmissionSpike) * 20.0;
            weights.DriftWeight += LearningRate * Math.Max(0, f.MaxLatencyDrift) * 0.1;
            weights.QueueDelayWeight += LearningRate * Math.Max(0, f.AverageQueueGrowth) * 0.2;
            ClampWeights();
        }

        public void LearnFromFalsePositive(double predictionStrength)
        {
            if (predictionStrength < 0.6)
                return;

            double delta = LearningRate * predictionStrength;
            weights.QueueAccelerationWeight -= delta;
            weights.RetransGrowthWeight -= delta;
            weights.DriftWeight -= delta;
            weights.QueueDelayWeight -= delta;
            ClampWeights();
        }

        private void ClampWeights()
        {
            weights.QueueAccelerationWeight = Clamp(weights.QueueAccelerationWeight);
            weights.RetransGrowthWeight = Clamp(weights.RetransGrowthWeight);
            weights.DriftWeight = Clamp(weights.DriftWeight);
            weights.QueueDelayWeight = Clamp(weights.QueueDelayWeight);
        }

        private double Clamp(double value)
        {
            return Math.Max(0.1, Math.Min(5.0, value));
        }
    }
}
