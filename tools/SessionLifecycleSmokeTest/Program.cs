using System.Net;
using System.Net.Sockets;
using System.Text;
using CoiCoop.Networking;

static void Require(bool condition, string message) {
    if (!condition) throw new Exception(message);
}

static bool WaitUntil(Func<bool> condition, int timeout = 3000) {
    var start = Environment.TickCount64;
    while (Environment.TickCount64 - start < timeout) {
        if (condition()) return true;
        Thread.Sleep(5);
    }
    return false;
}

static (PersistentCommandSession host, TcpClient peer, StreamReader reader, StreamWriter writer) Connect() {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    listener.Stop();
    var host = new PersistentCommandSession(true, port);
    host.Start();
    TcpClient peer = null;
    Require(WaitUntil(() => {
        var attempt = new TcpClient();
        try { attempt.Connect(IPAddress.Loopback, port); peer = attempt; return true; }
        catch (SocketException) { attempt.Dispose(); return false; }
    }), "Host did not listen");
    peer.ReceiveTimeout = 3000;
    var reader = new StreamReader(peer.GetStream(), Encoding.UTF8);
    var writer = new StreamWriter(peer.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
    writer.WriteLine(NetworkProtocol.Hello());
    Require(NetworkProtocol.IsCompatibleWelcome(reader.ReadLine()), "Handshake rejected");
    Require(WaitUntil(() => host.IsConnected), "Host did not connect");
    host.MarkGameplayReady();
    return (host, peer, reader, writer);
}

static void Ready(PersistentCommandSession host, StreamWriter writer) {
    writer.WriteLine(NetworkProtocol.Ready(host.LocalBaselineId));
    Require(WaitUntil(() => host.PeerGameplayReady), "READY not observed");
}

static void TestBufferedBurst() {
    var (host, peer, reader, writer) = Connect();
    using (host) using (peer) {
        // HELLO was a separate exchange; READY + every SUBMIT arrive together.
        // The old DataAvailable/StreamReader combination strands the remainder
        // in StreamReader's private buffer when the socket itself becomes empty.
        var burst = new StringBuilder(NetworkProtocol.Ready(host.LocalBaselineId) + "\n");
        for (var i = 0; i < 20; i++) burst.Append(NetworkProtocol.Submit(i, new byte[] { (byte)i }) + "\n");
        writer.Write(burst.ToString());
        var received = new List<ReceivedAuthorityCommand>();
        Require(WaitUntil(() => {
            while (host.TryDequeueReceived(out var command)) received.Add(command);
            return received.Count == 20;
        }, 700), "Coalesced commands stalled until another socket write");
        for (var i = 0; i < 20; i++) {
            Require(received[i].AuthoritySequence == i && received[i].ClientCommandId == i
                && received[i].Payload.SequenceEqual(new byte[] { (byte)i }), "Burst reordered or corrupted");
        }
        Console.WriteLine("PASS: coalesced READY + 20 commands drain without a later write");
    }
}

static void TestPartialLineDoesNotBlockSend() {
    var (host, peer, reader, writer) = Connect();
    using (host) using (peer) {
        Ready(host, writer);
        writer.Write("PING|12");
        Thread.Sleep(100); // give RX a chance to observe an incomplete line
        Require(host.SubmitLocalCommand(new byte[] { 42 }), "Host submission failed");
        // A partial inbound line must not prevent the opposite direction from
        // making progress (the previous implementation blocks in ReadLine).
        Require(NetworkProtocol.TryReadCommit(reader.ReadLine(), out _, out _, out _, out _),
            "Partial inbound line blocked outgoing COMMIT");
        writer.Write("3\n");
        Require(reader.ReadLine() == NetworkProtocol.Pong(123), "Fragmented PING was corrupted");
        Console.WriteLine("PASS: fragmented RX does not block TX and reassembles correctly");
    }
}

static void TestUnverifiedCommands() {
    var (host, peer, reader, writer) = Connect();
    using (host) using (peer) {
        writer.WriteLine(NetworkProtocol.Submit(0, new byte[] { 1 }));
        writer.WriteLine(NetworkProtocol.Ready("WRONG_BASELINE"));
        writer.WriteLine(NetworkProtocol.Submit(1, new byte[] { 2 }));
        writer.WriteLine(NetworkProtocol.Ping(7));
        Require(reader.ReadLine() == NetworkProtocol.Pong(7), "Protocol did not process rejection test");
        Require(!host.PeerGameplayReady && !host.TryDequeueReceived(out _),
            "Unverified guest mutated host authority queue");
        Ready(host, writer);
        writer.WriteLine(NetworkProtocol.Submit(2, new byte[] { 3 }));
        ReceivedAuthorityCommand accepted = null;
        Require(WaitUntil(() => host.TryDequeueReceived(out accepted)), "Verified command not accepted");
        Require(accepted.AuthoritySequence == 0, "Rejected commands consumed authority sequences");
        Console.WriteLine("PASS: pre-READY and wrong-baseline commands cannot mutate host world");
    }
}

static void TestEofAndStaleReconnect() {
    var (host, peer, reader, writer) = Connect();
    using (host) {
        Ready(host, writer);
        Require(host.AdvanceHostAuthorityFrame() == 0, "Host did not advance");
        var endpoint = (IPEndPoint)peer.Client.RemoteEndPoint;
        peer.Dispose();
        Require(WaitUntil(() => !host.IsConnected && host.RequiresResync),
            "Idle EOF was not detected or progressed session stayed reusable");
        using var stale = new TcpClient();
        stale.Connect(endpoint);
        stale.ReceiveTimeout = 3000;
        using var staleReader = new StreamReader(stale.GetStream(), Encoding.UTF8);
        using var staleWriter = new StreamWriter(stale.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
        staleWriter.WriteLine(NetworkProtocol.Hello());
        Require(staleReader.ReadLine() == "REJECT|RESYNC_REQUIRED", "Stale reconnect reused progressed authority state");
        Console.WriteLine("PASS: idle EOF detected; stale reconnect requires a fresh host snapshot");
    }
}

var tests = new Action[] { TestBufferedBurst, TestPartialLineDoesNotBlockSend, TestUnverifiedCommands, TestEofAndStaleReconnect };
var failures = 0;
foreach (var test in tests) {
    try { test(); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + test.Method.Name + ": " + ex.Message); }
}
Console.WriteLine($"SESSION LIFECYCLE: {tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;
