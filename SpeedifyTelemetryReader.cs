using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WTFnetApp
{
    public class SpeedifyTelemetryReader
    {
        private ClientWebSocket ws;
        private bool running;
        private readonly Action<MetricsSnapshot> onSnapshot;
        private readonly SpeedifyMetricsParser parser = new SpeedifyMetricsParser();

        public SpeedifyTelemetryReader(Action<MetricsSnapshot> handler)
        {
            onSnapshot = handler;
        }

        public void Start()
        {
            if (running) return;

            running = true;
            Logger.Log("speedify", "websocket telemetry starting");
            Task.Run(StreamLoop);
        }

        public async void Stop()
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

        private async Task StreamLoop()
        {
            while (running)
            {
                try
                {
                    ws = new ClientWebSocket();
                    ws.Options.AddSubProtocol("event-protocol");
                    ws.Options.SetRequestHeader("Origin", "easywsclient://");

                    Logger.Log("speedify", "connecting websocket...");
                    await ws.ConnectAsync(
                        new Uri("ws://127.0.0.1:9330/systemdata"),
                        CancellationToken.None);

                    Logger.Log("speedify", "websocket connected");

                    await Send(ws, @"[""request_current_state"",null,""speedify_sdk""]");
                    await Send(ws, @"[""request_networks"",null,""speedify_sdk""]");
                    await Send(ws, @"[""request_streaming_stats"",null,""speedify_sdk""]");
                    await Send(ws, @"[""request_stats"",null,""speedify_sdk""]");
                    await Send(ws, @"[""request_connection_stats"",null,""speedify_sdk""]");
                    await Send(ws, @"[""request_session_stats"",null,""speedify_sdk""]");
                    await Send(ws, @"[""request_adapter_stats"",null,""speedify_sdk""]");
                    await Send(ws, @"[""request_tunnel_stats"",null,""speedify_sdk""]");
                    await Send(ws, @"[""request_latency_stats"",null,""speedify_sdk""]");

                    await ReceiveLoop(ws);

                    Logger.Log("speedify", "websocket stream ended — restarting");
                }
                catch (Exception ex)
                {
                    Logger.Log("speedify", "websocket error: " + ex.Message);
                }

                try
                {
                    ws?.Dispose();
                    ws = null;
                }
                catch
                {
                }

                if (running)
                    await Task.Delay(2000);
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
                    if (!TryUnwrapSpeedifyMessage(rawMessage, out string eventName, out string payloadJson))
                        continue;

                    Logger.Log("speedify-ws-event", eventName);

                    if (eventName == "report_connection_stats")
                    {
                        MetricsSnapshot snap = parser.ParseConnectionStats(payloadJson);
                        if (snap != null)
                            onSnapshot?.Invoke(snap);
                    }
                    else if (eventName == "report_session_stats")
                    {
                        Logger.Log("speedify-session", payloadJson);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("speedify", "parse error: " + ex.Message);
                }
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

        private bool TryUnwrapSpeedifyMessage(string raw, out string eventName, out string payloadJson)
        {
            eventName = null;
            payloadJson = null;

            try
            {
                if (string.IsNullOrWhiteSpace(raw)) return false;
                if (!raw.StartsWith("[\"")) return false;

                int firstQuote = 2;
                int secondQuote = raw.IndexOf('"', firstQuote);
                if (secondQuote < 0) return false;

                eventName = raw.Substring(firstQuote, secondQuote - firstQuote);

                int firstComma = raw.IndexOf(',', secondQuote + 1);
                if (firstComma < 0) return false;

                int payloadStart = firstComma + 1;
                while (payloadStart < raw.Length && char.IsWhiteSpace(raw[payloadStart]))
                    payloadStart++;

                if (payloadStart >= raw.Length) return false;

                char openChar = raw[payloadStart];
                char closeChar;

                if (openChar == '{') closeChar = '}';
                else if (openChar == '[') closeChar = ']';
                else return false;

                int depth = 0;
                bool inString = false;
                bool escape = false;

                for (int i = payloadStart; i < raw.Length; i++)
                {
                    char c = raw[i];

                    if (escape)
                    {
                        escape = false;
                        continue;
                    }

                    if (c == '\\' && inString)
                    {
                        escape = true;
                        continue;
                    }

                    if (c == '"')
                    {
                        inString = !inString;
                        continue;
                    }

                    if (inString) continue;

                    if (c == openChar) depth++;
                    else if (c == closeChar)
                    {
                        depth--;
                        if (depth == 0)
                        {
                            payloadJson = raw.Substring(payloadStart, i - payloadStart + 1);
                            return !string.IsNullOrWhiteSpace(eventName);
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}