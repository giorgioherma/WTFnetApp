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

        public static void LogAnalysis(NetworkAnalysisFrame frame)
        {
            if (frame == null)
                return;

            if ((DateTime.Now - lastAnalysisLog).TotalMilliseconds < 1000)
                return;

            lastAnalysisLog = DateTime.Now;

            string ts = $"[{frame.Time:HH:mm:ss}]";
            string columns =
                $"LAT {frame.Latency,5:F1}ms | " +
                $"JIT {frame.Jitter,4:F1} | " +
                $"LOSS {frame.PacketLoss * 100,4:F1}% | " +
                $"Q {frame.QueueDelay,5:F1} | " +
                $"DRIFT {frame.LatencyDrift,6:+0.0;-0.0;0.0} | " +
                $"RETR {frame.RetransmissionRate,5:F3} | " +
                $"SCORE {frame.CongestionScore,4:F1} | " +
                $"SRC {frame.Source}";

            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] {ts}");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] {columns}");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] ╔════════════════ NETWORK ANALYSIS ════════════════╗");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] LATENCY       {frame.Latency,7:F1} ms");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] JITTER        {frame.Jitter,7:F1} ms");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] PACKET LOSS   {frame.PacketLoss * 100,7:F1} %");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] DRIFT         {frame.LatencyDrift,7:+0.0;-0.0;0.0} ms");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] ");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] QUEUE DELAY   {frame.QueueDelay,7:F1}");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] QUEUE GROWTH  {frame.QueueGrowth,7:+0.0;-0.0;0.0}");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] QUEUE ACCEL   {frame.QueueAcceleration,7:+0.0;-0.0;0.0}");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] ");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] RETRANS       {frame.RetransmissionRate,7:F3}");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] SCORE         {frame.CongestionScore,7:F1}");
            WriteLine($"{DateTime.Now:HH:mm:ss.fff} [analysis] SOURCE        {frame.Source}");
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
