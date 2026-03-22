using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WTFnetApp
{
    public class VPNServerList
    {
        private readonly HashSet<string> knownServers = new HashSet<string>();
        private readonly string storePath = "speedify_servers.txt";
        private CancellationTokenSource token;
        private bool running;

        public int GetSpeedifyPid()
        {
            var processes = Process.GetProcessesByName("Speedify");
            if (processes.Length == 0)
                return -1;

            return processes[0].Id;
        }

        public List<string> GetSpeedifyServers()
        {
            var servers = new List<string>();

            int pid = GetSpeedifyPid();
            if (pid <= 0)
                return servers;

            try
            {
                var proc = new Process();
                proc.StartInfo.FileName = "netstat";
                proc.StartInfo.Arguments = "-ano";
                proc.StartInfo.RedirectStandardOutput = true;
                proc.StartInfo.UseShellExecute = false;
                proc.StartInfo.CreateNoWindow = true;
                proc.Start();

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(2000);

                foreach (var line in output.Split('\n'))
                {
                    if (!line.Contains(pid.ToString()))
                        continue;

                    if (!line.Contains("TCP"))
                        continue;

                    var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3)
                        continue;

                    string remote = parts[2];
                    if (!remote.Contains(":"))
                        continue;

                    string ip = remote.Split(':')[0];
                    if (!servers.Contains(ip))
                        servers.Add(ip);
                }
            }
            catch (Exception ex)
            {
                Logger.Log("server", "tracker error " + ex.Message);
            }

            return servers;
        }

        public void Start()
        {
            if (running)
                return;

            running = true;
            token = new CancellationTokenSource();

            foreach (var ip in LoadServers())
                knownServers.Add(ip);

            _ = CheckServersOnStartup();
            _ = Task.Run(() => TrackLoop(token.Token));
        }

        public void Stop()
        {
            if (!running)
                return;

            running = false;
            token?.Cancel();
        }

        private async Task TrackLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TrackServers();

                try
                {
                    await Task.Delay(5000, ct);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private void TrackServers()
        {
            var servers = GetSpeedifyServers();

            foreach (var ip in servers)
            {
                if (knownServers.Contains(ip))
                    continue;

                knownServers.Add(ip);
                Logger.Log("server", $"NEW SPEEDIFY SERVER {ip}");
                SaveServer(ip);
            }
        }

        public Task RunManualPingScan()
        {
            return PingServers("MANUAL");
        }

        private void SaveServer(string ip)
        {
            File.AppendAllText(storePath, ip + Environment.NewLine);
        }

        private List<string> LoadServers()
        {
            if (!File.Exists(storePath))
                return new List<string>();

            return File.ReadAllLines(storePath)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToList();
        }

        private async Task CheckServersOnStartup()
        {
            await PingServers("STARTUP");
        }

        private async Task PingServers(string reason)
        {
            var servers = LoadServers();

            foreach (var activeServer in GetSpeedifyServers())
            {
                if (!servers.Contains(activeServer))
                    servers.Add(activeServer);
            }

            foreach (var ip in servers)
            {
                int ping = await WtfFastPing.Ping(ip);
                Logger.Log("server", $"SERVER {ip} {reason} PING {ping}ms");
            }
        }
    }
}
