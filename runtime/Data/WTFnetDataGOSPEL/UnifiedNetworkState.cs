using System;
using System.Collections.Generic;

namespace WTFnetApp
{
    public enum UnifiedNetworkSourceKind
    {
        None = 0,
        FastPing = 1,
        Speedify = 2
    }

    public enum UnifiedNetworkHealthState
    {
        Unknown = 0,
        Healthy = 1,
        OffBaseline = 2,
        Warning = 3,
        Critical = 4,
        Stale = 5
    }

    public enum UnifiedIssueSource
    {
        Unknown = 0,
        None = 1,
        FastPing = 2,
        SpeedifyAggregate = 3,
        SpeedifyLink = 4,
        LocalRoute = 5,
        Tunnel = 6,
        Bonding = 7,
        Congestion = 8
    }

    public enum UnifiedRecommendedAction
    {
        None = 0,
        Monitor = 1,
        Wait = 2,
        ManualRetest = 3,
        ConsiderServerSwitch = 4,
        ConsiderBondingChange = 5,
        TunnelCheck = 6
    }

    public sealed class UnifiedLinkState
    {
        public string Id;
        public string Guid;
        public string Key;
        public string Name;
        public string Isp;
        public string Protocol;
        public string LocalIp;
        public string PrivateIp;
        public string RemoteIp;
        public string Role;

        public bool Connected;
        public bool IsPrimary;
        public bool IsBackup;
        public bool IsProxy;
        public bool IsAggregate;

        public bool Congested;
        public bool DownloadCongested;
        public bool UploadCongested;

        public double LatencyMs = -1;
        public double JitterMs = -1;
        public double RxBps = 0;
        public double TxBps = 0;
        public double TotalBps = 0;
        public double Mos = -1;

        public double QualityScore = -1;
        public double StabilityScore = -1;
        public double ResponsivenessScore = -1;
        public double ConfidenceScore = -1;
    }

    public sealed class UnifiedNetworkState
    {
        public DateTime TimeUtc = DateTime.UtcNow;

        // =========================================================
        // SOURCE / RESOLUTION CONTEXT
        // =========================================================

        public UnifiedNetworkSourceKind SourceKind = UnifiedNetworkSourceKind.None;

        public bool HasFastPing;
        public bool HasSpeedify;
        public bool SpeedifyAvailable;
        public bool SpeedifyTelemetryFresh;

        public string PrimaryLatencySource;
        public string PrimaryJitterSource;
        public string PrimaryConfidenceSource;
        public string Notes;

        // =========================================================
        // CORE RESOLVED QUALITY METRICS
        // =========================================================

        public double LatencyMs = -1;
        public double JitterMs = -1;
        public double JitterPressure = 0;
        public double BaselineLatencyMs = -1;
        public double FloorLatencyMs = -1;
        public double DisplayedPingMs = -1;

        public double LossRate = 0;
        public double BadQualityRate = 0;
        public double Confidence = 0;

        public bool SpikeDetected;
        public bool IsStale;

        public UnifiedNetworkHealthState HealthState = UnifiedNetworkHealthState.Unknown;
        public string HealthReason;

        // =========================================================
        // TREND / PREDICTION INPUTS
        // =========================================================

        public double QueueDelayMs = -1;
        public double LatencyDriftMs = 0;
        public double JitterDriftMs = 0;

        public double SpikeRate = 0;
        public double LossRateTrend = 0;
        public double ConfidenceTrend = 0;

        public double RecentLatencyMs = -1;
        public double AverageLatencyMs = -1;
        public double MaxLatencyMs = -1;
        public double MicroJitterMs = -1;
        public double DynamicBaseMs = -1;

        // =========================================================
        // SPEEDIFY / TOPOLOGY / TUNNEL
        // =========================================================

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

        // =========================================================
        // FASTPING DETAILS
        // =========================================================

        public string FastPingPrimaryTarget;
        public string FastPingSecondaryTarget;

        public double FastPingInstantMs = -1;
        public double FastPingStableMs = -1;
        public double FastPingBaselineMs = -1;
        public double FastPingFloorMs = -1;
        public double FastPingJitterMs = -1;
        public double FastPingConfidence = 0;
        public double FastPingLossRate = 0;
        public double FastPingBadQualityRate = 0;
        public double FastPingPrimarySecondaryDivergenceMs = 0;

        public bool FastPingSpikeDetected;
        public bool FastPingIsStale;

        public string FastPingHealthState;

        // =========================================================
        // LINK-LEVEL RESOLVED VIEW
        // =========================================================

        public List<UnifiedLinkState> Links = new List<UnifiedLinkState>();

        // =========================================================
        // DISPLAY / PRESENTATION SEMANTICS
        // =========================================================

        public string DisplayPingText = "-- ms";
        public string DisplayPingColorKey = "gray";
        public string DisplayTunnelState = "Unknown";

        public string DisplayNatText = "Unknown";
        public string DisplayNatColorKey = "gray";

        // =========================================================
        // COMBINED SCORES (computed elsewhere, stored here)
        // =========================================================

        public double QualityScore = -1;
        public double StabilityScore = -1;
        public double ResponsivenessScore = -1;
        public double CongestionScore = -1;
        public double ConfidenceScore = -1;

        public double LagRiskScore = -1;
        public double SpikeRiskScore = -1;
        public double DisconnectRiskScore = -1;
        public double RecoveryScore = -1;

        public double TunnelQualityScore = -1;
        public double BondingBenefitScore = -1;
        public double BondingHelpfulnessScore = -1;
        public double LinkBalanceScore = -1;
        public double BestLinkScore = -1;
        public double WorstLinkScore = -1;

        public double ServerSwitchHelpfulnessScore = -1;
        public double ThresholdTightnessScore = -1;

        // =========================================================
        // DECISION / PREDICTION OUTPUTS
        // =========================================================

        public double LagProbability = -1;
        public double PredictionConfidence = -1;

        public UnifiedIssueSource IssueSource = UnifiedIssueSource.Unknown;
        public string IssueReason;

        public UnifiedRecommendedAction RecommendedAction = UnifiedRecommendedAction.None;
        public string RecommendedActionReason;

        // =========================================================
        // LEGACY / PASSTHROUGH COMPATIBILITY
        // =========================================================

        public double PacketLoss = 0;

        // =========================================================
        // CONVENIENCE
        // =========================================================

        public bool HasUsableLatency
        {
            get { return LatencyMs > 0; }
        }

        public bool IsBonded
        {
            get { return ActiveConnectionCount >= 2; }
        }

        public bool HasLinkData
        {
            get { return Links != null && Links.Count > 0; }
        }

        public void RefreshDisplayState(NatState natState)
        {
            double ping = DisplayedPingMs > 0 ? DisplayedPingMs : LatencyMs;

            DisplayPingText = FormatPingText(ping);
            DisplayPingColorKey = GetPingColorKey(ping);
            DisplayTunnelState = BuildTunnelState();

            DisplayNatText = natState.ToString();
            DisplayNatColorKey = GetNatColorKey(natState);
        }

        private string BuildTunnelState()
        {
            if (SourceKind == UnifiedNetworkSourceKind.FastPing)
                return "No Tunnel";

            if (TunnelConnected)
                return "Tunnel OK";

            if (HasSpeedify || SpeedifyAvailable)
                return "Tunnel Down";

            return "Unknown";
        }

        private string FormatPingText(double pingMs)
        {
            if (pingMs <= 0)
                return "-- ms";

            return ((int)Math.Round(pingMs)).ToString() + " ms";
        }

        private string GetPingColorKey(double pingMs)
        {
            if (SourceKind == UnifiedNetworkSourceKind.FastPing)
            {
                if (pingMs > 0)
                {
                    if (pingMs <= 50)
                        return "green";

                    if (pingMs <= 90)
                        return "yellow";

                    return "red";
                }

                return "gray";
            }

            if (IsStale || pingMs <= 0)
                return "gray";

            switch (HealthState)
            {
                case UnifiedNetworkHealthState.Critical:
                    return "red";

                case UnifiedNetworkHealthState.Warning:
                    return "red";

                case UnifiedNetworkHealthState.OffBaseline:
                    return "yellow";

                case UnifiedNetworkHealthState.Healthy:
                    return "green";

                case UnifiedNetworkHealthState.Stale:
                    return "gray";

                case UnifiedNetworkHealthState.Unknown:
                default:
                    break;
            }

            if (pingMs <= 50)
                return "green";

            if (pingMs <= 90)
                return "yellow";

            return "red";
        }

        private string GetNatColorKey(NatState natState)
        {
            switch (natState)
            {
                case NatState.Lost:
                    return "red";

                case NatState.Unstable:
                    return "yellow";

                case NatState.GracePeriod:
                    return "gold";

                case NatState.Stable:
                    return "green";

                default:
                    return "gray";
            }
        }

        public UnifiedNetworkState Clone()
        {
            UnifiedNetworkState clone = (UnifiedNetworkState)MemberwiseClone();

            if (Links != null)
            {
                clone.Links = new List<UnifiedLinkState>(Links.Count);

                for (int i = 0; i < Links.Count; i++)
                {
                    UnifiedLinkState src = Links[i];
                    if (src == null)
                    {
                        clone.Links.Add(null);
                        continue;
                    }

                    UnifiedLinkState copy = new UnifiedLinkState
                    {
                        Id = src.Id,
                        Guid = src.Guid,
                        Key = src.Key,
                        Name = src.Name,
                        Isp = src.Isp,
                        Protocol = src.Protocol,
                        LocalIp = src.LocalIp,
                        PrivateIp = src.PrivateIp,
                        RemoteIp = src.RemoteIp,
                        Role = src.Role,

                        Connected = src.Connected,
                        IsPrimary = src.IsPrimary,
                        IsBackup = src.IsBackup,
                        IsProxy = src.IsProxy,
                        IsAggregate = src.IsAggregate,

                        Congested = src.Congested,
                        DownloadCongested = src.DownloadCongested,
                        UploadCongested = src.UploadCongested,

                        LatencyMs = src.LatencyMs,
                        JitterMs = src.JitterMs,
                        RxBps = src.RxBps,
                        TxBps = src.TxBps,
                        TotalBps = src.TotalBps,
                        Mos = src.Mos,

                        QualityScore = src.QualityScore,
                        StabilityScore = src.StabilityScore,
                        ResponsivenessScore = src.ResponsivenessScore,
                        ConfidenceScore = src.ConfidenceScore
                    };

                    clone.Links.Add(copy);
                }
            }

            return clone;
        }
    }
}