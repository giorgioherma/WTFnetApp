using System.Drawing;
using System.Windows.Forms;

namespace WTFnetApp
{
    public class LteDashboardForm : Form
    {
        public Label info;

        public LteDashboardForm()
        {
            Text = "LTE / 5G Gaming Quality";
            Size = new Size(360, 320);

            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            TopMost = true;

            info = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10),
                ForeColor = Color.Lime,
                BackColor = Color.Black,
                TextAlign = ContentAlignment.TopLeft
            };

            Controls.Add(info);
        }
    }
}