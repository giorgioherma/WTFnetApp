namespace WTFnetApp
{
    public struct LagPattern
    {
        public double AvgQueueGrowth;
        public double AvgLatencyDrift;
        public double AvgRetransmissionGrowth;
        public double AvgCongestionScore;
        public int Samples;
    }
}
