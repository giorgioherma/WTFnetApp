using System;
using System.Drawing;
using System.Threading.Tasks;

namespace WTFnetApp
{
    public sealed class OverlayRenderState
    {
        public string PingText = "-- ms";
        public Color PingColor = Color.Gray;

        public string NatText = "Unknown";
        public Color NatColor = Color.Gray;
    }

    public sealed class NetworkEngineState
    {
        public double PingMs = -1;
        public string PingColorKey = "gray";
        public string TunnelState = "Unknown";
        public DashboardBank Bank;
        public OverlayRenderState Overlay = new OverlayRenderState();
    }

    public sealed class NetworkMetricsEngine
    {
        private readonly DataSet dataSet = new DataSet();
        private readonly WtfFastPing fastPing = new WtfFastPing();
        private readonly NetworkEngineState state = new NetworkEngineState();

        public NetworkMetricsEngine()
        {
            state.Bank = dataSet.Bank;
            fastPing.Start();
        }

        public async Task StopAsync()
        {
            await fastPing.StopAsync();
        }

        public void Update(MetricsSnapshot snapshot, NatState natState)
        {
            if (snapshot == null)
                return;

            dataSet.Add(snapshot);

            fastPing.UpdateTunnelLatency(snapshot.AggregateLatencyMs);

            string primaryTarget = snapshot.PrimaryRemoteIp;
            if (string.IsNullOrWhiteSpace(primaryTarget))
                primaryTarget = snapshot.ConnectedServerPublicIp;

            if (!string.IsNullOrWhiteSpace(primaryTarget))
                fastPing.UpdateTargets(primaryTarget, "1.1.1.1");

            FastPingState pingState = fastPing.GetState();

            state.PingMs = pingState.DisplayedPingMs;
            state.PingColorKey = GetPingColorKey(pingState);
            state.TunnelState = snapshot.TunnelConnected ? "Tunnel OK" : "Tunnel Down";

            BuildOverlayState(pingState, natState);
            FillBank(snapshot, pingState);
        }

        public NetworkEngineState GetState()
        {
            return state;
        }

        public DataSet GetDataSet()
        {
            return dataSet;
        }

        private void BuildOverlayState(FastPingState pingState, NatState natState)
        {
            state.Overlay.PingText = FormatPingText(state.PingMs);
            state.Overlay.PingColor = GetPingColor(state.PingMs, pingState);
            state.Overlay.NatText = natState.ToString();
            state.Overlay.NatColor = GetNatColor(natState);
        }
        
        private void FillBank(MetricsSnapshot snapshot, FastPingState pingState)
        {
            DashboardBank bank = dataSet.Bank;

            bank.LastUpdateUtc = DateTime.UtcNow;

            bank.PingMs = state.PingMs;
            bank.PingColorKey = state.PingColorKey;
            bank.TunnelState = state.TunnelState;

            bank.TunnelConnected = snapshot.TunnelConnected;
            bank.ConnectedServerName = snapshot.ConnectedServerName;
            bank.ConnectedServerPublicIp = snapshot.ConnectedServerPublicIp;

            bank.AggregateJitterMs = snapshot.AggregateJitterMs;
            bank.AggregateRxBps = snapshot.AggregateRxBps;
            bank.AggregateTxBps = snapshot.AggregateTxBps;
            bank.AggregateTotalBps = snapshot.AggregateTotalBps;

            bank.ActiveConnectionCount = snapshot.ActiveConnectionCount;
            bank.BackupConnectionCount = snapshot.BackupConnectionCount;

            bank.PrimaryRemoteIp = snapshot.PrimaryRemoteIp;

            bank.SpikeDetected = pingState.SpikeDetected;
            bank.PingConfidence = pingState.Confidence;
            bank.PingStale = pingState.IsStale;
        }

        private string FormatPingText(double pingMs)
        {
            if (pingMs <= 0)
                return "-- ms";

            return ((int)Math.Round(pingMs)).ToString() + " ms";
        }

        private Color GetPingColor(double pingMs, FastPingState pingState)
        {
            if (pingState == null || pingState.IsStale || pingMs <= 0)
                return Color.Gray;

            if (pingMs < 50)
                return Color.LimeGreen;

            if (pingMs < 90)
                return Color.Orange;

            return Color.Red;
        }

        private Color GetNatColor(NatState natState)
        {
            switch (natState)
            {
                case NatState.Lost:
                    return Color.Red;

                case NatState.Unstable:
                    return Color.Orange;

                case NatState.GracePeriod:
                    return Color.Gold;

                case NatState.Stable:
                    return Color.LimeGreen;

                default:
                    return Color.Gray;
            }
        }

        private string GetPingColorKey(FastPingState pingState)
        {
            if (pingState == null || pingState.IsStale || pingState.DisplayedPingMs <= 0)
                return "gray";

            double ping = pingState.DisplayedPingMs;

            if (ping <= 50)
                return "green";

            if (ping <= 90)
                return "yellow";

            return "red";
        }
    }
}