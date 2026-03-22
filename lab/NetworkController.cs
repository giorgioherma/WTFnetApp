namespace WTFnetApp
{
    public class SpeedefyController
    {
        private readonly DecisionEngine decisionEngine = new DecisionEngine();
        private readonly NetworkController networkController = new NetworkController();

        private NetworkControlState currentState = NetworkControlState.Calibration;
        private bool enabled = true;

        public NetworkControlState CurrentState
        {
            get { return currentState; }
        }

        public double CurrentLearningThreshold
        {
            get { return decisionEngine.CurrentLearningThreshold; }
        }

        public bool Enabled
        {
            get { return enabled; }
            set { enabled = value; }
        }

        public void ApplySlowThresholdDelta(double delta)
        {
            decisionEngine.ApplySlowThresholdDelta(delta);
        }

        public NetworkControlState Update(MetricsSnapshot snapshot, DataSet dataSet)
        {
            if (!enabled || snapshot == null)
                return currentState;

            currentState = decisionEngine.Update(snapshot, dataSet);
            networkController.Update(snapshot, dataSet);

            snapshot.ControlState = currentState.ToString().ToUpperInvariant();
            return currentState;
        }

        public void Reset()
        {
            currentState = NetworkControlState.Calibration;
        }
    }
}