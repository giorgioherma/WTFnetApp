using System.Collections.Generic;

namespace WTFnetApp
{
    public sealed class SpeedifyTelemetrySnapshot
    {
        public bool TunnelConnected;
        public bool AggregateCongested;

        public int ActiveConnectionCount;
        public int BackupConnectionCount;

        public double AggregateLatencyMs = -1;
        public double AggregateJitterMs = -1;
        public double AggregateRxBps = 0;
        public double AggregateTxBps = 0;
        public double AggregateTotalBps = 0;

        public string ConnectedServerName;
        public string ConnectedServerPublicIp;
        public string PrimaryRemoteIp;
        public string SpeedifyRemoteIp;

        public double Adapter1Latency = -1;
        public double Adapter2Latency = -1;

        public string CurrentState;
        public string CurrentMode;

        public bool HasConnectionStats;
        public bool HasNetworks;
        public bool HasSessionStats;
        public bool HasCurrentState;
        public bool HasConnectedServer;
        public bool StructuredCoreComplete;

        public int ParseWarningCount;

        public bool ConnectedLegacy;
        public bool CongestedLegacy;
        public bool DownloadCongested;
        public bool UploadCongested;

        public double LatencyMs = -1;
        public double BaseLatencyMs = -1;
        public double LegacyJitterMs = -1;
        public double ExternalPingMs = -1;

        public double MosScore = -1;

        public long ReceiveBytesPerSecond;
        public long SendBytesPerSecond;
        public long ReceiveBytes;
        public long SendBytes;

        public double LagScore = -1;
        public double QueueDelay = -1;
        public double QueueGrowth = 0;
        public double QueueAcceleration = 0;
        public double LatencyDrift = 0;
        public double RetransmissionGrowth = 0;
        public double QueuePressure = 0;
        public double AggressionFactor = 0;
        public double ServerScore = 1.0;
        public double BurstMultiplier = 0;
        public double PatternMatchScore = 0;
        public double SignatureAvgQueueGrowth = 0;
        public double LagPressure = 0;
        public double RetransmissionRate = 0;

        public string PredictionConfidence;
        public string DominantSignal;
        public string LagEventType;
        public string GamePhase;
        public string NetworkState;
        public string ControlState;
        public string IssueSource;

        public int PendingLagEvents;
        public bool LearningEnabled;

        public readonly List<SpeedifySessionWindowStats> SessionWindows = new List<SpeedifySessionWindowStats>();
    }
}
