using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CoiCoop.Networking;

/// <summary>
/// Persistent two-peer transport for the co-op prototype.
///
/// The host is authoritative for both command ordering and authority-frame
/// assignment. Commands accepted while host frame N is open are scheduled for
/// N+1. A FRAME marker seals the previous frame on the wire, so the client knows
/// that all COMMITs for that frame have already arrived before it executes them.
/// </summary>
internal sealed class PersistentCommandSession : IDisposable {
    private const int LoopSleepMs = 5;

    private readonly bool m_isHost;
    private readonly int m_port;
    private readonly Action<string> m_log;
    private readonly ConcurrentQueue<string> m_outgoing = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<ReceivedAuthorityCommand> m_incoming = new ConcurrentQueue<ReceivedAuthorityCommand>();
    private readonly ConcurrentQueue<StateProbeSnapshot> m_incomingStateProbes = new ConcurrentQueue<StateProbeSnapshot>();
    private readonly AuthorityCommandSequencer m_sequencer = new AuthorityCommandSequencer();
    private readonly object m_authorityLock = new object();

    private Thread m_thread;
    private volatile bool m_stop;
    private volatile bool m_connected;
    private volatile bool m_localGameplayReady;
    private volatile bool m_peerGameplayReady;
    private long m_nextLocalCommandId;
    private long m_currentHostAuthorityFrame = -1;
    private long m_latestAnnouncedAuthorityFrame = -1;
    private long m_peerProgressFrame = -1;
    private long m_peerProgressSequence = -1;
    private TcpListener m_listener;
    private TcpClient m_client;

    public PersistentCommandSession(bool isHost, int port, Action<string> log = null) {
        m_isHost = isHost;
        m_port = port;
        m_log = log;
    }

    public bool IsHost => m_isHost;
    public bool IsConnected => m_connected;
    public bool IsGameplayReady => m_localGameplayReady;
    public bool PeerGameplayReady => m_peerGameplayReady;
    public string LocalClientId => m_isHost ? "host" : "client";
    public long LatestAnnouncedAuthorityFrame => Interlocked.Read(ref m_latestAnnouncedAuthorityFrame);

    public void Start() {
        if (m_thread != null) {
            return;
        }

        m_thread = new Thread(Run) {
            IsBackground = true,
            Name = m_isHost ? "COI-Coop persistent host" : "COI-Coop persistent client"
        };
        m_thread.Start();
    }

    /// <summary>
    /// Called from the first outer simulation batch after the save is actually
    /// ready. The client sends READY so the host cannot start frame 0 while the
    /// second process is still loading the world.
    /// </summary>
    public void MarkGameplayReady() {
        if (!m_connected || m_localGameplayReady) {
            return;
        }

        m_localGameplayReady = true;
        if (!m_isHost) {
            m_outgoing.Enqueue(NetworkProtocol.Ready());
            m_log?.Invoke("CLIENT gameplay READY queued");
        }
    }

    /// <summary>
    /// Host-only. Seals the next authority frame. All commands assigned to this
    /// frame were enqueued before the FRAME marker because assignment and sealing
    /// use the same lock.
    /// </summary>
    public long AdvanceHostAuthorityFrame() {
        if (!m_isHost) {
            throw new InvalidOperationException("Only the host can advance the authority frame.");
        }

        if (!m_connected || !m_localGameplayReady || !m_peerGameplayReady) {
            return -1;
        }

        lock (m_authorityLock) {
            var frame = m_currentHostAuthorityFrame + 1;
            m_currentHostAuthorityFrame = frame;
            m_outgoing.Enqueue(NetworkProtocol.Frame(frame));
            Interlocked.Exchange(ref m_latestAnnouncedAuthorityFrame, frame);
            return frame;
        }
    }

    /// <summary>
    /// Client-side barrier. FRAME is received only after all COMMITs for that
    /// authority frame were read from the same ordered TCP stream.
    /// </summary>
    public bool WaitForAuthorityFrame(long authorityFrame, int timeoutMs) {
        if (m_isHost) {
            return Interlocked.Read(ref m_currentHostAuthorityFrame) >= authorityFrame;
        }

        var started = Environment.TickCount;
        while (!m_stop && m_connected) {
            if (Interlocked.Read(ref m_latestAnnouncedAuthorityFrame) >= authorityFrame) {
                return true;
            }

            if (unchecked(Environment.TickCount - started) >= timeoutMs) {
                return false;
            }

            Thread.Sleep(1);
        }

        return false;
    }

    public void ReportProgress(long authorityFrame, long appliedThroughSequence) {
        if (!m_connected || !m_localGameplayReady) {
            return;
        }

        m_outgoing.Enqueue(NetworkProtocol.Progress(authorityFrame, appliedThroughSequence));
    }

    public bool TryGetPeerProgress(out long authorityFrame, out long appliedThroughSequence) {
        authorityFrame = Interlocked.Read(ref m_peerProgressFrame);
        appliedThroughSequence = Interlocked.Read(ref m_peerProgressSequence);
        return authorityFrame >= 0;
    }

    public void ReportStateProbe(StateProbeSnapshot probe) {
        if (probe == null) throw new ArgumentNullException(nameof(probe));
        if (!m_connected || !m_localGameplayReady) {
            return;
        }

        m_outgoing.Enqueue(NetworkProtocol.StateProbe(probe));
    }

    public bool TryDequeueStateProbe(out StateProbeSnapshot probe) {
        return m_incomingStateProbes.TryDequeue(out probe);
    }

    public bool SubmitLocalCommand(byte[] payload) {
        long ignoredClientCommandId;
        return SubmitLocalCommand(payload, out ignoredClientCommandId);
    }

    public bool SubmitLocalCommand(byte[] payload, out long clientCommandId) {
        if (payload == null) throw new ArgumentNullException(nameof(payload));

        clientCommandId = -1;
        if (!m_connected || !m_localGameplayReady) {
            return false;
        }

        clientCommandId = Interlocked.Increment(ref m_nextLocalCommandId) - 1;

        if (!m_isHost) {
            m_outgoing.Enqueue(NetworkProtocol.Submit(clientCommandId, payload));
            m_log?.Invoke("CLIENT queued SUBMIT id=" + clientCommandId + " bytes=" + payload.Length);
            return true;
        }

        AuthorityCommandEnvelope envelope;
        long authorityFrame;
        lock (m_authorityLock) {
            authorityFrame = m_currentHostAuthorityFrame + 1;
            if (!m_sequencer.TryAccept(
                    "host",
                    clientCommandId,
                    null,
                    CommandConflictMode.Ordered,
                    payload,
                    out envelope)) {
                return false;
            }

            m_incoming.Enqueue(new ReceivedAuthorityCommand(
                envelope.AuthoritySequence,
                authorityFrame,
                envelope.ClientId,
                envelope.ClientCommandId,
                envelope.Payload));

            m_outgoing.Enqueue(NetworkProtocol.Commit(
                envelope.AuthoritySequence,
                authorityFrame,
                envelope.ClientId,
                envelope.ClientCommandId,
                envelope.Payload));
        }

        m_log?.Invoke(
            "HOST queued COMMIT seq=" + envelope.AuthoritySequence
            + " frame=" + authorityFrame
            + " origin=host id=" + clientCommandId
            + " bytes=" + payload.Length);
        return true;
    }

    public bool TryDequeueReceived(out ReceivedAuthorityCommand command) {
        return m_incoming.TryDequeue(out command);
    }

    private void Run() {
        try {
            if (m_isHost) {
                RunHost();
            }
            else {
                RunClient();
            }
        }
        catch (Exception ex) {
            if (!m_stop) {
                m_log?.Invoke("network loop failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
        finally {
            SetDisconnected();
        }
    }

    private void RunHost() {
        m_listener = new TcpListener(IPAddress.Loopback, m_port);
        m_listener.Start();
        m_log?.Invoke("HOST listening on 127.0.0.1:" + m_port);

        while (!m_stop) {
            if (!m_listener.Pending()) {
                Thread.Sleep(LoopSleepMs);
                continue;
            }

            var client = m_listener.AcceptTcpClient();
            m_client = client;
            try {
                ConfigureClient(client);
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true }) {

                    var hello = reader.ReadLine();
                    if (!NetworkProtocol.IsCompatibleHello(hello)) {
                        writer.WriteLine("REJECT|INCOMPATIBLE_PROTOCOL");
                        continue;
                    }

                    writer.WriteLine(NetworkProtocol.Welcome());
                    SetConnected();
                    RunConnected(stream, reader, writer);
                }
            }
            catch (IOException ex) {
                if (!m_stop) m_log?.Invoke("HOST connection closed: " + ex.Message);
            }
            catch (SocketException ex) {
                if (!m_stop) m_log?.Invoke("HOST socket closed: " + ex.Message);
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
                ConfigureClient(client);
                client.Connect(IPAddress.Loopback, m_port);

                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true }) {

                    writer.WriteLine(NetworkProtocol.Hello());
                    if (!NetworkProtocol.IsCompatibleWelcome(reader.ReadLine())) {
                        throw new IOException("Host rejected protocol handshake.");
                    }

                    SetConnected();
                    RunConnected(stream, reader, writer);
                }
            }
            catch (IOException ex) {
                if (!m_stop) m_log?.Invoke("CLIENT connection unavailable: " + ex.Message);
            }
            catch (SocketException ex) {
                if (!m_stop) m_log?.Invoke("CLIENT connection unavailable: " + ex.Message);
            }
            finally {
                SetDisconnected();
                try { client.Close(); } catch { }
                m_client = null;
            }

            if (!m_stop) {
                Thread.Sleep(250);
            }
        }
    }

    private void RunConnected(
        NetworkStream stream,
        StreamReader reader,
        StreamWriter writer) {

        while (!m_stop) {
            while (m_outgoing.TryDequeue(out var line)) {
                writer.WriteLine(line);
            }

            while (stream.DataAvailable) {
                var line = reader.ReadLine();
                if (line == null) {
                    throw new IOException("Peer closed the connection.");
                }

                HandleIncomingLine(line);
            }

            Thread.Sleep(LoopSleepMs);
        }
    }

    private void HandleIncomingLine(string line) {
        if (NetworkProtocol.TryReadPing(line, out var nonce)) {
            m_outgoing.Enqueue(NetworkProtocol.Pong(nonce));
            return;
        }

        if (NetworkProtocol.TryReadPong(line, out _)) {
            return;
        }

        StateProbeSnapshot stateProbe;
        if (NetworkProtocol.TryReadStateProbe(line, out stateProbe)) {
            m_incomingStateProbes.Enqueue(stateProbe);
            return;
        }

        if (NetworkProtocol.IsReady(line)) {
            if (m_isHost) {
                m_peerGameplayReady = true;
                m_log?.Invoke("HOST received client gameplay READY");
            }
            return;
        }

        long progressFrame;
        long progressSequence;
        if (NetworkProtocol.TryReadProgress(line, out progressFrame, out progressSequence)) {
            Interlocked.Exchange(ref m_peerProgressFrame, progressFrame);
            Interlocked.Exchange(ref m_peerProgressSequence, progressSequence);
            return;
        }

        if (!m_isHost) {
            long frame;
            if (NetworkProtocol.TryReadFrame(line, out frame)) {
                Interlocked.Exchange(ref m_latestAnnouncedAuthorityFrame, frame);
                return;
            }
        }

        if (m_isHost) {
            HandleHostLine(line);
        }
        else {
            HandleClientLine(line);
        }
    }

    private void HandleHostLine(string line) {
        if (!NetworkProtocol.TryReadSubmit(line, out var clientCommandId, out var payload)) {
            m_log?.Invoke("HOST ignored malformed message");
            return;
        }

        AuthorityCommandEnvelope envelope;
        long authorityFrame;
        lock (m_authorityLock) {
            authorityFrame = m_currentHostAuthorityFrame + 1;
            if (!m_sequencer.TryAccept(
                    "client",
                    clientCommandId,
                    null,
                    CommandConflictMode.Ordered,
                    payload,
                    out envelope)) {
                m_log?.Invoke("HOST ignored duplicate SUBMIT id=" + clientCommandId);
                return;
            }

            m_incoming.Enqueue(new ReceivedAuthorityCommand(
                envelope.AuthoritySequence,
                authorityFrame,
                envelope.ClientId,
                envelope.ClientCommandId,
                envelope.Payload));

            m_outgoing.Enqueue(NetworkProtocol.Commit(
                envelope.AuthoritySequence,
                authorityFrame,
                envelope.ClientId,
                envelope.ClientCommandId,
                envelope.Payload));
        }

        m_log?.Invoke(
            "HOST accepted SUBMIT -> COMMIT seq=" + envelope.AuthoritySequence
            + " frame=" + authorityFrame
            + " id=" + clientCommandId
            + " bytes=" + payload.Length);
    }

    private void HandleClientLine(string line) {
        long authoritySequence;
        long authorityFrame;
        string originClientId;
        long clientCommandId;
        byte[] payload;
        if (!NetworkProtocol.TryReadCommit(
                line,
                out authoritySequence,
                out authorityFrame,
                out originClientId,
                out clientCommandId,
                out payload)) {
            m_log?.Invoke("CLIENT ignored malformed message");
            return;
        }

        m_incoming.Enqueue(new ReceivedAuthorityCommand(
            authoritySequence,
            authorityFrame,
            originClientId,
            clientCommandId,
            payload));

        m_log?.Invoke(
            "CLIENT received COMMIT seq=" + authoritySequence
            + " frame=" + authorityFrame
            + " origin=" + originClientId
            + " id=" + clientCommandId
            + " bytes=" + payload.Length);
    }

    private void SetConnected() {
        m_connected = true;
        m_localGameplayReady = false;
        m_peerGameplayReady = false;
        Interlocked.Exchange(ref m_peerProgressFrame, -1);
        Interlocked.Exchange(ref m_peerProgressSequence, -1);
        if (!m_isHost) {
            Interlocked.Exchange(ref m_latestAnnouncedAuthorityFrame, -1);
        }
        while (m_incomingStateProbes.TryDequeue(out _)) {
        }
        m_log?.Invoke(m_isHost ? "HOST session connected" : "CLIENT session connected");
    }

    private void SetDisconnected() {
        if (m_connected) {
            m_log?.Invoke(m_isHost ? "HOST session disconnected" : "CLIENT session disconnected");
        }
        m_connected = false;
        m_localGameplayReady = false;
        m_peerGameplayReady = false;

        while (m_outgoing.TryDequeue(out _)) {
        }
        while (m_incomingStateProbes.TryDequeue(out _)) {
        }
    }

    private static void ConfigureClient(TcpClient client) {
        client.NoDelay = true;
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
    }

    public void Dispose() {
        m_stop = true;
        m_connected = false;

        try { m_client?.Close(); } catch { }
        try { m_listener?.Stop(); } catch { }

        if (m_thread != null && m_thread.IsAlive) {
            m_thread.Join(2000);
        }

        m_thread = null;
        m_client = null;
        m_listener = null;
    }
}
