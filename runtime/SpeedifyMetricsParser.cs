using System;
using System.Collections.Generic;
using System.Globalization;

namespace WTFnetApp
{
    public class SpeedifyMetricsParser
    {
        private double smoothJitter = 0;
        private double previousLatency = 0;
        private double dynamicBase = 0;

        private readonly Queue<double> latencyWindow = new Queue<double>();
        private const int LatencyWindowSize = 8;

        public MetricsSnapshot ParseConnectionStats(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                MetricsSnapshot snapshot = new MetricsSnapshot();
                snapshot.TimeUtc = DateTime.UtcNow;

                snapshot.ActiveConnectionCount = ReadIntValue(json, "numActive");
                snapshot.BackupConnectionCount = ReadIntValue(json, "numBackup");

                string aggregateRow = FindAggregateRow(json);
                string firstRealRow = FindConnectedRow(json, 0);
                string secondRealRow = FindConnectedRow(json, 1);

                if (!string.IsNullOrWhiteSpace(aggregateRow))
                {
                    snapshot.TunnelConnected = ReadBoolValue(aggregateRow, "cnnct");
                    snapshot.AggregateCongested = ReadBoolValue(aggregateRow, "congested");
                    snapshot.AggregateLatencyMs = ReadDoubleValue(aggregateRow, "ltMs");
                    snapshot.AggregateJitterMs = ReadDoubleValue(aggregateRow, "jtMs");
                    snapshot.AggregateRxBps = ReadDoubleValue(aggregateRow, "rcvBps");
                    snapshot.AggregateTxBps = ReadDoubleValue(aggregateRow, "sndBps");
                    snapshot.AggregateTotalBps = ReadDoubleValue(aggregateRow, "totBps");

                    snapshot.IsCongested = ReadBoolValue(aggregateRow, "congested");
                    snapshot.IsDownloadCongested = ReadBoolValue(aggregateRow, "downloadCongested");
                    snapshot.IsUploadCongested = ReadBoolValue(aggregateRow, "uploadCongested");
                }

                if (!string.IsNullOrWhiteSpace(firstRealRow))
                {
                    snapshot.PrimaryRemoteIp = ReadStringValue(firstRealRow, "remoteIp");
                    snapshot.SpeedifyRemoteIp = string.IsNullOrWhiteSpace(snapshot.PrimaryRemoteIp)
                        ? "speedify"
                        : snapshot.PrimaryRemoteIp;

                    snapshot.Adapter1Latency = ReadDoubleValue(firstRealRow, "ltMs");
                    snapshot.ReceiveBytesPerSecond = (long)ReadDoubleValue(firstRealRow, "rcvBps");
                    snapshot.SendBytesPerSecond = (long)ReadDoubleValue(firstRealRow, "sndBps");

                    double mos = ReadDoubleValue(firstRealRow, "mos");
                    if (mos > 0)
                        snapshot.MosScore = mos;

                    double rcvBytes = ReadDoubleValue(firstRealRow, "rcvBytes");
                    if (rcvBytes > 0)
                        snapshot.ReceiveBytes = (long)rcvBytes;

                    double sndBytes = ReadDoubleValue(firstRealRow, "sndBytes");
                    if (sndBytes > 0)
                        snapshot.SendBytes = (long)sndBytes;
                }

                if (!string.IsNullOrWhiteSpace(secondRealRow))
                {
                    snapshot.Adapter2Latency = ReadDoubleValue(secondRealRow, "ltMs");
                }

                double latency = snapshot.AggregateLatencyMs > 0
                    ? snapshot.AggregateLatencyMs
                    : snapshot.Adapter1Latency;

                double jitter = snapshot.AggregateJitterMs > 0
                    ? snapshot.AggregateJitterMs
                    : 0;

                double microJitter = previousLatency > 0 && latency > 0
                    ? Math.Abs(latency - previousLatency)
                    : 0;

                smoothJitter = smoothJitter == 0
                    ? jitter
                    : smoothJitter * 0.6 + jitter * 0.4;

                if (latency > 0 && (dynamicBase == 0 || latency < dynamicBase))
                    dynamicBase = latency;

                previousLatency = latency;
                AddLatencySample(latency);

                snapshot.Recent = latency;
                snapshot.Average = GetWindowAverage();
                snapshot.Max = GetWindowMax();
                snapshot.Jitter = smoothJitter;
                snapshot.MicroJitter = microJitter;
                snapshot.DynamicBase = dynamicBase;

                snapshot.Connected = snapshot.TunnelConnected;
                snapshot.Congested = snapshot.AggregateCongested;

                snapshot.LatencyMs = latency;
                snapshot.BaseLatencyMs = dynamicBase;
                snapshot.QueueDelayMs = Math.Max(0, snapshot.LatencyMs - snapshot.BaseLatencyMs);

                return snapshot;
            }
            catch (Exception ex)
            {
                Logger.Log("speedify-parser", "ParseConnectionStats error: " + ex.Message);
                return null;
            }
        }

        private string FindAggregateRow(string json)
        {
            return FindRow(json, "\"guid\":\"speedify\"");
        }

        private string FindConnectedRow(string json, int connectedIndex)
        {
            int found = 0;
            int pos = 0;

            while (pos < json.Length)
            {
                int start = json.IndexOf('{', pos);
                if (start < 0)
                    break;

                int end = FindMatchingBrace(json, start);
                if (end < 0)
                    break;

                string row = json.Substring(start, end - start + 1);

                string guid = ReadStringValue(row, "guid");
                string protocol = ReadStringValue(row, "protocol");
                bool connected = ReadBoolValue(row, "cnnct");

                bool isAggregate = string.Equals(guid, "speedify", StringComparison.OrdinalIgnoreCase);
                bool isProxy = string.Equals(protocol, "proxy", StringComparison.OrdinalIgnoreCase);

                if (!isAggregate && !isProxy && connected)
                {
                    if (found == connectedIndex)
                        return row;

                    found++;
                }

                pos = end + 1;
            }

            return null;
        }

        private string FindRow(string json, string mustContain)
        {
            int pos = 0;

            while (pos < json.Length)
            {
                int start = json.IndexOf('{', pos);
                if (start < 0)
                    break;

                int end = FindMatchingBrace(json, start);
                if (end < 0)
                    break;

                string row = json.Substring(start, end - start + 1);
                if (row.IndexOf(mustContain, StringComparison.OrdinalIgnoreCase) >= 0)
                    return row;

                pos = end + 1;
            }

            return null;
        }

        private int FindMatchingBrace(string text, int startIndex)
        {
            int depth = 0;
            bool inString = false;
            bool escape = false;

            for (int i = startIndex; i < text.Length; i++)
            {
                char c = text[i];

                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escape = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                    continue;

                if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        private void AddLatencySample(double latency)
        {
            if (latency <= 0)
                return;

            latencyWindow.Enqueue(latency);

            while (latencyWindow.Count > LatencyWindowSize)
                latencyWindow.Dequeue();
        }

        private double GetWindowAverage()
        {
            if (latencyWindow.Count == 0)
                return 0;

            double sum = 0;
            foreach (double value in latencyWindow)
                sum += value;

            return sum / latencyWindow.Count;
        }

        private double GetWindowMax()
        {
            if (latencyWindow.Count == 0)
                return 0;

            double max = double.MinValue;
            foreach (double value in latencyWindow)
            {
                if (value > max)
                    max = value;
            }

            return max;
        }

        private static string ReadStringValue(string text, string name)
        {
            string key = "\"" + name + "\"";
            int keyPos = text.IndexOf(key, StringComparison.Ordinal);
            if (keyPos < 0)
                return null;

            int colon = text.IndexOf(':', keyPos + key.Length);
            if (colon < 0)
                return null;

            int firstQuote = text.IndexOf('"', colon + 1);
            if (firstQuote < 0)
                return null;

            int i = firstQuote + 1;
            bool escape = false;

            while (i < text.Length)
            {
                char c = text[i];

                if (escape)
                {
                    escape = false;
                    i++;
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    i++;
                    continue;
                }

                if (c == '"')
                    return text.Substring(firstQuote + 1, i - firstQuote - 1);

                i++;
            }

            return null;
        }

        private static bool ReadBoolValue(string text, string name)
        {
            string raw = ReadRawValue(text, name);
            if (raw == null)
                return false;

            return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static double ReadDoubleValue(string text, string name)
        {
            string raw = ReadRawValue(text, name);
            if (string.IsNullOrWhiteSpace(raw))
                return 0;

            double value;
            if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                return value;

            return 0;
        }

        private static int ReadIntValue(string text, string name)
        {
            string raw = ReadRawValue(text, name);
            if (string.IsNullOrWhiteSpace(raw))
                return 0;

            int intValue;
            if (int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out intValue))
                return intValue;

            double doubleValue;
            if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out doubleValue))
                return (int)doubleValue;

            return 0;
        }

        private static string ReadRawValue(string text, string name)
        {
            string key = "\"" + name + "\"";
            int keyPos = text.IndexOf(key, StringComparison.Ordinal);
            if (keyPos < 0)
                return null;

            int colon = text.IndexOf(':', keyPos + key.Length);
            if (colon < 0)
                return null;

            int start = colon + 1;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;

            if (start >= text.Length)
                return null;

            if (text[start] == '"')
            {
                int endQuote = text.IndexOf('"', start + 1);
                if (endQuote > start)
                    return text.Substring(start + 1, endQuote - start - 1);

                return null;
            }

            int end = start;
            while (end < text.Length)
            {
                char c = text[end];
                if (c == ',' || c == '}' || c == ']')
                    break;
                end++;
            }

            return text.Substring(start, end - start).Trim();
        }
    }
}