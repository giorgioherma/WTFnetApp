using System;
using System.Collections.Generic;

namespace WTFnetApp
{
    public sealed class DashboardBank
    {
        public DateTime LastUpdateUtc;

        public double PingMs = -1;
        public string PingColorKey = "gray";
        public string TunnelState = "Unknown";

        public bool TunnelConnected;
        public string ConnectedServerName;
        public string ConnectedServerPublicIp;

        public double AggregateJitterMs = -1;
        public double AggregateRxBps;
        public double AggregateTxBps;
        public double AggregateTotalBps;

        public int ActiveConnectionCount;
        public int BackupConnectionCount;

        public string PrimaryRemoteIp;

        public bool SpikeDetected;
        public double PingConfidence;
        public bool PingStale;
    }

    public sealed class DataSet
    {
        private const int MaxSamples = 120;
        private readonly Queue<MetricsSnapshot> samples = new Queue<MetricsSnapshot>();

        public DashboardBank Bank { get; } = new DashboardBank();

        public void Add(MetricsSnapshot snapshot)
        {
            samples.Enqueue(snapshot);

            while (samples.Count > MaxSamples)
                samples.Dequeue();
        }

        public IReadOnlyCollection<MetricsSnapshot> Samples => samples;
    }
}