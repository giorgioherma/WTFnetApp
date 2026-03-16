using System;

namespace WTFnetApp
{
    public static class SpeedifyMetricsAdapter
    {
        public static MetricsSnapshot Convert(SpeedifyMetrics metrics)
        {
            var snapshot = new MetricsSnapshot();

            if (metrics == null || metrics.Connections == null || metrics.Connections.Count == 0)
                return snapshot;

            double latSum = 0;
            double jitSum = 0;
            double lossSum = 0;

            int count = 0;

            foreach (var c in metrics.Connections)
            {
                if (c == null)
                    continue;

                latSum += c.Latency;
                jitSum += c.Jitter;
                lossSum += (c.LossReceive + c.LossSend) / 2.0;

                snapshot.ReceiveBytes += (long)(c.ReceiveMbps * 125000);
                snapshot.SendBytes += (long)(c.SendMbps * 125000);

                count++;
            }

            if (count == 0)
                return snapshot;

            snapshot.Recent = latSum / count;
            snapshot.Average = snapshot.Recent;

            snapshot.Jitter = Math.Min(jitSum / count, 200);
            snapshot.PacketLoss = lossSum / count;

            if (metrics.Connections.Count >= 1)
                snapshot.Adapter1Latency = metrics.Connections[0].Latency;

            if (metrics.Connections.Count >= 2)
                snapshot.Adapter2Latency = metrics.Connections[1].Latency;

            return snapshot;
        }
    }
}