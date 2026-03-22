using System;
using System.Drawing;
using System.Windows.Forms;

namespace WTFnetApp
{
    public partial class UIOverlay : Form
    {
        private string lastText = "";
        private string lastModeText = "";
        private Color lastPingColor = Color.Empty;
        private Color lastModeColor = Color.Empty;

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
    }
}
protected override bool ShowWithoutActivation => true;
    }
}