using System;

namespace WTFnetApp
{
    public enum NetworkControlState
    {
        Calibration,
        Normal,
        Warning,
        Panic
    }

    public class DecisionEngine
    {
        private const double QueueGrowthSignalThreshold = 0.2;
        private const double QueueAccelerationSignalThreshold = 0.1;
        private const double LatencyDriftSignalThreshold = 4.0;
        private const double NormalToWarningPatternThreshold = 0.6;
        private const double WarningToNormalPatternThreshold = 0.45;
        private const double PanicExitConfidenceThreshold = 0.30;
        private const double PanicEnterConfidenceThreshold = 0.45;
        private const double PanicEnterLearningConfidenceThreshold = 0.65;
        private const double PanicEnterBaseThreshold = 0.65;
        private const double PanicLeaveBaseThreshold = 0.50;
        private readonly LagLearningEngine learningEngine = new LagLearningEngine();
        private readonly LagPressureController lagPressureController = new LagPressureController();
        private readonly PlayerLagDetector playerLagDetector = new PlayerLagDetector();
        private readonly AggressionController aggressionController = new AggressionController();
        private readonly LearningGate learningGate = new LearningGate();
        private readonly FailureDetector failureDetector = new FailureDetector();
        private readonly ServerQualityEstimator serverQuality = new ServerQualityEstimator();

        private NetworkControlState state = NetworkControlState.Calibration;
        private int panicHoldTicks;
        private int persistenceTicks;

        public NetworkControlState CurrentState => state;
        public double CurrentLearningThreshold => learningEngine.DynamicThreshold;

        public void ApplySlowThresholdDelta(double delta)
        {
            learningEngine.ApplySlowThresholdDelta(delta);
        }

        public NetworkControlState Update(MetricsSnapshot snapshot, DataSet dataSet)
        {
            if (snapshot == null)
                return state;

            double baselineRtt = learningEngine.GetBaselineRtt();
            serverQuality.Update(baselineRtt, lagPressureController);

            bool degraded = failureDetector.Update(snapshot, baselineRtt, out bool recovered);
            if (recovered)
                lagPressureController.OnRecovery();

            lagPressureController.UpdateDecay(failureDetector.State, serverQuality.ServerScore);

            double thresholdScale = 1.0 / serverQuality.ServerScore;
            LagEventType lagEventType = playerLagDetector.Update(snapshot, baselineRtt, thresholdScale);
            if (lagEventType != LagEventType.None)
                lagPressureController.ApplyEvent(lagEventType, serverQuality.ServerScore);

            double aggression = aggressionController.Update(lagPressureController.LagPressure);

            GamePhase phase = ResolveGamePhase(snapshot);
            bool learningEnabled = learningGate.Update(snapshot, baselineRtt, phase) && !degraded;
            learningEngine.Update(snapshot, dataSet, learningEnabled);

            double effectiveConfidence = learningEngine.Confidence * serverQuality.ServerScore;
            double confidenceScore = ComputeConfidence(snapshot);
            bool queueBeforeLag = snapshot.QueueGrowth > QueueGrowthSignalThreshold;
            bool persisted = confidenceScore > 0.35 || IncrementPersistence(snapshot);
            bool enoughSignals = CountPositiveSignals(snapshot) >= 2;

            double panicEnter = (PanicEnterBaseThreshold / aggression) * thresholdScale;
            double panicLeave = (PanicLeaveBaseThreshold / aggression) * thresholdScale;

            if (degraded)
            {
                state = NetworkControlState.Calibration;
            }
            else if (state == NetworkControlState.Calibration)
            {
                if (learningEngine.LagSamples >= 10)
                    state = NetworkControlState.Normal;
            }
            else if (state == NetworkControlState.Normal)
            {
                if (snapshot.PatternMatchScore > NormalToWarningPatternThreshold && queueBeforeLag && enoughSignals && persisted)
                    state = NetworkControlState.Warning;
            }
            else if (state == NetworkControlState.Warning)
            {
                bool panicReady = snapshot.PatternMatchScore > panicEnter &&
                                  effectiveConfidence > PanicEnterLearningConfidenceThreshold &&
                                  confidenceScore > (PanicEnterConfidenceThreshold / aggression) &&
                                  queueBeforeLag && enoughSignals && persisted;

                if (panicReady)
                {
                    state = NetworkControlState.Panic;
                    panicHoldTicks = Math.Max(1, (int)Math.Round(15 * aggression));
                }
                else if (snapshot.PatternMatchScore < WarningToNormalPatternThreshold)
                {
                    state = NetworkControlState.Normal;
                }
            }
            else
            {
                if (panicHoldTicks > 0)
                {
                    panicHoldTicks--;
                }
                else if (snapshot.PatternMatchScore < panicLeave || confidenceScore < PanicExitConfidenceThreshold)
                {
                    state = NetworkControlState.Warning;
                }
            }

            FillDiagnostics(snapshot, lagEventType, aggression, learningEnabled);
            return state;
        }

        private bool IncrementPersistence(MetricsSnapshot snapshot)
        {
            bool active = snapshot.QueueGrowth > QueueGrowthSignalThreshold || snapshot.QueueAcceleration > QueueAccelerationSignalThreshold || snapshot.LatencyDrift > LatencyDriftSignalThreshold || snapshot.RetransmissionGrowth > 0;
            if (active)
                persistenceTicks++;
            else
                persistenceTicks = 0;

            return persistenceTicks >= 2;
        }

        private int CountPositiveSignals(MetricsSnapshot s)
        {
            int count = 0;
            if (s.QueueGrowth > QueueGrowthSignalThreshold) count++;
            if (s.QueueAcceleration > QueueAccelerationSignalThreshold) count++;
            if (s.LatencyDrift > LatencyDriftSignalThreshold) count++;
            if (s.RetransmissionGrowth > 0) count++;
            return count;
        }

        private double ComputeConfidence(MetricsSnapshot s)
        {
            double qg = Clamp01(s.QueueGrowth / 8.0);
            double qaDenom = Math.Abs(s.SignatureAvgQueueGrowth) + 1.0;
            double qa = Clamp01(s.QueueAcceleration / qaDenom);
            double qp = Clamp01(s.QueuePressure / 100.0);
            double drift = Clamp01(s.LatencyDrift / 20.0);
            double retrans = Clamp01(s.RetransmissionGrowth * 120.0);
            double cong = Clamp01(s.CongestionScore / 15.0);

            return 0.25 * qg + 0.20 * qa + 0.20 * qp + 0.15 * drift + 0.10 * retrans + 0.10 * cong;
        }

        private void FillDiagnostics(MetricsSnapshot snapshot, LagEventType lagEventType, double aggression, bool learningEnabled)
        {
            snapshot.LagPressure = lagPressureController.LagPressure;
            snapshot.LagEventType = lagEventType.ToString().ToUpperInvariant();
            snapshot.PendingLagEvents = playerLagDetector.PendingLagEvents;
            snapshot.BurstMultiplier = lagPressureController.BurstMultiplier;
            snapshot.AggressionFactor = aggression;
            snapshot.LearningEnabled = learningEnabled;
            snapshot.GamePhase = learningGate.CurrentPhase.ToString().ToUpperInvariant();
            snapshot.NetworkState = failureDetector.State.ToString().ToUpperInvariant();
            snapshot.ServerScore = serverQuality.ServerScore;
        }

        private GamePhase ResolveGamePhase(MetricsSnapshot snapshot)
        {
            if (snapshot == null)
                return GamePhase.Idle;

            if (snapshot.ReceiveBytesPerSecond < 10 && snapshot.SendBytesPerSecond < 10)
                return GamePhase.Idle;

            if (snapshot.ReceiveBytesPerSecond < 200)
                return GamePhase.Loading;

            return GamePhase.Active;
        }

        private double Clamp01(double value)
        {
            if (value < 0) return 0;
            if (value > 1) return 1;
            return value;
        }
    }
}
