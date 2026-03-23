using System;
using System.Drawing;
using System.Windows.Forms;

namespace WTFnetApp
{
    public class UIOverlay : Form
    {
        private readonly Label pingLabel;
        private readonly Label modeLabel;

        private string lastText = "";
        private string lastModeText = "";
        private Color lastPingColor = Color.Empty;
        private Color lastModeColor = Color.Empty;

        public UIOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            ForeColor = Color.White;
            Opacity = 0.80;
            Width = 220;
            Height = 70;
            Left = 20;
            Top = 20;

            pingLabel = new Label();
            pingLabel.AutoSize = true;
            pingLabel.Font = new Font("Segoe UI", 16F, FontStyle.Bold);
            pingLabel.ForeColor = Color.Lime;
            pingLabel.BackColor = Color.Transparent;
            pingLabel.Left = 12;
            pingLabel.Top = 8;
            pingLabel.Text = "-- ms";

            modeLabel = new Label();
            modeLabel.AutoSize = true;
            modeLabel.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
            modeLabel.ForeColor = Color.White;
            modeLabel.BackColor = Color.Transparent;
            modeLabel.Left = 12;
            modeLabel.Top = 38;
            modeLabel.Text = "NAT";

            Controls.Add(pingLabel);
            Controls.Add(modeLabel);
        }

        public void RenderOverlay(OverlayRenderState render)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => RenderOverlay(render)));
                return;
            }

            if (render == null)
                return;

            if (render.PingText == lastText &&
                render.NatText == lastModeText &&
                render.PingColor == lastPingColor &&
                render.NatColor == lastModeColor)
                return;

            lastText = render.PingText;
            lastModeText = render.NatText;
            lastPingColor = render.PingColor;
            lastModeColor = render.NatColor;

            pingLabel.Text = render.PingText;
            pingLabel.ForeColor = render.PingColor;

            modeLabel.Text = render.NatText;
            modeLabel.ForeColor = render.NatColor;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }
    }
}