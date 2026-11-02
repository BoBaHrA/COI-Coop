using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CoiCoop.Networking;

// Only transports snapshots. All access to the live game happens in the
// controller's Sync callback, never on this socket thread.
internal sealed class LiveWorldServer : IDisposable {
    internal sealed class Request {
        internal readonly ManualResetEventSlim Ready = new ManualResetEventSlim();
        internal volatile bool Cancelled;
        internal Snapshot Result;
        internal string Error;
        internal void Complete(Snapshot result, string error = null) {
            Result = result;
            Error = error;
            Ready.Set();
        }
    }

    internal sealed class Snapshot {
        internal byte[] Bytes;
        internal string Hash;
        internal string Ticket;
        internal long Step;
        internal bool Paused;
    }

    private readonly TcpListener m_listener;
    private readonly string m_password;
    private readonly string m_compatibility;
    private readonly Action<string> m_log;
    private readonly ConcurrentQueue<Request> m_requests = new ConcurrentQueue<Request>();
    private readonly Thread m_thread;
    private volatile bool m_stopped;
    private TcpClient m_client;

    internal LiveWorldServer(int port, string password, string compatibility, Action<string> log) {
        m_password = password;
        m_compatibility = compatibility;
        m_log = log;
        m_listener = new TcpListener(IPAddress.Any, port);
        // Fail synchronously on port conflicts, before advertising a working host.
        m_listener.Start();
        m_thread = new Thread(Run) { IsBackground = true, Name = "COI live snapshot server" };
        m_thread.Start();
    }

    internal bool TryTakeRequest(out Request request) => m_requests.TryDequeue(out request);

    internal static string Hash(byte[] bytes) {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
    }

    internal static string NewTicket() {
        var bytes = new byte[32];
        using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        return BitConverter.ToString(bytes).Replace("-", "");
    }

    private void Run() {
        while (!m_stopped) {
            Request request = null;
            try {
                using (var client = m_listener.AcceptTcpClient()) {
                    m_client = client;
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 30000;
                    client.NoDelay = true;
                    using (var stream = client.GetStream())
                    using (var writer = new BinaryWriter(stream, new UTF8Encoding(false))) {
                        var hello = ReadHeader(stream);
                        var fields = hello.Split('|');
                        if (fields.Length != 4 || fields[0] != "COI_LIVE" || fields[1] != "1"
                            || !string.Equals(fields[2], m_password, StringComparison.Ordinal)) {
                            writer.Write("REJECT: invalid connection key");
                            continue;
                        }
                        if (!string.Equals(fields[3], m_compatibility, StringComparison.Ordinal)) {
                            writer.Write("REJECT: different game or mod build");
                            continue;
                        }
                        request = new Request();
                        m_requests.Enqueue(request);
                        var started = Environment.TickCount;
                        while (!request.Ready.Wait(100) && !m_stopped) {
                            if (unchecked(Environment.TickCount - started) > 30000) break;
                        }
                        if (!request.Ready.IsSet || request.Result == null) {
                            writer.Write("REJECT: " + (request.Error ?? "host did not reach a snapshot boundary"));
                            continue;
                        }
                        var snapshot = request.Result;
                        writer.Write("COI_LIVE_SNAPSHOT_1");
                        writer.Write(m_compatibility);
                        writer.Write(snapshot.Ticket);
                        writer.Write(snapshot.Hash);
                        writer.Write(snapshot.Step);
                        writer.Write(snapshot.Paused);
                        writer.Write(snapshot.Bytes.Length);
                        writer.Write(snapshot.Bytes);
                        writer.Flush();
                        m_log?.Invoke("Live snapshot transferred, step=" + snapshot.Step);
                    }
                }
            }
            catch (Exception ex) {
                if (!m_stopped) m_log?.Invoke("Snapshot transfer failed: " + ex.Message);
                if (request != null) request.Cancelled = true;
            }
            finally {
                if (request != null && !request.Ready.IsSet) request.Cancelled = true;
                m_client = null;
            }
        }
    }

    private static string ReadHeader(Stream stream) {
        var builder = new StringBuilder();
        for (var i = 0; i < 512; i++) {
            var next = stream.ReadByte();
            if (next < 0) throw new IOException("Connection closed during header");
            if (next == '\n') return builder.ToString().TrimEnd('\r');
            if (next < 32 || next > 126) throw new IOException("Invalid header character");
            builder.Append((char)next);
        }
        throw new IOException("Header is too long");
    }

    public void Dispose() {
        m_stopped = true;
        try { m_listener.Stop(); } catch { }
        try { m_client?.Close(); } catch { }
        while (m_requests.TryDequeue(out var request)) { request.Cancelled = true; request.Complete(null, "Host closed"); }
        if (m_thread.IsAlive) m_thread.Join(1000);
    }
}
