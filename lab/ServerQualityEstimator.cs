using System;

namespace WTFnetApp
{
    public class ServerQualityEstimator
    {
        private DateTime lastUpdate = DateTime.MinValue;
        private double previousBaseline;

        public double ServerScore { get; private set; } = 1.0;

        public void Update(double baselineRtt, LagPressureController lagPressure)
        {
            if (baselineRtt <= 0)
                return;

            DateTime now = DateTime.UtcNow;
            if (lastUpdate != DateTime.MinValue && (now - lastUpdate).TotalSeconds < 20)
                return;

            lastUpdate = now;

            double raw = 1.0 - baselineRtt / 250.0;
            if (raw < 0.2) raw = 0.2;
            if (raw > 1.0) raw = 1.0;

            ServerScore = ServerScore * 0.8 + raw * 0.2;

            if (previousBaseline > 0 && Math.Abs(baselineRtt - previousBaseline) > 40 && lagPressure != null)
                lagPressure.OnRecovery();

            previousBaseline = baselineRtt;
        }
    }
}
