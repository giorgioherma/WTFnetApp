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

        public string SpeedifyCurrentState;
        public string SpeedifyCurrentMode;

        public bool SpeedifyHasConnectionStats;
        public bool SpeedifyHasNetworks;
        public bool SpeedifyHasSessionStats;
        public bool SpeedifyHasCurrentState;
        public bool SpeedifyHasConnectedServer;
        public bool SpeedifyStructuredCoreComplete;

        public int SpeedifyParseWarningCount;

        public bool SpeedifyConnectedLegacy;
        public bool SpeedifyCongestedLegacy;
        public bool SpeedifyDownloadCongested;
        public bool SpeedifyUploadCongested;

        public double SpeedifyLatencyMs = -1;
        public double SpeedifyBaseLatencyMs = -1;
        public double SpeedifyLegacyJitterMs = -1;
        public double SpeedifyExternalPingMs = -1;

        public double SpeedifyMosScore = -1;

        public long SpeedifyReceiveBytesPerSecond;
        public long SpeedifySendBytesPerSecond;
        public long SpeedifyReceiveBytes;
        public long SpeedifySendBytes;

        public double SpeedifyLagScore = -1;
        public double SpeedifyQueueDelay = -1;
        public double SpeedifyQueueGrowth = 0;
        public double SpeedifyQueueAcceleration = 0;
        public double SpeedifyLatencyDrift = 0;
        public double SpeedifyRetransmissionGrowth = 0;
        public double SpeedifyQueuePressure = 0;
        public double SpeedifyAggressionFactor = 0;
        public double SpeedifyServerScore = 1.0;
        public double SpeedifyBurstMultiplier = 0;
        public double SpeedifyPatternMatchScore = 0;
        public double SpeedifySignatureAvgQueueGrowth = 0;
        public double SpeedifyLagPressure = 0;
        public double SpeedifyRetransmissionRate = 0;

        public string SpeedifyPredictionConfidence;
        public string SpeedifyDominantSignal;
        public string SpeedifyLagEventType;
        public string SpeedifyGamePhase;
        public string SpeedifyNetworkState;
        public string SpeedifyControlState;
        public string SpeedifyIssueSource;

        public int SpeedifyPendingLagEvents;
        public bool SpeedifyLearningEnabled;

        public List<SpeedifySessionWindowStats> SpeedifySessionWindows = new List<SpeedifySessionWindowStats>();

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
        public double FastPingDisplayedPingMs = -1;
        public double FastPingTunnelLatencyMs = -1;

        public double FastPingConfidence = 0;
        public double FastPingLossRate = 0;
        public double FastPingBadQualityRate = 0;
        public double FastPingPrimarySecondaryDivergenceMs = 0;

        public bool FastPingSpikeDetected;
        public bool FastPingIsStale;
        public bool FastPingPrimaryFresh;
        public bool FastPingSecondaryFresh;
        public bool FastPingIsEvaluationMode;

        public double FastPingPrimaryInstantMs = -1;
        public double FastPingPrimaryStableMs = -1;
        public double FastPingPrimaryFloorMs = -1;
        public double FastPingPrimaryBaselineMs = -1;
        public double FastPingPrimaryJitterMs = -1;
        public double FastPingPrimaryLossRate = 0;
        public double FastPingPrimaryBadQualityRate = 0;
        public double FastPingPrimaryConfidence = 0;

        public double FastPingSecondaryInstantMs = -1;
        public double FastPingSecondaryStableMs = -1;
        public double FastPingSecondaryFloorMs = -1;
        public double FastPingSecondaryBaselineMs = -1;
        public double FastPingSecondaryJitterMs = -1;
        public double FastPingSecondaryLossRate = 0;
        public double FastPingSecondaryBadQualityRate = 0;
        public double FastPingSecondaryConfidence = 0;

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

        public string DisplayJitterText = "J --";
        public string DisplayJitterColorKey = "gray";

        public string DisplayTunnelState = "Unknown";

        public string DisplayNatText = "UNKNOWN";
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

            DisplayJitterText = FormatJitterText(JitterMs);
            DisplayJitterColorKey = GetJitterColorKey(JitterMs);

            DisplayTunnelState = BuildTunnelState();

            DisplayNatText = GetNatText(natState);
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

        private string FormatJitterText(double jitterMs)
        {
            if (jitterMs < 0)
                return "J --";

            return "J " + ((int)Math.Round(jitterMs)).ToString();
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

        private string GetJitterColorKey(double jitterMs)
        {
            if (IsStale || jitterMs < 0)
                return "gray";

            if (jitterMs <= 5)
                return "green";

            if (jitterMs <= 10)
                return "green";

            if (jitterMs <= 18)
                return "gold";

            if (jitterMs <= 28)
                return "yellow";

            return "red";
        }

        private string GetNatText(NatState natState)
        {
            switch (natState)
            {
                case NatState.Stable:
                    return "STABLE";

                case NatState.GracePeriod:
                    return "GRACE";

                case NatState.Unstable:
                    return "UNSTABLE";

                case NatState.Lost:
                    return "LOST";

                default:
                    return "UNKNOWN";
            }
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

            if (SpeedifySessionWindows != null)
            {
                clone.SpeedifySessionWindows = new List<SpeedifySessionWindowStats>(SpeedifySessionWindows.Count);

                for (int i = 0; i < SpeedifySessionWindows.Count; i++)
                {
                    SpeedifySessionWindowStats src = SpeedifySessionWindows[i];
                    if (src == null)
                    {
                        clone.SpeedifySessionWindows.Add(null);
                        continue;
                    }

                    clone.SpeedifySessionWindows.Add(new SpeedifySessionWindowStats
                    {
                        WindowKey = src.WindowKey,

                        BytesRecv = src.BytesRecv,
                        BytesSent = src.BytesSent,
                        EncryptedBytesRecv = src.EncryptedBytesRecv,
                        EncryptedBytesSent = src.EncryptedBytesSent,

                        MaxDownloadMbps = src.MaxDownloadMbps,
                        MaxUploadMbps = src.MaxUploadMbps,

                        NumFailovers = src.NumFailovers,
                        NumSessions = src.NumSessions,
                        RetransBytes = src.RetransBytes,
                        TotalConnectedMinutes = src.TotalConnectedMinutes,

                        DownBenefitNumConnections = src.DownBenefitNumConnections,
                        DownBenefitCombinedMbps = src.DownBenefitCombinedMbps,
                        DownBenefitFastestConnMbps = src.DownBenefitFastestConnMbps,

                        UpBenefitNumConnections = src.UpBenefitNumConnections,
                        UpBenefitCombinedMbps = src.UpBenefitCombinedMbps,
                        UpBenefitFastestConnMbps = src.UpBenefitFastestConnMbps,

                        TunBytesIn = src.TunBytesIn,
                        TunBytesOut = src.TunBytesOut,
                        TunDroppedIncoming = src.TunDroppedIncoming,
                        TunPacketWaits = src.TunPacketWaits,
                        TunReadQueue = src.TunReadQueue,

                        PacketHandlerBytesIn = src.PacketHandlerBytesIn,
                        PacketHandlerBytesOut = src.PacketHandlerBytesOut,
                        PacketHandlerDroppedOut = src.PacketHandlerDroppedOut,
                        PacketHandlerRetries = src.PacketHandlerRetries,

                        TotalFailoverSaves = src.TotalFailoverSaves,
                        TotalRedundantSaves = src.TotalRedundantSaves,
                        TotalSpeedSaves = src.TotalSpeedSaves,
                        TotalStreams = src.TotalStreams,
                        UniqueSaves = src.UniqueSaves
                    });
                }
            }

            return clone;
        }
    }
}