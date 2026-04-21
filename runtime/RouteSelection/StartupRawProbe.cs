using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WTFnetApp
{
    internal sealed class StartupRawProbe
    {
        private struct RawPreMeasureSignal
        {
            public int Count;
            public double LossSum;
            public double JitterSum;
            public double PingSum;
            public double SpikeSum;

            public void Observe(FastPingSignalAnalyzer.FastPingAggregateSnapshot window)
            {
                if (window == null || !window.IsValid)
                    return;

                Count++;
                if (window.AvgLossRate >= 0)
                    LossSum += window.AvgLossRate;
                if (window.AvgJitterMs >= 0)
                    JitterSum += window.AvgJitterMs;
                if (window.AvgPingMs >= 0)
                    PingSum += window.AvgPingMs;
                if (window.SpikeDensityPerMinute >= 0)
                    SpikeSum += window.SpikeDensityPerMinute;
            }
        }

        private readonly StartupProbeTimingConfig timing;
        private readonly Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot;
        private readonly CandidateUsableEvaluator isCandidateUsableForMeasurement;
        private readonly Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, Task<ProbeCandidateRuntimeData>> captureProbeAsync;
        private readonly Func<ProbeCandidateRuntimeData, ProbeCandidateRuntimeData, TimeSpan, TimeSpan, ProbeCandidateRuntimeData> mergeMeasuredRuntime;
        private readonly Func<string, string, string> appendNote;
        private readonly Action<string> log;
        private const int MainWindowSliceCount = 10;

        public StartupRawProbe(
            StartupProbeTimingConfig timing,
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
            CandidateUsableEvaluator isCandidateUsableForMeasurement,
            Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, Task<ProbeCandidateRuntimeData>> captureProbeAsync,
            Func<ProbeCandidateRuntimeData, ProbeCandidateRuntimeData, TimeSpan, TimeSpan, ProbeCandidateRuntimeData> mergeMeasuredRuntime,
            Func<string, string, string> appendNote,
            Action<string> log)
        {
            this.timing = timing;
            this.getShortWindowSnapshot = getShortWindowSnapshot;
            this.isCandidateUsableForMeasurement = isCandidateUsableForMeasurement;
            this.captureProbeAsync = captureProbeAsync;
            this.mergeMeasuredRuntime = mergeMeasuredRuntime;
            this.appendNote = appendNote;
            this.log = log;
        }

        public async Task<ProbeCandidateRuntimeData> RunAsync(
            ProbeCandidateDefinition candidate,
            string timeBucketKey,
            CancellationToken cancellationToken)
        {
            TimeSpan fixedSettle = timing.RoutedFixedSettleDuration > TimeSpan.Zero
                ? timing.RoutedFixedSettleDuration
                : TimeSpan.FromSeconds(6.0);
            TimeSpan check1 = timing.RoutedStableGateCheck1 > TimeSpan.Zero
                ? timing.RoutedStableGateCheck1
                : TimeSpan.FromSeconds(4.0);
            TimeSpan check2 = timing.RoutedStableGateCheck2 > check1
                ? timing.RoutedStableGateCheck2
                : TimeSpan.FromSeconds(6.0);
            TimeSpan forceStart = timing.RoutedStableGateForceStart > check2
                ? timing.RoutedStableGateForceStart
                : TimeSpan.FromSeconds(8.0);

            TimeSpan rawDuration = timing.RawMeasuredDuration > TimeSpan.Zero
                ? timing.RawMeasuredDuration
                : TimeSpan.FromSeconds(18.0);

            DateTime waitStartUtc = DateTime.UtcNow;
            DateTime fixedSettleEndUtc = waitStartUtc + fixedSettle;
            DateTime check1Utc = fixedSettleEndUtc + check1;
            DateTime check2Utc = fixedSettleEndUtc + check2;
            DateTime forceUtc = fixedSettleEndUtc + forceStart;
            DateTime nextWaitLogUtc = waitStartUtc;
            TimeSpan rawPollInterval = timing.SpeedifyTelemetryCheckInterval > TimeSpan.Zero
                ? timing.SpeedifyTelemetryCheckInterval
                : TimeSpan.FromMilliseconds(250);

            RawPreMeasureSignal preMeasureSignal = new RawPreMeasureSignal();

            while (DateTime.UtcNow < fixedSettleEndUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FastPingSignalAnalyzer.FastPingAggregateSnapshot window = getShortWindowSnapshot();
                preMeasureSignal.Observe(window);

                bool usableNow = isCandidateUsableForMeasurement(candidate, out string reasonNow);
                if (DateTime.UtcNow >= nextWaitLogUtc)
                {
                    log?.Invoke("startup-probe | raw fixed-settle waiting | reason=" + reasonNow +
                        " | usable=" + (usableNow ? "yes" : "no") +
                        " | remainingMs=" + Math.Max(0.0, (fixedSettleEndUtc - DateTime.UtcNow).TotalMilliseconds).ToString("0"));
                    nextWaitLogUtc = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                }

                await Task.Delay(rawPollInterval, cancellationToken);
            }

            bool check1Done = false;
            bool check2Done = false;

            while (DateTime.UtcNow < forceUtc)
            {
                cancellationToken.ThrowIfCancellationRequested();

                FastPingSignalAnalyzer.FastPingAggregateSnapshot window = getShortWindowSnapshot();
                preMeasureSignal.Observe(window);

                bool usableNow = isCandidateUsableForMeasurement(candidate, out string reasonNow);
                DateTime nowUtc = DateTime.UtcNow;

                if (!check1Done && nowUtc >= check1Utc)
                {
                    check1Done = true;
                    log?.Invoke("startup-probe | raw stable-start check | checkpoint=4s | usable=" + (usableNow ? "yes" : "no") +
                        " | reason=" + reasonNow);
                    if (usableNow)
                        break;
                }

                if (!check2Done && nowUtc >= check2Utc)
                {
                    check2Done = true;
                    log?.Invoke("startup-probe | raw stable-start check | checkpoint=6s | usable=" + (usableNow ? "yes" : "no") +
                        " | reason=" + reasonNow);
                    if (usableNow)
                        break;
                }

                await Task.Delay(rawPollInterval, cancellationToken);
            }

            bool forceUsable = isCandidateUsableForMeasurement(candidate, out string forceReason);
            log?.Invoke("startup-probe | raw stable-start check | checkpoint=8s | usable=" + (forceUsable ? "yes" : "no") +
                " | reason=" + forceReason);
            log?.Invoke("startup-probe | raw measurement start | durationMs=" + rawDuration.TotalMilliseconds.ToString("0"));

            List<TimeSpan> measuredSlices = BuildMainWindowSlices(rawDuration);
            ProbeCandidateRuntimeData aggregateRuntime = null;
            TimeSpan accumulatedMeasured = TimeSpan.Zero;

            for (int i = 0; i < measuredSlices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan slice = measuredSlices[i];
                ProbeCandidateRuntimeData sliceRuntime = await captureProbeAsync(
                    candidate,
                    TimeSpan.Zero,
                    slice);

                if (sliceRuntime == null)
                    return aggregateRuntime;

                sliceRuntime.Candidate = sliceRuntime.Candidate ?? candidate;
                sliceRuntime.TimeBucketKey = timeBucketKey;
                sliceRuntime.ReachedUsableState = true;
                sliceRuntime.MeasurementStarted = true;
                sliceRuntime.MeasurementCompleted = false;
                sliceRuntime.MeasurementStoppedEarly = false;
                sliceRuntime.Notes = appendNote(sliceRuntime.Notes, "measured-raw-direct");

                aggregateRuntime = aggregateRuntime == null
                    ? sliceRuntime
                    : mergeMeasuredRuntime(aggregateRuntime, sliceRuntime, accumulatedMeasured, slice);

                accumulatedMeasured += slice;

                if (ProbeScoring.TryGetMeasuredCatastrophicStopReason(aggregateRuntime, out string stopReason))
                {
                    aggregateRuntime.MeasurementStoppedEarly = true;
                    aggregateRuntime.MeasurementCompleted = false;
                    aggregateRuntime.MeasurementStopReason = stopReason;
                    aggregateRuntime.DiscardReason = stopReason;
                    aggregateRuntime.Notes = appendNote(aggregateRuntime.Notes, stopReason);
                    return aggregateRuntime;
                }
            }

            if (aggregateRuntime == null)
                return null;

            aggregateRuntime.ReachedUsableState = true;
            aggregateRuntime.MeasurementStarted = true;
            aggregateRuntime.MeasurementCompleted = true;
            aggregateRuntime.MeasurementStoppedEarly = false;
            aggregateRuntime.StartupAdjustedLossRate = -1;
            aggregateRuntime.StabilizationReached = false;
            aggregateRuntime.StabilizationTimedOut = false;
            aggregateRuntime.StabilizationReason = "raw-legacy-gated-measure";

            ApplyRawPreMeasureInfluence(aggregateRuntime, preMeasureSignal);

            return aggregateRuntime;
        }

        private void ApplyRawPreMeasureInfluence(
            ProbeCandidateRuntimeData runtime,
            RawPreMeasureSignal preMeasureSignal)
        {
            if (runtime == null || preMeasureSignal.Count <= 0)
                return;

            double preLoss = preMeasureSignal.LossSum / preMeasureSignal.Count;
            double preJitter = preMeasureSignal.JitterSum / preMeasureSignal.Count;
            double prePing = preMeasureSignal.PingSum / preMeasureSignal.Count;

            if (runtime.AvgPingMs >= 0 && prePing > 0)
                runtime.AvgPingMs = (runtime.AvgPingMs * 0.95) + (prePing * 0.05);

            if (runtime.AvgJitterMs >= 0 && preJitter > 0)
                runtime.AvgJitterMs = (runtime.AvgJitterMs * 0.95) + (preJitter * 0.05);

            if (runtime.AvgLossRate >= 0 && preLoss >= 0)
            {
                double lossAdjustment = 0.0;
                bool earlyUgly =
                    preLoss > runtime.AvgLossRate + 0.08 ||
                    (runtime.AvgJitterMs > 0 && preJitter > runtime.AvgJitterMs * 1.6);
                bool earlyExcellent =
                    runtime.AvgLossRate > preLoss + 0.08 &&
                    (runtime.AvgJitterMs > 0 && preJitter < runtime.AvgJitterMs * 0.75);

                if (earlyUgly)
                    lossAdjustment = 0.006;
                else if (earlyExcellent)
                    lossAdjustment = -0.003;

                runtime.AvgLossRate = Math.Max(0.0, runtime.AvgLossRate + lossAdjustment);
                runtime.StartupAdjustedLossRate = runtime.AvgLossRate;

                if (earlyUgly)
                    runtime.Notes = appendNote(runtime.Notes, "raw-premeasure-small-penalty");
                else if (earlyExcellent)
                    runtime.Notes = appendNote(runtime.Notes, "raw-premeasure-tiny-boost");
            }
        }

        private static List<TimeSpan> BuildMainWindowSlices(TimeSpan totalDuration)
        {
            List<TimeSpan> slices = new List<TimeSpan>();

            if (totalDuration <= TimeSpan.Zero)
            {
                slices.Add(TimeSpan.Zero);
                return slices;
            }

            long totalTicks = totalDuration.Ticks;
            long consumedTicks = 0;

            for (int i = 0; i < MainWindowSliceCount; i++)
            {
                long remainingTicks = totalTicks - consumedTicks;
                int remainingSlices = MainWindowSliceCount - i;

                long sliceTicks = remainingSlices <= 1
                    ? remainingTicks
                    : totalTicks / MainWindowSliceCount;

                if (sliceTicks <= 0)
                    sliceTicks = remainingTicks;

                slices.Add(TimeSpan.FromTicks(sliceTicks));
                consumedTicks += sliceTicks;
            }

            return slices.Where(x => x >= TimeSpan.Zero).ToList();
        }
    }
}
