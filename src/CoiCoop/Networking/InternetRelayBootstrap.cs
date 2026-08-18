using System;
using System.IO;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using Mafi;

namespace CoiCoop.Networking;

/// <summary>
/// Development internet bridge.
///
/// The already-proven gameplay/preview sessions remain unchanged on localhost.
/// This bridge carries their raw TCP byte streams through four authenticated
/// outbound WebSocket connections to the relay service. Both peers therefore
/// work behind NAT/CGNAT without inbound port forwarding.
///
/// Host:   relay WSS -> bridge TCP client -> 127.0.0.1:27015+n
/// Client: 127.0.0.1:27015+n -> bridge listener -> relay WSS
/// </summary>
internal sealed class InternetRelayBootstrap : IDisposable {
    private const int LaneCount = 4;
    private static readonly string[] LaneNames = { "gameplay", "preview", "path", "blueprint" };
    private static readonly object s_lock = new object();
    private static InternetRelayBootstrap s_current;

    private readonly bool m_isHost;
    private readonly int m_internalBasePort;
    private readonly Uri m_relayUri;
    private readonly string m_sessionCode;
    private readonly string m_sessionToken;
    private readonly RelayLaneBridge[] m_lanes = new RelayLaneBridge[LaneCount];

    private InternetRelayBootstrap(
        bool isHost,
        int internalBasePort,
        Uri relayUri,
        string sessionCode,
        string sessionToken) {

        m_isHost = isHost;
        m_internalBasePort = internalBasePort;
        m_relayUri = relayUri;
        m_sessionCode = sessionCode;
        m_sessionToken = sessionToken;
    }

    public static void EnsureStarted() {
        if (!ReadEnvironmentBool("COI_COOP_RELAY", false)) return;

        var mode = ReadEnvironmentInt("COI_COOP_MODE", 0);
        if (mode != 1 && mode != 2) return;

        var internalBasePort = ReadEnvironmentInt("COI_COOP_PORT", 27015);
        if (internalBasePort < 1024 || internalBasePort > 65532) {
            Log.Info("COI-Coop: RELAY disabled - invalid internal base port " + internalBasePort);
            return;
        }

        var relayText = Environment.GetEnvironmentVariable("COI_COOP_RELAY_WS");
        var sessionCode = NormalizeSessionCode(Environment.GetEnvironmentVariable("COI_COOP_SESSION_CODE"));
        var sessionToken = Environment.GetEnvironmentVariable("COI_COOP_SESSION_TOKEN");

        Uri relayUri;
        if (string.IsNullOrWhiteSpace(relayText)
            || !Uri.TryCreate(relayText, UriKind.Absolute, out relayUri)
            || (relayUri.Scheme != "ws" && relayUri.Scheme != "wss")) {

            Log.Info("COI-Coop: RELAY disabled - COI_COOP_RELAY_WS must be a ws:// or wss:// URL");
            return;
        }

        if (string.IsNullOrWhiteSpace(sessionCode) || string.IsNullOrWhiteSpace(sessionToken)) {
            Log.Info("COI-Coop: RELAY disabled - session code/token are missing");
            return;
        }

        lock (s_lock) {
            if (s_current != null) return;

            var next = new InternetRelayBootstrap(
                mode == 1,
                internalBasePort,
                relayUri,
                sessionCode,
                sessionToken);
            s_current = next;
            next.Start();
        }
    }

    private void Start() {
        for (var i = 0; i < LaneCount; i++) {
            var lane = new RelayLaneBridge(
                m_isHost,
                i,
                LaneNames[i],
                m_internalBasePort + i,
                m_relayUri,
                m_sessionCode,
                m_sessionToken,
                message => Log.Info("COI-Coop: RELAY " + message));
            m_lanes[i] = lane;
            lane.Start();
        }

        Log.Info(
            "COI-Coop: INTERNET RELAY enabled role=" + (m_isHost ? "HOST" : "CLIENT")
            + " session=" + m_sessionCode
            + " lanes=" + m_internalBasePort + "-" + (m_internalBasePort + LaneCount - 1)
            + " endpoint=" + SafeRelayDescription(m_relayUri));
    }

    public void Dispose() {
        for (var i = 0; i < m_lanes.Length; i++) {
            try { m_lanes[i]?.Dispose(); } catch { }
            m_lanes[i] = null;
        }

        lock (s_lock) {
            if (ReferenceEquals(s_current, this)) s_current = null;
        }
    }

    private static string SafeRelayDescription(Uri uri) {
        if (uri == null) return "?";
        return uri.Scheme + "://" + uri.Authority + uri.AbsolutePath;
    }

    private static string NormalizeSessionCode(string value) {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var raw = new StringBuilder();
        for (var i = 0; i < value.Length; i++) {
            var ch = char.ToUpperInvariant(value[i]);
            if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')) raw.Append(ch);
        }
        if (raw.Length != 8) return null;
        return raw.ToString(0, 4) + "-" + raw.ToString(4, 4);
    }

    private static int ReadEnvironmentInt(string name, int fallback) {
        var value = Environment.GetEnvironmentVariable(name);
        int parsed;
        return int.TryParse(value, out parsed) ? parsed : fallback;
    }

    private static bool ReadEnvironmentBool(string name, bool fallback) {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (string.Equals(value, "1", StringComparison.Ordinal)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(value, "0", StringComparison.Ordinal)
            || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)) return false;
        return fallback;
    }

    private sealed class RelayLaneBridge : IDisposable {
        private const int ReconnectDelayMs = 500;
        private const int LocalConnectTimeoutMs = 1000;
        private const int IoBufferBytes = 32 * 1024;

        private readonly bool m_isHost;
        private readonly int m_laneIndex;
        private readonly string m_laneName;
        private readonly int m_localPort;
        private readonly Uri m_relayBaseUri;
        private readonly string m_sessionCode;
        private readonly string m_sessionToken;
        private readonly Action<string> m_log;

        private Thread m_thread;
        private volatile bool m_stop;
        private TcpListener m_listener;
        private TcpClient m_localClient;
        private ClientWebSocket m_webSocket;

        public RelayLaneBridge(
            bool isHost,
            int laneIndex,
            string laneName,
            int localPort,
            Uri relayBaseUri,
            string sessionCode,
            string sessionToken,
            Action<string> log) {

            m_isHost = isHost;
            m_laneIndex = laneIndex;
            m_laneName = laneName;
            m_localPort = localPort;
            m_relayBaseUri = relayBaseUri;
            m_sessionCode = sessionCode;
            m_sessionToken = sessionToken;
            m_log = log;
        }

        public void Start() {
            if (m_thread != null) return;
            m_thread = new Thread(Run) {
                IsBackground = true,
                Name = "COI-Coop relay " + m_laneName
            };
            m_thread.Start();
        }

        private void Run() {
            if (m_isHost) RunHost();
            else RunClient();
        }

        private void RunHost() {
            while (!m_stop) {
                ClientWebSocket ws = null;
                TcpClient local = null;
                try {
                    ws = ConnectRelay();
                    if (ws == null) {
                        SleepReconnect();
                        continue;
                    }
                    m_webSocket = ws;

                    local = ConnectLocalHostSession(ws);
                    if (local == null) {
                        SleepReconnect();
                        continue;
                    }
                    m_localClient = local;
                    ConfigureTcp(local);

                    m_log?.Invoke(m_laneName + " relay connected to local host session");
                    PumpBidirectional(local, ws);
                }
                catch (Exception ex) {
                    if (!m_stop) m_log?.Invoke(m_laneName + " bridge failed: " + ex.GetType().Name + ": " + ex.Message);
                }
                finally {
                    ClosePair(local, ws);
                    m_localClient = null;
                    m_webSocket = null;
                }

                SleepReconnect();
            }
        }

        private void RunClient() {
            try {
                m_listener = new TcpListener(System.Net.IPAddress.Loopback, m_localPort);
                m_listener.Start();
                m_log?.Invoke(m_laneName + " local listener 127.0.0.1:" + m_localPort);

                while (!m_stop) {
                    TcpClient local = null;
                    ClientWebSocket ws = null;
                    try {
                        local = m_listener.AcceptTcpClient();
                        m_localClient = local;
                        ConfigureTcp(local);

                        ws = ConnectRelay();
                        if (ws == null) continue;
                        m_webSocket = ws;

                        m_log?.Invoke(m_laneName + " relay connected from local client session");
                        PumpBidirectional(local, ws);
                    }
                    catch (SocketException ex) {
                        if (!m_stop) m_log?.Invoke(m_laneName + " local listener socket closed: " + ex.Message);
                    }
                    catch (Exception ex) {
                        if (!m_stop) m_log?.Invoke(m_laneName + " bridge failed: " + ex.GetType().Name + ": " + ex.Message);
                    }
                    finally {
                        ClosePair(local, ws);
                        m_localClient = null;
                        m_webSocket = null;
                    }
                }
            }
            catch (Exception ex) {
                if (!m_stop) m_log?.Invoke(m_laneName + " listener failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally {
                try { m_listener?.Stop(); } catch { }
                m_listener = null;
            }
        }

        private ClientWebSocket ConnectRelay() {
            var ws = new ClientWebSocket();
            try {
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                ws.Options.SetRequestHeader("Authorization", "Bearer " + m_sessionToken);
                var uri = BuildLaneUri();
                ws.ConnectAsync(uri, CancellationToken.None).GetAwaiter().GetResult();
                m_log?.Invoke(
                    m_laneName + " WSS connected session=" + m_sessionCode
                    + " role=" + (m_isHost ? "host" : "client")
                    + " lane=" + m_laneIndex);
                return ws;
            }
            catch (Exception ex) {
                if (!m_stop) m_log?.Invoke(m_laneName + " WSS unavailable: " + ex.GetType().Name + ": " + ex.Message);
                try { ws.Dispose(); } catch { }
                return null;
            }
        }

        private Uri BuildLaneUri() {
            var builder = new UriBuilder(m_relayBaseUri);
            var prefix = string.IsNullOrEmpty(builder.Query) ? string.Empty : builder.Query.TrimStart('?') + "&";
            builder.Query = prefix
                + "code=" + Uri.EscapeDataString(m_sessionCode)
                + "&role=" + (m_isHost ? "host" : "client")
                + "&lane=" + m_laneIndex;
            return builder.Uri;
        }

        private TcpClient ConnectLocalHostSession(ClientWebSocket ws) {
            while (!m_stop && ws.State == WebSocketState.Open) {
                var client = new TcpClient();
                try {
                    var async = client.BeginConnect("127.0.0.1", m_localPort, null, null);
                    try {
                        if (!async.AsyncWaitHandle.WaitOne(LocalConnectTimeoutMs)) {
                            client.Close();
                        }
                        else {
                            client.EndConnect(async);
                            return client;
                        }
                    }
                    finally {
                        try { async.AsyncWaitHandle.Close(); } catch { }
                    }
                }
                catch {
                    try { client.Close(); } catch { }
                }

                Thread.Sleep(100);
            }
            return null;
        }

        private void PumpBidirectional(TcpClient local, ClientWebSocket ws) {
            using (var finished = new ManualResetEvent(false)) {
                var tcpToWs = new Thread(() => PumpTcpToWebSocket(local, ws, finished)) {
                    IsBackground = true,
                    Name = "COI-Coop relay " + m_laneName + " TCP>WS"
                };
                var wsToTcp = new Thread(() => PumpWebSocketToTcp(ws, local, finished)) {
                    IsBackground = true,
                    Name = "COI-Coop relay " + m_laneName + " WS>TCP"
                };

                tcpToWs.Start();
                wsToTcp.Start();
                finished.WaitOne();

                ClosePair(local, ws);
                tcpToWs.Join(1000);
                wsToTcp.Join(1000);
                m_log?.Invoke(m_laneName + " relay disconnected");
            }
        }

        private static void PumpTcpToWebSocket(
            TcpClient local,
            ClientWebSocket ws,
            EventWaitHandle finished) {

            var buffer = new byte[IoBufferBytes];
            try {
                var input = local.GetStream();
                while (ws.State == WebSocketState.Open) {
                    var read = input.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    ws.SendAsync(
                            new ArraySegment<byte>(buffer, 0, read),
                            WebSocketMessageType.Binary,
                            true,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
            }
            catch {
            }
            finally {
                try { finished.Set(); } catch { }
            }
        }

        private static void PumpWebSocketToTcp(
            ClientWebSocket ws,
            TcpClient local,
            EventWaitHandle finished) {

            var buffer = new byte[IoBufferBytes];
            try {
                var output = local.GetStream();
                while (ws.State == WebSocketState.Open) {
                    var result = ws.ReceiveAsync(
                            new ArraySegment<byte>(buffer),
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();

                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0) {
                        output.Write(buffer, 0, result.Count);
                        output.Flush();
                    }

                    // Text frames are relay control messages (currently PEER_READY)
                    // and are intentionally consumed rather than passed to the TCP lane.
                }
            }
            catch {
            }
            finally {
                try { finished.Set(); } catch { }
            }
        }

        private void SleepReconnect() {
            if (!m_stop) Thread.Sleep(ReconnectDelayMs);
        }

        private static void ConfigureTcp(TcpClient client) {
            client.NoDelay = true;
            client.ReceiveTimeout = 0;
            client.SendTimeout = 5000;
        }

        private static void ClosePair(TcpClient local, ClientWebSocket ws) {
            try { local?.Close(); } catch { }
            if (ws != null) {
                try { ws.Abort(); } catch { }
                try { ws.Dispose(); } catch { }
            }
        }

        public void Dispose() {
            m_stop = true;
            try { m_localClient?.Close(); } catch { }
            try { m_webSocket?.Abort(); } catch { }
            try { m_webSocket?.Dispose(); } catch { }
            try { m_listener?.Stop(); } catch { }

            if (m_thread != null && m_thread.IsAlive) m_thread.Join(2000);
            m_thread = null;
            m_listener = null;
            m_localClient = null;
            m_webSocket = null;
        }
    }
}
