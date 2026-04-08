using System;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace WTFnetApp
{
    public sealed class WtfFastPing
    {
        private const int NormalProbeIntervalMs = 250;
        private const int NormalPerTargetPeriodMs = 500;
        private const int NormalBaseProbeTimeoutMs = 450;
        private const int NormalMaxProbeTimeoutMs = 700;
        private const int NormalStaleAfterMs = 2200;
        private const int NormalMissesBeforeDegrade = 4;

        private const int EvaluationProbeIntervalMs = 600;
        private const int EvaluationPerTargetPeriodMs = 1200;
        private const int EvaluationBaseProbeTimeoutMs = 800;
        private const int EvaluationMaxProbeTimeoutMs = 1200;
        private const int EvaluationStaleAfterMs = 4500;
        private const int EvaluationMissesBeforeDegrade = 3;

        private const double StableAlpha = 0.35;
        private const double HugeSpikeStableAlpha = 0.10;
        private const double BaselineAlpha = 0.08;
        private const double JitterAlpha = 0.22;

        private const double SpikeDeltaMs = 15.0;
        private const double FloorSpikeDeltaMs = 18.0;
        private const double RelativeSpikeMultiplier = 1.35;

        private const double BaselineGoodJitterMs = 8.0;
        private const double BaselineGoodLossRate = 0.12;
        private const double BaselineGoodInstantDeltaMs = 16.0;

        private static readonly byte[] Payload = new byte[12] { 87, 84, 70, 70, 65, 83, 84, 80, 73, 78, 71, 33 };

        private readonly object sync = new object();

        private readonly ProbeTargetState primary = new ProbeTargetState("primary");
        private readonly ProbeTargetState secondary = new ProbeTargetState("secondary");

        private readonly Ping pingPrimary = new Ping();
        private readonly Ping pingSecondary = new Ping();

        private volatile bool running;
        private volatile bool evaluationMode;

        private Task probeLoopTask;
        private Task publishLoopTask;

        private string secondaryFallbackHost = "1.1.1.1";
        private double tunnelLatencyMs = -1;

        private FastPingState state = new FastPingState();

        private DateTime evaluationModeSinceUtc = DateTime.MinValue;
        private DateTime lastPrimaryProbeUtc = DateTime.MinValue;
        private DateTime lastSecondaryProbeUtc = DateTime.MinValue;

        private bool evaluationWindowActive;
        private DateTime evaluationWindowStartedUtc = DateTime.MinValue;
        private DateTime evaluationWindowEffectiveStartUtc = DateTime.MinValue;
        private DateTime lastEvaluationBurstUtc = DateTime.MinValue;

        private EvaluationStats lastEvaluationStats = new EvaluationStats();

        private readonly EvaluationSample[] evaluationSamples = new EvaluationSample[512];
        private int evaluationSampleIndex;
        private int evaluationSampleCount;

        private const int EvaluationWarmupMs = 8000;
        private const int EvaluationBurstIntervalMs = 12000;
        private const int EvaluationBurstProbeDelayMs = 175;
        private const int EvaluationBurstExtraProbes = 2;

        private const int PublishIntervalMs = 250;

        public event Action<FastPingState> StateUpdated;

        public bool IsEvaluationMode
        {
            get { return evaluationMode; }
        }

        public static async Task<int> Ping(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return -1;

            try
            {
                using (var ping = new Ping())
                {
                    PingReply reply = await ping.SendPingAsync(host, 1000, Payload);
                    if (reply.Status == IPStatus.Success)
                        return (int)reply.RoundtripTime;
                }
            }
            catch
            {
            }

            return -1;
        }

        public void Start()
        {
            if (running)
                return;

            running = true;
            probeLoopTask = RunProbeLoop();
            publishLoopTask = RunPublishLoop();

            Logger.Log("fastping", "probe started");
        }

        public async Task StopAsync()
        {
            running = false;

            try
            {
                if (probeLoopTask != null)
                    await probeLoopTask;
            }
            catch
            {
            }

            try
            {
                if (publishLoopTask != null)
                    await publishLoopTask;
            }
            catch
            {
            }

            try { pingPrimary.Dispose(); } catch { }
            try { pingSecondary.Dispose(); } catch { }

            Logger.Log("fastping", "probe stopped");
        }

        public void SetEvaluationMode(bool enabled)
        {
            if (evaluationMode == enabled)
                return;

            evaluationMode = enabled;
            evaluationModeSinceUtc = DateTime.UtcNow;

            Logger.Log("fastping", "evaluation mode -> " + (enabled ? "on" : "off"));
        }

        public void BeginEvaluationWindow()
        {
            SetEvaluationMode(true);

            lock (sync)
            {
                evaluationWindowActive = true;
                evaluationWindowStartedUtc = DateTime.UtcNow;
                evaluationWindowEffectiveStartUtc = evaluationWindowStartedUtc.AddMilliseconds(EvaluationWarmupMs);
                lastEvaluationBurstUtc = DateTime.MinValue;

                evaluationSampleIndex = 0;
                evaluationSampleCount = 0;
                Array.Clear(evaluationSamples, 0, evaluationSamples.Length);

                Logger.Log("fastping", "evaluation window started");
            }
        }

        public EvaluationStats EndEvaluationWindow()
        {
            SetEvaluationMode(false);

            lock (sync)
            {
                evaluationWindowActive = false;
                lastEvaluationStats = BuildEvaluationStats_NoLock();
                Logger.Log(
                    "fastping",
                    "evaluation window ended | valid=" + lastEvaluationStats.IsValid +
                    " | success=" + lastEvaluationStats.SuccessCount +
                    " | timeout=" + lastEvaluationStats.TimeoutCount +
                    " | exception=" + lastEvaluationStats.ExceptionCount +
                    " | median=" + FormatEvalDouble(lastEvaluationStats.MedianMs) +
                    " | p90=" + FormatEvalDouble(lastEvaluationStats.P90Ms) +
                    " | jitter=" + FormatEvalDouble(lastEvaluationStats.JitterMs));

                return lastEvaluationStats.Clone();
            }
        }

        public EvaluationStats GetLastEvaluationStats()
        {
            lock (sync)
            {
                return lastEvaluationStats.Clone();
            }
        }

        public void UpdateTargets(string primaryHost, string secondaryHost = "1.1.1.1")
        {
            lock (sync)
            {
                if (!string.IsNullOrWhiteSpace(primaryHost) &&
                    !string.Equals(primary.Host, primaryHost, StringComparison.OrdinalIgnoreCase))
                {
                    primary.Reset(primaryHost);
                    lastPrimaryProbeUtc = DateTime.MinValue;
                    Logger.Log("fastping", "primary target -> " + primaryHost);
                }

                if (!string.IsNullOrWhiteSpace(secondaryHost))
                    secondaryFallbackHost = secondaryHost;

                string resolvedSecondary = secondaryFallbackHost;

                if (string.Equals(primaryHost, resolvedSecondary, StringComparison.OrdinalIgnoreCase))
                    resolvedSecondary = "8.8.8.8";

                if (!string.Equals(secondary.Host, resolvedSecondary, StringComparison.OrdinalIgnoreCase))
                {
                    secondary.Reset(resolvedSecondary);
                    lastSecondaryProbeUtc = DateTime.MinValue;
                    Logger.Log("fastping", "secondary target -> " + resolvedSecondary);
                }

                RecomputeState_NoLock();
            }
        }

        public void UpdateTunnelLatency(double latencyMs)
        {
            lock (sync)
            {
                tunnelLatencyMs = latencyMs > 0 ? latencyMs : -1;
                RecomputeState_NoLock();
            }
        }

        public FastPingState GetState()
        {
            lock (sync)
            {
                return state.Clone();
            }
        }

        private async Task RunProbeLoop()
        {
            while (running)
            {
                try
                {
                    ProbeTargetState target = null;
                    Ping ping = null;

                    lock (sync)
                    {
                        DateTime now = DateTime.UtcNow;
                        bool primaryDue =
                            !string.IsNullOrWhiteSpace(primary.Host) &&
                            (lastPrimaryProbeUtc == DateTime.MinValue ||
                             (now - lastPrimaryProbeUtc).TotalMilliseconds >= GetPerTargetPeriodMs());

                        bool secondaryDue =
                            !string.IsNullOrWhiteSpace(secondary.Host) &&
                            (lastSecondaryProbeUtc == DateTime.MinValue ||
                             (now - lastSecondaryProbeUtc).TotalMilliseconds >= GetPerTargetPeriodMs());

                        if (primaryDue && secondaryDue)
                        {
                            double primaryAge = lastPrimaryProbeUtc == DateTime.MinValue
                                ? double.MaxValue
                                : (now - lastPrimaryProbeUtc).TotalMilliseconds;

                            double secondaryAge = lastSecondaryProbeUtc == DateTime.MinValue
                                ? double.MaxValue
                                : (now - lastSecondaryProbeUtc).TotalMilliseconds;

                            if (primaryAge >= secondaryAge)
                            {
                                target = primary;
                                ping = pingPrimary;
                                lastPrimaryProbeUtc = now;
                            }
                            else
                            {
                                target = secondary;
                                ping = pingSecondary;
                                lastSecondaryProbeUtc = now;
                            }
                        }
                        else if (primaryDue)
                        {
                            target = primary;
                            ping = pingPrimary;
                            lastPrimaryProbeUtc = now;
                        }
                        else if (secondaryDue)
                        {
                            target = secondary;
                            ping = pingSecondary;
                            lastSecondaryProbeUtc = now;
                        }
                    }

                    if (target != null && !string.IsNullOrWhiteSpace(target.Host))
                        await ProbeAsync(target, ping);

                    await Task.Delay(GetProbeIntervalMs());
                }
                catch (Exception ex)
                {
                    if (running)
                        Logger.Log("fastping", "loop error: " + ex.Message);

                    await Task.Delay(250);
                }
            }
        }

        private async Task RunPublishLoop()
        {
            while (running)
            {
                FastPingState snapshot = null;

                try
                {
                    lock (sync)
                    {
                        RecomputeState_NoLock();
                        snapshot = state.Clone();
                    }

                    try
                    {
                        StateUpdated?.Invoke(snapshot);
                    }
                    catch
                    {
                    }

                    await Task.Delay(PublishIntervalMs);
                }
                catch (Exception ex)
                {
                    if (running)
                        Logger.Log("fastping", "publish loop error: " + ex.Message);

                    await Task.Delay(250);
                }
            }
        }

        private async Task ProbeAsync(ProbeTargetState target, Ping ping)
        {
            string host;
            int timeoutMs;

            lock (sync)
            {
                host = target.Host;
                timeoutMs = ComputeProbeTimeout_NoLock();
                target.LastProbeUtc = DateTime.UtcNow;
            }

            try
            {
                PingReply reply = await ping.SendPingAsync(host, timeoutMs, Payload);

                lock (sync)
                {
                    if (reply.Status == IPStatus.Success)
                    {
                        Logger.LogFastPingRaw(reply.RoundtripTime + "ms | " + host);
                        RegisterSuccess_NoLock(target, reply.RoundtripTime);
                        RecordEvaluationSample_NoLock(
                            target.Name,
                            host,
                            reply.RoundtripTime,
                            true,
                            false,
                            false,
                            false);
                    }
                    else
                    {
                        Logger.LogFastPingRaw("FAIL(" + reply.Status + ") | " + host);
                        RegisterMiss_NoLock(target);
                        RecordEvaluationSample_NoLock(
                            target.Name,
                            host,
                            -1,
                            false,
                            true,
                            false,
                            false);
                    }

                    RecomputeState_NoLock();
                }
            }
            catch (Exception ex)
            {
                lock (sync)
                {
                    Logger.LogFastPingRaw("EXC(" + ex.GetType().Name + ") | " + host);
                    RegisterMiss_NoLock(target);
                    RecordEvaluationSample_NoLock(
                        target.Name,
                        host,
                        -1,
                        false,
                        false,
                        true,
                        false);
                    RecomputeState_NoLock();
                }
            }
            await MaybeRunEvaluationBurstAsync(target, ping);
        }

        private async Task MaybeRunEvaluationBurstAsync(ProbeTargetState target, Ping ping)
        {
            string host = null;
            bool shouldRun = false;

            lock (sync)
            {
                if (!evaluationWindowActive)
                    return;

                if (IsEvaluationWarmupActive_NoLock())
                    return;

                if (target == null || string.IsNullOrWhiteSpace(target.Host))
                    return;

                DateTime now = DateTime.UtcNow;

                if (lastEvaluationBurstUtc != DateTime.MinValue &&
                    (now - lastEvaluationBurstUtc).TotalMilliseconds < EvaluationBurstIntervalMs)
                    return;

                lastEvaluationBurstUtc = now;
                host = target.Host;
                shouldRun = true;
            }

            if (!shouldRun)
                return;

            Logger.Log("fastping", "evaluation burst -> " + host);

            for (int i = 0; i < EvaluationBurstExtraProbes; i++)
            {
                try
                {
                    await Task.Delay(EvaluationBurstProbeDelayMs);

                    int timeoutMs;
                    lock (sync)
                    {
                        timeoutMs = ComputeProbeTimeout_NoLock();
                    }

                    PingReply reply = await ping.SendPingAsync(host, timeoutMs, Payload);

                    lock (sync)
                    {
                        if (reply.Status == IPStatus.Success)
                        {
                            Logger.LogFastPingRaw(reply.RoundtripTime + "ms | " + host + " [burst]");
                            RegisterSuccess_NoLock(target, reply.RoundtripTime);
                            RecordEvaluationSample_NoLock(
                                target.Name,
                                host,
                                reply.RoundtripTime,
                                true,
                                false,
                                false,
                                true);
                        }
                        else
                        {
                            Logger.LogFastPingRaw("FAIL(" + reply.Status + ") | " + host + " [burst]");
                            RegisterMiss_NoLock(target);
                            RecordEvaluationSample_NoLock(
                                target.Name,
                                host,
                                -1,
                                false,
                                true,
                                false,
                                true);
                        }

                        RecomputeState_NoLock();
                    }
                }
                catch (Exception ex)
                {
                    lock (sync)
                    {
                        Logger.LogFastPingRaw("EXC(" + ex.GetType().Name + ") | " + host + " [burst]");
                        RegisterMiss_NoLock(target);
                        RecordEvaluationSample_NoLock(
                            target.Name,
                            host,
                            -1,
                            false,
                            false,
                            true,
                            true);
                        RecomputeState_NoLock();
                    }
                }
            }
        }

        private void RecordEvaluationSample_NoLock(
            string probeName,
            string host,
            double rttMs,
            bool success,
            bool timeout,
            bool exception,
            bool burst)
        {
            if (!evaluationWindowActive)
                return;

            DateTime now = DateTime.UtcNow;

            EvaluationSample sample = new EvaluationSample
            {
                TimeUtc = now,
                ProbeName = probeName,
                Host = host,
                RttMs = rttMs,
                Success = success,
                Timeout = timeout,
                Exception = exception,
                Burst = burst
            };

            evaluationSamples[evaluationSampleIndex] = sample;
            evaluationSampleIndex = (evaluationSampleIndex + 1) % evaluationSamples.Length;

            if (evaluationSampleCount < evaluationSamples.Length)
                evaluationSampleCount++;
        }

        private EvaluationStats BuildEvaluationStats_NoLock()
        {
            EvaluationStats stats = new EvaluationStats();

            if (evaluationSampleCount == 0)
                return stats;

            double[] rtts = new double[evaluationSampleCount];
            int rttCount = 0;

            double jitterSum = 0;
            int jitterPairs = 0;
            double lastRtt = -1;

            for (int i = 0; i < evaluationSampleCount; i++)
            {
                EvaluationSample sample = evaluationSamples[i];
                if (sample == null)
                    continue;

                if (sample.TimeUtc < evaluationWindowEffectiveStartUtc)
                    continue;

                stats.TotalSamples++;

                if (sample.Burst)
                    stats.BurstSampleCount++;

                if (sample.Success && sample.RttMs > 0)
                {
                    stats.SuccessCount++;

                    if (sample.Burst)
                        stats.BurstSuccessCount++;

                    rtts[rttCount++] = sample.RttMs;

                    if (lastRtt > 0)
                    {
                        jitterSum += Math.Abs(sample.RttMs - lastRtt);
                        jitterPairs++;
                    }

                    lastRtt = sample.RttMs;
                }
                else if (sample.Timeout)
                {
                    stats.TimeoutCount++;

                    if (sample.Burst)
                        stats.BurstTimeoutCount++;
                }
                else if (sample.Exception)
                {
                    stats.ExceptionCount++;

                    if (sample.Burst)
                        stats.BurstExceptionCount++;
                }
            }

            if (stats.TotalSamples <= 0)
                return stats;

            stats.TimeoutRate = (double)stats.TimeoutCount / stats.TotalSamples;
            stats.ExceptionRate = (double)stats.ExceptionCount / stats.TotalSamples;
            stats.SuccessRate = (double)stats.SuccessCount / stats.TotalSamples;

            if (stats.BurstSampleCount > 0)
            {
                stats.BurstSuccessRate = (double)stats.BurstSuccessCount / stats.BurstSampleCount;
                stats.BurstTimeoutRate = (double)stats.BurstTimeoutCount / stats.BurstSampleCount;
                stats.BurstExceptionRate = (double)stats.BurstExceptionCount / stats.BurstSampleCount;
            }

            if (rttCount > 0)
            {
                Array.Sort(rtts, 0, rttCount);

                stats.MedianMs = PercentileSorted(rtts, rttCount, 0.50);
                stats.P90Ms = PercentileSorted(rtts, rttCount, 0.90);
                stats.P95Ms = PercentileSorted(rtts, rttCount, 0.95);
                stats.MaxMs = rtts[rttCount - 1];
                stats.MinMs = rtts[0];
                stats.JitterMs = jitterPairs > 0 ? jitterSum / jitterPairs : 0;
            }

            stats.IsValid =
                stats.TotalSamples >= 16 &&
                stats.SuccessCount >= 10 &&
                stats.ExceptionCount <= 3 &&
                stats.TimeoutRate <= 0.35;

            return stats;
        }

        private static double PercentileSorted(double[] values, int count, double percentile)
        {
            if (values == null || count <= 0)
                return -1;

            if (count == 1)
                return values[0];

            double rawIndex = (count - 1) * percentile;
            int low = (int)Math.Floor(rawIndex);
            int high = (int)Math.Ceiling(rawIndex);

            if (low == high)
                return values[low];

            double fraction = rawIndex - low;
            return values[low] + (values[high] - values[low]) * fraction;
        }

        private static string FormatEvalDouble(double value)
        {
            if (value < 0)
                return "--";

            return ((int)Math.Round(value)).ToString();
        }

        private int GetProbeIntervalMs()
        {
            return evaluationMode ? EvaluationProbeIntervalMs : NormalProbeIntervalMs;
        }

        private int GetPerTargetPeriodMs()
        {
            return evaluationMode ? EvaluationPerTargetPeriodMs : NormalPerTargetPeriodMs;
        }

        private int GetBaseProbeTimeoutMs()
        {
            return evaluationMode ? EvaluationBaseProbeTimeoutMs : NormalBaseProbeTimeoutMs;
        }

        private int GetMaxProbeTimeoutMs()
        {
            return evaluationMode ? EvaluationMaxProbeTimeoutMs : NormalMaxProbeTimeoutMs;
        }

        private int GetStaleAfterMs()
        {
            return evaluationMode ? EvaluationStaleAfterMs : NormalStaleAfterMs;
        }

        private int GetMissesBeforeDegrade()
        {
            return evaluationMode ? EvaluationMissesBeforeDegrade : NormalMissesBeforeDegrade;
        }

        private bool IsEvaluationWarmupActive_NoLock()
        {
            if (!evaluationMode)
                return false;

            if (evaluationModeSinceUtc == DateTime.MinValue)
                return false;

            return (DateTime.UtcNow - evaluationModeSinceUtc).TotalMilliseconds < EvaluationWarmupMs;
        }

        private int ComputeProbeTimeout_NoLock()
        {
            double basis = tunnelLatencyMs > 0 ? tunnelLatencyMs : state.StablePingMs;
            if (basis <= 0)
                return GetBaseProbeTimeoutMs();

            int timeout = (int)Math.Round(basis * 2.8 + 80.0);

            if (timeout < GetBaseProbeTimeoutMs())
                timeout = GetBaseProbeTimeoutMs();

            if (timeout > GetMaxProbeTimeoutMs())
                timeout = GetMaxProbeTimeoutMs();

            return timeout;
        }

        private void RegisterSuccess_NoLock(ProbeTargetState target, long rttMsRaw)
        {
            double sample = rttMsRaw < 0 ? 0 : rttMsRaw;
            DateTime now = DateTime.UtcNow;

            target.LastSuccessUtc = now;
            target.ConsecutiveMisses = 0;
            target.LastSampleMs = sample;

            PushOutcome_NoLock(target, true);
            PushSuccessRtt_NoLock(target, sample);

            target.InstantPingMs = ComputeInstant_NoLock(target);
            target.FloorPingMs = ComputeFloor_NoLock(target);

            if (target.StablePingMs <= 0)
            {
                target.StablePingMs = sample;
            }
            else
            {
                bool hugeSpike =
                    sample > target.StablePingMs * 2.2 &&
                    sample - target.StablePingMs > 35.0;

                double alpha = hugeSpike ? HugeSpikeStableAlpha : StableAlpha;
                target.StablePingMs = target.StablePingMs + alpha * (sample - target.StablePingMs);
            }

            UpdateJitter_NoLock(target, sample);

            target.LossRate = ComputeLossRate_NoLock(target);

            UpdateBaseline_NoLock(target);
            UpdateBadQualityRate_NoLock(target);

            target.Confidence = ComputeConfidence_NoLock(target);
        }

        private void RegisterMiss_NoLock(ProbeTargetState target)
        {
            target.ConsecutiveMisses++;
            PushOutcome_NoLock(target, false);

            target.LossRate = ComputeLossRate_NoLock(target);
            UpdateBadQualityRate_NoLock(target);
            target.Confidence = ComputeConfidence_NoLock(target);
        }

        private void UpdateJitter_NoLock(ProbeTargetState target, double sample)
        {
            if (target.StablePingMs <= 0)
            {
                target.JitterMs = 0;
                return;
            }

            double deviation = Math.Abs(sample - target.StablePingMs);

            if (target.JitterMs <= 0)
                target.JitterMs = deviation;
            else
                target.JitterMs = target.JitterMs + JitterAlpha * (deviation - target.JitterMs);
        }

        private void UpdateBaseline_NoLock(ProbeTargetState target)
        {
            if (target.StablePingMs <= 0)
                return;

            if (target.BaselinePingMs <= 0)
            {
                target.BaselinePingMs = target.StablePingMs;
                return;
            }

            bool goodWindow =
                target.LossRate <= BaselineGoodLossRate &&
                target.JitterMs >= 0 && target.JitterMs <= BaselineGoodJitterMs &&
                target.InstantPingMs > 0 &&
                target.BaselinePingMs > 0 &&
                target.InstantPingMs <= target.BaselinePingMs + BaselineGoodInstantDeltaMs &&
                target.ConsecutiveMisses == 0;

            if (goodWindow)
            {
                target.BaselinePingMs =
                    target.BaselinePingMs + BaselineAlpha * (target.StablePingMs - target.BaselinePingMs);
            }
            else
            {
                if (target.StablePingMs < target.BaselinePingMs)
                {
                    target.BaselinePingMs =
                        target.BaselinePingMs + (BaselineAlpha * 0.5) * (target.StablePingMs - target.BaselinePingMs);
                }
                else
                {
                    target.BaselinePingMs =
                        target.BaselinePingMs + 0.01 * (target.StablePingMs - target.BaselinePingMs);
                }
            }
        }

        private void UpdateBadQualityRate_NoLock(ProbeTargetState target)
        {
            if (target.OutcomeCount == 0)
            {
                target.BadQualityRate = 0;
                return;
            }

            int bad = 0;
            int total = 0;

            double reference = target.BaselinePingMs > 0 ? target.BaselinePingMs : target.StablePingMs;

            for (int i = 0; i < target.SuccessCount; i++)
            {
                double value = target.SuccessRtts[i];
                if (value <= 0)
                    continue;

                total++;

                bool badSuccess =
                    reference > 0 &&
                    (value > reference + 25.0 || value > reference * 1.45);

                if (badSuccess)
                    bad++;
            }

            int misses = 0;
            for (int i = 0; i < target.OutcomeCount; i++)
            {
                if (target.Outcomes[i] == 0)
                    misses++;
            }

            bad += misses;
            total += (target.OutcomeCount - target.SuccessCount);

            if (total <= 0)
                target.BadQualityRate = 0;
            else
                target.BadQualityRate = (double)bad / total;
        }

        private void PushSuccessRtt_NoLock(ProbeTargetState target, double sample)
        {
            target.SuccessRtts[target.SuccessIndex] = sample;
            target.SuccessTimes[target.SuccessIndex] = DateTime.UtcNow;
            target.SuccessIndex = (target.SuccessIndex + 1) % target.SuccessRtts.Length;

            if (target.SuccessCount < target.SuccessRtts.Length)
                target.SuccessCount++;
        }

        private void PushOutcome_NoLock(ProbeTargetState target, bool success)
        {
            target.Outcomes[target.OutcomeIndex] = success ? (byte)1 : (byte)0;
            target.OutcomeIndex = (target.OutcomeIndex + 1) % target.Outcomes.Length;

            if (target.OutcomeCount < target.Outcomes.Length)
                target.OutcomeCount++;
        }

        private double ComputeInstant_NoLock(ProbeTargetState target)
        {
            if (target.SuccessCount == 0)
                return -1;

            int idx = (target.SuccessIndex - 1 + target.SuccessRtts.Length) % target.SuccessRtts.Length;
            double value = target.SuccessRtts[idx];

            return value > 0 ? value : -1;
        }

        private double ComputeFloor_NoLock(ProbeTargetState target)
        {
            if (target.SuccessCount == 0)
                return -1;

            DateTime cutoff = DateTime.UtcNow.AddSeconds(-8);
            double best = double.MaxValue;

            for (int i = 0; i < target.SuccessCount; i++)
            {
                double value = target.SuccessRtts[i];
                DateTime when = target.SuccessTimes[i];

                if (value <= 0 || when < cutoff)
                    continue;

                if (value < best)
                    best = value;
            }

            if (best != double.MaxValue)
                return best;

            for (int i = 0; i < target.SuccessCount; i++)
            {
                double value = target.SuccessRtts[i];
                if (value > 0 && value < best)
                    best = value;
            }

            return best == double.MaxValue ? -1 : best;
        }

        private double ComputeLossRate_NoLock(ProbeTargetState target)
        {
            if (target.OutcomeCount == 0)
                return 0;

            int misses = 0;
            for (int i = 0; i < target.OutcomeCount; i++)
            {
                if (target.Outcomes[i] == 0)
                    misses++;
            }

            return (double)misses / target.OutcomeCount;
        }

        private double ComputeConfidence_NoLock(ProbeTargetState target)
        {
            double confidence = 1.0;

            if (target.LastSuccessUtc == DateTime.MinValue)
                return 0.0;

            double ageMs = (DateTime.UtcNow - target.LastSuccessUtc).TotalMilliseconds;
            if (ageMs > 300) confidence -= 0.06;
            if (ageMs > 600) confidence -= 0.10;
            if (ageMs > 1000) confidence -= 0.14;
            if (ageMs > GetStaleAfterMs()) confidence -= 0.25;

            confidence -= target.LossRate * 0.35;
            confidence -= target.BadQualityRate * 0.20;

            if (target.JitterMs > 8.0)
                confidence -= 0.05;
            if (target.JitterMs > 14.0)
                confidence -= 0.07;
            if (target.JitterMs > 22.0)
                confidence -= 0.10;

            if (target.ConsecutiveMisses >= GetMissesBeforeDegrade())
                confidence -= 0.25;
            else if (target.ConsecutiveMisses == 3)
                confidence -= 0.12;
            else if (target.ConsecutiveMisses == 2)
                confidence -= 0.05;

            if (confidence < 0)
                confidence = 0;
            if (confidence > 1)
                confidence = 1;

            return confidence;
        }

        private void RecomputeState_NoLock()
        {
            FastPingState next = new FastPingState();

            next.IsEvaluationMode = evaluationMode;

            next.PrimaryTarget = primary.Host;
            next.SecondaryTarget = secondary.Host;

            next.PrimaryInstantMs = primary.InstantPingMs;
            next.PrimaryStableMs = primary.StablePingMs;
            next.PrimaryFloorMs = primary.FloorPingMs;
            next.PrimaryBaselineMs = primary.BaselinePingMs;
            next.PrimaryJitterMs = primary.JitterMs;
            next.PrimaryLossRate = primary.LossRate;
            next.PrimaryBadQualityRate = primary.BadQualityRate;
            next.PrimaryConfidence = primary.Confidence;

            next.SecondaryInstantMs = secondary.InstantPingMs;
            next.SecondaryStableMs = secondary.StablePingMs;
            next.SecondaryFloorMs = secondary.FloorPingMs;
            next.SecondaryBaselineMs = secondary.BaselinePingMs;
            next.SecondaryJitterMs = secondary.JitterMs;
            next.SecondaryLossRate = secondary.LossRate;
            next.SecondaryBadQualityRate = secondary.BadQualityRate;
            next.SecondaryConfidence = secondary.Confidence;

            next.TunnelLatencyMs = tunnelLatencyMs;

            bool primaryFresh = IsFresh_NoLock(primary);
            bool secondaryFresh = IsFresh_NoLock(secondary);

            double bestStableProbe = ChooseBestStableProbe_NoLock(primaryFresh, secondaryFresh);
            double bestInstantProbe = ChooseBestInstantProbe_NoLock(primaryFresh, secondaryFresh);
            double bestFloorProbe = ChooseBestFloorProbe_NoLock(primaryFresh, secondaryFresh);
            double bestBaselineProbe = ChooseBestBaselineProbe_NoLock(primaryFresh, secondaryFresh);
            double bestJitterProbe = ChooseBestJitterProbe_NoLock(primaryFresh, secondaryFresh);

            next.StablePingMs = bestStableProbe;
            next.InstantPingMs = bestInstantProbe;
            next.FloorPingMs = bestFloorProbe;
            next.BaselinePingMs = bestBaselineProbe;
            next.JitterMs = bestJitterProbe;

            next.SpikeDetected = !IsEvaluationWarmupActive_NoLock() &&
                                 IsSpike_NoLock(bestInstantProbe, bestStableProbe, bestFloorProbe);

            next.LossRate = Math.Max(primary.LossRate, secondary.LossRate);
            next.BadQualityRate = Math.Max(primary.BadQualityRate, secondary.BadQualityRate);
            next.Confidence = Math.Max(primary.Confidence, secondary.Confidence);

            next.IsStale = !primaryFresh && !secondaryFresh;
            next.PrimaryFresh = primaryFresh;
            next.SecondaryFresh = secondaryFresh;

            next.PrimarySecondaryDivergenceMs = ComputeDivergence_NoLock(primaryFresh, secondaryFresh);

            next.DisplayedPingMs = ComputeDisplayedPing_NoLock(next);
            next.OffBaseline = ComputeOffBaseline_NoLock(next);
            next.HealthState = ComputeHealthState_NoLock(next);

            state = next;
        }

        private bool IsSpike_NoLock(double instant, double stable, double floor)
        {
            if (instant <= 0)
                return false;

            if (stable > 0)
            {
                if (instant - stable >= SpikeDeltaMs)
                    return true;

                if (instant > stable * RelativeSpikeMultiplier)
                    return true;
            }

            if (floor > 0 && instant - floor >= FloorSpikeDeltaMs)
                return true;

            return false;
        }

        private bool ComputeOffBaseline_NoLock(FastPingState s)
        {
            if (IsEvaluationWarmupActive_NoLock())
                return false;

            if (s.BaselinePingMs <= 0 || s.InstantPingMs <= 0)
                return false;

            if (s.InstantPingMs > s.BaselinePingMs + 12.0)
                return true;

            if (s.JitterMs > 10.0)
                return true;

            if (s.LossRate > 0.15)
                return true;

            if (s.BadQualityRate > 0.18)
                return true;

            return false;
        }

        private bool HasHardDegrade_NoLock()
        {
            return
                primary.ConsecutiveMisses >= GetMissesBeforeDegrade() ||
                secondary.ConsecutiveMisses >= GetMissesBeforeDegrade();
        }

        private FastPingHealthState ComputeHealthState_NoLock(FastPingState s)
        {
            if (IsEvaluationWarmupActive_NoLock())
            {
                if (s.IsStale || s.Confidence < 0.20)
                    return FastPingHealthState.Stale;

                return FastPingHealthState.Healthy;
            }

            bool hardDegrade = HasHardDegrade_NoLock();

            if (s.IsStale || s.Confidence < 0.20)
                return FastPingHealthState.Stale;

            if (hardDegrade)
                return FastPingHealthState.Critical;

            if (s.LossRate >= 0.30 || s.BadQualityRate >= 0.45)
                return FastPingHealthState.Critical;

            if ((s.SpikeDetected && s.JitterMs >= 12.0) ||
                (s.InstantPingMs > 0 && s.BaselinePingMs > 0 && s.InstantPingMs > s.BaselinePingMs + 35.0) ||
                s.JitterMs >= 18.0 ||
                s.LossRate >= 0.22)
                return FastPingHealthState.Warning;

            if (s.OffBaseline)
                return FastPingHealthState.OffBaseline;

            return FastPingHealthState.Healthy;
        }

        private bool IsFresh_NoLock(ProbeTargetState target)
        {
            if (target.LastSuccessUtc == DateTime.MinValue)
                return false;

            return (DateTime.UtcNow - target.LastSuccessUtc).TotalMilliseconds <= GetStaleAfterMs();
        }

        private double ChooseBestStableProbe_NoLock(bool primaryFresh, bool secondaryFresh)
        {
            if (primaryFresh && secondaryFresh)
            {
                return primary.Confidence >= secondary.Confidence
                    ? primary.StablePingMs
                    : secondary.StablePingMs;
            }

            if (primaryFresh)
                return primary.StablePingMs;

            if (secondaryFresh)
                return secondary.StablePingMs;

            if (primary.StablePingMs > 0)
                return primary.StablePingMs;

            return secondary.StablePingMs;
        }

        private double ChooseBestInstantProbe_NoLock(bool primaryFresh, bool secondaryFresh)
        {
            if (primaryFresh && secondaryFresh)
            {
                if (primary.InstantPingMs > 0 && secondary.InstantPingMs > 0)
                    return Math.Min(primary.InstantPingMs, secondary.InstantPingMs);

                if (primary.InstantPingMs > 0)
                    return primary.InstantPingMs;

                return secondary.InstantPingMs;
            }

            if (primaryFresh)
                return primary.InstantPingMs;

            if (secondaryFresh)
                return secondary.InstantPingMs;

            if (primary.InstantPingMs > 0)
                return primary.InstantPingMs;

            return secondary.InstantPingMs;
        }

        private double ChooseBestFloorProbe_NoLock(bool primaryFresh, bool secondaryFresh)
        {
            if (primaryFresh && secondaryFresh)
            {
                if (primary.FloorPingMs > 0 && secondary.FloorPingMs > 0)
                    return Math.Min(primary.FloorPingMs, secondary.FloorPingMs);

                if (primary.FloorPingMs > 0)
                    return primary.FloorPingMs;

                return secondary.FloorPingMs;
            }

            if (primaryFresh)
                return primary.FloorPingMs;

            if (secondaryFresh)
                return secondary.FloorPingMs;

            if (primary.FloorPingMs > 0)
                return primary.FloorPingMs;

            return secondary.FloorPingMs;
        }

        private double ChooseBestBaselineProbe_NoLock(bool primaryFresh, bool secondaryFresh)
        {
            if (primaryFresh && secondaryFresh)
            {
                if (primary.BaselinePingMs > 0 && secondary.BaselinePingMs > 0)
                    return Math.Min(primary.BaselinePingMs, secondary.BaselinePingMs);

                if (primary.BaselinePingMs > 0)
                    return primary.BaselinePingMs;

                return secondary.BaselinePingMs;
            }

            if (primaryFresh)
                return primary.BaselinePingMs;

            if (secondaryFresh)
                return secondary.BaselinePingMs;

            if (primary.BaselinePingMs > 0)
                return primary.BaselinePingMs;

            return secondary.BaselinePingMs;
        }

        private double ChooseBestJitterProbe_NoLock(bool primaryFresh, bool secondaryFresh)
        {
            if (primaryFresh && secondaryFresh)
            {
                if (primary.Confidence >= secondary.Confidence)
                    return primary.JitterMs;

                return secondary.JitterMs;
            }

            if (primaryFresh)
                return primary.JitterMs;

            if (secondaryFresh)
                return secondary.JitterMs;

            if (primary.JitterMs > 0)
                return primary.JitterMs;

            return secondary.JitterMs;
        }

        private double ComputeDivergence_NoLock(bool primaryFresh, bool secondaryFresh)
        {
            if (!primaryFresh || !secondaryFresh)
                return 0;

            if (primary.StablePingMs <= 0 || secondary.StablePingMs <= 0)
                return 0;

            return Math.Abs(primary.StablePingMs - secondary.StablePingMs);
        }

        private double ComputeDisplayedPing_NoLock(FastPingState s)
        {
            double tunnel = s.TunnelLatencyMs;
            double stable = s.StablePingMs;
            double instant = s.InstantPingMs;

            if (s.IsStale)
            {
                if (tunnel > 0)
                    return tunnel;
                if (instant > 0)
                    return instant;
                if (stable > 0)
                    return stable;
                return -1;
            }

            if (tunnel <= 0)
            {
                if (instant > 0 && stable > 0)
                    return (0.70 * instant) + (0.30 * stable);

                if (instant > 0)
                    return instant;

                if (stable > 0)
                    return stable;

                return -1;
            }

            if (stable <= 0 && tunnel > 0)
                return tunnel;

            if (stable > 0 && tunnel <= 0)
                return stable;

            if (stable <= 0 && tunnel <= 0)
                return -1;

            if (instant > 0 && tunnel > 0 && instant > tunnel + SpikeDeltaMs)
                return Math.Max(instant, tunnel);

            return (0.65 * tunnel) + (0.35 * stable);
        }

        private sealed class ProbeTargetState
        {
            public ProbeTargetState(string name)
            {
                Name = name;
            }

            public string Name;
            public string Host;

            public DateTime LastProbeUtc;
            public DateTime LastSuccessUtc;

            public double LastSampleMs = -1;
            public double InstantPingMs = -1;
            public double StablePingMs = -1;
            public double FloorPingMs = -1;
            public double BaselinePingMs = -1;
            public double JitterMs = -1;
            public double LossRate;
            public double BadQualityRate;
            public double Confidence;

            public int ConsecutiveMisses;

            public readonly double[] SuccessRtts = new double[12];
            public readonly DateTime[] SuccessTimes = new DateTime[12];
            public int SuccessIndex;
            public int SuccessCount;

            public readonly byte[] Outcomes = new byte[16];
            public int OutcomeIndex;
            public int OutcomeCount;

            public void Reset(string host)
            {
                Host = host;

                LastProbeUtc = DateTime.MinValue;
                LastSuccessUtc = DateTime.MinValue;

                LastSampleMs = -1;
                InstantPingMs = -1;
                StablePingMs = -1;
                FloorPingMs = -1;
                BaselinePingMs = -1;
                JitterMs = -1;
                LossRate = 0;
                BadQualityRate = 0;
                Confidence = 0;
                ConsecutiveMisses = 0;

                Array.Clear(SuccessRtts, 0, SuccessRtts.Length);
                Array.Clear(SuccessTimes, 0, SuccessTimes.Length);
                Array.Clear(Outcomes, 0, Outcomes.Length);

                SuccessIndex = 0;
                SuccessCount = 0;
                OutcomeIndex = 0;
                OutcomeCount = 0;
            }
        }
    }

    public sealed class EvaluationSample
    {
        public DateTime TimeUtc;
        public string ProbeName;
        public string Host;
        public double RttMs;
        public bool Success;
        public bool Timeout;
        public bool Exception;
        public bool Burst;
    }

    public sealed class EvaluationStats
    {
        public int TotalSamples;
        public int SuccessCount;
        public int TimeoutCount;
        public int ExceptionCount;

        public int BurstSampleCount;
        public int BurstSuccessCount;
        public int BurstTimeoutCount;
        public int BurstExceptionCount;

        public double SuccessRate;
        public double TimeoutRate;
        public double ExceptionRate;

        public double BurstSuccessRate;
        public double BurstTimeoutRate;
        public double BurstExceptionRate;

        public double MinMs = -1;
        public double MedianMs = -1;
        public double P90Ms = -1;
        public double P95Ms = -1;
        public double MaxMs = -1;
        public double JitterMs = -1;

        public bool IsValid;

        public EvaluationStats Clone()
        {
            return (EvaluationStats)MemberwiseClone();
        }
    }

    public enum FastPingHealthState
    {
        Healthy,
        OffBaseline,
        Warning,
        Critical,
        Stale
    }

    public sealed class FastPingState
    {
        public string PrimaryTarget;
        public string SecondaryTarget;

        public double PrimaryInstantMs;
        public double PrimaryStableMs;
        public double PrimaryFloorMs;
        public double PrimaryBaselineMs;
        public double PrimaryJitterMs;
        public double PrimaryLossRate;
        public double PrimaryBadQualityRate;
        public double PrimaryConfidence;

        public double SecondaryInstantMs;
        public double SecondaryStableMs;
        public double SecondaryFloorMs;
        public double SecondaryBaselineMs;
        public double SecondaryJitterMs;
        public double SecondaryLossRate;
        public double SecondaryBadQualityRate;
        public double SecondaryConfidence;

        public double TunnelLatencyMs;

        public double InstantPingMs;
        public double StablePingMs;
        public double FloorPingMs;
        public double BaselinePingMs;
        public double JitterMs;
        public double DisplayedPingMs;
        public double LossRate;
        public double BadQualityRate;
        public double Confidence;

        public double PrimarySecondaryDivergenceMs;

        public bool PrimaryFresh;
        public bool SecondaryFresh;
        public bool SpikeDetected;
        public bool IsStale;
        public bool OffBaseline;
        public bool IsEvaluationMode;

        public FastPingHealthState HealthState;

        public FastPingState Clone()
        {
            return (FastPingState)MemberwiseClone();
        }
    }
}