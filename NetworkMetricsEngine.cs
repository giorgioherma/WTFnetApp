using System;

namespace WTFnetApp
{
    public class NetworkMetricsEngine
    {
        private MetricsProcessor processor = new MetricsProcessor();
        private DataSet dataSet = new DataSet();
        private NetworkCongestionDetector detector = new NetworkCongestionDetector();

        public MetricsSnapshot Process(MetricsSnapshot snapshot)
        {
            if (snapshot.QueueDelayMs > 0)
                snapshot.QueueDelay = snapshot.QueueDelayMs;

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
                Connected = snapshot.Recent > 0
            };

            dataSet.AddSample(sample);

            return snapshot;
        }
    }
}
