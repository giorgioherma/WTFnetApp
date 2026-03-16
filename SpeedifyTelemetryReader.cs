using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
namespace WTFnetApp
{
    public class SpeedifyTelemetryReader
    {
        private Process process;
        private bool running;
        private readonly Action<MetricsSnapshot> onSnapshot;
        private readonly Stopwatch sampleTimer = Stopwatch.StartNew();
        private double smoothLatency = 0;
        private double smoothJitter = 0;
        private double previousLatency = 0;
        private double dynamicBase = 0;

        public SpeedifyTelemetryReader(Action<MetricsSnapshot> handler) { onSnapshot = handler; }

        public void Start()
        {
            if (running) return;
            running = true;
            Logger.Log("speedify", "telemetry stream starting");
            Task.Run(StreamLoop);
        }

        public void Stop()
        {
            running = false;
            try { if (process != null && !process.HasExited) process.Kill(); } catch { }
            Logger.Log("speedify", "telemetry stopped");
        }

        private async Task StreamLoop()
        {
            while (running)
            {
                try
                {
                    process = Process.Start(new ProcessStartInfo
                    {
                        FileName = @"C:\Program Files (x86)\Speedify\speedify_cli.exe",
                        Arguments = "stats --json",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });

                    using (StreamReader reader = process.StandardOutput)
                    {
                        StringBuilder blockBuffer = new StringBuilder(2048);
                        while (running && !reader.EndOfStream)
                        {
                            string line = await reader.ReadLineAsync();
                            if (string.IsNullOrWhiteSpace(line))
                            {
                                if (blockBuffer.Length == 0) continue;
                                string json = blockBuffer.ToString();
                                blockBuffer.Clear();
                                try
                                {
                                    MetricsSnapshot snap = Parse(json);
                                    if (snap != null) onSnapshot?.Invoke(snap);
                                }
                                catch (Exception ex) { Logger.Log("speedify", ex.Message); }
                                continue;
                            }

                            blockBuffer.AppendLine(line);
                        }

                        if (blockBuffer.Length > 0)
                        {
                            try
                            {
                                MetricsSnapshot snap = Parse(blockBuffer.ToString());
                                if (snap != null) onSnapshot?.Invoke(snap);
                            }
                            catch (Exception ex) { Logger.Log("speedify", ex.Message); }
                        }
                    }

                    Logger.Log("speedify", "telemetry stream ended — restarting");
                }
                catch (Exception ex) { Logger.Log("speedify", ex.Message); }

                await Task.Delay(2000);
            }
        }

        private MetricsSnapshot Parse(string json)
        {
            if (sampleTimer.ElapsedMilliseconds < 200) return null;
            if (!json.Contains("connection_stats")) return null;

            if (!TryExtractConnectionMetrics(json, out double latency, out double jitter,
                    out double adapter1Latency, out double adapter2Latency,
                    out bool congested, out long receiveBytes, out long sendBytes))
                return null;

            sampleTimer.Restart();
            double microJitter = previousLatency > 0 ? Math.Abs(latency - previousLatency) : 0;
            smoothLatency = smoothLatency == 0 ? latency : smoothLatency * 0.75 + latency * 0.25;
            smoothJitter = smoothJitter == 0 ? jitter : smoothJitter * 0.6 + jitter * 0.4;
            if (latency > 0 && (dynamicBase == 0 || latency < dynamicBase)) dynamicBase = latency;
            previousLatency = latency;

            return new MetricsSnapshot
            {
                Recent = latency,
                Average = smoothLatency,
                Max = latency,
                Jitter = smoothJitter,
                MicroJitter = microJitter,
                PacketLoss = 0,
                DynamicBase = dynamicBase,
                Adapter1Latency = adapter1Latency,
                Adapter2Latency = adapter2Latency,
                Congested = congested,
                ReceiveBytes = receiveBytes,
                SendBytes = sendBytes
            };
        }

        private bool TryExtractConnectionMetrics(string json,
            out double tunnelLatency, out double tunnelJitter,
            out double adapter1Latency, out double adapter2Latency,
            out bool congested, out long receiveBytes, out long sendBytes)
        {
            tunnelLatency = 0;
            tunnelJitter = 0;
            adapter1Latency = 0;
            adapter2Latency = 0;
            congested = false;
            receiveBytes = 0;
            sendBytes = 0;

            int connectionsPos = json.IndexOf("\"connections\"");
            if (connectionsPos < 0) return false;

            int listStart = json.IndexOf('[', connectionsPos);
            if (listStart < 0) return false;

            int listEnd = json.IndexOf(']', listStart + 1);
            if (listEnd < 0) return false;

            int cursor = listStart + 1;
            while (cursor < listEnd)
            {
                int objStart = json.IndexOf('{', cursor);
                if (objStart < 0 || objStart >= listEnd) break;

                int objEnd = json.IndexOf('}', objStart + 1);
                if (objEnd < 0 || objEnd > listEnd) break;

                bool connected = ExtractBool(json, "connected", objStart, objEnd, false);
                double latency = ExtractValue(json, "latencyMs", objStart, objEnd);
                string protocol = ExtractString(json, "protocol", objStart, objEnd);

                if (connected && latency > 0 && !string.Equals(protocol, "proxy", StringComparison.OrdinalIgnoreCase))
                {
                    double j = ExtractValue(json, "jitterMs", objStart, objEnd);
                    if (tunnelLatency == 0 || latency < tunnelLatency)
                    {
                        tunnelLatency = latency;
                        tunnelJitter = j;
                    }

                    if (adapter1Latency == 0) adapter1Latency = latency;
                    else if (adapter2Latency == 0) adapter2Latency = latency;

                    if (ExtractBool(json, "congested", objStart, objEnd, false) ||
                        ExtractBool(json, "uploadCongested", objStart, objEnd, false) ||
                        ExtractBool(json, "downloadCongested", objStart, objEnd, false)) congested = true;

                    receiveBytes += (long)ExtractValue(json, "receiveBytes", objStart, objEnd);
                    sendBytes += (long)ExtractValue(json, "sendBytes", objStart, objEnd);
                }

                cursor = objEnd + 1;
            }

            return tunnelLatency > 0;
        }

        private double ExtractValue(string json, string key, int start, int endBoundary)
        {
            int keyPos = json.IndexOf("\"" + key + "\"", start);
            if (keyPos < 0 || keyPos > endBoundary) return 0;

            int colon = json.IndexOf(':', keyPos);
            if (colon < 0 || colon > endBoundary) return 0;

            int pos = colon + 1, end = pos;
            while (end < json.Length && end <= endBoundary && json[end] != ',' && json[end] != '}') end++;

            string raw = json.Substring(pos, end - pos).Trim();
            return double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        private bool ExtractBool(string json, string key, int start, int endBoundary, bool defaultValue)
        {
            int keyPos = json.IndexOf("\"" + key + "\"", start);
            if (keyPos < 0 || keyPos > endBoundary) return defaultValue;

            int colon = json.IndexOf(':', keyPos);
            if (colon < 0 || colon > endBoundary) return defaultValue;

            int pos = colon + 1;
            while (pos < json.Length && pos <= endBoundary && char.IsWhiteSpace(json[pos])) pos++;
            if (pos >= json.Length || pos > endBoundary) return defaultValue;

            if (pos + 3 <= endBoundary && json[pos] == 't') return true;
            if (pos + 4 <= endBoundary && json[pos] == 'f') return false;
            return defaultValue;
        }

        private string ExtractString(string json, string key, int start, int endBoundary)
        {
            int keyPos = json.IndexOf("\"" + key + "\"", start);
            if (keyPos < 0 || keyPos > endBoundary) return string.Empty;

            int colon = json.IndexOf(':', keyPos);
            if (colon < 0 || colon > endBoundary) return string.Empty;

            int pos = colon + 1;
            while (pos < json.Length && pos <= endBoundary && char.IsWhiteSpace(json[pos])) pos++;
            if (pos >= json.Length || pos > endBoundary || json[pos] != '"') return string.Empty;

            int end = json.IndexOf('"', pos + 1);
            if (end < 0 || end > endBoundary) return string.Empty;
            return json.Substring(pos + 1, end - pos - 1);
        }
   }
}
