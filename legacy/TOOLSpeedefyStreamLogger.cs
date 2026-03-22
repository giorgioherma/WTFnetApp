using System;
using System.IO;
using System.Text;

namespace WTFnetApp
{
    public static class SpeedifyStreamLogger
    {
        private static readonly object LockObj = new object();

        private static bool snapshotCaptured;
        private static string currentState;
        private static string networks;
        private static string connectionStats;
        private static string sessionStats;
        private static string streamingStats;
        private static string systemData;

        private static string SnapshotFilePath
        {
            get { return $"speedify_snapshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log"; }
        }

        public static void CaptureEvent(string eventName, string payload)
        {
            if (snapshotCaptured)
                return;

            switch (eventName)
            {
                case "report_current_state":
                    if (string.IsNullOrEmpty(currentState))
                        currentState = payload;
                    break;

                case "report_networks":
                    if (string.IsNullOrEmpty(networks))
                        networks = payload;
                    break;

                case "report_connection_stats":
                    if (string.IsNullOrEmpty(connectionStats))
                        connectionStats = payload;
                    break;

                case "report_session_stats":
                    if (string.IsNullOrEmpty(sessionStats))
                        sessionStats = payload;
                    break;

                case "report_streaming_stats":
                    if (string.IsNullOrEmpty(streamingStats))
                        streamingStats = payload;
                    break;

                case "report_system_data":
                    if (string.IsNullOrEmpty(systemData))
                        systemData = payload;
                    break;
            }

            if (HasFullSnapshot())
                SaveSnapshot();
        }

        private static bool HasFullSnapshot()
        {
            return !string.IsNullOrEmpty(currentState)
                && !string.IsNullOrEmpty(networks)
                && !string.IsNullOrEmpty(connectionStats)
                && !string.IsNullOrEmpty(sessionStats)
                && !string.IsNullOrEmpty(streamingStats)
                && !string.IsNullOrEmpty(systemData);
        }

        private static void SaveSnapshot()
        {
            if (snapshotCaptured)
                return;

            lock (LockObj)
            {
                if (snapshotCaptured)
                    return;

                var sb = new StringBuilder(1024 * 128);

                sb.AppendLine("============================================================");
                sb.AppendLine("SPEEDIFY FULL SNAPSHOT");
                sb.AppendLine($"TIME: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                sb.AppendLine("============================================================");
                sb.AppendLine();

                AppendSection(sb, "report_current_state", currentState);
                AppendSection(sb, "report_networks", networks);
                AppendSection(sb, "report_connection_stats", connectionStats);
                AppendSection(sb, "report_session_stats", sessionStats);
                AppendSection(sb, "report_streaming_stats", streamingStats);
                AppendSection(sb, "report_system_data", systemData);

                File.WriteAllText(SnapshotFilePath, sb.ToString(), Encoding.UTF8);

                snapshotCaptured = true;

                Logger.Log("speedify-stream", $"full snapshot saved to {SnapshotFilePath}");
            }
        }

        private static void AppendSection(StringBuilder sb, string sectionName, string payload)
        {
            sb.AppendLine($"[{sectionName}]");
            sb.AppendLine(payload ?? string.Empty);
            sb.AppendLine();
        }

        public static void Reset()
        {
            lock (LockObj)
            {
                snapshotCaptured = false;
                currentState = null;
                networks = null;
                connectionStats = null;
                sessionStats = null;
                streamingStats = null;
                systemData = null;
            }

            Logger.Log("speedify-stream", "snapshot collector reset");
        }
    }
}