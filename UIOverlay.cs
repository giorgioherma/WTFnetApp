using System;
using System.Drawing;
using System.Windows.Forms;

namespace WTFnetApp
{
    public partial class UIOverlay : Form
    {
        private Label pingLabel;
        private Label modeLabel;

        private string lastText = "";
        private string lastMode = "";
        private int lastNatState = -1;

        public UIOverlay()
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
        }

        private void PositionTopLeft()
        {
            var screen = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(screen.Left + 12, screen.Top + 12);
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
}