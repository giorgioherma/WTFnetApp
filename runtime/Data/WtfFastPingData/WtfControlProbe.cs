using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace WTFnetApp
{
    /// <summary>
    /// Slow, independent ICMP control stream. It is deliberately observational:
    /// it does not change FastPing health, overlay state, or route selection.
    /// </summary>
    internal sealed class WtfControlProbe
    {
        private const int IntervalMs = 1000; // one probe overall per second; targets alternate
        private const int TimeoutMs = 1500;
        private const int InitialOffsetMs = 500;
        private const int ComparisonIntervalSeconds = 5;
        private const int WindowSeconds = 10;

        private const string RawCsv = "wtfnet_control_probe.csv";
        private const string CompareCsv = "wtfnet_probe_comparison.csv";

        private static readonly byte[] Payload = new byte[] { 87, 84, 70, 67, 84, 82, 76, 33 };
        private readonly object csvSync = new object();
        private readonly Ping primaryPing = new Ping();
        private readonly Ping secondaryPing = new Ping();
        private readonly string primaryHost;
        private readonly string secondaryHost;
        private readonly Func<FastPingState> readFastState;
        private readonly Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> readFastWindow;
        private readonly Queue<ControlSample> recent = new Queue<ControlSample>();

        private volatile bool running;
        private Task probeTask;
        private DateTime lastComparisonUtc = DateTime.MinValue;

        public WtfControlProbe(
            string primaryHost,
            string secondaryHost,
            Func<FastPingState> readFastState,
            Func<FastPingSignalAnalyzer.FastPingAggregateSnapshot> readFastWindow)
        {
            this.primaryHost = primaryHost ?? throw new ArgumentNullException(nameof(primaryHost));
            this.secondaryHost = secondaryHost ?? throw new ArgumentNullException(nameof(secondaryHost));
            this.readFastState = readFastState ?? throw new ArgumentNullException(nameof(readFastState));
            this.readFastWindow = readFastWindow ?? throw new ArgumentNullException(nameof(readFastWindow));
        }

        public void Start()
        {
            if (running)
                return;

            running = true;
            probeTask = RunAsync();
            Logger.Log("control", "independent control probe started | interval=1000ms | timeout=1500ms | alternating targets");
        }

        public async Task StopAsync()
        {
            running = false;
            try
            {
                if (probeTask != null)
                    await probeTask;
            }
            finally
            {
                primaryPing.Dispose();
                secondaryPing.Dispose();
            }
        }

        private async Task RunAsync()
        {
            // Avoid firing both probe engines at precisely the same startup instant.
            await Task.Delay(InitialOffsetMs);

            int nextTarget = 0;

            while (running)
            {
                DateTime startedUtc = DateTime.UtcNow;
                bool primary = (nextTarget++ & 1) == 0;
                string host = primary ? primaryHost : secondaryHost;
                Ping ping = primary ? primaryPing : secondaryPing;
                string outcome = "EXCEPTION";
                long rtt = -1;

                try
                {
                    PingReply reply = await ping.SendPingAsync(host, TimeoutMs, Payload);
                    outcome = reply.Status.ToString();
                    if (reply.Status == IPStatus.Success)
                        rtt = reply.RoundtripTime;
                }
                catch (Exception ex)
                {
                    // An exception is distinct from an ICMP timeout, not assumed loss.
                    Logger.Log("control", "probe exception (" + host + "): " + ex.GetType().Name);
                }

                DateTime completedUtc = DateTime.UtcNow;
                var sample = new ControlSample
                {
                    AtUtc = completedUtc,
                    Host = host,
                    Outcome = outcome,
                    RttMs = rtt
                };

                recent.Enqueue(sample);
                while (recent.Count > 0 &&
                       (completedUtc - recent.Peek().AtUtc).TotalSeconds > 30)
                    recent.Dequeue();

                AppendCsv(
                    RawCsv,
                    "utc,target,outcome,rtt_ms,elapsed_ms",
                    completedUtc.ToString("o", CultureInfo.InvariantCulture) + "," +
                    host + "," + outcome + "," + rtt.ToString(CultureInfo.InvariantCulture) + "," +
                    (int)(completedUtc - startedUtc).TotalMilliseconds);

                if (lastComparisonUtc == DateTime.MinValue ||
                    (completedUtc - lastComparisonUtc).TotalSeconds >= ComparisonIntervalSeconds)
                {
                    lastComparisonUtc = completedUtc;
                    WriteComparison(completedUtc);
                }

                int remainingMs = IntervalMs - (int)(DateTime.UtcNow - startedUtc).TotalMilliseconds;
                if (running && remainingMs > 0)
                    await Task.Delay(remainingMs);
            }
        }

        private void WriteComparison(DateTime nowUtc)
        {
            ControlSample[] control = recent
                .Where(x => (nowUtc - x.AtUtc).TotalSeconds <= WindowSeconds)
                .ToArray();

            // These snapshots are read-only observations of the existing fast engine.
            FastPingState fastState = readFastState();
            FastPingSignalAnalyzer.FastPingAggregateSnapshot fast = readFastWindow();
            double baseline = fastState != null && fastState.BaselinePingMs > 0
                ? fastState.BaselinePingMs
                : 35.0;
            double elevatedThreshold = Math.Max(100.0, baseline * 2.5);

            long[] successes = control
                .Where(x => x.RttMs >= 0)
                .Select(x => x.RttMs)
                .OrderBy(x => x)
                .ToArray();

            int misses = control.Count(x => x.Outcome == "TimedOut");
            int exceptions = control.Length - successes.Length - misses;
            int elevated = successes.Count(x => x >= elevatedThreshold);

            int maxConsecutiveMisses = 0;
            int streak = 0;
            foreach (ControlSample sample in control)
            {
                streak = sample.Outcome == "TimedOut" ? streak + 1 : 0;
                maxConsecutiveMisses = Math.Max(maxConsecutiveMisses, streak);
            }

            double p95 = successes.Length == 0
                ? -1
                : successes[(int)Math.Ceiling((successes.Length - 1) * 0.95)];

            bool controlReady = control.Length >= 8;
            bool fastReady = fast != null && fast.IsValid &&
                             fastState != null && !fastState.IsStale;
            bool controlAlert = controlReady && (elevated >= 2 || maxConsecutiveMisses >= 3);
            bool fastAlert = fastReady &&
                             (fast.SpikeCount >= 3 ||
                              (fast.P95PingMs >= elevatedThreshold &&
                               fast.P95PingMs > fast.AvgPingMs + 25));

            string comparison = !controlReady || !fastReady ? "insufficient" :
                controlAlert && fastAlert ? "both" :
                fastAlert ? "fast-only" :
                controlAlert ? "control-only" : "clear";

            AppendCsv(
                CompareCsv,
                "utc,window_s,comparison,control_samples,control_successes,control_timeouts,control_exceptions,control_p95_ms,control_elevated_count,control_max_miss_streak,fast_samples,fast_avg_ms,fast_p95_ms,fast_spikes,fast_weighted_miss,fast_stale",
                nowUtc.ToString("o", CultureInfo.InvariantCulture) + "," +
                WindowSeconds + "," + comparison + "," +
                control.Length + "," + successes.Length + "," + misses + "," + exceptions + "," +
                F(p95) + "," + elevated + "," + maxConsecutiveMisses + "," +
                (fast == null ? 0 : fast.SampleCount) + "," +
                F(fast == null ? -1 : fast.AvgPingMs) + "," +
                F(fast == null ? -1 : fast.P95PingMs) + "," +
                (fast == null ? 0 : fast.SpikeCount) + "," +
                F(fast == null ? -1 : fast.AvgLossRate) + "," +
                (fastState == null || fastState.IsStale ? "1" : "0"));
        }

        private static string F(double value)
        {
            return value.ToString("0.0", CultureInfo.InvariantCulture);
        }

        private void AppendCsv(string path, string header, string line)
        {
            try
            {
                lock (csvSync)
                {
                    bool newFile = !File.Exists(path) || new FileInfo(path).Length == 0;
                    using (var writer = new StreamWriter(path, true))
                    {
                        if (newFile)
                            writer.WriteLine(header);
                        writer.WriteLine(line);
                    }
                }
            }
            catch (Exception ex)
            {
                // Logging problems must not disturb the live fast monitor.
                Logger.Log("control", "CSV write failed: " + ex.GetType().Name);
            }
        }

        private sealed class ControlSample
        {
            public DateTime AtUtc;
            public string Host;
            public string Outcome;
            public long RttMs;
        }
    }
}
