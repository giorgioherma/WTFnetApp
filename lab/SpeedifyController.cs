namespace WTFnetApp
{
    public class SpeedifyController
    {
        private NetworkControlState lastApplied = NetworkControlState.Calibration;

        public void Apply(NetworkControlState state, MetricsSnapshot snapshot)
        {
            double aggression = snapshot != null ? snapshot.AggressionFactor : 1.0;
            if (aggression <= 0)
                aggression = 1.0;

            double panicThreshold = 0.75 / aggression;
            double mitigationStrength = 1.0 * aggression;
            double cooldownSeconds = 3.0 * aggression;

            if (state == lastApplied)
                return;

            lastApplied = state;

            switch (state)
            {
                case NetworkControlState.Calibration:
                    Logger.Log("control", "state=CALIBRATION action=observe_only");
                    break;
                case NetworkControlState.Normal:
                    Logger.Log("control", $"state=NORMAL action=keep_fastest_primary strength={mitigationStrength:F2}");
                    break;
                case NetworkControlState.Warning:
                    Logger.Log("control", $"state=WARNING action=enable_multipath_bonding strength={mitigationStrength:F2} panic_thr={panicThreshold:F2}");
                    break;
                case NetworkControlState.Panic:
                    Logger.Log("control", $"state=PANIC action=prioritize_lowest_latency cooldown={cooldownSeconds:F1}s");
                    break;
            }
        }
    }
}
