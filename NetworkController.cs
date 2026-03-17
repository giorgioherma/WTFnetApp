namespace WTFnetApp
{
    public class NetworkController
    {
        private NetworkMetricsEngine metricsEngine = new NetworkMetricsEngine();
        private SpeedifyServerTracker serverTracker = new SpeedifyServerTracker();

        public void Start()
        {
            serverTracker.Start();
        }

        public void Stop()
        {
            serverTracker.Stop();
        }

        public MetricsSnapshot ProcessSnapshot(MetricsSnapshot snapshot)
        {
            if (snapshot == null)
                return null;

            snapshot = metricsEngine.Process(snapshot);

            // Future logic will go here:
            // prediction
            // connection switching
            // mitigation

            return snapshot;
        }
    }
}
