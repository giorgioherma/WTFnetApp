using System;

namespace WTFnetApp
{
    public enum NetworkState
    {
        Healthy,
        Degraded
    }

    public class FailureDetector
    {
        private DateTime? highRttSince;
        private DateTime? highLossSince;
        private DateTime? packetGapSince;
        private DateTime? recoverySince;

        public NetworkState State { get; private set; } = NetworkState.Healthy;

        public bool Update(MetricsSnapshot snapshot, double baselineRtt, out bool recovered)
        {
            recovered = false;
            if (snapshot == null)
                return State == NetworkState.Degraded;

            double rtt = snapshot.ExternalPingMs > 0 ? snapshot.ExternalPingMs : snapshot.Recent;
            bool highRtt = baselineRtt > 0 && rtt > baselineRtt + 500;
            bool highLoss = snapshot.PacketLoss > 0.30;
            bool packetGap = snapshot.ReceiveBytesPerSecond < 1;

            TouchSince(ref highRttSince, highRtt);
            TouchSince(ref highLossSince, highLoss);
            TouchSince(ref packetGapSince, packetGap);

            bool degraded = Held(highRttSince, 5) || Held(packetGapSince, 2) || Held(highLossSince, 3);
            if (degraded)
                State = NetworkState.Degraded;

            if (State == NetworkState.Degraded)
            {
                bool recoveredRtt = baselineRtt > 0 && rtt < baselineRtt + 120;
                TouchSince(ref recoverySince, recoveredRtt);
                if (Held(recoverySince, 3))
                {
                    State = NetworkState.Healthy;
                    highRttSince = null;
                    highLossSince = null;
                    packetGapSince = null;
                    recoverySince = null;
                    recovered = true;
                }
            }
            else
            {
                recoverySince = null;
            }

            return State == NetworkState.Degraded;
        }

        private void TouchSince(ref DateTime? since, bool condition)
        {
            if (condition)
            {
                if (!since.HasValue)
                    since = DateTime.UtcNow;
            }
            else
            {
                since = null;
            }
        }

        private bool Held(DateTime? since, double seconds)
        {
            return since.HasValue && (DateTime.UtcNow - since.Value).TotalSeconds >= seconds;
        }
    }
}
