using System;

namespace WTFnetApp
{
    public struct LagEventFeatures
    {
        public double AverageQueueGrowth;
        public double MaxQueueAcceleration;
        public double MaxLatencyDrift;
        public double RetransmissionSpike;
        public double AverageCongestionScore;
    }

    public class PatternAnalyzer
    {
        public LagEventFeatures ExtractFeatures(LagEvent lagEvent)
        {
            var result = new LagEventFeatures();
            if (lagEvent == null || lagEvent.Samples == null || lagEvent.PreSpikeCount <= 0)
                return result;

            int count = Math.Min(lagEvent.PreSpikeCount, lagEvent.Samples.Length);
            double queueGrowthSum = 0;
            double congestionSum = 0;

            for (int i = 0; i < count; i++)
            {
                var s = lagEvent.Samples[i];
                queueGrowthSum += s.QueueGrowth;
                congestionSum += s.CongestionScore;

                if (s.QueueAcceleration > result.MaxQueueAcceleration)
                    result.MaxQueueAcceleration = s.QueueAcceleration;

                if (s.LatencyDrift > result.MaxLatencyDrift)
                    result.MaxLatencyDrift = s.LatencyDrift;

                if (s.RetransmissionGrowth > result.RetransmissionSpike)
                    result.RetransmissionSpike = s.RetransmissionGrowth;
            }

            result.AverageQueueGrowth = count > 0 ? queueGrowthSum / count : 0;
            result.AverageCongestionScore = count > 0 ? congestionSum / count : 0;
            return result;
        }
    }
}
