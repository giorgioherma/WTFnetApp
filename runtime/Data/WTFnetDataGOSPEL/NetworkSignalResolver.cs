using System;

namespace WTFnetApp
{
    public sealed class NetworkSignalResolver
    {
        private const int SpeedifyFreshnessMs = 1500;

        private int consecutiveStableSpeedifySnapshots = 0;
        private DateTime lastStableSpeedifySnapshotUtc = DateTime.MinValue;
        private static readonly TimeSpan StableSnapshotGapReset = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan SpeedifyCheckHeartbeatInterval = TimeSpan.FromSeconds(30);
        private readonly FastPingSignalAnalyzer fastPingSignalAnalyzer;
        private string lastLoggedSpeedifyCheckReason;
        private bool? lastLoggedSpeedifyCheckValid;
        private bool? lastLoggedSpeedifyCheckConnected;
        private int? lastLoggedSpeedifyCheckActive;
        private string lastLoggedSpeedifyCheckServer;
        private string lastLoggedSpeedifyCheckPublicIp;
        private DateTime lastLoggedSpeedifyCheckUtc = DateTime.MinValue;

        public NetworkSignalResolver(FastPingSignalAnalyzer fastPingSignalAnalyzer)
        {
            this.fastPingSignalAnalyzer = fastPingSignalAnalyzer
                ?? throw new ArgumentNullException(nameof(fastPingSignalAnalyzer));
        }
       

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
                MaybeLogSpeedifyCheck(snapshot, speedifyFresh, speedifyValid, speedifyReason);

            bool speedifyUsable = speedifyValid;
            state.SpeedifyAvailable = speedifyUsable;

            if (hasSpeedify && speedifyFresh)
                ApplySpeedifyFields(state, snapshot);

            if (hasFastPing)
                ApplyFastPingFields(state, fastPing);

            if (hasFastPing)
            {
                state.SourceKind = speedifyUsable
                    ? UnifiedNetworkSourceKind.FastPingWithSpeedify
                    : UnifiedNetworkSourceKind.FastPing;

                var fastPingSnapshot = fastPingSignalAnalyzer != null
                    ? fastPingSignalAnalyzer.Last10sSnapshot
                    : null;

                NetworkMetricsCalculator.ResolveFromFastPing(state, fastPing, fastPingSnapshot);
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

            PopulateStartupDecision(state);

            state.RefreshDisplayState(natState);
            return state;
        }

        private void MaybeLogSpeedifyCheck(MetricsSnapshot snapshot, bool speedifyFresh, bool speedifyValid, string speedifyReason)
        {
            DateTime now = DateTime.UtcNow;
            bool connected = speedifyFresh && snapshot != null && snapshot.TunnelConnected;
            int active = speedifyFresh && snapshot != null ? snapshot.ActiveConnectionCount : 0;
            string server = speedifyFresh ? snapshot?.ConnectedServerName ?? string.Empty : string.Empty;
            string publicIp = speedifyFresh ? snapshot?.ConnectedServerPublicIp ?? string.Empty : string.Empty;
            string effectiveReason = speedifyFresh ? speedifyReason : "no fresh snapshot";

            bool changed =
                !lastLoggedSpeedifyCheckValid.HasValue ||
                lastLoggedSpeedifyCheckValid.Value != speedifyValid ||
                !string.Equals(lastLoggedSpeedifyCheckReason, effectiveReason, StringComparison.OrdinalIgnoreCase) ||
                !lastLoggedSpeedifyCheckConnected.HasValue ||
                lastLoggedSpeedifyCheckConnected.Value != connected ||
                !lastLoggedSpeedifyCheckActive.HasValue ||
                lastLoggedSpeedifyCheckActive.Value != active ||
                !string.Equals(lastLoggedSpeedifyCheckServer, server, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(lastLoggedSpeedifyCheckPublicIp, publicIp, StringComparison.OrdinalIgnoreCase);

            bool heartbeatDue = lastLoggedSpeedifyCheckUtc == DateTime.MinValue ||
                (now - lastLoggedSpeedifyCheckUtc) >= SpeedifyCheckHeartbeatInterval;

            if (!changed && !heartbeatDue)
                return;

            Logger.Log("speedify-check",
                "fresh=" + speedifyFresh +
                " | connected=" + connected +
                " | active=" + active +
                " | aggLat=" + (speedifyFresh ? snapshot.AggregateLatencyMs.ToString("0.0") : "0.0") +
                " | lat=" + (speedifyFresh ? snapshot.LatencyMs.ToString("0.0") : "0.0") +
                " | adapter1=" + (speedifyFresh ? snapshot.Adapter1Latency.ToString("0.0") : "0.0") +
                " | structuredCore=" + (speedifyFresh && snapshot.StructuredCoreComplete) +
                " | hasConn=" + (speedifyFresh && snapshot.HasConnectionStats) +
                " | hasNet=" + (speedifyFresh && snapshot.HasNetworks) +
                " | hasCurrent=" + (speedifyFresh && snapshot.HasCurrentState) +
                " | hasServer=" + (speedifyFresh && snapshot.HasConnectedServer) +
                " | warnings=" + (speedifyFresh ? snapshot.ParseWarningCount : 0) +
                " | valid=" + speedifyValid +
                " | reason=" + effectiveReason);

            lastLoggedSpeedifyCheckUtc = now;
            lastLoggedSpeedifyCheckReason = effectiveReason;
            lastLoggedSpeedifyCheckValid = speedifyValid;
            lastLoggedSpeedifyCheckConnected = connected;
            lastLoggedSpeedifyCheckActive = active;
            lastLoggedSpeedifyCheckServer = server;
            lastLoggedSpeedifyCheckPublicIp = publicIp;
        }

        private static void PopulateStartupDecision(UnifiedNetworkState state)
        {
            if (state == null)
                return;

            StartupDecisionSnapshot startup = state.StartupDecision ?? new StartupDecisionSnapshot();
            SpeedifyTelemetrySnapshot speedify = state.SpeedifyTelemetry ?? new SpeedifyTelemetrySnapshot();
            bool speedifyFresh = state.SpeedifyTelemetryFresh;

            if (!speedifyFresh)
            {
                startup.TunnelConnected = false;
                startup.HasFreshTelemetry = false;
                startup.ActiveConnectionCount = 0;
                startup.TimeUtc = state.TimeUtc;
                startup.AggregateLatencyMs = -1;
                startup.AggregateJitterMs = -1;
                startup.QueueDelayMs = 0;
                startup.ReportedLatencyMs = -1;
                startup.AggregateCongested = false;
                startup.DownloadCongested = false;
                startup.UploadCongested = false;
                state.StartupDecision = startup;
                return;
            }

            double tunnelLatencyMs = speedify.AggregateLatencyMs > 0
                ? speedify.AggregateLatencyMs
                : speedify.LatencyMs > 0
                    ? speedify.LatencyMs
                    : -1;

            bool hasLiveTelemetrySignal =
                speedify.ActiveConnectionCount > 0 ||
                tunnelLatencyMs > 0 ||
                speedify.AggregateJitterMs > 0 ||
                state.QueueDelayMs > 0;

            startup.TunnelConnected = speedify.TunnelConnected;
            startup.HasFreshTelemetry = state.SpeedifyTelemetryFresh && hasLiveTelemetrySignal;
            startup.ActiveConnectionCount = speedify.ActiveConnectionCount;
            startup.TimeUtc = state.TimeUtc;

            // Startup should use best available tunnel latency.
            // Speedify often reports aggregate latency as 0 while LatencyMs has the usable tunnel RTT.
            startup.AggregateLatencyMs = tunnelLatencyMs;

            startup.AggregateJitterMs = speedify.AggregateJitterMs;
            startup.QueueDelayMs = state.QueueDelayMs;
            startup.ReportedLatencyMs = speedify.LatencyMs;
            startup.AggregateCongested = speedify.AggregateCongested;
            startup.DownloadCongested = speedify.DownloadCongested;
            startup.UploadCongested = speedify.UploadCongested;

            state.StartupDecision = startup;
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

            bool startupGrace =
                consecutiveStableSpeedifySnapshots == 1 &&
                snapshot.HasConnectionStats &&
                snapshot.HasNetworks &&
                snapshot.HasCurrentState &&
                snapshot.HasConnectedServer &&
                snapshot.StructuredCoreComplete &&
                snapshot.TunnelConnected &&
                snapshot.ActiveConnectionCount > 0;

            if (consecutiveStableSpeedifySnapshots < 2 && !startupGrace)
            {
                reason = "waiting for consecutive stable snapshots";
                return false;
            }

            reason = startupGrace
                ? "startup grace accepted first stable snapshot"
                : "stable";

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
            SpeedifyTelemetrySnapshot speedify = state.SpeedifyTelemetry ?? new SpeedifyTelemetrySnapshot();

            speedify.TunnelConnected = snapshot.TunnelConnected;
            speedify.AggregateCongested = snapshot.AggregateCongested;
            speedify.ActiveConnectionCount = snapshot.ActiveConnectionCount;
            speedify.BackupConnectionCount = snapshot.BackupConnectionCount;
            speedify.AggregateLatencyMs = snapshot.AggregateLatencyMs;
            speedify.AggregateJitterMs = snapshot.AggregateJitterMs;
            speedify.AggregateRxBps = snapshot.AggregateRxBps;
            speedify.AggregateTxBps = snapshot.AggregateTxBps;
            speedify.AggregateTotalBps = snapshot.AggregateTotalBps;
            speedify.ConnectedServerName = snapshot.ConnectedServerName;
            speedify.ConnectedServerPublicIp = snapshot.ConnectedServerPublicIp;
            speedify.PrimaryRemoteIp = snapshot.PrimaryRemoteIp;
            speedify.SpeedifyRemoteIp = snapshot.SpeedifyRemoteIp;
            speedify.Adapter1Latency = snapshot.Adapter1Latency;
            speedify.Adapter2Latency = snapshot.Adapter2Latency;
            speedify.CurrentState = snapshot.TunnelCurrentState;
            speedify.CurrentMode = snapshot.TunnelCurrentMode;
            speedify.HasConnectionStats = snapshot.HasConnectionStats;
            speedify.HasNetworks = snapshot.HasNetworks;
            speedify.HasSessionStats = snapshot.HasSessionStats;
            speedify.HasCurrentState = snapshot.HasCurrentState;
            speedify.HasConnectedServer = snapshot.HasConnectedServer;
            speedify.StructuredCoreComplete = snapshot.StructuredCoreComplete;
            speedify.ParseWarningCount = snapshot.ParseWarningCount;
            speedify.ConnectedLegacy = snapshot.Connected;
            speedify.CongestedLegacy = snapshot.Congested;
            speedify.DownloadCongested = snapshot.IsDownloadCongested;
            speedify.UploadCongested = snapshot.IsUploadCongested;
            speedify.LatencyMs = snapshot.LatencyMs;
            speedify.BaseLatencyMs = snapshot.BaseLatencyMs;
            speedify.LegacyJitterMs = snapshot.Jitter;
            speedify.ExternalPingMs = snapshot.ExternalPingMs;
            speedify.MosScore = snapshot.MosScore;
            speedify.ReceiveBytesPerSecond = snapshot.ReceiveBytesPerSecond;
            speedify.SendBytesPerSecond = snapshot.SendBytesPerSecond;
            speedify.ReceiveBytes = snapshot.ReceiveBytes;
            speedify.SendBytes = snapshot.SendBytes;
            speedify.LagScore = snapshot.LagScore;
            speedify.QueueDelay = snapshot.QueueDelay;
            speedify.QueueGrowth = snapshot.QueueGrowth;
            speedify.QueueAcceleration = snapshot.QueueAcceleration;
            speedify.LatencyDrift = snapshot.LatencyDrift;
            speedify.RetransmissionGrowth = snapshot.RetransmissionGrowth;
            speedify.QueuePressure = snapshot.QueuePressure;
            speedify.AggressionFactor = snapshot.AggressionFactor;
            speedify.ServerScore = snapshot.ServerScore;
            speedify.BurstMultiplier = snapshot.BurstMultiplier;
            speedify.PatternMatchScore = snapshot.PatternMatchScore;
            speedify.SignatureAvgQueueGrowth = snapshot.SignatureAvgQueueGrowth;
            speedify.LagPressure = snapshot.LagPressure;
            speedify.RetransmissionRate = snapshot.RetransmissionRate;
            speedify.PredictionConfidence = snapshot.PredictionConfidence;
            speedify.DominantSignal = snapshot.DominantSignal;
            speedify.LagEventType = snapshot.LagEventType;
            speedify.GamePhase = snapshot.GamePhase;
            speedify.NetworkState = snapshot.NetworkState;
            speedify.ControlState = snapshot.ControlState;
            speedify.IssueSource = snapshot.IssueSource;
            speedify.PendingLagEvents = snapshot.PendingLagEvents;
            speedify.LearningEnabled = snapshot.LearningEnabled;

            speedify.SessionWindows.Clear();
            foreach (SpeedifySessionWindowStats src in snapshot.SessionWindows)
            {
                if (src == null)
                    continue;

                speedify.SessionWindows.Add(new SpeedifySessionWindowStats
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

            state.SpeedifyTelemetry = speedify;

            // resolved-consumer convenience fields preserved
            state.QueueDelayMs = snapshot.QueueDelayMs;
            state.PacketLoss = snapshot.PacketLoss;
            state.RecentLatencyMs = snapshot.Recent;
            state.AverageLatencyMs = snapshot.Average;
            state.MaxLatencyMs = snapshot.Max;
            state.MicroJitterMs = snapshot.MicroJitter;
            state.DynamicBaseMs = snapshot.DynamicBase;
            state.LatencyDriftMs = snapshot.LatencyDrift;
            state.CongestionScore = snapshot.CongestionScore;
            state.LagProbability = snapshot.LagProbability;
            state.PredictionConfidence = ParsePredictionConfidence(snapshot.PredictionConfidence);

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
            FastPingTelemetrySnapshot telemetry = state.FastPingTelemetry ?? new FastPingTelemetrySnapshot();

            telemetry.PrimaryTarget = fastPing.PrimaryTarget;
            telemetry.SecondaryTarget = fastPing.SecondaryTarget;
            telemetry.InstantMs = fastPing.InstantPingMs;
            telemetry.StableMs = fastPing.StablePingMs;
            telemetry.BaselineMs = fastPing.BaselinePingMs;
            telemetry.FloorMs = fastPing.FloorPingMs;
            telemetry.JitterMs = fastPing.JitterMs;
            telemetry.DisplayedPingMs = fastPing.DisplayedPingMs;
            telemetry.TunnelLatencyMs = fastPing.TunnelLatencyMs;
            telemetry.Confidence = fastPing.Confidence;
            telemetry.LossRate = fastPing.LossRate;
            telemetry.BadQualityRate = fastPing.BadQualityRate;
            telemetry.PrimarySecondaryDivergenceMs = fastPing.PrimarySecondaryDivergenceMs;
            telemetry.SpikeDetected = fastPing.SpikeDetected;
            telemetry.IsStale = fastPing.IsStale;
            telemetry.HealthState = fastPing.HealthState.ToString();
            telemetry.PrimaryFresh = fastPing.PrimaryFresh;
            telemetry.SecondaryFresh = fastPing.SecondaryFresh;
            telemetry.IsEvaluationMode = fastPing.IsEvaluationMode;
            telemetry.PrimaryInstantMs = fastPing.PrimaryInstantMs;
            telemetry.PrimaryStableMs = fastPing.PrimaryStableMs;
            telemetry.PrimaryFloorMs = fastPing.PrimaryFloorMs;
            telemetry.PrimaryBaselineMs = fastPing.PrimaryBaselineMs;
            telemetry.PrimaryJitterMs = fastPing.PrimaryJitterMs;
            telemetry.PrimaryLossRate = fastPing.PrimaryLossRate;
            telemetry.PrimaryBadQualityRate = fastPing.PrimaryBadQualityRate;
            telemetry.PrimaryConfidence = fastPing.PrimaryConfidence;
            telemetry.SecondaryInstantMs = fastPing.SecondaryInstantMs;
            telemetry.SecondaryStableMs = fastPing.SecondaryStableMs;
            telemetry.SecondaryFloorMs = fastPing.SecondaryFloorMs;
            telemetry.SecondaryBaselineMs = fastPing.SecondaryBaselineMs;
            telemetry.SecondaryJitterMs = fastPing.SecondaryJitterMs;
            telemetry.SecondaryLossRate = fastPing.SecondaryLossRate;
            telemetry.SecondaryBadQualityRate = fastPing.SecondaryBadQualityRate;
            telemetry.SecondaryConfidence = fastPing.SecondaryConfidence;

            state.FastPingTelemetry = telemetry;
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
