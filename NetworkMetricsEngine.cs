using System;

namespace WTFnetApp
{
    public class NetworkMetricsEngine
    {
        private readonly DataSet dataSet = new DataSet();
        private readonly PredictionEngine predictionEngine = new PredictionEngine();

        private double previousQueueDelay;
        private double previousQueueGrowth;
        private double previousRetransmissionRate;
        private double latencyBaseline;
        private double pingBaseline;

        public DataSet DataSet => dataSet;

        public MetricsSnapshot Process(MetricsSnapshot snapshot)
        {
            if (snapshot == null)
                return null;

            snapshot.QueueDelay = Math.Max(0, snapshot.LatencyMs - snapshot.BaseLatencyMs);
            snapshot.QueueGrowth = snapshot.QueueDelay - previousQueueDelay;
            snapshot.QueueAcceleration = snapshot.QueueGrowth - previousQueueGrowth;

            snapshot.QueuePressure = Math.Max(0, snapshot.QueueDelay) * Math.Max(0, snapshot.QueueGrowth);
            snapshot.QueuePressure = Math.Min(snapshot.QueuePressure, 500);

            previousQueueDelay = snapshot.QueueDelay;
            previousQueueGrowth = snapshot.QueueGrowth;

            if (latencyBaseline == 0)
                latencyBaseline = snapshot.Recent;
            else
                latencyBaseline = latencyBaseline * 0.97 + snapshot.Recent * 0.03;

            snapshot.LatencyDrift = snapshot.Recent - latencyBaseline;

            snapshot.RetransmissionGrowth = snapshot.RetransmissionRate - previousRetransmissionRate;
            previousRetransmissionRate = snapshot.RetransmissionRate;

            double congestionFlags = 0;
            if (snapshot.IsCongested) congestionFlags += 1.5;
            if (snapshot.IsDownloadCongested) congestionFlags += 0.8;
            if (snapshot.IsUploadCongested) congestionFlags += 0.8;

            snapshot.CongestionScore = predictionEngine.ComputeCongestionScore(snapshot, congestionFlags);

            double pingSource = snapshot.ExternalPingMs > 0 ? snapshot.ExternalPingMs : snapshot.Recent;
            if (pingBaseline == 0)
                pingBaseline = pingSource;
            else
                pingBaseline = pingBaseline * 0.97 + pingSource * 0.03;

            snapshot.LagScore = predictionEngine.ComputeLagScore(pingSource, pingBaseline);
            snapshot.HealthScore = Math.Max(0, snapshot.MosScore * (1.0 - snapshot.PacketLoss) - snapshot.RetransmissionRate * 2.0);
            snapshot.IssueSource = DetectIssueSource(snapshot);

            dataSet.AddSample(new NetSample
            {
                Time = DateTime.Now,
                Latency = snapshot.Recent,
                Jitter = snapshot.Jitter,
                Loss = snapshot.PacketLoss,
                QueueDelay = snapshot.QueueDelay,
                QueueGrowth = snapshot.QueueGrowth,
                QueueAcceleration = snapshot.QueueAcceleration,
                QueuePressure = snapshot.QueuePressure,
                CongestionScore = snapshot.CongestionScore,
                LatencyDrift = snapshot.LatencyDrift,
                RetransmissionRate = snapshot.RetransmissionRate,
                RetransmissionGrowth = snapshot.RetransmissionGrowth,
                Connected = snapshot.Recent > 0,
                MultiConnection = snapshot.ActiveConnections > 1
            });

            return snapshot;
        }

        private NetworkIssueSource DetectIssueSource(MetricsSnapshot snapshot)
        {
            if (snapshot.CongestionScore < 3 && snapshot.LagScore < 1.2)
                return NetworkIssueSource.None;

            if (snapshot.QueuePressure > 40 && snapshot.RetransmissionGrowth > 0)
                return NetworkIssueSource.LTE_Tower_Load;

            if (snapshot.Jitter > 20 && snapshot.PacketLoss > 0.01)
                return NetworkIssueSource.LocalWifi;

            if (snapshot.LatencyDrift > 8 && snapshot.RetransmissionRate < 0.005)
                return NetworkIssueSource.ISP_Routing;

            if (snapshot.LagScore > 1.5)
                return NetworkIssueSource.Server_Lag;

            return NetworkIssueSource.Unknown;
        }
    }
}
