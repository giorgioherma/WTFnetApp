namespace WTFnetApp
{
    public class AggressionController
    {
        public double AggressionFactor { get; private set; } = 0.5;

        public double Update(double lagPressure)
        {
            if (lagPressure < 0) lagPressure = 0;
            if (lagPressure > 1) lagPressure = 1;

            AggressionFactor = 0.5 + lagPressure * 1.5;
            return AggressionFactor;
        }
    }
}
