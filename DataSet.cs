using System;

namespace WTFnetApp
{
    public class NetSample
    {
        public DateTime Time;

        public double Latency;
        public double Jitter;
        public double Loss;

        public double Congestion;
        public double MosScore;

        public double QueueDelay;
        public double QueueGrowth;
        public double QueueAcceleration;
        public double CongestionScore;

        public bool MultiConnection;
        public bool Connected;

        public string Server;
    }

    public class DataSet
    {
        private const int MAX_SAMPLES = 75;

        private readonly NetSample[] buffer = new NetSample[MAX_SAMPLES];

        private int index = 0;
        private int count = 0;

        public void AddSample(NetSample sample)
        {
            buffer[index] = sample;

            index++;

            if (index >= MAX_SAMPLES)
                index = 0;

            if (count < MAX_SAMPLES)
                count++;
        }

        public int Count => count;

        private int StartIndex(int last)
        {
            if (last >= count)
                return 0;

            return (index - last + MAX_SAMPLES) % MAX_SAMPLES;
        }

        // =========================
        // LATENCY
        // =========================

        public int AvgLatency(int last)
        {
            if (count == 0) return 0;

            int start = StartIndex(last);
            int n = Math.Min(last, count);

            double sum = 0;

            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % MAX_SAMPLES;
                sum += buffer[idx].Latency;
            }

            return (int)(sum / n);
        }

        public int RecentLatency(int last = 5)
        {
            if (count == 0) return 0;

            int start = StartIndex(last);
            int n = Math.Min(last, count);

            double sum = 0;

            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % MAX_SAMPLES;
                sum += buffer[idx].Latency;
            }

            return (int)(sum / n);
        }

        public int MaxLatency(int last)
        {
            if (count == 0) return 0;

            int start = StartIndex(last);
            int n = Math.Min(last, count);

            double max = 0;

            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % MAX_SAMPLES;

                if (buffer[idx].Latency > max)
                    max = buffer[idx].Latency;
            }

            return (int)max;
        }

        // =========================
        // BASELINE LATENCY
        // =========================

        public int BaselineLatency(int last = 30)
        {
            if (count == 0) return 0;

            int start = StartIndex(last);
            int n = Math.Min(last, count);

            double min = double.MaxValue;

            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % MAX_SAMPLES;

                if (buffer[idx].Latency < min)
                    min = buffer[idx].Latency;
            }

            return (int)min;
        }

        // =========================
        // QUEUE DELAY
        // =========================

        public double QueueDelay(int last = 5)
        {
            int recent = RecentLatency(last);
            int baseLine = BaselineLatency();

            return Math.Max(0, recent - baseLine);
        }

        // =========================
        // JITTER
        // =========================

        public int AvgJitter(int last)
        {
            if (count == 0) return 0;

            int start = StartIndex(last);
            int n = Math.Min(last, count);

            double sum = 0;

            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % MAX_SAMPLES;
                sum += buffer[idx].Jitter;
            }

            return (int)(sum / n);
        }

        public double MicroJitter(int last = 5)
        {
            if (count < 2) return 0;

            int start = StartIndex(last);
            int n = Math.Min(last, count);

            double sum = 0;

            for (int i = 1; i < n; i++)
            {
                int idx1 = (start + i) % MAX_SAMPLES;
                int idx0 = (start + i - 1) % MAX_SAMPLES;

                sum += Math.Abs(buffer[idx1].Latency - buffer[idx0].Latency);
            }

            return sum / (n - 1);
        }

        public double JitterVariance(int last)
        {
            if (count == 0) return 0;

            int start = StartIndex(last);
            int n = Math.Min(last, count);

            double avg = AvgJitter(last);
            double sum = 0;

            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % MAX_SAMPLES;

                double d = buffer[idx].Jitter - avg;
                sum += d * d;
            }

            return sum / n;
        }

        // =========================
        // LOSS
        // =========================

        public double AvgLoss(int last)
        {
            if (count == 0) return 0;

            int start = StartIndex(last);
            int n = Math.Min(last, count);

            double sum = 0;

            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % MAX_SAMPLES;
                sum += buffer[idx].Loss;
            }

            return sum / n;
        }

        public double LossBurst(int last = 10)
        {
            if (count == 0) return 0;

            int start = StartIndex(last);
            int n = Math.Min(last, count);

            double max = 0;

            for (int i = 0; i < n; i++)
            {
                int idx = (start + i) % MAX_SAMPLES;

                if (buffer[idx].Loss > max)
                    max = buffer[idx].Loss;
            }

            return max;
        }

        // =========================
        // LATENCY TREND
        // =========================

        public double LatencySlope(int last)
        {
            if (count < 2)
                return 0;

            int start = StartIndex(last);
            int n = Math.Min(last, count);

            double first = buffer[start].Latency;
            double lastVal = buffer[(start + n - 1) % MAX_SAMPLES].Latency;

            return lastVal - first;
        }

        public double LatencyDelta()
        {
            if (count < 2)
                return 0;

            int last = (index - 1 + MAX_SAMPLES) % MAX_SAMPLES;
            int prev = (last - 1 + MAX_SAMPLES) % MAX_SAMPLES;

            return buffer[last].Latency - buffer[prev].Latency;
        }

        // =========================
        // LATEST
        // =========================

        public NetSample Latest()
        {
            if (count == 0)
                return null;

            int last = (index - 1 + MAX_SAMPLES) % MAX_SAMPLES;
            return buffer[last];
        }

        public NetSample[] GetSamples()
        {
            NetSample[] arr = new NetSample[count];

            for (int i = 0; i < count; i++)
            {
                int idx = (index - count + i + MAX_SAMPLES) % MAX_SAMPLES;
                arr[i] = buffer[idx];
            }

            return arr;
        }
    }
}
