using System;

namespace WTFnetApp
{
    public sealed class FastPingSignalAnalyzer
    {
        private const int StartConfirmSamples = 3;
        private const int RecoveryConfirmSamples = 6;
        private const int OngoingSummarySeconds = 30;
        private const int AggregatedSummarySeconds = 5;

        private const double CriticalSpikeDeltaMs = 45.0;
        private const double CriticalJitterMs = 22.0;
        private const double WarningSpikeDeltaMs = 25.0;
        private const double WarningJitterMs = 12.0;

        private TrackerState currentState = TrackerState.Healthy;
        private TrackerState pendingState = TrackerState.Healthy;

        private int pendingStateSamples;
        private int recoverySamples;

        private DateTime stateStartUtc = DateTime.MinValue;
        private DateTime lastSummaryUtc = DateTime.MinValue;
        private DateTime lastSampleUtc = DateTime.MinValue;
        private DateTime lastAggregatedLogUtc = DateTime.MinValue;

        private bool baselineLogged;

        private readonly RunningStats ongoingStats = new RunningStats();
        private readonly RollingWindowStats shortWindow = new RollingWindowStats(TimeSpan.FromSeconds(10), 512);
        private readonly RollingWindowStats longWindow = new RollingWindowStats(TimeSpan.FromSeconds(30), 1024);

        public FastPingAggregateSnapshot Last10sSnapshot { get; private set; } = new FastPingAggregateSnapshot();
        public FastPingAggregateSnapshot Last30sSnapshot { get; private set; } = new FastPingAggregateSnapshot();

        public void Reset()
        {
            currentState = TrackerState.Healthy;
            pendingState = TrackerState.Healthy;
            pendingStateSamples = 0;
            recoverySamples = 0;
            stateStartUtc = DateTime.MinValue;
            lastSummaryUtc = DateTime.MinValue;
            lastSampleUtc = DateTime.MinValue;
            lastAggregatedLogUtc = DateTime.MinValue;
            baselineLogged = false;

            ongoingStats.Reset();
            shortWindow.Reset();
            longWindow.Reset();

            Last10sSnapshot = new FastPingAggregateSnapshot();
            Last30sSnapshot = new FastPingAggregateSnapshot();
        }

        public void Update(FastPingState state)
        {
            if (state == null)
                return;

            DateTime nowUtc = DateTime.UtcNow;
            lastSampleUtc = nowUtc;

            if (!baselineLogged && state.BaselinePingMs > 0 && state.StablePingMs > 0)
            {
                Logger.LogDeviation(
                    "baseline established | " +
                    "stable=" + FormatMs(state.StablePingMs) + " | " +
                    "baseline=" + FormatMs(state.BaselinePingMs) + " | " +
                    "jitter=" + FormatMs(state.JitterMs) + " | " +
                    "primary=" + SafeText(state.PrimaryTarget) + " | " +
                    "secondary=" + SafeText(state.SecondaryTarget));

                baselineLogged = true;
            }

            shortWindow.Add(nowUtc, state);
            longWindow.Add(nowUtc, state);

            Last10sSnapshot = shortWindow.BuildSnapshot();
            Last30sSnapshot = longWindow.BuildSnapshot();
            
            MaybeLogAggregatedSummary();

            TrackerState observedState = ClassifyState(state);

            if (currentState == TrackerState.Healthy)
            {
                HandleHealthyState(state, observedState);
                return;
            }

            HandleNonHealthyState(state, observedState);
        }

        private void HandleHealthyState(FastPingState state, TrackerState observedState)
        {
            if (observedState == TrackerState.Healthy)
            {
                pendingState = TrackerState.Healthy;
                pendingStateSamples = 0;
                return;
            }

            if (pendingState != observedState)
            {
                pendingState = observedState;
                pendingStateSamples = 1;
            }
            else
            {
                pendingStateSamples++;
            }

            if (pendingStateSamples >= StartConfirmSamples)
                EnterState(state, observedState);
        }

        private void HandleNonHealthyState(FastPingState state, TrackerState observedState)
        {
            ongoingStats.Add(state);

            if (observedState == TrackerState.Healthy)
            {
                recoverySamples++;

                if (recoverySamples >= RecoveryConfirmSamples)
                    ExitToHealthy(state);
                else
                    MaybeLogOngoingSummary(state);

                return;
            }

            recoverySamples = 0;

            if (observedState > currentState)
            {
                EnterState(state, observedState);
                return;
            }

            if (observedState < currentState)
            {
                if (pendingState != observedState)
                {
                    pendingState = observedState;
                    pendingStateSamples = 1;
                }
                else
                {
                    pendingStateSamples++;
                }

                if (pendingStateSamples >= StartConfirmSamples)
                    TransitionState(state, observedState);
            }
            else
            {
                pendingState = observedState;
                pendingStateSamples = 0;
            }

            MaybeLogOngoingSummary(state);
        }

        private void EnterState(FastPingState state, TrackerState newState)
        {
            currentState = newState;
            pendingState = newState;
            pendingStateSamples = 0;
            recoverySamples = 0;

            stateStartUtc = DateTime.UtcNow;
            lastSummaryUtc = DateTime.UtcNow;

            ongoingStats.Reset();
            ongoingStats.Add(state);

            Logger.LogDeviation(BuildStartMessage(state, newState));
        }

        private void TransitionState(FastPingState state, TrackerState newState)
        {
            TimeSpan duration = DateTime.UtcNow - stateStartUtc;

            Logger.LogDeviation(
                "state transition | " +
                currentState.ToString().ToLowerInvariant() +
                " -> " +
                newState.ToString().ToLowerInvariant() +
                " | duration=" + FormatDuration(duration) + " | " +
                BuildMetricsTail(state));

            currentState = newState;
            pendingState = newState;
            pendingStateSamples = 0;
            recoverySamples = 0;

            stateStartUtc = DateTime.UtcNow;
            lastSummaryUtc = DateTime.UtcNow;

            ongoingStats.Reset();
            ongoingStats.Add(state);
        }

        private void ExitToHealthy(FastPingState state)
        {
            TimeSpan duration = DateTime.UtcNow - stateStartUtc;

            Logger.LogDeviation(
                "recovered | from=" +
                currentState.ToString().ToLowerInvariant() +
                " | duration=" + FormatDuration(duration) + " | " +
                BuildMetricsTail(state));

            currentState = TrackerState.Healthy;
            pendingState = TrackerState.Healthy;
            pendingStateSamples = 0;
            recoverySamples = 0;

            stateStartUtc = DateTime.MinValue;
            lastSummaryUtc = DateTime.MinValue;
            ongoingStats.Reset();
        }

        private void MaybeLogOngoingSummary(FastPingState state)
        {
            if (lastSummaryUtc == DateTime.MinValue)
            {
                lastSummaryUtc = DateTime.UtcNow;
                return;
            }

            if ((DateTime.UtcNow - lastSummaryUtc).TotalSeconds < OngoingSummarySeconds)
                return;

            TimeSpan duration = DateTime.UtcNow - stateStartUtc;

            Logger.LogDeviation(
                "ongoing " + currentState.ToString().ToLowerInvariant() +
                " | duration=" + FormatDuration(duration) +
                " | avgInst=" + FormatMs(ongoingStats.AvgInstantMs) +
                " | maxInst=" + FormatMs(ongoingStats.MaxInstantMs) +
                " | avgJit=" + FormatMs(ongoingStats.AvgJitterMs) +
                " | maxJit=" + FormatMs(ongoingStats.MaxJitterMs) +
                " | spikes=" + ongoingStats.SpikeCount +
                " | stale=" + ongoingStats.StaleCount +
                " | avgConf=" + Format01(ongoingStats.AvgConfidence) +
                " | avgLoss=" + FormatPct(ongoingStats.AvgLossRate) +
                " | avgBad=" + FormatPct(ongoingStats.AvgBadQualityRate));

            lastSummaryUtc = DateTime.UtcNow;
            ongoingStats.Reset();
            ongoingStats.Add(state);
        }

        private void MaybeLogAggregatedSummary()
        {
            DateTime nowUtc = DateTime.UtcNow;

            if (lastAggregatedLogUtc != DateTime.MinValue &&
                (nowUtc - lastAggregatedLogUtc).TotalSeconds < AggregatedSummarySeconds)
                return;

            FastPingAggregateSnapshot s10 = Last10sSnapshot;
            FastPingAggregateSnapshot s30 = Last30sSnapshot;

            if (!s10.IsValid && !s30.IsValid)
                return;

            double avg = s10.IsValid ? s10.AvgPingMs : s30.AvgPingMs;
            double max = s10.IsValid ? s10.MaxPingMs : s30.MaxPingMs;
            double p95 = s10.IsValid ? s10.P95PingMs : s30.P95PingMs;
            double jitter = s10.IsValid ? s10.AvgJitterMs : s30.AvgJitterMs;
            double loss = s10.IsValid ? s10.AvgLossRate : s30.AvgLossRate;

            double bad = s10.IsValid ? s10.AvgBadQualityRate : s30.AvgBadQualityRate;
            double conf = s10.IsValid ? s10.AvgConfidence : s30.AvgConfidence;

            Logger.Log("fastping-agg",
    $"avg={avg:0} max={max:0} p95={p95:0} j={jitter:0} loss={loss:0.00} " +
    $"bad={bad:0.00} conf={conf:0.00} " +
    $"q={s10.AvgQueueDelayMs:0.0} q95={s10.P95QueueDelayMs:0.0} " +
    $"qg={s10.QueueGrowthMsPerSec:+0.0;-0.0;0.0} qa={s10.QueueAccelerationMsPerSec2:+0.0;-0.0;0.0} " +
    $"qt={s10.QueueTrendConfidence:0.00} qb={(s10.QueueBuilding ? 1 : 0)} qd={(s10.QueueDraining ? 1 : 0)} " +
    $"spkD10={s10.SpikeDensityPerMinute:0.0} spkD30={s30.SpikeDensityPerMinute:0.0} " +
    $"burstD10={s10.LossBurstDensity:0.0} badT10={s10.BadTimeRatio:0.00} wSegLoss10={s10.WorstSegmentLoss:0.00}");

            lastAggregatedLogUtc = nowUtc;
        }

        private TrackerState ClassifyState(FastPingState state)
        {
            bool isStale = state.IsStale || state.HealthState == FastPingHealthState.Stale;
            if (isStale)
                return TrackerState.Critical;

            double baseline = state.BaselinePingMs > 0 ? state.BaselinePingMs : state.StablePingMs;
            double instant = state.InstantPingMs;
            double jitter = state.JitterMs;

            double deltaFromBaseline = (baseline > 0 && instant > 0) ? (instant - baseline) : 0;

            if (state.HealthState == FastPingHealthState.Critical)
                return TrackerState.Critical;

            if (state.HealthState == FastPingHealthState.Warning)
                return TrackerState.Warning;

            if (state.HealthState == FastPingHealthState.OffBaseline)
                return TrackerState.OffBaseline;

            if (state.LossRate >= 0.20 || state.BadQualityRate >= 0.35)
                return TrackerState.Critical;

            if (deltaFromBaseline >= CriticalSpikeDeltaMs || jitter >= CriticalJitterMs)
                return TrackerState.Critical;

            if ((deltaFromBaseline >= WarningSpikeDeltaMs && jitter >= 8.0) ||
                jitter >= WarningJitterMs ||
                (state.SpikeDetected && deltaFromBaseline >= 18.0))
                return TrackerState.Warning;

            if (deltaFromBaseline >= 15.0 || jitter >= 8.0)
                return TrackerState.OffBaseline;

            return TrackerState.Healthy;
        }

        private string BuildStartMessage(FastPingState state, TrackerState newState)
        {
            string prefix;

            switch (newState)
            {
                case TrackerState.OffBaseline:
                    prefix = "off-baseline start";
                    break;
                case TrackerState.Warning:
                    prefix = "warning start";
                    break;
                case TrackerState.Critical:
                    prefix = "critical start";
                    break;
                default:
                    prefix = "state start";
                    break;
            }

            return prefix + " | " + BuildMetricsTail(state);
        }

        private string BuildMetricsTail(FastPingState state)
        {
            bool isStale = state.IsStale || state.HealthState == FastPingHealthState.Stale;
            double baseline = state.BaselinePingMs > 0 ? state.BaselinePingMs : state.StablePingMs;
            double delta = (baseline > 0 && state.InstantPingMs > 0) ? state.InstantPingMs - baseline : 0;

            return
                "inst=" + FormatMs(state.InstantPingMs) + " | " +
                "stable=" + FormatMs(state.StablePingMs) + " | " +
                "base=" + FormatMs(state.BaselinePingMs) + " | " +
                "floor=" + FormatMs(state.FloorPingMs) + " | " +
                "jit=" + FormatMs(state.JitterMs) + " | " +
                "delta=" + FormatSignedMs(delta) + " | " +
                "conf=" + Format01(state.Confidence) + " | " +
                "loss=" + FormatPct(state.LossRate) + " | " +
                "bad=" + FormatPct(state.BadQualityRate) + " | " +
                "spike=" + (state.SpikeDetected ? "1" : "0") + " | " +
                "stale=" + (isStale ? "1" : "0") + " | " +
                "div=" + FormatMs(state.PrimarySecondaryDivergenceMs) + " | " +
                "health=" + state.HealthState.ToString().ToLowerInvariant() + " | " +
                "primary=" + SafeText(state.PrimaryTarget) + " | " +
                "secondary=" + SafeText(state.SecondaryTarget);
        }

        private static string FormatMs(double value)
        {
            if (value <= 0)
                return "-1";

            return value.ToString("0.0") + "ms";
        }

        private static string FormatSignedMs(double value)
        {
            if (value == 0)
                return "0.0ms";

            return value.ToString("+0.0;-0.0;0.0") + "ms";
        }

        private static string FormatPct(double value)
        {
            if (value < 0)
                return "-1";

            return (value * 100.0).ToString("0.0") + "%";
        }

        private static string Format01(double value)
        {
            if (value < 0)
                return "-1";

            return value.ToString("0.00");
        }

        private static string FormatDuration(TimeSpan duration)
        {
            return duration.ToString(@"hh\:mm\:ss");
        }

        private static string SafeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private enum TrackerState
        {
            Healthy = 0,
            OffBaseline = 1,
            Warning = 2,
            Critical = 3
        }

        public sealed class FastPingAggregateSnapshot
        {
            public int SampleCount;
            public int ValidPingCount;
            public int ValidQueueCount;
            public int SpikeCount;
            public int StaleCount;

            public double AvgQueueDelayMs = -1;
            public double MinQueueDelayMs = -1;
            public double MaxQueueDelayMs = -1;
            public double P95QueueDelayMs = -1;
            public double QueueRangeMs = -1;

            public double InstantQueueDelayMs = -1;
            public double QueueGrowthMsPerSec = 0;
            public double QueueAccelerationMsPerSec2 = 0;

            public double QueueTrendConfidence = 0;
            public bool QueueBuilding;
            public bool QueueDraining;

            public double AvgPingMs = -1;
            public double MaxPingMs = -1;
            public double P95PingMs = -1;

            public double AvgJitterMs = -1;
            public double MinJitterMs = -1;
            public double MaxJitterMs = -1;
            public double P95JitterMs = -1;

            public double AvgLossRate = -1;
            public double AvgBadQualityRate = -1;
            public double AvgConfidence = -1;

            public double SpikeDensityPerMinute = -1;
            public double StaleRate = -1;
            public double LossBurstDensity = -1;
            public double BadTimeRatio = -1;
            public double WorstSegmentLoss = -1;

            public bool IsValid
            {
                get { return ValidPingCount >= 5; }
            }
            
            public bool HasUsableQueueTrend
            {
                get
                {
                    return
                        ValidQueueCount >= 5 &&
                        QueueTrendConfidence >= 0.45 &&
                        AvgQueueDelayMs >= 1.5; // <-- important
                }
            }
        }

        private sealed class RunningStats
        {
            private int sampleCount;
            private int instantCount;
            private int jitterCount;
            private int confidenceCount;
            private int lossCount;
            private int badQualityCount;

            private double instantSum;
            private double jitterSum;
            private double confidenceSum;
            private double lossSum;
            private double badQualitySum;

            public double AvgInstantMs { get; private set; }
            public double AvgJitterMs { get; private set; }
            public double AvgConfidence { get; private set; }
            public double AvgLossRate { get; private set; }
            public double AvgBadQualityRate { get; private set; }

            public double MaxInstantMs { get; private set; }
            public double MaxJitterMs { get; private set; }

            public int SpikeCount { get; private set; }
            public int StaleCount { get; private set; }

            public void Reset()
            {
                sampleCount = 0;
                instantCount = 0;
                jitterCount = 0;
                confidenceCount = 0;
                lossCount = 0;
                badQualityCount = 0;

                instantSum = 0;
                jitterSum = 0;
                confidenceSum = 0;
                lossSum = 0;
                badQualitySum = 0;

                AvgInstantMs = 0;
                AvgJitterMs = 0;
                AvgConfidence = 0;
                AvgLossRate = 0;
                AvgBadQualityRate = 0;

                MaxInstantMs = 0;
                MaxJitterMs = 0;

                SpikeCount = 0;
                StaleCount = 0;
            }

            public void Add(FastPingState state)
            {
                if (state == null)
                    return;

                sampleCount++;

                if (state.InstantPingMs > 0)
                {
                    instantSum += state.InstantPingMs;
                    instantCount++;

                    if (state.InstantPingMs > MaxInstantMs)
                        MaxInstantMs = state.InstantPingMs;
                }

                if (state.JitterMs >= 0)
                {
                    jitterSum += state.JitterMs;
                    jitterCount++;

                    if (state.JitterMs > MaxJitterMs)
                        MaxJitterMs = state.JitterMs;
                }

                if (state.Confidence >= 0)
                {
                    confidenceSum += state.Confidence;
                    confidenceCount++;
                }

                if (state.LossRate >= 0)
                {
                    lossSum += state.LossRate;
                    lossCount++;
                }

                if (state.BadQualityRate >= 0)
                {
                    badQualitySum += state.BadQualityRate;
                    badQualityCount++;
                }

                if (state.SpikeDetected)
                    SpikeCount++;

                if (state.IsStale || state.HealthState == FastPingHealthState.Stale)
                    StaleCount++;

                AvgInstantMs = instantCount > 0 ? instantSum / instantCount : 0;
                AvgJitterMs = jitterCount > 0 ? jitterSum / jitterCount : 0;
                AvgConfidence = confidenceCount > 0 ? confidenceSum / confidenceCount : 0;
                AvgLossRate = lossCount > 0 ? lossSum / lossCount : 0;
                AvgBadQualityRate = badQualityCount > 0 ? badQualitySum / badQualityCount : 0;
            }
        }

        private sealed class RollingWindowStats
        {
            private readonly TimeSpan windowDuration;
            private readonly Sample[] samples;
            private int nextIndex;
            private int count;

            public RollingWindowStats(TimeSpan windowDuration, int capacity)
            {
                this.windowDuration = windowDuration;
                samples = new Sample[capacity];
            }

            public void Reset()
            {
                nextIndex = 0;
                count = 0;
                Array.Clear(samples, 0, samples.Length);
            }

            public void Add(DateTime timeUtc, FastPingState state)
            {
                if (state == null)
                    return;

                double stablePingMs = state.StablePingMs > 0 ? state.StablePingMs : -1;
                double baselinePingMs = state.BaselinePingMs > 0 ? state.BaselinePingMs : -1;

                double queueDelayMs =
                    stablePingMs > 0 && baselinePingMs > 0
                        ? Math.Max(0.0, stablePingMs - baselinePingMs)
                        : -1;

                samples[nextIndex] = new Sample
                {
                    TimeUtc = timeUtc,
                    PingMs = state.StablePingMs > 0
                        ? state.StablePingMs
                        : (state.InstantPingMs > 0 ? state.InstantPingMs : -1),
                    StablePingMs = stablePingMs,
                    BaselinePingMs = baselinePingMs,
                    QueueDelayMs = queueDelayMs,
                    JitterMs = state.JitterMs,
                    LossRate = state.LossRate,
                    BadQualityRate = state.BadQualityRate,
                    Confidence = state.Confidence,
                    Spike = state.SpikeDetected,
                    Stale = state.IsStale || state.HealthState == FastPingHealthState.Stale
                };

                nextIndex = (nextIndex + 1) % samples.Length;

                if (count < samples.Length)
                    count++;
            }

            public FastPingAggregateSnapshot BuildSnapshot()
            {
                FastPingAggregateSnapshot snapshot = new FastPingAggregateSnapshot();
                if (count == 0)
                    return snapshot;

                DateTime cutoff = DateTime.UtcNow - windowDuration;

                double[] pingValues = new double[count];
                double[] jitterValues = new double[count];
                double[] queueValues = new double[count];
                Sample[] activeSamples = new Sample[count];

                int pingCount = 0;
                int jitterCount = 0;
                int queueCount = 0;
                int activeCount = 0;

                double pingSum = 0;
                double jitterSum = 0;
                double queueSum = 0;

                double lossSum = 0;
                double badSum = 0;
                double confidenceSum = 0;

                int considered = 0;
                int spikeCount = 0;
                int staleCount = 0;

                double maxPing = -1;
                double minJitter = -1;
                double maxJitter = -1;

                double minQueue = -1;
                double maxQueue = -1;

                double latestQueue = -1;
                DateTime latestQueueTimeUtc = DateTime.MinValue;

                for (int i = 0; i < count; i++)
                {
                    Sample s = samples[i];
                    if (s == null)
                        continue;

                    if (s.TimeUtc < cutoff)
                        continue;

                    activeSamples[activeCount++] = s;
                    considered++;

                    if (s.PingMs > 0)
                    {
                        pingValues[pingCount] = s.PingMs;
                        pingCount++;
                        pingSum += s.PingMs;

                        if (s.PingMs > maxPing)
                            maxPing = s.PingMs;
                    }

                    if (s.JitterMs >= 0)
                    {
                        jitterValues[jitterCount] = s.JitterMs;
                        jitterCount++;
                        jitterSum += s.JitterMs;

                        if (minJitter < 0 || s.JitterMs < minJitter)
                            minJitter = s.JitterMs;

                        if (maxJitter < 0 || s.JitterMs > maxJitter)
                            maxJitter = s.JitterMs;
                    }

                    if (s.QueueDelayMs >= 0)
                    {
                        queueValues[queueCount] = s.QueueDelayMs;
                        queueCount++;
                        queueSum += s.QueueDelayMs;

                        if (minQueue < 0 || s.QueueDelayMs < minQueue)
                            minQueue = s.QueueDelayMs;

                        if (maxQueue < 0 || s.QueueDelayMs > maxQueue)
                            maxQueue = s.QueueDelayMs;

                        if (latestQueueTimeUtc == DateTime.MinValue || s.TimeUtc > latestQueueTimeUtc)
                        {
                            latestQueue = s.QueueDelayMs;
                            latestQueueTimeUtc = s.TimeUtc;
                        }
                    }

                    if (s.LossRate >= 0)
                        lossSum += s.LossRate;

                    if (s.BadQualityRate >= 0)
                        badSum += s.BadQualityRate;

                    if (s.Confidence >= 0)
                        confidenceSum += s.Confidence;

                    if (s.Spike)
                        spikeCount++;

                    if (s.Stale)
                        staleCount++;
                }

                snapshot.SampleCount = considered;
                snapshot.ValidPingCount = pingCount;
                snapshot.ValidQueueCount = queueCount;
                snapshot.SpikeCount = spikeCount;
                snapshot.StaleCount = staleCount;

                snapshot.AvgPingMs = pingCount > 0 ? pingSum / pingCount : -1;
                snapshot.MaxPingMs = maxPing;
                snapshot.P95PingMs = ComputePercentile(pingValues, pingCount, 0.95);

                snapshot.AvgJitterMs = jitterCount > 0 ? jitterSum / jitterCount : -1;
                snapshot.MinJitterMs = minJitter;
                snapshot.MaxJitterMs = maxJitter;
                snapshot.P95JitterMs = ComputePercentile(jitterValues, jitterCount, 0.95);

                snapshot.AvgQueueDelayMs = queueCount > 0 ? queueSum / queueCount : -1;
                snapshot.MinQueueDelayMs = minQueue;
                snapshot.MaxQueueDelayMs = maxQueue;
                snapshot.P95QueueDelayMs = ComputePercentile(queueValues, queueCount, 0.95);
                snapshot.QueueRangeMs =
                    (minQueue >= 0 && maxQueue >= 0)
                        ? (maxQueue - minQueue)
                        : -1;
                snapshot.InstantQueueDelayMs = latestQueue;

                snapshot.AvgLossRate = considered > 0 ? lossSum / considered : -1;
                snapshot.AvgBadQualityRate = considered > 0 ? badSum / considered : -1;
                snapshot.AvgConfidence = considered > 0 ? confidenceSum / considered : -1;
                snapshot.StaleRate = considered > 0 ? (double)staleCount / considered : -1;

                double windowMinutes = windowDuration.TotalMinutes;
                snapshot.SpikeDensityPerMinute = windowMinutes > 0 ? spikeCount / windowMinutes : -1;

                snapshot.QueueGrowthMsPerSec = ComputeQueueSlopeMsPerSec(cutoff);
                snapshot.QueueAccelerationMsPerSec2 = ComputeQueueAccelerationMsPerSec2(cutoff);
                ComputeStabilityMetrics(activeSamples, activeCount, out double lossBurstDensity, out double badTimeRatio, out double worstSegmentLoss);
                snapshot.LossBurstDensity = lossBurstDensity;
                snapshot.BadTimeRatio = badTimeRatio;
                snapshot.WorstSegmentLoss = worstSegmentLoss;

                snapshot.QueueTrendConfidence = ComputeQueueTrendConfidence(
                    considered,
                    queueCount,
                    snapshot.AvgConfidence,
                    snapshot.StaleRate,
                    snapshot.AvgLossRate,
                    snapshot.AvgQueueDelayMs);

                if (snapshot.QueueTrendConfidence > 0)
                {
                    double trustScale = Math.Max(0.20, snapshot.QueueTrendConfidence);
                    snapshot.QueueGrowthMsPerSec *= trustScale;
                    snapshot.QueueAccelerationMsPerSec2 *= trustScale;
                }

                snapshot.QueueBuilding =
                    snapshot.HasUsableQueueTrend &&
                    snapshot.QueueGrowthMsPerSec >= 3.0;

                snapshot.QueueDraining =
                    snapshot.HasUsableQueueTrend &&
                    snapshot.QueueGrowthMsPerSec <= -4.0 &&
                    snapshot.AvgJitterMs >= 0 &&
                    snapshot.AvgJitterMs < 12.0 &&
                    snapshot.AvgLossRate >= 0 &&
                    snapshot.AvgLossRate < 0.10;

                return snapshot;
            }

            private static void ComputeStabilityMetrics(
                Sample[] activeSamples,
                int activeCount,
                out double lossBurstDensity,
                out double badTimeRatio,
                out double worstSegmentLoss)
            {
                lossBurstDensity = -1;
                badTimeRatio = -1;
                worstSegmentLoss = -1;

                if (activeSamples == null || activeCount <= 0)
                    return;

                Array.Sort(activeSamples, 0, activeCount, SampleTimeComparer.Instance);

                DateTime firstTime = activeSamples[0].TimeUtc;
                DateTime lastTime = activeSamples[activeCount - 1].TimeUtc;
                double windowMs = Math.Max(1.0, (lastTime - firstTime).TotalMilliseconds);
                double windowSeconds = Math.Max(1.0, windowMs / 1000.0);

                int burstCount = 0;
                int lossStreak = 0;
                bool inBurst = false;

                double badTimeMs = 0.0;
                int segmentCount = Math.Max(1, (int)Math.Ceiling(windowMs / 2000.0));
                double[] segmentLossSum = new double[segmentCount];
                int[] segmentLossSamples = new int[segmentCount];

                for (int i = 0; i < activeCount; i++)
                {
                    Sample s = activeSamples[i];
                    bool lossObserved = s.LossRate > 0.0;

                    if (lossObserved)
                    {
                        lossStreak++;
                        if (lossStreak >= 2 && !inBurst)
                        {
                            burstCount++;
                            inBurst = true;
                        }
                    }
                    else
                    {
                        lossStreak = 0;
                        inBurst = false;
                    }

                    DateTime segmentEndTime = i + 1 < activeCount
                        ? activeSamples[i + 1].TimeUtc
                        : lastTime;
                    double sampleSpanMs = Math.Max(0.0, (segmentEndTime - s.TimeUtc).TotalMilliseconds);
                    if (i == activeCount - 1 && sampleSpanMs <= 0.0)
                        sampleSpanMs = Math.Min(2000.0, windowMs);

                    bool badState = (s.LossRate > 0.02) || (s.JitterMs >= WarningJitterMs);
                    if (badState)
                        badTimeMs += sampleSpanMs;

                    if (s.LossRate >= 0)
                    {
                        int segmentIndex = (int)((s.TimeUtc - firstTime).TotalMilliseconds / 2000.0);
                        if (segmentIndex < 0)
                            segmentIndex = 0;
                        if (segmentIndex >= segmentCount)
                            segmentIndex = segmentCount - 1;

                        segmentLossSum[segmentIndex] += s.LossRate;
                        segmentLossSamples[segmentIndex]++;
                    }
                }

                double worstLoss = -1;
                for (int i = 0; i < segmentCount; i++)
                {
                    if (segmentLossSamples[i] <= 0)
                        continue;

                    double segmentLoss = segmentLossSum[i] / segmentLossSamples[i];
                    if (segmentLoss > worstLoss)
                        worstLoss = segmentLoss;
                }

                lossBurstDensity = (burstCount / windowSeconds) * 60.0;
                badTimeRatio = Math.Max(0.0, Math.Min(1.0, badTimeMs / windowMs));
                worstSegmentLoss = worstLoss;
            }

            private sealed class SampleTimeComparer : System.Collections.Generic.IComparer<Sample>
            {
                public static readonly SampleTimeComparer Instance = new SampleTimeComparer();

                public int Compare(Sample x, Sample y)
                {
                    return x.TimeUtc.CompareTo(y.TimeUtc);
                }
            }

            private double ComputeQueueSlopeMsPerSec(DateTime cutoff)
            {
                double sumX = 0;
                double sumY = 0;
                double sumXY = 0;
                double sumX2 = 0;
                int n = 0;

                DateTime firstTimeUtc = DateTime.MinValue;

                for (int i = 0; i < count; i++)
                {
                    Sample s = samples[i];
                    if (s == null || s.TimeUtc < cutoff || s.QueueDelayMs < 0)
                        continue;

                    if (firstTimeUtc == DateTime.MinValue)
                        firstTimeUtc = s.TimeUtc;

                    double x = (s.TimeUtc - firstTimeUtc).TotalSeconds;
                    double y = s.QueueDelayMs;

                    sumX += x;
                    sumY += y;
                    sumXY += x * y;
                    sumX2 += x * x;
                    n++;
                }

                if (n < 4)
                    return 0;

                double denominator = (n * sumX2) - (sumX * sumX);
                if (Math.Abs(denominator) < 0.0001)
                    return 0;

                return ((n * sumXY) - (sumX * sumY)) / denominator;
            }

            private double ComputeQueueAccelerationMsPerSec2(DateTime cutoff)
            {
                double sumX = 0;
                double sumY = 0;
                double sumXY = 0;
                double sumX2 = 0;
                int n = 0;

                DateTime firstTimeUtc = DateTime.MinValue;
                double lastQueue = -1;
                double lastTimeSec = 0;

                for (int i = 0; i < count; i++)
                {
                    Sample s = samples[i];
                    if (s == null || s.TimeUtc < cutoff || s.QueueDelayMs < 0)
                        continue;

                    if (firstTimeUtc == DateTime.MinValue)
                        firstTimeUtc = s.TimeUtc;

                    double t = (s.TimeUtc - firstTimeUtc).TotalSeconds;

                    if (lastQueue >= 0)
                    {
                        double dt = t - lastTimeSec;
                        if (dt > 0.0001)
                        {
                            double slope = (s.QueueDelayMs - lastQueue) / dt;

                            sumX += t;
                            sumY += slope;
                            sumXY += t * slope;
                            sumX2 += t * t;
                            n++;
                        }
                    }

                    lastQueue = s.QueueDelayMs;
                    lastTimeSec = t;
                }

                if (n < 4)
                    return 0;

                double denominator = (n * sumX2) - (sumX * sumX);
                if (Math.Abs(denominator) < 0.0001)
                    return 0;

                return ((n * sumXY) - (sumX * sumY)) / denominator;
            }

            private static double ComputeQueueTrendConfidence(
                int sampleCount,
                int queueCount,
                double avgConfidence,
                double staleRate,
                double avgLossRate,
                double avgQueueDelayMs)
            {
                if (sampleCount <= 0 || queueCount < 4)
                    return 0;

                double confidence = 1.0;

                if (queueCount < 6)
                    confidence *= 0.70;
                else if (queueCount < 10)
                    confidence *= 0.85;

                if (avgConfidence >= 0)
                    confidence *= Math.Max(0.25, avgConfidence);

                if (staleRate >= 0)
                    confidence *= Math.Max(0.20, 1.0 - (staleRate * 0.85));

                if (avgLossRate >= 0)
                    confidence *= Math.Max(0.35, 1.0 - (avgLossRate * 1.2));

                if (avgQueueDelayMs >= 0 && avgQueueDelayMs < 1.5)
                    confidence *= 0.35;
                else if (avgQueueDelayMs >= 0 && avgQueueDelayMs < 3.0)
                    confidence *= 0.70;

                if (confidence < 0)
                    confidence = 0;
                if (confidence > 1)
                    confidence = 1;

                return confidence;
            }

            private static double ComputePercentile(double[] values, int count, double percentile)
            {
                if (count <= 0)
                    return -1;

                if (count == 1)
                    return values[0];

                double[] copy = new double[count];
                Array.Copy(values, copy, count);
                Array.Sort(copy);

                double rawIndex = (count - 1) * percentile;
                int low = (int)Math.Floor(rawIndex);
                int high = (int)Math.Ceiling(rawIndex);

                if (low == high)
                    return copy[low];

                double fraction = rawIndex - low;
                return copy[low] + ((copy[high] - copy[low]) * fraction);
            }

            private sealed class Sample
            {
                public DateTime TimeUtc;
                public double PingMs;
                public double StablePingMs;
                public double BaselinePingMs;
                public double QueueDelayMs;
                public double JitterMs;
                public double LossRate;
                public double BadQualityRate;
                public double Confidence;
                public bool Spike;
                public bool Stale;
            }
        }
    }
}
