using System;
using System.Data;
using System.Drawing;
using System.Threading.Tasks;

namespace WTFnetApp
{
    public sealed class OverlayRenderState
    {
        public string PingText = "-- ms";
        public Color PingColor = Color.Gray;

        public string JitterText = "J --";
        public Color JitterColor = Color.DeepSkyBlue;

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
        private readonly NetworkEngineState state = new NetworkEngineState();

        public NetworkMetricsEngine()
        {
            state.Bank = dataSet.Bank;
        }

        public Task StopAsync()
        {
            return Task.CompletedTask;
        }

        public void Update(MetricsSnapshot snapshot, UnifiedNetworkState unified, NatState natState)
        {
            if (unified == null)
                return;

            if (snapshot != null)
                dataSet.Add(snapshot);

            state.PingMs = unified.DisplayedPingMs > 0
                ? unified.DisplayedPingMs
                : unified.LatencyMs;

            state.PingColorKey = unified.DisplayPingColorKey;
            state.TunnelState = unified.DisplayTunnelState;

            BuildOverlayState(unified);
            FillBank(unified);
        }

        public NetworkEngineState GetState()
        {
            return state;
        }

        public DataSet GetDataSet()
        {
            return dataSet;
        }

        private void BuildOverlayState(UnifiedNetworkState unified)
        {
            state.Overlay.PingText = string.IsNullOrWhiteSpace(unified.DisplayPingText)
                ? "-- ms"
                : unified.DisplayPingText;

            state.Overlay.PingColor = GetColorFromKey(unified.DisplayPingColorKey);

            int jitter = unified.JitterMs > 0
                ? (int)Math.Round(unified.JitterMs)
                : -1;

            state.Overlay.JitterText = jitter >= 0
                ? "J " + jitter.ToString()
                : "J --";

            state.Overlay.JitterColor = GetJitterColor(unified.JitterMs, unified.IsStale);

            state.Overlay.NatText = string.IsNullOrWhiteSpace(unified.DisplayNatText)
                ? "Unknown"
                : unified.DisplayNatText;

            state.Overlay.NatColor = GetColorFromKey(unified.DisplayNatColorKey);
        }

        private void FillBank(UnifiedNetworkState unified)
        {
            DashboardBank bank = dataSet.Bank;

            bank.LastUpdateUtc = DateTime.UtcNow;

            bank.PingMs = state.PingMs;
            bank.PingColorKey = state.PingColorKey;
            bank.TunnelState = state.TunnelState;

            bank.TunnelConnected = unified.TunnelConnected;
            bank.ConnectedServerName = unified.ConnectedServerName;
            bank.ConnectedServerPublicIp = unified.ConnectedServerPublicIp;

            bank.AggregateJitterMs = unified.JitterMs > 0
                ? unified.JitterMs
                : (unified.AggregateJitterMs > 0 ? unified.AggregateJitterMs : unified.MicroJitterMs);

            bank.AggregateRxBps = unified.AggregateRxBps;
            bank.AggregateTxBps = unified.AggregateTxBps;
            bank.AggregateTotalBps = unified.AggregateTotalBps;

            bank.ActiveConnectionCount = unified.ActiveConnectionCount;
            bank.BackupConnectionCount = unified.BackupConnectionCount;

            bank.PrimaryRemoteIp = !string.IsNullOrWhiteSpace(unified.PrimaryRemoteIp)
                ? unified.PrimaryRemoteIp
                : unified.FastPingPrimaryTarget;

            bank.SpikeDetected = unified.SpikeDetected;
            bank.PingConfidence = unified.Confidence;
            bank.PingStale = unified.IsStale;
        }

        private Color GetJitterColor(double jitterMs, bool isStale)
        {
            if (isStale || jitterMs < 0)
                return Color.Gray;

            if (jitterMs <= 5)
                return Color.DeepSkyBlue;

            if (jitterMs <= 10)
                return Color.LimeGreen;

            if (jitterMs <= 18)
                return Color.Gold;

            if (jitterMs <= 28)
                return Color.Orange;

            return Color.Red;
        }

        private Color GetColorFromKey(string key)
        {
            switch ((key ?? string.Empty).ToLowerInvariant())
            {
                case "green":
                    return Color.LimeGreen;

                case "yellow":
                    return Color.Orange;

                case "gold":
                    return Color.Gold;

                case "red":
                    return Color.Red;

                case "gray":
                default:
                    return Color.Gray;
            }
        }
    }
}