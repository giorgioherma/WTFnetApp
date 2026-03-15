// ConnectionStability.cs
// Simple stability scoring for comparing network paths (WiFi vs LTE etc)
// Purpose: prefer stable links, not just lowest latency.

public static class ConnectionStability
{
    // Returns stability score between ~0 and 1
    // Higher = more stable
    public static double Score(double latencyAvg, double jitterAvg, double lossRate)
    {
        // Normalize latency (penalize high latency slowly)
        double latScore = 1.0 / (1.0 + latencyAvg / 50.0);

        // Jitter matters more for gaming
        double jitScore = 1.0 / (1.0 + jitterAvg / 10.0);

        // Packet loss penalty
        double lossScore = 1.0 - lossRate;

        // Weighted stability score
        return
            0.45 * latScore +
            0.40 * jitScore +
            0.15 * lossScore;
    }
}

// Example usage:
//
// double wifiScore = ConnectionStability.Score(wifiLatency, wifiJitter, wifiLoss);
// double lteScore  = ConnectionStability.Score(lteLatency,  lteJitter,  lteLoss);
//
// if (wifiScore > lteScore)
//     wifi becomes primary
// else
//     LTE becomes primary