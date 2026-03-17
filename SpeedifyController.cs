namespace WTFnetApp
{
    public class SpeedifyController
    {
        private NetworkControlState lastApplied = NetworkControlState.Stable;

        public void Apply(NetworkControlState state, MetricsSnapshot snapshot)
        {
            if (state == lastApplied)
                return;

            lastApplied = state;

            switch (state)
            {
                case NetworkControlState.Stable:
                    Logger.Log("control", "state=STABLE action=keep_fastest_primary");
                    break;
                case NetworkControlState.CongestionBuilding:
                    Logger.Log("control", "state=BUILDING action=enable_multipath_bonding");
                    break;
                case NetworkControlState.LagSpike:
                    Logger.Log("control", "state=LAG_SPIKE action=prioritize_lowest_latency");
                    break;
            }
        }
    }
}
