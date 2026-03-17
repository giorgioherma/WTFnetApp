using System;

namespace WTFnetApp
{
    public class SlowLearningManager
    {
        private static readonly TimeSpan EpochDuration = TimeSpan.FromSeconds(30);

        private DateTime epochStart = DateTime.UtcNow;

        private int predictedRiskEvents;
        private int confirmedLagEvents;
        private int falsePositives;
        private double lagPressureSum;
        private int lagPressureCount;

        private int mitigationAttempts;
        private int mitigationSuccesses;
        private int mitigationFailures;

        private bool lastPredictedRisk;
        private NetworkControlState lastState = NetworkControlState.Calibration;

        private bool mitigationPending;
        private DateTime mitigationStart;
        private double mitigationStartPressure;

        public void Observe(MetricsSnapshot snapshot, NetworkControlState state, double currentThreshold, Action<double> applyThresholdDelta)
        {
            if (snapshot == null)
                return;

            if (!IsLearningSampleAllowed(snapshot))
            {
                EvaluateEpochIfNeeded(applyThresholdDelta);
                return;
            }

            bool predictedRisk = snapshot.PatternMatchScore > currentThreshold;
            if (predictedRisk && !lastPredictedRisk)
                predictedRiskEvents++;
            lastPredictedRisk = predictedRisk;

            bool confirmedLag = !string.IsNullOrEmpty(snapshot.LagEventType) && snapshot.LagEventType != "NONE";
            if (confirmedLag)
                confirmedLagEvents++;

            lagPressureSum += snapshot.LagPressure;
            lagPressureCount++;

            bool enteredMitigation = (state == NetworkControlState.Warning || state == NetworkControlState.Panic) &&
                                    (lastState != NetworkControlState.Warning && lastState != NetworkControlState.Panic);

            if (enteredMitigation)
            {
                mitigationAttempts++;
                mitigationPending = true;
                mitigationStart = DateTime.UtcNow;
                mitigationStartPressure = snapshot.LagPressure;
            }

            if (mitigationPending && (DateTime.UtcNow - mitigationStart).TotalSeconds >= 3)
            {
                mitigationPending = false;
                if (snapshot.LagPressure < mitigationStartPressure - 0.03)
                    mitigationSuccesses++;
                else
                    mitigationFailures++;
            }

            lastState = state;
            EvaluateEpochIfNeeded(applyThresholdDelta);
        }

        private bool IsLearningSampleAllowed(MetricsSnapshot snapshot)
        {
            if (!snapshot.LearningEnabled)
                return false;

            if (snapshot.NetworkState == "DEGRADED")
                return false;

            if (snapshot.GamePhase == "LOADING" || snapshot.GamePhase == "CONNECTING" || snapshot.GamePhase == "IDLE")
                return false;

            return true;
        }

        private void EvaluateEpochIfNeeded(Action<double> applyThresholdDelta)
        {
            DateTime now = DateTime.UtcNow;
            if (now - epochStart < EpochDuration)
                return;

            falsePositives = predictedRiskEvents > confirmedLagEvents ? predictedRiskEvents - confirmedLagEvents : 0;
            double avgLagPressure = lagPressureCount > 0 ? lagPressureSum / lagPressureCount : 0;
            double successRate = mitigationAttempts > 0 ? (double)mitigationSuccesses / mitigationAttempts : 0;

            double delta = 0;
            if (falsePositives > confirmedLagEvents)
                delta = 0.01;
            else if (confirmedLagEvents > 0 && successRate >= 0.5)
                delta = -0.01;

            if (delta > 0.01) delta = 0.01;
            if (delta < -0.01) delta = -0.01;

            Logger.Log("slowlearn",
                $"epoch=30s predicted={predictedRiskEvents} confirmed={confirmedLagEvents} fp={falsePositives} avgLP={avgLagPressure:F2} mit={mitigationAttempts} succ={mitigationSuccesses} fail={mitigationFailures} successRate={successRate:P0} thrDelta={delta:+0.00;-0.00;0.00}");

            if (Math.Abs(delta) > 0 && applyThresholdDelta != null)
                applyThresholdDelta(delta);

            ResetEpoch(now);
        }

        private void ResetEpoch(DateTime now)
        {
            epochStart = now;
            predictedRiskEvents = 0;
            confirmedLagEvents = 0;
            falsePositives = 0;
            lagPressureSum = 0;
            lagPressureCount = 0;
            mitigationAttempts = 0;
            mitigationSuccesses = 0;
            mitigationFailures = 0;
            mitigationPending = false;
            lastPredictedRisk = false;
        }
    }
}
