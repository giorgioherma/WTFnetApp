using System;
using System.Collections.Generic;

public class NetSample
{
    public DateTime Time;
    public double Latency;
    public double Jitter;
    public double Congestion;
    public double MosScore;
}

public class DataSet
{
    private readonly Queue<NetSample> samples = new Queue<NetSample>();

    private const int MAX_SAMPLES = 75;

    public void AddSample(NetSample sample)
    {
        samples.Enqueue(sample);

        if (samples.Count > MAX_SAMPLES)
            samples.Dequeue();
    }

    public NetSample[] GetSamples()
    {
        return samples.ToArray();
    }

    public int Count
    {
        get { return samples.Count; }
    }
}
public class Example
{
    public static void Run()
    {
        DataSet dataSet = new DataSet();

        dataSet.AddSample(new NetSample
        {
            Time = DateTime.UtcNow,
            Latency = 42,
            Jitter = 5,
            Congestion = 0.25,
            MosScore = 4.2
        });
    }
}