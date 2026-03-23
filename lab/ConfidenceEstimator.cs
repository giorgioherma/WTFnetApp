using System;
using System.Threading;

namespace WTFnetApp
{
    public class ConfidenceEstimator
    {
        private int lagOccurrences;
        private int totalPatternOccurrences;

        public double Confidence
        {
            get
            {
                int total = totalPatternOccurrences;
                if (total <= 0)
                    return 0;

                return Math.Max(0, Math.Min(1,
                    (double)lagOccurrences / total));
            }
        }

        public void RecordPatternOccurrence()
        {
            Interlocked.Increment(ref totalPatternOccurrences);
        }

        public void RecordLagOccurrence()
        {
            Interlocked.Increment(ref lagOccurrences);
        }

        public void Reset()
        {
            lagOccurrences = 0;
            totalPatternOccurrences = 0;
        }
    }
}