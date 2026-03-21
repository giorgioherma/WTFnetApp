namespace WTFnetApp
{
    public enum NetworkIssueSource
    {
        None,
        LocalWifi,
        LTE_Tower_Load,
        ISP_Routing,
        Server_Lag,
        Unknown
    }

    public class MetricsSnapshot
    {
        public double Recent;
        public double Average;
        public double Max;

        public double Jitter;
        public double Variance;
        public double MicroJitter;
        public double LatencyDrift;
        public double LatencySlope;
        public double Trend;
        public double LatencyDelta;

        public double PacketLoss;
        public double LossBurst;
        public bool Congested;
        public bool IsCongested;
        public bool IsDownloadCongested;
        public bool IsUploadCongested;

        public int Priority;

        public long ReceiveBytes;
        public long SendBytes;
        public long ReceiveBytesPerSecond;
        public long SendBytesPerSecond;

        public double DynamicBase;

        public double QueueDelay;
        public double QueueGrowth;
        public double QueueAcceleration;
        public double QueuePressure;
        public double QueueDelayMs;

        public double Adapter1Latency;
        public double Adapter2Latency;

        public bool Connected;
        public bool MultiConnection;
        public string SpeedifyRemoteIp;

        public double LatencyMs;
        public double BaseLatencyMs;
        public double MosScore;
        public NatState NatState;



        public double CombinedBandwidthMbps;
        public double ExternalPingMs;

        public double RetransmissionRate;
        public double RetransmissionGrowth;

        public double CongestionScore;
        public double LagScore;
        public double PatternMatchScore;
        public double LearningConfidence;
        public double SignatureAvgQueueGrowth;
        public double LagPressure;
        public string LagEventType;
        public int PendingLagEvents;
        public double BurstMultiplier;
        public double AggressionFactor;
        public bool LearningEnabled;
        public string GamePhase;
        public string NetworkState;
        public double ServerScore;
        public double HealthScore;

        public int ActiveConnections;
        public int FailoverCount;

        public NetworkIssueSource IssueSource;
    }
}
