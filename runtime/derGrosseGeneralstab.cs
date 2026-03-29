using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WTFnetApp
{
    public class DerGrosseGeneralstab
    {
        // =========================
        // CORE COMPONENTS
        // =========================

        private UIOverlay overlay;
        private NatKeepAlive nat;
        private readonly NetworkMetricsEngine networkMetricsEngine = new NetworkMetricsEngine();
        private SpeedifyTelemetryReader speedifyReader;

        private readonly WtfFastPing fastPing = new WtfFastPing();
        private readonly FastPingDeviationTracker fastPingDeviationTracker = new FastPingDeviationTracker();
        private readonly NetworkSignalResolver networkSignalResolver = new NetworkSignalResolver();

        // Optional / legacy-ish pieces kept around for now
        private MetricsSnapshot lastSnapshot;
        private UnifiedNetworkState lastUnifiedState;

        private readonly object publishSync = new object();

        // =========================
        // CONSTRUCTOR
        // =========================

        public DerGrosseGeneralstab()
        {
            Logger.StartSession();
            Logger.StartDeviationSession();

            Logger.Log("system", "application starting");
            Logger.Log("engine", "initializing core");

            InitializeOverlay();
            InitializeNat();
            InitializeFastPing();
            InitializeTelemetry();

            Logger.Log("engine", "network monitor started");
        }

        // =========================
        // INITIALIZATION
        // =========================

        private void InitializeOverlay()
        {
            overlay = new UIOverlay();
            overlay.Show();

            Logger.Log("tray", "tray initialized");
        }

        private void InitializeNat()
        {
            nat = new NatKeepAlive();
            _ = nat.StartNatKeepAlive();
        }

        private void InitializeFastPing()
        {
            fastPing.StateUpdated += OnFastPingStateUpdated;
            fastPing.Start();
        }

        private void InitializeTelemetry()
        {
            speedifyReader = new SpeedifyTelemetryReader(OnSpeedifySnapshot);
            speedifyReader.Start();
        }

        // =========================
        // INPUT PATHS
        // =========================

        private void OnFastPingStateUpdated(FastPingState fastPingState)
        {
            try
            {
                if (fastPingState == null)
                    return;

                fastPingDeviationTracker.Update(fastPingState);
                PublishNetworkState(lastSnapshot, fastPingState);
            }
            catch (Exception ex)
            {
                Logger.Log("engine", "fastping update error: " + ex.Message);
            }
        }

        private void OnSpeedifySnapshot(MetricsSnapshot snapshot)
        {
            try
            {
                if (snapshot == null)
                    return;

                lastSnapshot = snapshot;

                string primaryTarget = null;

                if (!string.IsNullOrWhiteSpace(snapshot.ConnectedServerPublicIp))
                    primaryTarget = snapshot.ConnectedServerPublicIp;
                else if (!string.IsNullOrWhiteSpace(snapshot.PrimaryRemoteIp))
                    primaryTarget = snapshot.PrimaryRemoteIp;
                else if (!string.IsNullOrWhiteSpace(snapshot.SpeedifyRemoteIp))
                    primaryTarget = snapshot.SpeedifyRemoteIp;

                if (!string.IsNullOrWhiteSpace(primaryTarget))
                    fastPing.UpdateTargets(primaryTarget, "1.1.1.1");

                if (snapshot.AggregateLatencyMs > 0)
                    fastPing.UpdateTunnelLatency(snapshot.AggregateLatencyMs);
                else if (snapshot.LatencyMs > 0)
                    fastPing.UpdateTunnelLatency(snapshot.LatencyMs);
                else
                    fastPing.UpdateTunnelLatency(-1);

                FastPingState fastPingState = fastPing.GetState();

                fastPingDeviationTracker.Update(fastPingState);
                PublishNetworkState(snapshot, fastPingState);
            }
            catch (Exception ex)
            {
                Logger.Log("engine", "snapshot handoff error: " + ex.Message);
            }
        }

        // =========================
        // SHARED PUBLISH PATH
        // =========================

        private void PublishNetworkState(MetricsSnapshot snapshot, FastPingState fastPingState)
        {
            if (fastPingState == null)
                return;

            lock (publishSync)
            {
                UnifiedNetworkState unifiedState =
                    networkSignalResolver.Resolve(snapshot, fastPingState);

                lastUnifiedState = unifiedState;

                MetricsSnapshot effectiveSnapshot = snapshot;

                if (effectiveSnapshot == null || !unifiedState.SpeedifyTelemetryFresh)
                    effectiveSnapshot = BuildFallbackSnapshot(unifiedState);

                networkMetricsEngine.Update(effectiveSnapshot, unifiedState, nat.CurrentNatState);
                overlay?.RenderOverlay(networkMetricsEngine.GetState().Overlay);
            }
        }

        private MetricsSnapshot BuildFallbackSnapshot(UnifiedNetworkState unified)
        {
            MetricsSnapshot snapshot = new MetricsSnapshot();

            snapshot.TimeUtc = DateTime.UtcNow;

            snapshot.TunnelConnected = unified.TunnelConnected;
            snapshot.ActiveConnectionCount = unified.ActiveConnectionCount;
            snapshot.BackupConnectionCount = unified.BackupConnectionCount;

            snapshot.AggregateLatencyMs = unified.AggregateLatencyMs > 0 ? unified.AggregateLatencyMs : unified.LatencyMs;
            snapshot.AggregateJitterMs = unified.AggregateJitterMs > 0 ? unified.AggregateJitterMs : unified.JitterMs;
            snapshot.AggregateRxBps = unified.AggregateRxBps;
            snapshot.AggregateTxBps = unified.AggregateTxBps;
            snapshot.AggregateTotalBps = unified.AggregateTotalBps;
            snapshot.AggregateCongested = unified.AggregateCongested;

            snapshot.PrimaryRemoteIp = unified.PrimaryRemoteIp;
            snapshot.SpeedifyRemoteIp = unified.SpeedifyRemoteIp;
            snapshot.ConnectedServerPublicIp = unified.ConnectedServerPublicIp;
            snapshot.ConnectedServerName = unified.ConnectedServerName;

            snapshot.Adapter1Latency = unified.Adapter1Latency > 0 ? unified.Adapter1Latency : unified.LatencyMs;
            snapshot.Adapter2Latency = unified.Adapter2Latency;

            snapshot.Connected = unified.TunnelConnected;
            snapshot.Congested = unified.AggregateCongested;

            snapshot.LatencyMs = unified.LatencyMs;
            snapshot.BaseLatencyMs = unified.BaselineLatencyMs;
            snapshot.QueueDelayMs = unified.LatencyMs > 0 && unified.BaselineLatencyMs > 0
                ? Math.Max(0, unified.LatencyMs - unified.BaselineLatencyMs)
                : 0;

            snapshot.Recent = unified.LatencyMs;
            snapshot.Average = unified.LatencyMs;
            snapshot.Max = unified.LatencyMs;
            snapshot.Jitter = unified.JitterMs;
            snapshot.MicroJitter = 0;
            snapshot.DynamicBase = unified.BaselineLatencyMs;

            snapshot.PacketLoss = unified.LossRate;

            return snapshot;
        }

        // =========================
        // STARTUP
        // =========================

        public void Startup()
        {
            Logger.Log("startup", "detecting best server");

            _ = Task.Run(async () =>
            {
                string bestServer = await DetectBestServerFast();
                Logger.Log("startup", "Using server region: " + bestServer);
            });
        }

        // =========================
        // SERVER DETECTION
        // =========================

        private async Task<string> DetectBestServerFast()
        {
            var servers = new Dictionary<string, string>
            {
                { "Vienna", "1.1.1.1" },
                { "Ljubljana", "8.8.8.8" },
                { "Milan", "9.9.9.9" }
            };

            var tasks = new List<Task<Tuple<string, int>>>();

            foreach (var s in servers)
                tasks.Add(CheckServer(s.Key, s.Value));

            Tuple<string, int>[] results = await Task.WhenAll(tasks);

            string best = "Ljubljana";
            int bestPing = int.MaxValue;

            foreach (Tuple<string, int> r in results)
            {
                if (r.Item2 > 0 && r.Item2 < bestPing)
                {
                    bestPing = r.Item2;
                    best = r.Item1;
                }
            }

            Logger.Log("startup", "Best server: " + best + " (" + bestPing + "ms)");
            return best;
        }

        private async Task<Tuple<string, int>> CheckServer(string name, string host)
        {
            int ping = await WtfFastPing.Ping(host);

            Logger.Log("startup", "server check " + name + " = " + ping + "ms");

            return Tuple.Create(name, ping);
        }

        // =========================
        // ACCESSORS
        // =========================

        public DataSet GetDataSet()
        {
            return networkMetricsEngine.GetDataSet();
        }

        public NetworkEngineState GetNetworkState()
        {
            return networkMetricsEngine.GetState();
        }

        public FastPingState GetFastPingState()
        {
            return fastPing.GetState();
        }

        public UnifiedNetworkState GetUnifiedNetworkState()
        {
            return lastUnifiedState != null ? lastUnifiedState.Clone() : null;
        }

        // =========================
        // TRAY CONTROL
        // =========================

        public void ToggleOverlay()
        {
            if (overlay == null || overlay.IsDisposed)
                overlay = new UIOverlay();

            if (overlay.Visible)
                overlay.Hide();
            else
                overlay.Show();
        }

        public Task RunManualServerPingScan()
        {
            Logger.Log("server", "manual server ping scan disabled (StartupServerTest removed)");
            return Task.CompletedTask;
        }

        // =========================
        // SHUTDOWN
        // =========================

        public async Task StopAsync()
        {
            try
            {
                fastPing.StateUpdated -= OnFastPingStateUpdated;
                await fastPing.StopAsync();
            }
            catch (Exception ex)
            {
                Logger.Log("engine", "fastping stop error: " + ex.Message);
            }

            try
            {
                if (speedifyReader != null)
                    await speedifyReader.StopAsync();
            }
            catch (Exception ex)
            {
                Logger.Log("engine", "speedify stop error: " + ex.Message);
            }

            try
            {
                await networkMetricsEngine.StopAsync();
            }
            catch (Exception ex)
            {
                Logger.Log("engine", "metrics engine stop error: " + ex.Message);
            }
        }
    }
}