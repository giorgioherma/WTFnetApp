using System.Collections.Generic;

namespace WTFnetApp
{
    internal sealed class MiniSpeedifyMinisnap
    {
        // Core tunnel metrics (aggregate "speedify" row)
        public double TunnelLatencyMs { get; set; } = -1;
        public double AvgSpeedifyJitterMs { get; set; } = -1;

        // Transport stats (best-effort / optional)
        public double PacketLossPercent { get; set; } = -1;
        public double RetransmitRate { get; set; } = -1;
        public bool IsCongested { get; set; }

        // Per-link rows
        public List<SpeedifyLinkRow> Links { get; } = new List<SpeedifyLinkRow>();
    }

    internal sealed class SpeedifyLinkRow
    {
        public double LatencyMs { get; set; } = -1;
    }
}