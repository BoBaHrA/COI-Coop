using System.Net;
using System.Net.Sockets;
using CoiCoop.Networking;

static int GetFreePort() {
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    try {
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
    finally {
        listener.Stop();
    }
}

static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000) {
    var started = Environment.TickCount64;
    while (Environment.TickCount64 - started < timeoutMs) {
        if (condition()) return true;
        Thread.Sleep(10);
    }
    return false;
}

static bool TryWaitForCommand(
    PersistentCommandSession session,
    out ReceivedAuthorityCommand? command,
    int timeoutMs = 5000) {

    ReceivedAuthorityCommand? received = null;
    var ok = WaitUntil(() => {
        if (!session.TryDequeueReceived(out var item)) return false;
        received = item;
        return true;
    }, timeoutMs);

    command = received;
    return ok;
}

static int TestAuthoritySequencing() {
    var sequencer = new AuthorityCommandSequencer();

    if (!sequencer.TryAccept(
            "host",
            1,
            "ship:42:destination",
            CommandConflictMode.Supersede,
            new byte[] { 1 },
            out var shipA)) {
        Console.Error.WriteLine("FAIL: first ship command was rejected.");
        return 10;
    }

    if (sequencer.TryAccept(
            "host",
            1,
            "ship:42:destination",
            CommandConflictMode.Supersede,
            new byte[] { 1 },
            out _)) {
        Console.Error.WriteLine("FAIL: duplicate client command was accepted.");
        return 11;
    }

    if (!sequencer.TryAccept(
            "client",
            7,
            "ship:42:destination",
            CommandConflictMode.Supersede,
            new byte[] { 2 },
            out var shipB)) {
        Console.Error.WriteLine("FAIL: second ship command was rejected.");
        return 12;
    }

    if (!sequencer.TryAccept(
            "host",
            2,
            null,
            CommandConflictMode.Ordered,
            new byte[] { 3 },
            out var ordered)) {
        Console.Error.WriteLine("FAIL: ordered command was rejected.");
        return 13;
    }

    var collapsed = AuthorityCommandSequencer.CollapseSuperseded(
        new[] { shipA, ordered, shipB });

    if (collapsed.Count != 2
        || !ReferenceEquals(collapsed[0], ordered)
        || !ReferenceEquals(collapsed[1], shipB)) {
        Console.Error.WriteLine("FAIL: superseding conflict policy did not keep the latest ship intent while preserving ordered commands.");
        return 14;
    }

    if (shipA.AuthoritySequence >= shipB.AuthoritySequence) {
        Console.Error.WriteLine("FAIL: authority sequence did not preserve host acceptance order.");
        return 15;
    }

    Console.WriteLine("PASS: authority sequencing, duplicate suppression, and last-intent conflict collapse succeeded.");
    return 0;
}

static int TestPersistentCommandSession() {
    var port = GetFreePort();
    var hostLog = new List<string>();
    var clientLog = new List<string>();

    using var host = new PersistentCommandSession(true, port, hostLog.Add);
    using var client = new PersistentCommandSession(false, port, clientLog.Add);

    host.Start();
    client.Start();

    if (!WaitUntil(() => host.IsConnected && client.IsConnected)) {
        Console.Error.WriteLine("FAIL: persistent host/client session did not connect.");
        Console.Error.WriteLine(string.Join(Environment.NewLine, hostLog));
        Console.Error.WriteLine(string.Join(Environment.NewLine, clientLog));
        return 20;
    }

    host.MarkGameplayReady();
    client.MarkGameplayReady();
    if (!WaitUntil(() => host.PeerGameplayReady)) {
        Console.Error.WriteLine("FAIL: host did not receive client gameplay READY.");
        return 21;
    }

    var frame0 = host.AdvanceHostAuthorityFrame();
    if (frame0 != 0 || !client.WaitForAuthorityFrame(0, 5000)) {
        Console.Error.WriteLine("FAIL: authority frame 0 was not sealed/observed consistently.");
        return 22;
    }

    var hostPayload = new byte[] { 10, 20, 30, 40 };
    if (!host.SubmitLocalCommand(hostPayload)) {
        Console.Error.WriteLine("FAIL: host local command was not accepted for transport.");
        return 23;
    }

    if (!TryWaitForCommand(host, out var hostLocalCommit) || hostLocalCommit == null) {
        Console.Error.WriteLine("FAIL: host did not receive its own authority COMMIT for replay.");
        return 24;
    }

    if (!TryWaitForCommand(client, out var hostCommit) || hostCommit == null) {
        Console.Error.WriteLine("FAIL: client did not receive host authority COMMIT.");
        return 25;
    }

    if (hostLocalCommit.AuthoritySequence != 0
        || hostCommit.AuthoritySequence != 0
        || hostLocalCommit.AuthorityFrame != 1
        || hostCommit.AuthorityFrame != 1
        || hostLocalCommit.OriginClientId != "host"
        || hostCommit.OriginClientId != "host"
        || !hostLocalCommit.Payload.SequenceEqual(hostPayload)
        || !hostCommit.Payload.SequenceEqual(hostPayload)) {
        Console.Error.WriteLine("FAIL: host-local COMMIT was not delivered identically for authority frame 1.");
        return 26;
    }

    var clientPayload = new byte[] { 99, 88, 77 };
    if (!client.SubmitLocalCommand(clientPayload)) {
        Console.Error.WriteLine("FAIL: client local command was not accepted for transport.");
        return 27;
    }

    if (!TryWaitForCommand(host, out var hostReceivedClient) || hostReceivedClient == null) {
        Console.Error.WriteLine("FAIL: host did not receive/authorize client SUBMIT.");
        return 28;
    }

    if (!TryWaitForCommand(client, out var clientEchoCommit) || clientEchoCommit == null) {
        Console.Error.WriteLine("FAIL: client did not receive authority COMMIT for its own SUBMIT.");
        return 29;
    }

    if (hostReceivedClient.AuthoritySequence != 1
        || clientEchoCommit.AuthoritySequence != 1
        || hostReceivedClient.AuthorityFrame != 1
        || clientEchoCommit.AuthorityFrame != 1
        || hostReceivedClient.OriginClientId != "client"
        || clientEchoCommit.OriginClientId != "client"
        || !hostReceivedClient.Payload.SequenceEqual(clientPayload)
        || !clientEchoCommit.Payload.SequenceEqual(clientPayload)) {
        Console.Error.WriteLine("FAIL: client SUBMIT was not converted to one consistent frame-1 authority COMMIT.");
        return 30;
    }

    var frame1 = host.AdvanceHostAuthorityFrame();
    if (frame1 != 1 || !client.WaitForAuthorityFrame(1, 5000)) {
        Console.Error.WriteLine("FAIL: authority frame 1 marker did not arrive after its COMMITs.");
        return 31;
    }

    host.ReportProgress(1, 1);
    client.ReportProgress(1, 1);
    var progressOk = WaitUntil(() => {
        return host.TryGetPeerProgress(out var hostPeerFrame, out var hostPeerSeq)
            && client.TryGetPeerProgress(out var clientPeerFrame, out var clientPeerSeq)
            && hostPeerFrame == 1
            && hostPeerSeq == 1
            && clientPeerFrame == 1
            && clientPeerSeq == 1;
    });

    if (!progressOk) {
        Console.Error.WriteLine("FAIL: authority progress probes were not exchanged consistently.");
        return 32;
    }

    Console.WriteLine($"PASS: persistent session sealed authority frames and delivered one ordered stream on 127.0.0.1:{port}");
    return 0;
}

var authorityResult = TestAuthoritySequencing();
if (authorityResult != 0) {
    return authorityResult;
}

var persistentResult = TestPersistentCommandSession();
if (persistentResult != 0) {
    return persistentResult;
}

var port = GetFreePort();
Exception? hostError = null;
bool hostSucceeded = false;

var hostThread = new Thread(() => {
    try {
        hostSucceeded = LocalTransportProbe.HostOnce(port, acceptTimeoutMs: 5000);
    }
    catch (Exception ex) {
        hostError = ex;
    }
}) {
    IsBackground = true,
    Name = "COI-Coop smoke-test host"
};

hostThread.Start();
Thread.Sleep(100);

var clientSucceeded = LocalTransportProbe.ClientOnce(port);
var hostStopped = hostThread.Join(TimeSpan.FromSeconds(5));

if (!hostStopped) {
    Console.Error.WriteLine("FAIL: host did not stop after the probe.");
    return 1;
}

if (hostError is not null) {
    Console.Error.WriteLine("FAIL: host threw an exception:");
    Console.Error.WriteLine(hostError);
    return 2;
}

if (!hostSucceeded) {
    Console.Error.WriteLine("FAIL: host did not complete the handshake/PING exchange.");
    return 3;
}

if (!clientSucceeded) {
    Console.Error.WriteLine("FAIL: client rejected handshake or PONG did not match.");
    return 4;
}

Console.WriteLine($"PASS: COI-Coop one-shot handshake + bidirectional ping succeeded on 127.0.0.1:{port}");
return 0;
