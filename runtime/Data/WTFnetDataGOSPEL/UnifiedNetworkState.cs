using System;
using System.Collections.Generic;

namespace WTFnetApp
{
    public enum UnifiedNetworkSourceKind
    {
        None = 0,
        FastPing = 1,
        Speedify = 2,
        FastPingWithSpeedify = 3
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

    public sealed class FastPingTelemetrySnapshot
    {
        public string PrimaryTarget;
        public string SecondaryTarget;

        public double InstantMs = -1;
        public double StableMs = -1;
        public double BaselineMs = -1;
        public double FloorMs = -1;
        public double JitterMs = -1;
        public double DisplayedPingMs = -1;
        public double TunnelLatencyMs = -1;

        public double Confidence = 0;
        public double LossRate = 0;
        public double BadQualityRate = 0;
        public double PrimarySecondaryDivergenceMs = 0;

        public bool SpikeDetected;
        public bool IsStale;
        public bool PrimaryFresh;
        public bool SecondaryFresh;
        public bool IsEvaluationMode;

        public double PrimaryInstantMs = -1;
        public double PrimaryStableMs = -1;
        public double PrimaryFloorMs = -1;
        public double PrimaryBaselineMs = -1;
        public double PrimaryJitterMs = -1;
        public double PrimaryLossRate = 0;
        public double PrimaryBadQualityRate = 0;
        public double PrimaryConfidence = 0;

        public double SecondaryInstantMs = -1;
        public double SecondaryStableMs = -1;
        public double SecondaryFloorMs = -1;
        public double SecondaryBaselineMs = -1;
        public double SecondaryJitterMs = -1;
        public double SecondaryLossRate = 0;
        public double SecondaryBadQualityRate = 0;
        public double SecondaryConfidence = 0;

        public string HealthState;
    }

    public sealed class SpeedifyTelemetrySnapshot
    {
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

        public string CurrentState;
        public string CurrentMode;

        public bool HasConnectionStats;
        public bool HasNetworks;
        public bool HasSessionStats;
        public bool HasCurrentState;
        public bool HasConnectedServer;
        public bool StructuredCoreComplete;

        public int ParseWarningCount;

        public bool ConnectedLegacy;
        public bool CongestedLegacy;
        public bool DownloadCongested;
        public bool UploadCongested;

        public double LatencyMs = -1;
        public double BaseLatencyMs = -1;
        public double LegacyJitterMs = -1;
        public double ExternalPingMs = -1;

        public double MosScore = -1;

        public long ReceiveBytesPerSecond;
        public long SendBytesPerSecond;
        public long ReceiveBytes;
        public long SendBytes;

        public double LagScore = -1;
        public double QueueDelay = -1;
        public double QueueGrowth = 0;
        public double QueueAcceleration = 0;
        public double LatencyDrift = 0;
        public double RetransmissionGrowth = 0;
        public double QueuePressure = 0;
        public double AggressionFactor = 0;
        public double ServerScore = 1.0;
        public double BurstMultiplier = 0;
        public double PatternMatchScore = 0;
        public double SignatureAvgQueueGrowth = 0;
        public double LagPressure = 0;
        public double RetransmissionRate = 0;

        public string PredictionConfidence;
        public string DominantSignal;
        public string LagEventType;
        public string GamePhase;
        public string NetworkState;
        public string ControlState;
        public string IssueSource;

        public int PendingLagEvents;
        public bool LearningEnabled;

        public readonly List<SpeedifySessionWindowStats> SessionWindows = new List<SpeedifySessionWindowStats>();
    }


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

    public sealed class UnifiedNetworkState
    {
        public DateTime TimeUtc = DateTime.UtcNow;

        public UnifiedNetworkSourceKind SourceKind = UnifiedNetworkSourceKind.None;

        public bool HasFastPing;
        public bool HasSpeedify;
        public bool SpeedifyAvailable;
        public bool SpeedifyTelemetryFresh;

        public string PrimaryLatencySource;
        public string PrimaryJitterSource;
        public string PrimaryConfidenceSource;
        public string Notes;

        // resolved truth only
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

        // minimal combined context still consumed
        public double QueueDelayMs = -1;
        public double LatencyDriftMs = 0;
        public double RecentLatencyMs = -1;
        public double AverageLatencyMs = -1;
        public double MaxLatencyMs = -1;
        public double MicroJitterMs = -1;
        public double DynamicBaseMs = -1;

        public double PacketLoss = 0;
        public double CongestionScore = -1;
        public double LagProbability = -1;
        public double PredictionConfidence = -1;
        public double DeltaFastPingVsSpeedifyMs = -1;

        public FastPingTelemetrySnapshot FastPingTelemetry = new FastPingTelemetrySnapshot();
        public SpeedifyTelemetrySnapshot SpeedifyTelemetry = new SpeedifyTelemetrySnapshot();
        public StartupDecisionSnapshot StartupDecision = new StartupDecisionSnapshot();

        public List<UnifiedLinkState> Links = new List<UnifiedLinkState>();

        public string DisplayPingText = "-- ms";
        public string DisplayPingColorKey = "gray";
        public string DisplayJitterText = "J --";
        public string DisplayJitterColorKey = "gray";
        public string DisplayTunnelState = "Unknown";
        public string DisplayNatText = "UNKNOWN";
        public string DisplayNatColorKey = "gray";

        public double QualityScore = -1;
        public double StabilityScore = -1;
        public double ResponsivenessScore = -1;
        public double ConfidenceScore = -1;

        // compatibility accessors for existing consumers/logging
        public bool TunnelConnected { get => SpeedifyTelemetry.TunnelConnected; set => SpeedifyTelemetry.TunnelConnected = value; }
        public bool AggregateCongested { get => SpeedifyTelemetry.AggregateCongested; set => SpeedifyTelemetry.AggregateCongested = value; }
        public int ActiveConnectionCount { get => SpeedifyTelemetry.ActiveConnectionCount; set => SpeedifyTelemetry.ActiveConnectionCount = value; }
        public int BackupConnectionCount { get => SpeedifyTelemetry.BackupConnectionCount; set => SpeedifyTelemetry.BackupConnectionCount = value; }
        public double AggregateLatencyMs { get => SpeedifyTelemetry.AggregateLatencyMs; set => SpeedifyTelemetry.AggregateLatencyMs = value; }
        public double AggregateJitterMs { get => SpeedifyTelemetry.AggregateJitterMs; set => SpeedifyTelemetry.AggregateJitterMs = value; }
        public double AggregateRxBps { get => SpeedifyTelemetry.AggregateRxBps; set => SpeedifyTelemetry.AggregateRxBps = value; }
        public double AggregateTxBps { get => SpeedifyTelemetry.AggregateTxBps; set => SpeedifyTelemetry.AggregateTxBps = value; }
        public double AggregateTotalBps { get => SpeedifyTelemetry.AggregateTotalBps; set => SpeedifyTelemetry.AggregateTotalBps = value; }
        public string ConnectedServerName { get => SpeedifyTelemetry.ConnectedServerName; set => SpeedifyTelemetry.ConnectedServerName = value; }
        public string ConnectedServerPublicIp { get => SpeedifyTelemetry.ConnectedServerPublicIp; set => SpeedifyTelemetry.ConnectedServerPublicIp = value; }
        public string PrimaryRemoteIp { get => SpeedifyTelemetry.PrimaryRemoteIp; set => SpeedifyTelemetry.PrimaryRemoteIp = value; }
        public string SpeedifyRemoteIp { get => SpeedifyTelemetry.SpeedifyRemoteIp; set => SpeedifyTelemetry.SpeedifyRemoteIp = value; }
        public double SpeedifyLatencyMs { get => SpeedifyTelemetry.LatencyMs; set => SpeedifyTelemetry.LatencyMs = value; }
        public bool SpeedifyDownloadCongested { get => SpeedifyTelemetry.DownloadCongested; set => SpeedifyTelemetry.DownloadCongested = value; }
        public bool SpeedifyUploadCongested { get => SpeedifyTelemetry.UploadCongested; set => SpeedifyTelemetry.UploadCongested = value; }
        public string SpeedifyCurrentMode { get => SpeedifyTelemetry.CurrentMode; set => SpeedifyTelemetry.CurrentMode = value; }
        public string SpeedifyCurrentState { get => SpeedifyTelemetry.CurrentState; set => SpeedifyTelemetry.CurrentState = value; }

        public double Adapter1Latency { get => SpeedifyTelemetry.Adapter1Latency; set => SpeedifyTelemetry.Adapter1Latency = value; }
        public double Adapter2Latency { get => SpeedifyTelemetry.Adapter2Latency; set => SpeedifyTelemetry.Adapter2Latency = value; }
        public bool SpeedifyStructuredCoreComplete { get => SpeedifyTelemetry.StructuredCoreComplete; set => SpeedifyTelemetry.StructuredCoreComplete = value; }
        public bool SpeedifyHasConnectionStats { get => SpeedifyTelemetry.HasConnectionStats; set => SpeedifyTelemetry.HasConnectionStats = value; }
        public bool SpeedifyHasNetworks { get => SpeedifyTelemetry.HasNetworks; set => SpeedifyTelemetry.HasNetworks = value; }
        public bool SpeedifyHasSessionStats { get => SpeedifyTelemetry.HasSessionStats; set => SpeedifyTelemetry.HasSessionStats = value; }
        public bool SpeedifyHasCurrentState { get => SpeedifyTelemetry.HasCurrentState; set => SpeedifyTelemetry.HasCurrentState = value; }
        public bool SpeedifyHasConnectedServer { get => SpeedifyTelemetry.HasConnectedServer; set => SpeedifyTelemetry.HasConnectedServer = value; }
        public int SpeedifyParseWarningCount { get => SpeedifyTelemetry.ParseWarningCount; set => SpeedifyTelemetry.ParseWarningCount = value; }
        public double SpeedifyBaseLatencyMs { get => SpeedifyTelemetry.BaseLatencyMs; set => SpeedifyTelemetry.BaseLatencyMs = value; }
        public double SpeedifyLegacyJitterMs { get => SpeedifyTelemetry.LegacyJitterMs; set => SpeedifyTelemetry.LegacyJitterMs = value; }
        public double SpeedifyExternalPingMs { get => SpeedifyTelemetry.ExternalPingMs; set => SpeedifyTelemetry.ExternalPingMs = value; }
        public double SpeedifyMosScore { get => SpeedifyTelemetry.MosScore; set => SpeedifyTelemetry.MosScore = value; }
        public bool SpeedifyConnectedLegacy { get => SpeedifyTelemetry.ConnectedLegacy; set => SpeedifyTelemetry.ConnectedLegacy = value; }
        public bool SpeedifyCongestedLegacy { get => SpeedifyTelemetry.CongestedLegacy; set => SpeedifyTelemetry.CongestedLegacy = value; }
        public long SpeedifyReceiveBytesPerSecond { get => SpeedifyTelemetry.ReceiveBytesPerSecond; set => SpeedifyTelemetry.ReceiveBytesPerSecond = value; }
        public long SpeedifySendBytesPerSecond { get => SpeedifyTelemetry.SendBytesPerSecond; set => SpeedifyTelemetry.SendBytesPerSecond = value; }
        public long SpeedifyReceiveBytes { get => SpeedifyTelemetry.ReceiveBytes; set => SpeedifyTelemetry.ReceiveBytes = value; }
        public long SpeedifySendBytes { get => SpeedifyTelemetry.SendBytes; set => SpeedifyTelemetry.SendBytes = value; }
        public double SpeedifyLagScore { get => SpeedifyTelemetry.LagScore; set => SpeedifyTelemetry.LagScore = value; }
        public double SpeedifyQueueDelay { get => SpeedifyTelemetry.QueueDelay; set => SpeedifyTelemetry.QueueDelay = value; }
        public double SpeedifyQueueGrowth { get => SpeedifyTelemetry.QueueGrowth; set => SpeedifyTelemetry.QueueGrowth = value; }
        public double SpeedifyQueueAcceleration { get => SpeedifyTelemetry.QueueAcceleration; set => SpeedifyTelemetry.QueueAcceleration = value; }
        public double SpeedifyLatencyDrift { get => SpeedifyTelemetry.LatencyDrift; set => SpeedifyTelemetry.LatencyDrift = value; }
        public double SpeedifyRetransmissionGrowth { get => SpeedifyTelemetry.RetransmissionGrowth; set => SpeedifyTelemetry.RetransmissionGrowth = value; }
        public double SpeedifyQueuePressure { get => SpeedifyTelemetry.QueuePressure; set => SpeedifyTelemetry.QueuePressure = value; }
        public double SpeedifyAggressionFactor { get => SpeedifyTelemetry.AggressionFactor; set => SpeedifyTelemetry.AggressionFactor = value; }
        public double SpeedifyServerScore { get => SpeedifyTelemetry.ServerScore; set => SpeedifyTelemetry.ServerScore = value; }
        public string SpeedifyPredictionConfidence { get => SpeedifyTelemetry.PredictionConfidence; set => SpeedifyTelemetry.PredictionConfidence = value; }
        public string SpeedifyDominantSignal { get => SpeedifyTelemetry.DominantSignal; set => SpeedifyTelemetry.DominantSignal = value; }
        public string SpeedifyLagEventType { get => SpeedifyTelemetry.LagEventType; set => SpeedifyTelemetry.LagEventType = value; }
        public string SpeedifyGamePhase { get => SpeedifyTelemetry.GamePhase; set => SpeedifyTelemetry.GamePhase = value; }
        public string SpeedifyNetworkState { get => SpeedifyTelemetry.NetworkState; set => SpeedifyTelemetry.NetworkState = value; }
        public string SpeedifyControlState { get => SpeedifyTelemetry.ControlState; set => SpeedifyTelemetry.ControlState = value; }
        public int SpeedifyPendingLagEvents { get => SpeedifyTelemetry.PendingLagEvents; set => SpeedifyTelemetry.PendingLagEvents = value; }
        public double SpeedifyBurstMultiplier { get => SpeedifyTelemetry.BurstMultiplier; set => SpeedifyTelemetry.BurstMultiplier = value; }
        public bool SpeedifyLearningEnabled { get => SpeedifyTelemetry.LearningEnabled; set => SpeedifyTelemetry.LearningEnabled = value; }
        public double SpeedifyPatternMatchScore { get => SpeedifyTelemetry.PatternMatchScore; set => SpeedifyTelemetry.PatternMatchScore = value; }
        public double SpeedifySignatureAvgQueueGrowth { get => SpeedifyTelemetry.SignatureAvgQueueGrowth; set => SpeedifyTelemetry.SignatureAvgQueueGrowth = value; }
        public double SpeedifyLagPressure { get => SpeedifyTelemetry.LagPressure; set => SpeedifyTelemetry.LagPressure = value; }
        public double SpeedifyRetransmissionRate { get => SpeedifyTelemetry.RetransmissionRate; set => SpeedifyTelemetry.RetransmissionRate = value; }
        public string SpeedifyIssueSource { get => SpeedifyTelemetry.IssueSource; set => SpeedifyTelemetry.IssueSource = value; }
        public List<SpeedifySessionWindowStats> SpeedifySessionWindows => SpeedifyTelemetry.SessionWindows;

        public double FastPingDisplayedPingMs { get => FastPingTelemetry.DisplayedPingMs; set => FastPingTelemetry.DisplayedPingMs = value; }
        public double FastPingInstantMs { get => FastPingTelemetry.InstantMs; set => FastPingTelemetry.InstantMs = value; }
        public double FastPingStableMs { get => FastPingTelemetry.StableMs; set => FastPingTelemetry.StableMs = value; }
        public double FastPingBaselineMs { get => FastPingTelemetry.BaselineMs; set => FastPingTelemetry.BaselineMs = value; }
        public double FastPingFloorMs { get => FastPingTelemetry.FloorMs; set => FastPingTelemetry.FloorMs = value; }
        public double FastPingJitterMs { get => FastPingTelemetry.JitterMs; set => FastPingTelemetry.JitterMs = value; }
        public double FastPingConfidence { get => FastPingTelemetry.Confidence; set => FastPingTelemetry.Confidence = value; }
        public double FastPingLossRate { get => FastPingTelemetry.LossRate; set => FastPingTelemetry.LossRate = value; }
        public double FastPingBadQualityRate { get => FastPingTelemetry.BadQualityRate; set => FastPingTelemetry.BadQualityRate = value; }
        public bool FastPingSpikeDetected { get => FastPingTelemetry.SpikeDetected; set => FastPingTelemetry.SpikeDetected = value; }
        public bool FastPingIsStale { get => FastPingTelemetry.IsStale; set => FastPingTelemetry.IsStale = value; }
        public bool FastPingPrimaryFresh { get => FastPingTelemetry.PrimaryFresh; set => FastPingTelemetry.PrimaryFresh = value; }
        public bool FastPingSecondaryFresh { get => FastPingTelemetry.SecondaryFresh; set => FastPingTelemetry.SecondaryFresh = value; }
        public bool FastPingIsEvaluationMode { get => FastPingTelemetry.IsEvaluationMode; set => FastPingTelemetry.IsEvaluationMode = value; }
        public double FastPingPrimarySecondaryDivergenceMs { get => FastPingTelemetry.PrimarySecondaryDivergenceMs; set => FastPingTelemetry.PrimarySecondaryDivergenceMs = value; }
        public double FastPingTunnelLatencyMs { get => FastPingTelemetry.TunnelLatencyMs; set => FastPingTelemetry.TunnelLatencyMs = value; }
        public string FastPingPrimaryTarget { get => FastPingTelemetry.PrimaryTarget; set => FastPingTelemetry.PrimaryTarget = value; }
        public string FastPingSecondaryTarget { get => FastPingTelemetry.SecondaryTarget; set => FastPingTelemetry.SecondaryTarget = value; }
        public double FastPingPrimaryInstantMs { get => FastPingTelemetry.PrimaryInstantMs; set => FastPingTelemetry.PrimaryInstantMs = value; }
        public double FastPingPrimaryStableMs { get => FastPingTelemetry.PrimaryStableMs; set => FastPingTelemetry.PrimaryStableMs = value; }
        public double FastPingPrimaryBaselineMs { get => FastPingTelemetry.PrimaryBaselineMs; set => FastPingTelemetry.PrimaryBaselineMs = value; }
        public double FastPingPrimaryFloorMs { get => FastPingTelemetry.PrimaryFloorMs; set => FastPingTelemetry.PrimaryFloorMs = value; }
        public double FastPingPrimaryJitterMs { get => FastPingTelemetry.PrimaryJitterMs; set => FastPingTelemetry.PrimaryJitterMs = value; }
        public double FastPingPrimaryConfidence { get => FastPingTelemetry.PrimaryConfidence; set => FastPingTelemetry.PrimaryConfidence = value; }
        public double FastPingPrimaryLossRate { get => FastPingTelemetry.PrimaryLossRate; set => FastPingTelemetry.PrimaryLossRate = value; }
        public double FastPingPrimaryBadQualityRate { get => FastPingTelemetry.PrimaryBadQualityRate; set => FastPingTelemetry.PrimaryBadQualityRate = value; }
        public double FastPingSecondaryInstantMs { get => FastPingTelemetry.SecondaryInstantMs; set => FastPingTelemetry.SecondaryInstantMs = value; }
        public double FastPingSecondaryStableMs { get => FastPingTelemetry.SecondaryStableMs; set => FastPingTelemetry.SecondaryStableMs = value; }
        public double FastPingSecondaryBaselineMs { get => FastPingTelemetry.SecondaryBaselineMs; set => FastPingTelemetry.SecondaryBaselineMs = value; }
        public double FastPingSecondaryFloorMs { get => FastPingTelemetry.SecondaryFloorMs; set => FastPingTelemetry.SecondaryFloorMs = value; }
        public double FastPingSecondaryJitterMs { get => FastPingTelemetry.SecondaryJitterMs; set => FastPingTelemetry.SecondaryJitterMs = value; }
        public double FastPingSecondaryConfidence { get => FastPingTelemetry.SecondaryConfidence; set => FastPingTelemetry.SecondaryConfidence = value; }
        public double FastPingSecondaryLossRate { get => FastPingTelemetry.SecondaryLossRate; set => FastPingTelemetry.SecondaryLossRate = value; }
        public double FastPingSecondaryBadQualityRate { get => FastPingTelemetry.SecondaryBadQualityRate; set => FastPingTelemetry.SecondaryBadQualityRate = value; }
        public string FastPingHealthState { get => FastPingTelemetry.HealthState; set => FastPingTelemetry.HealthState = value; }

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
            if (TunnelConnected)
                return "Tunnel OK";
            if (HasSpeedify || SpeedifyAvailable)
                return "Tunnel Down";
            return "No Tunnel";
        }

        private static string FormatPingText(double pingMs)
        {
            return pingMs <= 0 ? "-- ms" : ((int)Math.Round(pingMs)).ToString() + " ms";
        }

        private static string FormatJitterText(double jitterMs)
        {
            return jitterMs < 0 ? "J --" : "J " + ((int)Math.Round(jitterMs)).ToString();
        }

        private string GetPingColorKey(double pingMs)
        {
            if (IsStale || pingMs <= 0)
                return "gray";

            switch (HealthState)
            {
                case UnifiedNetworkHealthState.Critical:
                case UnifiedNetworkHealthState.Warning:
                    return "red";
                case UnifiedNetworkHealthState.OffBaseline:
                    return "yellow";
                case UnifiedNetworkHealthState.Healthy:
                    return "green";
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
            if (jitterMs <= 10)
                return "green";
            if (jitterMs <= 18)
                return "gold";
            if (jitterMs <= 28)
                return "yellow";
            return "red";
        }

        private static string GetNatText(NatState natState)
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

        private static string GetNatColorKey(NatState natState)
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
            clone.FastPingTelemetry = CloneFastPingTelemetry(FastPingTelemetry);
            clone.SpeedifyTelemetry = CloneSpeedifyTelemetry(SpeedifyTelemetry);
            clone.StartupDecision = CloneStartupDecision(StartupDecision);
            clone.Links = CloneLinks(Links);
            return clone;
        }

        private static StartupDecisionSnapshot CloneStartupDecision(StartupDecisionSnapshot src)
        {
            if (src == null)
                return new StartupDecisionSnapshot();

            return new StartupDecisionSnapshot
            {
                TunnelConnected = src.TunnelConnected,
                HasFreshTelemetry = src.HasFreshTelemetry,
                ActiveConnectionCount = src.ActiveConnectionCount,
                TimeUtc = src.TimeUtc,
                AggregateLatencyMs = src.AggregateLatencyMs,
                AggregateJitterMs = src.AggregateJitterMs,
                QueueDelayMs = src.QueueDelayMs,
                ReportedLatencyMs = src.ReportedLatencyMs,
                AggregateCongested = src.AggregateCongested,
                DownloadCongested = src.DownloadCongested,
                UploadCongested = src.UploadCongested
            };
        }

        private static FastPingTelemetrySnapshot CloneFastPingTelemetry(FastPingTelemetrySnapshot src)
        {
            if (src == null)
                return new FastPingTelemetrySnapshot();

            return new FastPingTelemetrySnapshot
            {
                PrimaryTarget = src.PrimaryTarget,
                SecondaryTarget = src.SecondaryTarget,
                InstantMs = src.InstantMs,
                StableMs = src.StableMs,
                BaselineMs = src.BaselineMs,
                FloorMs = src.FloorMs,
                JitterMs = src.JitterMs,
                DisplayedPingMs = src.DisplayedPingMs,
                TunnelLatencyMs = src.TunnelLatencyMs,
                Confidence = src.Confidence,
                LossRate = src.LossRate,
                BadQualityRate = src.BadQualityRate,
                PrimarySecondaryDivergenceMs = src.PrimarySecondaryDivergenceMs,
                SpikeDetected = src.SpikeDetected,
                IsStale = src.IsStale,
                PrimaryFresh = src.PrimaryFresh,
                SecondaryFresh = src.SecondaryFresh,
                IsEvaluationMode = src.IsEvaluationMode,
                PrimaryInstantMs = src.PrimaryInstantMs,
                PrimaryStableMs = src.PrimaryStableMs,
                PrimaryFloorMs = src.PrimaryFloorMs,
                PrimaryBaselineMs = src.PrimaryBaselineMs,
                PrimaryJitterMs = src.PrimaryJitterMs,
                PrimaryLossRate = src.PrimaryLossRate,
                PrimaryBadQualityRate = src.PrimaryBadQualityRate,
                PrimaryConfidence = src.PrimaryConfidence,
                SecondaryInstantMs = src.SecondaryInstantMs,
                SecondaryStableMs = src.SecondaryStableMs,
                SecondaryFloorMs = src.SecondaryFloorMs,
                SecondaryBaselineMs = src.SecondaryBaselineMs,
                SecondaryJitterMs = src.SecondaryJitterMs,
                SecondaryLossRate = src.SecondaryLossRate,
                SecondaryBadQualityRate = src.SecondaryBadQualityRate,
                SecondaryConfidence = src.SecondaryConfidence,
                HealthState = src.HealthState
            };
        }

        private static List<UnifiedLinkState> CloneLinks(List<UnifiedLinkState> links)
        {
            List<UnifiedLinkState> output = new List<UnifiedLinkState>();
            if (links == null)
                return output;

            for (int i = 0; i < links.Count; i++)
            {
                UnifiedLinkState src = links[i];
                output.Add(src == null ? null : new UnifiedLinkState
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
                });
            }

            return output;
        }

        private static SpeedifyTelemetrySnapshot CloneSpeedifyTelemetry(SpeedifyTelemetrySnapshot src)
        {
            SpeedifyTelemetrySnapshot copy = new SpeedifyTelemetrySnapshot();
            if (src == null)
                return copy;

            copy.TunnelConnected = src.TunnelConnected;
            copy.AggregateCongested = src.AggregateCongested;
            copy.ActiveConnectionCount = src.ActiveConnectionCount;
            copy.BackupConnectionCount = src.BackupConnectionCount;
            copy.AggregateLatencyMs = src.AggregateLatencyMs;
            copy.AggregateJitterMs = src.AggregateJitterMs;
            copy.AggregateRxBps = src.AggregateRxBps;
            copy.AggregateTxBps = src.AggregateTxBps;
            copy.AggregateTotalBps = src.AggregateTotalBps;
            copy.ConnectedServerName = src.ConnectedServerName;
            copy.ConnectedServerPublicIp = src.ConnectedServerPublicIp;
            copy.PrimaryRemoteIp = src.PrimaryRemoteIp;
            copy.SpeedifyRemoteIp = src.SpeedifyRemoteIp;
            copy.Adapter1Latency = src.Adapter1Latency;
            copy.Adapter2Latency = src.Adapter2Latency;
            copy.CurrentState = src.CurrentState;
            copy.CurrentMode = src.CurrentMode;
            copy.HasConnectionStats = src.HasConnectionStats;
            copy.HasNetworks = src.HasNetworks;
            copy.HasSessionStats = src.HasSessionStats;
            copy.HasCurrentState = src.HasCurrentState;
            copy.HasConnectedServer = src.HasConnectedServer;
            copy.StructuredCoreComplete = src.StructuredCoreComplete;
            copy.ParseWarningCount = src.ParseWarningCount;
            copy.ConnectedLegacy = src.ConnectedLegacy;
            copy.CongestedLegacy = src.CongestedLegacy;
            copy.DownloadCongested = src.DownloadCongested;
            copy.UploadCongested = src.UploadCongested;
            copy.LatencyMs = src.LatencyMs;
            copy.BaseLatencyMs = src.BaseLatencyMs;
            copy.LegacyJitterMs = src.LegacyJitterMs;
            copy.ExternalPingMs = src.ExternalPingMs;
            copy.MosScore = src.MosScore;
            copy.ReceiveBytesPerSecond = src.ReceiveBytesPerSecond;
            copy.SendBytesPerSecond = src.SendBytesPerSecond;
            copy.ReceiveBytes = src.ReceiveBytes;
            copy.SendBytes = src.SendBytes;
            copy.LagScore = src.LagScore;
            copy.QueueDelay = src.QueueDelay;
            copy.QueueGrowth = src.QueueGrowth;
            copy.QueueAcceleration = src.QueueAcceleration;
            copy.LatencyDrift = src.LatencyDrift;
            copy.RetransmissionGrowth = src.RetransmissionGrowth;
            copy.QueuePressure = src.QueuePressure;
            copy.AggressionFactor = src.AggressionFactor;
            copy.ServerScore = src.ServerScore;
            copy.BurstMultiplier = src.BurstMultiplier;
            copy.PatternMatchScore = src.PatternMatchScore;
            copy.SignatureAvgQueueGrowth = src.SignatureAvgQueueGrowth;
            copy.LagPressure = src.LagPressure;
            copy.RetransmissionRate = src.RetransmissionRate;
            copy.PredictionConfidence = src.PredictionConfidence;
            copy.DominantSignal = src.DominantSignal;
            copy.LagEventType = src.LagEventType;
            copy.GamePhase = src.GamePhase;
            copy.NetworkState = src.NetworkState;
            copy.ControlState = src.ControlState;
            copy.IssueSource = src.IssueSource;
            copy.PendingLagEvents = src.PendingLagEvents;
            copy.LearningEnabled = src.LearningEnabled;
            if (src?.SessionWindows != null)
            {
                for (int i = 0; i < src.SessionWindows.Count; i++)
                {
                    SpeedifySessionWindowStats w = src.SessionWindows[i];
                    copy.SessionWindows.Add(w == null ? null : new SpeedifySessionWindowStats
                    {
                        WindowKey = w.WindowKey,
                        BytesRecv = w.BytesRecv,
                        BytesSent = w.BytesSent,
                        EncryptedBytesRecv = w.EncryptedBytesRecv,
                        EncryptedBytesSent = w.EncryptedBytesSent,
                        MaxDownloadMbps = w.MaxDownloadMbps,
                        MaxUploadMbps = w.MaxUploadMbps,
                        NumFailovers = w.NumFailovers,
                        NumSessions = w.NumSessions,
                        RetransBytes = w.RetransBytes,
                        TotalConnectedMinutes = w.TotalConnectedMinutes,
                        DownBenefitNumConnections = w.DownBenefitNumConnections,
                        DownBenefitCombinedMbps = w.DownBenefitCombinedMbps,
                        DownBenefitFastestConnMbps = w.DownBenefitFastestConnMbps,
                        UpBenefitNumConnections = w.UpBenefitNumConnections,
                        UpBenefitCombinedMbps = w.UpBenefitCombinedMbps,
                        UpBenefitFastestConnMbps = w.UpBenefitFastestConnMbps,
                        TunBytesIn = w.TunBytesIn,
                        TunBytesOut = w.TunBytesOut,
                        TunDroppedIncoming = w.TunDroppedIncoming,
                        TunPacketWaits = w.TunPacketWaits,
                        TunReadQueue = w.TunReadQueue,
                        PacketHandlerBytesIn = w.PacketHandlerBytesIn,
                        PacketHandlerBytesOut = w.PacketHandlerBytesOut,
                        PacketHandlerDroppedOut = w.PacketHandlerDroppedOut,
                        PacketHandlerRetries = w.PacketHandlerRetries,
                        TotalFailoverSaves = w.TotalFailoverSaves,
                        TotalRedundantSaves = w.TotalRedundantSaves,
                        TotalSpeedSaves = w.TotalSpeedSaves,
                        TotalStreams = w.TotalStreams,
                        UniqueSaves = w.UniqueSaves
                    });
                }
            }

            return copy;
        }
    }
}
