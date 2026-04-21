using System;
using System.Collections.Generic;
using System.Linq;
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

        private double pendingTunnelLatencyMs = -1;
        private int pendingTunnelLatencyConfirmCount = 0;
        private double lastAcceptedTunnelLatencyMs = -1;

        private const double TunnelLatencyVsFastPingBaselineRejectMs = 220.0;
        private const double TunnelLatencyHardMaxMs = 500.0;
        private const double TunnelLatencyJumpRejectMs = 200.0;
        private const int TunnelLatencyConfirmSamplesRequired = 2;

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
        private const string LearningStoreFileName = "route-learning-store.json";

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

        private static readonly StartupProbeTimingConfig StartupProbeTiming = new StartupProbeTimingConfig
        {
            TransitionGrace = TimeSpan.FromSeconds(2.0),
            RoutedFixedSettleDuration = TimeSpan.FromSeconds(6.0),
            SpeedifyWarmupPollInterval = TimeSpan.FromMilliseconds(250),
            
            RoutedStableGateCheck1 = TimeSpan.FromSeconds(4.0),
            RoutedStableGateCheck2 = TimeSpan.FromSeconds(6.0),
            RoutedStableGateForceStart = TimeSpan.FromSeconds(8.0),
            
            BaseMeasuredDuration = TimeSpan.FromSeconds(13.0),
            RawMeasuredDuration = TimeSpan.FromSeconds(18.0),
            PromisingCandidateExtensionDuration = TimeSpan.FromSeconds(4.0),
        };

        // =========================
        // CONSTRUCTOR
        // =========================

        public DerGrosseGeneralstab()
        {
            Logger.StartSession();
            Logger.StartDeviationSession();
            Logger.StartUnifiedMetricsSession();
            Logger.StartSpeedifyDebugSession();


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
            LoadLearningStoreFromDisk();

            startupProbeCoordinator = new StartupProbeCoordinator(
                disableSpeedifyAsync: DisableSpeedifyAsync,
                activateSpeedifyAsync: ActivateSpeedifyTargetAsync,
                getFastPingState: () => fastPing.GetState(),
                getShortWindowSnapshot: () => fastPingSignalAnalyzer.Last10sSnapshot,
                getWarmupTelemetry: GetStartupDecisionSnapshot,
                captureProbeAsync: CaptureProbeRuntimeAsync,
                learningStore: probeLearningStore,
                timing: StartupProbeTiming,
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

                lock (publishSync)
                {
                    lastSnapshot = snapshot;
                    lastSpeedifySnapshotUtc = DateTime.UtcNow;
                }

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

        private double ResolveTrustedTunnelLatency(MetricsSnapshot snapshot)
        {
            if (snapshot == null || !snapshot.TunnelConnected)
            {
                pendingTunnelLatencyMs = -1;
                pendingTunnelLatencyConfirmCount = 0;
                lastAcceptedTunnelLatencyMs = -1;
                return -1;
            }

            double candidate = -1;

            if (snapshot.AggregateLatencyMs > 0)
                candidate = snapshot.AggregateLatencyMs;
            else if (snapshot.LatencyMs > 0)
                candidate = snapshot.LatencyMs;

            if (candidate <= 0)
            {
                pendingTunnelLatencyMs = -1;
                pendingTunnelLatencyConfirmCount = 0;
                return -1;
            }

            if (candidate > TunnelLatencyHardMaxMs)
            {
                Logger.Log("fastping-tunnel",
                    "reject tunnel latency | reason=hard-max" +
                    " | candidate=" + candidate.ToString("0.0") +
                    " | max=" + TunnelLatencyHardMaxMs.ToString("0.0"));

                pendingTunnelLatencyMs = -1;
                pendingTunnelLatencyConfirmCount = 0;
                return -1;
            }

            FastPingState fastPingState = fastPing.GetState();
            double fastPingBaseline = fastPingState != null ? fastPingState.BaselinePingMs : -1;

            if (fastPingBaseline > 0 &&
                candidate > fastPingBaseline + TunnelLatencyVsFastPingBaselineRejectMs)
            {
                bool samePendingVsBaseline =
                    pendingTunnelLatencyMs > 0 &&
                    Math.Abs(pendingTunnelLatencyMs - candidate) <= 25.0;

                if (samePendingVsBaseline)
                    pendingTunnelLatencyConfirmCount++;
                else
                {
                    pendingTunnelLatencyMs = candidate;
                    pendingTunnelLatencyConfirmCount = 1;
                }

                Logger.Log("fastping-tunnel",
                    "hold tunnel latency | reason=vs-fastping-baseline" +
                    " | candidate=" + candidate.ToString("0.0") +
                    " | fpBase=" + fastPingBaseline.ToString("0.0") +
                    " | confirm=" + pendingTunnelLatencyConfirmCount +
                    "/" + TunnelLatencyConfirmSamplesRequired);

                if (pendingTunnelLatencyConfirmCount < TunnelLatencyConfirmSamplesRequired)
                    return lastAcceptedTunnelLatencyMs > 0 ? lastAcceptedTunnelLatencyMs : -1;
            }

            if (lastAcceptedTunnelLatencyMs > 0)
            {
                double jump = candidate - lastAcceptedTunnelLatencyMs;

                if (jump > TunnelLatencyJumpRejectMs)
                {
                    bool samePending =
                        pendingTunnelLatencyMs > 0 &&
                        Math.Abs(pendingTunnelLatencyMs - candidate) <= 25.0;

                    if (samePending)
                        pendingTunnelLatencyConfirmCount++;
                    else
                    {
                        pendingTunnelLatencyMs = candidate;
                        pendingTunnelLatencyConfirmCount = 1;
                    }

                    Logger.Log("fastping-tunnel",
                        "hold tunnel latency | reason=large-jump" +
                        " | candidate=" + candidate.ToString("0.0") +
                        " | lastAccepted=" + lastAcceptedTunnelLatencyMs.ToString("0.0") +
                        " | jump=" + jump.ToString("0.0") +
                        " | confirm=" + pendingTunnelLatencyConfirmCount +
                        "/" + TunnelLatencyConfirmSamplesRequired);

                    if (pendingTunnelLatencyConfirmCount < TunnelLatencyConfirmSamplesRequired)
                        return lastAcceptedTunnelLatencyMs;
                }
            }

            Logger.Log("fastping-tunnel",
                "accept tunnel latency" +
                " | candidate=" + candidate.ToString("0.0") +
                " | previous=" + lastAcceptedTunnelLatencyMs.ToString("0.0") +
                " | fpBase=" + fastPingBaseline.ToString("0.0"));

            lastAcceptedTunnelLatencyMs = candidate;
            pendingTunnelLatencyMs = -1;
            pendingTunnelLatencyConfirmCount = 0;
            return candidate;
        }

        // =========================
        // FASTPING TARGETS / TUNNEL CONTEXT
        // =========================

        private void UpdateFastPingTunnelLatency(MetricsSnapshot snapshot)
        {
            if (snapshot == null)
            {
                fastPing.UpdateTunnelLatency(-1);
                return;
            }

            double trustedLatency = ResolveTrustedTunnelLatency(snapshot);

            Logger.Log("fastping-tunnel",
                "apply tunnel latency" +
                " | agg=" + snapshot.AggregateLatencyMs.ToString("0.0") +
                " | snap=" + snapshot.LatencyMs.ToString("0.0") +
                " | trusted=" + trustedLatency.ToString("0.0") +
                " | connected=" + snapshot.TunnelConnected +
                " | server=" + (snapshot.ConnectedServerName ?? "-"));

            fastPing.UpdateTunnelLatency(trustedLatency);
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
            SaveLearningStoreToDisk();

            StartupApplyDecision applyDecision = BuildStartupApplyDecision(lastStartupProbeResult);
            Logger.Log("startup",
                "applyDecision=" + applyDecision.Kind +
                " | reason=" + applyDecision.Reason +
                " | coordinatorRestore=" + (lastStartupProbeResult.FinalRestoreSucceeded ? "succeeded" : "failed-or-not-confirmed") +
                " | " + BuildApplyDecisionDebugContext(lastStartupProbeResult));

            if (applyDecision.Kind == StartupApplyDecisionKind.ApplyWinner)
            {
                await ApplyWinnerAfterStartupProbeAsync(lastStartupProbeResult.Winner);
            }
            else if (applyDecision.Kind == StartupApplyDecisionKind.KeepRaw)
            {
                await DisableSpeedifyAsync();
            }
            else
            {
                Logger.Log("startup", "keeping existing best route (no winner apply)");
            }

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

        private void LoadLearningStoreFromDisk()
        {
            string path = GetLearningStorePath();
            ServerProbeLearningStore.LearningStoreLoadResult loadResult =
                probeLearningStore.LoadFromJsonFile(path);

            if (loadResult.Status == ServerProbeLearningStore.LearningStoreLoadStatus.Loaded)
            {
                Logger.Log("startup", "learning store loaded | profiles=" + loadResult.ProfilesApplied + " | path=" + path);
                return;
            }

            if (loadResult.Status == ServerProbeLearningStore.LearningStoreLoadStatus.Missing)
            {
                Logger.Log("startup", "learning store missing | seeded defaults only | path=" + path);
                return;
            }

            if (loadResult.Status == ServerProbeLearningStore.LearningStoreLoadStatus.Invalid)
            {
                Logger.Log("startup", "learning store invalid | using seeded defaults | path=" + path);
                return;
            }

            Logger.Log("startup", "learning store load error | using seeded defaults | path=" + path);
        }

        private void SaveLearningStoreToDisk()
        {
            string path = GetLearningStorePath();
            bool saved = probeLearningStore.SaveToJsonFile(path);

            if (saved)
                Logger.Log("startup", "learning store saved | profiles=" + probeLearningStore.GetProfileCount() + " | path=" + path);
            else
                Logger.Log("startup", "learning store save failed | path=" + path);
        }

        private static string GetLearningStorePath()
        {
            string baseDir =
                System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WTFnetApp");

            return System.IO.Path.Combine(baseDir, LearningStoreFileName);
        }

        private async Task ApplyWinnerAfterStartupProbeAsync(ProbeScoreResult winner)
        {
            if (winner == null || winner.Candidate == null)
                return;

            if (winner.Candidate.Kind == ProbeCandidateKind.Raw)
            {
                Logger.Log("startup", "winner is raw baseline -> already finalized by coordinator");
                return;
            }

            Logger.Log("startup", "winner target already active from coordinator restore | " + winner.Candidate);
        }

        private StartupApplyDecision BuildStartupApplyDecision(StartupProbeResult result)
        {
            if (result == null || result.Winner == null)
            {
                return new StartupApplyDecision
                {
                    Kind = StartupApplyDecisionKind.KeepCurrent,
                    Reason = "no winner result"
                };
            }

            ProbeWinnerConfidenceKind confidenceKind = result.WinnerConfidence != null
                ? result.WinnerConfidence.Kind
                : ProbeWinnerConfidenceKind.NoClearWinner;

            if (confidenceKind == ProbeWinnerConfidenceKind.HighConfidence)
            {
                return new StartupApplyDecision
                {
                    Kind = StartupApplyDecisionKind.ApplyWinner,
                    Reason = "high confidence winner"
                };
            }

            bool winnerRisky = result.Winner.WarningLevel == ProbeWarningLevel.Risky;
            bool winnerBorderline = result.Winner.WarningLevel == ProbeWarningLevel.Borderline;
            bool winnerIsRaw = result.Winner.Candidate != null &&
                               result.Winner.Candidate.Kind == ProbeCandidateKind.Raw;

            if (winnerRisky)
            {
                return new StartupApplyDecision
                {
                    Kind = winnerIsRaw ? StartupApplyDecisionKind.KeepRaw : StartupApplyDecisionKind.KeepCurrent,
                    Reason = "winner marked risky"
                };
            }

            if (confidenceKind == ProbeWinnerConfidenceKind.NoClearWinner)
            {
                return new StartupApplyDecision
                {
                    Kind = result.Baseline != null ? StartupApplyDecisionKind.KeepRaw : StartupApplyDecisionKind.KeepCurrent,
                    Reason = "no clear winner"
                };
            }

            if (confidenceKind == ProbeWinnerConfidenceKind.Tentative)
            {
                double baselineGap = result.Baseline != null
                    ? result.Baseline.OverallStabilityScore - result.Winner.OverallStabilityScore
                    : 0.0;

                bool meaningfulAdvantage =
                    result.WinnerConfidence != null &&
                    result.WinnerConfidence.GapToRunnerUpScore >= 12.0 &&
                    baselineGap >= 8.0;

                if (meaningfulAdvantage &&
                    result.Winner.WarningLevel == ProbeWarningLevel.None &&
                    !result.Winner.IsDiscardedEarly)
                {
                    return new StartupApplyDecision
                    {
                        Kind = StartupApplyDecisionKind.ApplyWinner,
                        Reason = "tentative but meaningful clean advantage"
                    };
                }

                return new StartupApplyDecision
                {
                    Kind = winnerIsRaw ? StartupApplyDecisionKind.KeepRaw : StartupApplyDecisionKind.KeepCurrent,
                    Reason = "tentative winner without strong safe advantage"
                };
            }

            if (winnerBorderline)
            {
                return new StartupApplyDecision
                {
                    Kind = winnerIsRaw ? StartupApplyDecisionKind.KeepRaw : StartupApplyDecisionKind.KeepCurrent,
                    Reason = "borderline winner held back"
                };
            }

            return new StartupApplyDecision
            {
                Kind = winnerIsRaw ? StartupApplyDecisionKind.KeepRaw : StartupApplyDecisionKind.KeepCurrent,
                Reason = "conservative default hold"
            };
        }

        private static string BuildApplyDecisionDebugContext(StartupProbeResult result)
        {
            if (result == null)
                return "decisionInputs=none";

            ProbeScoreResult winner = result.Winner;
            ProbeCandidateRuntimeData runtime = winner?.Runtime;

            string winnerName = winner?.Candidate?.ToString() ?? "none";
            string confidenceKind = result.WinnerConfidence?.Kind.ToString() ?? "NoClearWinner";
            string confidenceScore = result.WinnerConfidence != null
                ? result.WinnerConfidence.ConfidenceScore.ToString("0.0")
                : "--";
            string warning = winner != null ? winner.WarningLevel.ToString() : "None";
            string measured = runtime != null && runtime.MeasurementStarted ? "yes" : "no";
            string earlyStop = runtime != null && runtime.MeasurementStoppedEarly ? "yes" : "no";

            string baselineGap = "--";
            if (winner != null && result.Baseline != null)
            {
                baselineGap = (result.Baseline.OverallStabilityScore - winner.OverallStabilityScore)
                    .ToString("0.0");
            }

            string runnerUpGap = result.WinnerConfidence != null
                ? result.WinnerConfidence.GapToRunnerUpScore.ToString("0.0")
                : "--";

            return "winner=" + winnerName +
                   " | confidence=" + confidenceKind +
                   "@" + confidenceScore +
                   " | warning=" + warning +
                   " | measured=" + measured +
                   " | earlyStop=" + earlyStop +
                   " | baselineGap=" + baselineGap +
                   " | runnerUpGap=" + runnerUpGap;
        }

        private enum StartupApplyDecisionKind
        {
            ApplyWinner = 0,
            KeepCurrent = 1,
            KeepRaw = 2
        }

        private sealed class StartupApplyDecision
        {
            public StartupApplyDecisionKind Kind { get; set; }
            public string Reason { get; set; }
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
            MetricsSnapshot snapshot;
            DateTime speedifySnapshotUtc;

            lock (publishSync)
            {
                snapshot = lastSnapshot;
                speedifySnapshotUtc = lastSpeedifySnapshotUtc;
            }

            if (snapshot == null)
                return false;

            if (snapshot.TimeUtc == DateTime.MinValue)
                return false;

            if (speedifySnapshotUtc == DateTime.MinValue)
                return false;

            double ageMs = (DateTime.UtcNow - speedifySnapshotUtc).TotalMilliseconds;
            return ageMs >= 0 && ageMs <= 3000;
        }

        private StartupDecisionSnapshot GetStartupDecisionSnapshot()
        {
            UnifiedNetworkState unified = GetLastUnifiedState();
            if (unified == null)
                return new StartupDecisionSnapshot();

            StartupDecisionSnapshot startup = unified.StartupDecision;
            if (startup == null)
                return new StartupDecisionSnapshot();

            StartupDecisionSnapshot snapshot = new StartupDecisionSnapshot
            {
                TunnelConnected = startup.TunnelConnected,
                HasFreshTelemetry = startup.HasFreshTelemetry,
                ActiveConnectionCount = startup.ActiveConnectionCount,
                TimeUtc = startup.TimeUtc,
                AggregateLatencyMs = startup.AggregateLatencyMs,
                AggregateJitterMs = startup.AggregateJitterMs,
                QueueDelayMs = startup.QueueDelayMs,
                ReportedLatencyMs = startup.ReportedLatencyMs,
                AggregateCongested = startup.AggregateCongested,
                DownloadCongested = startup.DownloadCongested,
                UploadCongested = startup.UploadCongested
            };

            if (!IsFreshSpeedifyState(unified))
                snapshot.HasFreshTelemetry = false;

            return snapshot;
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

            DateTime disconnectWaitStartUtc = DateTime.UtcNow;
            bool disconnected = await WaitForSpeedifyConnectivityStateAsync(
                shouldBeConnected: false,
                connectTarget: "raw",
                maxWaitMs: 12000,
                pollMs: 300,
                requiredStableChecks: 3);

            if (disconnected)
            {
                Logger.Log("startup", "Speedify disconnect confirmed | elapsedMs=" +
                    (DateTime.UtcNow - disconnectWaitStartUtc).TotalMilliseconds.ToString("0"));
                Logger.Log("startup", "Speedify disabled for raw baseline");
                return true;
            }

            bool disconnectedLate = await WaitForSpeedifyConnectivityStateAsync(
                shouldBeConnected: false,
                connectTarget: "raw",
                maxWaitMs: 3000,
                pollMs: 250,
                requiredStableChecks: 2);

            if (disconnectedLate)
            {
                Logger.Log("startup", "Speedify disconnect confirmed after timeout grace");
                return true;
            }

            bool stillConnected = IsSpeedifyCurrentlyConnected();
            Logger.Log("startup", "Speedify disconnect timeout | stillConnected=" + (stillConnected ? "yes" : "no"));
            return !stillConnected;
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

            DateTime connectWaitStartUtc = DateTime.UtcNow;
            bool connected = await WaitForSpeedifyConnectivityStateAsync(
                shouldBeConnected: true,
                connectTarget: connectTarget,
                maxWaitMs: 15000,
                pollMs: 300,
                requiredStableChecks: 3);

            if (connected)
            {
                Logger.Log("startup", "Speedify connect transport confirmed | " + connectTarget +
                    " | elapsedMs=" + (DateTime.UtcNow - connectWaitStartUtc).TotalMilliseconds.ToString("0"));
                Logger.Log("startup", "Speedify activation succeeded; startup readiness will wait for stable live stats");
                Logger.Log("startup", "Speedify target active | " + connectTarget);
                return true;
            }

            Logger.Log("startup", "Speedify target did not become active in time | " + connectTarget);
            return false;
        }

        private bool IsSpeedifyCurrentlyConnected()
        {
            MetricsSnapshot snapshot;
            lock (publishSync)
            {
                snapshot = lastSnapshot;
            }

            if (snapshot == null)
                return false;

            if (!HasRecentSpeedifySnapshot())
                return false;

            return snapshot.TunnelConnected;
        }

        private async Task<bool> WaitForSpeedifyConnectivityStateAsync(
            bool shouldBeConnected,
            string connectTarget,
            int maxWaitMs,
            int pollMs,
            int requiredStableChecks)
        {
            int stableChecks = 0;
            int waited = 0;

            while (waited < maxWaitMs)
            {
                MetricsSnapshot snapshot;
                lock (publishSync)
                {
                    snapshot = lastSnapshot;
                }

                bool stateMatch = IsSpeedifyStateConsistent(snapshot, shouldBeConnected, connectTarget);
                if (stateMatch)
                {
                    stableChecks++;
                    if (stableChecks >= requiredStableChecks)
                        return true;
                }
                else
                {
                    stableChecks = 0;
                }

                await Task.Delay(pollMs);
                waited += pollMs;
            }

            return false;
        }

        private bool IsSpeedifyStateConsistent(MetricsSnapshot snapshot, bool shouldBeConnected, string connectTarget)
        {
            _ = connectTarget;

            if (snapshot == null || !HasRecentSpeedifySnapshot())
                return false;

            if (IsTunnelStateChurning(snapshot))
                return false;

            if (!shouldBeConnected)
                return !snapshot.TunnelConnected;

            if (!snapshot.TunnelConnected)
                return false;

            if (snapshot.ActiveConnectionCount <= 0)
                return false;

            bool hasIdentity =
                !string.IsNullOrWhiteSpace(snapshot.ConnectedServerPublicIp) ||
                !string.IsNullOrWhiteSpace(snapshot.ConnectedServerName);

            if (!hasIdentity)
                return false;

            return true;
        }

        private static bool IsTunnelStateChurning(MetricsSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.TunnelCurrentState))
                return false;

            string state = snapshot.TunnelCurrentState.Trim();
            if (state.Length == 0)
                return false;

            return state.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   state.IndexOf("reconnect", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   state.IndexOf("disconnect", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class SpeedifyWindowAccumulator
        {
            private int freshSampleCount;
            private int activeConnectionMax;
            private int backupConnectionMax;
            private bool tunnelConnected;
            private bool aggregateCongested;
            private DateTime lastAcceptedUtc = DateTime.MinValue;

            private double aggregateLatencySum;
            private int aggregateLatencyCount;

            private double aggregateJitterSum;
            private int aggregateJitterCount;

            private double reportedLatencySum;
            private int reportedLatencyCount;
            private readonly List<double> aggregateLatencySamples = new List<double>();

            public int FreshSampleCount => freshSampleCount;
            public bool TunnelConnected => tunnelConnected;
            public bool AggregateCongested => aggregateCongested;
            public int ActiveConnectionCount => activeConnectionMax;
            public int BackupConnectionCount => backupConnectionMax;

            public double AverageAggregateLatencyMs => aggregateLatencyCount > 0 ? aggregateLatencySum / aggregateLatencyCount : -1;
            public double AverageAggregateJitterMs => aggregateJitterCount > 0 ? aggregateJitterSum / aggregateJitterCount : -1;
            public double AverageReportedLatencyMs => reportedLatencyCount > 0 ? reportedLatencySum / reportedLatencyCount : -1;
            public double AggregateLatencyStdDevMs => ComputeStdDev(aggregateLatencySamples);

            public void Add(UnifiedNetworkState state)
            {
                if (!IsFreshSpeedifyState(state))
                    return;

                SpeedifyTelemetrySnapshot speedify = state.SpeedifyTelemetry ?? new SpeedifyTelemetrySnapshot();

                if (lastAcceptedUtc != DateTime.MinValue &&
                    state.TimeUtc <= lastAcceptedUtc)
                {
                    return;
                }

                freshSampleCount++;
                lastAcceptedUtc = state.TimeUtc;
                tunnelConnected = tunnelConnected || speedify.TunnelConnected;
                aggregateCongested = aggregateCongested || speedify.AggregateCongested || speedify.DownloadCongested || speedify.UploadCongested;
                activeConnectionMax = Math.Max(activeConnectionMax, speedify.ActiveConnectionCount);
                backupConnectionMax = Math.Max(backupConnectionMax, speedify.BackupConnectionCount);

                if (speedify.AggregateLatencyMs > 0)
                {
                    aggregateLatencySum += speedify.AggregateLatencyMs;
                    aggregateLatencyCount++;
                    aggregateLatencySamples.Add(speedify.AggregateLatencyMs);
                }

                if (speedify.AggregateJitterMs > 0)
                {
                    aggregateJitterSum += speedify.AggregateJitterMs;
                    aggregateJitterCount++;
                }

                if (speedify.LatencyMs > 0)
                {
                    reportedLatencySum += speedify.LatencyMs;
                    reportedLatencyCount++;
                }
            }

            private static double ComputeStdDev(List<double> values)
            {
                if (values == null || values.Count < 2)
                    return -1;

                double mean = values.Average();
                double varianceSum = 0;

                for (int i = 0; i < values.Count; i++)
                {
                    double delta = values[i] - mean;
                    varianceSum += delta * delta;
                }

                double variance = varianceSum / values.Count;
                return Math.Sqrt(Math.Max(0, variance));
            }
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

            bool isSpeedifyCandidate = candidate.Kind == ProbeCandidateKind.Speedify;
            SpeedifyWindowAccumulator speedifyWindow = null;

            if (observe > TimeSpan.Zero)
            {
                if (isSpeedifyCandidate)
                    speedifyWindow = await SampleSpeedifyWindowAsync(observe, TimeSpan.Zero);
                else
                    await Task.Delay(observe);
            }

            FastPingSignalAnalyzer.FastPingAggregateSnapshot window =
                observe >= TimeSpan.FromSeconds(20)
                    ? fastPingSignalAnalyzer.Last30sSnapshot
                    : fastPingSignalAnalyzer.Last10sSnapshot;

            FastPingState fastPingState = fastPing.GetState();
            UnifiedNetworkState unified = GetLastUnifiedState();
            SpeedifyTelemetrySnapshot speedify = unified?.SpeedifyTelemetry ?? new SpeedifyTelemetrySnapshot();
            MetricsSnapshot snapshot;
            lock (publishSync)
            {
                snapshot = lastSnapshot;
            }

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

                TunnelConnected = speedifyWindow != null && speedifyWindow.FreshSampleCount > 0
                    ? speedifyWindow.TunnelConnected
                    : (unified != null ? speedify.TunnelConnected : snapshot != null && snapshot.TunnelConnected),
                AggregateCongested = speedifyWindow != null && speedifyWindow.FreshSampleCount > 0
                    ? speedifyWindow.AggregateCongested
                    : (unified != null ? (speedify.AggregateCongested || speedify.DownloadCongested || speedify.UploadCongested) : snapshot != null && snapshot.AggregateCongested),
                ActiveConnectionCount = speedifyWindow != null && speedifyWindow.FreshSampleCount > 0
                    ? speedifyWindow.ActiveConnectionCount
                    : (unified != null ? speedify.ActiveConnectionCount : snapshot != null ? snapshot.ActiveConnectionCount : 0),
                BackupConnectionCount = speedifyWindow != null && speedifyWindow.FreshSampleCount > 0
                    ? speedifyWindow.BackupConnectionCount
                    : (unified != null ? speedify.BackupConnectionCount : snapshot != null ? snapshot.BackupConnectionCount : 0),
                SpeedifyAggregateLatencyMs = speedifyWindow != null && speedifyWindow.FreshSampleCount > 0
                    ? speedifyWindow.AverageAggregateLatencyMs
                    : (unified != null ? speedify.AggregateLatencyMs : snapshot != null ? snapshot.AggregateLatencyMs : -1),
                SpeedifyAggregateJitterMs = speedifyWindow != null && speedifyWindow.FreshSampleCount > 0
                    ? speedifyWindow.AverageAggregateJitterMs
                    : (unified != null ? speedify.AggregateJitterMs : snapshot != null ? snapshot.AggregateJitterMs : -1),
                SpeedifyReportedLatencyMs = speedifyWindow != null && speedifyWindow.FreshSampleCount > 0
                    ? speedifyWindow.AverageReportedLatencyMs
                    : (unified != null ? speedify.LatencyMs : snapshot != null ? snapshot.LatencyMs : -1),
                SpeedifyFreshSampleCount = speedifyWindow != null ? speedifyWindow.FreshSampleCount : 0,
                SpeedifyExpectedSampleCount = speedifyWindow != null ? Math.Max(1.0, Math.Round(observe.TotalMilliseconds / (observe <= TimeSpan.FromSeconds(5) ? 200.0 : 300.0))) : -1,
                SpeedifyAggregateLatencyStdDevMs = speedifyWindow != null ? speedifyWindow.AggregateLatencyStdDevMs : -1,

                IsValid = window != null && window.IsValid,
                Notes = unified != null ? unified.HealthReason : null
            };

            if (candidate.Kind == ProbeCandidateKind.Raw)
            {
                runtime.TunnelConnected = false;
                runtime.AggregateCongested = false;
                runtime.ActiveConnectionCount = 0;
                runtime.BackupConnectionCount = 0;
                runtime.SpeedifyAggregateLatencyMs = -1;
                runtime.SpeedifyAggregateJitterMs = -1;
                runtime.SpeedifyReportedLatencyMs = -1;
                runtime.SpeedifyFreshSampleCount = 0;
                runtime.SpeedifyExpectedSampleCount = -1;
                runtime.SpeedifyAggregateLatencyStdDevMs = -1;
                runtime.Notes = "Raw baseline capture";
            }
            else if (speedifyWindow != null && speedifyWindow.FreshSampleCount < 4)
            {
                runtime.Notes = string.IsNullOrWhiteSpace(runtime.Notes)
                    ? "Speedify low-confidence window"
                    : runtime.Notes + " | Speedify low-confidence window";
            }

            if (speedifyWindow != null && speedifyWindow.AggregateLatencyStdDevMs >= 0)
            {
                string stdDevNote = "Speedify latency stdev " + speedifyWindow.AggregateLatencyStdDevMs.ToString("0.0") + "ms";
                runtime.Notes = string.IsNullOrWhiteSpace(runtime.Notes)
                    ? stdDevNote
                    : runtime.Notes + " | " + stdDevNote;
            }

            Logger.Log("startup",
                "capture probe | " + candidate +
                " | avgPing=" + FormatProbeMetric(runtime.AvgPingMs) +
                " | p95=" + FormatProbeMetric(runtime.P95PingMs) +
                " | avgJit=" + FormatProbeMetric(runtime.AvgJitterMs) +
                " | p95Jit=" + FormatProbeMetric(runtime.P95JitterMs) +
                " | loss=" + FormatProbePercent(runtime.AvgLossRate) +
                " | conf=" + FormatProbeMetric(runtime.AvgConfidence) +
                " | spkD=" + FormatProbeMetric(runtime.SpikeDensityPerMinute) +
                " | spFresh=" + (speedifyWindow != null ? speedifyWindow.FreshSampleCount.ToString() : "0") +
                " | spAggLat=" + FormatProbeMetric(runtime.SpeedifyAggregateLatencyMs) +
                " | spAggJit=" + FormatProbeMetric(runtime.SpeedifyAggregateJitterMs) +
                " | spLatStd=" + (speedifyWindow != null ? FormatProbeMetric(speedifyWindow.AggregateLatencyStdDevMs) : "--"));

            Logger.Log("runtime",
                "capture | " + candidate +
                " | candidate=" + candidate +
                " | spFresh=" + runtime.SpeedifyFreshSampleCount +
                " | spExpected=" + (runtime.SpeedifyExpectedSampleCount > 0 ? runtime.SpeedifyExpectedSampleCount.ToString("0") : "--") +
                " | spLat=" + FormatProbeMetric(runtime.SpeedifyAggregateLatencyMs) +
                " | spJit=" + FormatProbeMetric(runtime.SpeedifyAggregateJitterMs) +
                " | spStd=" + FormatProbeMetric(runtime.SpeedifyAggregateLatencyStdDevMs) +
                " | fpLat=" + FormatProbeMetric(runtime.AvgPingMs) +
                " | fpJit=" + FormatProbeMetric(runtime.AvgJitterMs));

            return runtime;
        }

        private async Task<SpeedifyWindowAccumulator> SampleSpeedifyWindowAsync(TimeSpan duration, TimeSpan pollInterval)
        {
            SpeedifyWindowAccumulator accumulator = new SpeedifyWindowAccumulator();
            if (duration <= TimeSpan.Zero)
                return accumulator;

            TimeSpan effectivePollInterval;
            if (pollInterval > TimeSpan.Zero)
            {
                effectivePollInterval = pollInterval;
            }
            else
            {
                effectivePollInterval = duration <= TimeSpan.FromSeconds(5)
                    ? TimeSpan.FromMilliseconds(200)
                    : TimeSpan.FromMilliseconds(300);
            }

            DateTime deadlineUtc = DateTime.UtcNow + duration;
            while (DateTime.UtcNow < deadlineUtc)
            {
                UnifiedNetworkState unified = GetLastUnifiedState();
                accumulator.Add(unified);
                await Task.Delay(effectivePollInterval);
            }

            return accumulator;
        }

        private static bool IsFreshSpeedifyState(UnifiedNetworkState state)
        {
            if (state == null)
                return false;

            if (!state.SpeedifyTelemetryFresh)
                return false;

            if (state.TimeUtc == DateTime.MinValue)
                return false;

            double ageMs = (DateTime.UtcNow - state.TimeUtc).TotalMilliseconds;
            return ageMs >= 0 && ageMs <= 3000;
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
            return CloneStartupProbeResult(lastStartupProbeResult);
        }

        private static StartupProbeResult CloneStartupProbeResult(StartupProbeResult source)
        {
            if (source == null)
                return null;

            Dictionary<ProbeScoreResult, ProbeScoreResult> scoreMap =
                new Dictionary<ProbeScoreResult, ProbeScoreResult>();

            List<ProbeScoreResult> clonedAllResults = CloneProbeScoreList(source.AllResults, scoreMap);
            List<ProbeScoreResult> clonedShortlist = CloneProbeScoreList(source.Shortlist, scoreMap);

            return new StartupProbeResult
            {
                RawReadiness = source.RawReadiness == null
                    ? null
                    : new ProbeReadinessResult
                    {
                        Kind = source.RawReadiness.Kind,
                        Reason = source.RawReadiness.Reason
                    },
                Baseline = GetOrCloneProbeScoreResult(source.Baseline, scoreMap),
                Winner = GetOrCloneProbeScoreResult(source.Winner, scoreMap),
                FinalRestoreSucceeded = source.FinalRestoreSucceeded,
                AllResults = clonedAllResults,
                Shortlist = clonedShortlist,
                WinnerConfidence = source.WinnerConfidence == null
                    ? null
                    : new ProbeWinnerConfidenceResult
                    {
                        Kind = source.WinnerConfidence.Kind,
                        ConfidenceScore = source.WinnerConfidence.ConfidenceScore,
                        Reason = source.WinnerConfidence.Reason,
                        GapToRunnerUpScore = source.WinnerConfidence.GapToRunnerUpScore
                    },
                SummaryText = source.SummaryText
            };
        }

        private static List<ProbeScoreResult> CloneProbeScoreList(
            List<ProbeScoreResult> source,
            Dictionary<ProbeScoreResult, ProbeScoreResult> scoreMap)
        {
            if (source == null)
                return new List<ProbeScoreResult>();

            List<ProbeScoreResult> cloned = new List<ProbeScoreResult>(source.Count);
            foreach (ProbeScoreResult item in source)
                cloned.Add(GetOrCloneProbeScoreResult(item, scoreMap));

            return cloned;
        }

        private static ProbeScoreResult GetOrCloneProbeScoreResult(
            ProbeScoreResult source,
            Dictionary<ProbeScoreResult, ProbeScoreResult> scoreMap)
        {
            if (source == null)
                return null;

            ProbeScoreResult existing;
            if (scoreMap.TryGetValue(source, out existing))
                return existing;

            ProbeScoreResult cloned = new ProbeScoreResult
            {
                Candidate = CloneCandidateDefinition(source.Candidate),
                Runtime = CloneProbeRuntime(source.Runtime),
                OverallStabilityScore = source.OverallStabilityScore,
                JitterStabilityScore = source.JitterStabilityScore,
                StartupSelectionScore = source.StartupSelectionScore,
                WarningLevel = source.WarningLevel,
                Badge = source.Badge,
                IsDiscardedEarly = source.IsDiscardedEarly,
                IsShortlisted = source.IsShortlisted,
                IsWinner = source.IsWinner,
                SummaryText = source.SummaryText,
                WarningText = source.WarningText,
                BadgeText = source.BadgeText,
                JitterRangeMs = source.JitterRangeMs,
                LatencyContribution = source.LatencyContribution,
                JitterContribution = source.JitterContribution,
                SpikeContribution = source.SpikeContribution,
                LossStaleBadContribution = source.LossStaleBadContribution,
                SpeedifyGlobalAdjustment = source.SpeedifyGlobalAdjustment,
                SpeedifyPositiveInfluenceCapApplied = source.SpeedifyPositiveInfluenceCapApplied,
                SpeedifyLatencyModifier = source.SpeedifyLatencyModifier,
                SpeedifyJitterModifier = source.SpeedifyJitterModifier,
                SpeedifySpikeModifier = source.SpeedifySpikeModifier,
                SpeedifyLossStaleModifier = source.SpeedifyLossStaleModifier,
                SpeedifyHealthLabel = source.SpeedifyHealthLabel,
                ConfidenceModifier = source.ConfidenceModifier
            };

            scoreMap[source] = cloned;
            return cloned;
        }

        private static ProbeCandidateRuntimeData CloneProbeRuntime(ProbeCandidateRuntimeData source)
        {
            if (source == null)
                return null;

            return new ProbeCandidateRuntimeData
            {
                Candidate = CloneCandidateDefinition(source.Candidate),
                TimeBucketKey = source.TimeBucketKey,
                AvgPingMs = source.AvgPingMs,
                MaxPingMs = source.MaxPingMs,
                P95PingMs = source.P95PingMs,
                AvgJitterMs = source.AvgJitterMs,
                MinJitterMs = source.MinJitterMs,
                MaxJitterMs = source.MaxJitterMs,
                P95JitterMs = source.P95JitterMs,
                AvgLossRate = source.AvgLossRate,
                StartupAdjustedLossRate = source.StartupAdjustedLossRate,
                AvgBadQualityRate = source.AvgBadQualityRate,
                AvgConfidence = source.AvgConfidence,
                SpikeDensityPerMinute = source.SpikeDensityPerMinute,
                StaleRate = source.StaleRate,
                TunnelConnected = source.TunnelConnected,
                AggregateCongested = source.AggregateCongested,
                ActiveConnectionCount = source.ActiveConnectionCount,
                BackupConnectionCount = source.BackupConnectionCount,
                SpeedifyAggregateLatencyMs = source.SpeedifyAggregateLatencyMs,
                SpeedifyAggregateJitterMs = source.SpeedifyAggregateJitterMs,
                SpeedifyReportedLatencyMs = source.SpeedifyReportedLatencyMs,
                SpeedifyFreshSampleCount = source.SpeedifyFreshSampleCount,
                SpeedifyExpectedSampleCount = source.SpeedifyExpectedSampleCount,
                SpeedifyAggregateLatencyStdDevMs = source.SpeedifyAggregateLatencyStdDevMs,
                IsValid = source.IsValid,
                Notes = source.Notes,
                StabilizationReached = source.StabilizationReached,
                StabilizationTimedOut = source.StabilizationTimedOut,
                ReadinessKind = source.ReadinessKind,
                ReadinessReason = source.ReadinessReason,
                ReachedUsableState = source.ReachedUsableState,
                MeasurementStarted = source.MeasurementStarted,
                MeasurementCompleted = source.MeasurementCompleted,
                MeasurementStoppedEarly = source.MeasurementStoppedEarly,
                MeasurementStopReason = source.MeasurementStopReason,
                DiscardReason = source.DiscardReason,
                StartupSpeedifyTrusted = source.StartupSpeedifyTrusted,
            };
        }

        private static ProbeCandidateDefinition CloneCandidateDefinition(ProbeCandidateDefinition source)
        {
            if (source == null)
                return null;

            return new ProbeCandidateDefinition
            {
                RouteKey = source.RouteKey,
                DisplayName = source.DisplayName,
                ConnectTarget = source.ConnectTarget,
                Kind = source.Kind
            };
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
            Logger.Log("startup", "manual route recheck requested (runs startup route evaluation flow)");
            Startup();
            return Task.CompletedTask;
        }

        public Task RunManualServerPingScan()
        {
            Logger.Log("startup", "manual server ping scan requested (alias to startup route evaluation flow)");
            Startup();
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
                {
                    nat.StopNatKeepAlive();
                    await nat.RunningTask;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("engine", "nat stop error: " + ex.Message);
            }
        }
    }
}
