using System;

namespace WTFnetApp
{
    public class AdaptiveThresholdController
    {
        private double dynamicThreshold = 0.60;

        public double DynamicThreshold => dynamicThreshold;

        public void RegisterFalsePositive()
        {
            dynamicThreshold = Math.Min(0.90, dynamicThreshold + 0.03);
        }

        public void RegisterLagMatch()
        {
            dynamicThreshold = Math.Max(0.55, dynamicThreshold - 0.01);
        }

        public void ApplyEpochDelta(double delta)
        {
            if (delta > 0.01) delta = 0.01;
            if (delta < -0.01) delta = -0.01;
            dynamicThreshold = Math.Max(0.55, Math.Min(0.90, dynamicThreshold + delta));
        }
    }
}
