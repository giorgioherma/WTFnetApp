using System;

namespace WTFnetApp
{
    public sealed class FastPingTelemetrySnapshot
    {
        public string PrimaryTarget;
        public string SecondaryTarget;

        public double InstantMs = -1;
        public double StableMs = -1;
        public double BaselineMs = -1;
        public double FloorMs = -1;
        public double JitterMs = -1;
        public double DisplayedPingMs = -1;
        public double TunnelLatencyMs = -1;

        public double Confidence = 0;
        public double LossRate = 0;
        public double BadQualityRate = 0;
        public double PrimarySecondaryDivergenceMs = 0;

        public bool SpikeDetected;
        public bool IsStale;
        public bool PrimaryFresh;
        public bool SecondaryFresh;
        public bool IsEvaluationMode;

        public double PrimaryInstantMs = -1;
        public double PrimaryStableMs = -1;
        public double PrimaryFloorMs = -1;
        public double PrimaryBaselineMs = -1;
        public double PrimaryJitterMs = -1;
        public double PrimaryLossRate = 0;
        public double PrimaryBadQualityRate = 0;
        public double PrimaryConfidence = 0;

        public double SecondaryInstantMs = -1;
        public double SecondaryStableMs = -1;
        public double SecondaryFloorMs = -1;
        public double SecondaryBaselineMs = -1;
        public double SecondaryJitterMs = -1;
        public double SecondaryLossRate = 0;
        public double SecondaryBadQualityRate = 0;
        public double SecondaryConfidence = 0;

        public string HealthState;
    }
}
