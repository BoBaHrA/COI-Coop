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

var authorityResult = TestAuthoritySequencing();
if (authorityResult != 0) {
    return authorityResult;
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

Console.WriteLine($"PASS: COI-Coop transport handshake + bidirectional ping succeeded on 127.0.0.1:{port}");
return 0;
