using System;

namespace WTFnetApp
{
    public class LagLearningEngine
    {
        private const int TickMs = 200;
        private const int SamplesPerSecond = 1000 / TickMs;
        private const int PreSpikeSamples = 2 * SamplesPerSecond;
        private const int RttWindowSamples = 30 * SamplesPerSecond;
        private const int TrainingCapacity = 32;

        private readonly double[] rttWindow = new double[RttWindowSamples];
        private int rttIndex;
        private int rttCount;

        private readonly LagPattern[] trainingSet = new LagPattern[TrainingCapacity];
        private int trainingIndex;
        private int trainingCount;
        private int lagSamples;

        private readonly ConfidenceEstimator confidenceEstimator = new ConfidenceEstimator();
        private readonly AdaptiveThresholdController thresholdController = new AdaptiveThresholdController();

        private bool pendingPrediction;
        private DateTime pendingPredictionTime;

        public int LagSamples => lagSamples;
        public double Confidence => confidenceEstimator.Confidence;
        public double DynamicThreshold => thresholdController.DynamicThreshold;

        public void Update(MetricsSnapshot snapshot, DataSet dataSet, bool learningEnabled)
        {
            if (snapshot == null)
                return;

            double rtt = snapshot.ExternalPingMs > 0 ? snapshot.ExternalPingMs : snapshot.Recent;
            AddRtt(rtt);

            double baselineRtt = GetBaselineRtt();
            bool lagSpike = IsLagSpike(rtt, baselineRtt);

            LagPattern signature = BuildSignature();
            double patternMatch = ComputePatternMatch(snapshot, signature);
            snapshot.LearningConfidence = Confidence;
            snapshot.PatternMatchScore = patternMatch;
            snapshot.SignatureAvgQueueGrowth = signature.AvgQueueGrowth;

            if (!learningEnabled)
            {
                pendingPrediction = false;
                return;
            }

            HandlePredictionTracking(lagSpike, patternMatch);

            if (lagSpike)
            {
                lagSamples++;
                CaptureLagPattern(dataSet);
            }
        }

        private void HandlePredictionTracking(bool lagSpike, double patternMatch)
        {
            DateTime now = DateTime.UtcNow;

            if (pendingPrediction && (now - pendingPredictionTime).TotalSeconds >= 1)
            {
                pendingPrediction = false;
                if (!lagSpike)
                    thresholdController.RegisterFalsePositive();
            }

            if (patternMatch > thresholdController.DynamicThreshold && !pendingPrediction)
            {
                pendingPrediction = true;
                pendingPredictionTime = now;
                confidenceEstimator.RecordPatternOccurrence();
            }

            if (lagSpike && pendingPrediction)
            {
                pendingPrediction = false;
                confidenceEstimator.RecordLagOccurrence();
                thresholdController.RegisterLagMatch();
            }
        }

        private bool IsLagSpike(double currentRtt, double baselineRtt)
        {
            if (currentRtt <= 0 || baselineRtt <= 0)
                return false;

            bool aboveBase = currentRtt > baselineRtt + 60;
            bool jump1s = false;

            if (rttCount > SamplesPerSecond)
            {
                int idx = (rttIndex - SamplesPerSecond - 1 + RttWindowSamples) % RttWindowSamples;
                double prev = rttWindow[idx];
                if (prev > 0)
                    jump1s = currentRtt - prev > 40;
            }

            return aboveBase || jump1s;
        }

        private void CaptureLagPattern(DataSet dataSet)
        {
            if (dataSet == null || dataSet.Count == 0)
                return;

            var window = new NetSample[PreSpikeSamples];
            dataSet.CopyRecentSamples(window, PreSpikeSamples);

            double qg = 0;
            double drift = 0;
            double rg = 0;
            double score = 0;
            int n = 0;

            for (int i = 0; i < window.Length; i++)
            {
                var s = window[i];
                if (s == null)
                    continue;

                qg += s.QueueGrowth;
                drift += s.LatencyDrift;
                rg += s.RetransmissionGrowth;
                score += s.CongestionScore;
                n++;
            }

            if (n == 0)
                return;

            trainingSet[trainingIndex] = new LagPattern
            {
                AvgQueueGrowth = qg / n,
                AvgLatencyDrift = drift / n,
                AvgRetransmissionGrowth = rg / n,
                AvgCongestionScore = score / n,
                Samples = n
            };

            trainingIndex = (trainingIndex + 1) % TrainingCapacity;
            if (trainingCount < TrainingCapacity)
                trainingCount++;
        }

        private LagPattern BuildSignature()
        {
            var signature = new LagPattern();
            if (trainingCount == 0)
                return signature;

            for (int i = 0; i < trainingCount; i++)
            {
                signature.AvgQueueGrowth += trainingSet[i].AvgQueueGrowth;
                signature.AvgLatencyDrift += trainingSet[i].AvgLatencyDrift;
                signature.AvgRetransmissionGrowth += trainingSet[i].AvgRetransmissionGrowth;
                signature.AvgCongestionScore += trainingSet[i].AvgCongestionScore;
            }

            double n = trainingCount;
            signature.AvgQueueGrowth /= n;
            signature.AvgLatencyDrift /= n;
            signature.AvgRetransmissionGrowth /= n;
            signature.AvgCongestionScore /= n;
            signature.Samples = trainingCount;
            return signature;
        }

        private double ComputePatternMatch(MetricsSnapshot s, LagPattern signature)
        {
            double qg = Normalize(s.QueueGrowth, signature.AvgQueueGrowth);
            double drift = Normalize(s.LatencyDrift, signature.AvgLatencyDrift);
            double rg = Normalize(s.RetransmissionGrowth, signature.AvgRetransmissionGrowth);
            double cs = Normalize(s.CongestionScore, signature.AvgCongestionScore);

            return 0.35 * qg + 0.25 * drift + 0.25 * rg + 0.15 * cs;
        }

        private double Normalize(double value, double signatureValue)
        {
            double denom = Math.Max(1.0, Math.Abs(signatureValue));
            double normalized = value / denom;
            if (normalized < 0) return 0;
            if (normalized > 1) return 1;
            return normalized;
        }

        private void AddRtt(double rtt)
        {
            if (rtt <= 0)
                return;

            rttWindow[rttIndex] = rtt;
            rttIndex = (rttIndex + 1) % RttWindowSamples;
            if (rttCount < RttWindowSamples)
                rttCount++;
        }

        public double GetBaselineRtt()
        {
            if (rttCount == 0)
                return 0;

            double[] temp = new double[rttCount];
            int n = 0;
            for (int i = 0; i < rttCount; i++)
            {
                double v = rttWindow[i];
                if (v > 0)
                    temp[n++] = v;
            }

            if (n == 0)
                return 0;

            Array.Sort(temp, 0, n);
            int idx = (int)(n * 0.2);
            if (idx >= n)
                idx = n - 1;

            return temp[idx];
        }
    }
}
