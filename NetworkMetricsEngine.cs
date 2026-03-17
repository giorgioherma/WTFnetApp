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
        private double bandwidthBaseline;

        public MetricsSnapshot Process(MetricsSnapshot snapshot)
        {
            snapshot.QueueDelay = snapshot.QueueDelayMs;

            if (snapshot.QueueDelay < 20)
            {
                if (queueBaseline == 0)
                    queueBaseline = snapshot.QueueDelay;
                else
                    queueBaseline = queueBaseline * 0.95 + snapshot.QueueDelay * 0.05;
            }

            if (previousQueueDelay > 0)
                snapshot.QueueGrowth = snapshot.QueueDelay - previousQueueDelay;
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

            double retransGrowth = snapshot.RetransmissionRate - previousRetransRate;
            previousRetransRate = snapshot.RetransmissionRate;
            snapshot.RetransmissionGrowth = retransGrowth;

            if (latencyBaseline == 0)
                latencyBaseline = snapshot.Recent;
            else
                latencyBaseline = latencyBaseline * 0.97 + snapshot.Recent * 0.03;

            snapshot.LatencyDrift = snapshot.Recent - latencyBaseline;

            double slope = snapshot.Recent - previousLatency;
            previousLatency = snapshot.Recent;
            snapshot.LatencySlope = slope;

            if (latencySlopeBaseline == 0)
                latencySlopeBaseline = slope;
            else
                latencySlopeBaseline = latencySlopeBaseline * 0.9 + slope * 0.1;

            double slopePressure = slope - latencySlopeBaseline;

            double throughputPressure = 0;
            if (snapshot.CombinedBandwidthMbps > 0)
            {
                if (bandwidthBaseline == 0)
                    bandwidthBaseline = snapshot.CombinedBandwidthMbps;
                else
                    bandwidthBaseline = bandwidthBaseline * 0.97 + snapshot.CombinedBandwidthMbps * 0.03;

                if (bandwidthBaseline > 0)
                    throughputPressure = (bandwidthBaseline - snapshot.CombinedBandwidthMbps) / bandwidthBaseline;
            }

            double congestion = 0;

            if (snapshot.IsCongested)
                congestion += 2.0;

            if (snapshot.IsDownloadCongested)
                congestion += 1.5;

            if (snapshot.IsUploadCongested)
                congestion += 1.5;

            if (queueBaseline > 0 && snapshot.QueueDelay > queueBaseline * 2)
                congestion += 1.0;

            if (snapshot.QueueGrowth > 5)
                congestion += 1.0;

            if (snapshot.QueueAcceleration > 5)
                congestion += 1.5;

            if (snapshot.RetransmissionRate > 0.02)
                congestion += 1.5;

            if (snapshot.LatencyDrift > 8)
                congestion += 1.0;

            if (slopePressure > 3)
                congestion += 1.5;

            if (retransGrowth > 0.01 && snapshot.QueueGrowth > 2)
                congestion += 2.0;

            if (throughputPressure > 0.30 && snapshot.QueueGrowth > 0)
                congestion += 1.0;

            int queueCycle = dataSet.DetectQueueCycle();
            if (queueCycle > 0 && snapshot.QueueAcceleration > 0)
                congestion += 1.0;

            congestionMomentum += snapshot.QueueAcceleration;
            congestionMomentum *= 0.85;
            congestionMomentum = Math.Max(-20, Math.Min(20, congestionMomentum));

            if (congestionMomentum > 10)
                congestion += 2;

            double boundedCongestion = Math.Min(15, Math.Max(0, congestion));

            if (smoothedCongestionScore == 0)
                smoothedCongestionScore = boundedCongestion;
            else
                smoothedCongestionScore = smoothedCongestionScore * 0.7 + boundedCongestion * 0.3;

            snapshot.CongestionScore = Math.Min(15, Math.Max(0, smoothedCongestionScore));

            var timeline = dataSet.GetTimelineSummary();
            Logger.Log("timeline", timeline);

            double health = snapshot.MosScore;
            health *= (1.0 - snapshot.PacketLoss);
            health -= snapshot.RetransmissionRate * 2;

            snapshot.HealthScore = Math.Max(0, health);

            snapshot.IssueSource = DetectIssueSource(snapshot);
            Logger.LogAnalysis(NetworkAnalysisFrame.FromSnapshot(snapshot));

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
                CongestionScore = snapshot.CongestionScore,
                LatencyDrift = snapshot.LatencyDrift
            };

            dataSet.AddSample(sample);

            return snapshot;
        }

        private NetworkIssueSource DetectIssueSource(MetricsSnapshot snapshot)
        {
            if (snapshot == null)
                return NetworkIssueSource.Unknown;

            if (snapshot.CongestionScore < 2 && Math.Abs(snapshot.LatencyDrift) < 2)
                return NetworkIssueSource.None;

            if (snapshot.Jitter > 15 &&
                snapshot.PacketLoss > 0.005 &&
                snapshot.QueueDelay < 5 &&
                (snapshot.LatencySlope > 3 || snapshot.Recent > snapshot.Average + 8))
            {
                return NetworkIssueSource.LocalWifi;
            }

            if (snapshot.QueueGrowth > 0 &&
                snapshot.QueueAcceleration > 0 &&
                snapshot.RetransmissionGrowth > 0 &&
                snapshot.LatencyDrift > 1)
            {
                return NetworkIssueSource.LTE_Tower_Load;
            }

            if (snapshot.LatencyDrift > 3 &&
                snapshot.RetransmissionRate < 0.005 &&
                snapshot.QueueDelay < 6)
            {
                return NetworkIssueSource.ISP_Routing;
            }

            if (snapshot.Recent > snapshot.Average + 10 &&
                snapshot.QueueDelay < 4 &&
                snapshot.RetransmissionRate < 0.005)
            {
                return NetworkIssueSource.Server_Lag;
            }

            return NetworkIssueSource.Unknown;
        }
    }
}
