using System;

namespace WTFnetApp
{
    public struct PatternMemory
    {
        public bool Active;
        public double AverageInterval;
        public double AverageQueueGrowth;
        public double LagProbability;
        public int Count;
    }

    public class PatternMemoryManager
    {
        private const int MaxPatterns = 16;
        private readonly PatternMemory[] patterns = new PatternMemory[MaxPatterns];
        private DateTime lastLagEventTime = DateTime.MinValue;

        public void Learn(LagEvent lagEvent, LagEventFeatures features)
        {
            if (lagEvent == null)
                return;

            if (lastLagEventTime == DateTime.MinValue)
            {
                lastLagEventTime = lagEvent.Time;
                return;
            }

            double interval = (lagEvent.Time - lastLagEventTime).TotalSeconds;
            lastLagEventTime = lagEvent.Time;

            if (interval <= 0)
                return;

            int slot = FindOrCreateSlot(interval);
            var p = patterns[slot];

            p.Active = true;
            p.Count++;
            p.AverageInterval = Blend(p.AverageInterval, interval, p.Count);
            p.AverageQueueGrowth = Blend(p.AverageQueueGrowth, features.AverageQueueGrowth, p.Count);
            p.LagProbability = Math.Max(0, Math.Min(1, p.LagProbability * 0.8 + 0.2));

            patterns[slot] = p;
        }

        private int FindOrCreateSlot(double interval)
        {
            int firstFree = -1;
            int closest = 0;
            double closestDist = double.MaxValue;

            for (int i = 0; i < patterns.Length; i++)
            {
                if (!patterns[i].Active)
                {
                    if (firstFree < 0)
                        firstFree = i;
                    continue;
                }

                double d = Math.Abs(patterns[i].AverageInterval - interval);
                if (d < closestDist)
                {
                    closestDist = d;
                    closest = i;
                }
            }

            if (closestDist <= 2.0)
                return closest;

            return firstFree >= 0 ? firstFree : closest;
        }

        private double Blend(double oldValue, double value, int count)
        {
            if (count <= 1)
                return value;

            return oldValue * 0.8 + value * 0.2;
        }
    }
}
