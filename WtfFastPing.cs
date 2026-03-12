using System;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

public class WtfFastPing
{
    private readonly string host;
    private readonly Action<double> onPing;

    private bool running = false;

    // ~15 pings per second
    private const int INTERVAL_MS = 66;

    public WtfFastPing(string targetHost, Action<double> pingHandler)
    {
        host = targetHost;
        onPing = pingHandler;
    }

    public void Start()
    {
        if (running) return;

        running = true;

        Task.Run(async () =>
        {
            Ping ping = new Ping();

            while (running)
            {
                try
                {
                    PingReply reply = await ping.SendPingAsync(host, 1000);

                    if (reply.Status == IPStatus.Success)
                    {
                        onPing?.Invoke(reply.RoundtripTime);
                    }
                    else
                    {
                        onPing?.Invoke(-1);
                    }
                }
                catch
                {
                    onPing?.Invoke(-1);
                }

                await Task.Delay(INTERVAL_MS);
            }
        });
    }

    public void Stop()
    {
        running = false;
    }
}