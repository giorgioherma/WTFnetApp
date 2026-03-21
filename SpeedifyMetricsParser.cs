using System;
using System.Collections.Generic;

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

            if (!json.Contains("connections") &&
                !json.Contains("bytes_recv") &&
                !json.Contains("bytes_sent"))
                return null;

            double latency = 0;
            double jitter = 0;
            double adapter1Latency = 0;
            double adapter2Latency = 0;
            bool congested = false;
            long receiveBytes = 0;
            long sendBytes = 0;

            string speedifyRemoteIp = "speedify";
            TryExtractString(json, "remoteIp", 0, json.Length - 1, out speedifyRemoteIp);

            if (TryExtractValue(json, "ltMs", 0, json.Length - 1, out double ltMs))
                latency = ltMs;

            if (TryExtractValue(json, "jtMs", 0, json.Length - 1, out double jtMs))
                jitter = jtMs;

            adapter1Latency = latency;
            adapter2Latency = 0;

            if (TryExtractBool(json, "congested", 0, json.Length - 1, out bool isCongested))
                congested = isCongested;

            if (TryExtractValue(json, "rcvBytes", 0, json.Length - 1, out double rcvBytes))
                receiveBytes = (long)rcvBytes;
            else if (TryExtractValue(json, "bytes_recv", 0, json.Length - 1, out double bytesRecv))
                receiveBytes = (long)bytesRecv;

            if (TryExtractValue(json, "sndBytes", 0, json.Length - 1, out double sndBytes))
                sendBytes = (long)sndBytes;
            else if (TryExtractValue(json, "bytes_sent", 0, json.Length - 1, out double bytesSent))
                sendBytes = (long)bytesSent;

            double microJitter = previousLatency > 0
                ? Math.Abs(latency - previousLatency)
                : 0;

            smoothJitter = smoothJitter == 0
                ? jitter
                : smoothJitter * 0.6 + jitter * 0.4;

            if (latency > 0 && (dynamicBase == 0 || latency < dynamicBase))
                dynamicBase = latency;

            previousLatency = latency;

            AddLatencySample(latency);

            double windowAverage = GetWindowAverage();
            double windowMax = GetWindowMax();

            var snapshot = new MetricsSnapshot
            {
                Recent = latency,
                Average = windowAverage,
                Max = windowMax,
                Jitter = smoothJitter,
                MicroJitter = microJitter,
                PacketLoss = 0,
                DynamicBase = dynamicBase,
                Adapter1Latency = adapter1Latency,
                Adapter2Latency = adapter2Latency,
                Congested = congested,
                Connected = true,
                ReceiveBytes = receiveBytes,
                SendBytes = sendBytes,
                SpeedifyRemoteIp = string.IsNullOrWhiteSpace(speedifyRemoteIp) ? "speedify" : speedifyRemoteIp
            };

            int globalStart = 0;
            int globalEnd = json.Length - 1;

            if (TryExtractValue(json, "ltMs", globalStart, globalEnd, out double latencyMs))
                snapshot.LatencyMs = latencyMs;

            snapshot.BaseLatencyMs = dynamicBase;
            snapshot.QueueDelayMs = Math.Max(0, snapshot.LatencyMs - snapshot.BaseLatencyMs);

            if (TryExtractValue(json, "mos", globalStart, globalEnd, out double mos))
                snapshot.MosScore = mos;

            if (TryExtractBool(json, "congested", globalStart, globalEnd, out bool snapCongested))
                snapshot.IsCongested = snapCongested;

            if (TryExtractBool(json, "downloadCongested", globalStart, globalEnd, out bool isDownloadCongested))
                snapshot.IsDownloadCongested = isDownloadCongested;

            if (TryExtractBool(json, "uploadCongested", globalStart, globalEnd, out bool isUploadCongested))
                snapshot.IsUploadCongested = isUploadCongested;

            if (TryExtractValue(json, "rcvBps", globalStart, globalEnd, out double rcvBps))
                snapshot.ReceiveBytesPerSecond = (long)rcvBps;

            if (TryExtractValue(json, "sndBps", globalStart, globalEnd, out double sndBps2))
                snapshot.SendBytesPerSecond = (long)sndBps2;

            if (TryExtractValue(json, "combined_mbps", globalStart, globalEnd, out double combinedMbps))
                snapshot.CombinedBandwidthMbps = combinedMbps;

            if (TryExtractValue(json, "num_connections", globalStart, globalEnd, out double activeConnections))
                snapshot.ActiveConnections = (int)activeConnections;

            if (TryExtractValue(json, "num_failovers", globalStart, globalEnd, out double failoverCount))
                snapshot.FailoverCount = (int)failoverCount;

            if (snapshot.PacketLoss <= 0 &&
                TryExtractValue(json, "packets_in", globalStart, globalEnd, out double packetsIn) &&
                TryExtractValue(json, "dropped_incoming", globalStart, globalEnd, out double droppedIncoming) &&
                packetsIn > 0)
            {
                snapshot.PacketLoss = droppedIncoming / packetsIn;
            }

            if (TryExtractValue(json, "retrans_bytes", globalStart, globalEnd, out double retransBytes) &&
                TryExtractValue(json, "bytes_sent", globalStart, globalEnd, out double totalBytesSent) &&
                totalBytesSent > 0)
            {
                snapshot.RetransmissionRate = retransBytes / totalBytesSent;
            }

            return snapshot;
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

        private bool TryExtractValue(string json, string key, int start, int endBoundary, out double value)
        {
            value = 0;

            int keyPos = json.IndexOf("\"" + key + "\"", start, StringComparison.Ordinal);
            if (keyPos < 0 || keyPos > endBoundary) return false;

            int colon = json.IndexOf(':', keyPos);
            if (colon < 0 || colon > endBoundary) return false;

            int pos = colon + 1;
            int end = pos;

            while (end < json.Length && end <= endBoundary && json[end] != ',' && json[end] != '}')
                end++;

            string raw = json.Substring(pos, end - pos).Trim();

            return double.TryParse(
                raw,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }

        private bool TryExtractBool(string json, string key, int start, int endBoundary, out bool value)
        {
            value = false;

            int keyPos = json.IndexOf("\"" + key + "\"", start, StringComparison.Ordinal);
            if (keyPos < 0 || keyPos > endBoundary) return false;

            int colon = json.IndexOf(':', keyPos);
            if (colon < 0 || colon > endBoundary) return false;

            int pos = colon + 1;
            while (pos < json.Length && pos <= endBoundary && char.IsWhiteSpace(json[pos]))
                pos++;

            if (pos >= json.Length || pos > endBoundary) return false;

            if (pos + 3 <= json.Length && json[pos] == 't')
            {
                value = true;
                return true;
            }

            if (pos + 4 <= json.Length && json[pos] == 'f')
            {
                value = false;
                return true;
            }

            return false;
        }

        private bool TryExtractString(string json, string key, int start, int endBoundary, out string value)
        {
            value = null;

            int keyPos = json.IndexOf("\"" + key + "\"", start, StringComparison.Ordinal);
            if (keyPos < 0 || keyPos > endBoundary)
                return false;

            int colon = json.IndexOf(':', keyPos);
            if (colon < 0 || colon > endBoundary)
                return false;

            int pos = colon + 1;
            while (pos < json.Length && pos <= endBoundary && char.IsWhiteSpace(json[pos]))
                pos++;

            if (pos >= json.Length || pos > endBoundary || json[pos] != '"')
                return false;

            int end = json.IndexOf('"', pos + 1);
            if (end < 0 || end > endBoundary)
                return false;

            value = json.Substring(pos + 1, end - pos - 1);
            return true;
        }
    }
}