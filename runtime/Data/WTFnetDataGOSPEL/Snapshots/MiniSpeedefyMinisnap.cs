using System;

internal sealed class MiniSpeedifyMiniSnap
{
    public DateTime TimeUtc { get; set; }
    public bool IsFresh { get; set; }

    // 🔹 Tunnel state
    public bool TunnelConnected { get; set; }
    public int ActiveConnectionCount { get; set; }
    public int BackupConnectionCount { get; set; }

    // 🔹 Latency (FastPing-aligned)
    public double TunnelAvgPingMs { get; set; }
    public double TunnelReportedPingMs { get; set; }

    // 🔹 Jitter
    public double TunnelAvgJitterMs { get; set; }

    // 🔹 Congestion
    public bool IsCongested { get; set; }

    // 🔹 Base / queue (advanced)
    public double BasePingMs { get; set; }
    public double DynamicBasePingMs { get; set; }
}