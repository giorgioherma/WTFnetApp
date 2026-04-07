using System;

namespace WTFnetApp
{
    public sealed class FastPingDeviationTracker
    {
        private const int StartConfirmSamples = 3;
        private const int RecoveryConfirmSamples = 6;
        private const int OngoingSummarySeconds = 30;
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

        private bool baselineLogged;

        private readonly RunningStats ongoingStats = new RunningStats();

        public void Reset()
        {
            currentState = TrackerState.Healthy;
            pendingState = TrackerState.Healthy;
            pendingStateSamples = 0;
            recoverySamples = 0;
            stateStartUtc = DateTime.MinValue;
            lastSummaryUtc = DateTime.MinValue;
            lastSampleUtc = DateTime.MinValue;
            baselineLogged = false;
            ongoingStats.Reset();
        }

        public void Update(FastPingState state)
        {
            if (state == null)
                return;

            lastSampleUtc = DateTime.UtcNow;

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
            {
                EnterState(state, observedState);
            }
        }

        private void HandleNonHealthyState(FastPingState state, TrackerState observedState)
        {
            ongoingStats.Add(state);

            if (observedState == TrackerState.Healthy)
            {
                recoverySamples++;

                if (recoverySamples >= RecoveryConfirmSamples)
                {
                    ExitToHealthy(state);
                }
                else
                {
                    MaybeLogOngoingSummary(state);
                }

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
                {
                    TransitionState(state, observedState);
                }
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

            if (deltaFromBaseline >= WarningSpikeDeltaMs || jitter >= WarningJitterMs || state.SpikeDetected)
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

        private sealed class RunningStats
        {
            private int sampleCount;
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
                    if (state.InstantPingMs > MaxInstantMs)
                        MaxInstantMs = state.InstantPingMs;
                }

                if (state.JitterMs > 0)
                {
                    jitterSum += state.JitterMs;
                    if (state.JitterMs > MaxJitterMs)
                        MaxJitterMs = state.JitterMs;
                }

                confidenceSum += state.Confidence;
                lossSum += state.LossRate;
                badQualitySum += state.BadQualityRate;

                if (state.SpikeDetected)
                    SpikeCount++;

                if (state.IsStale || state.HealthState == FastPingHealthState.Stale)
                    StaleCount++;

                AvgInstantMs = instantSum / sampleCount;
                AvgJitterMs = jitterSum / sampleCount;
                AvgConfidence = confidenceSum / sampleCount;
                AvgLossRate = lossSum / sampleCount;
                AvgBadQualityRate = badQualitySum / sampleCount;
            }
        }
    }
}