using System;

namespace WTFnetApp
{
    public class LagPressureController
    {
        private const double DecayRatePerSecond = 0.04;
        private DateTime lastUpdate = DateTime.UtcNow;
        private DateTime lastEventTime = DateTime.MinValue;
        private int burstCount;

        public double LagPressure { get; private set; }
        public double BurstMultiplier { get; private set; } = 1.0;

        public void UpdateDecay(NetworkState networkState, double serverScore)
        {
            DateTime now = DateTime.UtcNow;
            double dt = (now - lastUpdate).TotalSeconds;
            if (dt < 0)
                dt = 0;

            lastUpdate = now;
            if (serverScore < 0.2) serverScore = 0.2;
            if (serverScore > 1.0) serverScore = 1.0;
            LagPressure -= (DecayRatePerSecond * serverScore) * dt;
            LagPressure = Clamp01(LagPressure);

            if (networkState == NetworkState.Degraded && LagPressure > 0.8)
                LagPressure = 0.8;
        }

        public void ApplyEvent(LagEventType type, double serverScore)
        {
            double weight = BaseWeight(type);
            if (weight <= 0)
                return;

            DateTime now = DateTime.UtcNow;
            if ((now - lastEventTime).TotalSeconds <= 3)
                burstCount++;
            else
                burstCount = 0;

            lastEventTime = now;
            BurstMultiplier = 1.0 + burstCount * 0.5;

            double adjusted = weight * ClampServerScore(serverScore) * BurstMultiplier;
            LagPressure = Clamp01(LagPressure + adjusted);
        }

        public void OnRecovery()
        {
            LagPressure *= 0.5;
            burstCount = 0;
            BurstMultiplier = 1.0;
        }

        private double BaseWeight(LagEventType type)
        {
            switch (type)
            {
                case LagEventType.MinorJitter: return 0.05;
                case LagEventType.Rubberband: return 0.12;
                case LagEventType.Teleport: return 0.20;
                case LagEventType.Freeze: return 0.35;
                default: return 0;
            }
        }

        private double Clamp01(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }

        private double ClampServerScore(double value)
        {
            if (value < 0.2) return 0.2;
            if (value > 1.0) return 1.0;
            return value;
        }
    }
}
