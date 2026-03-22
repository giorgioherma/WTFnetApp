using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace WTFnetApp
{
    public sealed class WtfFastPing
    {
        private const int ProbeIntervalMs = 125;          // loop cadence
        private const int PerTargetPeriodMs = 250;        // each target gets probed every 250 ms
        private const int BaseProbeTimeoutMs = 300;       // internal deadline only, never shown as ping
        private const int MaxProbeTimeoutMs = 450;
        private const int StaleAfterMs = 1200;
        private const int MissesBeforeDegrade = 3;
        private const double StableAlpha = 0.35;
        private const double SpikeDeltaMs = 15.0;
        private const double FloorSpikeDeltaMs = 18.0;

        private static readonly byte[] Payload = new byte[12] { 87, 84, 70, 70, 65, 83, 84, 80, 73, 78, 71, 33 };

        private readonly object sync = new object();

        private readonly ProbeTargetState primary = new ProbeTargetState("primary");
        private readonly ProbeTargetState secondary = new ProbeTargetState("secondary");

        private readonly Ping pingPrimary = new Ping();
        private readonly Ping pingSecondary = new Ping();

        private volatile bool running;
        private Task loopTask;

        private string secondaryFallbackHost = "1.1.1.1";
        private double tunnelLatencyMs = -1;

        private FastPingState state = new FastPingState();

        public void Start()
        {
            if (running)
                return;

            running = true;
            loopTask = RunLoop();
            Logger.Log("fastping", "probe started");
        }

        public async Task StopAsync()
        {
            running = false;

            try
            {
                if (loopTask != null)
                    await loopTask;
            }
            catch
            {
            }

            try { pingPrimary.Dispose(); } catch { }
            try { pingSecondary.Dispose(); } catch { }

            Logger.Log("fastping", "probe stopped");
        }

        public void UpdateTargets(string primaryHost, string secondaryHost = "1.1.1.1")
        {
            lock (sync)
            {
                if (!string.IsNullOrWhiteSpace(primaryHost) && !string.Equals(primary.Host, primaryHost, StringComparison.OrdinalIgnoreCase))
                {
                    primary.Reset(primaryHost);
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
                    Logger.Log("fastping", "secondary target -> " + resolvedSecondary);
                }
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

        private async Task RunLoop()
        {
            bool probePrimaryNext = true;

            while (running)
            {
                try
                {
                    ProbeTargetState target;
                    Ping ping;

                    lock (sync)
                    {
                        target = probePrimaryNext ? primary : secondary;
                        ping = probePrimaryNext ? pingPrimary : pingSecondary;
                    }

                    probePrimaryNext = !probePrimaryNext;

                    if (!string.IsNullOrWhiteSpace(target.Host))
                        await ProbeAsync(target, ping);

                    await Task.Delay(ProbeIntervalMs);
                }
                catch (Exception ex)
                {
                    if (running)
                        Logger.Log("fastping", "loop error: " + ex.Message);

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

                if (reply.Status == IPStatus.Success)
                {
                    lock (sync)
                    {
                        RegisterSuccess_NoLock(target, reply.RoundtripTime);
                        RecomputeState_NoLock();
                    }
                }
                else
                {
                    lock (sync)
                    {
                        RegisterMiss_NoLock(target);
                        RecomputeState_NoLock();
                    }
                }
            }
            catch
            {
                lock (sync)
                {
                    RegisterMiss_NoLock(target);
                    RecomputeState_NoLock();
                }
            }
        }

        private int ComputeProbeTimeout_NoLock()
        {
            double basis = tunnelLatencyMs > 0 ? tunnelLatencyMs : state.StablePingMs;
            if (basis <= 0)
                return BaseProbeTimeoutMs;

            int timeout = (int)Math.Round(basis * 2.8 + 80.0);

            if (timeout < BaseProbeTimeoutMs)
                timeout = BaseProbeTimeoutMs;
            if (timeout > MaxProbeTimeoutMs)
                timeout = MaxProbeTimeoutMs;

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

                if (!hugeSpike)
                {
                    target.StablePingMs = target.StablePingMs + StableAlpha * (sample - target.StablePingMs);
                }
                else
                {
                    target.StablePingMs = target.StablePingMs + 0.10 * (sample - target.StablePingMs);
                }
            }

            target.LossRate = ComputeLossRate_NoLock(target);
            target.Confidence = ComputeConfidence_NoLock(target);
        }

        private void RegisterMiss_NoLock(ProbeTargetState target)
        {
            target.ConsecutiveMisses++;
            PushOutcome_NoLock(target, false);
            target.LossRate = ComputeLossRate_NoLock(target);
            target.Confidence = ComputeConfidence_NoLock(target);
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

            double best = double.MaxValue;
            int found = 0;

            for (int i = 0; i < target.SuccessCount; i++)
            {
                int idx = (target.SuccessIndex - 1 - i + target.SuccessRtts.Length) % target.SuccessRtts.Length;
                double value = target.SuccessRtts[idx];
                if (value <= 0)
                    continue;

                if (value < best)
                    best = value;

                found++;
                if (found == 2)
                    break;
            }

            return best == double.MaxValue ? -1 : best;
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
            if (ageMs > 250) confidence -= 0.10;
            if (ageMs > 500) confidence -= 0.20;
            if (ageMs > 900) confidence -= 0.25;
            if (ageMs > StaleAfterMs) confidence -= 0.30;

            confidence -= target.LossRate * 0.60;

            if (target.ConsecutiveMisses >= MissesBeforeDegrade)
                confidence -= 0.25;

            if (confidence < 0)
                confidence = 0;
            if (confidence > 1)
                confidence = 1;

            return confidence;
        }

        private void RecomputeState_NoLock()
        {
            FastPingState next = new FastPingState();

            next.PrimaryTarget = primary.Host;
            next.SecondaryTarget = secondary.Host;

            next.PrimaryInstantMs = primary.InstantPingMs;
            next.PrimaryStableMs = primary.StablePingMs;
            next.PrimaryFloorMs = primary.FloorPingMs;
            next.PrimaryLossRate = primary.LossRate;
            next.PrimaryConfidence = primary.Confidence;

            next.SecondaryInstantMs = secondary.InstantPingMs;
            next.SecondaryStableMs = secondary.StablePingMs;
            next.SecondaryFloorMs = secondary.FloorPingMs;
            next.SecondaryLossRate = secondary.LossRate;
            next.SecondaryConfidence = secondary.Confidence;

            next.TunnelLatencyMs = tunnelLatencyMs;

            bool primaryFresh = IsFresh_NoLock(primary);
            bool secondaryFresh = IsFresh_NoLock(secondary);

            double bestStableProbe = ChooseBestStableProbe_NoLock(primaryFresh, secondaryFresh);
            double bestInstantProbe = ChooseBestInstantProbe_NoLock(primaryFresh, secondaryFresh);
            double bestFloorProbe = ChooseBestFloorProbe_NoLock(primaryFresh, secondaryFresh);

            next.StablePingMs = bestStableProbe;
            next.InstantPingMs = bestInstantProbe;
            next.FloorPingMs = bestFloorProbe;

            next.SpikeDetected =
                bestInstantProbe > 0 &&
                bestStableProbe > 0 &&
                bestInstantProbe - bestStableProbe >= SpikeDeltaMs;

            if (bestInstantProbe > 0 && bestFloorProbe > 0 && bestInstantProbe - bestFloorProbe >= FloorSpikeDeltaMs)
                next.SpikeDetected = true;

            next.LossRate = Math.Max(primary.LossRate, secondary.LossRate);
            next.Confidence = Math.Max(primary.Confidence, secondary.Confidence);

            next.IsStale = !primaryFresh && !secondaryFresh;

            next.DisplayedPingMs = ComputeDisplayedPing_NoLock(next);

            state = next;
        }

        private bool IsFresh_NoLock(ProbeTargetState target)
        {
            if (target.LastSuccessUtc == DateTime.MinValue)
                return false;

            return (DateTime.UtcNow - target.LastSuccessUtc).TotalMilliseconds <= StaleAfterMs;
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

        private double ComputeDisplayedPing_NoLock(FastPingState s)
        {
            double tunnel = s.TunnelLatencyMs;
            double stable = s.StablePingMs;
            double instant = s.InstantPingMs;

            if (s.IsStale)
            {
                if (tunnel > 0)
                    return tunnel;
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
            public double LossRate;
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
                LossRate = 0;
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

    public sealed class FastPingState
    {
        public string PrimaryTarget;
        public string SecondaryTarget;

        public double PrimaryInstantMs;
        public double PrimaryStableMs;
        public double PrimaryFloorMs;
        public double PrimaryLossRate;
        public double PrimaryConfidence;

        public double SecondaryInstantMs;
        public double SecondaryStableMs;
        public double SecondaryFloorMs;
        public double SecondaryLossRate;
        public double SecondaryConfidence;

        public double TunnelLatencyMs;

        public double InstantPingMs;
        public double StablePingMs;
        public double FloorPingMs;
        public double DisplayedPingMs;
        public double LossRate;
        public double Confidence;

        public bool SpikeDetected;
        public bool IsStale;

        public FastPingState Clone()
        {
            return (FastPingState)MemberwiseClone();
        }
    }
}