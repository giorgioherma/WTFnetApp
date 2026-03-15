using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace WTFnetApp
{
    public class SpeedifyTelemetryReader
    {
        private Process process;
        private bool running;

        private readonly Action<MetricsSnapshot> onSnapshot;

        // smoothing
        private double smoothJitter = 0;
        private double smoothLatency = 0;

        // baseline tracking
        private double dynamicBase = 0;
        private int baseSamples = 0;

        public SpeedifyTelemetryReader(Action<MetricsSnapshot> handler)
        {
            onSnapshot = handler;
        }

        public void Start()
        {
            if (running)
                return;

            running = true;

            Logger.Log("speedify", "telemetry stream starting");

            Task.Run(StreamLoop);
        }

        public void Stop()
        {
            running = false;

            try
            {
                if (process != null && !process.HasExited)
                    process.Kill();
            }
            catch { }

            Logger.Log("speedify", "telemetry stopped");
        }

        private async Task StreamLoop()
        {
            while (running)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "speedify_cli",
                        Arguments = "stats --json",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    process = Process.Start(psi);

                    using (StreamReader reader = process.StandardOutput)
                    {
                        while (running && !reader.EndOfStream)
                        {
                            string line = await reader.ReadLineAsync();

                            if (string.IsNullOrWhiteSpace(line))
                                continue;

                            try
                            {
                                MetricsSnapshot snap = Parse(line);

                                if (snap != null)
                                    onSnapshot?.Invoke(snap);
                            }
                            catch (Exception ex)
                            {
                                Logger.Log("speedify", ex.Message);
                            }
                        }
                    }

                    Logger.Log("speedify", "telemetry stream ended — restarting");
                }
                catch (Exception ex)
                {
                    Logger.Log("speedify", ex.Message);
                }

                await Task.Delay(2000);
            }
        }

        // =========================
        // JSON PARSER (COMPATIBLE)
        // =========================

        private MetricsSnapshot Parse(string json)
        {
            double latency = ExtractValue(json, "latency");
            double jitter = ExtractValue(json, "jitter");
            double loss = ExtractValue(json, "packetLoss");

            // =========================
            // SMOOTHING
            // =========================

            if (smoothLatency == 0)
                smoothLatency = latency;
            else
                smoothLatency = smoothLatency * 0.75 + latency * 0.25;

            if (smoothJitter == 0)
                smoothJitter = jitter;
            else
                smoothJitter = smoothJitter * 0.6 + jitter * 0.4;

            // =========================
            // DYNAMIC BASELINE
            // =========================

            if (latency > 0 && latency < 120)
            {
                dynamicBase =
                    (dynamicBase * baseSamples + latency) /
                    (baseSamples + 1);

                baseSamples++;

                if (baseSamples > 200)
                    baseSamples = 200;
            }

            MetricsSnapshot snap = new MetricsSnapshot();

            snap.Recent = latency;
            snap.Average = smoothLatency;
            snap.Max = latency;

            snap.Jitter = smoothJitter;
            snap.MicroJitter = jitter;
            snap.PacketLoss = loss;

            snap.DynamicBase = dynamicBase;

            snap.Variance = 0;
            snap.LatencySlope = 0;
            snap.Trend = 0;

            return snap;
        }

        // =========================
        // SIMPLE JSON VALUE EXTRACTOR
        // =========================

        private double ExtractValue(string json, string key)
        {
            try
            {
                int pos = json.IndexOf("\"" + key + "\"");

                if (pos < 0)
                    return 0;

                pos = json.IndexOf(":", pos);

                if (pos < 0)
                    return 0;

                pos++;

                int end = json.IndexOfAny(new char[] { ',', '}' }, pos);

                if (end < 0)
                    end = json.Length;

                string value = json.Substring(pos, end - pos).Trim();

                double result;

                if (double.TryParse(
                        value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out result))
                    return result;
            }
            catch
            {
            }

            return 0;
        }
    }
}
