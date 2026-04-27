using System;

namespace WTFnetApp
{
    public sealed class MiniSpeedifyMiniSnap
    {
        public DateTime TimeUtc = DateTime.MinValue;
        public bool HasConnectionStats;
        public bool TunnelAggregatePresent;
        public double AggregateLatencyMs = -1;
        public double AggregateJitterMs = -1;
        public double ReportedLatencyMs = -1;
        public double BaseLatencyMs = -1;
        public double DynamicBaseMs = -1;
        public bool AggregateCongested;
        public int ActiveConnectionCount = -1;
        public int BackupConnectionCount = -1;
        public bool IsFresh;
        public int ParseWarningCount;
    }
}