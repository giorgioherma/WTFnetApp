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

    internal sealed class NatKeepAlive
    {
        // =========================
        // NAT EVENT TRACKING
        // =========================

        private readonly List<DateTime> natEvents = new List<DateTime>();
        private readonly object sync = new object();

        private DateTime lastNatRecreateUtc = DateTime.MinValue;
        private DateTime lastSuccessfulSendUtc = DateTime.MinValue;
        private DateTime lastStateChangeUtc = DateTime.MinValue;

        public DateTime LastNatRecreate => lastNatRecreateUtc == DateTime.MinValue
            ? DateTime.MinValue
            : lastNatRecreateUtc.ToLocalTime();

        public NatState CurrentNatState => currentNatState;

        private readonly int natEventWindowMinutes = 5;
        private readonly int natEventThreshold = 3;

        private readonly int natGraceSeconds = 15;
        private readonly int silentFailureSeconds = 20;

        private readonly int warmupSends = 5;
        private readonly int warmupDelayMs = 150;

        private readonly int stableDelayMs = 8000;
        private readonly int graceDelayMs = 3000;
        private readonly int unstableDelayMs = 4000;
        private readonly int lostDelayMs = 2500;

        private UdpClient natClient;
        private CancellationTokenSource natToken;
        private Task natLoopTask;

        private bool natKeepAliveRunning = false;
        private int natSendFailures = 0;
        private int natLocalPort = -1;
        private int preferredEndpointIndex = 0;

        private NatState currentNatState = NatState.Unknown;

        private readonly IPEndPoint[] endpoints =
        {
            new IPEndPoint(IPAddress.Parse("1.1.1.1"), 3074),
            new IPEndPoint(IPAddress.Parse("8.8.8.8"), 3074),
            new IPEndPoint(IPAddress.Parse("9.9.9.9"), 3074)
        };

        private readonly byte[] data = Encoding.ASCII.GetBytes("ping");

        private void SetNatState(NatState newState)
        {
            if (currentNatState == newState)
                return;

            currentNatState = newState;
            lastStateChangeUtc = DateTime.UtcNow;
            Logger.Log("nat", $"state -> {currentNatState}");
        }

        // =========================
        // START
        // =========================

        public Task StartNatKeepAlive()
        {
            lock (sync)
            {
                if (natKeepAliveRunning)
                    return natLoopTask ?? Task.CompletedTask;

                natKeepAliveRunning = true;
                natToken = new CancellationTokenSource();
                natSendFailures = 0;

                natLoopTask = Task.Run(() => RunNatKeepAliveAsync(natToken.Token));
                return natLoopTask;
            }
        }

        public Task RunningTask
        {
            get
            {
                lock (sync)
                {
                    return natLoopTask ?? Task.CompletedTask;
                }
            }
        }

        private async Task RunNatKeepAliveAsync(CancellationToken cancellationToken)
        {
            try
            {
                await CreateOrRecreateSocketAsync(markAsReset: natLocalPort != -1, cancellationToken);
                Logger.Log("nat", $"keepalive started | port:{natLocalPort}");

                await PerformWarmupAsync(cancellationToken);

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (ShouldMarkSilentFailure())
                    {
                        if (currentNatState == NatState.Stable)
                            SetNatState(NatState.Unstable);
                    }

                    bool success = await TrySendKeepAliveAsync(cancellationToken);

                    if (success)
                    {
                        natSendFailures = 0;
                        lastSuccessfulSendUtc = DateTime.UtcNow;

                        if ((DateTime.UtcNow - lastNatRecreateUtc).TotalSeconds <= natGraceSeconds)
                            SetNatState(NatState.GracePeriod);
                        else
                            SetNatState(NatState.Stable);
                    }
                    else
                    {
                        natSendFailures++;

                        Logger.Log("nat", $"send failure {natSendFailures}");

                        if (natSendFailures >= 2)
                            SetNatState(NatState.Unstable);

                        if (natSendFailures >= 3)
                        {
                            Logger.Log("nat", $"NAT LOST | port:{natLocalPort}");
                            SetNatState(NatState.Lost);

                            await RecreateSocketAfterLossAsync(cancellationToken);
                            continue;
                        }
                    }

                    int delayMs = GetCurrentDelayMs();
                    await Task.Delay(delayMs, cancellationToken);
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Log("nat", "keepalive fatal error | " + ex.Message);
            }
            finally
            {
                CleanupSocket();

                lock (sync)
                {
                    natKeepAliveRunning = false;
                    natLoopTask = null;

                    try
                    {
                        natToken?.Dispose();
                    }
                    catch
                    {
                    }

                    natToken = null;
                }

                SetNatState(NatState.Unknown);
                Logger.Log("nat", "keepalive stopped");
            }
        }

        private async Task<bool> TrySendKeepAliveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                IPEndPoint endpoint = endpoints[preferredEndpointIndex];
                await natClient.SendAsync(data, data.Length, endpoint);
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (SocketException)
            {
                RotateEndpointOnFailure();
                return false;
            }
            catch (Exception)
            {
                RotateEndpointOnFailure();
                return false;
            }
        }

        private async Task PerformWarmupAsync(CancellationToken cancellationToken)
        {
            Logger.Log("nat", $"warmup start | sends:{warmupSends}");

            for (int i = 0; i < warmupSends; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                bool success = await TrySendKeepAliveAsync(cancellationToken);
                if (success)
                {
                    natSendFailures = 0;
                    lastSuccessfulSendUtc = DateTime.UtcNow;
                    SetNatState(NatState.GracePeriod);
                }

                await Task.Delay(warmupDelayMs, cancellationToken);
            }

            Logger.Log("nat", "warmup complete");
        }

        private async Task RecreateSocketAfterLossAsync(CancellationToken cancellationToken)
        {
            CleanupSocket();

            await Task.Delay(lostDelayMs, cancellationToken);

            await CreateOrRecreateSocketAsync(markAsReset: true, cancellationToken);
            natSendFailures = 0;

            Logger.Log("nat", $"socket recreated after loss | port:{natLocalPort}");

            await PerformWarmupAsync(cancellationToken);
        }

        private async Task CreateOrRecreateSocketAsync(bool markAsReset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CleanupSocket();

            natClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));

            var ep = natClient.Client.LocalEndPoint as IPEndPoint;
            int newPort = ep != null ? ep.Port : -1;
            int oldPort = natLocalPort;

            if (markAsReset || (oldPort != -1 && newPort != oldPort))
            {
                lastNatRecreateUtc = DateTime.UtcNow;
                RegisterNatEvent();

                Logger.Log("nat", $"NAT RECREATED | old:{oldPort} new:{newPort}");

                if (natEvents.Count >= natEventThreshold)
                    SetNatState(NatState.Unstable);
                else
                    SetNatState(NatState.GracePeriod);
            }
            else
            {
                if (lastNatRecreateUtc == DateTime.MinValue)
                    lastNatRecreateUtc = DateTime.UtcNow;

                SetNatState(NatState.GracePeriod);
            }

            natLocalPort = newPort;
            lastSuccessfulSendUtc = DateTime.UtcNow;

            await Task.CompletedTask;
        }

        private void RegisterNatEvent()
        {
            DateTime now = DateTime.UtcNow;
            natEvents.Add(now);

            natEvents.RemoveAll(t =>
                (now - t).TotalMinutes > natEventWindowMinutes);
        }

        private void RotateEndpointOnFailure()
        {
            preferredEndpointIndex++;
            if (preferredEndpointIndex >= endpoints.Length)
                preferredEndpointIndex = 0;
        }

        private int GetCurrentDelayMs()
        {
            switch (currentNatState)
            {
                case NatState.GracePeriod:
                    return graceDelayMs;

                case NatState.Unstable:
                    return unstableDelayMs;

                case NatState.Lost:
                    return lostDelayMs;

                case NatState.Stable:
                    return stableDelayMs;

                default:
                    return unstableDelayMs;
            }
        }

        private bool ShouldMarkSilentFailure()
        {
            if (lastSuccessfulSendUtc == DateTime.MinValue)
                return false;

            return (DateTime.UtcNow - lastSuccessfulSendUtc).TotalSeconds > silentFailureSeconds;
        }

        private void CleanupSocket()
        {
            try
            {
                natClient?.Close();
            }
            catch
            {
            }

            try
            {
                natClient?.Dispose();
            }
            catch
            {
            }

            natClient = null;
        }

        // =========================
        // STOP
        // =========================

        public void StopNatKeepAlive()
        {
            lock (sync)
            {
                if (!natKeepAliveRunning)
                    return;

                Logger.Log("nat", "stop requested");
                natToken?.Cancel();
            }
        }
    }
}