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
            ReadinessMinWait = TimeSpan.FromSeconds(4.0),
            ReadinessMaxWait = TimeSpan.FromSeconds(10.0),
            ReadinessConsecutiveChecks = 3,
            StabilizeMinWait = TimeSpan.FromSeconds(2.5),
            StabilizeMaxWait = TimeSpan.FromSeconds(16.0),
            StabilizeConsecutiveChecks = 3,
            ObserveDuration = TimeSpan.FromSeconds(10.0)
        };

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
            LoadLearningStoreFromDisk();

            startupProbeCoordinator = new StartupProbeCoordinator(
                disableSpeedifyAsync: DisableSpeedifyAsync,
                activateSpeedifyAsync: ActivateSpeedifyTargetAsync,
                getFastPingState: () => fastPing.GetState(),
                getShortWindowSnapshot: () => fastPingSignalAnalyzer.Last10sSnapshot,
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

            string timeBucket = ProbeScoring.GetCurrentTimeBucketKey(DateTime.Now);
            List<ProbeCandidateDefinition> orderedCandidates = OrderStartupCandidatesByLearning(candidates, timeBucket);

            lastStartupProbeResult = await startupProbeCoordinator.RunAsync(orderedCandidates, CancellationToken.None);

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
                Logger.Log("startup", "keeping current route state (no winner apply)");
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

        private List<ProbeCandidateDefinition> OrderStartupCandidatesByLearning(
            List<ProbeCandidateDefinition> candidates,
            string timeBucketKey)
        {
            if (candidates == null || candidates.Count == 0)
                return candidates ?? new List<ProbeCandidateDefinition>();

            List<HistoricalCandidateOrderingEntry> entries = new List<HistoricalCandidateOrderingEntry>();

            for (int i = 0; i < candidates.Count; i++)
            {
                ProbeCandidateDefinition candidate = candidates[i];
                if (candidate == null)
                    continue;

                ServerProbeLearningStore.HistoricalRouteOrderingScore historical =
                    probeLearningStore.EvaluateHistoricalOrderingScore(candidate.RouteKey, timeBucketKey);

                entries.Add(new HistoricalCandidateOrderingEntry
                {
                    Candidate = candidate,
                    OriginalIndex = i,
                    HistoricalScore = historical?.Score ?? 1000.0,
                    Trusted = historical != null && historical.IsTrusted,
                    BadBias = historical != null && historical.HasBadBias,
                    Reason = historical?.Reason ?? "default order | no profile"
                });
            }

            LogCandidateOrdering(entries, "original");

            bool hasTrusted = entries.Any(x => x.Trusted);
            if (!hasTrusted)
            {
                Logger.Log("startup", "startup ordering | trusted learning unavailable | using catalog default order");
                return entries
                    .OrderBy(x => x.OriginalIndex)
                    .Select(x => x.Candidate)
                    .ToList();
            }

            List<HistoricalCandidateOrderingEntry> ordered = entries
                .OrderBy(x => ComputeOrderingGroup(x))
                .ThenBy(x => x.Trusted ? x.HistoricalScore : x.OriginalIndex)
                .ThenBy(x => x.OriginalIndex)
                .ToList();

            LogCandidateOrdering(ordered, "reordered");

            return ordered.Select(x => x.Candidate).ToList();
        }

        private static int ComputeOrderingGroup(HistoricalCandidateOrderingEntry entry)
        {
            if (entry == null)
                return 3;

            if (!entry.Trusted)
                return 1;

            if (entry.BadBias)
                return 2;

            return 0;
        }

        private void LogCandidateOrdering(
            List<HistoricalCandidateOrderingEntry> entries,
            string label)
        {
            if (entries == null || entries.Count == 0)
                return;

            string text = string.Join(" | ", entries.Select(x =>
                (x.Candidate?.RouteKey ?? "unknown") +
                " => histScore=" + x.HistoricalScore.ToString("0.0") +
                " | " + (x.Trusted ? "trusted" : "untrusted") +
                " | " + x.Reason));

            Logger.Log("startup", "startup ordering | " + label + " | " + text);
        }

        private sealed class HistoricalCandidateOrderingEntry
        {
            public ProbeCandidateDefinition Candidate { get; set; }
            public int OriginalIndex { get; set; }
            public double HistoricalScore { get; set; }
            public bool Trusted { get; set; }
            public bool BadBias { get; set; }
            public string Reason { get; set; }
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
                Logger.Log("startup", "winner is raw baseline -> keeping speedify disabled");
                await DisableSpeedifyAsync();
                return;
            }

            Logger.Log("startup", "applying winner target | " + winner.Candidate);
            await ActivateSpeedifyTargetAsync(winner.Candidate.ConnectTarget);
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

            bool winnerCleanStabilization =
                result.Winner.Runtime != null &&
                result.Winner.Runtime.StabilizationReached &&
                !result.Winner.Runtime.StabilizationTimedOut;

            bool winnerFallbackStabilization =
                result.Winner.Runtime != null &&
                result.Winner.Runtime.StabilizationTimedOut;

            bool rawFallbackStabilization =
                result.Baseline != null &&
                result.Baseline.Runtime != null &&
                result.Baseline.Runtime.StabilizationTimedOut;

            if (confidenceKind == ProbeWinnerConfidenceKind.HighConfidence && winnerCleanStabilization)
            {
                return new StartupApplyDecision
                {
                    Kind = StartupApplyDecisionKind.ApplyWinner,
                    Reason = "high confidence + clean stabilization"
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

            if (!winnerCleanStabilization)
            {
                return new StartupApplyDecision
                {
                    Kind = winnerIsRaw ? StartupApplyDecisionKind.KeepRaw : StartupApplyDecisionKind.KeepCurrent,
                    Reason = "winner stabilization not clean"
                };
            }

            if (winnerFallbackStabilization || rawFallbackStabilization)
            {
                return new StartupApplyDecision
                {
                    Kind = winnerIsRaw ? StartupApplyDecisionKind.KeepRaw : StartupApplyDecisionKind.KeepCurrent,
                    Reason = "fallback stabilization detected"
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
            string stabilized = runtime != null && runtime.StabilizationReached ? "yes" : "no";
            string fallback = runtime != null && runtime.StabilizationTimedOut ? "yes" : "no";

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
                   " | stabilized=" + stabilized +
                   " | fallback=" + fallback +
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
                WarningLevel = source.WarningLevel,
                Badge = source.Badge,
                IsDiscardedEarly = source.IsDiscardedEarly,
                IsShortlisted = source.IsShortlisted,
                IsWinner = source.IsWinner,
                SummaryText = source.SummaryText,
                WarningText = source.WarningText,
                BadgeText = source.BadgeText,
                JitterRangeMs = source.JitterRangeMs
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
                AvgBadQualityRate = source.AvgBadQualityRate,
                AvgConfidence = source.AvgConfidence,
                SpikeDensityPerMinute = source.SpikeDensityPerMinute,
                StaleRate = source.StaleRate,
                TunnelConnected = source.TunnelConnected,
                AggregateCongested = source.AggregateCongested,
                ActiveConnectionCount = source.ActiveConnectionCount,
                BackupConnectionCount = source.BackupConnectionCount,
                IsValid = source.IsValid,
                Notes = source.Notes,
                StabilizationReached = source.StabilizationReached,
                StabilizationTimedOut = source.StabilizationTimedOut,
                ReadinessKind = source.ReadinessKind,
                ReadinessReason = source.ReadinessReason
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
