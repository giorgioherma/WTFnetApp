using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WTFnetApp
{
    public class DerGrosseGeneralstab
    {
        // =========================
        // CORE COMPONENTS
        // =========================

        private UIOverlay overlay;
        private NatKeepAlive nat;
        private readonly NetworkMetricsEngine networkMetricsEngine = new NetworkMetricsEngine();
        private SpeedifyTelemetryReader speedifyReader;

        // Optional / legacy-ish pieces kept around for now
        private MetricsSnapshot lastSnapshot;

        // =========================
        // CONSTRUCTOR
        // =========================

        public DerGrosseGeneralstab()
        {
            Logger.Log("engine", "initializing core");

            InitializeOverlay();
            InitializeNat();
            InitializeTelemetry();

            Logger.Log("engine", "network monitor started");
        }

        // =========================
        // INITIALIZATION
        // =========================

        private void InitializeOverlay()
        {
            overlay = new UIOverlay();
            overlay.Show();

            Logger.Log("tray", "tray initialized");
        }

        private void InitializeNat()
        {
            nat = new NatKeepAlive();
            _ = nat.StartNatKeepAlive();
        }

        private void InitializeTelemetry()
        {
            speedifyReader = new SpeedifyTelemetryReader(OnSpeedifySnapshot);
            speedifyReader.Start();
        }

        // =========================
        // SPEEDIFY SNAPSHOT INPUT
        // =========================

        private void OnSpeedifySnapshot(MetricsSnapshot snapshot)
        {
            try
            {
                if (snapshot == null)
                    return;

                lastSnapshot = snapshot;

                networkMetricsEngine.Update(snapshot, nat.CurrentNatState);
                overlay?.RenderOverlay(networkMetricsEngine.GetState().Overlay);
            }
            catch (Exception ex)
            {
                Logger.Log("engine", "snapshot handoff error: " + ex.Message);
            }
        }

        // =========================
        // STARTUP
        // =========================

        public void Startup()
        {
            Logger.Log("startup", "detecting best server");

            _ = Task.Run(async () =>
            {
                string bestServer = await DetectBestServerFast();
                Logger.Log("startup", "Using server region: " + bestServer);
            });
        }

        // =========================
        // SERVER DETECTION
        // =========================

        private async Task<string> DetectBestServerFast()
        {
            var servers = new Dictionary<string, string>
            {
                { "Vienna", "1.1.1.1" },
                { "Ljubljana", "8.8.8.8" },
                { "Milan", "9.9.9.9" }
            };

            var tasks = new List<Task<Tuple<string, int>>>();

            foreach (var s in servers)
                tasks.Add(CheckServer(s.Key, s.Value));

            Tuple<string, int>[] results = await Task.WhenAll(tasks);

            string best = "Ljubljana";
            int bestPing = int.MaxValue;

            foreach (Tuple<string, int> r in results)
            {
                if (r.Item2 > 0 && r.Item2 < bestPing)
                {
                    bestPing = r.Item2;
                    best = r.Item1;
                }
            }

            Logger.Log("startup", "Best server: " + best + " (" + bestPing + "ms)");
            return best;
        }

        private async Task<Tuple<string, int>> CheckServer(string name, string host)
        {
            int ping = await WtfFastPing.Ping(host);

            Logger.Log("startup", "server check " + name + " = " + ping + "ms");

            return Tuple.Create(name, ping);
        }

        // =========================
        // LOGGING
        // =========================

        private void LogSnapshot()
        {
            if (lastSnapshot == null)
                return;

            Logger.Log(
                "net",
                "ping=" + lastSnapshot.Recent.ToString("F0") + " " +
                "avg=" + lastSnapshot.Average.ToString("F1") + " " +
                "jitter=" + lastSnapshot.Jitter.ToString("F1") + " " +
                "loss=" + lastSnapshot.PacketLoss.ToString("F2"));
        }

        // =========================
        // TRAY CONTROL
        // =========================

        public void ToggleOverlay()
        {
            if (overlay == null || overlay.IsDisposed)
                overlay = new UIOverlay();

            if (overlay.Visible)
                overlay.Hide();
            else
                overlay.Show();
        }

        public Task RunManualServerPingScan()
        {
            Logger.Log("server", "manual server ping scan disabled (StartupServerTest removed)");
            return Task.CompletedTask;
        }

        // =========================
        // ACCESSORS
        // =========================

        public DataSet GetDataSet()
        {
            return networkMetricsEngine.GetDataSet();
        }

        public NetworkEngineState GetNetworkState()
        {
            return networkMetricsEngine.GetState();
        }

        // =========================
        // SHUTDOWN
        // =========================

        public async Task StopAsync()
        {
            try
            {
                if (speedifyReader != null)
                    await speedifyReader.StopAsync();
            }
            catch (Exception ex)
            {
                Logger.Log("engine", "speedify stop error: " + ex.Message);
            }

            try
            {
                await networkMetricsEngine.StopAsync();
            }
            catch (Exception ex)
            {
                Logger.Log("engine", "metrics engine stop error: " + ex.Message);
            }
        }
    }
}