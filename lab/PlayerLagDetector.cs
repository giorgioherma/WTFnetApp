using System;

namespace WTFnetApp
{
    public enum LagEventType
    {
        None,
        MinorJitter,
        Rubberband,
        Teleport,
        Freeze
    }

    public struct PendingLagEvent
    {
        public bool Active;
        public DateTime Timestamp;
        public LagEventType EventType;
    }

    public class PlayerLagDetector
    {
        private const int TickMs = 200;
        private const int RttWindowSamples = 10;

        private readonly double[] rttWindow = new double[RttWindowSamples];
        private int rttIndex;
        private int rttCount;

        private PendingLagEvent pending;
        private double lastRtt = -1;
        private double prevReceiveBps;
        private bool prevGap;

        public int PendingLagEvents => pending.Active ? 1 : 0;

        public LagEventType Update(MetricsSnapshot snapshot, double baselineRtt, double lagThresholdScale)
        {
            if (snapshot == null)
                return LagEventType.None;

            double rtt = snapshot.ExternalPingMs > 0 ? snapshot.ExternalPingMs : snapshot.Recent;
            AddRtt(rtt);

            double receive = snapshot.ReceiveBytesPerSecond;
            double packetGapMs = receive <= 1 ? TickMs : 0;
            bool packetGap = packetGapMs > 0;
            bool packetBurst = prevGap && receive > Math.Max(2000, prevReceiveBps * 1.5);

            LagEventType detected = DetectEvent(snapshot, baselineRtt, rtt, packetGapMs, packetBurst, lagThresholdScale);
            if (detected != LagEventType.None && !pending.Active)
            {
                pending.Active = true;
                pending.Timestamp = DateTime.UtcNow;
                pending.EventType = detected;
            }

            LagEventType confirmed = LagEventType.None;
            if (pending.Active)
            {
                if (ShouldCancel(snapshot, baselineRtt, rtt, packetGap, packetBurst))
                {
                    pending.Active = false;
                }
                else if ((DateTime.UtcNow - pending.Timestamp).TotalSeconds >= 1.5)
                {
                    confirmed = pending.EventType;
                    pending.Active = false;
                }
            }

            prevGap = packetGap;
            prevReceiveBps = receive;
            lastRtt = rtt;
            return confirmed;
        }

        private LagEventType DetectEvent(MetricsSnapshot s, double baselineRtt, double rtt, double packetGapMs, bool packetBurst, double lagThresholdScale)
        {
            double variance = ComputeRttVariance();
            if (variance > 15)
                return LagEventType.MinorJitter;

            if (baselineRtt <= 0)
                return LagEventType.None;

            double rubberbandThreshold = baselineRtt + 80 * lagThresholdScale;
            double freezeThreshold = baselineRtt + 120 * lagThresholdScale;

            bool nearBaselineFastReturn = lastRtt > 0 && lastRtt > rubberbandThreshold && rtt <= baselineRtt + 20;
            if (rtt > rubberbandThreshold && nearBaselineFastReturn)
                return LagEventType.Rubberband;

            if (packetGapMs > 300 && packetBurst)
                return LagEventType.Teleport;

            if (rtt > freezeThreshold && packetGapMs > 200)
                return LagEventType.Freeze;

            return LagEventType.None;
        }

        private bool ShouldCancel(MetricsSnapshot s, double baselineRtt, double rtt, bool packetGap, bool packetBurst)
        {
            bool noQueueBuild = s.QueueGrowth < 0.2;
            bool loadingTraffic = s.ReceiveBytesPerSecond < 100;
            bool extremeSpike = baselineRtt > 0 && rtt > baselineRtt + 250;
            bool quickReturn = baselineRtt > 0 && rtt <= baselineRtt + 20;

            return (extremeSpike && noQueueBuild) || loadingTraffic || (packetGap && packetBurst) || quickReturn;
        }

        private void AddRtt(double rtt)
        {
            if (rtt <= 0)
                return;

            rttWindow[rttIndex] = rtt;
            rttIndex = (rttIndex + 1) % RttWindowSamples;
            if (rttCount < RttWindowSamples)
                rttCount++;
        }

        private double ComputeRttVariance()
        {
            if (rttCount <= 1)
                return 0;

            double sum = 0;
            for (int i = 0; i < rttCount; i++)
                sum += rttWindow[i];

            double mean = sum / rttCount;
            double var = 0;
            for (int i = 0; i < rttCount; i++)
            {
                double d = rttWindow[i] - mean;
                var += d * d;
            }

            return Math.Sqrt(var / rttCount);
        }
    }
}
