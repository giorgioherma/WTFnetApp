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

        private static string logFile = "wtfnet_log.txt";
        private static string deviationLogFile = "wtfnet_deviation_log.txt";
        private static string rawFastPingLogFile = "wtfnet_fastping_raw.txt";

        private static DateTime lastAnalysisLog = DateTime.MinValue;

        private static string lastAnalysisHealth = string.Empty;
        private static string lastAnalysisNatColor = string.Empty;
        private static bool lastAnalysisStale;
        private static bool lastAnalysisSpike;
        private static int lastAnalysisLossBucket = -1;
        private static int lastAnalysisBadBucket = -1;
        private static int lastAnalysisPingBucket = -1;
        private static int lastAnalysisJitterBucket = -1;

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

        // =========================================================
        // BASIC LOG
        // =========================================================

        public static void Log(string category, string message)
        {
            string line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}";

            WriteLine(line);
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

        // =========================================================
        // HELPERS
        // =========================================================

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

        private static string FormatMs(double value)
        {
            if (value <= 0)
                return "-1";

            return value.ToString("0.0") + "ms";
        }

        private static string FormatPct(double value)
        {
            if (value < 0)
                return "-1";

            return (value * 100.0).ToString("0.0") + "%";
        }

        private static string Format01(double value)
        {
            if (value < 0)
                return "-1";

            return value.ToString("0.00");
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

        private static string Short01(double v)
        {
            if (v < 0)
                return "--";

            return v.ToString("0.00");
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
    }
}