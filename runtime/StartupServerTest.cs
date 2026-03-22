namespace WTFnetApp
{
    using System.Threading.Tasks;

    public class StartupServerTest
    {
        private readonly NetworkMetricsEngine metricsEngine = new NetworkMetricsEngine();
        private readonly VPNServerList speedifyserverLog = new VPNServerList();
        private readonly DecisionEngine decisionEngine = new DecisionEngine();
        private readonly SpeedifyController speedifyController = new SpeedifyController();
        private readonly SlowLearningManager slowLearningManager = new SlowLearningManager();

        private double latestExternalPing = -1;

        public void Start()
        {
            serverTracker.Start();
        }

        public void Stop()
        {
            serverTracker.Stop();
        }

        public void UpdateExternalPing(double ping)
        {
            if (ping > 0)
                latestExternalPing = ping;
        }

        public MetricsSnapshot ProcessSnapshot(MetricsSnapshot snapshot)
        {
            if (snapshot == null)
                return null;

            snapshot.ExternalPingMs = latestExternalPing;
            snapshot = metricsEngine.Process(snapshot);

            var state = decisionEngine.Update(snapshot, metricsEngine.DataSet);
            speedifyController.Apply(state, snapshot);

            slowLearningManager.Observe(
                snapshot,
                state,
                decisionEngine.CurrentLearningThreshold,
                decisionEngine.ApplySlowThresholdDelta);

            return snapshot;
        }

        public Task RunManualServerPingScan()
        {
            return serverTracker.RunManualPingScan();
        }
    }
}
