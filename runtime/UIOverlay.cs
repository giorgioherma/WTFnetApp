using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WTFnetApp
{
    public sealed class UIOverlay : Form
    {
        private string pingText = "-- ms";
        private string jitterText = "J --";
        private string modeText = "NAT";

        private Color pingColor = Color.LimeGreen;
        private Color jitterColor = Color.DeepSkyBlue;
        private Color modeColor = Color.Gold;

        private string pendingPingText = "-- ms";
        private string pendingJitterText = "J --";
        private string pendingModeText = "NAT";

        private Color pendingPingColor = Color.LimeGreen;
        private Color pendingJitterColor = Color.DeepSkyBlue;
        private Color pendingModeColor = Color.Gold;

        private string lastPaintedPingText = string.Empty;
        private string lastPaintedJitterText = string.Empty;
        private string lastPaintedModeText = string.Empty;

        private Color lastPaintedPingColor = Color.Empty;
        private Color lastPaintedJitterColor = Color.Empty;
        private Color lastPaintedModeColor = Color.Empty;

        private readonly object stateLock = new object();

        private readonly Font pingFont;
        private readonly Font infoFont;
        private readonly Timer renderTimer;

        private const int OverlayWidth = 180;
        private const int OverlayHeight = 92;
        private const int CornerRadius = 14;

        private const int RenderIntervalMs = 250;

        private const int PanelAlpha = 120;
        private const int ShineAlpha = 18;
        private const int BorderAlpha = 90;
        private const int ShadowAlpha = 130;

        public UIOverlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;

            Width = OverlayWidth;
            Height = OverlayHeight;
            Left = 20;
            Top = 20;

            BackColor = Color.Black;
            DoubleBuffered = true;

            pingFont = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Point);
            infoFont = new Font("Segoe UI", 10.25f, FontStyle.Bold, GraphicsUnit.Point);

            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw,
                true);

            renderTimer = new Timer();
            renderTimer.Interval = RenderIntervalMs;
            renderTimer.Tick += RenderTimer_Tick;

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_LAYERED = 0x00080000;
                const int WS_EX_TRANSPARENT = 0x00000020;
                const int WS_EX_TOOLWINDOW = 0x00000080;
                const int WS_EX_NOACTIVATE = 0x08000000;

                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTopMost();
            RenderNow(force: true);

            if (!DesignMode)
                renderTimer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyTopMost();
            RenderNow(force: true);

            if (!renderTimer.Enabled)
                renderTimer.Start();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            ApplyTopMost();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x0021;
            const int MA_NOACTIVATE = 3;

            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }

            base.WndProc(ref m);
        }

        private void RenderTimer_Tick(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated)
                return;

            RenderNow(force: false);
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            RefreshTopMost();
        }

        public void RenderOverlay(OverlayRenderState render)
        {
            if (render == null || IsDisposed)
                return;

            string newPingText = render.PingText;
            string newJitterText = render.JitterText;
            string newModeText = render.NatText;

            Color newPingColor = render.PingColor;
            Color newJitterColor = render.JitterColor;
            Color newModeColor = render.NatColor;

            if (string.IsNullOrWhiteSpace(newPingText))
                newPingText = "-- ms";

            if (string.IsNullOrWhiteSpace(newJitterText))
                newJitterText = "J --";

            if (string.IsNullOrWhiteSpace(newModeText))
                newModeText = "NAT";

            lock (stateLock)
            {
                pendingPingText = newPingText;
                pendingJitterText = newJitterText;
                pendingModeText = newModeText;

                pendingPingColor = newPingColor;
                pendingJitterColor = newJitterColor;
                pendingModeColor = newModeColor;
            }

            if (InvokeRequired || !IsHandleCreated)
                return;

            if (!Visible)
                Show();
        }

        public void SetOverlayPosition(int x, int y)
        {
            if (IsDisposed)
                return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => SetOverlayPosition(x, y)));
                return;
            }

            Left = x;
            Top = y;
            RenderNow(force: true);
        }

        public void RefreshTopMost()
        {
            if (IsDisposed)
                return;

            if (InvokeRequired)
            {
                BeginInvoke(new Action(RefreshTopMost));
                return;
            }

            ApplyTopMost();
            RenderNow(force: true);
        }

        private void RenderNow(bool force)
        {
            if (!IsHandleCreated)
                return;

            string nextPingText;
            string nextJitterText;
            string nextModeText;

            Color nextPingColor;
            Color nextJitterColor;
            Color nextModeColor;

            lock (stateLock)
            {
                nextPingText = pendingPingText;
                nextJitterText = pendingJitterText;
                nextModeText = pendingModeText;

                nextPingColor = pendingPingColor;
                nextJitterColor = pendingJitterColor;
                nextModeColor = pendingModeColor;
            }

            bool changed =
                force ||
                nextPingText != lastPaintedPingText ||
                nextJitterText != lastPaintedJitterText ||
                nextModeText != lastPaintedModeText ||
                nextPingColor != lastPaintedPingColor ||
                nextJitterColor != lastPaintedJitterColor ||
                nextModeColor != lastPaintedModeColor;

            if (!changed)
                return;

            pingText = nextPingText;
            jitterText = nextJitterText;
            modeText = nextModeText;

            pingColor = nextPingColor;
            jitterColor = nextJitterColor;
            modeColor = nextModeColor;

            if (!Visible)
                Show();

            using (Bitmap bitmap = new Bitmap(OverlayWidth, OverlayHeight, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                Rectangle panelRect = new Rectangle(0, 0, OverlayWidth - 1, OverlayHeight - 1);

                using (GraphicsPath panelPath = CreateRoundedRect(panelRect, CornerRadius))
                {
                    using (SolidBrush bgBrush = new SolidBrush(Color.FromArgb(PanelAlpha, 34, 34, 40)))
                        g.FillPath(bgBrush, panelPath);

                    Rectangle shineRect = new Rectangle(1, 1, OverlayWidth - 3, (OverlayHeight / 2) - 4);
                    using (GraphicsPath shinePath = CreateRoundedRect(shineRect, CornerRadius - 2))
                    using (SolidBrush shineBrush = new SolidBrush(Color.FromArgb(ShineAlpha, 255, 255, 255)))
                        g.FillPath(shineBrush, shinePath);

                    using (Pen borderPen = new Pen(Color.FromArgb(BorderAlpha, 150, 150, 160), 1f))
                        g.DrawPath(borderPen, panelPath);
                }

                using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(ShadowAlpha, 0, 0, 0)))
                using (SolidBrush pingBrush = new SolidBrush(pingColor))
                using (SolidBrush jitterBrush = new SolidBrush(jitterColor))
                using (SolidBrush modeBrush = new SolidBrush(modeColor))
                {
                    g.DrawString(pingText, pingFont, shadowBrush, 14f, 7f);
                    g.DrawString(pingText, pingFont, pingBrush, 13f, 6f);

                    g.DrawString(jitterText, infoFont, shadowBrush, 15f, 38f);
                    g.DrawString(jitterText, infoFont, jitterBrush, 14f, 37f);

                    g.DrawString(modeText, infoFont, shadowBrush, 15f, 59f);
                    g.DrawString(modeText, infoFont, modeBrush, 14f, 58f);
                }

                SetBitmap(bitmap);
            }

            lastPaintedPingText = pingText;
            lastPaintedJitterText = jitterText;
            lastPaintedModeText = modeText;

            lastPaintedPingColor = pingColor;
            lastPaintedJitterColor = jitterColor;
            lastPaintedModeColor = modeColor;

            ApplyTopMost();
        }

        private void ApplyTopMost()
        {
            if (!IsHandleCreated)
                return;

            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_TOPMOST,
                Left,
                Top,
                Width,
                Height,
                NativeMethods.SWP_NOACTIVATE);
        }

        private static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();

            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();

            return path;
        }

        private void SetBitmap(Bitmap bitmap)
        {
            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;

            try
            {
                screenDc = NativeMethods.GetDC(IntPtr.Zero);
                memDc = NativeMethods.CreateCompatibleDC(screenDc);

                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = NativeMethods.SelectObject(memDc, hBitmap);

                NativeMethods.SIZE size = new NativeMethods.SIZE(bitmap.Width, bitmap.Height);
                NativeMethods.POINT sourcePoint = new NativeMethods.POINT(0, 0);
                NativeMethods.POINT topPos = new NativeMethods.POINT(Left, Top);

                NativeMethods.BLENDFUNCTION blend = new NativeMethods.BLENDFUNCTION();
                blend.BlendOp = NativeMethods.AC_SRC_OVER;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = NativeMethods.AC_SRC_ALPHA;

                NativeMethods.UpdateLayeredWindow(
                    Handle,
                    screenDc,
                    ref topPos,
                    ref size,
                    memDc,
                    ref sourcePoint,
                    0,
                    ref blend,
                    NativeMethods.ULW_ALPHA);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero)
                    NativeMethods.SelectObject(memDc, oldBitmap);

                if (hBitmap != IntPtr.Zero)
                    NativeMethods.DeleteObject(hBitmap);

                if (memDc != IntPtr.Zero)
                    NativeMethods.DeleteDC(memDc);

                if (screenDc != IntPtr.Zero)
                    NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

                if (renderTimer != null)
                {
                    renderTimer.Stop();
                    renderTimer.Tick -= RenderTimer_Tick;
                    renderTimer.Dispose();
                }

                if (pingFont != null)
                    pingFont.Dispose();

                if (infoFont != null)
                    infoFont.Dispose();
            }

            base.Dispose(disposing);
        }

        private static class NativeMethods
        {
            public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

            public const int ULW_ALPHA = 0x00000002;
            public const byte AC_SRC_OVER = 0x00;
            public const byte AC_SRC_ALPHA = 0x01;

            public const uint SWP_NOACTIVATE = 0x0010;

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT
            {
                public int X;
                public int Y;

                public POINT(int x, int y)
                {
                    X = x;
                    Y = y;
                }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct SIZE
            {
                public int cx;
                public int cy;

                public SIZE(int width, int height)
                {
                    cx = width;
                    cy = height;
                }
            }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            public struct BLENDFUNCTION
            {
                public byte BlendOp;
                public byte BlendFlags;
                public byte SourceConstantAlpha;
                public byte AlphaFormat;
            }

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool UpdateLayeredWindow(
                IntPtr hwnd,
                IntPtr hdcDst,
                ref POINT pptDst,
                ref SIZE psize,
                IntPtr hdcSrc,
                ref POINT pprSrc,
                int crKey,
                ref BLENDFUNCTION pblend,
                int dwFlags);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern IntPtr GetDC(IntPtr hWnd);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            [DllImport("gdi32.dll", SetLastError = true)]
            public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

            [DllImport("gdi32.dll", SetLastError = true)]
            public static extern bool DeleteDC(IntPtr hdc);

            [DllImport("gdi32.dll", SetLastError = true)]
            public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

            [DllImport("gdi32.dll", SetLastError = true)]
            public static extern bool DeleteObject(IntPtr hObject);

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPos(
                IntPtr hWnd,
                IntPtr hWndInsertAfter,
                int X,
                int Y,
                int cx,
                int cy,
                uint uFlags);
        }
    }
}