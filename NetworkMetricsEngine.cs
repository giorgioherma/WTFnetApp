using System;

namespace WTFnetApp
{
    public class NetworkMetricsEngine
    {
        private MetricsProcessor processor = new MetricsProcessor();
        private DataSet dataSet = new DataSet();
        private NetworkCongestionDetector detector = new NetworkCongestionDetector();
        private double previousQueueDelay;
        private double previousQueueGrowth;
        private double previousRetransRate;

        private double queueBaseline;
        private double congestionMomentum;

        private double latencyBaseline;
        private double smoothedCongestionScore;

        private double previousLatency;
        private double latencySlopeBaseline;

        public MetricsSnapshot Process(MetricsSnapshot snapshot)
        {
            snapshot.QueueDelay = snapshot.QueueDelayMs;

            if (previousQueueDelay > 0)
                snapshot.QueueGrowth = Math.Max(0, snapshot.QueueDelay - previousQueueDelay);
            else
                snapshot.QueueGrowth = 0;

            if (previousQueueGrowth != 0)
            {
                double accel = snapshot.QueueGrowth - previousQueueGrowth;

                if (Math.Abs(accel) < 2)
                    accel = 0;

                snapshot.QueueAcceleration = accel;
            }
            else
            {
                snapshot.QueueAcceleration = 0;
            }

            previousQueueDelay = snapshot.QueueDelay;
            previousQueueGrowth = snapshot.QueueGrowth;

            double congestion = 0;

            if (snapshot.IsCongested)
                congestion += 2.0;

            if (snapshot.IsDownloadCongested)
                congestion += 1.5;

            if (snapshot.IsUploadCongested)
                congestion += 1.5;

            if (snapshot.QueueDelay > 10)
                congestion += 0.5;

            if (snapshot.QueueGrowth > 5)
                congestion += 1.0;

            if (snapshot.QueueAcceleration > 5)
                congestion += 1.5;

            if (snapshot.RetransmissionRate > 0.02)
                congestion += 1.5;

            snapshot.CongestionScore = congestion;

            double health = snapshot.MosScore;
            health *= (1.0 - snapshot.PacketLoss);
            health -= snapshot.RetransmissionRate * 2;

            snapshot.HealthScore = Math.Max(0, health);

            if (snapshot.IsCongested)
                snapshot.Congested = snapshot.IsCongested;

            if (snapshot.ReceiveBytesPerSecond > 0)
                snapshot.ReceiveBytes = snapshot.ReceiveBytesPerSecond;

            if (snapshot.SendBytesPerSecond > 0)
                snapshot.SendBytes = snapshot.SendBytesPerSecond;

            var sample = new NetSample
            {
                Time = DateTime.Now,
                Latency = snapshot.Recent,
                Jitter = snapshot.Jitter,
                Loss = snapshot.PacketLoss,
                MultiConnection = snapshot.ActiveConnections > 1,
                Connected = snapshot.Recent > 0,
                QueueDelay = snapshot.QueueDelay,
                QueueGrowth = snapshot.QueueGrowth,
                QueueAcceleration = snapshot.QueueAcceleration,
                CongestionScore = snapshot.CongestionScore
            };

            dataSet.AddSample(sample);

            return snapshot;
        }
    }
}
