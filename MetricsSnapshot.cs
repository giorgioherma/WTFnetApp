namespace WTFnetApp
{
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

        // =========================
        // TREND ANALYSIS
        // =========================

        public double LatencySlope;
        public double Trend;

        // =========================
        // BURST DETECTION
        // =========================

        public double LatencyDelta;

        // =========================
        // ADAPTER DETAILS
        // =========================

        public double Adapter1Latency;
        public double Adapter2Latency;
    }
}
