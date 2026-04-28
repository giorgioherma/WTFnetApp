using System;
using System.IO;
using System.Windows.Forms;

namespace WTFnetApp
{
    public sealed class SpeedifySettingsForm : Form
    {
        private readonly TextBox speedifyCliPathTextBox;
        private readonly Label statusLabel;

        public SpeedifySettingsForm()
        {
            Text = "WTFnetApp Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Width = 640;
            Height = 180;

            Label pathLabel = new Label
            {
                Left = 12,
                Top = 18,
                Width = 130,
                Text = "Speedify CLI Path"
            };

            speedifyCliPathTextBox = new TextBox
            {
                Left = 150,
                Top = 14,
                Width = 360,
                ReadOnly = true
            };

            Button browseButton = new Button
            {
                Left = 520,
                Top = 12,
                Width = 90,
                Text = "Browse..."
            };
            browseButton.Click += BrowseButton_Click;

            Button clearButton = new Button
            {
                Left = 520,
                Top = 46,
                Width = 90,
                Text = "Clear"
            };
            clearButton.Click += ClearButton_Click;

            statusLabel = new Label
            {
                Left = 150,
                Top = 50,
                Width = 360,
                Text = "Status: Not configured"
            };

            Button closeButton = new Button
            {
                Left = 520,
                Top = 96,
                Width = 90,
                Text = "Close",
                DialogResult = DialogResult.OK
            };
            AcceptButton = closeButton;

            Controls.Add(pathLabel);
            Controls.Add(speedifyCliPathTextBox);
            Controls.Add(browseButton);
            Controls.Add(clearButton);
            Controls.Add(statusLabel);
            Controls.Add(closeButton);

            RefreshFromSettings();
        }

        private void BrowseButton_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Filter = "Speedify CLI (speedify_cli.exe)|speedify_cli.exe|Executable (*.exe)|*.exe";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                dialog.Title = "Select Speedify CLI";
                dialog.InitialDirectory = ResolveInitialDirectory();

                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                string selectedPath = dialog.FileName;
                if (!File.Exists(selectedPath))
                {
                    MessageBox.Show(this, "Invalid file selected", "WTFnetApp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (!string.Equals(Path.GetFileName(selectedPath), "speedify_cli.exe", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(
                        this,
                        "Selected file is not named speedify_cli.exe. It may not work as expected.",
                        "WTFnetApp",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                SaveSpeedifyCliPath(selectedPath);
            }
        }

        private void ClearButton_Click(object sender, EventArgs e)
        {
            SaveSpeedifyCliPath(string.Empty);
        }

        private void SaveSpeedifyCliPath(string path)
        {
            Properties.Settings.Default.SpeedifyCliPath = path ?? string.Empty;
            Properties.Settings.Default.Save();
            RefreshFromSettings();

            if (string.IsNullOrWhiteSpace(path))
                Logger.Log("settings", "speedify cli path cleared");
            else
                Logger.Log("settings", "speedify cli path set | path=" + path);
        }

        private void RefreshFromSettings()
        {
            string path = Properties.Settings.Default.SpeedifyCliPath;
            speedifyCliPathTextBox.Text = path ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                statusLabel.Text = "Status: Not configured";
                return;
            }

            statusLabel.Text = File.Exists(path)
                ? "Status: Valid"
                : "Status: File not found";
        }

        private static string ResolveInitialDirectory()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Speedify"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Speedify")
            };

            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate))
                    return candidate;
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        }
    }
}
