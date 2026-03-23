using System;
using System.Collections.Generic;

namespace WTFnetApp
{
    public sealed class MetricsSnapshot
    {
        public DateTime TimeUtc = DateTime.UtcNow;

        // Tunnel / aggregate
        public bool TunnelConnected;
        public int ActiveConnectionCount;
        public int BackupConnectionCount;

        public double AggregateLatencyMs = -1;
        public double AggregateJitterMs = -1;
        public double AggregateRxBps;
        public double AggregateTxBps;
        public double AggregateTotalBps;
        public bool AggregateCongested;

        // Connected server / remote
        public string PrimaryRemoteIp;
        public string SpeedifyRemoteIp;
        public string ConnectedServerPublicIp;
        public string ConnectedServerName;

        // Real adapter links
        public double Adapter1Latency;
        public double Adapter2Latency;

        // Link list
        public readonly List<LinkSnapshot> Links = new List<LinkSnapshot>();

        // Legacy / derived compatibility fields
        public bool Connected;
        public bool Congested;

        public double LatencyMs = -1;
        public double BaseLatencyMs;
        public double QueueDelayMs;

        public double Recent;
        public double Average;
        public double Max;
        public double Jitter;
        public double MicroJitter;
        public double DynamicBase;

        public bool IsCongested;
        public bool IsDownloadCongested;
        public bool IsUploadCongested;

        public long ReceiveBytesPerSecond;
        public long SendBytesPerSecond;

        public long ReceiveBytes;
        public long SendBytes;

        public double MosScore;

        // Optional extras for later expansion
        public double PacketLoss;
        public double ExternalPingMs = -1;
        public double LagScore;
        public double QueueDelay;
        public double QueueGrowth;
        public double QueueAcceleration;
        public double LatencyDrift;
        public double RetransmissionGrowth;
        public double QueuePressure;
        public double CongestionScore;
        public double AggressionFactor;
        public double ServerScore = 1.0;
        public double LagProbability;

        public string PredictionConfidence;
        public string DominantSignal;
        public string LagEventType;
        public string GamePhase;
        public string NetworkState;
        public string ControlState;

        public int PendingLagEvents;
        public double BurstMultiplier;
        public bool LearningEnabled;
        public double PatternMatchScore;
        public double SignatureAvgQueueGrowth;
        public double LagPressure;
        public double RetransmissionRate;
        public string IssueSource;
    }

    public sealed class LinkSnapshot
    {
        public string Id;
        public string Guid;
        public string Key;
        public string Name;
        public string Isp;
        public string Protocol;

        public bool Connected;
        public bool IsProxy;
        public bool IsAggregate;

        public bool Congested;
        public bool DownloadCongested;
        public bool UploadCongested;

        public double LatencyMs = -1;
        public double JitterMs = -1;
        public double Mos = -1;

        public double RxBps;
        public double TxBps;
        public double TotalBps;

        public string LocalIp;
        public string PrivateIp;
        public string RemoteIp;
    }
}