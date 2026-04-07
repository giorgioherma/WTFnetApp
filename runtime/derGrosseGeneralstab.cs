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
        private SpeedifyTelemetryReader speedifyReader;

        private readonly WtfFastPing fastPing = new WtfFastPing();
        private readonly FastPingDeviationTracker fastPingDeviationTracker = new FastPingDeviationTracker();
        private readonly NetworkSignalResolver networkSignalResolver = new NetworkSignalResolver();

        // =========================
        // CURRENT STATE CACHE
        // =========================

        private MetricsSnapshot lastSnapshot;
        private UnifiedNetworkState lastUnifiedState;

        private readonly object publishSync = new object();

        // =========================
        // FASTPING TARGET ORCHESTRATION
        // =========================

        private string lastFastPingPrimaryTarget;
        private string lastFastPingSecondaryTarget;

        private string lastValidTunnelProbeTarget;
        private DateTime lastValidTunnelProbeTargetUtc = DateTime.MinValue;

        private const int TunnelProbeTargetGraceSeconds = 8;

        private ProbeTargetMode currentProbeTargetMode = ProbeTargetMode.Unknown;

        private enum ProbeTargetMode
        {
            Unknown = 0,
            ExternalOnly = 1,
            TunnelAware = 2
        }

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

            // Safe startup defaults even before Speedify gives anything useful.
            UpdateFastPingTargetsIfChanged("1.1.1.1", "8.8.8.8");
            LogProbeModeIfChanged(ProbeTargetMode.ExternalOnly, "startup default");

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

                UpdateFastPingTargetsFromSnapshot(snapshot);
                UpdateFastPingTunnelLatency(snapshot);

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
                NatState natState = nat != null ? nat.CurrentNatState : NatState.Unknown;

                UnifiedNetworkState unifiedState =
                    networkSignalResolver.Resolve(snapshot, fastPingState, natState);

                // Store immutable snapshot
                lastUnifiedState = unifiedState?.Clone();

                // Use SAME immutable version everywhere
                var safeState = lastUnifiedState;

                overlay?.RenderUnified(safeState);

                // Optional logging
                Logger.LogAnalysis(safeState);
            }
        }

        // =========================
        // FASTPING TARGET MANAGEMENT
        // =========================

        private void UpdateFastPingTargetsFromSnapshot(MetricsSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            string freshTunnelTarget = ChooseBestTunnelProbeTarget(snapshot);

            bool tunnelConnected = snapshot.TunnelConnected;
            bool hasFreshTunnelTarget = IsUsableProbeTarget(freshTunnelTarget);

            if (hasFreshTunnelTarget)
            {
                lastValidTunnelProbeTarget = freshTunnelTarget;
                lastValidTunnelProbeTargetUtc = DateTime.UtcNow;
            }

            string primaryTarget;
            string secondaryTarget;
            ProbeTargetMode targetMode;
            string reason;

            if (tunnelConnected)
            {
                if (hasFreshTunnelTarget)
                {
                    primaryTarget = freshTunnelTarget;
                    secondaryTarget = "1.1.1.1";
                    targetMode = ProbeTargetMode.TunnelAware;
                    reason = "fresh tunnel target available";
                }
                else if (IsRecentTunnelProbeTargetAvailable())
                {
                    primaryTarget = lastValidTunnelProbeTarget;
                    secondaryTarget = "1.1.1.1";
                    targetMode = ProbeTargetMode.TunnelAware;
                    reason = "reusing recent tunnel target during grace";
                }
                else
                {
                    primaryTarget = "1.1.1.1";
                    secondaryTarget = "8.8.8.8";
                    targetMode = ProbeTargetMode.ExternalOnly;
                    reason = "tunnel connected but no usable tunnel target";
                }
            }
            else
            {
                primaryTarget = "1.1.1.1";
                secondaryTarget = "8.8.8.8";
                targetMode = ProbeTargetMode.ExternalOnly;
                reason = "tunnel not connected";
            }

            LogProbeModeIfChanged(targetMode, reason);
            UpdateFastPingTargetsIfChanged(primaryTarget, secondaryTarget);
        }

        private void UpdateFastPingTunnelLatency(MetricsSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            if (snapshot.TunnelConnected)
            {
                if (snapshot.AggregateLatencyMs > 0)
                    fastPing.UpdateTunnelLatency(snapshot.AggregateLatencyMs);
                else if (snapshot.LatencyMs > 0)
                    fastPing.UpdateTunnelLatency(snapshot.LatencyMs);
                else
                    fastPing.UpdateTunnelLatency(-1);
            }
            else
            {
                fastPing.UpdateTunnelLatency(-1);
            }
        }

        private string ChooseBestTunnelProbeTarget(MetricsSnapshot snapshot)
        {
            if (snapshot == null)
                return null;

            if (IsUsableProbeTarget(snapshot.ConnectedServerPublicIp))
                return NormalizeProbeTarget(snapshot.ConnectedServerPublicIp);

            if (IsUsableProbeTarget(snapshot.PrimaryRemoteIp))
                return NormalizeProbeTarget(snapshot.PrimaryRemoteIp);

            if (IsUsableProbeTarget(snapshot.SpeedifyRemoteIp))
                return NormalizeProbeTarget(snapshot.SpeedifyRemoteIp);

            return null;
        }

        private bool IsRecentTunnelProbeTargetAvailable()
        {
            if (!IsUsableProbeTarget(lastValidTunnelProbeTarget))
                return false;

            if (lastValidTunnelProbeTargetUtc == DateTime.MinValue)
                return false;

            return (DateTime.UtcNow - lastValidTunnelProbeTargetUtc).TotalSeconds <= TunnelProbeTargetGraceSeconds;
        }

        private void UpdateFastPingTargetsIfChanged(string primaryTarget, string secondaryTarget)
        {
            primaryTarget = NormalizeProbeTarget(primaryTarget);
            secondaryTarget = NormalizeProbeTarget(secondaryTarget);

            if (!IsUsableProbeTarget(primaryTarget))
            {
                Logger.Log("fastping", "ignored invalid primary target: " + (primaryTarget ?? "<null>"));
                return;
            }

            if (!IsUsableProbeTarget(secondaryTarget))
            {
                Logger.Log("fastping", "ignored invalid secondary target: " + (secondaryTarget ?? "<null>"));
                return;
            }

            bool changed =
                !string.Equals(lastFastPingPrimaryTarget, primaryTarget, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(lastFastPingSecondaryTarget, secondaryTarget, StringComparison.OrdinalIgnoreCase);

            if (!changed)
                return;

            Logger.Log("fastping", "target handoff | primary=" + primaryTarget + " | secondary=" + secondaryTarget);

            fastPing.UpdateTargets(primaryTarget, secondaryTarget);

            lastFastPingPrimaryTarget = primaryTarget;
            lastFastPingSecondaryTarget = secondaryTarget;
        }

        private void LogProbeModeIfChanged(ProbeTargetMode newMode, string reason)
        {
            if (currentProbeTargetMode == newMode)
                return;

            currentProbeTargetMode = newMode;
            Logger.Log("fastping", "probe mode -> " + newMode.ToString().ToLowerInvariant() + " | " + reason);
        }

        private static string NormalizeProbeTarget(string host)
        {
            return string.IsNullOrWhiteSpace(host) ? null : host.Trim();
        }

        private static bool IsUsableProbeTarget(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return false;

            host = host.Trim();

            if (string.Equals(host, "speedify", StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(host, "unknown", StringComparison.OrdinalIgnoreCase))
                return false;

            if (string.Equals(host, "none", StringComparison.OrdinalIgnoreCase))
                return false;

            if (host.IndexOf(' ') >= 0)
                return false;

            return true;
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

        public FastPingState GetFastPingState()
        {
            return fastPing.GetState();
        }

        public UnifiedNetworkState GetUnifiedNetworkState()
        {
            return lastUnifiedState != null ? lastUnifiedState.Clone() : null;
        }

        public UnifiedNetworkState GetLastUnifiedState()
        {
            return lastUnifiedState != null
                ? lastUnifiedState.Clone()
                : null;
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
                if (nat != null)
                    nat.StopNatKeepAlive();
            }
            catch (Exception ex)
            {
                Logger.Log("engine", "nat stop error: " + ex.Message);
            }
        }
    }
}