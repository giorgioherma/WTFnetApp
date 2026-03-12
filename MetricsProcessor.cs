using System;
using System.Linq;
using System.Collections.Generic;

public class MetricsProcessor
{
    private readonly DataSet data;

    public MetricsProcessor(DataSet dataSet)
    {
        data = dataSet;
    }

    private List<NetSample> Samples()
    {
        return data.GetSamples().ToList();
    }
    public double LatencyAvg()
    {
        var samples = Samples();
        if (samples.Count == 0) return 0;

        return samples.Average(s => s.Latency);
    }

    public double JitterAvg()
    {
        var samples = Samples();
        if (samples.Count == 0) return 0;

        return samples.Average(s => s.Jitter);
    }

    public double JitterVariance()
    {
        var samples = Samples();
        if (samples.Count == 0) return 0;

        double avg = samples.Average(s => s.Jitter);
        return samples.Average(s => Math.Pow(s.Jitter - avg, 2));
    }

    public double CongestionLevel()
    {
        var samples = Samples();
        if (samples.Count == 0) return 0;

        return samples.Last().Congestion;
    }

    public double CongestionTrend()
    {
        var samples = Samples();
        if (samples.Count < 2) return 0;

        return samples.Last().Congestion - samples.First().Congestion;
    }

    public double CalculateSpfScore()
    {
        var samples = Samples();
        if (samples.Count == 0) return 0;

        var s = samples.Last();

        double latScore = Math.Max(0, 1 - (s.Latency / 200));
        double jitScore = Math.Max(0, 1 - (s.Jitter / 50));
        double congScore = 1 - s.Congestion;
        double mosScore = s.MosScore / 5;

        return
            0.40 * latScore +
            0.35 * jitScore +
            0.15 * congScore +
            0.10 * mosScore;
    }

    public double CalculateTrend()
    {
        var samples = data.GetSamples();
        int count = samples.Length;

        if (count < 10)
            return 0;

        // Short window: recent samples
        int shortWindow = 3;

        // Long window: baseline samples
        int longWindow = 10;

        if (count < longWindow)
            return 0;

        double shortSum = 0;
        double longSum = 0;

        // Recent latency average
        for (int i = count - shortWindow; i < count; i++)
        {
            shortSum += samples[i].Latency;
        }

        // Older baseline latency average
        for (int i = count - longWindow; i < count - shortWindow; i++)
        {
            longSum += samples[i].Latency;
        }

        double shortAvg = shortSum / shortWindow;
        double longAvg = longSum / (longWindow - shortWindow);

        return shortAvg - longAvg;
    }

    public bool PredictSpike()
    {
        var samples = Samples();
        if (samples.Count < 10) return false;

        var last = samples.Last();
        var earlier = samples[samples.Count - 10];

        double jitterSlope = last.Jitter - earlier.Jitter;
        double congestionSlope = last.Congestion - earlier.Congestion;

        return jitterSlope > 5 && congestionSlope > 0.1;
    }

    public string ConnectionState()
    {
        double spf = CalculateSpfScore();

        if (PredictSpike()) return "Danger";
        if (spf > 0.8) return "Stable";
        if (spf > 0.6) return "Warning";

        return "Poor";
    }
}