using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CoiCoop.Networking;

/// <summary>
/// First persistent two-peer transport for the co-op prototype.
///
/// The host is authoritative for command ordering. Client commands are submitted
/// to the host, assigned an authority sequence, and broadcast back as COMMITs.
/// Host-local commands are committed directly and broadcast to the client.
///
/// This class deliberately knows nothing about Captain of Industry command types;
/// payloads are opaque bytes. Game-side code decides when/how to deserialize and
/// eventually replay them on the simulation thread.
/// </summary>
internal sealed class PersistentCommandSession : IDisposable {
    private const int LoopSleepMs = 5;

    private readonly bool m_isHost;
    private readonly int m_port;
    private readonly Action<string> m_log;
    private readonly ConcurrentQueue<string> m_outgoing = new ConcurrentQueue<string>();
    private readonly ConcurrentQueue<ReceivedAuthorityCommand> m_incoming = new ConcurrentQueue<ReceivedAuthorityCommand>();
    private readonly AuthorityCommandSequencer m_sequencer = new AuthorityCommandSequencer();
    private readonly object m_authorityLock = new object();

    private Thread m_thread;
    private volatile bool m_stop;
    private volatile bool m_connected;
    private long m_nextLocalCommandId;
    private TcpListener m_listener;
    private TcpClient m_client;

    public PersistentCommandSession(bool isHost, int port, Action<string> log = null) {
        m_isHost = isHost;
        m_port = port;
        m_log = log;
    }

    public bool IsHost => m_isHost;
    public bool IsConnected => m_connected;

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

    public bool SubmitLocalCommand(byte[] payload) {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (!m_connected) {
            return false;
        }

        var clientCommandId = Interlocked.Increment(ref m_nextLocalCommandId) - 1;

        if (!m_isHost) {
            m_outgoing.Enqueue(NetworkProtocol.Submit(clientCommandId, payload));
            m_log?.Invoke("CLIENT queued SUBMIT id=" + clientCommandId + " bytes=" + payload.Length);
            return true;
        }

        AuthorityCommandEnvelope envelope;
        lock (m_authorityLock) {
            if (!m_sequencer.TryAccept(
                    "host",
                    clientCommandId,
                    null,
                    CommandConflictMode.Ordered,
                    payload,
                    out envelope)) {
                return false;
            }
        }

        m_outgoing.Enqueue(NetworkProtocol.Commit(
            envelope.AuthoritySequence,
            envelope.ClientId,
            envelope.ClientCommandId,
            envelope.Payload));

        m_log?.Invoke(
            "HOST queued COMMIT seq=" + envelope.AuthoritySequence
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
        lock (m_authorityLock) {
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
        }

        // The host must inspect/replay the remote command on the simulation thread.
        m_incoming.Enqueue(new ReceivedAuthorityCommand(
            envelope.AuthoritySequence,
            envelope.ClientId,
            envelope.ClientCommandId,
            envelope.Payload));

        // The client receives the same authority envelope, including its own
        // commands. Later lockstep mode will execute only COMMITs on both peers.
        m_outgoing.Enqueue(NetworkProtocol.Commit(
            envelope.AuthoritySequence,
            envelope.ClientId,
            envelope.ClientCommandId,
            envelope.Payload));

        m_log?.Invoke(
            "HOST accepted SUBMIT -> COMMIT seq=" + envelope.AuthoritySequence
            + " id=" + clientCommandId
            + " bytes=" + payload.Length);
    }

    private void HandleClientLine(string line) {
        if (!NetworkProtocol.TryReadCommit(
                line,
                out var authoritySequence,
                out var originClientId,
                out var clientCommandId,
                out var payload)) {
            m_log?.Invoke("CLIENT ignored malformed message");
            return;
        }

        m_incoming.Enqueue(new ReceivedAuthorityCommand(
            authoritySequence,
            originClientId,
            clientCommandId,
            payload));

        m_log?.Invoke(
            "CLIENT received COMMIT seq=" + authoritySequence
            + " origin=" + originClientId
            + " id=" + clientCommandId
            + " bytes=" + payload.Length);
    }

    private void SetConnected() {
        m_connected = true;
        m_log?.Invoke(m_isHost ? "HOST session connected" : "CLIENT session connected");
    }

    private void SetDisconnected() {
        if (m_connected) {
            m_log?.Invoke(m_isHost ? "HOST session disconnected" : "CLIENT session disconnected");
        }
        m_connected = false;

        while (m_outgoing.TryDequeue(out _)) {
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
