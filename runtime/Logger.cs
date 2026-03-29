using System;
using System.Diagnostics;
using System.IO;

namespace WTFnetApp
{
    public static class Logger
    {
        private static readonly object lockObj = new object();
        private static readonly object deviationLockObj = new object();

        private static string logFile = "wtfnet_log.txt";
        private static string deviationLogFile = "wtfnet_deviation_log.txt";

        private static DateTime lastAnalysisLog = DateTime.MinValue;

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

        // =========================================================
        // BASIC LOG
        // =========================================================
        public static void Log(string category, string message)
        {
            string line =
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}";

            WriteLine(line);
        }

        // =========================================================
        // CRITICAL EVENTS (disconnects, failures, etc.)
        // =========================================================
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
        // ANALYSIS LOGGING (throttled)
        // =========================================================
        public static void LogAnalysis(MetricsSnapshot s)
        {
            if (s == null)
                return;

            if ((DateTime.Now - lastAnalysisLog).TotalMilliseconds < 1000)
                return;

            lastAnalysisLog = DateTime.Now;

            string ts = $"[{s.TimeUtc:yyyy-MM-dd HH:mm:ss}]";

            string columns =
                $"LAT {s.LatencyMs,5:F1}ms | " +
                $"JIT {s.Jitter,4:F1} | " +
                $"Q {s.QueueDelayMs,5:F1} | " +
                $"DRIFT {s.LatencyDrift,6:+0.0;-0.0;0.0} | " +
                $"SCORE {s.CongestionScore,4:F1} | " +
                $"LP {s.LagPressure,4:F2} | " +
                $"AGG {s.AggressionFactor,4:F2} | " +
                $"EVT {s.LagEventType} | " +
                $"NET {s.NetworkState}";

            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] {ts}");
            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] {columns}");

            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] ╔════════════════ NETWORK ANALYSIS ════════════════╗");
            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] LATENCY       {s.LatencyMs,7:F1} ms");
            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] JITTER        {s.Jitter,7:F1} ms");
            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] DRIFT         {s.LatencyDrift,7:+0.0;-0.0;0.0} ms");

            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] ");
            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] QUEUE DELAY   {s.QueueDelayMs,7:F1}");
            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] QUEUE GROWTH  {s.QueueGrowth,7:+0.0;-0.0;0.0}");
            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] QUEUE ACCEL   {s.QueueAcceleration,7:+0.0;-0.0;0.0}");
            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] QUEUE PRESS   {s.QueuePressure,7:F1}");

            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] ");
            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] SCORE         {s.CongestionScore,7:F1}");
            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] SOURCE        {s.IssueSource}");

            WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [analysis] ╚══════════════════════════════════════════════════╝");
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
    }
}