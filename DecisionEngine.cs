namespace WTFnetApp
{
    public enum NetworkControlState
    {
        Stable,
        CongestionBuilding,
        LagSpike
    }

    public class DecisionEngine
    {
        private NetworkControlState state = NetworkControlState.Stable;
        private NetworkControlState pending = NetworkControlState.Stable;
        private int confirmCount;

        public NetworkControlState CurrentState => state;

        public NetworkControlState Update(MetricsSnapshot s)
        {
            var target = Evaluate(s);

            if (target == state)
            {
                pending = state;
                confirmCount = 0;
                return state;
            }

            if (target != pending)
            {
                pending = target;
                confirmCount = 1;
                return state;
            }

            confirmCount++;
            if (confirmCount >= 3)
            {
                state = pending;
                confirmCount = 0;
            }

            return state;
        }

        private NetworkControlState Evaluate(MetricsSnapshot s)
        {
            if (s.LagScore > 1.5)
                return NetworkControlState.LagSpike;

            if (s.CongestionScore >= 3 && s.CongestionScore <= 6 && s.QueueAcceleration > 0)
                return NetworkControlState.CongestionBuilding;

            return NetworkControlState.Stable;
        }
    }
}
