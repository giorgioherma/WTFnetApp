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

            state.PingColorKey = GetPingColorKey(unified);
            state.TunnelState = BuildTunnelState(unified);

            BuildOverlayState(unified, natState);
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

        private void BuildOverlayState(UnifiedNetworkState unified, NatState natState)
        {
            state.Overlay.PingText = FormatPingText(state.PingMs);
            state.Overlay.PingColor = GetPingColor(unified);
            state.Overlay.NatText = natState.ToString();
            state.Overlay.NatColor = GetNatColor(natState);
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

            bank.AggregateJitterMs = unified.AggregateJitterMs > 0
                ? unified.AggregateJitterMs
                : unified.JitterMs;

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

        private string BuildTunnelState(UnifiedNetworkState unified)
        {
            if (unified == null)
                return "Unknown";

            if (unified.SourceKind == UnifiedNetworkSourceKind.FastPing)
                return "No Tunnel";

            if (unified.TunnelConnected)
                return "Tunnel OK";

            if (unified.HasSpeedify || unified.SpeedifyAvailable)
                return "Tunnel Down";

            return "Unknown";
        }

        private string FormatPingText(double pingMs)
        {
            if (pingMs <= 0)
                return "-- ms";

            return ((int)Math.Round(pingMs)).ToString() + " ms";
        }

        private Color GetPingColor(UnifiedNetworkState unified)
        {
            if (unified == null || unified.IsStale || state.PingMs <= 0)
                return Color.Gray;

            switch (unified.HealthState)
            {
                case UnifiedNetworkHealthState.Critical:
                    return Color.Red;

                case UnifiedNetworkHealthState.Warning:
                    return Color.OrangeRed;

                case UnifiedNetworkHealthState.OffBaseline:
                    return Color.Orange;

                case UnifiedNetworkHealthState.Healthy:
                    return Color.LimeGreen;

                case UnifiedNetworkHealthState.Stale:
                    return Color.Gray;

                case UnifiedNetworkHealthState.Unknown:
                default:
                    break;
            }

            if (state.PingMs < 50)
                return Color.LimeGreen;

            if (state.PingMs < 90)
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

        private string GetPingColorKey(UnifiedNetworkState unified)
        {
            if (unified == null || unified.IsStale || state.PingMs <= 0)
                return "gray";

            switch (unified.HealthState)
            {
                case UnifiedNetworkHealthState.Critical:
                    return "red";

                case UnifiedNetworkHealthState.Warning:
                    return "red";

                case UnifiedNetworkHealthState.OffBaseline:
                    return "yellow";

                case UnifiedNetworkHealthState.Healthy:
                    return "green";

                case UnifiedNetworkHealthState.Stale:
                    return "gray";

                case UnifiedNetworkHealthState.Unknown:
                default:
                    break;
            }

            if (state.PingMs <= 50)
                return "green";

            if (state.PingMs <= 90)
                return "yellow";

            return "red";
        }
    }
}