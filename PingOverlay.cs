using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Threading;
using WinTimer = System.Windows.Forms.Timer;

namespace WTFnetApp
{
    public class DashboardForm : Form
    {
        public Label info;


4       private string displayText = "0 ms";

        public PingOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;

            BackColor = Color.Black;
            TransparencyKey = Color.Black;
            Opacity = 0.9;

            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);

            StartPosition = FormStartPosition.Manual;
            Location = new Point(10, 10);
            Size = new Size(220, 90);
        }

        public void UpdatePing(string text)
        {
            displayText = text;
            Invalidate();
        }
        private Color GetPingColor(string text)
        {
            if (int.TryParse(text.Replace(" ms", ""), out int ping))
            {
                if (ping < 50) return Color.LimeGreen;
                if (ping < 100) return Color.Orange;
                return Color.Red;
            }
            return Color.White;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var font = new Font("Segoe UI", 26, FontStyle.Bold);
            var size = g.MeasureString(displayText, font);

            float x = (Width - size.Width) / 2;
            float y = (Height - size.Height) / 2;

            Color color = GetPingColor(displayText);

            // 🔥 SUBTLE GLOW (tight, no gaps)
            using (var glowBrush = new SolidBrush(Color.FromArgb(15, color)))
            {
                g.DrawString(displayText, font, glowBrush, x - 1, y);
                g.DrawString(displayText, font, glowBrush, x + 1, y);
                g.DrawString(displayText, font, glowBrush, x, y - 1);
                g.DrawString(displayText, font, glowBrush, x, y + 1);
            }

            // 🔥 THIN OUTLINE
            using (var outlineBrush = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            {
                g.DrawString(displayText, font, outlineBrush, x - 1, y);
                g.DrawString(displayText, font, outlineBrush, x + 1, y);
                g.DrawString(displayText, font, outlineBrush, x, y - 1);
                g.DrawString(displayText, font, outlineBrush, x, y + 1);
            }

            // 🟢 MAIN TEXT (ONLY ONCE)
            using var mainBrush = new SolidBrush(color);
            g.DrawString(displayText, font, mainBrush, x, y);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_TRANSPARENT = 0x20;
                const int WS_EX_LAYERED = 0x80000;

                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
                return cp;
            }
        }
    }
}