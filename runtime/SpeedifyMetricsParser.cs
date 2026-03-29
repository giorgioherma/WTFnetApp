using System;
using System.Collections.Generic;
using System.Text.Json;

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
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;

                    MetricsSnapshot snapshot = new MetricsSnapshot();

                    snapshot.TimeUtc = DateTime.UtcNow;

                    JsonElement numActive;
                    if (root.TryGetProperty("numActive", out numActive))
                        snapshot.ActiveConnectionCount = ReadInt(numActive);

                    JsonElement numBackup;
                    if (root.TryGetProperty("numBackup", out numBackup))
                        snapshot.BackupConnectionCount = ReadInt(numBackup);

                    JsonElement aggregateRow = default(JsonElement);
                    bool hasAggregate = false;

                    JsonElement firstRealRow = default(JsonElement);
                    bool hasFirstReal = false;

                    JsonElement secondRealRow = default(JsonElement);
                    bool hasSecondReal = false;

                    JsonElement connections;
                    if (root.TryGetProperty("connections", out connections) &&
                        connections.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement row in connections.EnumerateArray())
                        {
                            string guid = ReadString(row, "guid");
                            string protocol = ReadString(row, "protocol");
                            bool connected = ReadBool(row, "cnnct");

                            bool isAggregate = string.Equals(guid, "speedify", StringComparison.OrdinalIgnoreCase);
                            bool isProxy = string.Equals(protocol, "proxy", StringComparison.OrdinalIgnoreCase);

                            if (isAggregate)
                            {
                                aggregateRow = row;
                                hasAggregate = true;
                                continue;
                            }

                            if (!isProxy && connected)
                            {
                                if (!hasFirstReal)
                                {
                                    firstRealRow = row;
                                    hasFirstReal = true;
                                }
                                else if (!hasSecondReal)
                                {
                                    secondRealRow = row;
                                    hasSecondReal = true;
                                }
                            }
                        }
                    }

                    // Aggregate tunnel row = main truth for ping/tunnel
                    if (hasAggregate)
                    {
                        snapshot.TunnelConnected = ReadBool(aggregateRow, "cnnct");
                        snapshot.AggregateCongested = ReadBool(aggregateRow, "congested");
                        snapshot.AggregateLatencyMs = ReadDouble(aggregateRow, "ltMs");
                        snapshot.AggregateJitterMs = ReadDouble(aggregateRow, "jtMs");
                        snapshot.AggregateRxBps = ReadDouble(aggregateRow, "rcvBps");
                        snapshot.AggregateTxBps = ReadDouble(aggregateRow, "sndBps");
                        snapshot.AggregateTotalBps = ReadDouble(aggregateRow, "totBps");
                    }

                    // Real connected links
                    if (hasFirstReal)
                    {
                        snapshot.PrimaryRemoteIp = ReadString(firstRealRow, "remoteIp");
                        snapshot.SpeedifyRemoteIp = string.IsNullOrWhiteSpace(snapshot.PrimaryRemoteIp)
                            ? "speedify"
                            : snapshot.PrimaryRemoteIp;

                        snapshot.Adapter1Latency = ReadDouble(firstRealRow, "ltMs");
                    }

                    if (hasSecondReal)
                    {
                        snapshot.Adapter2Latency = ReadDouble(secondRealRow, "ltMs");
                    }

                    // Legacy compatibility values
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

                    if (hasAggregate)
                    {
                        snapshot.IsCongested = ReadBool(aggregateRow, "congested");
                        snapshot.IsDownloadCongested = ReadBool(aggregateRow, "downloadCongested");
                        snapshot.IsUploadCongested = ReadBool(aggregateRow, "uploadCongested");
                    }

                    if (hasFirstReal)
                    {
                        snapshot.ReceiveBytesPerSecond = (long)ReadDouble(firstRealRow, "rcvBps");
                        snapshot.SendBytesPerSecond = (long)ReadDouble(firstRealRow, "sndBps");

                        double mos = ReadDouble(firstRealRow, "mos");
                        if (mos > 0)
                            snapshot.MosScore = mos;

                        double rcvBytes = ReadDouble(firstRealRow, "rcvBytes");
                        if (rcvBytes > 0)
                            snapshot.ReceiveBytes = (long)rcvBytes;

                        double sndBytes = ReadDouble(firstRealRow, "sndBytes");
                        if (sndBytes > 0)
                            snapshot.SendBytes = (long)sndBytes;
                    }

                    return snapshot;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("speedify-parser", "ParseConnectionStats error: " + ex.Message);
                return null;
            }
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

        private static string ReadString(JsonElement parent, string name)
        {
            JsonElement value;
            if (parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();

            return null;
        }

        private static bool ReadBool(JsonElement parent, string name)
        {
            JsonElement value;
            if (parent.TryGetProperty(name, out value))
            {
                if (value.ValueKind == JsonValueKind.True) return true;
                if (value.ValueKind == JsonValueKind.False) return false;
            }

            return false;
        }

        private static double ReadDouble(JsonElement parent, string name)
        {
            JsonElement value;
            if (parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Number)
            {
                try { return value.GetDouble(); }
                catch { return 0; }
            }

            return 0;
        }

        private static int ReadInt(JsonElement value)
        {
            try
            {
                return value.GetInt32();
            }
            catch
            {
                try { return (int)value.GetDouble(); }
                catch { return 0; }
            }
        }
    }
}