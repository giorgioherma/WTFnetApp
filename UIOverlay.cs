using System;
using System.Drawing;
using System.Windows.Forms;

namespace WTFnetApp
{
    public partial class UIOverlay : Form
    {
        private Label titleLabel;
        private Label statusLabel;

        private Label metricsHeader;
        private Label latencyLabel;
        private Label jitterLabel;
        private Label lossLabel;
        private Label driftLabel;

        private Label diagnosticsHeader;
        private Label qDelayLabel;
        private Label qGrowthLabel;
        private Label qAccelLabel;
        private Label retransLabel;
        private Label congestionLabel;
        private Label issueLabel;

        private Panel natDot;
        private Panel mobileDot;
        private Panel ethernetDot;
        private Panel sliderTrack;
        private Panel sliderFill;

        private string lastKey = "";

        public UIOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(290, 340);

            TopMost = true;
            ShowInTaskbar = false;
            MaximizeBox = false;
            MinimizeBox = false;

            BackColor = Color.FromArgb(230, 230, 230);
            Opacity = 180.0 / 255.0;

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);

            var font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);

            var headerStrip = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(290, 32),
                BackColor = Color.FromArgb(180, 180, 180)
            };

            titleLabel = new Label
            {
                Location = new Point(10, 6),
                Size = new Size(200, 18),
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(25, 25, 25),
                BackColor = Color.Transparent,
                Text = "NETWORK STATUS"
            };

            natDot = NewDot(262, 8, 18, 14);
            headerStrip.Controls.Add(titleLabel);
            headerStrip.Controls.Add(natDot);

            statusLabel = NewLabel(font, 36, "Waiting for network telemetry");

            metricsHeader = NewSectionLabel(60, "METRICS");
            latencyLabel = NewLabel(font, 82, "Latency: -- ms");
            jitterLabel = NewLabel(font, 104, "Jitter: -- ms");
            lossLabel = NewLabel(font, 126, "Loss: -- %");
            driftLabel = NewLabel(font, 148, "Drift: -- ms");

            diagnosticsHeader = NewSectionLabel(172, "DIAGNOSTICS");
            qDelayLabel = NewLabel(font, 194, "QueueDelay: --");
            qGrowthLabel = NewLabel(font, 216, "QueueGrowth: --");
            qAccelLabel = NewLabel(font, 238, "QueueAccel: --");
            retransLabel = NewLabel(font, 260, "Retrans: --");
            congestionLabel = NewLabel(font, 282, "Congestion: --");
            issueLabel = NewLabel(font, 304, "Issue: --");

            // Keep minimalistic indicators and slider without text.
            sliderTrack = new Panel
            {
                Location = new Point(186, 38),
                Size = new Size(66, 8),
                BackColor = Color.FromArgb(205, 205, 205)
            };

            sliderFill = new Panel
            {
                Location = new Point(0, 0),
                Size = new Size(0, 8),
                BackColor = Color.FromArgb(90, 90, 90)
            };

            sliderTrack.Controls.Add(sliderFill);

            mobileDot = NewDot(258, 35, 12, 12);
            ethernetDot = NewDot(274, 35, 12, 12);

            Controls.Add(headerStrip);
            Controls.Add(statusLabel);
            Controls.Add(metricsHeader);
            Controls.Add(latencyLabel);
            Controls.Add(jitterLabel);
            Controls.Add(lossLabel);
            Controls.Add(driftLabel);
            Controls.Add(diagnosticsHeader);
            Controls.Add(qDelayLabel);
            Controls.Add(qGrowthLabel);
            Controls.Add(qAccelLabel);
            Controls.Add(retransLabel);
            Controls.Add(congestionLabel);
            Controls.Add(issueLabel);
            Controls.Add(sliderTrack);
            Controls.Add(mobileDot);
            Controls.Add(ethernetDot);

            PositionTopLeft();
        }

        private Label NewLabel(Font font, int y, string text)
        {
            return new Label
            {
                Location = new Point(12, y),
                Size = new Size(270, 20),
                Font = font,
                ForeColor = Color.FromArgb(25, 25, 25),
                BackColor = Color.Transparent,
                Text = text
            };
        }

        private Label NewSectionLabel(int y, string text)
        {
            return new Label
            {
                Location = new Point(12, y),
                Size = new Size(270, 20),
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                ForeColor = Color.FromArgb(70, 70, 70),
                BackColor = Color.Transparent,
                Text = text
            };
        }

        private Panel NewDot(int x, int y, int w, int h)
        {
            return new Panel
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                BackColor = Color.Gray
            };
        }

        private void PositionTopLeft()
        {
            var screen = Screen.PrimaryScreen.Bounds;
            Location = new Point(screen.Left + 12, screen.Top + 12);
        }

        public void UpdateStatus(MetricsSnapshot snapshot, int natState, string systemState, int weight)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdateStatus(snapshot, natState, systemState, weight)));
                return;
            }

            string key = snapshot == null
                ? $"waiting|{natState}|{systemState}|{weight}"
                : $"{natState}|{systemState}|{weight}|{snapshot.Recent:F1}|{snapshot.Jitter:F1}|{snapshot.PacketLoss:F2}|{snapshot.LatencyDrift:F1}|{snapshot.QueueDelay:F1}|{snapshot.QueueGrowth:F1}|{snapshot.QueueAcceleration:F1}|{snapshot.RetransmissionRate:F3}|{snapshot.CongestionScore:F1}|{snapshot.IssueSource}|{snapshot.ActiveConnections}";

            if (key == lastKey)
                return;

            lastKey = key;
            natDot.BackColor = NatColor(natState);

            if (snapshot == null)
            {
                statusLabel.Text = "Waiting for network telemetry";
                latencyLabel.Text = "Latency: -- ms";
                jitterLabel.Text = "Jitter: -- ms";
                lossLabel.Text = "Loss: -- %";
                driftLabel.Text = "Drift: -- ms";
                qDelayLabel.Text = "QueueDelay: --";
                qGrowthLabel.Text = "QueueGrowth: --";
                qAccelLabel.Text = "QueueAccel: --";
                retransLabel.Text = "Retrans: --";
                congestionLabel.Text = "Congestion: --";
                issueLabel.Text = "Issue: --";
                sliderFill.Width = 0;
                mobileDot.BackColor = Color.Gray;
                ethernetDot.BackColor = Color.Gray;
                return;
            }

            double latency = Math.Max(0, snapshot.Recent);
            double jitter = Math.Max(0, snapshot.Jitter);
            double loss = Math.Max(0, snapshot.PacketLoss) * 100.0;
            double drift = snapshot.LatencyDrift;

            statusLabel.Text = $"NAT: {systemState}";
            latencyLabel.Text = $"Latency: {latency:F1} ms";
            jitterLabel.Text = $"Jitter: {jitter:F1} ms";
            lossLabel.Text = $"Loss: {loss:F1} %";
            driftLabel.Text = $"Drift: {drift:+0.0;-0.0;0.0} ms";

            qDelayLabel.Text = $"QueueDelay: {snapshot.QueueDelay:F1}";
            qGrowthLabel.Text = $"QueueGrowth: {snapshot.QueueGrowth:+0.0;-0.0;0.0}";
            qAccelLabel.Text = $"QueueAccel: {snapshot.QueueAcceleration:+0.0;-0.0;0.0}";
            retransLabel.Text = $"Retrans: {snapshot.RetransmissionRate:F3}";
            congestionLabel.Text = $"Congestion: {snapshot.CongestionScore:F1}";
            issueLabel.Text = $"Issue: {snapshot.IssueSource}";

            latencyLabel.ForeColor = PingColor(latency);

            weight = Math.Max(0, Math.Min(100, weight));
            sliderFill.Width = (int)(sliderTrack.Width * (weight / 100.0));

            bool multi = snapshot.ActiveConnections > 1 || snapshot.Adapter2Latency > 0;
            mobileDot.BackColor = multi ? Color.FromArgb(56, 168, 96) : Color.FromArgb(140, 140, 140);
            ethernetDot.BackColor = snapshot.Recent > 0 ? Color.FromArgb(56, 168, 96) : Color.FromArgb(140, 140, 140);
        }

        private Color NatColor(int natState)
        {
            switch (natState)
            {
                case 0: return Color.Red;
                case 1: return Color.Gold;
                default: return Color.LimeGreen;
            }
        }

        private Color PingColor(double ping)
        {
            if (ping < 50) return Color.LimeGreen;
            if (ping < 90) return Color.Orange;
            return Color.Red;
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
}
