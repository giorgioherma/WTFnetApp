using System;

namespace WTFnetApp
{
    public class NetworkAnalysisFrame
    {
        public DateTime Time;

        public double Latency;
        public double Jitter;
        public double PacketLoss;

        public double QueueDelay;
        public double QueueGrowth;
        public double QueueAcceleration;
        public double QueuePressure;

        public double LatencyDrift;
        public double RetransmissionRate;
        public double CongestionScore;

        public NetworkIssueSource Source;

        public static NetworkAnalysisFrame FromSnapshot(MetricsSnapshot snapshot)
        {
            if (snapshot == null)
                return null;

            return new NetworkAnalysisFrame
            {
                Time = DateTime.Now,
                Latency = snapshot.Recent,
                Jitter = snapshot.Jitter,
                PacketLoss = snapshot.PacketLoss,
                QueueDelay = snapshot.QueueDelay,
                QueueGrowth = snapshot.QueueGrowth,
                QueueAcceleration = snapshot.QueueAcceleration,
                QueuePressure = snapshot.QueuePressure,
                LatencyDrift = snapshot.LatencyDrift,
                RetransmissionRate = snapshot.RetransmissionRate,
                CongestionScore = snapshot.CongestionScore,
                Source = snapshot.IssueSource
            };
        }
    }
}
