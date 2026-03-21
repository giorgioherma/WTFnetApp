using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WTFnetApp
{
    public enum NatState
    {
        Unknown = 0,
        Stable = 1,
        GracePeriod = 2,
        Unstable = 3,
        Lost = 4
    }

    internal class NatKeepAlive
    {
        // =========================
        // NAT EVENT TRACKING
        // =========================

        private readonly List<DateTime> natEvents = new List<DateTime>();
        private DateTime lastNatRecreate = DateTime.MinValue;

        public DateTime LastNatRecreate => lastNatRecreate;
        public NatState CurrentNatState => currentNatState;

        private readonly int natEventWindowMinutes = 5;
        private readonly int natEventThreshold = 3;

        private UdpClient natClient;
        private CancellationTokenSource natToken;
        private bool natKeepAliveRunning = false;

        private int natSendFailures = 0;
        private int natLocalPort = -1;

        private readonly int natGraceSeconds = 15;

        private NatState currentNatState = NatState.Unknown;

        private void SetNatState(NatState newState)
        {
            if (currentNatState == newState)
                return;

            currentNatState = newState;
            Logger.Log("nat", $"state -> {currentNatState}");
        }

        // =========================
        // START
        // =========================

        public async Task StartNatKeepAlive()
        {
            if (natKeepAliveRunning)
                return;

            natKeepAliveRunning = true;
            natToken = new CancellationTokenSource();
            natSendFailures = 0;

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
                int newPort = ep != null ? ep.Port : -1;

                if (natLocalPort != -1 && newPort != natLocalPort)
                {
                    Logger.Log("nat", "NAT recreated");
                    Logger.Log("nat", $"NAT RECREATED | old:{natLocalPort} new:{newPort}");

                    lastNatRecreate = DateTime.Now;
                    natEvents.Add(DateTime.Now);

                    natEvents.RemoveAll(t =>
                        (DateTime.Now - t).TotalMinutes > natEventWindowMinutes);

                    if (natEvents.Count >= natEventThreshold)
                        SetNatState(NatState.Unstable);
                    else
                        SetNatState(NatState.GracePeriod);
                }
                else
                {
                    lastNatRecreate = DateTime.Now;
                    SetNatState(NatState.Stable);
                }

                natLocalPort = newPort;
                SetNatState(NatState.GracePeriod);
                Logger.Log("nat", $"keepalive started | port:{natLocalPort}");

                // =========================
                // WARMUP
                // =========================

                for (int i = 0; i < 3; i++)
                {
                    try
                    {
                        await natClient.SendAsync(data, data.Length, endpoints[endpointIndex]);
                        natSendFailures = 0;
                    }
                    catch
                    {
                    }

                    await Task.Delay(200);
                }

                // =========================
                // KEEPALIVE LOOP
                // =========================

                while (!natToken.Token.IsCancellationRequested)
                {
                    try
                    {
                        await natClient.SendAsync(data, data.Length, endpoints[endpointIndex]);

                        natSendFailures = 0;

                        if ((DateTime.Now - lastNatRecreate).TotalSeconds <= natGraceSeconds)
                            SetNatState(NatState.GracePeriod);
                        else
                            SetNatState(NatState.Stable);

                        endpointIndex++;
                        if (endpointIndex >= endpoints.Length)
                            endpointIndex = 0;
                    }
                    catch
                    {
                        natSendFailures++;

                        Logger.Log("nat", $"send failure {natSendFailures}");

                        if (natSendFailures >= 1)
                            SetNatState(NatState.Unstable);

                        if (natSendFailures >= 3)
                        {
                            Logger.Log("nat", $"NAT LOST | port:{natLocalPort}");
                            SetNatState(NatState.Lost);
                            break;
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
                SetNatState(NatState.Unknown);

                Logger.Log("nat", "keepalive stopped");
            }
        }

        // =========================
        // STOP
        // =========================

        public void StopNatKeepAlive()
        {
            if (!natKeepAliveRunning)
                return;

            Logger.Log("nat", "stop requested");
            SetNatState(NatState.Unknown);
            natToken?.Cancel();
        }
    }
}