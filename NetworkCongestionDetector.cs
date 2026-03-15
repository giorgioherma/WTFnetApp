using System;

namespace WTFnetApp
{
    public class NetworkCongestionDetector
    {
        private const int BUFFER_SIZE = 30;

        private readonly double[] pingBuffer = new double[BUFFER_SIZE];
        private int pingIndex = 0;
        private int sampleCount = 0;

        private double previousAvg = 0;

        private double pressure = 0;

        private int spikeConfirm = 0;
        private DateTime lastSpike = DateTime.MinValue;

        private int badStreak = 0;
        private int goodStreak = 0;

        public class DecisionScore
        {
            public double HardSpike;
            public double Predicted;
            public double Micro;
            public double Pressure;
            public double Stability;
            public double HighLatency;
            public double QueueGrowth;
            public double VarianceShock;
            public double Burst;
        }

        public class Result
        {
            public DecisionScore Score;
            public string Context;
        }

        public Result Update(
            double avg,
            double recentAvg,
            double jitter,
            double maxPing,
            double lossRate,
            double dynBase)
        {
            // =========================
            // IGNORE EXTREME VALUES
            // =========================

            if (maxPing > 400)
                maxPing = dynBase + 20;

            // =========================
            // BUFFER UPDATE
            // =========================

            pingBuffer[pingIndex] = recentAvg;
            pingIndex = (pingIndex + 1) % BUFFER_SIZE;

            if (sampleCount < BUFFER_SIZE)
                sampleCount++;

            // =========================
            // SLOPE
            // =========================

            double slope = (recentAvg - previousAvg) * 0.7;
            previousAvg = recentAvg;

            bool risingQueue =
                slope > 8 &&
                recentAvg > dynBase + 6;

            // =========================
            // LTE VARIANCE
            // =========================

            bool varianceShock =
                jitter > Math.Max(30, dynBase * 0.7);

            // =========================
            // BURST DETECTION
            // =========================

            bool burst =
                maxPing > dynBase + 90 &&
                jitter > 25;

            // =========================
            // HARD SPIKE
            // =========================

            bool spikeCandidate =
                recentAvg > dynBase + 80 ||
                maxPing > dynBase + 140;

            if (spikeCandidate)
                spikeConfirm++;
            else
                spikeConfirm -= 2;

            if (spikeConfirm < 0)
                spikeConfirm = 0;

            bool hardSpike = spikeConfirm >= 2;

            if (hardSpike)
                lastSpike = DateTime.Now;

            // spike cooldown
            if ((DateTime.Now - lastSpike).TotalSeconds > 3)
                spikeConfirm = 0;

            // =========================
            // MICRO JITTER
            // =========================

            bool micro =
                jitter > Math.Max(22, dynBase * 0.45) &&
                recentAvg > dynBase + 10;

            // =========================
            // STABLE
            // =========================

            bool stable =
                jitter < 18 &&
                lossRate < 0.03 &&
                recentAvg < dynBase + 8;

            // =========================
            // COMFORT
            // =========================

            bool uncomfortable =
                avg > dynBase + 30 ||
                (avg > dynBase + 18 && jitter > 26);

            // =========================
            // PRESSURE SYSTEM
            // =========================

            if (micro) pressure += 0.3;
            if (hardSpike) pressure += 1.0;
            if (uncomfortable) pressure += 0.2;
            if (risingQueue) pressure += 0.5;
            if (varianceShock) pressure += 0.4;
            if (burst) pressure += 0.6;

            double decay = stable ? 0.9 : 0.5;

            pressure -= decay;

            if (pressure < 0)
                pressure = 0;

            if (pressure > 10)
                pressure = 10;

            // =========================
            // PREDICTION
            // =========================

            bool predicted =
                pressure > 2.0 &&
                jitter > dynBase * 0.3;

            // =========================
            // STREAK LOGIC
            // =========================

            if (hardSpike || predicted)
            {
                badStreak += 2;
                goodStreak = 0;
            }
            else if (micro || varianceShock || risingQueue)
            {
                badStreak += 1;
                goodStreak = 0;
            }
            else if (stable)
            {
                goodStreak++;

                if (goodStreak >= 3)
                {
                    badStreak = Math.Max(0, badStreak - 3);
                    pressure *= 0.35;
                }
                else
                {
                    badStreak = Math.Max(0, badStreak - 1);
                }
            }
            else
            {
                badStreak = Math.Max(0, badStreak - 1);
            }

            if (badStreak > 10)
                badStreak = 10;

            // =========================
            // SCORE
            // =========================

            var score = new DecisionScore
            {
                HardSpike = hardSpike ? 3.0 : 0,
                Predicted = predicted ? 2.0 : 0,
                Micro = micro ? 1.2 : 0,
                Pressure = pressure,
                Stability = stable ? 2.5 : 0,
                HighLatency = uncomfortable ? 1.5 : 0,
                QueueGrowth = risingQueue ? 1.0 : 0,
                VarianceShock = varianceShock ? 1.3 : 0,
                Burst = burst ? 1.4 : 0
            };

            string context =
                $"avg:{avg:F0} recent:{recentAvg:F0} jitter:{jitter:F0} " +
                $"loss:{lossRate:F2} pressure:{pressure:F2} slope:{slope:F1}";

            return new Result
            {
                Score = score,
                Context = context
            };
        }
    }
}
