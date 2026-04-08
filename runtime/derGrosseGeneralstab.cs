using System;
using System.Threading.Tasks;

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

        private const int RouteEvaluationMeasureMs = 35000;
        private const int RouteEvaluationSettleMs = 2000;

        // =========================
        // CURRENT STATE CACHE
        // =========================

        private MetricsSnapshot lastSnapshot;
        private UnifiedNetworkState lastUnifiedState;

        private readonly object publishSync = new object();

        private volatile bool startupEvaluationRunning;
        private DateTime lastStartupEvaluationUtc = DateTime.MinValue;

        private const string SpeedifyCliPath = @"C:\Program Files (x86)\Speedify\speedify_cli.exe";
        private const string SpeedifyExePath = @"C:\Program Files (x86)\Speedify\Speedify.exe";

        private DateTime lastSpeedifySnapshotUtc = DateTime.MinValue;

        private EvaluationStats lastRouteEvaluationStats;

        private readonly DateTime appStartedUtc = DateTime.UtcNow;
        private const int StartupUiGraceSeconds = 10;

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
            Logger.StartUnifiedMetricsSession();

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
                lastSpeedifySnapshotUtc = DateTime.UtcNow;

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

                lastUnifiedState = unifiedState?.Clone();

                // During startup grace, keep measurement/logging real,
                // but do not let the overlay scream while everything settles.
                if (!IsStartupUiGraceActive())
                    overlay?.RenderUnified(lastUnifiedState);

                Logger.LogAnalysis(lastUnifiedState);
                Logger.LogUnifiedCsv(lastUnifiedState);
            }
        }

        private bool IsStartupUiGraceActive()
        {
            return (DateTime.UtcNow - appStartedUtc).TotalSeconds < StartupUiGraceSeconds;
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
            if (startupEvaluationRunning)
            {
                Logger.Log("startup", "startup evaluation already running");
                return;
            }

            if (!IsSpeedifyInstalled())
            {
                Logger.Log("startup", "Speedify not installed -> skipping startup route evaluation");
                return;
            }

            Logger.Log("startup", "starting startup route evaluation");

            startupEvaluationRunning = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunStartupRouteEvaluationAsync();
                    lastStartupEvaluationUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    Logger.Log("startup", "startup route evaluation error: " + ex.Message);
                }
                finally
                {
                    startupEvaluationRunning = false;
                }
            });
        }

        private bool IsSpeedifyInstalled()
        {
            try
            {
                return System.IO.File.Exists(SpeedifyCliPath) || System.IO.File.Exists(SpeedifyExePath);
            }
            catch
            {
                return false;
            }
        }

        private async Task RunStartupRouteEvaluationAsync()
        {
            Logger.Log("startup", "route evaluation requested");

            await EnsureSpeedifyReadyForEvaluationAsync();

            if (!IsSpeedifyReadyForEvaluation())
            {
                Logger.Log("startup", "Speedify not ready -> skipping route evaluation");
                return;
            }

            Logger.Log("startup", "checking current Speedify server via CLI");

            string raw = await GetCurrentSpeedifyServerRawAsync();

            if (!string.IsNullOrWhiteSpace(raw))
                Logger.Log("startup", "current server raw:\n" + CleanJson(raw));
            else
                Logger.Log("startup", "failed to read current server");

            Logger.Log("startup", "starting fastping evaluation window");

            fastPing.BeginEvaluationWindow();

            try
            {
                await Task.Delay(RouteEvaluationSettleMs);
                await Task.Delay(RouteEvaluationMeasureMs);
            }
            finally
            {
                lastRouteEvaluationStats = fastPing.EndEvaluationWindow();
            }

            if (lastRouteEvaluationStats == null)
            {
                Logger.Log("startup", "evaluation produced no stats");
                return;
            }

            Logger.Log(
                "startup",
                "evaluation stats | valid=" + lastRouteEvaluationStats.IsValid +
                " | success=" + lastRouteEvaluationStats.SuccessCount +
                " | timeout=" + lastRouteEvaluationStats.TimeoutCount +
                " | exception=" + lastRouteEvaluationStats.ExceptionCount +
                " | median=" + FormatEvalForLog(lastRouteEvaluationStats.MedianMs) +
                " | p90=" + FormatEvalForLog(lastRouteEvaluationStats.P90Ms) +
                " | p95=" + FormatEvalForLog(lastRouteEvaluationStats.P95Ms) +
                " | jitter=" + FormatEvalForLog(lastRouteEvaluationStats.JitterMs) +
                " | timeoutRate=" + lastRouteEvaluationStats.TimeoutRate.ToString("0.00") +
                " | successRate=" + lastRouteEvaluationStats.SuccessRate.ToString("0.00") +
                " | burstSamples=" + lastRouteEvaluationStats.BurstSampleCount +
                " | burstTimeoutRate=" + lastRouteEvaluationStats.BurstTimeoutRate.ToString("0.00") +
                " | burstExceptionRate=" + lastRouteEvaluationStats.BurstExceptionRate.ToString("0.00"));
        }

        private static string FormatEvalForLog(double value)
        {
            return value > 0
                ? ((int)Math.Round(value)).ToString()
                : "--";
        }

        private async Task EnsureSpeedifyReadyForEvaluationAsync()
        {
            await EnsureSpeedifyProcessRunningAsync();
            await EnsureSpeedifyTelemetryAvailableAsync();
            await EnsureSpeedifyConnectedAsync();
            await WaitForFreshUsableSpeedifySnapshotAsync();
        }

        private async Task EnsureSpeedifyProcessRunningAsync()
        {
            if (IsSpeedifyProcessRunning())
            {
                Logger.Log("startup", "Speedify process already running");
                return;
            }

            if (!System.IO.File.Exists(SpeedifyExePath))
            {
                Logger.Log("startup", "Speedify executable not found");
                return;
            }

            Logger.Log("startup", "launching Speedify process");

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = SpeedifyExePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Logger.Log("startup", "failed to launch Speedify: " + ex.Message);
                return;
            }

            const int maxWaitMs = 10000;
            const int pollMs = 250;
            int waited = 0;

            while (waited < maxWaitMs)
            {
                if (IsSpeedifyProcessRunning())
                {
                    Logger.Log("startup", "Speedify process detected");
                    return;
                }

                await Task.Delay(pollMs);
                waited += pollMs;
            }

            Logger.Log("startup", "Speedify process did not appear in time");
        }

        private bool IsSpeedifyProcessRunning()
        {
            try
            {
                System.Diagnostics.Process[] processes =
                    System.Diagnostics.Process.GetProcessesByName("Speedify");

                return processes != null && processes.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private async Task EnsureSpeedifyTelemetryAvailableAsync()
        {
            const int maxWaitMs = 10000;
            const int pollMs = 250;
            int waited = 0;

            while (waited < maxWaitMs)
            {
                if (HasRecentSpeedifySnapshot())
                {
                    Logger.Log("startup", "Speedify telemetry available");
                    return;
                }

                await Task.Delay(pollMs);
                waited += pollMs;
            }

            Logger.Log("startup", "Speedify telemetry not available in time");
        }

        private async Task EnsureSpeedifyConnectedAsync()
        {
            if (IsSpeedifyCurrentlyConnected())
            {
                Logger.Log("startup", "Speedify already connected");
                return;
            }

            if (!System.IO.File.Exists(SpeedifyCliPath))
            {
                Logger.Log("startup", "Speedify CLI not found -> cannot force connect");
                return;
            }

            Logger.Log("startup", "requesting Speedify connect");

            await RunSpeedifyCliAsync("connect");

            const int maxWaitMs = 12000;
            const int pollMs = 500;
            int waited = 0;

            while (waited < maxWaitMs)
            {
                if (IsSpeedifyCurrentlyConnected())
                {
                    Logger.Log("startup", "Speedify connected");
                    return;
                }

                await Task.Delay(pollMs);
                waited += pollMs;
            }

            Logger.Log("startup", "Speedify did not connect in time");
        }

        private bool IsSpeedifyCurrentlyConnected()
        {
            if (lastSnapshot == null)
                return false;

            if (!HasRecentSpeedifySnapshot())
                return false;

            return lastSnapshot.TunnelConnected;
        }

        private async Task WaitForFreshUsableSpeedifySnapshotAsync()
        {
            const int maxWaitMs = 12000;
            const int pollMs = 250;
            int waited = 0;

            while (waited < maxWaitMs)
            {
                if (IsSpeedifyReadyForEvaluation())
                    return;

                await Task.Delay(pollMs);
                waited += pollMs;
            }
        }

        private bool IsSpeedifyReadyForEvaluation()
        {
            if (!IsSpeedifyProcessRunning())
                return false;

            if (!HasRecentSpeedifySnapshot())
                return false;

            if (lastSnapshot == null)
                return false;

            return lastSnapshot.TunnelConnected;
        }

        private bool HasRecentSpeedifySnapshot()
        {
            if (lastSnapshot == null)
                return false;

            if (lastSnapshot.TimeUtc == DateTime.MinValue)
                return false;

            if (lastSpeedifySnapshotUtc == DateTime.MinValue)
                return false;

            double ageMs = (DateTime.UtcNow - lastSpeedifySnapshotUtc).TotalMilliseconds;
            return ageMs >= 0 && ageMs <= 3000;
        }

        private async Task<int> RunSpeedifyCliAsync(string arguments)
        {
            if (!System.IO.File.Exists(SpeedifyCliPath))
                return -1;

            try
            {
                using (var process = new System.Diagnostics.Process())
                {
                    process.StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = SpeedifyCliPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    process.Start();
                    await Task.Run(() => process.WaitForExit());
                    return process.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("startup", "Speedify CLI error: " + ex.Message);
                return -1;
            }
        }

        private async Task<string> RunSpeedifyCliReadAsync(string arguments)
        {
            if (!System.IO.File.Exists(SpeedifyCliPath))
                return null;

            try
            {
                using (var process = new System.Diagnostics.Process())
                {
                    process.StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = SpeedifyCliPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = System.Text.Encoding.Unicode,
                        StandardErrorEncoding = System.Text.Encoding.Unicode,
                        CreateNoWindow = true
                    };

                    process.Start();

                    string output = await process.StandardOutput.ReadToEndAsync();
                    string error = await process.StandardError.ReadToEndAsync();
                    await Task.Run(() => process.WaitForExit());

                    if (!string.IsNullOrWhiteSpace(error))
                        Logger.Log("startup", "Speedify CLI stderr: " + error.Trim());

                    return SanitizeCliOutput(output);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("startup", "Speedify CLI read error: " + ex.Message);
                return null;
            }
        }

        private async Task<string> GetCurrentSpeedifyServerRawAsync()
        {
            return await RunSpeedifyCliReadAsync("show currentserver");
        }

        // =========================
        // ACCESSORS
        // =========================

        public FastPingState GetFastPingState()
        {
            return fastPing.GetState();
        }

        private string CleanJson(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return raw;

            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(raw))
                {
                    return System.Text.Json.JsonSerializer.Serialize(
                        doc,
                        new System.Text.Json.JsonSerializerOptions
                        {
                            WriteIndented = true
                        });
                }
            }
            catch
            {
                return raw.Replace("\r", "").Replace("\n", "").Trim();
            }
        }

        private string SanitizeCliOutput(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return raw;

            return raw
                .Replace("\0", "")
                .Replace("\uFEFF", "")
                .Trim();
        }

        public UnifiedNetworkState GetUnifiedNetworkState()
        {
            return GetLastUnifiedState();
        }

        public UnifiedNetworkState GetLastUnifiedState()
        {
            return lastUnifiedState != null
                ? lastUnifiedState.Clone()
                : null;
        }

        public EvaluationStats GetLastRouteEvaluationStats()
        {
            return lastRouteEvaluationStats != null
                ? lastRouteEvaluationStats.Clone()
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

        public Task RunManualRouteRecheck()
        {
            Logger.Log("startup", "manual recheck requested");
            Startup();
            return Task.CompletedTask;
        }

        public Task RunManualServerPingScan()
        {
            return RunManualRouteRecheck();
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