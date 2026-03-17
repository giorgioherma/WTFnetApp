using System;
using System.Drawing;
using System.Windows.Forms;

namespace WTFnetApp
{
    public class Tray
    {
        NotifyIcon trayIcon;
        ContextMenuStrip trayMenu;

        DerGrosseGeneralstab core;
        LteDashboardForm dashboard;

        public Tray(DerGrosseGeneralstab core)
        {
            this.core = core;

            Logger.Log("tray", "tray initialized");

            InitializeTray();
        }

        void InitializeTray()
        {
            trayMenu = new ContextMenuStrip();

            trayMenu.Items.Add("Dashboard", null, (s, e) => ToggleDashboard());
            trayMenu.Items.Add("Toggle Overlay", null, (s, e) => core.ToggleOverlay());
            trayMenu.Items.Add("Ping Servers Now", null, async (s, e) => await RunManualPingScan());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, Exit);

            trayIcon = new NotifyIcon
            {
                Text = "WTFNetApp",
                Icon = SystemIcons.Application,
                ContextMenuStrip = trayMenu,
                Visible = true
            };

            trayIcon.DoubleClick += (s, e) => core.ToggleOverlay();
        }


        async System.Threading.Tasks.Task RunManualPingScan()
        {
            try
            {
                await core.RunManualServerPingScan();
            }
            catch (Exception ex)
            {
                Logger.Log("server", "manual ping scan failed " + ex.Message);
            }
        }

        void ToggleDashboard()
        {
            if (dashboard == null || dashboard.IsDisposed)
            {
                dashboard = new LteDashboardForm();
                dashboard.Show();
            }
            else
            {
                dashboard.Visible = !dashboard.Visible;
            }
        }

        void Exit(object sender, EventArgs e)
        {
            Logger.Log("system", "application exiting");

            trayIcon.Visible = false;
            trayIcon.Dispose();

            Application.Exit();
        }
    }
}