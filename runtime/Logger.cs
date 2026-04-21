using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace WTFnetApp
{
    public static class Logger
    {
        private static readonly object lockObj = new object();
        private static readonly object deviationLockObj = new object();
        private static readonly object rawFastPingLockObj = new object();
        private static readonly object unifiedCsvLockObj = new object();
        private static readonly object speedifyDebugLockObj = new object();

        private static string logFile = "wtfnet_log.txt";
        private static string deviationLogFile = "wtfnet_deviation_log.txt";
        private static string rawFastPingLogFile = "wtfnet_fastping_raw.txt";
        private static string unifiedCsvFile = "wtfnet_unified_metrics.csv";
        private static string speedifyDebugLogFile = "wtfnet_speedify_debug.txt";

        private static DateTime lastOperationalLog = DateTime.MinValue;
        private static DateTime lastUnifiedCsvLog = DateTime.MinValue;
        private static bool isShuttingDown;
        private static bool speedifyParserDeepDebugEnabled;
        private static readonly object speedifyDebugFilterLockObj = new object();
        private static readonly Dictionary<string, string> lastSpeedifyCategoryFingerprint =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> lastSpeedifyCategoryLogUtc =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> downgradedLinkKeysAwaitingRecovery =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string lastLogRoute = string.Empty;
        private static string lastLogSource = string.Empty;
        private static string lastLogServer = string.Empty;
        private static string lastLogPublicIp = string.Empty;
        private static string lastLogHealth = string.Empty;
        private static string lastLogWarning = string.Empty;
        private static bool lastLogTunnelConnected;
        private static bool lastLogStale;
        private static bool lastLogSpeedifyValid;
        private static int lastLogActiveCount = -1;
        private static int lastLogBackupCount = -1;
        private static int lastLogLatencyBucket = -1;
        private static int lastLogJitterBucket = -1;
        private static int lastLogLossBucket = -1;
        private static int lastLogConfidenceBand = -1;

        private static string lastUnifiedSource = string.Empty;
        private static string lastUnifiedHealth = string.Empty;
        private static string lastUnifiedServer = string.Empty;
        private static string lastUnifiedMode = string.Empty;
        private static bool lastUnifiedTunnel;
        private static bool lastUnifiedAggCongested;
        private static bool lastUnifiedSpike;
        private static bool lastUnifiedStale;
        private static int lastUnifiedPingBucket = -1;
        private static int lastUnifiedJitterBucket = -1;
        private static int lastUnifiedLossBucket = -1;
        private static int lastUnifiedBadBucket = -1;

        // =========================================================
        // SESSION START
        // =========================================================

        public static void StartSession()
        {
            WriteLine("");
            WriteLine("============================================================");
            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [system] NEW SESSION");
            WriteLine("============================================================");
        }

        public static void StartDeviationSession()
        {
            WriteDeviationLine("");
            WriteDeviationLine("============================================================");
            WriteDeviationLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [deviation] NEW SESSION");
            WriteDeviationLine("============================================================");
        }

        public static void StartFastPingRawSession()
        {
            WriteRawFastPingLine("");
            WriteRawFastPingLine("============================================================");
            WriteRawFastPingLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [fastping-raw] NEW SESSION");
            WriteRawFastPingLine("============================================================");
        }

        public static void StartUnifiedMetricsSession()
        {
            lock (unifiedCsvLockObj)
            {
                try
                {
                    bool writeHeader = !File.Exists(unifiedCsvFile) || new FileInfo(unifiedCsvFile).Length == 0;

                    using (StreamWriter writer = new StreamWriter(unifiedCsvFile, true))
                    {
                        if (writeHeader)
                            writer.WriteLine(BuildUnifiedHeader());
                    }
                }
                catch
                {
                }
            }
        }

        public static void StartSpeedifyDebugSession()
        {
            WriteSpeedifyDebugLine("");
            WriteSpeedifyDebugLine("============================================================");
            WriteSpeedifyDebugLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [speedify-debug] NEW SESSION");
            WriteSpeedifyDebugLine("============================================================");
        }

        // =========================================================
        // OPTIONAL FILE NAME OVERRIDES
        // =========================================================

        public static void SetLogFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return;

            lock (lockObj)
            {
                logFile = fileName;
            }
        }

        public static void SetDeviationLogFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return;

            lock (deviationLockObj)
            {
                deviationLogFile = fileName;
            }
        }

        public static void SetFastPingRawLogFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return;

            lock (rawFastPingLockObj)
            {
                rawFastPingLogFile = fileName;
            }
        }

        public static void SetUnifiedMetricsFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return;

            lock (unifiedCsvLockObj)
            {
                unifiedCsvFile = fileName;
            }
        }

        public static void SetSpeedifyDebugLogFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return;

            lock (speedifyDebugLockObj)
            {
                speedifyDebugLogFile = fileName;
            }
        }

        public static void SetSpeedifyParserDeepDebug(bool enabled)
        {
            lock (speedifyDebugFilterLockObj)
            {
                speedifyParserDeepDebugEnabled = enabled;
            }
        }

        public static bool IsSpeedifyParserDeepDebugEnabled()
        {
            lock (speedifyDebugFilterLockObj)
            {
                return speedifyParserDeepDebugEnabled;
            }
        }

        // =========================================================
        // BASIC LOG
        // =========================================================

        public static void Log(string category, string message)
        {
            string normalizedCategory = category?.Trim() ?? string.Empty;

            if (!IsSpeedifyParserDeepDebugEnabled() &&
                IsSpeedifyDeepDebugOnlyCategory(normalizedCategory))
            {
                return;
            }

            if (IsSpeedifyDebugCategory(normalizedCategory) &&
                !ShouldWriteSpeedifyDebug(normalizedCategory, message))
            {
                return;
            }

            string line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}";

            if (IsSpeedifyDebugCategory(normalizedCategory))
            {
                WriteSpeedifyDebugLine(line);
                return;
            }

            WriteLine(line);

            if (IsStartupDebugCategory(category))
                WriteSpeedifyDebugLine(line);
        }

        public static void LogCritical(string message)
        {
            string line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [CRITICAL] {message}";

            WriteLine(line);
        }

        // =========================================================
        // DEVIATION LOG
        // =========================================================

        public static void LogDeviation(string message)
        {
            string line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [deviation] {message}";

            WriteDeviationLine(line);
        }

        public static void LogDeviation(string category, string message)
        {
            string line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}";

            WriteDeviationLine(line);
        }

        // =========================================================
        // RAW FASTPING LOG
        // =========================================================

        public static void LogFastPingRaw(string message)
        {
            string line =
                $"{DateTime.Now:HH:mm:ss.fff} {message}";

            WriteRawFastPingLine(line);
        }

        public static void SetShuttingDown(bool shuttingDown)
        {
            lock (lockObj)
            {
                isShuttingDown = shuttingDown;
            }
        }

        // =========================================================
        // OPERATIONAL LOGGING (compact + change-based + heartbeat)
        // =========================================================

        public static void LogAnalysis(UnifiedNetworkState s)
        {
            if (s == null)
                return;

            DateTime now = DateTime.Now;
            SpeedifyTelemetrySnapshot speedify = s.SpeedifyTelemetry ?? new SpeedifyTelemetrySnapshot();
            string route = ResolveRouteLabel(s);
            string source = ResolveSourceLabel(s.SourceKind);
            string server = SafeText(speedify.ConnectedServerName);
            string publicIp = SafeText(speedify.ConnectedServerPublicIp);
            string health = SafeText(s.HealthState);
            string warning = ResolveWarningLabel(s);
            bool tunnelConnected = speedify.TunnelConnected;
            bool stale = s.IsStale;
            bool speedifyValid = s.SpeedifyAvailable;
            int activeCount = speedify.ActiveConnectionCount;
            int backupCount = speedify.BackupConnectionCount;
            int latencyBucket = GetPingBucket(s.DisplayedPingMs > 0 ? s.DisplayedPingMs : s.LatencyMs);
            int jitterBucket = GetJitterBucket(s.JitterMs);
            int lossBucket = GetRateBucket(s.LossRate);
            int confidenceBand = GetConfidenceBand(s.Confidence);

            lock (lockObj)
            {
                bool changed =
                    route != lastLogRoute ||
                    source != lastLogSource ||
                    tunnelConnected != lastLogTunnelConnected ||
                    server != lastLogServer ||
                    publicIp != lastLogPublicIp ||
                    health != lastLogHealth ||
                    warning != lastLogWarning ||
                    stale != lastLogStale ||
                    speedifyValid != lastLogSpeedifyValid ||
                    activeCount != lastLogActiveCount ||
                    backupCount != lastLogBackupCount ||
                    latencyBucket != lastLogLatencyBucket ||
                    jitterBucket != lastLogJitterBucket ||
                    lossBucket != lastLogLossBucket ||
                    confidenceBand != lastLogConfidenceBand;

                bool heartbeatDue =
                    !isShuttingDown &&
                    (now - lastOperationalLog).TotalSeconds >= 5;

                bool tooSoon =
                    (now - lastOperationalLog).TotalMilliseconds < 1200;

                if ((!changed && !heartbeatDue) || (tooSoon && !heartbeatDue))
                    return;

                lastOperationalLog = now;
                lastLogRoute = route;
                lastLogSource = source;
                lastLogTunnelConnected = tunnelConnected;
                lastLogServer = server;
                lastLogPublicIp = publicIp;
                lastLogHealth = health;
                lastLogWarning = warning;
                lastLogStale = stale;
                lastLogSpeedifyValid = speedifyValid;
                lastLogActiveCount = activeCount;
                lastLogBackupCount = backupCount;
                lastLogLatencyBucket = latencyBucket;
                lastLogJitterBucket = jitterBucket;
                lastLogLossBucket = lossBucket;
                lastLogConfidenceBand = confidenceBand;
            }

            Logger.Log("resolver",
                "route=" + route +
                " | src=" + source +
                " | tunnel=" + speedify.TunnelConnected +
                " | server=" + server +
                " | ip=" + publicIp +
                " | active=" + speedify.ActiveConnectionCount +
                " | backup=" + speedify.BackupConnectionCount +
                " | lat=" + ShortMs(speedify.AggregateLatencyMs > 0 ? speedify.AggregateLatencyMs : speedify.LatencyMs) +
                " | jit=" + ShortMs(speedify.AggregateJitterMs) +
                " | valid=" + (speedifyValid ? "True" : "False") +
                " | warn=" + warning);

            Logger.Log("unified",
                "route=" + route +
                " | stable=" + ShortMs(s.FastPingStableMs) +
                " | inst=" + ShortMs(s.FastPingInstantMs) +
                " | lat=" + ShortMs(s.DisplayedPingMs > 0 ? s.DisplayedPingMs : s.LatencyMs) +
                " | jit=" + ShortMs(s.JitterMs) +
                " | loss=" + ShortLoss(s.LossRate) +
                " | conf=" + ShortConf(s.Confidence) +
                " | stale=" + s.IsStale +
                " | quality=" + ShortHealth(s.HealthState) +
                " | valid=" + (speedifyValid ? "True" : "False"));
        }

        // =========================================================
        // UNIFIED CSV LOGGING
        // =========================================================

        public static void LogUnifiedCsv(UnifiedNetworkState s)
        {
            if (s == null)
                return;

            SpeedifyTelemetrySnapshot speedify = s.SpeedifyTelemetry ?? new SpeedifyTelemetrySnapshot();

            DateTime now = DateTime.Now;
            double shownPing = s.DisplayedPingMs > 0 ? s.DisplayedPingMs : s.LatencyMs;

            lock (unifiedCsvLockObj)
            {
                string source = SafeCsv(GetUnifiedSourceForCsv(s.SourceKind));
                string health = SafeCsv(s.HealthState);
                string server = SafeCsv(speedify.ConnectedServerName);
                string mode = SafeCsv(speedify.CurrentMode);

                bool tunnel = speedify.TunnelConnected;
                bool aggCongested = speedify.AggregateCongested;
                bool spike = s.SpikeDetected;
                bool stale = s.IsStale;

                int pingBucket = GetPingBucket(shownPing);
                int jitterBucket = GetJitterBucket(s.JitterMs);
                int lossBucket = GetRateBucket(s.LossRate);
                int badBucket = GetRateBucket(s.BadQualityRate);

                bool changed =
                    source != lastUnifiedSource ||
                    health != lastUnifiedHealth ||
                    server != lastUnifiedServer ||
                    mode != lastUnifiedMode ||
                    tunnel != lastUnifiedTunnel ||
                    aggCongested != lastUnifiedAggCongested ||
                    spike != lastUnifiedSpike ||
                    stale != lastUnifiedStale ||
                    pingBucket != lastUnifiedPingBucket ||
                    jitterBucket != lastUnifiedJitterBucket ||
                    lossBucket != lastUnifiedLossBucket ||
                    badBucket != lastUnifiedBadBucket;

                bool heartbeatDue =
                    (now - lastUnifiedCsvLog).TotalSeconds >= 5;

                bool tooSoon =
                    (now - lastUnifiedCsvLog).TotalMilliseconds < 1200;

                if ((!changed && !heartbeatDue) || (tooSoon && !heartbeatDue))
                    return;

                lastUnifiedCsvLog = now;
                lastUnifiedSource = source;
                lastUnifiedHealth = health;
                lastUnifiedServer = server;
                lastUnifiedMode = mode;
                lastUnifiedTunnel = tunnel;
                lastUnifiedAggCongested = aggCongested;
                lastUnifiedSpike = spike;
                lastUnifiedStale = stale;
                lastUnifiedPingBucket = pingBucket;
                lastUnifiedJitterBucket = jitterBucket;
                lastUnifiedLossBucket = lossBucket;
                lastUnifiedBadBucket = badBucket;

                try
                {
                    bool writeHeader = !File.Exists(unifiedCsvFile) || new FileInfo(unifiedCsvFile).Length == 0;

                    using (StreamWriter writer = new StreamWriter(unifiedCsvFile, true))
                    {
                        if (writeHeader)
                            writer.WriteLine(BuildUnifiedHeader());

                        writer.WriteLine(BuildUnifiedRow(s, now));
                    }
                }
                catch
                {
                }
            }
        }

        private static string BuildUnifiedHeader()
        {
            return string.Join(";", new[]
            {
                "time",
                "source",
                "health",
                "health_reason",
                "notes",

                "ping",
                "jitter",
                "baseline",
                "floor",
                "confidence",
                "loss",
                "bad",
                "spike",
                "stale",

                "nat_text",
                "nat_color",

                "fp_displayed_ping",
                "fp_instant",
                "fp_stable",
                "fp_baseline",
                "fp_floor",
                "fp_jitter",
                "fp_confidence",
                "fp_loss",
                "fp_bad",
                "fp_spike",
                "fp_stale",
                "fp_primary_fresh",
                "fp_secondary_fresh",
                "fp_eval_mode",
                "fp_divergence",
                "fp_tunnel_latency",
                "fp_primary_target",
                "fp_secondary_target",

                "fp_primary_instant",
                "fp_primary_stable",
                "fp_primary_baseline",
                "fp_primary_floor",
                "fp_primary_jitter",
                "fp_primary_confidence",
                "fp_primary_loss",
                "fp_primary_bad",

                "fp_secondary_instant",
                "fp_secondary_stable",
                "fp_secondary_baseline",
                "fp_secondary_floor",
                "fp_secondary_jitter",
                "fp_secondary_confidence",
                "fp_secondary_loss",
                "fp_secondary_bad",

                "sp_tunnel_connected",
                "sp_agg_congested",
                "sp_active",
                "sp_backup",
                "sp_agg_latency",
                "sp_agg_jitter",
                "sp_agg_rx_bps",
                "sp_agg_tx_bps",
                "sp_agg_total_bps",
                "sp_server",
                "sp_public_ip",
                "sp_primary_remote_ip",
                "sp_remote_ip",
                "sp_adapter1_latency",
                "sp_adapter2_latency",
                "sp_mode",
                "sp_state",

                "sp_structured_core_complete",
                "sp_has_connection_stats",
                "sp_has_networks",
                "sp_has_session_stats",
                "sp_has_current_state",
                "sp_has_connected_server",
                "sp_parse_warning_count",

                "sp_latency_legacy",
                "sp_base_latency",
                "sp_jitter_legacy",
                "sp_external_ping",
                "sp_mos",
                "sp_connected_legacy",
                "sp_congested_legacy",
                "sp_download_congested",
                "sp_upload_congested",
                "sp_rx_bytes_per_sec",
                "sp_tx_bytes_per_sec",
                "sp_rx_bytes",
                "sp_tx_bytes",

                "sp_recent_latency",
                "sp_average_latency",
                "sp_max_latency",
                "sp_micro_jitter",
                "sp_dynamic_base",
                "sp_queue_delay_ms",
                "sp_packet_loss",

                "sp_lag_score",
                "sp_queue_delay",
                "sp_queue_growth",
                "sp_queue_acceleration",
                "sp_latency_drift",
                "sp_retransmission_growth",
                "sp_queue_pressure",
                "sp_congestion_score",
                "sp_aggression_factor",
                "sp_server_score",
                "sp_lag_probability",
                "sp_prediction_confidence_text",
                "sp_prediction_confidence_num",
                "sp_dominant_signal",
                "sp_lag_event_type",
                "sp_game_phase",
                "sp_network_state",
                "sp_control_state",
                "sp_pending_lag_events",
                "sp_burst_multiplier",
                "sp_learning_enabled",
                "sp_pattern_match_score",
                "sp_signature_avg_queue_growth",
                "sp_lag_pressure",
                "sp_retransmission_rate",
                "sp_issue_source",

                "delta_fp_sp",
                "delta_res_fp",
                "delta_res_sp"
            });
        }

        private static string BuildUnifiedRow(UnifiedNetworkState s, DateTime now)
        {
            FastPingTelemetrySnapshot fastPing = s.FastPingTelemetry ?? new FastPingTelemetrySnapshot();
            SpeedifyTelemetrySnapshot speedify = s.SpeedifyTelemetry ?? new SpeedifyTelemetrySnapshot();

            double shownPing = s.DisplayedPingMs > 0 ? s.DisplayedPingMs : s.LatencyMs;

            double deltaFpSp = SafeDelta(fastPing.DisplayedPingMs, speedify.AggregateLatencyMs);
            double deltaResFp = SafeDelta(shownPing, fastPing.DisplayedPingMs);
            double deltaResSp = SafeDelta(shownPing, speedify.AggregateLatencyMs);

            return string.Join(";", new[]
            {
                now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                SafeCsv(GetUnifiedSourceForCsv(s.SourceKind)),
                SafeCsv(s.HealthState),
                SafeCsv(s.HealthReason),
                SafeCsv(s.Notes),

                F(shownPing),
                F(s.JitterMs),
                F(s.BaselineLatencyMs),
                F(s.FloorLatencyMs),
                F(s.Confidence),
                F(s.LossRate),
                F(s.BadQualityRate),
                B(s.SpikeDetected),
                B(s.IsStale),

                SafeCsv(s.DisplayNatText),
                SafeCsv(s.DisplayNatColorKey),

                F(fastPing.DisplayedPingMs),
                F(fastPing.InstantMs),
                F(fastPing.StableMs),
                F(fastPing.BaselineMs),
                F(fastPing.FloorMs),
                F(fastPing.JitterMs),
                F(fastPing.Confidence),
                F(fastPing.LossRate),
                F(fastPing.BadQualityRate),
                B(fastPing.SpikeDetected),
                B(fastPing.IsStale),
                B(fastPing.PrimaryFresh),
                B(fastPing.SecondaryFresh),
                B(fastPing.IsEvaluationMode),
                F(fastPing.PrimarySecondaryDivergenceMs),
                F(fastPing.TunnelLatencyMs),
                SafeCsv(fastPing.PrimaryTarget),
                SafeCsv(fastPing.SecondaryTarget),

                F(fastPing.PrimaryInstantMs),
                F(fastPing.PrimaryStableMs),
                F(fastPing.PrimaryBaselineMs),
                F(fastPing.PrimaryFloorMs),
                F(fastPing.PrimaryJitterMs),
                F(fastPing.PrimaryConfidence),
                F(fastPing.PrimaryLossRate),
                F(fastPing.PrimaryBadQualityRate),

                F(fastPing.SecondaryInstantMs),
                F(fastPing.SecondaryStableMs),
                F(fastPing.SecondaryBaselineMs),
                F(fastPing.SecondaryFloorMs),
                F(fastPing.SecondaryJitterMs),
                F(fastPing.SecondaryConfidence),
                F(fastPing.SecondaryLossRate),
                F(fastPing.SecondaryBadQualityRate),

                B(speedify.TunnelConnected),
                B(speedify.AggregateCongested),
                I(speedify.ActiveConnectionCount),
                I(speedify.BackupConnectionCount),
                F(speedify.AggregateLatencyMs),
                F(speedify.AggregateJitterMs),
                F(speedify.AggregateRxBps),
                F(speedify.AggregateTxBps),
                F(speedify.AggregateTotalBps),
                SafeCsv(speedify.ConnectedServerName),
                SafeCsv(speedify.ConnectedServerPublicIp),
                SafeCsv(speedify.PrimaryRemoteIp),
                SafeCsv(speedify.SpeedifyRemoteIp),
                F(speedify.Adapter1Latency),
                F(speedify.Adapter2Latency),
                SafeCsv(speedify.CurrentMode),
                SafeCsv(speedify.CurrentState),

                B(speedify.StructuredCoreComplete),
                B(speedify.HasConnectionStats),
                B(speedify.HasNetworks),
                B(speedify.HasSessionStats),
                B(speedify.HasCurrentState),
                B(speedify.HasConnectedServer),
                I(speedify.ParseWarningCount),

                F(speedify.LatencyMs),
                F(speedify.BaseLatencyMs),
                F(speedify.LegacyJitterMs),
                F(speedify.ExternalPingMs),
                F(speedify.MosScore),
                B(speedify.ConnectedLegacy),
                B(speedify.CongestedLegacy),
                B(speedify.DownloadCongested),
                B(speedify.UploadCongested),
                L(speedify.ReceiveBytesPerSecond),
                L(speedify.SendBytesPerSecond),
                L(speedify.ReceiveBytes),
                L(speedify.SendBytes),

                F(s.RecentLatencyMs),
                F(s.AverageLatencyMs),
                F(s.MaxLatencyMs),
                F(s.MicroJitterMs),
                F(s.DynamicBaseMs),
                F(s.QueueDelayMs),
                F(s.PacketLoss),

                F(speedify.LagScore),
                F(speedify.QueueDelay),
                F(speedify.QueueGrowth),
                F(speedify.QueueAcceleration),
                F(speedify.LatencyDrift),
                F(speedify.RetransmissionGrowth),
                F(speedify.QueuePressure),
                F(s.CongestionScore),
                F(speedify.AggressionFactor),
                F(speedify.ServerScore),
                F(s.LagProbability),
                SafeCsv(speedify.PredictionConfidence),
                F(s.PredictionConfidence),
                SafeCsv(speedify.DominantSignal),
                SafeCsv(speedify.LagEventType),
                SafeCsv(speedify.GamePhase),
                SafeCsv(speedify.NetworkState),
                SafeCsv(speedify.ControlState),
                I(speedify.PendingLagEvents),
                F(speedify.BurstMultiplier),
                B(speedify.LearningEnabled),
                F(speedify.PatternMatchScore),
                F(speedify.SignatureAvgQueueGrowth),
                F(speedify.LagPressure),
                F(speedify.RetransmissionRate),
                SafeCsv(speedify.IssueSource),

                F(deltaFpSp),
                F(deltaResFp),
                F(deltaResSp)
            });
        }

        // =========================================================
        // INTERNAL WRITE
        // =========================================================

        private static void WriteLine(string line)
        {
            lock (lockObj)
            {
                try
                {
                    File.AppendAllText(logFile, line + Environment.NewLine);
                }
                catch
                {
                }
            }

            Debug.WriteLine(line);
        }

        private static void WriteDeviationLine(string line)
        {
            lock (deviationLockObj)
            {
                try
                {
                    File.AppendAllText(deviationLogFile, line + Environment.NewLine);
                }
                catch
                {
                }
            }

            Debug.WriteLine(line);
        }

        private static void WriteRawFastPingLine(string line)
        {
            lock (rawFastPingLockObj)
            {
                try
                {
                    File.AppendAllText(rawFastPingLogFile, line + Environment.NewLine);
                }
                catch
                {
                }
            }

            Debug.WriteLine(line);
        }

        private static void WriteSpeedifyDebugLine(string line)
        {
            lock (speedifyDebugLockObj)
            {
                try
                {
                    File.AppendAllText(speedifyDebugLogFile, line + Environment.NewLine);
                }
                catch
                {
                }
            }

            Debug.WriteLine(line);
        }

        // =========================================================
        // HELPERS
        // =========================================================

        private static bool IsSpeedifyDebugCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return false;

            switch (category.Trim().ToLowerInvariant())
            {
                case "speedify":
                case "speedify-ws":
                case "speedify-snapshot-raw":
                case "speedify-snapshot-event":
                case "speedify-raw-in":
                case "speedify-payload":
                case "speedify-snapshot-payload":
                case "speedify-state":
                case "speedify-link":
                case "speedify-event":
                case "speedify-reader-snapshot":
                case "speedify-parsed":
                case "speedify-conn-row":
                case "speedify-net-row":
                case "speedify-snapshot":
                case "speedify-check":
                case "speedify-new-event":
                case "speedify-snapshot-skip":
                    return true;

                default:
                    return false;
            }
        }

        private static bool IsSpeedifyDeepDebugOnlyCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return false;

            switch (category.Trim().ToLowerInvariant())
            {
                case "speedify-conn-row":
                case "speedify-parsed":
                case "speedify-net-row":
                case "speedify-new-event":
                case "speedify-snapshot-event":
                case "speedify-snapshot-skip":
                    return true;

                default:
                    return false;
            }
        }

        private static bool ShouldWriteSpeedifyDebug(string category, string message)
        {
            lock (speedifyDebugFilterLockObj)
            {
                if (ShouldSuppressDuplicateDowngrade(category, message))
                    return false;

                if (string.Equals(category, "speedify-check", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(category, "speedify-snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    return ShouldWriteByChangeOrHeartbeat(
                        category,
                        BuildSpeedifyFingerprint(category, message));
                }
            }

            return true;
        }

        private static bool ShouldSuppressDuplicateDowngrade(string category, string message)
        {
            if (!string.Equals(category, "speedify-link", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            string key = ExtractPipeValue(message, "key");
            if (string.IsNullOrWhiteSpace(key))
                return false;

            bool isDowngrade = message.IndexOf("downgraded", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isDowngrade)
            {
                if (downgradedLinkKeysAwaitingRecovery.Contains(key))
                    return true;

                downgradedLinkKeysAwaitingRecovery.Add(key);
                return false;
            }

            string connectedValue = ExtractPipeValue(message, "connected");
            bool recovered = string.Equals(connectedValue, "true", StringComparison.OrdinalIgnoreCase);
            if (recovered)
                downgradedLinkKeysAwaitingRecovery.Remove(key);

            return false;
        }

        private static bool ShouldWriteByChangeOrHeartbeat(string category, string fingerprint)
        {
            DateTime nowUtc = DateTime.UtcNow;
            DateTime lastUtc;
            lastSpeedifyCategoryLogUtc.TryGetValue(category, out lastUtc);

            bool heartbeatDue = (nowUtc - lastUtc).TotalSeconds >= 5.0;
            string previousFingerprint;
            bool hadPrevious = lastSpeedifyCategoryFingerprint.TryGetValue(category, out previousFingerprint);
            bool changed = !hadPrevious || !string.Equals(previousFingerprint, fingerprint, StringComparison.Ordinal);

            if (!changed && !heartbeatDue)
                return false;

            lastSpeedifyCategoryFingerprint[category] = fingerprint ?? string.Empty;
            lastSpeedifyCategoryLogUtc[category] = nowUtc;
            return true;
        }

        private static string BuildSpeedifyFingerprint(string category, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            if (string.Equals(category, "speedify-check", StringComparison.OrdinalIgnoreCase))
            {
                return
                    "fresh=" + ExtractPipeValue(message, "fresh") +
                    "|connected=" + ExtractPipeValue(message, "connected") +
                    "|active=" + ExtractPipeValue(message, "active") +
                    "|valid=" + ExtractPipeValue(message, "valid") +
                    "|reason=" + ExtractPipeValue(message, "reason") +
                    "|structuredCore=" + ExtractPipeValue(message, "structuredCore") +
                    "|warnings=" + ExtractPipeValue(message, "warnings");
            }

            if (string.Equals(category, "speedify-snapshot", StringComparison.OrdinalIgnoreCase))
            {
                return
                    "connected=" + ExtractPipeValue(message, "connected") +
                    "|server=" + ExtractPipeValue(message, "server") +
                    "|pubIp=" + ExtractPipeValue(message, "pubIp") +
                    "|links=" + ExtractPipeValue(message, "links") +
                    "|warnings=" + ExtractPipeValue(message, "warnings");
            }

            return message;
        }

        private static string ExtractPipeValue(string message, string key)
        {
            if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(key))
                return string.Empty;

            string[] parts = message.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i]?.Trim();
                if (string.IsNullOrWhiteSpace(part))
                    continue;

                int equalsIndex = part.IndexOf('=');
                if (equalsIndex <= 0)
                    continue;

                string partKey = part.Substring(0, equalsIndex).Trim();
                if (!string.Equals(partKey, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                return part.Substring(equalsIndex + 1).Trim();
            }

            return string.Empty;
        }

        private static bool IsStartupDebugCategory(string category)
        {
            return string.Equals(
                category?.Trim(),
                "startup",
                StringComparison.OrdinalIgnoreCase);
        }

        private static int GetRateBucket(double value)
        {
            if (value < 0)
                return -1;

            if (value < 0.10)
                return 0;

            if (value < 0.20)
                return 1;

            if (value < 0.35)
                return 2;

            return 3;
        }

        private static int GetPingBucket(double value)
        {
            if (value <= 0)
                return -1;

            if (value < 25)
                return 0;

            if (value < 35)
                return 1;

            if (value < 50)
                return 2;

            if (value < 80)
                return 3;

            return 4;
        }

        private static int GetJitterBucket(double value)
        {
            if (value < 0)
                return -1;

            if (value < 5)
                return 0;

            if (value < 10)
                return 1;

            if (value < 18)
                return 2;

            return 3;
        }

        private static string SafeText(object value)
        {
            if (value == null)
                return "-";

            string s = value.ToString();
            return string.IsNullOrWhiteSpace(s) ? "-" : s;
        }

        private static string ShortHealth(object h)
        {
            string s = h?.ToString() ?? "-";

            if (s.StartsWith("Healthy", StringComparison.OrdinalIgnoreCase)) return "OK";
            if (s.StartsWith("OffBaseline", StringComparison.OrdinalIgnoreCase)) return "OB";
            if (s.StartsWith("Warning", StringComparison.OrdinalIgnoreCase)) return "WRN";
            if (s.StartsWith("Critical", StringComparison.OrdinalIgnoreCase)) return "CRIT";
            if (s.StartsWith("Stale", StringComparison.OrdinalIgnoreCase)) return "STAL";

            return s.Length > 4 ? s.Substring(0, 4).ToUpperInvariant() : s.ToUpperInvariant();
        }

        private static string ShortMs(double v)
        {
            if (v <= 0)
                return "--";

            return ((int)Math.Round(v)).ToString();
        }

        private static string ShortPct(double v)
        {
            if (v < 0)
                return "--";

            return ((int)Math.Round(v * 100)).ToString();
        }

        private static string Short01Compact(double v)
        {
            if (v < 0)
                return "--";

            return ((int)Math.Round(v * 100)).ToString();
        }

        private static string ShortLoss(double v)
        {
            if (v < 0)
                return "-";

            return v.ToString("0.00");
        }

        private static string ShortConf(double v)
        {
            if (v < 0)
                return "-";

            return v.ToString("0.00");
        }

        private static string ShortNat(object color)
        {
            string c = color?.ToString()?.ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(c))
                return "-";

            switch (c)
            {
                case "green":
                    return "G";
                case "gold":
                case "yellow":
                    return "Y";
                case "red":
                    return "R";
                case "gray":
                case "grey":
                    return "X";
                default:
                    return "?";
            }
        }

        private static string F(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0)
                return "";

            return v.ToString("0.0");
        }

        private static string I(int v)
        {
            if (v < 0)
                return "";

            return v.ToString();
        }

        private static string L(long v)
        {
            if (v < 0)
                return "";

            return v.ToString();
        }

        private static string B(bool v)
        {
            return v ? "1" : "0";
        }

        private static UnifiedNetworkSourceKind GetUnifiedSourceForCsv(UnifiedNetworkSourceKind sourceKind)
        {
            if (sourceKind == UnifiedNetworkSourceKind.FastPingWithSpeedify)
                return UnifiedNetworkSourceKind.FastPing;

            return sourceKind;
        }

        private static string SafeCsv(object value)
        {
            if (value == null)
                return "";

            string s = value.ToString();
            if (string.IsNullOrWhiteSpace(s))
                return "";

            return s.Replace(";", ",")
                    .Replace(Environment.NewLine, " ")
                    .Replace("\n", " ")
                    .Replace("\r", " ");
        }

        private static double SafeDelta(double a, double b)
        {
            if (a <= 0 || b <= 0)
                return -1;

            return a - b;
        }

        private static string ResolveRouteLabel(UnifiedNetworkState state)
        {
            if (state != null &&
                (state.SourceKind == UnifiedNetworkSourceKind.Speedify ||
                 state.SourceKind == UnifiedNetworkSourceKind.FastPingWithSpeedify))
                return "speedify";

            return "raw";
        }

        private static string ResolveSourceLabel(UnifiedNetworkSourceKind sourceKind)
        {
            switch (sourceKind)
            {
                case UnifiedNetworkSourceKind.FastPing:
                    return "fastping";
                case UnifiedNetworkSourceKind.Speedify:
                    return "speedify";
                case UnifiedNetworkSourceKind.FastPingWithSpeedify:
                    return "fastping+speedify";
                default:
                    return "none";
            }
        }

        private static string ResolveWarningLabel(UnifiedNetworkState state)
        {
            if (state == null)
                return "-";

            if (state.HealthState == UnifiedNetworkHealthState.Critical)
                return "Critical";

            if (state.HealthState == UnifiedNetworkHealthState.Warning)
                return "Warning";

            if (state.HealthState == UnifiedNetworkHealthState.Stale || state.IsStale)
                return "Stale";

            return "None";
        }

        private static int GetConfidenceBand(double value)
        {
            if (value < 0)
                return -1;

            if (value < 0.35)
                return 0;

            if (value < 0.70)
                return 1;

            return 2;
        }
    }
}
