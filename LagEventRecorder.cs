using System;

namespace WTFnetApp
{
    public class LagEvent
    {
        public DateTime Time;
        public double LagScore;
        public int PreSpikeCount;
        public int TotalCount;
        public NetSample[] Samples;
    }

    public class LagEventRecorder
    {
        private const int PreSpikeSamples = 15; // 3s @ 200ms
        private const int PostSpikeSamples = 5; // 1s @ 200ms
        private const int TotalSamples = PreSpikeSamples + PostSpikeSamples;

        private readonly LagEvent reusableEvent = new LagEvent
        {
            Samples = new NetSample[TotalSamples],
            PreSpikeCount = PreSpikeSamples,
            TotalCount = TotalSamples
        };

        private bool capturing;
        private int remainingPostSamples;
        private bool eventReady;

        public bool TryCapture(MetricsSnapshot snapshot, DataSet dataSet, out LagEvent lagEvent)
        {
            lagEvent = null;

            if (snapshot == null || dataSet == null)
                return false;

            if (!capturing && snapshot.LagScore > 1.5)
            {
                capturing = true;
                remainingPostSamples = PostSpikeSamples;
                reusableEvent.Time = DateTime.Now;
                reusableEvent.LagScore = snapshot.LagScore;
                return false;
            }

            if (capturing)
            {
                remainingPostSamples--;
                if (remainingPostSamples <= 0)
                {
                    capturing = false;
                    dataSet.CopyRecentSamples(reusableEvent.Samples, TotalSamples);
                    Logger.Log("lag", $"LAG EVENT score={reusableEvent.LagScore:F2}");
                    for (int i = 0; i < reusableEvent.Samples.Length; i++)
                    {
                        var sample = reusableEvent.Samples[i];
                        if (sample == null)
                            continue;

                        Logger.Log("lagtrace",
                            $"LAT={sample.Latency:F1} Q={sample.QueueDelay:F1} QG={sample.QueueGrowth:F1} QA={sample.QueueAcceleration:F1} DR={sample.LatencyDrift:F1} RET={sample.RetransmissionGrowth:F3} SCORE={sample.CongestionScore:F1}");
                    }
                    eventReady = true;
                }
            }

            if (!eventReady)
                return false;

            eventReady = false;
            lagEvent = reusableEvent;
            return true;
        }
    }
}
