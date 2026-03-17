namespace WTFnetApp
{
    using System.Threading.Tasks;

    public class NetworkController
    {
        private readonly NetworkMetricsEngine metricsEngine = new NetworkMetricsEngine();
        private readonly SpeedifyServerTracker serverTracker = new SpeedifyServerTracker();
        private readonly DecisionEngine decisionEngine = new DecisionEngine();
        private readonly SpeedifyController speedifyController = new SpeedifyController();

        private double latestExternalPing = -1;
        private readonly LagEventRecorder lagEventRecorder = new LagEventRecorder();
        private readonly PatternAnalyzer patternAnalyzer = new PatternAnalyzer();
        private readonly SignalWeights signalWeights = new SignalWeights();
        private readonly SignalWeightTrainer weightTrainer;
        private readonly LagPredictor lagPredictor;
        private readonly PatternMemoryManager patternMemory = new PatternMemoryManager();

        private double latestExternalPing = -1;

        public NetworkController()
        {
            weightTrainer = new SignalWeightTrainer(signalWeights);
            lagPredictor = new LagPredictor(signalWeights);
        }

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
            LagPrediction prediction = lagPredictor.Predict(snapshot);
            snapshot.LagProbability = prediction.Probability;
            snapshot.PredictionConfidence = prediction.Confidence;
            snapshot.DominantSignal = prediction.DominantSignal;

            LagEvent lagEvent;
            if (lagEventRecorder.TryCapture(snapshot, metricsEngine.DataSet, out lagEvent))
            {
                LagEventFeatures features = patternAnalyzer.ExtractFeatures(lagEvent);
                weightTrainer.LearnFromLagEvent(features);
                patternMemory.Learn(lagEvent, features);
                Logger.Log("lag", $"event learned conf={snapshot.PredictionConfidence} dom={snapshot.DominantSignal}");
            }
            else if (snapshot.LagScore < 1.0)
            {
                weightTrainer.LearnFromFalsePositive(snapshot.LagProbability);
            }

            var state = decisionEngine.Update(snapshot);
            speedifyController.Apply(state, snapshot);

            return snapshot;
        }

        public Task RunManualServerPingScan()
        {
            return serverTracker.RunManualPingScan();
        }
    }
}
