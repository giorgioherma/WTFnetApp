using System;
using System.Collections.Generic;

namespace WTFnetApp
{
    public sealed class SpeedifyStructuredState
    {
        public DateTime TimeUtc = DateTime.UtcNow;

        public SpeedifyTunnelState Tunnel = new SpeedifyTunnelState();
        public List<SpeedifyLinkState> Links = new List<SpeedifyLinkState>();
        public List<SpeedifySessionWindowStats> SessionWindows = new List<SpeedifySessionWindowStats>();

        public bool HasConnectionStats;
        public bool HasNetworks;
        public bool HasSessionStats;
        public bool HasCurrentState;
        public bool HasConnectedServer;

        public DateTime LastConnectionStatsUtc;
        public DateTime LastNetworksUtc;
        public DateTime LastSessionStatsUtc;
        public DateTime LastCurrentStateUtc;
        public DateTime LastConnectedServerUtc;

        public readonly List<string> ParseWarnings = new List<string>();

        public bool IsCoreComplete
        {
            get
            {
                return HasConnectionStats &&
                       HasNetworks &&
                       HasSessionStats;
            }
        }
    }

    public sealed class SpeedifyTunnelState
    {
        public bool Connected;
        public bool Congested;
        public bool DownloadCongested;
        public bool UploadCongested;

        public int ActiveConnectionCount;
        public int BackupConnectionCount;

        public double LatencyMs = -1;
        public double JitterMs = -1;
        public double RxBps;
        public double TxBps;
        public double TotalBps;

        public string ConnectedServerPublicIp;
        public string ConnectedServerName;

        public string CurrentState;
        public string CurrentMode;
    }

    public sealed class SpeedifyLinkState
    {
        public string Guid;
        public string Key;
        public string Name;
        public string Isp;
        public string IspType;
        public string Description;
        public string Protocol;
        public string Type;

        // refresh tracking (VERY IMPORTANT)
        public int LastSeenGeneration { get; set; }
        public bool SeenInCurrentRefresh { get; set; }

        public bool Connected;
        public bool IsProxy;
        public bool IsAggregate;
        public bool Offline;
        public bool Sleeping;

        public bool Congested;
        public bool DownloadCongested;
        public bool UploadCongested;

        public int Priority;
        public int WorkingPriority;
        public int NumConns;
        public int ConnectionState;

        public double LatencyMs = -1;
        public double JitterMs = -1;
        public double Mos = -1;

        public double RxBps;
        public double TxBps;
        public double TotalBps;

        public long ReceiveBytes;
        public long SendBytes;

        public double RcvEstMbps;
        public double SndEstMbps;

        public string Ip;
        public string LocalIp;
        public string PrivateIp;
        public string RemoteIp;

        public string[] IpList = Array.Empty<string>();
    }

    public sealed class SpeedifySessionWindowStats
    {
        public string WindowKey;

        public long BytesRecv;
        public long BytesSent;
        public long EncryptedBytesRecv;
        public long EncryptedBytesSent;

        public double MaxDownloadMbps;
        public double MaxUploadMbps;

        public int NumFailovers;
        public int NumSessions;
        public long RetransBytes;
        public long TotalConnectedMinutes;

        public int DownBenefitNumConnections;
        public double DownBenefitCombinedMbps;
        public double DownBenefitFastestConnMbps;

        public int UpBenefitNumConnections;
        public double UpBenefitCombinedMbps;
        public double UpBenefitFastestConnMbps;

        public long TunBytesIn;
        public long TunBytesOut;
        public int TunDroppedIncoming;
        public int TunPacketWaits;
        public int TunReadQueue;

        public long PacketHandlerBytesIn;
        public long PacketHandlerBytesOut;
        public int PacketHandlerDroppedOut;
        public int PacketHandlerRetries;

        public int TotalFailoverSaves;
        public int TotalRedundantSaves;
        public int TotalSpeedSaves;
        public int TotalStreams;
        public int UniqueSaves;
    }
}
