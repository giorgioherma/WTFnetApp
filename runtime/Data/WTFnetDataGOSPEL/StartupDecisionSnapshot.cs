using System;

namespace WTFnetApp
{
    public sealed class StartupDecisionSnapshot
    {
        public bool TunnelConnected;
        public bool HasFreshTelemetry;
        public int ActiveConnectionCount;
        public DateTime TimeUtc;
        public double AggregateLatencyMs = -1;
        public double AggregateJitterMs = -1;
        public double QueueDelayMs = -1;
        public double ReportedLatencyMs = -1;
        public bool AggregateCongested;
        public bool DownloadCongested;
        public bool UploadCongested;
    }
}
