using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace WTFnetApp
{
    public class WtfFastPing
    {
        private readonly string[] hosts;
        private int hostIndex;
        private int hostPingCount = 0;

        private readonly Action<double, long> onPing;

        private bool running;

        private const int INTERVAL_MS = 90;
        private const int TIMEOUT_MS = 900;

        public WtfFastPing(string[] targetHosts, Action<double, long> handler)
        {
            if (targetHosts == null || targetHosts.Length == 0)
                throw new ArgumentException("At least one host required");

            hosts = targetHosts;
            onPing = handler;
        }

        public void Start()
        {
            if (running) return;

            running = true;

            Logger.Log("fastping", "prediction probe started");

            Task.Run(async () =>
            {
                while (running)
                {
                    long cycleStart = Stopwatch.GetTimestamp();

                    string host = hosts[hostIndex];

                    hostPingCount++;

                    if (hostPingCount >= 3)
                    {
                        hostPingCount = 0;
                        hostIndex = (hostIndex + 1) % hosts.Length;
                    }

                    long timestamp;

                    try
                    {
                        using (Ping ping = new Ping())
                        {
                            var reply = await ping.SendPingAsync(host, TIMEOUT_MS);

                            timestamp = Stopwatch.GetTimestamp();

                            if (reply.Status == IPStatus.Success)
                            {
                                double latency = reply.RoundtripTime;

                                if (latency > 600)
                                    latency = 600;

                                if (latency > 120)
                                    Logger.Log("fastping", "high ping " + host + " " + latency);

                                onPing?.Invoke(latency, timestamp);
                            }
                            else
                            {
                                if ((Environment.TickCount & 15) == 0)
                                    Logger.Log("fastping", "timeout " + host);

                                onPing?.Invoke(-1, timestamp);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        timestamp = Stopwatch.GetTimestamp();

                        Logger.Log("fastping", "error " + ex.Message);

                        onPing?.Invoke(-1, timestamp);
                    }

                    long elapsed = Stopwatch.GetTimestamp() - cycleStart;
                    double elapsedMs = elapsed * 1000.0 / Stopwatch.Frequency;

                    int delay = (int)Math.Max(0, INTERVAL_MS - elapsedMs);

                    if (delay > 0)
                        await Task.Delay(delay);
                }
            });
        }

        public void Stop()
        {
            running = false;
            Logger.Log("fastping", "prediction probe stopped");
        }

        internal static async Task<int> Ping(string host)
        {
            throw new NotImplementedException();
        }
    }
}
#if(false)

        // =========================
        // QUICK PROBE PING
        // =========================

        public static async Task<int> Ping(string host)
        {
            try
            {
                using (Ping p = new Ping())
                {
                    var reply = await p.SendPingAsync(host, 400);

                    if (reply.Status == IPStatus.Success)
                        return (int)reply.RoundtripTime;
                }
            }
            catch
            {
            }

            return -1;
        }

    }
}
#endif