using System;
using System.Collections.Generic;
using System.Threading;
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
        private readonly FastPingSignalAnalyzer fastPingSignalAnalyzer = new FastPingSignalAnalyzer();
        private readonly NetworkSignalResolver networkSignalResolver = new NetworkSignalResolver();
        private readonly ServerProbeLearningStore probeLearningStore = new ServerProbeLearningStore();

        private StartupProbeCoordinator startupProbeCoordinator;

        // =========================
        // CURRENT STATE CACHE
        // =========================

        private MetricsSnapshot lastSnapshot;
        private UnifiedNetworkState lastUnifiedState;

        private readonly object publishSync = new object();

        private volatile bool startupEvaluationRunning;
        private DateTime lastStartupEvaluationUtc = DateTime.MinValue;
        private DateTime lastSpeedifySnapshotUtc = DateTime.MinValue;

        private StartupProbeResult lastStartupProbeResult;
        private EvaluationStats lastRouteEvaluationStats;

        private readonly DateTime appStartedUtc = DateTime.UtcNow;
        private const int StartupUiGraceSeconds = 10;

        // =========================
        // SPEEDIFY PATHS
        // =========================

        private const string SpeedifyCliPath = @"C:\Program Files (x86)\Speedify\speedify_cli.exe";
        private const string SpeedifyExePath = @"C:\Program Files (x86)\Speedify\Speedify.exe";

        // =========================
        // FIXED FAIR PROBE TARGETS
        // =========================

        private const string FixedPrimaryProbeTarget = "1.1.1.1";
        private const string FixedSecondaryProbeTarget = "8.8.8.8";

        private string lastFastPingPrimaryTarget;
        private string lastFastPingSecondaryTarget;

        // =========================
        // PROBE TIMING
        // =========================

        private const int RawBaselineObserveMs = 12000;
        private const int CandidateObserveMs = 10000;

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
            InitializeRouteSelection();

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

            UpdateFastPingTargetsIfChanged(FixedPrimaryProbeTarget, FixedSecondaryProbeTarget);
            Logger.Log("fastping", "probe mode -> externalonly | forced fixed targets for fair comparison");

            fastPing.Start();
        }

        private void InitializeTelemetry()
        {
            speedifyReader = new SpeedifyTelemetryReader(OnSpeedifySnapshot);
            speedifyReader.Start();
        }

        private void InitializeRouteSelection()
        {
            probeLearningStore.SeedDefaultsIfEmpty();

            startupProbeCoordinator = new StartupProbeCoordinator(
                disableSpeedifyAsync: DisableSpeedifyAsync,
                activateSpeedifyAsync: ActivateSpeedifyTargetAsync,
                getFastPingState: () => fastPing.GetState(),
                getShortWindowSnapshot: () => fastPingSignalAnalyzer.Last10sSnapshot,
                captureProbeAsync: CaptureProbeRuntimeAsync,
                learningStore: probeLearningStore,
                log: message => Logger.Log("startup", message));
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

                fastPingSignalAnalyzer.Update(fastPingState);
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

                // Keep comparison fair: always same external targets
                UpdateFastPingTargetsIfChanged(FixedPrimaryProbeTarget, FixedSecondaryProbeTarget);

                // Keep tunnel latency as supporting context only
                UpdateFastPingTunnelLatency(snapshot);

                FastPingState fastPingState = fastPing.GetState();

                fastPingSignalAnalyzer.Update(fastPingState);
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
        // FASTPING TARGETS / TUNNEL CONTEXT
        // =========================

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
                Logger.Log("startup", "startup evaluation already running | recheck ignored");
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

            await EnsureSpeedifyProcessRunningAsync();
            await EnsureSpeedifyTelemetryAvailableAsync();

            List<ProbeCandidateDefinition> candidates = BuildStartupCandidates();
            if (candidates.Count == 0)
            {
                Logger.Log("startup", "no startup candidates available");
                return;
            }

            lastStartupProbeResult = await startupProbeCoordinator.RunAsync(candidates, CancellationToken.None);

            if (lastStartupProbeResult == null)
            {
                Logger.Log("startup", "startup probe produced no result");
                return;
            }

            ApplyLearningFromStartupProbe(lastStartupProbeResult);
            await ApplyWinnerAfterStartupProbeAsync(lastStartupProbeResult.Winner);

            Logger.Log("startup", lastStartupProbeResult.SummaryText ?? "startup probe finished");

            lastRouteEvaluationStats = fastPing.GetLastEvaluationStats();
        }

        private List<ProbeCandidateDefinition> BuildStartupCandidates()
        {
            List<ProbeCandidateDefinition> list = new List<ProbeCandidateDefinition>();

            IReadOnlyList<SpeedifyServerRegion> defaults = SpeedifyServerRegionCatalog.GetDefaultRegions();
            if (defaults == null)
                return list;

            foreach (SpeedifyServerRegion region in defaults)
            {
                if (region == null)
                    continue;

                list.Add(new ProbeCandidateDefinition
                {
                    RouteKey = string.IsNullOrWhiteSpace(region.CountryCode)
                        ? region.ConnectTarget
                        : region.CountryCode,
                    DisplayName = region.FriendlyName,
                    ConnectTarget = region.ConnectTarget,
                    Kind = ProbeCandidateKind.Speedify
                });
            }

            return list;
        }

        private void ApplyLearningFromStartupProbe(StartupProbeResult result)
        {
            if (result == null || result.AllResults == null)
                return;

            foreach (ProbeScoreResult score in result.AllResults)
            {
                if (score?.Runtime == null)
                    continue;

                if (string.IsNullOrWhiteSpace(score.Runtime.TimeBucketKey))
                    score.Runtime.TimeBucketKey = ProbeScoring.GetCurrentTimeBucketKey(DateTime.Now);

                probeLearningStore.UpdateFromResult(score);
            }
        }

        private async Task ApplyWinnerAfterStartupProbeAsync(ProbeScoreResult winner)
        {
            if (winner == null || winner.Candidate == null)
                return;

            if (winner.Candidate.Kind == ProbeCandidateKind.Raw)
            {
                Logger.Log("startup", "winner is raw baseline -> keeping speedify disabled");
                await DisableSpeedifyAsync();
                return;
            }

            Logger.Log("startup", "applying winner target | " + winner.Candidate);
            await ActivateSpeedifyTargetAsync(winner.Candidate.ConnectTarget);
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

        // =========================
        // ROUTE SELECTION GLUE
        // =========================

        private async Task<bool> DisableSpeedifyAsync()
        {
            Logger.Log("startup", "disable speedify requested");

            if (!System.IO.File.Exists(SpeedifyCliPath))
            {
                Logger.Log("startup", "Speedify CLI not found -> cannot disable speedify");
                return false;
            }

            await RunSpeedifyCliAsync("disconnect");

            const int maxWaitMs = 12000;
            const int pollMs = 350;
            int waited = 0;

            while (waited < maxWaitMs)
            {
                if (!IsSpeedifyCurrentlyConnected())
                {
                    Logger.Log("startup", "Speedify disabled for raw baseline");
                    return true;
                }

                await Task.Delay(pollMs);
                waited += pollMs;
            }

            Logger.Log("startup", "Speedify did not fully disconnect in time");
            return !IsSpeedifyCurrentlyConnected();
        }

        private async Task<bool> ActivateSpeedifyTargetAsync(string connectTarget)
        {
            if (string.IsNullOrWhiteSpace(connectTarget))
                return false;

            if (!System.IO.File.Exists(SpeedifyCliPath))
            {
                Logger.Log("startup", "Speedify CLI not found -> cannot connect target");
                return false;
            }

            Logger.Log("startup", "activating Speedify target | " + connectTarget);

            await RunSpeedifyCliAsync("connect " + connectTarget);

            const int maxWaitMs = 15000;
            const int pollMs = 400;
            int waited = 0;

            while (waited < maxWaitMs)
            {
                if (IsSpeedifyCurrentlyConnected() && HasRecentSpeedifySnapshot())
                {
                    Logger.Log("startup", "Speedify target active | " + connectTarget);
                    return true;
                }

                await Task.Delay(pollMs);
                waited += pollMs;
            }

            Logger.Log("startup", "Speedify target did not become active in time | " + connectTarget);
            return false;
        }

        private bool IsSpeedifyCurrentlyConnected()
        {
            if (lastSnapshot == null)
                return false;

            if (!HasRecentSpeedifySnapshot())
                return false;

            return lastSnapshot.TunnelConnected;
        }

        private async Task<ProbeCandidateRuntimeData> CaptureProbeRuntimeAsync(
            ProbeCandidateDefinition candidate,
            TimeSpan settle,
            TimeSpan observe)
        {
            if (candidate == null)
                throw new ArgumentNullException(nameof(candidate));

            if (settle > TimeSpan.Zero)
                await Task.Delay(settle);

            if (observe > TimeSpan.Zero)
                await Task.Delay(observe);

            FastPingSignalAnalyzer.FastPingAggregateSnapshot window =
                observe >= TimeSpan.FromSeconds(20)
                    ? fastPingSignalAnalyzer.Last30sSnapshot
                    : fastPingSignalAnalyzer.Last10sSnapshot;

            FastPingState fastPingState = fastPing.GetState();
            UnifiedNetworkState unified = GetLastUnifiedState();
            MetricsSnapshot snapshot = lastSnapshot;

            ProbeCandidateRuntimeData runtime = new ProbeCandidateRuntimeData
            {
                Candidate = candidate,
                TimeBucketKey = ProbeScoring.GetCurrentTimeBucketKey(DateTime.Now),

                AvgPingMs = window != null ? window.AvgPingMs : -1,
                MaxPingMs = window != null ? window.MaxPingMs : -1,
                P95PingMs = window != null ? window.P95PingMs : -1,

                AvgJitterMs = window != null ? window.AvgJitterMs : -1,
                MinJitterMs = window != null ? window.MinJitterMs : -1,
                MaxJitterMs = window != null ? window.MaxJitterMs : -1,
                P95JitterMs = window != null ? window.P95JitterMs : -1,

                AvgLossRate = window != null ? window.AvgLossRate : -1,
                AvgBadQualityRate = window != null ? window.AvgBadQualityRate : -1,
                AvgConfidence = window != null ? window.AvgConfidence : (fastPingState != null ? fastPingState.Confidence : -1),

                SpikeDensityPerMinute = window != null ? window.SpikeDensityPerMinute : -1,
                StaleRate = window != null ? window.StaleRate : -1,

                TunnelConnected = snapshot != null && snapshot.TunnelConnected,
                AggregateCongested = snapshot != null && snapshot.AggregateCongested,
                ActiveConnectionCount = snapshot != null ? snapshot.ActiveConnectionCount : 0,
                BackupConnectionCount = snapshot != null ? snapshot.BackupConnectionCount : 0,

                IsValid = window != null && window.IsValid,
                Notes = unified != null ? unified.HealthReason : null
            };

            if (candidate.Kind == ProbeCandidateKind.Raw)
            {
                runtime.TunnelConnected = false;
                runtime.AggregateCongested = false;
                runtime.ActiveConnectionCount = 0;
                runtime.BackupConnectionCount = 0;
                runtime.Notes = "Raw baseline capture";
            }

            Logger.Log("startup",
                "capture probe | " + candidate +
                " | avgPing=" + FormatProbeMetric(runtime.AvgPingMs) +
                " | p95=" + FormatProbeMetric(runtime.P95PingMs) +
                " | avgJit=" + FormatProbeMetric(runtime.AvgJitterMs) +
                " | p95Jit=" + FormatProbeMetric(runtime.P95JitterMs) +
                " | loss=" + FormatProbePercent(runtime.AvgLossRate) +
                " | conf=" + FormatProbeMetric(runtime.AvgConfidence) +
                " | spkD=" + FormatProbeMetric(runtime.SpikeDensityPerMinute));

            return runtime;
        }

        private static string FormatProbeMetric(double value)
        {
            return value >= 0 ? value.ToString("0.0") : "--";
        }

        private static string FormatProbePercent(double value)
        {
            return value >= 0 ? (value * 100.0).ToString("0.0") + "%" : "--";
        }

        // =========================
        // SPEEDIFY CLI
        // =========================

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

        // =========================
        // ACCESSORS
        // =========================

        public FastPingState GetFastPingState()
        {
            return fastPing.GetState();
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

        public StartupProbeResult GetLastStartupProbeResult()
        {
            return lastStartupProbeResult;
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