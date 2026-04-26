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
            public int LossCount;
            public int JitterCount;
            public int PingCount;
            public int SpikeCount;

            public double LossSum;
            public double JitterSum;
            public double PingSum;
            public double SpikeSum;

            public void Observe(FastPingSignalAnalyzer.FastPingAggregateSnapshot window)
            {
                if (window == null || !window.IsValid)
                    return;

                if (window.AvgLossRate >= 0)
                {
                    LossSum += window.AvgLossRate;
                    LossCount++;
                }

                if (window.AvgJitterMs >= 0)
                {
                    JitterSum += window.AvgJitterMs;
                    JitterCount++;
                }

                if (window.AvgPingMs >= 0)
                {
                    PingSum += window.AvgPingMs;
                    PingCount++;
                }

                if (window.SpikeDensityPerMinute >= 0)
                {
                    SpikeSum += window.SpikeDensityPerMinute;
                    SpikeCount++;
                }
            }

            public bool HasAnyData
            {
                get
                {
                    return LossCount > 0 ||
                           JitterCount > 0 ||
                           PingCount > 0 ||
                           SpikeCount > 0;
                }
            }

            public double AvgLossRate
            {
                get { return LossCount > 0 ? (LossSum / LossCount) : -1; }
            }

            public double AvgJitterMs
            {
                get { return JitterCount > 0 ? (JitterSum / JitterCount) : -1; }
            }

            public double AvgPingMs
            {
                get { return PingCount > 0 ? (PingSum / PingCount) : -1; }
            }

            public double AvgSpikeDensityPerMinute
            {
                get { return SpikeCount > 0 ? (SpikeSum / SpikeCount) : -1; }
            }
        }

        private readonly StartupProbeTimingConfig timing;
        private readonly Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot;
        private readonly CandidateUsableEvaluator isCandidateUsableForMeasurement;
        private readonly Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, DateTime, Task<ProbeCandidateRuntimeData>> captureProbeAsync;
        private readonly Func<ProbeCandidateRuntimeData, ProbeCandidateRuntimeData, TimeSpan, TimeSpan, ProbeCandidateRuntimeData> mergeMeasuredRuntime;
        private readonly Func<string, string, string> appendNote;
        private readonly Action<string> log;
        private const int MainWindowSliceCount = 10;

        public StartupRawProbe(
            StartupProbeTimingConfig timing,
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> getShortWindowSnapshot,
            CandidateUsableEvaluator isCandidateUsableForMeasurement,
            Func<ProbeCandidateDefinition, TimeSpan, TimeSpan, DateTime, Task<ProbeCandidateRuntimeData>> captureProbeAsync,
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
                    log?.Invoke("startup-probe | raw stable-start check | checkpoint=" +
                        check1.TotalSeconds.ToString("0.#") + "s | usable=" + (usableNow ? "yes" : "no") +
                        " | reason=" + reasonNow);
                    if (usableNow)
                        break;
                }

                if (!check2Done && nowUtc >= check2Utc)
                {
                    check2Done = true;
                    log?.Invoke("startup-probe | raw stable-start check | checkpoint=" +
                        check2.TotalSeconds.ToString("0.#") + "s | usable=" + (usableNow ? "yes" : "no") +
                        " | reason=" + reasonNow);
                    if (usableNow)
                        break;
                }

                await Task.Delay(rawPollInterval, cancellationToken);
            }

            bool forceUsable = isCandidateUsableForMeasurement(candidate, out string forceReason);
            log?.Invoke("startup-probe | raw stable-start check | checkpoint=" +
                forceStart.TotalSeconds.ToString("0.#") + "s | usable=" + (forceUsable ? "yes" : "no") +
                " | reason=" + forceReason);

            log?.Invoke("startup-probe | raw measurement start | durationMs=" + rawDuration.TotalMilliseconds.ToString("0"));

            List<TimeSpan> measuredSlices = BuildMainWindowSlices(rawDuration);
            ProbeCandidateRuntimeData aggregateRuntime = null;
            TimeSpan accumulatedMeasured = TimeSpan.Zero;

            for (int i = 0; i < measuredSlices.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                TimeSpan slice = measuredSlices[i];
                DateTime sliceStartUtc = DateTime.UtcNow;
                ProbeCandidateRuntimeData sliceRuntime = await captureProbeAsync(
                    candidate,
                    TimeSpan.Zero,
                    slice,
                    sliceStartUtc);

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
            aggregateRuntime.StabilizationReason = "raw-fixed-settle-direct-measure";

            ApplyRawPreMeasureInfluence(aggregateRuntime, preMeasureSignal);

            return aggregateRuntime;
        }

        private void ApplyRawPreMeasureInfluence(
            ProbeCandidateRuntimeData runtime,
            RawPreMeasureSignal preMeasureSignal)
        {
            if (runtime == null || !preMeasureSignal.HasAnyData)
                return;

            double preLoss = preMeasureSignal.AvgLossRate;
            double preJitter = preMeasureSignal.AvgJitterMs;
            double prePing = preMeasureSignal.AvgPingMs;
            double preSpike = preMeasureSignal.AvgSpikeDensityPerMinute;

            double measuredPing = runtime.AvgPingMs;
            double measuredJitter = runtime.AvgJitterMs;
            double measuredLoss = runtime.AvgLossRate;
            double measuredSpike = runtime.SpikeDensityPerMinute;

            if (measuredPing >= 0 && prePing > 0)
                runtime.AvgPingMs = (measuredPing * 0.95) + (prePing * 0.05);

            if (measuredJitter >= 0 && preJitter > 0)
                runtime.AvgJitterMs = (measuredJitter * 0.95) + (preJitter * 0.05);

            if (measuredLoss < 0 || preLoss < 0)
                return;

            bool uglyLoss = preLoss > measuredLoss + 0.08;
            bool uglyJitter = measuredJitter > 0 && preJitter > measuredJitter * 1.6;
            bool uglySpike =
                preSpike >= 0 &&
                measuredSpike >= 0 &&
                (preSpike > measuredSpike + 12.0 || (measuredSpike > 0 && preSpike > measuredSpike * 1.75));

            bool earlyUgly = uglyLoss || uglyJitter || uglySpike;

            bool earlyExcellent =
                preLoss >= 0 && preLoss <= 0.02 &&
                preJitter > 0 && preJitter <= 6.0 &&
                prePing > 0 &&
                measuredPing > 0 &&
                prePing <= measuredPing + 2.0 &&
                measuredLoss >= 0 && measuredLoss <= 0.04 &&
                measuredJitter > 0 && measuredJitter <= 8.0 &&
                (preSpike < 0 || preSpike <= 4.0) &&
                (measuredSpike < 0 || measuredSpike <= 6.0);

            double lossAdjustment = 0.0;
            if (earlyUgly)
                lossAdjustment = 0.006;
            else if (earlyExcellent)
                lossAdjustment = -0.003;

            if (lossAdjustment == 0.0)
                return;

            runtime.AvgLossRate = Math.Max(0.0, measuredLoss + lossAdjustment);
            runtime.StartupAdjustedLossRate = runtime.AvgLossRate;

            if (earlyUgly)
                runtime.Notes = appendNote(runtime.Notes, "raw-premeasure-small-penalty");
            else if (earlyExcellent)
                runtime.Notes = appendNote(runtime.Notes, "raw-premeasure-tiny-boost");
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
            long baseSliceTicks = totalTicks / MainWindowSliceCount;
            long remainderTicks = totalTicks % MainWindowSliceCount;

            for (int i = 0; i < MainWindowSliceCount; i++)
            {
                long sliceTicks = baseSliceTicks;

                if (i == MainWindowSliceCount - 1)
                    sliceTicks += remainderTicks;

                slices.Add(TimeSpan.FromTicks(sliceTicks));
            }

            return slices;
        }
    }
}
