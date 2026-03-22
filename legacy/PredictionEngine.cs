using System;
using WTFnetApp;

namespace WTFnetApp
{
    public class PredictionEngine
    {
        private const int BandwidthWindowSize = 10;

        private readonly double[] bandwidthWindow = new double[BandwidthWindowSize];
        private int bandwidthIndex;
        private int bandwidthCount;

        private double previousScore;

        public double ComputeCongestionScore(MetricsSnapshot snapshot, double congestionFlags)
        {
            AddBandwidthSample(snapshot.CombinedBandwidthMbps);

            double queueSignal = Clamp01(snapshot.QueueGrowth / 8.0);
            double accelerationSignal = Clamp01((snapshot.QueueAcceleration + 2.0) / 6.0);
            double driftSignal = Clamp01(snapshot.LatencyDrift / 20.0);
            double retransSignal = Clamp01(snapshot.RetransmissionGrowth * 120.0);
            double pressureSignal = Clamp01(snapshot.QueuePressure / 200.0);
            double oscillationSignal = Clamp01(ComputeBandwidthStdDev() / 10.0);

            double normalizedScore =
                0.30 * queueSignal +
                0.25 * accelerationSignal +
                0.20 * driftSignal +
                0.15 * retransSignal +
                0.10 * pressureSignal +
                0.10 * oscillationSignal;

            double raw = Math.Max(0, Math.Min(15, normalizedScore * 15.0 + congestionFlags));
            previousScore = previousScore * 0.7 + raw * 0.3;
            return Math.Max(0, Math.Min(15, previousScore));
        }

        public double ComputeLagScore(double currentPing, double baselinePing)
        {
            if (baselinePing <= 0)
                return 0;

            return (currentPing - baselinePing) / baselinePing;
        }

        private void AddBandwidthSample(double bandwidthMbps)
        {
            if (bandwidthMbps < 0)
                bandwidthMbps = 0;

            bandwidthWindow[bandwidthIndex] = bandwidthMbps;
            bandwidthIndex = (bandwidthIndex + 1) % BandwidthWindowSize;
            if (bandwidthCount < BandwidthWindowSize)
                bandwidthCount++;
        }

        private double ComputeBandwidthStdDev()
        {
            if (bandwidthCount <= 1)
                return 0;

            double sum = 0;
            for (int i = 0; i < bandwidthCount; i++)
                sum += bandwidthWindow[i];

            double mean = sum / bandwidthCount;
            double var = 0;
            for (int i = 0; i < bandwidthCount; i++)
            {
                double d = bandwidthWindow[i] - mean;
                var += d * d;
            }

            return Math.Sqrt(var / bandwidthCount);
        }

        private double Clamp01(double x)
        {
            if (x < 0) return 0;
            if (x > 1) return 1;
            return x;
        }

        public MetricsSnapshot Process(MetricsSnapshot snapshot)

        private readonly DataSet dataSet = new DataSet();
        private readonly PredictionEngine predictionEngine = new PredictionEngine();

        private double previousQueueDelay;
        private double previousQueueGrowth;
        private double previousRetransmissionRate;
        private double latencyBaseline;
        private double pingBaseline;

        public DataSet DataSet => dataSet;

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
        latencyBaseline = latencyBaseline* 0.97 + snapshot.Recent* 0.03;

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
        pingBaseline = pingBaseline* 0.97 + pingSource* 0.03;

    snapshot.LagScore = predictionEngine.ComputeLagScore(pingSource, pingBaseline);
    snapshot.HealthScore = Math.Max(0, snapshot.MosScore* (1.0 - snapshot.PacketLoss) - snapshot.RetransmissionRate* 2.0);
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
        }
    

    return snapshot;
    }
}
            







