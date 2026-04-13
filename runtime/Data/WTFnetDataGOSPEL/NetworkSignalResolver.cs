using System;

namespace WTFnetApp
{
    public sealed class NetworkSignalResolver
    {
        private const int SpeedifyFreshnessMs = 1500;

        private int consecutiveStableSpeedifySnapshots = 0;
        private DateTime lastStableSpeedifySnapshotUtc = DateTime.MinValue;
        private static readonly TimeSpan StableSnapshotGapReset = TimeSpan.FromSeconds(2);

        public UnifiedNetworkState Resolve(MetricsSnapshot snapshot, FastPingState fastPing, NatState natState)
        {
            UnifiedNetworkState state = new UnifiedNetworkState();
            state.TimeUtc = DateTime.UtcNow;

            bool hasFastPing = fastPing != null;
            bool hasSpeedify = snapshot != null;

            state.HasFastPing = hasFastPing;
            state.HasSpeedify = hasSpeedify;

            bool speedifyFresh = hasSpeedify && IsSpeedifyFresh(snapshot);
            state.SpeedifyTelemetryFresh = speedifyFresh;

            string speedifyReason = "no snapshot";
            bool speedifyValid =
                hasSpeedify &&
                speedifyFresh &&
                IsTrustedSpeedifySnapshot(snapshot, out speedifyReason);

            if (hasSpeedify)
            {
                Logger.Log("speedify-check",
                    "fresh=" + speedifyFresh +
                    " | connected=" + snapshot.TunnelConnected +
                    " | active=" + snapshot.ActiveConnectionCount +
                    " | aggLat=" + snapshot.AggregateLatencyMs.ToString("0.0") +
                    " | lat=" + snapshot.LatencyMs.ToString("0.0") +
                    " | adapter1=" + snapshot.Adapter1Latency.ToString("0.0") +
                    " | structuredCore=" + snapshot.StructuredCoreComplete +
                    " | hasConn=" + snapshot.HasConnectionStats +
                    " | hasNet=" + snapshot.HasNetworks +
                    " | hasCurrent=" + snapshot.HasCurrentState +
                    " | hasServer=" + snapshot.HasConnectedServer +
                    " | warnings=" + snapshot.ParseWarningCount +
                    " | valid=" + speedifyValid +
                    " | reason=" + speedifyReason);
            }

            bool speedifyUsable = speedifyValid;
            state.SpeedifyAvailable = speedifyUsable;

            if (hasSpeedify)
                ApplySpeedifyFields(state, snapshot);

            if (hasFastPing)
                ApplyFastPingFields(state, fastPing);

            if (hasFastPing)
            {
                state.SourceKind = speedifyUsable
                    ? UnifiedNetworkSourceKind.FastPingWithSpeedify
                    : UnifiedNetworkSourceKind.FastPing;

                NetworkMetricsCalculator.ResolveFromFastPing(state, fastPing);
                NetworkQualityEvaluator.ClassifyFastPing(state);

                state.HealthReason = "Resolved from FastPing";
                state.Notes = speedifyUsable
                    ? "Primary source: FastPing probes; Speedify telemetry attached as context."
                    : "Speedify telemetry unavailable or stale; using probe-derived metrics.";

                if (speedifyUsable)
                    ApplySpeedifyContextOnly(state, snapshot, fastPing);
            }
            else if (speedifyUsable)
            {
                state.SourceKind = UnifiedNetworkSourceKind.Speedify;

                NetworkMetricsCalculator.ResolveFromSpeedify(state, snapshot);
                NetworkQualityEvaluator.ClassifySpeedify(state, snapshot);

                state.HealthReason = "Resolved from Speedify fallback";
                state.Notes = "FastPing unavailable; using fresh Speedify telemetry as fallback.";
            }
            else
            {
                ResolveEmpty(state);
            }

            state.JitterPressure = Math.Max(
                state.JitterPressure,
                ComputeJitterPressure(state.JitterMs));

            state.RefreshDisplayState(natState);
            return state;
        }

        private static bool IsStableSpeedifySnapshot(MetricsSnapshot snapshot, out string reason)
        {
            reason = "unknown";

            if (snapshot == null)
            {
                reason = "snapshot missing";
                return false;
            }

            // ---------------------------------------------------------
            // Core dataset presence
            // ---------------------------------------------------------

            if (!snapshot.HasConnectionStats)
            {
                reason = "missing connection stats";
                return false;
            }

            if (!snapshot.HasNetworks)
            {
                reason = "missing networks";
                return false;
            }

            if (!snapshot.HasCurrentState)
            {
                reason = "missing current state";
                return false;
            }

            if (!snapshot.HasConnectedServer)
            {
                reason = "missing connected server";
                return false;
            }

            if (!snapshot.StructuredCoreComplete)
            {
                reason = "structured core incomplete";
                return false;
            }

            // ---------------------------------------------------------
            // Tunnel must really be up
            // ---------------------------------------------------------

            if (!snapshot.TunnelConnected)
            {
                reason = "tunnel not connected";
                return false;
            }

            if (snapshot.ActiveConnectionCount <= 0)
            {
                reason = "no active speedify connections";
                return false;
            }

            if (string.IsNullOrWhiteSpace(snapshot.ConnectedServerName))
            {
                reason = "connected server name missing";
                return false;
            }

            if (string.IsNullOrWhiteSpace(snapshot.ConnectedServerPublicIp))
            {
                reason = "connected server public ip missing";
                return false;
            }

            // ---------------------------------------------------------
            // Need usable latency
            // ---------------------------------------------------------

            double usableLatency = ChoosePositive(
                snapshot.AggregateLatencyMs,
                snapshot.LatencyMs,
                snapshot.Adapter1Latency);

            if (usableLatency <= 0)
            {
                reason = "no usable speedify latency";
                return false;
            }

            // ---------------------------------------------------------
            // Need at least one real non-proxy participating link
            // ---------------------------------------------------------

            bool hasRealParticipatingLink = false;

            if (snapshot.Links != null)
            {
                foreach (LinkSnapshot link in snapshot.Links)
                {
                    if (link == null)
                        continue;

                    if (link.IsAggregate)
                        continue;

                    bool isProxy =
                        link.IsProxy ||
                        string.Equals(link.Protocol, "proxy", StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(link.Key) &&
                         link.Key.IndexOf("%proxy", StringComparison.OrdinalIgnoreCase) >= 0);

                    if (isProxy)
                        continue;

                    bool participating =
                        link.Connected && (link.LatencyMs > 0 || link.TotalBps > 0);

                    if (!participating)
                        continue;

                    hasRealParticipatingLink = true;
                    break;
                }
            }

            if (!hasRealParticipatingLink)
            {
                reason = "no active non-proxy link";
                return false;
            }

            // ---------------------------------------------------------
            // Parse warnings pressure
            // ---------------------------------------------------------

            if (snapshot.ParseWarningCount >= 6)
            {
                reason = "too many parse warnings";
                return false;
            }

            reason = "stable";
            return true;
        }

        private bool IsTrustedSpeedifySnapshot(MetricsSnapshot snapshot, out string reason)
        {
            if (!IsStableSpeedifySnapshot(snapshot, out reason))
            {
                consecutiveStableSpeedifySnapshots = 0;
                return false;
            }

            DateTime now = DateTime.UtcNow;

            if (lastStableSpeedifySnapshotUtc != DateTime.MinValue &&
                (now - lastStableSpeedifySnapshotUtc) > StableSnapshotGapReset)
            {
                consecutiveStableSpeedifySnapshots = 0;
            }

            consecutiveStableSpeedifySnapshots++;
            lastStableSpeedifySnapshotUtc = now;

            if (consecutiveStableSpeedifySnapshots < 2)
            {
                reason = "waiting for consecutive stable snapshots";
                return false;
            }

            reason = "stable";
            return true;
        }

        private static void ApplySpeedifyContextOnly(UnifiedNetworkState state, MetricsSnapshot snapshot, FastPingState fastPing)
        {
            if (state == null || snapshot == null || fastPing == null)
                return;

            state.JitterPressure = Math.Max(
                state.JitterPressure,
                ComputeJitterPressure(ChoosePositive(
                    snapshot.AggregateJitterMs,
                    snapshot.Jitter,
                    snapshot.MicroJitter)));

            double speedifyReferenceLatency = ChoosePositive(
                snapshot.AggregateLatencyMs,
                snapshot.LatencyMs,
                snapshot.Adapter1Latency);

            if (speedifyReferenceLatency > 0 && fastPing.StablePingMs > 0)
            {
                state.DeltaFastPingVsSpeedifyMs = Math.Abs(speedifyReferenceLatency - fastPing.StablePingMs);

                if (state.DeltaFastPingVsSpeedifyMs >= 10)
                {
                    state.Notes = AppendNote(
                        state.Notes,
                        "Speedify telemetry differs noticeably from end-to-end probes.");
                }

                if (state.DeltaFastPingVsSpeedifyMs >= 20)
                {
                    state.Notes = AppendNote(
                        state.Notes,
                        "Large tunnel-vs-probe mismatch detected.");
                }
            }
        }
        
        private static bool IsSpeedifyFresh(MetricsSnapshot snapshot)
        {
            if (snapshot == null)
                return false;

            if (snapshot.TimeUtc == DateTime.MinValue)
                return false;

            double ageMs = (DateTime.UtcNow - snapshot.TimeUtc).TotalMilliseconds;
            return ageMs >= 0 && ageMs <= SpeedifyFreshnessMs;
        }

        private static void ApplySpeedifyFields(UnifiedNetworkState state, MetricsSnapshot snapshot)
        {
            state.TunnelConnected = snapshot.TunnelConnected;
            state.AggregateCongested = snapshot.AggregateCongested;
            state.ActiveConnectionCount = snapshot.ActiveConnectionCount;
            state.BackupConnectionCount = snapshot.BackupConnectionCount;

            state.AggregateLatencyMs = snapshot.AggregateLatencyMs;
            state.AggregateJitterMs = snapshot.AggregateJitterMs;
            state.AggregateRxBps = snapshot.AggregateRxBps;
            state.AggregateTxBps = snapshot.AggregateTxBps;
            state.AggregateTotalBps = snapshot.AggregateTotalBps;

            state.ConnectedServerName = snapshot.ConnectedServerName;
            state.ConnectedServerPublicIp = snapshot.ConnectedServerPublicIp;
            state.PrimaryRemoteIp = snapshot.PrimaryRemoteIp;
            state.SpeedifyRemoteIp = snapshot.SpeedifyRemoteIp;

            state.Adapter1Latency = snapshot.Adapter1Latency;
            state.Adapter2Latency = snapshot.Adapter2Latency;

            state.RecentLatencyMs = snapshot.Recent;
            state.AverageLatencyMs = snapshot.Average;
            state.MaxLatencyMs = snapshot.Max;
            state.MicroJitterMs = snapshot.MicroJitter;
            state.DynamicBaseMs = snapshot.DynamicBase;
            state.QueueDelayMs = snapshot.QueueDelayMs;
            state.PacketLoss = snapshot.PacketLoss;

            state.LatencyDriftMs = snapshot.LatencyDrift;
            state.CongestionScore = snapshot.CongestionScore;
            state.LagProbability = snapshot.LagProbability;
            state.PredictionConfidence = ParsePredictionConfidence(snapshot.PredictionConfidence);

            state.SpeedifyConnectedLegacy = snapshot.Connected;
            state.SpeedifyCongestedLegacy = snapshot.Congested;
            state.SpeedifyDownloadCongested = snapshot.IsDownloadCongested;
            state.SpeedifyUploadCongested = snapshot.IsUploadCongested;

            state.SpeedifyLatencyMs = snapshot.LatencyMs;
            state.SpeedifyBaseLatencyMs = snapshot.BaseLatencyMs;
            state.SpeedifyLegacyJitterMs = snapshot.Jitter;
            state.SpeedifyExternalPingMs = snapshot.ExternalPingMs;

            state.SpeedifyMosScore = snapshot.MosScore;

            state.SpeedifyReceiveBytesPerSecond = snapshot.ReceiveBytesPerSecond;
            state.SpeedifySendBytesPerSecond = snapshot.SendBytesPerSecond;
            state.SpeedifyReceiveBytes = snapshot.ReceiveBytes;
            state.SpeedifySendBytes = snapshot.SendBytes;

            state.SpeedifyLagScore = snapshot.LagScore;
            state.SpeedifyQueueDelay = snapshot.QueueDelay;
            state.SpeedifyQueueGrowth = snapshot.QueueGrowth;
            state.SpeedifyQueueAcceleration = snapshot.QueueAcceleration;
            state.SpeedifyLatencyDrift = snapshot.LatencyDrift;
            state.SpeedifyRetransmissionGrowth = snapshot.RetransmissionGrowth;
            state.SpeedifyQueuePressure = snapshot.QueuePressure;
            state.SpeedifyAggressionFactor = snapshot.AggressionFactor;
            state.SpeedifyServerScore = snapshot.ServerScore;
            state.SpeedifyBurstMultiplier = snapshot.BurstMultiplier;
            state.SpeedifyPatternMatchScore = snapshot.PatternMatchScore;
            state.SpeedifySignatureAvgQueueGrowth = snapshot.SignatureAvgQueueGrowth;
            state.SpeedifyLagPressure = snapshot.LagPressure;
            state.SpeedifyRetransmissionRate = snapshot.RetransmissionRate;

            state.SpeedifyPredictionConfidence = snapshot.PredictionConfidence;
            state.SpeedifyDominantSignal = snapshot.DominantSignal;
            state.SpeedifyLagEventType = snapshot.LagEventType;
            state.SpeedifyGamePhase = snapshot.GamePhase;
            state.SpeedifyNetworkState = snapshot.NetworkState;
            state.SpeedifyControlState = snapshot.ControlState;
            state.SpeedifyIssueSource = snapshot.IssueSource;

            state.SpeedifyPendingLagEvents = snapshot.PendingLagEvents;
            state.SpeedifyLearningEnabled = snapshot.LearningEnabled;

            state.SpeedifyCurrentState = snapshot.TunnelCurrentState;
            state.SpeedifyCurrentMode = snapshot.TunnelCurrentMode;

            state.SpeedifyHasConnectionStats = snapshot.HasConnectionStats;
            state.SpeedifyHasNetworks = snapshot.HasNetworks;
            state.SpeedifyHasSessionStats = snapshot.HasSessionStats;
            state.SpeedifyHasCurrentState = snapshot.HasCurrentState;
            state.SpeedifyHasConnectedServer = snapshot.HasConnectedServer;
            state.SpeedifyStructuredCoreComplete = snapshot.StructuredCoreComplete;
            state.SpeedifyParseWarningCount = snapshot.ParseWarningCount;

            state.SpeedifySessionWindows.Clear();
            foreach (SpeedifySessionWindowStats src in snapshot.SessionWindows)
            {
                if (src == null)
                    continue;

                state.SpeedifySessionWindows.Add(new SpeedifySessionWindowStats
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

            state.Links.Clear();
            foreach (LinkSnapshot src in snapshot.Links)
            {
                if (src == null)
                    continue;

                state.Links.Add(new UnifiedLinkState
                {
                    Id = src.Id,
                    Guid = src.Guid,
                    Key = src.Key,
                    Name = src.Name,
                    Isp = src.Isp,
                    Protocol = src.Protocol,
                    Connected = src.Connected,
                    IsProxy = src.IsProxy,
                    IsAggregate = src.IsAggregate,
                    Congested = src.Congested,
                    DownloadCongested = src.DownloadCongested,
                    UploadCongested = src.UploadCongested,
                    LatencyMs = src.LatencyMs,
                    JitterMs = src.JitterMs,
                    Mos = src.Mos,
                    RxBps = src.RxBps,
                    TxBps = src.TxBps,
                    TotalBps = src.TotalBps,
                    LocalIp = src.LocalIp,
                    PrivateIp = src.PrivateIp,
                    RemoteIp = src.RemoteIp
                });
            }
        }

        private static void ApplyFastPingFields(UnifiedNetworkState state, FastPingState fastPing)
        {
            state.FastPingPrimaryTarget = fastPing.PrimaryTarget;
            state.FastPingSecondaryTarget = fastPing.SecondaryTarget;

            state.FastPingInstantMs = fastPing.InstantPingMs;
            state.FastPingStableMs = fastPing.StablePingMs;
            state.FastPingBaselineMs = fastPing.BaselinePingMs;
            state.FastPingFloorMs = fastPing.FloorPingMs;
            state.FastPingJitterMs = fastPing.JitterMs;
            state.FastPingDisplayedPingMs = fastPing.DisplayedPingMs;
            state.FastPingTunnelLatencyMs = fastPing.TunnelLatencyMs;

            state.FastPingConfidence = fastPing.Confidence;
            state.FastPingLossRate = fastPing.LossRate;
            state.FastPingBadQualityRate = fastPing.BadQualityRate;
            state.FastPingPrimarySecondaryDivergenceMs = fastPing.PrimarySecondaryDivergenceMs;

            state.FastPingSpikeDetected = fastPing.SpikeDetected;
            state.FastPingIsStale = fastPing.IsStale;
            state.FastPingHealthState = fastPing.HealthState.ToString();

            state.FastPingPrimaryFresh = fastPing.PrimaryFresh;
            state.FastPingSecondaryFresh = fastPing.SecondaryFresh;
            state.FastPingIsEvaluationMode = fastPing.IsEvaluationMode;

            state.FastPingPrimaryInstantMs = fastPing.PrimaryInstantMs;
            state.FastPingPrimaryStableMs = fastPing.PrimaryStableMs;
            state.FastPingPrimaryFloorMs = fastPing.PrimaryFloorMs;
            state.FastPingPrimaryBaselineMs = fastPing.PrimaryBaselineMs;
            state.FastPingPrimaryJitterMs = fastPing.PrimaryJitterMs;
            state.FastPingPrimaryLossRate = fastPing.PrimaryLossRate;
            state.FastPingPrimaryBadQualityRate = fastPing.PrimaryBadQualityRate;
            state.FastPingPrimaryConfidence = fastPing.PrimaryConfidence;

            state.FastPingSecondaryInstantMs = fastPing.SecondaryInstantMs;
            state.FastPingSecondaryStableMs = fastPing.SecondaryStableMs;
            state.FastPingSecondaryFloorMs = fastPing.SecondaryFloorMs;
            state.FastPingSecondaryBaselineMs = fastPing.SecondaryBaselineMs;
            state.FastPingSecondaryJitterMs = fastPing.SecondaryJitterMs;
            state.FastPingSecondaryLossRate = fastPing.SecondaryLossRate;
            state.FastPingSecondaryBadQualityRate = fastPing.SecondaryBadQualityRate;
            state.FastPingSecondaryConfidence = fastPing.SecondaryConfidence;
        }

        private static void ResolveEmpty(UnifiedNetworkState state)
        {
            state.SourceKind = UnifiedNetworkSourceKind.None;
            state.HealthState = UnifiedNetworkHealthState.Unknown;
            state.HealthReason = "No telemetry available";
            state.DeltaFastPingVsSpeedifyMs = -1;
            state.PrimaryLatencySource = "none";
            state.PrimaryJitterSource = "none";
            state.PrimaryConfidenceSource = "none";
            state.Notes = "No Speedify snapshot and no FastPing state were available.";
        }

        private static double ComputeJitterPressure(double jitterMs)
        {
            if (jitterMs <= 0)
                return 0.0;

            if (jitterMs <= 4)
                return 0.0;

            if (jitterMs <= 8)
                return 0.15;

            if (jitterMs <= 12)
                return 0.35;

            if (jitterMs <= 18)
                return 0.60;

            if (jitterMs <= 28)
                return 0.82;

            return 1.0;
        }

        private static double ChoosePositive(double first, double second, double third)
        {
            if (first > 0)
                return first;

            if (second > 0)
                return second;

            if (third > 0)
                return third;

            return -1;
        }

        private static double ParsePredictionConfidence(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return -1;

            double parsed;
            if (double.TryParse(value, out parsed))
                return parsed;

            switch (value.Trim().ToLowerInvariant())
            {
                case "low":
                    return 0.25;

                case "medium":
                    return 0.50;

                case "high":
                    return 0.80;

                case "very high":
                    return 0.95;

                default:
                    return -1;
            }
        }

        private static string AppendNote(string existing, string extra)
        {
            if (string.IsNullOrWhiteSpace(existing))
                return extra ?? string.Empty;

            if (string.IsNullOrWhiteSpace(extra))
                return existing;

            return existing + " " + extra;
        }
    }
}
