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
        // =========================
        // LATENCY
        // =========================

        public double Recent;
        public double Average;
        public double Max;

        // =========================
        // VARIABILITY
        // =========================

        public double Jitter;
        public double Variance;
        public double MicroJitter;

        // =========================
        // NETWORK HEALTH
        // =========================

        public double PacketLoss;
        public double LossBurst;
        public bool Congested;
        public int Priority;
        public long ReceiveBytes;
        public long SendBytes;

        // =========================
        // BASELINE
        // =========================

        public double DynamicBase;

        // =========================
        // QUEUE DETECTION
        // =========================

        public double QueueDelay;      // Recent - DynamicBase
        public double QueueGrowth;     // queue slope
        public double QueueAcceleration;
        public double QueuePressure;

        // =========================
        // TREND ANALYSIS
        // =========================

        public double LatencySlope;
        public double Trend;
        public double LatencyDrift;

        // =========================
        // BURST DETECTION
        // =========================

        public double LatencyDelta;

        // =========================
        // ADAPTER DETAILS
        // =========================

        public double Adapter1Latency;
        public double Adapter2Latency;

        // =========================
        // SPEEDIFY EXTENSIONS
        // =========================

        public double LatencyMs;
        public double BaseLatencyMs;
        public double QueueDelayMs;

        public double MosScore;

        public bool IsCongested;
        public bool IsDownloadCongested;
        public bool IsUploadCongested;

        public long ReceiveBytesPerSecond;
        public long SendBytesPerSecond;

        public double CombinedBandwidthMbps;
        public double ExternalPingMs;

        public double RetransmissionRate;
        public double RetransmissionGrowth;

        public double CongestionScore;
        public double CongestionMomentum;
        public double LagScore;
        public double LagProbability;
        public string PredictionConfidence;
        public string DominantSignal;
        public double HealthScore;

        public int ActiveConnections;
        public int FailoverCount;

        public NetworkIssueSource IssueSource;
    }
}
