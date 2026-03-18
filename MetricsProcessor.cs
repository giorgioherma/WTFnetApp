using System;

namespace WTFnetApp
{
    public class MetricsProcessor
    {
        // =========================
        // BUFFER
        // =========================

        private const int SIZE = 30;

        private readonly int[] buffer = new int[SIZE];

        private int index = 0;
        private int count = 0;

        // =========================
        // RUNNING STATS
        // =========================

        private double sum = 0;
        private double sumSq = 0;

        private int maxPing = 0;

        private int lastPing = 0;

        // packet loss tracking
        private int lossCount = 0;
        private int totalCount = 0;

        // dynamic baseline
        private double dynamicBase = 40;

        // =========================
        // ADD SAMPLE
        // =========================

        public void AddPing(int ping)
        {
            totalCount++;

            if (ping >= 900) // timeout
            {
                lossCount++;
                return;
            }

            int old = buffer[index];

            // remove old sample
            if (count == SIZE)
            {
                sum -= old;
                sumSq -= old * old;
            }

            // insert new
            buffer[index] = ping;

            sum += ping;
            sumSq += ping * ping;

            index++;
            if (index >= SIZE)
                index = 0;

            if (count < SIZE)
                count++;

            if (ping > maxPing)
                maxPing = ping;

            lastPing = ping;

            UpdateDynamicBaseline();
        }

        // =========================
        // BASELINE
        // =========================

        private void UpdateDynamicBaseline()
        {
            if (count < 5)
                return;

            double avg = sum / count;

            // slow adaptation
            dynamicBase = dynamicBase * 0.9 + avg * 0.1;
        }

        // =========================
        // SNAPSHOT
        // =========================

        public MetricsSnapshot Snapshot()
        {
            if (count == 0)
                return new MetricsSnapshot();

            double avg = sum / count;

            double variance =
                (sumSq / count) - (avg * avg);

            if (variance < 0)
                variance = 0;

            double jitter = Math.Sqrt(variance);

            double loss =
                totalCount > 0
                ? (double)lossCount / totalCount
                : 0;

            return new MetricsSnapshot
            {
                Average = avg,
                Recent = lastPing,
                Max = maxPing,
                Jitter = jitter,
                PacketLoss = loss,
                DynamicBase = dynamicBase
            };
        }
    }
}