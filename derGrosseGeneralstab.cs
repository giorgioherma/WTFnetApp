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

        private SpeedifyTelemetryReader speedifyReader;
        private MetricsSnapshot lastSnapshot;
        private NetworkController networkController;

        private WtfFastPing fastPing;

        // UI refresh timer
        private System.Windows.Forms.Timer uiTimer;

        // =========================
        // STATE
        // =========================

        private string speedifyMode = "speed";

        // =========================
        // CONSTRUCTOR
        // =========================

        public DerGrosseGeneralstab()
        {
            Logger.Log("engine", "initializing core");

            // Overlay
            overlay = new UIOverlay();
            overlay.Show();

            // NAT keepalive
            nat = new NatKeepAlive();
            _ = nat.StartNatKeepAlive();

            networkController = new NetworkController();
            networkController.Start();

            //Speedify telemetry
            speedifyReader = new SpeedifyTelemetryReader(OnSpeedifySnapshot);
            speedifyReader.Start();


            // Fast ping engine (prediction only)
            fastPing = new WtfFastPing(
                new string[]
                {
                    "1.1.1.1",
                    "8.8.8.8",
                    "9.9.9.9"
                },
                OnFastPing
            );

            fastPing.Start();

            // UI refresh timer
            uiTimer = new System.Windows.Forms.Timer();
            uiTimer.Interval = 250;
            uiTimer.Tick += UiTick;
            uiTimer.Start();

            Logger.Log("engine", "network monitor started");
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
                Logger.Log("startup", $"Using server region: {bestServer}");
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

            var tasks = new List<Task<(string, int)>>();

            foreach (var s in servers)
                tasks.Add(CheckServer(s.Key, s.Value));

            var results = await Task.WhenAll(tasks);

            string best = "Frankfurt";
            int bestPing = int.MaxValue;

            foreach (var r in results)
            {
                if (r.Item2 > 0 && r.Item2 < bestPing)
                {
                    bestPing = r.Item2;
                    best = r.Item1;
                }
            }

            Logger.Log("startup", $"Best server: {best} ({bestPing}ms)");

            return best;
        }

        private async Task<(string, int)> CheckServer(string name, string host)
        {
            int ping = await WtfFastPing.Ping(host);

            Logger.Log("startup", $"server check {name} = {ping}ms");

            return (name, ping);
        }

        // =========================
        // FAST PING CALLBACK
        // =========================

        private void OnFastPing(double latency, long timestamp)
        {
            // Prediction engine will use this later.
            // Currently not connected to UI or logs.
        }

        // =========================
        // SPEEDIFY SNAPSHOT INPUT
        // =========================

        public void OnSpeedifySnapshot(MetricsSnapshot snapshot)
        {
            snapshot = networkController.ProcessSnapshot(snapshot);
            lastSnapshot = snapshot;
        }

        // =========================
        // UI UPDATE TIMER
        // =========================

        private void UiTick(object sender, EventArgs e)
        {
            if (overlay == null)
                return;

            try
            {
                UpdateOverlay();
                LogSnapshot();
            }
            catch (Exception ex)
            {
                Logger.Log("error", ex.ToString());
            }
        }

        // =========================
        // OVERLAY UPDATE
        // =========================

        private void UpdateOverlay()
        {
            double natAge =
                (DateTime.Now - nat.LastNatRecreate).TotalSeconds;

            int natState =
                natAge < 15 ? 0 :
                natAge < 60 ? 1 :
                2;

            string systemState =
                natState == 0 ? "ACTIVE" :
                natState == 1 ? "WARM" :
                "IDLE";

            int weight = 0;
            if (lastSnapshot != null)
            {
                weight = (int)Math.Max(0, Math.Min(100, (lastSnapshot.CongestionScore / 15.0) * 100.0));
            }

            overlay.UpdateStatus(lastSnapshot, natState, systemState, weight);
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
                $"ping={lastSnapshot.Recent:F0} " +
                $"avg={lastSnapshot.Average:F1} " +
                $"jitter={lastSnapshot.Jitter:F1} " +
                $"loss={lastSnapshot.PacketLoss:F2}"
            );

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
    }
}
