using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WTFnetApp
{
    internal class NatKeepAlive
    {
        // =========================
        // NAT EVENT TRACKING
        // =========================
        private List<DateTime> natEvents = new List<DateTime>();
        private DateTime lastNatRecreate = DateTime.MinValue;
        
        public DateTime LastNatRecreate => lastNatRecreate;


        private int natEventWindowMinutes = 5;
        private int natEventThreshold = 3;

        private UdpClient natClient;
        private CancellationTokenSource natToken;
        private bool natKeepAliveRunning = false;
        private int natSendFailures = 0;
        private int natLocalPort = -1;
        private int natGraceSeconds = 15;

        // =========================
        // START
        // =========================
        public async Task StartNatKeepAlive()
        {
            if (natKeepAliveRunning)
                return;

            natKeepAliveRunning = true;
            natToken = new CancellationTokenSource();

            var endpoints = new[]
            {
                new IPEndPoint(IPAddress.Parse("1.1.1.1"), 3074),
                new IPEndPoint(IPAddress.Parse("8.8.8.8"), 3074),
                new IPEndPoint(IPAddress.Parse("9.9.9.9"), 3074)
            };

            byte[] data = Encoding.ASCII.GetBytes("ping");
            int endpointIndex = 0;

            try
            {
                natClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));

                var ep = (IPEndPoint)natClient.Client.LocalEndPoint;
                int newPort = ep?.Port ?? -1;

                lastNatRecreate = DateTime.Now;

                if (natLocalPort != -1 && newPort != natLocalPort)
                {
                    Log("nat", $"NAT RECREATED | old:{natLocalPort} new:{newPort}");

                    lastNatRecreate = DateTime.Now;

                    natEvents.Add(DateTime.Now);

                    natEvents.RemoveAll(t =>
                        (DateTime.Now - t).TotalMinutes > natEventWindowMinutes);
                }

                natLocalPort = newPort;

                Log("nat", $"keepalive started | port:{natLocalPort}");

                // warm-up
                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        await natClient.SendAsync(data, data.Length, endpoints[endpointIndex]);
                    }
                    catch { }

                    await Task.Delay(200);
                }

                while (!natToken.Token.IsCancellationRequested)
                {
                    try
                    {
                        await natClient.SendAsync(data, data.Length, endpoints[endpointIndex]);

                        natSendFailures = 0;

                        endpointIndex++;
                        if (endpointIndex >= endpoints.Length)
                            endpointIndex = 0;
                    }
                    catch
                    {
                        natSendFailures++;

                        Log("nat", $"send failure {natSendFailures}");

                        if (natSendFailures >= 3)
                        {
                            Log("nat", $"NAT LOST | port:{natLocalPort}");

                            StopNatKeepAlive();
                            await Task.Delay(500);

                            _ = StartNatKeepAlive();
                            return;
                        }
                    }

                    await Task.Delay(10000, natToken.Token);
                }
            }
            catch (TaskCanceledException)
            {
            }
            finally
            {
                try
                {
                    natClient?.Close();
                }
                catch { }

                natClient = null;
                natKeepAliveRunning = false;

                Log("nat", "keepalive stopped");
            }
        }

        // =========================
        // STOP
        // =========================
        public void StopNatKeepAlive()
        {
            if (!natKeepAliveRunning) return;

            Log("nat", "stop requested");

            natToken?.Cancel();
        }
        // =========================
        // LOG (simple placeholder)
        // =========================
        private void Log(string tag, string text)
        {
            Console.WriteLine($"[{tag}] {text}");
        }
    }
}