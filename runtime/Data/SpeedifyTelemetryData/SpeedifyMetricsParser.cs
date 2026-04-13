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
        private int linkRefreshGeneration = 0;

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

                            Logger.Log("speedify-parsed",
                                "event=report_connection_stats" +
                                " | tunnelConnected=" + structuredState.Tunnel.Connected +
                                " | aggLatency=" + structuredState.Tunnel.LatencyMs +
                                " | aggJitter=" + structuredState.Tunnel.JitterMs +
                                " | active=" + structuredState.Tunnel.ActiveConnectionCount +
                                " | backup=" + structuredState.Tunnel.BackupConnectionCount +
                                " | links=" + structuredState.Links.Count +
                                " | warnings=" + structuredState.ParseWarnings.Count +
                                " | warnKeys=" + string.Join(",", structuredState.ParseWarnings));

                            return BuildMetricsSnapshotFromStructured();

                        case "report_networks":
                            ParseNetworksIntoStructured(root);

                            Logger.Log("speedify-parsed",
                                "event=report_networks" +
                                " | links=" + structuredState.Links.Count);

                            return null;

                        case "report_session_stats":
                            ParseSessionStatsIntoStructured(root);

                            Logger.Log("speedify-parsed",
                                "event=report_session_stats" +
                                " | windows=" + structuredState.SessionWindows.Count);

                            return null;

                        case "report_current_state":
                            ParseCurrentStateIntoStructured(root);

                            Logger.Log("speedify-parsed",
                                "event=report_current_state" +
                                " | connected=" + structuredState.Tunnel.Connected +
                                " | state=" + structuredState.Tunnel.CurrentState +
                                " | mode=" + structuredState.Tunnel.CurrentMode);

                            return null;

                        case "report_connected_server":
                            ParseConnectedServerIntoStructured(root);

                            Logger.Log("speedify-parsed",
                                "event=report_connected_server" +
                                " | server=" + SafeText(structuredState.Tunnel.ConnectedServerName) +
                                " | publicIp=" + SafeText(structuredState.Tunnel.ConnectedServerPublicIp));

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

        private void ParseConnectionStatsIntoStructured(JsonElement root)
        {
            DateTime nowUtc = DateTime.UtcNow;
            structuredState.TimeUtc = nowUtc;
            structuredState.HasConnectionStats = true;
            structuredState.LastConnectionStatsUtc = nowUtc;

            structuredState.ParseWarnings.Clear();

            int generation = BeginLinkRefresh();

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

                    Logger.Log("speedify-conn-row",
                        "guid=" + guid +
                        " | protocol=" + protocol +
                        " | builtKey=" + key +
                        " | name=" + ReadString(row, "name") +
                        " | isp=" + ReadString(row, "isp") +
                        " | ltMs=" + ReadDoubleOrDefault(row, "ltMs", -1) +
                        " | jtMs=" + ReadDoubleOrDefault(row, "jtMs", -1));

                    bool isAggregate = string.Equals(guid, "speedify", StringComparison.OrdinalIgnoreCase);
                    bool isProxy = string.Equals(protocol, "proxy", StringComparison.OrdinalIgnoreCase);

                    if (isAggregate)
                    {
                        ParseAggregateTunnelRow(row);
                        hasAggregate = true;
                        continue;
                    }

                    if (isProxy)
                    {
                        SpeedifyLinkState canonicalLink = FindCanonicalLinkByGuid(guid);
                        if (canonicalLink != null)
                            MarkLinkSeen(canonicalLink, generation);

                        continue;
                    }

                    SpeedifyLinkState link = GetOrCreateLink(key);
                    MarkLinkSeen(link, generation);

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
                    link.IsProxy = false;
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

                    link.RcvEstMbps = ReadDoubleOrDefaultEither(row, "rcvEstMbps", "rcvEst", link.RcvEstMbps);
                    link.SndEstMbps = ReadDoubleOrDefaultEither(row, "sndEstMbps", "sndEst", link.SndEstMbps);

                    string ip = ReadString(row, "ip");
                    string localIp = ReadString(row, "localIp");
                    string privateIp = ReadString(row, "privateIp");
                    string remoteIp = ReadString(row, "remoteIp");

                    link.Ip = !string.IsNullOrWhiteSpace(ip) ? ip : null;
                    link.LocalIp = !string.IsNullOrWhiteSpace(localIp) ? localIp : null;
                    link.PrivateIp = !string.IsNullOrWhiteSpace(privateIp) ? privateIp : null;
                    link.RemoteIp = !string.IsNullOrWhiteSpace(remoteIp) ? remoteIp : null;

                    link.IpList = ReadStringArray(row, "ipList", link.IpList);
                }
            }

            RemoveLinksNotSeenInGeneration(generation, keepAggregateRows: false);

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

            structuredState.Tunnel.LatencyMs = ReadDoubleOrDefault(row, "ltMs", 0);
            structuredState.Tunnel.JitterMs = ReadDoubleOrDefault(row, "jtMs", 0);
            structuredState.Tunnel.RxBps = ReadDoubleOrDefault(row, "rcvBps", 0);
            structuredState.Tunnel.TxBps = ReadDoubleOrDefault(row, "sndBps", 0);
            structuredState.Tunnel.TotalBps = ReadDoubleOrDefault(row, "totBps", 0);
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

                Logger.Log("speedify-net-row",
                    "guid=" + guid +
                    " | builtKey=" + key +
                    " | name=" + ReadString(row, "name") +
                    " | isp=" + ReadString(row, "isp") +
                    " | ip=" + ReadString(row, "ip") +
                    " | type=" + ReadString(row, "type"));

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
                ReadAnyAsString(root, "state"),
                ReadAnyAsString(root, "currentState"),
                structuredState.Tunnel.CurrentState);

            structuredState.Tunnel.CurrentMode = FirstNonEmpty(
                ReadAnyAsString(root, "mode"),
                ReadAnyAsString(root, "connectionMode"),
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
                ReadString(root, "longName"),
                ReadString(root, "name"),
                ReadString(root, "serverName"),
                ReadString(root, "displayName"),
                structuredState.Tunnel.ConnectedServerName);

            structuredState.Tunnel.ConnectedServerPublicIp = FirstNonEmpty(
                ReadFirstStringFromArray(root, "publicIP"),
                ReadFirstStringFromArray(root, "publicIp"),
                ReadString(root, "publicIp"),
                ReadString(root, "ip"),
                ReadString(root, "serverIp"),
                structuredState.Tunnel.ConnectedServerPublicIp);
        }

        private MetricsSnapshot BuildMetricsSnapshotFromStructured()
        {
            MetricsSnapshot snapshot = new MetricsSnapshot();
            snapshot.TimeUtc = structuredState.TimeUtc != DateTime.MinValue
                ? structuredState.TimeUtc
                : DateTime.UtcNow;

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

            SpeedifyLinkState firstReal = GetBestLinkByJitterStability();
            SpeedifyLinkState secondReal = GetSecondBestLinkByJitterStability(firstReal);

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

            Logger.Log("speedify-snapshot",
                "connected=" + snapshot.TunnelConnected +
                " | aggLat=" + snapshot.AggregateLatencyMs +
                " | aggJit=" + snapshot.AggregateJitterMs +
                " | lat=" + snapshot.LatencyMs +
                " | base=" + snapshot.BaseLatencyMs +
                " | dynBase=" + snapshot.DynamicBase +
                " | server=" + SafeText(snapshot.ConnectedServerName) +
                " | pubIp=" + SafeText(snapshot.ConnectedServerPublicIp) +
                " | links=" + snapshot.Links.Count +
                " | warnings=" + snapshot.ParseWarningCount);

            return snapshot;
        }

        private SpeedifyLinkState GetBestLinkByJitterStability()
        {
            SpeedifyLinkState best = null;
            double bestScore = double.MaxValue;

            foreach (SpeedifyLinkState link in structuredState.Links)
            {
                if (link == null)
                    continue;

                if (link.IsAggregate || link.IsProxy)
                    continue;

                if (!link.Connected)
                    continue;

                double score = ComputeLinkJitterStabilityScore(link);

                if (score < bestScore)
                {
                    bestScore = score;
                    best = link;
                }
            }

            return best;
        }

        private SpeedifyLinkState GetSecondBestLinkByJitterStability(SpeedifyLinkState first)
        {
            SpeedifyLinkState best = null;
            double bestScore = double.MaxValue;

            foreach (SpeedifyLinkState link in structuredState.Links)
            {
                if (link == null)
                    continue;

                if (ReferenceEquals(link, first))
                    continue;

                if (link.IsAggregate || link.IsProxy)
                    continue;

                if (!link.Connected)
                    continue;

                double score = ComputeLinkJitterStabilityScore(link);

                if (score < bestScore)
                {
                    bestScore = score;
                    best = link;
                }
            }

            return best;
        }

        private double ComputeLinkJitterStabilityScore(SpeedifyLinkState link)
        {
            if (link == null)
                return double.MaxValue;

            double latency = link.LatencyMs > 0 ? link.LatencyMs : 999;
            double jitter = link.JitterMs > 0 ? link.JitterMs : 999;
            double mosPenalty = link.Mos > 0 ? (5.0 - link.Mos) * 10.0 : 10.0;

            double congestionPenalty = 0.0;
            if (link.Congested) congestionPenalty += 25.0;
            if (link.DownloadCongested) congestionPenalty += 12.0;
            if (link.UploadCongested) congestionPenalty += 12.0;

            return
                (jitter * 2.8) +
                (latency * 0.30) +
                mosPenalty +
                congestionPenalty;
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

        private SpeedifyLinkState FindCanonicalLinkByGuid(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
                return null;

            SpeedifyLinkState proxyMatch = null;

            foreach (SpeedifyLinkState existing in structuredState.Links)
            {
                if (existing == null)
                    continue;

                if (!string.Equals(existing.Guid, guid, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!existing.IsProxy && !existing.IsAggregate)
                    return existing;

                if (proxyMatch == null)
                    proxyMatch = existing;
            }

            return proxyMatch;
        }

        private SpeedifySessionWindowStats ParseWindow(string windowKey, JsonElement root)
        {
            SpeedifySessionWindowStats window = new SpeedifySessionWindowStats();
            window.WindowKey = windowKey;

            window.BytesRecv = ReadLongPropertyEither(root, "bytes_recv", "bytesRecv");
            window.BytesSent = ReadLongPropertyEither(root, "bytes_sent", "bytesSent");
            window.EncryptedBytesRecv = ReadLongPropertyEither(root, "encrypted_bytes_recv", "encryptedBytesRecv");
            window.EncryptedBytesSent = ReadLongPropertyEither(root, "encrypted_bytes_sent", "encryptedBytesSent");

            window.MaxDownloadMbps = ReadDoublePropertyEither(root, "max_download_mbps", "maxDownloadMbps");
            window.MaxUploadMbps = ReadDoublePropertyEither(root, "max_upload_mbps", "maxUploadMbps");

            window.NumFailovers = ReadIntPropertyEither(root, "num_failovers", "numFailovers");
            window.NumSessions = ReadIntPropertyEither(root, "num_sessions", "numSessions");
            window.RetransBytes = ReadLongPropertyEither(root, "retrans_bytes", "retransBytes");
            window.TotalConnectedMinutes = ReadLongPropertyEither(root, "total_connected_minutes", "totalConnectedMinutes");

            window.DownBenefitNumConnections = ReadNestedIntEither(root, "down_benefit", "downBenefit", "num_connections", "numConnections");
            window.DownBenefitCombinedMbps = ReadNestedDoubleEither(root, "down_benefit", "downBenefit", "combined_mbps", "combinedMbps");
            window.DownBenefitFastestConnMbps = ReadNestedDoubleEither(root, "down_benefit", "downBenefit", "fastest_conn_mbps", "fastestConnMbps");

            window.UpBenefitNumConnections = ReadNestedIntEither(root, "up_benefit", "upBenefit", "num_connections", "numConnections");
            window.UpBenefitCombinedMbps = ReadNestedDoubleEither(root, "up_benefit", "upBenefit", "combined_mbps", "combinedMbps");
            window.UpBenefitFastestConnMbps = ReadNestedDoubleEither(root, "up_benefit", "upBenefit", "fastest_conn_mbps", "fastestConnMbps");

            window.TunBytesIn = ReadNestedLongEither(root, "tun", "tun", "bytes_in", "bytesIn");
            window.TunBytesOut = ReadNestedLongEither(root, "tun", "tun", "bytes_out", "bytesOut");
            window.TunDroppedIncoming = ReadNestedIntEither(root, "tun", "tun", "dropped_incoming", "droppedIncoming");
            window.TunPacketWaits = ReadNestedIntEither(root, "tun", "tun", "packet_waits", "packetWaits");
            window.TunReadQueue = ReadNestedIntEither(root, "tun", "tun", "read_queue", "readQueue");

            window.PacketHandlerBytesIn = ReadNestedLongEither(root, "packet_handler", "packetHandler", "bytes_in", "bytesIn");
            window.PacketHandlerBytesOut = ReadNestedLongEither(root, "packet_handler", "packetHandler", "bytes_out", "bytesOut");
            window.PacketHandlerDroppedOut = ReadNestedIntEither(root, "packet_handler", "packetHandler", "dropped_out", "droppedOut");
            window.PacketHandlerRetries = ReadNestedIntEither(root, "packet_handler", "packetHandler", "retries", "retries");

            window.TotalFailoverSaves = ReadNestedIntEither(root, "streaming", "streaming", "total_failover_saves", "totalFailoverSaves");
            window.TotalRedundantSaves = ReadNestedIntEither(root, "streaming", "streaming", "total_redundant_saves", "totalRedundantSaves");
            window.TotalSpeedSaves = ReadNestedIntEither(root, "streaming", "streaming", "total_speed_saves", "totalSpeedSaves");
            window.TotalStreams = ReadNestedIntEither(root, "streaming", "streaming", "total_streams", "totalStreams");
            window.UniqueSaves = ReadNestedIntEither(root, "streaming", "streaming", "unique_saves", "uniqueSaves");

            return window;
        }

        private void ValidateStructuredCompleteness()
        {
            foreach (SpeedifyLinkState link in structuredState.Links)
            {
                if (link == null)
                    continue;

                if (link.IsAggregate)
                    continue;

                bool staleForConnectionStats =
                    structuredState.HasConnectionStats &&
                    (link.LastSeenGeneration <= 0 || link.LastSeenGeneration < linkRefreshGeneration - 1);
                if (staleForConnectionStats)
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

        private int BeginLinkRefresh()
        {
            linkRefreshGeneration++;

            if (linkRefreshGeneration == int.MaxValue)
                linkRefreshGeneration = 1;

            foreach (SpeedifyLinkState link in structuredState.Links)
            {
                if (link == null)
                    continue;

                link.SeenInCurrentRefresh = false;
            }

            return linkRefreshGeneration;
        }

        private void MarkLinkSeen(SpeedifyLinkState link, int generation)
        {
            if (link == null)
                return;

            link.LastSeenGeneration = generation;
            link.SeenInCurrentRefresh = true;
        }

        private void RemoveLinksNotSeenInGeneration(int generation, bool keepAggregateRows)
        {
            for (int i = structuredState.Links.Count - 1; i >= 0; i--)
            {
                SpeedifyLinkState link = structuredState.Links[i];

                if (link == null)
                {
                    structuredState.Links.RemoveAt(i);
                    continue;
                }

                if (keepAggregateRows && link.IsAggregate)
                    continue;

                if (link.LastSeenGeneration < generation - 1)
                    structuredState.Links.RemoveAt(i);
            }
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
                   value.TryGetProperty("bytes_recv", out _) ||
                   value.TryGetProperty("numFailovers", out _) ||
                   value.TryGetProperty("num_failovers", out _) ||
                   value.TryGetProperty("totalConnectedMinutes", out _) ||
                   value.TryGetProperty("total_connected_minutes", out _) ||
                   value.TryGetProperty("tun", out _) ||
                   value.TryGetProperty("packetHandler", out _) ||
                   value.TryGetProperty("packet_handler", out _) ||
                   value.TryGetProperty("streaming", out _);
        }

        private static string BuildLinkKey(JsonElement row)
        {
            string key = ReadString(row, "key");
            if (!string.IsNullOrWhiteSpace(key))
                return key;

            string guid = ReadString(row, "guid");
            string protocol = ReadString(row, "protocol");

            if (!string.IsNullOrWhiteSpace(guid) && !string.IsNullOrWhiteSpace(protocol))
                return guid + "|" + protocol;

            if (!string.IsNullOrWhiteSpace(guid))
                return guid;

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

        private static string ReadAnyAsString(JsonElement parent, string name)
        {
            JsonElement value;
            if (!parent.TryGetProperty(name, out value))
                return null;

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();

                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return value.ToString();

                default:
                    return null;
            }
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

        private static string SafeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
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

        private static string ReadFirstStringFromArray(JsonElement parent, string name)
        {
            JsonElement value;
            if (!parent.TryGetProperty(name, out value))
                return null;

            if (value.ValueKind != JsonValueKind.Array)
                return null;

            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    return item.GetString();
            }

            return null;
        }

        private static int ReadIntPropertyEither(JsonElement parent, string firstName, string secondName)
        {
            JsonElement value;
            if (parent.TryGetProperty(firstName, out value))
                return ReadInt(value);

            if (parent.TryGetProperty(secondName, out value))
                return ReadInt(value);

            return 0;
        }

        private static long ReadLongPropertyEither(JsonElement parent, string firstName, string secondName)
        {
            JsonElement value;
            if (parent.TryGetProperty(firstName, out value))
            {
                try { return value.GetInt64(); }
                catch
                {
                    try { return (long)value.GetDouble(); }
                    catch { return 0; }
                }
            }

            if (parent.TryGetProperty(secondName, out value))
            {
                try { return value.GetInt64(); }
                catch
                {
                    try { return (long)value.GetDouble(); }
                    catch { return 0; }
                }
            }

            return 0;
        }

        private static double ReadDoublePropertyEither(JsonElement parent, string firstName, string secondName)
        {
            JsonElement value;
            if (parent.TryGetProperty(firstName, out value) && value.ValueKind == JsonValueKind.Number)
            {
                try { return value.GetDouble(); }
                catch { }
            }

            if (parent.TryGetProperty(secondName, out value) && value.ValueKind == JsonValueKind.Number)
            {
                try { return value.GetDouble(); }
                catch { }
            }

            return 0;
        }

        private static double ReadDoubleOrDefaultEither(JsonElement parent, string firstName, string secondName, double fallback)
        {
            JsonElement value;
            if (parent.TryGetProperty(firstName, out value) && value.ValueKind == JsonValueKind.Number)
            {
                try { return value.GetDouble(); }
                catch { return fallback; }
            }

            if (parent.TryGetProperty(secondName, out value) && value.ValueKind == JsonValueKind.Number)
            {
                try { return value.GetDouble(); }
                catch { return fallback; }
            }

            return fallback;
        }

        private static int ReadNestedIntEither(JsonElement parent, string firstObjectName, string secondObjectName, string firstChildName, string secondChildName)
        {
            JsonElement obj;
            JsonElement child;

            if (parent.TryGetProperty(firstObjectName, out obj))
            {
                if (obj.TryGetProperty(firstChildName, out child))
                    return ReadInt(child);

                if (obj.TryGetProperty(secondChildName, out child))
                    return ReadInt(child);
            }

            if (parent.TryGetProperty(secondObjectName, out obj))
            {
                if (obj.TryGetProperty(firstChildName, out child))
                    return ReadInt(child);

                if (obj.TryGetProperty(secondChildName, out child))
                    return ReadInt(child);
            }

            return 0;
        }

        private static long ReadNestedLongEither(JsonElement parent, string firstObjectName, string secondObjectName, string firstChildName, string secondChildName)
        {
            JsonElement obj;
            JsonElement child;

            if (parent.TryGetProperty(firstObjectName, out obj))
            {
                if (obj.TryGetProperty(firstChildName, out child))
                {
                    try { return child.GetInt64(); }
                    catch
                    {
                        try { return (long)child.GetDouble(); }
                        catch { return 0; }
                    }
                }

                if (obj.TryGetProperty(secondChildName, out child))
                {
                    try { return child.GetInt64(); }
                    catch
                    {
                        try { return (long)child.GetDouble(); }
                        catch { return 0; }
                    }
                }
            }

            if (parent.TryGetProperty(secondObjectName, out obj))
            {
                if (obj.TryGetProperty(firstChildName, out child))
                {
                    try { return child.GetInt64(); }
                    catch
                    {
                        try { return (long)child.GetDouble(); }
                        catch { return 0; }
                    }
                }

                if (obj.TryGetProperty(secondChildName, out child))
                {
                    try { return child.GetInt64(); }
                    catch
                    {
                        try { return (long)child.GetDouble(); }
                        catch { return 0; }
                    }
                }
            }

            return 0;
        }

        private static double ReadNestedDoubleEither(JsonElement parent, string firstObjectName, string secondObjectName, string firstChildName, string secondChildName)
        {
            JsonElement obj;
            JsonElement child;

            if (parent.TryGetProperty(firstObjectName, out obj))
            {
                if (obj.TryGetProperty(firstChildName, out child))
                {
                    try { return child.GetDouble(); }
                    catch { }
                }

                if (obj.TryGetProperty(secondChildName, out child))
                {
                    try { return child.GetDouble(); }
                    catch { }
                }
            }

            if (parent.TryGetProperty(secondObjectName, out obj))
            {
                if (obj.TryGetProperty(firstChildName, out child))
                {
                    try { return child.GetDouble(); }
                    catch { }
                }

                if (obj.TryGetProperty(secondChildName, out child))
                {
                    try { return child.GetDouble(); }
                    catch { }
                }
            }

            return 0;
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
