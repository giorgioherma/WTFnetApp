using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WTFnetApp
{
    internal static class Program
    {
        [STAThread]
        static async Task Main()
        {
            Logger.Log("system", "application starting");

            Application.ThreadException += (s, e) =>
            {
                Logger.Log("crash", e.Exception.ToString());
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Logger.Log("crash", e.ExceptionObject.ToString());
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var generalstab = new DerGrosseGeneralstab();
            generalstab.Startup();

            var tray = new Tray(generalstab);

            Application.Run();
        }
    }
}