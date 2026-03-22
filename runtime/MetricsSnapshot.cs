using System;
using System.Collections.Generic;

namespace WTFnetApp
{
    public sealed class MetricsSnapshot
    {
        public DateTime TimeUtc = DateTime.UtcNow;

        public bool TunnelConnected;
        public int ActiveConnectionCount;
        public int BackupConnectionCount;

        public double AggregateLatencyMs = -1;
        public double AggregateJitterMs = -1;
        public double AggregateRxBps;
        public double AggregateTxBps;
        public double AggregateTotalBps;
        public bool AggregateCongested;

        public string PrimaryRemoteIp;
        public string ConnectedServerPublicIp;
        public string ConnectedServerName;

        public readonly List<LinkSnapshot> Links = new List<LinkSnapshot>();
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