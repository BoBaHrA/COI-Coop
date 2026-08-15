using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CoiCoop.Networking;

/// <summary>
/// Auxiliary sidecar transport.
///
/// Presentation previews use a latest-wins lane. Small state adapters that must
/// not be overwritten by a newer mouse preview (currently sandbox source state)
/// use a separate reliable FIFO lane on the same TCP connection. Neither lane
/// participates in authoritative simulation ordering or can halt the main replay.
/// </summary>
internal sealed class PlacementPreviewSession : IDisposable {
    private const string HelloLine = "COI_COOP_PREVIEW|1";
    private const string WelcomeLine = "COI_COOP_PREVIEW_OK|1";
    private const int LoopSleepMs = 10;

    private readonly bool m_isHost;
    private readonly int m_port;
    private readonly Action<string> m_log;
    private readonly object m_outgoingLock = new object();
    private readonly object m_incomingLock = new object();
    private readonly ConcurrentQueue<PlacementPreviewState> m_reliableOutgoing
        = new ConcurrentQueue<PlacementPreviewState>();
    private readonly ConcurrentQueue<PlacementPreviewState> m_reliableIncoming
        = new ConcurrentQueue<PlacementPreviewState>();

    private Thread m_thread;
    private volatile bool m_stop;
    private volatile bool m_connected;
    private TcpListener m_listener;
    private TcpClient m_client;

    private long m_nextRevision;
    private PlacementPreviewState m_latestOutgoing;
    private long m_lastSentRevision = -1;
    private PlacementPreviewState m_latestIncoming;
    private long m_lastConsumedIncomingRevision = -1;

    public PlacementPreviewSession(bool isHost, int port, Action<string> log = null) {
        if (port < 1024 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        m_isHost = isHost;
        m_port = port;
        m_log = log;
    }

    public bool IsConnected => m_connected;

    public void Start() {
        if (m_thread != null) return;
        m_thread = new Thread(Run) {
            IsBackground = true,
            Name = m_isHost ? "COI-Coop preview host" : "COI-Coop preview client"
        };
        m_thread.Start();
    }

    public long Publish(string kind, byte[] payload) {
        var revision = Interlocked.Increment(ref m_nextRevision) - 1;
        var state = new PlacementPreviewState(revision, kind, payload);
        lock (m_outgoingLock) {
            m_latestOutgoing = state;
        }
        return revision;
    }

    public long PublishReliable(string kind, byte[] payload) {
        var revision = Interlocked.Increment(ref m_nextRevision) - 1;
        m_reliableOutgoing.Enqueue(new PlacementPreviewState(revision, kind, payload));
        return revision;
    }

    public long Clear() => Publish("NONE", Array.Empty<byte>());

    public bool TryTakeLatestPeerState(out PlacementPreviewState state) {
        lock (m_incomingLock) {
            state = m_latestIncoming;
            if (state == null || state.Revision <= m_lastConsumedIncomingRevision) {
                state = null;
                return false;
            }
            m_lastConsumedIncomingRevision = state.Revision;
            return true;
        }
    }

    public bool TryDequeueReliablePeerState(out PlacementPreviewState state) {
        return m_reliableIncoming.TryDequeue(out state);
    }

    private void Run() {
        try {
            if (m_isHost) RunHost();
            else RunClient();
        }
        catch (Exception ex) {
            if (!m_stop) m_log?.Invoke("preview loop failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally {
            SetDisconnected();
        }
    }

    private void RunHost() {
        m_listener = new TcpListener(IPAddress.Loopback, m_port);
        m_listener.Start();
        m_log?.Invoke("PREVIEW HOST listening on 127.0.0.1:" + m_port);

        while (!m_stop) {
            if (!m_listener.Pending()) {
                Thread.Sleep(LoopSleepMs);
                continue;
            }

            var client = m_listener.AcceptTcpClient();
            m_client = client;
            try {
                Configure(client);
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true }) {
                    if (!string.Equals(reader.ReadLine(), HelloLine, StringComparison.Ordinal)) {
                        writer.WriteLine("REJECT");
                        continue;
                    }
                    writer.WriteLine(WelcomeLine);
                    SetConnected();
                    RunConnected(stream, reader, writer);
                }
            }
            catch (IOException ex) {
                if (!m_stop) m_log?.Invoke("PREVIEW HOST connection closed: " + ex.Message);
            }
            catch (SocketException ex) {
                if (!m_stop) m_log?.Invoke("PREVIEW HOST socket closed: " + ex.Message);
            }
            finally {
                SetDisconnected();
                try { client.Close(); } catch { }
                m_client = null;
            }
        }
    }

    private void RunClient() {
        while (!m_stop) {
            var client = new TcpClient();
            m_client = client;
            try {
                Configure(client);
                client.Connect(IPAddress.Loopback, m_port);
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true }) {
                    writer.WriteLine(HelloLine);
                    if (!string.Equals(reader.ReadLine(), WelcomeLine, StringComparison.Ordinal)) {
                        throw new IOException("Preview host rejected handshake.");
                    }
                    SetConnected();
                    RunConnected(stream, reader, writer);
                }
            }
            catch (IOException ex) {
                if (!m_stop) m_log?.Invoke("PREVIEW CLIENT unavailable: " + ex.Message);
            }
            catch (SocketException ex) {
                if (!m_stop) m_log?.Invoke("PREVIEW CLIENT unavailable: " + ex.Message);
            }
            finally {
                SetDisconnected();
                try { client.Close(); } catch { }
                m_client = null;
            }

            if (!m_stop) Thread.Sleep(250);
        }
    }

    private void RunConnected(NetworkStream stream, StreamReader reader, StreamWriter writer) {
        while (!m_stop) {
            PlacementPreviewState reliable;
            while (m_reliableOutgoing.TryDequeue(out reliable)) {
                writer.WriteLine(Serialize(reliable));
                if (reliable.Revision > m_lastSentRevision) {
                    m_lastSentRevision = reliable.Revision;
                }
            }

            PlacementPreviewState outgoing = null;
            lock (m_outgoingLock) {
                if (m_latestOutgoing != null && m_latestOutgoing.Revision > m_lastSentRevision) {
                    outgoing = m_latestOutgoing;
                }
            }

            if (outgoing != null) {
                writer.WriteLine(Serialize(outgoing));
                m_lastSentRevision = outgoing.Revision;
            }

            while (stream.DataAvailable) {
                var line = reader.ReadLine();
                if (line == null) throw new IOException("Preview peer closed connection.");
                PlacementPreviewState incoming;
                if (!TryParse(line, out incoming)) continue;

                if (IsReliableKind(incoming.Kind)) {
                    m_reliableIncoming.Enqueue(incoming);
                    continue;
                }

                lock (m_incomingLock) {
                    if (m_latestIncoming == null || incoming.Revision > m_latestIncoming.Revision) {
                        m_latestIncoming = incoming;
                    }
                }
            }

            Thread.Sleep(LoopSleepMs);
        }
    }

    private static bool IsReliableKind(string kind) {
        return kind != null && kind.StartsWith("SANDBOX_", StringComparison.Ordinal);
    }

    private static string Serialize(PlacementPreviewState state) {
        return "PREVIEW|" + state.Revision + "|" + state.Kind + "|" + Convert.ToBase64String(state.Payload);
    }

    private static bool TryParse(string line, out PlacementPreviewState state) {
        state = null;
        var parts = (line ?? string.Empty).Split('|');
        if (parts.Length != 4
            || !string.Equals(parts[0], "PREVIEW", StringComparison.Ordinal)
            || !long.TryParse(parts[1], out var revision)
            || revision < 0
            || string.IsNullOrWhiteSpace(parts[2])) {
            return false;
        }

        try {
            state = new PlacementPreviewState(
                revision,
                parts[2],
                Convert.FromBase64String(parts[3] ?? string.Empty));
            return true;
        }
        catch (FormatException) {
            return false;
        }
    }

    private void SetConnected() {
        m_connected = true;
        m_lastSentRevision = -1;
        lock (m_incomingLock) {
            m_latestIncoming = null;
            m_lastConsumedIncomingRevision = -1;
        }
        while (m_reliableIncoming.TryDequeue(out _)) { }
        m_log?.Invoke(m_isHost ? "PREVIEW HOST connected" : "PREVIEW CLIENT connected");
    }

    private void SetDisconnected() {
        if (m_connected) m_log?.Invoke(m_isHost ? "PREVIEW HOST disconnected" : "PREVIEW CLIENT disconnected");
        m_connected = false;
        while (m_reliableOutgoing.TryDequeue(out _)) { }
        while (m_reliableIncoming.TryDequeue(out _)) { }
    }

    private static void Configure(TcpClient client) {
        client.NoDelay = true;
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
    }

    public void Dispose() {
        m_stop = true;
        m_connected = false;
        try { m_client?.Close(); } catch { }
        try { m_listener?.Stop(); } catch { }
        if (m_thread != null && m_thread.IsAlive) m_thread.Join(2000);
        m_thread = null;
        m_client = null;
        m_listener = null;
    }
}
