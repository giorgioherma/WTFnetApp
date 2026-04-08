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

        private readonly SpeedifyStructuredState structuredState = new SpeedifyStructuredState();

        public SpeedifyStructuredState CurrentStructuredState
        {
            get { return structuredState; }
        }

        public MetricsSnapshot ParseEvent(string eventName, string json)
        {
            if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;

                    switch (eventName)
                    {
                        case "report_connection_stats":
                            ParseConnectionStatsIntoStructured(root);
                            return BuildMetricsSnapshotFromStructured();

                        case "report_networks":
                            ParseNetworksIntoStructured(root);
                            return null;

                        case "report_session_stats":
                            ParseSessionStatsIntoStructured(root);
                            return null;

                        case "report_current_state":
                            ParseCurrentStateIntoStructured(root);
                            return null;

                        case "report_connected_server":
                            ParseConnectedServerIntoStructured(root);
                            return null;

                        default:
                            return null;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("speedify-parser", "ParseEvent error [" + eventName + "]: " + ex.Message);
                return null;
            }
        }

        public MetricsSnapshot ParseConnectionStats(string json)
        {
            return ParseEvent("report_connection_stats", json);
        }

        private void ParseConnectionStatsIntoStructured(JsonElement root)
        {
            structuredState.TimeUtc = DateTime.UtcNow;
            structuredState.HasConnectionStats = true;
            structuredState.LastConnectionStatsUtc = DateTime.UtcNow;

            structuredState.Tunnel.ActiveConnectionCount = ReadIntProperty(root, "numActive");
            structuredState.Tunnel.BackupConnectionCount = ReadIntProperty(root, "numBackup");

            bool hasAggregate = false;

            JsonElement connections;
            if (root.TryGetProperty("connections", out connections) &&
                connections.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement row in connections.EnumerateArray())
                {
                    string guid = ReadString(row, "guid");
                    string protocol = ReadString(row, "protocol");
                    string key = BuildLinkKey(row);

                    bool isAggregate = string.Equals(guid, "speedify", StringComparison.OrdinalIgnoreCase);
                    bool isProxy = string.Equals(protocol, "proxy", StringComparison.OrdinalIgnoreCase);

                    if (isAggregate)
                    {
                        ParseAggregateTunnelRow(row);
                        hasAggregate = true;
                        continue;
                    }

                    SpeedifyLinkState link = GetOrCreateLink(key);

                    link.Guid = FirstNonEmpty(link.Guid, guid);
                    link.Key = key;
                    link.Name = FirstNonEmpty(link.Name, ReadString(row, "name"));
                    link.Isp = FirstNonEmpty(link.Isp, ReadString(row, "isp"));
                    link.IspType = FirstNonEmpty(link.IspType, ReadString(row, "ispType"));
                    link.Description = FirstNonEmpty(
                        link.Description,
                        ReadString(row, "description"),
                        ReadString(row, "desc"));
                    link.Protocol = FirstNonEmpty(link.Protocol, protocol);
                    link.Type = FirstNonEmpty(link.Type, ReadString(row, "type"));

                    link.Connected = ReadBool(row, "cnnct");
                    link.IsProxy = isProxy;
                    link.IsAggregate = false;
                    link.Offline = ReadBoolWithFallback(row, "offline", link.Offline);
                    link.Sleeping = ReadBoolWithFallback(row, "sleeping", link.Sleeping);

                    link.Congested = ReadBoolWithFallback(row, "congested", link.Congested);
                    link.DownloadCongested = ReadBoolWithFallback(row, "downloadCongested", link.DownloadCongested);
                    link.UploadCongested = ReadBoolWithFallback(row, "uploadCongested", link.UploadCongested);

                    link.Priority = ReadIntOrDefault(row, "priority", link.Priority);
                    link.WorkingPriority = ReadIntOrDefault(row, "workingPriority", link.WorkingPriority);
                    link.NumConns = ReadIntOrDefault(row, "numConns", link.NumConns);
                    link.ConnectionState = ReadIntOrDefault(row, "connectionState", link.ConnectionState);

                    link.LatencyMs = ReadDoubleOrDefault(row, "ltMs", link.LatencyMs);
                    link.JitterMs = ReadDoubleOrDefault(row, "jtMs", link.JitterMs);
                    link.Mos = ReadDoubleOrDefault(row, "mos", link.Mos);

                    link.RxBps = ReadDoubleOrDefault(row, "rcvBps", link.RxBps);
                    link.TxBps = ReadDoubleOrDefault(row, "sndBps", link.TxBps);
                    link.TotalBps = ReadDoubleOrDefault(row, "totBps", link.TotalBps);

                    link.ReceiveBytes = ReadLongOrDefault(row, "rcvBytes", link.ReceiveBytes);
                    link.SendBytes = ReadLongOrDefault(row, "sndBytes", link.SendBytes);

                    link.RcvEstMbps = ReadDoubleOrDefault(row, "rcvEst", link.RcvEstMbps);
                    link.SndEstMbps = ReadDoubleOrDefault(row, "sndEst", link.SndEstMbps);

                    link.Ip = FirstNonEmpty(link.Ip, ReadString(row, "ip"));
                    link.LocalIp = FirstNonEmpty(link.LocalIp, ReadString(row, "localIp"));
                    link.PrivateIp = FirstNonEmpty(link.PrivateIp, ReadString(row, "privateIp"));
                    link.RemoteIp = FirstNonEmpty(link.RemoteIp, ReadString(row, "remoteIp"));

                    link.IpList = ReadStringArray(row, "ipList", link.IpList);
                }
            }

            if (!hasAggregate)
                AddWarningOnce("connection_stats_missing_aggregate");

            ValidateStructuredCompleteness();
        }

        private void ParseAggregateTunnelRow(JsonElement row)
        {
            structuredState.Tunnel.Connected = ReadBool(row, "cnnct");
            structuredState.Tunnel.Congested = ReadBoolWithFallback(row, "congested", structuredState.Tunnel.Congested);
            structuredState.Tunnel.DownloadCongested = ReadBoolWithFallback(row, "downloadCongested", structuredState.Tunnel.DownloadCongested);
            structuredState.Tunnel.UploadCongested = ReadBoolWithFallback(row, "uploadCongested", structuredState.Tunnel.UploadCongested);

            structuredState.Tunnel.LatencyMs = ReadDoubleOrDefault(row, "ltMs", structuredState.Tunnel.LatencyMs);
            structuredState.Tunnel.JitterMs = ReadDoubleOrDefault(row, "jtMs", structuredState.Tunnel.JitterMs);
            structuredState.Tunnel.RxBps = ReadDoubleOrDefault(row, "rcvBps", structuredState.Tunnel.RxBps);
            structuredState.Tunnel.TxBps = ReadDoubleOrDefault(row, "sndBps", structuredState.Tunnel.TxBps);
            structuredState.Tunnel.TotalBps = ReadDoubleOrDefault(row, "totBps", structuredState.Tunnel.TotalBps);
        }

        private void ParseNetworksIntoStructured(JsonElement root)
        {
            structuredState.TimeUtc = DateTime.UtcNow;
            structuredState.HasNetworks = true;
            structuredState.LastNetworksUtc = DateTime.UtcNow;

            JsonElement networks;
            if (!TryGetNetworksArray(root, out networks))
            {
                AddWarningOnce("networks_dataset_missing_array");
                return;
            }

            foreach (JsonElement row in networks.EnumerateArray())
            {
                string guid = ReadString(row, "guid");
                string key = BuildLinkKey(row);
                SpeedifyLinkState link = GetOrCreateLink(key);

                link.Guid = FirstNonEmpty(link.Guid, guid);
                link.Key = key;
                link.Name = FirstNonEmpty(link.Name, ReadString(row, "name"));
                link.Isp = FirstNonEmpty(link.Isp, ReadString(row, "isp"));
                link.IspType = FirstNonEmpty(link.IspType, ReadString(row, "ispType"));
                link.Description = FirstNonEmpty(
                    link.Description,
                    ReadString(row, "description"),
                    ReadString(row, "desc"));
                link.Protocol = FirstNonEmpty(link.Protocol, ReadString(row, "protocol"));
                link.Type = FirstNonEmpty(link.Type, ReadString(row, "type"));

                link.IsAggregate = string.Equals(link.Guid, "speedify", StringComparison.OrdinalIgnoreCase);

                link.Connected = ReadBoolWithEitherFallback(row, "connected", "cnnct", link.Connected);
                link.Offline = ReadBoolWithFallback(row, "offline", link.Offline);
                link.Sleeping = ReadBoolWithFallback(row, "sleeping", link.Sleeping);
                link.IsProxy = ReadBoolWithFallback(row, "isProxy", link.IsProxy);

                link.Priority = ReadIntOrDefault(row, "priority", link.Priority);
                link.WorkingPriority = ReadIntOrDefault(row, "workingPriority", link.WorkingPriority);
                link.NumConns = ReadIntOrDefault(row, "numConns", link.NumConns);
                link.ConnectionState = ReadIntOrDefault(row, "connectionState", link.ConnectionState);

                link.Ip = FirstNonEmpty(link.Ip, ReadString(row, "ip"));
                link.LocalIp = FirstNonEmpty(link.LocalIp, ReadString(row, "localIp"));
                link.PrivateIp = FirstNonEmpty(link.PrivateIp, ReadString(row, "privateIp"));
                link.RemoteIp = FirstNonEmpty(link.RemoteIp, ReadString(row, "remoteIp"));

                link.IpList = ReadStringArray(row, "ipList", link.IpList);
            }

            ValidateStructuredCompleteness();
        }

        private void ParseSessionStatsIntoStructured(JsonElement root)
        {
            structuredState.TimeUtc = DateTime.UtcNow;
            structuredState.HasSessionStats = true;
            structuredState.LastSessionStatsUtc = DateTime.UtcNow;

            structuredState.SessionWindows.Clear();

            JsonElement windowsElement;
            if (root.TryGetProperty("windows", out windowsElement) &&
                windowsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty windowProperty in windowsElement.EnumerateObject())
                {
                    SpeedifySessionWindowStats window = ParseWindow(windowProperty.Name, windowProperty.Value);
                    structuredState.SessionWindows.Add(window);
                }

                return;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                bool foundAny = false;

                foreach (JsonProperty prop in root.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object && LooksLikeSessionWindowObject(prop.Value))
                    {
                        structuredState.SessionWindows.Add(ParseWindow(prop.Name, prop.Value));
                        foundAny = true;
                    }
                }

                if (!foundAny)
                    AddWarningOnce("session_stats_no_windows_detected");
            }
        }

        private void ParseCurrentStateIntoStructured(JsonElement root)
        {
            structuredState.TimeUtc = DateTime.UtcNow;
            structuredState.HasCurrentState = true;
            structuredState.LastCurrentStateUtc = DateTime.UtcNow;

            structuredState.Tunnel.CurrentState = FirstNonEmpty(
                ReadString(root, "state"),
                ReadString(root, "currentState"),
                structuredState.Tunnel.CurrentState);

            structuredState.Tunnel.CurrentMode = FirstNonEmpty(
                ReadString(root, "mode"),
                ReadString(root, "connectionMode"),
                structuredState.Tunnel.CurrentMode);

            bool? connected = ReadNullableBool(root, "connected");
            if (connected.HasValue)
                structuredState.Tunnel.Connected = connected.Value;
        }

        private void ParseConnectedServerIntoStructured(JsonElement root)
        {
            structuredState.TimeUtc = DateTime.UtcNow;
            structuredState.HasConnectedServer = true;
            structuredState.LastConnectedServerUtc = DateTime.UtcNow;

            structuredState.Tunnel.ConnectedServerName = FirstNonEmpty(
                ReadString(root, "name"),
                ReadString(root, "serverName"),
                ReadString(root, "displayName"),
                structuredState.Tunnel.ConnectedServerName);

            structuredState.Tunnel.ConnectedServerPublicIp = FirstNonEmpty(
                ReadString(root, "publicIp"),
                ReadString(root, "ip"),
                ReadString(root, "serverIp"),
                structuredState.Tunnel.ConnectedServerPublicIp);
        }

        private MetricsSnapshot BuildMetricsSnapshotFromStructured()
        {
            MetricsSnapshot snapshot = new MetricsSnapshot();
            snapshot.TimeUtc = DateTime.UtcNow;

            snapshot.TunnelConnected = structuredState.Tunnel.Connected;
            snapshot.ActiveConnectionCount = structuredState.Tunnel.ActiveConnectionCount;
            snapshot.BackupConnectionCount = structuredState.Tunnel.BackupConnectionCount;

            snapshot.AggregateLatencyMs = structuredState.Tunnel.LatencyMs;
            snapshot.AggregateJitterMs = structuredState.Tunnel.JitterMs;
            snapshot.AggregateRxBps = structuredState.Tunnel.RxBps;
            snapshot.AggregateTxBps = structuredState.Tunnel.TxBps;
            snapshot.AggregateTotalBps = structuredState.Tunnel.TotalBps;
            snapshot.AggregateCongested = structuredState.Tunnel.Congested;

            snapshot.ConnectedServerPublicIp = structuredState.Tunnel.ConnectedServerPublicIp;
            snapshot.ConnectedServerName = structuredState.Tunnel.ConnectedServerName;

            snapshot.TunnelCurrentState = structuredState.Tunnel.CurrentState;
            snapshot.TunnelCurrentMode = structuredState.Tunnel.CurrentMode;

            snapshot.HasConnectionStats = structuredState.HasConnectionStats;
            snapshot.HasNetworks = structuredState.HasNetworks;
            snapshot.HasSessionStats = structuredState.HasSessionStats;
            snapshot.HasCurrentState = structuredState.HasCurrentState;
            snapshot.HasConnectedServer = structuredState.HasConnectedServer;
            snapshot.StructuredCoreComplete = structuredState.IsCoreComplete;
            snapshot.ParseWarningCount = structuredState.ParseWarnings != null
                ? structuredState.ParseWarnings.Count
                : 0;

            SpeedifyLinkState firstReal = GetFirstUsableConnectedLink();
            SpeedifyLinkState secondReal = GetSecondUsableConnectedLink(firstReal);

            if (firstReal != null)
            {
                snapshot.PrimaryRemoteIp = firstReal.RemoteIp;
                snapshot.SpeedifyRemoteIp = string.IsNullOrWhiteSpace(firstReal.RemoteIp)
                    ? "speedify"
                    : firstReal.RemoteIp;

                snapshot.Adapter1Latency = firstReal.LatencyMs > 0 ? firstReal.LatencyMs : 0;
                snapshot.ReceiveBytesPerSecond = (long)firstReal.RxBps;
                snapshot.SendBytesPerSecond = (long)firstReal.TxBps;
                snapshot.ReceiveBytes = firstReal.ReceiveBytes;
                snapshot.SendBytes = firstReal.SendBytes;

                if (firstReal.Mos > 0)
                    snapshot.MosScore = firstReal.Mos;
            }
            else
            {
                snapshot.SpeedifyRemoteIp = "speedify";
            }

            if (secondReal != null)
                snapshot.Adapter2Latency = secondReal.LatencyMs > 0 ? secondReal.LatencyMs : 0;

            double latency = structuredState.Tunnel.LatencyMs > 0
                ? structuredState.Tunnel.LatencyMs
                : (firstReal != null && firstReal.LatencyMs > 0 ? firstReal.LatencyMs : 0);

            double jitter = structuredState.Tunnel.JitterMs > 0
                ? structuredState.Tunnel.JitterMs
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

            snapshot.Connected = structuredState.Tunnel.Connected;
            snapshot.Congested = structuredState.Tunnel.Congested;

            snapshot.LatencyMs = latency;
            snapshot.BaseLatencyMs = dynamicBase;
            snapshot.QueueDelayMs = Math.Max(0, snapshot.LatencyMs - snapshot.BaseLatencyMs);

            snapshot.IsCongested = structuredState.Tunnel.Congested;
            snapshot.IsDownloadCongested = structuredState.Tunnel.DownloadCongested;
            snapshot.IsUploadCongested = structuredState.Tunnel.UploadCongested;

            snapshot.Links.Clear();

            foreach (SpeedifyLinkState link in structuredState.Links)
            {
                if (link == null)
                    continue;

                LinkSnapshot projected = new LinkSnapshot();
                projected.Id = !string.IsNullOrWhiteSpace(link.Guid) ? link.Guid : link.Key;
                projected.Guid = link.Guid;
                projected.Key = link.Key;
                projected.Name = link.Name;
                projected.Isp = link.Isp;
                projected.Protocol = link.Protocol;

                projected.Connected = link.Connected;
                projected.IsProxy = link.IsProxy;
                projected.IsAggregate = link.IsAggregate;

                projected.Congested = link.Congested;
                projected.DownloadCongested = link.DownloadCongested;
                projected.UploadCongested = link.UploadCongested;

                projected.LatencyMs = link.LatencyMs;
                projected.JitterMs = link.JitterMs;
                projected.Mos = link.Mos;

                projected.RxBps = link.RxBps;
                projected.TxBps = link.TxBps;
                projected.TotalBps = link.TotalBps;

                projected.LocalIp = link.LocalIp;
                projected.PrivateIp = link.PrivateIp;
                projected.RemoteIp = link.RemoteIp;

                snapshot.Links.Add(projected);
            }

            snapshot.SessionWindows.Clear();

            foreach (SpeedifySessionWindowStats src in structuredState.SessionWindows)
            {
                if (src == null)
                    continue;

                snapshot.SessionWindows.Add(new SpeedifySessionWindowStats
                {
                    WindowKey = src.WindowKey,

                    BytesRecv = src.BytesRecv,
                    BytesSent = src.BytesSent,
                    EncryptedBytesRecv = src.EncryptedBytesRecv,
                    EncryptedBytesSent = src.EncryptedBytesSent,

                    MaxDownloadMbps = src.MaxDownloadMbps,
                    MaxUploadMbps = src.MaxUploadMbps,

                    NumFailovers = src.NumFailovers,
                    NumSessions = src.NumSessions,
                    RetransBytes = src.RetransBytes,
                    TotalConnectedMinutes = src.TotalConnectedMinutes,

                    DownBenefitNumConnections = src.DownBenefitNumConnections,
                    DownBenefitCombinedMbps = src.DownBenefitCombinedMbps,
                    DownBenefitFastestConnMbps = src.DownBenefitFastestConnMbps,

                    UpBenefitNumConnections = src.UpBenefitNumConnections,
                    UpBenefitCombinedMbps = src.UpBenefitCombinedMbps,
                    UpBenefitFastestConnMbps = src.UpBenefitFastestConnMbps,

                    TunBytesIn = src.TunBytesIn,
                    TunBytesOut = src.TunBytesOut,
                    TunDroppedIncoming = src.TunDroppedIncoming,
                    TunPacketWaits = src.TunPacketWaits,
                    TunReadQueue = src.TunReadQueue,

                    PacketHandlerBytesIn = src.PacketHandlerBytesIn,
                    PacketHandlerBytesOut = src.PacketHandlerBytesOut,
                    PacketHandlerDroppedOut = src.PacketHandlerDroppedOut,
                    PacketHandlerRetries = src.PacketHandlerRetries,

                    TotalFailoverSaves = src.TotalFailoverSaves,
                    TotalRedundantSaves = src.TotalRedundantSaves,
                    TotalSpeedSaves = src.TotalSpeedSaves,
                    TotalStreams = src.TotalStreams,
                    UniqueSaves = src.UniqueSaves
                });
            }

            return snapshot;
        }

        private SpeedifyLinkState GetFirstUsableConnectedLink()
        {
            foreach (SpeedifyLinkState link in structuredState.Links)
            {
                if (link == null)
                    continue;

                if (link.IsAggregate || link.IsProxy)
                    continue;

                if (link.Connected)
                    return link;
            }

            return null;
        }

        private SpeedifyLinkState GetSecondUsableConnectedLink(SpeedifyLinkState first)
        {
            bool seenFirst = false;

            foreach (SpeedifyLinkState link in structuredState.Links)
            {
                if (link == null)
                    continue;

                if (link.IsAggregate || link.IsProxy)
                    continue;

                if (!link.Connected)
                    continue;

                if (!seenFirst)
                {
                    if (ReferenceEquals(link, first))
                        seenFirst = true;

                    continue;
                }

                return link;
            }

            return null;
        }

        private SpeedifyLinkState GetOrCreateLink(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                key = "link-" + structuredState.Links.Count;

            foreach (SpeedifyLinkState existing in structuredState.Links)
            {
                if (string.Equals(existing.Key, key, StringComparison.OrdinalIgnoreCase))
                    return existing;
            }

            SpeedifyLinkState link = new SpeedifyLinkState();
            link.Key = key;
            structuredState.Links.Add(link);
            return link;
        }

        private SpeedifySessionWindowStats ParseWindow(string windowKey, JsonElement root)
        {
            SpeedifySessionWindowStats window = new SpeedifySessionWindowStats();
            window.WindowKey = windowKey;

            window.BytesRecv = ReadLongProperty(root, "bytesRecv");
            window.BytesSent = ReadLongProperty(root, "bytesSent");
            window.EncryptedBytesRecv = ReadLongProperty(root, "encryptedBytesRecv");
            window.EncryptedBytesSent = ReadLongProperty(root, "encryptedBytesSent");

            window.MaxDownloadMbps = ReadDoubleProperty(root, "maxDownloadMbps");
            window.MaxUploadMbps = ReadDoubleProperty(root, "maxUploadMbps");

            window.NumFailovers = ReadIntProperty(root, "numFailovers");
            window.NumSessions = ReadIntProperty(root, "numSessions");
            window.RetransBytes = ReadLongProperty(root, "retransBytes");
            window.TotalConnectedMinutes = ReadLongProperty(root, "totalConnectedMinutes");

            window.DownBenefitNumConnections = ReadNestedInt(root, "downBenefit", "numConnections");
            window.DownBenefitCombinedMbps = ReadNestedDouble(root, "downBenefit", "combinedMbps");
            window.DownBenefitFastestConnMbps = ReadNestedDouble(root, "downBenefit", "fastestConnMbps");

            window.UpBenefitNumConnections = ReadNestedInt(root, "upBenefit", "numConnections");
            window.UpBenefitCombinedMbps = ReadNestedDouble(root, "upBenefit", "combinedMbps");
            window.UpBenefitFastestConnMbps = ReadNestedDouble(root, "upBenefit", "fastestConnMbps");

            window.TunBytesIn = ReadNestedLong(root, "tun", "bytesIn");
            window.TunBytesOut = ReadNestedLong(root, "tun", "bytesOut");
            window.TunDroppedIncoming = ReadNestedInt(root, "tun", "droppedIncoming");
            window.TunPacketWaits = ReadNestedInt(root, "tun", "packetWaits");
            window.TunReadQueue = ReadNestedInt(root, "tun", "readQueue");

            window.PacketHandlerBytesIn = ReadNestedLong(root, "packetHandler", "bytesIn");
            window.PacketHandlerBytesOut = ReadNestedLong(root, "packetHandler", "bytesOut");
            window.PacketHandlerDroppedOut = ReadNestedInt(root, "packetHandler", "droppedOut");
            window.PacketHandlerRetries = ReadNestedInt(root, "packetHandler", "retries");

            window.TotalFailoverSaves = ReadNestedInt(root, "streaming", "totalFailoverSaves");
            window.TotalRedundantSaves = ReadNestedInt(root, "streaming", "totalRedundantSaves");
            window.TotalSpeedSaves = ReadNestedInt(root, "streaming", "totalSpeedSaves");
            window.TotalStreams = ReadNestedInt(root, "streaming", "totalStreams");
            window.UniqueSaves = ReadNestedInt(root, "streaming", "uniqueSaves");

            return window;
        }

        private void ValidateStructuredCompleteness()
        {
            foreach (SpeedifyLinkState link in structuredState.Links)
            {
                if (link.IsAggregate)
                    continue;

                bool hasIdentity = !string.IsNullOrWhiteSpace(link.Name) ||
                                   !string.IsNullOrWhiteSpace(link.Description) ||
                                   !string.IsNullOrWhiteSpace(link.Isp);

                bool hasLiveStats = link.LatencyMs > 0 ||
                                    link.JitterMs > 0 ||
                                    link.RxBps > 0 ||
                                    link.TxBps > 0 ||
                                    link.TotalBps > 0 ||
                                    link.Connected;

                if (hasLiveStats && !hasIdentity)
                    AddWarningOnce("link_live_stats_without_identity:" + link.Key);

                if (hasIdentity && !hasLiveStats)
                    AddWarningOnce("link_identity_without_live_stats:" + link.Key);
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

        private void AddWarningOnce(string warning)
        {
            foreach (string existing in structuredState.ParseWarnings)
            {
                if (string.Equals(existing, warning, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            structuredState.ParseWarnings.Add(warning);
            Logger.Log("speedify-parser", warning);
        }

        private static bool TryGetNetworksArray(JsonElement root, out JsonElement networks)
        {
            if (root.ValueKind == JsonValueKind.Array)
            {
                networks = root;
                return true;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("networks", out networks) && networks.ValueKind == JsonValueKind.Array)
                    return true;

                if (root.TryGetProperty("connections", out networks) && networks.ValueKind == JsonValueKind.Array)
                    return true;
            }

            networks = default(JsonElement);
            return false;
        }

        private static bool LooksLikeSessionWindowObject(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object)
                return false;

            return value.TryGetProperty("bytesRecv", out _) ||
                   value.TryGetProperty("numFailovers", out _) ||
                   value.TryGetProperty("totalConnectedMinutes", out _) ||
                   value.TryGetProperty("tun", out _) ||
                   value.TryGetProperty("packetHandler", out _) ||
                   value.TryGetProperty("streaming", out _);
        }

        private static string BuildLinkKey(JsonElement row)
        {
            string guid = ReadString(row, "guid");
            if (!string.IsNullOrWhiteSpace(guid))
                return guid;

            string key = ReadString(row, "key");
            if (!string.IsNullOrWhiteSpace(key))
                return key;

            string name = ReadString(row, "name");
            string localIp = ReadString(row, "localIp");
            string remoteIp = ReadString(row, "remoteIp");

            if (!string.IsNullOrWhiteSpace(name) &&
                (!string.IsNullOrWhiteSpace(localIp) || !string.IsNullOrWhiteSpace(remoteIp)))
            {
                return name + "|" + localIp + "|" + remoteIp;
            }

            if (!string.IsNullOrWhiteSpace(name))
                return name;

            return Guid.NewGuid().ToString("N");
        }

        private static string[] ReadStringArray(JsonElement parent, string name, string[] fallback)
        {
            JsonElement value;
            if (!parent.TryGetProperty(name, out value))
                return fallback;

            if (value.ValueKind != JsonValueKind.Array)
                return fallback;

            List<string> items = new List<string>();
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    items.Add(item.GetString());
            }

            if (items.Count == 0)
                return fallback;

            return items.ToArray();
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

        private static bool ReadBoolWithFallback(JsonElement parent, string name, bool fallback)
        {
            JsonElement value;
            if (!parent.TryGetProperty(name, out value))
                return fallback;

            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;

            return fallback;
        }

        private static bool ReadBoolWithEitherFallback(JsonElement parent, string primaryName, string secondaryName, bool fallback)
        {
            JsonElement value;
            if (parent.TryGetProperty(primaryName, out value))
            {
                if (value.ValueKind == JsonValueKind.True) return true;
                if (value.ValueKind == JsonValueKind.False) return false;
            }

            if (parent.TryGetProperty(secondaryName, out value))
            {
                if (value.ValueKind == JsonValueKind.True) return true;
                if (value.ValueKind == JsonValueKind.False) return false;
            }

            return fallback;
        }

        private static bool? ReadNullableBool(JsonElement parent, string name)
        {
            JsonElement value;
            if (!parent.TryGetProperty(name, out value))
                return null;

            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;

            return null;
        }

        private static double ReadDoubleProperty(JsonElement parent, string name)
        {
            JsonElement value;
            if (parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Number)
            {
                try { return value.GetDouble(); }
                catch { return 0; }
            }

            return 0;
        }

        private static double ReadDoubleOrDefault(JsonElement parent, string name, double fallback)
        {
            JsonElement value;
            if (parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Number)
            {
                try { return value.GetDouble(); }
                catch { return fallback; }
            }

            return fallback;
        }

        private static int ReadIntProperty(JsonElement parent, string name)
        {
            JsonElement value;
            if (!parent.TryGetProperty(name, out value))
                return 0;

            return ReadInt(value);
        }

        private static int ReadIntOrDefault(JsonElement parent, string name, int fallback)
        {
            JsonElement value;
            if (!parent.TryGetProperty(name, out value))
                return fallback;

            try
            {
                return value.GetInt32();
            }
            catch
            {
                try { return (int)value.GetDouble(); }
                catch { return fallback; }
            }
        }

        private static long ReadLongProperty(JsonElement parent, string name)
        {
            JsonElement value;
            if (!parent.TryGetProperty(name, out value))
                return 0;

            try
            {
                return value.GetInt64();
            }
            catch
            {
                try { return (long)value.GetDouble(); }
                catch { return 0; }
            }
        }

        private static long ReadLongOrDefault(JsonElement parent, string name, long fallback)
        {
            JsonElement value;
            if (!parent.TryGetProperty(name, out value))
                return fallback;

            try
            {
                return value.GetInt64();
            }
            catch
            {
                try { return (long)value.GetDouble(); }
                catch { return fallback; }
            }
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

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
                return null;

            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return null;
        }

        private static int ReadNestedInt(JsonElement parent, string objectName, string childName)
        {
            JsonElement obj;
            if (!parent.TryGetProperty(objectName, out obj))
                return 0;

            JsonElement child;
            if (!obj.TryGetProperty(childName, out child))
                return 0;

            try
            {
                return child.GetInt32();
            }
            catch
            {
                try { return (int)child.GetDouble(); }
                catch { return 0; }
            }
        }

        private static long ReadNestedLong(JsonElement parent, string objectName, string childName)
        {
            JsonElement obj;
            if (!parent.TryGetProperty(objectName, out obj))
                return 0;

            JsonElement child;
            if (!obj.TryGetProperty(childName, out child))
                return 0;

            try
            {
                return child.GetInt64();
            }
            catch
            {
                try { return (long)child.GetDouble(); }
                catch { return 0; }
            }
        }

        private static double ReadNestedDouble(JsonElement parent, string objectName, string childName)
        {
            JsonElement obj;
            if (!parent.TryGetProperty(objectName, out obj))
                return 0;

            JsonElement child;
            if (!obj.TryGetProperty(childName, out child))
                return 0;

            try
            {
                return child.GetDouble();
            }
            catch
            {
                return 0;
            }
        }
    }
}