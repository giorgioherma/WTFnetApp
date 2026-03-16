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
        private readonly Stopwatch sampleTimer = Stopwatch.StartNew();

        // smoothing
        private double smoothJitter = 0;
        private double smoothLatency = 0;
        private double previousLatency = 0;

        // baseline tracking
        private double dynamicBase = 0;

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
            if (sampleTimer.ElapsedMilliseconds < 200)
                return null;

            if (!json.Contains("\"connections\""))
                return null;

            if (!TryExtractConnectionMetrics(
                    json,
                    out double latency,
                    out double adapter1Latency,
                    out double adapter2Latency,
                    out bool congested,
                    out int priority,
                    out long rcvBytes,
                    out long sndBytes))
                return null;

            sampleTimer.Restart();

            double latencyDelta =
                previousLatency > 0
                ? Math.Abs(latency - previousLatency)
                : 0;

            // =========================
            // SMOOTHING
            // =========================

            if (smoothLatency == 0)
                smoothLatency = latency;
            else
                smoothLatency = smoothLatency * 0.75 + latency * 0.25;

            if (smoothJitter == 0)
                smoothJitter = latencyDelta;
            else
                smoothJitter = smoothJitter * 0.6 + latencyDelta * 0.4;

            // =========================
            // DYNAMIC BASELINE
            // =========================

            if (latency > 0 && (dynamicBase == 0 || latency < dynamicBase))
                dynamicBase = latency;

            MetricsSnapshot snap = new MetricsSnapshot();

            snap.Recent = latency;
            snap.Average = smoothLatency;
            snap.Max = latency;

            snap.Jitter = smoothJitter;
            snap.MicroJitter = latencyDelta;
            snap.PacketLoss = 0;

            snap.DynamicBase = dynamicBase;

            snap.Adapter1Latency = adapter1Latency;
            snap.Adapter2Latency = adapter2Latency;

            snap.Congested = congested;
            snap.Priority = priority;
            snap.ReceiveBytes = rcvBytes;
            snap.SendBytes = sndBytes;

            snap.Variance = 0;
            snap.LatencySlope = 0;
            snap.Trend = 0;

            previousLatency = latency;

            return snap;
        }

        private bool TryExtractConnectionMetrics(
            string json,
            out double tunnelLatency,
            out double adapter1Latency,
            out double adapter2Latency,
            out bool congested,
            out int priority,
            out long rcvBytes,
            out long sndBytes)
        {
            tunnelLatency = 0;
            adapter1Latency = 0;
            adapter2Latency = 0;
            congested = false;
            priority = 0;
            rcvBytes = 0;
            sndBytes = 0;

            int connectionsPos = json.IndexOf("\"connections\"");

            if (connectionsPos < 0)
                return false;

            int listStart = json.IndexOf('[', connectionsPos);

            if (listStart < 0)
                return false;

            int listEnd = json.IndexOf(']', listStart + 1);

            if (listEnd < 0)
                return false;

            int cursor = listStart + 1;

            while (cursor < listEnd)
            {
                int objStart = json.IndexOf('{', cursor);

                if (objStart < 0 || objStart >= listEnd)
                    break;

                int objEnd = FindObjectEnd(json, objStart, listEnd);

                if (objEnd < 0)
                    break;

                bool isConnected =
                    ExtractBool(json, "connected", objStart, objEnd, true);

                if (isConnected)
                {
                    double latency =
                        ExtractValue(json, "jMs", objStart, objEnd);

                    if (latency > 0)
                    {
                        if (tunnelLatency == 0 || latency < tunnelLatency)
                            tunnelLatency = latency;

                        if (adapter1Latency == 0)
                            adapter1Latency = latency;
                        else if (adapter2Latency == 0)
                            adapter2Latency = latency;
                    }

                    if (!congested)
                        congested =
                            ExtractBool(json, "congested", objStart, objEnd, false);

                    if (priority == 0)
                        priority =
                            (int)ExtractValue(json, "priority", objStart, objEnd);

                    rcvBytes +=
                        (long)ExtractValue(json, "rcvBytes", objStart, objEnd);

                    sndBytes +=
                        (long)ExtractValue(json, "sndBytes", objStart, objEnd);
                }

                cursor = objEnd + 1;
            }

            return tunnelLatency > 0;
        }

        private int FindObjectEnd(string json, int start, int boundary)
        {
            int depth = 0;

            for (int i = start; i < boundary; i++)
            {
                if (json[i] == '{')
                    depth++;
                else if (json[i] == '}')
                {
                    depth--;

                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        // =========================
        // SIMPLE JSON VALUE EXTRACTOR
        // =========================

        private double ExtractValue(string json, string key)
        {
            return ExtractValue(json, key, 0, json.Length - 1);
        }

        private double ExtractValue(string json, string key, int start, int endBoundary)
        {
            try
            {
                int pos = json.IndexOf("\"" + key + "\"", start);

                if (pos < 0 || pos > endBoundary)
                    return 0;

                pos = json.IndexOf(":", pos);

                if (pos < 0 || pos > endBoundary)
                    return 0;

                pos++;

                int end = pos;

                while (end <= endBoundary)
                {
                    char ch = json[end];

                    if (ch == ',' || ch == '}')
                        break;

                    end++;
                }

                if (end > endBoundary)
                    end = endBoundary + 1;

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

        private bool ExtractBool(
            string json,
            string key,
            int start,
            int endBoundary,
            bool defaultValue)
        {
            int pos = json.IndexOf("\"" + key + "\"", start);

            if (pos < 0 || pos > endBoundary)
                return defaultValue;

            pos = json.IndexOf(':', pos);

            if (pos < 0 || pos > endBoundary)
                return defaultValue;

            pos++;

            while (pos <= endBoundary && char.IsWhiteSpace(json[pos]))
                pos++;

            if (pos + 3 <= endBoundary &&
                json[pos] == 't' &&
                json[pos + 1] == 'r' &&
                json[pos + 2] == 'u' &&
                json[pos + 3] == 'e')
                return true;

            if (pos + 4 <= endBoundary &&
                json[pos] == 'f' &&
                json[pos + 1] == 'a' &&
                json[pos + 2] == 'l' &&
                json[pos + 3] == 's' &&
                json[pos + 4] == 'e')
                return false;

            return defaultValue;
        }
    }
}
