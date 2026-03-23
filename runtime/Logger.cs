using System;
using System.IO;

namespace WTFnetApp
{
    public static class Logger
    {
        static readonly object lockObj = new object();
        static string logFile = "wtfnet_log.txt";
        static DateTime lastAnalysisLog = DateTime.MinValue;

        public static void Log(string category, string message)
        {
            string line =
                $"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}";

            WriteLine(line);
        }

        // ✅ SAFE analysis logging (no dependency on external classes)
        public static void LogAnalysis(MetricsSnapshot s)
        {
            if (s == null)
                return;

            if ((DateTime.Now - lastAnalysisLog).TotalMilliseconds < 1000)
                return;

            lastAnalysisLog = DateTime.Now;

            string ts = $"[{s.TimeUtc:HH:mm:ss}]";

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

            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] {ts}");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] {columns}");

            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] ╔════════════════ NETWORK ANALYSIS ════════════════╗");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] LATENCY       {s.LatencyMs,7:F1} ms");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] JITTER        {s.Jitter,7:F1} ms");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] DRIFT         {s.LatencyDrift,7:+0.0;-0.0;0.0} ms");

            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] ");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] QUEUE DELAY   {s.QueueDelayMs,7:F1}");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] QUEUE GROWTH  {s.QueueGrowth,7:+0.0;-0.0;0.0}");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] QUEUE ACCEL   {s.QueueAcceleration,7:+0.0;-0.0;0.0}");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] QUEUE PRESS   {s.QueuePressure,7:F1}");

            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] ");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] SCORE         {s.CongestionScore,7:F1}");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] SOURCE        {s.IssueSource}");

            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] ╚══════════════════════════════════════════════════╝");
        }

        private static void WriteLine(string line)
        {
            lock (lockObj)
            {
                File.AppendAllText(logFile, line + Environment.NewLine);
            }

            System.Diagnostics.Debug.WriteLine(line);
        }
    }
}