using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

namespace WTFnetApp
{
    class DecisionScore
    {
        public double HardSpike;
        public double Predicted;
        public double Micro;
        public double Stability;
        public double Pressure;
        public double HighLatency;

        public double Total =>
                HardSpike + Predicted + Micro + Pressure + HighLatency - Stability;


        public override string ToString()
        {
            return $"H:{HardSpike:F1} P:{Predicted:F1} M:{Micro:F1} HL:{HighLatency:F1} S:-{Stability:F1} PR:{Pressure:F1} => {Total:F1}";
        }
    }

    class NetLog
    {
        public DateTime Time;
        public int Avg, Recent, Max;
        public int Jitter;
        public double Loss;

        public double Pressure;
        public int Bad, Good;

        public double TrafficKB;
        public bool Download;
        public bool Unreal;

        public string Mode;
        public bool Allow;

        public DecisionScore Score;
        public string Decision;
        public string Block;

        public bool Test;
        public bool Debug;

        public override string ToString()
        {
            return
                $"[{Time:HH:mm:ss}] " +
                $"PING[{Avg}/{Recent}/{Max}] " +
                $"JIT:{Jitter} LOSS:{Loss:F2} " +
                $"SCORE[{Score}] " +
                $"NET[{TrafficKB:F0}KB DL:{(Download ? 1 : 0)} UNR:{(Unreal ? 1 : 0)}] " +
                $"MODE:{Mode} ALW:{(Allow ? 1 : 0)} " +
                $"DEC[{Decision}] BLK[{Block}] " +
                $"T:{(Test ? 1 : 0)} D:{(Debug ? 1 : 0)}";
        }
    }
    public class DashboardForm : Form
    {
        public Label info;

        public DashboardForm()
        {
            Text = "Network Dashboard";
            Size = new Size(300, 200);

            info = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10),
                ForeColor = Color.White,
                BackColor = Color.Black
            };

            Controls.Add(info);
        }

        private void InitializeComponent()
        {
            SuspendLayout();
            // 
            // DashboardForm
            // 
            ClientSize = new Size(248, 241);
            ForeColor = Color.Crimson;
            Name = "DashboardForm";
            ResumeLayout(false);

        }
    }
    public class PingOverlay : Form
    {
        private Label pingLabel;
        private Label modeLabel;

        private string lastText = "";
        private string lastMode = "";
        private int lastNatState = -1;

        public PingOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(130, 48);

            TopMost = true;
            ShowInTaskbar = false;

            BackColor = Color.FromArgb(35, 35, 35);
            Opacity = 0.92;

            pingLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Consolas", 13, FontStyle.Bold),
                ForeColor = Color.Lime,
                BackColor = Color.Transparent,
                Text = "-- ms"
            };

            modeLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Consolas", 9, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.Transparent,
                Text = ""
            };

            Controls.Add(modeLabel);
            Controls.Add(pingLabel);

            PositionTopLeft();

            // reposition if resolution or monitor changes
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => PositionTopLeft();
        }

        private void PositionTopRight()
        {
            var screen = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(screen.Left - Width - 12, screen.Top + 12);
        }
        private void PositionTopLeft()
        {
            var screen = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(screen.Left - Width - 12, screen.Top + 12);
        }

        public void UpdatePing(string text, string mode, int natState)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdatePing(text, mode, natState)));
                return;
            }
            if (text == lastText && mode == lastMode && natState == lastNatState)
                return;

            lastText = text;
            lastMode = mode;
            lastNatState = natState;

            pingLabel.Text = text;
            modeLabel.Text = mode;

            switch (natState)
            {
                case 0:
                    modeLabel.ForeColor = Color.Red;
                    break;

                case 1:
                    modeLabel.ForeColor = Color.Gold;
                    break;

                default:
                    modeLabel.ForeColor = Color.LimeGreen;
                    break;
            }

            if (int.TryParse(text.Replace(" ms", ""), out int ping))
            {
                if (ping < 50)
                    pingLabel.ForeColor = Color.LimeGreen;
                else if (ping < 90)
                    pingLabel.ForeColor = Color.Orange;
                else
                    pingLabel.ForeColor = Color.Red;
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TRANSPARENT = 0x20;
                const int WS_EX_LAYERED = 0x80000;
                const int WS_EX_TOOLWINDOW = 0x80;
                const int WS_EX_NOACTIVATE = 0x8000000;

                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;
    }

    public partial class Form1 : Form
    {
        // =========================
        // UI COMPONENTS
        // =========================
        private LteProTool lteTool = new LteProTool();
        private LteDashboardForm lteProDashboard;
        private PingOverlay overlay;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private DashboardForm dashboard;

        private ToolStripMenuItem soundsMenuItem;
        private ToolStripMenuItem simulationMenu;

        private ToolStripMenuItem speedifyOffItem;
        private ToolStripMenuItem speedifySpeedItem;
        private ToolStripMenuItem speedifySmartItem;

        private bool lteDetailedMode = false;

        // =========================
        // UI STATE
        // =========================
        enum NetState { Good, Warning, Critical }
        private NetState currentNetState = NetState.Good;
        private bool overlayVisible = true;

        // =========================
        // CORE LOOP
        // =========================
        private bool isRunning = false;

        // =========================
        // NETWORK DATA
        // =========================
        private List<int> pings = new List<int>();
        private List<int> timeouts = new List<int>();

        int lastPing = -1;
        DateTime lastOverlayUpdate = DateTime.MinValue;
        private int window = 60;

        private string[] targets = { "8.8.8.8", "1.1.1.1", "9.9.9.9" };
        private int currentPing = 0;

        // =========================
        // DETECTION MEMORY
        // =========================
        private double preSpikePressure = 0;
        private int badStreak = 0;
        private int goodStreak = 0;

        // =========================
        // BASELINE LEARNING
        // =========================
        private Dictionary<int, List<int>> hourlyHistory = new Dictionary<int, List<int>>();

        // =========================
        // TRAFFIC MONITOR
        // =========================
        private long lastBytesSent = 0;
        private long lastBytesReceived = 0;
        private DateTime lastTrafficCheck = DateTime.Now;
        private DateTime lastTrafficRead = DateTime.MinValue;
        private int trafficIntervalMs = 2000;

        private double bytesPerSecond = 0;
        private int heavyTrafficStreak = 0;

        // =========================
        // SPEEDIFY CONTROL
        // =========================
        enum SpeedifyPolicy
        {
            Off,
            SpeedOnly,
            Smart
        }

        private SpeedifyPolicy speedifyPolicy = SpeedifyPolicy.Off;

        private string speedifyMode = "speed";
        private DateTime lastSpeedifyStateCheck = DateTime.MinValue;
        private int speedifyStateIntervalSec = 6;
        private bool speedifyEnabled = false;

        private DateTime lastCliCall = DateTime.MinValue;
        private int cliCooldownMs = 1200;

        private DateTime lastSwitch = DateTime.MinValue;
        private int switchCooldownSec = 15;

        // =========================
        // MODE PROTECTION
        // =========================
        private DateTime modeEnteredAt = DateTime.MinValue;
        private int minModeDurationSec = 8;

        // long-term smoothing
        private int minRedundantMinutes = 12;
        private int stableExitMinutes = 4;

        private DateTime redundantEnteredAt = DateTime.MinValue;
        private DateTime stableSince = DateTime.MinValue;

        // =========================
        // REDUNDANT TRACKING
        // =========================
        private DateTime redundantStart = DateTime.MinValue;
        private double totalRedundantSeconds = 0;
        private double estimatedKbps = 300;

        private int preRedundantPing = 0;
        private DateTime redundantCheckStart = DateTime.MinValue;

        // =========================
        // RECONNECT
        // =========================
        private DateTime lastReconnect = DateTime.MinValue;
        private int reconnectCooldownSec = 20;
        private bool reconnecting = false;

        // =========================
        // CONNECTION CACHE
        // =========================
        private bool hasMultiConnection = false;
        private DateTime lastConnectionCheck = DateTime.MinValue;
        private int connectionCheckIntervalSec = 5;

        // =========================
        // NAT KEEP ALIVE
        // =========================
        private NatKeepAlive nat;

        // =========================
        // DEBUG / SIMULATION
        // =========================
        private bool debugMode = false;

        private bool simulateSpeedify = false;
        private bool simulateMultiConnection = false;

        private bool forceRedundant = false;
        private bool forceSpeed = false;

        private bool playSounds = true;

        // =========================
        // DEBUG INFO
        // =========================
        private string lastDecisionReason = string.Empty;
        private string lastBlockReason = string.Empty;

        // =========================
        // LOG THROTTLING
        // =========================
        private DateTime lastMetricsLog = DateTime.MinValue;
        private DateTime lastStateLog = DateTime.MinValue;
        private DateTime lastTraceLog = DateTime.MinValue;
        private DateTime lastBlockLog = DateTime.MinValue;

        // =========================
        // SYNC
        // =========================
        private readonly SemaphoreSlim speedifyLock = new SemaphoreSlim(1, 1);

        // =========================
        // DATA TRACKING
        // =========================
        private List<double> dailyUsageMb = new List<double>();
        private DateTime lastDay = DateTime.Now.Date;

        void ToggleLteProDashboard()
        {
            if (lteProDashboard == null || lteProDashboard.IsDisposed)
            {
                lteProDashboard = new LteDashboardForm();
                lteProDashboard.Show();
            }
            else
            {
                lteProDashboard.Visible = !lteProDashboard.Visible;
            }
        }


        public Form1()
        {
            InitializeComponent();

            nat = new NatKeepAlive();
            _ = nat.StartNatKeepAlive();

        // hide main window
            Opacity = 0;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;

            overlay = new PingOverlay();
            overlay.Show();

            // TRAY MENU
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open Dashboard", null, (s, e) => ToggleDashboard());

            var speedifyMenu = new ToolStripMenuItem("Speedify");

            speedifyOffItem = new ToolStripMenuItem("OFF", null, async (s, e) => await SetSpeedifyOff());
            speedifyOffItem.CheckOnClick = false;

            speedifySpeedItem = new ToolStripMenuItem("SPEED ONLY", null, async (s, e) => await SetSpeedifySpeedOnly());
            speedifySpeedItem.CheckOnClick = false;

            speedifySmartItem = new ToolStripMenuItem("SMART GAMING", null, async (s, e) => await SetSpeedifySmart());
            speedifySmartItem.CheckOnClick = false;

            speedifyMenu.DropDownItems.Add(speedifyOffItem);
            speedifyMenu.DropDownItems.Add(speedifySpeedItem);
            speedifyMenu.DropDownItems.Add(speedifySmartItem);

            trayMenu.Items.Add(speedifyMenu);

            UpdateSpeedifyMenuChecks();

            soundsMenuItem = new ToolStripMenuItem("Toggle Sounds", null, (s, e) => ToggleSounds());
            soundsMenuItem.CheckOnClick = false;
            soundsMenuItem.Checked = playSounds;
            trayMenu.Items.Add(soundsMenuItem);

            trayMenu.Items.Add("Toggle Debug Mode (F6)", null, (s, e) =>
            {
                debugMode = !debugMode;

                simulationMenu.Enabled = debugMode;

                Log("system", $"Debug mode {(debugMode ? "enabled" : "disabled")}");
            });
            simulationMenu = new ToolStripMenuItem("Simulation");
            simulationMenu.Enabled = debugMode;

            simulationMenu.DropDownItems.Add("SIM ALL", null, (s, e) => ToggleSimAll());
            simulationMenu.DropDownItems.Add(new ToolStripSeparator());

            simulationMenu.DropDownItems.Add("Simulate Speedify", null, (s, e) => simulateSpeedify = !simulateSpeedify);
            simulationMenu.DropDownItems.Add("Simulate Multi Connection", null, (s, e) => simulateMultiConnection = !simulateMultiConnection);
            trayMenu.Items.Add(simulationMenu);

            trayMenu.Items.Add("LTEProTool", null, (s, e) => ToggleLteProDashboard());

            trayMenu.Items.Add("Exit", null, (s, e) =>
            {
                trayIcon.Visible = false;
                Application.Exit();
                Environment.Exit(0);
            });

            trayIcon = new NotifyIcon
            {
                Text = "WTFNetApp",
                Icon = SystemIcons.Application,
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            Log("system", "================================");
            Log("system", "WTFNetApp started");
            Log("system", $"Session {DateTime.Now}");
            Log("system", "================================");

            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F6) debugMode = !debugMode;
                if (e.KeyCode == Keys.F8) ToggleOverlay();
                if (e.KeyCode == Keys.F9)
                {
                    if (speedifyPolicy == SpeedifyPolicy.Smart)
                    {
                        _ = SetSpeedifySpeedOnly();
                        Log("speedify", "HOTKEY → SPEED ONLY");
                    }
                    else
                    {
                        _ = SetSpeedifySmart();
                        Log("speedify", "HOTKEY → SMART");
                    }
                }
                if (e.KeyCode == Keys.F11) forceRedundant = true;
                if (e.KeyCode == Keys.F12) forceSpeed = true;
            };
            _ = StartMonitoringLoop();
        }
        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }
        // =========================
        // SPEEDIFY HELPERS
        // =========================

        void PlaySwitchSound(string mode)
        {
            if (!playSounds)
                return;

            try
            {
                switch (mode)
                {
                    case "redundant":
                        System.Media.SystemSounds.Hand.Play();       // strong alert
                        break;

                    case "speed":
                        System.Media.SystemSounds.Asterisk.Play();   // soft notification
                        break;
                }
            }
            catch
            {
                // ignore sound errors
            }
        }

        async Task SetSpeedifyMode(string newMode, string reason)
        {
            if (speedifyMode == newMode) return;

            bool onCooldown = (DateTime.Now - lastSwitch).TotalSeconds < switchCooldownSec;

            // block only entering redundant on cooldown
            if (onCooldown && newMode == "redundant")
            {
                Log("speedify", $"BLOCKED cooldown | {newMode} | {reason}");
                return;
            }

            // handle OFF properly (no "mode off")
            if (newMode != "off")
            {
                await RunSpeedify($"mode {newMode}");
            }

            speedifyMode = newMode;
            lastSwitch = DateTime.Now;
            modeEnteredAt = DateTime.Now;

            PlaySwitchSound(newMode);

            Log("speedify", $"SWITCH {newMode.ToUpper()} | {reason}");
        }

        async Task RunSpeedify(string args)
        {
            await speedifyLock.WaitAsync();
            try
            {
                if ((DateTime.Now - lastCliCall).TotalMilliseconds < cliCooldownMs)
                    return;

                lastCliCall = DateTime.Now;

                if (debugMode && simulateSpeedify)
                {
                    Log("speedify", $"SIM SPEEDIFY: {args}"); return;
                }

                string path = @"C:\Program Files (x86)\Speedify\speedify_cli.exe";

                if (!File.Exists(path))
                {
                    Log("errors", "Speedify CLI NOT FOUND");
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

                await Task.Delay(1200);
            }
            catch (Exception ex)
            {
                Log("errors", "Speedify error: " + ex.Message);
            }
            finally
            {
                speedifyLock.Release();
            }
        }

        async void ToggleSpeedify()
        {
            speedifyEnabled = !speedifyEnabled;

            if (speedifyEnabled)
            {
                await RunSpeedify("connect");
                Log("speedify", "CONNECTED");

                await SetSpeedifyMode("speed", "initial connect");
            }
            else
            {
                await RunSpeedify("disconnect");

                await SetSpeedifyMode("off", "manual toggle off");
            }
        }

        async void SoftReconnect()
        {
            if ((DateTime.Now - lastReconnect).TotalSeconds < reconnectCooldownSec) return;
            if (reconnecting) return;

            reconnecting = true;
            lastReconnect = DateTime.Now;

            await RunSpeedify("disconnect");
            await RunSpeedify("connect");

            reconnecting = false;
        }

        bool HasMultipleConnections()
        {
            if ((DateTime.Now - lastConnectionCheck).TotalSeconds < connectionCheckIntervalSec)
                return hasMultiConnection;

            lastConnectionCheck = DateTime.Now;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = @"C:\Program Files (x86)\Speedify\speedify_cli.exe",
                    Arguments = "show adapters",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    string output = proc.StandardOutput.ReadToEnd();

                    proc.WaitForExit();

                    var connected = output.Split('\n')
                        .Count(l => l.TrimStart().StartsWith("Connected ", StringComparison.OrdinalIgnoreCase));

                    hasMultiConnection = connected > 1;
                }
            }
            catch
            {
                hasMultiConnection = false;
            }

            return hasMultiConnection;
        }
        async Task VerifySpeedifyState()
        {
            if (!speedifyEnabled)
                return;

            if ((DateTime.Now - lastSpeedifyStateCheck).TotalSeconds < speedifyStateIntervalSec)
                return;

            lastSpeedifyStateCheck = DateTime.Now;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = @"C:\Program Files (x86)\Speedify\speedify_cli.exe",
                    Arguments = "show status",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var proc = Process.Start(psi))
                {
                    if (proc == null)
                        return;

                    string output = await proc.StandardOutput.ReadToEndAsync();
                    proc.WaitForExit();

                    string o = output.ToLowerInvariant();

                    if (o.Contains("disconnected"))
                        speedifyMode = "off";
                    else if (o.Contains("redundant"))
                        speedifyMode = "redundant";
                    else
                        speedifyMode = "speed";
                }
            }
            catch
            {
                Log("errors", "Speedify state read failed");
            }
        }

        void ToggleSimAll()
        {
            bool state = !(simulateSpeedify && simulateMultiConnection);

            simulateSpeedify = state;
            simulateMultiConnection = state;

            Log("simulation", $"SIM ALL {(state ? "ON" : "OFF")}");
        }

        void UpdateDailyUsage(double currentMb)
        {
            if (DateTime.Now.Date != lastDay)
            {
                dailyUsageMb.Add(currentMb);

                if (dailyUsageMb.Count > 30)
                    dailyUsageMb.RemoveAt(0);

                lastDay = DateTime.Now.Date;
            }
        }

        double GetMonthlyEstimate()
        {
            if (dailyUsageMb.Count == 0) return 0;

            return dailyUsageMb.Average() * 30;
        }

        async Task SetSpeedifyOff()
        {
            speedifyPolicy = SpeedifyPolicy.Off;

            await RunSpeedify("disconnect");

            speedifyEnabled = false;
            speedifyMode = "off";

            Log("speedify", "POLICY OFF");

            UpdateSpeedifyMenuChecks();
        }

        async Task SetSpeedifySpeedOnly()
        {
            speedifyPolicy = SpeedifyPolicy.SpeedOnly;

            if (!speedifyEnabled)
            {
                await RunSpeedify("connect");
                speedifyEnabled = true;
            }

            await SetSpeedifyMode("speed", "speed-only mode");

            Log("speedify", "POLICY SPEED ONLY");

            UpdateSpeedifyMenuChecks();
        }

        async Task SetSpeedifySmart()
        {
            speedifyPolicy = SpeedifyPolicy.Smart;

            if (!speedifyEnabled)
            {
                await RunSpeedify("connect");
                speedifyEnabled = true;
            }

            await SetSpeedifyMode("speed", "smart start");

            Log("speedify", "POLICY SMART");

            UpdateSpeedifyMenuChecks();
        }

        // =========================
        // UI HELPERS
        // =========================

        void UpdateSpeedifyMenuChecks()
        {
            speedifyOffItem.Checked = speedifyPolicy == SpeedifyPolicy.Off;
            speedifySpeedItem.Checked = speedifyPolicy == SpeedifyPolicy.SpeedOnly;
            speedifySmartItem.Checked = speedifyPolicy == SpeedifyPolicy.Smart;
        }

        void ToggleSounds()
        {
            playSounds = !playSounds;

            soundsMenuItem.Checked = playSounds;

            Log("system", $"Sounds {(playSounds ? "enabled" : "disabled")}");
        }

        void ToggleOverlay()
        {
            overlayVisible = !overlayVisible;

            if (overlay == null) return;

            if (overlayVisible)
                overlay.Show();
            else
                overlay.Hide();
        }

        void ToggleDashboard()
        {
            if (dashboard == null || dashboard.IsDisposed)
            {
                dashboard = new DashboardForm();
                dashboard.Show();
            }
            else
            {
                dashboard.Visible = !dashboard.Visible;

                if (dashboard.Visible)
                {
                    dashboard.BringToFront();   // ?? useful
                    dashboard.Activate();       // ?? focus
                }
            }
        }
        void Log(string category, string text)
        {
            try
            {
                string path = Path.Combine(@"F:\NetworkLogs", DateTime.Now.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(path);

                string file = Path.Combine(path, $"{category}.log");

                // rotate file if too big (5MB)
                if (File.Exists(file) && new FileInfo(file).Length > 5_000_000)
                {
                    string oldFile = file + ".old";

                    if (File.Exists(oldFile))
                        File.Delete(oldFile);

                    File.Move(file, oldFile);
                }

                string line = $"{DateTime.Now:HH:mm:ss} | {text}";

                File.AppendAllText(file, line + Environment.NewLine);

                if (debugMode)
                    Debug.WriteLine($"[{category}] {line}");
            }
            catch { }
        }
        void UpdateTraffic()
        {
            if ((DateTime.Now - lastTrafficRead).TotalMilliseconds < trafficIntervalMs)
                return;

            lastTrafficRead = DateTime.Now;

            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up &&
                                i.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                long totalSent = 0;
                long totalReceived = 0;

                foreach (var ni in interfaces)
                {
                    var stats = ni.GetIPv4Statistics();
                    totalSent += stats.BytesSent;
                    totalReceived += stats.BytesReceived;
                }

                var now = DateTime.Now;
                double seconds = (now - lastTrafficCheck).TotalSeconds;

                if (seconds > 0.5) // prevents spikes from tiny intervals
                {
                    long delta =
                        (totalSent - lastBytesSent) +
                        (totalReceived - lastBytesReceived);

                    bytesPerSecond = delta / seconds;
                }

                lastBytesSent = totalSent;
                lastBytesReceived = totalReceived;
                lastTrafficCheck = now;

                // =========================
                // STREAK LOGIC (smoothed)
                // =========================
                if (bytesPerSecond > 800_000)
                    heavyTrafficStreak++;
                else
                    heavyTrafficStreak = Math.Max(0, heavyTrafficStreak - 1);
            }
            catch
            {
                if (debugMode)
                    Log("errors", "Traffic read failed");
            }
        }
        async Task StartMonitoringLoop()
        {
            while (true)
            {
                try
                {
                    await Timer_Tick(null, null);
                }
                catch (Exception ex)
                {
                    Log("errors", "Main loop error: " + ex.Message);
                }

                await Task.Delay(1000);
            }
        }

        // =========================
        // MAIN LOOP
        // =========================

        async Task Timer_Tick(object sender, EventArgs e)
        {
            if (isRunning) return;
            isRunning = true;

            try
            {
                UpdateTraffic();

                await VerifySpeedifyState();

                string target = targets[currentPing];
                currentPing = (currentPing + 1) % targets.Length;

                int? result = null;

                try
                {
                    using (var ping = new Ping())
                    {
                        var r = await ping.SendPingAsync(target, 500);

                        if (r.Status == IPStatus.Success)
                            result = (int)r.RoundtripTime;
                    }
                }
                catch
                {
                }
                if (result.HasValue)
                {
                    lastPing = result.Value;

                    pings.Add(result.Value);
                    timeouts.Add(0);
                }
                else
                {
                    lastPing = -1;

                    pings.Add(0);
                    timeouts.Add(1);
                }
                if (pings.Count > window)
                {
                    pings.RemoveAt(0);
                    timeouts.RemoveAt(0);
                }

                var valid = pings.Where(x => x > 0).ToList();
                if (valid.Count < 5) return;

                int avg = (int)valid.Average();
                int recentAvg = (int)valid.Skip(Math.Max(0, valid.Count - 6)).Average();
                int maxPing = valid.Max();

                int hour = DateTime.Now.Hour;
                if (!hourlyHistory.ContainsKey(hour))
                    hourlyHistory[hour] = new List<int>();

                if (avg < 200) hourlyHistory[hour].Add(avg);
                if (hourlyHistory[hour].Count > 300) hourlyHistory[hour].RemoveAt(0);

                int baseline = hourlyHistory[hour].Count < 5
                    ? avg
                    : (int)hourlyHistory[hour].Average();

                int jitter = (int)valid
                        .Skip(1)
                        .Select((p, i) => Math.Abs(p - valid[i]))
                        .Average();

                double lossRate = timeouts.Sum() / (double)window;


                int dynBase = Math.Min(baseline, 60);

                if ((DateTime.Now - lastMetricsLog).TotalSeconds > 5)
                {
                    lastMetricsLog = DateTime.Now;

                    Log("metrics",
                        $"avg:{avg} recent:{recentAvg} max:{maxPing} " +
                        $"jitter:{jitter} loss:{lossRate:F2} " +
                        $"pressure:{preSpikePressure:F2} bad:{badStreak} good:{goodStreak}");
                }

                // =========================
                // LTE PRO TOOL
                // =========================

                var lte = lteTool.Evaluate(
                    pings,
                    avg,
                    jitter,
                    lossRate,
                    preSpikePressure
                );
                if (lteProDashboard != null && lteProDashboard.Visible)
                {
                    if (!lteDetailedMode)
                    {
                        // BIG antenna mode
                        Color color =
                            lte.Quality > 85 ? Color.Lime :
                            lte.Quality > 65 ? Color.Yellow :
                            Color.Red;

                        lteProDashboard.info.ForeColor = color;

                        lteProDashboard.info.Text =
                            $"{lte.Quality:F0}\n" +
                            $"{lte.Trend}\n\n" +
                            $"BEST: {lte.Best:F0}";
                    }
                    else
                    {
                        // detailed mode
                        lteProDashboard.info.ForeColor = Color.White;

                        lteProDashboard.info.Text =
                            $"QUALITY: {lte.Quality:F0}\n" +
                            $"STATE: {lte.Label}\n\n" +
                            $"CONF: {(lte.Confidence * 100):F0}%\n" +
                            $"SPIKES: {lte.Spikes}";
                    }
                }
                // =========================
                // DETECTION
                // =========================

                bool hardSpike =
                    recentAvg > dynBase + 70 ||
                    maxPing > dynBase + 120;

                bool micro =
                    jitter > Math.Max(18, dynBase * 0.35) &&
                    recentAvg > dynBase + 8;

                bool stable =
                    jitter < 16 &&
                    lossRate < 0.02 &&
                    avg < dynBase + 6;


                // =========================
                // STABLE TIMER TRACKING
                // =========================
                if (stable)
                {
                    if (stableSince == DateTime.MinValue)
                        stableSince = DateTime.Now;
                }
                else
                {
                    stableSince = DateTime.MinValue;
                }


                // =========================
                // COMFORT LOGIC
                // =========================
                bool uncomfortablePing =
                    avg > dynBase + 25 ||
                    (avg > dynBase + 15 && jitter > 22);

                bool sustainedBad = uncomfortablePing && badStreak >= 3;


                // =========================
                // PREDICTION
                // =========================
                bool predicted = preSpikePressure > 1.8;


                // =========================
                // CONTEXT
                // =========================
                string context =
                    $"avg:{avg} jitter:{jitter} loss:{lossRate:F2} " +
                    $"bad:{badStreak} good:{goodStreak} pre:{preSpikePressure:F2}";


                // =========================
                // SCORE SYSTEM
                // =========================
                var score = new DecisionScore
                {
                    HardSpike = hardSpike ? 3.0 : 0,
                    Predicted = predicted ? 2.0 : 0,
                    Micro = micro ? 1.2 : 0,
                    Pressure = preSpikePressure,
                    Stability = stable ? 2.5 : 0,
                    HighLatency = uncomfortablePing ? 1.5 : 0
                };

                // =========================
                // PRESSURE SYSTEM (IMPROVED)
                // =========================

                // spikes = strong signal
                if (micro) preSpikePressure += 0.4;
                if (hardSpike) preSpikePressure += 1.8;

                // constant bad ping = slow pressure
                if (uncomfortablePing) preSpikePressure += 0.25;

                // decay (stability clears pressure)
                double decay = stable ? 0.6 : 0.35;
                preSpikePressure = Math.Max(preSpikePressure - decay, 0);

                // clamp
                preSpikePressure = Math.Min(preSpikePressure, 10);
                badStreak = Math.Min(badStreak, 10);

                // =========================
                // STREAK LOGIC
                // =========================
                if (hardSpike || predicted)
                {
                    badStreak += 2;
                    goodStreak = 0;
                }
                else if (micro)
                {
                    badStreak += 1;
                    goodStreak = 0;
                }
                else if (uncomfortablePing)
                {
                    // NEW: slow accumulation for constant lag
                    badStreak += 1;
                    goodStreak = 0;
                }
                else if (stable)
                {
                    goodStreak++;

                    if (goodStreak >= 2)
                    {
                        badStreak = Math.Max(0, badStreak - 3);
                        preSpikePressure *= 0.4;
                    }
                    else
                    {
                        badStreak = Math.Max(0, badStreak - 1);
                    }
                }
                else
                {
                    badStreak = Math.Max(0, badStreak - 1);
                    goodStreak = Math.Max(0, goodStreak - 1);
                }

                // =========================
                // DEBUG REASON BUILD
                // =========================
                string decisionReason = "";

                if (hardSpike) decisionReason += "hardSpike ";
                if (predicted) decisionReason += "predicted ";
                if (micro) decisionReason += "micro ";
                if (stable) decisionReason += "stable ";
                if (uncomfortablePing) decisionReason += "highLatency ";

                // store combined reason+context after decisionReason exists
                lastDecisionReason = decisionReason + " | " + context;

                // =========================
                // ENTER CONDITION (UPDATED)
                // =========================
                bool shouldEnterRedundant =
                    score.Total > 3.5 ||   // smart scoring
                    sustainedBad;          // constant bad ping override

                // =========================
                // FLAGS
                // =========================
                bool unrealisticTraffic = heavyTrafficStreak >= 4 && avg < 80;
                bool hardDownload = bytesPerSecond > 2_500_000;

                bool canSwitch =
                    (DateTime.Now - lastSwitch).TotalSeconds > switchCooldownSec &&
                    (DateTime.Now - modeEnteredAt).TotalSeconds > minModeDurationSec;

                bool multiConnection;

                if (debugMode && simulateMultiConnection)
                    multiConnection = true;
                else
                    multiConnection = HasMultipleConnections();

                // =========================
                // ALLOW LOGIC
                // =========================

                bool allowRedundant;

                if (debugMode)
                {
                    // SIMULATION MODE (override real world)
                    allowRedundant =
                        simulateSpeedify &&
                        simulateMultiConnection;
                }
                else
                {
                    // REAL MODE
                    allowRedundant =
                        speedifyEnabled &&
                        speedifyPolicy == SpeedifyPolicy.Smart &&
                        multiConnection;

                }

                // traffic still blocks in both modes
                if (hardDownload || unrealisticTraffic)
                    allowRedundant = false;

                if (debugMode)
                {
                    if ((DateTime.Now - lastStateLog).TotalSeconds > 10)
                    {
                        lastStateLog = DateTime.Now;

                        Log("state", $"mode:{speedifyMode} allow:{allowRedundant} canSwitch:{canSwitch}");
                    }
                }
                if (debugMode && (DateTime.Now - lastTraceLog).TotalSeconds > 15)
                {
                    lastTraceLog = DateTime.Now;

                    Log("trace",
                $@"----- NETWORK SAMPLE -----
                avg:{avg}
                recent:{recentAvg}
                max:{maxPing}

                jitter:{jitter}
                loss:{lossRate:F2}

                pressure:{preSpikePressure:F2}
                badStreak:{badStreak}
                goodStreak:{goodStreak}

                hardSpike:{hardSpike}
                micro:{micro}
                predicted:{predicted}

                score:{score.Total:F2}

                mode:{speedifyMode}
                allowRedundant:{allowRedundant}

                trafficKB:{bytesPerSecond / 1024:F0}
                download:{hardDownload}
                ---------------------------");
                }

                string blockReason = "";

                if (debugMode)
                {
                    if (!simulateSpeedify) blockReason += "SimSpeedOff ";
                    if (!simulateMultiConnection) blockReason += "SimNoMulti ";
                }
                else
                {
                    if (!speedifyEnabled) blockReason += "SpeedifyOff ";
                    if (!multiConnection) blockReason += "NoMulti ";
                }

                if (hardDownload) blockReason += "Download ";
                if (unrealisticTraffic) blockReason += "Traffic ";

                lastBlockReason = blockReason;

                if (!allowRedundant && (DateTime.Now - lastBlockLog).TotalSeconds > 10)
                {
                    lastBlockLog = DateTime.Now;

                    Log("decisions", $"BLOCKED REDUNDANT | {blockReason} | {context}");
                }

                // =========================
                // EXIT LOGIC (HIGHEST PRIORITY)
                // =========================

                if (speedifyMode == "redundant")
                {
                    double redundantMinutes =
                        (DateTime.Now - redundantEnteredAt).TotalMinutes;

                    bool redundantMinimumReached =
                        redundantMinutes >= minRedundantMinutes;

                    bool stableLongEnough =
                        stableSince != DateTime.MinValue &&
                        (DateTime.Now - stableSince).TotalMinutes >= stableExitMinutes;

                    // TRAFFIC PROTECTION
                    if ((hardDownload || unrealisticTraffic) &&
                        canSwitch &&
                        goodStreak >= 2)
                    {
                        await SetSpeedifyMode("speed", $"traffic protection | {context}");

                        if (redundantStart != DateTime.MinValue)
                        {
                            totalRedundantSeconds += (DateTime.Now - redundantStart).TotalSeconds;
                            redundantStart = DateTime.MinValue;
                        }

                        return;
                    }

                    // INEFFECTIVE REDUNDANT CHECK
                    if (!redundantMinimumReached &&
                        redundantCheckStart != DateTime.MinValue &&
                        (DateTime.Now - redundantCheckStart).TotalSeconds > 5)
                    {
                        int improvement = preRedundantPing - avg;

                        Log("decisions", $"REDUNDANT CHECK | improvement:{improvement}ms | {context}");

                        if (improvement < 12 && goodStreak >= 2)
                        {
                            await SetSpeedifyMode("speed", $"ineffective ({improvement}ms) | {context}");

                            if (redundantStart != DateTime.MinValue)
                            {
                                totalRedundantSeconds += (DateTime.Now - redundantStart).TotalSeconds;
                                redundantStart = DateTime.MinValue;
                            }

                            redundantCheckStart = DateTime.MinValue;
                            return;
                        }

                        redundantCheckStart = DateTime.MinValue;
                    }

                    // MINIMUM REDUNDANT TIME
                    if (!redundantMinimumReached)
                    {
                        return;
                    }

                    // ?? STABLE EXIT
                    // STABLE EXIT
                    if (stableLongEnough && canSwitch)
                    {
                        await SetSpeedifyMode("speed", $"stable recovery | {context}");

                        if (redundantStart != DateTime.MinValue)
                        {
                            totalRedundantSeconds += (DateTime.Now - redundantStart).TotalSeconds;
                            redundantStart = DateTime.MinValue;
                        }

                        return;
                    }

                    // ANTI-STICK HARD EXIT
                    if (stable &&
                        goodStreak >= 4 &&
                        canSwitch)
                    {
                        await SetSpeedifyMode("speed", $"force exit stable | {context}");

                        if (redundantStart != DateTime.MinValue)
                        {
                            totalRedundantSeconds += (DateTime.Now - redundantStart).TotalSeconds;
                            redundantStart = DateTime.MinValue;
                        }
                        return;
                    }
                }
                // =========================
                // ENTER LOGIC
                // =========================
                if (allowRedundant &&
                    speedifyMode != "redundant" &&
                    canSwitch &&
                    shouldEnterRedundant)
                {
                    await SetSpeedifyMode("redundant",
                        $"{decisionReason} | SCORE:{score.Total:F2} | {context}");

                    redundantEnteredAt = DateTime.Now;
                    stableSince = DateTime.MinValue;

                    redundantStart = DateTime.Now;
                    preRedundantPing = avg;
                    redundantCheckStart = DateTime.Now;

                    return;
                }

                // =========================
                // FALLBACK SAFETY
                // =========================
                if (!allowRedundant &&
                    speedifyMode == "redundant" &&
                    canSwitch)
                {
                    await SetSpeedifyMode("speed", $"forced safety | {context}");

                    if (redundantStart != DateTime.MinValue)
                    {
                        totalRedundantSeconds += (DateTime.Now - redundantStart).TotalSeconds;
                        redundantStart = DateTime.MinValue;
                    }

                    return;
                }

                // =========================
                // DATA CALC
                // =========================
                double currentSeconds = totalRedundantSeconds;

                if (speedifyMode == "redundant" && redundantStart != DateTime.MinValue)
                {
                    currentSeconds += (DateTime.Now - redundantStart).TotalSeconds;
                }

                double mbUsed = (currentSeconds * estimatedKbps) / 8 / 1024;

                UpdateDailyUsage(mbUsed);
                double monthlyEstimate = GetMonthlyEstimate();

                // =========================
                // UI
                // =========================
                double natAge = (DateTime.Now - nat.LastNatRecreate).TotalSeconds;

                int natState;

                if (natAge < 15)
                    natState = 0;
                else if (natAge < 60)
                    natState = 1;
                else
                    natState = 2;

                if (overlay != null)
                {
                    string overlayMode =
                        speedifyMode == "redundant" ? "RED" :
                        speedifyMode == "speed" ? "SPD" :
                        "OFF";

                    string natText =
                        natState == 0 ? "NAT!" :
                        natState == 1 ? "NAT~" :
                        "NAT";

                    if ((DateTime.Now - lastOverlayUpdate).TotalMilliseconds >= 250)
                    {
                        lastOverlayUpdate = DateTime.Now;

                        overlay.UpdatePing($"{lastPing} ms", $"{overlayMode} {natText}", natState);
                    }
                }
                // =========================
                // DASHBOARD
                // =========================
                if (dashboard != null && dashboard.Visible)
                {
                    double remaining = switchCooldownSec - (DateTime.Now - lastSwitch).TotalSeconds;
                    remaining = Math.Max(0, remaining);

                    string modeVisual =
                        speedifyMode == "redundant" ? "REDUNDANT" :
                        speedifyMode == "speed" ? "SPEED" :
                        "OFF";

                    string policyText =
                        speedifyPolicy == SpeedifyPolicy.Off ? "OFF" :
                        speedifyPolicy == SpeedifyPolicy.SpeedOnly ? "SPEED ONLY" :
                        "SMART";

                    dashboard.info.Text =
                        $"POLICY: {policyText}\n" +

                        $"PING: {avg} ms\n" +
                        $"REC: {recentAvg} ms\n" +
                        $"MAX: {maxPing} ms\n" +
                        $"JIT: {jitter}\n" +
                        $"LOSS: {lossRate:F2}\n\n" +

                        $"PRE SPK: {preSpikePressure:F2}\n" +
                        $"BAD STRE: {badStreak}\n" +
                        $"GOOD STRE: {goodStreak}\n\n" +

                        $"SPIKE: {hardSpike}\n" +
                        $"MICRO: {micro}\n" +
                        $"PREDICTED: {predicted}\n\n" +
                        $"SCORE: {score.Total:F2}\n" +
                        $"SCORE FULL: {score}\n" +

                        $"STATE: {currentNetState}\n" +
                        $"MODE: {modeVisual}\n\n" +

                        $"ALLOW RED: {allowRedundant}\n" +
                        $"COOLDOWN: {(remaining > 0 ? $"{remaining:F0}s" : "READY")}\n\n" +

                        $"DEBUG: {debugMode}\n" +
                        $"SIM SPD: {simulateSpeedify}\n" +
                        $"SIM MULTI: {simulateMultiConnection}\n" +

                        $"LAST DECISION RZN: {lastDecisionReason}\n" +
                        $"LAST BLOCK RZN: {lastBlockReason}\n\n" +

                        $"TRAFFIC: {bytesPerSecond / 1024:F0} KB/s\n" +
                        $"DWNLD: {hardDownload}\n" +
                        $"SUSPEC: {unrealisticTraffic}\n\n" +

                        $"DATA: {mbUsed:F1} MB\n" +
                        $"MONTH: {monthlyEstimate:F0} MB";
                }
                // =========================
                // FINAL LOG
                // =========================
                var log = new NetLog
                {
                    Time = DateTime.Now,
                    Avg = avg,
                    Recent = recentAvg,
                    Max = maxPing,
                    Jitter = jitter,
                    Loss = lossRate,

                    Pressure = preSpikePressure,
                    Bad = badStreak,
                    Good = goodStreak,

                    TrafficKB = bytesPerSecond / 1024,
                    Download = hardDownload,
                    Unreal = unrealisticTraffic,

                    Mode = speedifyMode,
                    Allow = allowRedundant,

                    Score = score,
                    Decision = $"{lastDecisionReason} | SCORE:{score.Total:F2}",
                    Block = lastBlockReason,

                    Debug = debugMode
                };

                // SMART LOGGING
                bool important =
                        hardSpike ||
                        predicted ||
                        score.Total > 3.5 ||
                        speedifyMode == "redundant";

                if (important || debugMode)
                    Log("decisions", log.ToString());
            }
            finally
            {
                isRunning = false;
            }
        }
    }
}