using System;

namespace WTFnetApp
{
    // Lightweight startup-focused snapshot.
    // Built from report_connection_stats parser flow (not a raw payload mirror).
    // Not the full Speedify runtime truth model.
    public sealed class MiniSpeedifyMiniSnap
    {
        public DateTime TimeUtc = DateTime.MinValue;
        public bool HasConnectionStats;
        public bool TunnelAggregatePresent;
        public double AggregateLatencyMs = -1;
        public double AggregateJitterMs = -1;
        public double ReportedLatencyMs = -1;
        // Parser-derived baseline from connection_stats sequence history.
        public double BaseLatencyMs = -1;
        // Mirrors parser-derived dynamic baseline from connection_stats sequence history.
        public double DynamicBaseMs = -1;
        public bool AggregateCongested;
        public int ActiveConnectionCount = -1;
        public int BackupConnectionCount = -1;
        public bool IsFresh;
        public int ParseWarningCount;
    }
}
