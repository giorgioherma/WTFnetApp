using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WTFnetApp
{
    public class SpeedifyTelemetryReader
    {
        private const string SpeedifyWsHost = "127.0.0.1";
        private const int SpeedifyWsPort = 9330;
        private const string SpeedifyWsPath = "/";

        private ClientWebSocket ws;
        private volatile bool running;

        private readonly HashSet<string> capturedSnapshotEvents = new HashSet<string>();
        private readonly HashSet<string> seenEventNames = new HashSet<string>();

        private readonly Action<MetricsSnapshot> onSnapshot;
        private readonly SpeedifyMetricsParser parser = new SpeedifyMetricsParser();

        public SpeedifyTelemetryReader(Action<MetricsSnapshot> handler)
        {
            onSnapshot = handler;
        }

        public void Start()
        {
            if (running)
                return;

            running = true;
            _ = RunLoop();
        }

        public async Task StopAsync()
        {
            running = false;

            try
            {
                if (ws != null)
                {
                    if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                    {
                        await ws.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "stopping",
                            CancellationToken.None);
                    }

                    ws.Dispose();
                    ws = null;
                }
            }
            catch
            {
            }

            Logger.Log("speedify", "websocket telemetry stopped");
        }

        private async Task RunLoop()
        {
            while (running)
            {
                try
                {
                    Logger.Log("speedify", "websocket telemetry starting");
                    await StreamLoop();
                }
                catch (Exception ex)
                {
                    if (running)
                        Logger.Log("speedify", "stream loop error: " + ex.Message);
                }

                if (running)
                    await Task.Delay(2000);
            }
        }

        private async Task StreamLoop()
        {
            try
            {
                capturedSnapshotEvents.Clear();
                seenEventNames.Clear();

                ws = new ClientWebSocket();
                ws.Options.AddSubProtocol("event-protocol");
                ws.Options.SetRequestHeader("Origin", "easywsclient://");

                string uri = "ws://" + SpeedifyWsHost + ":" + SpeedifyWsPort + SpeedifyWsPath;
                Logger.Log("speedify", "connecting websocket: " + uri);

                await ws.ConnectAsync(new Uri(uri), CancellationToken.None);

                Logger.Log("speedify", "websocket connected");

                await SendRequests(ws, "request_referrer_data");
                await ReceiveLoop(ws);

                if (running)
                    Logger.Log("speedify", "websocket stream ended - restarting");
            }
            catch (Exception ex)
            {
                if (running)
                    Logger.Log("speedify", "websocket error: " + ex.Message);
            }

            try
            {
                if (ws != null)
                {
                    ws.Dispose();
                    ws = null;
                }
            }
            catch
            {
            }
        }

        private async Task SendRequests(ClientWebSocket socket, params string[] requestNames)
        {
            foreach (string requestName in requestNames)
            {
                string msg = "[\"" + requestName + "\",null,\"speedify_sdk\"]";
                await Send(socket, msg);
            }
        }

        private async Task ReceiveLoop(ClientWebSocket socket)
        {
            byte[] buffer = new byte[65536];

            while (running && socket != null && socket.State == WebSocketState.Open)
            {
                StringBuilder messageBuilder = new StringBuilder(8192);
                WebSocketReceiveResult result;

                do
                {
                    result = await socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Logger.Log("speedify", "server closed websocket");
                        return;
                    }

                    messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
                while (!result.EndOfMessage);

                string rawMessage = messageBuilder.ToString().Trim();
                if (string.IsNullOrWhiteSpace(rawMessage))
                    continue;

                try
                {
                    string eventName;
                    string payloadJson;

                    if (!TryParseSpeedifyMessage(rawMessage, out eventName, out payloadJson))
                        continue;

                    if (!seenEventNames.Contains(eventName))
                    {
                        seenEventNames.Add(eventName);
                        Logger.Log("speedify-new-event", eventName);
                    }

                    if (!capturedSnapshotEvents.Contains(eventName))
                    {
                        capturedSnapshotEvents.Add(eventName);
                        Logger.Log("speedify-snapshot-event", eventName);

                        if (ShouldCaptureFullSnapshot(eventName))
                        {
                            Logger.Log("speedify-snapshot-raw", rawMessage);
                            Logger.Log("speedify-snapshot-payload", payloadJson);
                        }
                        else
                        {
                            Logger.Log("speedify-snapshot-skip", eventName + " (name-only)");
                        }
                    }

                    if (eventName == "report_connection_stats")
                    {
                        MetricsSnapshot snap = parser.ParseConnectionStats(payloadJson);
                        if (snap != null && onSnapshot != null)
                            onSnapshot(snap);
                    }
                }
                catch (Exception ex)
                {
                    if (running)
                        Logger.Log("speedify", "parse error: " + ex.Message);
                }
            }
        }

        private bool ShouldCaptureFullSnapshot(string eventName)
        {
            switch (eventName)
            {
                case "report_current_state":
                case "report_connected_server":
                case "report_connection_settings":
                case "report_privacy_settings":
                case "report_networks":
                case "report_streaming_stats":
                case "report_connection_stats":
                case "report_session_stats":
                case "report_latency_stats":
                case "report_adapter_stats":
                case "report_tunnel_stats":
                case "report_stats":
                case "report_system_data":
                case "report_version_data":
                case "report_user_location":
                case "report_networksharing_discovery":
                case "report_nag":
                    return true;

                default:
                    return false;
            }
        }

        private async Task Send(ClientWebSocket socket, string msg)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(msg + "\n");

            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);

            Logger.Log("speedify", "sent: " + msg);
        }

        private bool TryParseSpeedifyMessage(string raw, out string eventName, out string payloadJson)
        {
            eventName = null;
            payloadJson = null;

            try
            {
                using (JsonDocument doc = JsonDocument.Parse(raw))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Array)
                        return false;

                    JsonElement arr = doc.RootElement;

                    if (arr.GetArrayLength() < 2)
                        return false;

                    if (arr[0].ValueKind != JsonValueKind.String)
                        return false;

                    eventName = arr[0].GetString();
                    payloadJson = arr[1].GetRawText();

                    return !string.IsNullOrWhiteSpace(eventName);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}