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
                if (totalPatternOccurrences <= 0)
                    return 0;

                return (double)lagOccurrences / totalPatternOccurrences;
            }
        }

        public void RecordPatternOccurrence()
        {
            totalPatternOccurrences++;
        }

        public void RecordLagOccurrence()
        {
            lagOccurrences++;
        }
    }
}
