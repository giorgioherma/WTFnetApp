using System;
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

        private static DateTime lastAnalysisLog = DateTime.MinValue;
        private static DateTime lastUnifiedCsvLog = DateTime.MinValue;

        private static string lastAnalysisHealth = string.Empty;
        private static string lastAnalysisNatColor = string.Empty;
        private static bool lastAnalysisStale;
        private static bool lastAnalysisSpike;
        private static int lastAnalysisLossBucket = -1;
        private static int lastAnalysisBadBucket = -1;
        private static int lastAnalysisPingBucket = -1;
        private static int lastAnalysisJitterBucket = -1;

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

        // =========================================================
        // BASIC LOG
        // =========================================================

        public static void Log(string category, string message)
        {
            string line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}";

            if (IsSpeedifyDebugCategory(category))
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

        // =========================================================
        // ANALYSIS LOGGING (compact + change-based + heartbeat)
        // =========================================================

        public static void LogAnalysis(UnifiedNetworkState s)
        {
            if (s == null)
                return;

            DateTime now = DateTime.Now;

            double shownPing = s.DisplayedPingMs > 0 ? s.DisplayedPingMs : s.LatencyMs;

            lock (lockObj)
            {
                string health = SafeText(s.HealthState);
                string natColor = SafeText(s.DisplayNatColorKey);

                bool stale = s.IsStale;
                bool spike = s.SpikeDetected;

                int lossBucket = GetRateBucket(s.LossRate);
                int badBucket = GetRateBucket(s.BadQualityRate);
                int pingBucket = GetPingBucket(shownPing);
                int jitterBucket = GetJitterBucket(s.JitterMs);

                bool changed =
                    health != lastAnalysisHealth ||
                    natColor != lastAnalysisNatColor ||
                    stale != lastAnalysisStale ||
                    spike != lastAnalysisSpike ||
                    lossBucket != lastAnalysisLossBucket ||
                    badBucket != lastAnalysisBadBucket ||
                    pingBucket != lastAnalysisPingBucket ||
                    jitterBucket != lastAnalysisJitterBucket;

                bool heartbeatDue =
                    (now - lastAnalysisLog).TotalSeconds >= 15;

                bool tooSoon =
                    (now - lastAnalysisLog).TotalMilliseconds < 1200;

                if ((!changed && !heartbeatDue) || (tooSoon && !heartbeatDue))
                    return;

                lastAnalysisLog = now;
                lastAnalysisHealth = health;
                lastAnalysisNatColor = natColor;
                lastAnalysisStale = stale;
                lastAnalysisSpike = spike;
                lastAnalysisLossBucket = lossBucket;
                lastAnalysisBadBucket = badBucket;
                lastAnalysisPingBucket = pingBucket;
                lastAnalysisJitterBucket = jitterBucket;
            }

            string line =
                $"{now:HH:mm:ss} [a] " +
                $"{ShortHealth(s.HealthState)} " +
                $"p{ShortMs(shownPing)} " +
                $"j{ShortMs(s.JitterMs)} " +
                $"l{ShortPct(s.LossRate)} " +
                $"b{ShortPct(s.BadQualityRate)} " +
                $"c{Short01Compact(s.Confidence)} " +
                $"{(s.SpikeDetected ? "!" : "-")}" +
                $"{(s.IsStale ? "S" : "-")}" +
                $"{ShortNat(s.DisplayNatColorKey)}";

            WriteLine(line);
        }

        // =========================================================
        // UNIFIED CSV LOGGING
        // =========================================================

        public static void LogUnifiedCsv(UnifiedNetworkState s)
        {
            if (s == null)
                return;

            DateTime now = DateTime.Now;
            double shownPing = s.DisplayedPingMs > 0 ? s.DisplayedPingMs : s.LatencyMs;

            lock (unifiedCsvLockObj)
            {
                string source = SafeCsv(GetUnifiedSourceForCsv(s.SourceKind));
                string health = SafeCsv(s.HealthState);
                string server = SafeCsv(s.ConnectedServerName);
                string mode = SafeCsv(s.SpeedifyCurrentMode);

                bool tunnel = s.TunnelConnected;
                bool aggCongested = s.AggregateCongested;
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
            double shownPing = s.DisplayedPingMs > 0 ? s.DisplayedPingMs : s.LatencyMs;

            double deltaFpSp = SafeDelta(s.FastPingDisplayedPingMs, s.AggregateLatencyMs);
            double deltaResFp = SafeDelta(shownPing, s.FastPingDisplayedPingMs);
            double deltaResSp = SafeDelta(shownPing, s.AggregateLatencyMs);

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

                F(s.FastPingDisplayedPingMs),
                F(s.FastPingInstantMs),
                F(s.FastPingStableMs),
                F(s.FastPingBaselineMs),
                F(s.FastPingFloorMs),
                F(s.FastPingJitterMs),
                F(s.FastPingConfidence),
                F(s.FastPingLossRate),
                F(s.FastPingBadQualityRate),
                B(s.FastPingSpikeDetected),
                B(s.FastPingIsStale),
                B(s.FastPingPrimaryFresh),
                B(s.FastPingSecondaryFresh),
                B(s.FastPingIsEvaluationMode),
                F(s.FastPingPrimarySecondaryDivergenceMs),
                F(s.FastPingTunnelLatencyMs),
                SafeCsv(s.FastPingPrimaryTarget),
                SafeCsv(s.FastPingSecondaryTarget),

                F(s.FastPingPrimaryInstantMs),
                F(s.FastPingPrimaryStableMs),
                F(s.FastPingPrimaryBaselineMs),
                F(s.FastPingPrimaryFloorMs),
                F(s.FastPingPrimaryJitterMs),
                F(s.FastPingPrimaryConfidence),
                F(s.FastPingPrimaryLossRate),
                F(s.FastPingPrimaryBadQualityRate),

                F(s.FastPingSecondaryInstantMs),
                F(s.FastPingSecondaryStableMs),
                F(s.FastPingSecondaryBaselineMs),
                F(s.FastPingSecondaryFloorMs),
                F(s.FastPingSecondaryJitterMs),
                F(s.FastPingSecondaryConfidence),
                F(s.FastPingSecondaryLossRate),
                F(s.FastPingSecondaryBadQualityRate),

                B(s.TunnelConnected),
                B(s.AggregateCongested),
                I(s.ActiveConnectionCount),
                I(s.BackupConnectionCount),
                F(s.AggregateLatencyMs),
                F(s.AggregateJitterMs),
                F(s.AggregateRxBps),
                F(s.AggregateTxBps),
                F(s.AggregateTotalBps),
                SafeCsv(s.ConnectedServerName),
                SafeCsv(s.ConnectedServerPublicIp),
                SafeCsv(s.PrimaryRemoteIp),
                SafeCsv(s.SpeedifyRemoteIp),
                F(s.Adapter1Latency),
                F(s.Adapter2Latency),
                SafeCsv(s.SpeedifyCurrentMode),
                SafeCsv(s.SpeedifyCurrentState),

                B(s.SpeedifyStructuredCoreComplete),
                B(s.SpeedifyHasConnectionStats),
                B(s.SpeedifyHasNetworks),
                B(s.SpeedifyHasSessionStats),
                B(s.SpeedifyHasCurrentState),
                B(s.SpeedifyHasConnectedServer),
                I(s.SpeedifyParseWarningCount),

                F(s.SpeedifyLatencyMs),
                F(s.SpeedifyBaseLatencyMs),
                F(s.SpeedifyLegacyJitterMs),
                F(s.SpeedifyExternalPingMs),
                F(s.SpeedifyMosScore),
                B(s.SpeedifyConnectedLegacy),
                B(s.SpeedifyCongestedLegacy),
                B(s.SpeedifyDownloadCongested),
                B(s.SpeedifyUploadCongested),
                L(s.SpeedifyReceiveBytesPerSecond),
                L(s.SpeedifySendBytesPerSecond),
                L(s.SpeedifyReceiveBytes),
                L(s.SpeedifySendBytes),

                F(s.RecentLatencyMs),
                F(s.AverageLatencyMs),
                F(s.MaxLatencyMs),
                F(s.MicroJitterMs),
                F(s.DynamicBaseMs),
                F(s.QueueDelayMs),
                F(s.PacketLoss),

                F(s.SpeedifyLagScore),
                F(s.SpeedifyQueueDelay),
                F(s.SpeedifyQueueGrowth),
                F(s.SpeedifyQueueAcceleration),
                F(s.SpeedifyLatencyDrift),
                F(s.SpeedifyRetransmissionGrowth),
                F(s.SpeedifyQueuePressure),
                F(s.CongestionScore),
                F(s.SpeedifyAggressionFactor),
                F(s.SpeedifyServerScore),
                F(s.LagProbability),
                SafeCsv(s.SpeedifyPredictionConfidence),
                F(s.PredictionConfidence),
                SafeCsv(s.SpeedifyDominantSignal),
                SafeCsv(s.SpeedifyLagEventType),
                SafeCsv(s.SpeedifyGamePhase),
                SafeCsv(s.SpeedifyNetworkState),
                SafeCsv(s.SpeedifyControlState),
                I(s.SpeedifyPendingLagEvents),
                F(s.SpeedifyBurstMultiplier),
                B(s.SpeedifyLearningEnabled),
                F(s.SpeedifyPatternMatchScore),
                F(s.SpeedifySignatureAvgQueueGrowth),
                F(s.SpeedifyLagPressure),
                F(s.SpeedifyRetransmissionRate),
                SafeCsv(s.SpeedifyIssueSource),

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
                case "speedify-parser":
                case "speedify-snapshot-raw":
                case "speedify-snapshot-event":
                    return true;

                default:
                    return false;
            }
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
    }
}
